using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>
/// P1-5：Outbox 装饰器，包装 <see cref="IRelationProjectionWriter"/> 与
/// <see cref="ITransactionalRelationProjectionWriter"/>。
/// </summary>
/// <remarks>
/// 语义：
/// <list type="bullet">
/// <item>
/// 事务路径（<see cref="WriteAsync(IReadOnlyList{ContextRelation}, string, IWriteTransactionScope, CancellationToken)"/>）：
/// 先把每条 writable relation 转换为 <see cref="RelationOutboxRecord"/> 调用
/// <see cref="IRelationOutboxStore.EnqueueBatchAsync"/>，scope 非空时与 relation upsert 共享同一 Postgres 事务——
/// commit 一起持久化，rollback 一起回滚，保证 outbox 与 relations 表强一致。
/// 然后委托给 inner writer 完成实际 upsert。
/// </item>
/// <item>
/// 非事务路径（<see cref="WriteAsync(IReadOnlyList{ContextRelation}, string, CancellationToken)"/>）：
/// 先委托 inner writer 完成 upsert，再用 scope=null 入队 outbox（best-effort 独立事务）。
/// 此时若进程在 relation 落库与 outbox 入队之间崩溃，outbox 会缺失该条记录——
/// RelationReconciliationWorker 的 stale-edge 扫描会作为兜底，但无法对这一条做精确回放。
/// </item>
/// <item>
/// outbox 记录只在 <see cref="RelationProjectorOutputValidator"/> 验证通过后入队
/// （与 <see cref="RelationProjectionWriteResult.WrittenCount"/> 语义对齐）。
/// 被 High 级诊断跳过的 relation 既不入库也不入队 outbox。
/// </item>
/// </list>
/// </remarks>
public sealed class OutboxAwareRelationProjectionWriter : IRelationProjectionWriter, ITransactionalRelationProjectionWriter
{
    private readonly IRelationProjectionWriter _inner;
    private readonly ITransactionalRelationProjectionWriter _txInner;
    private readonly IRelationOutboxStore _outboxStore;

    public OutboxAwareRelationProjectionWriter(
        RelationProjectionWriter inner,
        IRelationOutboxStore outboxStore)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(outboxStore);
        _inner = inner;
        _txInner = inner; // RelationProjectionWriter 同时实现两个接口
        _outboxStore = outboxStore;
    }

    /// <inheritdoc />
    public async Task<RelationProjectionWriteResult> WriteAsync(
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        CancellationToken cancellationToken = default)
    {
        // 非事务路径：先委托 inner writer 完成 upsert，再 best-effort 入队 outbox。
        // 调用顺序：inner.WriteAsync 不抛异常即视为 relation 已落库，再入队 outbox。
        // 若 inner 抛异常，outbox 不入队（关系也未落库，无 outbox 缺失风险）。
        var result = await _inner.WriteAsync(relations, provenance, cancellationToken).ConfigureAwait(false);
        await EnqueueOutboxForResultAsync(result, relations, provenance, scope: null, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<RelationProjectionWriteResult> WriteAsync(
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        IWriteTransactionScope scope,
        CancellationToken cancellationToken = default)
    {
        // 事务路径：先委托 inner writer 完成 validator + filter + upsert（在 scope 内），
        // 然后用同一 scope 入队 outbox——所有 outbox 行与 relation 行共享同一 Postgres 事务。
        // 注意：inner writer 完成后还未 commit，但 validator 已运行，
        // 我们可以根据 result.WrittenCount 与 result.SkippedRelationIds 推导出 writable 子集。
        var result = await _txInner.WriteAsync(relations, provenance, scope, cancellationToken).ConfigureAwait(false);
        await EnqueueOutboxForResultAsync(result, relations, provenance, scope: scope, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 根据 inner writer 返回的 result 推导出已落库的 writable 子集，为每条 relation 构造一条 outbox 记录入队。
    /// 被 High 级诊断跳过的 relation（SkippedRelationIds）既未落库也不入队 outbox。
    /// </summary>
    private async Task EnqueueOutboxForResultAsync(
        RelationProjectionWriteResult result,
        IReadOnlyList<ContextRelation> relations,
        string provenance,
        IWriteTransactionScope? scope,
        CancellationToken cancellationToken)
    {
        if (result.WrittenCount == 0) return;

        var skipped = result.SkippedRelationIds.Count > 0
            ? new HashSet<string>(result.SkippedRelationIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var records = new List<RelationOutboxRecord>(result.WrittenCount);
        var now = DateTimeOffset.UtcNow;
        foreach (var relation in relations)
        {
            if (skipped is not null && skipped.Contains(relation.Id)) continue;

            records.Add(new RelationOutboxRecord
            {
                OutboxId = Guid.NewGuid().ToString("N"),
                WorkspaceId = relation.WorkspaceId,
                CollectionId = relation.CollectionId,
                RelationId = relation.Id,
                OperationKind = RelationOutboxOperationKind.Upsert,
                Provenance = string.IsNullOrWhiteSpace(relation.Provenance) ? provenance : relation.Provenance,
                Payload = relation,
                State = RelationOutboxStates.Pending,
                MaxRetryCount = 3,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (records.Count > 0)
        {
            await _outboxStore.EnqueueBatchAsync(records, scope, cancellationToken).ConfigureAwait(false);
        }
    }
}
