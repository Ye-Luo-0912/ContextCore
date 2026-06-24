using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>解析 vector source 的生命周期元数据；只读取运行时 metadata，不读取 eval label 或样本标识。</summary>
public sealed class VectorSourceLifecycleMetadataResolver
{
    private const string LifecycleKey = "lifecycle";
    private const string StatusKey = "status";
    private const string ReviewStatusKey = "reviewStatus";
    private const string StableReviewStatusKey = "stableReviewStatus";
    private const string SourceTypeKey = "sourceType";
    private const string SourceKindKey = "sourceKind";
    private const string SourceKey = "source";
    private const string SourceModeKey = "sourceMode";
    public const string BackfillPrefix = "vectorLifecycleBackfill.";
    public const string BackfilledLifecycleKey = BackfillPrefix + "lifecycle";
    public const string BackfilledReviewStatusKey = BackfillPrefix + "reviewStatus";
    public const string BackfilledMetadataSourceKey = BackfillPrefix + "metadataSource";
    public const string BackfilledReasonKey = BackfillPrefix + "reason";
    public const string BackfilledEvidenceKeysKey = BackfillPrefix + "evidenceMetadataKeys";
    public const string BackfilledPolicyVersionKey = BackfillPrefix + "policyVersion";
    public const string BackfilledAppliedAtKey = BackfillPrefix + "appliedAt";

    private static readonly string[] ReplacementKeys =
    [
        "supersededBy",
        "replacedBy",
        "replacementItemId",
        "replacementId",
        "superseded_by",
        "replaced_by"
    ];

    public VectorSourceLifecycleMetadata Resolve(VectorIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Resolve(entry.Layer, entry.ItemKind, entry.Metadata);
    }

    public VectorSourceLifecycleMetadata Resolve(VectorQueryPreviewCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Resolve(candidate.Layer, candidate.ItemKind, candidate.Metadata);
    }

    public VectorSourceLifecycleMetadata Resolve(VectorReindexSourceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Resolve(item.Layer, item.ItemKind, item.Metadata);
    }

    private static VectorSourceLifecycleMetadata Resolve(
        string layer,
        string itemKind,
        IReadOnlyDictionary<string, string> metadata)
    {
        var explicitLifecycle = Get(metadata, LifecycleKey);
        var backfilledLifecycle = Get(metadata, BackfilledLifecycleKey);
        var status = Get(metadata, StatusKey);
        var reviewStatus = ResolveReviewStatus(metadata);
        var lifecycle = ResolveLifecycle(explicitLifecycle, status, backfilledLifecycle);
        var metadataSource = ResolveMetadataSource(explicitLifecycle, status, backfilledLifecycle, metadata);
        var sourceType = ResolveSourceType(metadata);
        var hasReplacementInfo = ReplacementKeys.Any(key => HasMetadataValue(metadata, key));
        var hasReviewStatus = !string.IsNullOrWhiteSpace(reviewStatus);
        var known = !string.IsNullOrWhiteSpace(lifecycle);
        var deprecated = IsDeprecated(lifecycle) || IndicatesDeprecatedSource(layer, sourceType);
        var historical = IsHistorical(lifecycle) || IndicatesHistoricalSource(layer, sourceType);
        var rejected = IsRejected(lifecycle);
        var superseded = IsSuperseded(lifecycle) || hasReplacementInfo;
        var replacementMissing = (IsSuperseded(lifecycle) || historical || deprecated) && !hasReplacementInfo;
        var legacySourceWithoutLifecycle = !known && IndicatesLegacySource(sourceType);
        var deprecatedSourceWithoutLifecycle = !known && (IndicatesDeprecatedSource(layer, sourceType) || IndicatesHistoricalSource(layer, sourceType));
        var complete = known
                       && (!replacementMissing || hasReplacementInfo)
                       && !legacySourceWithoutLifecycle
                       && !deprecatedSourceWithoutLifecycle;

        return new VectorSourceLifecycleMetadata
        {
            Lifecycle = lifecycle,
            ReviewStatus = reviewStatus,
            MetadataSource = metadataSource,
            SourceType = sourceType,
            Layer = layer,
            ItemKind = itemKind,
            IsKnownLifecycle = known,
            HasReviewStatus = hasReviewStatus,
            HasReplacementInfo = hasReplacementInfo,
            MissingReplacementInfo = replacementMissing,
            IsLifecycleMetadataComplete = complete,
            IsDeprecated = deprecated,
            IsHistorical = historical,
            IsRejected = rejected,
            IsSuperseded = superseded,
            LegacySourceWithoutLifecycle = legacySourceWithoutLifecycle,
            DeprecatedSourceWithoutLifecycle = deprecatedSourceWithoutLifecycle,
            RequiresAuditProfile = historical || deprecated || legacySourceWithoutLifecycle || deprecatedSourceWithoutLifecycle
        };
    }

