using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>标准化 legacy relation type，并为 shadow-only fixture 构建元数据回填。</summary>
public sealed class RelationTypeNormalizer
{
    public const string PolicyVersion = "graph-foundation-g5.1";

    public const string FixtureBackfillCreatedFrom = "relation_corpus_fixture_backfill";

    /// <summary>
    /// 用于存储旧版关系类型到新标准关系类型的映射。此字典帮助在处理遗留数据时，将不再使用的关系类型名称转换为当前系统支持的标准名称。
    /// 映射采用不区分大小写的方式进行比较，确保了无论输入的原始关系类型格式如何，都能正确找到对应的标准化版本。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["supersedes"] = ContextRelationTypes.Replaces,
            ["is_superseded_by"] = ContextRelationTypes.SupersededBy,
            ["replacedBy"] = "replaced_by",
            ["dependsOn"] = ContextRelationTypes.DependsOn,
            ["evidenceFor"] = ContextRelationTypes.EvidenceFor
        };

    public bool TryNormalize(string relationType, out string normalizedType)
    {
        normalizedType = Normalize(relationType);
        return !string.Equals(relationType, normalizedType, StringComparison.OrdinalIgnoreCase);
    }

    public string Normalize(string relationType)
    {
        if (string.IsNullOrWhiteSpace(relationType))
        {
            return string.Empty;
        }

        return LegacyMap.TryGetValue(relationType.Trim(), out var normalized)
            ? normalized
            : relationType.Trim();
    }

    public IReadOnlyDictionary<string, string> GetLegacyMappings()
    {
        return LegacyMap;
    }

    public ContextRelation NormalizeRelation(ContextRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        var normalizedType = Normalize(relation.RelationType);
        if (string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase))
        {
            return Clone(relation, relation.RelationType, relation.Confidence, relation.SourceRefs, relation.Metadata);
        }

        var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["originalRelationType"] = relation.RelationType,
            ["normalizedRelationType"] = normalizedType
        };
        return Clone(relation, normalizedType, relation.Confidence, relation.SourceRefs, metadata);
    }

    public ContextRelation NormalizeAndBackfillFixtureRelation(
        ContextRelation relation,
        string sourceOperationId = "relation-corpus-hygiene-g5.1")
    {
        ArgumentNullException.ThrowIfNull(relation);

        var normalizedType = Normalize(relation.RelationType);
        var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase))
        {
            metadata.TryAdd("originalRelationType", relation.RelationType);
            metadata["normalizedRelationType"] = normalizedType;
        }

        var sourceRefs = relation.SourceRefs
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var confidence = relation.Confidence;

        if (CanBackfillDeterministicEvidence(relation))
        {
            metadata.TryAdd("evidenceRefs", $"fixture:relation:{relation.Id}");
            metadata.TryAdd("sourceRefs", string.Join(",", new[] { relation.SourceId, relation.TargetId }
                .Where(item => !string.IsNullOrWhiteSpace(item))));
            metadata.TryAdd("sourceOperationId", sourceOperationId);
            metadata.TryAdd("sourceItemId", relation.SourceId);
            metadata.TryAdd("createdFrom", FixtureBackfillCreatedFrom);
            metadata.TryAdd("confidenceReason", "deterministic_fixture_relation");
            metadata.TryAdd("lifecycle", StableMemoryLifecycle.Active);
            metadata.TryAdd("reviewStatus", RelationReviewStatuses.Reviewed);
            metadata.TryAdd("policyVersion", PolicyVersion);

            if (confidence <= 0)
            {
                confidence = 1.0;
            }

            metadata.TryAdd("confidence", confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            AddIfMissing(sourceRefs, relation.SourceId);
            AddIfMissing(sourceRefs, relation.TargetId);
            AddIfMissing(sourceRefs, $"fixture:relation:{relation.Id}");
        }
        else if (!HasEvidence(relation))
        {
            metadata.TryAdd("reviewStatus", RelationReviewStatuses.NeedsEvidence);
            metadata.TryAdd("lifecycle", ContextMemoryStatus.Candidate.ToString());
            metadata.TryAdd("policyVersion", PolicyVersion);
        }

        return Clone(relation, normalizedType, confidence, sourceRefs, metadata);
    }

    public static bool HasEvidence(ContextRelation relation)
    {
        return relation.SourceRefs.Count > 0
            || HasMetadataValue(relation.Metadata, "evidenceRefs")
            || HasMetadataValue(relation.Metadata, "sourceRefs")
            || HasMetadataValue(relation.Metadata, "reviewId")
            || HasMetadataValue(relation.Metadata, "lastReviewId")
            || HasMetadataValue(relation.Metadata, "sourceOperationId");
    }

    public static bool HasConfidence(ContextRelation relation)
    {
        return relation.Confidence > 0 || HasMetadataValue(relation.Metadata, "confidence");
    }

    public static bool HasLifecycle(ContextRelation relation)
    {
        return HasMetadataValue(relation.Metadata, "lifecycle");
    }

    public static bool HasReviewStatus(ContextRelation relation)
    {
        return HasMetadataValue(relation.Metadata, "reviewStatus");
    }

    public static bool CanBackfillDeterministicEvidence(ContextRelation relation)
    {
        if (string.IsNullOrWhiteSpace(relation.Id)
            || string.IsNullOrWhiteSpace(relation.SourceId)
            || string.IsNullOrWhiteSpace(relation.TargetId))
        {
            return false;
        }

        if (relation.WorkspaceId.StartsWith("eval", StringComparison.OrdinalIgnoreCase)
            || relation.Id.StartsWith("rel:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var createdFrom = ReadMetadata(relation.Metadata, "createdFrom", "source", "generatedBy");
        return createdFrom.Contains("fixture", StringComparison.OrdinalIgnoreCase)
            || createdFrom.Contains("deterministic", StringComparison.OrdinalIgnoreCase)
            || createdFrom.Contains("stable_lifecycle_review", StringComparison.OrdinalIgnoreCase);
    }

    private static ContextRelation Clone(
        ContextRelation relation,
        string relationType,
        double confidence,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata)
    {
        return new ContextRelation
        {
            Id = relation.Id,
            WorkspaceId = relation.WorkspaceId,
            CollectionId = relation.CollectionId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relationType,
            Weight = relation.Weight,
            Confidence = confidence,
            SourceRefs = sourceRefs.ToArray(),
            Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            CreatedAt = relation.CreatedAt
        };
    }

    private static bool HasMetadataValue(IReadOnlyDictionary<string, string> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static string ReadMetadata(IReadOnlyDictionary<string, string> metadata, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static void AddIfMissing(List<string> values, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }
}
