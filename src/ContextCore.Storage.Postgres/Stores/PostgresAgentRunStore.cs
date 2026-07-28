using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 任务 F3：PostgreSQL 持久化 Agent Run Store。
/// 替代 <see cref="ContextCore.Core.Services.AgentRunRuntime.InMemoryAgentRunStore"/>，
/// 让 HA 场景下 Agent Run 元数据可跨进程持久化与崩溃恢复。
/// </summary>
/// <remarks>
/// 设计要点（参考 <see cref="PostgresAgentCheckpointStore"/>）：
///   1. 表 <c>agent_runs</c> 反规范化 workspace_id / run_id / session_id / state / turn 字段以便索引查询；
///      完整 <see cref="AgentRun"/> 对象保存在 <c>data jsonb</c>，由 store 反序列化。
///   2. 主键 (workspace_id, run_id)：跨 workspace 隔离 + 同 workspace 内 run_id 唯一。
///   3. <see cref="CreateAsync"/> 使用 <c>INSERT ... ON CONFLICT DO NOTHING</c> 保证幂等。
///   4. <see cref="TransitionStateAsync"/> 使用 expected-state CAS：
///      <c>UPDATE ... SET state=@new WHERE workspace_id AND run_id AND state=@expected</c>；
///      0 行受影响时抛 <see cref="InvalidOperationException"/>（状态已被其他实例推进或逆退）。
///   5. <see cref="UpdateAsync"/> 更新可变字段（turn / final_answer / failure_reason / 预算 JSON）。
///   6. <c>turn_budget_json</c> / <c>cost_budget_json</c> 存预算 JSON 列（与 checkpoint state_json 模式一致）。
/// </remarks>
public sealed class PostgresAgentRunStore : PostgresStoreBase, IAgentRunStore, IPersistentAgentRunStore
{
    /// <summary>初始化 Postgres 持久化 Agent Run Store。</summary>
    public PostgresAgentRunStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("agent_runs")} (
    workspace_id, run_id, session_id, task, state, turn,
    created_at, updated_at, finished_at, failure_reason, final_answer,
    turn_budget_json, cost_budget_json, data)
VALUES (
    @workspace_id, @run_id, @session_id, @task, @state, @turn,
    @created_at, @updated_at, @finished_at, @failure_reason, @final_answer,
    @turn_budget_json, @cost_budget_json, @data)
