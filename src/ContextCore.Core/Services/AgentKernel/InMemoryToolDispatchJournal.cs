using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentKernel;

// ===========================================================================
// InMemoryToolDispatchJournal
//
// 进程内 Tool Dispatch Journal 默认实现。
// 维护每个 RequestId 的状态机进度（Prepared → Dispatched → Committed → ResultDelivered）。
//
// 设计决策：
//   - 使用 ConcurrentDictionary 支持多线程并发访问。
//   - 状态推进使用精确前驱状态匹配（state == expected），
//     禁止跨级跳跃（如 Prepared → Committed）；违反时抛 InvalidOperationException（InvalidTransition）。
//     当前已到达/超过目标状态时幂等成功（AlreadyApplied/AlreadyAdvanced，不报错）。
//   - PrepareAsync 对已存在的 request_id 验证语义等价
//     （ToolName / IdempotencyKey / PayloadDigest / WorkspaceId / RunId），
//     不等价时抛 InvalidOperationException（RequestIdReuseDetected）。
//   - 缺失前驱记录时抛 InvalidOperationException（不再 auto-create stub），
//     与 PostgresToolDispatchJournal 的精确状态 CAS 语义一致，保证审计链完整。
//   - 进程内实现仅用于测试/单机部署；生产部署应替换为持久化实现（DB/WAL）。
//   - 不持久化到磁盘：进程崩溃后状态丢失。生产部署需注入持久化实现。
// ===========================================================================

/// <summary>
/// 进程内 Tool Dispatch Journal 默认实现。
/// 维护 tool 调用状态机进度以支持 exactly-once 语义。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后 journal 状态丢失。
/// 生产部署应注入基于 DB/WAL 的持久化实现以保证崩溃恢复的 exactly-once。
///
/// Mark* 方法在缺失前驱记录时抛 <see cref="InvalidOperationException"/>，
/// 不再 auto-create stub 条目——保证审计链完整，与 PostgresToolDispatchJournal 语义一致。
/// </remarks>
public sealed class InMemoryToolDispatchJournal : IToolDispatchJournal
{
    private readonly ConcurrentDictionary<string, ToolDispatchJournalEntry> _entries = new(StringComparer.Ordinal);
    // 结果缓存（MarkCommittedWithResultAsync 写入，PrepareAsync 读取）
    private readonly ConcurrentDictionary<string, DurableToolResult> _results = new(StringComparer.Ordinal);

    /// <summary>
    /// 逻辑状态顺序映射。DispatchingIntent=4 的数值大于 Dispatched=1，
    /// 破坏了基于数值大小的状态顺序判断，因此使用此字典按逻辑顺序判断前向推进。
    /// </summary>
    private static readonly Dictionary<ToolDispatchState, int> s_logicalOrder = new()
    {
        { ToolDispatchState.Prepared, 0 },
        { ToolDispatchState.DispatchingIntent, 1 },
        { ToolDispatchState.Dispatched, 2 },
        { ToolDispatchState.Reconciling, 3 },
        { ToolDispatchState.Committed, 4 },
        { ToolDispatchState.ResultDelivered, 5 }
    };

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

        // 根据当前 journal 状态构建 Prepare 结果
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
            NeedsReconciliation = currentState == ToolDispatchState.Dispatched || currentState == ToolDispatchState.DispatchingIntent,
            ExternalOperationId = current?.ExternalOperationId,
            CachedResult = cachedResult
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Prepare + 前置 Intent 合并为单次原子写——新条目直接以 DispatchingIntent 落库，
    /// 既有 Prepared 前驱（旧两步流程崩溃残留）原子推进到 DispatchingIntent。
    /// 返回 ShouldDispatch=true 时 journal 必已处于 DispatchingIntent，调用方可直接 Dispatch。
    /// </remarks>
    public ValueTask<ToolDispatchPrepareResult> PrepareWithIntentAsync(ToolDispatchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != ToolDispatchState.Prepared && entry.State != ToolDispatchState.DispatchingIntent)
        {
            throw new ArgumentException(
                $"PrepareWithIntentAsync 入口的 State 必须为 Prepared 或 DispatchingIntent，实际为 {entry.State}。", nameof(entry));
        }

        var now = DateTimeOffset.UtcNow;
        var inserted = false;
        var advancedFromPrepared = false;
        // AddOrUpdate 原子语义：新插入 → DispatchingIntent；已存在 → 语义等价校验 + Prepared 前驱推进。
        _entries.AddOrUpdate(
            entry.RequestId,
            _ =>
            {
                inserted = true;
                return entry with { State = ToolDispatchState.DispatchingIntent, UpdatedAt = now };
            },
            (_, existing) =>
            {
                ValidateSemanticEquivalence(entry.RequestId, existing, entry);
                if (existing.State == ToolDispatchState.Prepared)
                {
                    advancedFromPrepared = true;
                    // 推进时补写 external_operation_id（框架在 Prepare 时生成，
                    // 旧两步流程的 Prepared 残留可能无该值；COALESCE 语义=已有值优先）。
                    return existing with
                    {
                        State = ToolDispatchState.DispatchingIntent,
                        UpdatedAt = now,
                        ExternalOperationId = existing.ExternalOperationId ?? entry.ExternalOperationId
                    };
                }
                return existing; // 幂等：不覆盖已推进的状态
            });

        _entries.TryGetValue(entry.RequestId, out var current);
        var currentState = current?.State ?? ToolDispatchState.DispatchingIntent;
        DurableToolResult? cachedResult = null;
        if (currentState >= ToolDispatchState.Committed)
        {
            _results.TryGetValue(entry.RequestId, out cachedResult);
        }

