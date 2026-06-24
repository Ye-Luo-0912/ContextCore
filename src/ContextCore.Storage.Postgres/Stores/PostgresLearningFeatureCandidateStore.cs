using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 反馈特征候选存储；只用于离线候选 parity 和导出投影。</summary>
public sealed class PostgresLearningFeatureCandidateStore : PostgresStoreBase, ILearningFeatureCandidateStore
{
    public PostgresLearningFeatureCandidateStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task UpsertAsync(
        FeedbackFeatureCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            throw new ArgumentException("candidateId is required.", nameof(candidate));
        }

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("learning_feature_candidates")} (
    candidate_id, source_feedback_id, capability_id, label_kind, training_use,
    created_at, data)
VALUES (
    @candidate_id, @source_feedback_id, @capability_id, @label_kind, @training_use,
    @created_at, @data)
ON CONFLICT (candidate_id) DO UPDATE SET
    source_feedback_id = EXCLUDED.source_feedback_id,
    capability_id = EXCLUDED.capability_id,
    label_kind = EXCLUDED.label_kind,
    training_use = EXCLUDED.training_use,
    created_at = EXCLUDED.created_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("candidate_id", candidate.CandidateId.Trim());
        command.Parameters.AddWithValue("source_feedback_id", candidate.SourceFeedbackId);
        command.Parameters.AddWithValue("capability_id", candidate.CapabilityId);
        command.Parameters.AddWithValue("label_kind", candidate.LabelKind);
        command.Parameters.AddWithValue("training_use", candidate.TrainingUse);
        command.Parameters.AddWithValue("created_at", ResolveCreatedAt(candidate));
        AddJson(command, "data", candidate);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FeedbackFeatureCandidate>> QueryAsync(
        LearningFeatureCandidateQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = new List<string>();
        PostgresLearningFeedbackStore.AddFilter(command, filters, "candidate_id", query.CandidateId);
        PostgresLearningFeedbackStore.AddFilter(command, filters, "source_feedback_id", query.SourceFeedbackId);
        PostgresLearningFeedbackStore.AddFilter(command, filters, "capability_id", query.CapabilityId);
        PostgresLearningFeedbackStore.AddFilter(command, filters, "label_kind", query.LabelKind);
        PostgresLearningFeedbackStore.AddFilter(command, filters, "training_use", query.TrainingUse);
        PostgresLearningFeedbackStore.AddJsonFilter(command, filters, "TargetType", "targetType", "target_type", query.TargetType);
        command.Parameters.AddWithValue("take", TakeOrDefault(query.Limit));
        command.Parameters.AddWithValue("skip", Math.Max(0, query.Offset));
        command.CommandText = $"""
SELECT data
FROM {Table("learning_feature_candidates")}
{PostgresLearningFeedbackStore.BuildWhere(filters)}
ORDER BY created_at DESC, candidate_id ASC
LIMIT @take OFFSET @skip;
""";

        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => CountTableAsync("learning_feature_candidates", cancellationToken);

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
DELETE FROM {Table("learning_feature_candidates")}
WHERE (data -> 'Metadata' ->> 'workspaceId' = @workspace_id OR data -> 'metadata' ->> 'workspaceId' = @workspace_id)
  AND (data -> 'Metadata' ->> 'collectionId' = @collection_id OR data -> 'metadata' ->> 'collectionId' = @collection_id);
""";
        command.Parameters.AddWithValue("workspace_id", workspaceId);
        command.Parameters.AddWithValue("collection_id", collectionId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteBySourceFeedbackIdPrefixAsync(
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
DELETE FROM {Table("learning_feature_candidates")}
WHERE source_feedback_id LIKE @feedback_id_prefix;
""";
        command.Parameters.AddWithValue("feedback_id_prefix", $"{feedbackIdPrefix.Trim()}%");
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string ExportJsonLines(IEnumerable<FeedbackFeatureCandidate> candidates)
    {
        return string.Join(
            Environment.NewLine,
            candidates.Select(candidate => System.Text.Json.JsonSerializer.Serialize(candidate)));
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

    private async Task<IReadOnlyList<FeedbackFeatureCandidate>> ReadCandidatesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<FeedbackFeatureCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Serializer.Deserialize<FeedbackFeatureCandidate>(reader.GetString(0)));
        }

        return rows;
    }

    private static DateTimeOffset ResolveCreatedAt(FeedbackFeatureCandidate candidate)
    {
        return candidate.Metadata.TryGetValue("createdAt", out var value)
               && DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }
}
