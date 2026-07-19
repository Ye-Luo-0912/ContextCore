using System.IO.Compression;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.ControlRoom.Backup;

/// <summary>
/// P1-2-3：备份校验器。加载清单后重新解压/读取归档并对比每条目 SHA-256，
/// 输出 <see cref="BackupVerifyResult"/>，包括哈希不匹配、孤儿与缺失条目。
/// </summary>
/// <remarks>
/// 设计选择：
/// <list type="bullet">
/// <item>不修改原归档或清单；只读取。</item>
/// <item>逐条目流式读取并哈希，避免一次性载入。</item>
/// <item>归档本身的哈希（<see cref="BackupManifest.ArchiveHash"/>）也会重新计算并对比。</item>
/// <item>Postgres 转储条目（如果出现在清单中）以同样的流式哈希方式校验，不会连接数据库。</item>
/// </list>
/// </remarks>
public static class BackupVerifier
{
    /// <summary>
    /// 校验 ZIP 归档与清单是否一致。
    /// </summary>
    /// <param name="manifest">已加载的清单。</param>
    /// <param name="archivePath">归档文件路径（必须存在）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<BackupVerifyResult> VerifyZipAsync(
        BackupManifest manifest,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("备份归档不存在。", archivePath);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 1) 归档本身哈希
        var actualArchiveHash = Sha256Utility.HashFile(archivePath);
        var archiveHashMatched = string.Equals(
            actualArchiveHash, manifest.ArchiveHash, StringComparison.OrdinalIgnoreCase);

        // 2) 逐条目重新哈希并对比
        var expected = manifest.Entries.ToDictionary(
            e => NormalizePath(e.RelativePath),
            e => e,
            StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashMismatches = new List<string>();
        var missing = new List<string>();

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
                continue;
            }
            var key = NormalizePath(entry.FullName);
            seen.Add(key);

            if (!expected.TryGetValue(key, out var expectedEntry))
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var ms = new MemoryStream(checked((int)Math.Min(entry.Length, 16 * 1024 * 1024)));
            await entryStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            var actualHash = Sha256Utility.HashStream(ms);

            if (!string.Equals(actualHash, expectedEntry.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                hashMismatches.Add(entry.FullName);
            }
        }

        // 3) 缺失条目：清单声明但归档中没有
        foreach (var kv in expected)
        {
            if (!seen.Contains(kv.Key))
            {
                missing.Add(kv.Value.RelativePath);
            }
        }

        // 4) 孤儿条目：归档中有但清单没有
        var orphans = seen
            .Where(s => !expected.ContainsKey(s))
            .ToList();

        stopwatch.Stop();
        return new BackupVerifyResult
        {
            ManifestPath = string.Empty, // 由调用方填充
            ArchivePath = archivePath,
            ExpectedEntryCount = manifest.Entries.Count,
            VerifiedEntryCount = manifest.Entries.Count - hashMismatches.Count - missing.Count,
            HashMismatchedPaths = hashMismatches,
            OrphanPaths = orphans,
            MissingPaths = missing,
            ArchiveHashMatched = archiveHashMatched,
            Elapsed = stopwatch.Elapsed
        };
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/');
}
