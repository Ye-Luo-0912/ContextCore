using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.Postgres.Backup;

/// <summary>
/// 备份清单生成器。遍历数据根目录、计算每个文件的 SHA-256，
/// 同时为归档（ZIP）本身计算整体哈希，生成 <see cref="BackupManifest"/>。
/// </summary>
/// <remarks>
/// 设计选择：
/// <list type="bullet">
/// <item>流式遍历目录，避免一次性枚举大量文件占用内存。</item>
/// <item>使用 <see cref="Sha256Utility.HashFile"/> 复用 FileShare.ReadWrite | Delete 语义，与运行时并发读取兼容。</item>
/// <item>Postgres 转储条目（如果存在）由调用方先写入临时目录，再交给本类统一扫描。</item>
/// <item>不修改文件系统；只读取。</item>
/// <item>JSON 序列化采用 <see cref="JsonStringEnumConverter"/>，与 ContextCore 现有 artifact/eval 序列化约定一致；枚举以字符串形式记录便于人工审阅。</item>
/// </list>
/// </remarks>
public static class BackupManifestGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 备份清单使用的 JSON 序列化选项：camelCase 属性名 + 字符串枚举 + 缩进。
    /// 与 ContextCore 现有 artifact/eval 序列化约定一致，便于人工审阅与跨工具一致性。
    /// </summary>
    public static JsonSerializerOptions SerializerOptions => JsonOptions;

    /// <summary>
    /// 为 ZIP 归档生成清单：解压后逐文件计算 SHA-256，并计算 ZIP 本身的哈希与大小。
    /// </summary>
    /// <param name="archivePath">ZIP 归档路径。</param>
    /// <param name="sourceDescription">源描述（如数据根目录或 Postgres 连接目标，不含凭据）。</param>
    /// <param name="sourceKind">源存储类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对应归档的备份清单。</returns>
    public static async Task<BackupManifest> ForZipAsync(
        string archivePath,
        string sourceDescription,
        BackupStorageKind sourceKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("备份归档不存在。", archivePath);
        }

        var archiveInfo = new FileInfo(archivePath);
        var archiveHash = Sha256Utility.HashFile(archivePath);
        var entries = new List<BackupManifestEntry>();
        var createdUtc = DateTimeOffset.UtcNow;

        using var archiveStream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read);

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
            {
                // 目录条目：跳过
                continue;
            }

            using var entryStream = entry.Open();
            using var ms = new MemoryStream(checked((int)Math.Min(entry.Length, 1024 * 1024)));
            await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

            var hash = Sha256Utility.HashStream(new MemoryStream(ms.ToArray()));
            entries.Add(new BackupManifestEntry
            {
                RelativePath = entry.FullName.Replace('\\', '/'),
                SizeBytes = entry.Length,
                ContentHash = hash,
                StorageKind = BackupStorageKind.FileSystem,
                LastModifiedUtc = entry.LastWriteTime.UtcDateTime,
                Category = InferCategory(entry.FullName)
            });
        }

        return new BackupManifest
        {
            SchemaVersion = "v1",
            ArchiveName = Path.GetFileName(archivePath),
            ArchiveSizeBytes = archiveInfo.Length,
            ArchiveHash = archiveHash,
            CreatedAtUtc = createdUtc,
            SourceDescription = sourceDescription,
            SourceKind = sourceKind,
            Entries = entries
        };
    }

    /// <summary>
    /// 为数据根目录生成清单（不打包），用于校验源端完整性的场景。
    /// </summary>
    /// <param name="dataRoot">数据根目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>源端清单；ArchiveName/ArchiveHash 为空（无归档）。</returns>
    public static async Task<BackupManifest> ForDataRootAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Directory.Exists(dataRoot))
        {
            throw new DirectoryNotFoundException($"数据根目录不存在：{dataRoot}");
        }

        var entries = new List<BackupManifestEntry>();
        var createdUtc = DateTimeOffset.UtcNow;

        foreach (var file in EnumerateFilesSafe(dataRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists || (info.Attributes & FileAttributes.Hidden) != 0)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(dataRoot, file).Replace('\\', '/');
            var hash = await Task.Run(() => Sha256Utility.HashFile(file), cancellationToken).ConfigureAwait(false);
            entries.Add(new BackupManifestEntry
            {
                RelativePath = relativePath,
                SizeBytes = info.Length,
                ContentHash = hash,
                StorageKind = BackupStorageKind.FileSystem,
                LastModifiedUtc = info.LastWriteTimeUtc,
                Category = InferCategory(relativePath)
            });
        }

        return new BackupManifest
        {
            SchemaVersion = "v1",
            ArchiveName = string.Empty,
            ArchiveSizeBytes = 0,
            ArchiveHash = string.Empty,
            CreatedAtUtc = createdUtc,
            SourceDescription = dataRoot,
            SourceKind = BackupStorageKind.FileSystem,
            Entries = entries
        };
    }

    /// <summary>
    /// 为 PostgreSQL 转储文件（pg_dump -Fc）生成清单。
    /// 清单中包含转储文件本身（作为归档）与每个表的元数据条目。
    /// </summary>
    /// <param name="dumpPath">.dump 文件路径。</param>
    /// <param name="connectionStringDescription">连接描述（不含凭据）；若传入原始连接字符串，将自动调用 <see cref="StripCredentialsFromConnectionString"/> 去除密码。</param>
    /// <param name="dumpResult">已完成的 <see cref="PostgresDumpResult"/>（含表清单与文件哈希）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>对应 PostgreSQL 转储的备份清单。</returns>
    public static async Task<BackupManifest> ForPostgresDumpAsync(
        string dumpPath,
        string connectionStringDescription,
        PostgresDumpResult dumpResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);
        ArgumentNullException.ThrowIfNull(dumpResult);
        if (!File.Exists(dumpPath))
        {
            throw new FileNotFoundException("PostgreSQL 转储文件不存在。", dumpPath);
        }

        var archiveInfo = new FileInfo(dumpPath);
        var archiveHash = await Task.Run(() => Sha256Utility.HashFile(dumpPath), cancellationToken).ConfigureAwait(false);
        var createdUtc = DateTimeOffset.UtcNow;
        var safeDescription = StripCredentialsFromConnectionString(connectionStringDescription);

        var entries = new List<BackupManifestEntry>
        {
            new()
            {
                RelativePath = $"postgres://dump/{Path.GetFileName(dumpPath)}",
                SizeBytes = archiveInfo.Length,
                ContentHash = archiveHash,
                StorageKind = BackupStorageKind.Postgres,
                LastModifiedUtc = archiveInfo.LastWriteTimeUtc,
                Category = "postgres.dump"
            }
        };

        foreach (var table in dumpResult.Tables)
        {
            entries.Add(new BackupManifestEntry
            {
                RelativePath = $"postgres://{table.Schema}.{table.Name}",
                SizeBytes = table.ApproximateBytes,
                ContentHash = string.Empty, // 表级哈希由 dump 文件统一覆盖；保留空以与 ForZip 行为对齐
                StorageKind = BackupStorageKind.Postgres,
                LastModifiedUtc = createdUtc,
                Category = "postgres.table"
            });
        }

        return new BackupManifest
        {
            SchemaVersion = "v1",
            ArchiveName = Path.GetFileName(dumpPath),
            ArchiveSizeBytes = archiveInfo.Length,
            ArchiveHash = archiveHash,
            CreatedAtUtc = createdUtc,
            SourceDescription = safeDescription,
            SourceKind = BackupStorageKind.Postgres,
            Entries = entries
        };
    }

    /// <summary>
    /// 从连接字符串中剥离密码等敏感字段，仅保留 host/port/database/user 等元数据。
    /// 用于清单中安全记录备份来源。
    /// </summary>
    /// <remarks>
    /// 实现方式：用正则匹配 key=value 对，过滤掉 Password / Pwd / SSL Password 等键。
    /// 不依赖 NpgsqlConnectionStringBuilder 以避免在清单生成路径上引入对 Npgsql 的强耦合。
    /// 标记为 public 以便 Service 项目的 AdminEndpoints 在 pg-create 响应中复用同一脱敏逻辑。
    /// </remarks>
    public static string StripCredentialsFromConnectionString(string connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr)) return string.Empty;

        // 匹配 key=value 对（支持引号值与转义）
        var pattern = new Regex(
            @"(?<key>[^=;\s]+)\s*=\s*(?<value>(?:'[^']*'|""[^""]*""|[^;\s]*))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Password", "Pwd", "Passfile", "SslPassword", "SSL Password"
        };

        var kept = new List<string>();
        foreach (Match m in pattern.Matches(connStr))
        {
            var key = m.Groups["key"].Value;
            if (sensitiveKeys.Contains(key)) continue;
            kept.Add($"{key}={m.Groups["value"].Value}");
        }
        return string.Join("; ", kept);
    }

    /// <summary>
    /// 将清单写入磁盘（与归档同目录、同名 .json 后缀）。
    /// </summary>
    public static async Task WriteAsync(
        BackupManifest manifest,
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        var dir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await using var fs = new FileStream(
            manifestPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(fs, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从磁盘加载清单。
    /// </summary>
    public static async Task<BackupManifest> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("备份清单不存在。", manifestPath);
        }

        await using var fs = new FileStream(
            manifestPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, FileOptions.Asynchronous);
        var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(fs, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return manifest ?? throw new InvalidDataException("备份清单内容为空或格式不正确。");
    }

    /// <summary>
    /// 从路径推断条目分类。规则简单：按顶层段 + memory 第二层推断。
    /// </summary>
    internal static string InferCategory(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return "other";
        }

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "other";
        }

        var top = segments[0];
        // workspaces/{ws}/collections/{col}/{category}/...
        // segments: [0]=workspaces [1]=ws [2]=collections [3]=col [4]=category [5]=subcategory(可选)
        if (string.Equals(top, "workspaces", StringComparison.OrdinalIgnoreCase)
            && segments.Length >= 5
            && string.Equals(segments[2], "collections", StringComparison.OrdinalIgnoreCase))
        {
            // segments[3] 是 collection 名（不参与分类），segments[4] 才是 category
            return segments[4] switch
            {
                "memory" => segments.Length >= 6 ? $"memory.{segments[5]}" : "memory",
                var category when !string.IsNullOrEmpty(category) => category,
                _ => "workspaces"
            };
        }

        // 顶层分类：system / eval / reports / traces / jobs
        return top.ToLowerInvariant();
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(current);
                files = Directory.GetFiles(current);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var f in files)
            {
                yield return f;
            }
            foreach (var d in subdirs)
            {
                stack.Push(d);
            }
        }
    }
}
