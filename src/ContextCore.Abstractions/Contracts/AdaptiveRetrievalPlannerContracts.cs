// ===========================================================================
// Adaptive Retrieval Planner —— 自适应检索规划器契约
// ===========================================================================
// 角色：在确定性受控规划器（IAgentRetrievalQueryPlanner）之上增加反馈驱动的
// 自适应层：记录每轮检索的实际结果（命中数 / 预算是否超限 / 是否有效），
// 按计划签名聚合为自适应策略（Token 预算乘数 / 查询收敛乘数 / 召回增强乘数），
// 后续规划时应用该策略调整输出，让检索策略随真实执行效果收敛，而非静态启发式。
//
// 设计决策：
//   1. 底层规划器保持确定性 / 幂等：自适应仅调整规划参数（预算 / 查询收敛 /
//      权重），给定相同输入 + 相同反馈状态，仍产生相同计划（可审计、可回归）。
//   2. 反馈按"计划签名"聚合：签名由输入（任务 + 意图 + 未解决目标）确定性派生，
//      相同形态的检索任务共享同一自适应状态，跨请求积累统计。
//   3. 持久化：反馈记录跨进程重启保留（Postgres 实现为 retrieval_plan_feedback 表），
//      进程内 InMemory 实现供单节点 / 测试使用。
//   4. 正交于决策引擎：本规划器回答"检索什么 / 多少预算"，不替代候选排序。
// ===========================================================================

namespace ContextCore.Abstractions;

/// <summary>
/// 单轮检索结果反馈（自适应规划器的学习信号）。
/// 由检索执行方在每轮检索后记录（调用 <see cref="IAdaptiveRetrievalPlanner.RecordOutcomeAsync"/>）。
/// </summary>
public sealed record RetrievalPlanFeedback
{
    /// <summary>计划签名（由输入确定性派生，见 <see cref="IAdaptiveRetrievalPlanner"/>）。</summary>
    public required string PlanSignature { get; init; }

    /// <summary>本轮主导查询文本（诊断用）。</summary>
    public string QueryText { get; init; } = string.Empty;

    /// <summary>本轮返回的命中数。</summary>
    public int HitsReturned { get; init; }

    /// <summary>是否超出 Token 预算（超限应触发收敛策略）。</summary>
    public bool BudgetExceeded { get; init; }

    /// <summary>本轮检索结果是否被实际采用（有效信号；false = 结果未被使用）。</summary>
    public bool Effective { get; init; } = true;

    /// <summary>记录时间（UTC）。</summary>
    public required DateTimeOffset RecordedAtUtc { get; init; }
}

/// <summary>
/// 自适应检索策略（由近期反馈聚合计算）。
/// </summary>
public sealed record AdaptiveRetrievalPolicy
{
    /// <summary>计划签名（本策略对应的签名）。</summary>
    public required string PlanSignature { get; init; }

    /// <summary>
    /// Token 预算乘数（0.5–1.0）：近期预算频繁超限时 <b>下调</b>（默认 1.0）。
    /// </summary>
    public required double TokenBudgetMultiplier { get; init; }

    /// <summary>
    /// 查询收敛乘数（0.5–1.0）：近期预算频繁超限时 <b>收敛</b>查询集
    /// （按权重保留前 ceil(count × 乘数) 条；默认 1.0 不收敛）。
    /// </summary>
    public required double QueryConvergenceMultiplier { get; init; }

    /// <summary>
    /// 召回增强乘数（1.0–1.5）：近期命中持续偏低时 <b>增强</b>查询权重
    /// 以扩大召回（默认 1.0 不增强）。
    /// </summary>
    public required double RecallBoostMultiplier { get; init; }

    /// <summary>参与聚合的反馈样本数（&lt; 阈值时策略为中性默认值）。</summary>
    public required int FeedbackSampleCount { get; init; }

    /// <summary>策略计算时间（UTC）。</summary>
    public required DateTimeOffset ComputedAtUtc { get; init; }

