using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 F1：InMemoryAgentRunStore — 进程内 Agent Run Store（开发/测试用）
//
// 实现 IAgentRunStore 的进程内默认实现，与 InMemoryAgentCheckpointStore 模式对齐：
//   - ConcurrentDictionary 维护 (workspaceId, runId) → AgentRun 映射；
//   - CreateAsync 幂等（同主键 TryAdd 不覆盖）；
//   - TransitionStateAsync 使用 expected-state CAS（CAS 失败抛 InvalidOperationException）；
//   - UpdateAsync 直接覆盖（保留 State 不变以避免 CAS 旁路）；
//   - ListBySessionAsync / ListByStateAsync 线性扫描过滤。
//
// 设计决策：
//   - 不持久化到磁盘：进程崩溃后状态丢失。生产部署应注入持久化实现。
//   - 线程安全：所有读写通过 ConcurrentDictionary 原子操作。
//   - CAS 实现：使用 ConcurrentDictionary 索引器 + Interlocked.CompareExchange 模拟。
// ===========================================================================

/// <summary>
/// 任务 F1：进程内 Agent Run Store 默认实现（开发/测试用）。
/// 维护 Run 元数据的进程内映射，支持 expected-state CAS 推进。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后状态丢失。
/// 生产部署应注入基于 DB/WAL 的持久化实现（如 <c>PostgresAgentRunStore</c>）。
/// </remarks>
public sealed class InMemoryAgentRunStore : IAgentRunStore
{
    private readonly ConcurrentDictionary<string, AgentRun> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var key = Key(run.WorkspaceId, run.RunId);
        // 幂等：同主键 TryAdd 不覆盖（与 Postgres ON CONFLICT DO NOTHING 一致）
        _runs.TryAdd(key, run);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runs.TryGetValue(Key(workspaceId, runId), out var run);
        return ValueTask.FromResult(run);
    }

    /// <inheritdoc />
    public ValueTask TransitionStateAsync(
        string workspaceId,
        string runId,
        AgentRunState expectedCurrentState,
        AgentRunState newState,
        CancellationToken cancellationToken = default,
        string? leaseToken = null,
        long? fencingToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        // P0-4：InMemory 实现不维护 lease 注册表（dev/test 场景下 lease 与 store 通常不共享实例），
        // 故 leaseToken/fencingToken 参数仅用于接口对齐，实际校验由 Postgres 实现完成。
        // 若需在测试中验证 fencing 行为，应直接使用 PostgresAgentRunStore + PostgresAgentRunLease。
        _ = leaseToken;
        _ = fencingToken;

        var key = Key(workspaceId, runId);
        // 循环 CAS：匹配 expectedCurrentState 时替换为 newState
        while (true)
        {
            if (!_runs.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"Agent Run 不存在：workspace_id={workspaceId}, run_id={runId}。" +
                    $"无法推进状态机（缺失 Run 元数据）。");
            }

            if (existing.State != expectedCurrentState)
            {
                throw new InvalidOperationException(
                    $"Agent Run 状态机 CAS 失败：workspace_id={workspaceId}, run_id={runId}。" +
                    $"期望当前状态={expectedCurrentState}，实际={existing.State}。" +
                    $"状态已被其他实例推进或不可逆退。");
            }

            var updated = existing with
            {
                State = newState,
                UpdatedAt = DateTimeOffset.UtcNow,
                FinishedAt = (newState == AgentRunState.Completed
                              || newState == AgentRunState.Failed
                              || newState == AgentRunState.Cancelled)
                    ? DateTimeOffset.UtcNow
                    : existing.FinishedAt
            };

            if (_runs.TryUpdate(key, updated, existing))
            {
                return ValueTask.CompletedTask;
            }

            // CAS 失败 = 被并发修改 → 重试
        }
    }

    /// <inheritdoc />
    public ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var key = Key(run.WorkspaceId, run.RunId);

        while (_runs.TryGetValue(key, out var existing))
        {
            // 保留存储中的 State（避免 UpdateAsync 旁路 CAS）
            var updated = run with { State = existing.State };
            if (_runs.TryUpdate(key, updated, existing))
            {
                return ValueTask.CompletedTask;
            }
        }

        // Run 不存在时静默忽略（与 Postgres UPDATE 0 行语义一致；不抛异常避免掩盖业务错误）
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var results = _runs.Values
            .Where(r => r.WorkspaceId == workspaceId && r.SessionId == sessionId)
            .OrderBy(r => r.CreatedAt)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<AgentRun>>(results);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
        AgentRunState state,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take < 0)
        {
            take = 100;
        }

        var results = _runs.Values
            .Where(r => r.State == state)
            .OrderBy(r => r.CreatedAt)
            .Take(take)
            .ToList();
        return ValueTask.FromResult<IReadOnlyList<AgentRun>>(results);
    }

    private static string Key(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";
}
