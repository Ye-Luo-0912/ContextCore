using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.BoundedContext;

/// <summary>
/// R22-2：默认 <see cref="IContextRepairDetector"/> 实现。
/// 基于 <see cref="PackageQualityReport"/> 8 个指标检测 7 类确定性异常。
/// </summary>
/// <remarks>
/// <b>设计原则</b>（对齐用户规格与 R22-1 契约）：
/// <list type="bullet">
/// <item>纯函数式评估：输入 <see cref="ContextDecisionResult"/> + <see cref="PackageQualityReport"/>，
/// 输出 <see cref="ContextRepairDiagnosis"/> 列表；不调用任何 store，不修改状态。</item>
/// <item>阈值参数化：7 个阈值通过构造函数配置；不暴露在接口中（避免接口污染）。</item>
/// <item>多异常同时触发时返回多个 Diagnosis（orchestrator 决定修复顺序）。</item>
/// <item>qualityReport 为 null 时返回空列表（无质量报告则无法检测）。</item>
/// <item>WorkspaceId/CollectionId 从 <see cref="ContextCandidateEnvelope"/> 推导（首选 SelectedEnvelopes[0]，
/// 回退 DroppedEnvelopes[0]，再回退 string.Empty）。</item>
/// </list>
///
/// <b>阈值方向</b>（所有指标 1.0 = 最优）：
/// <list type="bullet">
/// <item>AnchorCoverage &lt; 阈值 → <see cref="ContextRepairReason.PrimaryAnchorUncovered"/></item>
/// <item>HardConstraintSatisfaction &lt; 阈值 → <see cref="ContextRepairReason.HardConstraintMissing"/></item>
/// <item>RequiredItemCoverage &lt; 阈值 → <see cref="ContextRepairReason.MustHitMissing"/></item>
/// <item>Redundancy &lt; 阈值 → <see cref="ContextRepairReason.SevereRedundancy"/></item>
/// <item>SectionBalance &lt; 阈值 → <see cref="ContextRepairReason.SectionSqueezeAnomaly"/></item>
/// <item>TokenEfficiency &lt; 阈值 → <see cref="ContextRepairReason.TokenUtilizationTooLow"/></item>
/// <item>LifecycleRisk &lt; 阈值 → <see cref="ContextRepairReason.LifecycleConflictUnresolved"/></item>
/// </list>
/// </remarks>
public sealed class DefaultContextRepairDetector : IContextRepairDetector
{
    /// <summary>Anchor 覆盖率阈值（低于此值触发 PrimaryAnchorUncovered）。默认 0.80。</summary>
    public const double DefaultAnchorCoverageThreshold = 0.80;

    /// <summary>Hard constraint 满足度阈值（低于此值触发 HardConstraintMissing）。默认 1.0（必须完全满足）。</summary>
    public const double DefaultHardConstraintSatisfactionThreshold = 1.0;

    /// <summary>Must-hit 覆盖率阈值（低于此值触发 MustHitMissing）。默认 1.0（必须完全命中）。</summary>
    public const double DefaultRequiredItemCoverageThreshold = 1.0;

    /// <summary>无冗余度阈值（低于此值触发 SevereRedundancy）。默认 0.70。</summary>
    public const double DefaultRedundancyThreshold = 0.70;

    /// <summary>Section 均衡度阈值（低于此值触发 SectionSqueezeAnomaly）。默认 0.50。</summary>
    public const double DefaultSectionBalanceThreshold = 0.50;

    /// <summary>Token 利用率阈值（低于此值触发 TokenUtilizationTooLow）。默认 0.30。</summary>
    public const double DefaultTokenEfficiencyThreshold = 0.30;

    /// <summary>Lifecycle 风险阈值（低于此值触发 LifecycleConflictUnresolved）。默认 0.80。</summary>
    public const double DefaultLifecycleRiskThreshold = 0.80;

