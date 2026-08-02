using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// 默认 <see cref="IPromotionJudge"/> 实现：基于规则引擎的端到端学习闭环裁决器。
/// </summary>
/// <remarks>
/// <b>设计原则</b>（与 project memory 硬边界一致）：
/// <list type="bullet">
/// <item>最小作用域：仅决定 proposal 是否推进 / 回滚 / 晋升 / 拒绝；不接触生产 Policy / 配置 / 模型启用。</item>
/// <item>不使用单一质量总分决定上线：必须逐条 ExpectedGain 与 RollbackCondition 评估。</item>
/// <item>Token budget / section quota / duplicate suppression 导致的 dropped 不能被当作不相关负样本：
/// Judge 只用 metric 值的方向比较，不接触候选级 selected/dropped 标签。</item>
/// </list>
///
/// <b>裁决逻辑</b>：
/// <list type="number">
/// <item>RollbackCondition 触发检查：对每个 condition，若 experimentMetrics 包含对应 metric 且 IsTriggered → Decision=Rollback。</item>
/// <item>ExpectedGain 方向检查：对每个 gain，比较 experimentMetrics 与 baselineMetrics 的 delta 方向与 EstimatedDelta 方向是否一致；任一相反 → Reject。</item>
/// <item>Stage 推进规则：
///   <list type="bullet">
///   <item>OfflineExperiment → Shadow：所有 ExpectedGain 方向匹配 + 无 RollbackCondition 触发 → Advance。</item>
///   <item>Shadow → ScopedCanary：无 RollbackCondition 触发 + ExpectedGain 方向不相反 → Advance。</item>
///   <item>ScopedCanary → Promotion：所有 ExpectedGain 达置信阈值（默认）+ 无 RollbackCondition 触发 → Promote。</item>
///   <item>AutomaticRollback：返回 Rollback（终态）。</item>
///   <item>Promotion：返回 Promote（终态）。</item>
///   </list>
/// </item>
/// <item>Hold：metric 数据不足（ExpectedGain 的 metric 在 experiment/baseline 都缺失）。</item>
/// </list>
/// </remarks>
public sealed class DefaultPromotionJudge : IPromotionJudge
{
    /// <summary>默认期望置信度阈值：ScopedCanary 阶段晋升为 Promotion 要求 ExpectedGain.Confidence >= 此值。</summary>
    public const double DefaultPromotionConfidenceThreshold = 0.70;

