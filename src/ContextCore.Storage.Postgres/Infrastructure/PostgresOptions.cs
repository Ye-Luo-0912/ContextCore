namespace ContextCore.Storage.Postgres;

/// <summary>PostgreSQL 存储后端配置。</summary>
public sealed class PostgresOptions
{
    /// <summary>PostgreSQL 连接字符串。涉及密码时应从用户目录私有配置或环境变量读取。</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>是否在首次访问存储时自动执行轻量建表迁移。</summary>
    public bool AutoMigrate { get; init; } = true;

    /// <summary>是否在迁移时创建 pgvector 扩展。</summary>
    public bool EnablePgVectorExtension { get; init; } = true;

    /// <summary>命令超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>表名前缀，默认使用 cc_ 避免和业务表冲突。</summary>
    public string TablePrefix { get; init; } = "cc_";
}