namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// R28-B.8：Production Canary Gate — 渐进推进配置
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
/// R28-B.8：Canary Gate 渐进推进配置。
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

        return new CanaryGateOptions
        {
            PercentageLadder = ladder,
            MinObservationPeriod = minObservation,
            MaxDivergenceRate = maxDivergence,
            MaxErrorRateDelta = maxErrorDelta,
            MaxLatencyMultiplier = maxLatencyMultiplier
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
