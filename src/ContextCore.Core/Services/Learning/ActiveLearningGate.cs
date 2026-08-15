namespace ContextCore.Core.Services.Learning;

/// <summary>Active 学习所需的三项统计可信证据。</summary>
public sealed record ActiveLearningEvidence(
    bool RecallImproved,
    bool TaskSuccessImproved,
    bool SafetyGatesPreserved);

/// <summary>Active 门槛决策：三项证据齐备才允许 Active，否则保持关闭并给出退回目标。</summary>
public sealed record ActiveLearningGateDecision(
    bool Active,
    IReadOnlyList<string> Missing,
    string RollbackTo);

/// <summary>
/// Active 学习门槛：只有 Required-Evidence Recall@TokenBudget、任务结果与安全门
/// 均有统计可信提升才允许 Active；默认保持关闭。任何一项缺失或上线后漂移/退化时，
/// 退回上一稳定 deterministic/learned 策略。门槛本身不翻转 Active，只做判定。
/// </summary>
public static class ActiveLearningGate
{
    /// <summary>
    /// 判定是否允许 Active。稳定策略引用为失败时的退回目标。
    /// </summary>
    public static ActiveLearningGateDecision Evaluate(
        ActiveLearningEvidence evidence,
        string stablePolicyRef)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(stablePolicyRef);

        var missing = new List<string>();
        if (!evidence.RecallImproved)
        {
            missing.Add("Required-Evidence Recall@TokenBudget 无统计可信提升。");
        }

        if (!evidence.TaskSuccessImproved)
        {
            missing.Add("任务结果无统计可信提升。");
        }

        if (!evidence.SafetyGatesPreserved)
        {
            missing.Add("安全门出现回退。");
        }

        return new ActiveLearningGateDecision(
            Active: missing.Count == 0,
            Missing: missing,
            RollbackTo: stablePolicyRef);
    }
}
