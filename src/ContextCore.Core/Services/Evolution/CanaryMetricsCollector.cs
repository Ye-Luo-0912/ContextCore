using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// Canary Metrics 采集器（工作包 C）
//
// 目标：
// 1. 从 shadow/parity 报告聚合 CanaryProgressionService.EvaluateAsync 消费的三个指标：
// divergence_rate、error_rate、p95_latency_ms。
// 2. 按 runId 维度聚合，每个 run 拥有独立的观察窗口样本列表。
// 3. 推进到下一档百分比后调用 Reset 清空样本，开始新的观察窗口。
// 4. 线程安全：每个 runId 的样本列表用 lock 保护。
//
// 设计边界：
// - 本采集器仅负责聚合；不负责采集（采集由 AuthoritativeRuntime 的 shadow 路径调用 RecordObservation）。
// - P95 计算：将 durations 排序，取第 95 百分位（durations[(int)(count * 0.95)]）。
// - Divergent 定义：ParityLevel < Hard（含 Divergent 与 Diagnostic）。
// ===========================================================================

/// <summary>
/// Canary 观察窗口的聚合指标。
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
    /// V2 路径产出质量分（0.0-1.0）。
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

    // -----------------------------------------------------------------------
    // 外部结果指标（ground truth 信号）。null = 未采集，聚合器应优雅跳过。
    // 与 <see cref="ExternalResultMetrics"/> 同名字段对齐，但此处为窗口内加权均值。
    // -----------------------------------------------------------------------

    /// <summary>任务成功率（1.0 = 全部成功；0.0 = 全部失败）；null = 未采集。</summary>
    public double? TaskSuccessRate { get; init; }

    /// <summary>Tool 调用成功率（1.0 = 全部成功）；null = 未采集。</summary>
    public double? ToolSuccessRate { get; init; }

    /// <summary>修复率（自动修复成功次数 / 需要修复的总次数）；null = 未采集。</summary>
    public double? RepairRate { get; init; }

    /// <summary>安全违规率（0.0 = 无违规；越高越严重）；null = 未采集。</summary>
    public double? SafetyViolationRate { get; init; }

    /// <summary>上下文精确率（相关候选 / 总候选）；null = 未采集。</summary>
    public double? ContextPrecision { get; init; }

    /// <summary>上下文召回率 proxy（命中 / 应命中）；null = 未采集。</summary>
    public double? ContextRecallProxy { get; init; }

    /// <summary>用户接受率（用户接受 / 总展示；1.0 = 全部接受）；null = 未采集。</summary>
    public double? UserAcceptance { get; init; }

    /// <summary>回答质量分（人工评分或 LLM-as-judge；范围 [0.0, 1.0]）；null = 未采集。</summary>
    public double? AnswerQuality { get; init; }

    /// <summary>Token 成本（每千次请求的 token 消耗；越低越好）；null = 未采集。</summary>
    public double? TokenCost { get; init; }

    /// <summary>推理成本（每千次请求的推理费用，单位美元；越低越好）；null = 未采集。</summary>
    public double? InferenceCost { get; init; }

    /// <summary>
    /// V2 路径延迟 DDSketch 的二进制序列化字节。
    /// 由 GetAggregatedMetrics 从本地 DDSketch.Serialize() 产出；CanaryLeaderHostedService.ToSample
    /// 透传到 canary_metrics_samples.v2_latency_sketch bytea 列，供 Leader 聚合时 MergeFrom 合并。
    /// null/空数组 = 无 sketch 数据（回退到加权平均 P95）。
    /// </summary>
    public byte[]? V2LatencySketch { get; init; }

    /// <summary>
    /// Legacy 路径延迟 DDSketch 的二进制序列化字节。语义同 <see cref="V2LatencySketch"/>。
    /// </summary>
    public byte[]? LegacyLatencySketch { get; init; }

    /// <summary>
    /// 任务成功率分子（sum of TaskSuccessRate over non-null samples）。
    /// 与 <see cref="TaskSuccessCount"/>（分母）配合，聚合时 SUM(分子) / SUM(分母) 替代 AVG(rate)。
    /// null = 未采集（TaskSuccessRate 也为 null）。
    /// </summary>
    public double? TaskSuccessSum { get; init; }

    /// <summary>
    /// 任务成功率分母（count of non-null TaskSuccessRate samples）。
    /// null = 未采集。
    /// </summary>
    public long? TaskSuccessCount { get; init; }

    /// <summary>
    /// Tool 调用成功率分子（sum of ToolSuccessRate over non-null samples）。
    /// 与 <see cref="ToolSuccessCount"/>（分母）配合，聚合时 SUM(分子) / SUM(分母) 替代 AVG(rate)。
    /// null = 未采集。
    /// </summary>
    public double? ToolSuccessSum { get; init; }

    /// <summary>
    /// Tool 调用成功率分母（count of non-null ToolSuccessRate samples）。
    /// null = 未采集。
    /// </summary>
    public long? ToolSuccessCount { get; init; }

    /// <summary>观察窗口起始时间（UTC）。</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>观察窗口结束时间（UTC）。</summary>
    public required DateTimeOffset WindowEnd { get; init; }
}

