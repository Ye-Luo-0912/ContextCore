using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Runtime;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>
/// 控制室的核心服务，负责创建应用状态、执行各类操作命令并返回格式化结果。
/// </summary>
public sealed partial class ControlRoomService
{

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ControlRoomState _state;

    public ControlRoomService(ControlRoomState state)
    {
        _state = state;
    }

    /// <summary>直接访问底层状态（供 ControlRoom 命令使用，不对外暴露为公开 API）。</summary>
    public ControlRoomState State => _state;

    public static ControlRoomState CreateState(
        string storageKind,
        string rootPath,
        string workspaceId,
        string collectionId,
        ControlRoomMode mode = ControlRoomMode.Direct,
        string? serviceBaseUrl = null,
        HttpClient? serviceHttpClient = null,
        RetrievalAttentionRerankOptions? attentionRerankOptions = null,
        RetrievalPlanningOptions? retrievalPlanningOptions = null,
        GraphExpansionApplyOptions? graphExpansionApplyOptions = null,
        string? apiKey = null,
        string? apiKeyHeaderName = null)
    {
        if (mode == ControlRoomMode.Service)
        {
            return CreateServiceState(workspaceId, collectionId, serviceBaseUrl, serviceHttpClient, apiKey, apiKeyHeaderName);
        }

        var resolvedRootPath = FileStorageOptions.ResolveRootPath(rootPath);

        // ControlRoom 保持轻量，不依赖 ASP.NET DI；这里按存储类型组装一套本地运行时对象图。
        if (string.Equals(storageKind, "memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storageKind, "inmemory", StringComparison.OrdinalIgnoreCase))
        {
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
                ModelName = "control-room-mock-embedding",
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
                LearningStore = new InMemoryContextLearningStore(),
                GraphExpansionApplyOptions = graphExpansionApplyOptions,
                AttentionRerankOptions = attentionRerankOptions,
                RetrievalPlanningOptions = retrievalPlanningOptions
            });

            return new ControlRoomState
            {
                Mode = ControlRoomMode.Direct,
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

        var options = new FileStorageOptions { RootPath = resolvedRootPath };
        var fileContextStore = new FileContextStore(options);
        var fileIndex = new FileContextIndex(options);
        var fileMemoryStore = new FileMemoryStore(options);
        var fileConstraintStore = new FileConstraintStore(options);
        var fileRelationStore = new FileRelationStore(options);
        var fileVectorStore = new FileVectorStore(options);
        var fileRetrievalTraceStore = new FileRetrievalTraceStore(options);
        var filePackagePolicyStore = new FileContextPackagePolicyStore(options);
        var fileLearningFeedbackStore = new FileLearningFeedbackStore(options);
        var fileLearningFeedbackReviewStore = new FileLearningFeedbackReviewStore(options);
        var fileGlobalStore = new FileGlobalContextStore(options);
        var fileJobQueue = new FileContextJobQueue(options);
        var embeddingOptions = new EmbeddingOptions
        {
            ModelName = EmbeddingModelPaths.DefaultModelName,
            MaxBatchSize = 8,
            MaxSequenceLength = 256,
            OnnxIntraOpNumThreads = 1,
            OnnxInterOpNumThreads = 1,
            QueryInstruction = BgeQueryInstructions.BgeZhV15
        };
        var fileEmbeddingProvider = new OnnxEmbeddingProvider(
            embeddingOptions,
            new OnnxEmbeddingSessionManager(embeddingOptions));
        var fileModelOptions = ModelGatewayDefaults.CreateDefaultOptions();
        var fileApiKeyResolver = new ApiKeyResolver();
        var fileModelAdapters = ModelAdapterFactory.CreateAdapters(fileModelOptions, fileApiKeyResolver);
        var fileModelUsageLogStore = new InMemoryModelUsageLogStore();
        var fileTokenizerResolver = new DefaultContextTokenizerResolver();
        var fileRuntime = ContextRuntimeBuilder.Build(new RuntimeBuildOptions
        {
            ContextStore = fileContextStore,
            MemoryStore = fileMemoryStore,
            ConstraintStore = fileConstraintStore,
            RelationStore = fileRelationStore,
            GlobalContextStore = fileGlobalStore,
            VectorStore = fileVectorStore,
            EmbeddingProvider = fileEmbeddingProvider,
            RetrievalTraceStore = fileRetrievalTraceStore,
            TokenizerResolver = fileTokenizerResolver,
            PromotionRecordStore = fileMemoryStore,
            WorkingMemoryService = fileMemoryStore,
            ShortTermMemoryStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            LearningStore = new InMemoryContextLearningStore(),
            GraphExpansionApplyOptions = graphExpansionApplyOptions,
            AttentionRerankOptions = attentionRerankOptions,
            RetrievalPlanningOptions = retrievalPlanningOptions
        });

        return new ControlRoomState
        {
            Mode = ControlRoomMode.Direct,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StorageKind = "filesystem",
            RootPath = resolvedRootPath,
            ContextStore = fileContextStore,
            Index = fileIndex,
            MemoryStore = fileMemoryStore,
            WorkingMemory = fileMemoryStore,
            ConstraintStore = fileConstraintStore,
            RelationStore = fileRelationStore,
            GlobalContextStore = fileGlobalStore,
            JobQueue = fileJobQueue,
            JobQueryStore = fileJobQueue,
            PromotionService = fileRuntime.PromotionService,
            PromotionCandidateStore = fileMemoryStore,
            PackageBuilder = fileRuntime.PackageBuilder,
            TokenizerResolver = fileTokenizerResolver,
            PackagePolicyStore = filePackagePolicyStore,
            LearningFeedbackStore = fileLearningFeedbackStore,
            LearningFeedbackReviewStore = fileLearningFeedbackReviewStore,
            VectorStore = fileVectorStore,
            EmbeddingProvider = fileEmbeddingProvider,
            RetrievalTraceStore = fileRetrievalTraceStore,
            Retriever = fileRuntime.Retriever,
            ModelGatewayOptions = fileModelOptions,
            ModelHealthService = new ModelHealthService(fileModelOptions, fileModelAdapters, fileApiKeyResolver),
            ModelUsageLogStore = fileModelUsageLogStore
        };
    }

    public static ControlRoomState CreateServiceState(
        string workspaceId,
        string collectionId,
        string? serviceBaseUrl,
        HttpClient? serviceHttpClient = null,
        string? apiKey = null,
        string? apiKeyHeaderName = null)
    {
        if (string.IsNullOrWhiteSpace(serviceBaseUrl))
        {
            throw new InvalidOperationException("ControlRoom Service 模式需要提供 Service BaseUrl。");
        }

        var normalizedBaseUrl = NormalizeServiceBaseUrl(serviceBaseUrl);
        var httpClient = serviceHttpClient ?? new HttpClient
        {
            BaseAddress = new Uri(normalizedBaseUrl, UriKind.Absolute)
        };
        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = new Uri(normalizedBaseUrl, UriKind.Absolute);
        }

        // 注入 API Key 认证头：当 Service 启用 RequireApiKey 时，每个请求都需携带正确的 key。
        // 认证头在 HttpClient 上设置一次，ContextCoreClient 发出的所有请求都会自动带上。
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var headerName = string.IsNullOrWhiteSpace(apiKeyHeaderName)
                ? "X-ContextCore-Key"
                : apiKeyHeaderName;
            httpClient.DefaultRequestHeaders.Add(headerName, apiKey);
        }

        var client = new ContextCoreClient(httpClient);

        // P5-4: Service Mode 不再创建本地运行时对象——所有操作通过 ServiceClient 远程调用。
        return new ControlRoomState
        {
            Mode = ControlRoomMode.Service,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StorageKind = "service",
            RootPath = string.Empty,
            ServiceBaseUrl = normalizedBaseUrl,
            ServiceClient = client,
            TokenizerResolver = new DefaultContextTokenizerResolver(),
            ModelGatewayOptions = ModelGatewayDefaults.CreateDefaultOptions()
        };
    }

    public static WorkspaceDiscoveryResult DiscoverWorkspaces(string rootPath)
    {
        var absoluteRoot = FileStorageOptions.ResolveRootPath(rootPath);
        var workspacesPath = Path.Combine(absoluteRoot, "workspaces");
        if (!Directory.Exists(workspacesPath))
        {
            return new WorkspaceDiscoveryResult
            {
                RootPath = absoluteRoot,
                Workspaces = []
            };
        }

        var workspaces = Directory.EnumerateDirectories(workspacesPath)
            .Select(workspaceDirectory =>
            {
                var workspaceId = Path.GetFileName(workspaceDirectory) ?? string.Empty;
                var collectionsPath = Path.Combine(workspaceDirectory, "collections");
                var collections = Directory.Exists(collectionsPath)
                    ? Directory.EnumerateDirectories(collectionsPath)
                        .Select(collectionDirectory => Path.GetFileName(collectionDirectory) ?? string.Empty)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : [];

                return new WorkspaceDiscoveryItem
                {
                    WorkspaceId = workspaceId,
                    CollectionIds = collections
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.WorkspaceId))
            .OrderBy(item => item.WorkspaceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WorkspaceDiscoveryResult
        {
            RootPath = absoluteRoot,
            Workspaces = workspaces
        };
    }

    private ContextCoreClient GetServiceClient()
    {
        if (!_state.IsServiceMode || _state.ServiceClient is null)
        {
            throw new InvalidOperationException("当前不是 ControlRoom Service 模式。");
        }

        return _state.ServiceClient;
    }

    private static string NormalizeServiceBaseUrl(string value)
    {
        var normalized = value.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return normalized;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>控制室状态页的核心计数和最后一次包构建结果。</summary>
public sealed class ControlRoomStatus
{
    public ControlRoomMode Mode { get; init; } = ControlRoomMode.Direct;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string StorageKind { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public string? ServiceBaseUrl { get; init; }

    public string ReadinessState { get; init; } = string.Empty;

    public string ReadinessMessage { get; init; } = string.Empty;

    public string ProviderState { get; init; } = string.Empty;

    public bool ProductionReady { get; init; }

    public int RawItemCount { get; init; }

    public int WorkingMemoryCount { get; init; }

    public int CandidateMemoryCount { get; init; }

    public int StableMemoryCount { get; init; }

    public int ConstraintCount { get; init; }

    public int RelationCount { get; init; }

    public int IndexEntryCount { get; init; }

    public int QueuedJobCount { get; init; }

    public int RunningJobCount { get; init; }

    public int FailedJobCount { get; init; }

    public int SucceededJobCount { get; init; }

    public ContextPackage? LastPackage { get; init; }

    public string RetrievalBaseline { get; init; } = string.Empty;

    public bool RuntimeFromCache { get; init; }

    public int RuntimeCacheTtlSeconds { get; init; }

    public int RuntimeWarningCount { get; init; }
}

/// <summary>ControlRoom Direct File Mode 使用的轻量 readiness 结论。</summary>
public sealed record LocalReadiness(
    string State,
    string Message,
    string ProviderState,
    bool ProductionReady);

/// <summary>当前存储根目录下发现的工作区集合列表。</summary>
public sealed class WorkspaceDiscoveryResult
{
    public string RootPath { get; init; } = string.Empty;

    public IReadOnlyList<WorkspaceDiscoveryItem> Workspaces { get; init; } = Array.Empty<WorkspaceDiscoveryItem>();
}

/// <summary>单个工作区及其包含的集合 ID。</summary>
public sealed class WorkspaceDiscoveryItem
{
    public string WorkspaceId { get; init; } = string.Empty;

    public IReadOnlyList<string> CollectionIds { get; init; } = Array.Empty<string>();
}

/// <summary>控制室首页所需的完整快照，供文本仪表盘一次性渲染。</summary>
public sealed class DashboardSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public ControlRoomMode Mode { get; init; } = ControlRoomMode.Direct;

    public string StorageKind { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public string? ServiceBaseUrl { get; init; }

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public bool WorkspaceDataFound { get; init; }

    public IReadOnlyList<SystemHealthItem> Health { get; init; } = Array.Empty<SystemHealthItem>();

    public MemoryLayerSummary Memory { get; init; } = new();

    public IReadOnlyList<RecentOperation> RecentOperations { get; init; } = Array.Empty<RecentOperation>();

    public IReadOnlyList<CompressionQualityReport> RecentCompressionQuality { get; init; } = Array.Empty<CompressionQualityReport>();

    public JobsSummary Jobs { get; init; } = new();

    public PackageSummary? LatestPackage { get; init; }

    public IReadOnlyList<string> Alerts { get; set; } = Array.Empty<string>();
}

public sealed class ServiceMemorySnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ContextMemoryItem> Working { get; init; } = Array.Empty<ContextMemoryItem>();

    public IReadOnlyList<ContextMemoryItem> Candidates { get; init; } = Array.Empty<ContextMemoryItem>();

    public IReadOnlyList<ContextMemoryItem> Stable { get; init; } = Array.Empty<ContextMemoryItem>();

    public IReadOnlyList<ContextGlobalItem> Global { get; init; } = Array.Empty<ContextGlobalItem>();

    public MemoryLayoutDiagnostics MemoryLayoutDiagnostics { get; init; } = new();

    public TraceLayoutDiagnostics TraceLayoutDiagnostics { get; init; } = new();
}

public sealed class ServiceCandidateMemorySnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public CandidateMemorySnapshot Snapshot { get; init; } = new();

    public CandidateMemoryDiagnosticsReport Diagnostics { get; init; } = new();
}

public sealed class ServiceStableMemorySnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public StableMemorySnapshot Snapshot { get; init; } = new();

    public StableMemoryDiagnosticsReport Diagnostics { get; init; } = new();
}

public sealed class ServiceConstraintsSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ContextConstraint> Constraints { get; init; } = Array.Empty<ContextConstraint>();
}

public sealed class ServiceConstraintGapsSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ConstraintGapCandidate> Gaps { get; init; } = Array.Empty<ConstraintGapCandidate>();

