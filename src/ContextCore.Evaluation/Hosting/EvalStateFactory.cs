using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Evaluation.Hosting;
using ContextCore.Evaluation.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Runtime;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Evaluation.Hosting;

/// <summary>
/// 评测隔离运行时状态工厂，替代 ControlRoomService.CreateState 的 InMemory 分支。
/// Evaluation 不能引用 ControlRoom，因此在此处内联运行时对象图组装逻辑。
/// </summary>
internal static class EvalStateFactory
{
    public static IEvalStateServiceMode CreateInMemoryState(
        string workspaceId,
        string collectionId)
    {
        var resolvedRootPath = FileStorageOptions.ResolveRootPath("eval");

        var contextStore = new InMemoryContextStore();
        var index = new InMemoryContextIndex();
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var relationStore = new InMemoryRelationStore();
        var vectorStore = new InMemoryVectorStore();
        var retrievalTraceStore = new InMemoryRetrievalTraceStore();
        var packagePolicyStore = new InMemoryContextPackagePolicyStore();
        var learningFeedbackStore = new InMemoryLearningFeedbackStore();
        var learningFeedbackReviewStore = new InMemoryLearningFeedbackReviewStore();
        var globalStore = new InMemoryGlobalContextStore();
        var jobQueue = new InMemoryJobQueue();
        var embeddingProvider = new MockEmbeddingProvider(new EmbeddingOptions
        {
            ModelName = "eval-mock-embedding",
            Dimensions = 512,
            MaxBatchSize = 16
        });
        var modelOptions = ModelGatewayDefaults.CreateDefaultOptions();
        var apiKeyResolver = new ApiKeyResolver();
        var modelAdapters = ModelAdapterFactory.CreateAdapters(modelOptions, apiKeyResolver);
        var modelUsageLogStore = new InMemoryModelUsageLogStore();
        var tokenizerResolver = new DefaultContextTokenizerResolver();

        var runtime = ContextRuntimeBuilder.Build(new RuntimeBuildOptions
        {
            ContextStore = contextStore,
            MemoryStore = memoryStore,
            ConstraintStore = constraintStore,
            RelationStore = relationStore,
            GlobalContextStore = globalStore,
            VectorStore = vectorStore,
            EmbeddingProvider = embeddingProvider,
            RetrievalTraceStore = retrievalTraceStore,
            TokenizerResolver = tokenizerResolver,
            PromotionRecordStore = memoryStore,
            WorkingMemoryService = memoryStore,
            ShortTermMemoryStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            LearningStore = new InMemoryContextLearningStore()
        });

        return new EvalState
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StorageKind = "memory",
            RootPath = resolvedRootPath,
            ContextStore = contextStore,
            Index = index,
            MemoryStore = memoryStore,
            WorkingMemory = memoryStore,
            ConstraintStore = constraintStore,
            RelationStore = relationStore,
            GlobalContextStore = globalStore,
            JobQueue = jobQueue,
            JobQueryStore = jobQueue,
            PromotionService = runtime.PromotionService,
            PromotionCandidateStore = memoryStore,
            PackageBuilder = runtime.PackageBuilder,
            TokenizerResolver = tokenizerResolver,
            PackagePolicyStore = packagePolicyStore,
            LearningFeedbackStore = learningFeedbackStore,
            LearningFeedbackReviewStore = learningFeedbackReviewStore,
            VectorStore = vectorStore,
            EmbeddingProvider = embeddingProvider,
            RetrievalTraceStore = retrievalTraceStore,
            Retriever = runtime.Retriever,
            ModelGatewayOptions = modelOptions,
            ModelHealthService = new ModelHealthService(modelOptions, modelAdapters, apiKeyResolver),
            ModelUsageLogStore = modelUsageLogStore
        };
    }
}

/// <summary>评测隔离运行时状态，实现 IEvalStateServiceMode。仅用于 InMemory 评测场景。</summary>
internal sealed class EvalState : IEvalStateServiceMode
{
    public bool IsServiceMode => false;

    public string WorkspaceId { get; init; } = "default";

    public string CollectionId { get; init; } = "test";

    public string StorageKind { get; init; } = "memory";

    public string RootPath { get; init; } = ".";

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

    public ILearningFeedbackStore LearningFeedbackStore { get; init; } = default!;

    public ILearningFeedbackReviewStore LearningFeedbackReviewStore { get; init; } = default!;

    public IContextTokenizerResolver TokenizerResolver { get; init; } = new DefaultContextTokenizerResolver();

    public IVectorStore VectorStore { get; init; } = default!;

    public IEmbeddingProvider EmbeddingProvider { get; init; } = default!;

    public IRetrievalTraceStore RetrievalTraceStore { get; init; } = default!;

    public IContextRetriever Retriever { get; init; } = default!;

    public ModelGatewayOptions ModelGatewayOptions { get; init; } = new();

    public IModelHealthService ModelHealthService { get; init; } = default!;

    public IModelUsageLogStore ModelUsageLogStore { get; init; } = default!;

    public ContextPackage? LastPackage { get; set; }
}
