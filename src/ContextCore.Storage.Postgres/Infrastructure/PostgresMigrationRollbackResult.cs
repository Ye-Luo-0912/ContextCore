namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>
/// Migration rollback 操作结果。。
/// </summary>
public sealed record PostgresMigrationRollbackResult
{
    /// <summary>是否实际执行了 rollback（confirm=true 且 target version 合法且所有相关 migration 支持 rollback）。</summary>
    public bool RolledBack { get; init; }

    /// <summary>是否需要 confirm。confirm=false 时为 true， RolledBack=false。</summary>
    public bool ConfirmRequired { get; init; }

    /// <summary>回滚前的 schema 版本。</summary>
    public string? PreviousSchemaVersion { get; init; }

    /// <summary>目标回滚到的 schema 版本。</summary>
    public string? TargetSchemaVersion { get; init; }

    /// <summary>回滚后的实际 schema 版本（成功时等于 target，失败时为 null）。</summary>
    public string? ActualSchemaVersion { get; init; }

    /// <summary>本次回滚涉及的 migration id 列表（按应用倒序）。</summary>
    public IReadOnlyList<string> RolledBackMigrations { get; init; } = Array.Empty<string>();

    /// <summary>诊断信息（不支持回滚的原因、连接错误等）。</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}
