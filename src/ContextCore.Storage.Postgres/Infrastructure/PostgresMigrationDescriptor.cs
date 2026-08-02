namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// 单个 migration 的不可变描述符，用于版本化迁移注册表和回滚判断。
/// 为既有 cumulative idempotent baseline 引入版本化与回滚元数据。
/// </summary>
public sealed record PostgresMigrationDescriptor
{
    /// <summary>Migration 唯一标识，例如 "0001_operational_store_baseline"。</summary>
    public string MigrationId { get; init; } = string.Empty;

    /// <summary>该 migration 应用后的 schema 版本，例如 "cc-schema-v13"。</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>人类可读描述。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// 该 migration 是否支持 rollback。
    /// 基线 cumulative idempotent migration 不支持（DROP TABLE 会丢失数据）。
    /// 未来按版本切分的 migration 可设为 true 并提供真实 down DDL。
    /// </summary>
    public bool SupportsRollback { get; init; }

    /// <summary>
    /// 该 migration 引入的表后缀列表（用于审计和未来 rollback 规划）。
    /// 基线 migration 引入全部 RequiredOperationalTableSuffixes。
    /// </summary>
    public IReadOnlyList<string> IntroducedTableSuffixes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 不支持回滚时的明确原因（SupportsRollback=false 时必填）。
    /// </summary>
    public string RollbackNotSupportedReason { get; init; } = string.Empty;
}
