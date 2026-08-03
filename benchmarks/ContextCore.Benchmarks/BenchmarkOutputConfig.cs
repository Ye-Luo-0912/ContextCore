using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace ContextCore.Benchmarks;

/// <summary>
/// 统一基准输出配置。
/// 固定 artifacts 路径到 <c>benchmarks/results/</c>，确保每次运行产生 JSON + Markdown 报告，
/// 便于建立 baseline/current 对比工作流：
/// <list type="bullet">
/// <item>变更前运行 → 复制 JSON 报告为 <c>benchmarks/results/baseline.json</c></item>
/// <item>变更后运行 → 复制 JSON 报告为 <c>benchmarks/results/current.json</c></item>
/// <item>使用 BenchmarkDotNet 的 <c>--diff baseline.json</c> 生成对比</li>
/// </list>
/// </summary>
/// <remarks>
/// 已采集指标（BenchmarkDotNet 内置）：
/// Mean / Median / StdDev / StdErr、p50 / p95（Percentile 列）、
/// Allocated bytes（[MemoryDiagnoser]）、Gen0 / Gen1 / Gen2 collections。
///
/// 待补充域指标（需自定义 EventCounter 或手动埋点）：
/// File I/O bytes、DB query count、Cache hit/miss、trace write amplification。
/// </remarks>
public class BenchmarkOutputConfig : ManualConfig
{
    public BenchmarkOutputConfig()
    {
        // 固定输出路径：benchmarks/results/
        // 从 bin/Debug/net10.0/ 向上四级到 benchmarks/，再进入 results/
        var resultsPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "..", "..", "..", "..", "results"));
        ArtifactsPath = resultsPath;

        // 集中化 Job 配置，提供可靠的迭代次数下限，
        // 避免 CI 因样本数不足 / 噪声触发假阳性回归告警。
        // 各 benchmark 类不再声明 [SimpleJob]，统一由此处决定测量参数，
        // 防止 config Job 与 attribute Job 并存导致重复运行。
        // - MinWarmupCount=3 / MaxWarmupCount=10：足够预热 JIT 与 CPU 分支预测器
        // - MinIterationCount=15 / MaxIterationCount=25：N≥15 保证 StdErr 收敛，
        // 配合 benchmark-compare.sh 的置信区间检查（2×StdErr）抑制噪声假阳性
        // - 离群值剔除（OutlierMode.RemoveUpper）为 BenchmarkDotNet 默认行为，保持默认即可
        // - 不设置 MaxAbsoluteError / MeanAbsoluteError：误差驱动的迭代次数在 CI 上不可预测，
        // 固定上下限更可控；置信区间检查由 benchmark-compare.sh 在统计层完成
        AddJob(Job.Default
            .WithMinWarmupCount(3)
            .WithMaxWarmupCount(10)
            .WithMinIterationCount(15)
            .WithMaxIterationCount(25));

        // JSON 全量导出（含所有统计指标），用于 baseline/current 对比
        AddExporter(JsonExporter.Full);
        // Markdown GitHub 格式导出，便于 review
        AddExporter(MarkdownExporter.GitHub);
        // CSV 导出，便于脚本处理
        AddExporter(CsvExporter.Default);

        AddLogger(ConsoleLogger.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
    }
}
