using ContextCore.Abstractions.Models;

namespace ContextCore.Abstractions;

/// <summary>
/// 上下文演化 Agent 契约。
/// 监测上下文的演化机会（promotion 积压、stale 稳定记忆、约束缺口等），
/// 提出演化步骤并可选执行。
/// </summary>
/// <remarks>
/// 该契约是演化的编排入口，内部可组合 <c>ShortTermPromotionCandidateService</c>、
/// <c>StableReviewCandidateService</c>、<c>StableLifecycleReviewService</c>、
/// <c>ConstraintGapCandidateService</c> 等已有服务。
/// 实现不得绕过既有 approval 工作流（write/delete/command 操作仍需审批）。
/// </remarks>
public interface IContextEvolutionAgent
{
    /// <summary>
    /// 执行一个演化周期：监测目标 → 提出步骤 → 可选应用。
    /// </summary>
    /// <param name="request">周期请求（工作空间、目标类型过滤器、MaxSteps、AutoApply）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 周期结果，包含处理的目标、产出的步骤及各状态计数。
    /// 当 <see cref="EvolutionCycleRequest.AutoApply"/>=false 时，步骤状态为 <see cref="EvolutionStepStatus.Proposed"/>，
    /// 调用方需审批后再次调用以应用。
    /// </returns>
    Task<EvolutionCycleResult> RunCycleAsync(
        EvolutionCycleRequest request,
        CancellationToken cancellationToken = default);
}
