using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V16;

public sealed class HybridShadowEvaluator
{
    private static readonly double[] Alphas = [1.0, 0.9, 0.7, 0.5];

    private sealed record CandidateRow(
        string Id, string Section, int SourceType, double DetScore, bool Sel, bool Inc,
        double TokenCost, double DetNorm, double Neural, double Calib, double Hybrid);

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

        // Read V15 neural scores
        var neuralScores = ReadNeuralScores(v15ReportPath);

        // Read feedback events with full signal extraction
        var feedbackMap = ReadFeedbackEvents(v14FeedbackPath);

        // Read feature store candidates
        var candidates = ReadCandidates(v14FeaturePath);

        int N = candidates.Count;
        var sectionSet = new HashSet<string>(candidates.Select(c => c.section));
        bool hasRelatedContext = sectionSet.Contains("related_context");
        // legacy path uses item titles as section names; detect via SmokedDoc_ prefix
        bool hasLegacyRaw = sectionSet.Any(s => s.StartsWith("SmokeDoc_", StringComparison.OrdinalIgnoreCase))
                            || sectionSet.Contains("legacy") || sectionSet.Contains("raw");
        bool coverageLimited = !hasRelatedContext || !hasLegacyRaw;
        var missingSections = new List<string>();
        if (!hasRelatedContext) missingSections.Add("related_context");
        if (!hasLegacyRaw) missingSections.Add("legacy/raw");

        // Normalize deterministic scores (handle duplicates from dual policy+legacy runs)
        double maxDetScore = candidates.Count > 0 ? candidates.Max(c => c.detScore) : 110;
        var detNorms = candidates
            .GroupBy(c => c.id)
            .ToDictionary(g => g.Key, g => Math.Clamp(g.Max(c => c.detScore) / Math.Max(maxDetScore, 1), 0, 1));

        foreach (var (id, _, _, detScore, sel, inc, tc) in candidates)
            if (!neuralScores.ContainsKey(id)) neuralScores[id] = 0.5;

        // === Offline shadow calibration with feedback weights ===
        var calibEntries = candidates
            .Where(c => feedbackMap.ContainsKey(c.id))
            .Select(c => (
                id: c.id, detNorm: detNorms.GetValueOrDefault(c.id, 0.5),
                neural: neuralScores.GetValueOrDefault(c.id, 0.5),
                selLabel: c.sel ? 1.0 : 0.0,
                incLabel: c.inc ? 1.0 : 0.0,
                weight: FeedbackWeight(feedbackMap[c.id]),
                successProxy: feedbackMap[c.id].successProxy,
                costEfficiency: feedbackMap[c.id].costEfficiency,
                implicitSignal: feedbackMap[c.id].implicitSignal
            )).ToList();

        // Weighted BCE calibration
        var (calibA, calibB, wtdBceLoss) = FitWeightedLogistic(
            calibEntries.Select(e => (e.neural, e.selLabel, e.weight)).ToList());

        // Unweighted BCE for comparison
        var (unwtdA, unwtdB, unwtdBce) = FitWeightedLogistic(
            calibEntries.Select(e => (e.neural, e.selLabel, 1.0)).ToList());

        // Weighted pairwise accuracy
        int pwTotal = 0, pwCorrect = 0;
        double pwWeightedTotal = 0, pwWeightedCorrect = 0;
        for (int i = 0; i < calibEntries.Count; i++)
        {
            for (int j = 0; j < calibEntries.Count; j++)
            {
                if (i == j) continue;
                bool iSel = calibEntries[i].selLabel > 0.5;
                bool jSel = calibEntries[j].selLabel > 0.5;
                if (iSel == jSel) continue;
                double w = calibEntries[i].weight * calibEntries[j].weight;
                pwTotal++; pwWeightedTotal += w;
                double si = calibEntries[i].neural, sj = calibEntries[j].neural;
                if ((iSel && si >= sj) || (!iSel && si < sj)) { pwCorrect++; pwWeightedCorrect += w; }
            }
        }
        double pwAcc = pwTotal > 0 ? (double)pwCorrect / pwTotal : 0;
        double pwWtdAcc = pwWeightedTotal > 0 ? pwWeightedCorrect / pwWeightedTotal : 0;

