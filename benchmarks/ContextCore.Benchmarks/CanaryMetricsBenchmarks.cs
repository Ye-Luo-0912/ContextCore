using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Benchmarks;

// ===========================================================================
// R29 WP-F-1：Canary Metrics 微基准
//
// 覆盖：
//   §1 DefaultCanaryMetricsCollector.RecordObservation（ring buffer + 滚动计数器）
//      - 小规模（n=100）/ 中规模（n=1000）/ 大规模（n=10000）
//      - 包含容量内 vs 容量溢出（触发 EvictOldest）两条路径
//   §2 DefaultCanaryMetricsCollector.GetAggregatedMetrics（聚合 + DDSketch quantile）
//   §3 DDSketch.Add + GetQuantile（P95 估算微基准，绕过 collector 测纯 sketch 性能）
//
// 指标：Mean / Median / StdDev / P95（BenchmarkDotNet 默认）+ Allocated bytes（[MemoryDiagnoser]）
//
// 依赖：
//   - DefaultCanaryMetricsCollector（public，含 ring buffer + DDSketch）
//   - DDSketch（internal sealed，通过 ContextCore.Core 的 InternalsVisibleTo 暴露）
//   - ParityReport（R28-B B-2 双路径对比报告）
// ===========================================================================

/// <summary>
/// WP-F-1 §1+§2：Canary Metrics Collector 微基准。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class CanaryMetricsCollectorBenchmarks
{
    private List<ParityReport> _parityReports = null!;
    private List<(TimeSpan v2, TimeSpan? legacy)> _durations = null!;

    [Params(100, 1000, 10000)]
    public int ObservationCount { get; set; }

    // 容量上限：1000（DefaultMaxSamplesPerRun）。ObservationCount > 1000 时触发 EvictOldest。
    private const int CollectorCapacity = 1000;

    [GlobalSetup]
    public void Setup()
    {
        _parityReports = new List<ParityReport>(ObservationCount);
        _durations = new List<(TimeSpan, TimeSpan?)>(ObservationCount);
        var rand = new Random(20260725);

        for (int i = 0; i < ObservationCount; i++)
        {
            // 80% Hard，15% Diagnostic，5% Divergent（模拟真实分布）
            var level = (rand.NextDouble() switch
            {
                < 0.05 => ParityLevel.Divergent,
                < 0.20 => ParityLevel.Diagnostic,
                _ => ParityLevel.Hard
            });

            var v2Selected = 8 + rand.Next(0, 5);
            var legacySelected = 8 + rand.Next(0, 5);
            var common = Math.Min(v2Selected, legacySelected);
            var onlyV2 = v2Selected - common;
            var onlyLegacy = legacySelected - common;
            var union = v2Selected + legacySelected - common;
            var jaccard = union == 0 ? 1.0 : (double)common / union;

            _parityReports.Add(new ParityReport(
                LegacySelectedCount: legacySelected,
                V2SelectedCount: v2Selected,
                CommonSelectedCount: common,
                OnlyInLegacyCount: onlyLegacy,
                OnlyInV2Count: onlyV2,
                JaccardIndex: jaccard,
                ParityLevel: level,
                LegacyTokenTotal: legacySelected * 100,
                V2TokenTotal: v2Selected * 100,
                WorkingSetCandidateCount: 12));

            // V2 latency ~5-20ms，Legacy latency ~8-25ms（模拟 V2 略快于 Legacy）
            var v2Ms = 5 + rand.Next(0, 15);
            var legacyMs = 8 + rand.Next(0, 17);
            _durations.Add((TimeSpan.FromMilliseconds(v2Ms), TimeSpan.FromMilliseconds(legacyMs)));
        }
    }

    // §1 RecordObservation：写入 N 次观察（含 ring buffer eviction 路径）
    [Benchmark]
    [BenchmarkCategory("Record")]
    public void RecordObservations()
    {
        var collector = new DefaultCanaryMetricsCollector(maxSamplesPerRun: CollectorCapacity);
        const string runId = "bench-run";
        for (int i = 0; i < ObservationCount; i++)
        {
            var (v2, legacy) = _durations[i];
            collector.RecordObservation(
                runId,
                _parityReports[i],
                v2Succeeded: true,
                legacySucceeded: true,
                v2Duration: v2,
                legacyDuration: legacy);
        }
    }

    // §2 GetAggregatedMetrics：写入 N 次后聚合查询
    [Benchmark]
    [BenchmarkCategory("Aggregate")]
    public CanaryObservationMetrics GetAggregatedMetrics()
    {
        var collector = new DefaultCanaryMetricsCollector(maxSamplesPerRun: CollectorCapacity);
        const string runId = "bench-run";
        for (int i = 0; i < ObservationCount; i++)
        {
            var (v2, legacy) = _durations[i];
            collector.RecordObservation(
                runId,
                _parityReports[i],
                v2Succeeded: true,
                legacySucceeded: true,
                v2Duration: v2,
                legacyDuration: legacy);
        }
        return collector.GetAggregatedMetrics(runId);
    }

    // §2b GetAggregatedMetrics 在满载 ring buffer 后查询（触发 DDSketch quantile 估算）
    [Benchmark]
    [BenchmarkCategory("Aggregate")]
    public CanaryObservationMetrics GetAggregatedMetrics_AfterEviction()
    {
        var collector = new DefaultCanaryMetricsCollector(maxSamplesPerRun: CollectorCapacity);
        const string runId = "bench-run";
        // 写入 ObservationCount 次，超过容量触发 eviction
        for (int i = 0; i < ObservationCount; i++)
        {
            var (v2, legacy) = _durations[i];
            collector.RecordObservation(
                runId,
                _parityReports[i],
                v2Succeeded: i % 50 != 0, // 模拟 2% V2 失败
                legacySucceeded: i % 30 != 0, // 模拟 3.3% Legacy 失败
                v2Duration: v2,
                legacyDuration: legacy);
        }
        return collector.GetAggregatedMetrics(runId);
    }
}

