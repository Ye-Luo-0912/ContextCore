namespace ContextCore.Storage.FileSystem;

/// <summary>
/// 文件系统存储的配置选项。
/// </summary>
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
