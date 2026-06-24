using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL Context Package Build Trace 存储，实现 <see cref="IContextPackageBuildTraceStore"/>。</summary>
public sealed class PostgresContextPackageBuildTraceStore : PostgresStoreBase, IContextPackageBuildTraceStore
{
    public PostgresContextPackageBuildTraceStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextPackageBuildResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("package_build_traces")} (
    workspace_id, collection_id, build_id, created_at, data)
VALUES (
    @workspace_id, @collection_id, @build_id, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, build_id) DO UPDATE SET
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", result.Package.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", result.Package.CollectionId);
        command.Parameters.AddWithValue("build_id", result.BuildId);
        command.Parameters.AddWithValue("created_at", result.CreatedAt);
        AddJson(command, "data", result);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextPackageBuildResult>> QueryRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("package_build_traces")}
WHERE workspace_id = @workspace_id AND collection_id = @collection_id
ORDER BY created_at DESC
LIMIT {(take > 0 ? take : 20)};
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);

        var results = new List<ContextPackageBuildResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = Serializer.Deserialize<ContextPackageBuildResult>(json);
            results.Add(item);
        }

        return results;
    }
}
