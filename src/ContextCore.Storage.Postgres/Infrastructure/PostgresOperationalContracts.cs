using Npgsql;

namespace ContextCore.Storage.Postgres.Infrastructure;

public interface IPostgresConnectionFactory : IAsyncDisposable
{
    PostgresOptions Options { get; }

    ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> PingAsync(CancellationToken cancellationToken = default);
}

public interface IStoreMigrationRunner
{
    IReadOnlyList<PostgresStoreMigration> ListMigrations();

    Task<PostgresMigrationPlan> PreviewMigrationsAsync(CancellationToken cancellationToken = default);

    Task<PostgresMigrationApplyResult> ApplyMigrationsAsync(bool confirm, CancellationToken cancellationToken = default);

    Task<string?> GetAppliedVersionAsync(CancellationToken cancellationToken = default);
}

public sealed record PostgresStoreMigration
{
    public string MigrationId { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string SchemaVersion { get; init; } = string.Empty;

    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();
}

public sealed record PostgresMigrationPlan
{
    public bool DryRun { get; init; } = true;

    public bool ProviderEnabled { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string SchemaName { get; init; } = string.Empty;

    public string RedactedConnectionString { get; init; } = string.Empty;

    public string? CurrentSchemaVersion { get; init; }

    public IReadOnlyList<PostgresStoreMigration> Migrations { get; init; } = Array.Empty<PostgresStoreMigration>();

    public IReadOnlyList<string> PendingMigrations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingRequiredTables { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed record PostgresMigrationApplyResult
{
    public bool Applied { get; init; }

    public bool ConfirmRequired { get; init; }

    public string? SchemaVersion { get; init; }

    public IReadOnlyList<string> AppliedMigrations { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
