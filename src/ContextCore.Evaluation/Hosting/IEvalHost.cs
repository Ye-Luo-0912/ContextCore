using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// 评测 CLI 的宿主抽象，暴露评测命令所需的运行时状态和少量服务方法。
/// 由 LocalEvalHost 实现；Evaluation 通过此接口调用，无需引用 ControlRoom。
/// </summary>
public interface IEvalHost
{
    IEvalStateServiceMode State { get; }

    Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default);

    Task<VectorReindexPlan> CreateServiceVectorReindexPlanAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default);

    Task<VectorReindexSubmitResponse> SubmitServiceVectorReindexAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default);
}
