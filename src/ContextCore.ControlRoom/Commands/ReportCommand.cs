using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>生成 Markdown 报告并输出或保存到文件的命令。</summary>
public static class ReportCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (args.Count == 0 || !string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("report supports: export --out <path>");
            return;
        }

        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? CommandHelpers.GetOption(args, "-o")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "contextcore-controlroom-report.md");

        var markdown = await service.BuildMarkdownReportAsync(cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(outputPath, markdown, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Report exported: {Path.GetFullPath(outputPath)}");
    }
}
