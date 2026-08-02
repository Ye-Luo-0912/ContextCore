using System.Collections.Generic;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// PostgreSQL 上下文学习记录与案例存储。
/// 替代 UnsupportedContextLearningStore，让 Postgres provider 在 HA 场景下能持久化晋升反馈、学习记录与学习案例。
/// </summary>
public sealed class PostgresContextLearningStore : PostgresStoreBase, IContextLearningStore
{
    public PostgresContextLearningStore(PostgresConnectionFactory connectionFactory, PostgresJsonSerializer serializer, PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task AddFeedbackAsync(PromotionFeedbackSignal feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        var normalized = Normalize(feedback);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("context_learning_feedback")} (workspace_id, collection_id, feedback_id, candidate_id, capability_id, created_at, data)
VALUES (@workspace_id, @collection_id, @feedback_id, @candidate_id, @capability_id, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, feedback_id) DO UPDATE SET
    candidate_id = EXCLUDED.candidate_id,
    capability_id = EXCLUDED.capability_id,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("feedback_id", normalized.FeedbackId);
        command.Parameters.AddWithValue("candidate_id", normalized.CandidateId);
        command.Parameters.AddWithValue("capability_id", string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PromotionFeedbackSignal>> QueryFeedbackAsync(PromotionFeedbackSignalQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.WorkspaceId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId!);

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
        if (!string.IsNullOrWhiteSpace(query.CandidateId))
        {
            where.Append(" AND candidate_id = @candidate_id");
            command.Parameters.AddWithValue("candidate_id", query.CandidateId);
        }
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            where.Append(" AND data->>'Action' = @action");
            command.Parameters.AddWithValue("action", query.Action);
        }

        var limit = TakeOrDefault(query.Limit);
        command.Parameters.AddWithValue("limit", limit);
        command.CommandText = $"""
SELECT data
FROM {Table("context_learning_feedback")}
{where}
ORDER BY created_at DESC
LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<PromotionFeedbackSignal>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddRecordAsync(ContextLearningRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var normalized = Normalize(record);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("context_learning_records")} (workspace_id, collection_id, record_id, source_id, created_at, data)
VALUES (@workspace_id, @collection_id, @record_id, @source_id, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, record_id) DO UPDATE SET
    source_id = EXCLUDED.source_id,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("record_id", normalized.RecordId);
        command.Parameters.AddWithValue("source_id", normalized.SourceId ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningRecord?> GetRecordAsync(string recordId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("context_learning_records")}
WHERE record_id = @record_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("record_id", recordId);

        return await ExecuteScalarJsonAsync<ContextLearningRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningRecord>> QueryRecordsAsync(ContextLearningRecordQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.WorkspaceId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId!);

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
        if (!string.IsNullOrWhiteSpace(query.SourceKind))
        {
            where.Append(" AND data->>'SourceKind' = @source_kind");
            command.Parameters.AddWithValue("source_kind", query.SourceKind);
        }
        if (!string.IsNullOrWhiteSpace(query.SourceId))
        {
            where.Append(" AND source_id = @source_id");
            command.Parameters.AddWithValue("source_id", query.SourceId);
        }
        if (query.Signal is not null)
        {
            where.Append(" AND data->>'Signal' = @signal");
            command.Parameters.AddWithValue("signal", query.Signal.Value.ToString());
        }
        if (query.FailureType is not null)
        {
            where.Append(" AND data->>'FailureType' = @failure_type");
            command.Parameters.AddWithValue("failure_type", query.FailureType.Value.ToString());
        }

        var limit = TakeOrDefault(query.Limit);
        command.Parameters.AddWithValue("limit", limit);
        command.CommandText = $"""
SELECT data
FROM {Table("context_learning_records")}
{where}
ORDER BY created_at DESC
LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<ContextLearningRecord>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContextLearningCase> AddCaseAsync(ContextLearningCase learningCase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(learningCase);
        var normalized = Normalize(learningCase);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("context_learning_cases")} (workspace_id, collection_id, case_id, source_record_id, created_at, data)
VALUES (@workspace_id, @collection_id, @case_id, @source_record_id, @created_at, @data)
ON CONFLICT (workspace_id, collection_id, case_id) DO UPDATE SET
    source_record_id = EXCLUDED.source_record_id,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("case_id", normalized.CaseId);
        command.Parameters.AddWithValue("source_record_id", normalized.SourceRecordId ?? string.Empty);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return normalized;
    }