/// <summary>
/// Canary Metrics 采集器接口。按 runId 聚合 shadow/parity 观察样本。
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
    /// Legacy 路径执行耗时（用于 Legacy P95 计算）。
    /// 缺省值 <c>null</c> 时回退到 <paramref name="v2Duration"/>（向后兼容旧调用点），
    /// 但生产路径应显式传入真实 Legacy 耗时——否则 latency multiplier 无法发现 V2 延迟回退。
    /// </param>
    /// <param name="qualityScore">
    /// V2 路径产出质量分（0.0-1.0）。
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

    /// <summary>
    /// 记录一次 shadow/parity 评估结果 + 外部结果指标（ground truth 信号）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="parityReport">parity 对比报告。</param>
    /// <param name="v2Succeeded">V2 路径是否成功。</param>
    /// <param name="legacySucceeded">Legacy 路径是否成功。</param>
    /// <param name="v2Duration">V2 路径执行耗时。</param>
    /// <param name="legacyDuration">Legacy 路径执行耗时（缺省回退到 v2Duration）。</param>
    /// <param name="qualityScore">V2 路径产出质量分（0.0-1.0）。</param>
    /// <param name="externalMetrics">
    /// 外部结果指标（可为 null）。非 null 字段会存入 ring buffer 样本并参与滚动聚合；
    /// null 字段在聚合时优雅跳过（不计入均值）。当本参数整体为 null 时等价于 <see cref="RecordObservation"/>。
    /// </param>
    /// <remarks>
    /// 外部指标按"非 null 字段计数"独立聚合：每个字段维护 (sum, count) 两个滚动累加器，
    /// 均值 = sum / count（不计入未采集样本）。这与 parity/error/latency 的"全样本聚合"语义不同：
    /// 外部信号通常稀疏（如 UserAcceptance 仅在用户反馈时才有），按字段独立计数更准确。
    /// </remarks>
    void RecordObservationWithExternalMetrics(
        string runId,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan? legacyDuration = null,
        double? qualityScore = null,
        ExternalResultMetrics? externalMetrics = null);

    /// <summary>获取当前观察窗口的聚合指标。</summary>
    /// <param name="runId">Canary run ID。</param>
    /// <returns>聚合指标；无样本时返回 TotalObservations=0 的空指标。</returns>
    CanaryObservationMetrics GetAggregatedMetrics(string runId);

    /// <summary>重置指定 run 的指标（推进到下一档百分比后调用，开始新的观察窗口）。</summary>
    /// <param name="runId">Canary run ID。</param>
    void Reset(string runId);
}

