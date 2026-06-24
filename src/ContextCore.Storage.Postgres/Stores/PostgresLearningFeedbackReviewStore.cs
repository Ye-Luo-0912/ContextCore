using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>PostgreSQL 反馈审核记录存储；保持按 feedbackId 覆盖的现有审核语义。</summary>
public sealed class PostgresLearningFeedbackReviewStore : PostgresStoreBase, ILearningFeedbackReviewStore
{
    public PostgresLearningFeedbackReviewStore(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    public async Task UpsertAsync(
        LearningFeedbackReviewRecord review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (string.IsNullOrWhiteSpace(review.FeedbackId))
        {
            throw new ArgumentException("feedbackId is required.", nameof(review));
        }

        var normalized = Normalize(review);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("learning_feedback_reviews")} (
    feedback_id, review_id, review_status, reviewer, reviewed_at, data)
VALUES (
    @feedback_id, @review_id, @review_status, @reviewer, @reviewed_at, @data)
ON CONFLICT (feedback_id, review_id) DO UPDATE SET
    review_status = EXCLUDED.review_status,
    reviewer = EXCLUDED.reviewer,
    reviewed_at = EXCLUDED.reviewed_at,
    data = EXCLUDED.data;
""";
        command.Parameters.AddWithValue("feedback_id", normalized.FeedbackId);
        command.Parameters.AddWithValue("review_id", BuildReviewId(normalized.FeedbackId));
        command.Parameters.AddWithValue("review_status", normalized.ReviewStatus.ToString());
        command.Parameters.AddWithValue("reviewer", normalized.Reviewer);
        command.Parameters.AddWithValue("reviewed_at", normalized.ReviewedAt);
        AddJson(command, "data", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LearningFeedbackReviewRecord>> QueryAsync(
        LearningFeedbackReviewQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        var filters = new List<string>();
        PostgresLearningFeedbackStore.AddFilter(command, filters, "feedback_id", query.FeedbackId);
        if (query.ReviewStatus is not null)
        {
            filters.Add("review_status = @review_status");
            command.Parameters.AddWithValue("review_status", query.ReviewStatus.Value.ToString());
        }

        PostgresLearningFeedbackStore.AddFilter(command, filters, "reviewer", query.Reviewer);
        command.Parameters.AddWithValue("take", TakeOrDefault(query.Limit));
        command.Parameters.AddWithValue("skip", Math.Max(0, query.Offset));
        command.CommandText = $"""
SELECT data
FROM {Table("learning_feedback_reviews")}
{PostgresLearningFeedbackStore.BuildWhere(filters)}
ORDER BY reviewed_at DESC, feedback_id ASC
LIMIT @take OFFSET @skip;
""";

        return await ReadReviewsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LearningFeedbackReviewRecord?> GetLatestReviewAsync(
        string feedbackId,
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            new LearningFeedbackReviewQuery { FeedbackId = feedbackId, Limit = 1 },
            cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => CountTableAsync("learning_feedback_reviews", cancellationToken);

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
DELETE FROM {Table("learning_feedback_reviews")}
WHERE (data -> 'Metadata' ->> 'workspaceId' = @workspace_id OR data -> 'metadata' ->> 'workspaceId' = @workspace_id)
  AND (data -> 'Metadata' ->> 'collectionId' = @collection_id OR data -> 'metadata' ->> 'collectionId' = @collection_id);
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
DELETE FROM {Table("learning_feedback_reviews")}
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

    private async Task<IReadOnlyList<LearningFeedbackReviewRecord>> ReadReviewsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<LearningFeedbackReviewRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Serializer.Deserialize<LearningFeedbackReviewRecord>(reader.GetString(0)));
        }

        return rows;
    }

    private static LearningFeedbackReviewRecord Normalize(LearningFeedbackReviewRecord source)
        => new()
        {
            FeedbackId = source.FeedbackId.Trim(),
            Reviewer = source.Reviewer,
            ReviewStatus = source.ReviewStatus,
            ReviewReason = source.ReviewReason,
            ApprovedCapability = source.ApprovedCapability,
            ApprovedLabelKind = source.ApprovedLabelKind,
            RedactionChecked = source.RedactionChecked,
            TrainingUse = string.IsNullOrWhiteSpace(source.TrainingUse) ? "disabled_until_review" : source.TrainingUse,
            ReviewedAt = source.ReviewedAt == default ? DateTimeOffset.UtcNow : source.ReviewedAt,
            Metadata = new Dictionary<string, string>(source.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static string BuildReviewId(string feedbackId) => $"review-{feedbackId.Trim()}";
}
