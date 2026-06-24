namespace ContextCore.Storage.Postgres.Infrastructure;

/// <summary>PostgreSQL 存储后端配置。</summary>
public class PostgresOptions
{
    /// <summary>是否启用 PostgreSQL operational store 能力；默认关闭，避免误触发连接或迁移。</summary>
    public bool Enabled { get; init; }

    /// <summary>PostgreSQL 连接字符串。涉及密码时应从用户目录私有配置或环境变量读取。</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>是否在首次访问存储时自动执行轻量建表迁移。</summary>
    public bool AutoMigrate { get; init; } = true;

    /// <summary>可选 schema 名称；为空时使用连接默认 search_path，保持旧表名兼容。</summary>
    public string SchemaName { get; init; } = string.Empty;

    /// <summary>是否在迁移时创建 pgvector 扩展。</summary>
    public bool EnablePgVectorExtension { get; init; } = true;

    /// <summary>命令超时时间（秒）。</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>表名前缀，默认使用 cc_ 避免和业务表冲突。</summary>
    public string TablePrefix { get; init; } = "cc_";

    /// <summary>provider 标识，写入 diagnostics 时不包含敏感连接信息。</summary>
    public string ProviderId { get; init; } = "postgres-operational-v1";
}

/// <summary>DB1 operational store 配置契约；与旧 <see cref="PostgresOptions"/> 保持兼容。</summary>
public sealed class PostgresStoreOptions : PostgresOptions;