/// <summary>
/// / 默认的 <see cref="ICanaryMetricsCollector"/> 实现。
/// 使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> 按 runId 聚合观察样本。
/// </summary>
/// <remarks>
/// 优化：
/// - 固定容量 ring buffer 替代无界 List（默认 1000 样本/runId）。
/// - 滚动计数器（DivergentCount / V2ErrorCount / LegacyErrorCount）随 Add 更新，
/// GetAggregatedMetrics 无需 O(n) 扫描。
/// - DDSketch 替代全量排序：P95 查询复杂度 O(b log b)，b 通常 < 100（相对误差 1%）。
/// - WindowStart/WindowEnd 在 Add 路径更新；GetAggregatedMetrics 无需复制样本列表。
/// </remarks>
public sealed class DefaultCanaryMetricsCollector : ICanaryMetricsCollector
{
    /// <summary>每个 runId 的样本容量上限（默认 1000）。</summary>
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
        => RecordObservationWithExternalMetrics(
            runId, parityReport, v2Succeeded, legacySucceeded,
            v2Duration, legacyDuration, qualityScore, externalMetrics: null);

    /// <inheritdoc />
    public void RecordObservationWithExternalMetrics(
        string runId,
        ParityReport parityReport,
        bool v2Succeeded,
        bool legacySucceeded,
        TimeSpan v2Duration,
        TimeSpan? legacyDuration = null,
        double? qualityScore = null,
        ExternalResultMetrics? externalMetrics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(parityReport);

        // Divergent = ParityLevel < Hard（含 Divergent 与 Diagnostic）
        var divergent = parityReport.ParityLevel < ParityLevel.Hard;
        // 缺省 legacyDuration 时回退到 v2Duration（向后兼容旧调用点与测试），
        // 但生产路径应显式传入真实 Legacy 耗时——否则 latency multiplier 无法发现 V2 延迟回退。
        var legacyMs = (legacyDuration ?? v2Duration).TotalMilliseconds;
        var v2Ms = v2Duration.TotalMilliseconds;
        // 缺省 qualityScore 时记为 0.0（无质量信号；不参与 AverageQualityScore 的提升）
        var quality = qualityScore ?? 0.0;

        var bucket = _buckets.GetOrAdd(runId, _ => new ObservationBucket(_maxSamplesPerRun));
        lock (bucket)
        {
            // ring buffer 容量超限时淘汰最旧样本，并回滚其计数贡献
            if (bucket.IsFull)
            {
                bucket.EvictOldest();
            }

            // 滚动计数器：随 Add 更新，GetAggregatedMetrics 无需 O(n) 扫描
            bucket.TotalObservations++;
            if (divergent) bucket.DivergentCount++;
            if (!v2Succeeded) bucket.V2ErrorCount++;
            if (!legacySucceeded) bucket.LegacyErrorCount++;

            // 质量分滚动求和（用于 AverageQualityScore 计算）
            bucket.QualityScoreSum += quality;

            // DDSketch 累积：P95 查询走 sketch，无需全量排序
            bucket.V2LatencySketch.Add(v2Ms);
            bucket.LegacyLatencySketch.Add(legacyMs);

            // 外部指标按"非 null 字段计数"独立聚合（稀疏信号按字段计数更准确）
            var sample = new ObservationSample(
                Divergent: divergent,
                V2Succeeded: v2Succeeded,
                LegacySucceeded: legacySucceeded,
                V2DurationMs: v2Ms,
                LegacyDurationMs: legacyMs,
                QualityScore: quality,
                TaskSuccessRate: externalMetrics?.TaskSuccessRate,
                ToolSuccessRate: externalMetrics?.ToolSuccessRate,
                RepairRate: externalMetrics?.RepairRate,
                SafetyViolationRate: externalMetrics?.SafetyViolationRate,
                ContextPrecision: externalMetrics?.ContextPrecision,
                ContextRecallProxy: externalMetrics?.ContextRecallProxy,
                UserAcceptance: externalMetrics?.UserAcceptance,
                AnswerQuality: externalMetrics?.AnswerQuality,
                TokenCost: externalMetrics?.TokenCost,
                InferenceCost: externalMetrics?.InferenceCost);
            bucket.AccumulateExternal(sample);

            // 保留样本记录用于诊断（可选；ring buffer 自动淘汰）
            bucket.AddSample(sample);

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
                TaskSuccessRate = null,
                ToolSuccessRate = null,
                RepairRate = null,
                SafetyViolationRate = null,
                ContextPrecision = null,
                ContextRecallProxy = null,
                UserAcceptance = null,
                AnswerQuality = null,
                TokenCost = null,
                InferenceCost = null,
                V2LatencySketch = null,
                LegacyLatencySketch = null,
                TaskSuccessSum = null,
                TaskSuccessCount = null,
                ToolSuccessSum = null,
                ToolSuccessCount = null,
                WindowStart = now,
                WindowEnd = now
            };
        }