    private static string ResolveReviewStatus(IReadOnlyDictionary<string, string> metadata)
    {
        var stableReviewStatus = Get(metadata, StableReviewStatusKey);
        if (!string.IsNullOrWhiteSpace(stableReviewStatus))
        {
            return stableReviewStatus;
        }

        var reviewStatus = Get(metadata, ReviewStatusKey);
        return string.IsNullOrWhiteSpace(reviewStatus)
            ? Get(metadata, BackfilledReviewStatusKey)
            : reviewStatus;
    }

    private static string ResolveLifecycle(
        string explicitLifecycle,
        string status,
        string backfilledLifecycle)
    {
        if (!string.IsNullOrWhiteSpace(explicitLifecycle))
        {
            return explicitLifecycle;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            return status;
        }

        return backfilledLifecycle;
    }

    private static string ResolveMetadataSource(
        string explicitLifecycle,
        string status,
        string backfilledLifecycle,
        IReadOnlyDictionary<string, string> metadata)
    {
        if (!string.IsNullOrWhiteSpace(explicitLifecycle))
        {
            return LifecycleKey;
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            return StatusKey;
        }

        if (!string.IsNullOrWhiteSpace(backfilledLifecycle))
        {
            var source = Get(metadata, BackfilledMetadataSourceKey);
            return string.IsNullOrWhiteSpace(source) ? "vector_lifecycle_backfill" : source;
        }

        return string.Empty;
    }

    private static string ResolveSourceType(IReadOnlyDictionary<string, string> metadata)
    {
        var sourceType = Get(metadata, SourceTypeKey);
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            return sourceType;
        }

        var sourceKind = Get(metadata, SourceKindKey);
        if (!string.IsNullOrWhiteSpace(sourceKind))
        {
            return sourceKind;
        }

        var source = Get(metadata, SourceKey);
        if (!string.IsNullOrWhiteSpace(source))
        {
            return source;
        }

        return Get(metadata, SourceModeKey);
    }

    private static string Get(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    private static bool HasMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsDeprecated(string lifecycle)
    {
        return string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistorical(string lifecycle)
    {
        return string.Equals(lifecycle, "Historical", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRejected(string lifecycle)
    {
        return string.Equals(lifecycle, "Rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuperseded(string lifecycle)
    {
        return string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesLegacySource(string sourceType)
    {
        return string.Equals(sourceType, "legacy", StringComparison.OrdinalIgnoreCase)
               || string.Equals(sourceType, "historical", StringComparison.OrdinalIgnoreCase)
               || string.Equals(sourceType, "deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesDeprecatedSource(string layer, string sourceType)
    {
        return string.Equals(sourceType, "deprecated", StringComparison.OrdinalIgnoreCase)
               || string.Equals(layer, "deprecated_evidence", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IndicatesHistoricalSource(string layer, string sourceType)
    {
        return string.Equals(sourceType, "historical", StringComparison.OrdinalIgnoreCase)
               || string.Equals(layer, "historical_context", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>vector source 生命周期元数据解析结果。</summary>
public sealed class VectorSourceLifecycleMetadata
{
    public string Lifecycle { get; init; } = string.Empty;

    public string ReviewStatus { get; init; } = string.Empty;

    public string MetadataSource { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string ItemKind { get; init; } = string.Empty;

    public bool IsKnownLifecycle { get; init; }

    public bool HasReviewStatus { get; init; }

    public bool HasReplacementInfo { get; init; }

    public bool MissingReplacementInfo { get; init; }

    public bool IsLifecycleMetadataComplete { get; init; }

    public bool IsDeprecated { get; init; }

    public bool IsHistorical { get; init; }

    public bool IsRejected { get; init; }

    public bool IsSuperseded { get; init; }

    public bool LegacySourceWithoutLifecycle { get; init; }

    public bool DeprecatedSourceWithoutLifecycle { get; init; }

    public bool RequiresAuditProfile { get; init; }
}
