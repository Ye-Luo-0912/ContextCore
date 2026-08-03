using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Retrieval;

// ===========================================================================
// AdaptiveRetrievalPlanner —— 自适应检索规划器（反馈驱动的策略调整装饰器）
//
// 目标：
// 在确定性受控规划器（IAgentRetrievalQueryPlanner）之上叠加反馈驱动的自适应层：
// 按计划签名聚合近期检索结果（命中数 / 预算超限 / 有效性），计算自适应策略
// （Token 预算乘数 / 查询收敛乘数 / 召回增强乘数），后续规划时应用该策略。
//
// 自适应语义（样本数 ≥ IAdaptiveRetrievalPlanner.MinFeedbackSamples 才生效）：
// 1. 预算超限率 ≥ 0.5 → TokenBudgetMultiplier=0.75 + QueryConvergenceMultiplier=0.75
// （收敛预算与查询集，避免反复撞墙）。
// 2. 平均命中数 < 1.0 → RecallBoostMultiplier=1.25（增强查询权重扩大召回）。
// 3. 样本不足或指标未达阈值 → 中性默认（1.0 / 1.0 / 1.0）。
//
// 设计原则：
// - 底层规划器保持确定性 / 幂等：自适应仅调整规划参数；给定相同输入 +
// 相同反馈状态，仍产生相同计划（可审计、可回归）。
// - 纯内存计算：除反馈存储读写外不调用任何存储 / 检索执行器。
// - 空输入仍产出受控计划（透传底层计划，绝不抛异常）。
// ===========================================================================

/// <summary>
/// 自适应检索规划器默认实现：装饰 <see cref="IAgentRetrievalQueryPlanner"/>，
/// 按计划签名应用反馈驱动的策略调整。
/// </summary>
public sealed class AdaptiveRetrievalPlanner : IAdaptiveRetrievalPlanner
{
    /// <summary>策略聚合时读取的近期反馈条数上限。</summary>
    public const int FeedbackLookbackLimit = 20;

    /// <summary>预算超限率阈值（≥ 时触发收敛策略）。</summary>
    private const double BudgetExceededRateThreshold = 0.5;

    /// <summary>预算超限时的 Token 预算乘数。</summary>
    private const double BudgetConvergeTokenMultiplier = 0.75;

    /// <summary>预算超限时的查询收敛乘数。</summary>
    private const double BudgetConvergeQueryMultiplier = 0.75;

    /// <summary>平均命中数阈值（&lt; 时触发召回增强）。</summary>
    private const double LowHitThreshold = 1.0;

    /// <summary>召回增强乘数（低命中时提升查询权重）。</summary>
    private const double RecallBoostMultiplierValue = 1.25;

    /// <summary>查询权重上限（增强后钳制，防止权重失控）。</summary>
    private const double MaxQueryWeight = 2.0;

    private readonly IAgentRetrievalQueryPlanner _inner;
    private readonly IRetrievalPlanFeedbackStore _feedbackStore;

