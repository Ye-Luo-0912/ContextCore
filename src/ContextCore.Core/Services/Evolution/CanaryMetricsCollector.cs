using System.Collections.Concurrent;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// R28-B.8：Canary Metrics 采集器（工作包 C）
//
// 目标（对齐 R28-B.8 规格）：
//   1. 从 shadow/parity 报告聚合 CanaryProgressionService.EvaluateAsync 消费的三个指标：
//      divergence_rate、error_rate、p95_latency_ms。
//   2. 按 runId 维度聚合，每个 run 拥有独立的观察窗口样本列表。
//   3. 推进到下一档百分比后调用 Reset 清空样本，开始新的观察窗口。
//   4. 线程安全：每个 runId 的样本列表用 lock 保护。
//
// 设计边界：
//   - 本采集器仅负责聚合；不负责采集（采集由 AuthoritativeRuntime 的 shadow 路径调用 RecordObservation）。
//   - P95 计算：将 durations 排序，取第 95 百分位（durations[(int)(count * 0.95)]）。
//   - Divergent 定义：ParityLevel < Hard（含 Divergent 与 Diagnostic）。
// ===========================================================================

/// <summary>
/// R28-B.8：Canary 观察窗口的聚合指标。
/// </summary>
public sealed record CanaryObservationMetrics
{
    /// <summary>观察窗口内的总观察次数。</summary>
    public required int TotalObservations { get; init; }

    /// <summary>发散观察次数（ParityLevel &lt; Hard）。</summary>
    public required int DivergentCount { get; init; }

    /// <summary>发散率 = DivergentCount / TotalObservations。</summary>
    public required double DivergenceRate { get; init; }

    /// <summary>V2 路径错误次数。</summary>
    public required int V2ErrorCount { get; init; }

    /// <summary>Legacy 路径错误次数。</summary>
    public required int LegacyErrorCount { get; init; }

    /// <summary>V2 错误率 = V2ErrorCount / TotalObservations。</summary>
    public required double V2ErrorRate { get; init; }

    /// <summary>Legacy 错误率 = LegacyErrorCount / TotalObservations。</summary>
    public required double LegacyErrorRate { get; init; }

    /// <summary>V2 p95 延迟（毫秒）。</summary>
    public required double V2P95LatencyMs { get; init; }

    /// <summary>Legacy p95 延迟（毫秒）。</summary>
    public required double LegacyP95LatencyMs { get; init; }

    /// <summary>观察窗口起始时间（UTC）。</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>观察窗口结束时间（UTC）。</summary>
    public required DateTimeOffset WindowEnd { get; init; }
}

/// <summary>
/// R28-B.8：Canary Metrics 采集器接口。按 runId 聚合 shadow/parity 观察样本。
/// </summary>
public interface ICanaryMetricsCollector
{
    /// <summary>记录一次 shadow/parity 评估结果。</summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="parityReport">parity 对比报告（从中提取 ParityLevel 判定是否 Divergent）。</param>
    /// <param name="v2Succeeded">V2 路径是否成功（false 计入 V2ErrorCount）。</param>
    /// <param name="legacySucceeded">Legacy 路径是否成功（false 计入 LegacyErrorCount）。</param>
    /// <param name="v2Duration">V2 路径执行耗时（用于 V2 P95 计算）。</param>
    /// <param name="legacyDuration">
    /// R28-D P0-6：Legacy 路径执行耗时（用于 Legacy P95 计算）。
    /// 缺省值 <c>null</c> 时回退到 <paramref name="v2Duration"/>（向后兼容旧调用点），
    /// 但生产路径应显式传入真实 Legacy 耗时——否则 latency multiplier 无法发现 V2 延迟回退。
    /// </param>
    void RecordObservation(
        string runId,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan? legacyDuration = null);

    /// <summary>获取当前观察窗口的聚合指标。</summary>
    /// <param name="runId">Canary run ID。</param>
    /// <returns>聚合指标；无样本时返回 TotalObservations=0 的空指标。</returns>
    CanaryObservationMetrics GetAggregatedMetrics(string runId);

    /// <summary>重置指定 run 的指标（推进到下一档百分比后调用，开始新的观察窗口）。</summary>
    /// <param name="runId">Canary run ID。</param>
    void Reset(string runId);
}

/// <summary>
/// R28-B.8：默认的 <see cref="ICanaryMetricsCollector"/> 实现。
/// 使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 按 runId 聚合观察样本。
/// </summary>
public sealed class DefaultCanaryMetricsCollector : ICanaryMetricsCollector
{
    /// <summary>内部观察样本（per-runId 列表的容器，含锁保护）。</summary>
    private readonly ConcurrentDictionary<string, ObservationBucket> _buckets
        = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void RecordObservation(
        string runId,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan? legacyDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(parityReport);

        // Divergent = ParityLevel < Hard（含 Divergent 与 Diagnostic）
        var divergent = parityReport.ParityLevel < ParityLevel.Hard;
        // R28-D P0-6：缺省 legacyDuration 时回退到 v2Duration（向后兼容旧调用点与测试），
        // 但生产路径应显式传入真实 Legacy 耗时——否则 latency multiplier 无法发现 V2 延迟回退。
        var legacyMs = (legacyDuration ?? v2Duration).TotalMilliseconds;
        var sample = new ObservationSample(
            Divergent: divergent,
            V2Succeeded: v2Succeeded,
            LegacySucceeded: legacySucceeded,
            V2DurationMs: v2Duration.TotalMilliseconds,
            LegacyDurationMs: legacyMs);

        var bucket = _buckets.GetOrAdd(runId, _ => new ObservationBucket());
        lock (bucket)
        {
            bucket.Samples.Add(sample);
            if (bucket.Samples.Count == 1)
            {
                bucket.WindowStart = DateTimeOffset.UtcNow;
            }
            bucket.WindowEnd = DateTimeOffset.UtcNow;
        }
    }