    public string? Status { get; init; }

    public string? Severity { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

public sealed class ServiceCandidateConstraintsSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ContextConstraint> Constraints { get; init; } = Array.Empty<ContextConstraint>();

    public ContextMemoryStatus? Status { get; init; } = ContextMemoryStatus.Candidate;

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

public sealed class ServiceRelationsSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public ContextCoreRelationsResponse Relations { get; init; } = new();

    public IReadOnlyList<RelationTypeDefinition> RelationTypes { get; init; } = Array.Empty<RelationTypeDefinition>();

    public RelationGraphDiagnosticsReport Diagnostics { get; init; } = new();

    public RelationGraphDiagnosticsReport? ItemDiagnostics { get; init; }

    public GraphExpansionShadowTraceQualityReport GraphShadowTraceQualitySummary { get; init; } = new();

    public IReadOnlyList<GraphExpansionShadowTraceRecord> RecentGraphShadowTraces { get; init; } =
        Array.Empty<GraphExpansionShadowTraceRecord>();
}

public sealed class ServicePolicySnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ContextPackagePolicy> Policies { get; init; } = Array.Empty<ContextPackagePolicy>();

    public ContextPackagePolicy DefaultPolicy { get; init; } = new();

    public IReadOnlyList<ProviderCapabilityResponse> ProviderCapabilities { get; init; } = Array.Empty<ProviderCapabilityResponse>();

    public IReadOnlyList<string> LifecycleNotes { get; init; } = Array.Empty<string>();
}

public sealed class ServiceShortTermMemorySnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ShortTermMemorySummary Summary { get; init; } = new();

    public IReadOnlyList<ShortTermRawEvent> RawEvents { get; init; } = Array.Empty<ShortTermRawEvent>();

    public ShortTermArchiveSummary ArchiveSummary { get; init; } = new();

    public ShortTermArchiveItemsResponse ArchiveItems { get; init; } = new();

    public IReadOnlyList<ShortTermCompactionRun> RecentRuns { get; init; } = Array.Empty<ShortTermCompactionRun>();

    public ShortTermMaintenanceStatusResponse? Maintenance { get; init; }
}

public sealed class ServicePromotionCandidatesSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ShortTermPromotionCandidate> Candidates { get; init; } = Array.Empty<ShortTermPromotionCandidate>();

    public PromotionCandidateStatus? Status { get; init; }

    public string? Kind { get; init; }

    public string? SuggestedTargetLayer { get; init; }

    public double? MinConfidence { get; init; }

    public double? MinImportance { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

public sealed class ServiceStableReviewCandidatesSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<StableReviewCandidate> Candidates { get; init; } = Array.Empty<StableReviewCandidate>();

    public string? Status { get; init; }

    public string? ValidationStatus { get; init; }

    public string? Kind { get; init; }

    public string? SuggestedStableTarget { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

public sealed class ServiceLearningSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ContextLearningSummary? Summary { get; init; }

    public IReadOnlyList<PromotionFeedbackSignal> FeedbackSignals { get; init; } = Array.Empty<PromotionFeedbackSignal>();

    public IReadOnlyList<ContextLearningRecord> Records { get; init; } = Array.Empty<ContextLearningRecord>();

    public IReadOnlyList<ContextLearningCase> Cases { get; init; } = Array.Empty<ContextLearningCase>();

    public IReadOnlyList<ContextLearningCase> RegressionCases { get; init; } = Array.Empty<ContextLearningCase>();

    public ContextLearningCaseGenerationResult? LastGeneration { get; init; }

    public ContextLearningCaseStatusUpdateResponse? LastStatusUpdate { get; init; }

    public int PositiveCount { get; init; }

    public int NegativeCount { get; init; }

    public int StaleCount { get; init; }

    public IReadOnlyDictionary<ContextFailureType, int> FailureTypeSummary { get; init; } =
        new Dictionary<ContextFailureType, int>();
}

public sealed class ServicePolicyFeedbackDatasetSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public PolicyFeedbackDataset Dataset { get; init; } = new();

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
}

public sealed class ServiceLearningFeaturesSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public LearningFeatureDataset Dataset { get; init; } = new();

    public LearningDatasetQualityReport QualityReport { get; init; } = new();

    public LearningFeedbackSummaryReport LearningFeedbackSummary { get; init; } = new();

    public LearningFeedbackReviewSummaryReport LearningFeedbackReviewSummary { get; init; } = new();

    public LearningFeedbackFeatureCandidateReport? LearningFeedbackFeatureCandidateReport { get; init; }

    public LearningFeedbackQualityReport? LearningFeedbackQualityReport { get; init; }

    public LearningApprovedFeedbackDatasetGateReport? LearningApprovedFeedbackDatasetGateReport { get; init; }

    public RouterIntentClassifierBaselineReport? RouterIntentBaselineReport { get; init; }

    public RouterShadowTraceQualityReport? RouterShadowTraceQualityReport { get; init; }

    public RouterDisagreementTriageReport? RouterDisagreementTriageA3Report { get; init; }

    public RouterDisagreementTriageReport? RouterDisagreementTriageExtendedReport { get; init; }

    public int RouterHardNegativeCount { get; init; }

    public RouterGuardedOptInReadinessGateReport? RouterGuardedOptInReadinessGateReport { get; init; }

    public CandidateRerankerFeatureCompletenessReport? CandidateRerankerFeatureCompletenessA3Report { get; init; }

    public CandidateRerankerFeatureCompletenessReport? CandidateRerankerFeatureCompletenessExtendedReport { get; init; }

    public CandidateRerankerShadowEvalReport? CandidateRerankerShadowEvalA3Report { get; init; }

    public CandidateRerankerShadowEvalReport? CandidateRerankerShadowEvalExtendedReport { get; init; }

    public CandidateRerankerShadowFailureAuditReport? CandidateRerankerShadowFailureAuditA3Report { get; init; }

    public CandidateRerankerShadowFailureAuditReport? CandidateRerankerShadowFailureAuditExtendedReport { get; init; }

    public CandidateRerankerScoreDistributionReport? CandidateRerankerScoreDistributionA3Report { get; init; }

    public CandidateRerankerScoreDistributionReport? CandidateRerankerScoreDistributionExtendedReport { get; init; }

    public CandidateRerankerListwiseCalibrationReport? CandidateRerankerListwiseCalibrationA3Report { get; init; }

    public CandidateRerankerListwiseCalibrationReport? CandidateRerankerListwiseCalibrationExtendedReport { get; init; }

    public CandidateRerankerFormalPriorityAlignmentReport? CandidateRerankerFormalPriorityAlignmentA3Report { get; init; }

    public CandidateRerankerFormalPriorityAlignmentReport? CandidateRerankerFormalPriorityAlignmentExtendedReport { get; init; }

    public CandidateRerankerShadowTraceQualityReport? CandidateRerankerShadowTraceQualityReport { get; init; }

    public LearningReadinessRegistry? LearningReadinessRegistry { get; init; }

    public LearningRuntimeChangeReadinessGateReport? LearningRuntimeChangeReadinessGateReport { get; init; }

    public ContextCoreFoundationFreezeReport? FoundationFreezeReport { get; init; }

    public ArchitectureCleanupFreezeReport? ArchitectureCleanupFreezeReport { get; init; }

    public ArchitectureCleanupFreezeGateReport? ArchitectureCleanupFreezeGateReport { get; init; }

    public FoundationServiceStatusResponse? FoundationServiceStatus { get; init; }

    public FoundationReportNavigationResponse? FoundationReportNavigation { get; init; }

    public FoundationApiSecurityDiagnosticsReport? FoundationApiSecurityDiagnostics { get; init; }

    public FoundationApiContractReport? FoundationApiContractReport { get; init; }

    public FoundationServiceAuthDiagnosticsReport? FoundationServiceAuthDiagnostics { get; init; }

    public FoundationServiceDeploymentProfileGateReport? FoundationServiceDeploymentProfileGate { get; init; }

    public FoundationOpenApiContractReport? FoundationOpenApiContractReport { get; init; }

    public HostedServiceSmokeReport? HostedServiceSmokeReport { get; init; }

    public ServiceFoundationFreezeReport? ServiceFoundationFreezeReport { get; init; }

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
}

public sealed class ServiceVectorIndexSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public VectorIndexStatusResponse Status { get; init; } = new();

    public VectorIndexDiagnosticsReport Diagnostics { get; init; } = new();

    public VectorReindexPreviewResponse ReindexPreview { get; init; } = new();

    public VectorIndexCoverageReport Coverage { get; init; } = new();

    public ServiceVectorShadowQualitySummary ShadowQuality { get; init; } = new();
}

