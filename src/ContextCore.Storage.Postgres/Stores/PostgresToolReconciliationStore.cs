using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

// ===========================================================================
// PostgresToolReconciliationStore — Tool 对账记录存储（PostgreSQL 持久化实现）
//
// Tool Reconciliation Control Plane（-B1）：对账记录跨进程持久化，
// 替代 InMemoryToolReconciliationStore 成为 ProductionHA 组合根下的真相源：
// - 多实例 ToolReconciliationWorker / 人工 resolve 端点共享同一数据库，
// 杜绝"对账记录只在创建它的实例内存中"导致的裁决丢失；
// - CreateAsync 按 (workspace_id, run_id, request_id) UNIQUE 幂等（P0-5 完整租户键）；
// - TryBeginAsync 领取裁决租约（P0-4）：CTE + FOR UPDATE SKIP LOCKED 原子领取，
//   Pending → Running 并写入 lease_owner / lease_token / lease_expires_at /
//   fencing_token+1 / attempt_count+1；租约过期的 Running 记录可被重新接管；
// - RenewLeaseAsync / TryResetToPendingAsync / MarkResolvedAsync / MarkRejectedAsync
//   全部校验 lease_token = @token AND lease_expires_at > clock_timestamp()（P0-4）；
// - ResolveReconciliationAtomicallyAsync（P0-3）单事务完成：锁定对账记录 →
//   验证唯一裁决者（lease + fencing）→ 锁定 Journal 行并按状态分支
//   （仅 DispatchingIntent/Dispatched/Reconciling 可推进 Committed；Committed/ResultDelivered
//   按结果指纹幂等判定，Prepared/缺失 → Corrupted）→ Durable Result UPSERT（含指纹）→
//   记录终态 → Run 状态推进（停车且无其他未决 → Queued，同一事务）→ 审计事件追加，
//   任意一步失败整体回滚，杜绝"记录 Resolved 而 Journal 仍 DispatchingIntent"的撕裂；
// - deadline_utc 列 + ControlRoom 列表（ListAsync）支持过期未决高亮与告警计数；
// - external_operation_id partial 索引支持按 journal 外部操作 ID 反查。
// ===========================================================================

/// <summary>
/// Tool 对账记录存储（PostgreSQL 实现）。完整 <see cref="ToolReconciliationRecord"/>
/// 持久化到 <c>data jsonb</c>，规范化字段（run_id / request_id / status / deadline_utc /
/// external_operation_id / 租约字段等）用于索引查询与 CAS。
/// </summary>
public sealed class PostgresToolReconciliationStore : PostgresStoreBase, IToolReconciliationStore
{
    /// <summary>ControlRoom 分页大小上限（服务端 clamp，防止无界读取）。</summary>
    public const int MaxPageSize = 200;

    /// <summary>规范化读取列清单：CAS 更新只改这些列，读取必须走列而非 data jsonb（jsonb 会过期）。</summary>
    private const string ReadColumns = """
        reconciliation_id, run_id, workspace_id, request_id, tool_name,
        external_operation_id, reconciliation_handler, status,
        result, side_effect_occurred, reason,
        created_at, updated_at, resolved_at, deadline_utc,
        lease_owner, lease_token, lease_expires_at, fencing_token, attempt_count, next_attempt_at, last_error,
        decision_request_id
        """;

