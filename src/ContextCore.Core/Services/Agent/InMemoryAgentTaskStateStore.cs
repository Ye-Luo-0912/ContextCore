using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// R24-2：InMemoryAgentTaskStateStore — Agent 任务状态内存存储实现。
//
// 实现 IAgentTaskStateStore：
//   - ConcurrentDictionary<string, AgentTaskState> 后端
//   - SaveAsync 幂等（同 TaskId 覆盖）
//   - GetAsync 不存在返回 null
//   - ListBySessionAsync 按 SessionId 过滤 + UpdatedAt 倒序
//   - DeleteAsync 存在/不存在
// ===========================================================================

/// <summary>
/// R24-2：<see cref="IAgentTaskStateStore"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 适用于测试 / 演示 / 单机开发场景。生产场景需替换为持久化实现（如 PostgresAgentTaskStateStore）。
/// </remarks>
public sealed class InMemoryAgentTaskStateStore : IAgentTaskStateStore
{
    private readonly ConcurrentDictionary<string, AgentTaskState> _tasks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(AgentTaskState taskState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskState);
        cancellationToken.ThrowIfCancellationRequested();
        _tasks[taskState.TaskId] = taskState;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentTaskState?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentTaskState>> ListBySessionAsync(
        AgentSessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        cancellationToken.ThrowIfCancellationRequested();

        var list = _tasks.Values
            .Where(t => string.Equals(t.Session.Value, sessionId.Value, StringComparison.Ordinal))
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.TaskId)
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentTaskState>>(list);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string taskId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_tasks.TryRemove(taskId, out _));
    }

    /// <summary>当前任务总数（测试与诊断用）。</summary>
    public int Count => _tasks.Count;
}
