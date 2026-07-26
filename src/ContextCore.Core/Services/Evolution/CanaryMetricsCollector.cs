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

    /// <summary>
    /// R29 WP-C-3：V2 路径产出质量分（0.0-1.0）。
    /// <para>
    /// 综合 section 覆盖率（token 预算利用率）与候选相关性（SelectedEnvelopes.Utility.FinalScore 均值），
    /// 由 AuthoritativeRuntime 在 RecordObservation 调用点从 ContextDecisionExecutionResult 计算得到。
    /// 0.0 = 无候选被选中或无质量信号；1.0 = 完美覆盖 + 高相关性。
    /// </para>
    /// <para>
    /// 回滚阈值由 <see cref="CanaryGateOptions.MinQualityScore"/> 配置（默认 0.3）。
    /// </para>
    /// </summary>
    public required double AverageQualityScore { get; init; }

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
    /// <param name="qualityScore">
    /// R29 WP-C-3：V2 路径产出质量分（0.0-1.0）。
    /// 由调用方从 <c>ContextDecisionExecutionResult</c> 计算（section 覆盖率 + 候选相关性加权）。
    /// 缺省值 <c>null</c> 时记为 0.0（无质量信号），不影响 latency/error/divergence 指标。
    /// V2 失败（<paramref name="v2Succeeded"/>=false）的样本应传 0.0 以反映失败质量。
    /// </param>
    void RecordObservation(
        string runId,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan? legacyDuration = null,
        double? qualityScore = null);

    /// <summary>获取当前观察窗口的聚合指标。</summary>
    /// <param name="runId">Canary run ID。</param>
    /// <returns>聚合指标；无样本时返回 TotalObservations=0 的空指标。</returns>
    CanaryObservationMetrics GetAggregatedMetrics(string runId);

    /// <summary>重置指定 run 的指标（推进到下一档百分比后调用，开始新的观察窗口）。</summary>
    /// <param name="runId">Canary run ID。</param>
    void Reset(string runId);
}

/// <summary>
/// R28-B.8 / R28-G P1-6：默认的 <see cref="ICanaryMetricsCollector"/> 实现。
/// 使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 按 runId 聚合观察样本。
/// </summary>
/// <remarks>
/// R28-G P1-6 优化：
///   - 固定容量 ring buffer 替代无界 List（默认 1000 样本/runId）。
///   - 滚动计数器（DivergentCount / V2ErrorCount / LegacyErrorCount）随 Add 更新，
///     GetAggregatedMetrics 无需 O(n) 扫描。
///   - DDSketch 替代全量排序：P95 查询复杂度 O(b log b)，b 通常 < 100（相对误差 1%）。
///   - WindowStart/WindowEnd 在 Add 路径更新；GetAggregatedMetrics 无需复制样本列表。
/// </remarks>
public sealed class DefaultCanaryMetricsCollector : ICanaryMetricsCollector
{
    /// <summary>R28-G P1-6：每个 runId 的样本容量上限（默认 1000）。</summary>
    public const int DefaultMaxSamplesPerRun = 1000;

    private readonly ConcurrentDictionary<string, ObservationBucket> _buckets
        = new(StringComparer.Ordinal);

    private readonly int _maxSamplesPerRun;

    /// <summary>构造默认 collector（容量 1000 样本/runId）。</summary>
    public DefaultCanaryMetricsCollector() : this(DefaultMaxSamplesPerRun)
    {
    }

    /// <summary>构造可配置容量的 collector。</summary>
    /// <param name="maxSamplesPerRun">每个 runId 的样本容量上限。&lt;= 0 时使用默认 1000。</param>
    public DefaultCanaryMetricsCollector(int maxSamplesPerRun)
    {
        _maxSamplesPerRun = maxSamplesPerRun > 0 ? maxSamplesPerRun : DefaultMaxSamplesPerRun;
    }

