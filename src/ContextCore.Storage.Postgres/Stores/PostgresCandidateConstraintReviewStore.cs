using System.Collections.Generic;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL CandidateConstraint activate / reject 审核历史存储。
/// R14-PG-4：替代 UnsupportedCandidateConstraintReviewStore，让 Postgres provider 在 HA 场景下能持久化候选约束审核记录。
/// </summary>
public sealed class PostgresCandidateConstraintReviewStore : PostgresStoreBase, ICandidateConstraintReviewStore
{
    public PostgresCandidateConstraintReviewStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task AppendReviewAsync(CandidateConstraintReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("candidate_constraint_reviews")} (workspace_id, collection_id, review_id, constraint_id, reviewed_at, created_at, data)
VALUES (@workspace_id, @collection_id, @review_id, @constraint_id, @reviewed_at, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, review_id) DO UPDATE SET
    constraint_id = EXCLUDED.constraint_id,
    reviewed_at = EXCLUDED.reviewed_at,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", CollectionKey(normalized.CollectionId));
        command.Parameters.AddWithValue("review_id", normalized.ReviewId);
        command.Parameters.AddWithValue("constraint_id", normalized.ConstraintId);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandidateConstraintReviewRecord>> QueryReviewsAsync(string constraintId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("candidate_constraint_reviews")}
WHERE constraint_id = @constraint_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("constraint_id", constraintId);

        return await ExecuteReaderJsonAsync<CandidateConstraintReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static CandidateConstraintReviewRecord Normalize(CandidateConstraintReviewRecord item)
    {
        return new CandidateConstraintReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            ConstraintId = item.ConstraintId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            ActivatedConstraintId = item.ActivatedConstraintId,
            SourceConstraintGapId = item.SourceConstraintGapId,
            SourceSampleId = item.SourceSampleId,
            SourceOperationId = item.SourceOperationId,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
