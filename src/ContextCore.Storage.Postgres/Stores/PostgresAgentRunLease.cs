using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 运行时能力补齐：PostgreSQL 持久化 Agent Run 租约实现。
/// 让 HA 场景下同一时刻仅一个 Host 实例处理同一 Run。
/// </summary>
/// <remarks>
/// <b>租约模型</b>（每个 run_id 至多一条行），复用 <see cref="PostgresCanaryLeaderLease"/> 模式：
/// <code>
/// TryAcquireAsync:
///   INSERT INTO agent_run_leases (run_id, owner, lease_token, acquired_at, lease_expires_at)
///   VALUES (...)
///   ON CONFLICT (run_id) DO UPDATE
///     SET owner = EXCLUDED.owner, lease_token = EXCLUDED.lease_token, ...
///     WHERE agent_run_leases.lease_expires_at &lt; now
///   RETURNING lease_token;
///   - 无现有行 → INSERT 成功，返回 token
///   - 现有行过期 → ON CONFLICT DO UPDATE WHERE 子句命中，更新并返回 token
///   - 现有行未过期 → ON CONFLICT DO UPDATE WHERE 子句不命中，0 行返回，返回 null
/// </code>
///
/// <b>RenewAsync</b>：UPDATE WHERE lease_token = @token，延长 lease_expires_at。
/// <b>ReleaseAsync</b>：DELETE WHERE lease_token = @token（主动让出）。
/// <b>ReapExpiredAsync</b>：DELETE WHERE lease_expires_at &lt; now（崩溃实例持有的过期租约最终释放）。
/// </remarks>
public sealed class PostgresAgentRunLease : PostgresStoreBase, IAgentRunLease
{
    /// <summary>初始化 PostgreSQL Agent Run 租约存储。</summary>
    public PostgresAgentRunLease(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// 原子 CAS 获取租约：使用 <c>INSERT ... ON CONFLICT DO UPDATE WHERE lease_expires_at &lt; now</c>。
    /// 无现有行或现有行过期时获取成功；现有行未过期时返回 null（已被其他实例持有）。
    /// P0-4：成功获取时 <c>fencing_token = 旧值 + 1</c>（新插入为 1），RETURNING 返回新的 fencing_token，
    /// 供调用方在副作用 UPDATE 的 WHERE 子句中校验。续约（RenewAsync）不递增 fencing_token。
    /// </remarks>
    public async ValueTask<LeasedAgentRun?> TryAcquireAsync(
        string runId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var token = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(leaseDuration);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // P0-4：fencing_token 在 ON CONFLICT DO UPDATE 时 = agent_run_leases.fencing_token + 1（抢占过期），
        // 新插入时 = 1（VALUES 中指定）。RETURNING 同时返回 lease_token 与 fencing_token 以便调用方使用。
        command.CommandText = $"""
INSERT INTO {Table("agent_run_leases")} (run_id, owner, lease_token, fencing_token, acquired_at, lease_expires_at)
VALUES (@run_id, @owner, @token, 1, @now, @expires_at)
ON CONFLICT (run_id) DO UPDATE
SET owner = EXCLUDED.owner,
    lease_token = EXCLUDED.lease_token,
    fencing_token = {Table("agent_run_leases")}.fencing_token + 1,
    acquired_at = EXCLUDED.acquired_at,
    lease_expires_at = EXCLUDED.lease_expires_at
WHERE {Table("agent_run_leases")}.lease_expires_at < @now
RETURNING lease_token, fencing_token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);

        // P0-4：RETURNING 返回两列（lease_token, fencing_token）；0 行返回 = 已被其他实例持有。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // 0 行返回（ON CONFLICT WHERE 不命中）→ 已被其他实例持有，返回 null
            return null;
        }

        // RETURNING 的 lease_token 应与本次生成的 token 一致（INSERT 路径）或被 ON CONFLICT 设为 EXCLUDED（UPDATE 路径）；
        // 两种路径下 lease_token 都等于 @token，可直接使用本地变量。
        var returnedToken = reader.GetString(0);
        var fencingToken = reader.GetInt64(1);

        return new LeasedAgentRun
        {
            RunId = runId,
            LeaseToken = returnedToken,
            Owner = owner,
            ExpiresAt = expiresAt,
            FencingToken = fencingToken
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 续租约（心跳）：UPDATE WHERE lease_token = @token AND lease_expires_at > now()，延长 lease_expires_at。
    /// 0 行受影响表示租约已被抢占或已过期，返回 false；调用方应立即停止处理该 run。
    /// 过期检查防止 stale 实例续租已过期的租约（fencing 安全边界）。
    /// </remarks>
    public async ValueTask<bool> RenewAsync(
        string runId,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "续租时间必须为正。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var newExpiresAt = DateTimeOffset.UtcNow.Add(extension);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("agent_run_leases")}
SET lease_expires_at = @new_expires_at
WHERE run_id = @run_id
  AND lease_token = @token
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 释放租约（主动让出）：DELETE WHERE lease_token = @token。
    /// 通常在 run 完成（Completed/Failed/Cancelled）后调用。0 行受影响不抛异常。
    /// </remarks>
    public async ValueTask ReleaseAsync(
        string runId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("agent_run_leases")}
WHERE run_id = @run_id
  AND lease_token = @token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("token", leaseToken);

        // 0 行受影响不抛异常：租约可能已过期被 reaper 释放，或已被其他实例抢占。
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 回收过期租约（后台清理）：DELETE WHERE lease_expires_at &lt; now。
    /// 应由定时任务（如 <c>LeaseReaperService</c>）周期性调用，
    /// 确保崩溃实例持有的过期租约最终被释放，让其他实例可以重新获取租约。
    /// </remarks>
    public async ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("agent_run_leases")}
WHERE lease_expires_at < @now;
""";
        command.Parameters.AddWithValue("now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-6：查询是否存在未过期租约：SELECT 1 WHERE lease_expires_at &gt;= now LIMIT 1。
    /// 用于 Recovery Worker 在标记 Run 为 Failed 前校验是否有活跃 Owner。
    /// </remarks>
    public async ValueTask<bool> HasActiveLeaseAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT 1 FROM {Table("agent_run_leases")}
WHERE run_id = @run_id AND lease_expires_at >= @now
LIMIT 1;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("now", now);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-7：单 SQL 原子操作 — 只有无活跃租约 AND 状态匹配时才更新为 Failed。
    /// 消除 Recovery Worker 中 HasActiveLeaseAsync + TransitionStateAsync 的 check-then-act 竞态。
    /// NOT EXISTS 子查询检查活跃租约（lease_expires_at >= clock_timestamp()）。
    /// 同步更新 data JSON 中的 State / UpdatedAt / FinishedAt（与 TransitionStateAsync 一致）。
    /// </remarks>
    public async ValueTask<int> MarkFailedIfLeaseExpiredAsync(
        string workspaceId,
        string runId,
        AgentRunState expectedCurrentState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("agent_runs")}
SET state = @failed_state,
    updated_at = @updated_at,
    finished_at = @finished_at,
    data = data || jsonb_build_object('State', to_jsonb(@failed_state_name), 'UpdatedAt', to_jsonb(@updated_at), 'FinishedAt', to_jsonb(@finished_at))
WHERE workspace_id = @workspace_id
  AND run_id = @run_id
  AND state = @expected_state
  AND NOT EXISTS (
      SELECT 1 FROM {Table("agent_run_leases")}
      WHERE run_id = @run_id
        AND lease_expires_at >= clock_timestamp()
  );
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("expected_state", (byte)expectedCurrentState);
        command.Parameters.AddWithValue("failed_state", (byte)AgentRunState.Failed);
        command.Parameters.AddWithValue("failed_state_name", AgentRunState.Failed.ToString());
        command.Parameters.AddWithValue("updated_at", now);
        command.Parameters.AddWithValue("finished_at", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
