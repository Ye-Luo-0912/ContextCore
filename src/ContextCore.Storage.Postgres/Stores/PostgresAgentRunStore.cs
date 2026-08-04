using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Agent Run Store。
/// 替代 <see cref="ContextCore.Core.Services.AgentRunRuntime.InMemoryAgentRunStore"/>，
/// 让 HA 场景下 Agent Run 元数据可跨进程持久化与崩溃恢复。
/// </summary>
/// <remarks>
/// 设计要点（参考 <see cref="PostgresAgentCheckpointStore"/>）：
/// 1. 表 <c>agent_runs</c> 反规范化 workspace_id / run_id / session_id / state / turn 字段以便索引查询；
/// 完整 <see cref="AgentRun"/> 对象保存在 <c>data jsonb</c>，由 store 反序列化。
/// 2. 主键 (workspace_id, run_id)：跨 workspace 隔离 + 同 workspace 内 run_id 唯一。
/// 3. <see cref="CreateAsync"/> 使用 <c>INSERT ... ON CONFLICT DO NOTHING</c> 保证幂等。
/// 4. <see cref="TransitionStateAsync"/> 使用 expected-state CAS：
/// <c>UPDATE ... SET state=@new WHERE workspace_id AND run_id AND state=@expected</c>；
/// 0 行受影响时抛 <see cref="InvalidOperationException"/>（状态已被其他实例推进或逆退）。
/// 5. <see cref="UpdateAsync"/> 更新可变字段（turn / final_answer / failure_reason / 预算 JSON）。
/// 6. <c>turn_budget_json</c> / <c>cost_budget_json</c> 存预算 JSON 列（与 checkpoint state_json 模式一致）。
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
    priority, max_retries, retry_count, next_retry_at,
    created_at, updated_at, finished_at, failure_reason, final_answer,
    turn_budget_json, cost_budget_json, idempotency_key, data)
VALUES (
    @workspace_id, @run_id, @session_id, @task, @state, @turn,
    @priority, @max_retries, @retry_count, @next_retry_at,
    @created_at, @updated_at, @finished_at, @failure_reason, @final_answer,
    @turn_budget_json, @cost_budget_json, @idempotency_key, @data)