ON CONFLICT (workspace_id, run_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("workspace_id", run.WorkspaceId);
        command.Parameters.AddWithValue("run_id", run.RunId);
        command.Parameters.AddWithValue("session_id", run.SessionId);
        command.Parameters.AddWithValue("task", run.Task ?? string.Empty);
        command.Parameters.AddWithValue("state", (byte)run.State);
        command.Parameters.AddWithValue("turn", run.Turn);
        command.Parameters.AddWithValue("created_at", run.CreatedAt);
        command.Parameters.AddWithValue("updated_at", run.UpdatedAt);
        command.Parameters.AddWithValue("finished_at", (object?)run.FinishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("failure_reason", (object?)run.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("final_answer", (object?)run.FinalAnswer ?? DBNull.Value);
        command.Parameters.AddWithValue("turn_budget_json", run.TurnBudget is null
            ? DBNull.Value
            : JsonSerializer.Serialize(run.TurnBudget));
        command.Parameters.AddWithValue("cost_budget_json", run.CostBudget is null
            ? DBNull.Value
            : JsonSerializer.Serialize(run.CostBudget));
        AddJson(command, "data", run);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        return await ExecuteScalarJsonAsync<AgentRun>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask TransitionStateAsync(
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
        // P0-4：lease 校验要求 leaseToken + fencingToken 同时提供（或同时为 null）
        var leaseValidated = leaseToken is not null && fencingToken is not null;

        var now = DateTimeOffset.UtcNow;
        var isTerminal = newState == AgentRunState.Completed
                         || newState == AgentRunState.Failed
                         || newState == AgentRunState.Cancelled;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. expected-state CAS：UPDATE WHERE workspace_id AND run_id AND state = expected
        // P0-3 修复双真源：同一 UPDATE 语句中同步更新 state 列与 data JSON 中的
        // State / UpdatedAt / FinishedAt 字段，消除"state 列 ≠ data JSON.State"的不一致。
        // data JSON 的 State 字段使用枚举名字符串（与 PostgresJsonSerializer 的 JsonStringEnumConverter 一致）。
        // 使用 jsonb || jsonb_build_object 合并覆盖（原子操作，无 read-before-write）。
        // P0-4：当 leaseToken + fencingToken 提供时，WHERE 追加 EXISTS 子查询校验 agent_run_leases
        // 中仍由当前实例持有该 lease；lease 被抢占后 fencing_token 不匹配 → 0 行受影响 → 抛异常。
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            var setFinished = isTerminal ? ", finished_at = @finished_at" : string.Empty;
            // 终态时同步更新 data JSON 中的 FinishedAt 字段；非终态不覆盖（保留原值）
            var dataMerge = isTerminal
                ? "data = data || jsonb_build_object('State', to_jsonb(@new_state_name), 'UpdatedAt', to_jsonb(@updated_at), 'FinishedAt', to_jsonb(@finished_at))"
                : "data = data || jsonb_build_object('State', to_jsonb(@new_state_name), 'UpdatedAt', to_jsonb(@updated_at))";
            // P0-4：lease fencing 校验子句（EXISTS 子查询到 agent_run_leases）
            var leaseClause = leaseValidated
                ? $" AND EXISTS (SELECT 1 FROM {Table("agent_run_leases")} l WHERE l.run_id = @run_id AND l.lease_token = @lease_token AND l.fencing_token = @fencing_token)"
                : string.Empty;
            updateCommand.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = @new_state, updated_at = @updated_at{setFinished}, {dataMerge}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND state = @expected_state{leaseClause};
""";
            updateCommand.Parameters.AddWithValue("workspace_id", workspaceId);
            updateCommand.Parameters.AddWithValue("run_id", runId);
            updateCommand.Parameters.AddWithValue("expected_state", (byte)expectedCurrentState);
            updateCommand.Parameters.AddWithValue("new_state", (byte)newState);
            updateCommand.Parameters.AddWithValue("new_state_name", newState.ToString());
            updateCommand.Parameters.AddWithValue("updated_at", now);
            if (isTerminal)
            {
                updateCommand.Parameters.AddWithValue("finished_at", now);
            }
            if (leaseValidated)
            {
                updateCommand.Parameters.AddWithValue("lease_token", leaseToken!);
                updateCommand.Parameters.AddWithValue("fencing_token", fencingToken!.Value);
            }

            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected > 0)
            {
                return; // CAS 成功
            }
        }

        // 2. 0 行受影响：检查行是否存在以区分"逆退/已推进/lease 失效"与"Run 不存在"
        await using var selectCommand = connection.CreateCommand();
        selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        selectCommand.CommandText = $"""
SELECT state FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
LIMIT 1;
""";
        selectCommand.Parameters.AddWithValue("workspace_id", workspaceId);
        selectCommand.Parameters.AddWithValue("run_id", runId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var currentState = (AgentRunState)reader.GetByte(0);
            // P0-4：lease 校验失败时给出专门的错误信息（区分于状态 CAS 失败）
            if (leaseValidated && currentState == expectedCurrentState)
            {
                throw new InvalidOperationException(
                    $"Agent Run lease fencing 校验失败：workspace_id={workspaceId}, run_id={runId}。" +
                    $"状态机前件匹配（{expectedCurrentState}），但 lease_token/fencing_token 不匹配——" +
                    $"lease 已被其他实例抢占，应立即停止处理该 Run。");
            }
            throw new InvalidOperationException(
                $"Agent Run 状态机 CAS 失败：workspace_id={workspaceId}, run_id={runId}。" +
                $"期望当前状态={expectedCurrentState}，实际={currentState}。" +
                $"状态已被其他实例推进或不可逆退。");
        }

        // 3. 行不存在
        throw new InvalidOperationException(
            $"Agent Run 不存在：workspace_id={workspaceId}, run_id={runId}。" +
            $"无法推进状态机（缺失 Run 元数据）。");
    }

    /// <inheritdoc />
    public async ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 注意：UPDATE 不修改 state 列（避免旁路 CAS）；state 只能通过 TransitionStateAsync 推进
        command.CommandText = $"""
UPDATE {Table("agent_runs")}
SET session_id = @session_id,
    task = @task,
    turn = @turn,
    updated_at = @updated_at,
    finished_at = @finished_at,
    failure_reason = @failure_reason,
    final_answer = @final_answer,
    turn_budget_json = @turn_budget_json,
    cost_budget_json = @cost_budget_json,
    data = @data
WHERE workspace_id = @workspace_id AND run_id = @run_id;
""";
        command.Parameters.AddWithValue("workspace_id", run.WorkspaceId);
        command.Parameters.AddWithValue("run_id", run.RunId);
        command.Parameters.AddWithValue("session_id", run.SessionId);
        command.Parameters.AddWithValue("task", run.Task ?? string.Empty);
        command.Parameters.AddWithValue("turn", run.Turn);
        command.Parameters.AddWithValue("updated_at", run.UpdatedAt);
        command.Parameters.AddWithValue("finished_at", (object?)run.FinishedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("failure_reason", (object?)run.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("final_answer", (object?)run.FinalAnswer ?? DBNull.Value);
        command.Parameters.AddWithValue("turn_budget_json", run.TurnBudget is null
            ? DBNull.Value
            : JsonSerializer.Serialize(run.TurnBudget));
        command.Parameters.AddWithValue("cost_budget_json", run.CostBudget is null
            ? DBNull.Value
            : JsonSerializer.Serialize(run.CostBudget));
        AddJson(command, "data", run);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(
        string workspaceId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND session_id = @session_id
ORDER BY created_at ASC;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("session_id", sessionId);
        return await ExecuteReaderJsonAsync<AgentRun>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
        AgentRunState state,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            take = 100;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("agent_runs")}
WHERE state = @state
ORDER BY created_at ASC
LIMIT @take;
""";
        command.Parameters.AddWithValue("state", (byte)state);
        command.Parameters.AddWithValue("take", take);
        return await ExecuteReaderJsonAsync<AgentRun>(command, cancellationToken).ConfigureAwait(false);
    }
}
