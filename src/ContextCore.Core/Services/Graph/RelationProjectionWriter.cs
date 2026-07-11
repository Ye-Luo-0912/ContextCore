using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// GRAPH-09 / 4.4：统一的关系投影写入边界。在 IRelationStore.BatchUpsertAsync 之前
/// 调用 <see cref="RelationProjectorOutputValidator"/> 进行验证，过滤 High 级诊断的 relation，
/// 再将剩余 relation 落库。有 High 级诊断的 relation 被跳过（不写入），但整批不抛异常。
/// 同时负责在 relation.Provenance 为空时填充调用方传入的 provenance。
/// </summary>
public sealed class RelationProjectionWriter : IRelationProjectionWriter
{
    private readonly IRelationStore _relationStore;
    private readonly RelationProjectorOutputValidator _validator;

    public RelationProjectionWriter(
        IRelationStore relationStore,
        RelationProjectorOutputValidator validator)
    {
        _relationStore = relationStore ?? throw new ArgumentNullException(nameof(relationStore));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <inheritdoc />
    public async Task<RelationProjectionWriteResult> WriteAsync(
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        if (string.IsNullOrWhiteSpace(provenance))
        {
            throw new ArgumentException("provenance must be non-empty.", nameof(provenance));
        }

        if (relations.Count == 0)
        {
            return new RelationProjectionWriteResult
            {
                Provenance = provenance,
                RequestedCount = 0,
                WrittenCount = 0,
                SkippedCount = 0,
                IsValid = true,
                Diagnostics = Array.Empty<RelationProjectorOutputDiagnostic>(),
                SkippedRelationIds = Array.Empty<string>()
            };
        }

        // 1. 填充 Provenance（仅在原值为空时）
        var prepared = EnsureProvenance(relations, provenance);

        // 2. 调用 validator 验证
        var diagnostics = _validator.Validate(prepared, provenance);

        // 3. 过滤 High 级诊断的 relation（被跳过，不写入）
        var skippedRelationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in diagnostics)
        {
            if (string.Equals(d.Severity, "High", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(d.RelationId))
            {
                skippedRelationIds.Add(d.RelationId);
            }
        }

        var writable = new List<ContextRelation>(prepared.Count);
        foreach (var r in prepared)
        {
            if (!skippedRelationIds.Contains(r.Id))
            {
                writable.Add(r);
            }
        }

        // 4. 落库
        if (writable.Count > 0)
        {
            await _relationStore.BatchUpsertAsync(writable, cancellationToken).ConfigureAwait(false);
        }

        var isValid = skippedRelationIds.Count == 0;

        return new RelationProjectionWriteResult
        {
            Provenance = provenance,
            RequestedCount = prepared.Count,
            WrittenCount = writable.Count,
            SkippedCount = skippedRelationIds.Count,
            IsValid = isValid,
            Diagnostics = diagnostics,
            SkippedRelationIds = skippedRelationIds.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static IReadOnlyList<ContextRelation> EnsureProvenance(
        IReadOnlyList<ContextRelation> relations,
        string provenance)
    {
        // 快速路径：所有 relation 都已有非空 Provenance，则原样返回
        var needsFill = false;
        foreach (var r in relations)
        {
            if (string.IsNullOrWhiteSpace(r.Provenance))
            {
                needsFill = true;
                break;
            }
        }

        if (!needsFill)
        {
            return relations;
        }

        var result = new List<ContextRelation>(relations.Count);
        foreach (var r in relations)
        {
            result.Add(string.IsNullOrWhiteSpace(r.Provenance)
                ? CloneWithProvenance(r, provenance)
                : r);
        }

        return result;
    }

    private static ContextRelation CloneWithProvenance(ContextRelation source, string provenance)
    {
        return new ContextRelation
        {
            Id = source.Id,
            WorkspaceId = source.WorkspaceId,
            CollectionId = source.CollectionId,
            SourceId = source.SourceId,
            TargetId = source.TargetId,
            RelationType = source.RelationType,
            Weight = source.Weight,
            Confidence = source.Confidence,
            SourceRefs = source.SourceRefs,
            Metadata = source.Metadata,
            CreatedAt = source.CreatedAt,
            SourceNodeKind = source.SourceNodeKind,
            TargetNodeKind = source.TargetNodeKind,
            Lifecycle = source.Lifecycle,
            ReviewStatus = source.ReviewStatus,
            UpdatedAt = source.UpdatedAt,
            Provenance = provenance
        };
    }
}
