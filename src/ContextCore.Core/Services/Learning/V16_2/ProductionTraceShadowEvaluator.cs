using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V16_2;

public sealed class ProductionTraceShadowEvaluator
{
    private static readonly double[] Alphas = [1.0, 0.9, 0.7, 0.5];

    private sealed record TraceRow(
        string RowKey, string CandidateId, string Section, int SourceType,
        double DetScore, bool SelectedByScoring, bool IncludedInPackage,
        double TokenCost, string Provenance, double DetNorm, double Neural, double Hybrid
    );

    public void BuildAndWrite(string outputDir, bool smokeOnly = false)
    {
        var vDir = Path.Combine(outputDir, "learning", "v16_2");
        Directory.CreateDirectory(vDir);
        var now = DateTimeOffset.UtcNow.ToString("O");

        var v14Feature = Path.Combine(outputDir, "learning", "v14", "feature-store.jsonl");
        var v14Feedback = Path.Combine(outputDir, "learning", "v14", "feedback-events.jsonl");
        var v15Report = Path.Combine(outputDir, "learning", "v15", "neural-selection-dry-run-report.json");
        var v14GatePath = Path.Combine(outputDir, "learning", "v14", "foundation-gate.json");

        bool v14GateReady = GateReady(v14GatePath);
        var neuralScores = ReadNeuralScores(v15Report);
        var feedbackMap = ReadFeedbackEvents(v14Feedback);
        var rawRows = ReadTraceRows(v14Feature, smokeOnly);

        int N = rawRows.Count;
        var smokeRows = rawRows.Where(r => r.Provenance == "smoke").ToList();
        var prodRows = rawRows.Where(r => r.Provenance == "production-like").ToList();
        int nSmoke = smokeRows.Count, nProd = prodRows.Count;

        bool hasRealData = nProd > 0;
        bool insufficientRealData = nProd < 8;

        var sectionSet = new HashSet<string>(rawRows.Select(r => r.Section));
        bool legacyCovered = sectionSet.Any(s => s.StartsWith("SmokeDoc_", StringComparison.OrdinalIgnoreCase)) || sectionSet.Contains("legacy") || sectionSet.Contains("raw");
        bool relatedCovered = sectionSet.Contains("related_context");

        // Global normalization
        double globalMaxProxy = feedbackMap.Values.Count > 0 ? feedbackMap.Values.Max(f => f.sp) : 1;
        double globalMaxCe = feedbackMap.Values.Count > 0 ? feedbackMap.Values.Max(f => f.ce) : 1;
        double globalMaxSig = feedbackMap.Values.Count > 0 ? feedbackMap.Values.Max(f => f.si) : 1;

        double maxDet = rawRows.Count > 0 ? rawRows.Max(r => r.DetScore) : 110;
        double GetDetNorm(double s) => Math.Clamp(s / Math.Max(maxDet, 1), 0, 1);

        var rows = rawRows.Select(r =>
        {
            double dn = GetDetNorm(r.DetScore);
            double nr = neuralScores.GetValueOrDefault(r.CandidateId, 0.5);
            return new TraceRow(r.RowKey, r.CandidateId, r.Section, r.SourceType,
                r.DetScore, r.SelectedByScoring, r.IncludedInPackage,
                r.TokenCost, r.Provenance, dn, nr, 0);
        }).ToList();

        // === Alpha sweep with per-source and combined splits ===
        var combinedAlphaResults = RunAlphaSweep(rows);
        var smokeAlphaResults = nSmoke > 0 ? RunAlphaSweep(rows.Where(r => r.Provenance == "smoke").ToList()) : new List<object>();
        var prodAlphaResults = nProd > 0 ? RunAlphaSweep(rows.Where(r => r.Provenance == "production-like").ToList()) : new List<object>();

        // Alpha=1 invariant check on combined
        bool alpha1InvariantPassed = CheckAlpha1Invariant(combinedAlphaResults);

        // === Calibration with per-source split ===
        var calDataAll = BuildCalibrationData(rows, feedbackMap, globalMaxProxy, globalMaxCe, globalMaxSig);
        var calDataSmoke = BuildCalibrationData(smokeRows.Select(r => rows.First(x => x.RowKey == r.RowKey)).ToList(), feedbackMap, globalMaxProxy, globalMaxCe, globalMaxSig);
        var calDataProd = BuildCalibrationData(prodRows.Select(r => rows.First(x => x.RowKey == r.RowKey)).ToList(), feedbackMap, globalMaxProxy, globalMaxCe, globalMaxSig);

        var calCombined = ComputeCalibrationStats(calDataAll);
        var calSmoke = nSmoke > 0 ? ComputeCalibrationStats(calDataSmoke) : null;
        var calProd = nProd > 0 ? ComputeCalibrationStats(calDataProd) : null;

        // === Write production-trace-shadow-evaluation.json ===
        WriteJson(vDir, "production-trace-shadow-evaluation.json", new
        {
            GeneratedAt = now,
            V14GateReady = v14GateReady,
            RowIdentity = "RowKey = operationId|requestId|candidateId|section|sourceType|rowIndex — provenance tagged by operationId prefix",
            TraceProvenance = new
            {
                TotalRows = N,
                SmokeControlRows = nSmoke,
                ProductionLikeRows = nProd,
                InsufficientRealTraceData = insufficientRealData,
                TraceSourceDistribution = new
                {
                    Smoke = smokeRows.Select(r => r.Section).Distinct().Count(),
                    ProductionLike = prodRows.Select(r => r.Section).Distinct().Count()
                }
            },
            Alphas,
            AlphaSweepCombined = combinedAlphaResults,
            AlphaSweepSmoke = smokeAlphaResults,
            AlphaSweepProductionLike = prodAlphaResults,
            Alpha1InvariantPassed = alpha1InvariantPassed,
            Calibration = new
            {
                Combined = calCombined,
                Smoke = calSmoke,
                ProductionLike = calProd,
                GlobalNormalization = true,
                WeightFormula = "0.5*norm(successProxy) + 0.3*norm(costEfficiency) + 0.2*norm(implicitSignal)"
            },
            RuntimeSafety = new
            {
                BlendAlpha = 1.0, NeuralBiasActive = false,
                RuntimeInfluenceAllowed = false, PackageOutputChanged = false,
                VectorBindingChanged = false, RuntimePromotionApplied = false
            }
        });

        // === Write runtime-influence-readiness-gate.json ===
        bool v162ProdTraceShadowReady = v14GateReady && N >= 10 && hasRealData;
        bool metricIntegrityReady = v162ProdTraceShadowReady && alpha1InvariantPassed;
        bool runtimeInfluenceReadinessCandidate = v162ProdTraceShadowReady && metricIntegrityReady && !insufficientRealData;
        bool prodGeneralizationReady = v162ProdTraceShadowReady && !insufficientRealData && hasRealData;

        WriteJson(vDir, "runtime-influence-readiness-gate.json", new
        {
            GeneratedAt = now,
            V16_2ProductionTraceShadowReady = v162ProdTraceShadowReady,
            V16_2MetricIntegrityReady = metricIntegrityReady,
            Alpha1InvariantPassed = alpha1InvariantPassed,
            RowKeyUniqueness = rawRows.Select(r => r.RowKey).Distinct().Count() == rawRows.Count,
            Coverage = new { LegacyRawCovered = legacyCovered, RelatedContextCovered = relatedCovered },
            InsufficientRealTraceData = insufficientRealData,
            RuntimeInfluenceAllowed = false,
            RuntimeInfluenceReadinessCandidate = runtimeInfluenceReadinessCandidate,
            ProductionGeneralizationReady = prodGeneralizationReady,
            ProductionGeneralizationNote = insufficientRealData ? "Insufficient real-trace data for generalization declaration." : "Production-like trace sufficient for shadow evaluation.",
            PackageOutputChanged = false,
            RuntimePromotionApplied = false,
            VectorBindingChanged = false,
            V14GatePreserved = v14GateReady,
            NextSteps = insufficientRealData
                ? new[] { "Re-run V14 runtime-trace-smoke (or a dedicated production collector) to accumulate more real-data trace rows", "Target: production-like rows >= 8", "After sufficient data, re-run v16_2-evaluate" }
                : new[] { "V16.2 shadow evaluation complete with sufficient data", "RuntimeInfluenceAllowed remains false", "Ready for V17 production evaluation with runtime-influence shadow gating" }
        });

        // Write markdowns
        WriteMdEval(vDir, now, N, nSmoke, nProd, combinedAlphaResults, smokeAlphaResults, prodAlphaResults, alpha1InvariantPassed, insufficientRealData, v14GateReady);
        WriteMdGate(vDir, now, v162ProdTraceShadowReady, metricIntegrityReady, runtimeInfluenceReadinessCandidate, prodGeneralizationReady, insufficientRealData, v14GateReady);
    }

