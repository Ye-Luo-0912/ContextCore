using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;

namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// 评测运行时所需的最小状态视图，仅包含 Evaluation runners 实际使用的主链 stores 与服务。
/// 由 EvalState 实现。
/// </summary>
public interface IEvalStateCore
{
    string WorkspaceId { get; }

    string CollectionId { get; }

    IContextStore ContextStore { get; }

    IMemoryStore MemoryStore { get; }

    IConstraintStore ConstraintStore { get; }

    IRelationStore RelationStore { get; }

    IVectorStore VectorStore { get; }

    IEmbeddingProvider EmbeddingProvider { get; }

    IContextRetriever Retriever { get; }

    IContextPackageBuilder PackageBuilder { get; }
}

/// <summary>
/// EvalCommand 与 IEvalHost 所需的扩展状态视图，在 IEvalStateCore 之上增加
/// Service/host 模式判定、服务客户端、学习反馈存储、作业队列、检索 trace、
/// 模型网关健康，以及存储路径解析所需的判别字段。
/// Evaluation runners 仅依赖 IEvalStateCore。
/// </summary>
public interface IEvalStateServiceMode : IEvalStateCore
{
    bool IsServiceMode { get; }

    string StorageKind { get; }

    string RootPath { get; }

    string? ServiceBaseUrl { get; }

    ContextCoreClient? ServiceClient { get; }

    IContextJobQueue JobQueue { get; }

    IContextJobQueryStore JobQueryStore { get; }

    ILearningFeedbackStore LearningFeedbackStore { get; }

    ILearningFeedbackReviewStore LearningFeedbackReviewStore { get; }

    IRetrievalTraceStore RetrievalTraceStore { get; }

    ModelGatewayOptions ModelGatewayOptions { get; }

    IModelHealthService ModelHealthService { get; }

    IModelUsageLogStore ModelUsageLogStore { get; }
}
