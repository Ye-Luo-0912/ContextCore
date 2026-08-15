namespace ContextCore.Core.Services.Learning;

/// <summary>候选策略的灰度推进阶段。</summary>
public enum RolloutStage
{
    /// <summary>未启动。</summary>
    NotStarted = 0,

    /// <summary>Shadow：不影响结果，只记录差异与成本。</summary>
    Shadow = 1,

    /// <summary>小流量 Canary。</summary>
    Canary = 2
}

/// <summary>灰度门槛配置：样本门槛与必备的运维保障。</summary>
public sealed record RolloutGateConfig(
    int ShadowSampleThreshold,
    double CanaryTrafficFraction,
    bool VersionPinned,
    bool ParallelBaseline,
    bool KillSwitchEnabled,
    bool AutoRollbackEnabled);

/// <summary>灰度门槛决策：当前应处阶段与进入 Canary 的前置条件。</summary>
public sealed record RolloutGateDecision(
    RolloutStage Stage,
    bool ReadyForCanary,
    int ShadowSamples,
    IReadOnlyList<string> Blockers);

/// <summary>
/// Shadow 与 canary 门槛：候选策略先 shadow（不影响结果、只记录差异与成本），
/// 达到样本门槛且版本固定、并行基线、kill switch、自动回滚齐备后才允许小流量 canary。
/// 门槛只推进阶段，不自动开启 Active。
/// </summary>
public static class ShadowCanaryGate
{
    /// <summary>
    /// 根据当前 shadow 样本数与配置决定应处阶段。
    /// </summary>
    public static RolloutGateDecision Evaluate(RolloutGateConfig config, int shadowSamples)
    {
        ArgumentNullException.ThrowIfNull(config);
        var samples = Math.Max(0, shadowSamples);

        var blockers = new List<string>();
        if (samples < config.ShadowSampleThreshold)
        {
            blockers.Add($"shadow 样本 {samples} 未达到门槛 {config.ShadowSampleThreshold}。");
        }

        if (!config.VersionPinned)
        {
            blockers.Add("候选策略版本未固定。");
        }

        if (!config.ParallelBaseline)
        {
            blockers.Add("缺少并行基线（当前策略同时运行对比）。");
        }

        if (!config.KillSwitchEnabled)
        {
            blockers.Add("缺少 kill switch。");
        }

        if (!config.AutoRollbackEnabled)
        {
            blockers.Add("缺少自动回滚。");
        }

        if (config.CanaryTrafficFraction is <= 0.0 or > 0.5)
        {
            blockers.Add($"canary 流量比例 {config.CanaryTrafficFraction} 必须是小流量（0-0.5）。");
        }

        var readyForCanary = blockers.Count == 0;
        return new RolloutGateDecision(
            Stage: samples == 0 ? RolloutStage.NotStarted : readyForCanary ? RolloutStage.Canary : RolloutStage.Shadow,
            ReadyForCanary: readyForCanary,
            ShadowSamples: samples,
            Blockers: blockers);
    }
}
