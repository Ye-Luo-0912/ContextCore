using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V16;

public sealed class HybridShadowEvaluator
{
    private static readonly double[] Alphas = [1.0, 0.9, 0.7, 0.5];
    private const string SectionsMissing = "related_context, legacy/raw";

    private sealed record CandidateRow(
        string Id, string Section, int SourceType, double DetScore, bool Sel, bool Inc,
        double TokenCost, double DetNorm, double Neural, double Calib, double Hybrid,
        double HybridCalib
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

        bool v14GateReady = false;
        if (File.Exists(v14GatePath))
        {
            try
            {
                using var gdoc = JsonDocument.Parse(File.ReadAllText(v14GatePath));
                v14GateReady = gdoc.RootElement.TryGetProperty("LearningDataPipelineReady", out var ldp) && ldp.GetBoolean();
            }
            catch { }
        }

        // Read V15 per-candidate neural scores
        var neuralScores = new Dictionary<string, (double selection, double rank, double drop)>();
        if (File.Exists(v15ReportPath))
        {
            try
            {
                using var v15doc = JsonDocument.Parse(File.ReadAllText(v15ReportPath));
                if (v15doc.RootElement.TryGetProperty("PerCandidate", out var perCand))
                {
                    foreach (var row in perCand.EnumerateArray())
                    {
                        var cid = row.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                        var ns = row.TryGetProperty("neuralSelectionScore", out var s) ? s.GetDouble() : 0.5;
                        var nr = row.TryGetProperty("neuralRankingScore", out var r) ? r.GetDouble() : 0.5;
                        var nd = row.TryGetProperty("neuralDropProbability", out var d) ? d.GetDouble() : 0.5;
                        if (!string.IsNullOrWhiteSpace(cid))
                            neuralScores[cid] = (ns, nr, nd);
                    }
                }
            }
            catch { }
        }

        // Read feedback events
        var feedbackMap = new Dictionary<string, (double successProxy, int implicitSignal, double costEfficiency)>();
        if (File.Exists(v14FeedbackPath))
        {
            foreach (var line in File.ReadLines(v14FeedbackPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var d = JsonDocument.Parse(line).RootElement;
                    var cid = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                    var sp = d.TryGetProperty("downstreamSuccessProxy", out var dp) ? dp.GetDouble() : 0;
                    var us = d.TryGetProperty("userImplicitSignal", out var ui) ? ui.GetByte() : (byte)0;
                    var ce = d.TryGetProperty("costEfficiencyScore", out var cs) ? cs.GetDouble() : 0;
                    feedbackMap[cid] = (sp, us, ce);
                }
                catch { }
            }
        }

        // Read feature store trace rows
        var candidates = new List<(string id, string section, int sourceType, double detScore, bool sel, bool inc, double tokenCost)>();
        if (File.Exists(v14FeaturePath))
        {
            foreach (var line in File.ReadLines(v14FeaturePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var d = JsonDocument.Parse(line).RootElement;
                    var cid = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                    var sec = d.TryGetProperty("section", out var sn) ? sn.GetString() ?? "" : "";
                    var st = d.TryGetProperty("sourceType", out var s) ? (int)s.GetByte() : 1;
                    var ds = d.TryGetProperty("deterministicScore", out var dss) ? dss.GetDouble() : 0;
                    var sel = d.TryGetProperty("selectedByScoring", out var sl) && sl.GetBoolean();
                    var inc = d.TryGetProperty("includedInPackage", out var ip) && ip.GetBoolean();
                    var tc = d.TryGetProperty("tokenCost", out var tk) ? tk.GetDouble() : 0;
                    candidates.Add((cid, sec, st, ds, sel, inc, tc));
                }
                catch { }
            }
        }

        int N = candidates.Count;
        var sectionSet = new HashSet<string>(candidates.Select(c => c.section));
        bool coverageLimited = !sectionSet.Contains("related_context") && !sectionSet.Contains("legacy");

        // Normalize deterministic scores
        double maxDetScore = candidates.Count > 0 ? candidates.Max(c => c.detScore) : 110;
        var detNorms = candidates.Select(c => (c.id, norm: Math.Clamp(c.detScore / maxDetScore, 0, 1))).ToDictionary(x => x.id, x => x.norm);

        // Merge neural scores
        foreach (var (id, _, _, detScore, sel, inc, tc) in candidates)
        {
            if (!neuralScores.ContainsKey(id))
                neuralScores[id] = (0.5, 0.5, 0.5);
        }

        // === Offline shadow calibration (logistic) ===
        var calibEntries = candidates
            .Where(c => neuralScores.ContainsKey(c.id) && feedbackMap.ContainsKey(c.id))
            .Select(c => (
                c.id, c.detScore, detNorm: detNorms.GetValueOrDefault(c.id, 0.5),
                neural: neuralScores[c.id].selection,
                selLabel: c.sel ? 1.0 : 0.0,
                successProxy: feedbackMap[c.id].successProxy,
                costEfficiency: feedbackMap[c.id].costEfficiency
            )).ToList();

        // Simple logistic calibration: fit a,b such that P(sel) ~ sigmoid(a * neuralScore + b)
        var (calibA, calibB, calibLoss) = FitLogisticBinary(
            calibEntries.Select(e => (e.neural, e.selLabel)).ToList());

        // Pairwise ranking calibration: count how often neural score respects the selection order
        int pairwiseTotal = 0, pairwiseCorrect = 0;
        for (int i = 0; i < calibEntries.Count; i++)
        {
            for (int j = 0; j < calibEntries.Count; j++)
            {
                if (i == j) continue;
                bool iSelected = calibEntries[i].selLabel > 0.5;
                bool jSelected = calibEntries[j].selLabel > 0.5;
                if (iSelected == jSelected) continue;
                pairwiseTotal++;
                double iScore = calibEntries[i].neural;
                double jScore = calibEntries[j].neural;
                if (iSelected && iScore >= jScore) pairwiseCorrect++;
                else if (!iSelected && iScore < jScore) pairwiseCorrect++;
            }
        }
        double pairwiseAccuracy = pairwiseTotal > 0 ? (double)pairwiseCorrect / pairwiseTotal : 0;

        // Calibrated neural scores
        var calibratedScores = new Dictionary<string, double>();
        foreach (var (id, ns) in neuralScores)
        {
            double z = calibA * ns.selection + calibB;
            calibratedScores[id] = 1.0 / (1.0 + Math.Exp(-z));
        }

        // === Alpha sweep ===
        var alphaResults = new List<object>();
        foreach (double alpha in Alphas)
        {
            double GetNeuralScore(string id) => neuralScores.TryGetValue(id, out var ns) ? ns.selection : 0.5;

            var ranking = candidates.Select(c => new CandidateRow(
                c.id, c.section, c.sourceType, c.detScore, c.sel, c.inc, c.tokenCost,
                detNorms.GetValueOrDefault(c.id, 0),
                GetNeuralScore(c.id),
                calibratedScores.GetValueOrDefault(c.id, 0.5),
                alpha * detNorms.GetValueOrDefault(c.id, 0) + (1 - alpha) * GetNeuralScore(c.id),
                alpha * detNorms.GetValueOrDefault(c.id, 0) + (1 - alpha) * calibratedScores.GetValueOrDefault(c.id, 0.5)
            )).ToList();

            var detRanking = ranking.OrderByDescending(r => r.DetNorm).ToList();
            var hybRanking = ranking.OrderByDescending(r => r.Hybrid).ToList();

            var detRanks = new Dictionary<string, int>();
            for (int i = 0; i < ranking.Count; i++) detRanks[ranking[i].Id] = i + 1;
            var hybRanks = new Dictionary<string, int>();
            for (int i = 0; i < ranking.Count; i++) hybRanks[ranking[i].Id] = i + 1;

            double rankDeltaSum = 0;
            int disagreementCount = 0;
            double threshold = CalibrationSelectionThreshold(alpha, ranking);
            foreach (var r in ranking)
            {
                int dr = detRanks.GetValueOrDefault(r.Id, 0);
                int hr = hybRanks.GetValueOrDefault(r.Id, 0);
                rankDeltaSum += Math.Abs(dr - hr);
                bool hybridWouldSelect = r.Hybrid >= threshold;
                if (r.Sel != hybridWouldSelect) disagreementCount++;
            }
            double meanRankDelta = ranking.Count > 0 ? rankDeltaSum / ranking.Count : 0;

            int TopKChurn(int k)
            {
                var detTopK = detRanking.Take(k).Select(r => r.Id).ToHashSet();
                var hybTopK = hybRanking.Take(k).Select(r => r.Id).ToHashSet();
                return k - detTopK.Intersect(hybTopK).Count();
            }

            alphaResults.Add(new
            {
                Alpha = alpha,
                NeuralWeight = Math.Round(1 - alpha, 2),
                DeterministicWeight = alpha,
                MeanRankDelta = Math.Round(meanRankDelta, 4),
                SelectionDisagreementCount = disagreementCount,
                SelectionDisagreementRate = ranking.Count > 0 ? Math.Round((double)disagreementCount / ranking.Count, 4) : 0,
                Top3Churn = TopKChurn(3),
                Top5Churn = TopKChurn(5),
                Top10Churn = TopKChurn(10),
                MeanHybridScore = Math.Round(ranking.Average(r => r.Hybrid), 4),
                MeanNeuralScore = Math.Round(ranking.Average(r => r.Neural), 4),
                MeanDetScoreNorm = Math.Round(ranking.Average(r => r.DetNorm), 4)
            });
        }

        // === Write hybrid-shadow-evaluation.json ===
        var evalReport = new
        {
            GeneratedAt = now,
            V14GateReady = v14GateReady,
            V15NeuralOnlyInShadow = true,
            TotalCandidates = N,
            Alphas = Alphas,
            AlphaSweepResults = alphaResults,
            Coverage = new
            {
                CoveredSections = sectionSet.ToArray(),
                CoverageLimited = coverageLimited,
                MissingSectionsNote = coverageLimited ? $"Sections not in V14 smoke trace: {SectionsMissing}" : "Full coverage"
            },
            RuntimeSafety = new
            {
                BlendAlpha = 1.0,
                NeuralBiasActive = false,
                RuntimeInfluenceAllowed = false,
                PackageOutputChanged = false,
                VectorBindingChanged = false,
                RuntimePromotionApplied = false
            }
        };
        File.WriteAllText(Path.Combine(v16Dir, "hybrid-shadow-evaluation.json"),
            JsonSerializer.Serialize(evalReport, new JsonSerializerOptions { WriteIndented = true }));

        // === Write neural-calibration-shadow.json ===
        var calibrationReport = new
        {
            GeneratedAt = now,
            CalibrationMethod = "Binary logistic regression (neural selection score → P(candidate selected))",
            CalibrationCoefficients = new { a = Math.Round(calibA, 6), b = Math.Round(calibB, 6) },
            LossFunction = "Binary cross-entropy",
            FinalLoss = Math.Round(calibLoss, 6),
            PairwiseRanking = new
            {
                TotalPairs = pairwiseTotal,
                CorrectPairs = pairwiseCorrect,
                Accuracy = Math.Round(pairwiseAccuracy, 4),
                Interpretation = pairwiseAccuracy >= 0.7 ? "strong pairwise ordering" : pairwiseAccuracy >= 0.55 ? "weak pairwise ordering" : "random-level pairwise ordering",
                Limitation = "V15 neural scores are near-constant (~0.5) due to seeded-weights MLP without training. Calibration cannot improve upon uniform scores."
            },
            PerCandidateCalibration = calibEntries.Select(e => new
            {
                candidateId = e.id,
                originalNeuralScore = Math.Round(e.neural, 4),
                calibratedScore = Math.Round(calibratedScores.GetValueOrDefault(e.id, 0.5), 4),
                selectedLabel = e.selLabel > 0.5,
                calibratedProbability = Math.Round(1.0 / (1.0 + Math.Exp(-(calibA * e.neural + calibB))), 4),
                successProxy = Math.Round(e.successProxy, 4),
                costEfficiency = Math.Round(e.costEfficiency, 4)
            }),
            CalibrationWarning = "Offline shadow calibration only. Does not affect runtime scoring. V16 does not ship calibration coefficients to production.",
            OfflineShadowCalibration = true
        };
        File.WriteAllText(Path.Combine(v16Dir, "neural-calibration-shadow.json"),
            JsonSerializer.Serialize(calibrationReport, new JsonSerializerOptions { WriteIndented = true }));

        // === Write v16-readiness-gate.json ===
        bool v16ShadowReady = v14GateReady && N > 0 && alphaResults.Count == Alphas.Length;
        bool productionGeneralizationReady = v16ShadowReady && !coverageLimited;
        var gate = new
        {
            GeneratedAt = now,
            V16ShadowEvaluationReady = v16ShadowReady,
            RuntimeInfluenceAllowed = false,
            ProductionGeneralizationReady = productionGeneralizationReady,
            BlockedByCoverage = coverageLimited ? $"Missing sections: {SectionsMissing}" : "none",
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            V14GatePreserved = v14GateReady,
            NextCoverageRequirements = coverageLimited
                ? new[] { "Run V14 runtime-trace-smoke against a corpus that includes related_context candidates (requires context store items with graph-expanded IDs and whitelisted relation types)", "Run V14 runtime-trace-smoke against a legacy-path corpus to cover legacy/raw sections", "Re-run V15 and V16 against full-coverage trace before declaring ProductionGeneralizationReady" }
                : new[] { "All coverage requirements met" },
            Interpretability = "V16 shadow evaluation uses calibratable hybrid scoring to understand neural vs deterministic trade-offs without runtime binding. Results are informational only."
        };
        File.WriteAllText(Path.Combine(v16Dir, "v16-readiness-gate.json"),
            JsonSerializer.Serialize(gate, new JsonSerializerOptions { WriteIndented = true }));

        // === Write markdown reports ===
        WriteMarkdownHybridEval(v16Dir, now, N, alphaResults, sectionSet, coverageLimited, v14GateReady);
        WriteMarkdownCalibration(v16Dir, now, calibA, calibB, calibLoss, pairwiseAccuracy, pairwiseTotal, pairwiseCorrect, calibEntries, calibratedScores);
        WriteMarkdownGate(v16Dir, now, v16ShadowReady, productionGeneralizationReady, coverageLimited, v14GateReady);
    }

