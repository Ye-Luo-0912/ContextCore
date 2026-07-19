using System.Reflection;
using System.Text;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Backup;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Shared;

namespace ContextCore.Tests;

/// <summary>
/// R14-PG-10：PostgresPitrRunner + PostgresPitrOptions + BackupManifestGenerator.ForPostgresDumpAsync + StripCredentials 单元测试。
/// 仅覆盖不依赖真实 PostgreSQL 实例的逻辑；端到端流程由 PostgresBackupIntegrationTests 覆盖。
/// </summary>
[TestClass]
[TestCategory("Contract")]
public sealed class PostgresPitrRunnerTests
{
    [TestMethod]
    public void PostgresPitrOptions_Defaults_AreSensible()
    {
        var opts = new PostgresPitrOptions();

        Assert.IsTrue(opts.ArchiveCommand.Contains("cp %p", StringComparison.Ordinal),
            $"默认 ArchiveCommand 应包含 'cp %p'，实际：{opts.ArchiveCommand}");
        Assert.IsTrue(opts.ArchiveCommand.Contains("%f", StringComparison.Ordinal),
            $"默认 ArchiveCommand 应包含 '%f'，实际：{opts.ArchiveCommand}");
        Assert.IsTrue(opts.ArchiveCommand.Contains("{archive_dir}", StringComparison.Ordinal),
            $"默认 ArchiveCommand 应包含 '{{archive_dir}}' 占位符，实际：{opts.ArchiveCommand}");
        Assert.AreEqual("promote", opts.RecoveryTargetAction);
        Assert.IsTrue(opts.BaseBackupCompressionLevel >= 1 && opts.BaseBackupCompressionLevel <= 9,
            $"BaseBackupCompressionLevel 应在 [1,9]，实际：{opts.BaseBackupCompressionLevel}");
        Assert.AreEqual(6, opts.BaseBackupCompressionLevel);
        Assert.IsNull(opts.WalArchiveDirectory);
    }

    [TestMethod]
    public void PostgresPitrOptions_ArchiveCommand_InjectsWalArchiveDirectory()
    {
        var opts = new PostgresPitrOptions
        {
            WalArchiveDirectory = "/var/lib/postgresql/wal_archive"
        };

        var resolved = opts.ResolveArchiveCommand(opts.WalArchiveDirectory!);
        Assert.IsTrue(resolved.Contains("/var/lib/postgresql/wal_archive", StringComparison.Ordinal),
            $"解析后的 archive_command 应注入 WalArchiveDirectory，实际：{resolved}");
        Assert.IsFalse(resolved.Contains("{archive_dir}", StringComparison.Ordinal),
            $"解析后的 archive_command 不应保留占位符，实际：{resolved}");
        Assert.IsTrue(resolved.Contains("cp %p", StringComparison.Ordinal));
        Assert.IsTrue(resolved.Contains("%f", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PostgresPitrOptions_CustomArchiveCommand_PreservedWhenNoPlaceholder()
    {
        var opts = new PostgresPitrOptions
        {
            ArchiveCommand = "test-wrapper --src %p --dst /archive/%f",
            WalArchiveDirectory = "/archive"
        };

        var resolved = opts.ResolveArchiveCommand("/archive");
        // 无 {archive_dir} 占位符时，原样返回
        Assert.AreEqual("test-wrapper --src %p --dst /archive/%f", resolved);
    }

    [TestMethod]
    public void PostgresPitrRunner_Constructor_NullOptions_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new PostgresPitrRunner(options: null!));
    }

    [TestMethod]
    public async Task PostgresPitrRunner_Constructs_WithOptionsAsync()
    {
        // 仅验证构造不会抛出；不实际连接数据库
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=cc_test;Username=test;Password=test",
            SchemaName = "public",
            TablePrefix = "cc_"
        };
        await using var runner = new PostgresPitrRunner(options, new PostgresDumpOptions(), new PostgresPitrOptions());
        Assert.IsNotNull(runner);
    }

    [TestMethod]
    public void PitrRestoreResult_Defaults_AreEmpty()
    {
        var result = new PitrRestoreResult();
        Assert.AreEqual(string.Empty, result.BaseBackupPath);
        Assert.AreEqual(string.Empty, result.WalArchiveDir);
        Assert.IsNull(result.TargetTime);
        Assert.AreEqual(default, result.RestoredToTimestamp);
        Assert.AreEqual(0, result.WALFilesApplied);
        Assert.AreEqual(TimeSpan.Zero, result.Elapsed);
    }

    [TestMethod]
    public void WalArchiveFile_Defaults_AreEmpty()
    {
        var file = new WalArchiveFile();
        Assert.AreEqual(string.Empty, file.Name);
        Assert.AreEqual(0, file.SizeBytes);
        Assert.AreEqual(default, file.ModifiedUtc);
    }

