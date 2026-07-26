using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// R28-E P1-4：InMemoryToolDispatchJournal
//
// 进程内 Tool Dispatch Journal 默认实现。
// 维护每个 RequestId 的状态机进度（Prepared → Dispatched → Committed → ResultDelivered）。
//
// 设计决策：
//   - 使用 ConcurrentDictionary 支持多线程并发访问。
//   - 状态推进为单向（只能向前），违反时抛 InvalidOperationException。
//   - P0-3：缺失前驱记录时抛 InvalidOperationException（不再 auto-create stub），
//     与 PostgresToolDispatchJournal 的 expected-state CAS 语义一致，保证审计链完整。
//   - 进程内实现仅用于测试/单机部署；生产部署应替换为持久化实现（DB/WAL）。
//   - 不持久化到磁盘：进程崩溃后状态丢失。生产部署需注入持久化实现。
// ===========================================================================

/// <summary>
/// R28-E P1-4：进程内 Tool Dispatch Journal 默认实现。
/// 维护 tool 调用状态机进度以支持 exactly-once 语义。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后 journal 状态丢失。
/// 生产部署应注入基于 DB/WAL 的持久化实现以保证崩溃恢复的 exactly-once。
///
/// <b>P0-3</b>：Mark* 方法在缺失前驱记录时抛 <see cref="InvalidOperationException"/>，
/// 不再 auto-create stub 条目——保证审计链完整，与 PostgresToolDispatchJournal 语义一致。
/// </remarks>
public sealed class InMemoryToolDispatchJournal : IToolDispatchJournal
{
    private readonly ConcurrentDictionary<string, ToolDispatchJournalEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != ToolDispatchState.Prepared)
        {
            throw new ArgumentException(
                $"PrepareAsync 入口的 State 必须为 Prepared，实际为 {entry.State}。", nameof(entry));
        }

        // 若已存在条目，保留原有状态（幂等：重复 Prepare 不覆盖已推进的状态）
        _entries.TryAdd(entry.RequestId, entry);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkDispatchedAsync(string requestId, string? externalOperationId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            // P0-3：缺失 Prepared 前驱 → 抛异常（不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录：request_id={requestId}，目标状态=Dispatched（期望前驱=Prepared）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) =>
            {
                ValidateForwardTransition(existing.State, ToolDispatchState.Dispatched);
                return existing with
                {
                    State = ToolDispatchState.Dispatched,
                    ExternalOperationId = externalOperationId ?? existing.ExternalOperationId,
                    UpdatedAt = now
                };
            });

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkCommittedAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            // P0-3：缺失 Dispatched 前驱 → 抛异常（不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录：request_id={requestId}，目标状态=Committed（期望前驱=Dispatched）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) =>
            {
                ValidateForwardTransition(existing.State, ToolDispatchState.Committed);
                return existing with
                {
                    State = ToolDispatchState.Committed,
                    UpdatedAt = now
                };
            });

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkResultDeliveredAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            // P0-3：缺失 Committed 前驱 → 抛异常（不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录：request_id={requestId}，目标状态=ResultDelivered（期望前驱=Committed）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) =>
            {
                ValidateForwardTransition(existing.State, ToolDispatchState.ResultDelivered);
                return existing with
                {
                    State = ToolDispatchState.ResultDelivered,
                    UpdatedAt = now
                };
            });

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ToolDispatchJournalEntry?> GetEntryAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return ValueTask.FromResult<ToolDispatchJournalEntry?>(null);
        }

        _entries.TryGetValue(requestId, out var entry);
        return ValueTask.FromResult(entry);
    }

    /// <summary>验证状态向前推进（不可逆退）。</summary>
    private static void ValidateForwardTransition(ToolDispatchState current, ToolDispatchState target)
    {
        if ((int)target <= (int)current)
        {
            throw new InvalidOperationException(
                $"Tool dispatch state 不可逆退：当前={current}，目标={target}。" +
                $"状态机只能向前推进：Prepared → Dispatched → Committed → ResultDelivered。");
        }
    }
}
