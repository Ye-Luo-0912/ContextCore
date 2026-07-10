using System.Globalization;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>构建 vector source lifecycle metadata coverage 报告；只读统计，不写 index。</summary>
public sealed class VectorLifecycleMetadataCoverageReportBuilder
{
    private readonly VectorSourceLifecycleMetadataResolver _resolver;

    public VectorLifecycleMetadataCoverageReportBuilder(VectorSourceLifecycleMetadataResolver? resolver = null)
    {
        _resolver = resolver ?? new VectorSourceLifecycleMetadataResolver();
    }

    public VectorLifecycleMetadataCoverageReport Build(
        string operationId,
        string workspaceId,
        string collectionId,
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> entries,
        VectorIndexDiagnosticsReport diagnostics,
        VectorIndexStatusResponse status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(sourceItems);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(status);

        var items = BuildItems(sourceItems, entries);
        var total = items.Count;
        var known = items.Count(item => item.Metadata.IsKnownLifecycle);
        var warnings = BuildWarnings(items, diagnostics).ToArray();

        return new VectorLifecycleMetadataCoverageReport
        {
            OperationId = operationId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            ProviderId = status.Provider,
            EmbeddingModel = status.Model,
            Dimension = status.Dimension,
            TotalVectorSourceItems = total,
            KnownLifecycleCount = known,
            UnknownLifecycleCount = items.Count(item => !item.Metadata.IsKnownLifecycle),
            MissingReviewStatusCount = items.Count(item => !item.Metadata.HasReviewStatus),
            MissingReplacementInfoCount = items.Count(item => item.Metadata.MissingReplacementInfo),
            LegacySourceWithoutLifecycleCount = items.Count(item => item.Metadata.LegacySourceWithoutLifecycle),
            DeprecatedSourceWithoutLifecycleCount = items.Count(item => item.Metadata.DeprecatedSourceWithoutLifecycle),
            LifecycleCoverageRate = total == 0 ? 0 : known / (double)total,
            CoverageByLayer = BuildBuckets(items, item => item.Layer),
            CoverageByItemKind = BuildBuckets(items, item => item.ItemKind),
            CoverageBySourceType = BuildBuckets(items, item => item.Metadata.SourceType),
            DuplicateCount = diagnostics.DuplicateCount,
            OrphanCount = diagnostics.OrphanCount,
            DimensionMismatchCount = diagnostics.DimensionMismatchCount,
            ProviderUnavailableCount = diagnostics.ProviderUnavailableCount,
            Recommendation = ResolveRecommendation(total, known, diagnostics),
            Warnings = warnings,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public string ToMarkdown(VectorLifecycleMetadataCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector Lifecycle Metadata Coverage Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.CreatedAt:O}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Workspace: `{report.WorkspaceId}`");
        builder.AppendLine($"- Collection: `{report.CollectionId}`");
        builder.AppendLine($"- Provider: `{report.ProviderId}`");
        builder.AppendLine($"- Model: `{report.EmbeddingModel}`");
        builder.AppendLine($"- Dimension: `{report.Dimension}`");
        builder.AppendLine($"- TotalVectorSourceItems: `{report.TotalVectorSourceItems}`");
        builder.AppendLine($"- KnownLifecycleCount: `{report.KnownLifecycleCount}`");
        builder.AppendLine($"- UnknownLifecycleCount: `{report.UnknownLifecycleCount}`");
        builder.AppendLine($"- MissingReviewStatusCount: `{report.MissingReviewStatusCount}`");
        builder.AppendLine($"- MissingReplacementInfoCount: `{report.MissingReplacementInfoCount}`");
        builder.AppendLine($"- LegacySourceWithoutLifecycleCount: `{report.LegacySourceWithoutLifecycleCount}`");
        builder.AppendLine($"- DeprecatedSourceWithoutLifecycleCount: `{report.DeprecatedSourceWithoutLifecycleCount}`");
        builder.AppendLine($"- LifecycleCoverageRate: `{report.LifecycleCoverageRate:P2}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        AppendBucketTable(builder, "Coverage By Layer", report.CoverageByLayer.Values);
        AppendBucketTable(builder, "Coverage By Item Kind", report.CoverageByItemKind.Values);
        AppendBucketTable(builder, "Coverage By Source Type", report.CoverageBySourceType.Values);

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

    private IReadOnlyList<CoverageItem> BuildItems(
        IReadOnlyList<VectorReindexSourceItem> sourceItems,
        IReadOnlyList<VectorIndexEntry> entries)
    {
        var latestEntries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ItemId))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entry => entry.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        if (sourceItems.Count > 0)
        {
            return sourceItems
                .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
                .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(item =>
                {
                    latestEntries.TryGetValue(item.ItemId, out var entry);
                    return WithSidecarMetadata(item, entry);
                })
                .Select(item => new CoverageItem(
                    item.ItemId,
                    NormalizeBucketKey(item.Layer),
                    NormalizeBucketKey(item.ItemKind),
                    _resolver.Resolve(item)))
                .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return entries
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemId))
            .GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
            .Select(entry => new CoverageItem(
                entry.ItemId,
                NormalizeBucketKey(entry.Layer),
                NormalizeBucketKey(entry.ItemKind),
                _resolver.Resolve(entry)))
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, VectorLifecycleMetadataCoverageBucket> BuildBuckets(
        IReadOnlyList<CoverageItem> items,
        Func<CoverageItem, string> keySelector)
    {
        return items
            .GroupBy(item => NormalizeBucketKey(keySelector(item)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var values = group.ToArray();
                    var total = values.Length;
                    var known = values.Count(item => item.Metadata.IsKnownLifecycle);
                    return new VectorLifecycleMetadataCoverageBucket
                    {
                        Key = group.Key,
                        Total = total,
                        KnownLifecycleCount = known,
                        UnknownLifecycleCount = values.Count(item => !item.Metadata.IsKnownLifecycle),
                        MissingReviewStatusCount = values.Count(item => !item.Metadata.HasReviewStatus),
                        MissingReplacementInfoCount = values.Count(item => item.Metadata.MissingReplacementInfo),
                        LegacySourceWithoutLifecycleCount = values.Count(item => item.Metadata.LegacySourceWithoutLifecycle),
                        DeprecatedSourceWithoutLifecycleCount = values.Count(item => item.Metadata.DeprecatedSourceWithoutLifecycle),
                        LifecycleCoverageRate = total == 0 ? 0 : known / (double)total
                    };
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildWarnings(
        IReadOnlyList<CoverageItem> items,
        VectorIndexDiagnosticsReport diagnostics)
    {
        if (items.Any(item => !item.Metadata.IsKnownLifecycle))
        {
            yield return "存在 unknown lifecycle source；normal/current-task vector profile 会阻断这些候选。";
        }

        if (items.Any(item => item.Metadata.LegacySourceWithoutLifecycle || item.Metadata.DeprecatedSourceWithoutLifecycle))
        {
            yield return "存在 legacy/deprecated/historical source 缺少显式 lifecycle metadata。";
        }

        if (items.Any(item => item.Metadata.MissingReplacementInfo))
        {
            yield return "存在历史或替代链相关 source 缺少 replacement metadata。";
        }

        if (diagnostics.DuplicateCount > 0)
        {
            yield return "存在重复 vector entry，应先去重以避免存储噪声。";
        }

        if (diagnostics.DimensionMismatchCount > 0 || diagnostics.ProviderUnavailableCount > 0)
        {
            yield return "vector diagnostics 仍有阻断项，不能进入 retrieval shadow。";
        }
    }

    private static string ResolveRecommendation(
        int total,
        int known,
        VectorIndexDiagnosticsReport diagnostics)
    {
        if (diagnostics.ProviderUnavailableCount > 0
            || diagnostics.DimensionMismatchCount > 0
            || diagnostics.DuplicateCount > 0
            || diagnostics.OrphanCount > 0)
        {
            return VectorLifecycleMetadataCoverageRecommendations.BlockedByDiagnostics;
        }

        if (total == 0 || known == 0)
        {
            return VectorLifecycleMetadataCoverageRecommendations.BlockedByUnknownLifecycle;
        }

        return known == total
            ? VectorLifecycleMetadataCoverageRecommendations.ReadyForVectorShadowEval
            : VectorLifecycleMetadataCoverageRecommendations.NeedsLifecycleMetadataBackfill;
    }

    private static void AppendBucketTable(
        StringBuilder builder,
        string title,
        IEnumerable<VectorLifecycleMetadataCoverageBucket> buckets)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Key | Total | Known | Unknown | MissingReview | MissingReplacement | LegacyNoLifecycle | DeprecatedNoLifecycle | Coverage |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var bucket in buckets)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {bucket.Key} | {bucket.Total} | {bucket.KnownLifecycleCount} | {bucket.UnknownLifecycleCount} | {bucket.MissingReviewStatusCount} | {bucket.MissingReplacementInfoCount} | {bucket.LegacySourceWithoutLifecycleCount} | {bucket.DeprecatedSourceWithoutLifecycleCount} | {bucket.LifecycleCoverageRate:P2} |"));
        }

        builder.AppendLine();
    }

    private static string NormalizeBucketKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }

    private static VectorReindexSourceItem WithSidecarMetadata(
        VectorReindexSourceItem source,
        VectorIndexEntry? entry)
    {
        if (entry is null)
        {
            return source;
        }

        var metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entry.Metadata
                     .Where(pair => pair.Key.StartsWith(VectorSourceLifecycleMetadataResolver.BackfillPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            metadata[pair.Key] = pair.Value;
        }

        return new VectorReindexSourceItem
        {
            ItemId = source.ItemId,
            ItemKind = source.ItemKind,
            Layer = source.Layer,
            Text = source.Text,
            UpdatedAt = source.UpdatedAt,
            Metadata = metadata
        };
    }

    private sealed record CoverageItem(
        string ItemId,
        string Layer,
        string ItemKind,
        VectorSourceLifecycleMetadata Metadata);
}
