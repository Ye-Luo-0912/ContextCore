using ContextCore.Storage.Postgres;

namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// 版本化 migration 注册表，按版本顺序排列。
/// R14-PG-8：当前只包含基线 migration（cumulative idempotent），未来可追加按版本切分的 migration。
/// 注册表是不可变的，启动时构建一次。
/// </summary>
public static class PostgresMigrationRegistry
{
    /// <summary>
    /// 已知 migration 列表，按 SchemaVersion 升序排列。
    /// </summary>
    public static IReadOnlyList<PostgresMigrationDescriptor> Migrations { get; } = new[]
    {
        new PostgresMigrationDescriptor
        {
            MigrationId = PostgresMigrationRunner.BaselineMigrationId,
            SchemaVersion = PostgresMigrationRunner.SchemaVersion,
            Description = "Baseline operational store: 所有 ContextCore Data Plane 表与索引的 cumulative idempotent DDL（CREATE TABLE IF NOT EXISTS）。包含 R14-PG-1~6 引入的全部表。",
            SupportsRollback = false,
            IntroducedTableSuffixes = PostgresMigrationRunner.RequiredOperationalTableSuffixes,
            RollbackNotSupportedReason = "Baseline migration 是 cumulative idempotent DDL，DROP TABLE 会丢失业务数据。回滚请使用备份恢复（R14-PG-10 PITR runbook）或显式 DROP SCHEMA（仅 smoke 测试场景）。"
        }
    };

    /// <summary>按 schema 版本号查找 migration 描述符，找不到返回 null。</summary>
    public static PostgresMigrationDescriptor? FindByVersion(string schemaVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        return Migrations.FirstOrDefault(m => string.Equals(m.SchemaVersion, schemaVersion, StringComparison.Ordinal));
    }

    /// <summary>按 migration id 查找描述符，找不到返回 null。</summary>
    public static PostgresMigrationDescriptor? FindById(string migrationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        return Migrations.FirstOrDefault(m => string.Equals(m.MigrationId, migrationId, StringComparison.Ordinal));
    }

    /// <summary>返回所有 migration 的简短列表（用于 ListMigrations API）。</summary>
    public static IReadOnlyList<PostgresStoreMigration> ToStoreMigrationList()
    {
        return Migrations
            .Select(m => new PostgresStoreMigration
            {
                MigrationId = m.MigrationId,
                Description = m.Description,
                SchemaVersion = m.SchemaVersion,
                RequiredTables = m.IntroducedTableSuffixes
                    .Select(suffix => suffix)
                    .ToArray()
            })
            .ToArray();
    }
}
