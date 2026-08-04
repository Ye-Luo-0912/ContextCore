using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化 Model Node Membership Store（P0-15）。
/// </summary>
/// <remarks>
/// 每 node_id 一行，维护节点成员资格租约：
/// <list type="bullet">
/// <item>领取/续租单语句原子完成：INSERT ... ON CONFLICT DO UPDATE 带 WHERE——仅当旧租约已过期
/// 或同一 instance_id 时允许覆盖（被其他活跃实例持有时不返回行 → 调用方退避重试）。</item>
/// <item>租约过期即 stale cutoff：<see cref="GetActiveMembersAsync"/> 用
/// <c>lease_expires_at &gt; clock_timestamp()</c> 过滤（clock_timestamp 而非 now，
/// 确保基于真实当前时间而非事务开始时间）。</item>
/// <item><see cref="SetServingEnabledAsync"/> 校验 lease_token + 租约未过期（fencing）。</item>
/// </list>
/// 每次领取/续租生成新令牌：调用方以最新返回的令牌为准（续租后旧令牌失效，防止过期持有者篡改）。
/// </remarks>
public sealed class PostgresModelNodeMembershipStore : PostgresStoreBase, IModelNodeMembershipStore
{
    public PostgresModelNodeMembershipStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask<ModelNodeMembership?> TryAcquireOrRenewLeaseAsync(
        string nodeId,
        string instanceId,
        TimeSpan leaseDuration,
        bool servingEnabled,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("model_node_membership")} (node_id, instance_id, lease_token, lease_expires_at, last_heartbeat, serving_enabled)
VALUES (@node_id, @instance_id, @lease_token, @lease_expires_at, now(), @serving_enabled)
ON CONFLICT (node_id) DO UPDATE
SET instance_id = EXCLUDED.instance_id,
    lease_token = EXCLUDED.lease_token,
    lease_expires_at = EXCLUDED.lease_expires_at,
    last_heartbeat = now(),
    serving_enabled = EXCLUDED.serving_enabled
WHERE {Table("model_node_membership")}.lease_expires_at <= now()
   OR {Table("model_node_membership")}.instance_id = EXCLUDED.instance_id
RETURNING node_id, instance_id, lease_token, lease_expires_at, last_heartbeat, serving_enabled;
""";
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("lease_token", NewToken());
        command.Parameters.AddWithValue("lease_expires_at", DateTimeOffset.UtcNow + leaseDuration);
        command.Parameters.AddWithValue("serving_enabled", servingEnabled);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadMembership(reader);
        }

        // 无 RETURNING 行：被其他活跃实例持有（租约未过期且 instance_id 不同）。
        return null;
    }

    public async ValueTask<ModelNodeMembership?> GetAsync(string nodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT node_id, instance_id, lease_token, lease_expires_at, last_heartbeat, serving_enabled
FROM {Table("model_node_membership")}
WHERE node_id = @node_id;
""";
        command.Parameters.AddWithValue("node_id", nodeId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadMembership(reader);
    }

    public async ValueTask<IReadOnlyList<ModelNodeMembership>> GetActiveMembersAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT node_id, instance_id, lease_token, lease_expires_at, last_heartbeat, serving_enabled
FROM {Table("model_node_membership")}
WHERE lease_expires_at > clock_timestamp()
ORDER BY node_id;
""";

        var results = new List<ModelNodeMembership>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadMembership(reader));
        }
        return results;
    }

    public async ValueTask<bool> SetServingEnabledAsync(
        string nodeId,
        string instanceId,
        string leaseToken,
        bool servingEnabled,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 校验 lease_token + 租约未过期（fencing：过期持有者不得篡改状态）。
        command.CommandText = $"""
UPDATE {Table("model_node_membership")}
SET serving_enabled = @serving_enabled, last_heartbeat = now()
WHERE node_id = @node_id
  AND instance_id = @instance_id
  AND lease_token = @lease_token
  AND lease_expires_at > clock_timestamp()
RETURNING node_id;
""";
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("lease_token", leaseToken);
        command.Parameters.AddWithValue("serving_enabled", servingEnabled);

        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static ModelNodeMembership ReadMembership(System.Data.Common.DbDataReader reader)
    {
        var nodeIdOrdinal = reader.GetOrdinal("node_id");
        var instanceIdOrdinal = reader.GetOrdinal("instance_id");
        var leaseTokenOrdinal = reader.GetOrdinal("lease_token");
        var leaseExpiresAtOrdinal = reader.GetOrdinal("lease_expires_at");
        var lastHeartbeatOrdinal = reader.GetOrdinal("last_heartbeat");
        var servingEnabledOrdinal = reader.GetOrdinal("serving_enabled");

        return new ModelNodeMembership
        {
            NodeId = reader.GetString(nodeIdOrdinal),
            InstanceId = reader.GetString(instanceIdOrdinal),
            LeaseToken = reader.GetString(leaseTokenOrdinal),
            LeaseExpiresAt = reader.GetFieldValue<DateTimeOffset>(leaseExpiresAtOrdinal),
            LastHeartbeat = reader.GetFieldValue<DateTimeOffset>(lastHeartbeatOrdinal),
            ServingEnabled = reader.GetBoolean(servingEnabledOrdinal)
        };
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");
}
