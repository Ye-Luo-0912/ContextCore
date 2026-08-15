using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Quality;

namespace ContextCore.Evaluation.Commands;

public static partial class EvalCommand
{
    /// <summary>默认数据集目录（仓库内保留的评测语料区）。</summary>
    private const string DefaultDatasetDir = "eval/contexts/quality";

    /// <summary>
    /// 构建版本化分层评测集。
    /// 从声明文件构建 train/dev/test 划分，写入 dataset.json + 三个 split jsonl。
    /// 版本不可变；覆盖门未过或版本已存在（未 --force）时失败。
    /// </summary>
    private static async Task ExecuteDatasetBuildAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var declarationsPath = CommandHelpers.GetOption(args, "--in")
            ?? Path.Combine(DefaultDatasetDir, "declarations.json");
        var version = CommandHelpers.GetOption(args, "--version") ?? "v1";
        var outDirectory = CommandHelpers.GetOption(args, "--out-dir") ?? DefaultDatasetDir;
        var force = CommandHelpers.HasFlag(args, "--force");

        var declarations = await EvalDatasetBuilder.LoadDeclarationsAsync(declarationsPath, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await EvalDatasetBuilder.BuildAsync(declarations, version, outDirectory, force, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"[Dataset] 版本 {manifest.Version} 构建完成：{Path.GetFullPath(Path.Combine(outDirectory, version))}");
        Console.WriteLine($"[Dataset] 样本总数={manifest.SampleCount}，" +
            $"train={manifest.SplitCounts["train"]}，dev={manifest.SplitCounts["dev"]}，test={manifest.SplitCounts["test"]}");
        Console.WriteLine($"[Dataset] 覆盖门={manifest.CoverageComplete}，" +
            $"覆盖维度数={manifest.CoverageCounts.Count(kv => kv.Value > 0)}/{EvalCoverageDimensions.All.Count}");
        foreach (var dim in EvalCoverageDimensions.All)
        {
            Console.WriteLine($"[Dataset]   {dim}: {manifest.CoverageCounts[dim]}");
        }
    }

    /// <summary>校验已构建的数据集目录（清单/计数/可追溯性/覆盖完整性）。</summary>
    private static async Task ExecuteDatasetVerifyAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var versionDir = CommandHelpers.GetOption(args, "--dir")
            ?? Path.Combine(DefaultDatasetDir, "v1");
        var result = await EvalDatasetBuilder.VerifyAsync(versionDir, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[Dataset] 校验：{versionDir}");
        Console.WriteLine($"[Dataset] 结果={(result.Ok ? "通过" : "未通过")}");
        if (result.Manifest is not null)
        {
            Console.WriteLine($"[Dataset] 版本={result.Manifest.Version}，样本={result.Manifest.SampleCount}，" +
                $"覆盖完整={result.Manifest.CoverageComplete}");
        }
        foreach (var error in result.Errors)
        {
            Console.WriteLine($"[Dataset] 错误：{error}");
        }
    }
}