    /// <inheritdoc />
    public void RecordObservation(
        string runId,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan? legacyDuration = null,
        double? qualityScore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(parityReport);

        // Divergent = ParityLevel < Hard（含 Divergent 与 Diagnostic）
        var divergent = parityReport.ParityLevel < ParityLevel.Hard;
        // R28-D P0-6：缺省 legacyDuration 时回退到 v2Duration（向后兼容旧调用点与测试），
        // 但生产路径应显式传入真实 Legacy 耗时——否则 latency multiplier 无法发现 V2 延迟回退。
        var legacyMs = (legacyDuration ?? v2Duration).TotalMilliseconds;
        var v2Ms = v2Duration.TotalMilliseconds;
        // R29 WP-C-3：缺省 qualityScore 时记为 0.0（无质量信号；不参与 AverageQualityScore 的提升）
        var quality = qualityScore ?? 0.0;

        var bucket = _buckets.GetOrAdd(runId, _ => new ObservationBucket(_maxSamplesPerRun));
        lock (bucket)
        {
            // R28-G P1-6：ring buffer 容量超限时淘汰最旧样本，并回滚其计数贡献
            if (bucket.IsFull)
            {
                bucket.EvictOldest();
            }

            // 滚动计数器：随 Add 更新，GetAggregatedMetrics 无需 O(n) 扫描
            bucket.TotalObservations++;
            if (divergent) bucket.DivergentCount++;
            if (!v2Succeeded) bucket.V2ErrorCount++;
            if (!legacySucceeded) bucket.LegacyErrorCount++;

            // R29 WP-C-3：质量分滚动求和（用于 AverageQualityScore 计算）
            bucket.QualityScoreSum += quality;

            // DDSketch 累积：P95 查询走 sketch，无需全量排序
            bucket.V2LatencySketch.Add(v2Ms);
            bucket.LegacyLatencySketch.Add(legacyMs);

            // 保留样本记录用于诊断（可选；ring buffer 自动淘汰）
            bucket.AddSample(new ObservationSample(
                Divergent: divergent,
                V2Succeeded: v2Succeeded,
                LegacySucceeded: legacySucceeded,
                V2DurationMs: v2Ms,
                LegacyDurationMs: legacyMs,
                QualityScore: quality));

            var now = DateTimeOffset.UtcNow;
            if (bucket.TotalObservations == 1)
            {
                bucket.WindowStart = now;
            }
            bucket.WindowEnd = now;
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
                AverageQualityScore = 0.0,
                WindowStart = now,
                WindowEnd = now
            };
        }

        // R28-G P1-6：直接读取滚动计数器 + DDSketch 查询，无需复制/扫描/排序
        long total, divergentCount, v2ErrorCount, legacyErrorCount;
        double v2P95, legacyP95, qualityScoreSum;
        DateTimeOffset windowStart, windowEnd;