    private readonly double _promotionConfidenceThreshold;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造默认 judge。</summary>
    /// <param name="promotionConfidenceThreshold">晋升置信度阈值（默认 0.70）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultPromotionJudge(
        double promotionConfidenceThreshold = DefaultPromotionConfidenceThreshold,
        TimeProvider? timeProvider = null)
    {
        if (promotionConfidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(promotionConfidenceThreshold), "置信度阈值必须在 [0, 1] 区间");
        }
        _promotionConfidenceThreshold = promotionConfidenceThreshold;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<PromotionJudgeResult> JudgeAsync(
        PromotionJudgeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. AutomaticRollback 阶段：终态，直接返回 Rollback
        if (request.CurrentStage == OptimizationStage.AutomaticRollback)
        {
            return Task.FromResult(new PromotionJudgeResult(
                decision: PromotionDecision.Rollback,
                rationale: "Stage=AutomaticRollback is terminal; pipeline已命中回滚条件，自动切回基线路径。",
                nextStage: null,
                conditions: Array.Empty<string>()));
        }

        // 2. Promotion 阶段：终态，直接返回 Promote
        if (request.CurrentStage == OptimizationStage.Promotion)
        {
            return Task.FromResult(new PromotionJudgeResult(
                decision: PromotionDecision.Promote,
                rationale: "Stage=Promotion is terminal; proposal 已成功晋升为基线路径。",
                nextStage: null,
                conditions: Array.Empty<string>()));
        }

        // 3. RollbackCondition 触发检查：任一 condition 命中 experimentMetrics → Rollback
        var triggeredConditions = new List<RollbackCondition>();
        foreach (var condition in request.Proposal.RollbackConditions)
        {
            if (request.ExperimentMetrics.TryGetValue(condition.MetricName, out var expValue) &&
                condition.IsTriggered(expValue))
            {
                triggeredConditions.Add(condition);
            }
        }
        if (triggeredConditions.Count > 0)
        {
            var reasons = string.Join("; ", triggeredConditions.Select(c => $"{c.MetricName} {c.Operator} {c.Threshold} ({c.Description})"));
            return Task.FromResult(new PromotionJudgeResult(
                decision: PromotionDecision.Rollback,
                rationale: $"RollbackCondition triggered: {reasons}",
                nextStage: null,
                conditions: triggeredConditions.Select(c => c.Description).ToList()));
        }

        // 4. ExpectedGain 方向检查
        var gainsWithExperimentData = new List<(ExpectedGain gain, double baseline, double experiment)>();
        var missingMetricGains = new List<ExpectedGain>();
        var contradictingGains = new List<ExpectedGain>();

        foreach (var gain in request.Proposal.ExpectedGains)
        {
            var hasBaseline = request.BaselineMetrics.TryGetValue(gain.MetricName, out var baseline);
            var hasExperiment = request.ExperimentMetrics.TryGetValue(gain.MetricName, out var experiment);
            if (!hasBaseline || !hasExperiment)
            {
                missingMetricGains.Add(gain);
                continue;
            }
            var actualDelta = experiment - baseline;
            var expectedDirection = Math.Sign(gain.EstimatedDelta);
            var actualDirection = Math.Sign(actualDelta);
            if (expectedDirection == 0 || actualDirection == 0)
            {
                // delta 为 0：视为无信号
                missingMetricGains.Add(gain);
                continue;
            }
            if (actualDirection != expectedDirection)
            {
                contradictingGains.Add(gain);
                continue;
            }
            gainsWithExperimentData.Add((gain, baseline, experiment));
        }

        // 5. 任一 ExpectedGain 方向相反 → Reject（适用于 OfflineExperiment 与 Shadow 阶段）
        if (contradictingGains.Count > 0 &&
            request.CurrentStage is OptimizationStage.OfflineExperiment or OptimizationStage.Shadow)
        {
            var reasons = string.Join("; ", contradictingGains.Select(g => $"{g.MetricName} expected {g.EstimatedDelta}, contradicted"));
            return Task.FromResult(new PromotionJudgeResult(
                decision: PromotionDecision.Reject,
                rationale: $"ExpectedGain contradicted: {reasons}",
                nextStage: null,
                conditions: Array.Empty<string>()));
        }

        // 6. Stage 推进规则
        return Task.FromResult(request.CurrentStage switch
        {
            OptimizationStage.OfflineExperiment => JudgeOfflineExperiment(
                gainsWithExperimentData, missingMetricGains, contradictingGains),
            OptimizationStage.Shadow => JudgeShadow(
                gainsWithExperimentData, missingMetricGains, contradictingGains),
            OptimizationStage.ScopedCanary => JudgeScopedCanary(
                gainsWithExperimentData, missingMetricGains, contradictingGains),
            _ => new PromotionJudgeResult(
                decision: PromotionDecision.Hold,
                rationale: $"Stage {request.CurrentStage} 无可处理规则；保持 Hold。",
                nextStage: null,
                conditions: Array.Empty<string>())
        });
    }

    private PromotionJudgeResult JudgeOfflineExperiment(
        List<(ExpectedGain gain, double baseline, double experiment)> matched,
        List<ExpectedGain> missing,
        List<ExpectedGain> contradicted)
    {
        // 数据不足 → Hold
        if (matched.Count == 0)
        {
            return new PromotionJudgeResult(
                decision: PromotionDecision.Hold,
                rationale: "OfflineExperiment: 无匹配的 ExpectedGain 数据；继续观察。",
                nextStage: null,
                conditions: new[] { "采集至少 1 条 ExpectedGain metric 的 baseline + experiment 数据" });
        }
        // 已有匹配数据且方向一致 → 推进到 Shadow
        return new PromotionJudgeResult(
            decision: PromotionDecision.Advance,
            rationale: $"OfflineExperiment: {matched.Count} 条 ExpectedGain 方向匹配，可推进到 Shadow 阶段。",
            nextStage: OptimizationStage.Shadow,
            conditions: new[] { "Shadow 阶段需持续观察 RollbackCondition" });
    }

    private PromotionJudgeResult JudgeShadow(
        List<(ExpectedGain gain, double baseline, double experiment)> matched,
        List<ExpectedGain> missing,
        List<ExpectedGain> contradicted)
    {
        // Shadow 阶段：无 RollbackCondition 触发 + ExpectedGain 方向不相反即可推进（已在上面检查）
        return new PromotionJudgeResult(
            decision: PromotionDecision.Advance,
            rationale: $"Shadow: 无 RollbackCondition 触发，{matched.Count} 条 ExpectedGain 方向匹配；可推进到 ScopedCanary。",
            nextStage: OptimizationStage.ScopedCanary,
            conditions: new[] { "ScopedCanary 阶段需满足置信度阈值 >= " + _promotionConfidenceThreshold.ToString("0.00") });
    }

    private PromotionJudgeResult JudgeScopedCanary(
        List<(ExpectedGain gain, double baseline, double experiment)> matched,
        List<ExpectedGain> missing,
        List<ExpectedGain> contradicted)
    {
        // ScopedCanary → Promotion：要求所有 ExpectedGain 达置信度阈值
        var belowThreshold = matched.Where(t => t.gain.Confidence < _promotionConfidenceThreshold).ToList();
        if (matched.Count == 0)
        {
            return new PromotionJudgeResult(
                decision: PromotionDecision.Hold,
                rationale: "ScopedCanary: 无匹配数据；继续观察 canary 指标。",
                nextStage: null,
                conditions: new[] { "采集至少 1 条 ExpectedGain 的 canary 数据" });
        }
        if (belowThreshold.Count > 0)
        {
            var names = string.Join(", ", belowThreshold.Select(t => t.gain.MetricName));
            return new PromotionJudgeResult(
                decision: PromotionDecision.Hold,
                rationale: $"ScopedCanary: {belowThreshold.Count} 条 ExpectedGain 置信度低于阈值 {_promotionConfidenceThreshold:0.00}: {names}；继续观察。",
                nextStage: null,
                conditions: new[] { $"ExpectedGain 置信度需达 {_promotionConfidenceThreshold:0.00}" });
        }
        return new PromotionJudgeResult(
            decision: PromotionDecision.Promote,
            rationale: $"ScopedCanary: {matched.Count} 条 ExpectedGain 全部达到置信度阈值且方向匹配，可晋升为基线。",
            nextStage: null,
            conditions: Array.Empty<string>());
    }
}
