using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL vector lifecycle metadata review 历史存储。
/// 替代 UnsupportedVectorLifecycleMetadataReviewStore，让 Postgres provider 在 HA 场景下能持久化人工决策记录。
/// </summary>
public sealed class PostgresVectorLifecycleMetadataReviewStore : PostgresStoreBase, IVectorLifecycleMetadataReviewStore
{
    public PostgresVectorLifecycleMetadataReviewStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(VectorLifecycleMetadataReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("vector_lifecycle_metadata_reviews")} (workspace_id, collection_id, review_id, candidate_id, reviewed_at, created_at, data)
VALUES (@workspace_id, @collection_id, @review_id, @candidate_id, @reviewed_at, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, review_id) DO UPDATE SET
    candidate_id = EXCLUDED.candidate_id,
    reviewed_at = EXCLUDED.reviewed_at,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("review_id", normalized.ReviewId);
        command.Parameters.AddWithValue("candidate_id", normalized.CandidateId);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        // VectorLifecycleMetadataReviewRecord DTO 没有 CreatedAt 字段，使用 ReviewedAt 作为行创建时间。
        command.Parameters.AddWithValue("created_at", normalized.ReviewedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> ListAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("vector_lifecycle_metadata_reviews")}
WHERE candidate_id = @candidate_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("candidate_id", candidateId);

        return await ExecuteReaderJsonAsync<VectorLifecycleMetadataReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorLifecycleMetadataReviewRecord>> QueryAsync(
        string workspaceId,
        string? collectionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", workspaceId);

        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", collectionId);
        }

        command.CommandText = $"""
SELECT data
FROM {Table("vector_lifecycle_metadata_reviews")}
{where}
ORDER BY reviewed_at DESC;
""";

        return await ExecuteReaderJsonAsync<VectorLifecycleMetadataReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static VectorLifecycleMetadataReviewRecord Normalize(VectorLifecycleMetadataReviewRecord item)
    {
        return new VectorLifecycleMetadataReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            CandidateId = item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            MustHitItemId = item.MustHitItemId,
            Decision = item.Decision,
            ResultStatus = item.ResultStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            ProposedLifecycle = item.ProposedLifecycle,
            ProposedReviewStatus = item.ProposedReviewStatus,
            ProposedTargetSection = item.ProposedTargetSection,
            EvidenceRefs = [.. item.EvidenceRefs],
            SourceRefs = [.. item.SourceRefs],
            SidecarWritten = item.SidecarWritten,
            UnsafeApprovalBlocked = item.UnsafeApprovalBlocked,
            BlockedReason = item.BlockedReason,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