    /// <inheritdoc />
    public CanaryObservationMetrics GetAggregatedMetrics(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (!_buckets.TryGetValue(runId, out var bucket))
        {
            var now = DateTimeOffset.UtcNow;
            return new CanaryObservationMetrics
            {
                TotalObservations = 0,
                DivergentCount = 0,
                DivergenceRate = 0.0,
                V2ErrorCount = 0,
                LegacyErrorCount = 0,
                V2ErrorRate = 0.0,
                LegacyErrorRate = 0.0,
                V2P95LatencyMs = 0.0,
                LegacyP95LatencyMs = 0.0,
                WindowStart = now,
                WindowEnd = now
            };
        }

        List<ObservationSample> snapshot;
        DateTimeOffset windowStart;
        DateTimeOffset windowEnd;
        lock (bucket)
        {
            snapshot = new List<ObservationSample>(bucket.Samples);
            windowStart = bucket.WindowStart;
            windowEnd = bucket.WindowEnd;
        }

        var total = snapshot.Count;
        if (total == 0)
        {
            return new CanaryObservationMetrics
            {
                TotalObservations = 0,
                DivergentCount = 0,
                DivergenceRate = 0.0,
                V2ErrorCount = 0,
                LegacyErrorCount = 0,
                V2ErrorRate = 0.0,
                LegacyErrorRate = 0.0,
                V2P95LatencyMs = 0.0,
                LegacyP95LatencyMs = 0.0,
                WindowStart = windowStart,
                WindowEnd = windowEnd
            };
        }

        var divergentCount = snapshot.Count(s => s.Divergent);
        var v2ErrorCount = snapshot.Count(s => !s.V2Succeeded);
        var legacyErrorCount = snapshot.Count(s => !s.LegacySucceeded);

        return new CanaryObservationMetrics
        {
            TotalObservations = total,
            DivergentCount = divergentCount,
            DivergenceRate = (double)divergentCount / total,
            V2ErrorCount = v2ErrorCount,
            LegacyErrorCount = legacyErrorCount,
            V2ErrorRate = (double)v2ErrorCount / total,
            LegacyErrorRate = (double)legacyErrorCount / total,
            V2P95LatencyMs = ComputeP95(snapshot.Select(s => s.V2DurationMs).ToList()),
            // R28-D P0-6：使用真实 Legacy 耗时计算 Legacy P95（修复此前用 V2 P95 近似导致
            // latency multiplier 无法发现 V2 延迟回退的问题）。
            LegacyP95LatencyMs = ComputeP95(snapshot.Select(s => s.LegacyDurationMs).ToList()),
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
    }

    /// <inheritdoc />
    public void Reset(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_buckets.TryGetValue(runId, out var bucket))
        {
            lock (bucket)
            {
                bucket.Samples.Clear();
                var now = DateTimeOffset.UtcNow;
                bucket.WindowStart = now;
                bucket.WindowEnd = now;
            }
        }
    }

    /// <summary>
    /// 计算 P95 百分位：将 durations 升序排序后取第 95 百分位索引。
    /// </summary>
    private static double ComputeP95(List<double> durations)
    {
        if (durations.Count == 0)
        {
            return 0.0;
        }
        durations.Sort();
        var index = (int)(durations.Count * 0.95);
        if (index >= durations.Count)
        {
            index = durations.Count - 1;
        }
        return durations[index];
    }

    /// <summary>单次观察样本（内部 record）。</summary>
    private sealed record ObservationSample(
        bool Divergent,
        bool V2Succeeded,
        bool LegacySucceeded,
        double V2DurationMs,
        double LegacyDurationMs);

    /// <summary>per-runId 的样本桶（含锁保护）。</summary>
    private sealed class ObservationBucket
    {
        public List<ObservationSample> Samples { get; } = new();
        public DateTimeOffset WindowStart { get; set; }
        public DateTimeOffset WindowEnd { get; set; }
    }
}

/// <summary>
/// R28-B.8：<see cref="CanaryObservationMetrics"/> 扩展方法，
/// 转换为 <see cref="CanaryProgressionService"/> 可消费的 baseline/experiment 指标字典。
/// </summary>
public static class CanaryMetricsExtensions
{
    /// <summary>将 <see cref="CanaryObservationMetrics"/> 转换为基线（Legacy 路径）指标字典。</summary>
    public static IReadOnlyDictionary<string, double> ToBaselineMetrics(this CanaryObservationMetrics metrics)
        => new Dictionary<string, double>
        {
            ["error_rate"] = metrics.LegacyErrorRate,
            ["p95_latency_ms"] = metrics.LegacyP95LatencyMs
        };

    /// <summary>将 <see cref="CanaryObservationMetrics"/> 转换为实验路径（V2 路径）指标字典。</summary>
    public static IReadOnlyDictionary<string, double> ToExperimentMetrics(this CanaryObservationMetrics metrics)
        => new Dictionary<string, double>
        {
            ["error_rate"] = metrics.V2ErrorRate,
            ["p95_latency_ms"] = metrics.V2P95LatencyMs,
            ["divergence_rate"] = metrics.DivergenceRate
        };
}
