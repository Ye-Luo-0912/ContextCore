using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.BoundedContext;

/// <summary>
/// R22-3：默认 <see cref="IBoundedContextOrchestrator"/> 实现。
/// 编排单次有界修复循环：Plan → Decide → Build → Quality Evaluate → Optional Single Repair → Finalize。
/// </summary>
/// <remarks>
/// <b>设计原则</b>（对齐用户规格与 R22-1 契约）：
/// <list type="bullet">
/// <item><b>不使用无限循环</b>：最多一次修复，不允许 Build → Evaluate → Refine → Evaluate → ... 递归。</item>
/// <item><b>修复预算必须显式</b>：调用方传入 <see cref="ContextRepairBudget"/>；预算全 0 时跳过修复。</item>
/// <item><b>多异常优先级</b>：检测器返回的 Diagnosis 列表按检测顺序处理；orchestrator 仅取 <b>第一条</b>
/// 触发修复（其余异常记录在 Diagnoses 中但不修复）。</item>
/// <item><b>Finalize 不重评估</b>：修复完成后不再调用 Detector 二次评估；最终 QualityReport 来自 executor 响应
/// 或原始 QualityReport（无修复时）。</item>
/// <item><b>幂等</b>：相同输入（detector + executor 均为纯函数实现时）应产生相同输出。</item>
/// </list>
///
/// <b>编排流程</b>：
/// <list type="number">
/// <item><b>Plan</b>：调用方传入 DecisionResult + QualityReport + Budget。</item>
/// <item><b>Decide</b>：已完成（DecisionResult 为输入）。</item>
/// <item><b>Build</b>：已完成（DecisionResult 为输入）。</item>
/// <item><b>Quality Evaluate</b>：调用 <see cref="IContextRepairDetector.DetectAsync"/> 检测异常。</item>
/// <item><b>Optional Single Repair</b>：若检测到异常且预算允许，调用 <see cref="IContextRepairExecutor.ExecuteAsync"/> 执行一次修复。</item>
/// <item><b>Finalize</b>：返回 <see cref="BoundedContextOrchestrationResult"/>。</item>
/// </list>
///
/// <b>跳过修复的条件</b>：
/// <list type="bullet">
/// <item>无异常检测到（diagnoses 为空）。</item>
/// <item>预算全 0（MaxAdditionalStoreCalls=0 AND MaxAdditionalCandidates=0 AND
/// MaxAdditionalTokens=0 AND MaxAdditionalLatency=Zero）。</item>
/// </list>
/// </remarks>
public sealed class DefaultBoundedContextOrchestrator : IBoundedContextOrchestrator
{
    private readonly IContextRepairDetector _detector;
    private readonly IContextRepairExecutor _executor;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造默认 orchestrator。</summary>
    /// <param name="detector">修复检测器（必填）。</param>
    /// <param name="executor">修复执行器（必填）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultBoundedContextOrchestrator(
        IContextRepairDetector detector,
        IContextRepairExecutor executor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(executor);
        _detector = detector;
        _executor = executor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<BoundedContextOrchestrationResult> OrchestrateAsync(
        ContextDecisionResult decision,
        PackageQualityReport qualityReport,
        ContextRepairBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(qualityReport);
        ArgumentNullException.ThrowIfNull(budget);
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = _timeProvider.GetUtcNow();

        // 4. Quality Evaluate
        var diagnoses = await _detector.DetectAsync(decision, qualityReport, cancellationToken).ConfigureAwait(false);

        // 5. Optional Single Repair
        ContextRepairResponse? response = null;
        var finalDecision = decision;
        var finalQualityReport = qualityReport;

        if (diagnoses.Count > 0 && HasBudget(budget))
        {
            // 仅取第一条 Diagnosis 触发修复（优先级顺序）
            var firstDiagnosis = diagnoses[0];
            var repairRequest = new ContextRepairRequest
            {
                RepairRequestId = $"repair-{Guid.NewGuid():N}",
                Diagnosis = firstDiagnosis,
                Budget = budget,
                OriginalDecision = decision,
                OriginalQualityReport = qualityReport,
                RequestedAt = _timeProvider.GetUtcNow()
            };

            response = await _executor.ExecuteAsync(repairRequest, cancellationToken).ConfigureAwait(false);

            if (response.WasRepaired)
            {
                finalDecision = response.RepairedDecision;
                finalQualityReport = response.RepairedQualityReport ?? qualityReport;
            }
        }

        // 6. Finalize
        var completedAt = _timeProvider.GetUtcNow();
        return new BoundedContextOrchestrationResult
        {
            OrchestrationId = $"orch-{Guid.NewGuid():N}",
            FinalDecision = finalDecision,
            FinalQualityReport = finalQualityReport,
            Diagnoses = diagnoses,
            RepairResponse = response,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
    }

    /// <summary>
    /// 检查预算是否允许修复（任一字段 > 0 即视为有预算）。
    /// 全 0 预算视为"显式不修复"。
    /// </summary>
    private static bool HasBudget(ContextRepairBudget budget)
    {
        return budget.MaxAdditionalStoreCalls > 0
            || budget.MaxAdditionalCandidates > 0
            || budget.MaxAdditionalTokens > 0
            || budget.MaxAdditionalLatency > TimeSpan.Zero;
    }
}