        // 直接读取滚动计数器 + DDSketch 查询，无需复制/扫描/排序
        long total, divergentCount, v2ErrorCount, legacyErrorCount;
        double v2P95, legacyP95, qualityScoreSum;
        DateTimeOffset windowStart, windowEnd;
        ExternalAccumulator extSnapshot;
        byte[]? v2SketchBytes;
        byte[]? legacySketchBytes;

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
            extSnapshot = bucket.ExternalMetrics.Clone();
            // 序列化 DDSketch 字节，供 Leader 跨实例合并
            v2SketchBytes = bucket.V2LatencySketch.Serialize();
            legacySketchBytes = bucket.LegacyLatencySketch.Serialize();
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
                TaskSuccessRate = null,
                ToolSuccessRate = null,
                RepairRate = null,
                SafetyViolationRate = null,
                ContextPrecision = null,
                ContextRecallProxy = null,
                UserAcceptance = null,
                AnswerQuality = null,
                TokenCost = null,
                InferenceCost = null,
                V2LatencySketch = v2SketchBytes,
                LegacyLatencySketch = legacySketchBytes,
                TaskSuccessSum = extSnapshot.SumOf(FieldTaskSuccessRate),
                TaskSuccessCount = extSnapshot.CountOf(FieldTaskSuccessRate),
                ToolSuccessSum = extSnapshot.SumOf(FieldToolSuccessRate),
                ToolSuccessCount = extSnapshot.CountOf(FieldToolSuccessRate),
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
            // 质量分均值 = sum / total（包含 V2 失败样本的 0.0 贡献）
            AverageQualityScore = qualityScoreSum / total,
            // 外部指标按字段独立均值（稀疏信号不计入未采集样本）
            TaskSuccessRate = extSnapshot.MeanOf(FieldTaskSuccessRate),
            ToolSuccessRate = extSnapshot.MeanOf(FieldToolSuccessRate),
            RepairRate = extSnapshot.MeanOf(FieldRepairRate),
            SafetyViolationRate = extSnapshot.MeanOf(FieldSafetyViolationRate),
            ContextPrecision = extSnapshot.MeanOf(FieldContextPrecision),
            ContextRecallProxy = extSnapshot.MeanOf(FieldContextRecallProxy),
            UserAcceptance = extSnapshot.MeanOf(FieldUserAcceptance),
            AnswerQuality = extSnapshot.MeanOf(FieldAnswerQuality),
            TokenCost = extSnapshot.MeanOf(FieldTokenCost),
            InferenceCost = extSnapshot.MeanOf(FieldInferenceCost),
            // DDSketch 序列化字节（供 Leader 跨实例 MergeFrom 合并查询 P95）
            V2LatencySketch = v2SketchBytes,
            LegacyLatencySketch = legacySketchBytes,
            // 成功率分子/分母（供 Leader 跨实例 SUM(分子)/SUM(分母) 聚合）
            TaskSuccessSum = extSnapshot.SumOf(FieldTaskSuccessRate),
            TaskSuccessCount = extSnapshot.CountOf(FieldTaskSuccessRate),
            ToolSuccessSum = extSnapshot.SumOf(FieldToolSuccessRate),
            ToolSuccessCount = extSnapshot.CountOf(FieldToolSuccessRate),
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
        double QualityScore,
        double? TaskSuccessRate,
        double? ToolSuccessRate,
        double? RepairRate,
        double? SafetyViolationRate,
        double? ContextPrecision,
        double? ContextRecallProxy,
        double? UserAcceptance,
        double? AnswerQuality,
        double? TokenCost,
        double? InferenceCost);

    // -----------------------------------------------------------------------
    // 外部指标字段常量（用于 ExternalAccumulator 数组索引）。
    // 顺序与 CanaryObservationMetrics / ExternalResultMetrics 字段对齐。
    // -----------------------------------------------------------------------
    private const int FieldTaskSuccessRate = 0;
    private const int FieldToolSuccessRate = 1;
    private const int FieldRepairRate = 2;
    private const int FieldSafetyViolationRate = 3;
    private const int FieldContextPrecision = 4;
    private const int FieldContextRecallProxy = 5;
    private const int FieldUserAcceptance = 6;
    private const int FieldAnswerQuality = 7;
    private const int FieldTokenCost = 8;
    private const int FieldInferenceCost = 9;
    private const int ExternalFieldCount = 10;

    /// <summary>
    /// 外部指标的滚动累加器（per-field sum + count）。
    /// 每个 nullable 字段独立计数：未采集样本不计入该字段的均值，避免稀疏信号被稀释。
    /// </summary>
    private sealed class ExternalAccumulator
    {
        private readonly double[] _sum = new double[ExternalFieldCount];
        private readonly long[] _count = new long[ExternalFieldCount];

        public void Add(int field, double? value)
        {
            if (value.HasValue)
            {
                _sum[field] += value.Value;
                _count[field]++;
            }
        }

        public void Subtract(int field, double? value)
        {
            if (value.HasValue)
            {
                _sum[field] -= value.Value;
                _count[field]--;
            }
        }

        public double? MeanOf(int field)
            => _count[field] > 0 ? _sum[field] / _count[field] : null;

        /// <summary>返回指定字段的分子（sum of non-null values）；无样本时返回 null。</summary>
        public double? SumOf(int field)
            => _count[field] > 0 ? _sum[field] : null;

        /// <summary>返回指定字段的分母（count of non-null samples）；无样本时返回 null。</summary>
        public long? CountOf(int field)
            => _count[field] > 0 ? _count[field] : null;

        public ExternalAccumulator Clone()
        {
            var copy = new ExternalAccumulator();
            for (var i = 0; i < ExternalFieldCount; i++)
            {
                copy._sum[i] = _sum[i];
                copy._count[i] = _count[i];
            }
            return copy;
        }

        public void Reset()
        {
            Array.Clear(_sum, 0, ExternalFieldCount);
            Array.Clear(_count, 0, ExternalFieldCount);
        }
    }

    /// <summary>
    /// per-runId 的样本桶（含锁保护）。
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
            ExternalMetrics = new ExternalAccumulator();
        }

        public long TotalObservations { get; set; } // 累计观察次数（含已淘汰样本）
        public long DivergentCount { get; set; }
        public long V2ErrorCount { get; set; }
        public long LegacyErrorCount { get; set; }
        // 质量分滚动求和（与 TotalObservations 配合计算均值）
        public double QualityScoreSum { get; set; }
        public DDSketch V2LatencySketch { get; }
        public DDSketch LegacyLatencySketch { get; }
        // 外部指标 per-field 累加器（稀疏信号按字段计数）
        public ExternalAccumulator ExternalMetrics { get; }
        public DateTimeOffset WindowStart { get; set; }
        public DateTimeOffset WindowEnd { get; set; }

        public bool IsFull => _count == _ringBuffer.Length;

        public void AddSample(ObservationSample sample)
        {
            _ringBuffer[_head] = sample;
            _head = (_head + 1) % _ringBuffer.Length;
            if (_count < _ringBuffer.Length) _count++;
        }

        /// <summary>将样本中的外部指标累加到滚动累加器（null 字段跳过）。</summary>
        public void AccumulateExternal(ObservationSample sample)
        {
            ExternalMetrics.Add(FieldTaskSuccessRate, sample.TaskSuccessRate);
            ExternalMetrics.Add(FieldToolSuccessRate, sample.ToolSuccessRate);
            ExternalMetrics.Add(FieldRepairRate, sample.RepairRate);
            ExternalMetrics.Add(FieldSafetyViolationRate, sample.SafetyViolationRate);
            ExternalMetrics.Add(FieldContextPrecision, sample.ContextPrecision);
            ExternalMetrics.Add(FieldContextRecallProxy, sample.ContextRecallProxy);
            ExternalMetrics.Add(FieldUserAcceptance, sample.UserAcceptance);
            ExternalMetrics.Add(FieldAnswerQuality, sample.AnswerQuality);
            ExternalMetrics.Add(FieldTokenCost, sample.TokenCost);
            ExternalMetrics.Add(FieldInferenceCost, sample.InferenceCost);
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
            // 回滚质量分贡献，保持 AverageQualityScore 一致性
            QualityScoreSum -= oldest.QualityScore;

            // 回滚外部指标累加器（保持 per-field 均值一致性）
            ExternalMetrics.Subtract(FieldTaskSuccessRate, oldest.TaskSuccessRate);
            ExternalMetrics.Subtract(FieldToolSuccessRate, oldest.ToolSuccessRate);
            ExternalMetrics.Subtract(FieldRepairRate, oldest.RepairRate);
            ExternalMetrics.Subtract(FieldSafetyViolationRate, oldest.SafetyViolationRate);
            ExternalMetrics.Subtract(FieldContextPrecision, oldest.ContextPrecision);
            ExternalMetrics.Subtract(FieldContextRecallProxy, oldest.ContextRecallProxy);
            ExternalMetrics.Subtract(FieldUserAcceptance, oldest.UserAcceptance);
            ExternalMetrics.Subtract(FieldAnswerQuality, oldest.AnswerQuality);
            ExternalMetrics.Subtract(FieldTokenCost, oldest.TokenCost);
            ExternalMetrics.Subtract(FieldInferenceCost, oldest.InferenceCost);

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
            ExternalMetrics.Reset();
            var now = DateTimeOffset.UtcNow;
            WindowStart = now;
            WindowEnd = now;
        }
    }
}

