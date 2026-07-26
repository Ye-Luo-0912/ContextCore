using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 任务 D：PostgreSQL 持久化 Canary Leader 租约实现。
/// </summary>
/// <remarks>
/// 确保 <see cref="ContextCore.Core.Services.Evolution.CanaryProgressionHostedService"/>
/// 同一时刻仅一个实例处理同一 run，避免多实例同时推进/回滚同一 Canary。
///
/// <b>租约模型</b>（每个 run_id 至多一条行）：
/// <code>
/// TryAcquireAsync:
///   INSERT INTO canary_leader_leases (run_id, owner, lease_token, acquired_at, lease_expires_at)
///   VALUES (...)
///   ON CONFLICT (run_id) DO UPDATE
///     SET owner = EXCLUDED.owner, lease_token = EXCLUDED.lease_token, ...
///     WHERE canary_leader_leases.lease_expires_at &lt; now
///   RETURNING lease_token;
///   - 无现有行 → INSERT 成功，返回 token
///   - 现有行过期 → ON CONFLICT DO UPDATE WHERE 子句命中，更新并返回 token
///   - 现有行未过期 → ON CONFLICT DO UPDATE WHERE 子句不命中，0 行返回，返回 null
/// </code>
///
/// <b>RenewAsync</b>：UPDATE WHERE lease_token = @token，延长 lease_expires_at。
/// <b>ReleaseAsync</b>：DELETE WHERE lease_token = @token（主动让出）。
/// <b>ReapExpiredAsync</b>：DELETE WHERE lease_expires_at &lt; now（崩溃 leader 持有的过期租约最终释放）。
///
/// 复用 P0-1/P0-2 的租约模式（CAS + token 匹配），但状态机更简单：
/// leader 租约无需 Pending → Leased → Acked 流转，只有 "持有" 与 "未持有" 两个状态。
/// </remarks>
public sealed class PostgresCanaryLeaderLease : PostgresStoreBase, ICanaryLeaderLease
{
    /// <summary>初始化 PostgreSQL Canary Leader 租约存储。</summary>
    public PostgresCanaryLeaderLease(
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
    /// </remarks>
    public async ValueTask<LeasedLeadership?> TryAcquireAsync(
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
        command.CommandText = $"""
INSERT INTO {Table("canary_leader_leases")} (run_id, owner, lease_token, acquired_at, lease_expires_at)
VALUES (@run_id, @owner, @token, @now, @expires_at)
ON CONFLICT (run_id) DO UPDATE
SET owner = EXCLUDED.owner,
    lease_token = EXCLUDED.lease_token,
    acquired_at = EXCLUDED.acquired_at,
    lease_expires_at = EXCLUDED.lease_expires_at
WHERE {Table("canary_leader_leases")}.lease_expires_at < @now
RETURNING lease_token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // 0 行返回（ON CONFLICT WHERE 不命中）→ 已被其他实例持有，返回 null
        if (result is null or DBNull)
        {
            return null;
        }

        return new LeasedLeadership
        {
            RunId = runId,
            LeaseToken = token,
            Owner = owner,
            ExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 续租约（leader 心跳）：UPDATE WHERE lease_token = @token，延长 lease_expires_at。
    /// 0 行受影响表示租约已被抢占或过期释放，返回 false；调用方应立即停止处理该 run。
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
UPDATE {Table("canary_leader_leases")}
SET lease_expires_at = @new_expires_at
WHERE run_id = @run_id
  AND lease_token = @token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 释放租约（主动让出 leader）：DELETE WHERE lease_token = @token。
    /// 通常在 run 完成（Promoted）或回滚后调用。0 行受影响不抛异常（租约可能已过期被 reaper 释放）。
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
DELETE FROM {Table("canary_leader_leases")}
WHERE run_id = @run_id
  AND lease_token = @token;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("token", leaseToken);

        // 0 行受影响不抛异常：租约可能已过期被 reaper 释放，或已被其他实例抢占。
        // ReleaseAsync 是"尽力让出"语义，调用方不关心是否真正由自己释放。
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 回收过期租约（后台清理）：DELETE WHERE lease_expires_at &lt; now。
    /// 应由定时任务（如 <c>CanaryLeaderHostedService</c>）周期性调用，
    /// 确保崩溃 leader 持有的过期租约最终被释放，让其他实例可以重新获取租约。
    /// </remarks>
    public async ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("canary_leader_leases")}
WHERE lease_expires_at < @now;
""";
        command.Parameters.AddWithValue("now", now);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
