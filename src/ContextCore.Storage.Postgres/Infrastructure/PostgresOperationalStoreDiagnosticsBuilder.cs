using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.Postgres.Infrastructure;

public static class PostgresOperationalStoreDiagnosticsBuilder
{
    public static PostgresOperationalStoreDiagnostics BuildNotConfigured(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PostgresOperationalStoreDiagnostics
        {
            ProviderEnabled = false,
            ProviderId = string.IsNullOrWhiteSpace(options.ProviderId) ? "postgres-operational-v1" : options.ProviderId,
            Status = "NotConfigured",
            ConnectionAvailable = false,
            SchemaExists = false,
            CurrentSchemaVersion = null,
            PendingMigrations = 1,
            TableCount = 0,
            RequiredTableMissingCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
            ProviderCapabilityStatus = "Disabled",
            RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
            AutoMigrate = options.AutoMigrate,
            RequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
            MissingRequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
            SchemaVerification = BuildNotConfiguredVerification(options),
            Diagnostics = ["NotConfigured"]
        };
    }

    public static async Task<PostgresOperationalStoreDiagnostics> BuildAsync(
        PostgresOptions options,
        IPostgresConnectionFactory? connectionFactory,
        IStoreMigrationRunner? migrationRunner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ConnectionString) || connectionFactory is null || migrationRunner is null)
        {
            return BuildNotConfigured(options);
        }

        var diagnostics = new List<string>();
        var ping = await connectionFactory.PingAsync(cancellationToken).ConfigureAwait(false);
        if (!ping.Success)
        {
            diagnostics.Add("ConnectionUnavailable");
            if (!string.IsNullOrWhiteSpace(ping.ErrorMessage))
            {
                diagnostics.Add(RedactDiagnostic(ping.ErrorMessage));
            }

            return new PostgresOperationalStoreDiagnostics
            {
                ProviderEnabled = true,
                ProviderId = options.ProviderId,
                Status = "ConnectionUnavailable",
                ConnectionAvailable = false,
                SchemaExists = false,
                PendingMigrations = 1,
                RequiredTableMissingCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                ProviderCapabilityStatus = "Unavailable",
                RedactedConnectionString = PostgresMigrationRunner.RedactConnectionString(options.ConnectionString),
                AutoMigrate = options.AutoMigrate,
                RequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                MissingRequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                SchemaVerification = new PostgresSchemaVerificationReport
                {
                    ProviderEnabled = true,
                    ConnectionAvailable = false,
                    SchemaName = options.SchemaName,
                    RequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                    MissingRequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
                    RequiredIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                    MissingIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
                    RequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                    MissingRequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
                    RequiredIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                    MissingIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
                    Diagnostics = diagnostics,
                    Recommendation = "BlockedByConnection"
                },
                Diagnostics = diagnostics
            };
        }

        var plan = await migrationRunner.PreviewMigrationsAsync(cancellationToken).ConfigureAwait(false);
        var tableCount = Math.Max(0, plan.RequiredTables.Count - plan.MissingRequiredTables.Count);
        var status = plan.PendingMigrations.Count == 0 && plan.MissingRequiredTables.Count == 0
            ? "Ready"
            : "MigrationPending";

        return new PostgresOperationalStoreDiagnostics
        {
            ProviderEnabled = true,
            ProviderId = options.ProviderId,
            Status = status,
            ConnectionAvailable = true,
            SchemaExists = tableCount > 0,
            CurrentSchemaVersion = plan.CurrentSchemaVersion,
            PendingMigrations = plan.PendingMigrations.Count,
            TableCount = tableCount,
            RequiredTableMissingCount = plan.MissingRequiredTables.Count,
            ProviderCapabilityStatus = status == "Ready" ? "Ready" : "MigrationRequired",
            RedactedConnectionString = plan.RedactedConnectionString,
            AutoMigrate = options.AutoMigrate,
            RequiredTables = plan.RequiredTables,
            MissingRequiredTables = plan.MissingRequiredTables,
            SchemaVerification = migrationRunner is PostgresMigrationRunner postgresRunner
                ? await postgresRunner.VerifySchemaAsync(cancellationToken).ConfigureAwait(false)
                : null,
            Diagnostics = plan.Diagnostics
        };
    }

    private static PostgresSchemaVerificationReport BuildNotConfiguredVerification(PostgresOptions options)
    {
        return new PostgresSchemaVerificationReport
        {
            ProviderEnabled = false,
            ConnectionAvailable = false,
            SchemaName = options.SchemaName,
            RequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
            MissingRequiredTableCount = PostgresMigrationRunner.RequiredOperationalTableSuffixes.Count,
            RequiredIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
            MissingIndexCount = PostgresMigrationRunner.RequiredOperationalIndexDefinitions.Count,
            RequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
            MissingRequiredTables = PostgresMigrationRunner.GetRequiredTableNames(options),
            RequiredIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
            MissingIndexes = PostgresMigrationRunner.GetRequiredIndexNames(options),
            Diagnostics = ["NotConfigured"],
            Recommendation = "NotConfigured"
        };
    }

    private static string RedactDiagnostic(string message)
    {
        return message.Replace("Password=", "Password=***;", StringComparison.OrdinalIgnoreCase);
    }
}
