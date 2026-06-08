using ContextCore.Storage.FileSystem;

namespace ContextCore.Service;

/// <summary>存储层配置选项，对应 appsettings.json 中的 <c>Storage</c> 节。</summary>
public sealed class StorageOptions
{
	/// <summary>
	/// 存储提供商类型：当前 Service-ready 的值为 <c>filesystem</c> 或 <c>memory</c>。
	/// <c>postgres</c> 仍处于 Experimental/Partial 状态，完整契约补齐前不允许作为服务后端启动。
	/// </summary>
	public string Provider { get; set; } = "filesystem";

	/// <summary>
	/// 是否显式承认 PostgreSQL 仍处于实验阶段。
	/// 当前版本即使设置为 <c>true</c> 也不会启动 PostgreSQL provider，
	/// 该开关仅用于让错误信息区分“误配置”和“明确尝试实验能力”。
	/// 注意：它不是绕过启动保护的开关，避免半成品 provider 造成数据分裂。
	/// </summary>
	public bool AllowExperimentalPostgres { get; set; }

	/// <summary>
	/// 文件系统存储的根目录路径（仅 Provider 为 <c>filesystem</c> 时生效）。
	/// 空字符串或未配置时自动回退到 <see cref="FileStorageOptions.DefaultRootPath"/>
	/// （即仓库根目录下的 <c>context-core-data</c> 专用目录）。
	/// 支持环境变量展开；只有显式配置绝对路径时才会写到项目目录外。
	/// </summary>
	public string RootPath { get; set; } = string.Empty;

	/// <summary>
	/// 经过环境变量展开和绝对化处理后的存储根目录路径，供 DI 注册和日志使用。
	/// </summary>
	public string ResolvedRootPath => FileStorageOptions.ResolveRootPath(RootPath);

	/// <summary>是否为内存模式。</summary>
	public bool IsMemory =>
		string.Equals(Provider, "memory", StringComparison.OrdinalIgnoreCase);

	/// <summary>是否为文件系统模式。</summary>
	public bool IsFileSystem =>
		string.Equals(Provider, "filesystem", StringComparison.OrdinalIgnoreCase);

	/// <summary>是否请求 PostgreSQL 实验后端。</summary>
	public bool IsPostgres =>
		string.Equals(Provider, "postgres", StringComparison.OrdinalIgnoreCase)
		|| string.Equals(Provider, "postgresql", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// PostgreSQL 连接字符串（仅 Provider 为 <c>postgres</c> 时生效）。
        /// 支持 <c>env:VAR_NAME</c> 格式，启动时自动替换为对应环境变量的值。
        /// </summary>
        public string PostgresConnectionString { get; set; } = string.Empty;

        /// <summary>经过环境变量展开后的 PostgreSQL 连接字符串。</summary>
        public string ResolvedPostgresConnectionString =>
                PostgresConnectionString.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
                        ? Environment.GetEnvironmentVariable(PostgresConnectionString[4..]) ?? string.Empty
                        : PostgresConnectionString;
}

/// <summary>压缩提供商配置选项，对应 appsettings.json 中的 <c>Compression</c> 节。</summary>
public sealed class CompressionProviderOptions
{
	public string Provider { get; set; } = "llm";
}

/// <summary>后台作业 worker 的轮询与启停配置。</summary>
public sealed class JobWorkerOptions
{
	public bool Enabled { get; set; } = true;

	public int PollIntervalMilliseconds { get; set; } = 1000;

	/// <summary>并发处理的作业数，默认 1（顺序处理）。
	/// 设为大于 1 时 worker 将同时从队列取出并并发执行多个作业。
	/// PostgreSQL 队列已使用 SELECT FOR UPDATE SKIP LOCKED 确保无重复消费。</summary>
	public int Concurrency { get; set; } = 1;
}

/// <summary>短期记忆维护 worker 的启停与周期配置。</summary>
public sealed class ShortTermMaintenanceOptions
{
	public bool Enabled { get; set; }

	public bool RunOnStartup { get; set; }

	public int IntervalSeconds { get; set; } = 300;
}

/// <summary>Lifecycle-aware ranker shadow 配置。Enabled 控制运行时 shadow，DebugEndpointEnabled 仅控制只读 debug endpoint。</summary>
public sealed class LearningRankerShadowOptions
{
	public bool Enabled { get; set; }

	public bool DebugEndpointEnabled { get; set; } = true;

	public bool TraceCollectionEnabled { get; set; }

	public int MaxCandidatesPerTrace { get; set; } = 50;

	public string Profile { get; set; } = "lifecycle-aware-v1";
}
