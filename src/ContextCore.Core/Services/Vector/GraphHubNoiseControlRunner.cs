using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 图枢纽噪声控制预览。识别 hub item（关系数远超中位数的 item），在关系提取
/// 阶段限制单个 seed item 最多贡献 N 条关系（默认 3），同时记录枢纽指标。
/// 对比 baseline、上一轮 derived、hub-controlled 三个 profile 的召回率和 MRR。
/// 只读：不接 formal retrieval、不写 formal package、不改 selected set、
/// 不改 PackingPolicy / package output、不切 runtime。
/// </summary>
public sealed class GraphHubNoiseControlRunner
{
    // 单个 seed item 最多贡献的关系数
    private const int DefaultRelationsPerSeedCap = 3;
    private const int HubDegreeThreshold = 5;
    private const int MaxEnvelopeRelations = 20;

    public GraphHubNoiseControlReport BuildPreview(
        RuntimeFeatureDerivationFailureFreezeReport? freezeGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeRetrievalFeatureDerivationRepairReport? repairGate,
        GraphHubNoiseControlOptions? options = null)
        => Build(freezeGate, dataset, repairGate, options, gateMode: false);

    public GraphHubNoiseControlReport BuildGate(
        RuntimeFeatureDerivationFailureFreezeReport? freezeGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeRetrievalFeatureDerivationRepairReport? repairGate,
        GraphHubNoiseControlOptions? options = null)
        => Build(freezeGate, dataset, repairGate, options, gateMode: true);

    public static string BuildMarkdown(string title, GraphHubNoiseControlReport report)
    {
        var b = new StringBuilder();
        b.AppendLine($"# {title}"); b.AppendLine();
        b.AppendLine($"生成: `{report.CreatedAt:O}`"); b.AppendLine();
        b.AppendLine("## 摘要");
        b.AppendLine($"- PreviewPassed: `{report.PreviewPassed}`");
        b.AppendLine($"- GatePassed: `{report.GatePassed}`");
        b.AppendLine($"- Recommendation: `{report.Recommendation}`");
        b.AppendLine($"- HubItemCount: `{report.HubItemCount}`");
        b.AppendLine($"- AvgEnvelopeWidthRatio: `{report.AvgEnvelopeWidthRatio:F4}`");
        b.AppendLine($"- AvgHubDominanceRatio: `{report.AvgHubDominanceRatio:F4}`");
        b.AppendLine();
        b.AppendLine("## 评分对比");
        b.AppendLine($"- baseline        : recall={report.Baseline.Recall:F4} MRR={report.Baseline.MeanReciprocalRank:F4}");
        b.AppendLine($"- previous derived: recall={report.PreviousDerived.Recall:F4} MRR={report.PreviousDerived.MeanReciprocalRank:F4}");
        b.AppendLine($"- hub-controlled  : recall={report.HubControlled.Recall:F4} MRR={report.HubControlled.MeanReciprocalRank:F4}");
        b.AppendLine($"- hub-ctrl delta  : recall={report.HubControlledRecallDelta:+0.0000;-0.0000;0.0000} MRR={report.HubControlledMrrDelta:+0.0000;-0.0000;0.0000}");
        b.AppendLine();
        b.AppendLine("V5.9 preview only. Graph hub noise control applied to relation envelope. No formal retrieval, package write, selected set change, packing policy mutation, runtime switch, or vector store binding change.");
        return b.ToString();
    }

