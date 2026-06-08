using ContextCore.Abstractions;
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

    public IContextStore ContextStore { get; init; } = default!;

    public IContextIndex Index { get; init; } = default!;

    public IMemoryStore MemoryStore { get; init; } = default!;

    public IWorkingMemoryService WorkingMemory { get; init; } = default!;

    public IConstraintStore ConstraintStore { get; init; } = default!;

    public IRelationStore RelationStore { get; init; } = default!;

    public IGlobalContextStore GlobalContextStore { get; init; } = default!;

    public IContextJobQueue JobQueue { get; init; } = default!;

    public IContextJobQueryStore JobQueryStore { get; init; } = default!;

    public IMemoryPromotionService PromotionService { get; init; } = default!;

    public IPromotionCandidateStore PromotionCandidateStore { get; init; } = default!;

    public IContextPackageBuilder PackageBuilder { get; init; } = default!;

    public IContextPackagePolicyStore PackagePolicyStore { get; init; } = default!;

    public IContextTokenizerResolver TokenizerResolver { get; init; } = new DefaultContextTokenizerResolver();

    public IVectorStore VectorStore { get; init; } = default!;

    public IEmbeddingProvider EmbeddingProvider { get; init; } = default!;

    public IRetrievalTraceStore RetrievalTraceStore { get; init; } = default!;

    public IContextRetriever Retriever { get; init; } = default!;

    public ModelGatewayOptions ModelGatewayOptions { get; init; } = new();

    public IModelHealthService ModelHealthService { get; init; } = default!;

    public IModelUsageLogStore ModelUsageLogStore { get; init; } = default!;

    public ContextPackage? LastPackage { get; set; }

    public bool IsServiceMode => Mode == ControlRoomMode.Service;
}
