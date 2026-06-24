using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 运行时反馈事件存储；仅供显式 diagnostics/parity 使用。</summary>
public sealed class PostgresLearningFeedbackStore : PostgresStoreBase, ILearningFeedbackStore
{
    public PostgresLearningFeedbackStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task<LearningFeedbackEvent?> GetAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedbackId))
        {
            return null;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT data
FROM {Table("learning_feedback_events")}
WHERE feedback_id = @feedback_id
LIMIT 1;
""";
        command.Parameters.AddWithValue("feedback_id", feedbackId.Trim());
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is string json ? Serializer.Deserialize<LearningFeedbackEvent>(json) : null;
    }

    public async Task UpsertAsync(
        LearningFeedbackEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedbackEvent);
        if (string.IsNullOrWhiteSpace(feedbackEvent.FeedbackId))
        {
            throw new ArgumentException("feedbackId is required.", nameof(feedbackEvent));
        }

        var normalized = Normalize(feedbackEvent);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("learning_feedback_events")} (
    feedback_id, workspace_id, collection_id, capability_id, target_id,
    target_type, feedback_kind, created_at, data)
VALUES (
    @feedback_id, @workspace_id, @collection_id, @capability_id, @target_id,
    @target_type, @feedback_kind, @created_at, @data)
ON CONFLICT (feedback_id) DO UPDATE SET
    workspace_id = EXCLUDED.workspace_id,
    collection_id = EXCLUDED.collection_id,
    capability_id = EXCLUDED.capability_id,
    target_id = EXCLUDED.target_id,
    target_type = EXCLUDED.target_type,
    feedback_kind = EXCLUDED.feedback_kind,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("feedback_id", normalized.FeedbackId);
        command.Parameters.AddWithValue("workspace_id", normalized.WorkspaceId);
        command.Parameters.AddWithValue("collection_id", normalized.CollectionId);
        command.Parameters.AddWithValue("capability_id", normalized.CapabilityId);
        command.Parameters.AddWithValue("target_id", normalized.TargetId);
        command.Parameters.AddWithValue("target_type", normalized.TargetType);
        command.Parameters.AddWithValue("feedback_kind", normalized.FeedbackKind);
        command.Parameters.AddWithValue("created_at", normalized.CreatedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LearningFeedbackEvent>> QueryAsync(
        LearningFeedbackEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = new List<string>();
        AddFilter(command, filters, "workspace_id", query.WorkspaceId);
        AddFilter(command, filters, "collection_id", query.CollectionId);
        AddFilter(command, filters, "capability_id", query.CapabilityId);
        AddFilter(command, filters, "target_id", query.TargetId);
        AddFilter(command, filters, "target_type", query.TargetType);
        AddFilter(command, filters, "feedback_kind", query.FeedbackKind);
        AddJsonFilter(command, filters, "Source", "source", "source", query.Source);
        AddJsonFilter(command, filters, "SourceOperationId", "sourceOperationId", "source_operation_id", query.SourceOperationId);
        command.Parameters.AddWithValue("take", TakeOrDefault(query.Limit));
        command.Parameters.AddWithValue("skip", Math.Max(0, query.Offset));
        command.CommandText = $"""
SELECT data
FROM {Table("learning_feedback_events")}
{BuildWhere(filters)}
ORDER BY created_at DESC, feedback_id ASC
LIMIT @take OFFSET @skip;
""";

        return await ReadFeedbackAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => CountTableAsync("learning_feedback_events", cancellationToken);

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
DELETE FROM {Table("learning_feedback_events")}
WHERE workspace_id = @workspace_id
  AND collection_id = @collection_id;
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteByFeedbackIdPrefixAsync(
        string feedbackIdPrefix,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedbackIdPrefix))
        {
            return 0;
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("learning_feedback_events")}
WHERE feedback_id LIKE @feedback_id_prefix;
""";
        command.Parameters.AddWithValue("feedback_id_prefix", $"{feedbackIdPrefix.Trim()}%");
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CountTableAsync(string table, CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {Table(table)};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<LearningFeedbackEvent>> ReadFeedbackAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<LearningFeedbackEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Serializer.Deserialize<LearningFeedbackEvent>(reader.GetString(0)));
        }

        return rows;
    }

    private static LearningFeedbackEvent Normalize(LearningFeedbackEvent source)
        => new()
        {
            FeedbackId = source.FeedbackId.Trim(),
            WorkspaceId = source.WorkspaceId,
            CollectionId = source.CollectionId,
            Source = source.Source,
            SourceOperationId = source.SourceOperationId,
            CapabilityId = source.CapabilityId,
            TargetId = source.TargetId,
            TargetType = source.TargetType,
            FeedbackKind = source.FeedbackKind,
            FeedbackValue = source.FeedbackValue,
            Reason = source.Reason,
            UserCorrection = source.UserCorrection,
            RedactionMode = source.RedactionMode,
            MetadataOnly = source.MetadataOnly,
            TrainingUse = string.IsNullOrWhiteSpace(source.TrainingUse) ? "disabled_until_review" : source.TrainingUse,
            Confidence = source.Confidence,
            CreatedAt = source.CreatedAt == default ? DateTimeOffset.UtcNow : source.CreatedAt,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    internal static void AddFilter(
        NpgsqlCommand command,
        List<string> filters,
        string columnName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var parameterName = columnName.Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase);
        filters.Add($"{columnName} = @{parameterName}");
        command.Parameters.AddWithValue(parameterName, value.Trim());
    }

    internal static void AddJsonFilter(
        NpgsqlCommand command,
        List<string> filters,
        string pascalKey,
        string camelKey,
        string parameterName,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        filters.Add($"(data ->> '{pascalKey}' = @{parameterName} OR data ->> '{camelKey}' = @{parameterName})");
        command.Parameters.AddWithValue(parameterName, value.Trim());
    }

    internal static string BuildWhere(IReadOnlyList<string> filters)
        => filters.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", filters)}";
}
