using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Runtime;
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
                LearningStore = new InMemoryContextLearningStore()
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
            LearningStore = new InMemoryContextLearningStore()
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

    public IReadOnlyList<OperationalReportSnapshot> OperationalReports { get; init; } =
        Array.Empty<OperationalReportSnapshot>();

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




