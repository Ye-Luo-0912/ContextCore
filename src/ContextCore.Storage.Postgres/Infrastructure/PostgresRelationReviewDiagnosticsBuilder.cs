using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>Postgres relation review / diagnostics provider 的只读诊断构建器。</summary>
public static class PostgresRelationReviewDiagnosticsBuilder
{
    public static PostgresRelationReviewProviderDiagnostics BuildNotConfigured(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new PostgresRelationReviewProviderDiagnostics
        {
            ProviderEnabled = false,
            ProviderId = options.ProviderId,
            UseForRuntime = false,
            RequiredIndexes = RequiredIndexNames(options),
            MissingRequiredIndexes = RequiredIndexNames(options),
            RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
            Diagnostics = ["NotConfigured"],
            Recommendation = "NotConfigured"
        };
    }

    public static async Task<PostgresRelationReviewProviderDiagnostics> BuildAsync(
        PostgresOptions options,
        PostgresConnectionFactory connectionFactory,
        PostgresMigrationRunner migrationRunner,
        bool useForRuntime = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(migrationRunner);

        var requiredIndexes = RequiredIndexNames(options);
        var ping = await connectionFactory.PingAsync(cancellationToken).ConfigureAwait(false);
        if (!ping.Success)
        {
            return new PostgresRelationReviewProviderDiagnostics
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
                    : ["BlockedByConnection", PostgresMigrationRunner.RedactConnectionString(ping.ErrorMessage)],
                Recommendation = "BlockedByConnection"
            };
        }

        var verification = await migrationRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var reviewsTable = PostgresNames.Table(options, "relation_reviews");
        var diagnosticsTable = PostgresNames.Table(options, "relation_diagnostics");
        var relationReviewsTableExists = await RelationExistsAsync(connection, reviewsTable, options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var relationDiagnosticsTableExists = await RelationExistsAsync(connection, diagnosticsTable, options.CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var missingIndexes = verification.MissingIndexes
            .Where(requiredIndexes.Contains)
            .ToArray();
        var reviewCount = relationReviewsTableExists
            ? await CountTableAsync(connection, reviewsTable, options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
            : 0;
        var diagnosticsCount = relationDiagnosticsTableExists
            ? await CountTableAsync(connection, diagnosticsTable, options.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false)
            : 0;

        var diagnostics = new List<string>();
        if (!relationReviewsTableExists)
        {
            diagnostics.Add("RelationReviewsTableMissing");
        }

        if (!relationDiagnosticsTableExists)
        {
            diagnostics.Add("RelationDiagnosticsTableMissing");
        }

        if (missingIndexes.Length > 0)
        {
            diagnostics.Add("RequiredRelationReviewIndexMissing");
        }

        if (useForRuntime)
        {
            diagnostics.Add("UseForRuntimeRequestedButPhaseDB2_1KeepsRuntimeFileSystem");
        }

        return new PostgresRelationReviewProviderDiagnostics
        {
            ProviderEnabled = options.Enabled,
            ProviderId = options.ProviderId,
            UseForRuntime = useForRuntime,
            ConnectionAvailable = true,
            SchemaVersion = verification.CurrentSchemaVersion,
            RelationReviewsTableExists = relationReviewsTableExists,
            RelationDiagnosticsTableExists = relationDiagnosticsTableExists,
            RequiredIndexes = requiredIndexes,
            MissingRequiredIndexes = missingIndexes,
            ReviewCount = reviewCount,
            DiagnosticsCount = diagnosticsCount,
            RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
            Diagnostics = diagnostics,
            Recommendation = diagnostics.Count == 0 ? "ReadyForParityEval" : "SchemaIncomplete"
        };
    }

    private static IReadOnlyList<string> RequiredIndexNames(PostgresOptions options)
    {
        return
        [
            PostgresNames.Index(options, "relation_reviews", "relation"),
            PostgresNames.Index(options, "relation_diagnostics", "relation"),
            PostgresNames.Index(options, "relation_diagnostics", "item"),
            PostgresNames.Index(options, "relation_diagnostics", "kind"),
            PostgresNames.Index(options, "relation_diagnostics", "severity")
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
}
