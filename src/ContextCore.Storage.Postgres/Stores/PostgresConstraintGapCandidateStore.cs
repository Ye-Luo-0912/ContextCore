using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 约束语料缺口候选项存储。
/// 替代 UnsupportedConstraintGapCandidateStore，让 Postgres provider 在 HA 场景下能持久化缺口候选与审核记录。
/// </summary>
public sealed class PostgresConstraintGapCandidateStore : PostgresStoreBase, IConstraintGapCandidateStore
{
    public PostgresConstraintGapCandidateStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task<ConstraintGapCandidate> SaveAsync(ConstraintGapCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var normalized = Normalize(candidate);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("constraint_gap_candidates")} (workspace_id, collection_id, gap_id, status, created_at, updated_at, data)
VALUES (@workspace_id, @collection_id, @gap_id, @status, @created_at, @updated_at, @data)
ON CONFLICT (workspace_id, collection_id, gap_id) DO UPDATE SET
    status = EXCLUDED.status,
    created_at = EXCLUDED.created_at,
    updated_at = EXCLUDED.updated_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("gap_id", normalized.GapId);
        command.Parameters.AddWithValue("status", normalized.Status ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return normalized;
    }

    public async Task<ConstraintGapCandidate?> GetAsync(string gapId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("constraint_gap_candidates")}
WHERE gap_id = @gap_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("gap_id", gapId);

        return await ExecuteScalarJsonAsync<ConstraintGapCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapCandidate>> QueryAsync(ConstraintGapCandidateQuery query, CancellationToken cancellationToken = default)
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
        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            where.Append(" AND data->>'Source' = @source");
            command.Parameters.AddWithValue("source", query.Source);
        }
        if (!string.IsNullOrWhiteSpace(query.SourceSampleId))
        {
            where.Append(" AND data->>'SourceSampleId' = @source_sample_id");
            command.Parameters.AddWithValue("source_sample_id", query.SourceSampleId);
        }
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Append(" AND status = @status");
            command.Parameters.AddWithValue("status", query.Status);
        }
        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            where.Append(" AND data->>'Severity' = @severity");
            command.Parameters.AddWithValue("severity", query.Severity);
        }

        var limit = query.Limit > 0 ? query.Limit : 20;
        var offset = Math.Max(0, query.Offset);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("offset", offset);
        command.CommandText = $"""
SELECT data
FROM {Table("constraint_gap_candidates")}
{where}
ORDER BY created_at DESC
OFFSET @offset LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<ConstraintGapCandidate>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConstraintGapCandidate?> UpdateStatusAsync(
        string gapId,
        string status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // SELECT + UPDATE 模式：先读取现有 candidate，构造更新后的 data jsonb，再写回。
        ConstraintGapCandidate? existing;
        await using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            selectCommand.CommandText = $"""
SELECT data
FROM {Table("constraint_gap_candidates")}
WHERE gap_id = @gap_id
LIMIT 1;
""";
            selectCommand.Parameters.AddWithValue("gap_id", gapId);
            existing = await ExecuteScalarJsonAsync<ConstraintGapCandidate>(selectCommand, cancellationToken).ConfigureAwait(false);
        }

        if (existing is null)
        {
            return null;
        }

        var normalizedStatus = status.Trim();
        var metadata = new Dictionary<string, string>(existing.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["lastReviewStatus"] = normalizedStatus,
            ["lastReviewedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
        if (!string.IsNullOrWhiteSpace(reviewer))
        {
            metadata["lastReviewer"] = reviewer!.Trim();
        }
        if (!string.IsNullOrWhiteSpace(reason))
        {
            metadata["lastReviewReason"] = reason!.Trim();
        }

        var updated = new ConstraintGapCandidate
        {
            GapId = existing.GapId,
            WorkspaceId = existing.WorkspaceId,
            CollectionId = existing.CollectionId,
            SessionId = existing.SessionId,
            Source = existing.Source,
            SourceSampleId = existing.SourceSampleId,
            SourceOperationId = existing.SourceOperationId,
            ExpectedConstraintText = existing.ExpectedConstraintText,
            MatchedConstraintIds = existing.MatchedConstraintIds,
            SuggestedConstraintTitle = existing.SuggestedConstraintTitle,
            SuggestedConstraintScope = existing.SuggestedConstraintScope,
            SuggestedConstraintType = existing.SuggestedConstraintType,
            Severity = existing.Severity,
            Reason = existing.Reason,
            EvidenceRefs = existing.EvidenceRefs,
            Status = normalizedStatus,
            CreatedAt = existing.CreatedAt,
            Metadata = metadata
        };

        await using var updateCommand = connection.CreateCommand();
        updateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        updateCommand.CommandText = $"""
UPDATE {Table("constraint_gap_candidates")}
SET status = @status,
    updated_at = @updated_at,
    data = @data
WHERE gap_id = @gap_id;
""";
        updateCommand.Parameters.AddWithValue("gap_id", gapId);
        updateCommand.Parameters.AddWithValue("status", normalizedStatus);
        updateCommand.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        AddJson(updateCommand, "data", updated);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task AppendReviewAsync(ConstraintGapReviewRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("constraint_gap_reviews")} (workspace_id, collection_id, review_id, gap_id, reviewed_at, created_at, data)
VALUES (@workspace_id, @collection_id, @review_id, @gap_id, @reviewed_at, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, review_id) DO UPDATE SET
    gap_id = EXCLUDED.gap_id,
    reviewed_at = EXCLUDED.reviewed_at,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("review_id", normalized.ReviewId);
        command.Parameters.AddWithValue("gap_id", normalized.GapId);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConstraintGapReviewRecord>> QueryReviewsAsync(string gapId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("constraint_gap_reviews")}
WHERE gap_id = @gap_id
ORDER BY reviewed_at DESC;
""";
        command.Parameters.AddWithValue("gap_id", gapId);

        return await ExecuteReaderJsonAsync<ConstraintGapReviewRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    private static ConstraintGapCandidate Normalize(ConstraintGapCandidate item)
    {
        return new ConstraintGapCandidate
        {
            GapId = string.IsNullOrWhiteSpace(item.GapId) ? Guid.NewGuid().ToString("N") : item.GapId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Source = item.Source,
            SourceSampleId = item.SourceSampleId,
            SourceOperationId = item.SourceOperationId,
            ExpectedConstraintText = item.ExpectedConstraintText,
            MatchedConstraintIds = [.. item.MatchedConstraintIds],
            SuggestedConstraintTitle = item.SuggestedConstraintTitle,
            SuggestedConstraintScope = item.SuggestedConstraintScope,
            SuggestedConstraintType = item.SuggestedConstraintType,
            Severity = item.Severity,
            Reason = item.Reason,
            EvidenceRefs = [.. item.EvidenceRefs],
            Status = item.Status,
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ConstraintGapReviewRecord Normalize(ConstraintGapReviewRecord item)
    {
        return new ConstraintGapReviewRecord
        {
            ReviewId = string.IsNullOrWhiteSpace(item.ReviewId) ? Guid.NewGuid().ToString("N") : item.ReviewId,
            GapId = item.GapId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            CreatedConstraintId = item.CreatedConstraintId,
            TargetItemKind = item.TargetItemKind,
            TargetLayer = item.TargetLayer,
            SourceSampleId = item.SourceSampleId,
            SourceOperationId = item.SourceOperationId,
            ExpectedConstraintText = item.ExpectedConstraintText,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            ReviewedAt = item.ReviewedAt == default ? DateTimeOffset.UtcNow : item.ReviewedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase),
            Warnings = [.. item.Warnings],
            Errors = [.. item.Errors]
        };
    }
}
