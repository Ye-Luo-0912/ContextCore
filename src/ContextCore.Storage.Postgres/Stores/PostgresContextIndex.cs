using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 上下文索引存储，实现 <see cref="IContextIndex"/>。</summary>
public sealed class PostgresContextIndex : PostgresStoreBase, IContextIndex
{
    public PostgresContextIndex(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task UpsertAsync(ContextIndexEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("context_index")} (
    workspace_id, collection_id, id, key, kind, weight, created_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @key, @kind, @weight, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, id) DO UPDATE SET
    key = EXCLUDED.key,
    kind = EXCLUDED.kind,
    weight = EXCLUDED.weight,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", entry.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", entry.CollectionId);
        command.Parameters.AddWithValue("id", entry.Id);
        command.Parameters.AddWithValue("key", entry.Key);
        command.Parameters.AddWithValue("kind", entry.Kind);
        command.Parameters.AddWithValue("weight", entry.Weight);
        command.Parameters.AddWithValue("created_at", entry.CreatedAt);
        AddJson(command, "data", entry);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextIndexEntry>> SearchAsync(
        IndexQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var conditions = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            conditions.Add("collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }

        if (!string.IsNullOrWhiteSpace(query.Key))
        {
            conditions.Add("key ILIKE @key");
            command.Parameters.AddWithValue("key", $"%{query.Key}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            conditions.Add("kind = @kind");
            command.Parameters.AddWithValue("kind", query.Kind);
        }

        var take = query.Take > 0 ? query.Take : 50;
        var where = string.Join(" AND ", conditions);
        command.CommandText = $"""
SELECT data FROM {Table("context_index")}
WHERE {where}
ORDER BY weight DESC, created_at DESC
LIMIT {take};
""";

        var results = new List<ContextIndexEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = Serializer.Deserialize<ContextIndexEntry>(json);
            if (item is not null) results.Add(item);
        }

        return results;
    }
}