    public AdaptiveRetrievalPlanner(
        IAgentRetrievalQueryPlanner inner,
        IRetrievalPlanFeedbackStore feedbackStore)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _feedbackStore = feedbackStore ?? throw new ArgumentNullException(nameof(feedbackStore));
    }

    /// <inheritdoc />
    public async Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        var basePlan = _inner.Plan(input, ct);
        var recent = await _feedbackStore.ListRecentAsync(signature, FeedbackLookbackLimit, ct).ConfigureAwait(false);
        var policy = ComputePolicy(signature, recent);

        return ApplyPolicy(basePlan, policy);
    }

    /// <inheritdoc />
    public ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        return _feedbackStore.RecordAsync(feedback, ct);
    }

    /// <inheritdoc />
    public async ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var signature = AdaptiveRetrievalPlanSignature.Compute(input);
        var recent = await _feedbackStore.ListRecentAsync(signature, FeedbackLookbackLimit, ct).ConfigureAwait(false);
        return ComputePolicy(signature, recent);
    }

    /// <inheritdoc />
    public async ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(string planSignature, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planSignature);
        var recent = await _feedbackStore.ListRecentAsync(planSignature, FeedbackLookbackLimit, ct).ConfigureAwait(false);
        return ComputePolicy(planSignature, recent);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(string planSignature, int limit = 20, CancellationToken ct = default)
        => _feedbackStore.ListRecentAsync(planSignature, limit, ct);

    /// <inheritdoc />
    public ValueTask<int> ResetAsync(string? planSignature = null, CancellationToken ct = default)
        => _feedbackStore.ClearAsync(planSignature, ct);

    // ── 策略计算 ─────────────────────────────────────────────────────────────

    private static AdaptiveRetrievalPolicy ComputePolicy(string signature, IReadOnlyList<RetrievalPlanFeedback> recent)
    {
        if (recent.Count < IAdaptiveRetrievalPlanner.MinFeedbackSamples)
        {
            return new AdaptiveRetrievalPolicy
            {
                PlanSignature = signature,
                TokenBudgetMultiplier = 1.0,
                QueryConvergenceMultiplier = 1.0,
                RecallBoostMultiplier = 1.0,
                FeedbackSampleCount = recent.Count,
                ComputedAtUtc = DateTimeOffset.UtcNow,
                Note = $"反馈样本不足（{recent.Count}/{IAdaptiveRetrievalPlanner.MinFeedbackSamples}），策略为中性默认。"
            };
        }

        var exceededRate = (double)recent.Count(f => f.BudgetExceeded) / recent.Count;
        var avgHits = recent.Average(f => f.HitsReturned);

        var tokenMultiplier = exceededRate >= BudgetExceededRateThreshold ? BudgetConvergeTokenMultiplier : 1.0;
        var queryMultiplier = exceededRate >= BudgetExceededRateThreshold ? BudgetConvergeQueryMultiplier : 1.0;
        var recallBoost = avgHits < LowHitThreshold ? RecallBoostMultiplierValue : 1.0;

        var noteParts = new System.Collections.Generic.List<string>(2);
        if (tokenMultiplier < 1.0)
        {
            noteParts.Add($"预算超限率 {exceededRate:P0} ≥ 50%，Token 预算下调至 {tokenMultiplier:P0}、查询收敛至 {queryMultiplier:P0}");
        }
        if (recallBoost > 1.0)
        {
            noteParts.Add($"平均命中 {avgHits:F1} < 1，召回权重增强至 {recallBoost:P0}");
        }
        if (noteParts.Count == 0)
        {
            noteParts.Add("近期检索指标健康，策略保持中性");
        }

        return new AdaptiveRetrievalPolicy
        {
            PlanSignature = signature,
            TokenBudgetMultiplier = tokenMultiplier,
            QueryConvergenceMultiplier = queryMultiplier,
            RecallBoostMultiplier = recallBoost,
            FeedbackSampleCount = recent.Count,
            ComputedAtUtc = DateTimeOffset.UtcNow,
            Note = string.Join("；", noteParts) + "。"
        };
    }

    // ── 策略应用 ─────────────────────────────────────────────────────────────

    private static AgentRetrievalPlan ApplyPolicy(AgentRetrievalPlan basePlan, AdaptiveRetrievalPolicy policy)
    {
        var queries = basePlan.ControlledQueries;
        var note = string.Empty;

        // 1. Token 预算：乘数缩放 + 钳制到底层规划器的 [Min, Max] 区间
        var tokenBudget = (int)Math.Round(basePlan.TokenBudget * policy.TokenBudgetMultiplier, MidpointRounding.AwayFromZero);
        tokenBudget = Math.Clamp(tokenBudget, DefaultAgentRetrievalQueryPlanner.MinTokenBudget, DefaultAgentRetrievalQueryPlanner.MaxTokenBudget);
        if (tokenBudget != basePlan.TokenBudget)
        {
            note = $"[自适应] Token 预算 {basePlan.TokenBudget} → {tokenBudget}";
        }

        // 2. 查询收敛：按权重保留前 ceil(count × 乘数) 条，保持原始相对顺序
        if (policy.QueryConvergenceMultiplier < 1.0 && queries.Count > 1)
        {
            var keep = Math.Max(1, (int)Math.Ceiling(queries.Count * policy.QueryConvergenceMultiplier));
            queries = queries
                .Select((q, index) => (Query: q, Index: index))
                .OrderByDescending(t => t.Query.Weight)
                .ThenBy(t => t.Index)
                .Take(keep)
                .OrderBy(t => t.Index)
                .Select(t => t.Query)
                .ToArray();
            note = string.Concat(note, string.IsNullOrEmpty(note) ? string.Empty : "；", $"[自适应] 查询收敛 {keep}/{basePlan.ControlledQueries.Count}");
        }

        // 3. 召回增强：提升查询权重（钳制上限），扩大召回
        if (policy.RecallBoostMultiplier > 1.0 && queries.Count > 0)
        {
            queries = queries
                .Select(q => q with { Weight = Math.Min(MaxQueryWeight, q.Weight * policy.RecallBoostMultiplier) })
                .ToArray();
            note = string.Concat(note, string.IsNullOrEmpty(note) ? string.Empty : "；", "[自适应] 召回权重增强");
        }

        var reason = basePlan.Reason;
        if (!string.IsNullOrEmpty(note))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? note : $"{reason.TrimEnd()} {note}。";
        }

        return basePlan with
        {
            ControlledQueries = queries,
            TokenBudget = tokenBudget,
            Reason = reason
        };
    }
}
