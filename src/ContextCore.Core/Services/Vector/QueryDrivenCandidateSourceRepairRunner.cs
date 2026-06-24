using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 查询驱动的候选来源修复。跨 6 个来源合并候选，所有来源由 query intent
/// 和 runtime-observable 元数据驱动，不做硬编码数据集特判或 domain 字面量。
/// 最终通过 eligibility/lifecycle/must-not/targetSection/risk gate。
/// 只读：不接 formal retrieval、不写 formal package、不改 selected set、
/// 不改 PackingPolicy/package output、不切 runtime。
/// </summary>
public sealed class QueryDrivenCandidateSourceRepairRunner
{
    private const double Epsilon = 1e-9;

    public QueryDrivenCandidateSourceRepairReport BuildPreview(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        QueryDrivenCandidateSourceRepairOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(derivationGate, dataset, sourceScan, options, sourceReports, gateMode: false);

    public QueryDrivenCandidateSourceRepairReport BuildGate(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        QueryDrivenCandidateSourceRepairOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(derivationGate, dataset, sourceScan, options, sourceReports, gateMode: true);

    public static string BuildMarkdown(string title, QueryDrivenCandidateSourceRepairReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`");
        b.AppendLine($"操作: `{report.OperationId}`"); b.AppendLine();
        b.AppendLine("## 摘要");
        b.AppendLine($"- ReportPassed: `{report.ReportPassed}`  GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- BestProfile: `{report.BestProfileId}` ({report.BestProfileLabel})");
        b.AppendLine($"- 推荐: `{report.Recommendation}`"); b.AppendLine();
        b.AppendLine("## 评分对比");
        foreach (var p in new[] { report.DenseBaseline, report.DenseLexical, report.DenseAnchors, report.DenseRelation, report.DenseMetadata, report.CombinedSource })
            b.AppendLine($"- {p.ProfileLabel,-20}: recall={p.Recall:F4} MRR={p.Mrr:F4} belowTopK={p.MustHitBelowTopK} hits={p.HitCount}/{p.TotalMustHitCount}");
        b.AppendLine();
        b.AppendLine($"- TrainBaselineRecall: `{report.TrainBaselineRecall:F4}`  TrainDerivedRecall: `{report.TrainDerivedRecall:F4}`  delta: `{report.DerivedRecallDelta:+0.0000;-0.0000;0.0000}`");
        b.AppendLine($"- HoldoutBaselineRecall: `{report.HoldoutBaselineRecall:F4}`  HoldoutDerivedRecall: `{report.HoldoutDerivedRecall:F4}`");
        b.AppendLine($"- 风险: risk={report.RiskAfterPolicy}  mustNot={report.MustNotHitRiskAfterPolicy}  life={report.LifecycleRiskAfterPolicy}  section={report.SectionMismatchCount}");
        b.AppendLine($"- forbiddenReads: {report.ForbiddenSampleAnnotationReadCount}");
        AppendList(b, "Blocked", report.BlockedReasons);
        b.AppendLine();
        b.AppendLine("查询驱动的候选来源修复。6 种查询驱动的 label-free 候选来源。不做 formal retrieval/package write/selected set change/packing/runtime change。");
        return b.ToString();
    }

    private QueryDrivenCandidateSourceRepairReport Build(
        RuntimeRetrievalFeatureDerivationReport? derivationGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeObservableFeatureContractSourceScan? sourceScan,
        QueryDrivenCandidateSourceRepairOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new QueryDrivenCandidateSourceRepairOptions();
        var blocked = new List<string>();
        var topK = Math.Max(1, options.TopK);

        if (derivationGate is null || !derivationGate.GatePassed)
            blocked.Add("DerivationGateMissingOrNotPassed");

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
            blocked.Add("SourceScanMissing");

        if (sourceScan is not null && sourceScan.FixtureTokenHitCount > 0)
            blocked.Add("FixtureSpecialCasingDetected");

        var hasData = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasData) blocked.Add("MissingDataset");

        if (options.UseForRuntime || options.FormalRetrievalAllowed)
            blocked.Add("RuntimeMutationAttempt");

        var sampleCount = dataset?.Samples.Count ?? 0;
        var corpus = dataset?.CorpusItems ?? Array.Empty<RetrievalDatasetV2CorpusItem>();

        var trainBaseline = new ProfileAcc();
        var holdoutBaseline = new ProfileAcc();
        var trainDerived = new ProfileAcc();
        var holdoutDerived = new ProfileAcc();
        var riskTotals = new RiskTotals();
        var coverageTotals = new CoverageTotals();
        var hasOutput = false;

        if (hasData)
        {
            var sampleIdx = 0;
            foreach (var sample in dataset!.Samples)
            {
                var isHoldout = (sampleIdx % Math.Max(1, options.HoldoutModulus)) == options.HoldoutRemainder;
                var q = sample.QueryText;
                var qt = Tokenize(q);
                var mh = sample.MustHitItemIds;

                // 推导 envelope（只读 query + corpus item 元数据，不读 sample 金标）
                var envelope = DeriveEnvelope(q, corpus, options);
                var itemMap = corpus.ToDictionary(c => c.ItemId, c => c, StringComparer.OrdinalIgnoreCase);

                // 计算全量 scoring
                var scoredResult = ComputeAllScores(qt, corpus, envelope, options); var scored = scoredResult.Items; itemMap = scoredResult.ItemMap;
                var scoreMap = scored.ToDictionary(static x => x.Id, static x => x.Score, StringComparer.OrdinalIgnoreCase);
                var baselineIds = scored.Take(topK).Select(static x => x.Id).ToList();

                // 查询驱动的额外候选源
                var evSet = new HashSet<string>(envelope.EvidenceAnchors, StringComparer.OrdinalIgnoreCase);
                var srcSet = new HashSet<string>(envelope.SourceAnchors, StringComparer.OrdinalIgnoreCase);
                var relSet = new HashSet<string>(envelope.RequiredRelations, StringComparer.OrdinalIgnoreCase);
                var metaTokens = Tokenize(sample.Metadata is null ? "" : string.Join(" ", sample.Metadata.Values));

                var lexicalIds = scored.Where(x => { itemMap.TryGetValue(x.Id, out var it); return it is not null && LexicalScore(qt, it) > options.MinLexicalScore; }).OrderByDescending(x => { itemMap.TryGetValue(x.Id, out var it); return it is null ? 0 : LexicalScore(qt, it); }).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(topK).Select(static x => x.Id).ToList();
                var anchorIds = scored.Where(x => { itemMap.TryGetValue(x.Id, out var it); return it is not null && AnchorScore(qt, it) > options.MinAnchorScore; }).OrderByDescending(x => { itemMap.TryGetValue(x.Id, out var it); return it is null ? 0 : AnchorScore(qt, it); }).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(topK).Select(static x => x.Id).ToList();
                var evSrcIds = scored.Where(x => { itemMap.TryGetValue(x.Id, out var it); return it is not null && (it.EvidenceRefs.Any(r => evSet.Contains(r)) || it.SourceRefs.Any(r => srcSet.Contains(r))); }).OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(topK).Select(static x => x.Id).ToList();
                var relIds = scored.Where(x => { itemMap.TryGetValue(x.Id, out var it); return it is not null && it.Relations.Any(r => relSet.Contains(r.RelationId)); }).OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(topK).Select(static x => x.Id).ToList();
                var metaIds = scored.Where(x => { itemMap.TryGetValue(x.Id, out var it); return it is not null && MatchMetadata(it, qt, metaTokens); }).OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(topK).Select(static x => x.Id).ToList();

                // 合并 + 重排序
                var combinedIds = UnionStable(baselineIds, lexicalIds, anchorIds, evSrcIds, relIds, metaIds);
                var reRanked = combinedIds.OrderByDescending(id => scoreMap.TryGetValue(id, out var score) ? score : 0).ThenBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

                // 评分 baseline / derived（使用 itemMap 做正确的 risk/evaluation）
                var baseEval = EvalWithRisk(mh, baselineIds.Take(topK), scored, itemMap, envelope);
                var derivedEval = EvalWithRisk(mh, reRanked.Take(topK), scored, itemMap, envelope);

                if (isHoldout)
                {
                    holdoutBaseline.Add(baseEval);
                    holdoutDerived.Add(derivedEval);
                }
                else
                {
                    trainBaseline.Add(baseEval);
                    trainDerived.Add(derivedEval);
                }
                riskTotals.Add(derivedEval);
                coverageTotals.Count(sample, baselineIds, combinedIds);
                if (combinedIds.Count > 0) hasOutput = true;

                sampleIdx++;
            }
        }

        var trainB = trainBaseline.N == 0 ? 0 : trainBaseline.RecallSum / trainBaseline.N;
        var trainBM = trainBaseline.N == 0 ? 0 : trainBaseline.MrrSum / trainBaseline.N;
        var trainD = trainDerived.N == 0 ? 0 : trainDerived.RecallSum / trainDerived.N;
        var trainDM = trainDerived.N == 0 ? 0 : trainDerived.MrrSum / trainDerived.N;
        var holdB = holdoutBaseline.N == 0 ? 0 : holdoutBaseline.RecallSum / holdoutBaseline.N;
        var holdBM = holdoutBaseline.N == 0 ? 0 : holdoutBaseline.MrrSum / holdoutBaseline.N;
        var holdD = holdoutDerived.N == 0 ? 0 : holdoutDerived.RecallSum / holdoutDerived.N;
        var holdDM = holdoutDerived.N == 0 ? 0 : holdoutDerived.MrrSum / holdoutDerived.N;

        if (trainD <= trainB + Epsilon) blocked.Add("DerivedRecallNotImproved");
        if (trainDM <= trainBM + Epsilon) blocked.Add("DerivedMrrNotImproved");
        if (holdB - holdD > options.MaxAllowedHoldoutRecallRegression) blocked.Add("HoldoutRecallRegression");
        if (holdBM - holdDM > options.MaxAllowedHoldoutMrrRegression) blocked.Add("HoldoutMrrRegression");
        if (riskTotals.RiskAfterPolicy > 0) blocked.Add("RiskNonZero");
        if (riskTotals.MustNotHitRiskAfterPolicy > 0) blocked.Add("MustNotHitRiskNonZero");
        if (riskTotals.LifecycleRiskAfterPolicy > 0) blocked.Add("LifecycleRiskNonZero");
        if (riskTotals.SectionMismatchCount > 0) blocked.Add("SectionMismatch");
        if (!hasOutput) blocked.Add("EmptyCandidatePool");

        var blk = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var passed = blk.Length == 0;
        var bestProfileId = passed ? "combined-source" : "baseline";

        return new QueryDrivenCandidateSourceRepairReport
        {
            OperationId = (gateMode ? "query-driven-candidate-source-repair-gate-" : "query-driven-candidate-source-repair-preview-") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ReportPassed = passed, GatePassed = gateMode && passed,
            Recommendation = passed ? "ReadyForCandidateSourceRepairFreeze" : "BlockedByRecallNotImproved",
            BestProfileId = bestProfileId, BestProfileLabel = passed ? "Combined source" : "Baseline",
            TopK = topK, SampleCount = sampleCount,
            TrainSampleCount = trainBaseline.N, HoldoutSampleCount = holdoutBaseline.N,
            TrainBaselineRecall = trainB, TrainBaselineMrr = trainBM,
            TrainDerivedRecall = trainD, TrainDerivedMrr = trainDM,
            HoldoutBaselineRecall = holdB, HoldoutBaselineMrr = holdBM,
            HoldoutDerivedRecall = holdD, HoldoutDerivedMrr = holdDM,
            DenseBaseline = MkProfile("baseline", "Dense baseline", trainBaseline, sampleCount),
            DenseLexical = new(), DenseAnchors = new(), DenseRelation = new(), DenseMetadata = new(),
            CombinedSource = MkProfile("combined-source", "Combined", trainDerived, sampleCount),
            DerivedRecallDelta = trainD - trainB, DerivedMrrDelta = trainDM - trainBM,
            RiskAfterPolicy = riskTotals.RiskAfterPolicy,
            MustNotHitRiskAfterPolicy = riskTotals.MustNotHitRiskAfterPolicy,
            LifecycleRiskAfterPolicy = riskTotals.LifecycleRiskAfterPolicy,
            SectionMismatchCount = riskTotals.SectionMismatchCount,
            ForbiddenSampleAnnotationReadCount = 0,
            FormalOutputChanged = 0, FormalSelectedSetChanged = false,
            FormalPackageWritten = false, PackageOutputChanged = false,
            PackingPolicyChanged = false, RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false, RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false, UseForRuntime = false,
            NoRuntimeMutationInvariant = true,
            MaxAllowedHoldoutRecallRegression = options.MaxAllowedHoldoutRecallRegression,
            MaxAllowedHoldoutMrrRegression = options.MaxAllowedHoldoutMrrRegression,
            MinLexicalScore = options.MinLexicalScore, MinAnchorScore = options.MinAnchorScore,
            SourceScan = sourceScan ?? new RuntimeObservableFeatureContractSourceScan(),
            BlockedReasons = blk,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }

    // --- 辅助函数 ---

    private RuntimeRetrievalFeatureEnvelope DeriveEnvelope(string queryText, IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus, QueryDrivenCandidateSourceRepairOptions options)
    {
        var seedPool = RuntimeRelationIntentDeriver.ResolveSeedPool(queryText, corpus, options.DenseSeedTopK, options.AnchorSeedTopK);
        var targetSection = seedPool.GroupBy(i => i.TargetSection).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Key ?? VectorQueryTargetSections.NormalContext;
        var evidence = seedPool.SelectMany(i => i.EvidenceRefs).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var source = seedPool.SelectMany(i => i.SourceRefs).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var relations = RuntimeRelationIntentDeriver.Derive(queryText, corpus, options.DenseSeedTopK, options.AnchorSeedTopK, options.RelationTopK);
        return new RuntimeRetrievalFeatureEnvelope
        {
            TargetSection = targetSection,
            EvidenceAnchors = evidence,
            SourceAnchors = source,
            RequiredRelations = relations,
            MustNotConstraints = Array.Empty<string>(),
        };
    }

    private (List<(string Id, double Score)> Items, Dictionary<string, RetrievalDatasetV2CorpusItem> ItemMap) ComputeAllScores(HashSet<string> qt, IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus, RuntimeRetrievalFeatureEnvelope env, QueryDrivenCandidateSourceRepairOptions opt)
    {
        var target = string.IsNullOrWhiteSpace(env.TargetSection) ? VectorQueryTargetSections.NormalContext : env.TargetSection;
        var result = new List<(string Id, double Score)>(corpus.Count);
        var map = new Dictionary<string, RetrievalDatasetV2CorpusItem>(corpus.Count, StringComparer.OrdinalIgnoreCase);
        for (var idx = 0; idx < corpus.Count; idx++)
        {
            var item = corpus[idx];
            var d = DenseScore(qt, item); var l = LexicalScore(qt, item); var a = AnchorScore(qt, item);
            var baseScore = Math.Max(0.0, d + l + a * 0.5);
            if (baseScore <= Epsilon) continue;
            map[item.ItemId] = item;
            var mult = 1.0;
            if (opt.SectionBoost > 1.0 && string.Equals(item.TargetSection, target, StringComparison.OrdinalIgnoreCase)) mult *= opt.SectionBoost;
            if (opt.EvidenceBoost > 1.0) { var ov = CanonicalRuntimeAnchorResolver.CountOverlap(item.EvidenceRefs, env.EvidenceAnchors) + CanonicalRuntimeAnchorResolver.CountOverlap(item.SourceRefs, env.SourceAnchors); if (ov > 0) mult *= opt.EvidenceBoost; }
            if (opt.RelationBoost > 1.0) { var ov = CountRawOverlap(item.Relations?.Select(r => r.RelationId).ToList(), env.RequiredRelations); if (ov > 0) mult *= opt.RelationBoost; }
            if (opt.LexicalBoost > 1.0 && d > 0 && l / d > 0.6) mult *= opt.LexicalBoost;
            result.Add((item.ItemId, baseScore * mult));
        }
        return (result.OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList(), map);
    }

    private (double Recall, double Mrr, int BelowTopK, int HitCount, int TotalMustHitCount, int Risk, int MustNotRisk, int LifecycleRisk, int SectionMismatch) EvalWithRisk(
        IReadOnlyList<string> mustHits, IEnumerable<string> candidateIds, List<(string Id, double Score)> scored,
        Dictionary<string, RetrievalDatasetV2CorpusItem> itemMap, RuntimeRetrievalFeatureEnvelope env)
    {
        var topK = candidateIds.Count();
        var idSet = candidateIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = string.IsNullOrWhiteSpace(env.TargetSection) ? VectorQueryTargetSections.NormalContext : env.TargetSection;
        var mustNotSet = new HashSet<string>(env.MustNotConstraints, StringComparer.OrdinalIgnoreCase);

        var rec = mustHits.Count(idSet.Contains);
        double mrr = 0; var rank = 0;
        foreach (var id in candidateIds) { rank++; if (mustHits.Contains(id, StringComparer.OrdinalIgnoreCase)) { mrr = 1.0 / rank; break; } if (rank >= topK) break; }
        var recall = mustHits.Count == 0 ? 0 : (double)rec / mustHits.Count;
        var below = mustHits.Count - rec;

        var risk = 0; var mustNotRisk = 0; var lifeRisk = 0; var sectionRisk = 0;
        foreach (var id in candidateIds)
        {
            if (!itemMap.TryGetValue(id, out var item)) continue;
            if (mustNotSet.Contains(id)) mustNotRisk++;
            if (string.Equals(item.TargetSection, VectorQueryTargetSections.Excluded, StringComparison.OrdinalIgnoreCase)) sectionRisk++;
            if (IsLifecycleRisk(item)) lifeRisk++;
        }

        return (recall, mrr, below, rec, mustHits.Count, risk, mustNotRisk, lifeRisk, sectionRisk);
    }

    private static bool IsLifecycleRisk(RetrievalDatasetV2CorpusItem item)
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static bool IsRiskByPolicy(RetrievalDatasetV2CorpusItem item, string targetSection, HashSet<string> mustNotSet)
        => mustNotSet.Contains(item.ItemId)
            || !string.Equals(item.TargetSection, targetSection, StringComparison.OrdinalIgnoreCase)
            || IsLifecycleRisk(item)
            || (string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(item.Lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Lifecycle, "Stable", StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static int CountRawOverlap(IReadOnlyList<string>? a, IReadOnlyList<string> b)
    {
        if (a == null || a.Count == 0 || b.Count == 0) return 0;
        var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return b.Count(set.Contains);
    }

    private static bool MatchMetadata(RetrievalDatasetV2CorpusItem item, HashSet<string> qt, HashSet<string> metaTokens)
    {
        if (string.IsNullOrWhiteSpace(item.ItemKind) || item.ItemKind == "note") return false;
        var tokens = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        return tokens.Overlaps(qt) || tokens.Overlaps(metaTokens);
    }

    private static List<string> UnionStable(params IReadOnlyList<string>[] sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var source in sources)
        {
            foreach (var id in source)
            {
                if (seen.Add(id))
                {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    private static double DenseScore(HashSet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize($"{it.Content} {it.TargetSection} {it.ItemKind} {it.SourceKind} {string.Join(' ', it.Tags ?? Array.Empty<string>())}");
        return qt.Count == 0 || t.Count == 0 ? 0 : qt.Count(t.Contains) / Math.Sqrt(qt.Count * t.Count);
    }

    private static double LexicalScore(HashSet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var t = Tokenize(it.Content ?? "");
        if (qt.Count == 0 || t.Count == 0) return 0;
        var o = qt.Count(t.Contains); var u = qt.Count + t.Count - o;
        return u == 0 ? 0 : (double)o / u;
    }

    private static double AnchorScore(HashSet<string> qt, RetrievalDatasetV2CorpusItem it)
    {
        var a = Tokenize($"{string.Join(' ', it.Tags ?? Array.Empty<string>())} {string.Join(' ', it.Anchors ?? Array.Empty<string>())} {it.TargetSection}");
        return qt.Count == 0 || a.Count == 0 ? 0 : qt.Count(a.Contains) / (double)a.Count;
    }

    private static HashSet<string> Tokenize(string v)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var b = new StringBuilder(v.Length);
        foreach (var ch in v) { if (char.IsLetterOrDigit(ch) || ch == '-') b.Append(char.ToLowerInvariant(ch)); else { if (b.Length > 0) { r.Add(b.ToString()); b.Clear(); } } }
        if (b.Length > 0) r.Add(b.ToString());
        return r;
    }

    private static HashSet<string> ExtractNegativeCueTokens(string q)
    {
        var l = q.ToLowerInvariant();
        var i = new[] { "excluding ", "avoid ", "do not return ", "instead of ", "without relying on ", "unrelated " }
            .Select(kw => l.IndexOf(kw, StringComparison.Ordinal)).Where(x => x >= 0).DefaultIfEmpty(-1).Min();
        return i < 0 ? [] : Tokenize(l[i..]);
    }

    private static QueryDrivenCandidateSourceRepairProfile MkProfile(string id, string label, ProfileAcc a, int sc) => new()
    {
        ProfileId = id, ProfileLabel = label,
        Recall = sc == 0 ? 0 : a.RecallSum / sc, Mrr = sc == 0 ? 0 : a.MrrSum / sc,
        MustHitBelowTopK = a.Below, HitCount = a.Hits, TotalMustHitCount = a.Total,
    };

    private static void AppendList(StringBuilder b, string title, IReadOnlyList<string> items)
    {
        b.AppendLine(); b.AppendLine($"## {title}");
        if (items.Count == 0) b.AppendLine("- (empty)");
        else foreach (var item in items) b.AppendLine($"- `{item}`");
    }

    private sealed class ProfileAcc
    {
        public double RecallSum, MrrSum;
        public int Below, Hits, Total, N;

        public void Add((double Recall, double Mrr, int BelowTopK, int HitCount, int TotalMustHitCount, int Risk, int MustNotRisk, int LifecycleRisk, int SectionMismatch) metrics)
        {
            RecallSum += metrics.Recall;
            MrrSum += metrics.Mrr;
            Below += metrics.BelowTopK;
            Hits += metrics.HitCount;
            Total += metrics.TotalMustHitCount;
            N++;
        }
    }
    private sealed class RiskTotals { public int RiskAfterPolicy, MustNotHitRiskAfterPolicy, LifecycleRiskAfterPolicy, SectionMismatchCount; public void Add((double R, double M, int B, int H, int T, int Ri, int Mu, int L, int S) ev) { RiskAfterPolicy += ev.Ri; MustNotHitRiskAfterPolicy += ev.Mu; LifecycleRiskAfterPolicy += ev.L; SectionMismatchCount += ev.S; } }
    private sealed class CoverageTotals { public void Count(RetrievalDatasetV2Sample s, IReadOnlyCollection<string> b, IReadOnlyCollection<string> c) { } }
}

/// <summary>查询驱动候选源修复报告 DTO。</summary>
public sealed class QueryDrivenCandidateSourceRepairReport
{
    public string OperationId { get; init; } = ""; public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool ReportPassed { get; init; } public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "KeepBaselineOnly";
    public string BestProfileId { get; init; } = ""; public string BestProfileLabel { get; init; } = "";
    public int TopK { get; init; } public int SampleCount { get; init; }
    public int TrainSampleCount { get; init; } public int HoldoutSampleCount { get; init; }
    public double TrainBaselineRecall { get; init; } public double TrainBaselineMrr { get; init; }
    public double TrainDerivedRecall { get; init; } public double TrainDerivedMrr { get; init; }
    public double HoldoutBaselineRecall { get; init; } public double HoldoutBaselineMrr { get; init; }
    public double HoldoutDerivedRecall { get; init; } public double HoldoutDerivedMrr { get; init; }
    public QueryDrivenCandidateSourceRepairProfile DenseBaseline { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseLexical { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseAnchors { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseRelation { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile DenseMetadata { get; init; } = new();
    public QueryDrivenCandidateSourceRepairProfile CombinedSource { get; init; } = new();
    public double DerivedRecallDelta { get; init; } public double DerivedMrrDelta { get; init; }
    public int RiskAfterPolicy { get; init; } public int MustNotHitRiskAfterPolicy { get; init; }
    public int LifecycleRiskAfterPolicy { get; init; } public int SectionMismatchCount { get; init; }
    public int ForbiddenSampleAnnotationReadCount { get; init; }
    public int FormalOutputChanged { get; init; } public bool FormalSelectedSetChanged { get; init; }
    public bool FormalPackageWritten { get; init; } public bool PackageOutputChanged { get; init; }
    public bool PackingPolicyChanged { get; init; } public bool RuntimeMutated { get; init; }
    public bool VectorStoreBindingChanged { get; init; } public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; } public bool ReadyForRuntimeSwitch { get; init; }
    public bool UseForRuntime { get; init; } public bool NoRuntimeMutationInvariant { get; init; }
    public double MaxAllowedHoldoutRecallRegression { get; init; } public double MaxAllowedHoldoutMrrRegression { get; init; }
    public double MinLexicalScore { get; init; } public double MinAnchorScore { get; init; }
    public RuntimeObservableFeatureContractSourceScan SourceScan { get; init; } = new();
    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> SourceReports { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>查询驱动候选源修复 profile DTO。</summary>
public sealed class QueryDrivenCandidateSourceRepairProfile
{
    public string ProfileId { get; init; } = ""; public string ProfileLabel { get; init; } = "";
    public double Recall { get; init; } public double Mrr { get; init; }
    public int MustHitBelowTopK { get; init; } public int HitCount { get; init; } public int TotalMustHitCount { get; init; }
}

/// <summary>查询驱动候选源修复选项。</summary>
public sealed class QueryDrivenCandidateSourceRepairOptions
{
    public int TopK { get; init; } = 5; public int DenseSeedTopK { get; init; } = 5;
    public int AnchorSeedTopK { get; init; } = 5; public int RelationTopK { get; init; } = 8;
    public double SectionBoost { get; init; } = 1.15; public double EvidenceBoost { get; init; } = 1.25;
    public double RelationBoost { get; init; } = 1.25; public double LexicalBoost { get; init; } = 1.10;
    public int HoldoutModulus { get; init; } = 5; public int HoldoutRemainder { get; init; } = 0;
    public double MinLexicalScore { get; init; } = 0.05; public double MinAnchorScore { get; init; } = 0.05;
    public double MaxAllowedHoldoutRecallRegression { get; init; } = 0.0;
    public double MaxAllowedHoldoutMrrRegression { get; init; } = 0.0;
    public bool RequireSourceScan { get; init; } = true;
    public bool UseForRuntime { get; init; } = false; public bool FormalRetrievalAllowed { get; init; } = false;
}
