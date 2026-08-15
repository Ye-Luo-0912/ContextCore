using System.Text.Json;
using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Quality;

namespace ContextCore.Evaluation.Commands;

public static partial class EvalCommand
{
    /// <summary>
    /// 质量指标契约冒烟检查：用内置固定数据集计算全部指标与切片，
    /// 连续计算两次并逐位比对，验证同一输入重复执行得到一致结论。
    /// 输出 JSON 写入被忽略的 artifacts/，不产生报告 Markdown。
    /// </summary>
    private static async Task ExecuteQualityMetricsSmokeAsync(
        IEvalHost service,
        IReadOnlyList<string> args,
        string subcommand,
        CancellationToken cancellationToken)
    {
        var outputPath = CommandHelpers.GetOption(args, "--out")
            ?? Path.Combine("artifacts", "quality", "quality-metrics-smoke.json");
        var k = CommandHelpers.GetIntOption(args, "--k", QualityMetricContracts.DefaultK);
        var tokenBudget = CommandHelpers.GetIntOption(args, "--budget", QualityMetricContracts.DefaultTokenBudget);

        var smoke = BuildSmokeDataset();
        var slices = smoke
            .GroupBy(s => s.Key.NormalizedKey)
            .Select(g => (Key: g.First().Key, Samples: (IReadOnlyList<SampleQualityMetrics>)g.Select(x => x.Metrics).ToArray()))
            .ToArray();
        var aggregation = QualityMetrics.AggregateBySlice(slices);

        // 同一输入重复执行：两次计算逐位一致才算通过。
        var repeat = QualityMetrics.AggregateBySlice(slices);
        var determinismPassed = JsonSerializer.Serialize(aggregation) == JsonSerializer.Serialize(repeat);

        var payload = new QualityMetricsSmokeReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            ContractVersion = "lr0e-v1",
            K = k,
            TokenBudget = tokenBudget,
            DeterminismPassed = determinismPassed,
            Overall = aggregation.Overall,
            Slices = aggregation.Slices
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"[QualityMetrics] JSON: {Path.GetFullPath(outputPath)}");
        Console.WriteLine($"[QualityMetrics] 契约版本 lr0e-v1，K={k}，TokenBudget={tokenBudget}");
        Console.WriteLine($"[QualityMetrics] 确定性（两次计算逐位一致）: {determinismPassed}");
        Console.WriteLine($"[QualityMetrics] 样本数={aggregation.Overall.SampleCount}（不可评分 {aggregation.Overall.UnscorableCount}），" +
            $"Recall@{k}={Format(aggregation.Overall.RecallAtKMean)}，" +
            $"Recall@TokenBudget={Format(aggregation.Overall.RecallAtTokenBudgetMean)}，" +
            $"Precision@{k}={Format(aggregation.Overall.PrecisionAtKMean)}，" +
            $"MRR={Format(aggregation.Overall.MrrMean)}，" +
            $"nDCG@{k}={Format(aggregation.Overall.NdcgAtKMean)}，" +
            $"关键证据漏失率={aggregation.Overall.KeyEvidenceMissRate:F3}");
    }

    private static string Format(double? value) => value.HasValue ? value.Value.ToString("F3") : "N/A";

    private static IReadOnlyList<(QualitySliceKey Key, SampleQualityMetrics Metrics)> BuildSmokeDataset()
    {
        var k = QualityMetricContracts.DefaultK;
        var budget = QualityMetricContracts.DefaultTokenBudget;
        return
        [
            // InMemory / 单问句：全命中。
            (Slice("d1", "ChatMode", "InMemory", "1"),
                QualityMetrics.EvaluateSample("smoke-01", Required("r1", "r2"),
                    Ranked(("r1", 300, 0), ("r2", 400, 0), ("x", 100, 0)), k, budget)),
            // InMemory / 单问句：一条漏失（关键证据漏失率分母）。
            (Slice("d1", "ChatMode", "InMemory", "1"),
                QualityMetrics.EvaluateSample("smoke-02", Required("r3", "r4"),
                    Ranked(("r3", 200, 0), ("y", 150, 0)), k, budget)),
            // FileSystem / 多问句：带相关等级与禁止证据。
            (Slice("d1", "ChatMode", "FileSystem", "4"),
                QualityMetrics.EvaluateSample("smoke-03",
                    new QualityEvidenceExpectation
                    {
                        RequiredEvidenceIds = ["r5"],
                        RelevantEvidenceIds = [new RelevantEvidenceGrade { EvidenceId = "r6", Grade = 2 }],
                        ForbiddenExcludedIds = ["bad"]
                    },
                    Ranked(("r6", 100, 0), ("r5", 250, 0), ("bad", 50, 0)), k, budget)),
            // Postgres / 多问句：无正相关证据（不可评分，聚合排除）。
            (Slice("d1", "ChatMode", "Postgres", "4"),
                QualityMetrics.EvaluateSample("smoke-04", new QualityEvidenceExpectation(),
                    Ranked(("x", 100, 0)), k, budget))
        ];
    }

    private static QualitySliceKey Slice(string dataset, string mode, string provider, string bucket) =>
        new() { Dataset = dataset, Mode = mode, Provider = provider, QueryCountBucket = bucket };

    private static QualityEvidenceExpectation Required(params string[] ids) =>
        new() { RequiredEvidenceIds = ids };

    private static RankedEvidence[] Ranked(params (string Id, int Tokens, int Grade)[] items) =>
        items.Select(i => new RankedEvidence { EvidenceId = i.Id, TokenCount = i.Tokens, RelevanceGrade = i.Grade }).ToArray();
}

/// <summary>质量指标冒烟检查报告（机器契约，写入 artifacts/）。</summary>
public sealed class QualityMetricsSmokeReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string ContractVersion { get; init; } = string.Empty;

    public int K { get; init; }

    public int TokenBudget { get; init; }

    /// <summary>同一输入两次计算是否逐位一致。</summary>
    public bool DeterminismPassed { get; init; }

    public QualityMetricAggregate Overall { get; init; } = new();

    public IReadOnlyList<QualitySliceResult> Slices { get; init; } = Array.Empty<QualitySliceResult>();
}
