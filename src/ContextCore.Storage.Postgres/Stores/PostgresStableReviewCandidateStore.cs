using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL Stable review 候选项存储。
/// R14-PG-3：替代 UnsupportedStableReviewCandidateStore，让 Postgres provider 在 HA 场景下能持久化 Stable review 候选项与决策记录。
/// </summary>
public sealed class PostgresStableReviewCandidateStore : PostgresStoreBase, IStableReviewCandidateStore
{
    public PostgresStableReviewCandidateStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(StableReviewCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("stable_review_candidates")} (workspace_id, collection_id, stable_review_candidate_id, kind, status, created_at, data)
VALUES (@workspace_id, @collection_id, @stable_review_candidate_id, @kind, @status, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, stable_review_candidate_id) DO UPDATE SET
    kind = EXCLUDED.kind,
    status = EXCLUDED.status,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("stable_review_candidate_id", normalized.StableReviewCandidateId);
        command.Parameters.AddWithValue("kind", normalized.Kind ?? string.Empty);
        command.Parameters.AddWithValue("status", normalized.Status ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StableReviewCandidate?> GetAsync(string stableReviewCandidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("stable_review_candidates")}
WHERE stable_review_candidate_id = @stable_review_candidate_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("stable_review_candidate_id", stableReviewCandidateId);

        return await ExecuteScalarJsonAsync<StableReviewCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewCandidate>> QueryAsync(StableReviewCandidateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId);

        if (!string.IsNullOrWhiteSpace(query.CollectionId))
        {
            where.Append(" AND collection_id = @collection_id");
            command.Parameters.AddWithValue("collection_id", query.CollectionId);
        }
        if (!string.IsNullOrWhiteSpace(query.SessionId))
        {
            where.Append(" AND data->>'SessionId' = @session_id");
            command.Parameters.AddWithValue("session_id", query.SessionId);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Append(" AND status = @status");
            command.Parameters.AddWithValue("status", query.Status);
        }
        if (!string.IsNullOrWhiteSpace(query.ValidationStatus))
        {
            where.Append(" AND data->>'ValidationStatus' = @validation_status");
            command.Parameters.AddWithValue("validation_status", query.ValidationStatus);
        }
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            where.Append(" AND kind = @kind");
            command.Parameters.AddWithValue("kind", query.Kind);
        }
        if (!string.IsNullOrWhiteSpace(query.SuggestedStableTarget))
        {
            where.Append(" AND data->>'SuggestedStableTarget' = @suggested_stable_target");
            command.Parameters.AddWithValue("suggested_stable_target", query.SuggestedStableTarget);
        }

        var limit = query.Limit > 0 ? query.Limit : 20;
        var offset = Math.Max(0, query.Offset);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        command.CommandText = $"""
SELECT data
FROM {Table("stable_review_candidates")}
{where}
ORDER BY created_at DESC
OFFSET @offset LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<StableReviewCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendReviewAsync(StableReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("stable_review_records")} (workspace_id, collection_id, review_id, stable_review_candidate_id, reviewed_at, created_at, data)
VALUES (@workspace_id, @collection_id, @review_id, @stable_review_candidate_id, @reviewed_at, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, review_id) DO UPDATE SET
    stable_review_candidate_id = EXCLUDED.stable_review_candidate_id,
    reviewed_at = EXCLUDED.reviewed_at,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("review_id", normalized.ReviewId);
        command.Parameters.AddWithValue("stable_review_candidate_id", normalized.StableReviewCandidateId);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StableReviewRecord>> QueryReviewsAsync(string stableReviewCandidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableReviewCandidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("stable_review_records")}
WHERE stable_review_candidate_id = @stable_review_candidate_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("stable_review_candidate_id", stableReviewCandidateId);

        return await ExecuteReaderJsonAsync<StableReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static StableReviewCandidate Normalize(StableReviewCandidate item)
    {
        return new StableReviewCandidate
        {
            StableReviewCandidateId = string.IsNullOrWhiteSpace(item.StableReviewCandidateId) ? Guid.NewGuid().ToString("N") : item.StableReviewCandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceCandidateId = item.SourceCandidateId,
            SourceTargetItemId = item.SourceTargetItemId,
            SourceLearningCaseId = item.SourceLearningCaseId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            SuggestedStableTarget = item.SuggestedStableTarget,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = [.. item.EvidenceRefs],
            RiskFlags = [.. item.RiskFlags],
            ValidationStatus = item.ValidationStatus,
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Status = item.Status,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static StableReviewRecord Normalize(StableReviewRecord item)
    {
        return new StableReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            StableReviewCandidateId = item.StableReviewCandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            StableTargetItemId = item.StableTargetItemId,
            StableTargetItemKind = item.StableTargetItemKind,
            TargetLayer = item.TargetLayer,
            SourcePromotionCandidateId = item.SourcePromotionCandidateId,
            SourceTargetItemId = item.SourceTargetItemId,
            SourceLearningCaseId = item.SourceLearningCaseId,
            EvidenceRefs = [.. item.EvidenceRefs],
            ValidationStatus = item.ValidationStatus,
            RiskFlags = [.. item.RiskFlags],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
