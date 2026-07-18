using ContextCore.Core;
using ContextCore.Storage.FileSystem;

namespace ContextCore.Service;

/// <summary>存储层配置选项，对应 appsettings.json 中的 <c>Storage</c> 节。</summary>
public sealed class StorageOptions
{
	/// <summary>
	/// 存储提供商类型：<c>filesystem</c> / <c>memory</c> / <c>postgres</c> 均可作为服务后端启动。
	/// <c>postgres</c> 已实现核心存储契约（context / memory / relation / vector / jobs 等，含 pgvector），
	/// 但 learning feedback / review store 仍为 <see cref="UnsupportedLearningFeedbackStore"/>，
	/// 待补齐后可移除 <see cref="AllowExperimentalPostgres"/> 标志位。
	/// </summary>
	public string Provider { get; set; } = "filesystem";

	/// <summary>
	/// 是否显式承认 PostgreSQL 仍处于实验阶段（部分学习反馈契约未实现）。
	/// 该标志位不阻止 postgres provider 启动，仅用于在日志和诊断中区分
	/// “误配置”与“明确尝试实验能力”，便于运维定位问题。
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

/// <summary>
/// R13-F：Package Template Cache Canary 配置。控制是否启用 Package Template 缓存的 canary 试点。
/// 默认关闭（<see cref="Enabled"/>=false）；启用时仅缓存 <see cref="AllowedWorkspaces"/> 列出的工作空间。
/// 启用前置条件：单实例（<see cref="RequireSingleInstance"/>）+ InMemory version store（生产已具备）。
/// </summary>
/// <remarks>
/// 启用流程：
/// 1. 在 appsettings.json 中将 <c>PackageTemplateCache:Enabled</c> 设为 <c>true</c>；
/// 2. 在 <c>AllowedWorkspaces</c> 中显式列出允许缓存的工作空间 ID（空列表 = 不缓存任何工作空间）；
/// 3. 重启 Service。
/// 多进程检测命中（<see cref="FileSystemInstanceGuard.IsMultiProcessDetected"/>）时即使 Enabled=true 也不会启用缓存，
/// 以保证 InMemory version store 与进程内缓存的一致性边界（advisory，不抛异常）。
/// </remarks>
public sealed class PackageTemplateCacheOptions
{
	/// <summary>是否启用 Package Template Cache canary。默认 false（生产关闭）。</summary>
	public bool Enabled { get; set; }

	/// <summary>
	/// 允许缓存的工作空间 ID 集合。空集合 = 不缓存任何工作空间（即使 <see cref="Enabled"/>=true）。
	/// 非空集合中未列出的工作空间请求仍走全量流水线，缓存路径仅对列表内工作空间生效。
	/// </summary>
	public List<string> AllowedWorkspaces { get; set; } = new();

	/// <summary>
	/// 缓存最大条目数。超过后按 CLOCK 策略淘汰。默认与 <see cref="InMemoryContextStateCache.DefaultMaxEntries"/> 一致。
	/// </summary>
	public int MaxEntries { get; set; } = 10_000;

	/// <summary>
	/// 缓存条目生存期（TTL）。null 表示无 TTL（仅由 scope 失效或 CLOCK 淘汰移除）。
	/// canary 阶段建议设置 TTL（如 00:10:00）以兜底防止版本失配漏网。
	/// </summary>
	public TimeSpan? Ttl { get; set; }

	/// <summary>
	/// factory 执行超时。null 表示仅依赖 shutdown 取消。
	/// canary 阶段建议设置超时（如 00:00:30）以防止 factory 长时间挂起阻塞 single-flight 等待者。
	/// </summary>
	public TimeSpan? FactoryTimeout { get; set; }

	/// <summary>
	/// 是否要求单实例才启用缓存。默认 true。
	/// 检测到多进程时（<see cref="FileSystemInstanceGuard.IsMultiProcessDetected"/>）即使 <see cref="Enabled"/>=true 也不会启用缓存。
	/// 设为 false 可在多进程下强制启用（不推荐——InMemory version store 多进程下命中率与正确性均会退化）。
	/// </summary>
	public bool RequireSingleInstance { get; set; } = true;
}

