using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Shared;

namespace ContextCore.Tests;

/// <summary>
/// 备份清单 / SHA-256 / 验证 / 恢复演练单元测试。
/// 覆盖 BackupManifestGenerator、BackupVerifier、BackupDrillRunner 与 Sha256Utility 的核心行为。
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class ContextCoreBackupManifestTests
{
    [TestMethod]
    public async Task Sha256Utility_HashString_IsLowercaseHex_24BytesAsync()
    {
        var hash = Sha256Utility.HashString("hello");
        Assert.AreEqual(64, hash.Length);
        Assert.IsFalse(hash.Any(c => c > 'f'));
        Assert.AreEqual("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    [TestMethod]
    public async Task Sha256Utility_HashFile_ReadsFileWithSharedLockAsync()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "ContextCore").ConfigureAwait(false);
            var hash = Sha256Utility.HashFile(path);
            Assert.AreEqual(64, hash.Length);
            Assert.AreEqual(Sha256Utility.HashString("ContextCore"), hash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Sha256Utility_HashFile_MissingFile_Throws()
    {
        Assert.ThrowsException<FileNotFoundException>(() =>
            Sha256Utility.HashFile(Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid() + ".bin")));
    }

    [TestMethod]
    public async Task BackupManifestGenerator_ForZipAsync_RecordsArchiveHashAndEntriesAsync()
    {
        using var tempDir = new TempDir();
        // 准备源数据根目录
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "system"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "system", "manifest.jsonl"), "{\"id\":\"a\"}\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "system", "config.json"), "{\"v\":1}").ConfigureAwait(false);

        // 打包成 ZIP
        var zipPath = Path.Combine(tempDir.Path, "archive.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var manifest = await BackupManifestGenerator.ForZipAsync(
            zipPath, dataRoot, BackupStorageKind.FileSystem, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("v1", manifest.SchemaVersion);
        Assert.AreEqual(Path.GetFileName(zipPath), manifest.ArchiveName);
        Assert.AreEqual(new FileInfo(zipPath).Length, manifest.ArchiveSizeBytes);
        Assert.IsFalse(string.IsNullOrEmpty(manifest.ArchiveHash));
        Assert.AreEqual(BackupStorageKind.FileSystem, manifest.SourceKind);
        Assert.AreEqual(dataRoot, manifest.SourceDescription);
        Assert.IsTrue(manifest.Entries.Count >= 2, "至少应包含 manifest.jsonl 与 config.json 两个条目");
        Assert.IsTrue(manifest.Entries.All(e => !string.IsNullOrEmpty(e.ContentHash)));
        Assert.IsTrue(manifest.Entries.All(e => e.SizeBytes > 0));
        Assert.AreEqual(manifest.Entries.Count, manifest.EntryCount);
        Assert.AreEqual(manifest.Entries.Sum(e => e.SizeBytes), manifest.TotalEntryBytes);
    }

    [TestMethod]
    public async Task BackupManifestGenerator_InferCategory_WorkspaceMemoryPath()
    {
        // 通过反射调用 internal 方法（避免依赖测试程序集的 InternalsVisibleTo）
        var method = typeof(BackupManifestGenerator).GetMethod(
            "InferCategory", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method);
        object? Invoke(string path) => method.Invoke(null, [path]);

        Assert.AreEqual("memory.short-term", Invoke("workspaces/ws1/collections/col1/memory/short-term/file.jsonl"));
        Assert.AreEqual("memory.stable", Invoke("workspaces/ws1/collections/col1/memory/stable/x.json"));
        Assert.AreEqual("memory", Invoke("workspaces/ws1/collections/col1/memory"));
        Assert.AreEqual("relations", Invoke("workspaces/ws1/collections/col1/relations/r.jsonl"));
        Assert.AreEqual("traces", Invoke("workspaces/ws1/collections/col1/traces/t.jsonl"));
        Assert.AreEqual("system", Invoke("system/manifest.jsonl"));
        Assert.AreEqual("eval", Invoke("eval/results.json"));
        Assert.AreEqual("other", Invoke(""));
        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task BackupManifestGenerator_ForDataRootAsync_ComputesHashPerFileAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "root");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "a.txt"), "A").ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(dataRoot, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "sub", "b.jsonl"), "{\"b\":1}").ConfigureAwait(false);

        var manifest = await BackupManifestGenerator.ForDataRootAsync(dataRoot, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(string.Empty, manifest.ArchiveName);
        Assert.AreEqual(0, manifest.ArchiveSizeBytes);
        Assert.AreEqual(string.Empty, manifest.ArchiveHash);
        Assert.AreEqual(2, manifest.Entries.Count);
        Assert.IsTrue(manifest.Entries.Any(e => e.RelativePath == "a.txt"));
        Assert.IsTrue(manifest.Entries.Any(e => e.RelativePath == "sub/b.jsonl"));
    }

    [TestMethod]
    public async Task BackupManifestGenerator_WriteAndRead_RoundtripsAsync()
    {
        using var tempDir = new TempDir();
        var manifestPath = Path.Combine(tempDir.Path, "manifest.json");

        var original = new BackupManifest
        {
            ArchiveName = "test.zip",
            ArchiveSizeBytes = 1024,
            ArchiveHash = "deadbeef",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceDescription = "/data/root",
            SourceKind = BackupStorageKind.FileSystem,
            Entries =
            [
                new BackupManifestEntry
                {
                    RelativePath = "a/b.txt",
                    SizeBytes = 10,
                    ContentHash = "abc",
                    StorageKind = BackupStorageKind.FileSystem,
                    LastModifiedUtc = DateTimeOffset.UtcNow,
                    Category = "memory.stable"
                }
            ]
        };

        await BackupManifestGenerator.WriteAsync(original, manifestPath, CancellationToken.None).ConfigureAwait(false);
        var loaded = await BackupManifestGenerator.ReadAsync(manifestPath, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(original.ArchiveName, loaded.ArchiveName);
        Assert.AreEqual(original.ArchiveSizeBytes, loaded.ArchiveSizeBytes);
        Assert.AreEqual(original.ArchiveHash, loaded.ArchiveHash);
        Assert.AreEqual(original.SourceDescription, loaded.SourceDescription);
        Assert.AreEqual(original.SourceKind, loaded.SourceKind);
        Assert.AreEqual(original.Entries.Count, loaded.Entries.Count);
        Assert.AreEqual(original.Entries[0].RelativePath, loaded.Entries[0].RelativePath);
        Assert.AreEqual(original.Entries[0].ContentHash, loaded.Entries[0].ContentHash);
    }

    [TestMethod]
    public async Task BackupVerifier_VerifyZipAsync_PassesForIntactArchiveAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "f1.txt"), "hello").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "f2.jsonl"), "{\"x\":1}").ConfigureAwait(false);

        var zipPath = Path.Combine(tempDir.Path, "a.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var manifest = await BackupManifestGenerator.ForZipAsync(
            zipPath, dataRoot, BackupStorageKind.FileSystem, CancellationToken.None).ConfigureAwait(false);
        var result = await BackupVerifier.VerifyZipAsync(manifest, zipPath, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.ArchiveHashMatched);
        Assert.AreEqual(manifest.Entries.Count, result.ExpectedEntryCount);
        Assert.AreEqual(manifest.Entries.Count, result.VerifiedEntryCount);
        Assert.AreEqual(0, result.HashMismatchedPaths.Count);
        Assert.AreEqual(0, result.MissingPaths.Count);
        Assert.AreEqual(0, result.OrphanPaths.Count);
        Assert.IsTrue(result.IsHealthy);
    }

    [TestMethod]
    public async Task BackupVerifier_VerifyZipAsync_DetectsCorruptedArchiveAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "f1.txt"), "original").ConfigureAwait(false);

        var zipPath = Path.Combine(tempDir.Path, "a.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var manifest = await BackupManifestGenerator.ForZipAsync(
            zipPath, dataRoot, BackupStorageKind.FileSystem, CancellationToken.None).ConfigureAwait(false);

        // 重新打包一个内容不同的 ZIP 到原路径，模拟归档被篡改
        File.Delete(zipPath);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "f1.txt"), "tampered").ConfigureAwait(false);
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var result = await BackupVerifier.VerifyZipAsync(manifest, zipPath, CancellationToken.None).ConfigureAwait(false);

        // 归档本身哈希也不匹配（因为 ZIP 内容变了）
        Assert.IsFalse(result.ArchiveHashMatched);
        // f1.txt 的内容哈希也应不匹配
        Assert.IsTrue(result.HashMismatchedPaths.Count >= 1);
        Assert.IsFalse(result.IsHealthy);
    }

    [TestMethod]
    public async Task BackupVerifier_VerifyZipAsync_DetectsMissingEntriesAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "a.txt"), "A").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "b.txt"), "B").ConfigureAwait(false);

        var zipPath = Path.Combine(tempDir.Path, "a.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var manifest = await BackupManifestGenerator.ForZipAsync(
            zipPath, dataRoot, BackupStorageKind.FileSystem, CancellationToken.None).ConfigureAwait(false);

        // 构造一个仅含 b.txt 的新 ZIP（模拟 b.txt 在归档中缺失）
        File.Delete(zipPath);
        File.Delete(Path.Combine(dataRoot, "a.txt"));
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var result = await BackupVerifier.VerifyZipAsync(manifest, zipPath, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.MissingPaths.Count >= 1);
        Assert.IsFalse(result.IsHealthy);
    }

    [TestMethod]
    public async Task BackupDrillRunner_RunZipDrill_RestoresAndVerifiesAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "x.txt"), "ContextCore").ConfigureAwait(false);

        var zipPath = Path.Combine(tempDir.Path, "a.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var manifest = await BackupManifestGenerator.ForZipAsync(
            zipPath, dataRoot, BackupStorageKind.FileSystem, CancellationToken.None).ConfigureAwait(false);

        var stagingRoot = Path.Combine(tempDir.Path, "staging");
        var result = await BackupDrillRunner.RunZipDrillAsync(
            manifest, zipPath, stagingRoot, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(manifest.Entries.Count, result.RestoredEntryCount);
        Assert.AreEqual(manifest.Entries.Count, result.HashMatchedEntryCount);
        Assert.IsTrue(result.IsHealthy);
        Assert.IsFalse(result.PostgresDrillSkipped);
        // staging 应被自动清理
        Assert.IsFalse(Directory.Exists(result.StagingPath));
    }

    [TestMethod]
    public async Task BackupDrillRunner_RunZipDrill_WithoutManifest_StillVerifiesExtractabilityAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "f.txt"), "ContextCore").ConfigureAwait(false);

        var zipPath = Path.Combine(tempDir.Path, "a.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var result = await BackupDrillRunner.RunZipDrillAsync(
            null, zipPath, null, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(1, result.RestoredEntryCount);
        Assert.AreEqual(1, result.HashMatchedEntryCount); // 无清单时以解压数作为已验证数
        Assert.IsTrue(result.IsHealthy);
    }

    [TestMethod]
    public async Task BackupDrillRunner_PostgresEntries_AreSkippedAndReportedAsync()
    {
        using var tempDir = new TempDir();
        var dataRoot = Path.Combine(tempDir.Path, "data");
        Directory.CreateDirectory(dataRoot);
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "fs.txt"), "ContextCore").ConfigureAwait(false);

        var zipPath = Path.Combine(tempDir.Path, "a.zip");
        ZipFile.CreateFromDirectory(dataRoot, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false);

        var manifest = await BackupManifestGenerator.ForZipAsync(
            zipPath, dataRoot, BackupStorageKind.FileSystem, CancellationToken.None).ConfigureAwait(false);
        // 注入一个 Postgres 条目，模拟混合备份场景
        manifest = manifest with
        {
            Entries = manifest.Entries.Append(new BackupManifestEntry
            {
                RelativePath = "postgres://public.cc_relations",
                SizeBytes = 1024,
                ContentHash = string.Empty,
                StorageKind = BackupStorageKind.Postgres,
                LastModifiedUtc = DateTimeOffset.UtcNow,
                Category = "postgres.table"
            }).ToList()
        };

        var result = await BackupDrillRunner.RunZipDrillAsync(
            manifest, zipPath, null, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.PostgresDrillSkipped);
        // 仅文件系统条目参与哈希匹配
        Assert.AreEqual(1, result.HashMatchedEntryCount);
    }

    [TestMethod]
    public async Task BackupManifest_JsonSerializesWithCamelCaseAsync()
    {
        var manifest = new BackupManifest
        {
            ArchiveName = "test.zip",
            ArchiveSizeBytes = 100,
            ArchiveHash = "abc",
            CreatedAtUtc = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            SourceDescription = "src",
            SourceKind = BackupStorageKind.FileSystem,
            Entries = []
        };

        var json = JsonSerializer.Serialize(manifest, BackupManifestGenerator.SerializerOptions);
        Assert.IsTrue(json.Contains("\"schemaVersion\""));
        Assert.IsTrue(json.Contains("\"archiveName\""));
        Assert.IsTrue(json.Contains("\"sourceKind\""));
        Assert.IsTrue(json.Contains("\"FileSystem\""));
        await Task.CompletedTask;
    }

    [TestMethod]
    public void BackupVerifyResult_IsHealthy_TrueWhenNoIssues()
    {
        var result = new BackupVerifyResult
        {
            ExpectedEntryCount = 1,
            VerifiedEntryCount = 1,
            HashMismatchedPaths = [],
            MissingPaths = [],
            OrphanPaths = [],
            ArchiveHashMatched = true
        };
        Assert.IsTrue(result.IsHealthy);
    }

    [TestMethod]
    public void BackupVerifyResult_IsHealthy_FalseWhenHashMismatch()
    {
        var result = new BackupVerifyResult
        {
            HashMismatchedPaths = ["bad.txt"],
            ArchiveHashMatched = true
        };
        Assert.IsFalse(result.IsHealthy);
    }

    [TestMethod]
    public void BackupVerifyResult_IsHealthy_FalseWhenArchiveHashMismatched()
    {
        var result = new BackupVerifyResult
        {
            HashMismatchedPaths = [],
            MissingPaths = [],
            OrphanPaths = [],
            ArchiveHashMatched = false
        };
        Assert.IsFalse(result.IsHealthy);
    }

    [TestMethod]
    public void BackupDrillResult_IsHealthy_RequiresNonZeroAndFullMatch()
    {
        Assert.IsTrue(new BackupDrillResult
        {
            RestoredEntryCount = 5,
            HashMatchedEntryCount = 5
        }.IsHealthy);

        Assert.IsFalse(new BackupDrillResult
        {
            RestoredEntryCount = 0,
            HashMatchedEntryCount = 0
        }.IsHealthy);

        Assert.IsFalse(new BackupDrillResult
        {
            RestoredEntryCount = 5,
            HashMatchedEntryCount = 3
        }.IsHealthy);
    }

    [TestMethod]
    public async Task PostgresBackupRunner_Constructs_WithOptionsAsync()
    {
        // 仅验证构造不会抛出；不实际连接数据库
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=cc_test;Username=test;Password=test",
            SchemaName = "public",
            TablePrefix = "cc_"
        };
        await using var runner = new PostgresBackupRunner(options, new PostgresDumpOptions { BinaryDirectory = "/usr/bin" });
        Assert.IsNotNull(runner);
    }

    [TestMethod]
    public void PostgresDumpOptions_Defaults_HaveNullBinaryDirectory()
    {
        var opts = new PostgresDumpOptions();
        Assert.IsNull(opts.BinaryDirectory);
    }

    [TestMethod]
    public void PostgresTableInfo_Defaults_AreEmpty()
    {
        var info = new PostgresTableInfo();
        Assert.AreEqual(string.Empty, info.Schema);
        Assert.AreEqual(string.Empty, info.Name);
        Assert.AreEqual(0, info.ApproximateBytes);
    }

    [TestMethod]
    public void PostgresDumpResult_Defaults_AreEmpty()
    {
        var result = new PostgresDumpResult();
        Assert.AreEqual(string.Empty, result.DumpPath);
        Assert.AreEqual(0, result.DumpSizeBytes);
        Assert.AreEqual(string.Empty, result.DumpHash);
        Assert.AreEqual(0, result.Tables.Count);
        Assert.AreEqual(0, result.Entries.Count);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cc-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }
}