/// <summary>
/// <see cref="CanaryObservationMetrics"/> 扩展方法，
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
    /// 新增 <c>quality_score</c> 字段，由 CanaryProgressionService.CheckRollbackThresholds
    /// 与 <see cref="CanaryGateOptions.MinQualityScore"/> 比较决定是否触发回滚。
    /// <para>
    /// 新增外部指标键值对（task_success_rate / tool_success_rate / repair_rate /
    /// safety_violation_rate / context_precision / context_recall_proxy / user_acceptance /
    /// answer_quality / token_cost / inference_cost）。
    /// 仅当外部指标非 null（已采集）时写入字典；null 字段不写入（CanaryProgressionService
    /// 通过 TryGetValue 检测，未采集时跳过回滚检查，优雅降级）。
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, double> ToExperimentMetrics(this CanaryObservationMetrics metrics)
    {
        var dict = new Dictionary<string, double>
        {
            ["error_rate"] = metrics.V2ErrorRate,
            ["p95_latency_ms"] = metrics.V2P95LatencyMs,
            ["divergence_rate"] = metrics.DivergenceRate,
            ["quality_score"] = metrics.AverageQualityScore
        };

        // 外部指标仅在非 null 时写入字典；CheckRollbackThresholds 用 TryGetValue 优雅降级
        if (metrics.TaskSuccessRate.HasValue) dict["task_success_rate"] = metrics.TaskSuccessRate.Value;
        if (metrics.ToolSuccessRate.HasValue) dict["tool_success_rate"] = metrics.ToolSuccessRate.Value;
        if (metrics.RepairRate.HasValue) dict["repair_rate"] = metrics.RepairRate.Value;
        if (metrics.SafetyViolationRate.HasValue) dict["safety_violation_rate"] = metrics.SafetyViolationRate.Value;
        if (metrics.ContextPrecision.HasValue) dict["context_precision"] = metrics.ContextPrecision.Value;
        if (metrics.ContextRecallProxy.HasValue) dict["context_recall_proxy"] = metrics.ContextRecallProxy.Value;
        if (metrics.UserAcceptance.HasValue) dict["user_acceptance"] = metrics.UserAcceptance.Value;
        if (metrics.AnswerQuality.HasValue) dict["answer_quality"] = metrics.AnswerQuality.Value;
        if (metrics.TokenCost.HasValue) dict["token_cost"] = metrics.TokenCost.Value;
        if (metrics.InferenceCost.HasValue) dict["inference_cost"] = metrics.InferenceCost.Value;

        return dict;
    }
}