        return new ValueTask<ToolDispatchPrepareResult>(new ToolDispatchPrepareResult
        {
            CurrentState = currentState,
            // 本次新插入或既有 Prepared 已推进 → 外部调用尚未开始，可安全 Dispatch
            ShouldDispatch = inserted || advancedFromPrepared,
            // 既有 DispatchingIntent/Dispatched（崩溃残留/并发分派）→ 外部调用可能已开始，需对账
            NeedsReconciliation = !inserted && !advancedFromPrepared
                                  && (currentState == ToolDispatchState.DispatchingIntent || currentState == ToolDispatchState.Dispatched),
            ExternalOperationId = current?.ExternalOperationId,
            CachedResult = cachedResult
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// 在外部 Tool 调用发起前持久化 DispatchingIntent 状态，创建 durable 边界。
    /// 与 MarkDispatchedAsync 不同，本方法在状态已超过 DispatchingIntent 时抛异常（而非幂等成功），
    /// 因为继续 Dispatch 会导致外部副作用重复执行。
    /// </remarks>
    public ValueTask MarkDispatchingIntentAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=DispatchingIntent（期望前驱=Prepared）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) =>
            {
                if (existing.State == ToolDispatchState.DispatchingIntent)
                {
                    return existing;
                }

                if (existing.State == ToolDispatchState.Prepared)
                {
                    return existing with
                    {
                        State = ToolDispatchState.DispatchingIntent,
                        UpdatedAt = now
                    };
                }

                if (s_logicalOrder[existing.State] > s_logicalOrder[ToolDispatchState.DispatchingIntent])
                {
                    throw new InvalidOperationException(
                        $"Tool dispatch state 已超过 DispatchingIntent（AlreadyAdvanced）：request_id={requestId}，" +
                        $"当前={existing.State}，目标=DispatchingIntent。" +
                        $"状态已被并发推进，外部调用可能已开始，禁止重复 Dispatch。");
                }

                throw new InvalidOperationException(
                    $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
                    $"当前={existing.State}，期望前驱=Prepared，目标=DispatchingIntent。" +
                    $"状态机只能逐级向前推进：Prepared → DispatchingIntent → Dispatched → Reconciling → Committed → ResultDelivered。");
            });

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
            // 缺失 Prepared 前驱 → 抛异常（MissingPredecessor，不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Dispatched（期望前驱=Prepared）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, new[] { ToolDispatchState.Prepared, ToolDispatchState.DispatchingIntent }, ToolDispatchState.Dispatched, externalOperationId, now));

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
            // 缺失 Dispatched 前驱 → 抛异常（MissingPredecessor，不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Committed（期望前驱=Dispatched）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, new[] { ToolDispatchState.Dispatched }, ToolDispatchState.Committed, null, now));

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
                requestId, existing, new[] { ToolDispatchState.Dispatched }, ToolDispatchState.Committed, null, now));

        // 同事务语义：原子写入结果缓存（InMemory 用 ConcurrentDictionary 模拟）
        _results[requestId] = result;

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 将模糊态（DispatchingIntent/Dispatched）显式推进到 Reconciling。
    /// 已 Reconciling/已提交（>Reconciling）幂等成功；Prepared（外部调用从未开始）抛
    /// InvalidTransition——它应被重新 Dispatch 而非对账。
    /// </remarks>
    public ValueTask BeginReconciliationAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }

        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Reconciling（期望前驱=DispatchingIntent/Dispatched）。" +
                    $"必须先调用 PrepareAsync 写入条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId,
                existing,
                new[] { ToolDispatchState.DispatchingIntent, ToolDispatchState.Dispatched },
                ToolDispatchState.Reconciling,
                null,
                now));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask MarkReconciledWithResultAsync(string requestId, DurableToolResult result, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        }
        ArgumentNullException.ThrowIfNull(result);

        // 推进状态机到 Committed（期望前驱=Reconciling）
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            requestId,
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=Committed（期望前驱=Reconciling）。" +
                    $"必须先经 BeginReconciliationAsync 进入对账状态，再提交对账结果。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, new[] { ToolDispatchState.Reconciling }, ToolDispatchState.Committed, null, now));

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
            // 缺失 Committed 前驱 → 抛异常（MissingPredecessor，不再 auto-create stub）
            _ =>
            {
                throw new InvalidOperationException(
                    $"Tool dispatch journal 缺失前驱记录（MissingPredecessor）：request_id={requestId}，" +
                    $"目标状态=ResultDelivered（期望前驱=Committed）。" +
                    $"必须先调用 PrepareAsync 写入 Prepared 条目，再推进状态机。");
            },
            (_, existing) => ApplyTransition(
                requestId, existing, new[] { ToolDispatchState.Committed }, ToolDispatchState.ResultDelivered, null, now));

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
    /// 应用精确前驱状态转换。
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
        IReadOnlyList<ToolDispatchState> expectedStates,
        ToolDispatchState targetState,
        string? externalOperationId,
        DateTimeOffset now)
    {
        // current == any expected → 正常前向推进（Applied）
        if (expectedStates.Contains(existing.State))
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

        // current > target (logical) → AlreadyAdvanced（幂等，不修改）
        if (s_logicalOrder[existing.State] > s_logicalOrder[targetState])
        {
            return existing;
        }

        // current < expected → InvalidTransition（跨级跳跃，禁止）
        throw new InvalidOperationException(
            $"Tool dispatch state 跨级跳跃（InvalidTransition）：request_id={requestId}，" +
            $"当前={existing.State}，期望前驱={string.Join("/", expectedStates)}，目标={targetState}。" +
            $"状态机只能逐级向前推进：Prepared → DispatchingIntent → Dispatched → Reconciling → Committed → ResultDelivered，" +
            $"不允许跳过中间状态（如 Prepared → Committed）。");
    }

    /// <summary>
    /// 验证 PrepareAsync 语义等价。
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
