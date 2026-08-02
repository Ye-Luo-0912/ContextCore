namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// Production Canary Gate — 渐进推进配置
//
// 目标（对齐 R28-B.8 规格）：
//   1. 替代单一 ScopedCanary 阶段为渐进百分比阶梯（1→5→10→25→50→100）。
//   2. 每个阶段最小观察时长 + parity/error/latency 三类自动回滚阈值。
//   3. 配置可通过环境变量覆盖（CC_CANARY_*）以便不同环境调整策略。
//
// 设计边界：
//   - 本类仅承载配置；推进/回滚决策逻辑由 CanaryProgressionService 实现。
//   - 阈值语义：
//     * MaxDivergenceRate：V2 vs Legacy 输出差异率（Jaccard 距离），超过则回滚。
//     * MaxErrorRateDelta：V2 错误率 - Legacy 错误率，超过则回滚。
//     * MaxLatencyMultiplier：V2 p95 / Legacy p95，超过则回滚。
// ===========================================================================

/// <summary>
/// Canary Gate 渐进推进配置。
/// </summary>
/// <remarks>
/// 默认值符合 R28-B.8 规格：
/// <list type="bullet">
/// <item>百分比阶梯：1 → 5 → 10 → 25 → 50 → 100。</item>
/// <item>每阶段最小观察时长：10 分钟。</item>
/// <item>parity 差异率阈值：5%（超过自动回滚）。</item>
/// <item>错误率差阈值：2%（V2 错误率 - Legacy 错误率 > 2% 自动回滚）。</item>
/// <item>p95 延迟倍数阈值：2.0x（V2 p95 / Legacy p95 > 2.0 自动回滚）。</item>
/// </list>
/// </remarks>
public sealed class CanaryGateOptions
{
    /// <summary>渐进百分比阶梯。默认 1→5→10→25→50→100。</summary>
    /// <remarks>
    /// 必须为递增的正整数序列，末位必须是 100（代表完全晋升到 V2 only）。
    /// 推进策略：每次 AdvanceAsync 推进到阶梯中的下一档；末档 100 后不再推进。
    /// </remarks>
    public IReadOnlyList<int> PercentageLadder { get; init; } = [1, 5, 10, 25, 50, 100];