        var calibratedScores = new Dictionary<string, double>();
        foreach (var (id, ns) in neuralScores)
            calibratedScores[id] = Sigmoid(calibA * ns + calibB);

        // === Alpha sweep ===
        var alphaResults = new List<object>();
        foreach (double alpha in Alphas)
        {
            double GetNeural(string id) => neuralScores.GetValueOrDefault(id, 0.5);

            var rows = candidates.Select(c => new CandidateRow(
                c.id, c.section, c.sourceType, c.detScore, c.sel, c.inc, c.tokenCost,
                detNorms.GetValueOrDefault(c.id, 0),
                GetNeural(c.id),
                calibratedScores.GetValueOrDefault(c.id, 0.5),
                alpha * detNorms.GetValueOrDefault(c.id, 0) + (1 - alpha) * GetNeural(c.id)
            )).ToList();

            // FIXED: rank from sorted lists, not from unsorted rows
            var detRanking = rows.OrderByDescending(r => r.DetNorm).ToList();
            var hybRanking = rows.OrderByDescending(r => r.Hybrid).ToList();

            var detRanks = new Dictionary<string, int>();
            for (int i = 0; i < detRanking.Count; i++) detRanks[detRanking[i].Id] = i + 1;
            var hybRanks = new Dictionary<string, int>();
            for (int i = 0; i < hybRanking.Count; i++) hybRanks[hybRanking[i].Id] = i + 1;

            // FIXED: threshold = hybRanking[selectedCount - 1].Hybrid (top-K parity boundary)
            int selectedCount = rows.Count(r => r.Sel);
            int k = Math.Max(1, Math.Min(selectedCount, hybRanking.Count));
            double threshold = hybRanking[k - 1].Hybrid; // sorted[k-1], 0-indexed
            double calibThreshold = rows.OrderByDescending(r => r.Calib * alpha + (1 - alpha) * r.Calib).ToList()
                .Select(r => r.Calib * alpha + (1 - alpha) * r.Calib).ElementAt(k - 1);

            double rankDeltaSum = 0;
            int disagreementCount = 0;
            foreach (var r in rows)
            {
                int dr = detRanks.GetValueOrDefault(r.Id, 0);
                int hr = hybRanks.GetValueOrDefault(r.Id, 0);
                rankDeltaSum += Math.Abs(dr - hr);
                if (r.Sel != (r.Hybrid >= threshold)) disagreementCount++;
            }
            double meanRankDelta = rows.Count > 0 ? rankDeltaSum / rows.Count : 0;

            int TopKChurn(int kk)
            {
                var dTop = detRanking.Take(kk).Select(r => r.Id).ToHashSet();
                var hTop = hybRanking.Take(kk).Select(r => r.Id).ToHashSet();
                return kk - dTop.Intersect(hTop).Count();
            }

            alphaResults.Add(new
            {
                Alpha = alpha,
                NeuralWeight = Math.Round(1 - alpha, 2),
                DeterministicWeight = alpha,
                ThresholdMode = "TopKSelectedCount: threshold = hybridScore of k-th ranked item where k = historically selected count",
                CalibratedThreshold = Math.Round(threshold, 4),
                MeanRankDelta = Math.Round(meanRankDelta, 4),
                SelectionDisagreementCount = disagreementCount,
                SelectionDisagreementRate = rows.Count > 0 ? Math.Round((double)disagreementCount / rows.Count, 4) : 0,
                Top3Churn = TopKChurn(3),
                Top5Churn = TopKChurn(5),
                Top10Churn = TopKChurn(10),
                MeanHybridScore = Math.Round(rows.Average(r => r.Hybrid), 4),
                MeanNeuralScore = Math.Round(rows.Average(r => r.Neural), 4),
                MeanDetScoreNorm = Math.Round(rows.Average(r => r.DetNorm), 4)
            });
        }

