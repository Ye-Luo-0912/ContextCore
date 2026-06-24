using System.Globalization;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 vector index 覆盖率报告；只读聚合 plan / diagnostics / status，不写入 index。</summary>
public static class VectorIndexCoverageReportBuilder
{
    public static VectorIndexCoverageReport Build(
        VectorReindexPlan plan,
        VectorIndexDiagnosticsReport diagnostics,
        VectorIndexStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(status);

        var sourceItems = BuildSourceItems(plan);
        var total = plan.TotalCandidates > 0 ? plan.TotalCandidates : sourceItems.Count;
        var missingIds = plan.MissingItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staleIds = plan.StaleItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexed = Math.Max(0, total - missingIds.Count);
        var warnings = BuildWarnings(plan, diagnostics, status).ToArray();

        return new VectorIndexCoverageReport
        {
            WorkspaceId = plan.WorkspaceId,
            CollectionId = plan.CollectionId,
            TotalSourceItems = total,
            IndexedItems = indexed,
            CoverageRate = total == 0 ? 0d : indexed / (double)total,
            CoverageByLayer = BuildCoverageBuckets(sourceItems, static item => item.Layer, missingIds, staleIds),
            CoverageByItemKind = BuildCoverageBuckets(sourceItems, static item => item.ItemKind, missingIds, staleIds),
            MissingByLayer = CountByLayer(sourceItems, missingIds),
            StaleByLayer = CountByLayer(sourceItems, staleIds),
            DuplicateCount = diagnostics.DuplicateCount,
            OrphanCount = diagnostics.OrphanCount,
            DimensionMismatchCount = diagnostics.DimensionMismatchCount,
            ProviderUnavailableCount = diagnostics.ProviderUnavailableCount,
            EmbeddingModel = status.Model,
            EmbeddingProvider = status.Provider,
            Dimension = status.Dimension,
            Recommendation = ResolveRecommendation(total, indexed, plan, diagnostics),
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static string ToMarkdown(VectorIndexCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector Index Coverage Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{report.WorkspaceId}`");
        builder.AppendLine($"- Collection: `{report.CollectionId}`");
        builder.AppendLine($"- TotalSourceItems: `{report.TotalSourceItems}`");
        builder.AppendLine($"- IndexedItems: `{report.IndexedItems}`");
        builder.AppendLine($"- CoverageRate: `{report.CoverageRate:P2}`");
        builder.AppendLine($"- DuplicateCount: `{report.DuplicateCount}`");
        builder.AppendLine($"- OrphanCount: `{report.OrphanCount}`");
        builder.AppendLine($"- DimensionMismatchCount: `{report.DimensionMismatchCount}`");
        builder.AppendLine($"- ProviderUnavailableCount: `{report.ProviderUnavailableCount}`");
        builder.AppendLine($"- Embedding: `{report.EmbeddingProvider}/{report.EmbeddingModel}` dim=`{report.Dimension}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        AppendBucketTable(builder, "Coverage By Layer", report.CoverageByLayer.Values);
        AppendBucketTable(builder, "Coverage By Item Kind", report.CoverageByItemKind.Values);

        if (report.Warnings.Count > 0)
        {
            builder.AppendLine("## Warnings");
            builder.AppendLine();
            foreach (var warning in report.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<CoverageSourceItem> BuildSourceItems(VectorReindexPlan plan)
    {
        return plan.Items
            .Where(static item => item.Action is "Create" or "Update" or "Skip")
            .GroupBy(static item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(static item => new CoverageSourceItem(
                item.ItemId,
                NormalizeBucketKey(item.Layer),
                NormalizeBucketKey(item.ItemKind)))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, VectorIndexCoverageBucket> BuildCoverageBuckets(
        IReadOnlyList<CoverageSourceItem> items,
        Func<CoverageSourceItem, string> keySelector,
        IReadOnlySet<string> missingIds,
        IReadOnlySet<string> staleIds)
    {
        return items
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                group =>
                {
                    var total = group.Count();
                    var missing = group.Count(item => missingIds.Contains(item.ItemId));
                    var stale = group.Count(item => staleIds.Contains(item.ItemId));
                    var indexed = Math.Max(0, total - missing);
                    return new VectorIndexCoverageBucket
                    {
                        Key = group.Key,
                        TotalSourceItems = total,
                        IndexedItems = indexed,
                        MissingItems = missing,
                        StaleItems = stale,
                        CoverageRate = total == 0 ? 0d : indexed / (double)total
                    };
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int> CountByLayer(
        IReadOnlyList<CoverageSourceItem> items,
        IReadOnlySet<string> itemIds)
    {
        return items
            .Where(item => itemIds.Contains(item.ItemId))
            .GroupBy(static item => item.Layer, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildWarnings(
        VectorReindexPlan plan,
        VectorIndexDiagnosticsReport diagnostics,
        VectorIndexStatusResponse status)
    {
        foreach (var warning in plan.Warnings.Concat(status.Warnings))
        {
            if (!string.IsNullOrWhiteSpace(warning))
            {
                yield return warning;
            }
        }

        if (diagnostics.DuplicateCount > 0)
        {
            yield return "存在重复 vector entry，应先去重以减少存储噪声。";
        }

        if (diagnostics.OrphanCount > 0)
        {
            yield return "存在 orphan vector entry，应确认来源后清理。";
        }

        if (diagnostics.DimensionMismatchCount > 0)
        {
            yield return "存在 vector 维度不一致，不能进入 shadow baseline。";
        }
    }

    private static string ResolveRecommendation(
        int total,
        int indexed,
        VectorReindexPlan plan,
        VectorIndexDiagnosticsReport diagnostics)
    {
        if (diagnostics.ProviderUnavailableCount > 0
            || diagnostics.DimensionMismatchCount > 0
            || diagnostics.DuplicateCount > 0
            || diagnostics.OrphanCount > 0)
        {
            return VectorIndexCoverageRecommendations.BlockedByDiagnostics;
        }

        if (total == 0 || indexed == 0)
        {
            return VectorIndexCoverageRecommendations.NeedsInitialIndexing;
        }

        if (plan.ToCreate > 0 || plan.ToUpdate > 0 || diagnostics.StaleCount > 0)
        {
            return VectorIndexCoverageRecommendations.NeedsReindex;
        }

        return VectorIndexCoverageRecommendations.ReadyForVectorShadowEval;
    }

    private static void AppendBucketTable(
        StringBuilder builder,
        string title,
        IEnumerable<VectorIndexCoverageBucket> buckets)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Key | Total | Indexed | Missing | Stale | Coverage |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var bucket in buckets)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {bucket.Key} | {bucket.TotalSourceItems} | {bucket.IndexedItems} | {bucket.MissingItems} | {bucket.StaleItems} | {bucket.CoverageRate:P2} |"));
        }

        builder.AppendLine();
    }

    private static string NormalizeBucketKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }

    private sealed record CoverageSourceItem(
        string ItemId,
        string Layer,
        string ItemKind);
}