ON CONFLICT (workspace_id, run_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("workspace_id", run.WorkspaceId);
        command.Parameters.AddWithValue("run_id", run.RunId);
        command.Parameters.AddWithValue("session_id", run.SessionId);
        command.Parameters.AddWithValue("task", run.Task ?? string.Empty);
        command.Parameters.AddWithValue("state", (byte)run.State);
        command.Parameters.AddWithValue("turn", run.Turn);
        command.Parameters.AddWithValue("priority", run.Priority);
        command.Parameters.AddWithValue("max_retries", run.MaxRetries);
        command.Parameters.AddWithValue("retry_count", run.RetryCount);
        command.Parameters.AddWithValue("next_retry_at", (object?)run.NextRetryAtUtc ?? DBNull.Value);
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
        command.Parameters.AddWithValue("idempotency_key", (object?)run.IdempotencyKey ?? DBNull.Value);
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
    public async ValueTask<AgentRun?> GetByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        // null/空 idempotencyKey 不参与查询（与 partial UNIQUE 索引 WHERE idempotency_key IS NOT NULL 语义一致）
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 走 (workspace_id, idempotency_key) partial UNIQUE 索引点查
        command.CommandText = $"""
SELECT data
FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND idempotency_key = @idempotency_key
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        return await ExecuteScalarJsonAsync<AgentRun>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Atomic create-or-get. INSERT ... ON CONFLICT DO NOTHING with RETURNING;
    /// if no row returned, SELECT existing by idempotency_key or primary key.
    /// </remarks>
    public async ValueTask<AgentRunCreateResult> CreateOrGetByIdempotencyKeyAsync(
        AgentRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCommand.CommandText = $"""
INSERT INTO {Table("agent_runs")} (
    workspace_id, run_id, session_id, task, state, turn,
    priority, max_retries, retry_count, next_retry_at,
    created_at, updated_at, finished_at, failure_reason, final_answer,
    turn_budget_json, cost_budget_json, idempotency_key, data)
VALUES (
    @workspace_id, @run_id, @session_id, @task, @state, @turn,
    @priority, @max_retries, @retry_count, @next_retry_at,
    @created_at, @updated_at, @finished_at, @failure_reason, @final_answer,
    @turn_budget_json, @cost_budget_json, @idempotency_key, @data)
ON CONFLICT (workspace_id, run_id) DO NOTHING
RETURNING data;
""";        insertCommand.Parameters.AddWithValue("workspace_id", run.WorkspaceId);
        insertCommand.Parameters.AddWithValue("run_id", run.RunId);
        insertCommand.Parameters.AddWithValue("session_id", run.SessionId);
        insertCommand.Parameters.AddWithValue("task", run.Task ?? string.Empty);
        insertCommand.Parameters.AddWithValue("state", (byte)run.State);
        insertCommand.Parameters.AddWithValue("turn", run.Turn);
        insertCommand.Parameters.AddWithValue("priority", run.Priority);
        insertCommand.Parameters.AddWithValue("max_retries", run.MaxRetries);
        insertCommand.Parameters.AddWithValue("retry_count", run.RetryCount);
        insertCommand.Parameters.AddWithValue("next_retry_at", (object?)run.NextRetryAtUtc ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("created_at", run.CreatedAt);
        insertCommand.Parameters.AddWithValue("updated_at", run.UpdatedAt);        insertCommand.Parameters.AddWithValue("finished_at", (object?)run.FinishedAt ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("failure_reason", (object?)run.FailureReason ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("final_answer", (object?)run.FinalAnswer ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("turn_budget_json", run.TurnBudget is null ? DBNull.Value : JsonSerializer.Serialize(run.TurnBudget));
        insertCommand.Parameters.AddWithValue("cost_budget_json", run.CostBudget is null ? DBNull.Value : JsonSerializer.Serialize(run.CostBudget));
        insertCommand.Parameters.AddWithValue("idempotency_key", (object?)run.IdempotencyKey ?? DBNull.Value);
        AddJson(insertCommand, "data", run);
        // ON CONFLICT (workspace_id, run_id) DO NOTHING 仅覆盖主键冲突。
        // 当两次请求使用相同 IdempotencyKey 但不同 run_id（每次 POST 生成新 GUID）时，
        // 会触发 partial UNIQUE 索引 (workspace_id, idempotency_key) 的 unique_violation（23505）。
        // 捕获该异常后落入门下 SELECT 逻辑，返回已有 Run（幂等去重）。
        object? insertedData;
        try
        {
            insertedData = await insertCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            insertedData = null;
        }
        if (insertedData is not null and not DBNull)
        {
            var insertedRun = Serializer.Deserialize<AgentRun>((string)insertedData);
            return new AgentRunCreateResult { Created = true, Run = insertedRun ?? run, WasExisting = false };
        }

        if (!string.IsNullOrWhiteSpace(run.IdempotencyKey))
        {
            var existing = await GetByIdempotencyKeyAsync(run.WorkspaceId, run.IdempotencyKey, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return new AgentRunCreateResult { Created = false, Run = existing, WasExisting = true };
            }
        }
        var existingById = await GetAsync(run.WorkspaceId, run.RunId, ct).ConfigureAwait(false);
        if (existingById is not null)
        {
            return new AgentRunCreateResult { Created = false, Run = existingById, WasExisting = true };
        }

        throw new InvalidOperationException(
            $"INSERT conflict but no existing row found for workspace_id={run.WorkspaceId}, run_id={run.RunId}.");
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
        // lease 校验要求 leaseToken + fencingToken 同时提供（或同时为 null）
        var leaseValidated = leaseToken is not null && fencingToken is not null;

        var now = DateTimeOffset.UtcNow;
        // 终态集合与 AgentRunStateMachine.IsTerminalState 对齐（P0-6：AdmissionRejected 亦是终态，
        // 配额拒绝后记录 finished_at 供审计/列举）。RecoveryBlocked/RecoveryCorrupted/
        // ReconciliationRejected 由各自专用路径写 finished_at，此处不重复覆盖。
        var isTerminal = newState == AgentRunState.Completed
                         || newState == AgentRunState.Failed
                         || newState == AgentRunState.Cancelled
                         || newState == AgentRunState.LeaseLost
                         || newState == AgentRunState.DeadLettered
                         || newState == AgentRunState.AdmissionRejected;

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. expected-state CAS：UPDATE WHERE workspace_id AND run_id AND state = expected
        // 修复双真源：同一 UPDATE 语句中同步更新 state 列与 data JSON 中的
        // State / UpdatedAt / FinishedAt 字段，消除"state 列 ≠ data JSON.State"的不一致。
        // data JSON 的 State 字段使用枚举名字符串（与 PostgresJsonSerializer 的 JsonStringEnumConverter 一致）。
        // 使用 jsonb || jsonb_build_object 合并覆盖（原子操作，无 read-before-write）。
        // 当 leaseToken + fencingToken 提供时，WHERE 追加 EXISTS 子查询校验 agent_run_leases
        // 中仍由当前实例持有该 lease；lease 被抢占后 fencing_token 不匹配 → 0 行受影响 → 抛异常。
        await using (var updateCommand = connection.CreateCommand())
        {
            updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            var setFinished = isTerminal ? ", finished_at = @finished_at" : string.Empty;
            // 终态时同步更新 data JSON 中的 FinishedAt 字段；非终态不覆盖（保留原值）
            var dataMerge = isTerminal
                ? "data = data || jsonb_build_object('State', to_jsonb(@new_state_name), 'UpdatedAt', to_jsonb(@updated_at), 'FinishedAt', to_jsonb(@finished_at))"
                : "data = data || jsonb_build_object('State', to_jsonb(@new_state_name), 'UpdatedAt', to_jsonb(@updated_at))";
            // + lease fencing 校验子句（EXISTS 子查询到 agent_run_leases）
            // 同时校验 lease_expires_at > clock_timestamp()，防止已过期但未被 reaper 清理的租约
            // 仍能通过 fencing 校验（fencing_token 匹配但租约实际已过期 → 仍应拒绝写入）。
            var leaseClause = leaseValidated
                ? $" AND EXISTS (SELECT 1 FROM {Table("agent_run_leases")} l WHERE l.run_id = @run_id AND l.lease_token = @lease_token AND l.fencing_token = @fencing_token AND l.lease_expires_at > clock_timestamp())"
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
            // lease 校验失败时给出专门的错误信息（区分于状态 CAS 失败）
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
        // 注意：UPDATE 不修改 state 列（避免旁路 CAS）；state 只能通过 TransitionStateAsync 推进。
        // 调度/重试列（priority / max_retries / retry_count / next_retry_at）随 data 同步归一化，
        // 保证列与 data jsonb 中 AgentRun 记录一致（与 CreateAsync 相同的反规范化模式）。
        command.CommandText = $"""
UPDATE {Table("agent_runs")}
SET session_id = @session_id,
    task = @task,
    turn = @turn,
    priority = @priority,
    max_retries = @max_retries,
    retry_count = @retry_count,
    next_retry_at = @next_retry_at,
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
        command.Parameters.AddWithValue("priority", run.Priority);
        command.Parameters.AddWithValue("max_retries", run.MaxRetries);
        command.Parameters.AddWithValue("retry_count", run.RetryCount);
        command.Parameters.AddWithValue("next_retry_at", (object?)run.NextRetryAtUtc ?? DBNull.Value);
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
        DateTimeOffset? afterUpdatedAt = null,
        string? afterRunId = null,
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
  AND (@after_updated_at IS NULL
       OR updated_at > @after_updated_at
       OR (updated_at = @after_updated_at AND run_id > @after_run_id))
ORDER BY updated_at ASC, run_id ASC
LIMIT @take;
""";
        command.Parameters.AddWithValue("state", (byte)state);
        command.Parameters.AddWithValue("take", take);
        command.Parameters.AddWithValue("after_updated_at", (object?)afterUpdatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("after_run_id", (object?)afterRunId ?? DBNull.Value);
        return await ExecuteReaderJsonAsync<AgentRun>(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 单条 SQL 事务内完成"领取 + 真正写入 Scheduler Claim（P0-8）"：
    /// 1. 三层嵌套（Postgres 限制：FOR UPDATE 不能与窗口函数同层）：
    /// 内层 SELECT ... FOR UPDATE SKIP LOCKED（锁定候选行，被锁行跳过下轮再取）；
    /// 中层 ROW_NUMBER() OVER (PARTITION BY workspace_id ORDER BY priority DESC, created_at ASC, run_id ASC)
    /// 计算每 workspace 内排名（公平轮转上限）；
    /// 外层 WHERE ws_rank &lt;= @per_workspace。
    /// 2. tokens CTE 为每行生成唯一 claim_token（md5(random + clock)，单表达式复用保证
    /// 列与 data jsonb 写入同一令牌）。
    /// 3. UPDATE ... FROM tokens：领取行统一置为 Claimed（state=22）+ claim_owner / claim_token /
    /// claim_expires_at——Scheduler Claim Lease 真正落库（不再只打 UpdatedAt 补丁）：
    ///   - Queued（state=21）→ 领取；
    ///   - Claimed（state=22）且 claim 已过期 → 重新领取（节点崩溃后其他节点接管）；
    ///   - Failed（state=8）且可重试 → 重置为 Claimed + retry_count+1 + next_retry_at=指数退避
    ///     （base × 2^(retry_count-1)，封顶 cap），清空失败字段与 checkpoint 指针
    ///     （新 Attempt 全新启动，不复用前序 Attempt 的 checkpoint 上下文）；
    ///   - RecoveryDependencyUnavailable（state=17）退避门通过 → 领取（事件流保留，按哈希链重放）。
    ///   - Created（state=0）/ PendingAdmission（state=19）/ AdmissionRejected（state=20）永不领取
    ///     （P0-6 Admission 边界：配额未通过的 Run 不得进入可调度状态）。
    /// 4. 不可变 Attempt：重试**不删除** agent_run_events（前序 Attempt 历史保留，不可变审计）。
    ///    RunRetryScheduled / AttemptStarted 边界标记由 Actor 在重试 Attempt 开始时
    ///    于既有事件链上续写；恢复重放以最后一个 RunRetryScheduled 为界只重放当前 Attempt。
    /// 5. data jsonb 同步打补丁（State/UpdatedAt/ClaimOwner/ClaimToken/ClaimExpiresAtUtc/RetryCount/
    /// NextRetryAtUtc/FinishedAt/FailureReason/FinalAnswer），保持"state 列 == data JSON.State"单真源。
    /// </remarks>
    public async ValueTask<IReadOnlyList<AgentRun>> ClaimPendingBatchAsync(
        int take,
        int perWorkspace,
        TimeSpan retryBackoffBase,
        TimeSpan retryBackoffMax,
        string claimOwner,
        TimeSpan claimDuration,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Array.Empty<AgentRun>();
        }
        if (perWorkspace <= 0)
        {
            perWorkspace = take;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var backoffBase = retryBackoffBase > TimeSpan.Zero ? retryBackoffBase : TimeSpan.FromSeconds(30);
        var backoffMax = retryBackoffMax > TimeSpan.Zero ? retryBackoffMax : TimeSpan.FromMinutes(30);
        var backoffInterval = backoffBase > backoffMax ? backoffMax : backoffBase;

        // 前置标记：过期的 Claimed（claim 租约到期未续约）→ ClaimExpired（显式失效状态，可观测）。
        // 与主领取同事务：先标记过期，再领取 Queued / ClaimExpired，避免并发窗口。
        await using (var expiryCommand = connection.CreateCommand())
        {
            expiryCommand.Transaction = transaction;
            expiryCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            expiryCommand.CommandText = $"""
                UPDATE {Table("agent_runs")}
                SET state = 24,
                    updated_at = clock_timestamp(),
                    data = data || jsonb_build_object(
                        'State', to_jsonb('ClaimExpired'::text),
                        'UpdatedAt', to_jsonb(clock_timestamp()))
                WHERE state = 22
                  AND (claim_expires_at IS NULL OR claim_expires_at <= clock_timestamp());
                """;
            await expiryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("take", take);
        command.Parameters.AddWithValue("per_workspace", perWorkspace);
        command.Parameters.AddWithValue("claim_owner", claimOwner);
        command.Parameters.AddWithValue("claim_duration", claimDuration > TimeSpan.Zero ? claimDuration : TimeSpan.FromSeconds(60));
        // interval 参数：Npgsql 将 TimeSpan 映射为 interval；LEAST(interval × 2^n, cap) 实现指数退避封顶。
        command.Parameters.AddWithValue("backoff_base", backoffInterval);
        command.Parameters.AddWithValue("backoff_cap", backoffMax);
        command.CommandText = $"""
WITH eligible AS (
    SELECT workspace_id, run_id, ws_rank
    FROM (
        SELECT workspace_id, run_id,
               ROW_NUMBER() OVER (
                   PARTITION BY workspace_id
                   ORDER BY priority DESC, created_at ASC, run_id ASC) AS ws_rank
        FROM (
            SELECT workspace_id, run_id, priority, created_at, state
            FROM {Table("agent_runs")}
            WHERE (
                -- Queued（state=21）且退避门通过（next_retry_at 为 null 或已到期）
                (state = 21 AND (next_retry_at IS NULL OR next_retry_at <= clock_timestamp()))
                OR
                -- Claimed（state=22）且 claim 已过期（节点领取后崩溃 → 其他节点接管）。
                -- 前置标记已把过期 Claimed 转为 ClaimExpired（state=24），此处同时认两种形态
                -- （防御：并发窗口内可能仍是过期 Claimed）。
                (state = 22 AND (claim_expires_at IS NULL OR claim_expires_at <= clock_timestamp()))
                OR
                -- ClaimExpired（state=24）：claim 已显式失效，可直接重新领取
                (state = 24)
                OR
                -- Failed（state=8）且配置了重试（max_retries > 0）且未耗尽（retry_count < max_retries）且退避门通过
                (state = 8 AND max_retries > 0 AND retry_count < max_retries
                 AND (next_retry_at IS NULL OR next_retry_at <= clock_timestamp()))
                OR
                -- Recovery Integrity State：恢复依赖不可用（state = 17）为可重试状态（非终态），
                -- 退避门（NextRetryAtUtc）通过后由 Durable Scheduler 领取重新入队。
                (state = 17 AND (next_retry_at IS NULL OR next_retry_at <= clock_timestamp()))
            )
            ORDER BY priority DESC, created_at ASC, run_id ASC
            LIMIT @take
            FOR UPDATE SKIP LOCKED
        ) locked
    ) ranked
    WHERE ws_rank <= @per_workspace
),
-- 每行唯一 claim_token：同一表达式在列与 data jsonb 中复用（md5(random + clock) 每行仅求值一次）。
tokens AS (
    SELECT workspace_id, run_id,
           md5(random()::text || clock_timestamp()::text) AS claim_token
    FROM eligible
),
updated AS (
    UPDATE {Table("agent_runs")} ar
    SET state = 22,
        claim_owner = @claim_owner,
        claim_token = t.claim_token,
        claim_expires_at = clock_timestamp() + @claim_duration,
        claim_attempt = ar.claim_attempt + 1,
        retry_count = CASE WHEN ar.state = 8 THEN ar.retry_count + 1 ELSE ar.retry_count END,
        next_retry_at = CASE WHEN ar.state = 8
            THEN clock_timestamp() + LEAST(@backoff_base * POWER(2, GREATEST(ar.retry_count, 0))::double precision, @backoff_cap)
            ELSE ar.next_retry_at END,
        updated_at = clock_timestamp(),
        finished_at = CASE WHEN ar.state = 8 THEN NULL ELSE ar.finished_at END,
        failure_reason = CASE WHEN ar.state = 8 THEN NULL ELSE ar.failure_reason END,
        final_answer = CASE WHEN ar.state = 8 THEN NULL ELSE ar.final_answer END,
        last_checkpoint_id = CASE WHEN ar.state = 8 THEN NULL ELSE ar.last_checkpoint_id END,
        last_checkpoint_sequence = CASE WHEN ar.state = 8 THEN NULL ELSE ar.last_checkpoint_sequence END,
        data = CASE WHEN ar.state = 8
            THEN data || jsonb_build_object(
                'State', to_jsonb('Claimed'::text),
                'UpdatedAt', to_jsonb(clock_timestamp()),
                'ClaimOwner', to_jsonb(@claim_owner),
                'ClaimToken', to_jsonb(t.claim_token),
                'ClaimExpiresAtUtc', to_jsonb(clock_timestamp() + @claim_duration),
                'RetryCount', to_jsonb(ar.retry_count + 1),
                'ClaimAttempt', to_jsonb(ar.claim_attempt + 1),
                'NextRetryAtUtc', to_jsonb(clock_timestamp() + LEAST(@backoff_base * POWER(2, GREATEST(ar.retry_count, 0))::double precision, @backoff_cap)),
                'FinishedAt', to_jsonb(NULL::text),
                'FailureReason', to_jsonb(NULL::text),
                'FinalAnswer', to_jsonb(NULL::text))
            ELSE data || jsonb_build_object(
                'State', to_jsonb('Claimed'::text),
                'UpdatedAt', to_jsonb(clock_timestamp()),
                'ClaimOwner', to_jsonb(@claim_owner),
                'ClaimToken', to_jsonb(t.claim_token),
                'ClaimExpiresAtUtc', to_jsonb(clock_timestamp() + @claim_duration),
                'ClaimAttempt', to_jsonb(ar.claim_attempt + 1))
            END
    FROM tokens t
    WHERE ar.workspace_id = t.workspace_id AND ar.run_id = t.run_id
    RETURNING ar.workspace_id, ar.run_id, ar.data, ar.claim_token
)
SELECT workspace_id, run_id, data, claim_token FROM updated;
""";

        var runs = new List<AgentRun>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var data = reader.GetString(2);
                var run = Serializer.Deserialize<AgentRun>(data);
                if (run is not null)
                {
                    // 从列回填 claim_token（data jsonb 与列同值；防御性校验一致性）。
                    runs.Add(run with { ClaimToken = reader.GetString(3) });
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return runs;
    }

    /// <inheritdoc />
    public async ValueTask<AgentRun?> TryClaimSingleAsync(
        string workspaceId,
        string runId,
        string claimOwner,
        TimeSpan claimDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimOwner);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 单行 CTE 生成唯一 claim_token（列与 data jsonb 同一值）。
        command.CommandText = $"""
WITH claim AS (
    SELECT md5(random()::text || clock_timestamp()::text) AS token
)
UPDATE {Table("agent_runs")} ar
SET state = 22,
    claim_owner = @claim_owner,
    claim_token = claim.token,
    claim_expires_at = clock_timestamp() + @claim_duration,
    claim_attempt = ar.claim_attempt + 1,
    updated_at = clock_timestamp(),
    data = data || jsonb_build_object(
        'State', to_jsonb('Claimed'::text),
        'UpdatedAt', to_jsonb(clock_timestamp()),
        'ClaimOwner', to_jsonb(@claim_owner),
        'ClaimToken', to_jsonb(claim.token),
        'ClaimExpiresAtUtc', to_jsonb(clock_timestamp() + @claim_duration),
        'ClaimAttempt', to_jsonb(ar.claim_attempt + 1))
FROM claim
WHERE ar.workspace_id = @workspace_id AND ar.run_id = @run_id
  AND (ar.state = 21 OR ar.state = 24)
  AND (ar.next_retry_at IS NULL OR ar.next_retry_at <= clock_timestamp())
RETURNING ar.data;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("claim_owner", claimOwner);
        command.Parameters.AddWithValue("claim_duration", claimDuration > TimeSpan.Zero ? claimDuration : TimeSpan.FromSeconds(60));

        object? result;
        try
        {
            result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
        if (result is null or DBNull)
        {
            return null; // 非 Queued / 已被领取 / 退避中 → 不可领取
        }
        var data = (string)result;
        var run = Serializer.Deserialize<AgentRun>(data);
        if (run is null)
        {
            return null;
        }
        // data jsonb 已含完整 claim 补丁（State=Claimed + ClaimOwner/ClaimToken/ClaimExpiresAtUtc）。
        return run;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReleaseClaimAsync(
        string workspaceId,
        string runId,
        string claimToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = 21,
    claim_owner = NULL,
    claim_token = NULL,
    claim_expires_at = NULL,
    updated_at = clock_timestamp(),
    data = data || jsonb_build_object(
        'State', to_jsonb('Queued'::text),
        'UpdatedAt', to_jsonb(clock_timestamp()),
        'ClaimOwner', to_jsonb(NULL::text),
        'ClaimToken', to_jsonb(NULL::text),
        'ClaimExpiresAtUtc', to_jsonb(NULL::text))
WHERE workspace_id = @workspace_id AND run_id = @run_id
  AND state = 22
  AND claim_token = @claim_token;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("claim_token", claimToken);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 原子死信：Failed（state=8）且 max_retries &gt; 0 且 retry_count &gt;= max_retries 且退避门通过
    /// → state=DeadLettered（终态）+ finished_at + data jsonb 打补丁（State/UpdatedAt/FinishedAt）。
    /// 保留 failure_reason / 事件流作为审计证据（死信后不再自动恢复，需运维介入）。
    /// 终态写入天然幂等（重复执行无副作用），LIMIT take 分批；无需 SKIP LOCKED。
    /// </remarks>
    public async ValueTask<IReadOnlyList<AgentRun>> DeadLetterExhaustedRunsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Array.Empty<AgentRun>();
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("take", take);
        command.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = 18,
    updated_at = clock_timestamp(),
    finished_at = COALESCE(finished_at, clock_timestamp()),
    data = data || jsonb_build_object(
        'State', to_jsonb('DeadLettered'::text),
        'UpdatedAt', to_jsonb(clock_timestamp()),
        'FinishedAt', to_jsonb(COALESCE(finished_at, clock_timestamp())))
WHERE state = 8
  AND max_retries > 0
  AND retry_count >= max_retries
  AND (next_retry_at IS NULL OR next_retry_at <= clock_timestamp())
ORDER BY priority DESC, created_at ASC
LIMIT @take
RETURNING data;
""";
        return await ExecuteReaderJsonAsync<AgentRun>(command, cancellationToken).ConfigureAwait(false);
    }
}
