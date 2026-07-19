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

    /// <summary>R14-PG-8：查询已应用的 migration 历史，按 applied_at 升序。</summary>
    Task<IReadOnlyList<PostgresMigrationHistoryEntry>> GetMigrationHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// R14-PG-8：回滚到指定 schema 版本。
    /// 当前 baseline migration 不支持真实回滚（cumulative idempotent DDL），调用会返回 RolledBack=false
    /// 并在 Diagnostics 中说明原因。未来按版本切分的 migration 可支持真实 down DDL。
    /// </summary>
    Task<PostgresMigrationRollbackResult> RollbackAsync(
        string targetSchemaVersion,
        bool confirm,
        CancellationToken cancellationToken = default);
}

/// <summary>R14-PG-8：已应用的 migration 历史记录。</summary>
public sealed record PostgresMigrationHistoryEntry
{
    public string MigrationId { get; init; } = string.Empty;

    public string SchemaVersion { get; init; } = string.Empty;

    public DateTimeOffset AppliedAt { get; init; }

    public string? Checksum { get; init; }
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
