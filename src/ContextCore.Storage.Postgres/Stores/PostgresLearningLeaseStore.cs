using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Learning Materialization worker 池级租约实现。
/// </summary>
/// <remarks>
/// <b>租约模型</b>（每个 lease_id 至多一行），复用 <see cref="PostgresAgentRunLease"/> 模式：
/// <code>
/// TryAcquireAsync:
/// INSERT INTO learning_leases (lease_id, lease_owner, lease_token, acquired_at, lease_expires_at)
/// VALUES (...)
/// ON CONFLICT (lease_id) DO UPDATE
/// SET lease_owner = EXCLUDED.lease_owner, lease_token = EXCLUDED.lease_token, ...
/// WHERE learning_leases.lease_expires_at &lt; now
/// RETURNING lease_token;
/// - 无现有行 → INSERT 成功，返回 token
/// - 现有行过期 → ON CONFLICT DO UPDATE WHERE 子句命中，更新并返回 token
/// - 现有行未过期 → ON CONFLICT DO UPDATE WHERE 子句不命中，0 行返回，返回 null
/// </code>
///
/// <b>RenewAsync</b>：UPDATE WHERE lease_token = @token AND lease_expires_at &gt; clock_timestamp()。
/// <b>ReleaseAsync</b>：DELETE WHERE lease_token = @token（主动让出）。
/// <b>ReapExpiredAsync</b>：DELETE WHERE lease_expires_at &lt; now（崩溃实例持有的过期租约最终释放）。
/// </remarks>
public sealed class PostgresLearningLeaseStore : PostgresStoreBase, ILearningLeaseStore
{
    /// <summary>初始化 PostgreSQL Learning Lease 存储。</summary>
    public PostgresLearningLeaseStore(
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
    public async ValueTask<LearningLease?> TryAcquireAsync(
        string leaseId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
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
INSERT INTO {Table("learning_leases")} (lease_id, lease_owner, lease_token, acquired_at, lease_expires_at)
VALUES (@lease_id, @owner, @token, @now, @expires_at)
ON CONFLICT (lease_id) DO UPDATE
SET lease_owner = EXCLUDED.lease_owner,
    lease_token = EXCLUDED.lease_token,
    acquired_at = EXCLUDED.acquired_at,
    lease_expires_at = EXCLUDED.lease_expires_at
WHERE {Table("learning_leases")}.lease_expires_at < @now
RETURNING lease_token;
""";
        command.Parameters.AddWithValue("lease_id", leaseId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires_at", expiresAt);

        // RETURNING 返回 lease_token；0 行返回 = 已被其他实例持有。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var returnedToken = reader.GetString(0);
        return new LearningLease
        {
            LeaseId = leaseId,
            Owner = owner,
            LeaseToken = returnedToken,
            AcquiredAt = now,
            ExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 续租约（心跳）：UPDATE WHERE lease_token = @token AND lease_expires_at &gt; clock_timestamp()。
    /// 0 行受影响表示租约已被抢占或已过期，返回 false；调用方应立即停止处理。
    /// 过期检查防止 stale 实例续租已过期的租约（fencing 安全边界）。
    /// </remarks>
    public async ValueTask<bool> RenewAsync(
        string leaseId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "续约时间必须为正。");
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        var newExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("learning_leases")}
SET lease_expires_at = @new_expires_at
WHERE lease_id = @lease_id
  AND lease_token = @token
  AND lease_expires_at > clock_timestamp();
""";
        command.Parameters.AddWithValue("lease_id", leaseId);
        command.Parameters.AddWithValue("token", leaseToken);
        command.Parameters.AddWithValue("new_expires_at", newExpiresAt);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 释放租约（主动让出）：DELETE WHERE lease_token = @token。
    /// 0 行受影响不抛异常。
    /// </remarks>
    public async ValueTask<bool> ReleaseAsync(
        string leaseId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("learning_leases")}
WHERE lease_id = @lease_id
  AND lease_token = @token;
""";
        command.Parameters.AddWithValue("lease_id", leaseId);
        command.Parameters.AddWithValue("token", leaseToken);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 清理所有已过期租约（崩溃实例持有的过期租约最终释放）。
    /// </remarks>
    public async ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("learning_leases")}
WHERE lease_expires_at < clock_timestamp();
""";
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    /// <inheritdoc />
    public async ValueTask<bool> HasActiveLeaseAsync(string leaseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT 1
FROM {Table("learning_leases")}
WHERE lease_id = @lease_id
  AND lease_expires_at > clock_timestamp()
LIMIT 1;
""";
        command.Parameters.AddWithValue("lease_id", leaseId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }
}