    [TestMethod]
    public void StripCredentialsFromConnectionString_RemovesPassword()
    {
        // 方法已标记为 public 以便跨程序集（Service/AdminEndpoints）复用同一脱敏逻辑
        // 使用 Npgsql 标准 key（Username 而非带空格的 User Id）；正则不支持 key 含空格
        var input = "Host=h;Port=5432;Database=db;Username=u;Password=secret";
        var result = BackupManifestGenerator.StripCredentialsFromConnectionString(input);

        Assert.IsFalse(result.Contains("Password=secret", StringComparison.Ordinal),
            $"剥离后不应包含 Password=secret，实际：{result}");
        Assert.IsFalse(result.Contains("secret", StringComparison.Ordinal),
            $"剥离后不应包含密码值 'secret'，实际：{result}");
        Assert.IsTrue(result.Contains("Host=h", StringComparison.Ordinal), $"应保留 Host，实际：{result}");
        Assert.IsTrue(result.Contains("Database=db", StringComparison.Ordinal), $"应保留 Database，实际：{result}");
        Assert.IsTrue(result.Contains("Username=u", StringComparison.Ordinal), $"应保留 Username，实际：{result}");
        Assert.IsTrue(result.Contains("Port=5432", StringComparison.Ordinal), $"应保留 Port，实际：{result}");
    }