    private static List<object> RunAlphaSweep(List<TraceRow> rows)
    {
        var results = new List<object>();
        if (rows.Count == 0) return results;
        foreach (double alpha in Alphas)
        {
            var swept = rows.Select(r => new TraceRow(
                r.RowKey, r.CandidateId, r.Section, r.SourceType,
                r.DetScore, r.SelectedByScoring, r.IncludedInPackage, r.TokenCost,
                r.Provenance, r.DetNorm, r.Neural, alpha * r.DetNorm + (1 - alpha) * r.Neural
            )).ToList();

            var detRank = swept.OrderByDescending(r => r.DetNorm).ToList();
            var hybRank = swept.OrderByDescending(r => r.Hybrid).ToList();

            var detRanks = new Dictionary<string, int>(); for (int i = 0; i < detRank.Count; i++) detRanks[detRank[i].RowKey] = i + 1;
            var hybRanks = new Dictionary<string, int>(); for (int i = 0; i < hybRank.Count; i++) hybRanks[hybRank[i].RowKey] = i + 1;

            int sCnt = swept.Count(r => r.SelectedByScoring);
            int iCnt = swept.Count(r => r.IncludedInPackage);
            int kS = Math.Max(1, Math.Min(sCnt, hybRank.Count));
            int kI = Math.Max(1, Math.Min(iCnt, hybRank.Count));
            double thrS = hybRank[kS - 1].Hybrid;
            double thrI = hybRank[kI - 1].Hybrid;

            double rdSum = 0; int sDis = 0, iDis = 0;
            foreach (var r in swept)
            {
                rdSum += Math.Abs(detRanks.GetValueOrDefault(r.RowKey, 0) - hybRanks.GetValueOrDefault(r.RowKey, 0));
                if (r.SelectedByScoring != (r.Hybrid >= thrS)) sDis++;
                if (r.IncludedInPackage != (r.Hybrid >= thrI)) iDis++;
            }

            int Churn(int kk) { var d = detRank.Take(kk).Select(r => r.RowKey).ToHashSet(); var h = hybRank.Take(kk).Select(r => r.RowKey).ToHashSet(); return kk - d.Intersect(h).Count(); }
            int t3 = Churn(3), t5 = Churn(5), t10 = Churn(10);
            bool a1ok = true;
            if (Math.Abs(alpha - 1.0) < 0.001) a1ok = Math.Abs(rdSum / Math.Max(swept.Count, 1)) < 0.001 && t3 == 0 && t5 == 0 && t10 == 0;

            results.Add(new { Alpha = alpha, NeuralWt = Math.Round(1 - alpha, 2),
                MeanRankDelta = Math.Round(swept.Count > 0 ? rdSum / swept.Count : 0, 4),
                ScoringDisagree = sDis, InclusionDisagree = iDis,
                Top3Churn = t3, Top5Churn = t5, Top10Churn = t10, Alpha1Invariant = a1ok });
        }
        return results;
    }

