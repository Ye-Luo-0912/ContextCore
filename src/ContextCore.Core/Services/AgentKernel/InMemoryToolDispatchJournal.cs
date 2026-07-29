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
//   - P0-3 CAS-1：状态推进使用精确前驱状态匹配（state == expected），
//     禁止跨级跳跃（如 Prepared → Committed）；违反时抛 InvalidOperationException（InvalidTransition）。
//     当前已到达/超过目标状态时幂等成功（AlreadyApplied/AlreadyAdvanced，不报错）。
//   - P0-3 CAS-2：PrepareAsync 对已存在的 request_id 验证语义等价
//     （ToolName / IdempotencyKey / PayloadDigest / WorkspaceId / RunId），
//     不等价时抛 InvalidOperationException（RequestIdReuseDetected）。
//   - P0-3：缺失前驱记录时抛 InvalidOperationException（不再 auto-create stub），
//     与 PostgresToolDispatchJournal 的精确状态 CAS 语义一致，保证审计链完整。
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
    // P0-3：结果缓存（MarkCommittedWithResultAsync 写入，PrepareAsync 读取）
    private readonly ConcurrentDictionary<string, DurableToolResult> _results = new(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>InMemory 实现仅在进程内缓存结果，不在同事务内持久化到外部存储，返回 false。</remarks>
    public bool PersistsResults => false;

    /// <inheritdoc />
    public ValueTask<ToolDispatchPrepareResult> PrepareAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != ToolDispatchState.Prepared)
        {
            throw new ArgumentException(
                $"PrepareAsync 入口的 State 必须为 Prepared，实际为 {entry.State}。", nameof(entry));
        }

        // AddOrUpdate 原子语义：
        //   - key 不存在 → 写入新条目（add factory）。
        //   - key 已存在 → 验证语义等价后保留既有状态（update factory，不覆盖已推进的状态）。
        _entries.AddOrUpdate(
            entry.RequestId,
            _ => entry,
            (_, existing) =>
            {
                ValidateSemanticEquivalence(entry.RequestId, existing, entry);
                return existing; // 幂等：重复 Prepare 不覆盖已推进的状态
            });

        // P0-3：根据当前 journal 状态构建 Prepare 结果
        _entries.TryGetValue(entry.RequestId, out var current);
        var currentState = current?.State ?? entry.State;
        DurableToolResult? cachedResult = null;
        if (currentState >= ToolDispatchState.Committed)
        {
            _results.TryGetValue(entry.RequestId, out cachedResult);
        }

        return new ValueTask<ToolDispatchPrepareResult>(new ToolDispatchPrepareResult
        {
            CurrentState = currentState,
            ShouldDispatch = currentState == ToolDispatchState.Prepared,
            NeedsReconciliation = currentState == ToolDispatchState.Dispatched,
            ExternalOperationId = current?.ExternalOperationId,
            CachedResult = cachedResult
        });
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
            // P0-3：缺失 Prepared 前驱 → 抛异常（MissingPredecessor，不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Dispatched（期望前驱=Prepared）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, ToolDispatchState.Prepared, ToolDispatchState.Dispatched, externalOperationId, now));

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
            // P0-3：缺失 Dispatched 前驱 → 抛异常（MissingPredecessor，不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Committed（期望前驱=Dispatched）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, ToolDispatchState.Dispatched, ToolDispatchState.Committed, null, now));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkCommittedWithResultAsync(string requestId, DurableToolResult result, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }
        ArgumentNullException.ThrowIfNull(result);

        // 推进状态机到 Committed（复用 MarkCommittedAsync 的 CAS 逻辑）
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Committed（期望前驱=Dispatched）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, ToolDispatchState.Dispatched, ToolDispatchState.Committed, null, now));

        // 同事务语义：原子写入结果缓存（InMemory 用 ConcurrentDictionary 模拟）
        _results[requestId] = result;

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
            // P0-3：缺失 Committed 前驱 → 抛异常（MissingPredecessor，不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=ResultDelivered（期望前驱=Committed）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, ToolDispatchState.Committed, ToolDispatchState.ResultDelivered, null, now));

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

    /// <summary>
    /// P0-3 CAS-1：应用精确前驱状态转换。
    /// <list type="bullet">
    ///   <item>current == expected → 正常前向推进（Applied），返回更新后的 entry。</item>
    ///   <item>current == target → 幂等成功（AlreadyApplied），返回既有 entry 不修改。</item>
    ///   <item>current &gt; target → 幂等成功（AlreadyAdvanced），返回既有 entry 不修改。</item>
    ///   <item>current &lt; expected → 抛 <see cref="InvalidOperationException"/>（InvalidTransition，禁止跨级跳跃）。</item>
    /// </list>
    /// </summary>
    private static ToolDispatchJournalEntry ApplyTransition(
        string requestId,
        ToolDispatchJournalEntry existing,
        ToolDispatchState expectedState,
        ToolDispatchState targetState,
        string? externalOperationId,
        DateTimeOffset now)
    {
        // current == expected → 正常前向推进（Applied）
        if (existing.State == expectedState)
        {
            return existing with
            {
                State = targetState,
                ExternalOperationId = targetState == ToolDispatchState.Dispatched
                    ? (externalOperationId ?? existing.ExternalOperationId)
                    : existing.ExternalOperationId,
                UpdatedAt = now
            };
        }

        // current == target → AlreadyApplied（幂等，不修改）
        if (existing.State == targetState)
        {
            return existing;
        }

        // current > target → AlreadyAdvanced（幂等，不修改）
        if ((int)existing.State > (int)targetState)
        {
            return existing;
        }

        // current < expected → InvalidTransition（跨级跳跃，禁止）
        throw new InvalidOperationException(
            $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
            $"当前={existing.State}，期望前驱={expectedState}，目标={targetState}。" +
            $"状态机只能逐级向前推进：Prepared → Dispatched → Committed → ResultDelivered，" +
            $"不允许跳过中间状态（如 Prepared → Committed）。");
    }

    /// <summary>
    /// P0-3 CAS-2：验证 PrepareAsync 语义等价。
    /// 比较既有条目与新条目的 ToolName / IdempotencyKey / PayloadDigest / WorkspaceId / RunId，
    /// 任一不等价时抛 <see cref="InvalidOperationException"/>（RequestIdReuseDetected）。
    /// </summary>
    private static void ValidateSemanticEquivalence(
        string requestId,
        ToolDispatchJournalEntry existing,
        ToolDispatchJournalEntry incoming)
    {
        var mismatches = new List<string>(5);
        if (!string.Equals(existing.ToolName, incoming.ToolName, StringComparison.Ordinal))
        {
            mismatches.Add($"ToolName（既有={existing.ToolName}，新={incoming.ToolName}）");
        }
        if (!string.Equals(existing.IdempotencyKey, incoming.IdempotencyKey, StringComparison.Ordinal))
        {
            mismatches.Add($"IdempotencyKey（既有={existing.IdempotencyKey ?? "<null>"}，新={incoming.IdempotencyKey ?? "<null>"}）");
        }
        if (!string.Equals(existing.PayloadDigest, incoming.PayloadDigest, StringComparison.Ordinal))
        {
            mismatches.Add($"PayloadDigest（既有={existing.PayloadDigest ?? "<null>"}，新={incoming.PayloadDigest ?? "<null>"}）");
        }
        if (!string.Equals(existing.WorkspaceId, incoming.WorkspaceId, StringComparison.Ordinal))
        {
            mismatches.Add($"WorkspaceId（既有={existing.WorkspaceId ?? "<null>"}，新={incoming.WorkspaceId ?? "<null>"}）");
        }
        if (!string.Equals(existing.RunId, incoming.RunId, StringComparison.Ordinal))
        {
            mismatches.Add($"RunId（既有={existing.RunId ?? "<null>"}，新={incoming.RunId ?? "<null>"}）");
        }

        if (mismatches.Count > 0)
        {
            throw new InvalidOperationException(
                $"RequestIdReuseDetected：request_id={requestId} 已存在但语义字段不等价——" +
                $"同一 RequestId 不能复用为另一项操作。差异：{string.Join("；", mismatches)}。");
        }
    }
}