    [TestMethod]
    public void StripCredentialsFromConnectionString_HandlesNoPassword()
    {
        var input = "Host=h;Port=5432;Database=db;Username=u";
        var result = BackupManifestGenerator.StripCredentialsFromConnectionString(input);

        // 无密码时所有键值对都应保留
        Assert.IsTrue(result.Contains("Host=h", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Database=db", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Username=u", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Port=5432", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StripCredentialsFromConnectionString_HandlesPwdAndSslPassword()
    {
        var input = "Host=h;Pwd=p1;SslPassword=p2;Database=db";
        var result = BackupManifestGenerator.StripCredentialsFromConnectionString(input);

        Assert.IsFalse(result.Contains("p1", StringComparison.Ordinal), $"应剥离 Pwd 值，实际：{result}");
        Assert.IsFalse(result.Contains("p2", StringComparison.Ordinal), $"应剥离 SslPassword 值，实际：{result}");
        Assert.IsTrue(result.Contains("Host=h", StringComparison.Ordinal));
        Assert.IsTrue(result.Contains("Database=db", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StripCredentialsFromConnectionString_EmptyInput_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, BackupManifestGenerator.StripCredentialsFromConnectionString(string.Empty));
        Assert.AreEqual(string.Empty, BackupManifestGenerator.StripCredentialsFromConnectionString(null!));
    }

    [TestMethod]
    public async Task BackupManifestGenerator_ForPostgresDumpAsync_RecordsDumpHashAndTableEntriesAsync()
    {
        using var tempDir = new TempDir();
        var dumpPath = Path.Combine(tempDir.Path, "test.dump");
        var dumpContent = "fake pg_dump binary content for testing";
        await File.WriteAllTextAsync(dumpPath, dumpContent).ConfigureAwait(false);

        var expectedHash = Sha256Utility.HashString(dumpContent);

        var dumpResult = new PostgresDumpResult
        {
            DumpPath = dumpPath,
            DumpSizeBytes = new FileInfo(dumpPath).Length,
            DumpHash = expectedHash,
            Tables =
            [
                new PostgresTableInfo { Schema = "public", Name = "cc_contexts", ApproximateBytes = 4096 },
                new PostgresTableInfo { Schema = "public", Name = "cc_memory", ApproximateBytes = 8192 },
            ],
            Entries = []
        };

        var connStrDesc = "Host=localhost;Port=5432;Database=cc_test;Username=test;Password=secret";

        var manifest = await BackupManifestGenerator.ForPostgresDumpAsync(
            dumpPath, connStrDesc, dumpResult, CancellationToken.None).ConfigureAwait(false);

        // 验证清单字段
        Assert.AreEqual("v1", manifest.SchemaVersion);
        Assert.AreEqual("test.dump", manifest.ArchiveName);
        Assert.AreEqual(new FileInfo(dumpPath).Length, manifest.ArchiveSizeBytes);
        Assert.AreEqual(expectedHash, manifest.ArchiveHash);
        Assert.AreEqual(BackupStorageKind.Postgres, manifest.SourceKind);

        // SourceDescription 应已剥离密码
        Assert.IsFalse(manifest.SourceDescription.Contains("Password=secret", StringComparison.Ordinal));
        Assert.IsFalse(manifest.SourceDescription.Contains("secret", StringComparison.Ordinal));
        Assert.IsTrue(manifest.SourceDescription.Contains("Host=localhost", StringComparison.Ordinal));
        Assert.IsTrue(manifest.SourceDescription.Contains("Database=cc_test", StringComparison.Ordinal));

        // 条目：1 个 dump 条目 + 2 个表条目
        Assert.AreEqual(3, manifest.Entries.Count);
        var dumpEntry = manifest.Entries.Single(e => e.Category == "postgres.dump");
        Assert.AreEqual("postgres://dump/test.dump", dumpEntry.RelativePath);
        Assert.AreEqual(expectedHash, dumpEntry.ContentHash);
        Assert.AreEqual(BackupStorageKind.Postgres, dumpEntry.StorageKind);

        var tableEntries = manifest.Entries.Where(e => e.Category == "postgres.table").ToList();
        Assert.AreEqual(2, tableEntries.Count);
        Assert.IsTrue(tableEntries.Any(e => e.RelativePath == "postgres://public.cc_contexts"));
        Assert.IsTrue(tableEntries.Any(e => e.RelativePath == "postgres://public.cc_memory"));
        Assert.IsTrue(tableEntries.All(e => e.StorageKind == BackupStorageKind.Postgres));
    }

    [TestMethod]
    public async Task BackupManifestGenerator_ForPostgresDumpAsync_MissingFile_ThrowsAsync()
    {
        using var tempDir = new TempDir();
        var dumpPath = Path.Combine(tempDir.Path, "nonexistent.dump");
        var dumpResult = new PostgresDumpResult { DumpPath = dumpPath };

        await Assert.ThrowsExceptionAsync<FileNotFoundException>(() =>
            BackupManifestGenerator.ForPostgresDumpAsync(
                dumpPath, "Host=h;Database=db", dumpResult, CancellationToken.None));
    }

    [TestMethod]
    public async Task BackupManifestGenerator_ForPostgresDumpAsync_NullDumpResult_ThrowsAsync()
    {
        using var tempDir = new TempDir();
        var dumpPath = Path.Combine(tempDir.Path, "test.dump");
        await File.WriteAllTextAsync(dumpPath, "x").ConfigureAwait(false);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            BackupManifestGenerator.ForPostgresDumpAsync(
                dumpPath, "Host=h", dumpResult: null!, CancellationToken.None));
    }

    [TestMethod]
    public async Task PostgresPitrRunner_ListWalArchiveFilesAsync_EmptyDir_ReturnsEmptyAsync()
    {
        using var tempDir = new TempDir();
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=cc_test;Username=test;Password=test"
        };

        await using var runner = new PostgresPitrRunner(options);
        var result = await runner.ListWalArchiveFilesAsync(tempDir.Path, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task PostgresPitrRunner_ListWalArchiveFilesAsync_NonExistentDir_ReturnsEmptyAsync()
    {
        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=cc_test;Username=test;Password=test"
        };

        await using var runner = new PostgresPitrRunner(options);
        var result = await runner.ListWalArchiveFilesAsync(
            Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task PostgresPitrRunner_ListWalArchiveFilesAsync_ListsFilesWithMetadataAsync()
    {
        using var tempDir = new TempDir();
        var walDir = Path.Combine(tempDir.Path, "wal");
        Directory.CreateDirectory(walDir);
        var wal1 = Path.Combine(walDir, "000000010000000000000001");
        var wal2 = Path.Combine(walDir, "000000010000000000000002");
        await File.WriteAllTextAsync(wal1, new string('a', 16 * 1024 * 1024)).ConfigureAwait(false); // 16MB
        await File.WriteAllTextAsync(wal2, new string('b', 16 * 1024 * 1024)).ConfigureAwait(false);

        var options = new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=cc_test;Username=test;Password=test"
        };

        await using var runner = new PostgresPitrRunner(options);
        var result = await runner.ListWalArchiveFilesAsync(walDir, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Any(f => f.Name == "000000010000000000000001"));
        Assert.IsTrue(result.Any(f => f.Name == "000000010000000000000002"));
        Assert.IsTrue(result.All(f => f.SizeBytes == 16 * 1024 * 1024));
    }

    [TestMethod]
    public void BackupCommand_PrintPgHelp_ListsAllPgSubcommands()
    {
        // PrintPgHelp 是 private static 方法；通过反射调用并捕获 stdout
        var method = typeof(ContextCore.ControlRoom.Commands.BackupCommand).GetMethod(
            "PrintPgHelp",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "PrintPgHelp 应存在");

        var originalOut = Console.Out;
        var sb = new StringBuilder();
        try
        {
            Console.SetOut(new StringWriter(sb));
            method.Invoke(null, null);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = sb.ToString();
        var expectedSubcommands = new[]
        {
            "pg-create",
            "pg-restore",
            "pg-verify",
            "pg-drill",
            "pg-pitr-prepare",
            "pg-pitr-restore"
        };

        foreach (var sub in expectedSubcommands)
        {
            Assert.IsTrue(output.Contains(sub, StringComparison.Ordinal),
                $"PrintPgHelp 输出应包含 '{sub}'，实际输出：{output}");
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "cc-pitr-test-" + Guid.NewGuid().ToString("N"));
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