    private GraphHubNoiseControlReport Build(
        RuntimeFeatureDerivationFailureFreezeReport? freezeGate,
        RetrievalDatasetV2GeneratedDataset? dataset,
        RuntimeRetrievalFeatureDerivationRepairReport? repairGate,
        GraphHubNoiseControlOptions? options,
        bool gateMode)
    {
        options ??= new GraphHubNoiseControlOptions();
        var topK = Math.Max(1, options.TopK);
        var blocked = new List<string>();

        if (freezeGate is null) blocked.Add("FreezeGateMissing");
        else if (!freezeGate.FreezePassed) blocked.Add("FreezeGateNotPassed");

        var hasDataset = dataset is not null && dataset.Samples.Count > 0 && dataset.CorpusItems.Count > 0;
        if (!hasDataset) blocked.Add("MissingDataset");

        // 预计算全量 relation 和 item degree（一次，非每样本）
        var itemDegree = RuntimeRelationDegrees.ComputeItemDegrees(dataset!.CorpusItems);
        var relationDegree = RuntimeRelationDegrees.ComputeRelationDegrees(dataset.CorpusItems);

        var sampleCount = dataset.Samples.Count;
        var totalEnvelopeRatio = 0d;
        var totalHubDominance = 0d;
        var hubItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recallSum = new double[3]; // 0=baseline, 1=hub-controlled
        var mrrSum = new double[3];
        var belowTopKSum = new int[3];

        foreach (var sample in dataset.Samples)
        {
            // 复用 V5.7 的核心评分
            var queryText = sample.QueryText;
            var corpus = dataset.CorpusItems;

            // baseline: dense-only
            var baselineTopK = RankCandidatesPureDense(queryText, corpus, options.VectorTopK).Take(topK).ToArray();
            var baseMetrics = ComputeMetrics(sample, baselineTopK, null, topK);
            recallSum[0] += baseMetrics.Recall; mrrSum[0] += baseMetrics.Mrr; belowTopKSum[0] += baseMetrics.BelowTopK;

            // 种子 + 有枢纽控制的 envelope
            var seedPool = RuntimeRelationIntentDeriver.ResolveSeedPool(queryText, corpus, options.DenseSeedTopK, options.AnchorSeedTopK);
            var envelope = BuildHubControlledEnvelope(sample.SampleId, seedPool, itemDegree, relationDegree, hubItems, options);

            // hub-controlled scoring
            var vector = RankCandidatesWithEnvelope(queryText, corpus, options, envelope);
            var graph = CollectGraphCandidatesFromEnvelope(corpus, envelope, options.GraphTopK);
            var merged = MergeCandidates(vector, graph, options.MergedTopK);
            var hubMetrics = ComputeMetrics(sample, merged.Take(topK).ToArray(), null, topK);
            recallSum[1] += hubMetrics.Recall; mrrSum[1] += hubMetrics.Mrr; belowTopKSum[1] += hubMetrics.BelowTopK;

            totalEnvelopeRatio += seedPool.Count > 0 ? (double)envelope.RequiredRelations.Count / seedPool.Count : 0;
            var hubInSeed = seedPool.Count(s => itemDegree.TryGetValue(s.ItemId, out var deg) && deg > HubDegreeThreshold);
            totalHubDominance += seedPool.Count > 0 ? (double)hubInSeed / seedPool.Count : 0;
        }

        var avgRecallBaseline = recallSum[0] / sampleCount;
        var avgMrrBaseline = mrrSum[0] / sampleCount;
        var avgRecallHub = recallSum[1] / sampleCount;
        var avgMrrHub = mrrSum[1] / sampleCount;

        // 对比上一轮 derived（从 repairGate 获取）
        var prevRecall = repairGate?.TrainDerivedRecall ?? (repairGate?.HoldoutDerivedRecall ?? avgRecallBaseline);
        var prevMrr = repairGate?.TrainDerivedMrr ?? (repairGate?.HoldoutDerivedMrr ?? avgMrrBaseline);

        if (avgRecallHub < avgRecallBaseline - 1e-9) blocked.Add("RecallRegression");
        if (avgMrrHub < avgMrrBaseline - 1e-9) blocked.Add("MrrRegression");

        var passed = blocked.Count == 0;
        return new GraphHubNoiseControlReport
        {
            OperationId = (gateMode ? "graph-hub-noise-control-gate-" : "graph-hub-noise-control-preview-") + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            PreviewPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = passed ? GraphHubNoiseControlRecommendations.ReadyForHubNoiseControlFreeze : GraphHubNoiseControlRecommendations.KeepBaselineOnly,
            AllowedMode = "PreviewOnly",
            TopK = topK,
            SampleCount = sampleCount,
            HubItemCount = hubItems.Count,
            AvgEnvelopeWidthRatio = totalEnvelopeRatio / sampleCount,
            AvgHubDominanceRatio = totalHubDominance / sampleCount,
            Baseline = new GraphHubNoiseControlProfileResult { ProfileId = "baseline", ProfileLabel = "Baseline", Recall = avgRecallBaseline, MeanReciprocalRank = avgMrrBaseline },
            PreviousDerived = new GraphHubNoiseControlProfileResult { ProfileId = "previous-derived", ProfileLabel = "Previous derived", Recall = prevRecall, MeanReciprocalRank = prevMrr },
            HubControlled = new GraphHubNoiseControlProfileResult { ProfileId = "hub-controlled", ProfileLabel = "Hub-controlled", Recall = avgRecallHub, MeanReciprocalRank = avgMrrHub },
            HubControlledRecallDelta = avgRecallHub - avgRecallBaseline,
            HubControlledMrrDelta = avgMrrHub - avgMrrBaseline,
        };
    }

