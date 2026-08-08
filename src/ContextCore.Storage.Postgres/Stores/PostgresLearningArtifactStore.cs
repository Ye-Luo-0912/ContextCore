using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using NpgsqlTypes;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Learning Artifact 存储：数据集快照工件（DatasetSnapshot + Lineage +
/// Completeness + Replay Manifest）持久化，按 (workspace_id, snapshot_id) 点查。
/// </summary>
public sealed class PostgresLearningArtifactStore : PostgresStoreBase, ILearningArtifactStore
{
    public PostgresLearningArtifactStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async ValueTask SaveAsync(
        DatasetSnapshotArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("dataset_snapshots")} (
    workspace_id, snapshot_id, schema_version, created_at, data)
VALUES (
    @workspace_id, @snapshot_id, @schema_version, @created_at, @data)
ON CONFLICT (workspace_id, snapshot_id) DO UPDATE SET
    schema_version = EXCLUDED.schema_version,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", artifact.Snapshot.WorkspaceId);
        command.Parameters.AddWithValue("snapshot_id", artifact.Snapshot.SnapshotId);
        command.Parameters.AddWithValue("schema_version", artifact.Snapshot.SchemaVersion);
        command.Parameters.AddWithValue("created_at", artifact.Snapshot.CreatedAt);
        AddJson(command, "data", artifact);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DatasetSnapshotArtifact?> GetAsync(
        string workspaceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("dataset_snapshots")}
WHERE workspace_id = @workspace_id
  AND snapshot_id = @snapshot_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("snapshot_id", snapshotId);

        var data = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return data is null ? null : Serializer.Deserialize<DatasetSnapshotArtifact>(data);
    }

    public async ValueTask<IReadOnlyList<DatasetSnapshotArtifact>> ListRecentAsync(
        string workspaceId,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("dataset_snapshots")}
WHERE workspace_id = @workspace_id
ORDER BY created_at DESC
LIMIT {(take > 0 ? take : 20)};
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);

        var results = new List<DatasetSnapshotArtifact>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = Serializer.Deserialize<DatasetSnapshotArtifact>(reader.GetString(0));
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return results;
    }
}