    /// <summary>每个阶段最小观察时长（默认 10 分钟）。</summary>
    /// <remarks>
    /// 进入新百分比档后必须观察满此时长才允许推进到下一档。
    /// 未达此时长时 EvaluateAsync 返回 Hold（不可推进）。
    /// </remarks>
    public TimeSpan MinObservationPeriod { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>parity 差异率阈值（超过则自动回滚）。</summary>
    /// <remarks>
    /// 语义：V2 vs Legacy 输出差异率 = 1 - JaccardIndex。
    /// 默认 0.05（5%）：差异率 > 5% 时 CanaryProgressionService 触发回滚。
    /// </remarks>
    public double MaxDivergenceRate { get; init; } = 0.05;

    /// <summary>错误率阈值（V2 vs Legacy 错误率差，超过则自动回滚）。</summary>
    /// <remarks>
    /// 语义：experimentMetrics["error_rate"] - baselineMetrics["error_rate"]。
    /// 默认 0.02（2%）：错误率差 > 2% 时触发回滚。
    /// </remarks>
    public double MaxErrorRateDelta { get; init; } = 0.02;

    /// <summary>p95 延迟倍数阈值（V2 p95 / Legacy p95，超过则自动回滚）。</summary>
    /// <remarks>
    /// 语义：experimentMetrics["p95_latency_ms"] / baselineMetrics["p95_latency_ms"]。
    /// 默认 2.0：V2 p95 是 Legacy p95 的 2 倍以上时触发回滚。
    /// </remarks>
    public double MaxLatencyMultiplier { get; init; } = 2.0;

    /// <summary>
    /// 质量分下限阈值（V2 路径 quality_score &lt; 此值则自动回滚）。
    /// </summary>
    /// <remarks>
    /// 语义：experimentMetrics["quality_score"]（0.0-1.0）&lt; MinQualityScore 时触发回滚。
    /// <para>
    /// 质量分由 AuthoritativeRuntime 从 ContextDecisionExecutionResult 计算：
    /// quality_score = 0.5 × SectionCoverage + 0.5 × AvgRelevance。
    /// 综合反映 V2 产出的内容覆盖度（token 预算利用率）与候选相关性（FinalScore 均值）。
    /// 权重为固定默认值（0.5/0.5）；如需调整，可在 CanaryQualityScoreCalculator 中修改。
    /// </para>
    /// <para>
    /// 默认 0.3：质量分 &lt; 0.3 时视为 V2 产出质量严重退化（如无候选被选中、token 预算利用率极低）。
    /// 设为 0.0 时禁用质量分回滚阈值（仅 latency/error/divergence 三类阈值生效）。
    /// </para>
    /// </remarks>
    public double MinQualityScore { get; init; } = 0.3;

    /// <summary>
    /// 任务 C：任务成功率下限阈值（V2 路径 task_success_rate &lt; 此值则自动回滚）。
    /// </summary>
    /// <remarks>
    /// 语义：experimentMetrics["task_success_rate"]（0.0-1.0）&lt; MinTaskSuccessRate 时触发回滚。
    /// <para>
    /// 默认 0.7：任务成功率 &lt; 70% 时视为 V2 路径功能严重退化。设为 0.0 时禁用此检查。
    /// 仅当 task_success_rate 已采集（非 null）时检查；未采集时跳过（优雅降级）。
    /// </para>
    /// </remarks>
    public double MinTaskSuccessRate { get; init; } = 0.7;

    /// <summary>
    /// 任务 C：安全违规率上限阈值（V2 路径 safety_violation_rate &gt; 此值则自动回滚）。
    /// </summary>
    /// <remarks>
    /// 语义：experimentMetrics["safety_violation_rate"]（0.0-1.0）&gt; MaxSafetyViolationRate 时触发回滚。
    /// <para>
    /// 默认 0.0：任何安全违规（&gt; 0）都触发回滚（零容忍策略）。设为 1.0 时禁用此检查。
    /// 仅当 safety_violation_rate 已采集（非 null）时检查；未采集时跳过（优雅降级）。
    /// </para>
    /// </remarks>
    public double MaxSafetyViolationRate { get; init; } = 0.0;

    /// <summary>
    /// 任务 C：用户接受率下限阈值（V2 路径 user_acceptance &lt; 此值则自动回滚）。
    /// </summary>
    /// <remarks>
    /// 语义：experimentMetrics["user_acceptance"]（0.0-1.0）&lt; MinUserAcceptance 时触发回滚。
    /// <para>
    /// 默认 0.5：用户接受率 &lt; 50% 时视为 V2 路径用户体验退化。设为 0.0 时禁用此检查。
    /// 仅当 user_acceptance 已采集（非 null）时检查；未采集时跳过（优雅降级）。
    /// </para>
    /// </remarks>
    public double MinUserAcceptance { get; init; } = 0.5;

    /// <summary>
    /// 从环境变量构建配置。未设置的环境变量回退到默认值。
    /// </summary>
    /// <remarks>
    /// 支持的环境变量：
    /// <list type="bullet">
    /// <item><c>CC_CANARY_PERCENTAGE_LADDER</c>：逗号分隔的百分比阶梯（如 "1,5,10,25,50,100"）。</item>
    /// <item><c>CC_CANARY_MIN_OBSERVATION_MINUTES</c>：最小观察时长（分钟，整数）。</item>
    /// <item><c>CC_CANARY_MAX_DIVERGENCE_RATE</c>：parity 差异率阈值（double）。</item>
    /// <item><c>CC_CANARY_MAX_ERROR_RATE_DELTA</c>：错误率差阈值（double）。</item>
    /// <item><c>CC_CANARY_MAX_LATENCY_MULTIPLIER</c>：p95 延迟倍数阈值（double）。</item>
    /// <item><c>CC_CANARY_MIN_QUALITY_SCORE</c>：质量分下限阈值（double，0.0-1.0）。</item>
    /// <item><c>CC_CANARY_MIN_TASK_SUCCESS_RATE</c>：任务成功率下限阈值（double，0.0-1.0）。</item>
    /// <item><c>CC_CANARY_MAX_SAFETY_VIOLATION_RATE</c>：安全违规率上限阈值（double，0.0-1.0）。</item>
    /// <item><c>CC_CANARY_MIN_USER_ACCEPTANCE</c>：用户接受率下限阈值（double，0.0-1.0）。</item>
    /// </list>
    /// </remarks>
    public static CanaryGateOptions FromEnvironment()
    {
        var ladder = ParseLadder(Environment.GetEnvironmentVariable("CC_CANARY_PERCENTAGE_LADDER"));
        var minObservation = ParseTimeSpan(
            Environment.GetEnvironmentVariable("CC_CANARY_MIN_OBSERVATION_MINUTES"),
            TimeSpan.FromMinutes(10));
        var maxDivergence = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MAX_DIVERGENCE_RATE"),
            0.05);
        var maxErrorDelta = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MAX_ERROR_RATE_DELTA"),
            0.02);
        var maxLatencyMultiplier = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MAX_LATENCY_MULTIPLIER"),
            2.0);
        var minQualityScore = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MIN_QUALITY_SCORE"),
            0.3);
        var minTaskSuccessRate = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MIN_TASK_SUCCESS_RATE"),
            0.7);
        var maxSafetyViolationRate = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MAX_SAFETY_VIOLATION_RATE"),
            0.0);
        var minUserAcceptance = ParseDouble(
            Environment.GetEnvironmentVariable("CC_CANARY_MIN_USER_ACCEPTANCE"),
            0.5);

        return new CanaryGateOptions
        {
            PercentageLadder = ladder,
            MinObservationPeriod = minObservation,
            MaxDivergenceRate = maxDivergence,
            MaxErrorRateDelta = maxErrorDelta,
            MaxLatencyMultiplier = maxLatencyMultiplier,
            MinQualityScore = minQualityScore,
            MinTaskSuccessRate = minTaskSuccessRate,
            MaxSafetyViolationRate = maxSafetyViolationRate,
            MinUserAcceptance = minUserAcceptance
        };
    }

    private static IReadOnlyList<int> ParseLadder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [1, 5, 10, 25, 50, 100];
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ladder = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var pct))
            {
                return [1, 5, 10, 25, 50, 100];
            }
            ladder.Add(Math.Clamp(pct, 0, 100));
        }

        if (ladder.Count == 0 || ladder[^1] != 100)
        {
            // 末位必须是 100（完全晋升）；否则回退默认值
            return [1, 5, 10, 25, 50, 100];
        }

        return ladder;
    }

    private static TimeSpan ParseTimeSpan(string? value, TimeSpan defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, out var minutes) && minutes >= 0)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        return defaultValue;
    }

    private static double ParseDouble(string? value, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (double.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }
}
