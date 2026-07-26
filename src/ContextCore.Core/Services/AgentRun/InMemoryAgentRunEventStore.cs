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
/// </remarks>
public sealed class InMemoryAgentRunEventStore : IAgentRunEventStore
{
    private readonly ConcurrentDictionary<string, List<AgentRunEvent>> _events = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask AppendAsync(AgentRunEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

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