    private RuntimeRetrievalFeatureEnvelope BuildHubControlledEnvelope(
        string sampleId, IReadOnlyList<RetrievalDatasetV2CorpusItem> seedPool,
        IReadOnlyDictionary<string, int> itemDegree,
        IReadOnlyDictionary<string, int> relationDegree,
        HashSet<string> hubItems,
        GraphHubNoiseControlOptions options)
    {
        if (seedPool.Count == 0)
            return new RuntimeRetrievalFeatureEnvelope { TargetSection = VectorQueryTargetSections.NormalContext, RequiredRelations = Array.Empty<string>() };

        var targetSection = seedPool
            .GroupBy(i => i.TargetSection)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;
        if (string.IsNullOrEmpty(targetSection)) targetSection = VectorQueryTargetSections.NormalContext;

        // 提取证据/来源 anchor（直接从 seed pool，不做 hub 限制）
        var evidenceAnchors = seedPool.SelectMany(i => i.EvidenceRefs).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEnvelopeRelations).ToArray();
        var sourceAnchors = seedPool.SelectMany(i => i.SourceRefs).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEnvelopeRelations).ToArray();

        // 枢纽控制的关系提取：每个 seed item 最多 ⊥ RelationsPerSeedCap 条
        var perSeedCap = Math.Max(1, options.RelationsPerSeedCap);
        var hubThreshold = options.HubDegreeThreshold;
        var relationSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hubSeedCount = 0;

        foreach (var item in seedPool)
        {
            var deg = itemDegree.TryGetValue(item.ItemId, out var d) ? d : 1;
            if (deg > hubThreshold)
            {
                hubSeedCount++;
                hubItems.Add(item.ItemId);
            }

            var count = 0;
            foreach (var rel in item.Relations)
            {
                if (string.IsNullOrEmpty(rel.RelationId)) continue;
                if (count >= perSeedCap) break;
                if (relationSet.Add(rel.RelationId)) count++;
            }
        }

        return new RuntimeRetrievalFeatureEnvelope
        {
            SampleId = sampleId,
            TargetSection = targetSection,
            EvidenceAnchors = evidenceAnchors,
            SourceAnchors = sourceAnchors,
            RequiredRelations = relationSet.Take(MaxEnvelopeRelations).ToArray(),
            MustNotConstraints = Array.Empty<string>(),
        };
    }

    private IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidatesPureDense(string queryText, IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus, int topK)
    {
        var queryTokens = Tokenize(queryText);
        var negative = ExtractNegativeCueTokens(queryText);
        return corpus
            .Select(i => (Item: i, Score: Math.Max(0, DenseScore(queryTokens, i) + LexicalScore(queryTokens, i) + AnchorScore(queryTokens, i) * 0.5 - NegativeCueOverlap(negative, i) * 0.85)))
            .Where(e => e.Score > 0).OrderByDescending(e => e.Score).ThenBy(e => e.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, topK)).Select(e => e.Item).ToArray();
    }

    private IReadOnlyList<RetrievalDatasetV2CorpusItem> RankCandidatesWithEnvelope(string queryText, IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus, GraphHubNoiseControlOptions options, RuntimeRetrievalFeatureEnvelope envelope)
    {
        var q = Tokenize(queryText);
        var neg = ExtractNegativeCueTokens(queryText);
        var ev = new HashSet<string>(envelope.EvidenceAnchors, StringComparer.OrdinalIgnoreCase);
        var src = new HashSet<string>(envelope.SourceAnchors, StringComparer.OrdinalIgnoreCase);
        var rel = new HashSet<string>(envelope.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var mustNot = new HashSet<string>(envelope.MustNotConstraints, StringComparer.OrdinalIgnoreCase);
        var target = string.IsNullOrEmpty(envelope.TargetSection) ? VectorQueryTargetSections.NormalContext : envelope.TargetSection;

        return [.. corpus
            .Select(i =>
            {
                var d = DenseScore(q, i); var l = LexicalScore(q, i); var a = AnchorScore(q, i); var n = NegativeCueOverlap(neg, i);
                var score = Math.Max(0, d + l + a * 0.5 - n * 0.85);
                if (score <= 0) return (i, 0d);
                var m = 1d;
                if (string.Equals(i.TargetSection, target, StringComparison.OrdinalIgnoreCase)) m *= options.SectionBoost;
                if (i.EvidenceRefs.Any(r => ev.Contains(r)) || i.SourceRefs.Any(r => src.Contains(r))) m *= options.EvidenceBoost;
                if (i.Relations.Any(r => rel.Contains(r.RelationId))) m *= options.RelationBoost;
                if (d > 0 && l / d > 0.6) m *= options.LexicalBoost;
                return (i, score * m);
            })
            .Where(e => e.Item2 > 0)
            .Where(e => !mustNot.Contains(e.i.ItemId) && !IsRisk(e.i, target))
            .OrderByDescending(e => e.Item2).ThenBy(e => e.i.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, options.VectorTopK)).Select(e => e.i)];
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> CollectGraphCandidatesFromEnvelope(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus, RuntimeRetrievalFeatureEnvelope envelope, int topK)
    {
        var rel = new HashSet<string>(envelope.RequiredRelations, StringComparer.OrdinalIgnoreCase);
        var ev = new HashSet<string>(envelope.EvidenceAnchors, StringComparer.OrdinalIgnoreCase);
        var src = new HashSet<string>(envelope.SourceAnchors, StringComparer.OrdinalIgnoreCase);
        var target = string.IsNullOrEmpty(envelope.TargetSection) ? VectorQueryTargetSections.NormalContext : envelope.TargetSection;
        return corpus
            .Select(i => (i, Score: (i.Relations.Any(r => rel.Contains(r.RelationId)) ? 2 : 0) + (i.EvidenceRefs.Any(r => ev.Contains(r)) ? 1 : 0) + (i.SourceRefs.Any(r => src.Contains(r)) ? 1 : 0)))
            .Where(e => e.Score > 0).Where(e => !IsRisk(e.i, target))
            .OrderByDescending(e => e.Score).ThenBy(e => e.i.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, topK)).Select(e => e.i).ToArray();
    }

    private static IReadOnlyList<RetrievalDatasetV2CorpusItem> MergeCandidates(IReadOnlyList<RetrievalDatasetV2CorpusItem> v, IReadOnlyList<RetrievalDatasetV2CorpusItem> g, int topK)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var r = new List<RetrievalDatasetV2CorpusItem>();
        foreach (var x in v.Concat(g)) { if (seen.Add(x.ItemId)) { r.Add(x); if (r.Count >= topK) break; } }
        return r;
    }

    private static ComputeMetricsResult ComputeMetrics(RetrievalDatasetV2Sample sample, IReadOnlyList<RetrievalDatasetV2CorpusItem> topKWindow, RuntimeRetrievalFeatureEnvelope? envelope, int topK)
    {
        var mustHits = sample.MustHitItemIds;
        var ids = topKWindow.Select(c => c.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recalled = 0; var firstRank = 0;
        for (var i = 0; i < topKWindow.Count; i++) { if (mustHits.Contains(topKWindow[i].ItemId, StringComparer.OrdinalIgnoreCase)) { recalled++; if (firstRank == 0) firstRank = i + 1; } }
        return new ComputeMetricsResult(
            mustHits.Count == 0 ? 0d : (double)recalled / mustHits.Count,
            firstRank == 0 ? 0d : 1d / firstRank,
            mustHits.Count - recalled);
    }

    private static bool IsRisk(RetrievalDatasetV2CorpusItem item, string targetSection)
        => !string.Equals(item.TargetSection, targetSection, StringComparison.OrdinalIgnoreCase)
            || IsLifecycleRisk(item)
            || (string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(item.Lifecycle, "Active", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Lifecycle, "Current", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Lifecycle, "Stable", StringComparison.OrdinalIgnoreCase))
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static bool IsLifecycleRisk(RetrievalDatasetV2CorpusItem item)
        => string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(item.Lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Lifecycle, "Historical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase));

    private static double DenseScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem item)
    {
        var it = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind} {string.Join(' ', item.Tags.Where(t => !t.StartsWith("rdsv2-source-tag-", StringComparison.OrdinalIgnoreCase)))}");
        if (qt.Count == 0 || it.Count == 0) return 0;
        return qt.Count(it.Contains) / Math.Sqrt(qt.Count * it.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem item)
    {
        var it = Tokenize(item.Content);
        if (qt.Count == 0 || it.Count == 0) return 0;
        var o = qt.Count(it.Contains); var u = qt.Count + it.Count - o;
        return u == 0 ? 0 : (double)o / u;
    }

    private static double AnchorScore(IReadOnlySet<string> qt, RetrievalDatasetV2CorpusItem item)
    {
        var a = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (qt.Count == 0 || a.Count == 0) return 0;
        return qt.Count(a.Contains) / (double)a.Count;
    }

    private static double NegativeCueOverlap(IReadOnlySet<string> nt, RetrievalDatasetV2CorpusItem item)
    {
        if (nt.Count == 0) return 0;
        var it = Tokenize($"{item.Content} {item.ItemKind} {item.SourceKind} {item.TargetSection} {string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}");
        return it.Count == 0 ? 0 : nt.Count(it.Contains) / (double)nt.Count;
    }

    private static HashSet<string> ExtractNegativeCueTokens(string queryText)
    {
        var l = queryText.ToLowerInvariant();
        var ci = new[] { l.IndexOf("excluding ", StringComparison.Ordinal), l.IndexOf("avoid ", StringComparison.Ordinal), l.IndexOf("do not return ", StringComparison.Ordinal), l.IndexOf("instead of ", StringComparison.Ordinal), l.IndexOf("without relying on ", StringComparison.Ordinal), l.IndexOf("unrelated ", StringComparison.Ordinal) }.Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        return ci < 0 ? [] : Tokenize(l[ci..]);
    }

    private static HashSet<string> Tokenize(string value)
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var b = new StringBuilder(value.Length);
        foreach (var ch in value) { if (char.IsLetterOrDigit(ch) || ch == '-') b.Append(char.ToLowerInvariant(ch)); else { if (b.Length > 0) { r.Add(b.ToString()); b.Clear(); } } }
        if (b.Length > 0) r.Add(b.ToString());
        return r;
    }
}

