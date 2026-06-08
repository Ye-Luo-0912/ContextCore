using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 全局上下文存储，实现 <see cref="IGlobalContextStore"/>。</summary>
public sealed class PostgresGlobalContextStore : PostgresStoreBase, IGlobalContextStore
{
    public PostgresGlobalContextStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextGlobalItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var now = DateTimeOffset.UtcNow;
        var isNew = string.IsNullOrWhiteSpace(item.Id);
        var normalized = new ContextGlobalItem
        {
            Id = isNew ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Scope = item.Scope,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags,
            SourceRefs = item.SourceRefs,
            Importance = item.Importance,
            Version = item.Version,
            Metadata = item.Metadata,
            CreatedAt = isNew ? now : item.CreatedAt,
            UpdatedAt = now,
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("global_context_items")} (
    workspace_id, id, scope, created_at, updated_at, data)
VALUES (
    @workspace_id, @id, @scope, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, id) DO UPDATE SET
    scope = EXCLUDED.scope,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("scope", normalized.Scope.ToString());
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextGlobalItem>> QueryAsync(
        ContextGlobalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var conditions = new List<string> { "workspace_id = @workspace_id" };
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (query.Scope.HasValue)
        {
            conditions.Add("scope = @scope");
            command.Parameters.AddWithValue("scope", query.Scope.Value.ToString());
        }

        var take = query.Take > 0 ? query.Take : 100;
        var where = string.Join(" AND ", conditions);
        command.CommandText = $"""
SELECT data FROM {Table("global_context_items")}
WHERE {where}
ORDER BY updated_at DESC
LIMIT {take};
""";

        var results = new List<ContextGlobalItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = Serializer.Deserialize<ContextGlobalItem>(json);
            if (item is not null) results.Add(item);
        }

        return results;
    }
}
