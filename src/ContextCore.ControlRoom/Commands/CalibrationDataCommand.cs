using System.Globalization;
using ContextCore.Abstractions;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>
/// 校准数据导出命令。
/// </summary>
/// <remarks>
/// 用法：
///   export-calibration-data --out &lt;directory&gt; [--collection &lt;id&gt;] [--since &lt;ISO8601&gt;] [--until &lt;ISO8601&gt;]
///                            [--decision-id &lt;id&gt;] [--model-artifact-id &lt;id&gt;] [--model-name &lt;name&gt;]
///                            [--include-no-model-score] [--take &lt;N&gt;]
///
/// 输出：
///   {OutputDirectory}/calibration-data.jsonl          — 校准样本（每行一条 JSONL）
///   {OutputDirectory}/calibration-data.manifest.json   — 清单（含 SHA-256、正负样本统计与 model artifact 追溯）
///
/// 说明：
///   - 校准数据用于拟合 Platt / Temperature / Isotonic 校准参数：
///     predicted = ModelScore（模型原始推理分数）
///     observed  = IsSelected（二分类实际结果）
///     weight    = UtilityContribution（Expert 贡献比例，默认 1.0）
///   - 默认仅导出 ModelScore 非 null 的条目（校准必须有模型预测分数）。
///     使用 --include-no-model-score 可关闭此过滤（仅诊断用途）。
///   - 导出过程幂等：重复执行覆盖输出文件，不修改 ledger 状态。
/// </remarks>
public static class CalibrationDataCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var exporter = service.State.CalibrationDataExporter;
        if (exporter is null)
        {
            Console.Error.WriteLine("当前 ControlRoom 模式未配置校准数据导出器（仅 Direct 模式可用）。");
            Environment.ExitCode = 2;
            return;
        }

        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? CommandHelpers.GetOption(args, "-o");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("export-calibration-data 需要 --out <directory> 参数。");
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

        // --include-no-model-score 关闭 RequireModelScore 过滤（默认 true，仅诊断时关闭）
        var requireModelScore = !CommandHelpers.HasFlag(args, "--include-no-model-score");

        var request = new CalibrationDataExportRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            OutputDirectory = outputPath,
            ModelArtifactId = CommandHelpers.GetOption(args, "--model-artifact-id"),
            ModelName = CommandHelpers.GetOption(args, "--model-name"),
            RequireModelScore = requireModelScore,
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

        try
        {
            var result = await exporter.ExportAsync(request, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"校准数据已导出：{result.EntryCount} 条样本");
            Console.WriteLine($"  正样本：{result.PositiveCount}（{(result.PositiveRatio * 100):F2}%）  负样本：{result.NegativeCount}");
            Console.WriteLine($"  数据文件：{result.DataFilePath}");
            Console.WriteLine($"  清单文件：{result.ManifestFilePath}");
            Console.WriteLine($"  SHA-256 ：{result.Sha256Hash}");
            Console.WriteLine($"  Workspace：{result.WorkspaceId}  Collection：{result.CollectionId ?? "<跨集合>"}");
            if (result.ModelArtifactId is not null)
            {
                Console.WriteLine($"  ModelArtifact：{result.ModelArtifactId}");
            }
            if (result.ModelName is not null)
            {
                Console.WriteLine($"  ModelName：{result.ModelName}");
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
        export-calibration-data 用法：
          export-calibration-data --out <directory>
                                  [--workspace <id>]
                                  [--collection <id> | --collection ""]
                                  [--since <ISO8601>] [--until <ISO8601>]
                                  [--decision-id <id>]
                                  [--model-artifact-id <id>]
                                  [--model-name <name>]
                                  [--include-no-model-score]
                                  [--take <N>]

        选项：
          --out <directory>              输出目录（必填；不存在时自动创建）
          --workspace <id>               workspace 作用域（默认：当前 ControlRoom 选中工作区）
          --collection <id>              collection 作用域（默认：当前选中集合；传空字符串表示跨集合）
          --since <ISO8601>             仅导出 MaterializedAt >= Since 的条目
          --until <ISO8601>             仅导出 MaterializedAt <= Until 的条目
          --decision-id <id>             仅导出指定 DecisionId 的条目
          --model-artifact-id <id>       关联的 ModelArtifactId（写入 manifest 用于追溯）
          --model-name <name>            关联的 ModelName（写入 manifest 用于追溯）
          --include-no-model-score       包含 ModelScore=null 的条目（默认排除；仅诊断用途）
          --take <N>                     最大导出条目数（0 = 不限制）

        输出：
          {out}/calibration-data.jsonl          — 校准样本（每行一条 JSONL）
          {out}/calibration-data.manifest.json   — 清单（含 SHA-256、正负样本统计与追溯信息）

        说明：
          predicted = ModelScore（模型原始推理分数）
          observed  = IsSelected（二分类实际结果）
          weight    = UtilityContribution（Expert 贡献比例，默认 1.0）
        """);
    }
}
