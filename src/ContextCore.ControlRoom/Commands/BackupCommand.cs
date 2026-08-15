using System.IO.Compression;
using ContextCore.Abstractions.Models;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres.Backup;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.ControlRoom.Commands;

/// <summary>
/// 备份与恢复命令（支持 filesystem 与 postgres 两种存储后端）。
/// <list type="bullet">
/// <item><c>backup create [--output &lt;dir&gt;]</c>：将数据根目录打包为 ZIP 快照，同时生成 SHA-256 清单。</item>
/// <item><c>backup validate [--isolate]</c>：校验所有 JSONL 文件；<c>--isolate</c> 将损坏文件重命名并创建净版本。</item>
/// <item><c>backup verify &lt;manifest&gt; [--archive &lt;zip&gt;]</c>：根据清单重新哈希归档，输出完整性报告。</item>
/// <item><c>backup drill &lt;archive&gt; [--manifest &lt;path&gt;]</c>：将归档恢复到隔离 staging 目录并校验，完成后清理。</item>
/// <item><c>backup restore &lt;file&gt; [--confirm]</c>：从 ZIP 快照恢复（需 --confirm 确认，破坏性操作）。</item>
/// <item><c>backup pg-create [--connection-string &lt;cs&gt;] [--output &lt;dir&gt;]</c>：通过 pg_dump 创建 PostgreSQL 转储并生成清单。</item>
/// <item><c>backup pg-restore &lt;dump&gt; [--manifest &lt;path&gt;] [--confirm]</c>：通过 pg_restore 恢复 PostgreSQL 转储（需 --confirm）。</item>
/// <item><c>backup pg-verify &lt;manifest&gt;</c>：校验 PostgreSQL 转储文件哈希与表清单。</item>
/// <item><c>backup pg-drill &lt;dump&gt; --staging-connection-string &lt;cs&gt; [--manifest &lt;path&gt;]</c>：在 staging 数据库恢复 PostgreSQL 转储并校验。</item>
/// <item><c>backup pg-pitr-prepare --wal-archive-dir &lt;dir&gt; [--output &lt;dir&gt;]</c>：启用 WAL 归档并创建基础备份。</item>
/// <item><c>backup pg-pitr-restore --base-backup &lt;path&gt; --wal-archive-dir &lt;dir&gt; --target-time &lt;ISO8601&gt; --target-connection-string &lt;cs&gt;</c>：执行 PITR 恢复。</item>
/// </list>
/// </summary>
public static class BackupCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var subCommand = args.Count > 0 ? args[0].ToLowerInvariant() : "help";
        var subArgs = args.Count > 1 ? args.Skip(1).ToList() : [];

        switch (subCommand)
        {
            case "create":
                await CreateBackupAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "validate":
                await ValidateAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "verify":
                await VerifyAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "drill":
                await DrillAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "restore":
                await RestoreAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-create":
                await PgCreateAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-restore":
                await PgRestoreAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-verify":
                await PgVerifyAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-drill":
                await PgDrillAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-pitr-prepare":
                await PgPitrPrepareAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-pitr-restore":
                await PgPitrRestoreAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
                break;
            case "pg-help":
            case "pg":
                PrintPgHelp();
                break;
            default:
                PrintHelp();
                break;
        }
    }

    private static async Task CreateBackupAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var root = service.State.RootPath;
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[backup] 数据根目录不存在：{root}");
            Environment.ExitCode = 1;
            return;
        }

        // 默认输出目录：数据根目录同级的 _backups 目录
        var outputDir = CommandHelpers.GetOption(args, "--output")
            ?? Path.Combine(Path.GetDirectoryName(root) ?? root, "_backups");
        Directory.CreateDirectory(outputDir);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var zipPath = Path.Combine(outputDir, $"contextcore_backup_{timestamp}.zip");
        var manifestPath = zipPath + ".manifest.json";

        Console.WriteLine($"[backup] 创建快照中...");
        Console.WriteLine($"  源目录：{root}");
        Console.WriteLine($"  目标：  {zipPath}");

        try
        {
            await Task.Run(() => ZipFile.CreateFromDirectory(root, zipPath,
                CompressionLevel.Fastest, includeBaseDirectory: false), ct)
                .ConfigureAwait(false);

            var size = new FileInfo(zipPath).Length;
            Console.WriteLine($"[backup] 生成 SHA-256 清单...");
            var manifest = await BackupManifestGenerator.ForZipAsync(
                zipPath, root, BackupStorageKind.FileSystem, ct).ConfigureAwait(false);
            await BackupManifestGenerator.WriteAsync(manifest, manifestPath, ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[backup] 完成。大小：{size / 1024.0:F1} KB → {zipPath}");
            Console.WriteLine($"[backup] 清单：{manifest.EntryCount} 条目，{manifest.TotalEntryBytes / 1024.0:F1} KB 内容 → {manifestPath}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { /* ignore */ }
            }
            if (File.Exists(manifestPath))
            {
                try { File.Delete(manifestPath); } catch { /* ignore */ }
            }
            Console.Error.WriteLine($"[backup] 创建失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task ValidateAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var root = service.State.RootPath;
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"[validate] 数据根目录不存在：{root}");
            Environment.ExitCode = 1;
            return;
        }

        var isolate = args.Contains("--isolate", StringComparer.OrdinalIgnoreCase);
        var inspector = new FileJsonLineInspector();
        var jsonlFiles = Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories);
        var corruptCount = 0;

        Console.WriteLine($"[validate] 扫描 {jsonlFiles.Length} 个 JSONL 文件（根目录：{root}）...");

        foreach (var file in jsonlFiles)
        {
            ct.ThrowIfCancellationRequested();
            var report = await inspector.InspectAsync(file, ct).ConfigureAwait(false);
            if (report.IsHealthy)
            {
                Console.WriteLine($"  ✓ {Path.GetRelativePath(root, file)} ({report.ValidLines} 行)");
                continue;
            }

            corruptCount++;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ✗ {Path.GetRelativePath(root, file)} — {report.CorruptLines} 行损坏 / {report.TotalLines} 行");
            foreach (var issue in report.Issues.Take(5))
            {
                Console.WriteLine($"    行 {issue.LineNumber}: {issue.Message}");
            }
            Console.ResetColor();

            if (isolate)
            {
                await IsolateCorruptFileAsync(file, report, ct).ConfigureAwait(false);
            }
        }

        Console.WriteLine();
        if (corruptCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[validate] 全部通过。共 {jsonlFiles.Length} 个文件，无损坏。");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[validate] 发现 {corruptCount} 个损坏文件（共 {jsonlFiles.Length} 个）。");
            if (!isolate)
                Console.WriteLine("  提示：使用 --isolate 自动将损坏行隔离（重命名原文件 + 保留有效行）。");
            Console.ResetColor();
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// 将损坏的 JSONL 文件隔离：原文件重命名为 <c>*.jsonl.corrupt</c>，同位置写入仅含有效行的净版本。
    /// </summary>
    private static async Task IsolateCorruptFileAsync(
        string filePath,
        FileJsonLineInspectionReport report,
        CancellationToken ct)
    {
        var corruptPath = filePath + ".corrupt";
        File.Move(filePath, corruptPath, overwrite: true);

        // 读原文件，只保留有效行
        var lines = await File.ReadAllLinesAsync(corruptPath, ct).ConfigureAwait(false);
        var corruptLineNumbers = new HashSet<int>(report.Issues.Select(i => i.LineNumber));
        var cleanLines = lines
            .Select((line, idx) => (line, lineNumber: idx + 1))
            .Where(t => !corruptLineNumbers.Contains(t.lineNumber) && !string.IsNullOrWhiteSpace(t.line))
            .Select(t => t.line);

        await File.WriteAllLinesAsync(filePath, cleanLines, ct).ConfigureAwait(false);

        Console.WriteLine($"    → 已隔离：损坏原文件 → {Path.GetFileName(corruptPath)}，有效行保存至 {Path.GetFileName(filePath)}");
    }

    private static async Task VerifyAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var manifestPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            Console.Error.WriteLine("[verify] 用法：backup verify <manifest.json> [--archive <zip>]");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"[verify] 清单文件不存在：{manifestPath}");
            Environment.ExitCode = 1;
            return;
        }

        var archivePath = CommandHelpers.GetOption(args, "--archive");
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            // 默认：与清单同目录、去掉 .manifest.json 后缀
            archivePath = StripManifestExtension(manifestPath);
            if (!File.Exists(archivePath))
            {
                Console.Error.WriteLine($"[verify] 未找到归档文件：{archivePath}（可用 --archive 指定）");
                Environment.ExitCode = 1;
                return;
            }
        }

        if (!File.Exists(archivePath))
        {
            Console.Error.WriteLine($"[verify] 归档文件不存在：{archivePath}");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"[verify] 校验中...");
        Console.WriteLine($"  清单：{manifestPath}");
        Console.WriteLine($"  归档：{archivePath}");

        try
        {
            var manifest = await BackupManifestGenerator.ReadAsync(manifestPath, ct).ConfigureAwait(false);
            var result = await BackupVerifier.VerifyZipAsync(manifest, archivePath, ct).ConfigureAwait(false);
            result = result with { ManifestPath = manifestPath };

            Console.WriteLine($"  期望条目数：{result.ExpectedEntryCount}");
            Console.WriteLine($"  已校验条目：{result.VerifiedEntryCount}");
            Console.WriteLine($"  归档哈希匹配：{(result.ArchiveHashMatched ? "是" : "否")}");

            if (result.HashMismatchedPaths.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  哈希不匹配 ({result.HashMismatchedPaths.Count}):");
                foreach (var p in result.HashMismatchedPaths.Take(10))
                    Console.WriteLine($"    - {p}");
                Console.ResetColor();
            }

            if (result.MissingPaths.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  缺失条目 ({result.MissingPaths.Count}):");
                foreach (var p in result.MissingPaths.Take(10))
                    Console.WriteLine($"    - {p}");
                Console.ResetColor();
            }

            if (result.OrphanPaths.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  孤儿条目 ({result.OrphanPaths.Count}):");
                foreach (var p in result.OrphanPaths.Take(10))
                    Console.WriteLine($"    - {p}");
                Console.ResetColor();
            }

            Console.WriteLine($"  耗时：{result.Elapsed.TotalSeconds:F2}s");
            if (result.IsHealthy)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[verify] ✓ 通过：归档与清单完全一致。");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[verify] ✗ 未通过：归档与清单存在差异。");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[verify] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task DrillAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var archivePath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            Console.Error.WriteLine("[drill] 用法：backup drill <archive.zip> [--manifest <path>]");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(archivePath))
        {
            Console.Error.WriteLine($"[drill] 归档文件不存在：{archivePath}");
            Environment.ExitCode = 1;
            return;
        }

        var manifestPath = CommandHelpers.GetOption(args, "--manifest");
        var stagingRoot = CommandHelpers.GetOption(args, "--staging-root");
        BackupManifest? manifest = null;
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"[drill] 清单文件不存在：{manifestPath}");
                Environment.ExitCode = 1;
                return;
            }
            manifest = await BackupManifestGenerator.ReadAsync(manifestPath, ct).ConfigureAwait(false);
            Console.WriteLine($"[drill] 使用清单：{manifestPath}（{manifest.EntryCount} 条目）");
        }
        else
        {
            // 尝试在归档同目录查找默认清单
            var defaultManifest = archivePath + ".manifest.json";
            if (File.Exists(defaultManifest))
            {
                manifest = await BackupManifestGenerator.ReadAsync(defaultManifest, ct).ConfigureAwait(false);
                Console.WriteLine($"[drill] 使用默认清单：{defaultManifest}（{manifest.EntryCount} 条目）");
            }
            else
            {
                Console.WriteLine($"[drill] 未指定清单；仅校验可解压性。");
            }
        }

        Console.WriteLine($"[drill] 恢复演练中...");
        Console.WriteLine($"  归档：{archivePath}");

        try
        {
            var result = await BackupDrillRunner.RunZipDrillAsync(
                manifest, archivePath, stagingRoot, ct).ConfigureAwait(false);

            Console.WriteLine($"  恢复条目数：{result.RestoredEntryCount}");
            Console.WriteLine($"  哈希匹配数：{result.HashMatchedEntryCount}");
            if (result.PostgresDrillSkipped)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Postgres 转储条目已跳过（需独立数据库恢复演练）。");
                Console.ResetColor();
            }
            Console.WriteLine($"  Staging 路径：{result.StagingPath}（已自动清理）");
            Console.WriteLine($"  耗时：{result.Elapsed.TotalSeconds:F2}s");

            if (result.IsHealthy)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[drill] ✓ 通过：归档可恢复且所有条目哈希匹配。");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[drill] ✗ 未通过：恢复条目数或哈希不匹配。");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[drill] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task RestoreAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var zipPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            Console.Error.WriteLine("[restore] 用法：backup restore <backup-file.zip> [--confirm]");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(zipPath))
        {
            Console.Error.WriteLine($"[restore] 备份文件不存在：{zipPath}");
            Environment.ExitCode = 1;
            return;
        }

        var confirmed = args.Contains("--confirm", StringComparer.OrdinalIgnoreCase);
        var root = service.State.RootPath;

        if (!confirmed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[restore] 警告：此操作将清空 {root} 并从备份恢复。");
            Console.WriteLine("  重新运行并添加 --confirm 参数以确认执行。");
            Console.ResetColor();
            return;
        }

        Console.WriteLine($"[restore] 恢复中...");
        Console.WriteLine($"  备份：{zipPath}");
        Console.WriteLine($"  目标：{root}");

        try
        {
            // 恢复前保留一份当前数据的快速备份
            var safetyDir = Path.Combine(
                Path.GetDirectoryName(root) ?? root,
                "_backups",
                "pre-restore_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss"));
            if (Directory.Exists(root))
            {
                await Task.Run(() => ZipFile.CreateFromDirectory(root, safetyDir + ".zip",
                    CompressionLevel.Fastest, includeBaseDirectory: false), ct)
                    .ConfigureAwait(false);
                Console.WriteLine($"  安全备份已创建：{safetyDir}.zip");

                // 清空目标目录（保留目录本身）
                foreach (var dir in Directory.GetDirectories(root))
                    Directory.Delete(dir, recursive: true);
                foreach (var file in Directory.GetFiles(root))
                    File.Delete(file);
            }

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true), ct)
                .ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[restore] 完成。数据已从 {Path.GetFileName(zipPath)} 恢复至 {root}。");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[restore] 恢复失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static string StripManifestExtension(string manifestPath)
    {
        // 移除末尾 ".manifest.json"（如有），返回归档候选路径
        var suffix = ".manifest.json";
        if (manifestPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return manifestPath[..^suffix.Length];
        }
        return manifestPath;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("用法：controlroom backup <子命令> [选项]");
        Console.WriteLine();
        Console.WriteLine("子命令：");
        Console.WriteLine("  create   [--output <dir>]            创建 ZIP 快照（默认输出至 <data-root>/../_backups/），同时生成 SHA-256 清单");
        Console.WriteLine("  validate [--isolate]                  校验所有 JSONL；--isolate 自动隔离损坏文件");
        Console.WriteLine("  verify   <manifest> [--archive <zip>] 根据清单重新哈希归档，输出完整性报告");
        Console.WriteLine("  drill    <archive> [--manifest <p>]  将归档恢复到隔离 staging 目录并校验，完成后清理");
        Console.WriteLine("  restore  <file> [--confirm]           从 ZIP 快照恢复（破坏性，需 --confirm）");
        Console.WriteLine("  pg-help                              显示 PostgreSQL 备份/恢复子命令帮助");
        Console.WriteLine();
        Console.WriteLine("PostgreSQL 子命令（pg-create / pg-restore / pg-verify / pg-drill / pg-pitr-prepare / pg-pitr-restore）：");
        Console.WriteLine("  使用 `backup pg-help` 查看详细用法。");
    }

    private static void PrintPgHelp()
    {
        Console.WriteLine("PostgreSQL 备份/恢复子命令：");
        Console.WriteLine();
        Console.WriteLine("  pg-create [--connection-string <cs>] [--output <dir>]");
        Console.WriteLine("    通过 pg_dump -Fc 创建 PostgreSQL 转储（.dump），并生成清单（.manifest.json）");
        Console.WriteLine("    --connection-string：源数据库连接串；省略时使用 service.State.PostgresOptions.ConnectionString");
        Console.WriteLine("    --output：输出目录；默认 <data-root>/../_backups/");
        Console.WriteLine();
        Console.WriteLine("  pg-restore <dump> [--manifest <path>] [--connection-string <cs>] [--confirm]");
        Console.WriteLine("    通过 pg_restore 将 .dump 恢复到目标数据库（破坏性，需 --confirm）");
        Console.WriteLine("    --manifest：清单路径（默认 <dump>.manifest.json）；用于恢复前显示元数据");
        Console.WriteLine("    --connection-string：目标数据库连接串；省略时使用 service.State.PostgresOptions.ConnectionString");
        Console.WriteLine();
        Console.WriteLine("  pg-verify <manifest>");
        Console.WriteLine("    重新计算 .dump 文件哈希并对比清单；通过 ListTablesAsync 验证表清单一致");
        Console.WriteLine();
        Console.WriteLine("  pg-drill <dump> --staging-connection-string <cs> [--manifest <path>]");
        Console.WriteLine("    在临时 staging 数据库恢复 .dump 并校验表清单；staging 连接串必须与源不同");
        Console.WriteLine("    完成后不自动删除 staging 数据库（调用方决定是否清理）");
        Console.WriteLine();
        Console.WriteLine("  pg-pitr-prepare --wal-archive-dir <dir> [--output <dir>] [--connection-string <cs>]");
        Console.WriteLine("    启用 WAL 归档（ALTER SYSTEM）+ 调用 pg_basebackup 创建基础备份");
        Console.WriteLine("    需要超级用户权限；执行后需重启 PostgreSQL 生效");
        Console.WriteLine();
        Console.WriteLine("  pg-pitr-restore --base-backup <path> --wal-archive-dir <dir>");
        Console.WriteLine("                  --target-time <ISO8601> --target-connection-string <cs>");
        Console.WriteLine("    在目标实例上执行 PITR 恢复至指定时间点（UTC ISO 8601）");
        Console.WriteLine("    调用方需先停止 PostgreSQL、解压 base.tar.gz 到 data 目录、再调用本命令");
    }

    // ── PostgreSQL 备份/恢复子命令 ────────────────────────────────────

    private static PostgresOptions ResolvePostgresOptions(ControlRoomService service, IReadOnlyList<string> args)
    {
        var cs = CommandHelpers.GetOption(args, "--connection-string");
        if (!string.IsNullOrWhiteSpace(cs))
        {
            return new PostgresOptions
            {
                ConnectionString = cs,
                AutoMigrate = false,
                SchemaName = service.State.PostgresOptions?.SchemaName ?? string.Empty,
                TablePrefix = service.State.PostgresOptions?.TablePrefix ?? "cc_"
            };
        }

        if (service.State.PostgresOptions is { } stateOpts)
        {
            return stateOpts;
        }

        throw new InvalidOperationException(
            "未提供 PostgreSQL 连接串。请使用 --connection-string 参数，或确保 ControlRoomState.PostgresOptions 已设置。");
    }

    private static async Task PgCreateAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var options = ResolvePostgresOptions(service, args);
        var outputDir = CommandHelpers.GetOption(args, "--output")
            ?? Path.Combine(Path.GetDirectoryName(service.State.RootPath) ?? service.State.RootPath, "_backups");
        Directory.CreateDirectory(outputDir);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var dumpPath = Path.Combine(outputDir, $"postgres_{timestamp}.dump");
        var manifestPath = dumpPath + ".manifest.json";

        Console.WriteLine($"[pg-create] 创建 PostgreSQL 转储...");
        Console.WriteLine($"  输出：{dumpPath}");

        try
        {
            await using var runner = new PostgresBackupRunner(options);
            var (envOk, envErr) = await runner.ValidateEnvironmentAsync(ct).ConfigureAwait(false);
            if (!envOk)
            {
                Console.Error.WriteLine($"[pg-create] 环境校验失败：{envErr}");
                Environment.ExitCode = 1;
                return;
            }

            var dumpResult = await runner.DumpAsync(dumpPath, ct).ConfigureAwait(false);
            var manifest = await BackupManifestGenerator.ForPostgresDumpAsync(
                dumpPath, options.ConnectionString, dumpResult, ct).ConfigureAwait(false);
            await BackupManifestGenerator.WriteAsync(manifest, manifestPath, ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[pg-create] 完成。大小：{dumpResult.DumpSizeBytes / 1024.0:F1} KB → {dumpPath}");
            Console.WriteLine($"[pg-create] 清单：{manifest.EntryCount} 条目 → {manifestPath}");
            Console.WriteLine($"[pg-create] 表清单：{string.Join(", ", dumpResult.Tables.Select(t => $"{t.Schema}.{t.Name}").Take(10))}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            if (File.Exists(dumpPath)) { try { File.Delete(dumpPath); } catch { /* ignore */ } }
            if (File.Exists(manifestPath)) { try { File.Delete(manifestPath); } catch { /* ignore */ } }
            Console.Error.WriteLine($"[pg-create] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task PgRestoreAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var dumpPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(dumpPath))
        {
            Console.Error.WriteLine("[pg-restore] 用法：backup pg-restore <dump> [--manifest <path>] [--connection-string <cs>] [--confirm]");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"[pg-restore] 转储文件不存在：{dumpPath}");
            Environment.ExitCode = 1;
            return;
        }

        var confirmed = args.Contains("--confirm", StringComparer.OrdinalIgnoreCase);
        if (!confirmed)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[pg-restore] 警告：此操作将向目标数据库写入数据（--clean --if-exists 会先清理）。");
            Console.WriteLine("  重新运行并添加 --confirm 参数以确认执行。");
            Console.ResetColor();
            return;
        }

        var manifestPath = CommandHelpers.GetOption(args, "--manifest") ?? (dumpPath + ".manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = await BackupManifestGenerator.ReadAsync(manifestPath, ct).ConfigureAwait(false);
            Console.WriteLine($"[pg-restore] 清单：{manifest.EntryCount} 条目，归档哈希：{manifest.ArchiveHash[..12]}...");
        }

        var options = ResolvePostgresOptions(service, args);

        Console.WriteLine($"[pg-restore] 恢复中...");
        Console.WriteLine($"  转储：{dumpPath}");

        try
        {
            await using var runner = new PostgresBackupRunner(options);
            await runner.RestoreAsync(dumpPath, cleanBeforeRestore: true, ct).ConfigureAwait(false);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[pg-restore] 完成。");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pg-restore] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task PgVerifyAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var manifestPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            Console.Error.WriteLine("[pg-verify] 用法：backup pg-verify <manifest> [--connection-string <cs>]");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"[pg-verify] 清单文件不存在：{manifestPath}");
            Environment.ExitCode = 1;
            return;
        }

        var manifest = await BackupManifestGenerator.ReadAsync(manifestPath, ct).ConfigureAwait(false);
        var dumpDir = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        var dumpPath = Path.Combine(dumpDir, manifest.ArchiveName);

        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"[pg-verify] 转储文件不存在：{dumpPath}（清单位于 {manifestPath}）");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"[pg-verify] 校验中...");
        Console.WriteLine($"  清单：{manifestPath}");
        Console.WriteLine($"  转储：{dumpPath}");

        var actualHash = await Task.Run(() => ContextCore.Storage.Shared.Sha256Utility.HashFile(dumpPath), ct).ConfigureAwait(false);
        var hashMatched = string.Equals(actualHash, manifest.ArchiveHash, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  归档哈希期望：{manifest.ArchiveHash}");
        Console.WriteLine($"  归档哈希实际：{actualHash}");
        Console.WriteLine($"  归档哈希匹配：{(hashMatched ? "是" : "否")}");

        // 校验表清单：通过 ListTablesAsync 重新列出表，与清单中 postgres://schema.table 条目对比
        var options = ResolvePostgresOptions(service, args);
        var expectedTables = manifest.Entries
            .Where(e => e.Category == "postgres.table")
            .Select(e => e.RelativePath)
            .ToHashSet();

        try
        {
            await using var runner = new PostgresBackupRunner(options);
            var actualTables = await runner.ListTablesAsync(ct).ConfigureAwait(false);
            var actualTablePaths = actualTables.Select(t => $"postgres://{t.Schema}.{t.Name}").ToHashSet();

            Console.WriteLine($"  期望表数：{expectedTables.Count}");
            Console.WriteLine($"  实际表数：{actualTables.Count}");

            var missing = expectedTables.Except(actualTablePaths).ToList();
            var orphan = actualTablePaths.Except(expectedTables).ToList();

            if (missing.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  缺失表 ({missing.Count}): {string.Join(", ", missing.Take(10))}");
                Console.ResetColor();
            }
            if (orphan.Any())
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  孤儿表 ({orphan.Count}): {string.Join(", ", orphan.Take(10))}");
                Console.ResetColor();
            }

            var healthy = hashMatched && !missing.Any() && !orphan.Any();
            if (healthy)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[pg-verify] ✓ 通过：转储文件与清单完全一致。");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[pg-verify] ✗ 未通过：存在差异。");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pg-verify] 表清单校验失败（无法连接数据库）：{ex.Message}");
            Console.WriteLine("  归档哈希校验仍已完成；表清单校验需可连接数据库。");
            Environment.ExitCode = hashMatched ? 0 : 1;
        }
    }

    private static async Task PgDrillAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var dumpPath = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(dumpPath))
        {
            Console.Error.WriteLine("[pg-drill] 用法：backup pg-drill <dump> --staging-connection-string <cs> [--manifest <path>]");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"[pg-drill] 转储文件不存在：{dumpPath}");
            Environment.ExitCode = 1;
            return;
        }

        var stagingCs = CommandHelpers.GetOption(args, "--staging-connection-string");
        if (string.IsNullOrWhiteSpace(stagingCs))
        {
            Console.Error.WriteLine("[pg-drill] 必须提供 --staging-connection-string，且必须与源数据库连接串不同。");
            Environment.ExitCode = 1;
            return;
        }

        var sourceOptions = ResolvePostgresOptions(service, args);
        if (string.Equals(stagingCs, sourceOptions.ConnectionString, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("[pg-drill] staging 连接串必须与源数据库连接串不同（避免覆盖生产数据）。");
            Environment.ExitCode = 1;
            return;
        }

        var stagingOptions = new PostgresOptions
        {
            ConnectionString = stagingCs,
            AutoMigrate = false,
            SchemaName = sourceOptions.SchemaName,
            TablePrefix = sourceOptions.TablePrefix
        };

        var manifestPath = CommandHelpers.GetOption(args, "--manifest") ?? (dumpPath + ".manifest.json");
        BackupManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            manifest = await BackupManifestGenerator.ReadAsync(manifestPath, ct).ConfigureAwait(false);
            Console.WriteLine($"[pg-drill] 使用清单：{manifestPath}（{manifest.EntryCount} 条目）");
        }

        Console.WriteLine($"[pg-drill] 恢复演练中...");
        Console.WriteLine($"  转储：{dumpPath}");
        Console.WriteLine($"  staging：{BackupManifestGenerator.StripCredentialsFromConnectionString(stagingCs)}");

        try
        {
            await using var stagingRunner = new PostgresBackupRunner(stagingOptions);
            var (envOk, envErr) = await stagingRunner.ValidateEnvironmentAsync(ct).ConfigureAwait(false);
            if (!envOk)
            {
                Console.Error.WriteLine($"[pg-drill] staging 环境校验失败：{envErr}");
                Environment.ExitCode = 1;
                return;
            }

            await stagingRunner.RestoreAsync(dumpPath, cleanBeforeRestore: true, ct).ConfigureAwait(false);

            var actualTables = await stagingRunner.ListTablesAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"  staging 表数：{actualTables.Count}");

            var expectedTables = manifest?.Entries
                .Where(e => e.Category == "postgres.table")
                .Select(e => e.RelativePath)
                .ToHashSet() ?? new HashSet<string>();
            var actualTablePaths = actualTables.Select(t => $"postgres://{t.Schema}.{t.Name}").ToHashSet();
            var missing = expectedTables.Except(actualTablePaths).ToList();

            var healthy = !missing.Any();
            if (manifest is not null && expectedTables.Count > 0)
            {
                Console.WriteLine($"  期望表数：{expectedTables.Count}");
                Console.WriteLine($"  缺失表数：{missing.Count}");
            }

            if (healthy)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[pg-drill] ✓ 通过：转储可恢复至 staging 数据库且表清单一致。");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[pg-drill] ✗ 未通过：表清单不匹配。缺失：{string.Join(", ", missing.Take(10))}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pg-drill] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task PgPitrPrepareAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var walArchiveDir = CommandHelpers.GetOption(args, "--wal-archive-dir");
        if (string.IsNullOrWhiteSpace(walArchiveDir))
        {
            Console.Error.WriteLine("[pg-pitr-prepare] 必须提供 --wal-archive-dir <dir>");
            Environment.ExitCode = 1;
            return;
        }

        Directory.CreateDirectory(walArchiveDir);

        var outputDir = CommandHelpers.GetOption(args, "--output")
            ?? Path.Combine(Path.GetDirectoryName(service.State.RootPath) ?? service.State.RootPath, "_backups", "pitr");
        Directory.CreateDirectory(outputDir);

        var options = ResolvePostgresOptions(service, args);
        var pitrOptions = new PostgresPitrOptions { WalArchiveDirectory = walArchiveDir };

        Console.WriteLine($"[pg-pitr-prepare] 启用 WAL 归档...");
        Console.WriteLine($"  WAL 归档目录：{walArchiveDir}");
        Console.WriteLine($"  archive_command：{pitrOptions.ResolveArchiveCommand(walArchiveDir)}");

        try
        {
            await using var pitr = new PostgresPitrRunner(options, null, pitrOptions);
            await pitr.EnableWalArchivingAsync(ct).ConfigureAwait(false);
            Console.WriteLine("[pg-pitr-prepare] ALTER SYSTEM 完成。需重启 PostgreSQL 或执行 SELECT pg_reload_conf() 生效。");

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
            var baseBackupDir = Path.Combine(outputDir, $"basebackup_{timestamp}");
            Console.WriteLine($"[pg-pitr-prepare] 创建基础备份 → {baseBackupDir}");
            await pitr.CreateBaseBackupAsync(baseBackupDir, ct).ConfigureAwait(false);

            var walFiles = await pitr.ListWalArchiveFilesAsync(walArchiveDir, ct).ConfigureAwait(false);
            Console.WriteLine($"[pg-pitr-prepare] WAL 归档目录当前 {walFiles.Count} 个文件：");
            foreach (var f in walFiles.Take(10))
            {
                Console.WriteLine($"  - {f.Name} ({f.SizeBytes} bytes, {f.ModifiedUtc:O})");
            }

            var (envOk, envErr) = await pitr.ValidatePitrEnvironmentAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[pg-pitr-prepare] 环境校验：{(envOk ? "通过" : "未通过")}");
            if (!envOk) Console.WriteLine($"  详情：{envErr}");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[pg-pitr-prepare] 完成。基础备份：{baseBackupDir}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pg-pitr-prepare] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task PgPitrRestoreAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var baseBackup = CommandHelpers.GetOption(args, "--base-backup");
        var walArchiveDir = CommandHelpers.GetOption(args, "--wal-archive-dir");
        var targetTimeStr = CommandHelpers.GetOption(args, "--target-time");
        var targetCs = CommandHelpers.GetOption(args, "--target-connection-string");

        if (string.IsNullOrWhiteSpace(baseBackup) || string.IsNullOrWhiteSpace(walArchiveDir)
            || string.IsNullOrWhiteSpace(targetCs))
        {
            Console.Error.WriteLine("[pg-pitr-restore] 用法：backup pg-pitr-restore --base-backup <path> --wal-archive-dir <dir> --target-time <ISO8601> --target-connection-string <cs>");
            Environment.ExitCode = 1;
            return;
        }

        DateTimeOffset? targetTime = null;
        if (!string.IsNullOrWhiteSpace(targetTimeStr))
        {
            if (!DateTimeOffset.TryParse(targetTimeStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                Console.Error.WriteLine($"[pg-pitr-restore] --target-time 格式无效：{targetTimeStr}（应为 ISO 8601）");
                Environment.ExitCode = 1;
                return;
            }
            targetTime = parsed.ToUniversalTime();
        }

        var options = ResolvePostgresOptions(service, args);

        Console.WriteLine($"[pg-pitr-restore] PITR 恢复中...");
        Console.WriteLine($"  base backup：{baseBackup}");
        Console.WriteLine($"  WAL 归档目录：{walArchiveDir}");
        Console.WriteLine($"  target time：{targetTime?.ToString("O") ?? "(最新可用 WAL)"}");

        try
        {
            await using var pitr = new PostgresPitrRunner(options);
            var result = await pitr.RestoreToPointInTimeAsync(
                baseBackup, walArchiveDir, targetTime, targetCs, ct).ConfigureAwait(false);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[pg-pitr-restore] 完成。");
            Console.WriteLine($"  恢复完成时间：{result.RestoredToTimestamp:O}");
            Console.WriteLine($"  已应用 WAL 文件数（best-effort）：{result.WALFilesApplied}");
            Console.WriteLine($"  耗时：{result.Elapsed.TotalSeconds:F2}s");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pg-pitr-restore] 失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}
