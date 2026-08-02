using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 短期记忆晋升候选项存储。
/// 替代 UnsupportedShortTermPromotionCandidateStore，让 Postgres provider 在 HA 场景下能持久化晋升候选与审核记录。
/// </summary>
public sealed class PostgresShortTermPromotionCandidateStore : PostgresStoreBase, IShortTermPromotionCandidateStore
{
    public PostgresShortTermPromotionCandidateStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task SaveAsync(ShortTermPromotionCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("short_term_promotion_candidates")} (workspace_id, collection_id, candidate_id, kind, status, created_at, data)
VALUES (@workspace_id, @collection_id, @candidate_id, @kind, @status, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, candidate_id) DO UPDATE SET
    kind = EXCLUDED.kind,
    status = EXCLUDED.status,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("candidate_id", normalized.CandidateId);
        command.Parameters.AddWithValue("kind", normalized.Kind ?? string.Empty);
        command.Parameters.AddWithValue("status", normalized.Status.ToString());
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShortTermPromotionCandidate?> GetAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("short_term_promotion_candidates")}
WHERE candidate_id = @candidate_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("candidate_id", candidateId);

        return await ExecuteScalarJsonAsync<ShortTermPromotionCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryAsync(ShortTermPromotionCandidateQuery query, CancellationToken cancellationToken = default)
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
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            where.Append(" AND kind = @kind");
            command.Parameters.AddWithValue("kind", query.Kind);
        }
        if (!string.IsNullOrWhiteSpace(query.SuggestedTargetLayer))
        {
            where.Append(" AND data->>'SuggestedTargetLayer' = @suggested_target_layer");
            command.Parameters.AddWithValue("suggested_target_layer", query.SuggestedTargetLayer);
        }
        if (query.Status is not null)
        {
            where.Append(" AND status = @status");
            command.Parameters.AddWithValue("status", query.Status.Value.ToString());
        }
        if (query.MinConfidence is not null)
        {
            where.Append(" AND (data->>'Confidence')::float8 >= @min_confidence");
            command.Parameters.AddWithValue("min_confidence", query.MinConfidence.Value);
        }
        if (query.MinImportance is not null)
        {
            where.Append(" AND (data->>'Importance')::float8 >= @min_importance");
            command.Parameters.AddWithValue("min_importance", query.MinImportance.Value);
        }

        var limit = query.Limit > 0 ? query.Limit : 20;
        var offset = Math.Max(0, query.Offset);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        command.CommandText = $"""
SELECT data
FROM {Table("short_term_promotion_candidates")}
{where}
ORDER BY created_at DESC
OFFSET @offset LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<ShortTermPromotionCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendReviewAsync(PromotionCandidateReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("short_term_promotion_candidate_reviews")} (workspace_id, collection_id, review_id, candidate_id, reviewed_at, created_at, data)
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

    public async Task<IReadOnlyList<PromotionCandidateReviewRecord>> QueryReviewsAsync(string candidateId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("short_term_promotion_candidate_reviews")}
WHERE candidate_id = @candidate_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("candidate_id", candidateId);

        return await ExecuteReaderJsonAsync<PromotionCandidateReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static ShortTermPromotionCandidate Normalize(ShortTermPromotionCandidate item)
    {
        return new ShortTermPromotionCandidate
        {
            CandidateId = string.IsNullOrWhiteSpace(item.CandidateId) ? Guid.NewGuid().ToString("N") : item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceWorkingItemId = item.SourceWorkingItemId,
            Kind = item.Kind,
            Title = item.Title,
            Summary = item.Summary,
            SuggestedTargetLayer = item.SuggestedTargetLayer,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = [.. item.EvidenceRefs],
            Tags = [.. item.Tags],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Status = item.Status,
            DedupeKey = item.DedupeKey,
            SourceFingerprint = item.SourceFingerprint,
            GeneratedBy = item.GeneratedBy,
            PolicyVersion = item.PolicyVersion,
            RuleName = item.RuleName,
            RuleVersion = item.RuleVersion,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static PromotionCandidateReviewRecord Normalize(PromotionCandidateReviewRecord item)
    {
        return new PromotionCandidateReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            CandidateId = item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            TargetItemId = item.TargetItemId,
            TargetItemKind = item.TargetItemKind,
            TargetLayer = item.TargetLayer,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
