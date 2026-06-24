using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>PostgreSQL learning feedback provider 的只读诊断构建器。</summary>
public static class PostgresLearningFeedbackDiagnosticsBuilder
{
    public static PostgresLearningFeedbackDiagnosticsReport BuildNotConfigured(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new PostgresLearningFeedbackDiagnosticsReport
        {
            ProviderEnabled = false,
            UseForRuntime = false,
            Diagnostics = ["NotConfigured"],
            Status = "NotConfigured"
        };
    }

    public static async Task<PostgresLearningFeedbackDiagnosticsReport> BuildAsync(
        PostgresOptions options,
        PostgresConnectionFactory connectionFactory,
        PostgresMigrationRunner migrationRunner,
        bool useForRuntime = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(migrationRunner);

        var ping = await connectionFactory.PingAsync(cancellationToken).ConfigureAwait(false);
        if (!ping.Success)
        {
            return new PostgresLearningFeedbackDiagnosticsReport
            {
                ProviderEnabled = options.Enabled,
                ConnectionAvailable = false,
                UseForRuntime = useForRuntime,
                Diagnostics = string.IsNullOrWhiteSpace(ping.ErrorMessage)
                    ? ["BlockedByConnection"]
                    : ["BlockedByConnection", PostgresMigrationRunner.RedactConnectionString(ping.ErrorMessage)],
                Status = "BlockedByConnection"
            };
        }

        var verification = await migrationRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var feedbackTable = PostgresNames.Table(options, "learning_feedback_events");
        var reviewTable = PostgresNames.Table(options, "learning_feedback_reviews");
        var candidateTable = PostgresNames.Table(options, "learning_feature_candidates");
        var feedbackTableExists = await RelationExistsAsync(connection, feedbackTable, options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var reviewTableExists = await RelationExistsAsync(connection, reviewTable, options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var candidateTableExists = await RelationExistsAsync(connection, candidateTable, options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var requiredIndexes = RequiredLearningIndexNames(options);
        var missingIndexes = verification.MissingIndexes.Where(requiredIndexes.Contains).ToArray();
        var diagnostics = new List<string>();
        if (!feedbackTableExists)
        {
            diagnostics.Add("LearningFeedbackEventsTableMissing");
        }

        if (!reviewTableExists)
        {
            diagnostics.Add("LearningFeedbackReviewsTableMissing");
        }

        if (!candidateTableExists)
        {
            diagnostics.Add("LearningFeatureCandidatesTableMissing");
        }

        if (missingIndexes.Length > 0)
        {
            diagnostics.Add("LearningFeedbackRequiredIndexMissing");
        }

        if (useForRuntime)
        {
            diagnostics.Add("UseForRuntimeRequestedButDB3KeepsRuntimeFileSystem");
        }

        return new PostgresLearningFeedbackDiagnosticsReport
        {
            ProviderEnabled = options.Enabled,
            ConnectionAvailable = true,
            SchemaVersion = verification.CurrentSchemaVersion ?? string.Empty,
            FeedbackTableExists = feedbackTableExists,
            ReviewTableExists = reviewTableExists,
            FeatureCandidateTableExists = candidateTableExists,
            RequiredIndexesExist = missingIndexes.Length == 0,
            FeedbackCount = feedbackTableExists
                ? await CountTableAsync(connection, feedbackTable, options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
                : 0,
            ReviewCount = reviewTableExists
                ? await CountTableAsync(connection, reviewTable, options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
                : 0,
            FeatureCandidateCount = candidateTableExists
                ? await CountTableAsync(connection, candidateTable, options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
                : 0,
            UseForRuntime = useForRuntime,
            Diagnostics = diagnostics,
            Status = diagnostics.Count == 0 ? "ReadyForParityEval" : "SchemaIncomplete"
        };
    }

    private static IReadOnlyList<string> RequiredLearningIndexNames(PostgresOptions options)
        =>
        [
            PostgresNames.Index(options, "learning_feedback_events", "capability"),
            PostgresNames.Index(options, "learning_feedback_reviews", "status"),
            PostgresNames.Index(options, "learning_feature_candidates", "capability")
        ];

    private static async Task<bool> RelationExistsAsync(
        Npgsql.NpgsqlConnection connection,
        string qualifiedName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeoutSeconds;
        command.CommandText = "SELECT to_regclass(@relation_name)::text;";
        command.Parameters.AddWithValue("relation_name", qualifiedName);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text && !string.IsNullOrWhiteSpace(text);
    }

    private static async Task<int> CountTableAsync(
        Npgsql.NpgsqlConnection connection,
        string tableName,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = timeoutSeconds;
        command.CommandText = $"SELECT count(*) FROM {tableName};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
