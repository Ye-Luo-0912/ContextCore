using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Graph;

namespace ContextCore.Evaluation.Runners;

/// <summary>
/// eval/fixture/deterministic 感知的关系回填策略。
/// 将 eval 特判从生产 Core 移到 Evaluation 工具层。
/// 实现 <see cref="IRelationBackfillPolicy"/>，供 <see cref="RelationGraphValidationService"/> 和 eval runner 使用。
/// </summary>
public sealed class RelationEvalBackfillPolicy : IRelationBackfillPolicy
{
    public const string PolicyVersion = "graph-foundation-g5.1";

    public const string FixtureBackfillCreatedFrom = "relation_corpus_fixture_backfill";

    private readonly RelationTypeNormalizer _typeNormalizer = new();

    /// <summary>
    /// 判断关系是否可确定性回填证据。
    /// eval workspace、rel: 前缀 ID、或 createdFrom 含 fixture/deterministic/stable_lifecycle_review 的关系可回填。
    /// </summary>
    public bool CanBackfillDeterministicEvidence(ContextRelation relation)
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

    /// <summary>
    /// 标准化关系类型并回填 fixture 元数据（eval corpus hygiene 专用）。
    /// 对可确定性回填的关系填充 evidence/sourceRefs/lifecycle/reviewStatus；
    /// 对缺少证据的关系标记 NeedsEvidence/Candidate。
    /// </summary>
    public ContextRelation NormalizeAndBackfillFixtureRelation(
        ContextRelation relation,
        string sourceOperationId = "relation-corpus-hygiene-g5.1")
    {
        ArgumentNullException.ThrowIfNull(relation);

        var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
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
        // 正式字段作为默认值
        var lifecycle = relation.Lifecycle;
        var reviewStatus = relation.ReviewStatus;

        if (CanBackfillDeterministicEvidence(relation))
        {
            metadata.TryAdd("evidenceRefs", $"fixture:relation:{relation.Id}");
            metadata.TryAdd("sourceRefs", string.Join(",", new[] { relation.SourceId, relation.TargetId }
                .Where(item => !string.IsNullOrWhiteSpace(item))));
            metadata.TryAdd("sourceOperationId", sourceOperationId);
            metadata.TryAdd("sourceItemId", relation.SourceId);
            metadata.TryAdd("createdFrom", FixtureBackfillCreatedFrom);
            metadata.TryAdd("confidenceReason", "deterministic_fixture_relation");
            // Metadata 仅兜底（旧数据迁移）
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
            // 正式字段
            lifecycle = StableMemoryLifecycle.Active;
            reviewStatus = RelationReviewStatuses.Reviewed;
        }
        else if (!RelationTypeNormalizer.HasEvidence(relation))
        {
            // Metadata 仅兜底（旧数据迁移）
            metadata.TryAdd("reviewStatus", RelationReviewStatuses.NeedsEvidence);
            metadata.TryAdd("lifecycle", ContextMemoryStatus.Candidate.ToString());
            metadata.TryAdd("policyVersion", PolicyVersion);
            // 正式字段
            reviewStatus = RelationReviewStatuses.NeedsEvidence;
            lifecycle = ContextMemoryStatus.Candidate.ToString();
        }

        return Clone(relation, normalizedType, confidence, sourceRefs, metadata, lifecycle, reviewStatus);
    }

    private static ContextRelation Clone(
        ContextRelation relation,
        string relationType,
        double confidence,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyDictionary<string, string> metadata,
        string? lifecycleOverride = null,
        string? reviewStatusOverride = null)
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
            CreatedAt = relation.CreatedAt,
            // 保留正式字段
            SourceNodeKind = relation.SourceNodeKind,
            TargetNodeKind = relation.TargetNodeKind,
            Lifecycle = lifecycleOverride ?? relation.Lifecycle,
            ReviewStatus = reviewStatusOverride ?? relation.ReviewStatus,
            UpdatedAt = relation.UpdatedAt,
            Provenance = relation.Provenance
        };
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
