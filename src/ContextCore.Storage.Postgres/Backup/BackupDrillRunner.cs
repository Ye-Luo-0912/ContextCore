using System.IO.Compression;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Shared;

namespace ContextCore.Storage.Postgres.Backup;

/// <summary>
/// 恢复演练器。将 ZIP 归档解压到隔离的 staging 目录，
/// 重新计算每个文件的 SHA-256 并与清单对比，输出 <see cref="BackupDrillResult"/>，
/// 完成后自动清理 staging 目录。
/// </summary>
/// <remarks>
/// 设计选择：
/// <list type="bullet">
/// <item>不接触生产数据根目录——始终在隔离的 staging 目录下操作。</item>
/// <item>Postgres 转储条目（如果有）只做哈希校验，不调用 pg_restore（独立数据库依赖）。</item>
/// <item>无论校验是否通过，都会清理 staging 目录。</item>
/// <item>如果清单中包含 Postgres 条目，会在结果中标记 <see cref="BackupDrillResult.PostgresDrillSkipped"/>。</item>
/// </list>
/// </remarks>
public static class BackupDrillRunner
{
    /// <summary>
    /// 执行恢复演练。
    /// </summary>
    /// <param name="manifest">已加载的清单；如果为 null，则跳过哈希对比，仅验证可解压性。</param>
    /// <param name="archivePath">ZIP 归档路径。</param>
    /// <param name="stagingRoot">staging 根目录；为空时使用系统临时目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<BackupDrillResult> RunZipDrillAsync(
        BackupManifest? manifest,
        string archivePath,
        string? stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("备份归档不存在。", archivePath);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stagingPath = Path.Combine(
            stagingRoot ?? Path.GetTempPath(),
            "cc-drill-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss") + "-" + Path.GetRandomFileName());
        Directory.CreateDirectory(stagingPath);

        int restoredCount = 0;
        int hashMatched = 0;
        bool postgresSkipped = false;

        try
        {
            // 1) 解压到 staging
            using (var archiveStream = new FileStream(
                archivePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Length == 0 && string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    var destPath = Path.Combine(stagingPath, entry.FullName);
                    var dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using var entryStream = entry.Open();
                    using var destStream = new FileStream(
                        destPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 81920, FileOptions.Asynchronous);
                    await entryStream.CopyToAsync(destStream, cancellationToken).ConfigureAwait(false);
                    restoredCount++;
                }
            }

            // 2) 与清单对比哈希（如果有清单）
            if (manifest is not null)
            {
                // 检查清单是否含 Postgres 条目（这种条目无法在 staging 中找到对应文件）
                foreach (var entry in manifest.Entries)
                {
                    if (entry.StorageKind == BackupStorageKind.Postgres)
                    {
                        postgresSkipped = true;
                        continue;
                    }

                    var relativePath = NormalizePath(entry.RelativePath);
                    var expectedFile = Path.Combine(stagingPath, relativePath);
                    if (!File.Exists(expectedFile))
                    {
                        continue;
                    }

                    var actualHash = Sha256Utility.HashFile(expectedFile);
                    if (string.Equals(actualHash, entry.ContentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        hashMatched++;
                    }
                }
            }
            else
            {
                // 无清单：仅以成功解压数作为已验证数
                hashMatched = restoredCount;
            }

            stopwatch.Stop();
            return new BackupDrillResult
            {
                ArchivePath = archivePath,
                StagingPath = stagingPath,
                RestoredEntryCount = restoredCount,
                HashMatchedEntryCount = hashMatched,
                Elapsed = stopwatch.Elapsed,
                PostgresDrillSkipped = postgresSkipped
            };
        }
        finally
        {
            // 总是清理 staging 目录
            TryCleanup(stagingPath);
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 清理失败不致命；结果已记录 stagingPath 便于人工清理
        }
    }
}
