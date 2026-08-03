using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 持久化集群级 Canary 紧急覆盖（Kill Switch）实现。
/// </summary>
/// <remarks>
/// 以 <c>canary_emergency_overrides</c> 表（run_id 主键，每 run 至多一行）承载覆盖记录：
/// <list type="bullet">
/// <item><c>TrySetOverrideAsync</c>：<c>INSERT ... ON CONFLICT (run_id) DO UPDATE WHERE cleared_at IS NOT NULL</c>，
/// 已存在活跃覆盖时不覆盖并返回 false；已清除的历史覆盖被新覆盖替换；新 run 直接插入。</item>
/// <item><c>TryClearOverrideAsync</c>：<c>UPDATE ... WHERE run_id = @run_id AND cleared_at IS NULL</c>，
/// 仅清除活跃覆盖，返回是否真正生效。</item>
/// <item>活跃语义：<c>cleared_at IS NULL</c>；配合部分唯一索引
/// <c>(run_id) WHERE cleared_at IS NULL</c>，数据库层保证同一 run 至多一条活跃覆盖。</item>
/// </list>
/// 路由层（AuthoritativeRetrievalRuntime / AuthoritativePackageRuntime）在 canary 命中 V2 时
/// 先检查本表，存在活跃覆盖则强制回退 V1；CanaryProgressionService 恢复时同样优先本表。
/// </remarks>
public sealed class PostgresCanaryEmergencyOverrideStore : PostgresStoreBase, ICanaryEmergencyOverrideStore
{
    /// <summary>初始化 PostgreSQL Canary 紧急覆盖存储。</summary>
    public PostgresCanaryEmergencyOverrideStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <inheritdoc />
    public async ValueTask<CanaryEmergencyOverride?> GetActiveAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT run_id, reason, operator_name, created_at, cleared_at, cleared_by
FROM {Table("canary_emergency_overrides")}
WHERE run_id = @run_id AND cleared_at IS NULL
LIMIT 1;
""";
        command.Parameters.AddWithValue("run_id", runId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadOverride(reader);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT run_id, reason, operator_name, created_at, cleared_at, cleared_by
FROM {Table("canary_emergency_overrides")}
WHERE cleared_at IS NULL
ORDER BY created_at;
""";

        var results = new List<CanaryEmergencyOverride>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadOverride(reader));
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TrySetOverrideAsync(
        string runId,
        string reason,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        // 仅当不存在活跃覆盖时写入：无现有行 → INSERT 生效；现有行已清除 → WHERE 命中并替换；
        // 现有行仍活跃 → WHERE 不命中，0 行返回 false（不覆盖、不报错）。
        command.CommandText = $"""
INSERT INTO {Table("canary_emergency_overrides")} (run_id, reason, operator_name, created_at)
VALUES (@run_id, @reason, @operator_name, now())
ON CONFLICT (run_id) DO UPDATE
SET reason = EXCLUDED.reason,
    operator_name = EXCLUDED.operator_name,
    created_at = EXCLUDED.created_at,
    cleared_at = NULL,
    cleared_by = NULL
WHERE {Table("canary_emergency_overrides")}.cleared_at IS NOT NULL
RETURNING run_id;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("operator_name", operatorName);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryClearOverrideAsync(
        string runId,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
UPDATE {Table("canary_emergency_overrides")}
SET cleared_at = now(), cleared_by = @operator_name
WHERE run_id = @run_id AND cleared_at IS NULL;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("operator_name", operatorName);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    private static CanaryEmergencyOverride ReadOverride(NpgsqlDataReader reader)
    {
        return new CanaryEmergencyOverride
        {
            RunId = reader.GetString(reader.GetOrdinal("run_id")),
            Reason = reader.GetString(reader.GetOrdinal("reason")),
            OperatorName = reader.GetString(reader.GetOrdinal("operator_name")),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            ClearedAt = reader.IsDBNull(reader.GetOrdinal("cleared_at"))
                ? null
                : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("cleared_at")),
            ClearedBy = reader.IsDBNull(reader.GetOrdinal("cleared_by"))
                ? null
                : reader.GetString(reader.GetOrdinal("cleared_by")),
        };
    }
}