    private static (double a, double b, double loss) FitLogisticBinary(List<(double x, double label)> data)
    {
        if (data.Count == 0) return (0, 0, 0);
        double a = 0, b = 0;
        double lr = 0.01;
        int epochs = 500;
        for (int e = 0; e < epochs; e++)
        {
            double gradA = 0, gradB = 0, totalLoss = 0;
            foreach (var (x, y) in data)
            {
                double z = a * x + b;
                double p = 1.0 / (1.0 + Math.Exp(-z));
                p = Math.Clamp(p, 1e-7, 1 - 1e-7);
                gradA += (p - y) * x;
                gradB += (p - y);
                totalLoss += -(y * Math.Log(p) + (1 - y) * Math.Log(1 - p));
            }
            a -= lr * gradA / data.Count;
            b -= lr * gradB / data.Count;
            if (e == epochs - 1) return (a, b, totalLoss / data.Count);
        }
        return (a, b, 0);
    }

    private static double CalibrationSelectionThreshold(double alpha, List<CandidateRow> ranking)
    {
        var sorted = ranking.OrderByDescending(r => r.Hybrid).ToList();
        int selectedCount = ranking.Count(r => r.Sel);
        int k = Math.Max(1, Math.Min(selectedCount, sorted.Count));
        return k < sorted.Count ? sorted[k].Hybrid : sorted.Last().Hybrid;
    }