    private readonly double _anchorCoverageThreshold;
    private readonly double _hardConstraintSatisfactionThreshold;
    private readonly double _requiredItemCoverageThreshold;
    private readonly double _redundancyThreshold;
    private readonly double _sectionBalanceThreshold;
    private readonly double _tokenEfficiencyThreshold;
    private readonly double _lifecycleRiskThreshold;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造默认修复检测器。</summary>
    /// <param name="anchorCoverageThreshold">Anchor 覆盖率阈值（默认 0.80）。</param>
    /// <param name="hardConstraintSatisfactionThreshold">Hard constraint 满足度阈值（默认 1.0）。</param>
    /// <param name="requiredItemCoverageThreshold">Must-hit 覆盖率阈值（默认 1.0）。</param>
    /// <param name="redundancyThreshold">无冗余度阈值（默认 0.70）。</param>
    /// <param name="sectionBalanceThreshold">Section 均衡度阈值（默认 0.50）。</param>
    /// <param name="tokenEfficiencyThreshold">Token 利用率阈值（默认 0.30）。</param>
    /// <param name="lifecycleRiskThreshold">Lifecycle 风险阈值（默认 0.80）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultContextRepairDetector(
        double anchorCoverageThreshold = DefaultAnchorCoverageThreshold,
        double hardConstraintSatisfactionThreshold = DefaultHardConstraintSatisfactionThreshold,
        double requiredItemCoverageThreshold = DefaultRequiredItemCoverageThreshold,
        double redundancyThreshold = DefaultRedundancyThreshold,
        double sectionBalanceThreshold = DefaultSectionBalanceThreshold,
        double tokenEfficiencyThreshold = DefaultTokenEfficiencyThreshold,
        double lifecycleRiskThreshold = DefaultLifecycleRiskThreshold,
        TimeProvider? timeProvider = null)
    {
        if (anchorCoverageThreshold < 0.0 || anchorCoverageThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorCoverageThreshold), "Threshold must be in [0, 1].");
        }
        if (hardConstraintSatisfactionThreshold < 0.0 || hardConstraintSatisfactionThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(hardConstraintSatisfactionThreshold), "Threshold must be in [0, 1].");
        }
        if (requiredItemCoverageThreshold < 0.0 || requiredItemCoverageThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredItemCoverageThreshold), "Threshold must be in [0, 1].");
        }
        if (redundancyThreshold < 0.0 || redundancyThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(redundancyThreshold), "Threshold must be in [0, 1].");
        }
        if (sectionBalanceThreshold < 0.0 || sectionBalanceThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectionBalanceThreshold), "Threshold must be in [0, 1].");
        }
        if (tokenEfficiencyThreshold < 0.0 || tokenEfficiencyThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenEfficiencyThreshold), "Threshold must be in [0, 1].");
        }
        if (lifecycleRiskThreshold < 0.0 || lifecycleRiskThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lifecycleRiskThreshold), "Threshold must be in [0, 1].");
        }

        _anchorCoverageThreshold = anchorCoverageThreshold;
        _hardConstraintSatisfactionThreshold = hardConstraintSatisfactionThreshold;
        _requiredItemCoverageThreshold = requiredItemCoverageThreshold;
        _redundancyThreshold = redundancyThreshold;
        _sectionBalanceThreshold = sectionBalanceThreshold;
        _tokenEfficiencyThreshold = tokenEfficiencyThreshold;
        _lifecycleRiskThreshold = lifecycleRiskThreshold;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContextRepairDiagnosis>> DetectAsync(
        ContextDecisionResult decision,
        PackageQualityReport? qualityReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // 无质量报告则无法检测（验收标准：返回空列表，不抛异常）
        if (qualityReport is null)
        {
            return Task.FromResult<IReadOnlyList<ContextRepairDiagnosis>>(Array.Empty<ContextRepairDiagnosis>());
        }

        cancellationToken.ThrowIfCancellationRequested();

        var (workspaceId, collectionId) = ExtractScope(decision);
        var decisionRequestId = decision.RequestId;
        var diagnosedAt = _timeProvider.GetUtcNow();
        var diagnoses = new List<ContextRepairDiagnosis>(capacity: 7);

        // 1. AnchorCoverage → PrimaryAnchorUncovered
        if (qualityReport.AnchorCoverage.Score < _anchorCoverageThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.PrimaryAnchorUncovered,
                qualityReport.AnchorCoverage,
                _anchorCoverageThreshold,
                "re-retrieve-anchor-coverage",
                diagnosedAt,
                qualityReport));
        }

        // 2. HardConstraintSatisfaction → HardConstraintMissing
        if (qualityReport.HardConstraintSatisfaction.Score < _hardConstraintSatisfactionThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.HardConstraintMissing,
                qualityReport.HardConstraintSatisfaction,
                _hardConstraintSatisfactionThreshold,
                "inject-missing-hard-constraint",
                diagnosedAt,
                qualityReport));
        }

        // 3. RequiredItemCoverage → MustHitMissing
        if (qualityReport.RequiredItemCoverage.Score < _requiredItemCoverageThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.MustHitMissing,
                qualityReport.RequiredItemCoverage,
                _requiredItemCoverageThreshold,
                "re-retrieve-must-hit",
                diagnosedAt,
                qualityReport));
        }

        // 4. Redundancy → SevereRedundancy
        if (qualityReport.Redundancy.Score < _redundancyThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.SevereRedundancy,
                qualityReport.Redundancy,
                _redundancyThreshold,
                "drop-redundant",
                diagnosedAt,
                qualityReport));
        }

        // 5. SectionBalance → SectionSqueezeAnomaly
        if (qualityReport.SectionBalance.Score < _sectionBalanceThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.SectionSqueezeAnomaly,
                qualityReport.SectionBalance,
                _sectionBalanceThreshold,
                "rebalance-sections",
                diagnosedAt,
                qualityReport));
        }

        // 6. TokenEfficiency → TokenUtilizationTooLow
        if (qualityReport.TokenEfficiency.Score < _tokenEfficiencyThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.TokenUtilizationTooLow,
                qualityReport.TokenEfficiency,
                _tokenEfficiencyThreshold,
                "expand-candidate-pool",
                diagnosedAt,
                qualityReport));
        }

        // 7. LifecycleRisk → LifecycleConflictUnresolved
        if (qualityReport.LifecycleRisk.Score < _lifecycleRiskThreshold)
        {
            diagnoses.Add(BuildDiagnosis(
                decisionRequestId, workspaceId, collectionId,
                ContextRepairReason.LifecycleConflictUnresolved,
                qualityReport.LifecycleRisk,
                _lifecycleRiskThreshold,
                "resolve-lifecycle-conflict",
                diagnosedAt,
                qualityReport));
        }

        return Task.FromResult<IReadOnlyList<ContextRepairDiagnosis>>(diagnoses);
    }

    private static ContextRepairDiagnosis BuildDiagnosis(
        string decisionRequestId,
        string workspaceId,
        string collectionId,
        ContextRepairReason reason,
        PackageQualityMetric metric,
        double threshold,
        string suggestedStrategy,
        DateTimeOffset diagnosedAt,
        PackageQualityReport qualityReport)
    {
        return new ContextRepairDiagnosis
        {
            DiagnosisId = $"diag-{Guid.NewGuid():N}",
            DecisionRequestId = decisionRequestId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Reason = reason,
            ReasonDetail = $"{metric.Name}={metric.Score:F4} < threshold {threshold:F4} ({metric.Detail})",
            TriggerMetricValue = metric.Score,
            TriggerMetricThreshold = threshold,
            QualityReport = qualityReport,
            SuggestedRepairStrategy = suggestedStrategy,
            DiagnosedAt = diagnosedAt
        };
    }

    private static (string WorkspaceId, string CollectionId) ExtractScope(ContextDecisionResult decision)
    {
        // 首选 SelectedEnvelopes[0]，回退 DroppedEnvelopes[0]，再回退空字符串
        if (decision.SelectedEnvelopes.Count > 0)
        {
            var env = decision.SelectedEnvelopes[0];
            return (env.WorkspaceId, env.CollectionId);
        }
        if (decision.DroppedEnvelopes.Count > 0)
        {
            var env = decision.DroppedEnvelopes[0];
            return (env.WorkspaceId, env.CollectionId);
        }
        return (string.Empty, string.Empty);
    }
}
