using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// / 4.4：统一的关系投影写入边界。在 IRelationStore.BatchUpsertAsync 之前
/// 调用 <see cref="RelationProjectorOutputValidator"/> 进行验证，过滤 High 级诊断的 relation，
/// 再将剩余 relation 落库。有 High 级诊断的 relation 被跳过（不写入），但整批不抛异常。
/// 同时负责在 relation.Provenance 为空时填充调用方传入的 provenance。
/// </summary>
/// <remarks>
/// 同时实现 <see cref="ITransactionalRelationProjectionWriter"/>——当注入的 <see cref="IRelationStore"/>
/// 支持 <see cref="ITransactionalRelationStore"/> 时，事务路径走 BatchUpsertAsync(relations, scope, ct) 重载，
/// 复用调用方提供的 <see cref="IWriteTransactionScope"/>。
/// </remarks>
public sealed class RelationProjectionWriter : IRelationProjectionWriter, ITransactionalRelationProjectionWriter
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
            return EmptyResult(provenance);
        }

        // 1. 填充 Provenance（仅在原值为空时）
        var prepared = EnsureProvenance(relations, provenance);

        // 2. 调用 validator 验证
        var diagnostics = _validator.Validate(prepared, provenance);

        // 3. 过滤 High 级诊断的 relation（被跳过，不写入）
        var (writable, skippedRelationIds, isValid) = FilterWritable(prepared, diagnostics);

        // 4. 落库
        if (writable.Count > 0)
        {
            await _relationStore.BatchUpsertAsync(writable, cancellationToken).ConfigureAwait(false);
        }

        return BuildResult(provenance, prepared.Count, writable.Count, skippedRelationIds, isValid, diagnostics);
    }

    /// <summary>
    /// 在指定事务作用域内写入关系。仅当注入的 <see cref="IRelationStore"/> 实现
    /// <see cref="ITransactionalRelationStore"/> 时才走事务路径——否则抛出 <see cref="InvalidOperationException"/>。
    /// 提交由调用方通过 scope.CommitAsync 完成。
    /// </summary>
    public async Task<RelationProjectionWriteResult> WriteAsync(
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrWhiteSpace(provenance))
        {
            throw new ArgumentException("provenance must be non-empty.", nameof(provenance));
        }
        if (_relationStore is not ITransactionalRelationStore txStore)
        {
            throw new InvalidOperationException(
                "底层 IRelationStore 未实现 ITransactionalRelationStore，无法走事务路径。" +
                "请确保 Postgres provider 已正确注册或回退到无事务路径。");
        }
        if (!scope.IsActive)
        {
            throw new InvalidOperationException("事务作用域已结束（Commit/Rollback），无法继续写入。");
        }

        if (relations.Count == 0)
        {
            return EmptyResult(provenance);
        }

        // 1. 填充 Provenance
        var prepared = EnsureProvenance(relations, provenance);

        // 2. 验证
        var diagnostics = _validator.Validate(prepared, provenance);

        // 3. 过滤
        var (writable, skippedRelationIds, isValid) = FilterWritable(prepared, diagnostics);

        // 4. 在事务作用域内落库
        if (writable.Count > 0)
        {
            await txStore.BatchUpsertAsync(writable, scope, cancellationToken).ConfigureAwait(false);
        }

        return BuildResult(provenance, prepared.Count, writable.Count, skippedRelationIds, isValid, diagnostics);
    }

    private static RelationProjectionWriteResult EmptyResult(string provenance) => new()
    {
        Provenance = provenance,
        RequestedCount = 0,
        WrittenCount = 0,
        SkippedCount = 0,
        IsValid = true,
        Diagnostics = Array.Empty<RelationProjectorOutputDiagnostic>(),
        SkippedRelationIds = Array.Empty<string>()
    };

    private static (List<ContextRelation> Writable, HashSet<string> SkippedRelationIds, bool IsValid) FilterWritable(
        IReadOnlyList<ContextRelation> prepared,
        IReadOnlyList<RelationProjectorOutputDiagnostic> diagnostics)
    {
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

        return (writable, skippedRelationIds, skippedRelationIds.Count == 0);
    }

    private static RelationProjectionWriteResult BuildResult(
        string provenance,
        int requestedCount,
        int writtenCount,
        HashSet<string> skippedRelationIds,
        bool isValid,
        IReadOnlyList<RelationProjectorOutputDiagnostic> diagnostics)
    {
        return new RelationProjectionWriteResult
        {
            Provenance = provenance,
            RequestedCount = requestedCount,
            WrittenCount = writtenCount,
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
