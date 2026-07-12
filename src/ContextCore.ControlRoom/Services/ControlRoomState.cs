using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Storage.FileSystem;

namespace ContextCore.ControlRoom.Services;

/// <summary>ControlRoom 的运行模式：直接读本地存储，或通过 Service API 远程连接。</summary>
public enum ControlRoomMode
{
    Direct,
    Service
}

/// <summary>控制室运行时状态对象，持有当前工作区、集合及各存储层的服务引用。</summary>
public sealed class ControlRoomState
{
    public ControlRoomMode Mode { get; init; } = ControlRoomMode.Direct;

    public string WorkspaceId { get; init; } = "default";

    public string CollectionId { get; init; } = "test";

    public string StorageKind { get; init; } = "filesystem";

    public string RootPath { get; init; } = FileStorageOptions.DefaultRootPath;

    public string? ServiceBaseUrl { get; init; }

    public ContextCoreClient? ServiceClient { get; init; }

    // P5-4: Service Mode 不再创建本地运行时对象，这些属性在 Service Mode 下为 null。
    // Direct Mode（InMemory/FileSystem）会完整赋值；Service Mode 通过 ServiceClient 远程调用。
    public IContextStore? ContextStore { get; init; }

    public IContextIndex? Index { get; init; }

    public IMemoryStore? MemoryStore { get; init; }

    public IWorkingMemoryService? WorkingMemory { get; init; }

    public IConstraintStore? ConstraintStore { get; init; }

    public IRelationStore? RelationStore { get; init; }

    public IGlobalContextStore? GlobalContextStore { get; init; }

    public IContextJobQueue? JobQueue { get; init; }

    public IContextJobQueryStore? JobQueryStore { get; init; }

    public IMemoryPromotionService? PromotionService { get; init; }

    public IPromotionCandidateStore? PromotionCandidateStore { get; init; }

    public IContextPackageBuilder? PackageBuilder { get; init; }

    public IContextPackagePolicyStore? PackagePolicyStore { get; init; }

    public ILearningFeedbackStore? LearningFeedbackStore { get; init; }

    public ILearningFeedbackReviewStore? LearningFeedbackReviewStore { get; init; }

    public IContextTokenizerResolver TokenizerResolver { get; init; } = new DefaultContextTokenizerResolver();

    public IVectorStore? VectorStore { get; init; }

    public IEmbeddingProvider? EmbeddingProvider { get; init; }

    public IRetrievalTraceStore? RetrievalTraceStore { get; init; }

    public IContextRetriever? Retriever { get; init; }

    public ModelGatewayOptions ModelGatewayOptions { get; init; } = new();

    public IModelHealthService? ModelHealthService { get; init; }

    public IModelUsageLogStore? ModelUsageLogStore { get; init; }

    public ContextPackage? LastPackage { get; set; }

    public bool IsServiceMode => Mode == ControlRoomMode.Service;
}