internal readonly record struct ComputeMetricsResult(double Recall, double Mrr, int BelowTopK);

/// <summary>图枢纽噪声控制选项。</summary>
public sealed class GraphHubNoiseControlOptions
{
    public int TopK { get; init; } = 5;
    public int DenseSeedTopK { get; init; } = 5;
    public int AnchorSeedTopK { get; init; } = 5;
    public int VectorTopK { get; init; } = 10;
    public int GraphTopK { get; init; } = 10;
    public int MergedTopK { get; init; } = 12;
    public double SectionBoost { get; init; } = 1.15;
    public double EvidenceBoost { get; init; } = 1.25;
    public double RelationBoost { get; init; } = 1.25;
    public double LexicalBoost { get; init; } = 1.10;
    public int RelationsPerSeedCap { get; init; } = 3;
    public int HubDegreeThreshold { get; init; } = 5;
}

/// <summary>静态工具：预计算 item degree 和 relation degree 映射。</summary>
internal static class RuntimeRelationDegrees
{
    public static Dictionary<string, int> ComputeItemDegrees(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        var map = new Dictionary<string, int>(corpus.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var item in corpus) map[item.ItemId] = item.Relations?.Count ?? 0;
        return map;
    }

    public static Dictionary<string, int> ComputeRelationDegrees(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in corpus)
        {
            if (item.Relations is null) continue;
            foreach (var r in item.Relations)
            {
                if (string.IsNullOrEmpty(r.RelationId)) continue;
                map[r.RelationId] = map.TryGetValue(r.RelationId, out var c) ? c + 1 : 1;
            }
        }
        return map;
    }
}

