using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Contracts;

/// <summary>
/// 评测 CLI 的宿主抽象，暴露评测命令所需的运行时状态和少量服务方法。
/// ControlRoomService 实现此接口；Evaluation 通过此接口调用，无需引用 ControlRoom。
/// 承载于 Evaluation.Contracts，与 IEvalState 一致，避免污染 Client SDK。
/// </summary>
public interface IEvalHost
{
    IEvalState State { get; }

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
