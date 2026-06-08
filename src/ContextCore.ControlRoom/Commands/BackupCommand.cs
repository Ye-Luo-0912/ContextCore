using System.IO.Compression;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Storage.FileSystem;

namespace ContextCore.ControlRoom.Commands;

/// <summary>
/// 备份与恢复命令（仅支持 filesystem 存储）。
/// <list type="bullet">
///   <item><c>backup create [--output &lt;dir&gt;]</c>：将数据根目录打包为 ZIP 快照。</item>
///   <item><c>backup validate [--isolate]</c>：校验所有 JSONL 文件；<c>--isolate</c> 将损坏文件重命名并创建净版本。</item>
///   <item><c>backup restore &lt;file&gt; [--confirm]</c>：从 ZIP 快照恢复（需 --confirm 确认，破坏性操作）。</item>
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
            case "restore":
                await RestoreAsync(service, subArgs, cancellationToken).ConfigureAwait(false);
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

        Console.WriteLine($"[backup] 创建快照中...");
        Console.WriteLine($"  源目录：{root}");
        Console.WriteLine($"  目标：  {zipPath}");

        try
        {
            await Task.Run(() => ZipFile.CreateFromDirectory(root, zipPath,
                CompressionLevel.Fastest, includeBaseDirectory: false), ct)
                .ConfigureAwait(false);

            var size = new FileInfo(zipPath).Length;
            Console.WriteLine($"[backup] 完成。大小：{size / 1024.0:F1} KB → {zipPath}");
        }
        catch (Exception ex)
        {
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { /* ignore */ }
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

    private static void PrintHelp()
    {
        Console.WriteLine("用法：controlroom backup <子命令> [选项]");
        Console.WriteLine();
        Console.WriteLine("子命令：");
        Console.WriteLine("  create   [--output <dir>]   创建 ZIP 快照（默认输出至 <data-root>/../_backups/）");
        Console.WriteLine("  validate [--isolate]        校验所有 JSONL；--isolate 自动隔离损坏文件");
        Console.WriteLine("  restore  <file> [--confirm] 从 ZIP 快照恢复（破坏性，需 --confirm）");
    }
}