    /// <summary>策略说明（中文，解释为何采用当前乘数；用于审计与调试）。</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 检索计划反馈的持久化存储。
/// </summary>
public interface IRetrievalPlanFeedbackStore
{
    /// <summary>记录一条检索结果反馈。</summary>
    ValueTask RecordAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default);

    /// <summary>列出指定签名最近 N 条反馈（按记录时间倒序，最新在前）。</summary>
    ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListRecentAsync(string planSignature, int limit = 20, CancellationToken ct = default);

    /// <summary>清除反馈（planSignature 为 null 时清除全部；否则仅清除该签名）。返回清除条数。</summary>
    ValueTask<int> ClearAsync(string? planSignature = null, CancellationToken ct = default);
}

/// <summary>
/// 自适应检索规划器：在确定性受控规划器之上叠加反馈驱动的策略调整。
/// </summary>
/// <remarks>
/// 自适应语义（按签名聚合近期反馈，样本数 ≥ <see cref="MinFeedbackSamples"/> 才生效）：
/// <list type="bullet">
///   <item>预算超限率 ≥ 0.5 → TokenBudgetMultiplier=0.75 + QueryConvergenceMultiplier=0.75
///     （收敛预算与查询集，避免反复撞墙）。</item>
///   <item>平均命中数 &lt; 1.0 → RecallBoostMultiplier=1.25（增强查询权重扩大召回）。</item>
///   <item>样本不足或指标未达阈值 → 中性默认（1.0 / 1.0 / 1.0）。</item>
/// </list>
/// 调整仅作用于规划参数，底层规划器本身保持确定性；给定相同输入 + 相同反馈状态，
/// <see cref="Plan"/> 仍产生确定输出。
/// </remarks>
public interface IAdaptiveRetrievalPlanner
{
    /// <summary>自适应规划器触发自适应所需的最小反馈样本数（低于则策略中性）。</summary>
    const int MinFeedbackSamples = 3;

    /// <summary>
    /// 规划本轮受控检索：先按输入派生计划签名，从反馈存储读取近期反馈计算策略，
    /// 再由底层规划器产出基础计划并应用策略调整。
    /// </summary>
    Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default);

    /// <summary>记录一轮检索结果反馈（携带计划签名，见 <see cref="RetrievalPlanFeedback"/>）。</summary>
    ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default);

    /// <summary>按输入派生的签名计算当前自适应策略（供诊断 / 端点查询）。</summary>
    ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default);

    /// <summary>按显式计划签名计算当前自适应策略（供诊断 / 端点查询）。</summary>
    ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(string planSignature, CancellationToken ct = default);

    /// <summary>列出指定计划签名最近 N 条反馈（按记录时间倒序；供诊断 / 端点查询）。</summary>
    ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(string planSignature, int limit = 20, CancellationToken ct = default);

    /// <summary>清除反馈并重置自适应状态（planSignature 为 null 时清除全部）。返回清除条数。</summary>
    ValueTask<int> ResetAsync(string? planSignature = null, CancellationToken ct = default);
}

/// <summary>
/// 计划签名派生助手：从规划输入确定性派生计划签名（FNV-1a 64 位哈希的十六进制）。
/// 规划器 / 运维端点 / 测试共用同一算法，保证反馈记录与策略查询落到同一签名。
/// </summary>
public static class AdaptiveRetrievalPlanSignature
{
    /// <summary>
    /// 从输入派生计划签名：对（原始任务 + 最新意图 + 未解决目标）拼接文本做
    /// FNV-1a 64 位哈希，输出 <c>sig:{16 位十六进制}</c>。相同输入永远产生相同签名。
    /// </summary>
    public static string Compute(AgentRetrievalPlannerInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var task = (input.OriginalTask ?? string.Empty).Trim();
        var intent = (input.LatestAssistantIntent ?? string.Empty).Trim();
        var goals = string.Join("\n", (input.UnresolvedGoals ?? Array.Empty<string>()).Where(g => !string.IsNullOrWhiteSpace(g)));
        var seed = $"{task}|{intent}|{goals}";

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(seed))
        {
            hash ^= b;
            hash *= prime;
        }

        return "sig:" + ((long)hash).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
