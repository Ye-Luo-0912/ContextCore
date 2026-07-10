using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V15;

public sealed class NeuralDryRunBuilder
{
    private const int InputDim = 10;
    private const int HiddenDim = 8;
    private const int OutputDim = 3;
    private const int Seed = 42;

    public void BuildAndWrite(string outputDir)
    {
        var v15Dir = Path.Combine(outputDir, "learning", "v15");
        Directory.CreateDirectory(v15Dir);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var featurePath = Path.Combine(outputDir, "learning", "v14", "feature-store.jsonl");
        var feedbackPath = Path.Combine(outputDir, "learning", "v14", "feedback-events.jsonl");
        var gatePath = Path.Combine(outputDir, "learning", "v14", "foundation-gate.json");

        // Read V14 feature store
        var traceRows = new List<JsonElement>();
        if (File.Exists(featurePath))
        {
            foreach (var line in File.ReadLines(featurePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { traceRows.Add(JsonDocument.Parse(line).RootElement); }
                catch { }
            }
        }

        // Read V14 feedback events
        var feedbackEvents = new List<JsonElement>();
        if (File.Exists(feedbackPath))
        {
            foreach (var line in File.ReadLines(feedbackPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { feedbackEvents.Add(JsonDocument.Parse(line).RootElement); }
                catch { }
            }
        }

        // Read V14 gate for precondition check
        bool v14GateReady = false;
        if (File.Exists(gatePath))
        {
            try
            {
                var gate = JsonDocument.Parse(File.ReadAllText(gatePath)).RootElement;
                if (gate.TryGetProperty("LearningDataPipelineReady", out var ldp) && ldp.GetBoolean())
                    v14GateReady = true;
            }
            catch { }
        }

        int totalCandidates = traceRows.Count;

        // Extract feature vectors from trace rows
        var featureVectors = new List<(string candidateId, float[] fv, double detScore, bool selected, bool included, string section, int sourceType, int authority, int strategyType, double tokenCost)>();
        foreach (var row in traceRows)
        {
            try
            {
                var cid = row.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                var st = row.TryGetProperty("sourceType", out var s) ? (int)(s.GetByte()) : 1;
                var auth = row.TryGetProperty("authority", out var a) ? (int)(a.GetByte()) : 1;
                var strat = row.TryGetProperty("strategyType", out var sg) ? (int)(sg.GetByte()) : 1;
                var detScore = row.TryGetProperty("deterministicScore", out var ds) ? ds.GetDouble() : 0;
                var sel = row.TryGetProperty("selectedByScoring", out var sb) && sb.GetBoolean();
                var inc = row.TryGetProperty("includedInPackage", out var ip) && ip.GetBoolean();
                var sec = row.TryGetProperty("section", out var sn) ? sn.GetString() ?? "" : "";
                var tc = row.TryGetProperty("tokenCost", out var tk) ? tk.GetDouble() : 0;

                var fv = BuildFeatureVector(st, auth, strat, detScore, tc, sel, inc);
                featureVectors.Add((cid, fv, detScore, sel, inc, sec, st, auth, strat, tc));
            }
            catch { }
        }

        // Build feedback signal map
        var feedbackMap = new Dictionary<string, (bool selected, bool included, double successProxy)>();
        foreach (var fb in feedbackEvents)
        {
            try
            {
                var cid = fb.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                var sel = fb.TryGetProperty("selected", out var s) && s.GetBoolean();
                var inc = fb.TryGetProperty("includedInPackage", out var i) && i.GetBoolean();
                var sp = fb.TryGetProperty("downstreamSuccessProxy", out var dp) ? dp.GetDouble() : 0;
                feedbackMap[cid] = (sel, inc, sp);
            }
            catch { }
        }

        // === Deterministic MLP (10→8→3) with seeded random weights ===
        var (w1, b1, w2, b2) = BuildDeterministicMlpWeights(Seed);

        // Forward pass for each candidate
        var comparisonRows = new List<object>();
        var allSelectionScores = new List<double>();
        var allDetScores = new List<double>();
        int noneProbabilityDrops = 0;
        int neuralAgreesWithDeterministic = 0;

        foreach (var (cid, fv, detScore, sel, inc, sec, st, auth, strat, tc) in featureVectors)
        {
            var output = ForwardPass(fv, w1, b1, w2, b2);
            double neuralSelection = output[0];
            double neuralRank = output[1];
            double neuralDropProb = output[2];

            // Hybrid blend: alpha=1.0 (V15 dry run, no neural bias in runtime)
            double hybridScore = detScore; // pure deterministic in V15 dry run

            // Normalize det score for comparison (clamp to [0..1])
            double normDetScore = Math.Clamp(detScore / 110.0, 0, 1);

            allSelectionScores.Add(neuralSelection);
            allDetScores.Add(normDetScore);

            bool agreeDirection = (neuralSelection >= 0.5 && sel) || (neuralSelection < 0.5 && !sel);
            if (agreeDirection) neuralAgreesWithDeterministic++;
            if (neuralDropProb >= 0.5) noneProbabilityDrops++;

            comparisonRows.Add(new
            {
                candidateId = cid,
                section = sec,
                sourceType = st, authority = auth, strategyType = strat,
                deterministicScore = Math.Round(detScore, 4),
                deterministicScoreNormalized = Math.Round(normDetScore, 4),
                neuralSelectionScore = Math.Round(neuralSelection, 4),
                neuralRankingScore = Math.Round(neuralRank, 4),
                neuralDropProbability = Math.Round(neuralDropProb, 4),
                hybridScore = Math.Round(hybridScore, 4),
                selected = sel,
                includedInPackage = inc,
                neuralAgrees = agreeDirection
            });
        }

        // Compute comparison statistics
        int n = allSelectionScores.Count;
        double[] detSorted = allDetScores.ToArray();
        double[] neuralSorted = allSelectionScores.ToArray();

        double spearmanRho = ComputeSpearmanRho(detSorted, neuralSorted);
        double meanDet = allDetScores.Count > 0 ? allDetScores.Average() : 0;
        double meanNeural = allSelectionScores.Count > 0 ? allSelectionScores.Average() : 0;
        double agreementRate = n > 0 ? (double)neuralAgreesWithDeterministic / n : 0;

        // === Write neural-selection-dry-run-report.json ===
        var dryRunReport = new
        {
            GeneratedAt = now,
            V14GateReady = v14GateReady,
            LearningDataPipelineReady = v14GateReady,
            NeuralBiasActive = false,
            NeuralOnlyInShadowReport = true,
            PackageOutputChanged = false,
            VectorBindingChanged = false,
            RuntimePromotionApplied = false,
            Summary = new
            {
                TotalCandidates = totalCandidates,
                FeatureDimension = InputDim,
                ModelArchitecture = "10→8→3 (MLP dry-run, seeded weights, no training)",
                TotalParameters = (InputDim * HiddenDim + HiddenDim) + (HiddenDim * OutputDim + OutputDim),
                DeterministicSeed = Seed,
                SpearmanRho = Math.Round(spearmanRho, 4),
                PearsonInterpretation = spearmanRho >= 0.8 ? "strong correlation" : spearmanRho >= 0.5 ? "moderate correlation" : "weak correlation",
                MeanDeterministicScoreNormalized = Math.Round(meanDet, 4),
                MeanNeuralSelectionScore = Math.Round(meanNeural, 4),
                SelectionDirectionAgreement = Math.Round(agreementRate, 4),
                NeuralDropCandidates = noneProbabilityDrops,
                RuntimeImpact = "none — neural scores are shadow-only, not fed to any runtime pipeline",
                V15Status = "dry-run complete, no production binding"
            },
            PerCandidate = comparisonRows
        };
        File.WriteAllText(Path.Combine(v15Dir, "neural-selection-dry-run-report.json"),
            JsonSerializer.Serialize(dryRunReport, new JsonSerializerOptions { WriteIndented = true }));

        // === Write hybrid-shadow-comparison.json ===
        var hybridComparison = new
        {
            GeneratedAt = now,
            ComparisonMethod = "Deterministic (V14) vs Neural (V15 dry-run MLP forward pass)",
            DeterministicScoring = new
            {
                Source = "V14 strategy + trace-derived scores",
                Preserved = true,
                RetrievalUnchanged = true
            },
            NeuralScoring = new
            {
                Source = "V15 10→8→3 MLP seeded dry-run weights",
                InRuntimePipeline = false,
                InShadowArtifactsOnly = true,
                NeuralBiasActive = false,
                BlendAlpha = 1.0,
                BlendNote = "Alpha=1.0: pure deterministic. Neural scores exist only in this shadow report."
            },
            ComparisonStatistics = new
            {
                SpearmanRankCorrelation = Math.Round(spearmanRho, 4),
                SelectionDirectionAgreementRate = Math.Round(agreementRate, 4),
                MeanDeterministicNormalized = Math.Round(meanDet, 4),
                MeanNeuralSelection = Math.Round(meanNeural, 4),
                MaxDeterministicNormalized = allDetScores.Count > 0 ? Math.Round(allDetScores.Max(), 4) : 0,
                MaxNeuralSelection = allSelectionScores.Count > 0 ? Math.Round(allSelectionScores.Max(), 4) : 0,
                MinDeterministicNormalized = allDetScores.Count > 0 ? Math.Round(allDetScores.Min(), 4) : 0,
                MinNeuralSelection = allSelectionScores.Count > 0 ? Math.Round(allSelectionScores.Min(), 4) : 0
            },
            SafetyAssurance = new
            {
                PackageOutputChanged = false,
                VectorBindingChanged = false,
                RuntimePromotionApplied = false,
                NeuralBiasActive = false,
                NeuralScoresOnlyInShadow = true,
                V14GatePreserved = v14GateReady
            }
        };
        File.WriteAllText(Path.Combine(v15Dir, "hybrid-shadow-comparison.json"),
            JsonSerializer.Serialize(hybridComparison, new JsonSerializerOptions { WriteIndented = true }));

        // === Write V14/V15 preflight coverage note (dynamic from V14 artifacts) ===
        // Read V14 runtime-trace-validation for section coverage
        var v14Sections = new List<string>();
        var v14Missing = new List<string>(new[] { "related_context", "legacy/raw" });
        bool legacyRawDetected = false;
        bool relatedContextDetected = false;
        var v14ValidationPath = Path.Combine(outputDir, "learning", "v14", "runtime-candidate-trace-validation.json");
        if (File.Exists(v14ValidationPath))
        {
            try
            {
                using var vdoc = JsonDocument.Parse(File.ReadAllText(v14ValidationPath));
                if (vdoc.RootElement.TryGetProperty("SectionCoverage", out var secCov))
                {
                    foreach (var s in secCov.EnumerateArray())
                    {
                        var sName = s.TryGetProperty("Section", out var sn) ? sn.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(sName)) v14Sections.Add(sName);
                    }
                }
            }
            catch { }
        }
        // Fallback: scan feature store for sections
        if (v14Sections.Count == 0)
        {
            var fsPath = Path.Combine(outputDir, "learning", "v14", "feature-store.jsonl");
            if (File.Exists(fsPath))
            {
                var secSet = new HashSet<string>();
                foreach (var line in File.ReadLines(fsPath).Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    try { var d = JsonDocument.Parse(line).RootElement; if (d.TryGetProperty("section", out var se)) { var s = se.GetString() ?? ""; if (!string.IsNullOrWhiteSpace(s)) secSet.Add(s); } } catch { }
                }
                v14Sections = secSet.ToList();
            }
        }
        relatedContextDetected = v14Sections.Contains("related_context");
        legacyRawDetected = v14Sections.Any(s => s.StartsWith("SmokeDoc_", StringComparison.OrdinalIgnoreCase)) || v14Sections.Contains("legacy") || v14Sections.Contains("raw");
        v14Missing.Clear();
        if (!relatedContextDetected) v14Missing.Add("related_context");
        if (!legacyRawDetected) v14Missing.Add("legacy/raw");

        var preflightNote = new
        {
            GeneratedAt = now,
            Note = "V14→V15 preflight coverage assessment (dynamically read from V14 artifacts)",
            V14CurrentCoverage = new
            {
                SectionCount = v14Sections.Count,
                SectionNames = v14Sections.OrderBy(s => s).ToArray(),
                SectionCountCanonical = v14Sections.Count,
                RelatedContextCoverageDetected = relatedContextDetected,
                LegacyRawCoverageDetected = legacyRawDetected,
                MissingSections = v14Missing.ToArray(),
                TraceRowCount = totalCandidates,
                SmokeEvaluationSource = "v14-runtime-trace-smoke dual-mode (policy + legacy)"
            },
            V15CoverageLimitation = new
            {
                CoverageSufficientForDryRun = v14Missing.Count == 0,
                Warning = v14Missing.Count > 0
                    ? "V15 dry-run feature vectors are derived from V14 smoke trace with coverage gaps. Missing sections: " + string.Join(", ", v14Missing) + ". Do not interpret selection agreement statistics as production generalization capability."
                    : "All target sections covered. V15 dry-run coverage is sufficient for shadow evaluation.",
                Recommendation = v14Missing.Count > 0
                    ? "Fix V14 smoke to cover missing sections before reducing BlendAlpha below 1.0."
                    : "V15 coverage is complete. Proceed to V16 shadow evaluation."
            }
        };
        File.WriteAllText(Path.Combine(v15Dir, "v15-preflight-coverage-note.json"),
            JsonSerializer.Serialize(preflightNote, new JsonSerializerOptions { WriteIndented = true }));

        // === Write markdown reports ===
        WriteMarkdownDryRunReport(v15Dir, now, totalCandidates, spearmanRho, agreementRate, meanDet, meanNeural, comparisonRows);
        WriteMarkdownHybridComparison(v15Dir, now, spearmanRho, agreementRate, meanDet, meanNeural, allDetScores, allSelectionScores, v14GateReady);
        WriteMarkdownPreflightNote(v15Dir, now, legacyRawDetected, relatedContextDetected, v14Sections.Count, v14Missing);
    }

    private static float[] BuildFeatureVector(int sourceType, int authority, int strategyType, double detScore, double tokenCost, bool selected, bool included)
    {
        var fv = new float[InputDim];
        fv[(int)V14.FeatureIndex.SourceType] = Math.Clamp(sourceType / 7f, 0f, 1f);
        fv[(int)V14.FeatureIndex.Authority] = Math.Clamp(authority / 5f, 0f, 1f);
        fv[(int)V14.FeatureIndex.StrategyType] = Math.Clamp(strategyType / 5f, 0f, 1f);
        fv[(int)V14.FeatureIndex.VectorScore] = (float)Math.Clamp(detScore / 110.0, 0, 1);
        fv[(int)V14.FeatureIndex.GraphScore] = (sourceType == 7 || sourceType == 3) ? 0.6f : 0.1f;
        fv[(int)V14.FeatureIndex.MemoryScore] = (authority == 5 || authority == 1) ? 0.7f : 0.2f;
        fv[(int)V14.FeatureIndex.RecencyScore] = (float)Math.Clamp(0.5 + detScore / 220.0, 0, 1);
        fv[(int)V14.FeatureIndex.TokenCost] = (float)Math.Clamp(tokenCost / 100.0, 0, 1);
        fv[(int)V14.FeatureIndex.UserPreferenceSignal] = selected ? (included ? 0.8f : 0.3f) : 0.1f;
        fv[(int)V14.FeatureIndex.HistoricalSuccessRate] = selected ? 0.65f : 0.15f;
        return fv;
    }

    private static (float[][] w1, float[] b1, float[][] w2, float[] b2) BuildDeterministicMlpWeights(int seed)
    {
        var rng = new Random(seed);
        // Xavier-uniform initialization
        float XavierStd(int fanIn, int fanOut) => MathF.Sqrt(6f / (fanIn + fanOut));

        var w1 = new float[HiddenDim][];
        var std1 = XavierStd(InputDim, HiddenDim);
        for (int i = 0; i < HiddenDim; i++)
        {
            w1[i] = new float[InputDim];
            for (int j = 0; j < InputDim; j++)
                w1[i][j] = (float)(rng.NextDouble() * 2 - 1) * std1;
        }
        var b1 = new float[HiddenDim];

        var w2 = new float[OutputDim][];
        var std2 = XavierStd(HiddenDim, OutputDim);
        for (int i = 0; i < OutputDim; i++)
        {
            w2[i] = new float[HiddenDim];
            for (int j = 0; j < HiddenDim; j++)
                w2[i][j] = (float)(rng.NextDouble() * 2 - 1) * std2;
        }
        var b2 = new float[OutputDim];

        return (w1, b1, w2, b2);
    }

    private static double[] ForwardPass(float[] input, float[][] w1, float[] b1, float[][] w2, float[] b2)
    {
        // Hidden layer: z1 = ReLU(w1 * input + b1)
        var hidden = new float[HiddenDim];
        for (int i = 0; i < HiddenDim; i++)
        {
            float sum = b1[i];
            for (int j = 0; j < InputDim; j++)
                sum += w1[i][j] * input[j];
            hidden[i] = Math.Max(0, sum); // ReLU
        }

        // Output layer: z2 = Sigmoid(w2 * hidden + b2)
        var output = new double[OutputDim];
        for (int i = 0; i < OutputDim; i++)
        {
            double sum = b2[i];
            for (int j = 0; j < HiddenDim; j++)
                sum += w2[i][j] * hidden[j];
            output[i] = 1.0 / (1.0 + Math.Exp(-sum)); // Sigmoid
        }
        return output;
    }

    private static double ComputeSpearmanRho(double[] x, double[] y)
    {
        if (x.Length < 2) return 0;
        int n = x.Length;

        int[] Rank(double[] values)
        {
            var sorted = values.Select((v, i) => (v, i)).OrderBy(p => p.v).ToArray();
            var ranks = new int[n];
            for (int i = 0; i < n; i++)
            {
                int j = i;
                while (j < n - 1 && Math.Abs(sorted[j].v - sorted[j + 1].v) < 1e-9) j++;
                double avgRank = (i + j) / 2.0 + 1;
                for (int k = i; k <= j; k++) ranks[sorted[k].i] = (int)avgRank;
                i = j;
            }
            return ranks;
        }

        var rx = Rank(x);
        var ry = Rank(y);
        double sumD2 = 0;
        for (int i = 0; i < n; i++) { var d = rx[i] - ry[i]; sumD2 += d * d; }
        return 1.0 - 6.0 * sumD2 / (n * (n * (double)n - 1));
    }

    private static void WriteMarkdownDryRunReport(string dir, string now, int total, double rho, double agree, double meanDet, double meanNeural, List<object> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V15 Neural Selection Dry-Run Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine($"- Candidates processed: {total}");
        sb.AppendLine($"- Model: 10→8→3 MLP, seeded deterministic weights (seed=42)");
        sb.AppendLine($"- Spearman rho (det vs neural): {rho:F4}");
        sb.AppendLine($"- Selection direction agreement: {agree:P2}");
        sb.AppendLine($"- Mean deterministic (normalized): {meanDet:F4}");
        sb.AppendLine($"- Mean neural selection score: {meanNeural:F4}");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine("- NeuralBiasActive: false");
        sb.AppendLine("- PackageOutputChanged: false");
        sb.AppendLine("- VectorBindingChanged: false");
        sb.AppendLine("- RuntimePromotionApplied: false");
        sb.AppendLine("- Neural scores: shadow artifacts only, not in runtime pipeline");
        sb.AppendLine();
        sb.AppendLine("## Per-Candidate Comparison");
        sb.AppendLine("| Candidate | Section | Deterministic | Neural Selection | Neural Rank | Drop Prob | Agrees |");
        sb.AppendLine("|-----------|---------|---------------|------------------|-------------|-----------|--------|");
        foreach (dynamic r in rows.Take(17))
        {
            sb.AppendLine($"| {r.candidateId} | {r.section} | {r.deterministicScoreNormalized:F4} | {r.neuralSelectionScore:F4} | {r.neuralRankingScore:F4} | {r.neuralDropProbability:F4} | {(r.neuralAgrees ? "yes" : "no")} |");
        }
        File.WriteAllText(Path.Combine(dir, "neural-selection-dry-run-report.md"), sb.ToString());
    }

    private static void WriteMarkdownHybridComparison(string dir, string now, double rho, double agree, double meanDet, double meanNeural, List<double> detScores, List<double> neuralScores, bool gateReady)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V15 Hybrid Shadow Comparison");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine();
        sb.AppendLine("## Deterministic vs Neural Score Comparison");
        sb.AppendLine($"- Spearman rank correlation: {rho:F4}");
        sb.AppendLine($"- Direction agreement: {agree:P2}");
        sb.AppendLine($"- Mean deterministic (normalized): {meanDet:F4}");
        sb.AppendLine($"- Mean neural selection: {meanNeural:F4}");
        sb.AppendLine();
        sb.AppendLine("## Scoring Pipeline Status");
        sb.AppendLine("- BlendAlpha: 1.0 (pure deterministic, V15 dry-run)");
        sb.AppendLine("- Neural scores: shadow-only, not in runtime");
        sb.AppendLine("- V14 foundation gate: " + (gateReady ? "PASSED" : "FAILED"));
        sb.AppendLine();
        sb.AppendLine("## Safety Gates");
        sb.AppendLine("- PackageOutputChanged: false");
        sb.AppendLine("- VectorBindingChanged: false");
        sb.AppendLine("- RuntimePromotionApplied: false");
        sb.AppendLine("- NeuralBiasActive: false");
        File.WriteAllText(Path.Combine(dir, "hybrid-shadow-comparison.md"), sb.ToString());
    }

    private static void WriteMarkdownPreflightNote(string dir, string now, bool legacyDetected, bool relatedDetected, int sectionCount, List<string> missing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V14→V15 Preflight Coverage Note");
        sb.AppendLine();
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"Sections detected: {sectionCount}");
        sb.AppendLine($"- LegacyRawCoverageDetected: {legacyDetected}");
        sb.AppendLine($"- RelatedContextCoverageDetected: {relatedDetected}");
        if (missing.Count > 0) sb.AppendLine($"- Missing: {string.Join(", ", missing)}");
        else sb.AppendLine("- All target sections covered");
        sb.AppendLine();
        if (missing.Count > 0)
            sb.AppendLine("## Warning\nCoverage gaps remain. Do not interpret V15 metrics as production generalization.\nRe-run V14 smoke with full coverage before reducing BlendAlpha.");
        else
            sb.AppendLine("## Status\nCoverage sufficient for V16 shadow evaluation. Proceed.");
        File.WriteAllText(Path.Combine(dir, "v15-preflight-coverage-note.md"), sb.ToString());
    }
}
