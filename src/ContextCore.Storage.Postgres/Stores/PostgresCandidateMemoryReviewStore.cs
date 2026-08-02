using System.Collections.Generic;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL CandidateMemory 人工 review / cleanup 审核历史存储。
/// 替代 UnsupportedCandidateMemoryReviewStore，让 Postgres provider 在 HA 场景下能持久化候选审核历史。
/// </summary>
public sealed class PostgresCandidateMemoryReviewStore : PostgresStoreBase, ICandidateMemoryReviewStore
{
    public PostgresCandidateMemoryReviewStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task AppendReviewAsync(CandidateMemoryReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);
        if (string.IsNullOrWhiteSpace(normalized.CollectionId))
        {
            throw new ArgumentException("CandidateMemory review 必须包含 collectionId。", nameof(record));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("candidate_memory_reviews")} (workspace_id, collection_id, review_id, candidate_id, reviewed_at, created_at, data)
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
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandidateMemoryReviewRecord>> QueryReviewsAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("candidate_memory_reviews")}
WHERE candidate_id = @candidate_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("candidate_id", candidateId);

        return await ExecuteReaderJsonAsync<CandidateMemoryReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static CandidateMemoryReviewRecord Normalize(CandidateMemoryReviewRecord item)
    {
        return new CandidateMemoryReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            CandidateId = item.CandidateId,
            CandidateKind = item.CandidateKind,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            SupersedeTargetCandidateId = item.SupersedeTargetCandidateId,
            EvidenceRefs = [.. item.EvidenceRefs],
            SourceRefs = [.. item.SourceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
