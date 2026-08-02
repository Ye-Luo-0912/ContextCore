namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 文件系统存储的配置选项。
/// </summary>
/// <remarks>
/// FileSystem 后端定位为 Alpha / 本地开发后端。
/// 并发边界（R13.1 #6）：单文件写入经 <see cref="FileLockProvider"/> 跨进程原子（Enqueue/Dequeue/Ack/Nack/Upsert/Update），
/// 读取经 FileShare.ReadWrite；但跨文件一致性（raw content + metadata 双文件）无事务原子性，
/// 进程崩溃可能留下 orphan raw 或 metadata 指向不存在的 raw。
/// 进程内优化（JobId→路径索引、Janitor 节流、ContextStateCache）在多进程下命中率下降但正确性不变（回退扫描/文件锁）。
/// 启动时调用 <see cref="FileSystemInstanceGuard.GetOrCreate"/>(<see cref="ResolvedRootPath"/>)
/// 可检测同一 root 是否已被他进程占用（advisory，不阻断）。正式多实例 / 生产部署应使用 Postgres 后端（<c>ContextCore.Storage.Postgres</c>）。
/// </remarks>
public sealed class FileStorageOptions
{
	/// <summary>配置系统中统一的存储根目录键名。</summary>
	public const string RootPathConfigurationKey = "Storage:RootPath";

	/// <summary>未显式配置时使用的项目内数据目录名称。</summary>
	public const string DefaultDataDirectoryName = "context-core-data";

	/// <summary>
	/// 跨项目统一的默认存储根目录。未显式配置时，数据写入仓库内专用目录
	/// <c>context-core-data</c>；若无法定位仓库根目录，则回退到当前应用目录下的同名目录。
	/// </summary>
	public static readonly string DefaultRootPath = ResolveDefaultRootPath();

	/// <summary>
	/// 获取或设置存储根目录路径。
	/// 空字符串或 <see langword="null"/> 时将在运行时回退到 <see cref="DefaultRootPath"/>。
	/// 支持环境变量展开；只有显式配置绝对路径时才会写到项目目录外。
	/// </summary>
	public string RootPath { get; set; } = DefaultRootPath;

	/// <summary>
	/// Trace 日期分片保留天数（按 UTC 自然日判定）。
	/// 保留今日与前 N 个完整自然日（共 N+1 天），第 N+1 天前的 yyyyMMdd 分片目录会在写入时被后台清理。
	/// 设为 0 禁用 retention（永久保留）。默认 30 天。
	/// </summary>
	public int TraceRetentionDays { get; set; } = 30;

	/// <summary>
	/// 获取经过环境变量展开和绝对化处理后的存储根目录路径。
	/// </summary>
	public string ResolvedRootPath => ResolveRootPath(RootPath);

	/// <summary>
	/// 统一解析 root path：空值使用默认目录，非空值先展开环境变量，再转为绝对路径。
	/// </summary>
	public static string ResolveRootPath(string? rootPath)
	{
		return Path.GetFullPath(
			string.IsNullOrWhiteSpace(rootPath)
				? DefaultRootPath
				: Environment.ExpandEnvironmentVariables(rootPath));
	}

	private static string ResolveDefaultRootPath()
	{
		var assemblyDirectory = Path.GetDirectoryName(typeof(FileStorageOptions).Assembly.Location);
		var directory = new DirectoryInfo(
			string.IsNullOrWhiteSpace(assemblyDirectory)
				? AppContext.BaseDirectory
				: assemblyDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "ContextCore.sln")))
			{
				return Path.Combine(directory.FullName, DefaultDataDirectoryName);
			}

			directory = directory.Parent;
		}

		return Path.Combine(
			string.IsNullOrWhiteSpace(assemblyDirectory)
				? AppContext.BaseDirectory
				: assemblyDirectory,
			DefaultDataDirectoryName);
	}
}
