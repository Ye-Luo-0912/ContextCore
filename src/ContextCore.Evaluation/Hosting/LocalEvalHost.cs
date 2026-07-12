using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Models;

namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// Evaluation 独立 CLI 的本地 IEvalHost 实现。
/// 使用 EvalStateFactory 创建 InMemory 隔离运行时状态，
/// 不依赖 ControlRoom 的 ControlRoomService。
/// </summary>
internal sealed class LocalEvalHost : IEvalHost
{
    private readonly IEvalStateServiceMode _state;

    public LocalEvalHost(string workspaceId, string collectionId)
    {
        _state = EvalStateFactory.CreateInMemoryState(workspaceId, collectionId);
    }

    public IEvalStateServiceMode State => _state;

    public Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        return new LearningFeedbackService(_state.LearningFeedbackStore)
            .SubmitAsync(request, cancellationToken);
    }

    public Task<VectorReindexPlan> CreateServiceVectorReindexPlanAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "CreateServiceVectorReindexPlanAsync 仅在 Service 模式下可用，本地 Evaluation CLI 不支持此操作。");
    }

    public Task<VectorReindexSubmitResponse> SubmitServiceVectorReindexAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "SubmitServiceVectorReindexAsync 仅在 Service 模式下可用，本地 Evaluation CLI 不支持此操作。");
    }
}