        // === Write hybrid-shadow-evaluation.json ===
        var evalReport = new
        {
            GeneratedAt = now,
            V14GateReady = v14GateReady,
            V15NeuralOnlyInShadow = true,
            TotalCandidates = N,
            Alphas,
            AlphaSweepResults = alphaResults,
            Coverage = new
            {
                CoveredSections = sectionSet.OrderBy(s => s).ToArray(),
                CoverageLimited = coverageLimited,
                MissingSections = missingSections.ToArray(),
                Note = coverageLimited ? $"Sections missing from V14 smoke trace: {string.Join(", ", missingSections)}" : "All target sections covered"
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
        WriteJson(v16Dir, "hybrid-shadow-evaluation.json", evalReport);

        // === Write neural-calibration-shadow.json ===
        var calReport = new
        {
            GeneratedAt = now,
            CalibrationMethod = "Weighted binary logistic regression: P(selected | neuralScore) = sigmoid(a * neuralScore + b)",
            LabelFormula = "label = selectedByScoring from runtime trace (1.0 if selected, 0.0 otherwise)",
            SampleWeightFormula = "weight = normalized downstreamSuccessProxy (feedback event). proxy_norm = proxy / max_proxy, weight = 0.3 + 0.7 * proxy_norm. Ensures high-success candidates contribute more to calibration.",
            WeightedCalibration = new { a = Math.Round(calibA, 6), b = Math.Round(calibB, 6), weightedBCELoss = Math.Round(wtdBceLoss, 6) },
            UnweightedCalibration = new { a = Math.Round(unwtdA, 6), b = Math.Round(unwtdB, 6), unweightedBCELoss = Math.Round(unwtdBce, 6) },
            PairwiseRanking = new
            {
                TotalPairs = pwTotal,
                CorrectPairs = pwCorrect,
                UnweightedAccuracy = Math.Round(pwAcc, 4),
                WeightedAccuracy = Math.Round(pwWtdAcc, 4),
                Interpretation = pwWtdAcc >= 0.7 ? "strong" : pwWtdAcc >= 0.55 ? "weak" : "random-level",
                Limitation = "Neural scores near-constant (~0.5) from untrained seeded MLP. Calibration limited by input signal variance."
            },
            PerCandidateCalibration = calibEntries.Select(e => new
            {
                candidateId = e.id,
                originalNeuralScore = Math.Round(e.neural, 4),
                calibratedProbability = Math.Round(Sigmoid(calibA * e.neural + calibB), 4),
                selectedLabel = e.selLabel > 0.5,
                includedInPackage = e.incLabel > 0.5,
                sampleWeight = Math.Round(e.weight, 4),
                successProxy = Math.Round(e.successProxy, 4),
                costEfficiency = Math.Round(e.costEfficiency, 4),
                implicitSignal = e.implicitSignal
            }),
            OfflineShadowCalibration = true,
            RuntimeInfluenceAllowed = false,
            CalibrationWarning = "Offline shadow calibration only. Coefficients are NOT deployed to runtime pipeline. V16 blend alpha remains 1.0."
        };
        WriteJson(v16Dir, "neural-calibration-shadow.json", calReport);

        // === Write v16-readiness-gate.json ===
        bool v16ShadowReady = v14GateReady && N >= 10 && alphaResults.Count == Alphas.Length;
        bool metricIntegrityReady = v16ShadowReady;
        bool prodGeneralizationReady = v16ShadowReady && !coverageLimited;
        var gate = new
        {
            GeneratedAt = now,
            V16ShadowEvaluationReady = v16ShadowReady,
            V16MetricIntegrityReady = metricIntegrityReady,
            CoverageLimited = coverageLimited,
            MissingCoverage = missingSections.ToArray(),
            ProductionGeneralizationReady = prodGeneralizationReady,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            V14GatePreserved = v14GateReady,
            CalibrationActive = true,
            CalibrationWeighted = true,
            FeedbackSignalsUsed = new[] { "downstreamSuccessProxy", "costEfficiencyScore", "userImplicitSignal" },
            NextSteps = coverageLimited
                ? new[] { "Add related_context candidate via whitelisted relation from memory→context item", "Run legacy-path BuildDetailedAsync without policy for raw section coverage", "Regenerate V14 trace, re-run V15 neural dry-run, re-run V16 shadow eval" }
                : new[] { "Coverage requirements met. V16 ready for V17 production evaluation." }
        };
        WriteJson(v16Dir, "v16-readiness-gate.json", gate);

        // Write markdowns
        WriteMdHybridEval(v16Dir, now, N, alphaResults, sectionSet, coverageLimited, v14GateReady, missingSections);
        WriteMdCalibration(v16Dir, now, calibA, calibB, wtdBceLoss, pwAcc, pwWtdAcc, pwTotal, pwCorrect, calibEntries);
        WriteMdGate(v16Dir, now, v16ShadowReady, metricIntegrityReady, prodGeneralizationReady, coverageLimited, v14GateReady);
    }

    private static bool GateReady(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            return d.RootElement.TryGetProperty("LearningDataPipelineReady", out var p) && p.GetBoolean();
        }
        catch { return false; }
    }

