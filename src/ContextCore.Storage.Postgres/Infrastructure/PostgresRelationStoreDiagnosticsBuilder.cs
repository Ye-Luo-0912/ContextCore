using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>Postgres RelationStore provider 的只读诊断构建器。</summary>
public static class PostgresRelationStoreDiagnosticsBuilder
{
    public static PostgresRelationStoreDiagnostics BuildNotConfigured(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new PostgresRelationStoreDiagnostics
        {
            ProviderEnabled = false,
            ProviderId = options.ProviderId,
            UseForRuntime = false,
            RelationTableExists = false,
            RelationReviewsTableExists = false,
            RequiredIndexes = RequiredRelationIndexNames(options),
            MissingRequiredIndexes = RequiredRelationIndexNames(options),
            RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
            Diagnostics = ["NotConfigured"],
            Recommendation = "NotConfigured"
        };
    }

    public static async Task<PostgresRelationStoreDiagnostics> BuildAsync(
        PostgresOptions options,
        PostgresConnectionFactory connectionFactory,
        PostgresMigrationRunner migrationRunner,
        bool useForRuntime = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(migrationRunner);

        var requiredIndexes = RequiredRelationIndexNames(options);
        var ping = await connectionFactory.PingAsync(cancellationToken).ConfigureAwait(false);
        if (!ping.Success)
        {
            return new PostgresRelationStoreDiagnostics
            {
                ProviderEnabled = options.Enabled,
                ProviderId = options.ProviderId,
                UseForRuntime = useForRuntime,
                ConnectionAvailable = false,
                RequiredIndexes = requiredIndexes,
                MissingRequiredIndexes = requiredIndexes,
                RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
                Diagnostics = string.IsNullOrWhiteSpace(ping.ErrorMessage)
                    ? ["BlockedByConnection"]
                    : ["BlockedByConnection", RedactDiagnostic(ping.ErrorMessage)],
                Recommendation = "BlockedByConnection"
            };
        }

        var verification = await migrationRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var relationTableExists = await RelationExistsAsync(
            connection,
            PostgresNames.Table(options, "relations"),
            options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        var relationReviewsTableExists = await RelationExistsAsync(
            connection,
            PostgresNames.Table(options, "relation_reviews"),
            options.CommandTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        var missingIndexes = verification.MissingIndexes
            .Where(requiredIndexes.Contains)
            .ToArray();
        var relationCount = relationTableExists
            ? await CountTableAsync(connection, PostgresNames.Table(options, "relations"), options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
            : 0;
        var reviewCount = relationReviewsTableExists
            ? await CountTableAsync(connection, PostgresNames.Table(options, "relation_reviews"), options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
            : 0;

        var diagnostics = new List<string>();
        if (!relationTableExists)
        {
            diagnostics.Add("RelationTableMissing");
        }

        if (!relationReviewsTableExists)
        {
            diagnostics.Add("RelationReviewsTableMissing");
        }

        if (missingIndexes.Length > 0)
        {
            diagnostics.Add("RequiredRelationIndexMissing");
        }

        if (useForRuntime)
        {
            diagnostics.Add("UseForRuntimeRequestedButPhaseDB2KeepsRuntimeFileSystem");
        }

        var recommendation = diagnostics.Count == 0 ? "ReadyForParityEval" : "SchemaIncomplete";
        return new PostgresRelationStoreDiagnostics
        {
            ProviderEnabled = options.Enabled,
            ProviderId = options.ProviderId,
            UseForRuntime = useForRuntime,
            ConnectionAvailable = true,
            SchemaVersion = verification.CurrentSchemaVersion,
            RelationTableExists = relationTableExists,
            RelationReviewsTableExists = relationReviewsTableExists,
            RequiredIndexes = requiredIndexes,
            MissingRequiredIndexes = missingIndexes,
            RelationCount = relationCount,
            ReviewCount = reviewCount,
            RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
            Diagnostics = diagnostics,
            Recommendation = recommendation
        };
    }

    private static IReadOnlyList<string> RequiredRelationIndexNames(PostgresOptions options)
    {
        return
        [
            PostgresNames.Index(options, "relations", "source"),
            PostgresNames.Index(options, "relations", "target"),
            PostgresNames.Index(options, "relations", "type"),
            PostgresNames.Index(options, "relation_reviews", "relation")
        ];
    }

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

    private static string RedactDiagnostic(string message)
    {
        return PostgresMigrationRunner.RedactConnectionString(message);
    }
}
