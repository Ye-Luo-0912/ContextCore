using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// 运行时关系意图推导：根据 query 意图、规划器伪相关反馈以及关系存储的一跳邻接，
/// 推出当前查询期望的 RequiredRelations。等价于运行时由规划器解析查询意图，并在
/// 关系存储上做一跳邻接展开。完全只读 query 和 item 侧元数据，不读样本侧任何
/// ground truth。
/// </summary>
internal static class RuntimeRelationIntentDeriver
{
    public static IReadOnlyList<string> Derive(
        string queryText,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        int denseSeedTopK,
        int anchorSeedTopK,
        int relationTopK)
    {
        if (corpus.Count == 0)
        {
            return Array.Empty<string>();
        }

        var queryTokens = Tokenize(queryText);
        if (queryTokens.Count == 0)
        {
            return [];
        }

        // Step A — anchor seed: items whose Tags ∪ Anchors strongly overlap with query tokens.
        var anchorSeed = corpus
            .Select(item =>
            {
                var anchorTokens = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}");
                var overlap = anchorTokens.Count == 0 ? 0 : queryTokens.Count(anchorTokens.Contains);
                return new SeedScore(item, overlap);
            })
            .Where(static entry => entry.Score > 0)
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, anchorSeedTopK))
            .ToArray();

        // Step B — dense seed: same scoring as V5.7 but exposed here for symmetry.
        var denseSeed = corpus
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                return new SeedScore(item, Math.Max(0, dense + lexical + anchor * 0.5));
            })
            .Where(static entry => entry.Score > 0)
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, denseSeedTopK))
            .ToArray();

        var seedItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seedPool = new List<RetrievalDatasetV2CorpusItem>();
        // Interleave anchor seed and dense seed to favour high-confidence overlap items
        // first; this keeps the relation-pool tight and avoids hub items (which carry
        // many relations) dominating.
        var maxSize = Math.Max(anchorSeed.Length, denseSeed.Length);
        for (var i = 0; i < maxSize; i++)
        {
            if (i < anchorSeed.Length && seedItemIds.Add(anchorSeed[i].Item.ItemId))
            {
                seedPool.Add(anchorSeed[i].Item);
            }

            if (i < denseSeed.Length && seedItemIds.Add(denseSeed[i].Item.ItemId))
            {
                seedPool.Add(denseSeed[i].Item);
            }
        }

        // 限制 relation 提取的来源：取得分最高的若干 seed item，但跳过自身关系数远超
        // 中位数的“枢纽 item”。枢纽 item 自带大量关系，会把 envelope 里的 relation 集
        // 撑成几乎覆盖整个 corpus，最终让 boost 退化成对所有候选的均匀加权。挑出非枢纽
        // 的 top item，让 boost 聚焦到查询真正命中的关系上。
        var relationCountByItem = corpus.Select(item => item.Relations.Count).Where(static count => count > 0).OrderBy(static count => count).ToArray();
        var medianRelationCount = relationCountByItem.Length == 0
            ? 0
            : relationCountByItem[relationCountByItem.Length / 2];
        var hubRelationCutoff = Math.Max(3, medianRelationCount * 4);

        var nonHubSeed = seedPool.Where(item => item.Relations.Count <= hubRelationCutoff).ToArray();
        var relationSeedTopK = Math.Min(nonHubSeed.Length, 2);
        var relationSeedItems = nonHubSeed.Take(relationSeedTopK).ToArray();

        // 仅汇总从非枢纽 seed item 拉到的 relation；不做 1-hop 扩展（1-hop 会把
        // 与查询无直接关联的关系也带进来，稀释 boost 的判别力）。
        var relationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in relationSeedItems)
        {
            foreach (var relation in item.Relations)
            {
                if (string.IsNullOrWhiteSpace(relation.RelationId))
                {
                    continue;
                }

                relationCounts.TryGetValue(relation.RelationId, out var count);
                relationCounts[relation.RelationId] = count + 1;
            }
        }

        return [.. relationCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, relationTopK))
            .Select(static pair => pair.Key)];
    }

    public static IReadOnlyList<RetrievalDatasetV2CorpusItem> ResolveSeedPool(
        string queryText,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        int denseSeedTopK,
        int anchorSeedTopK)
    {
        if (corpus.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var queryTokens = Tokenize(queryText);
        if (queryTokens.Count == 0)
        {
            return Array.Empty<RetrievalDatasetV2CorpusItem>();
        }

        var anchorSeed = corpus
            .Select(item =>
            {
                var anchorTokens = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)}");
                var overlap = anchorTokens.Count == 0 ? 0 : queryTokens.Count(anchorTokens.Contains);
                return new SeedScore(item, overlap);
            })
            .Where(static entry => entry.Score > 0)
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, anchorSeedTopK))
            .Select(static entry => entry.Item)
            .ToArray();

        var denseSeed = corpus
            .Select(item =>
            {
                var dense = DenseScore(queryTokens, item);
                var lexical = LexicalScore(queryTokens, item);
                var anchor = AnchorScore(queryTokens, item);
                return new SeedScore(item, Math.Max(0, dense + lexical + anchor * 0.5));
            })
            .Where(static entry => entry.Score > 0)
            .OrderByDescending(static entry => entry.Score)
            .ThenBy(static entry => entry.Item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, denseSeedTopK))
            .Select(static entry => entry.Item)
            .ToArray();

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pool = new List<RetrievalDatasetV2CorpusItem>();
        var max = Math.Max(anchorSeed.Length, denseSeed.Length);
        for (var i = 0; i < max; i++)
        {
            if (i < anchorSeed.Length && seenIds.Add(anchorSeed[i].ItemId))
            {
                pool.Add(anchorSeed[i]);
            }

            if (i < denseSeed.Length && seenIds.Add(denseSeed[i].ItemId))
            {
                pool.Add(denseSeed[i]);
            }
        }

        return pool;
    }

    private static double DenseScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize($"{item.Content} {item.TargetSection} {item.ItemKind} {item.SourceKind} {string.Join(' ', item.Tags.Where(static tag => !tag.StartsWith("rdsv2-source-tag-", StringComparison.OrdinalIgnoreCase)))}");
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(itemTokens.Contains);
        return overlap / Math.Sqrt(queryTokens.Count * itemTokens.Count);
    }

    private static double LexicalScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var itemTokens = Tokenize(item.Content);
        if (queryTokens.Count == 0 || itemTokens.Count == 0)
        {
            return 0;
        }

        var overlap = queryTokens.Count(itemTokens.Contains);
        var union = queryTokens.Count + itemTokens.Count - overlap;
        return union == 0 ? 0 : (double)overlap / union;
    }

    private static double AnchorScore(IReadOnlySet<string> queryTokens, RetrievalDatasetV2CorpusItem item)
    {
        var anchors = Tokenize($"{string.Join(' ', item.Tags)} {string.Join(' ', item.Anchors)} {item.TargetSection}");
        if (queryTokens.Count == 0 || anchors.Count == 0)
        {
            return 0;
        }

        return queryTokens.Count(anchors.Contains) / (double)anchors.Count;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                builder.Append(char.ToLowerInvariant(ch));
                continue;
            }

            FlushToken(builder, result);
        }

        FlushToken(builder, result);
        return result;
    }

    private static void FlushToken(StringBuilder builder, ISet<string> result)
    {
        if (builder.Length == 0)
        {
            return;
        }

        result.Add(builder.ToString());
        builder.Clear();
    }

    private readonly record struct SeedScore(RetrievalDatasetV2CorpusItem Item, double Score);
}