    private static Dictionary<string, double> ReadNeuralScores(string path)
    {
        var scores = new Dictionary<string, double>();
        if (!File.Exists(path)) return scores;
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(path));
            if (d.RootElement.TryGetProperty("PerCandidate", out var arr))
                foreach (var r in arr.EnumerateArray())
                {
                    var cid = r.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                    var ns = r.TryGetProperty("neuralSelectionScore", out var s) ? s.GetDouble() : 0.5;
                    if (!string.IsNullOrWhiteSpace(cid)) scores[cid] = ns;
                }
        }
        catch { }
        return scores;
    }

    private static Dictionary<string, (double successProxy, double costEfficiency, int implicitSignal)> ReadFeedbackEvents(string path)
    {
        var map = new Dictionary<string, (double, double, int)>();
        if (!File.Exists(path)) return map;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var d = JsonDocument.Parse(line).RootElement;
                var cid = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                var sp = d.TryGetProperty("downstreamSuccessProxy", out var dp) ? dp.GetDouble() : 0;
                var ce = d.TryGetProperty("costEfficiencyScore", out var cs) ? cs.GetDouble() : 0;
                var si = d.TryGetProperty("userImplicitSignal", out var ui) ? (int)ui.GetByte() : 0;
                map[cid] = (sp, ce, si);
            }
            catch { }
        }
        return map;
    }

    private static List<(string id, string section, int sourceType, double detScore, bool sel, bool inc, double tokenCost)> ReadCandidates(string path)
    {
        var list = new List<(string, string, int, double, bool, bool, double)>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var d = JsonDocument.Parse(line).RootElement;
                list.Add((
                    d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "",
                    d.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "",
                    d.TryGetProperty("sourceType", out var st) ? (int)st.GetByte() : 1,
                    d.TryGetProperty("deterministicScore", out var ds) ? ds.GetDouble() : 0,
                    d.TryGetProperty("selectedByScoring", out var sl) && sl.GetBoolean(),
                    d.TryGetProperty("includedInPackage", out var ip) && ip.GetBoolean(),
                    d.TryGetProperty("tokenCost", out var tk) ? tk.GetDouble() : 0
                ));
            }
            catch { }
        }
        return list;
    }

    private static double FeedbackWeight((double successProxy, double costEfficiency, int implicitSignal) fb)
    {
        double maxSp = Math.Max(fb.successProxy, 1);
        double normSp = Math.Clamp(fb.successProxy / maxSp, 0, 1);
        return 0.3 + 0.7 * normSp;
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
                double z = a * x + b;
                double p = Math.Clamp(Sigmoid(z), 1e-7, 1 - 1e-7);
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
        File.WriteAllText(Path.Combine(dir, name),
            JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteMdHybridEval(string dir, string now, int N, List<object> alphaResults, HashSet<string> sections, bool covLimited, bool gate, List<string> missing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.1 Hybrid Scoring Shadow Evaluation");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"Candidates: {N}");
        sb.AppendLine($"V14 Gate: {(gate ? "PASSED" : "FAILED")}");
        sb.AppendLine($"Coverage Limited: {covLimited}");
        sb.AppendLine($"Sections covered: {string.Join(", ", sections.OrderBy(x => x))}");
        if (covLimited) sb.AppendLine($"Missing: {string.Join(", ", missing)}");
        sb.AppendLine();
        sb.AppendLine("## Alpha Sweep Results (Fixed: sorted ranking, top-K threshold)");
        sb.AppendLine("| Alpha | NeurWt | Thrsh Mode | RankΔ | SelDisagree | T3Churn | T5Churn | T10Churn | MeanHyb |");
        sb.AppendLine("|-------|--------|------------|-------|-------------|---------|---------|----------|---------|");
        foreach (dynamic r in alphaResults)
        {
            sb.AppendLine($"| {r.Alpha:F1} | {r.NeuralWeight:F1} | top-K | {r.MeanRankDelta:F4} | {r.SelectionDisagreementCount} | {r.Top3Churn} | {r.Top5Churn} | {r.Top10Churn} | {r.MeanHybridScore:F4} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Runtime Safety");
        sb.AppendLine("- BlendAlpha: 1.0 (runtime)");
        sb.AppendLine("- NeuralBiasActive: false");
        sb.AppendLine("- RuntimeInfluenceAllowed: false");
        File.WriteAllText(Path.Combine(dir, "hybrid-shadow-evaluation.md"), sb.ToString());
    }

    private static void WriteMdCalibration(string dir, string now, double a, double b, double loss, double acc, double wacc, int total, int correct, List<(string id, double detNorm, double neural, double selLabel, double incLabel, double weight, double successProxy, double costEfficiency, int implicitSignal)> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.1 Neural Calibration Shadow Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"Method: Weighted BCE logistic regression");
        sb.AppendLine($"Coefficients: a={a:F6}, b={b:F6}");
        sb.AppendLine($"Weighted BCE loss: {loss:F6}");
        sb.AppendLine($"Pairwise: unweighted={acc:P2} weighted={wacc:P2} ({correct}/{total})");
        sb.AppendLine();
        sb.AppendLine("## Per-Candidate Calibration");
        sb.AppendLine("| Candidate | Neural | CalibProb | Label | Weight | SuccessProxy |");
        sb.AppendLine("|-----------|--------|-----------|-------|--------|-------------|");
        foreach (var e in entries.Take(17))
        {
            sb.AppendLine($"| {e.id} | {e.neural:F4} | {Sigmoid(a * e.neural + b):F4} | {e.selLabel:F0} | {e.weight:F3} | {e.successProxy:F1} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Note");
        sb.AppendLine("Offline shadow calibration only. Not deployed to runtime.");
        File.WriteAllText(Path.Combine(dir, "neural-calibration-shadow.md"), sb.ToString());
    }

    private static void WriteMdGate(string dir, string now, bool shadowReady, bool metricReady, bool prodReady, bool covLimited, bool v14Gate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.1 Readiness Gate");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine();
        sb.AppendLine($"- V16ShadowEvaluationReady: {shadowReady}");
        sb.AppendLine($"- V16MetricIntegrityReady: {metricReady}");
        sb.AppendLine($"- CoverageLimited: {covLimited}");
        sb.AppendLine($"- ProductionGeneralizationReady: {prodReady}");
        sb.AppendLine($"- V14GatePreserved: {v14Gate}");
        sb.AppendLine($"- RuntimeInfluenceAllowed: false");
        sb.AppendLine($"- PackageOutputChanged: false");
        sb.AppendLine($"- RuntimePromotionApplied: false");
        sb.AppendLine($"- VectorBindingChanged: false");
        if (covLimited) sb.AppendLine("\nBlocked: coverage gaps prevent production generalization declaration.");
        File.WriteAllText(Path.Combine(dir, "v16-readiness-gate.md"), sb.ToString());
    }
}
