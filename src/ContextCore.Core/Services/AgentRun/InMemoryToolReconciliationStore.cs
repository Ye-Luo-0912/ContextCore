using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// InMemoryToolReconciliationStore — Tool 对账记录存储（进程内默认实现）
//
// 支撑 Run 级"未裁决不完成"约束：
// - CreateAsync 按 RunId+RequestId 幂等（重复创建返回既有记录）；
// - HasUnresolvedForRunAsync 供 CompleteAsync 门禁查询（Pending/Running 存在 → 禁止 Completed）；
// - TryBeginAsync CAS Pending→Running 供 ToolReconciliationWorker 并发互斥；
// - MarkResolvedAsync / MarkRejectedAsync 供 Worker / 人工 resolve 端点裁决。
// ===========================================================================

/// <summary>
/// Tool 对账记录存储（进程内实现）。
/// </summary>
public sealed class InMemoryToolReconciliationStore : IToolReconciliationStore
{
    private readonly ConcurrentDictionary<string, ToolReconciliationRecord> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ToolReconciliationRecord> CreateAsync(ToolReconciliationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        // 按 RunId+RequestId 幂等：同一 Tool 调用只保留一条对账记录。
        var existing = _records.Values.FirstOrDefault(r =>
            string.Equals(r.RunId, record.RunId, StringComparison.Ordinal)
            && string.Equals(r.RequestId, record.RequestId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return ValueTask.FromResult(existing);
        }

        var created = record with { CreatedAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt };
        _records[record.ReconciliationId] = created;
        return ValueTask.FromResult(created);
    }

    /// <inheritdoc />
    public ValueTask<ToolReconciliationRecord?> GetAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _records.TryGetValue(reconciliationId, out var record);
        return ValueTask.FromResult(record);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = _records.Values
            .Where(r => string.Equals(r.RunId, runId, StringComparison.Ordinal))
            .OrderBy(r => r.CreatedAt)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ToolReconciliationRecord>>(records);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolReconciliationRecord>> QueryByExternalOperationIdAsync(string externalOperationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(externalOperationId);
        var records = _records.Values
            .Where(r => string.Equals(r.ExternalOperationId, externalOperationId, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ToolReconciliationRecord>>(records);
    }

    /// <inheritdoc />
    public ValueTask<ReconciliationListResult> ListAsync(ReconciliationQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);
        var now = DateTimeOffset.UtcNow;
        var filtered = _records.Values
            .Where(r => query.WorkspaceId is null || string.Equals(r.WorkspaceId, query.WorkspaceId, StringComparison.Ordinal))
            .Where(r => query.RunId is null || string.Equals(r.RunId, query.RunId, StringComparison.Ordinal))
            .Where(r => query.Status is null || r.Status == query.Status)
            .Where(r => !query.OverdueOnly || IsOverdue(r, now))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

        var overdueCount = filtered.Count(r => IsOverdue(r, now));
        var page = filtered
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit > 0 ? query.Limit : 50, 1, 200))
            .ToList();

        return ValueTask.FromResult(new ReconciliationListResult
        {
            Items = page,
            Total = filtered.Count,
            OverdueCount = overdueCount
        });
    }

    /// <inheritdoc />
    public ValueTask<bool> HasUnresolvedForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasUnresolved = _records.Values.Any(r =>
            string.Equals(r.RunId, runId, StringComparison.Ordinal)
            && (r.Status == ToolReconciliationStatus.Pending || r.Status == ToolReconciliationStatus.Running));
        return ValueTask.FromResult(hasUnresolved);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var records = _records.Values
            .Where(r => r.Status == ToolReconciliationStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<ToolReconciliationRecord>>(records);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryBeginAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updated = false;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                if (existing.Status != ToolReconciliationStatus.Pending)
                {
                    return existing; // 已 Running/Resolved/Rejected → 不重复接管
                }
                updated = true;
                return existing with
                {
                    Status = ToolReconciliationStatus.Running,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            });
        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryResetToPendingAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updated = false;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                if (existing.Status != ToolReconciliationStatus.Running)
                {
                    return existing; // 仅 Running 可回退
                }
                updated = true;
                return existing with
                {
                    Status = ToolReconciliationStatus.Pending,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            });
        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkResolvedAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkTerminal(reconciliationId, ToolReconciliationStatus.Resolved, outcome));
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkRejectedAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(MarkTerminal(reconciliationId, ToolReconciliationStatus.Rejected, outcome));
    }

    /// <summary>过期判定：设置了截止且未裁决（Pending/Running）且已超期。</summary>
    private static bool IsOverdue(ToolReconciliationRecord record, DateTimeOffset now)
        => record.DeadlineUtc.HasValue
           && (record.Status == ToolReconciliationStatus.Pending || record.Status == ToolReconciliationStatus.Running)
           && record.DeadlineUtc.Value < now;

    private bool MarkTerminal(string reconciliationId, ToolReconciliationStatus target, ToolReconciliationOutcome outcome)
    {
        var updated = false;
        _records.AddOrUpdate(
            reconciliationId,
            _ => throw new InvalidOperationException($"对账记录不存在：{reconciliationId}"),
            (_, existing) =>
            {
                if (existing.Status == ToolReconciliationStatus.Resolved || existing.Status == ToolReconciliationStatus.Rejected)
                {
                    return existing; // 已裁决 → 幂等
                }
                updated = true;
                var now = DateTimeOffset.UtcNow;
                return existing with
                {
                    Status = target,
                    SideEffectOccurred = outcome.SideEffectOccurred,
                    Result = outcome.Result,
                    Reason = outcome.Error,
                    UpdatedAt = now,
                    ResolvedAt = now
                };
            });
        return updated;
    }
}