    public PostgresToolReconciliationStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationRecord> CreateAsync(ToolReconciliationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("tool_reconciliation_entries")} (
    reconciliation_id, run_id, workspace_id, request_id, tool_name,
    external_operation_id, reconciliation_handler, status,
    result, side_effect_occurred, reason,
    created_at, updated_at, resolved_at, deadline_utc, data)
VALUES (
    @reconciliation_id, @run_id, @workspace_id, @request_id, @tool_name,
    @external_operation_id, @reconciliation_handler, @status,
    @result, @side_effect_occurred, @reason,
    @created_at, @updated_at, @resolved_at, @deadline_utc, @data)
ON CONFLICT (workspace_id, run_id, request_id) DO NOTHING;
""";
        command.Parameters.AddWithValue("reconciliation_id", record.ReconciliationId);
        command.Parameters.AddWithValue("run_id", record.RunId);
        command.Parameters.AddWithValue("workspace_id", record.WorkspaceId);
        command.Parameters.AddWithValue("request_id", record.RequestId);
        command.Parameters.AddWithValue("tool_name", record.ToolName);
        command.Parameters.AddWithValue("external_operation_id", (object?)record.ExternalOperationId ?? DBNull.Value);
        command.Parameters.AddWithValue("reconciliation_handler", (object?)record.ReconciliationHandler ?? DBNull.Value);
        command.Parameters.AddWithValue("status", (byte)record.Status);
        command.Parameters.AddWithValue("result", (object?)record.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("side_effect_occurred", (object?)record.SideEffectOccurred ?? DBNull.Value);
        command.Parameters.AddWithValue("reason", (object?)record.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("updated_at", (object?)record.UpdatedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("resolved_at", (object?)record.ResolvedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("deadline_utc", (object?)record.DeadlineUtc ?? DBNull.Value);
        AddJson(command, "data", record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // 幂等：无论 INSERT 是否生效，返回 (workspace_id, run_id, request_id) 对应的既有记录。
        var existing = await GetByRunAndRequestAsync(connection, record.WorkspaceId, record.RunId, record.RequestId, cancellationToken).ConfigureAwait(false);
        return existing ?? record;
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationRecord?> GetAsync(string reconciliationId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")} WHERE reconciliation_id = @reconciliation_id;";
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE run_id = @run_id
ORDER BY created_at ASC;
""";
        command.Parameters.AddWithValue("run_id", runId);
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolReconciliationRecord>> QueryByExternalOperationIdAsync(string externalOperationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(externalOperationId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE external_operation_id = @external_operation_id
ORDER BY created_at DESC;
""";
        command.Parameters.AddWithValue("external_operation_id", externalOperationId);
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ReconciliationListResult> ListAsync(ReconciliationQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit > 0 ? query.Limit : 50, 1, MaxPageSize);
        var offset = Math.Max(0, query.Offset);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 页面/总数 WHERE：基础过滤 + OverdueOnly（查询请求只看过期未决时）。
        // OverdueCount WHERE：始终按"过期未决"计算（DeadlineUtc < now 且 Pending/Running）。
        var pageWhere = BuildWhereClause(query, includeOverduePredicate: query.OverdueOnly);
        var overdueWhere = BuildWhereClause(query, includeOverduePredicate: true);
        command.CommandText = $"""
SELECT
    (SELECT count(*) FROM {Table("tool_reconciliation_entries")} WHERE {pageWhere}),
    (SELECT count(*) FROM {Table("tool_reconciliation_entries")} WHERE {overdueWhere})
""";
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        AddQueryParameters(command, query);

        int total;
        int overdueCount;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            // count(*) 在 Postgres 中返回 bigint；显式转 long 再收窄到 int。
            total = checked((int)reader.GetInt64(0));
            overdueCount = checked((int)reader.GetInt64(1));
        }

        // 分页条目：与过滤条件一致，按 CreatedAt 倒序（最新在前）。
        var pageCommand = connection.CreateCommand();
        pageCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        pageCommand.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE {pageWhere}
ORDER BY created_at DESC
LIMIT @limit OFFSET @offset;
""";
        pageCommand.Parameters.AddWithValue("limit", limit);
        pageCommand.Parameters.AddWithValue("offset", offset);
        AddQueryParameters(pageCommand, query);
        var items = await ReadManyAsync(pageCommand, cancellationToken).ConfigureAwait(false);

        return new ReconciliationListResult
        {
            Items = items,
            Total = total,
            OverdueCount = overdueCount
        };
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasUnresolvedForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT EXISTS (
    SELECT 1 FROM {Table("tool_reconciliation_entries")}
    WHERE run_id = @run_id
      AND status IN ({(byte)ToolReconciliationStatus.Pending}, {(byte)ToolReconciliationStatus.Running})
);
""";
        command.Parameters.AddWithValue("run_id", runId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is bool b && b;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ToolReconciliationRecord>> ListPendingAsync(int take, CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // P0-4：Pending 或租约已过期的 Running（Worker 崩溃后重新领取），
        // 并跳过 next_attempt_at 未到期的退避记录。
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE (status = @pending
       OR (status = @running AND (lease_expires_at IS NULL OR lease_expires_at <= clock_timestamp())))
  AND (next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp())
ORDER BY created_at ASC
LIMIT @take;
""";
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("take", TakeOrDefault(take));
        return await ReadManyAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationLease?> TryBeginAsync(
        string reconciliationId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciliationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var leaseToken = Guid.NewGuid().ToString("N");
        var expiresAt = now + leaseDuration;

        // CTE + FOR UPDATE SKIP LOCKED：原子领取裁决租约（P0-4）。
        // - Pending → 领取；Running 且租约已过期 → 接管（fencing 递增隔离旧持有者）。
        // - 有效租约持有中 / 终态 / 退避未到期 → 跳过（SKIP LOCKED 不阻塞并发领取者）。
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
WITH claim AS (
    SELECT reconciliation_id
    FROM {Table("tool_reconciliation_entries")}
    WHERE reconciliation_id = @reconciliation_id
      AND (status = @pending
           OR (status = @running AND (lease_expires_at IS NULL OR lease_expires_at <= clock_timestamp())))
      AND (next_attempt_at IS NULL OR next_attempt_at <= clock_timestamp())
    FOR UPDATE SKIP LOCKED
)
UPDATE {Table("tool_reconciliation_entries")} AS t
SET status = @running,
    lease_owner = @lease_owner,
    lease_token = @lease_token,
    lease_expires_at = @lease_expires_at,
    fencing_token = t.fencing_token + 1,
    attempt_count = t.attempt_count + 1,
    next_attempt_at = NULL,
    last_error = NULL,
    updated_at = @now
FROM claim
WHERE t.reconciliation_id = claim.reconciliation_id
RETURNING t.fencing_token;
""";
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("lease_owner", leaseOwner);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_expires_at", expiresAt);
        command.Parameters.AddWithValue("now", now);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is long fencingToken)
        {
            return new ToolReconciliationLease
            {
                LeaseToken = leaseToken,
                FencingToken = fencingToken,
                ExpiresAt = expiresAt
            };
        }

        // 0 行领取：区分"记录不存在"（抛）与"不可领取"（返回 null）。
        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        existsCommand.CommandText = $"""
SELECT 1 FROM {Table("tool_reconciliation_entries")}
WHERE reconciliation_id = @reconciliation_id
LIMIT 1;
""";
        existsCommand.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (exists is null or DBNull)
        {
            throw new InvalidOperationException($"对账记录不存在：{reconciliationId}");
        }
        return null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RenewLeaseAsync(
        string reconciliationId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciliationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET lease_expires_at = @lease_expires_at, updated_at = @now
WHERE reconciliation_id = @reconciliation_id
  AND status = @running
  AND lease_token = @lease_token
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("lease_expires_at", DateTimeOffset.UtcNow + leaseDuration);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> RenewHeartbeatBatchAsync(
        IReadOnlyList<ToolReconciliationHeartbeat> heartbeats,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heartbeats);
        if (heartbeats.Count == 0)
        {
            return Array.Empty<string>();
        }
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var reconciliationIds = new string[heartbeats.Count];
        var leaseTokens = new string[heartbeats.Count];
        for (var i = 0; i < heartbeats.Count; i++)
        {
            var heartbeat = heartbeats[i];
            ArgumentException.ThrowIfNullOrWhiteSpace(heartbeat.ReconciliationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(heartbeat.LeaseToken);
            reconciliationIds[i] = heartbeat.ReconciliationId;
            leaseTokens[i] = heartbeat.LeaseToken;
        }

        // 单条 SQL 批量续约：与单条路径同校验（reconciliation_id + lease_token + Running + 未过期），
        // RETURNING 返回成功续约的 reconciliation_id；未返回的即失败（租约被抢占或状态已改变）。
        var renewedIds = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")} AS t
SET lease_expires_at = @lease_expires_at, updated_at = @now
FROM unnest(@reconciliation_ids, @lease_tokens) AS req(reconciliation_id, lease_token)
WHERE t.reconciliation_id = req.reconciliation_id
  AND t.status = @running
  AND t.lease_token = req.lease_token
  AND t.lease_expires_at > clock_timestamp()
RETURNING t.reconciliation_id;
""";
        command.Parameters.AddWithValue("reconciliation_ids", reconciliationIds);
        command.Parameters.AddWithValue("lease_tokens", leaseTokens);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("lease_expires_at", DateTimeOffset.UtcNow + leaseDuration);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            renewedIds.Add(reader.GetString(0));
        }

        var failed = new List<string>(heartbeats.Count - renewedIds.Count);
        foreach (var heartbeat in heartbeats)
        {
            if (!renewedIds.Contains(heartbeat.ReconciliationId))
            {
                failed.Add(heartbeat.ReconciliationId);
            }
        }
        return failed;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryResetToPendingAsync(
        string reconciliationId,
        string leaseToken,
        string? lastError,
        TimeSpan? retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reconciliationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @pending,
    lease_owner = NULL,
    lease_token = NULL,
    lease_expires_at = NULL,
    next_attempt_at = @next_attempt_at,
    last_error = @last_error,
    updated_at = @now
WHERE reconciliation_id = @reconciliation_id
  AND status = @running
  AND lease_token = @lease_token
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("last_error", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("next_attempt_at", retryDelay.HasValue ? (object)(DateTimeOffset.UtcNow + retryDelay.Value) : DBNull.Value);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public ValueTask<bool> MarkResolvedAsync(string reconciliationId, string leaseToken, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default)
        => MarkTerminalAsync(reconciliationId, leaseToken, ToolReconciliationStatus.Resolved, outcome, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> MarkRejectedAsync(string reconciliationId, string leaseToken, ToolReconciliationOutcome outcome, CancellationToken cancellationToken = default)
        => MarkTerminalAsync(reconciliationId, leaseToken, ToolReconciliationStatus.Rejected, outcome, cancellationToken);

    /// <summary>CAS 推进到终态（Resolved/Rejected）：必须持有有效租约（P0-4）。已终态（幂等冲突）返回 false。</summary>
    private async ValueTask<bool> MarkTerminalAsync(
        string reconciliationId,
        string leaseToken,
        ToolReconciliationStatus target,
        ToolReconciliationOutcome outcome,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @target,
    result = @result,
    side_effect_occurred = @side_effect_occurred,
    reason = @reason,
    updated_at = @now,
    resolved_at = @now,
    lease_owner = NULL,
    lease_token = NULL,
    lease_expires_at = NULL,
    next_attempt_at = NULL,
    last_error = NULL
WHERE reconciliation_id = @reconciliation_id
  AND status IN (@pending, @running)
  AND lease_token = @lease_token
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("target", (byte)target);
        command.Parameters.AddWithValue("result", (object?)outcome.Result ?? DBNull.Value);
        command.Parameters.AddWithValue("side_effect_occurred", outcome.SideEffectOccurred);
        command.Parameters.AddWithValue("reason", (object?)outcome.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("reconciliation_id", reconciliationId);
        command.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        command.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<ToolReconciliationResolution> ResolveReconciliationAtomicallyAsync(
        string workspaceId,
        string runId,
        string requestId,
        string leaseToken,
        long expectedReconciliationVersion,
        ToolReconciliationOutcome outcome,
        DurableToolResult durableResult,
        CancellationToken cancellationToken = default,
        string? decisionRequestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(durableResult);
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // 1. 锁定 Reconciliation Record（完整租户键 (workspace_id, run_id, request_id)，P0-5）。
            ToolReconciliationRecord? record;
            await using (var lockCmd = connection.CreateCommand())
            {
                lockCmd.Transaction = transaction;
                lockCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                lockCmd.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND request_id = @request_id
FOR UPDATE;
""";
                lockCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                lockCmd.Parameters.AddWithValue("run_id", runId);
                lockCmd.Parameters.AddWithValue("request_id", requestId);
                await using var reader = await lockCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
            }

            if (record is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.NotFound };
            }
            if (record.Status is ToolReconciliationStatus.Resolved or ToolReconciliationStatus.Rejected)
            {
                // 客户端决策幂等——相同 DecisionRequestId 重试：outcome 一致 → 幂等成功（不覆盖首次真相）；
                // 相反 outcome → 决策冲突（客户端必须撤销或更换决策身份）；
                // 无决策身份或身份不同 → AlreadyTerminal（重复提交被拒绝）。
                if (!string.IsNullOrWhiteSpace(decisionRequestId)
                    && string.Equals(record.DecisionRequestId, decisionRequestId, StringComparison.Ordinal))
                {
                    var resolutionStatus = record.SideEffectOccurred == outcome.SideEffectOccurred
                        ? ToolReconciliationResolutionStatus.Resolved
                        : ToolReconciliationResolutionStatus.DecisionConflict;
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new ToolReconciliationResolution { Status = resolutionStatus, Record = record };
                }
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.AlreadyTerminal };
            }

            // 2. 验证唯一裁决者（P0-5）：租约匹配 + 未过期 + fencing 版本一致。
            if (!string.Equals(record.LeaseToken, leaseToken, StringComparison.Ordinal)
                || !record.LeaseExpiresAt.HasValue || record.LeaseExpiresAt.Value <= now)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.ArbitrationLost };
            }
            if (record.FencingToken != expectedReconciliationVersion)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ToolReconciliationResolution { Status = ToolReconciliationResolutionStatus.VersionMismatch };
            }

            // 3. 锁定 agent_runs 行（存在性 + 串行化本 Run 的审计事件追加；缺失 → fail-closed 回滚）。
            AgentRunState? auditState = null;
            bool runIsParking = false;
            await using (var runLockCmd = connection.CreateCommand())
            {
                runLockCmd.Transaction = transaction;
                runLockCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                runLockCmd.CommandText = $"""
SELECT state FROM {Table("agent_runs")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
FOR UPDATE;
""";
                runLockCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                runLockCmd.Parameters.AddWithValue("run_id", runId);
                await using var reader = await runLockCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"对账原子裁决失败：Run 不存在（workspace_id={workspaceId}, run_id={runId}），无法追加审计事件。");
                }
                auditState = (AgentRunState)reader.GetByte(0);
                runIsParking = auditState is AgentRunState.AwaitingReconciliation or AgentRunState.ReconciliationRunning;
            }

            // 4. 锁定 Journal 行并读取状态（裁决前提校验；与记录同一事务，行锁防止并发推进）。
            ToolDispatchState? journalState = null;
            await using (var journalLockCmd = connection.CreateCommand())
            {
                journalLockCmd.Transaction = transaction;
                journalLockCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                journalLockCmd.CommandText = $"""
SELECT state FROM {Table("tool_dispatch_journal_entries")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND request_id = @request_id
FOR UPDATE;
""";
                journalLockCmd.Parameters.AddWithValue("workspace_id", workspaceId);
                journalLockCmd.Parameters.AddWithValue("run_id", runId);
                journalLockCmd.Parameters.AddWithValue("request_id", requestId);
                await using var reader = await journalLockCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    journalState = (ToolDispatchState)reader.GetByte(0);
                }
            }

            // 4.1 按 Journal 状态分支：
            // - DispatchingIntent/Dispatched/Reconciling → 推进 Committed 并 UPSERT 结果（愉快/重试路径）；
            // - Committed/ResultDelivered → 幂等判定：既有结果指纹与本次完全一致才允许幂等成功，绝不覆盖；
            // - Prepared/缺失 → 记录标记损坏（Corrupted），不写结果、不终态化、不推进 Run。
            var incomingFingerprint = durableResult.ComputeFingerprint();
            bool writeResult = false;
            switch (journalState)
            {
                case ToolDispatchState.DispatchingIntent:
                case ToolDispatchState.Dispatched:
                case ToolDispatchState.Reconciling:
                    await AdvanceJournalToCommittedAsync(connection, transaction, workspaceId, runId, requestId, now, cancellationToken).ConfigureAwait(false);
                    writeResult = true;
                    break;

                case ToolDispatchState.Committed:
                case ToolDispatchState.ResultDelivered:
                {
                    var existingFingerprint = await ReadResultFingerprintAsync(connection, transaction, requestId, cancellationToken).ConfigureAwait(false);
                    if (existingFingerprint is null
                        || !string.Equals(existingFingerprint, incomingFingerprint, StringComparison.Ordinal))
                    {
                        var reason = existingFingerprint is null
                            ? "Journal 已提交但 Durable Result 缺失，无法幂等确认，记录标记损坏。"
                            : "Journal 已提交且既有结果指纹与本次裁决不一致，拒绝覆盖，记录标记损坏。";
                        var corrupted = await MarkRecordCorruptedAsync(connection, transaction, record, reason, leaseToken, expectedReconciliationVersion, now, cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return new ToolReconciliationResolution
                        {
                            Status = ToolReconciliationResolutionStatus.Corrupted,
                            Record = corrupted
                        };
                    }
                    // 指纹一致 → 幂等成功（复用既有已交付结果，不覆盖）。
                    break;
                }

                case null:
                case ToolDispatchState.Prepared:
                {
                    var reason = journalState is null
                        ? "Journal 行缺失，无法确认裁决前提，记录标记损坏。"
                        : "Journal 状态为 Prepared（外部副作用从未分派），无法裁决，记录标记损坏。";
                    var corrupted = await MarkRecordCorruptedAsync(connection, transaction, record, reason, leaseToken, expectedReconciliationVersion, now, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new ToolReconciliationResolution
                    {
                        Status = ToolReconciliationResolutionStatus.Corrupted,
                        Record = corrupted
                    };
                }
            }

            if (writeResult)
            {
                // 5. Durable Result UPSERT（与 journal / 记录同一事务，写入规范指纹供幂等判定）。
                await UpsertDurableResultAsync(connection, transaction, durableResult, incomingFingerprint, now, cancellationToken).ConfigureAwait(false);
            }

            // 6. 记录终态 + 清除租约（行已锁定，条件校验双重兜底：lease + fencing）。
            var target = outcome.SideEffectOccurred ? ToolReconciliationStatus.Resolved : ToolReconciliationStatus.Rejected;
            await FinalizeRecordTerminalAsync(connection, transaction, record, target, outcome, decisionRequestId, leaseToken, expectedReconciliationVersion, now, cancellationToken).ConfigureAwait(false);

            // 7. Run 状态推进：Run 处于停车状态且无其他未决对账记录 → Queued
            //    （同一事务内完成，杜绝"裁决提交后、进程内重新入队前崩溃"导致的永久停车）。
            if (runIsParking && !await HasOtherUnresolvedAsync(connection, transaction, workspaceId, runId, requestId, cancellationToken).ConfigureAwait(false))
            {
                await AdvanceParkedRunToQueuedAsync(connection, transaction, workspaceId, runId, now, cancellationToken).ConfigureAwait(false);
            }

            // 8. 审计事件追加（ToolReconciliationResolved，与记录终态同一事务；哈希链契约与 AgentRunEventChain 一致）。
            await AppendReconciliationAuditEventAsync(
                connection, transaction, record, target, outcome, auditState ?? AgentRunState.AwaitingReconciliation, now, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var terminal = record with
            {
                Status = target,
                SideEffectOccurred = outcome.SideEffectOccurred,
                Result = outcome.Result,
                Reason = outcome.Error,
                DecisionRequestId = decisionRequestId,
                UpdatedAt = now,
                ResolvedAt = now,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAt = null,
                NextAttemptAt = null,
                LastError = null
            };
            return new ToolReconciliationResolution
            {
                Status = ToolReconciliationResolutionStatus.Resolved,
                Record = terminal
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>按 WorkspaceId+RunId+RequestId 查询既有记录（CreateAsync 幂等返回）。</summary>
    private async Task<ToolReconciliationRecord?> GetByRunAndRequestAsync(
        NpgsqlConnection connection,
        string workspaceId,
        string runId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT {ReadColumns} FROM {Table("tool_reconciliation_entries")}
WHERE workspace_id = @workspace_id AND run_id = @run_id AND request_id = @request_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("request_id", requestId);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<int> RecoverParkedRunsAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return 0;
        }
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = DateTimeOffset.UtcNow;

            // 锁定候选 Run：停车状态（AwaitingReconciliation / ReconciliationRunning）且无任何未决对账记录
            // （Pending / Running）。FOR UPDATE SKIP LOCKED 防止多个 Worker 并发重复恢复。
            var candidates = new List<(string WorkspaceId, string RunId)>();
            await using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandTimeout = Options.CommandTimeoutSeconds;
                selectCmd.CommandText = $"""
SELECT workspace_id, run_id FROM {Table("agent_runs")}
WHERE state IN (@awaiting_reconciliation, @reconciliation_running)
  AND NOT EXISTS (
      SELECT 1 FROM {Table("tool_reconciliation_entries")} e
      WHERE e.workspace_id = {Table("agent_runs")}.workspace_id
        AND e.run_id = {Table("agent_runs")}.run_id
        AND e.status IN (@pending, @running))
LIMIT @limit
FOR UPDATE SKIP LOCKED;
""";
                selectCmd.Parameters.AddWithValue("awaiting_reconciliation", (byte)AgentRunState.AwaitingReconciliation);
                selectCmd.Parameters.AddWithValue("reconciliation_running", (byte)AgentRunState.ReconciliationRunning);
                selectCmd.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
                selectCmd.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
                selectCmd.Parameters.AddWithValue("limit", limit);
                await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            foreach (var (workspaceId, runId) in candidates)
            {
                await AdvanceParkedRunToQueuedAsync(connection, transaction, workspaceId, runId, now, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return candidates.Count;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 原子裁决内推进 Journal 至 Committed：DispatchingIntent/Dispatched → Reconciling → Committed，
    /// Reconciling → Committed（调用方已锁定 Journal 行并确认状态属于可推进集合）。
    /// </summary>
    private async Task AdvanceJournalToCommittedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        string requestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @reconciling, updated_at = @now
WHERE workspace_id = @workspace_id AND run_id = @run_id AND request_id = @request_id
  AND state IN (@dispatching_intent, @dispatched);

UPDATE {Table("tool_dispatch_journal_entries")}
SET state = @committed, updated_at = @now
WHERE workspace_id = @workspace_id AND run_id = @run_id AND request_id = @request_id
  AND state IN (@reconciling, @committed);
""";
        cmd.Parameters.AddWithValue("workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("run_id", runId);
        cmd.Parameters.AddWithValue("request_id", requestId);
        cmd.Parameters.AddWithValue("dispatching_intent", (byte)ToolDispatchState.DispatchingIntent);
        cmd.Parameters.AddWithValue("dispatched", (byte)ToolDispatchState.Dispatched);
        cmd.Parameters.AddWithValue("reconciling", (byte)ToolDispatchState.Reconciling);
        cmd.Parameters.AddWithValue("committed", (byte)ToolDispatchState.Committed);
        cmd.Parameters.AddWithValue("now", now);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>原子裁决内 UPSERT Durable Result（含规范指纹，供幂等判定）。</summary>
    private async Task UpsertDurableResultAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DurableToolResult durableResult,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
INSERT INTO {Table("tool_dispatch_results")} (
    tool_call_id, request_id, workspace_id, run_id, invocation_id, idempotency_key,
    side_effect, external_operation_id, result, succeeded, error, duration_ms, created_at, result_fingerprint)
VALUES (
    @tool_call_id, @request_id, @workspace_id, @run_id, @invocation_id, @idempotency_key,
    @side_effect, @external_operation_id, @result, @succeeded, @error, @duration_ms, @created_at, @result_fingerprint)
ON CONFLICT (request_id) DO UPDATE SET
    tool_call_id = EXCLUDED.tool_call_id,
    workspace_id = EXCLUDED.workspace_id,
    run_id = EXCLUDED.run_id,
    invocation_id = EXCLUDED.invocation_id,
    idempotency_key = EXCLUDED.idempotency_key,
    side_effect = EXCLUDED.side_effect,
    external_operation_id = EXCLUDED.external_operation_id,
    result = EXCLUDED.result,
    succeeded = EXCLUDED.succeeded,
    error = EXCLUDED.error,
    duration_ms = EXCLUDED.duration_ms,
    created_at = EXCLUDED.created_at,
    result_fingerprint = EXCLUDED.result_fingerprint;
""";
        cmd.Parameters.AddWithValue("tool_call_id", durableResult.ToolCallId);
        cmd.Parameters.AddWithValue("request_id", durableResult.RequestId);
        cmd.Parameters.AddWithValue("workspace_id", (object?)durableResult.WorkspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("run_id", (object?)durableResult.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("invocation_id", (object?)durableResult.InvocationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("idempotency_key", (object?)durableResult.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("side_effect", durableResult.SideEffect.ToString());
        cmd.Parameters.AddWithValue("external_operation_id", (object?)durableResult.ExternalOperationId ?? DBNull.Value);
        AddJson(cmd, "result", durableResult);
        cmd.Parameters.AddWithValue("succeeded", durableResult.Succeeded);
        cmd.Parameters.AddWithValue("error", (object?)durableResult.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("duration_ms", (long)Math.Round(durableResult.DurationMs));
        cmd.Parameters.AddWithValue("created_at", now);
        cmd.Parameters.AddWithValue("result_fingerprint", fingerprint);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取既有 Durable Result 的规范指纹（优先列值；旧数据无列值时从 result jsonb 反序列化重算）。
    /// 无结果行或无法反序列化 → null（调用方按损坏处理）。
    /// </summary>
    private async Task<string?> ReadResultFingerprintAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
SELECT result_fingerprint, result FROM {Table("tool_dispatch_results")}
WHERE request_id = @request_id;
""";
        cmd.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (!reader.IsDBNull(0))
        {
            return reader.GetString(0);
        }
        if (!reader.IsDBNull(1))
        {
            try
            {
                return Serializer.Deserialize<DurableToolResult>(reader.GetString(1)).ComputeFingerprint();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 把对账记录标记为损坏（Corrupted）：清除租约与重试指针，写入损坏原因。
    /// 不写结果、不终态化、不推进 Run——由调用方决定是否提交。
    /// </summary>
    private async Task<ToolReconciliationRecord> MarkRecordCorruptedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ToolReconciliationRecord record,
        string reason,
        string leaseToken,
        long expectedReconciliationVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @corrupted,
    last_error = @reason,
    updated_at = @now,
    lease_owner = NULL,
    lease_token = NULL,
    lease_expires_at = NULL,
    next_attempt_at = NULL
WHERE reconciliation_id = @reconciliation_id
  AND lease_token = @lease_token
  AND lease_expires_at > clock_timestamp()
  AND fencing_token = @fencing_token;
""";
        cmd.Parameters.AddWithValue("reconciliation_id", record.ReconciliationId);
        cmd.Parameters.AddWithValue("corrupted", (byte)ToolReconciliationStatus.Corrupted);
        cmd.Parameters.AddWithValue("reason", reason);
        cmd.Parameters.AddWithValue("lease_token", leaseToken);
        cmd.Parameters.AddWithValue("fencing_token", expectedReconciliationVersion);
        cmd.Parameters.AddWithValue("now", now);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"对账记录损坏标记 CAS 未命中（reconciliation_id={record.ReconciliationId}），lease/fencing 校验在锁内失效，整体回滚。");
        }
        return record with
        {
            Status = ToolReconciliationStatus.Corrupted,
            LastError = reason,
            UpdatedAt = now,
            LeaseOwner = null,
            LeaseToken = null,
            LeaseExpiresAt = null,
            NextAttemptAt = null
        };
    }

    /// <summary>记录终态 + 清除租约（行已锁定，条件校验双重兜底：lease + fencing）。</summary>
    private async Task FinalizeRecordTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ToolReconciliationRecord record,
        ToolReconciliationStatus target,
        ToolReconciliationOutcome outcome,
        string? decisionRequestId,
        string leaseToken,
        long expectedReconciliationVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
UPDATE {Table("tool_reconciliation_entries")}
SET status = @target,
    result = @result,
    side_effect_occurred = @side_effect_occurred,
    reason = @reason,
    decision_request_id = @decision_request_id,
    updated_at = @now,
    resolved_at = @now,
    lease_owner = NULL,
    lease_token = NULL,
    lease_expires_at = NULL,
    next_attempt_at = NULL,
    last_error = NULL
WHERE reconciliation_id = @reconciliation_id
  AND lease_token = @lease_token
  AND lease_expires_at > clock_timestamp()
  AND fencing_token = @fencing_token;
""";
        cmd.Parameters.AddWithValue("reconciliation_id", record.ReconciliationId);
        cmd.Parameters.AddWithValue("target", (byte)target);
        cmd.Parameters.AddWithValue("result", (object?)outcome.Result ?? DBNull.Value);
        cmd.Parameters.AddWithValue("side_effect_occurred", outcome.SideEffectOccurred);
        cmd.Parameters.AddWithValue("reason", (object?)outcome.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("decision_request_id", (object?)decisionRequestId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lease_token", leaseToken);
        cmd.Parameters.AddWithValue("fencing_token", expectedReconciliationVersion);
        cmd.Parameters.AddWithValue("now", now);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            // 行已锁定且预读校验通过，0 行 = 校验条件与预读不一致（版本/租约在锁内被外部篡改），fail-closed。
            throw new InvalidOperationException(
                $"对账原子裁决失败：记录终态 CAS 未命中（reconciliation_id={record.ReconciliationId}），" +
                $"lease/fencing 校验在锁内失效，整体回滚。");
        }
    }

    /// <summary>是否存在该 Run 的其他未决对账记录（Pending/Running；排除当前记录）。</summary>
    private async Task<bool> HasOtherUnresolvedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
SELECT EXISTS (
    SELECT 1 FROM {Table("tool_reconciliation_entries")}
    WHERE workspace_id = @workspace_id AND run_id = @run_id
      AND request_id != @request_id
      AND status IN (@pending, @running));
""";
        cmd.Parameters.AddWithValue("workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("run_id", runId);
        cmd.Parameters.AddWithValue("request_id", requestId);
        cmd.Parameters.AddWithValue("pending", (byte)ToolReconciliationStatus.Pending);
        cmd.Parameters.AddWithValue("running", (byte)ToolReconciliationStatus.Running);
        var exists = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return exists is not null && (bool)exists;
    }

    /// <summary>把停车状态的 Run 推进为 Queued（同一事务；调用方已锁定 run 行）。</summary>
    private async Task AdvanceParkedRunToQueuedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string workspaceId,
        string runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandTimeout = Options.CommandTimeoutSeconds;
        cmd.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = @queued, updated_at = @updated_at,
    data = data || jsonb_build_object('State', to_jsonb(@state_name), 'UpdatedAt', to_jsonb(@updated_at))
WHERE workspace_id = @workspace_id AND run_id = @run_id
  AND state IN (@awaiting_reconciliation, @reconciliation_running);
""";
        cmd.Parameters.AddWithValue("workspace_id", workspaceId);
        cmd.Parameters.AddWithValue("run_id", runId);
        cmd.Parameters.AddWithValue("queued", (byte)AgentRunState.Queued);
        cmd.Parameters.AddWithValue("state_name", AgentRunState.Queued.ToString());
        cmd.Parameters.AddWithValue("awaiting_reconciliation", (byte)AgentRunState.AwaitingReconciliation);
        cmd.Parameters.AddWithValue("reconciliation_running", (byte)AgentRunState.ReconciliationRunning);
        cmd.Parameters.AddWithValue("updated_at", now);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取单条记录（0 行 → null）。</summary>
    private static async Task<ToolReconciliationRecord?> ReadSingleAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return ReadRecord(reader);
    }

    /// <summary>读取多条记录（按查询 ORDER BY）。</summary>
    private static async Task<IReadOnlyList<ToolReconciliationRecord>> ReadManyAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var results = new List<ToolReconciliationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRecord(reader));
        }
        return results;
    }

    /// <summary>从规范化列构造 ToolReconciliationRecord（与 ReadColumns 顺序一致）。</summary>
    private static ToolReconciliationRecord ReadRecord(NpgsqlDataReader reader)
    {
        static string? NullableStr(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        static DateTimeOffset? NullableTime(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

        return new ToolReconciliationRecord
        {
            ReconciliationId = reader.GetString(0),
            RunId = reader.GetString(1),
            WorkspaceId = reader.GetString(2),
            RequestId = reader.GetString(3),
            ToolName = reader.GetString(4),
            ExternalOperationId = NullableStr(reader, 5),
            ReconciliationHandler = NullableStr(reader, 6),
            Status = (ToolReconciliationStatus)reader.GetInt16(7),
            Result = NullableStr(reader, 8),
            SideEffectOccurred = reader.IsDBNull(9) ? null : reader.GetBoolean(9),
            Reason = NullableStr(reader, 10),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
            UpdatedAt = NullableTime(reader, 12),
            ResolvedAt = NullableTime(reader, 13),
            DeadlineUtc = NullableTime(reader, 14),
            LeaseOwner = NullableStr(reader, 15),
            LeaseToken = NullableStr(reader, 16),
            LeaseExpiresAt = NullableTime(reader, 17),
            FencingToken = reader.GetInt64(18),
            AttemptCount = reader.GetInt32(19),
            NextAttemptAt = NullableTime(reader, 20),
            LastError = NullableStr(reader, 21),
            DecisionRequestId = NullableStr(reader, 22)
        };
    }

    /// <summary>
    /// 在原子裁决事务内追加 ToolReconciliationResolved 审计事件。
    /// 沿用事件哈希链契约（Sequence = 最后事件 + 1，PrevChainHash = 最后事件 ContentHash）；
    /// Storage.Postgres 不引用 Core，这里以与 <c>AgentRunEventChain.EventHashDto</c> 完全一致的
    /// 形状重算 SHA-256（默认 System.Text.Json 序列化，PascalCase + 枚举数值）。
    /// </summary>
    private async Task AppendReconciliationAuditEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ToolReconciliationRecord record,
        ToolReconciliationStatus terminalStatus,
        ToolReconciliationOutcome outcome,
        AgentRunState auditState,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int expectedSequence;
        string? expectedPrevHash;
        await using (var lastEventCmd = connection.CreateCommand())
        {
            lastEventCmd.Transaction = transaction;
            lastEventCmd.CommandTimeout = Options.CommandTimeoutSeconds;
            lastEventCmd.CommandText = $"""
SELECT sequence, content_hash
FROM {Table("agent_run_events")}
WHERE workspace_id = @workspace_id AND run_id = @run_id
ORDER BY sequence DESC
LIMIT 1;
""";
            lastEventCmd.Parameters.AddWithValue("workspace_id", record.WorkspaceId);
            lastEventCmd.Parameters.AddWithValue("run_id", record.RunId);
            await using var reader = await lastEventCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expectedSequence = reader.GetInt32(0) + 1;
                expectedPrevHash = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
            else
            {
                expectedSequence = 0;
                expectedPrevHash = null;
            }
        }

        var payload = JsonSerializer.Serialize(new
        {
            ReconciliationId = record.ReconciliationId,
            RequestId = record.RequestId,
            SideEffectOccurred = outcome.SideEffectOccurred,
            Result = outcome.Result,
            Error = outcome.Error
        });
        var auditEvent = BuildAuditEvent(record.RunId, record.WorkspaceId, expectedSequence, auditState, payload, expectedPrevHash, now);

        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandTimeout = Options.CommandTimeoutSeconds;
        insertCmd.CommandText = $"""
INSERT INTO {Table("agent_run_events")} (
    event_id, workspace_id, run_id, sequence,
    event_type, state, payload, content_hash, prev_chain_hash,
    occurred_at, data)
VALUES (
    @event_id, @workspace_id, @run_id, @sequence,
    @event_type, @state, @payload, @content_hash, @prev_chain_hash,
    @occurred_at, @data)
ON CONFLICT (workspace_id, run_id, sequence) DO NOTHING;
""";
        insertCmd.Parameters.AddWithValue("event_id", auditEvent.EventId);
        insertCmd.Parameters.AddWithValue("workspace_id", record.WorkspaceId);
        insertCmd.Parameters.AddWithValue("run_id", record.RunId);
        insertCmd.Parameters.AddWithValue("sequence", auditEvent.Sequence);
        insertCmd.Parameters.AddWithValue("event_type", (short)auditEvent.EventType);
        insertCmd.Parameters.AddWithValue("state", (short)auditEvent.State);
        insertCmd.Parameters.AddWithValue("payload", auditEvent.Payload ?? string.Empty);
        insertCmd.Parameters.AddWithValue("content_hash", (object?)auditEvent.ContentHash ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("prev_chain_hash", (object?)auditEvent.PrevChainHash ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("occurred_at", auditEvent.OccurredAt);
        AddJson(insertCmd, "data", auditEvent);

        var affected = await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            // Run 行已被本事务锁定，Sequence 冲突不应发生；发生即审计链异常，fail-closed 回滚。
            throw new InvalidOperationException(
                $"对账审计事件 Sequence 冲突：workspace_id={record.WorkspaceId}, run_id={record.RunId}, sequence={expectedSequence} 已存在。");
        }
    }

    /// <summary>构建带 ContentHash 的审计事件（哈希链契约与 <c>AgentRunEventChain.BuildEvent</c> 一致）。</summary>
    private static AgentRunEvent BuildAuditEvent(
        string runId,
        string workspaceId,
        int sequence,
        AgentRunState state,
        string payload,
        string? prevChainHash,
        DateTimeOffset occurredAt)
    {
        var temp = new AgentRunEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            RunId = runId,
            WorkspaceId = workspaceId,
            Sequence = sequence,
            EventType = AgentRunEventType.ToolReconciliationResolved,
            State = state,
            Payload = payload,
            ContentHash = null,
            PrevChainHash = prevChainHash,
            OccurredAt = occurredAt
        };
        var contentHash = ComputeContentHash(temp);
        return temp with { ContentHash = contentHash };
    }

    /// <summary>
    /// 计算事件 ContentHash（SHA-256 小写 hex）。ContentHash 字段不参与计算；
    /// 序列化形状必须与 <c>AgentRunEventChain.EventHashDto</c> 完全一致（默认 System.Text.Json）。
    /// </summary>
    private static string ComputeContentHash(AgentRunEvent @event)
    {
        var dto = new EventHashDto
        {
            EventId = @event.EventId,
            RunId = @event.RunId,
            WorkspaceId = @event.WorkspaceId,
            Sequence = @event.Sequence,
            EventType = @event.EventType,
            State = @event.State,
            Payload = @event.Payload,
            PrevChainHash = @event.PrevChainHash,
            OccurredAt = @event.OccurredAt
            // ContentHash 显式不参与
        };
        var json = JsonSerializer.Serialize(dto);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>哈希计算用 DTO（与 <c>AgentRunEventChain.EventHashDto</c> 同形：PascalCase + 枚举数值）。</summary>
    private sealed record EventHashDto
    {
        public required string EventId { get; init; }
        public required string RunId { get; init; }
        public required string WorkspaceId { get; init; }
        public required int Sequence { get; init; }
        public required AgentRunEventType EventType { get; init; }
        public required AgentRunState State { get; init; }
        public required string Payload { get; init; }
        public string? PrevChainHash { get; init; }
        public required DateTimeOffset OccurredAt { get; init; }
    }

    /// <summary>构造 ListAsync 过滤 WHERE 子句（无参数注入；参数由 AddQueryParameters 统一添加）。</summary>
    private static string BuildWhereClause(ReconciliationQuery query, bool includeOverduePredicate)
    {
        var clauses = new List<string> { "1 = 1" };

        if (!string.IsNullOrEmpty(query.WorkspaceId))
        {
            clauses.Add("workspace_id = @workspace_id");
        }
        if (!string.IsNullOrEmpty(query.RunId))
        {
            clauses.Add("run_id = @run_id");
        }
        if (query.Status.HasValue)
        {
            clauses.Add("status = @status");
        }
        if (includeOverduePredicate)
        {
            clauses.Add("deadline_utc IS NOT NULL AND deadline_utc < now()");
            clauses.Add($"status IN ({(byte)ToolReconciliationStatus.Pending}, {(byte)ToolReconciliationStatus.Running})");
        }

        return string.Join(" AND ", clauses);
    }

    /// <summary>为 ListAsync 添加过滤参数（与 BuildWhereClause 保持一致）。</summary>
    private static void AddQueryParameters(NpgsqlCommand command, ReconciliationQuery query)
    {
        if (!string.IsNullOrEmpty(query.WorkspaceId))
        {
            command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);
        }
        if (!string.IsNullOrEmpty(query.RunId))
        {
            command.Parameters.AddWithValue("run_id", query.RunId);
        }
        if (query.Status.HasValue)
        {
            command.Parameters.AddWithValue("status", (byte)query.Status.Value);
        }
    }
}
