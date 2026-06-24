using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL Relation review 存储；仅在显式 provider eval / diagnostics 中使用。</summary>
public sealed class PostgresRelationReviewStore : PostgresStoreBase, IRelationReviewStore
{
    public PostgresRelationReviewStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task AppendReviewAsync(
        RelationReviewRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("Relation review 必须包含 collectionId。", nameof(record));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("relation_reviews")} (
    workspace_id, collection_id, review_id, relation_id, review_status, reviewer, reviewed_at, data)
VALUES (
    @workspace_id, @collection_id, @review_id, @relation_id, @review_status, @reviewer, @reviewed_at, @data)
ON CONFLICT (workspace_id, collection_id, review_id) DO UPDATE SET
    relation_id = EXCLUDED.relation_id,
    review_status = EXCLUDED.review_status,
    reviewer = EXCLUDED.reviewer,
    reviewed_at = EXCLUDED.reviewed_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId!);
        command.Parameters.AddWithValue("review_id", normalized.ReviewId);
        command.Parameters.AddWithValue("relation_id", normalized.RelationId);
        command.Parameters.AddWithValue("review_status", ResolveReviewStatus(normalized));
        command.Parameters.AddWithValue("reviewer", normalized.Reviewer);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationId);
        return QueryAsync(["relation_id = @relation_id"], command =>
        {
            command.Parameters.AddWithValue("relation_id", relationId);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryByReviewStatusAsync(
        string workspaceId,
        string collectionId,
        string reviewStatus,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id", "review_status = @review_status"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("review_status", reviewStatus);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryByReviewerAsync(
        string workspaceId,
        string collectionId,
        string reviewer,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            ["workspace_id = @workspace_id", "collection_id = @collection_id", "reviewer = @reviewer"],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("reviewer", reviewer);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> QueryByOperationIdAsync(
        string workspaceId,
        string collectionId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        return QueryAsync(
            [
                "workspace_id = @workspace_id",
                "collection_id = @collection_id",
                "(data -> 'Metadata' ->> 'operationId' = @operation_id OR data -> 'metadata' ->> 'operationId' = @operation_id)"
            ],
            command =>
            {
                command.Parameters.AddWithValue("workspace_id", workspaceId);
                command.Parameters.AddWithValue("collection_id", collectionId);
                command.Parameters.AddWithValue("operation_id", operationId);
            },
            cancellationToken);
    }

    public async Task<RelationReviewRecord?> GetLatestReviewAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        var reviews = await QueryReviewsAsync(relationId, cancellationToken).ConfigureAwait(false);
        return reviews.FirstOrDefault();
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {Table("relation_reviews")};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> DeleteByScopeAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("relation_reviews")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RelationReviewRecord>> QueryAsync(
        IReadOnlyList<string> filters,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("relation_reviews")}
WHERE {string.Join(" AND ", filters)}
ORDER BY reviewed_at DESC, review_id ASC;
""";
        bind(command);

        var results = new List<RelationReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Serializer.Deserialize<RelationReviewRecord>(reader.GetString(0)));
        }

        return results;
    }

    private static string ResolveReviewStatus(RelationReviewRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ToReviewStatus))
        {
            return record.ToReviewStatus;
        }

        return string.IsNullOrWhiteSpace(record.Action) ? "Unknown" : record.Action;
    }

    private static RelationReviewRecord Normalize(RelationReviewRecord record)
    {
        var createdAt = record.CreatedAt == default ? DateTimeOffset.UtcNow : record.CreatedAt;
        return new RelationReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(record.ReviewId) ? Guid.NewGuid().ToString("N") : record.ReviewId,
            RelationId = record.RelationId,
            WorkspaceId = record.WorkspaceId,
            CollectionId = record.CollectionId,
            Action = record.Action,
            FromLifecycle = record.FromLifecycle,
            ToLifecycle = record.ToLifecycle,
            FromReviewStatus = record.FromReviewStatus,
            ToReviewStatus = record.ToReviewStatus,
            Reviewer = record.Reviewer,
            Reason = record.Reason,
            RelationType = record.RelationType,
            SourceId = record.SourceId,
            TargetId = record.TargetId,
            EvidenceRefs = [.. record.EvidenceRefs],
            SourceRefs = [.. record.SourceRefs],
            CreatedAt = createdAt,
            ReviewedAt = record.ReviewedAt == default ? createdAt : record.ReviewedAt,
            Metadata = new Dictionary<string, string>(record.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. record.Warnings],
            Errors = [.. record.Errors]
        };
    }
}
