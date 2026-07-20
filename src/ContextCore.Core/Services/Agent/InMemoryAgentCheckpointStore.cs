using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Agent;

// ===========================================================================
// R23-3：InMemoryAgentCheckpointStore — Agent checkpoint 内存存储实现。
//
// 目标（对齐 R23 规格）：
//   1. 实现 IAgentCheckpointStore 的 4 个方法（Save / Get / List / Delete）。
//   2. 仅 in-memory；进程重启后丢失；生产实现应替换为基于 Postgres 的 store。
//   3. 线程安全：ConcurrentDictionary + 按 session 维度分组查询。
// ===========================================================================

/// <summary>
/// R23-3：<see cref="IAgentCheckpointStore"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 适用于测试 / 演示 / 单机开发场景。生产场景需替换为持久化实现。
/// </remarks>
public sealed class InMemoryAgentCheckpointStore : IAgentCheckpointStore
{
    private readonly ConcurrentDictionary<string, AgentCheckpoint> _checkpoints
        = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveAsync(AgentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints[checkpoint.CheckpointId] = checkpoint;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentCheckpoint?> GetAsync(string checkpointId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        cancellationToken.ThrowIfCancellationRequested();
        _checkpoints.TryGetValue(checkpointId, out var cp);
        return Task.FromResult(cp);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentCheckpoint>> ListAsync(
        AgentSessionId sessionId,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), take, "take must be >= 0");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var list = _checkpoints.Values
            .Where(c => string.Equals(c.Session.Value, sessionId.Value, StringComparison.Ordinal))
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.CheckpointId)
            .Take(take == 0 ? int.MaxValue : take)
            .ToList();
        return Task.FromResult<IReadOnlyList<AgentCheckpoint>>(list);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string checkpointId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_checkpoints.TryRemove(checkpointId, out _));
    }

    /// <summary>当前 checkpoint 总数（测试与诊断用）。</summary>
    public int Count => _checkpoints.Count;
}
