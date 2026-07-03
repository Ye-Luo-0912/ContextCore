using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V16;

public sealed class HybridShadowEvaluator
{
    private static readonly double[] Alphas = [1.0, 0.9, 0.7, 0.5];

    private sealed record TraceRow(
        string RowKey, string CandidateId, string Section, int SourceType,
        double DetScore, bool SelectedByScoring, bool IncludedInPackage,
        double TokenCost, double DetNorm, double Neural, double Hybrid
    );

    public void BuildAndWrite(string outputDir)
    {
        var v16Dir = Path.Combine(outputDir, "learning", "v16");
        Directory.CreateDirectory(v16Dir);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var v14FeaturePath = Path.Combine(outputDir, "learning", "v14", "feature-store.jsonl");
        var v14FeedbackPath = Path.Combine(outputDir, "learning", "v14", "feedback-events.jsonl");
        var v15ReportPath = Path.Combine(outputDir, "learning", "v15", "neural-selection-dry-run-report.json");
        var v14GatePath = Path.Combine(outputDir, "learning", "v14", "foundation-gate.json");

        bool v14GateReady = GateReady(v14GatePath);

        var neuralScores = ReadNeuralScores(v15ReportPath);
        var feedbackMap = ReadFeedbackEvents(v14FeedbackPath);
        var rawCandidates = ReadTraceRows(v14FeaturePath);

        int N = rawCandidates.Count;
        var sectionSet = new HashSet<string>(rawCandidates.Select(c => c.Section));
        bool hasRelatedContext = sectionSet.Contains("related_context");
        bool hasLegacyRaw = sectionSet.Any(s => s.StartsWith("SmokeDoc_", StringComparison.OrdinalIgnoreCase))
                            || sectionSet.Contains("legacy") || sectionSet.Contains("raw");
        bool coverageLimited = !hasRelatedContext || !hasLegacyRaw;
        var missingSections = new List<string>();
        if (!hasRelatedContext) missingSections.Add("related_context");
        if (!hasLegacyRaw) missingSections.Add("legacy/raw");

        // === Global normalization for calibration signals ===
        double globalMaxProxy = feedbackMap.Values.Count > 0 ? feedbackMap.Values.Max(f => f.successProxy) : 1;
        double globalMaxCostEff = feedbackMap.Values.Count > 0 ? feedbackMap.Values.Max(f => f.costEfficiency) : 1;
        double globalMaxSig = feedbackMap.Values.Count > 0 ? feedbackMap.Values.Max(f => f.implicitSignal) : 1;

        // Normalize deterministic scores globally
        double maxDet = rawCandidates.Count > 0 ? rawCandidates.Max(c => c.DetScore) : 110;
        double GetDetNorm(double s) => Math.Clamp(s / Math.Max(maxDet, 1), 0, 1);

        // Build rows with unique RowKey
        var rows = rawCandidates.Select(c =>
        {
            double detNorm = GetDetNorm(c.DetScore);
            double neural = neuralScores.GetValueOrDefault(c.CandidateId, 0.5);
            return new TraceRow(c.RowKey, c.CandidateId, c.Section, c.SourceType,
                c.DetScore, c.SelectedByScoring, c.IncludedInPackage,
                c.TokenCost, detNorm, neural, 0); // Hybrid set per alpha
        }).ToList();

        // === Offline shadow calibration with global-normalized weights ===
        var calibData = rows
            .Where(r => feedbackMap.ContainsKey(r.CandidateId))
            .Select(r => {
                var fb = feedbackMap[r.CandidateId];
                double w = GlobalNormWeight(fb.successProxy, globalMaxProxy)
                         * 0.5 + GlobalNormWeight(fb.costEfficiency, globalMaxCostEff) * 0.3
                         + GlobalNormWeight(fb.implicitSignal, globalMaxSig) * 0.2;
                w = Math.Max(0.1, Math.Min(w, 1.0));
                return (r.RowKey, r.CandidateId, r.Neural, r.SelectedByScoring, r.IncludedInPackage,
                        weight: w, fb.successProxy, fb.costEfficiency, fb.implicitSignal);
            }).ToList();

        var (wtdA, wtdB, wtdBce) = FitWeightedLogistic(
            calibData.Select(e => (e.Neural, e.SelectedByScoring ? 1.0 : 0.0, e.weight)).ToList());
        var (unwA, unwB, unwBce) = FitWeightedLogistic(
            calibData.Select(e => (e.Neural, e.SelectedByScoring ? 1.0 : 0.0, 1.0)).ToList());

        // Pairwise ranking
        int pwTotal = 0, pwCorrect = 0;
        double pwWtdTot = 0, pwWtdCorr = 0;
        for (int i = 0; i < calibData.Count; i++)
            for (int j = i + 1; j < calibData.Count; j++)
            {
                bool iSel = calibData[i].SelectedByScoring;
                bool jSel = calibData[j].SelectedByScoring;
                if (iSel == jSel) continue;
                pwTotal++;
                double w = calibData[i].weight * calibData[j].weight;
                pwWtdTot += w;
                double si = calibData[i].Neural, sj = calibData[j].Neural;
                if ((iSel && si >= sj) || (!iSel && si < sj)) { pwCorrect++; pwWtdCorr += w; }
            }
        double pwAcc = pwTotal > 0 ? (double)pwCorrect / pwTotal : 0;
        double pwWtdAcc = pwWtdTot > 0 ? pwWtdCorr / pwWtdTot : 0;

        // === Alpha sweep with RowKey-based ranking ===
        var alphaResults = new List<object>();
        bool alpha1InvariantPassed = true;
        foreach (double alpha in Alphas)
        {
            var swept = rows.Select(r => new TraceRow(
                r.RowKey, r.CandidateId, r.Section, r.SourceType,
                r.DetScore, r.SelectedByScoring, r.IncludedInPackage, r.TokenCost,
                r.DetNorm, r.Neural,
                alpha * r.DetNorm + (1 - alpha) * r.Neural
            )).ToList();

            var detRanking = swept.OrderByDescending(r => r.DetNorm).ToList();
            var hybRanking = swept.OrderByDescending(r => r.Hybrid).ToList();

            var detRanks = new Dictionary<string, int>();
            for (int i = 0; i < detRanking.Count; i++) detRanks[detRanking[i].RowKey] = i + 1;
            var hybRanks = new Dictionary<string, int>();
            for (int i = 0; i < hybRanking.Count; i++) hybRanks[hybRanking[i].RowKey] = i + 1;

            int selCount = swept.Count(r => r.SelectedByScoring);
            int incCount = swept.Count(r => r.IncludedInPackage);
            int kSel = Math.Max(1, Math.Min(selCount, hybRanking.Count));
            int kInc = Math.Max(1, Math.Min(incCount, hybRanking.Count));
            double thrSel = hybRanking[kSel - 1].Hybrid;
            double thrInc = hybRanking[kInc - 1].Hybrid;

            double rankDeltaSum = 0;
            int scoringDisagree = 0, inclusionDisagree = 0;
            foreach (var r in swept)
            {
                int dr = detRanks.GetValueOrDefault(r.RowKey, 0);
                int hr = hybRanks.GetValueOrDefault(r.RowKey, 0);
                rankDeltaSum += Math.Abs(dr - hr);
                if (r.SelectedByScoring != (r.Hybrid >= thrSel)) scoringDisagree++;
                if (r.IncludedInPackage != (r.Hybrid >= thrInc)) inclusionDisagree++;
            }
            double meanRankDelta = swept.Count > 0 ? rankDeltaSum / swept.Count : 0;

            int TopKChurn(int kk)
            {
                var dTop = detRanking.Take(kk).Select(r => r.RowKey).ToHashSet();
                var hTop = hybRanking.Take(kk).Select(r => r.RowKey).ToHashSet();
                return kk - dTop.Intersect(hTop).Count();
            }

            int t3 = TopKChurn(3), t5 = TopKChurn(5), t10 = TopKChurn(10);

            // Alpha=1 invariant: pure deterministic → zero churn
            bool alpha1Invariant = true;
            if (Math.Abs(alpha - 1.0) < 0.001)
            {
                alpha1Invariant = Math.Abs(meanRankDelta) < 0.001 && t3 == 0 && t5 == 0 && t10 == 0;
                if (!alpha1Invariant) alpha1InvariantPassed = false;
            }

            alphaResults.Add(new
            {
                Alpha = alpha,
                NeuralWeight = Math.Round(1 - alpha, 2),
                DeterministicWeight = alpha,
                ThresholdMode = "TopKSelectedCount: threshold = hybridScore of k-th ranked item (0-indexed: k-1), k=selectedCount",
                SelectionThreshold = Math.Round(thrSel, 4),
                InclusionThreshold = Math.Round(thrInc, 4),
                MeanRankDelta = Math.Round(meanRankDelta, 4),
                ScoringSelectionDisagreementCount = scoringDisagree,
                ScoringSelectionDisagreementRate = swept.Count > 0 ? Math.Round((double)scoringDisagree / swept.Count, 4) : 0,
                PackageInclusionDisagreementCount = inclusionDisagree,
                PackageInclusionDisagreementRate = swept.Count > 0 ? Math.Round((double)inclusionDisagree / swept.Count, 4) : 0,
                Top3Churn = t3, Top5Churn = t5, Top10Churn = t10,
                Alpha1InvariantPassed = alpha1Invariant,
                MeanHybridScore = Math.Round(swept.Average(r => r.Hybrid), 4),
                MeanNeuralScore = Math.Round(swept.Average(r => r.Neural), 4),
                MeanDetScoreNorm = Math.Round(swept.Average(r => r.DetNorm), 4)
            });
        }

        // === Write hybrid-shadow-evaluation.json ===
        WriteJson(v16Dir, "hybrid-shadow-evaluation.json", new
        {
            GeneratedAt = now,
            V14GateReady = v14GateReady,
            RowIdentity = "RowKey = operationId|requestId|candidateId|section|sourceType|rowIndex — unique per trace row",
            TotalRows = N,
            TotalUniqueCandidateIds = rows.Select(r => r.CandidateId).Distinct().Count(),
            Alphas,
            AlphaSweepResults = alphaResults,
            Alpha1InvariantPassed = alpha1InvariantPassed,
            Coverage = new
            {
                CoveredSections = sectionSet.OrderBy(s => s).ToArray(),
                CoverageLimited = coverageLimited,
                MissingSections = missingSections.ToArray(),
                LegacyRawCoverageDetected = hasLegacyRaw,
                RelatedContextCoverageDetected = hasRelatedContext
            },
            RuntimeSafety = new
            {
                BlendAlpha = 1.0, NeuralBiasActive = false,
                RuntimeInfluenceAllowed = false, PackageOutputChanged = false,
                VectorBindingChanged = false, RuntimePromotionApplied = false
            }
        });

        // === Write neural-calibration-shadow.json ===
        WriteJson(v16Dir, "neural-calibration-shadow.json", new
        {
            GeneratedAt = now,
            CalibrationMethod = "Weighted binary logistic regression with global-normalized feedback signals",
            LabelFormula = "label = selectedByScoring (1.0 = selected, 0.0 = dropped)",
            SampleWeightFormula = "weight = 0.5 * norm(successProxy) + 0.3 * norm(costEfficiency) + 0.2 * norm(implicitSignal), where norm(x) = x / global_max",
            GlobalNormalization = new
            {
                GlobalMaxSuccessProxy = Math.Round(globalMaxProxy, 1),
                GlobalMaxCostEfficiency = Math.Round(globalMaxCostEff, 1),
                GlobalMaxImplicitSignal = Math.Round(globalMaxSig, 1)
            },
            WeightedCalibration = new { a = Math.Round(wtdA, 6), b = Math.Round(wtdB, 6), weightedBCELoss = Math.Round(wtdBce, 6) },
            UnweightedCalibration = new { a = Math.Round(unwA, 6), b = Math.Round(unwB, 6), unweightedBCELoss = Math.Round(unwBce, 6) },
            PairwiseRanking = new
            {
                TotalPairs = pwTotal, CorrectPairs = pwCorrect,
                UnweightedAccuracy = Math.Round(pwAcc, 4),
                WeightedAccuracy = Math.Round(pwWtdAcc, 4),
                Interpretation = pwWtdAcc >= 0.7 ? "strong" : pwWtdAcc >= 0.55 ? "weak" : "random-level"
            },
            FeedbackSignalUsageBreakdown = new
            {
                downstreamSuccessProxy_used = true,
                downstreamSuccessProxy_weight = 0.5,
                costEfficiencyScore_used = true,
                costEfficiencyScore_weight = 0.3,
                userImplicitSignal_used = true,
                userImplicitSignal_weight = 0.2
            },
            PerCandidateCalibration = calibData.Take(20).Select(e => new
            {
                candidateId = e.CandidateId,
                rowKey = e.RowKey,
                originalNeuralScore = Math.Round(e.Neural, 4),
                calibratedProbability = Math.Round(Sigmoid(wtdA * e.Neural + wtdB), 4),
                selectedByScoring = e.SelectedByScoring,
                includedInPackage = e.IncludedInPackage,
                sampleWeight = Math.Round(e.weight, 4),
                rawSuccessProxy = Math.Round(e.successProxy, 1),
                rawCostEfficiency = Math.Round(e.costEfficiency, 1),
                rawImplicitSignal = e.implicitSignal
            }),
            OfflineShadowCalibration = true,
            RuntimeInfluenceAllowed = false
        });

        // === Write v16-readiness-gate.json ===
        bool v16ShadowReady = v14GateReady && N >= 10 && alphaResults.Count == Alphas.Length;
        bool metricIntegrityReady = v16ShadowReady && alpha1InvariantPassed;
        bool v17ShadowEntryReady = v16ShadowReady && metricIntegrityReady;

        WriteJson(v16Dir, "v16-readiness-gate.json", new
        {
            GeneratedAt = now,
            V16ShadowEvaluationReady = v16ShadowReady,
            V16MetricIntegrityReady = metricIntegrityReady,
            Alpha1InvariantPassed = alpha1InvariantPassed,
            CoverageLimited = coverageLimited,
            MissingCoverage = missingSections.ToArray(),
            LegacyRawCoverageDetected = hasLegacyRaw,
            RelatedContextCoverageDetected = hasRelatedContext,
            V17ShadowEvaluationEntryReady = v17ShadowEntryReady,
            ProductionGeneralizationReady = false,
            ProductionGeneralizationNote = "Based on smoke corpus (33 rows, seeded stores). Production generalization requires full real-trace evaluation.",
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            V14GatePreserved = v14GateReady,
            Calibration = new
            {
                Active = true, Weighted = true,
                SignalsUsed = new[] { "downstreamSuccessProxy", "costEfficiencyScore", "userImplicitSignal" },
                GlobalNormalization = true
            },
            NextSteps = new[]
            {
                "V17: full production-trace shadow evaluation",
                "V17: evaluate against real-world selection outcomes, not smoke corpus",
                "V17: verify production generalization before declaring ProductionGeneralizationReady=true"
            }
        });

        // Write markdowns
        WriteMdHybridEval(v16Dir, now, N, alphaResults, sectionSet, coverageLimited, v14GateReady, missingSections, alpha1InvariantPassed);
        WriteMdCalibration(v16Dir, now, wtdA, wtdB, wtdBce, pwAcc, pwWtdAcc, pwTotal, pwCorrect, calibData, globalMaxProxy, globalMaxCostEff, globalMaxSig);
        WriteMdGate(v16Dir, now, v16ShadowReady, metricIntegrityReady, coverageLimited, v14GateReady, v17ShadowEntryReady, alpha1InvariantPassed);
    }

