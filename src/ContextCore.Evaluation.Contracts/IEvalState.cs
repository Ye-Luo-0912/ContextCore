using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;

namespace ContextCore.Evaluation.Contracts;

/// <summary>
/// 评测运行时所需的最小状态视图，由 ControlRoomState（ControlRoom 侧）和 EvalState（Evaluation 侧）实现。
/// 承载于 Evaluation.Contracts，使 Evaluation 可以引用而不依赖 ControlRoom，且不污染 Client SDK。
/// </summary>
public interface IEvalState
{
    bool IsServiceMode { get; }

    string WorkspaceId { get; }

    string CollectionId { get; }

    string StorageKind { get; }

    string RootPath { get; }

    string? ServiceBaseUrl { get; }

    ContextCoreClient? ServiceClient { get; }

    IContextStore ContextStore { get; }

    IContextIndex Index { get; }

    IMemoryStore MemoryStore { get; }

    IWorkingMemoryService WorkingMemory { get; }

    IConstraintStore ConstraintStore { get; }

    IRelationStore RelationStore { get; }

    IGlobalContextStore GlobalContextStore { get; }

    IContextJobQueue JobQueue { get; }

    IContextJobQueryStore JobQueryStore { get; }

    IMemoryPromotionService PromotionService { get; }

    IPromotionCandidateStore PromotionCandidateStore { get; }

    IContextPackageBuilder PackageBuilder { get; }

    IContextPackagePolicyStore PackagePolicyStore { get; }

    ILearningFeedbackStore LearningFeedbackStore { get; }

    ILearningFeedbackReviewStore LearningFeedbackReviewStore { get; }

    IArtifactStore ArtifactStore { get; }

    IContextTokenizerResolver TokenizerResolver { get; }

    IVectorStore VectorStore { get; }

    IEmbeddingProvider EmbeddingProvider { get; }

    IRetrievalTraceStore RetrievalTraceStore { get; }

    IContextRetriever Retriever { get; }

    ModelGatewayOptions ModelGatewayOptions { get; }

    IModelHealthService ModelHealthService { get; }

    IModelUsageLogStore ModelUsageLogStore { get; }

    ContextPackage? LastPackage { get; set; }
}