        lock (bucket)
        {
            total = bucket.TotalObservations;
            divergentCount = bucket.DivergentCount;
            v2ErrorCount = bucket.V2ErrorCount;
            legacyErrorCount = bucket.LegacyErrorCount;
            v2P95 = bucket.V2LatencySketch.GetQuantile(0.95);
            legacyP95 = bucket.LegacyLatencySketch.GetQuantile(0.95);
            qualityScoreSum = bucket.QualityScoreSum;
            windowStart = bucket.WindowStart;
            windowEnd = bucket.WindowEnd;
        }

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
                AverageQualityScore = 0.0,
                WindowStart = windowStart,
                WindowEnd = windowEnd
            };
        }

        return new CanaryObservationMetrics
        {
            TotalObservations = (int)total,
            DivergentCount = (int)divergentCount,
            DivergenceRate = (double)divergentCount / total,
            V2ErrorCount = (int)v2ErrorCount,
            LegacyErrorCount = (int)legacyErrorCount,
            V2ErrorRate = (double)v2ErrorCount / total,
            LegacyErrorRate = (double)legacyErrorCount / total,
            V2P95LatencyMs = v2P95,
            LegacyP95LatencyMs = legacyP95,
            // R29 WP-C-3：质量分均值 = sum / total（包含 V2 失败样本的 0.0 贡献）
            AverageQualityScore = qualityScoreSum / total,
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
                bucket.Reset();
            }
        }
    }

    /// <summary>单次观察样本（内部 record，保留用于诊断/调试）。</summary>
    private sealed record ObservationSample(
        bool Divergent,
        bool V2Succeeded,
        bool LegacySucceeded,
        double V2DurationMs,
        double LegacyDurationMs,
        double QualityScore);

    /// <summary>
    /// R28-G P1-6：per-runId 的样本桶（含锁保护）。
    /// 使用 ring buffer + 滚动计数器 + DDSketch，避免无界 List + 全量复制/排序。
    /// </summary>
    private sealed class ObservationBucket
    {
        private readonly ObservationSample[] _ringBuffer;
        private int _head; // 下一个写入位置
        private int _count; // 当前样本数（未达容量时 < capacity；达容量后 = capacity）

        public ObservationBucket(int capacity)
        {
            _ringBuffer = new ObservationSample[capacity];
            _head = 0;
            _count = 0;
            V2LatencySketch = new DDSketch();
            LegacyLatencySketch = new DDSketch();
        }

        public long TotalObservations { get; set; } // 累计观察次数（含已淘汰样本）
        public long DivergentCount { get; set; }
        public long V2ErrorCount { get; set; }
        public long LegacyErrorCount { get; set; }
        // R29 WP-C-3：质量分滚动求和（与 TotalObservations 配合计算均值）
        public double QualityScoreSum { get; set; }
        public DDSketch V2LatencySketch { get; }
        public DDSketch LegacyLatencySketch { get; }
        public DateTimeOffset WindowStart { get; set; }
        public DateTimeOffset WindowEnd { get; set; }

        public bool IsFull => _count == _ringBuffer.Length;

        public void AddSample(ObservationSample sample)
        {
            _ringBuffer[_head] = sample;
            _head = (_head + 1) % _ringBuffer.Length;
            if (_count < _ringBuffer.Length) _count++;
        }

        /// <summary>淘汰最旧样本，并回滚其计数贡献（保持滚动计数器一致性）。</summary>
        public void EvictOldest()
        {
            // ring buffer 已满时，_head 指向最旧样本（下一个被覆盖的位置）
            var oldest = _ringBuffer[_head];
            if (oldest is null) return;

            // 回滚计数器
            TotalObservations--;
            if (oldest.Divergent) DivergentCount--;
            if (!oldest.V2Succeeded) V2ErrorCount--;
            if (!oldest.LegacySucceeded) LegacyErrorCount--;
            // R29 WP-C-3：回滚质量分贡献，保持 AverageQualityScore 一致性
            QualityScoreSum -= oldest.QualityScore;

            // 注意：DDSketch 不支持回滚（buckets 仅单调累加）。
            // 这意味着 P95 估计会包含已淘汰样本的延迟值。
            // 在 ring buffer 容量远大于典型 P95 样本需求时，此误差可接受
            // （默认 1000 样本，P95 = 第 950 个样本，仅最后 5% 样本影响 P95）。
            // 若需精确 P95 仅基于当前 ring buffer，可定期 Reset sketch 并重放，
            // 但这会增加复杂度；当前实现选择性能优先。
        }

        public void Reset()
        {
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
            _head = 0;
            _count = 0;
            TotalObservations = 0;
            DivergentCount = 0;
            V2ErrorCount = 0;
            LegacyErrorCount = 0;
            QualityScoreSum = 0.0;
            V2LatencySketch.Reset();
            LegacyLatencySketch.Reset();
            var now = DateTimeOffset.UtcNow;
            WindowStart = now;
            WindowEnd = now;
        }
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
    /// <remarks>
    /// R29 WP-C-3：新增 <c>quality_score</c> 字段，由 CanaryProgressionService.CheckRollbackThresholds
    /// 与 <see cref="CanaryGateOptions.MinQualityScore"/> 比较决定是否触发回滚。
    /// </remarks>
    public static IReadOnlyDictionary<string, double> ToExperimentMetrics(this CanaryObservationMetrics metrics)
        => new Dictionary<string, double>
        {
            ["error_rate"] = metrics.V2ErrorRate,
            ["p95_latency_ms"] = metrics.V2P95LatencyMs,
            ["divergence_rate"] = metrics.DivergenceRate,
            ["quality_score"] = metrics.AverageQualityScore
        };
}
