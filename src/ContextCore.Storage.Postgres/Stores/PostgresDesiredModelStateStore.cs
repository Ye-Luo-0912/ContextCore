using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// R29 WP-A-2：PostgreSQL 持久化 Desired Model State Store。
/// 存储 HA 集群中各模型的期望状态（Active/Inactive），由各节点的 ReconcilerWorker 定期拉取并应用。
/// </summary>
public sealed class PostgresDesiredModelStateStore : PostgresStoreBase, IDesiredModelStateStore
{
    public PostgresDesiredModelStateStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask<DesiredModelState?> GetAsync(string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT model_id, desired_state, generation, content_hash, updated_at, updated_by
FROM {Table("desired_model_states")}
WHERE model_id = @model_id;
""";
        command.Parameters.AddWithValue("model_id", modelId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new DesiredModelState
        {
            ModelId = reader.GetString(reader.GetOrdinal("model_id")),
            DesiredState = reader.GetString(reader.GetOrdinal("desired_state")),
            Generation = reader.GetInt64(reader.GetOrdinal("generation")),
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
            UpdatedBy = reader.GetString(reader.GetOrdinal("updated_by"))
        };
    }

    public async ValueTask SetAsync(DesiredModelState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("desired_model_states")} (
    model_id, desired_state, generation, content_hash, updated_at, updated_by, data)
VALUES (
    @model_id, @desired_state, @generation, @content_hash, @updated_at, @updated_by, @data)
ON CONFLICT (model_id) DO UPDATE SET
    desired_state = EXCLUDED.desired_state,
    generation = EXCLUDED.generation,
    content_hash = EXCLUDED.content_hash,
    updated_at = EXCLUDED.updated_at,
    updated_by = EXCLUDED.updated_by,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("model_id", state.ModelId);
        command.Parameters.AddWithValue("desired_state", state.DesiredState);
        command.Parameters.AddWithValue("generation", state.Generation);
        command.Parameters.AddWithValue("content_hash", state.ContentHash);
        command.Parameters.AddWithValue("updated_at", state.UpdatedAt);
        command.Parameters.AddWithValue("updated_by", state.UpdatedBy);
        AddJson(command, "data", state);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<DesiredModelState>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT model_id, desired_state, generation, content_hash, updated_at, updated_by
FROM {Table("desired_model_states")}
ORDER BY updated_at DESC;
""";

        var results = new List<DesiredModelState>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new DesiredModelState
            {
                ModelId = reader.GetString(reader.GetOrdinal("model_id")),
                DesiredState = reader.GetString(reader.GetOrdinal("desired_state")),
                Generation = reader.GetInt64(reader.GetOrdinal("generation")),
                ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
                UpdatedBy = reader.GetString(reader.GetOrdinal("updated_by"))
            });
        }

        return results;
    }
}