    private static bool CheckAlpha1Invariant(List<object> results)
    {
        foreach (dynamic r in results) if (Math.Abs((double)r.Alpha - 1.0) < 0.001) return (bool)r.Alpha1Invariant;
        return false;
    }

    private static List<(string rk, string cid, double neural, bool sel, bool inc, double weight, double sp, double ce, double si)> BuildCalibrationData(List<TraceRow> rows, Dictionary<string, (double sp, double ce, double si)> fb, double mxSp, double mxCe, double mxSig)
    {
        return rows.Where(r => fb.ContainsKey(r.CandidateId)).Select(r => {
            var f = fb[r.CandidateId];
            double w = Math.Max(0.1, 0.5 * Norm(f.sp, mxSp) + 0.3 * Norm(f.ce, mxCe) + 0.2 * Norm(f.si, mxSig));
            return (r.RowKey, r.CandidateId, r.Neural, r.SelectedByScoring, r.IncludedInPackage, w, f.sp, f.ce, f.si);
        }).ToList();
    }

    private static object ComputeCalibrationStats(List<(string rk, string cid, double neural, bool sel, bool inc, double weight, double sp, double ce, double si)> data)
    {
        if (data.Count == 0) return new { RowCount = 0, WeightedBCE = 0.0, UnweightedBCE = 0.0, WeightedPairwiseAcc = 0.0, UnweightedPairwiseAcc = 0.0 };