    private static void WriteMarkdownHybridEval(string dir, string now, int N, List<object> alphaResults, HashSet<string> sections, bool covLimited, bool gate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16 Hybrid Scoring Shadow Evaluation");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"Candidates: {N}");
        sb.AppendLine($"V14 Gate: {(gate ? "PASSED" : "FAILED")}");
        sb.AppendLine($"Coverage Limited: {covLimited}");
        sb.AppendLine($"Sections covered: {string.Join(", ", sections)}");
        sb.AppendLine();
        sb.AppendLine("## Alpha Sweep Results");
        sb.AppendLine("| Alpha | Neural Wt | Mean Rank Delta | Selection Disagree | Top3 Churn | Top5 Churn | Top10 Churn | Mean Hybrid |");
        sb.AppendLine("|-------|-----------|-----------------|--------------------|------------|------------|-------------|-------------|");
        foreach (dynamic r in alphaResults)
        {
            sb.AppendLine($"| {r.Alpha:F1} | {r.NeuralWeight:F1} | {r.MeanRankDelta:F4} | {r.SelectionDisagreementCount} ({r.SelectionDisagreementRate:P0}) | {r.Top3Churn} | {r.Top5Churn} | {r.Top10Churn} | {r.MeanHybridScore:F4} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Runtime Safety");
        sb.AppendLine("- BlendAlpha: 1.0 (runtime)");
        sb.AppendLine("- NeuralBiasActive: false");
        sb.AppendLine("- RuntimeInfluenceAllowed: false");
        sb.AppendLine("- PackageOutputChanged: false");
        File.WriteAllText(Path.Combine(dir, "hybrid-shadow-evaluation.md"), sb.ToString());
    }

    private static void WriteMarkdownCalibration(string dir, string now, double a, double b, double loss, double acc, int total, int correct, List<(string id, double det, double norm, double neural, double label, double proxy, double ce)> entries, Dictionary<string, double> calibrated)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16 Neural Calibration Shadow Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"Method: Binary logistic regression");
        sb.AppendLine($"Coefficients: a={a:F6}, b={b:F6}");
        sb.AppendLine($"Final BCE loss: {loss:F6}");
        sb.AppendLine($"Pairwise ranking accuracy: {acc:P2} ({correct}/{total} pairs)");
        sb.AppendLine();
        sb.AppendLine("## Per-Candidate Calibration");
        sb.AppendLine("| Candidate | Neural Score | Calibrated Score | Label | Calibrated Prob |");
        sb.AppendLine("|-----------|-------------|-----------------|-------|----------------|");
        foreach (var e in entries.Take(17))
        {
            double calProb = 1.0 / (1.0 + Math.Exp(-(a * e.neural + b)));
            sb.AppendLine($"| {e.id} | {e.neural:F4} | {calibrated.GetValueOrDefault(e.id, 0.5):F4} | {e.label:F0} | {calProb:F4} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Note");
        sb.AppendLine("Offline shadow calibration only. Not deployed to runtime.");
        File.WriteAllText(Path.Combine(dir, "neural-calibration-shadow.md"), sb.ToString());
    }

    private static void WriteMarkdownGate(string dir, string now, bool shadowReady, bool prodReady, bool covLimited, bool v14Gate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16 Readiness Gate");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine();
        sb.AppendLine($"- V16ShadowEvaluationReady: {shadowReady}");
        sb.AppendLine($"- ProductionGeneralizationReady: {prodReady}");
        sb.AppendLine($"- CoverageLimited: {covLimited}");
        sb.AppendLine($"- V14 Gate Preserved: {v14Gate}");
        sb.AppendLine($"- RuntimeInfluenceAllowed: false");
        sb.AppendLine($"- PackageOutputChanged: false");
        sb.AppendLine($"- RuntimePromotionApplied: false");
        sb.AppendLine($"- VectorBindingChanged: false");
        if (covLimited) sb.AppendLine("\nBlocked: coverage gaps prevent production generalization declaration.");
        File.WriteAllText(Path.Combine(dir, "v16-readiness-gate.md"), sb.ToString());
    }
}