public sealed class ServiceVectorShadowQualitySummary
{
    public bool Available { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public string CurrentRecommendation { get; init; } = string.Empty;

    public string BestProfile { get; init; } = string.Empty;

    public int BestTopK { get; init; }

    public double BestMinSimilarity { get; init; }

    public int RiskAfterPolicy { get; init; }

    public double SimilaritySeparation { get; init; }

    public string ResidualRiskSourcePath { get; init; } = string.Empty;

    public int ResidualRiskCount { get; init; }

    public IReadOnlyDictionary<string, int> TopResidualRiskTypes { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> TopWhyPolicyAllowed { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> TopExpectedActions { get; init; } = Array.Empty<string>();

    public string LifecycleMetadataCoverageSourcePath { get; init; } = string.Empty;

    public double LifecycleMetadataCoverageRate { get; init; }

    public int UnknownLifecycleCount { get; init; }

    public int MissingReviewStatusCount { get; init; }

    public int MissingReplacementInfoCount { get; init; }

    public int BlockedByLifecycleMetadataGate { get; init; }

    public string LifecycleBackfillPlanSourcePath { get; init; } = string.Empty;

    public int BackfillUnknownLifecycleBefore { get; init; }

    public int BackfillAutoResolvableCount { get; init; }

    public int BackfillManualReviewRequiredCount { get; init; }

    public double BackfillExpectedCoverageAfter { get; init; }

    public string RecallLossA3SourcePath { get; init; } = string.Empty;

    public string RecallLossExtendedSourcePath { get; init; } = string.Empty;

    public double A3RecallAfterPolicy { get; init; }

    public double ExtendedRecallAfterPolicy { get; init; }

    public string A3RecallRecommendation { get; init; } = string.Empty;

    public string ExtendedRecallRecommendation { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, int> TopRecallMissReasons { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> IntentReadinessRecommendations { get; init; } =
        Array.Empty<string>();

    public string SafeRecallRecoveryA3SourcePath { get; init; } = string.Empty;

    public string SafeRecallRecoveryExtendedSourcePath { get; init; } = string.Empty;

    public double SafeRecoveryA3RecallAfterPolicy { get; init; }

    public double SafeRecoveryExtendedRecallAfterPolicy { get; init; }

    public string SafeRecoveryA3BestConfiguration { get; init; } = string.Empty;

    public string SafeRecoveryExtendedBestConfiguration { get; init; } = string.Empty;

    public int SafeRecoveryA3RecoveredBelowTopK { get; init; }

    public int SafeRecoveryExtendedRecoveredBelowTopK { get; init; }

    public IReadOnlyDictionary<string, int> BlockedMustHitClassificationCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string FusionShadowA3SourcePath { get; init; } = string.Empty;

    public string FusionShadowExtendedSourcePath { get; init; } = string.Empty;

    public string FusionBestStrategy { get; init; } = string.Empty;

    public double FusionA3RecallAfterPolicy { get; init; }

    public double FusionExtendedRecallAfterPolicy { get; init; }

    public int FusionRiskAfterPolicy { get; init; }

    public double FusionRecallGain { get; init; }

    public bool FusionReadinessGateSatisfied { get; init; }

    public string RepresentationBenchmarkA3SourcePath { get; init; } = string.Empty;

    public string RepresentationBenchmarkExtendedSourcePath { get; init; } = string.Empty;

    public string RepresentationBestDocumentProfile { get; init; } = string.Empty;

    public string RepresentationBestQueryProfile { get; init; } = string.Empty;

    public double RepresentationA3RecallAfterPolicy { get; init; }

    public double RepresentationExtendedRecallAfterPolicy { get; init; }

    public int RepresentationRiskAfterPolicy { get; init; }

    public int RepresentationRecoveredMissCount { get; init; }

    public bool RepresentationV4GateSatisfied { get; init; }

    public string QueryExpansionShadowA3SourcePath { get; init; } = string.Empty;

    public string QueryExpansionShadowExtendedSourcePath { get; init; } = string.Empty;

    public string QueryExpansionBestProfile { get; init; } = string.Empty;

    public double QueryExpansionA3RecallBefore { get; init; }

    public double QueryExpansionA3RecallAfter { get; init; }

    public double QueryExpansionExtendedRecallBefore { get; init; }

    public double QueryExpansionExtendedRecallAfter { get; init; }

    public int QueryExpansionRecoveredMissCount { get; init; }

    public int QueryExpansionRiskAfterPolicy { get; init; }

    public bool QueryExpansionV4GateSatisfied { get; init; }

    public string V4ReadinessGateSourcePath { get; init; } = string.Empty;

    public bool V4ReadinessGatePassed { get; init; }

    public IReadOnlyList<string> V4ReadinessGateFailReasons { get; init; } = Array.Empty<string>();

    public bool V4GateSatisfied { get; init; }

    public string ProviderComparisonSourcePath { get; init; } = string.Empty;
    public IReadOnlyList<VectorProviderComparisonV310Result> ProviderComparisonResults { get; init; } = Array.Empty<VectorProviderComparisonV310Result>();

    public string Qwen3ReadinessGateSourcePath { get; init; } = string.Empty;
    public bool Qwen3ReadinessGatePassed { get; init; }
    public string Qwen3Recommendation { get; init; } = string.Empty;
    public IReadOnlyList<string> Qwen3BlockedReasons { get; init; } = Array.Empty<string>();

    public string ProviderComparisonFreezeSourcePath { get; init; } = string.Empty;
    public string ProviderPromotionStatus { get; init; } = string.Empty;
    public bool ProviderConfigurationSanityPassed { get; init; }
    public string ProviderComparisonStatus { get; init; } = string.Empty;
    public bool VectorV4RecheckAllowed { get; init; }
    public IReadOnlyList<string> ProviderPromotionBlockedReasons { get; init; } = Array.Empty<string>();

    public string HybridPreviewSourcePath { get; init; } = string.Empty;
    public string HybridFullA3Recall { get; init; } = string.Empty;
    public string HybridFullExtendedRecall { get; init; } = string.Empty;
    public int HybridFullRiskAfterPolicy { get; init; }
    public string HybridReadinessRecommendation { get; init; } = string.Empty;
    public bool HybridReadinessGatePassed { get; init; }

    public string HybridAuditSourcePath { get; init; } = string.Empty;
    public bool HybridAuditPassed { get; init; }
    public string HybridAuditRecommendation { get; init; } = string.Empty;
    public int HybridAuditDenseDroppedCount { get; init; }
    public int HybridAuditEligibilityMismatchCount { get; init; }
    public int HybridAuditDedupOverwriteCount { get; init; }

    public string HybridFreezeSourcePath { get; init; } = string.Empty;
    public bool HybridFreezePassed { get; init; }
    public string HybridFreezeStatus { get; init; } = string.Empty;
    public string HybridFreezeRecommendation { get; init; } = string.Empty;
    public bool HybridV4RecheckAllowed { get; init; }
    public IReadOnlyList<string> HybridFreezeBlockedReasons { get; init; } = Array.Empty<string>();

    public string DatasetAlignmentAuditSourcePath { get; init; } = string.Empty;
    public string DatasetAlignmentRecommendation { get; init; } = string.Empty;
    public int DatasetAlignmentIssueCount { get; init; }
    public double DatasetAlignmentA3MustHitCorpusCoverage { get; init; }
    public double DatasetAlignmentExtendedMustHitCorpusCoverage { get; init; }
    public double DatasetAlignmentA3ProviderScopeCoverage { get; init; }
    public double DatasetAlignmentExtendedProviderScopeCoverage { get; init; }
    public int DatasetAlignmentEligibilityBlockCount { get; init; }
    public double DatasetAlignmentAnchorCoverageRate { get; init; }
    public IReadOnlyDictionary<string, int> DatasetAlignmentTopIssues { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string EligibilityRecallLossTriageSourcePath { get; init; } = string.Empty;
    public int EligibilityFilteredMustHitCount { get; init; }
    public int EligibilityCorrectlyBlockedCount { get; init; }
    public int EligibilityRouteToHistoricalCount { get; init; }
    public int EligibilityRouteToAuditCount { get; init; }
    public int EligibilityMetadataRepairNeededCount { get; init; }
    public int EligibilityEvalExpectationReviewNeededCount { get; init; }
    public int EligibilityUnsafeToRecoverCount { get; init; }
    public string EligibilityRecallLossRecommendation { get; init; } = string.Empty;

    public string LifecycleMetadataRepairPlanSourcePath { get; init; } = string.Empty;
    public int LifecycleMetadataRepairCandidateCount { get; init; }
    public int LifecycleMetadataRepairAutoRepairableCount { get; init; }
    public int LifecycleMetadataRepairHumanReviewRequiredCount { get; init; }
    public int LifecycleMetadataRepairForbiddenCount { get; init; }
    public double LifecycleMetadataRepairEstimatedRecallRecovery { get; init; }
    public int LifecycleMetadataRepairRiskEstimate { get; init; }
    public string LifecycleMetadataRepairRecommendation { get; init; } = string.Empty;

    public string LifecycleMetadataReviewCandidatesSourcePath { get; init; } = string.Empty;
    public int LifecycleMetadataReviewCandidateCount { get; init; }
    public int LifecycleMetadataReviewPendingCount { get; init; }
    public int LifecycleMetadataReviewCorrectlyBlockedSkippedCount { get; init; }
    public IReadOnlyDictionary<string, int> LifecycleMetadataReviewCountByLayer { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> LifecycleMetadataReviewCountByItemKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<VectorLifecycleMetadataReviewCandidate> LifecycleMetadataReviewRecentCandidates { get; init; } =
        Array.Empty<VectorLifecycleMetadataReviewCandidate>();
    public string LifecycleMetadataReviewRecommendation { get; init; } = string.Empty;

    public string LifecycleMetadataReviewSummarySourcePath { get; init; } = string.Empty;
    public int LifecycleMetadataReviewApprovedForSidecarCount { get; init; }
    public int LifecycleMetadataReviewRejectedCount { get; init; }
    public int LifecycleMetadataReviewNeedsEvidenceCount { get; init; }
    public int LifecycleMetadataReviewSupersededCount { get; init; }
    public int LifecycleMetadataReviewSidecarEntryCount { get; init; }
    public int LifecycleMetadataReviewUnsafeApprovalBlockedCount { get; init; }
    public string LifecycleMetadataReviewSidecarPreviewSourcePath { get; init; } = string.Empty;
    public int LifecycleMetadataReviewNormalContextApprovalCount { get; init; }
    public int LifecycleMetadataReviewAuditContextApprovalCount { get; init; }
    public int LifecycleMetadataReviewHistoricalContextApprovalCount { get; init; }
    public int LifecycleMetadataReviewDiagnosticsOnlyApprovalCount { get; init; }

    public string SidecarEligibilityPreviewSourcePath { get; init; } = string.Empty;
    public int SidecarEligibilityCandidateCount { get; init; }
    public int SidecarEligibilitySidecarEntryCount { get; init; }
    public int SidecarEligibilityApprovedSidecarCount { get; init; }
    public int SidecarEligibilityPendingReviewCount { get; init; }
    public int SidecarEligibilityEffectiveMetadataChangedCount { get; init; }
    public int SidecarEligibilityUnsafeBlockedCount { get; init; }
    public int SidecarEligibilityConflictBlockedCount { get; init; }
    public bool SidecarEligibilitySourceItemUnchanged { get; init; } = true;
    public string SidecarEligibilityRecommendation { get; init; } = string.Empty;

    public string LifecycleMetadataReviewBatchSourcePath { get; init; } = string.Empty;
    public string LifecycleMetadataReviewBatchId { get; init; } = string.Empty;
    public string LifecycleMetadataReviewBatchStatus { get; init; } = string.Empty;
    public int LifecycleMetadataReviewBatchCandidateCount { get; init; }
    public int LifecycleMetadataReviewBatchValidationErrorCount { get; init; }
    public int LifecycleMetadataReviewBatchWouldWriteSidecarCount { get; init; }
    public int LifecycleMetadataReviewBatchUnsafeBlockedCount { get; init; }
    public string LifecycleMetadataReviewBatchRecommendation { get; init; } = string.Empty;

    public string LifecycleMetadataEvidenceBackfillSourcePath { get; init; } = string.Empty;
    public int LifecycleMetadataEvidenceBackfillCandidateCount { get; init; }
    public int LifecycleMetadataEvidenceFoundCount { get; init; }
    public int LifecycleMetadataSourceRefFoundCount { get; init; }
    public int LifecycleMetadataProvenanceFoundCount { get; init; }
    public int LifecycleMetadataAutoRepairableAfterBackfillCount { get; init; }
    public int LifecycleMetadataNeedsEvidenceAfterBackfillCount { get; init; }
    public string LifecycleMetadataEvidenceBackfillRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2GenerationSourcePath { get; init; } = string.Empty;
    public int RetrievalDatasetV2CorpusItemCount { get; init; }
    public int RetrievalDatasetV2SampleCount { get; init; }
    public int RetrievalDatasetV2ValidationIssueCount { get; init; }
    public int RetrievalDatasetV2MissingEvidenceCount { get; init; }
    public int RetrievalDatasetV2MissingProvenanceCount { get; init; }
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2DifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2SplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public string RetrievalDatasetV2Recommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2MaterializationSourcePath { get; init; } = string.Empty;
    public string RetrievalDatasetV2DatasetId { get; init; } = string.Empty;
    public string RetrievalDatasetV2CorpusHash { get; init; } = string.Empty;
    public string RetrievalDatasetV2SamplesHash { get; init; } = string.Empty;
    public bool RetrievalDatasetV2MaterializationGatePassed { get; init; }
    public bool RetrievalDatasetV2MaterializationCorpusHashStable { get; init; }
    public bool RetrievalDatasetV2MaterializationSamplesHashStable { get; init; }
    public string RetrievalDatasetV2MaterializationRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2ShadowEvalSourcePath { get; init; } = string.Empty;
    public string RetrievalDatasetV2ShadowEvalDatasetId { get; init; } = string.Empty;
    public string RetrievalDatasetV2ShadowEvalBestProfileName { get; init; } = string.Empty;
    public double RetrievalDatasetV2ShadowEvalBestRecallAfterPolicy { get; init; }
    public double RetrievalDatasetV2ShadowEvalBestMrrAfterPolicy { get; init; }
    public int RetrievalDatasetV2ShadowEvalBestRiskAfterPolicy { get; init; }
    public bool RetrievalDatasetV2ShadowEvalPgVectorParityPassed { get; init; }
    public string RetrievalDatasetV2ShadowEvalRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2StressSourcePath { get; init; } = string.Empty;
    public string RetrievalDatasetV2StressDatasetId { get; init; } = string.Empty;
    public int RetrievalDatasetV2StressCorpusItemCount { get; init; }
    public int RetrievalDatasetV2StressSampleCount { get; init; }
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2StressSplitBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2StressDifficultyBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int RetrievalDatasetV2StressLeakageIssueCount { get; init; }
    public double RetrievalDatasetV2StressAnchorDominanceScore { get; init; }
    public double RetrievalDatasetV2StressDenseRecall { get; init; }
    public double RetrievalDatasetV2StressLexicalRecall { get; init; }
    public double RetrievalDatasetV2StressAnchorRecall { get; init; }
    public double RetrievalDatasetV2StressHybridRecall { get; init; }
    public double RetrievalDatasetV2StressHoldoutHybridRecall { get; init; }
    public string RetrievalDatasetV2StressRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2StressTriageSourcePath { get; init; } = string.Empty;
    public int RetrievalDatasetV2StressFailureCount { get; init; }
    public int RetrievalDatasetV2StressHoldoutFailureCount { get; init; }
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2StressFailureCountBySplit { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2StressFailureCountByDifficulty { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2StressFailureCountByReason { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int RetrievalDatasetV2StressDenseOnlyWinCount { get; init; }
    public int RetrievalDatasetV2StressHybridWinCount { get; init; }
    public int RetrievalDatasetV2StressAnchorRegressionCount { get; init; }
    public string RetrievalDatasetV2StressProfileComparisonSummary { get; init; } = string.Empty;
    public string RetrievalDatasetV2StressTriageRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2HybridRepairSourcePath { get; init; } = string.Empty;
    public string RetrievalDatasetV2HybridRepairBestProfileName { get; init; } = string.Empty;
    public double RetrievalDatasetV2HybridRepairRecallAfterPolicy { get; init; }
    public double RetrievalDatasetV2HybridRepairHoldoutRecallAfterPolicy { get; init; }
    public int RetrievalDatasetV2HybridRepairDenseWinnerLostCount { get; init; }
    public int RetrievalDatasetV2HybridRepairMustHitBelowTopKCount { get; init; }
    public int RetrievalDatasetV2HybridRepairNegativeDistractorCount { get; init; }
    public int RetrievalDatasetV2HybridRepairRiskAfterPolicy { get; init; }
    public string RetrievalDatasetV2HybridRepairRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2HybridRiskTriageSourcePath { get; init; } = string.Empty;
    public string RetrievalDatasetV2HybridRiskTriageProfileName { get; init; } = string.Empty;
    public int RetrievalDatasetV2HybridRiskCandidateCount { get; init; }
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2HybridRiskByType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> RetrievalDatasetV2HybridRiskBySplit { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public int RetrievalDatasetV2HybridMustNotPromotedCount { get; init; }
    public int RetrievalDatasetV2HybridEligibilityBypassCount { get; init; }
    public int RetrievalDatasetV2HybridRiskProjectionMismatchCount { get; init; }
    public string RetrievalDatasetV2HybridRiskTriageRecommendation { get; init; } = string.Empty;

    public string RetrievalDatasetV2StressFreezeSourcePath { get; init; } = string.Empty;
    public bool RetrievalDatasetV2StressFreezePassed { get; init; }
    public string RetrievalDatasetV2StressFreezeStatus { get; init; } = string.Empty;
    public string RetrievalDatasetV2StressFreezeRecommendation { get; init; } = string.Empty;
    public string RetrievalDatasetV2StressFreezeBestProfile { get; init; } = string.Empty;
    public double RetrievalDatasetV2StressFreezeStressRecall { get; init; }
    public double RetrievalDatasetV2StressFreezeHoldoutRecall { get; init; }
    public int RetrievalDatasetV2StressFreezeRiskAfterPolicy { get; init; }
    public int RetrievalDatasetV2StressFreezeMustNotHitRiskAfterPolicy { get; init; }
    public int RetrievalDatasetV2StressFreezeLifecycleRiskAfterPolicy { get; init; }
    public int RetrievalDatasetV2StressFreezeFormalOutputChanged { get; init; }
    public int RetrievalDatasetV2StressFreezeLeakageIssueCount { get; init; }
    public double RetrievalDatasetV2StressFreezeAnchorDominanceScore { get; init; }
    public bool RetrievalDatasetV2StressFreezeV4RecheckAllowed { get; init; }
    public bool RetrievalDatasetV2StressFreezeReadyForFormalRetrieval { get; init; }
    public bool RetrievalDatasetV2StressFreezeFormalRetrievalAllowed { get; init; }
    public IReadOnlyList<string> RetrievalDatasetV2StressFreezeBlockedReasons { get; init; } = Array.Empty<string>();

    public string VectorV4ReadinessRecheckSourcePath { get; init; } = string.Empty;
    public bool VectorV4ReadinessRecheckPassed { get; init; }
    public string VectorV4ReadinessRecheckRecommendation { get; init; } = string.Empty;
    public string VectorV4ReadinessLegacyStatus { get; init; } = string.Empty;
    public string VectorV4ReadinessSmallStatus { get; init; } = string.Empty;
    public string VectorV4ReadinessStressStatus { get; init; } = string.Empty;
    public string VectorV4ReadinessPgVectorStatus { get; init; } = string.Empty;
    public string VectorV4ReadinessHybridScoringStatus { get; init; } = string.Empty;
    public string VectorV4ReadinessRuntimeGateStatus { get; init; } = string.Empty;
    public string VectorV4ReadinessBestProfile { get; init; } = string.Empty;
    public double VectorV4ReadinessStressRecall { get; init; }
    public double VectorV4ReadinessHoldoutRecall { get; init; }
    public int VectorV4ReadinessRiskAfterPolicy { get; init; }
    public int VectorV4ReadinessFormalOutputChanged { get; init; }
    public bool VectorV4ReadinessReadyForGuardedFormalPreview { get; init; }
    public bool VectorV4ReadinessReadyForRuntimeSwitch { get; init; }
    public bool VectorV4ReadinessFormalRetrievalAllowed { get; init; }
    public IReadOnlyList<string> VectorV4ReadinessBlockedReasons { get; init; } = Array.Empty<string>();


    public string VectorShadowPackageComparisonSourcePath { get; init; } = string.Empty;
    public bool VectorShadowPackageComparisonGatePassed { get; init; }
    public string VectorShadowPackageComparisonRecommendation { get; init; } = string.Empty;
    public string VectorShadowPackageComparisonProfileName { get; init; } = string.Empty;
    public int VectorShadowPackageCandidateAddCount { get; init; }
    public int VectorShadowPackageCandidateRemoveCount { get; init; }
    public int VectorShadowPackageCandidateUnchangedCount { get; init; }
    public int VectorShadowPackageSectionChangedCount { get; init; }
    public int VectorShadowPackageTokenDeltaTotal { get; init; }
    public int VectorShadowPackageTokenDeltaMax { get; init; }
    public double VectorShadowPackageConstraintCoverageDelta { get; init; }
    public double VectorShadowPackageRelationCoverageDelta { get; init; }
    public int VectorShadowPackageRiskAfterPolicy { get; init; }
    public int VectorShadowPackageMustNotHitRiskAfterPolicy { get; init; }
    public int VectorShadowPackageLifecycleRiskAfterPolicy { get; init; }
    public int VectorShadowPackageFormalOutputChanged { get; init; }
    public bool VectorShadowPackagePackageOutputChanged { get; init; }
    public bool VectorShadowPackagePackingPolicyChanged { get; init; }
    public bool VectorShadowPackageRuntimeMutated { get; init; }
    public bool VectorShadowPackageReadyForRuntimeSwitch { get; init; }
    public bool VectorShadowPackageFormalRetrievalAllowed { get; init; }
    public IReadOnlyList<string> VectorShadowPackageBlockedReasons { get; init; } = Array.Empty<string>();



    public string VectorFormalPreviewAllowedMode { get; init; } = string.Empty;
    public bool VectorFormalPreviewFormalRetrievalAllowed { get; init; }
    public bool VectorFormalPreviewReadyForRuntimeSwitch { get; init; }
    public bool VectorFormalPreviewUseForRuntime { get; init; }
    public bool VectorFormalPreviewRuntimeSwitchAllowed { get; init; }
    public int VectorFormalPreviewRiskAfterPolicy { get; init; }
    public int VectorFormalPreviewFormalOutputChanged { get; init; }
    public bool VectorFormalPreviewPackageOutputChanged { get; init; }
    public bool VectorFormalPreviewPackingPolicyChanged { get; init; }
    public bool VectorFormalPreviewFormalPackageWritten { get; init; }
    public bool VectorFormalPreviewRuntimeMutated { get; init; }
    public int VectorFormalPreviewScopeLeakCount { get; init; }
    public IReadOnlyList<string> VectorFormalPreviewForbiddenChanges { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> VectorFormalPreviewBlockedReasons { get; init; } = Array.Empty<string>();













    public string FormalRetrievalIntegrationPlanSourcePath { get; init; } = string.Empty;
    public bool FormalRetrievalIntegrationPlanPassed { get; init; }
    public string FormalRetrievalIntegrationPlanRecommendation { get; init; } = string.Empty;
    public string FormalRetrievalIntegrationPlanAllowedMode { get; init; } = string.Empty;
    public string FormalRetrievalIntegrationPlanRequiredNextPhase { get; init; } = string.Empty;
    public bool FormalRetrievalIntegrationPlanFormalRetrievalAllowed { get; init; }
    public bool FormalRetrievalIntegrationPlanRuntimeSwitchAllowed { get; init; }
    public bool FormalRetrievalIntegrationPlanReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> FormalRetrievalIntegrationPlanIntegrationPoints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FormalRetrievalIntegrationPlanBlockedReasons { get; init; } = Array.Empty<string>();

    public string FormalRetrievalIntegrationDecisionSourcePath { get; init; } = string.Empty;
    public bool FormalRetrievalIntegrationDecisionPassed { get; init; }
    public bool FormalRetrievalIntegrationDecisionGatePassed { get; init; }
    public string FormalRetrievalIntegrationDecisionRecommendation { get; init; } = string.Empty;
    public string FormalRetrievalIntegrationDecisionValue { get; init; } = string.Empty;
    public string FormalRetrievalIntegrationDecisionNextAllowedPhase { get; init; } = string.Empty;
    public bool FormalRetrievalIntegrationDecisionReadyForFreeze { get; init; }
    public bool FormalRetrievalIntegrationDecisionReadyForNoOpBindingPlan { get; init; }
    public bool FormalRetrievalIntegrationDecisionFormalRetrievalAllowed { get; init; }
    public bool FormalRetrievalIntegrationDecisionRuntimeSwitchAllowed { get; init; }
    public bool FormalRetrievalIntegrationDecisionReadyForRuntimeSwitch { get; init; }
    public int FormalRetrievalIntegrationDecisionRiskAfterPolicy { get; init; }
    public int FormalRetrievalIntegrationDecisionFormalOutputChanged { get; init; }
    public bool FormalRetrievalIntegrationDecisionPackageOutputChanged { get; init; }
    public bool FormalRetrievalIntegrationDecisionPackingPolicyChanged { get; init; }
    public bool FormalRetrievalIntegrationDecisionRuntimeMutated { get; init; }
    public bool FormalRetrievalIntegrationDecisionVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> FormalRetrievalIntegrationDecisionBlockedReasons { get; init; } = Array.Empty<string>();

    public string ShadowFormalRetrievalAdapterPlanSourcePath { get; init; } = string.Empty;
    public bool ShadowFormalRetrievalAdapterPlanPassed { get; init; }
    public string ShadowFormalRetrievalAdapterPlanRecommendation { get; init; } = string.Empty;
    public string ShadowFormalRetrievalAdapterPlanAllowedMode { get; init; } = string.Empty;
    public string ShadowFormalRetrievalAdapterPlanVectorProviderSource { get; init; } = string.Empty;
    public string ShadowFormalRetrievalAdapterPlanGraphCandidateSource { get; init; } = string.Empty;
    public bool ShadowFormalRetrievalAdapterPlanFormalRetrievalAllowed { get; init; }
    public bool ShadowFormalRetrievalAdapterPlanRuntimeSwitchAllowed { get; init; }
    public IReadOnlyList<string> ShadowFormalRetrievalAdapterPlanForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ShadowFormalRetrievalAdapterPlanBlockedReasons { get; init; } = Array.Empty<string>();

    public string ShadowFormalRetrievalAdapterSourcePath { get; init; } = string.Empty;
    public bool ShadowFormalRetrievalAdapterPassed { get; init; }
    public bool ShadowFormalRetrievalAdapterGatePassed { get; init; }
    public string ShadowFormalRetrievalAdapterRecommendation { get; init; } = string.Empty;
    public string ShadowFormalRetrievalAdapterAllowedMode { get; init; } = string.Empty;
    public string ShadowFormalRetrievalAdapterVectorProviderSource { get; init; } = string.Empty;
    public string ShadowFormalRetrievalAdapterGraphCandidateSource { get; init; } = string.Empty;
    public int ShadowFormalRetrievalAdapterSampleCount { get; init; }
    public int ShadowFormalRetrievalAdapterRiskAfterPolicy { get; init; }
    public int ShadowFormalRetrievalAdapterMustNotHitRiskAfterPolicy { get; init; }
    public int ShadowFormalRetrievalAdapterLifecycleRiskAfterPolicy { get; init; }
    public int ShadowFormalRetrievalAdapterFormalOutputChanged { get; init; }
    public bool ShadowFormalRetrievalAdapterFormalSelectedSetChanged { get; init; }
    public bool ShadowFormalRetrievalAdapterPackageOutputChanged { get; init; }
    public bool ShadowFormalRetrievalAdapterPackingPolicyChanged { get; init; }
    public bool ShadowFormalRetrievalAdapterRuntimeMutated { get; init; }
    public bool ShadowFormalRetrievalAdapterVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ShadowFormalRetrievalAdapterBlockedReasons { get; init; } = Array.Empty<string>();

    public string FormalAdapterPackageShadowComparisonSourcePath { get; init; } = string.Empty;
    public bool FormalAdapterPackageShadowComparisonPassed { get; init; }
    public bool FormalAdapterPackageShadowComparisonGatePassed { get; init; }
    public string FormalAdapterPackageShadowComparisonRecommendation { get; init; } = string.Empty;
    public string FormalAdapterPackageShadowComparisonAllowedMode { get; init; } = string.Empty;
    public int FormalAdapterPackageShadowComparisonSampleCount { get; init; }
    public int FormalAdapterPackageShadowComparisonRiskAfterPolicy { get; init; }
    public int FormalAdapterPackageShadowComparisonMustNotHitRiskAfterPolicy { get; init; }
    public int FormalAdapterPackageShadowComparisonLifecycleRiskAfterPolicy { get; init; }
    public int FormalAdapterPackageShadowComparisonTokenDeltaTotal { get; init; }
    public int FormalAdapterPackageShadowComparisonTokenDeltaMax { get; init; }
    public int FormalAdapterPackageShadowComparisonTokenDeltaBudgetTotal { get; init; }
    public int FormalAdapterPackageShadowComparisonTokenDeltaBudgetPerSample { get; init; }
    public int FormalAdapterPackageShadowComparisonFormalOutputChanged { get; init; }
    public bool FormalAdapterPackageShadowComparisonFormalSelectedSetChanged { get; init; }
    public bool FormalAdapterPackageShadowComparisonPackageOutputChanged { get; init; }
    public bool FormalAdapterPackageShadowComparisonPackingPolicyChanged { get; init; }
    public bool FormalAdapterPackageShadowComparisonRuntimeMutated { get; init; }
    public bool FormalAdapterPackageShadowComparisonVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> FormalAdapterPackageShadowComparisonBlockedReasons { get; init; } = Array.Empty<string>();

    public string GraphVectorRetrievalQualityAuditSourcePath { get; init; } = string.Empty;
    public bool GraphVectorRetrievalQualityAuditPassed { get; init; }
    public bool GraphVectorRetrievalQualityAuditGatePassed { get; init; }
    public string GraphVectorRetrievalQualityAuditRecommendation { get; init; } = string.Empty;
    public string GraphVectorRetrievalQualityAuditAllowedMode { get; init; } = string.Empty;
    public int GraphVectorRetrievalQualityAuditSampleCount { get; init; }
    public double GraphVectorRetrievalQualityAuditRecall { get; init; }
    public double GraphVectorRetrievalQualityAuditPrecision { get; init; }
    public double GraphVectorRetrievalQualityAuditMrr { get; init; }
    public int GraphVectorRetrievalQualityAuditGraphNoiseCount { get; init; }
    public int GraphVectorRetrievalQualityAuditVectorNoiseCount { get; init; }
    public int GraphVectorRetrievalQualityAuditRankingRegressionCount { get; init; }
    public int GraphVectorRetrievalQualityAuditMustHitBelowTopKCount { get; init; }
    public int GraphVectorRetrievalQualityAuditRiskAfterPolicy { get; init; }
    public int GraphVectorRetrievalQualityAuditMustNotHitRiskAfterPolicy { get; init; }
    public int GraphVectorRetrievalQualityAuditLifecycleRiskAfterPolicy { get; init; }
    public int GraphVectorRetrievalQualityAuditSectionMismatchCount { get; init; }
    public int GraphVectorRetrievalQualityAuditMetadataEvidenceGapCount { get; init; }
    public IReadOnlyList<string> GraphVectorRetrievalQualityAuditFailureClusterIds { get; init; } = Array.Empty<string>();
    public int GraphVectorRetrievalQualityAuditFormalOutputChanged { get; init; }
    public bool GraphVectorRetrievalQualityAuditFormalSelectedSetChanged { get; init; }
    public bool GraphVectorRetrievalQualityAuditPackageOutputChanged { get; init; }
    public bool GraphVectorRetrievalQualityAuditPackingPolicyChanged { get; init; }
    public bool GraphVectorRetrievalQualityAuditRuntimeMutated { get; init; }
    public bool GraphVectorRetrievalQualityAuditVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> GraphVectorRetrievalQualityAuditBlockedReasons { get; init; } = Array.Empty<string>();

    public string RetrievalQualityRepairPreviewSourcePath { get; init; } = string.Empty;
    public bool RetrievalQualityRepairPreviewPassed { get; init; }
    public bool RetrievalQualityRepairPreviewGatePassed { get; init; }
    public string RetrievalQualityRepairPreviewRecommendation { get; init; } = string.Empty;
    public string RetrievalQualityRepairPreviewAllowedMode { get; init; } = string.Empty;
    public string RetrievalQualityRepairPreviewBestProfileId { get; init; } = string.Empty;
    public double RetrievalQualityRepairPreviewBaselineRecall { get; init; }
    public double RetrievalQualityRepairPreviewBaselinePrecision { get; init; }
    public double RetrievalQualityRepairPreviewBaselineMrr { get; init; }
    public double RetrievalQualityRepairPreviewBestRecall { get; init; }
    public double RetrievalQualityRepairPreviewBestPrecision { get; init; }
    public double RetrievalQualityRepairPreviewBestMrr { get; init; }
    public double RetrievalQualityRepairPreviewRecallDelta { get; init; }
    public double RetrievalQualityRepairPreviewMrrDelta { get; init; }
    public int RetrievalQualityRepairPreviewMustHitBelowTopKBaseline { get; init; }
    public int RetrievalQualityRepairPreviewMustHitBelowTopKBest { get; init; }
    public int RetrievalQualityRepairPreviewProfileEvaluatedCount { get; init; }
    public int RetrievalQualityRepairPreviewRiskAfterPolicy { get; init; }
    public int RetrievalQualityRepairPreviewMustNotHitRiskAfterPolicy { get; init; }
    public int RetrievalQualityRepairPreviewLifecycleRiskAfterPolicy { get; init; }
    public int RetrievalQualityRepairPreviewSectionMismatchCount { get; init; }
    public int RetrievalQualityRepairPreviewGraphNoiseCount { get; init; }
    public int RetrievalQualityRepairPreviewRankingRegressionCount { get; init; }
    public int RetrievalQualityRepairPreviewTokenDeltaTotal { get; init; }
    public int RetrievalQualityRepairPreviewTokenDeltaMax { get; init; }
    public int RetrievalQualityRepairPreviewFormalOutputChanged { get; init; }
    public bool RetrievalQualityRepairPreviewFormalSelectedSetChanged { get; init; }
    public bool RetrievalQualityRepairPreviewPackageOutputChanged { get; init; }
    public bool RetrievalQualityRepairPreviewPackingPolicyChanged { get; init; }
    public bool RetrievalQualityRepairPreviewRuntimeMutated { get; init; }
    public bool RetrievalQualityRepairPreviewVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> RetrievalQualityRepairPreviewBlockedReasons { get; init; } = Array.Empty<string>();

    public string RuntimeObservableFeatureContractSourcePath { get; init; } = string.Empty;
    public bool RuntimeObservableFeatureContractPassed { get; init; }
    public bool RuntimeObservableFeatureContractGatePassed { get; init; }
    public string RuntimeObservableFeatureContractRecommendation { get; init; } = string.Empty;
    public string RuntimeObservableFeatureContractAllowedMode { get; init; } = string.Empty;
    public string RuntimeObservableFeatureContractBestProfileId { get; init; } = string.Empty;
    public string RuntimeObservableFeatureContractBestProfileContractStatus { get; init; } = string.Empty;
    public int RuntimeObservableFeatureContractForbiddenForScoringCount { get; init; }
    public int RuntimeObservableFeatureContractEvalOnlyCount { get; init; }
    public int RuntimeObservableFeatureContractDerivedAtRuntimeCount { get; init; }
    public int RuntimeObservableFeatureContractRuntimeObservableCount { get; init; }
    public int RuntimeObservableFeatureContractScoringFeatureCount { get; init; }
    public int RuntimeObservableFeatureContractFilteringFeatureCount { get; init; }
    public int RuntimeObservableFeatureContractCandidateExpansionFeatureCount { get; init; }
    public int RuntimeObservableFeatureContractSourceScanFiles { get; init; }
    public int RuntimeObservableFeatureContractFixtureTokenHitCount { get; init; }
    public IReadOnlyList<string> RuntimeObservableFeatureContractFlaggedTokens { get; init; } = Array.Empty<string>();
    public int RuntimeObservableFeatureContractFormalOutputChanged { get; init; }
    public bool RuntimeObservableFeatureContractFormalSelectedSetChanged { get; init; }
    public bool RuntimeObservableFeatureContractPackageOutputChanged { get; init; }
    public bool RuntimeObservableFeatureContractPackingPolicyChanged { get; init; }
    public bool RuntimeObservableFeatureContractRuntimeMutated { get; init; }
    public bool RuntimeObservableFeatureContractVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> RuntimeObservableFeatureContractBlockedReasons { get; init; } = Array.Empty<string>();

    public string RuntimeRetrievalFeatureDerivationSourcePath { get; init; } = string.Empty;
    public bool RuntimeRetrievalFeatureDerivationPassed { get; init; }
    public bool RuntimeRetrievalFeatureDerivationGatePassed { get; init; }
    public string RuntimeRetrievalFeatureDerivationRecommendation { get; init; } = string.Empty;
    public string RuntimeRetrievalFeatureDerivationAllowedMode { get; init; } = string.Empty;
    public int RuntimeRetrievalFeatureDerivationSampleCount { get; init; }
    public double RuntimeRetrievalFeatureDerivationTargetSectionMatchRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationRequiredRelationCoverageRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationEvidenceAnchorCoverageRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationSourceAnchorCoverageRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationDerivationCompletenessRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationBaselineRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationBaselineMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationDerivedRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationDerivedMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationEvalDrivenRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationEvalDrivenMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationDerivedRecallDelta { get; init; }
    public double RuntimeRetrievalFeatureDerivationDerivedMrrDelta { get; init; }
    public int RuntimeRetrievalFeatureDerivationDerivedRiskAfterPolicy { get; init; }
    public int RuntimeRetrievalFeatureDerivationDerivedMustNotHitRiskAfterPolicy { get; init; }
    public int RuntimeRetrievalFeatureDerivationDerivedLifecycleRiskAfterPolicy { get; init; }
    public int RuntimeRetrievalFeatureDerivationDerivedSectionMismatchCount { get; init; }
    public int RuntimeRetrievalFeatureDerivationForbiddenSampleAnnotationReadCount { get; init; }
    public int RuntimeRetrievalFeatureDerivationSourceScanFiles { get; init; }
    public int RuntimeRetrievalFeatureDerivationFixtureTokenHitCount { get; init; }
    public int RuntimeRetrievalFeatureDerivationFormalOutputChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationFormalSelectedSetChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationPackageOutputChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationPackingPolicyChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRuntimeMutated { get; init; }
    public bool RuntimeRetrievalFeatureDerivationVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> RuntimeRetrievalFeatureDerivationBlockedReasons { get; init; } = Array.Empty<string>();

    public string RuntimeRetrievalFeatureDerivationRepairSourcePath { get; init; } = string.Empty;
    public bool RuntimeRetrievalFeatureDerivationRepairPassed { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRepairGatePassed { get; init; }
    public string RuntimeRetrievalFeatureDerivationRepairRecommendation { get; init; } = string.Empty;
    public string RuntimeRetrievalFeatureDerivationRepairAllowedMode { get; init; } = string.Empty;
    public int RuntimeRetrievalFeatureDerivationRepairTrainSampleCount { get; init; }
    public int RuntimeRetrievalFeatureDerivationRepairHoldoutSampleCount { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairTrainBaselineRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairTrainBaselineMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairTrainDerivedRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairTrainDerivedMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedRecall { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedMrr { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairCanonicalRelationCoverageRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairCanonicalEvidenceCoverageRate { get; init; }
    public double RuntimeRetrievalFeatureDerivationRepairCanonicalSourceCoverageRate { get; init; }
    public int RuntimeRetrievalFeatureDerivationRepairDerivedRiskAfterPolicy { get; init; }
    public int RuntimeRetrievalFeatureDerivationRepairForbiddenSampleAnnotationReadCount { get; init; }
    public int RuntimeRetrievalFeatureDerivationRepairSourceScanFiles { get; init; }
    public int RuntimeRetrievalFeatureDerivationRepairFixtureTokenHitCount { get; init; }
    public int RuntimeRetrievalFeatureDerivationRepairFormalOutputChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRepairFormalSelectedSetChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRepairPackageOutputChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRepairPackingPolicyChanged { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRepairRuntimeMutated { get; init; }
    public bool RuntimeRetrievalFeatureDerivationRepairVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> RuntimeRetrievalFeatureDerivationRepairBlockedReasons { get; init; } = Array.Empty<string>();

    public string FeatureDerivationFailureFreezeSourcePath { get; init; } = string.Empty;
    public bool FeatureDerivationFailureFreezePassed { get; init; }
    public string FeatureDerivationFailureFreezeStatus { get; init; } = string.Empty;
    public string FeatureDerivationFailureFreezeRecommendation { get; init; } = string.Empty;
    public bool FeatureDerivationFailureFreezeCanonicalResolverReusable { get; init; }
    public bool FeatureDerivationFailureFreezeRelationDeriverReady { get; init; }
    public IReadOnlyList<string> FeatureDerivationFailureFreezeDisabledCapabilities { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FeatureDerivationFailureFreezeRecommendedNextPhases { get; init; } = Array.Empty<string>();

    public string GraphHubNoiseControlSourcePath { get; init; } = string.Empty;
    public bool GraphHubNoiseControlPassed { get; init; }
    public bool GraphHubNoiseControlGatePassed { get; init; }
    public string GraphHubNoiseControlRecommendation { get; init; } = string.Empty;
    public int GraphHubNoiseControlHubItemCount { get; init; }
    public double GraphHubNoiseControlAvgDominance { get; init; }
    public double GraphHubNoiseControlBaselineRecall { get; init; }
    public double GraphHubNoiseControlHubCtrlRecall { get; init; }
    public double GraphHubNoiseControlRecallDelta { get; init; }

    public string RetrievalEvalProtocolGateSourcePath { get; init; } = string.Empty;
    public string RetrievalEvalProtocolSourceAuditPath { get; init; } = string.Empty;
    public bool RetrievalEvalProtocolGatePassed { get; init; }
    public string RetrievalEvalProtocolRecommendation { get; init; } = string.Empty;
    public string RetrievalEvalProtocolVersion { get; init; } = string.Empty;
    public int RetrievalEvalProtocolVectorTopK { get; init; }
    public int RetrievalEvalProtocolMergedTopK { get; init; }
    public int RetrievalEvalProtocolFinalTopK { get; init; }
    public int RetrievalEvalProtocolHashOrderSensitivityCount { get; init; }
    public bool RetrievalEvalProtocolTieBreakDeterministic { get; init; }
    public bool RetrievalEvalProtocolSourceNonDiscriminativeDetected { get; init; }
    public bool RetrievalEvalProtocolTemplateHomogeneityDetected { get; init; }
    public bool RetrievalEvalProtocolRuntimeChangeGatePassed { get; init; }
    public int RetrievalEvalProtocolRiskAfterPolicy { get; init; }
    public int RetrievalEvalProtocolMustNotHitRiskAfterPolicy { get; init; }
    public int RetrievalEvalProtocolLifecycleRiskAfterPolicy { get; init; }
    public int RetrievalEvalProtocolNonDiscriminativeSourceCount { get; init; }
    public double RetrievalEvalProtocolTemplateHomogeneityScore { get; init; }
    public double RetrievalEvalProtocolBaselineRecall { get; init; }
    public double RetrievalEvalProtocolMergedRecall { get; init; }
    public IReadOnlyList<string> RetrievalEvalProtocolBlockedReasons { get; init; } = Array.Empty<string>();

    public string InputMetadataEnrichmentSourcePath { get; init; } = string.Empty;
    public bool InputMetadataEnrichmentPreviewPassed { get; init; }
    public bool InputMetadataEnrichmentGatePassed { get; init; }
    public string InputMetadataEnrichmentRecommendation { get; init; } = string.Empty;
    public int InputMetadataEnrichmentCoverageDelta { get; init; }
    public double InputMetadataEnrichmentBeforeRecall { get; init; }
    public double InputMetadataEnrichmentAfterRecall { get; init; }
    public int InputMetadataEnrichmentIndependentNonDenseSourceCount { get; init; }
    public int InputMetadataEnrichmentRiskAfterPolicy { get; init; }
    public int InputMetadataEnrichmentMustNotHitRiskAfterPolicy { get; init; }
    public int InputMetadataEnrichmentLifecycleRiskAfterPolicy { get; init; }
    public bool InputMetadataEnrichmentPackageOutputChanged { get; init; }
    public bool InputMetadataEnrichmentPackingPolicyChanged { get; init; }
    public bool InputMetadataEnrichmentRuntimeMutated { get; init; }
    public bool InputMetadataEnrichmentVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> InputMetadataEnrichmentBlockedReasons { get; init; } = Array.Empty<string>();

    public string EnrichedCandidateSourceRepairRecheckSourcePath { get; init; } = string.Empty;
    public bool EnrichedCandidateSourceRepairRecheckPassed { get; init; }
    public bool EnrichedCandidateSourceRepairRecheckGatePassed { get; init; }
    public string EnrichedCandidateSourceRepairRecheckRecommendation { get; init; } = string.Empty;
    public bool EnrichedCandidateSourceRepairQualityImproved { get; init; }
    public double EnrichedCandidateSourceRepairTrainRecallDelta { get; init; }
    public double EnrichedCandidateSourceRepairHoldoutRecallDelta { get; init; }
    public int EnrichedCandidateSourceRepairMustHitBelowTopKDelta { get; init; }
    public int EnrichedCandidateSourceRepairRiskAfterPolicy { get; init; }
    public bool EnrichedCandidateSourceRepairPackageOutputChanged { get; init; }
    public bool EnrichedCandidateSourceRepairPackingPolicyChanged { get; init; }
    public bool EnrichedCandidateSourceRepairRuntimeMutated { get; init; }
    public bool EnrichedCandidateSourceRepairVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> EnrichedCandidateSourceRepairBlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EnrichedCandidateSourceRepairQualityBlockedReasons { get; init; } = Array.Empty<string>();

    public string SourceAwareRankingRepairSourcePath { get; init; } = string.Empty;
    public bool SourceAwareRankingRepairPassed { get; init; }
    public bool SourceAwareRankingRepairGatePassed { get; init; }
    public string SourceAwareRankingRepairRecommendation { get; init; } = string.Empty;
    public string SourceAwareRankingRepairSelectedProfileId { get; init; } = string.Empty;
    public double SourceAwareRankingRepairTrainDevRecallDelta { get; init; }
    public double SourceAwareRankingRepairTestRecallDelta { get; init; }
    public double SourceAwareRankingRepairHoldoutRecallDelta { get; init; }
    public double SourceAwareRankingRepairBlindHoldoutRecallDelta { get; init; }
    public int SourceAwareRankingRepairDenseWinnerLostCount { get; init; }
    public int SourceAwareRankingRepairUniqueSourceRecoveryCount { get; init; }
    public int SourceAwareRankingRepairSourceNoiseCount { get; init; }
    public double SourceAwareRankingRepairFallbackRate { get; init; }
    public int SourceAwareRankingRepairRiskAfterPolicy { get; init; }
    public bool SourceAwareRankingRepairPackageOutputChanged { get; init; }
    public bool SourceAwareRankingRepairPackingPolicyChanged { get; init; }
    public bool SourceAwareRankingRepairRuntimeMutated { get; init; }
    public bool SourceAwareRankingRepairVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> SourceAwareRankingRepairBlockedReasons { get; init; } = Array.Empty<string>();

    public string OutputTokenPriorityShadowSourcePath { get; init; } = string.Empty;
    public bool OutputTokenPriorityShadowPassed { get; init; }
    public bool OutputTokenPriorityShadowGatePassed { get; init; }
    public string OutputTokenPriorityShadowRecommendation { get; init; } = string.Empty;
    public string OutputTokenPriorityShadowProfileName { get; init; } = string.Empty;
    public int OutputTokenPriorityShadowTokenDeltaTotal { get; init; }
    public int OutputTokenPriorityShadowTokenDeltaMax { get; init; }
    public int OutputTokenPriorityShadowTokenDeltaP95 { get; init; }
    public int OutputTokenPriorityShadowTokenBudgetExceededCount { get; init; }
    public int OutputTokenPriorityShadowPriorityInversionCount { get; init; }
    public int OutputTokenPriorityShadowDroppedRequiredCandidateCount { get; init; }
    public int OutputTokenPriorityShadowSectionMismatchCount { get; init; }
    public int OutputTokenPriorityShadowRiskAfterPolicy { get; init; }
    public bool OutputTokenPriorityShadowFormalSelectedSetChanged { get; init; }
    public bool OutputTokenPriorityShadowPackageOutputChanged { get; init; }
    public bool OutputTokenPriorityShadowPackingPolicyChanged { get; init; }
    public bool OutputTokenPriorityShadowRuntimeMutated { get; init; }
    public bool OutputTokenPriorityShadowVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> OutputTokenPriorityShadowBlockedReasons { get; init; } = Array.Empty<string>();

    public string FormalAdapterInputContractSourcePath { get; init; } = string.Empty;
    public bool FormalAdapterInputContractPassed { get; init; }
    public bool FormalAdapterInputContractGatePassed { get; init; }
    public string FormalAdapterInputContractRecommendation { get; init; } = string.Empty;
    public string FormalAdapterInputContractVersion { get; init; } = string.Empty;
    public int FormalAdapterInputContractRuntimeInputFieldCount { get; init; }
    public int FormalAdapterInputContractDeniedFieldCount { get; init; }
    public int FormalAdapterInputContractForbiddenPropertyCount { get; init; }
    public int FormalAdapterInputContractFormalSourceForbiddenReadCount { get; init; }
    public int FormalAdapterInputContractEvalOnlyForbiddenReadCount { get; init; }
    public bool FormalAdapterInputContractDatasetEvalFieldsBlocked { get; init; }
    public bool FormalAdapterInputContractGoldLabelsBlocked { get; init; }
    public bool FormalAdapterInputContractSampleMetadataBlocked { get; init; }
    public bool FormalAdapterInputContractShadowArtifactFieldsBlocked { get; init; }
    public bool FormalAdapterInputContractFormalRetrievalAllowed { get; init; }
    public bool FormalAdapterInputContractRuntimeSwitchAllowed { get; init; }
    public bool FormalAdapterInputContractRuntimeMutated { get; init; }
    public bool FormalAdapterInputContractPackageOutputChanged { get; init; }
    public bool FormalAdapterInputContractPackingPolicyChanged { get; init; }
    public bool FormalAdapterInputContractVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> FormalAdapterInputContractBlockedReasons { get; init; } = Array.Empty<string>();
    public string ShadowMergeNextAllowedPhase { get; init; } = string.Empty;
    public int ShadowMergeObservationRunCount { get; init; }
    public int ShadowMergeSampleObservationCount { get; init; }
    public bool ShadowMergeDeterministicPreviewStable { get; init; }
    public int ShadowMergePreviewAddCountMin { get; init; }
    public int ShadowMergePreviewAddCountMax { get; init; }
    public int ShadowMergePreviewRemoveCountMin { get; init; }
    public int ShadowMergePreviewRemoveCountMax { get; init; }
    public int ShadowMergeAppliedAddCountMax { get; init; }
    public int ShadowMergeAppliedRemoveCountMax { get; init; }
    public int ShadowMergeRiskAfterPolicyMax { get; init; }
    public int ShadowMergeTokenDeltaTotalMax { get; init; }
    public int ShadowMergePriorityInversionCountTotal { get; init; }
    public int ShadowMergeSectionMismatchCountTotal { get; init; }
    public int ShadowMergeFormalOutputChangedMax { get; init; }
    public bool ShadowMergePackageOutputChanged { get; init; }
    public bool ShadowMergePackingPolicyChanged { get; init; }
    public bool ShadowMergeRuntimeMutated { get; init; }
    public bool ShadowMergeVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ShadowMergeBlockedReasons { get; init; } = Array.Empty<string>();

    public string FormalRetrievalIntegrationFreezeSourcePath { get; init; } = string.Empty;
    public bool FormalRetrievalIntegrationFreezePassed { get; init; }
    public string FormalRetrievalIntegrationFreezeRecommendation { get; init; } = string.Empty;
    public string FormalRetrievalIntegrationFreezeSelectedProfile { get; init; } = string.Empty;
    public int FormalRetrievalIntegrationFreezeFrozenArtifactCount { get; init; }
}

public sealed class ServicePlanningSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ContextPlanningSnapshot Snapshot { get; init; } = new();
}

public sealed class ServicePlanningProposalSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string CurrentInput { get; init; } = string.Empty;

    public RetrievalPlanProposal Proposal { get; init; } = new();
}

public sealed class ServiceRankerShadowDebugSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public LifecycleAwareRankerShadowDebugResponse Response { get; init; } = new();

    public RankerShadowTraceQualityReport TraceQualitySummary { get; init; } = new();

    public IReadOnlyList<LifecycleAwareRankerShadowTraceRecord> RecentShadowTraces { get; init; } =
        Array.Empty<LifecycleAwareRankerShadowTraceRecord>();
}

/// <summary>Service 模式下的运行时仪表盘快照。</summary>
public sealed class ServiceDashboardSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public RuntimeSnapshotResponse Snapshot { get; init; } = new();
}

public sealed class ServiceJobsSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public IReadOnlyList<ContextJob> Jobs { get; init; } = Array.Empty<ContextJob>();
}

public sealed class ServiceModelSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ContextCoreModelStatusResponse ModelStatus { get; init; } = new();

    public ContextCoreModelRouteResolveResponse? RouteResolution { get; init; }
}

public sealed class ServiceAdminRuntimeSnapshot
{
    public DateTimeOffset CurrentTime { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public RuntimeSnapshotResponse Runtime { get; init; } = new();

    public ContextCoreAdminStatusResponse AdminStatus { get; init; } = new();

    public ContextCoreBackupStatusResponse BackupStatus { get; init; } = new();

    public ContextCoreBackupValidateResponse BackupValidate { get; init; } = new();

    public FileLayoutStatus FileLayoutStatus { get; init; } = new();

    public MemoryLayoutDiagnostics MemoryLayoutDiagnostics { get; init; } = new();

    public TraceLayoutDiagnostics TraceLayoutDiagnostics { get; init; } = new();

    public ReportLayoutDiagnostics ReportLayoutDiagnostics { get; init; } = new();

    public StorageBoundaryReport StorageBoundaryReport { get; init; } = new();

    public PostgresOperationalStoreDiagnostics PostgresOperationalStoreDiagnostics { get; init; } = new();

    public PostgresRelationStoreDiagnostics PostgresRelationStoreDiagnostics { get; init; } = new();

    public PostgresRelationReviewProviderDiagnostics PostgresRelationReviewProviderDiagnostics { get; init; } = new();

    public PostgresRelationReviewParityReport PostgresRelationReviewParityReport { get; init; } = new();

    public PostgresRelationGovernanceParityReport PostgresRelationGovernanceParityReport { get; init; } = new();

    public PostgresRelationGovernanceReadinessGateReport PostgresRelationGovernanceReadinessGateReport { get; init; } = new();

    public PostgresRelationDualWriteQualityReport PostgresRelationDualWriteQualityReport { get; init; } = new();

    public PostgresRelationShadowReadQualityReport PostgresRelationShadowReadQualityReport { get; init; } = new();

    public PostgresRelationProviderSwitchSmokeReport PostgresRelationProviderSwitchSmokeReport { get; init; } = new();

    public PostgresRelationProviderSwitchGateReport PostgresRelationProviderSwitchGateReport { get; init; } = new();

    public PostgresRelationRuntimeCanaryReport PostgresRelationRuntimeCanaryReport { get; init; } = new();

    public PostgresRelationScopedServiceModeSmokeReport PostgresRelationScopedServiceModeSmokeReport { get; init; } = new();

    public PostgresRelationScopedServiceModeGateReport PostgresRelationScopedServiceModeGateReport { get; init; } = new();

    public PostgresRelationScopedExtendedCanaryReport PostgresRelationScopedExtendedCanaryReport { get; init; } = new();

    public PostgresRelationSelectedWorkspaceCanaryReport PostgresRelationSelectedWorkspaceCanaryReport { get; init; } = new();

    public PostgresRelationScopedExpansionReport PostgresRelationScopedExpansionReport { get; init; } = new();

    public PostgresRelationScopedObservationReport PostgresRelationScopedObservationReport { get; init; } = new();

    public PostgresRelationSelectedNormalWorkspaceCanaryReport PostgresRelationSelectedNormalWorkspaceCanaryReport { get; init; } = new();

    public PostgresRelationLimitedNormalScopeObservationReport PostgresRelationLimitedNormalScopeObservationReport { get; init; } = new();

    public PostgresRelationMultiNormalScopeCanaryReport PostgresRelationMultiNormalScopeCanaryReport { get; init; } = new();

    public LearningFeedbackDualWriteSmokeReport PostgresLearningFeedbackDualWriteSmokeReport { get; init; } = new();

    public LearningFeedbackShadowReadSmokeReport PostgresLearningFeedbackShadowReadSmokeReport { get; init; } = new();

    public LearningFeedbackScopedServiceModeSmokeReport PostgresLearningFeedbackScopedServiceModeSmokeReport { get; init; } = new();

    public LearningFeedbackScopedServiceModeGateReport PostgresLearningFeedbackScopedServiceModeGateReport { get; init; } = new();

    public LearningFeedbackSelectedNormalScopeCanaryReport PostgresLearningFeedbackSelectedNormalScopeCanaryReport { get; init; } = new();

    public LearningFeedbackLimitedScopeObservationReport PostgresLearningFeedbackLimitedScopeObservationReport { get; init; } = new();

    public LearningFeedbackLimitedScopeQualityReport PostgresLearningFeedbackLimitedScopeQualityReport { get; init; } = new();

    public LearningFeedbackPostgresFreezeGateReport PostgresLearningFeedbackFreezeGateReport { get; init; } = new();

    public PostgresJobQueueLeaseSmokeReport PostgresJobQueueLeaseSmokeReport { get; init; } = new();

    public PostgresJobQueueDualWriteSmokeReport PostgresJobQueueDualWriteSmokeReport { get; init; } = new();

    public PostgresJobQueueShadowReadSmokeReport PostgresJobQueueShadowReadSmokeReport { get; init; } = new();

    public PostgresJobQueueScopedWorkerCanaryReport PostgresJobQueueScopedWorkerCanaryReport { get; init; } = new();

    public PostgresJobQueueScopedWorkerQualityReport PostgresJobQueueScopedWorkerQualityReport { get; init; } = new();

    public PostgresJobQueueLimitedWorkerScopeObservationReport PostgresJobQueueLimitedWorkerScopeObservationReport { get; init; } = new();

    public PostgresJobQueueLimitedWorkerScopeQualityReport PostgresJobQueueLimitedWorkerScopeQualityReport { get; init; } = new();

    public JobQueuePostgresFreezeGateReport PostgresJobQueueFreezeGateReport { get; init; } = new();

    public PostgresVectorDiagnosticsReport PostgresVectorDiagnosticsReport { get; init; } = new();

    public PostgresVectorCompatibilityReport PostgresVectorCompatibilityReport { get; init; } = new();

    public PostgresVectorProviderSmokeReport PostgresVectorProviderSmokeReport { get; init; } = new();

    public PostgresVectorIndexParityReport PostgresVectorIndexParityReport { get; init; } = new();

    public PostgresVectorProviderScopedReindexPlan PostgresVectorProviderScopedReindexPlan { get; init; } = new();

    public PostgresVectorProviderScopedReindexResult PostgresVectorProviderScopedReindexResult { get; init; } = new();

    public PostgresVectorProviderScopedReindexReport PostgresVectorProviderScopedReindexReport { get; init; } = new();

    public PostgresVectorQueryPreviewReport PostgresVectorQueryPreviewReport { get; init; } = new();

    public PostgresVectorShadowEvalReport PostgresVectorShadowEvalA3Report { get; init; } = new();

    public PostgresVectorShadowEvalReport PostgresVectorShadowEvalExtendedReport { get; init; } = new();

    public PostgresVectorShadowEvalSummaryReport PostgresVectorShadowEvalSummaryReport { get; init; } = new();

    public VectorPostgresProviderFreezeGateReport PostgresVectorFreezeGateReport { get; init; } = new();
}

/// <summary>仪表盘上的单项健康状态。</summary>
public sealed class SystemHealthItem
{
    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;
}

/// <summary>按上下文层级汇总的数量信息。</summary>
public sealed class MemoryLayerSummary
{
    public int RawItems { get; init; }

    public int WorkingMemory { get; init; }

    public int CandidateMemory { get; init; }

    public int StableMemory { get; init; }

    public int GlobalItems { get; init; }

    public int Constraints { get; init; }

    public int Relations { get; init; }

    public int IndexEntries { get; init; }

    public int Packages { get; init; }
}

/// <summary>记忆条目的层级与生命周期状态计数，用于 Memory Layers 页面。</summary>
public sealed class MemoryStatusBreakdown
{
    public int Total { get; init; }

    public int WorkingLayer { get; init; }

    public int StructuredLayer { get; init; }

    public int StableLayer { get; init; }

    public int Candidate { get; init; }

    public int Verified { get; init; }

    public int Stable { get; init; }

    public int Deprecated { get; init; }

    public int Rejected { get; init; }
}

/// <summary>最近一次运行时操作或后台任务事件的摘要。</summary>
public sealed class RecentOperation
{
    public DateTimeOffset Time { get; init; }

    public string OperationName { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;

    public TimeSpan? Duration { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>后台作业状态计数摘要。</summary>
public sealed class JobsSummary
{
    public int Queued { get; init; }

    public int Running { get; init; }

    public int WaitingRetry { get; init; }

    public int Failed { get; init; }

    public int Succeeded { get; init; }

    public int RequiresReview { get; init; }
}

/// <summary>最近一次构建出的 ContextPackage 摘要。</summary>
public sealed class PackageSummary
{
    public string PackageId { get; init; } = string.Empty;

    public int SectionCount { get; init; }

    public int EstimatedTokens { get; init; }

    public string? TokenBudget { get; init; }

    public string TokenEstimateSource { get; init; } = string.Empty;

    public string TokenEstimateModel { get; init; } = string.Empty;

    public bool TokenEstimateIsFallback { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static PackageSummary FromPackage(ContextPackage package)
    {
        var tokenEstimateSource = package.Metadata.TryGetValue(ContextTokenizationMetadataKeys.Source, out var source)
            ? source
            : string.Empty;
        var tokenEstimateModel = package.Metadata.TryGetValue(ContextTokenizationMetadataKeys.Model, out var model)
            ? model
            : string.Empty;
        var tokenEstimateIsFallback = package.Metadata.TryGetValue(ContextTokenizationMetadataKeys.IsFallback, out var isFallback)
            && bool.TryParse(isFallback, out var parsedFallback)
            && parsedFallback;

        return new PackageSummary
        {
            PackageId = package.PackageId,
            SectionCount = package.Sections.Count,
            EstimatedTokens = package.EstimatedTokens,
            TokenBudget = package.Metadata.TryGetValue("tokenBudget", out var tokenBudget) ? tokenBudget : null,
            TokenEstimateSource = tokenEstimateSource,
            TokenEstimateModel = tokenEstimateModel,
            TokenEstimateIsFallback = tokenEstimateIsFallback,
            CreatedAt = package.CreatedAt
        };
    }
}

/// <summary>列表页中展示的一条统一条目，可来自 raw、memory、constraint 或 relation。</summary>
public sealed class ControlRoomListItem
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Layer { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public string Preview { get; init; } = string.Empty;
}

/// <summary>详情页使用的统一模型，保留字段、元数据、关系和正文。</summary>
public sealed class ControlRoomDetail
{
    public string Title { get; init; } = string.Empty;

    public Dictionary<string, string> Fields { get; init; } = new();

    public Dictionary<string, string> Metadata { get; init; } = new();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ContextRelation> Relations { get; init; } = Array.Empty<ContextRelation>();

    public string Content { get; init; } = string.Empty;
}

/// <summary>围绕一个条目的上下游关系图。</summary>
public sealed class RelationGraph
{
    public string Id { get; init; } = string.Empty;

    public IReadOnlyList<ContextRelation> Upstream { get; init; } = Array.Empty<ContextRelation>();

    public IReadOnlyList<ContextRelation> Downstream { get; init; } = Array.Empty<ContextRelation>();
}

/// <summary>索引命中项及其引用的上下文条目。</summary>
public sealed class IndexSearchResult
{
    public ContextIndexEntry Entry { get; init; } = new();

    public IReadOnlyList<ContextItem> Items { get; init; } = Array.Empty<ContextItem>();
}

/// <summary>检索调试详情，包含检索结果、由选中项组成的最终包和最近 trace。</summary>
public sealed class RetrievalDebugDetails
{
    public ContextRetrievalResult Result { get; init; } = new();

    public ContextPackage Package { get; init; } = new();

    public IReadOnlyList<ContextRetrievalTrace> RecentTraces { get; init; } = Array.Empty<ContextRetrievalTrace>();
}

/// <summary>模型网关状态页的数据模型。</summary>
public sealed class ControlRoomModelStatus
{
    public ModelGatewayOptions Options { get; init; } = new();

    public IReadOnlyList<ModelEndpointConfigurationStatus> Configuration { get; init; } = Array.Empty<ModelEndpointConfigurationStatus>();

    public IReadOnlyList<ModelHealthResult> Health { get; init; } = Array.Empty<ModelHealthResult>();

    public IReadOnlyList<ModelUsageLog> UsageLogs { get; init; } = Array.Empty<ModelUsageLog>();

    public int FallbackCount { get; init; }
}

/// <summary>包预览详情，包含最终包以及被选中/被丢弃的候选条目。</summary>
public sealed class PackagePreviewDetails
{
    public ContextPackage Package { get; init; } = new();

    public IReadOnlyList<PackageCandidateItem> SelectedItems { get; init; } = Array.Empty<PackageCandidateItem>();

    public IReadOnlyList<PackageCandidateItem> DroppedItems { get; init; } = Array.Empty<PackageCandidateItem>();

    public IReadOnlyList<ContextPackageUncertainty> Uncertainties { get; init; } = Array.Empty<ContextPackageUncertainty>();

    public ContextPackageBudgetReport Budget { get; init; } = new();

    public AttentionRerankComparisonReport AttentionRerankComparison { get; init; } = new();

    public IReadOnlyDictionary<string, string> PlanningMetadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>参与打包候选池的一条上下文来源。</summary>
public sealed class PackageCandidateItem
{
    public string Id { get; init; } = string.Empty;

    public string Kind { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string SectionName { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public double Score { get; init; }

    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    public int EstimatedTokens { get; init; }

    public static PackageCandidateItem FromDecision(ContextPackageDecision decision)
    {
        return new PackageCandidateItem
        {
            Id = decision.ItemId,
            Kind = decision.Kind,
            Type = decision.Type,
            SectionName = decision.SectionName,
            Reason = decision.Reason,
            Score = decision.Score,
            SourceRefs = decision.SourceRefs,
            EstimatedTokens = decision.EstimatedTokens
        };
    }

    public static PackageCandidateItem FromDropped(DroppedContextItem item)
    {
        return new PackageCandidateItem
        {
            Id = item.ItemId,
            Kind = item.Kind,
            Type = item.Type,
            Reason = item.Reason,
            Score = item.Score,
            SourceRefs = item.SourceRefs,
            EstimatedTokens = item.EstimatedTokens
        };
    }
}