        var (wa, wb, wl) = FitLogistic(data.Select(e => (e.neural, e.sel ? 1.0 : 0.0, e.weight)).ToList());
        var (ua, ub, ul) = FitLogistic(data.Select(e => (e.neural, e.sel ? 1.0 : 0.0, 1.0)).ToList());

        int pwT = 0, pwC = 0; double pwWT = 0, pwWC = 0;
        for (int i = 0; i < data.Count; i++)
            for (int j = i + 1; j < data.Count; j++)
            {
                if (data[i].sel == data[j].sel) continue;
                pwT++; double w = data[i].weight * data[j].weight; pwWT += w;
                if ((data[i].sel && data[i].neural >= data[j].neural) || (!data[i].sel && data[i].neural < data[j].neural)) { pwC++; pwWC += w; }
            }

        return new { RowCount = data.Count, WeightedBCE = Math.Round(wl, 6), UnweightedBCE = Math.Round(ul, 6), WeightedPairwiseAcc = Math.Round(pwWT > 0 ? pwWC / pwWT : 0, 4), UnweightedPairwiseAcc = Math.Round(pwT > 0 ? (double)pwC / pwT : 0, 4) };
    }

    private static (double a, double b, double loss) FitLogistic(List<(double x, double label, double weight)> d)
    {
        if (d.Count == 0) return (0, 0, 0);
        double a = 0, b = 0, lr = 0.01;
        for (int e = 0; e < 500; e++)
        {
            double ga = 0, gb = 0, l = 0, tw = 0;
            foreach (var (x, y, w) in d)
            {
                double p = Math.Clamp(1.0 / (1.0 + Math.Exp(-(a * x + b))), 1e-7, 1 - 1e-7);
                ga += w * (p - y) * x; gb += w * (p - y);
                l += w * (-(y * Math.Log(p) + (1 - y) * Math.Log(1 - p))); tw += w;
            }
            a -= lr * ga / Math.Max(tw, 1); b -= lr * gb / Math.Max(tw, 1);
            if (e == 499) return (a, b, l / Math.Max(tw, 1));
        }
        return (a, b, 0);
    }

    private static double Norm(double v, double max) => max > 0 ? Math.Clamp(v / max, 0, 1) : 0;
    private static bool GateReady(string p) { if (!File.Exists(p)) return false; try { using var d = JsonDocument.Parse(File.ReadAllText(p)); return d.RootElement.TryGetProperty("LearningDataPipelineReady", out var x) && x.GetBoolean(); } catch { return false; } }

    private static Dictionary<string, double> ReadNeuralScores(string p)
    {
        var m = new Dictionary<string, double>(); if (!File.Exists(p)) return m;
        try { using var d = JsonDocument.Parse(File.ReadAllText(p)); if (d.RootElement.TryGetProperty("PerCandidate", out var a)) foreach (var r in a.EnumerateArray()) { var cid = r.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : ""; m[cid] = r.TryGetProperty("neuralSelectionScore", out var s) ? s.GetDouble() : 0.5; } } catch { }
        return m;
    }

    private static Dictionary<string, (double sp, double ce, double si)> ReadFeedbackEvents(string p)
    {
        var m = new Dictionary<string, (double, double, double)>(); if (!File.Exists(p)) return m;
        foreach (var l in File.ReadLines(p)) { if (string.IsNullOrWhiteSpace(l)) continue; try { var d = JsonDocument.Parse(l).RootElement; m[d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : ""] = (d.TryGetProperty("downstreamSuccessProxy", out var x) ? x.GetDouble() : 0, d.TryGetProperty("costEfficiencyScore", out var y) ? y.GetDouble() : 0, d.TryGetProperty("userImplicitSignal", out var z) ? (double)z.GetByte() : 0); } catch { } }
        return m;
    }