/// <summary>
/// WP-F-1 §3：DDSketch 微基准（绕过 collector 测纯 sketch 性能）。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DDSketchBenchmarks
{
    private double[] _values = null!;

    [Params(100, 1000, 10000)]
    public int ValueCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // 对数正态分布（模拟真实 latency 分布：长尾）
        _values = new double[ValueCount];
        var rand = new Random(20260725);
        for (int i = 0; i < ValueCount; i++)
        {
            // latency 1-50ms，少量长尾到 200ms
            var baseMs = 1 + rand.NextDouble() * 49;
            if (rand.NextDouble() < 0.05) baseMs += rand.NextDouble() * 150;
            _values[i] = baseMs;
        }
    }

    // §3a Add：写入 N 个值
    [Benchmark]
    [BenchmarkCategory("Add")]
    public double AddMany()
    {
        var sketch = new DDSketch(relativeAccuracy: 0.01);
        for (int i = 0; i < ValueCount; i++)
        {
            sketch.Add(_values[i]);
        }
        return sketch.TotalCount;
    }

    // §3b Add + GetQuantile：写入后查询 P95
    [Benchmark]
    [BenchmarkCategory("Query")]
    public double AddAndGetP95()
    {
        var sketch = new DDSketch(relativeAccuracy: 0.01);
        for (int i = 0; i < ValueCount; i++)
        {
            sketch.Add(_values[i]);
        }
        return sketch.GetQuantile(0.95);
    }

    // §3c GetQuantile 在已填充 sketch 上查询多次（测 quantile 估算本身开销）
    [Benchmark]
    [BenchmarkCategory("Query")]
    public double QueryP95MultipleTimes()
    {
        var sketch = new DDSketch(relativeAccuracy: 0.01);
        for (int i = 0; i < ValueCount; i++)
        {
            sketch.Add(_values[i]);
        }
        // 查询 5 次（模拟生产环境多次读取 P95）
        double last = 0;
        for (int q = 0; q < 5; q++)
        {
            last = sketch.GetQuantile(0.95);
        }
        return last;
    }
}