    private static bool GateReady(string path)
    {
        if (!File.Exists(path)) return false;
        try { using var d = JsonDocument.Parse(File.ReadAllText(path)); return d.RootElement.TryGetProperty("LearningDataPipelineReady", out var p) && p.GetBoolean(); } catch { return false; }
    }

    private static Dictionary<string, double> ReadNeuralScores(string path)
    {
        var scores = new Dictionary<string, double>();
        if (!File.Exists(path)) return scores;
        try { using var d = JsonDocument.Parse(File.ReadAllText(path)); if (d.RootElement.TryGetProperty("PerCandidate", out var arr)) foreach (var r in arr.EnumerateArray()) { var cid = r.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : ""; var ns = r.TryGetProperty("neuralSelectionScore", out var s) ? s.GetDouble() : 0.5; if (!string.IsNullOrWhiteSpace(cid)) scores[cid] = ns; } } catch { }
        return scores;
    }

    private static Dictionary<string, (double successProxy, double costEfficiency, double implicitSignal)> ReadFeedbackEvents(string path)
    {
        var map = new Dictionary<string, (double, double, double)>();
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { var d = JsonDocument.Parse(line).RootElement; var cid = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : ""; var sp = d.TryGetProperty("downstreamSuccessProxy", out var dp) ? dp.GetDouble() : 0; var ce = d.TryGetProperty("costEfficiencyScore", out var cs) ? cs.GetDouble() : 0; var si = d.TryGetProperty("userImplicitSignal", out var ui) ? (double)ui.GetByte() : 0; map[cid] = (sp, ce, si); } catch { }
        }
        return map;
    }

    private static List<TraceRow> ReadTraceRows(string path)
    {
        var list = new List<TraceRow>();
        if (!File.Exists(path)) return list;
        int rowIdx = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var d = JsonDocument.Parse(line).RootElement;
                var opId = d.TryGetProperty("operationId", out var o) ? o.GetString() ?? "" : "";
                var reqId = d.TryGetProperty("requestId", out var r) ? r.GetString() ?? "" : "";
                var cid = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                var sec = d.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "";
                var st = d.TryGetProperty("sourceType", out var sb) ? (int)sb.GetByte() : 1;
                var ds = d.TryGetProperty("deterministicScore", out var dsj) ? dsj.GetDouble() : 0;
                var sel = d.TryGetProperty("selectedByScoring", out var sl) && sl.GetBoolean();
                var inc = d.TryGetProperty("includedInPackage", out var ip) && ip.GetBoolean();
                var tc = d.TryGetProperty("tokenCost", out var tk) ? tk.GetDouble() : 0;
                string rk = $"{opId}|{reqId}|{cid}|{sec}|{st}|{rowIdx++}";
                list.Add(new TraceRow(rk, cid, sec, st, ds, sel, inc, tc, 0, 0.5, 0));
            }
            catch { rowIdx++; }
        }
        return list;
    }

    private static double GlobalNormWeight(double value, double globalMax)
    {
        if (globalMax <= 0) return 0;
        return Math.Clamp(value / globalMax, 0, 1);
    }

    private static (double a, double b, double loss) FitWeightedLogistic(List<(double x, double label, double weight)> data)
    {
        if (data.Count == 0) return (0, 0, 0);
        double a = 0, b = 0, lr = 0.01;
        for (int e = 0; e < 500; e++)
        {
            double ga = 0, gb = 0, loss = 0, tw = 0;
            foreach (var (x, y, w) in data)
            {
                double p = Math.Clamp(Sigmoid(a * x + b), 1e-7, 1 - 1e-7);
                ga += w * (p - y) * x;
                gb += w * (p - y);
                loss += w * (-(y * Math.Log(p) + (1 - y) * Math.Log(1 - p)));
                tw += w;
            }
            a -= lr * ga / Math.Max(tw, 1);
            b -= lr * gb / Math.Max(tw, 1);
            if (e == 499) return (a, b, loss / Math.Max(tw, 1));
        }
        return (a, b, 0);
    }

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-z));

    private static void WriteJson(string dir, string name, object obj)
    {
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteMdHybridEval(string dir, string now, int N, List<object> results, HashSet<string> sections, bool covLimited, bool gate, List<string> missing, bool alpha1ok)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.1 Hybrid Scoring Shadow Evaluation");
        sb.AppendLine($"Generated: {now} | Rows: {N} | V14Gate: {(gate ? "PASS" : "FAIL")} | Alpha1Invariant: {(alpha1ok ? "PASS" : "FAIL")}");
        if (covLimited) sb.AppendLine($"CoverageLimited: MISSING {string.Join(",", missing)}");
        else sb.AppendLine($"Sections: {string.Join(", ", sections.OrderBy(x => x))}");
        sb.AppendLine();
        sb.AppendLine("| Alpha | NW | RankΔ | ScoringDis | InclDis | T3 | T5 | T10 | α1Inv |");
        sb.AppendLine("|-------|-----|--------|------------|---------|----|----|----|----|");
        foreach (dynamic r in results)
            sb.AppendLine($"| {r.Alpha:F1} | {r.NeuralWeight:F1} | {r.MeanRankDelta:F4} | {r.ScoringSelectionDisagreementCount} | {r.PackageInclusionDisagreementCount} | {r.Top3Churn} | {r.Top5Churn} | {r.Top10Churn} | {(r.Alpha1InvariantPassed ? "Y" : "N")} |");
        sb.AppendLine("\n## Runtime Safety\n- NeuralBiasActive: false | RuntimeInfluenceAllowed: false | PackageOutputChanged: false");
        File.WriteAllText(Path.Combine(dir, "hybrid-shadow-evaluation.md"), sb.ToString());
    }

    private static void WriteMdCalibration(string dir, string now, double a, double b, double loss, double acc, double wacc, int total, int correct, List<(string rk, string cid, double neural, bool sel, bool inc, double weight, double sp, double ce, double si)> entries, double maxProxy, double maxCe, double maxSig)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.1 Neural Calibration Shadow Report");
        sb.AppendLine($"Generated: {now} | Method: Weighted BCE logistic | a={a:F6} b={b:F6} loss={loss:F6}");
        sb.AppendLine($"Global max: successProxy={maxProxy:F1} costEfficiency={maxCe:F1} implicitSignal={maxSig:F1}");
        sb.AppendLine($"Weight: 0.5*norm(successProxy) + 0.3*norm(costEfficiency) + 0.2*norm(implicitSignal)");
        sb.AppendLine($"Pairwise: unweighted={acc:P2} weighted={wacc:P2} ({correct}/{total})");
        sb.AppendLine("## Offline shadow only. Not deployed to runtime.");
        File.WriteAllText(Path.Combine(dir, "neural-calibration-shadow.md"), sb.ToString());
    }

    private static void WriteMdGate(string dir, string now, bool shadow, bool metric, bool cov, bool v14, bool v17, bool alpha1)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.1 Readiness Gate");
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"- V16ShadowEvaluationReady: {shadow}");
        sb.AppendLine($"- V16MetricIntegrityReady: {metric}");
        sb.AppendLine($"- Alpha1InvariantPassed: {alpha1}");
        sb.AppendLine($"- CoverageLimited: {cov}");
        sb.AppendLine($"- V17ShadowEvaluationEntryReady: {v17}");
        sb.AppendLine($"- ProductionGeneralizationReady: false");
        sb.AppendLine($"- V14GatePreserved: {v14}");
        sb.AppendLine($"- RuntimeInfluenceAllowed: false");
        sb.AppendLine($"- PackageOutputChanged: false");
        File.WriteAllText(Path.Combine(dir, "v16-readiness-gate.md"), sb.ToString());
    }
}
