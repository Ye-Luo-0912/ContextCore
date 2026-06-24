using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 约束规则存储，实现 <see cref="IConstraintStore"/>。</summary>
public sealed class PostgresConstraintStore : PostgresStoreBase, IConstraintStore
{
    public PostgresConstraintStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ContextConstraint constraint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        var now = DateTimeOffset.UtcNow;
        var isNew = string.IsNullOrWhiteSpace(constraint.Id);
        var normalized = new ContextConstraint
        {
            Id = isNew ? Guid.NewGuid().ToString("N") : constraint.Id,
            WorkspaceId = constraint.WorkspaceId,
            CollectionId = constraint.CollectionId,
            Scope = constraint.Scope,
            Level = constraint.Level,
            Content = constraint.Content,
            AppliesToRefs = constraint.AppliesToRefs,
            SourceRefs = constraint.SourceRefs,
            Status = constraint.Status,
            Confidence = constraint.Confidence,
            Metadata = constraint.Metadata,
            CreatedAt = isNew ? now : constraint.CreatedAt,
            UpdatedAt = now,
        };

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("constraints")} (
    workspace_id, collection_id, id, scope, level, status, confidence, created_at, updated_at, data)
VALUES (
    @workspace_id, @collection_id, @id, @scope, @level, @status, @confidence, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, id) DO UPDATE SET
    collection_id = EXCLUDED.collection_id,
    scope = EXCLUDED.scope,
    level = EXCLUDED.level,
    status = EXCLUDED.status,
    confidence = EXCLUDED.confidence,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", (object?)normalized.CollectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("id", normalized.Id);
        command.Parameters.AddWithValue("scope", normalized.Scope.ToString());
        command.Parameters.AddWithValue("level", normalized.Level.ToString());
        command.Parameters.AddWithValue("status", normalized.Status.ToString());
        command.Parameters.AddWithValue("confidence", normalized.Confidence);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", normalized.UpdatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextConstraint?> GetAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data FROM {Table("constraints")}
WHERE id = @id
ORDER BY updated_at DESC
LIMIT 1;
""";
        command.Parameters.AddWithValue("id", constraintId);

        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return string.IsNullOrWhiteSpace(json)
            ? null
            : Serializer.Deserialize<ContextConstraint>(json);
    }

    public async Task<IReadOnlyList<ContextConstraint>> QueryAsync(
        ContextConstraintQuery query,
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

        if (query.Level.HasValue)
        {
            conditions.Add("level = @level");
            command.Parameters.AddWithValue("level", query.Level.Value.ToString());
        }

        if (query.Scope.HasValue)
        {
            conditions.Add("scope = @scope");
            command.Parameters.AddWithValue("scope", query.Scope.Value.ToString());
        }

        if (query.Status.HasValue)
        {
            conditions.Add("status = @status");
            command.Parameters.AddWithValue("status", query.Status.Value.ToString());
        }

        var take = query.Take > 0 ? query.Take : 100;
        var where = string.Join(" AND ", conditions);
        command.CommandText = $"""
SELECT data FROM {Table("constraints")}
WHERE {where}
ORDER BY confidence DESC, created_at DESC
LIMIT {take};
""";

        var results = new List<ContextConstraint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            var item = Serializer.Deserialize<ContextConstraint>(json);
            results.Add(item);
        }

        return results;
    }
}
