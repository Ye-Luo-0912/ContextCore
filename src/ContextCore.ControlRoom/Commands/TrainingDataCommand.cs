using System.Globalization;
using ContextCore.Abstractions;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>
/// R29 WP-E-3：训练数据导出命令。
/// </summary>
/// <remarks>
/// 用法：
///   export-training-data --out &lt;directory&gt; [--collection &lt;id&gt;] [--since &lt;ISO8601&gt;] [--until &lt;ISO8601&gt;]
///                          [--decision-id &lt;id&gt;] [--selected-only | --dropped-only]
///                          [--model-artifact-id &lt;id&gt;] [--take &lt;N&gt;]
///
/// 输出：
///   {OutputDirectory}/training-data.jsonl          — 训练样本（每行一条 JSONL）
///   {OutputDirectory}/training-data.manifest.json   — 清单（含 SHA-256 与 model artifact 追溯）
///
/// 说明：
///   - Direct 模式（InMemory / FileSystem）下从本地 IUtilityLedgerStore 导出。
///     本地 ledger 通常为空；生产 ledger 应由 Service 端 Postgres 维护，离线导出时
///     可通过 --storage postgres 直连或通过 Service API 拉取（待 WP-E-5）。
///   - 导出过程幂等：重复执行覆盖输出文件，不修改 ledger 状态。
/// </remarks>
public static class TrainingDataCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var exporter = service.State.TrainingDataExporter;
        if (exporter is null)
        {
            Console.Error.WriteLine("当前 ControlRoom 模式未配置训练数据导出器（仅 Direct 模式可用）。");
            Environment.ExitCode = 2;
            return;
        }

        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? CommandHelpers.GetOption(args, "-o");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("export-training-data 需要 --out <directory> 参数。");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            Environment.ExitCode = 2;
            return;
        }

        // workspace 默认取当前 ControlRoom 选中工作区；可用 --workspace 覆盖
        var workspaceId = CommandHelpers.GetOption(args, "--workspace")
            ?? service.State.WorkspaceId;
        // collection 默认取当前 ControlRoom 选中集合；可用 --collection 覆盖；--collection "" 表示跨集合
        var collectionArg = CommandHelpers.GetOption(args, "--collection");
        var collectionId = collectionArg is null
            ? service.State.CollectionId
            : (string.IsNullOrWhiteSpace(collectionArg) ? null : collectionArg);

        var request = new TrainingDataExportRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            OutputDirectory = outputPath,
            ModelArtifactId = CommandHelpers.GetOption(args, "--model-artifact-id"),
            Take = Math.Max(0, CommandHelpers.GetIntOption(args, "--take", defaultValue: 0))
        };

        // 时间范围过滤
        var sinceRaw = CommandHelpers.GetOption(args, "--since");
        if (sinceRaw is not null)
        {
            if (DateTimeOffset.TryParse(sinceRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since))
            {
                request = request with { Since = since };
            }
            else
            {
                Console.Error.WriteLine($"--since 值无效（需 ISO 8601）：{sinceRaw}");
                Environment.ExitCode = 2;
                return;
            }
        }
        var untilRaw = CommandHelpers.GetOption(args, "--until");
        if (untilRaw is not null)
        {
            if (DateTimeOffset.TryParse(untilRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until))
            {
                request = request with { Until = until };
            }
            else
            {
                Console.Error.WriteLine($"--until 值无效（需 ISO 8601）：{untilRaw}");
                Environment.ExitCode = 2;
                return;
            }
        }

        // DecisionId 过滤
        var decisionId = CommandHelpers.GetOption(args, "--decision-id");
        if (decisionId is not null)
        {
            request = request with { DecisionId = decisionId };
        }

        // IsSelected 过滤：--selected-only 仅导出选中样本；--dropped-only 仅导出被拒绝样本
        var selectedOnly = CommandHelpers.HasFlag(args, "--selected-only");
        var droppedOnly = CommandHelpers.HasFlag(args, "--dropped-only");
        if (selectedOnly && droppedOnly)
        {
            Console.Error.WriteLine("--selected-only 与 --dropped-only 互斥；请只指定其中一个。");
            Environment.ExitCode = 2;
            return;
        }
        if (selectedOnly)
        {
            request = request with { IsSelected = true };
        }
        else if (droppedOnly)
        {
            request = request with { IsSelected = false };
        }

        try
        {
            var result = await exporter.ExportAsync(request, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"训练数据已导出：{result.EntryCount} 条样本");
            Console.WriteLine($"  数据文件：{result.DataFilePath}");
            Console.WriteLine($"  清单文件：{result.ManifestFilePath}");
            Console.WriteLine($"  SHA-256 ：{result.Sha256Hash}");
            Console.WriteLine($"  Workspace：{result.WorkspaceId}  Collection：{result.CollectionId ?? "<跨集合>"}");
            if (result.ModelArtifactId is not null)
            {
                Console.WriteLine($"  ModelArtifact：{result.ModelArtifactId}");
            }
            Console.WriteLine($"  Schema  ：{result.SchemaVersion}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"导出失败：{ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("""
        export-training-data 用法：
          export-training-data --out <directory>
                               [--workspace <id>]
                               [--collection <id> | --collection ""]
                               [--since <ISO8601>] [--until <ISO8601>]
                               [--decision-id <id>]
                               [--selected-only | --dropped-only]
                               [--model-artifact-id <id>]
                               [--take <N>]

        选项：
          --out <directory>        输出目录（必填；不存在时自动创建）
          --workspace <id>         workspace 作用域（默认：当前 ControlRoom 选中工作区）
          --collection <id>        collection 作用域（默认：当前选中集合；传空字符串表示跨集合）
          --since <ISO8601>       仅导出 MaterializedAt >= Since 的条目
          --until <ISO8601>       仅导出 MaterializedAt <= Until 的条目
          --decision-id <id>       仅导出指定 DecisionId 的条目
          --selected-only          仅导出被选中的样本（IsSelected=true）
          --dropped-only           仅导出被拒绝的样本（IsSelected=false）
          --model-artifact-id <id> 关联的 ModelArtifactId（写入 manifest 用于追溯）
          --take <N>               最大导出条目数（0 = 不限制）

        输出：
          {out}/training-data.jsonl          — 训练样本（每行一条 JSONL）
          {out}/training-data.manifest.json   — 清单（含 SHA-256 与追溯信息）
        """);
    }
}