    public async Task<ContextLearningCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("context_learning_cases")}
WHERE case_id = @case_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("case_id", caseId);

        return await ExecuteScalarJsonAsync<ContextLearningCase>(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContextLearningCase>> QueryCasesAsync(ContextLearningCaseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.WorkspaceId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        var where = new StringBuilder("WHERE workspace_id = @workspace_id");
        command.Parameters.AddWithValue("workspace_id", query.WorkspaceId!);

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
        if (!string.IsNullOrWhiteSpace(query.SourceRecordId))
        {
            where.Append(" AND source_record_id = @source_record_id");
            command.Parameters.AddWithValue("source_record_id", query.SourceRecordId);
        }
        if (!string.IsNullOrWhiteSpace(query.CaseKind))
        {
            where.Append(" AND data->>'CaseKind' = @case_kind");
            command.Parameters.AddWithValue("case_kind", query.CaseKind);
        }
        if (query.Signal is not null)
        {
            where.Append(" AND data->>'Signal' = @signal");
            command.Parameters.AddWithValue("signal", query.Signal.Value.ToString());
        }
        if (query.FailureType is not null)
        {
            where.Append(" AND data->>'FailureType' = @failure_type");
            command.Parameters.AddWithValue("failure_type", query.FailureType.Value.ToString());
        }
        if (query.Status is not null)
        {
            where.Append(" AND data->>'Status' = @status");
            command.Parameters.AddWithValue("status", query.Status.Value.ToString());
        }

        var limit = TakeOrDefault(query.Limit);
        command.Parameters.AddWithValue("limit", limit);
        command.CommandText = $"""
SELECT data
FROM {Table("context_learning_cases")}
{where}
ORDER BY created_at DESC
LIMIT @limit;
""";

        return await ExecuteReaderJsonAsync<ContextLearningCase>(command, cancellationToken).ConfigureAwait(false);
    }

    private static PromotionFeedbackSignal Normalize(PromotionFeedbackSignal item)
    {
        return new PromotionFeedbackSignal
        {
            FeedbackId = string.IsNullOrWhiteSpace(item.FeedbackId) ? Guid.NewGuid().ToString("N") : item.FeedbackId,
            CandidateId = item.CandidateId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            Action = item.Action,
            Reviewer = item.Reviewer,
            Reason = item.Reason,
            SourceWorkingItemId = item.SourceWorkingItemId,
            CreatedTargetItemId = item.CreatedTargetItemId,
            SuggestedTargetLayer = item.SuggestedTargetLayer,
            ActualTargetLayer = item.ActualTargetLayer,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ContextLearningRecord Normalize(ContextLearningRecord item)
    {
        return new ContextLearningRecord
        {
            RecordId = string.IsNullOrWhiteSpace(item.RecordId) ? Guid.NewGuid().ToString("N") : item.RecordId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceKind = item.SourceKind,
            SourceId = item.SourceId,
            CandidateId = item.CandidateId,
            ReviewId = item.ReviewId,
            EventKind = item.EventKind,
            Signal = item.Signal,
            FailureType = item.FailureType,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Importance = item.Importance,
            EvidenceRefs = [.. item.EvidenceRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ContextLearningCase Normalize(ContextLearningCase item)
    {
        return new ContextLearningCase
        {
            CaseId = string.IsNullOrWhiteSpace(item.CaseId) ? Guid.NewGuid().ToString("N") : item.CaseId,
            SourceType = item.SourceType,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SessionId = item.SessionId,
            SourceRecordId = item.SourceRecordId,
            SourceKind = item.SourceKind,
            SourceId = item.SourceId,
            CaseKind = item.CaseKind,
            Title = item.Title,
            Summary = item.Summary,
            InputSummary = item.InputSummary,
            ExpectedBehavior = item.ExpectedBehavior,
            Signal = item.Signal,
            FailureType = item.FailureType,
            CorrectionReason = item.CorrectionReason,
            Status = item.Status,
            EvidenceRefs = [.. item.EvidenceRefs],
            PositiveRefs = [.. item.PositiveRefs],
            NegativeRefs = [.. item.NegativeRefs],
            CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
            Metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }
}