    private static List<TraceRow> ReadTraceRows(string p, bool smokeOnly)
    {
        var l = new List<TraceRow>(); if (!File.Exists(p)) return l;
        int idx = 0;
        foreach (var ln in File.ReadLines(p))
        {
            if (string.IsNullOrWhiteSpace(ln)) continue;
            try
            {
                var d = JsonDocument.Parse(ln).RootElement;
                var oid = d.TryGetProperty("operationId", out var o) ? o.GetString() ?? "" : "";
                string provenance = smokeOnly ? "smoke" : (oid.StartsWith("op-prod", StringComparison.OrdinalIgnoreCase) ? "production-like" : "smoke");
                var rid = d.TryGetProperty("requestId", out var r) ? r.GetString() ?? "" : "";
                var cid = d.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "" : "";
                var sec = d.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "";
                var st = d.TryGetProperty("sourceType", out var sb) ? (int)sb.GetByte() : 1;
                var ds = d.TryGetProperty("deterministicScore", out var dsj) ? dsj.GetDouble() : 0;
                var sel = d.TryGetProperty("selectedByScoring", out var sl) && sl.GetBoolean();
                var inc = d.TryGetProperty("includedInPackage", out var ip) && ip.GetBoolean();
                var tc = d.TryGetProperty("tokenCost", out var tk) ? tk.GetDouble() : 0;
                string rk = $"{oid}|{rid}|{cid}|{sec}|{st}|{idx++}";
                l.Add(new TraceRow(rk, cid, sec, st, ds, sel, inc, tc, provenance, 0, 0.5, 0));
            }
            catch { idx++; }
        }
        return l;
    }

    private static void WriteJson(string dir, string name, object obj) => File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteMdEval(string dir, string now, int N, int nS, int nP, List<object> comb, List<object> sm, List<object> pr, bool a1ok, bool insuff, bool v14)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.2 Production-Trace Shadow Evaluation");
        sb.AppendLine($"Generated: {now} | Total: {N} rows (smoke={nS}, prod-like={nP}) | V14Gate: {(v14 ? "PASS" : "FAIL")} | Alpha1Invariant: {(a1ok ? "PASS" : "FAIL")}");
        if (insuff) sb.AppendLine("## WARNING: InsufficientRealTraceData=true (production-like rows < 8)");
        sb.AppendLine("## Combined Alpha Sweep");
        sb.AppendLine("| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (dynamic r in comb) sb.AppendLine($"| {r.Alpha:F1} | {r.NeuralWt:F1} | {r.MeanRankDelta:F4} | {r.ScoringDisagree} | {r.InclusionDisagree} | {r.Top3Churn} | {r.Top5Churn} | {r.Top10Churn} | {(r.Alpha1Invariant ? "Y" : "N")} |");
        if (nS > 0) { sb.AppendLine("## Smoke Split"); sb.AppendLine("| Alpha | RankΔ | SDis | T3 | T5 | T10 |"); sb.AppendLine("|---|---|---|---|---|---|"); foreach (dynamic r in sm) sb.AppendLine($"| {r.Alpha:F1} | {r.MeanRankDelta:F4} | {r.ScoringDisagree} | {r.Top3Churn} | {r.Top5Churn} | {r.Top10Churn} |"); }
        if (nP > 0) { sb.AppendLine("## Production-Like Split"); sb.AppendLine("| Alpha | RankΔ | SDis | T3 | T5 | T10 |"); sb.AppendLine("|---|---|---|---|---|---|"); foreach (dynamic r in pr) sb.AppendLine($"| {r.Alpha:F1} | {r.MeanRankDelta:F4} | {r.ScoringDisagree} | {r.Top3Churn} | {r.Top5Churn} | {r.Top10Churn} |"); }
        sb.AppendLine("\n## Safety\nRuntimeInfluenceAllowed: false | PackageOutputChanged: false | VectorBindingChanged: false");
        File.WriteAllText(Path.Combine(dir, "production-trace-shadow-evaluation.md"), sb.ToString());
    }

    private static void WriteMdGate(string dir, string now, bool ready, bool metric, bool influence, bool gen, bool insuff, bool v14)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# V16.2 Runtime-Influence Readiness Gate");
        sb.AppendLine($"Generated: {now}");
        sb.AppendLine($"- V16_2ProductionTraceShadowReady: {ready}");
        sb.AppendLine($"- V16_2MetricIntegrityReady: {metric}");
        sb.AppendLine($"- RuntimeInfluenceAllowed: false");
        sb.AppendLine($"- RuntimeInfluenceReadinessCandidate: {influence}");
        sb.AppendLine($"- ProductionGeneralizationReady: {gen}");
        sb.AppendLine($"- V14GatePreserved: {v14}");
        if (insuff) sb.AppendLine($"- InsufficientRealTraceData: true");
        File.WriteAllText(Path.Combine(dir, "runtime-influence-readiness-gate.md"), sb.ToString());
    }
}
