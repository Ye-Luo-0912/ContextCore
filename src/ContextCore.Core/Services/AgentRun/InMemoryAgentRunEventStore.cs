using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 F2：InMemoryAgentRunEventStore — 进程内 Agent Run Event Store（开发/测试用）
//
// 实现 IAgentRunEventStore 的进程内默认实现，复用 Checkpoint 哈希链模式：
//   - ConcurrentDictionary 维护 (workspaceId, runId) → List<AgentRunEvent> 映射；
//   - AppendAsync 校验 sequence 连续性 + prev_chain_hash 匹配；
//   - ReadAsync 按 sequence 升序读取（fromSequence + take）；
//   - GetLastSequenceAsync 返回当前最大 sequence（空时返回 -1 表示无事件）。
//
// 设计决策：
//   - 不持久化到磁盘：进程崩溃后事件流丢失。生产部署应注入持久化实现。
//   - 并发追加通过锁保护（保证 sequence 连续性校验原子性）。
//   - 哈希链校验在 AppendAsync 入口执行，不匹配抛 InvalidOperationException。
// ===========================================================================

/// <summary>
/// 任务 F2：进程内 Agent Run Event Store 默认实现（开发/测试用）。
/// 维护 Run 事件流的进程内映射，支持 sequence 连续性 + 哈希链校验。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后事件流丢失。
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresAgentRunEventStore</c>）。
///
/// G4：<see cref="AppendBatchAsync"/> 支持批量事件 + 可选 Run 状态 CAS + 可选 checkpoint 游标。
/// 若构造时注入了 <see cref="IAgentRunStore"/>，则状态 CAS + 字段更新委托给它（非原子，
/// 仅供开发/测试；生产路径走 Postgres 单事务）。
/// </remarks>
public sealed class InMemoryAgentRunEventStore : IAgentRunEventStore
{
    private readonly ConcurrentDictionary<string, List<AgentRunEvent>> _events = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);
    private readonly IAgentRunStore? _runStore;

    /// <summary>初始化无 Run Store 委托的实例（AppendBatchAsync 的 runStateUpdate 被忽略）。</summary>
    public InMemoryAgentRunEventStore()
    {
        _runStore = null;
    }

    /// <summary>
    /// 初始化并注入 <see cref="IAgentRunStore"/>，使 <see cref="AppendBatchAsync"/> 能委托状态 CAS + 字段更新。
    /// </summary>
    /// <param name="runStore">Run 元数据存储（用于 AppendBatchAsync 的 runStateUpdate 委托）。</param>
    public InMemoryAgentRunEventStore(IAgentRunStore runStore)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(
        AgentRunEvent @event,
        CancellationToken cancellationToken = default,
        string? leaseToken = null,
        long? fencingToken = null)
    {
        ArgumentNullException.ThrowIfNull(@event);
        // P0-4：InMemory 实现不维护 lease 注册表，leaseToken/fencingToken 仅用于接口对齐；
        // 实际 fencing 校验由 Postgres 实现完成。
        _ = leaseToken;
        _ = fencingToken;

        var key = Key(@event.WorkspaceId, @event.RunId);
        var gate = _locks.GetOrAdd(key, _ => new object());

        lock (gate)
        {
            var list = _events.GetOrAdd(key, _ => new List<AgentRunEvent>());

            // 1. Sequence 连续性校验
            var expectedSequence = list.Count;
            if (@event.Sequence != expectedSequence)
            {
                throw new InvalidOperationException(
                    $"事件 Sequence 不连续：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                    $"期望={expectedSequence}，实际={@event.Sequence}。" +
                    $"事件流必须从 0 开始单调递增。");
            }

            // 2. PrevChainHash 链接校验（链头为 null；其余指向前一事件 ContentHash）
            string? expectedPrevHash = list.Count == 0
                ? null
                : list[^1].ContentHash;

            if (!string.Equals(expectedPrevHash, @event.PrevChainHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"事件 PrevChainHash 不匹配：workspace_id={@event.WorkspaceId}, run_id={@event.RunId}。" +
                    $"期望={expectedPrevHash ?? "<null>"}，实际={@event.PrevChainHash ?? "<null>"}。" +
                    $"事件哈希链被破坏或乱序。");
            }

            list.Add(@event);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask AppendBatchAsync(
        IReadOnlyList<AgentRunEvent> events,
        AgentRunStateUpdate? runStateUpdate,
        AgentCheckpointCursor? checkpointCursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        // 空批 + 无状态更新 → 直接返回
        if (events.Count == 0 && runStateUpdate is null)
        {
            return;
        }

        // 有事件时校验并追加
        if (events.Count > 0)
        {
            var first = events[0];
            var key = Key(first.WorkspaceId, first.RunId);
            var gate = _locks.GetOrAdd(key, _ => new object());

            lock (gate)
            {
                var list = _events.GetOrAdd(key, _ => new List<AgentRunEvent>());

                // 1. 校验首事件 Sequence 连续性（必须 = 当前 MAX + 1）
                var expectedSequence = list.Count;
                if (events[0].Sequence != expectedSequence)
                {
                    throw new InvalidOperationException(
                        $"批量事件首事件 Sequence 不连续：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                        $"期望={expectedSequence}，实际={events[0].Sequence}。" +
                        $"事件流必须从 0 开始单调递增。");
                }

                // 2. 校验首事件 PrevChainHash 链接（链头为 null；其余指向前一事件 ContentHash）
                string? expectedPrevHash = list.Count == 0
                    ? null
                    : list[^1].ContentHash;

                if (!string.Equals(expectedPrevHash, events[0].PrevChainHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"批量事件首事件 PrevChainHash 不匹配：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                        $"期望={expectedPrevHash ?? "<null>"}，实际={events[0].PrevChainHash ?? "<null>"}。" +
                        $"事件哈希链被破坏或乱序。");
                }

                // 3. 校验批量内 Sequence 连续性 + PrevChainHash 链接
                for (var i = 1; i < events.Count; i++)
                {
                    if (events[i].Sequence != events[i - 1].Sequence + 1)
                    {
                        throw new InvalidOperationException(
                            $"批量事件内 Sequence 不连续：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                            $"位置 {i}：期望={events[i - 1].Sequence + 1}，实际={events[i].Sequence}。");
                    }

                    if (!string.Equals(events[i - 1].ContentHash, events[i].PrevChainHash, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"批量事件内 PrevChainHash 链接断裂：workspace_id={first.WorkspaceId}, run_id={first.RunId}。" +
                            $"位置 {i}：期望={events[i - 1].ContentHash ?? "<null>"}，实际={events[i].PrevChainHash ?? "<null>"}。");
                    }
                }

                // 4. 原子追加所有事件
                list.AddRange(events);
            }
        }

        // 5. 委托 Run 状态 CAS + 字段更新到 IAgentRunStore（若注入）
        //    注意：InMemory 路径下事件追加与状态更新非原子（无共享事务）；仅供开发/测试。
        //    P0-4：透传 leaseToken/fencingToken（InMemory store 接受但不强制校验）。
        if (runStateUpdate is not null && _runStore is not null)
        {
            await _runStore.TransitionStateAsync(
                runStateUpdate.WorkspaceId,
                runStateUpdate.RunId,
                runStateUpdate.ExpectedCurrentState,
                runStateUpdate.NewState,
                cancellationToken,
                runStateUpdate.LeaseToken,
                runStateUpdate.FencingToken).ConfigureAwait(false);

            await _runStore.UpdateAsync(runStateUpdate.RunSnapshot, cancellationToken).ConfigureAwait(false);
        }

        // 6. checkpointCursor：InMemory 实现忽略（无 agent_runs 表的 last_checkpoint_id 列）
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentRunEvent>> ReadAsync(
        string workspaceId,
        string runId,
        int fromSequence = 0,
        int take = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (fromSequence < 0)
        {
            fromSequence = 0;
        }

        if (take <= 0)
        {
            take = 1000;
        }

        var key = Key(workspaceId, runId);
        if (!_events.TryGetValue(key, out var list))
        {
            return ValueTask.FromResult<IReadOnlyList<AgentRunEvent>>(Array.Empty<AgentRunEvent>());
        }

        // 快照读取（避免并发修改）
        lock (_locks.GetOrAdd(key, _ => new object()))
        {
            var results = list
                .Where(e => e.Sequence >= fromSequence)
                .OrderBy(e => e.Sequence)
                .Take(take)
                .ToList();
            return ValueTask.FromResult<IReadOnlyList<AgentRunEvent>>(results);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> GetLastSequenceAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var key = Key(workspaceId, runId);
        if (!_events.TryGetValue(key, out var list) || list.Count == 0)
        {
            // 无事件时返回 -1（表示无事件，与 0 区分；0 表示已有 1 个事件）
            return ValueTask.FromResult(-1);
        }

        lock (_locks.GetOrAdd(key, _ => new object()))
        {
            return ValueTask.FromResult(list.Count - 1);
        }
    }

    private static string Key(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";
}
