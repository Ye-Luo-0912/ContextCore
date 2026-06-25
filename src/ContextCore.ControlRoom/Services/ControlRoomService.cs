using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
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
public sealed class ControlRoomService
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
        GraphExpansionApplyOptions? graphExpansionApplyOptions = null)
    {
        if (mode == ControlRoomMode.Service)
        {
            return CreateServiceState(workspaceId, collectionId, serviceBaseUrl, serviceHttpClient);
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
            var memoryArtifactStore = new FileArtifactStore(new FileStorageOptions
            {
                RootPath = Path.Combine(resolvedRootPath, "memory-artifacts")
            });
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
            var planningSnapshotService = new PlanningSnapshotService(
                new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
                memoryStore,
                constraintStore,
                new InMemoryContextLearningStore());
            var planningSafetyProfile = RetrievalPlanSafetyProfile.CreateDefault();
            var planningProposalService = new RetrievalPlanProposalService(
                planningSnapshotService,
                new PlanningIntentDetector(),
                planningSafetyProfile);
            var planningValidator = new RetrievalPlanProposalValidator(planningSafetyProfile);
            var planningShadowExecutor = new ShadowRetrievalPlanExecutor(
                contextStore,
                memoryStore,
                relationStore,
                planningValidator,
                constraintStore);
            var relationExpansionProfileRegistry = new RelationExpansionProfileRegistry();
            var relationExpansionValidator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
            var relationExpansionPreviewService = new RelationExpansionPreviewService(
                relationStore,
                relationExpansionProfileRegistry,
                relationExpansionValidator);
            var graphExpansionApplyPolicy = new GraphExpansionApplyPolicy(
                relationExpansionPreviewService,
                contextStore,
                memoryStore,
                constraintStore);

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
                PromotionService = new BasicMemoryPromotionService(memoryStore, memoryStore),
                PromotionCandidateStore = memoryStore,
                PackageBuilder = new BasicContextPackageBuilder(
                    contextStore,
                    constraintStore,
                    globalStore,
                    memoryStore,
                    relationStore,
                    tokenizerResolver: tokenizerResolver,
                    workingMemoryService: memoryStore,
                    graphExpansionApplyOptions: graphExpansionApplyOptions,
                    graphExpansionApplyPolicy: graphExpansionApplyPolicy),
                TokenizerResolver = tokenizerResolver,
                PackagePolicyStore = packagePolicyStore,
                LearningFeedbackStore = learningFeedbackStore,
                LearningFeedbackReviewStore = learningFeedbackReviewStore,
                ArtifactStore = memoryArtifactStore,
                VectorStore = vectorStore,
                EmbeddingProvider = embeddingProvider,
                RetrievalTraceStore = retrievalTraceStore,
                Retriever = new HybridContextRetriever(
                    contextStore,
                    memoryStore,
                    relationStore,
                    embeddingProvider,
                    vectorStore,
                    retrievalTraceStore,
                    new RuleBasedContextAttentionScorer(),
                    attentionRerankOptions: attentionRerankOptions,
                    planningOptions: retrievalPlanningOptions,
                    planningProposalService: planningProposalService,
                    planningShadowExecutor: planningShadowExecutor),
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
        var fileArtifactStore = new FileArtifactStore(options);
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
        var filePlanningSnapshotService = new PlanningSnapshotService(
            new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            fileMemoryStore,
            fileConstraintStore,
            new InMemoryContextLearningStore());
        var filePlanningSafetyProfile = RetrievalPlanSafetyProfile.CreateDefault();
        var filePlanningProposalService = new RetrievalPlanProposalService(
            filePlanningSnapshotService,
            new PlanningIntentDetector(),
            filePlanningSafetyProfile);
        var filePlanningValidator = new RetrievalPlanProposalValidator(filePlanningSafetyProfile);
        var filePlanningShadowExecutor = new ShadowRetrievalPlanExecutor(
            fileContextStore,
            fileMemoryStore,
            fileRelationStore,
            filePlanningValidator,
            fileConstraintStore);
        var fileRelationExpansionProfileRegistry = new RelationExpansionProfileRegistry();
        var fileRelationExpansionValidator = new RelationExpansionPolicyValidator(new RelationTypeRegistry());
        var fileRelationExpansionPreviewService = new RelationExpansionPreviewService(
            fileRelationStore,
            fileRelationExpansionProfileRegistry,
            fileRelationExpansionValidator);
        var fileGraphExpansionApplyPolicy = new GraphExpansionApplyPolicy(
            fileRelationExpansionPreviewService,
            fileContextStore,
            fileMemoryStore,
            fileConstraintStore);

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
            PromotionService = new BasicMemoryPromotionService(fileMemoryStore, fileMemoryStore),
            PromotionCandidateStore = fileMemoryStore,
            PackageBuilder = new BasicContextPackageBuilder(
                fileContextStore,
                fileConstraintStore,
                fileGlobalStore,
                fileMemoryStore,
                fileRelationStore,
                tokenizerResolver: fileTokenizerResolver,
                workingMemoryService: fileMemoryStore,
                graphExpansionApplyOptions: graphExpansionApplyOptions,
                graphExpansionApplyPolicy: fileGraphExpansionApplyPolicy),
            TokenizerResolver = fileTokenizerResolver,
            PackagePolicyStore = filePackagePolicyStore,
            LearningFeedbackStore = fileLearningFeedbackStore,
            LearningFeedbackReviewStore = fileLearningFeedbackReviewStore,
            ArtifactStore = fileArtifactStore,
            VectorStore = fileVectorStore,
            EmbeddingProvider = fileEmbeddingProvider,
            RetrievalTraceStore = fileRetrievalTraceStore,
            Retriever = new HybridContextRetriever(
                fileContextStore,
                fileMemoryStore,
                fileRelationStore,
                fileEmbeddingProvider,
                fileVectorStore,
                fileRetrievalTraceStore,
                new RuleBasedContextAttentionScorer(),
                attentionRerankOptions: attentionRerankOptions,
                planningOptions: retrievalPlanningOptions,
                planningProposalService: filePlanningProposalService,
                planningShadowExecutor: filePlanningShadowExecutor),
            ModelGatewayOptions = fileModelOptions,
            ModelHealthService = new ModelHealthService(fileModelOptions, fileModelAdapters, fileApiKeyResolver),
            ModelUsageLogStore = fileModelUsageLogStore
        };
    }

    public static ControlRoomState CreateServiceState(
        string workspaceId,
        string collectionId,
        string? serviceBaseUrl,
        HttpClient? serviceHttpClient = null)
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

        var client = new ContextCoreClient(httpClient);
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
        var serviceArtifactStore = new FileArtifactStore(new FileStorageOptions
        {
            RootPath = FileStorageOptions.DefaultRootPath
        });
        var globalStore = new InMemoryGlobalContextStore();
        var jobQueue = new InMemoryJobQueue();
        var embeddingProvider = new MockEmbeddingProvider(new EmbeddingOptions
        {
            ModelName = "control-room-service-mode",
            Dimensions = 4
        });
        var modelOptions = ModelGatewayDefaults.CreateDefaultOptions();
        var apiKeyResolver = new ApiKeyResolver();
        var modelAdapters = ModelAdapterFactory.CreateAdapters(modelOptions, apiKeyResolver);
        var modelUsageLogStore = new InMemoryModelUsageLogStore();
        var tokenizerResolver = new DefaultContextTokenizerResolver();

        return new ControlRoomState
        {
            Mode = ControlRoomMode.Service,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            StorageKind = "service",
            RootPath = string.Empty,
            ServiceBaseUrl = normalizedBaseUrl,
            ServiceClient = client,
            ContextStore = contextStore,
            Index = index,
            MemoryStore = memoryStore,
            WorkingMemory = memoryStore,
            ConstraintStore = constraintStore,
            RelationStore = relationStore,
            GlobalContextStore = globalStore,
            JobQueue = jobQueue,
            JobQueryStore = jobQueue,
            PromotionService = new BasicMemoryPromotionService(memoryStore, memoryStore),
            PromotionCandidateStore = memoryStore,
            PackageBuilder = new BasicContextPackageBuilder(
                contextStore,
                constraintStore,
                globalStore,
                memoryStore,
                relationStore,
                tokenizerResolver: tokenizerResolver,
                workingMemoryService: memoryStore),
            TokenizerResolver = tokenizerResolver,
            PackagePolicyStore = packagePolicyStore,
            LearningFeedbackStore = learningFeedbackStore,
            LearningFeedbackReviewStore = learningFeedbackReviewStore,
            ArtifactStore = serviceArtifactStore,
            VectorStore = vectorStore,
            EmbeddingProvider = embeddingProvider,
            RetrievalTraceStore = retrievalTraceStore,
            Retriever = new HybridContextRetriever(
                contextStore,
                memoryStore,
                relationStore,
                embeddingProvider,
                vectorStore,
                retrievalTraceStore,
                new RuleBasedContextAttentionScorer()),
            ModelGatewayOptions = modelOptions,
            ModelHealthService = new ModelHealthService(modelOptions, modelAdapters, apiKeyResolver),
            ModelUsageLogStore = modelUsageLogStore
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

    public async Task<DashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            return await GetServiceModeDashboardAsync(cancellationToken).ConfigureAwait(false);
        }

        // 仪表盘一次聚合多类数据，渲染层只负责展示，不再直接访问 Store。
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var globals = await _state.GlobalContextStore.QueryAsync(new ContextGlobalQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var jobs = await QueryJobsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var recentOperations = await ReadRecentOperationsAsync(10, cancellationToken).ConfigureAwait(false);
        var recentCompressionQuality = await GetRecentCompressionQualityAsync(5, cancellationToken).ConfigureAwait(false);
        var modelStatus = await GetModelStatusAsync(5, cancellationToken).ConfigureAwait(false);
        var discovery = DiscoverWorkspaces(_state.RootPath);

        var health = BuildSystemHealth(status, recentOperations, modelStatus);
        var jobsSummary = new JobsSummary
        {
            Queued = jobs.Count(job => job.State == ContextJobState.Queued),
            Running = jobs.Count(job => job.State == ContextJobState.Running),
            WaitingRetry = jobs.Count(job => job.State == ContextJobState.WaitingRetry),
            Failed = jobs.Count(job => job.State == ContextJobState.Failed),
            Succeeded = jobs.Count(job => job.State == ContextJobState.Succeeded),
            RequiresReview = jobs.Count(job => job.State == ContextJobState.RequiresReview)
        };

        var snapshot = new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            StorageKind = _state.StorageKind,
            RootPath = Path.GetFullPath(_state.RootPath),
            WorkspaceDataFound = discovery.Workspaces.Count > 0,
            Health = health,
            Memory = new MemoryLayerSummary
            {
                RawItems = status.RawItemCount,
                WorkingMemory = status.WorkingMemoryCount,
                CandidateMemory = status.CandidateMemoryCount,
                StableMemory = status.StableMemoryCount,
                GlobalItems = globals.Count,
                Constraints = status.ConstraintCount,
                Relations = status.RelationCount,
                IndexEntries = status.IndexEntryCount,
                Packages = status.LastPackage is null ? 0 : 1
            },
            RecentOperations = recentOperations,
            RecentCompressionQuality = recentCompressionQuality,
            Jobs = jobsSummary,
            LatestPackage = status.LastPackage is null
                ? null
                : PackageSummary.FromPackage(status.LastPackage),
            Alerts = []
        };

        snapshot.Alerts = BuildAlerts(snapshot, status, modelStatus);
        return snapshot;
    }

    public async Task<ControlRoomStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            var runtimeStatus = await GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
            return new ControlRoomStatus
            {
                Mode = ControlRoomMode.Service,
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                StorageKind = runtimeStatus.Storage.Provider,
                RootPath = runtimeStatus.Storage.RootPath ?? string.Empty,
                ServiceBaseUrl = _state.ServiceBaseUrl,
                ReadinessState = runtimeStatus.Readiness.Status,
                ReadinessMessage = runtimeStatus.Readiness.Message,
                ProviderState = runtimeStatus.Readiness.ProviderState,
                ProductionReady = runtimeStatus.Readiness.ProductionReady,
                QueuedJobCount = runtimeStatus.Jobs.Queued,
                RunningJobCount = runtimeStatus.Jobs.Running,
                RetrievalBaseline = runtimeStatus.RetrievalBaseline,
                RuntimeFromCache = runtimeStatus.Readiness.FromCache,
                RuntimeCacheTtlSeconds = runtimeStatus.Readiness.CacheTtlSeconds,
                RuntimeWarningCount = runtimeStatus.Readiness.Warnings.Count
            };
        }

        var rawItems = await QueryRawAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var working = await QueryMemoryAsync(ContextMemoryLayer.Working, null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var candidates = await QueryMemoryAsync(null, ContextMemoryStatus.Candidate, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var stable = await QueryMemoryAsync(ContextMemoryLayer.Stable, ContextMemoryStatus.Stable, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var constraints = await QueryConstraintsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var relations = await QueryRelationsAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var indexEntries = await _state.Index.SearchAsync(new IndexQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = int.MaxValue
        }, cancellationToken).ConfigureAwait(false);
        var jobs = await QueryJobsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var readiness = BuildLocalReadiness(_state.StorageKind, _state.RootPath);

        return new ControlRoomStatus
        {
            Mode = ControlRoomMode.Direct,
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            StorageKind = _state.StorageKind,
            RootPath = _state.RootPath,
            ReadinessState = readiness.State,
            ReadinessMessage = readiness.Message,
            ProviderState = readiness.ProviderState,
            ProductionReady = readiness.ProductionReady,
            RawItemCount = rawItems.Count,
            WorkingMemoryCount = working.Count,
            CandidateMemoryCount = candidates.Count,
            StableMemoryCount = stable.Count,
            ConstraintCount = constraints.Count,
            RelationCount = relations.Count,
            IndexEntryCount = indexEntries.Count,
            QueuedJobCount = jobs.Count(job => job.State == ContextJobState.Queued),
            RunningJobCount = jobs.Count(job => job.State == ContextJobState.Running),
            FailedJobCount = jobs.Count(job => job.State == ContextJobState.Failed),
            SucceededJobCount = jobs.Count(job => job.State == ContextJobState.Succeeded),
            LastPackage = _state.LastPackage
        };
    }

    public Task<RuntimeStatusResponse> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStatusAsync(cancellationToken);
    }

    public Task<RuntimeReadinessResponse> GetRuntimeReadinessAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetReadinessAsync(cancellationToken);
    }

    public Task<RuntimeReadinessResponse> GetRuntimeDeepStatusAsync(
        bool refresh,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetDeepStatusAsync(refresh, cancellationToken);
    }

    public async Task<ServiceDashboardSnapshot> GetServiceDashboardSnapshotAsync(
        bool includeDeep = false,
        bool refreshDeep = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceClient()
            .GetRuntimeSnapshotAsync(includeDeep, refreshDeep, cancellationToken)
            .ConfigureAwait(false);

        return new ServiceDashboardSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot
        };
    }

    public async Task<ServiceJobsSnapshot> GetServiceJobsSnapshotAsync(
        ContextJobState? state = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var jobs = await QueryServiceJobsAsync(state, take, cancellationToken).ConfigureAwait(false);
        return new ServiceJobsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Jobs = jobs
        };
    }

    public async Task<ServiceModelSnapshot> GetServiceModelSnapshotAsync(
        ContextCoreModelRouteResolveRequest? routeRequest = null,
        CancellationToken cancellationToken = default)
    {
        var modelStatus = await GetServiceModelStatusAsync(cancellationToken).ConfigureAwait(false);
        var resolution = routeRequest is null
            ? null
            : await ResolveServiceModelRouteAsync(routeRequest, cancellationToken).ConfigureAwait(false);

        return new ServiceModelSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            ModelStatus = modelStatus,
            RouteResolution = resolution
        };
    }

    public async Task<ServiceAdminRuntimeSnapshot> GetServiceAdminRuntimeSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient()
            .GetRuntimeSnapshotAsync(includeDeep: false, refreshDeep: false, cancellationToken)
            .ConfigureAwait(false);
        var adminStatus = await GetServiceAdminStatusAsync(cancellationToken).ConfigureAwait(false);
        var backupStatus = await GetServiceBackupStatusAsync(cancellationToken).ConfigureAwait(false);
        var backupValidate = await ValidateServiceBackupAsync(cancellationToken).ConfigureAwait(false);
        var postgresDiagnostics = await GetPostgresStorageDiagnosticsSafeAsync(cancellationToken).ConfigureAwait(false);
        var layoutRoot = string.IsNullOrWhiteSpace(adminStatus.Storage.RootPath)
            ? _state.RootPath
            : adminStatus.Storage.RootPath;

        return new ServiceAdminRuntimeSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Runtime = runtime,
            AdminStatus = adminStatus,
            BackupStatus = backupStatus,
            BackupValidate = backupValidate,
            FileLayoutStatus = BuildFileLayoutStatus(layoutRoot),
            MemoryLayoutDiagnostics = BuildMemoryLayoutDiagnostics(layoutRoot, _state.WorkspaceId, _state.CollectionId),
            TraceLayoutDiagnostics = BuildTraceLayoutDiagnostics(layoutRoot, _state.WorkspaceId, _state.CollectionId),
            ReportLayoutDiagnostics = BuildReportLayoutDiagnostics(layoutRoot),
            StorageBoundaryReport = BuildStorageBoundaryReport(),
            PostgresOperationalStoreDiagnostics = postgresDiagnostics,
            PostgresRelationStoreDiagnostics = BuildPostgresRelationStoreDiagnostics(layoutRoot),
            PostgresRelationReviewProviderDiagnostics = BuildPostgresRelationReviewProviderDiagnostics(layoutRoot),
            PostgresRelationReviewParityReport = BuildPostgresRelationReviewParityReport(layoutRoot),
            PostgresRelationGovernanceParityReport = BuildPostgresRelationGovernanceParityReport(layoutRoot),
            PostgresRelationGovernanceReadinessGateReport = BuildPostgresRelationGovernanceReadinessGateReport(layoutRoot),
            PostgresRelationDualWriteQualityReport = BuildPostgresRelationDualWriteQualityReport(layoutRoot),
            PostgresRelationShadowReadQualityReport = BuildPostgresRelationShadowReadQualityReport(layoutRoot),
            PostgresRelationProviderSwitchSmokeReport = BuildPostgresRelationProviderSwitchSmokeReport(layoutRoot),
            PostgresRelationProviderSwitchGateReport = BuildPostgresRelationProviderSwitchGateReport(layoutRoot),
            PostgresRelationRuntimeCanaryReport = BuildPostgresRelationRuntimeCanaryReport(layoutRoot),
            PostgresRelationScopedServiceModeSmokeReport = BuildPostgresRelationScopedServiceModeSmokeReport(layoutRoot),
            PostgresRelationScopedServiceModeGateReport = BuildPostgresRelationScopedServiceModeGateReport(layoutRoot),
            PostgresRelationScopedExtendedCanaryReport = BuildPostgresRelationScopedExtendedCanaryReport(layoutRoot),
            PostgresRelationSelectedWorkspaceCanaryReport = BuildPostgresRelationSelectedWorkspaceCanaryReport(layoutRoot),
            PostgresRelationScopedExpansionReport = BuildPostgresRelationScopedExpansionReport(layoutRoot),
            PostgresRelationScopedObservationReport = BuildPostgresRelationScopedObservationReport(layoutRoot),
            PostgresRelationSelectedNormalWorkspaceCanaryReport = BuildPostgresRelationSelectedNormalWorkspaceCanaryReport(layoutRoot),
            PostgresRelationLimitedNormalScopeObservationReport = BuildPostgresRelationLimitedNormalScopeObservationReport(layoutRoot),
            PostgresRelationMultiNormalScopeCanaryReport = BuildPostgresRelationMultiNormalScopeCanaryReport(layoutRoot),
            PostgresLearningFeedbackDiagnosticsReport = BuildPostgresLearningFeedbackDiagnosticsReport(layoutRoot),
            PostgresLearningFeedbackParityReport = BuildPostgresLearningFeedbackParityReport(layoutRoot),
            PostgresLearningFeedbackReadinessGateReport = BuildPostgresLearningFeedbackReadinessGateReport(layoutRoot),
            PostgresLearningFeedbackDualWriteSmokeReport = BuildPostgresLearningFeedbackDualWriteSmokeReport(layoutRoot),
            PostgresLearningFeedbackShadowReadSmokeReport = BuildPostgresLearningFeedbackShadowReadSmokeReport(layoutRoot),
            PostgresLearningFeedbackProviderQualityReport = BuildPostgresLearningFeedbackProviderQualityReport(layoutRoot),
            PostgresLearningFeedbackScopedServiceModeSmokeReport = BuildPostgresLearningFeedbackScopedServiceModeSmokeReport(layoutRoot),
            PostgresLearningFeedbackScopedServiceModeGateReport = BuildPostgresLearningFeedbackScopedServiceModeGateReport(layoutRoot),
            PostgresLearningFeedbackSelectedNormalScopeCanaryReport = BuildPostgresLearningFeedbackSelectedNormalScopeCanaryReport(layoutRoot),
            PostgresLearningFeedbackLimitedScopeObservationReport = BuildPostgresLearningFeedbackLimitedScopeObservationReport(layoutRoot),
            PostgresLearningFeedbackLimitedScopeQualityReport = BuildPostgresLearningFeedbackLimitedScopeQualityReport(layoutRoot),
            PostgresLearningFeedbackFreezeGateReport = BuildPostgresLearningFeedbackFreezeGateReport(layoutRoot),
            PostgresJobQueueDiagnosticsReport = BuildPostgresJobQueueDiagnosticsReport(layoutRoot),
            PostgresJobQueueParityReport = BuildPostgresJobQueueParityReport(layoutRoot),
            PostgresJobQueueLeaseSmokeReport = BuildPostgresJobQueueLeaseSmokeReport(layoutRoot),
            PostgresJobQueueDualWriteSmokeReport = BuildPostgresJobQueueDualWriteSmokeReport(layoutRoot),
            PostgresJobQueueShadowReadSmokeReport = BuildPostgresJobQueueShadowReadSmokeReport(layoutRoot),
            PostgresJobQueueProviderQualityReport = BuildPostgresJobQueueProviderQualityReport(layoutRoot),
            PostgresJobQueueScopedWorkerCanaryReport = BuildPostgresJobQueueScopedWorkerCanaryReport(layoutRoot),
            PostgresJobQueueScopedWorkerQualityReport = BuildPostgresJobQueueScopedWorkerQualityReport(layoutRoot),
            PostgresJobQueueLimitedWorkerScopeObservationReport = BuildPostgresJobQueueLimitedWorkerScopeObservationReport(layoutRoot),
            PostgresJobQueueLimitedWorkerScopeQualityReport = BuildPostgresJobQueueLimitedWorkerScopeQualityReport(layoutRoot),
            PostgresJobQueueFreezeGateReport = BuildPostgresJobQueueFreezeGateReport(layoutRoot),
            PostgresVectorDiagnosticsReport = BuildPostgresVectorDiagnosticsReport(layoutRoot),
            PostgresVectorCompatibilityReport = BuildPostgresVectorCompatibilityReport(layoutRoot),
            PostgresVectorProviderSmokeReport = BuildPostgresVectorProviderSmokeReport(layoutRoot),
            PostgresVectorIndexParityReport = BuildPostgresVectorIndexParityReport(layoutRoot),
            PostgresVectorProviderScopedReindexPlan = BuildPostgresVectorProviderScopedReindexPlan(layoutRoot),
            PostgresVectorProviderScopedReindexResult = BuildPostgresVectorProviderScopedReindexResult(layoutRoot),
            PostgresVectorProviderScopedReindexReport = BuildPostgresVectorProviderScopedReindexReport(layoutRoot),
            PostgresVectorQueryPreviewReport = BuildPostgresVectorQueryPreviewReport(layoutRoot),
            PostgresVectorShadowEvalA3Report = BuildPostgresVectorShadowEvalReport(
                layoutRoot,
                "postgres-vector-shadow-eval-a3.json",
                "A3"),
            PostgresVectorShadowEvalExtendedReport = BuildPostgresVectorShadowEvalReport(
                layoutRoot,
                "postgres-vector-shadow-eval-extended.json",
                "Extended"),
            PostgresVectorShadowEvalSummaryReport = BuildPostgresVectorShadowEvalSummaryReport(layoutRoot),
            PostgresVectorFreezeGateReport = BuildPostgresVectorFreezeGateReport(layoutRoot)
        };
    }

    private async Task<PostgresOperationalStoreDiagnostics> GetPostgresStorageDiagnosticsSafeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetServiceClient()
                .GetPostgresStorageDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ContextCoreApiException or InvalidOperationException)
        {
            return new PostgresOperationalStoreDiagnostics
            {
                Status = "Unavailable",
                ProviderCapabilityStatus = "Unavailable",
                Diagnostics = [$"PostgresDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static FileLayoutStatus BuildFileLayoutStatus(string rootPath)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new FileArtifactStore(options).BuildStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new FileLayoutStatus
            {
                DataRoot = rootPath,
                Diagnostics = [$"FileLayoutStatusUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static PostgresRelationStoreDiagnostics BuildPostgresRelationStoreDiagnostics(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, "storage", "postgres", "postgres-relation-store-diagnostics.json");
            if (!File.Exists(path))
            {
                path = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "storage",
                    "postgres",
                    "postgres-relation-store-diagnostics.json");
            }

            if (!File.Exists(path))
            {
                return new PostgresRelationStoreDiagnostics
                {
                    ActiveRuntimeProvider = "FileSystemRelationStore",
                    Diagnostics = ["RelationStoreDiagnosticsReportMissing"],
                    Recommendation = "RunEvalPostgresRelationStoreDiagnostics"
                };
            }

            var report = JsonSerializer.Deserialize<PostgresRelationStoreDiagnostics>(
                File.ReadAllText(path),
                JsonOptions);
            return report ?? new PostgresRelationStoreDiagnostics
            {
                ActiveRuntimeProvider = "FileSystemRelationStore",
                Diagnostics = ["RelationStoreDiagnosticsReportInvalid"],
                Recommendation = "RunEvalPostgresRelationStoreDiagnostics"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PostgresRelationStoreDiagnostics
            {
                ActiveRuntimeProvider = "FileSystemRelationStore",
                Diagnostics = [$"RelationStoreDiagnosticsUnavailable:{ex.GetType().Name}"],
                Recommendation = "RunEvalPostgresRelationStoreDiagnostics"
            };
        }
    }

    private static PostgresRelationReviewProviderDiagnostics BuildPostgresRelationReviewProviderDiagnostics(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationReviewProviderDiagnostics>(
            rootPath,
            "postgres-relation-review-diagnostics.json",
            new PostgresRelationReviewProviderDiagnostics
            {
                ActiveRuntimeProvider = "FileSystemRelationStore",
                Diagnostics = ["RelationReviewDiagnosticsReportMissing"],
                Recommendation = "RunEvalPostgresRelationReviewDiagnostics"
            });
    }

    private static PostgresRelationReviewParityReport BuildPostgresRelationReviewParityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationReviewParityReport>(
            rootPath,
            "postgres-relation-review-parity-report.json",
            new PostgresRelationReviewParityReport
            {
                Diagnostics = ["RelationReviewParityReportMissing"],
                Recommendation = "RunEvalPostgresRelationReviewParity"
            });
    }

    private static PostgresRelationGovernanceParityReport BuildPostgresRelationGovernanceParityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationGovernanceParityReport>(
            rootPath,
            "postgres-relation-governance-parity-report.json",
            new PostgresRelationGovernanceParityReport
            {
                Diagnostics = ["RelationGovernanceParityReportMissing"],
                Recommendation = "RunEvalPostgresRelationGovernanceParity"
            });
    }

    private static PostgresRelationGovernanceReadinessGateReport BuildPostgresRelationGovernanceReadinessGateReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationGovernanceReadinessGateReport>(
            rootPath,
            "postgres-relation-governance-readiness-gate.json",
            new PostgresRelationGovernanceReadinessGateReport
            {
                BlockedReasons = ["RelationGovernanceReadinessGateReportMissing"],
                Recommendation = "RunEvalPostgresRelationGovernanceReadinessGate"
            });
    }

    private static PostgresRelationDualWriteQualityReport BuildPostgresRelationDualWriteQualityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationDualWriteQualityReport>(
            rootPath,
            "postgres-relation-dual-write-quality-report.json",
            new PostgresRelationDualWriteQualityReport
            {
                Diagnostics = ["RelationDualWriteQualityReportMissing"],
                Recommendation = "RunEvalPostgresRelationDualWriteQuality"
            });
    }

    private static PostgresRelationShadowReadQualityReport BuildPostgresRelationShadowReadQualityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationShadowReadQualityReport>(
            rootPath,
            "postgres-relation-shadow-read-quality-report.json",
            new PostgresRelationShadowReadQualityReport
            {
                Diagnostics = ["RelationShadowReadQualityReportMissing"],
                Recommendation = "RunEvalPostgresRelationShadowReadQuality"
            });
    }

    private static PostgresRelationProviderSwitchSmokeReport BuildPostgresRelationProviderSwitchSmokeReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationProviderSwitchSmokeReport>(
            rootPath,
            "postgres-relation-provider-switch-smoke-report.json",
            new PostgresRelationProviderSwitchSmokeReport
            {
                Diagnostics = ["RelationProviderSwitchSmokeReportMissing"],
                Recommendation = "RunEvalPostgresRelationProviderSwitchSmoke"
            });
    }

    private static PostgresRelationProviderSwitchGateReport BuildPostgresRelationProviderSwitchGateReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationProviderSwitchGateReport>(
            rootPath,
            "postgres-relation-provider-switch-gate.json",
            new PostgresRelationProviderSwitchGateReport
            {
                BlockedReasons = ["RelationProviderSwitchGateReportMissing"],
                Recommendation = "RunEvalPostgresRelationProviderSwitchGate"
            });
    }

    private static PostgresRelationRuntimeCanaryReport BuildPostgresRelationRuntimeCanaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationRuntimeCanaryReport>(
            rootPath,
            "postgres-relation-runtime-canary-report.json",
            new PostgresRelationRuntimeCanaryReport
            {
                Diagnostics = ["RelationRuntimeCanaryReportMissing"],
                Recommendation = "RunEvalPostgresRelationRuntimeCanary"
            });
    }

    private static PostgresRelationScopedServiceModeSmokeReport BuildPostgresRelationScopedServiceModeSmokeReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationScopedServiceModeSmokeReport>(
            rootPath,
            "postgres-relation-scoped-service-mode-smoke-report.json",
            new PostgresRelationScopedServiceModeSmokeReport
            {
                Diagnostics = ["RelationScopedServiceModeSmokeReportMissing"],
                Recommendation = "RunEvalPostgresRelationScopedServiceModeSmoke"
            });
    }

    private static PostgresRelationScopedServiceModeGateReport BuildPostgresRelationScopedServiceModeGateReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationScopedServiceModeGateReport>(
            rootPath,
            "postgres-relation-scoped-service-mode-gate.json",
            new PostgresRelationScopedServiceModeGateReport
            {
                BlockedReasons = ["RelationScopedServiceModeGateReportMissing"],
                Recommendation = "RunEvalPostgresRelationScopedServiceModeGate"
            });
    }

    private static PostgresRelationScopedExtendedCanaryReport BuildPostgresRelationScopedExtendedCanaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationScopedExtendedCanaryReport>(
            rootPath,
            "postgres-relation-scoped-extended-canary-report.json",
            new PostgresRelationScopedExtendedCanaryReport
            {
                Diagnostics = ["RelationScopedExtendedCanaryReportMissing"],
                Recommendation = "RunEvalPostgresRelationScopedExtendedCanary"
            });
    }

    private static PostgresRelationSelectedWorkspaceCanaryReport BuildPostgresRelationSelectedWorkspaceCanaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationSelectedWorkspaceCanaryReport>(
            rootPath,
            "postgres-relation-selected-workspace-canary-report.json",
            new PostgresRelationSelectedWorkspaceCanaryReport
            {
                Diagnostics = ["RelationSelectedWorkspaceCanaryReportMissing"],
                Recommendation = "RunEvalPostgresRelationSelectedWorkspaceCanary"
            });
    }

    private static PostgresRelationScopedExpansionReport BuildPostgresRelationScopedExpansionReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationScopedExpansionReport>(
            rootPath,
            "postgres-relation-scoped-expansion-smoke-report.json",
            new PostgresRelationScopedExpansionReport
            {
                Diagnostics = ["RelationScopedExpansionReportMissing"],
                Recommendation = "RunEvalPostgresRelationScopedExpansionSmoke"
            });
    }

    private static PostgresRelationScopedObservationReport BuildPostgresRelationScopedObservationReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationScopedObservationReport>(
            rootPath,
            "postgres-relation-scoped-observation-quality-report.json",
            new PostgresRelationScopedObservationReport
            {
                Diagnostics = ["RelationScopedObservationReportMissing"],
                Recommendation = "RunEvalPostgresRelationScopedObservationQuality"
            });
    }

    private static PostgresRelationSelectedNormalWorkspaceCanaryReport BuildPostgresRelationSelectedNormalWorkspaceCanaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationSelectedNormalWorkspaceCanaryReport>(
            rootPath,
            "postgres-relation-selected-normal-workspace-canary-report.json",
            new PostgresRelationSelectedNormalWorkspaceCanaryReport
            {
                Diagnostics = ["RelationSelectedNormalWorkspaceCanaryReportMissing"],
                Recommendation = "RunEvalPostgresRelationSelectedNormalWorkspaceCanary"
            });
    }

    private static PostgresRelationLimitedNormalScopeObservationReport BuildPostgresRelationLimitedNormalScopeObservationReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationLimitedNormalScopeObservationReport>(
            rootPath,
            "postgres-relation-limited-normal-scope-quality-report.json",
            new PostgresRelationLimitedNormalScopeObservationReport
            {
                Diagnostics = ["RelationLimitedNormalScopeObservationReportMissing"],
                Recommendation = "RunEvalPostgresRelationLimitedNormalScopeObservation"
            });
    }

    private static PostgresRelationMultiNormalScopeCanaryReport BuildPostgresRelationMultiNormalScopeCanaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresRelationMultiNormalScopeCanaryReport>(
            rootPath,
            "postgres-relation-multi-normal-scope-quality-report.json",
            new PostgresRelationMultiNormalScopeCanaryReport
            {
                Diagnostics = ["RelationMultiNormalScopeCanaryReportMissing"],
                Recommendation = "RunEvalPostgresRelationMultiNormalScopeCanary"
            });
    }

    private static PostgresLearningFeedbackDiagnosticsReport BuildPostgresLearningFeedbackDiagnosticsReport(string rootPath)
    {
        return ReadPostgresReport<PostgresLearningFeedbackDiagnosticsReport>(
            rootPath,
            "postgres-learning-feedback-diagnostics.json",
            new PostgresLearningFeedbackDiagnosticsReport
            {
                Diagnostics = ["PostgresLearningFeedbackDiagnosticsReportMissing"],
                Status = "RunEvalPostgresLearningFeedbackDiagnostics"
            });
    }

    private static PostgresLearningFeedbackParityReport BuildPostgresLearningFeedbackParityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresLearningFeedbackParityReport>(
            rootPath,
            "postgres-learning-feedback-parity-report.json",
            new PostgresLearningFeedbackParityReport
            {
                Diagnostics = ["PostgresLearningFeedbackParityReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackParity"
            });
    }

    private static LearningFeedbackPostgresReadinessGateReport BuildPostgresLearningFeedbackReadinessGateReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackPostgresReadinessGateReport>(
            rootPath,
            "postgres-learning-feedback-readiness-gate.json",
            new LearningFeedbackPostgresReadinessGateReport
            {
                FailedConditions = ["PostgresLearningFeedbackReadinessGateMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackReadinessGate"
            });
    }

    private static LearningFeedbackDualWriteSmokeReport BuildPostgresLearningFeedbackDualWriteSmokeReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackDualWriteSmokeReport>(
            rootPath,
            "postgres-learning-feedback-dual-write-smoke-report.json",
            new LearningFeedbackDualWriteSmokeReport
            {
                Mismatches = ["PostgresLearningFeedbackDualWriteSmokeReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackDualWriteSmoke"
            });
    }

    private static LearningFeedbackShadowReadSmokeReport BuildPostgresLearningFeedbackShadowReadSmokeReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackShadowReadSmokeReport>(
            rootPath,
            "postgres-learning-feedback-shadow-read-smoke-report.json",
            new LearningFeedbackShadowReadSmokeReport
            {
                Mismatches = ["PostgresLearningFeedbackShadowReadSmokeReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackShadowReadSmoke"
            });
    }

    private static LearningFeedbackProviderQualityReport BuildPostgresLearningFeedbackProviderQualityReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackProviderQualityReport>(
            rootPath,
            "postgres-learning-feedback-provider-quality-report.json",
            new LearningFeedbackProviderQualityReport
            {
                Diagnostics = ["PostgresLearningFeedbackProviderQualityReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackProviderQuality"
            });
    }

    private static LearningFeedbackScopedServiceModeSmokeReport BuildPostgresLearningFeedbackScopedServiceModeSmokeReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackScopedServiceModeSmokeReport>(
            rootPath,
            "postgres-learning-feedback-scoped-service-mode-smoke-report.json",
            new LearningFeedbackScopedServiceModeSmokeReport
            {
                Diagnostics = ["PostgresLearningFeedbackScopedServiceModeSmokeReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackScopedServiceModeSmoke"
            });
    }

    private static LearningFeedbackScopedServiceModeGateReport BuildPostgresLearningFeedbackScopedServiceModeGateReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackScopedServiceModeGateReport>(
            rootPath,
            "postgres-learning-feedback-scoped-service-mode-gate.json",
            new LearningFeedbackScopedServiceModeGateReport
            {
                BlockedReasons = ["PostgresLearningFeedbackScopedServiceModeGateMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackScopedServiceModeGate"
            });
    }

    private static LearningFeedbackSelectedNormalScopeCanaryReport BuildPostgresLearningFeedbackSelectedNormalScopeCanaryReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackSelectedNormalScopeCanaryReport>(
            rootPath,
            "postgres-learning-feedback-selected-normal-scope-canary-report.json",
            new LearningFeedbackSelectedNormalScopeCanaryReport
            {
                BlockedReasons = ["PostgresLearningFeedbackSelectedNormalScopeCanaryReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackSelectedNormalScopeCanary"
            });
    }

    private static LearningFeedbackLimitedScopeObservationReport BuildPostgresLearningFeedbackLimitedScopeObservationReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackLimitedScopeObservationReport>(
            rootPath,
            "postgres-learning-feedback-limited-scope-observation-report.json",
            new LearningFeedbackLimitedScopeObservationReport
            {
                BlockedReasons = ["PostgresLearningFeedbackLimitedScopeObservationReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackLimitedScopeObservation"
            });
    }

    private static LearningFeedbackLimitedScopeQualityReport BuildPostgresLearningFeedbackLimitedScopeQualityReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackLimitedScopeQualityReport>(
            rootPath,
            "postgres-learning-feedback-limited-scope-quality-report.json",
            new LearningFeedbackLimitedScopeQualityReport
            {
                BlockedReasons = ["PostgresLearningFeedbackLimitedScopeQualityReportMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackLimitedScopeQuality"
            });
    }

    private static LearningFeedbackPostgresFreezeGateReport BuildPostgresLearningFeedbackFreezeGateReport(string rootPath)
    {
        return ReadPostgresReport<LearningFeedbackPostgresFreezeGateReport>(
            rootPath,
            "postgres-learning-feedback-freeze-gate.json",
            new LearningFeedbackPostgresFreezeGateReport
            {
                BlockedReasons = ["PostgresLearningFeedbackFreezeGateMissing"],
                Recommendation = "RunEvalPostgresLearningFeedbackFreezeGate"
            });
    }

    private static PostgresJobQueueDiagnosticsReport BuildPostgresJobQueueDiagnosticsReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueDiagnosticsReport>(
            rootPath,
            "postgres-job-queue-diagnostics.json",
            new PostgresJobQueueDiagnosticsReport
            {
                Diagnostics = ["PostgresJobQueueDiagnosticsMissing"],
                Recommendation = "RunEvalPostgresJobQueueDiagnostics"
            });
    }

    private static PostgresJobQueueParityReport BuildPostgresJobQueueParityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueParityReport>(
            rootPath,
            "postgres-job-queue-parity-report.json",
            new PostgresJobQueueParityReport
            {
                Diagnostics = ["PostgresJobQueueParityMissing"],
                Recommendation = "RunEvalPostgresJobQueueParity"
            });
    }

    private static PostgresJobQueueLeaseSmokeReport BuildPostgresJobQueueLeaseSmokeReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueLeaseSmokeReport>(
            rootPath,
            "postgres-job-queue-lease-smoke-report.json",
            new PostgresJobQueueLeaseSmokeReport
            {
                Diagnostics = ["PostgresJobQueueLeaseSmokeMissing"],
                Recommendation = "RunEvalPostgresJobQueueLeaseSmoke"
            });
    }

    private static PostgresJobQueueDualWriteSmokeReport BuildPostgresJobQueueDualWriteSmokeReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueDualWriteSmokeReport>(
            rootPath,
            "postgres-job-queue-dual-write-smoke-report.json",
            new PostgresJobQueueDualWriteSmokeReport
            {
                Diagnostics = ["PostgresJobQueueDualWriteSmokeMissing"],
                Recommendation = "RunEvalPostgresJobQueueDualWriteSmoke"
            });
    }

    private static PostgresJobQueueShadowReadSmokeReport BuildPostgresJobQueueShadowReadSmokeReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueShadowReadSmokeReport>(
            rootPath,
            "postgres-job-queue-shadow-read-smoke-report.json",
            new PostgresJobQueueShadowReadSmokeReport
            {
                Diagnostics = ["PostgresJobQueueShadowReadSmokeMissing"],
                Recommendation = "RunEvalPostgresJobQueueShadowReadSmoke"
            });
    }

    private static PostgresJobQueueProviderQualityReport BuildPostgresJobQueueProviderQualityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueProviderQualityReport>(
            rootPath,
            "postgres-job-queue-provider-quality-report.json",
            new PostgresJobQueueProviderQualityReport
            {
                Diagnostics = ["PostgresJobQueueProviderQualityMissing"],
                Recommendation = "RunEvalPostgresJobQueueProviderQuality"
            });
    }

    private static PostgresJobQueueScopedWorkerCanaryReport BuildPostgresJobQueueScopedWorkerCanaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueScopedWorkerCanaryReport>(
            rootPath,
            "postgres-job-queue-scoped-worker-canary-report.json",
            new PostgresJobQueueScopedWorkerCanaryReport
            {
                Diagnostics = ["PostgresJobQueueScopedWorkerCanaryMissing"],
                Recommendation = "RunEvalPostgresJobQueueScopedWorkerCanary"
            });
    }

    private static PostgresJobQueueScopedWorkerQualityReport BuildPostgresJobQueueScopedWorkerQualityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueScopedWorkerQualityReport>(
            rootPath,
            "postgres-job-queue-scoped-worker-quality-report.json",
            new PostgresJobQueueScopedWorkerQualityReport
            {
                Diagnostics = ["PostgresJobQueueScopedWorkerQualityMissing"],
                Recommendation = "RunEvalPostgresJobQueueScopedWorkerQuality"
            });
    }

    private static PostgresJobQueueLimitedWorkerScopeObservationReport BuildPostgresJobQueueLimitedWorkerScopeObservationReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueLimitedWorkerScopeObservationReport>(
            rootPath,
            "postgres-job-queue-limited-worker-scope-observation-report.json",
            new PostgresJobQueueLimitedWorkerScopeObservationReport
            {
                Diagnostics = ["PostgresJobQueueLimitedWorkerScopeObservationMissing"],
                Recommendation = "RunEvalPostgresJobQueueLimitedWorkerScopeObservation"
            });
    }

    private static PostgresJobQueueLimitedWorkerScopeQualityReport BuildPostgresJobQueueLimitedWorkerScopeQualityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresJobQueueLimitedWorkerScopeQualityReport>(
            rootPath,
            "postgres-job-queue-limited-worker-scope-quality-report.json",
            new PostgresJobQueueLimitedWorkerScopeQualityReport
            {
                Diagnostics = ["PostgresJobQueueLimitedWorkerScopeQualityMissing"],
                Recommendation = "RunEvalPostgresJobQueueLimitedWorkerScopeQuality"
            });
    }

    private static JobQueuePostgresFreezeGateReport BuildPostgresJobQueueFreezeGateReport(string rootPath)
    {
        return ReadPostgresReport<JobQueuePostgresFreezeGateReport>(
            rootPath,
            "postgres-job-queue-freeze-gate.json",
            new JobQueuePostgresFreezeGateReport
            {
                BlockedReasons = ["PostgresJobQueueFreezeGateMissing"],
                Recommendation = "RunEvalPostgresJobQueueFreezeGate"
            });
    }

    private static PostgresVectorDiagnosticsReport BuildPostgresVectorDiagnosticsReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorDiagnosticsReport>(
            rootPath,
            "postgres-vector-diagnostics.json",
            new PostgresVectorDiagnosticsReport
            {
                Diagnostics = ["PostgresVectorDiagnosticsMissing"],
                Recommendation = "RunEvalPostgresVectorDiagnostics"
            });
    }

    private static PostgresVectorCompatibilityReport BuildPostgresVectorCompatibilityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorCompatibilityReport>(
            rootPath,
            "postgres-vector-compatibility.json",
            new PostgresVectorCompatibilityReport
            {
                Diagnostics = ["PostgresVectorCompatibilityMissing"],
                Recommendation = "RunEvalPostgresVectorCompatibility"
            });
    }

    private static PostgresVectorProviderSmokeReport BuildPostgresVectorProviderSmokeReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorProviderSmokeReport>(
            rootPath,
            "postgres-vector-provider-smoke-report.json",
            new PostgresVectorProviderSmokeReport
            {
                Diagnostics = ["PostgresVectorProviderSmokeMissing"],
                Recommendation = "RunEvalPostgresVectorProviderSmoke"
            });
    }

    private static PostgresVectorIndexParityReport BuildPostgresVectorIndexParityReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorIndexParityReport>(
            rootPath,
            "postgres-vector-parity-report.json",
            new PostgresVectorIndexParityReport
            {
                Diagnostics = ["PostgresVectorParityMissing"],
                Recommendation = "RunEvalPostgresVectorParity"
            });
    }

    private static PostgresVectorProviderScopedReindexPlan BuildPostgresVectorProviderScopedReindexPlan(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorProviderScopedReindexPlan>(
            rootPath,
            "postgres-vector-provider-scoped-reindex-plan.json",
            new PostgresVectorProviderScopedReindexPlan
            {
                Diagnostics = ["PostgresVectorProviderScopedReindexPlanMissing"],
                Recommendation = "RunEvalPostgresVectorProviderScopedReindexPlan"
            });
    }

    private static PostgresVectorProviderScopedReindexResult BuildPostgresVectorProviderScopedReindexResult(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorProviderScopedReindexResult>(
            rootPath,
            "postgres-vector-provider-scoped-reindex-apply-report.json",
            new PostgresVectorProviderScopedReindexResult
            {
                Diagnostics = ["PostgresVectorProviderScopedReindexApplyMissing"],
                Recommendation = "RunEvalPostgresVectorProviderScopedReindexApply"
            });
    }

    private static PostgresVectorProviderScopedReindexReport BuildPostgresVectorProviderScopedReindexReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorProviderScopedReindexReport>(
            rootPath,
            "postgres-vector-provider-scoped-reindex-quality-report.json",
            new PostgresVectorProviderScopedReindexReport
            {
                Diagnostics = ["PostgresVectorProviderScopedReindexQualityMissing"],
                Recommendation = "RunEvalPostgresVectorProviderScopedReindexQuality"
            });
    }

    private static PostgresVectorQueryPreviewReport BuildPostgresVectorQueryPreviewReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorQueryPreviewReport>(
            rootPath,
            "postgres-vector-query-preview-report.json",
            new PostgresVectorQueryPreviewReport
            {
                Diagnostics = ["PostgresVectorQueryPreviewMissing"],
                Recommendation = "RunEvalPostgresVectorQueryPreview"
            });
    }

    private static PostgresVectorShadowEvalReport BuildPostgresVectorShadowEvalReport(
        string rootPath,
        string fileName,
        string datasetName)
    {
        return ReadPostgresReport<PostgresVectorShadowEvalReport>(
            rootPath,
            fileName,
            new PostgresVectorShadowEvalReport
            {
                DatasetName = datasetName,
                Diagnostics = ["PostgresVectorShadowEvalMissing"],
                Recommendation = "RunEvalPostgresVectorShadowEval"
            });
    }

    private static PostgresVectorShadowEvalSummaryReport BuildPostgresVectorShadowEvalSummaryReport(string rootPath)
    {
        return ReadPostgresReport<PostgresVectorShadowEvalSummaryReport>(
            rootPath,
            "postgres-vector-shadow-eval-summary.json",
            new PostgresVectorShadowEvalSummaryReport
            {
                Diagnostics = ["PostgresVectorShadowEvalSummaryMissing"],
                Recommendation = "RunEvalPostgresVectorShadowEval"
            });
    }

    private static VectorPostgresProviderFreezeGateReport BuildPostgresVectorFreezeGateReport(string rootPath)
    {
        return ReadPostgresReport<VectorPostgresProviderFreezeGateReport>(
            rootPath,
            "postgres-vector-freeze-gate.json",
            new VectorPostgresProviderFreezeGateReport
            {
                BlockedReasons = ["PostgresVectorFreezeGateMissing"],
                Recommendation = "RunEvalPostgresVectorFreezeGate"
            });
    }

    private static T ReadPostgresReport<T>(string rootPath, string fileName, T fallback)
    {
        try
        {
            var path = Path.Combine(rootPath, "storage", "postgres", fileName);
            if (!File.Exists(path))
            {
                path = Path.Combine(Directory.GetCurrentDirectory(), "storage", "postgres", fileName);
            }

            if (!File.Exists(path))
            {
                return fallback;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return fallback;
        }
    }

    private static MemoryLayoutDiagnostics BuildMemoryLayoutDiagnostics(
        string rootPath,
        string workspaceId,
        string collectionId)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildMemoryLayoutDiagnostics(workspaceId, collectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new MemoryLayoutDiagnostics
            {
                DataRoot = rootPath,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = [$"MemoryLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static TraceLayoutDiagnostics BuildTraceLayoutDiagnostics(
        string rootPath,
        string workspaceId,
        string collectionId)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildTraceLayoutDiagnostics(workspaceId, collectionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new TraceLayoutDiagnostics
            {
                DataRoot = rootPath,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Diagnostics = [$"TraceLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static ReportLayoutDiagnostics BuildReportLayoutDiagnostics(string rootPath)
    {
        try
        {
            var options = new FileStorageOptions { RootPath = rootPath };
            return new ContextCoreDataLayout(options).BuildReportLayoutDiagnostics();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ReportLayoutDiagnostics
            {
                DataRoot = rootPath,
                Diagnostics = [$"ReportLayoutDiagnosticsUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    private static StorageBoundaryReport BuildStorageBoundaryReport()
    {
        try
        {
            return StorageResponsibilityRegistry.BuildReport();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new StorageBoundaryReport
            {
                Diagnostics = [$"StorageBoundaryReportUnavailable:{ex.GetType().Name}"]
            };
        }
    }

    public Task<ContextInputIngestionResult> IngestServiceAsync(
        ContextInputCommand command,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().IngestAsync(command, cancellationToken);
    }

    public Task<ContextQueryResponse> QueryServiceAsync(
        ContextQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryContextAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<ContextMemoryItem>> QueryServiceMemoryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryMemoryAsync(query, cancellationToken);
    }

    public Task<CandidateMemorySnapshot> GetServiceCandidateMemorySnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemorySnapshotAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<StableMemorySnapshot> GetServiceStableMemorySnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableMemorySnapshotAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<StableMemoryDiagnosticsReport> GetServiceStableMemoryDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableMemoryDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<StableMemoryExplanation> ExplainServiceStableMemoryAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainStableMemoryAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<StableReplacementChainResponse> GetServiceStableReplacementChainAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReplacementChainAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<StableLifecycleReviewResult> DeprecateServiceStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().DeprecateStableMemoryAsync(itemId, request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult> SupersedeServiceStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().SupersedeStableMemoryAsync(itemId, request, cancellationToken);
    }

    public Task<StableLifecycleReviewResult> RejectServiceStableMemoryAsync(
        string itemId,
        StableLifecycleReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectStableMemoryAsync(itemId, request, cancellationToken);
    }

    public Task<IReadOnlyList<StableLifecycleReviewRecord>> GetServiceStableMemoryReviewsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableMemoryReviewsAsync(itemId, cancellationToken);
    }

    public Task<CandidateMemoryRecord> GetServiceCandidateMemoryAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemoryAsync(
            candidateId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<CandidateMemoryExplanation> ExplainServiceCandidateMemoryAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainCandidateMemoryAsync(
            candidateId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<CandidateMemoryDiagnosticsReport> GetServiceCandidateMemoryDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemoryDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> MarkServiceCandidateMemoryReadyForStableReviewAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().MarkCandidateMemoryReadyForStableReviewAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> MarkServiceCandidateMemoryNeedsMoreEvidenceAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().MarkCandidateMemoryNeedsMoreEvidenceAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> RejectServiceCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectCandidateMemoryAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> ExpireServiceCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExpireCandidateMemoryAsync(candidateId, request, cancellationToken);
    }

    public Task<CandidateMemoryReviewResult> SupersedeServiceCandidateMemoryAsync(
        string candidateId,
        CandidateMemoryReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().SupersedeCandidateMemoryAsync(candidateId, request, cancellationToken);
    }

    public Task<IReadOnlyList<CandidateMemoryReviewRecord>> GetServiceCandidateMemoryReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateMemoryReviewsAsync(candidateId, cancellationToken);
    }

    public Task<IReadOnlyList<ContextGlobalItem>> QueryServiceGlobalContextAsync(
        ContextScope? scope = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryGlobalContextAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            scope,
            take,
            cancellationToken);
    }

    public Task<ContextPackageBuildResult> BuildServicePackageAsync(
        ContextPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().BuildPackageDetailedAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<ContextConstraint>> QueryServiceConstraintsAsync(
        ConstraintLevel? level = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryConstraintsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            level,
            take,
            cancellationToken);
    }

    public Task<IReadOnlyList<ContextConstraint>> QueryServiceCandidateConstraintsAsync(
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateConstraintsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status,
            take,
            offset,
            cancellationToken);
    }

    public Task<ContextConstraint> GetServiceCandidateConstraintAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateConstraintAsync(constraintId, cancellationToken);
    }

    public Task<CandidateConstraintReviewResult> ActivateServiceCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ActivateCandidateConstraintAsync(constraintId, request, cancellationToken);
    }

    public Task<CandidateConstraintReviewResult> RejectServiceCandidateConstraintAsync(
        string constraintId,
        CandidateConstraintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectCandidateConstraintAsync(constraintId, request, cancellationToken);
    }

    public Task<IReadOnlyList<CandidateConstraintReviewRecord>> GetServiceCandidateConstraintReviewsAsync(
        string constraintId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetCandidateConstraintReviewsAsync(constraintId, cancellationToken);
    }

    public Task<IReadOnlyList<ConstraintGapCandidate>> QueryServiceConstraintGapsAsync(
        string? status = null,
        string? severity = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetConstraintGapsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status: status,
            severity: severity,
            limit: take,
            offset: offset,
            cancellationToken: cancellationToken);
    }

    public Task<ConstraintGapCandidate> GetServiceConstraintGapAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetConstraintGapAsync(gapId, cancellationToken);
    }

    public Task<ConstraintGapReviewResult> AcceptServiceConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().AcceptConstraintGapAsync(gapId, request, cancellationToken);
    }

    public Task<ConstraintGapReviewResult> RejectServiceConstraintGapAsync(
        string gapId,
        ConstraintGapReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectConstraintGapAsync(gapId, request, cancellationToken);
    }

    public Task<IReadOnlyList<ConstraintGapReviewRecord>> GetServiceConstraintGapReviewsAsync(
        string gapId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetConstraintGapReviewsAsync(gapId, cancellationToken);
    }

    public Task<ContextProvenanceResponse> GetServiceProvenanceAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetProvenanceAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<ContextCoreRelationsResponse> QueryServiceRelationsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryRelationsAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<IReadOnlyList<RelationTypeDefinition>> GetServiceRelationTypesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationTypesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<RelationExpansionProfile>> GetServiceRelationExpansionProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationExpansionProfilesAsync(cancellationToken);
    }

    public Task<RelationExpansionPreviewResponse> PreviewServiceRelationExpansionAsync(
        string itemId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().PreviewRelationExpansionAsync(new RelationExpansionPreviewRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            ItemId = itemId,
            ProfileId = profileId
        }, cancellationToken);
    }

    public Task<IReadOnlyList<GraphExpansionShadowTraceRecord>> GetServiceGraphExpansionShadowTracesAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetGraphExpansionShadowTracesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<RelationGraphDiagnosticsReport> GetServiceRelationDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<RelationGraphDiagnosticsReport> GetServiceItemRelationDiagnosticsAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetItemRelationDiagnosticsAsync(
            itemId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<RelationExplainResponse> ExplainServiceRelationAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainRelationAsync(
            relationId,
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<RelationReviewResult> ReviewServiceRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ReviewRelationAsync(relationId, request, cancellationToken);
    }

    public Task<RelationReviewResult> RejectServiceRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectRelationAsync(relationId, request, cancellationToken);
    }

    public Task<RelationReviewResult> DeprecateServiceRelationAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().DeprecateRelationAsync(relationId, request, cancellationToken);
    }

    public Task<RelationReviewResult> MarkServiceRelationNeedsEvidenceAsync(
        string relationId,
        RelationReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().MarkRelationNeedsEvidenceAsync(relationId, request, cancellationToken);
    }

    public Task<IReadOnlyList<RelationReviewRecord>> GetServiceRelationReviewsAsync(
        string relationId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRelationReviewsAsync(relationId, cancellationToken);
    }

    public Task<IReadOnlyList<ContextJob>> QueryServiceJobsAsync(
        ContextJobState? state = null,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryJobsAsync(new ContextJobQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            State = state,
            Take = take
        }, cancellationToken);
    }

    public Task<ContextJob> GetServiceJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetJobAsync(jobId, cancellationToken);
    }

    public Task<ContextCoreRequeueJobResponse> RequeueServiceJobAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RequeueJobAsync(jobId, cancellationToken);
    }

    public Task<ContextCoreModelStatusResponse> GetServiceModelStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetModelStatusAsync(cancellationToken);
    }

    public Task<ContextCoreModelRouteResolveResponse> ResolveServiceModelRouteAsync(
        ContextCoreModelRouteResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ResolveModelRouteAsync(request, cancellationToken);
    }

    public Task<ContextCoreAdminStatusResponse> GetServiceAdminStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetAdminStatusAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken);
    }

    public Task<ContextCoreBackupStatusResponse> GetServiceBackupStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetBackupStatusAsync(cancellationToken);
    }

    public Task<ContextCoreBackupValidateResponse> ValidateServiceBackupAsync(CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ValidateBackupAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ContextPackagePolicy>> QueryServicePoliciesAsync(
        string? queryText = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryPackagePoliciesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            queryText,
            take,
            cancellationToken);
    }

    public Task<ContextPackagePolicy> GetServicePolicyAsync(
        string policyId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetPackagePolicyAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            policyId,
            cancellationToken);
    }

    public async Task<ServiceMemorySnapshot> GetServiceMemorySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var working = await QueryServiceMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var candidates = await QueryServiceMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Status = ContextMemoryStatus.Candidate,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var stable = await QueryServiceMemoryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        var globals = await QueryServiceGlobalContextAsync(take: 200, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ServiceMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Working = working,
            Candidates = candidates,
            Stable = stable,
            Global = globals,
            MemoryLayoutDiagnostics = BuildMemoryLayoutDiagnostics(_state.RootPath, _state.WorkspaceId, _state.CollectionId)
        };
    }

    public async Task<ServiceCandidateMemorySnapshot> GetServiceCandidateMemoryPageSnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceCandidateMemorySnapshotAsync(take, cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceCandidateMemoryDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return new ServiceCandidateMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot,
            Diagnostics = diagnostics
        };
    }

    public async Task<ServiceStableMemorySnapshot> GetServiceStableMemoryPageSnapshotAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceStableMemorySnapshotAsync(take, cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceStableMemoryDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return new ServiceStableMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot,
            Diagnostics = diagnostics
        };
    }

    public async Task<ServiceConstraintsSnapshot> GetServiceConstraintsSnapshotAsync(
        ConstraintLevel? level = null,
        CancellationToken cancellationToken = default)
    {
        var constraints = await QueryServiceConstraintsAsync(level, 200, cancellationToken).ConfigureAwait(false);
        return new ServiceConstraintsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Constraints = constraints
        };
    }

    public async Task<ServiceConstraintGapsSnapshot> GetServiceConstraintGapsSnapshotAsync(
        string? status = ConstraintGapStatus.Pending,
        string? severity = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var gaps = await QueryServiceConstraintGapsAsync(status, severity, take, offset, cancellationToken).ConfigureAwait(false);
        return new ServiceConstraintGapsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Gaps = gaps,
            Status = status,
            Severity = severity,
            Limit = take,
            Offset = offset
        };
    }

    public async Task<ServiceCandidateConstraintsSnapshot> GetServiceCandidateConstraintsSnapshotAsync(
        ContextMemoryStatus? status = ContextMemoryStatus.Candidate,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var constraints = await QueryServiceCandidateConstraintsAsync(
            status,
            take,
            offset,
            cancellationToken).ConfigureAwait(false);
        return new ServiceCandidateConstraintsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Constraints = constraints,
            Status = status,
            Limit = take,
            Offset = offset
        };
    }

    public async Task<ServiceRelationsSnapshot> GetServiceRelationsSnapshotAsync(
        string? itemId = null,
        CancellationToken cancellationToken = default)
    {
        var types = await GetServiceRelationTypesAsync(cancellationToken).ConfigureAwait(false);
        var diagnostics = await GetServiceRelationDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        ContextCoreRelationsResponse relations = new();
        RelationGraphDiagnosticsReport? itemDiagnostics = null;
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            relations = await QueryServiceRelationsAsync(itemId, cancellationToken).ConfigureAwait(false);
            itemDiagnostics = await GetServiceItemRelationDiagnosticsAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        var graphShadowTraces = await GetServiceGraphExpansionShadowTracesAsync(50, cancellationToken).ConfigureAwait(false);
        var graphShadowQuality = new GraphExpansionShadowTraceQualityReportBuilder()
            .Build(graphShadowTraces, _state.WorkspaceId, _state.CollectionId);

        return new ServiceRelationsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            ItemId = itemId ?? string.Empty,
            Relations = relations,
            RelationTypes = types,
            Diagnostics = diagnostics,
            ItemDiagnostics = itemDiagnostics,
            GraphShadowTraceQualitySummary = graphShadowQuality,
            RecentGraphShadowTraces = graphShadowTraces.Take(5).ToArray()
        };
    }

    public async Task<ServicePolicySnapshot> GetServicePolicySnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient().GetRuntimeSnapshotAsync(false, false, cancellationToken).ConfigureAwait(false);
        var policies = await QueryServicePoliciesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ServicePolicySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Policies = policies,
            DefaultPolicy = CreateDefaultServicePolicy(),
            ProviderCapabilities = runtime.Status.Capabilities,
            LifecycleNotes =
            [
                "正常模式下 deprecated/rejected 内容默认不注入。",
                "deep probe 保持手动触发，不自动扩展。"
            ]
        };
    }

    public async Task<ServiceShortTermMemorySnapshot> GetServiceShortTermMemorySnapshotAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetServiceClient()
            .GetRuntimeSnapshotAsync(includeDeep: false, refreshDeep: false, cancellationToken)
            .ConfigureAwait(false);
        var summary = await GetServiceClient().GetShortTermSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            latestRawTake: 10,
            cancellationToken).ConfigureAwait(false);
        var rawEvents = await GetServiceClient().GetShortTermRawEventsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            take: 20,
            cancellationToken).ConfigureAwait(false);
        var archiveSummary = await GetServiceClient().GetShortTermArchiveSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        var archiveItems = await GetServiceClient().GetShortTermArchiveItemsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            kind: null,
            limit: 10,
            cancellationToken).ConfigureAwait(false);
        var runs = await GetServiceClient().GetShortTermCompactionRunsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            trigger: null,
            take: 10,
            cancellationToken).ConfigureAwait(false);

        return new ServiceShortTermMemorySnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Summary = summary,
            RawEvents = rawEvents,
            ArchiveSummary = archiveSummary,
            ArchiveItems = archiveItems,
            RecentRuns = runs,
            Maintenance = runtime.Status.ShortTermMaintenance ?? runtime.Readiness.ShortTermMaintenance
        };
    }

    public Task<ShortTermMemoryCompactionResult> CompactServiceShortTermMemoryAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().CompactShortTermMemoryAsync(new ShortTermMemoryCompactionRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            SessionId = sessionId
        }, cancellationToken);
    }

    public Task<ShortTermArchiveSummary> GetServiceShortTermArchiveSummaryAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermArchiveSummaryAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            cancellationToken);
    }

    public Task<ShortTermArchiveItemsResponse> GetServiceShortTermArchiveItemsAsync(
        string? sessionId = null,
        string? kind = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermArchiveItemsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            kind,
            limit,
            cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermCompactionRun>> GetServiceShortTermCompactionRunsAsync(
        string? sessionId = null,
        string? trigger = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermCompactionRunsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            trigger,
            take,
            cancellationToken);
    }

    public Task<ShortTermCompactionRun> GetServiceShortTermCompactionRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermCompactionRunAsync(runId, cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateServiceShortTermPromotionCandidatesAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GenerateShortTermPromotionCandidatesAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            SessionId = sessionId
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ShortTermPromotionCandidate>> QueryServiceShortTermPromotionCandidatesAsync(
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        string? kind = null,
        string? suggestedTargetLayer = null,
        double? minConfidence = null,
        double? minImportance = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryShortTermPromotionCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            status,
            kind,
            suggestedTargetLayer,
            minConfidence,
            minImportance,
            take,
            offset,
            cancellationToken);
    }

    public Task<ShortTermPromotionCandidate> GetServiceShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermPromotionCandidateAsync(candidateId, cancellationToken);
    }

    public Task<ShortTermPromotionCandidateExplanation> ExplainServiceShortTermPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainShortTermPromotionCandidateAsync(candidateId, cancellationToken);
    }

    public Task<ReviewPromotionCandidateResponse> AcceptServiceShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().AcceptShortTermPromotionCandidateAsync(candidateId, request, cancellationToken);
    }

    public Task<ReviewPromotionCandidateResponse> RejectServiceShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectShortTermPromotionCandidateAsync(candidateId, request, cancellationToken);
    }

    public Task<ReviewPromotionCandidateResponse> ExpireServiceShortTermPromotionCandidateAsync(
        string candidateId,
        ReviewPromotionCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExpireShortTermPromotionCandidateAsync(candidateId, request, cancellationToken);
    }

    public Task<IReadOnlyList<PromotionCandidateReviewRecord>> GetServiceShortTermPromotionCandidateReviewsAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetShortTermPromotionCandidateReviewsAsync(candidateId, cancellationToken);
    }

    public async Task<ServicePromotionCandidatesSnapshot> GetServicePromotionCandidatesSnapshotAsync(
        string? sessionId = null,
        PromotionCandidateStatus? status = null,
        string? kind = null,
        string? suggestedTargetLayer = null,
        double? minConfidence = null,
        double? minImportance = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var candidates = await QueryServiceShortTermPromotionCandidatesAsync(
            sessionId,
            status,
            kind,
            suggestedTargetLayer,
            minConfidence,
            minImportance,
            take,
            offset,
            cancellationToken).ConfigureAwait(false);
        return new ServicePromotionCandidatesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Candidates = candidates,
            Status = status,
            Kind = kind,
            SuggestedTargetLayer = suggestedTargetLayer,
            MinConfidence = minConfidence,
            MinImportance = minImportance,
            Limit = take,
            Offset = offset
        };
    }

    public Task<IReadOnlyList<StableReviewCandidate>> GenerateServiceStableReviewCandidatesAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GenerateStableReviewCandidatesAsync(new StableReviewCandidateGenerationRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            SessionId = sessionId,
            Limit = 100
        }, cancellationToken);
    }

    public Task<IReadOnlyList<StableReviewCandidate>> QueryServiceStableReviewCandidatesAsync(
        string? sessionId = null,
        string? status = null,
        string? validationStatus = null,
        string? kind = null,
        string? suggestedStableTarget = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReviewCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            sessionId,
            status,
            validationStatus,
            kind,
            suggestedStableTarget,
            take,
            offset,
            cancellationToken);
    }

    public Task<StableReviewCandidate> GetServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReviewCandidateAsync(stableReviewCandidateId, cancellationToken);
    }

    public Task<StableReviewCandidateExplanation> ExplainServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ExplainStableReviewCandidateAsync(stableReviewCandidateId, cancellationToken);
    }

    public Task<StableReviewDecisionResult> AcceptServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().AcceptStableReviewCandidateAsync(stableReviewCandidateId, request, cancellationToken);
    }

    public Task<StableReviewDecisionResult> RejectServiceStableReviewCandidateAsync(
        string stableReviewCandidateId,
        StableReviewDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectStableReviewCandidateAsync(stableReviewCandidateId, request, cancellationToken);
    }

    public Task<IReadOnlyList<StableReviewRecord>> GetServiceStableReviewCandidateReviewsAsync(
        string stableReviewCandidateId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetStableReviewCandidateReviewsAsync(stableReviewCandidateId, cancellationToken);
    }

    public async Task<ServiceStableReviewCandidatesSnapshot> GetServiceStableReviewCandidatesSnapshotAsync(
        string? sessionId = null,
        string? status = null,
        string? validationStatus = null,
        string? kind = null,
        string? suggestedStableTarget = null,
        int take = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var candidates = await QueryServiceStableReviewCandidatesAsync(
            sessionId,
            status,
            validationStatus,
            kind,
            suggestedStableTarget,
            take,
            offset,
            cancellationToken).ConfigureAwait(false);

        return new ServiceStableReviewCandidatesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Candidates = candidates,
            Status = status,
            ValidationStatus = validationStatus,
            Kind = kind,
            SuggestedStableTarget = suggestedStableTarget,
            Limit = take,
            Offset = offset
        };
    }

    public Task<IReadOnlyList<ContextLearningRecord>> QueryServiceLearningRecordsAsync(
        ContextFeedbackSignal? signal = null,
        ContextFailureType? failureType = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryLearningRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Signal = signal,
            FailureType = failureType,
            Limit = limit,
            Offset = offset
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PromotionFeedbackSignal>> QueryServiceLearningFeedbackAsync(
        string? action = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningFeedbackAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            action: action,
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken);
    }

    public Task<ContextLearningRecord> GetServiceLearningRecordAsync(
        string recordId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningRecordAsync(recordId, cancellationToken);
    }

    public Task<IReadOnlyList<ContextLearningCase>> QueryServiceLearningCasesAsync(
        ContextFeedbackSignal? signal = null,
        ContextFailureType? failureType = null,
        ContextLearningCaseStatus? status = null,
        string? caseKind = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().QueryLearningCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Signal = signal,
            FailureType = failureType,
            Status = status,
            CaseKind = caseKind,
            Limit = limit,
            Offset = offset
        }, cancellationToken);
    }

    public Task<ContextLearningCase> GetServiceLearningCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningCaseAsync(caseId, cancellationToken);
    }

    public Task<ContextLearningCaseGenerationResult> GenerateServiceLearningCasesAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GenerateLearningCasesAsync(new ContextLearningCaseGenerationRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Limit = 100
        }, cancellationToken);
    }

    public Task<ContextLearningCaseStatusUpdateResponse> ActivateServiceLearningCaseAsync(
        string caseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ActivateLearningCaseAsync(caseId, CreateLearningCaseStatusRequest(reason), cancellationToken);
    }

    public Task<ContextLearningCaseStatusUpdateResponse> ArchiveServiceLearningCaseAsync(
        string caseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().ArchiveLearningCaseAsync(caseId, CreateLearningCaseStatusRequest(reason), cancellationToken);
    }

    public Task<ContextLearningCaseStatusUpdateResponse> RejectServiceLearningCaseAsync(
        string caseId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().RejectLearningCaseAsync(caseId, CreateLearningCaseStatusRequest(reason), cancellationToken);
    }

    public Task<ContextLearningSummary> GetServiceLearningSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetLearningSummaryAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<ContextLearningCase>> GetServiceRegressionLearningCasesAsync(
        int limit = 20,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetRegressionLearningCasesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken);
    }

    public async Task<ServiceLearningSnapshot> GetServiceLearningSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var records = await QueryServiceLearningRecordsAsync(
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var cases = await QueryServiceLearningCasesAsync(
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var feedback = await QueryServiceLearningFeedbackAsync(
            limit: limit,
            offset: offset,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var summary = await GetServiceLearningSummaryAsync(cancellationToken).ConfigureAwait(false);
        var regressionCases = await GetServiceRegressionLearningCasesAsync(
            limit: 20,
            offset: 0,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ServiceLearningSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Summary = summary,
            FeedbackSignals = feedback,
            Records = records,
            Cases = cases,
            RegressionCases = regressionCases,
            PositiveCount = records.Count(record => record.Signal == ContextFeedbackSignal.Positive),
            NegativeCount = records.Count(record => record.Signal == ContextFeedbackSignal.Negative),
            StaleCount = records.Count(record => record.Signal == ContextFeedbackSignal.Stale),
            FailureTypeSummary = records
                .GroupBy(record => record.FailureType)
                .ToDictionary(group => group.Key, group => group.Count())
        };
    }

    public async Task<ServicePolicyFeedbackDatasetSnapshot> GetServicePolicyFeedbackDatasetSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var dataset = await GetServiceClient()
            .GetPolicyFeedbackAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                limit: limit,
                offset: offset,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ServicePolicyFeedbackDatasetSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Dataset = dataset,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<ServiceLearningFeaturesSnapshot> GetServiceLearningFeaturesSnapshotAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var dataset = await GetServiceClient()
            .GetLearningFeaturesAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                limit: limit,
                offset: offset,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var qualityReport = await GetServiceClient()
            .GetLearningDatasetQualityAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var feedbackSummary = await GetServiceClient()
            .GetLearningFeedbackSummaryAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                Limit = 20
            }, cancellationToken)
            .ConfigureAwait(false);
        var feedbackReviewSummary = await GetServiceClient()
            .GetLearningFeedbackReviewSummaryAsync(new LearningFeedbackReviewQuery
            {
                Limit = 20
            }, cancellationToken)
            .ConfigureAwait(false);

        return new ServiceLearningFeaturesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Dataset = dataset,
            QualityReport = qualityReport,
            LearningFeedbackSummary = feedbackSummary,
            LearningFeedbackReviewSummary = feedbackReviewSummary,
            LearningFeedbackFeatureCandidateReport = await ReadLearningFeedbackFeatureCandidateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningFeedbackQualityReport = await ReadLearningFeedbackQualityReportAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningApprovedFeedbackDatasetGateReport = await ReadLearningApprovedFeedbackDatasetGateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterIntentBaselineReport = await ReadRouterIntentBaselineReportAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterShadowTraceQualityReport = await ReadRouterShadowTraceQualityReportAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterDisagreementTriageA3Report = await ReadRouterDisagreementTriageReportAsync(
                    RouterDisagreementTriageRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            RouterDisagreementTriageExtendedReport = await ReadRouterDisagreementTriageReportAsync(
                    RouterDisagreementTriageRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            RouterHardNegativeCount = await ReadRouterHardNegativeCountAsync(cancellationToken)
                .ConfigureAwait(false),
            RouterGuardedOptInReadinessGateReport = await ReadRouterGuardedOptInReadinessGateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFeatureCompletenessA3Report = await ReadCandidateRerankerFeatureCompletenessReportAsync(
                    CandidateRerankerFeatureCompletenessRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFeatureCompletenessExtendedReport = await ReadCandidateRerankerFeatureCompletenessReportAsync(
                    CandidateRerankerFeatureCompletenessRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowEvalA3Report = await ReadCandidateRerankerShadowEvalReportAsync(
                    CandidateRerankerShadowEvalRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowEvalExtendedReport = await ReadCandidateRerankerShadowEvalReportAsync(
                    CandidateRerankerShadowEvalRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowFailureAuditA3Report = await ReadCandidateRerankerShadowFailureAuditReportAsync(
                    CandidateRerankerShadowFailureAuditRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowFailureAuditExtendedReport = await ReadCandidateRerankerShadowFailureAuditReportAsync(
                    CandidateRerankerShadowFailureAuditRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerScoreDistributionA3Report = await ReadCandidateRerankerScoreDistributionReportAsync(
                    CandidateRerankerScoreDistributionRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerScoreDistributionExtendedReport = await ReadCandidateRerankerScoreDistributionReportAsync(
                    CandidateRerankerScoreDistributionRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerListwiseCalibrationA3Report = await ReadCandidateRerankerListwiseCalibrationReportAsync(
                    CandidateRerankerListwiseCalibrationRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerListwiseCalibrationExtendedReport = await ReadCandidateRerankerListwiseCalibrationReportAsync(
                    CandidateRerankerListwiseCalibrationRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFormalPriorityAlignmentA3Report = await ReadCandidateRerankerFormalPriorityAlignmentReportAsync(
                    CandidateRerankerFormalPriorityAlignmentRunner.A3ReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerFormalPriorityAlignmentExtendedReport = await ReadCandidateRerankerFormalPriorityAlignmentReportAsync(
                    CandidateRerankerFormalPriorityAlignmentRunner.ExtendedReportFileName,
                    cancellationToken)
                .ConfigureAwait(false),
            CandidateRerankerShadowTraceQualityReport = await ReadCandidateRerankerShadowTraceQualityReportAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningReadinessRegistry = await ReadLearningReadinessRegistryAsync(cancellationToken)
                .ConfigureAwait(false),
            LearningRuntimeChangeReadinessGateReport = await ReadLearningRuntimeChangeReadinessGateReportAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationFreezeReport = await ReadFoundationFreezeReportAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationServiceStatus = await ReadFoundationServiceStatusAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationReportNavigation = await ReadFoundationReportNavigationAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationApiSecurityDiagnostics = await ReadFoundationApiSecurityDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationApiContractReport = await ReadFoundationApiContractReportAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationServiceAuthDiagnostics = await ReadFoundationServiceAuthDiagnosticsAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationServiceDeploymentProfileGate = await ReadFoundationServiceDeploymentProfileGateAsync(cancellationToken)
                .ConfigureAwait(false),
            FoundationOpenApiContractReport = await ReadFoundationOpenApiContractReportAsync(cancellationToken)
                .ConfigureAwait(false),
            HostedServiceSmokeReport = await ReadHostedServiceSmokeReportAsync(cancellationToken)
                .ConfigureAwait(false),
            ServiceFoundationFreezeReport = await ReadServiceFoundationFreezeReportAsync(cancellationToken)
                .ConfigureAwait(false),
            ArchitectureCleanupFreezeReport = TryLoadArchitectureCleanupFreezeSummary()?.Report,
            ArchitectureCleanupFreezeGateReport = TryLoadArchitectureCleanupFreezeGateSummary()?.Report,
            ControlledAppliedMergeRuntimePreviewPlanReport = TryLoadControlledAppliedMergeRuntimePreviewPlanSummary()?.Report,
            ControlledAppliedMergeRuntimePreviewDryRunReport = TryLoadControlledAppliedMergeRuntimePreviewDryRunSummary()?.Report,
            ControlledAppliedMergeRuntimePreviewActivationPreflightReport = TryLoadControlledAppliedMergeRuntimePreviewActivationPreflightSummary()?.Report,
            ControlledAppliedMergeRuntimePreviewObservationWindowReport = TryLoadControlledAppliedMergeRuntimePreviewObservationWindowSummary()?.Report,
            ControlledAppliedMergeRuntimePreviewObservationHardeningReport = TryLoadControlledAppliedMergeRuntimePreviewObservationHardeningSummary()?.Report,
            ControlledAppliedMergeRuntimePreviewObservationFreezeReport = TryLoadControlledAppliedMergeRuntimePreviewObservationFreezeSummary()?.Report,
            ScopedRuntimePreviewApprovalPlanReport = TryLoadScopedRuntimePreviewApprovalPlanSummary()?.Report,
            ScopedRuntimePreviewAuthorizationReport = TryLoadScopedRuntimePreviewAuthorizationSummary()?.Report,
            ScopedRuntimePreviewAuthorizationHardeningReport = TryLoadScopedRuntimePreviewAuthorizationHardeningSummary()?.Report,
            ScopedRuntimePreviewActivationPreparationReport = TryLoadScopedRuntimePreviewActivationPreparationSummary()?.Report,
            ScopedRuntimePreviewActivationDryRunReport = TryLoadScopedRuntimePreviewActivationDryRunSummary()?.Report,
            ScopedRuntimePreviewActivationWindowPreflightReport = TryLoadScopedRuntimePreviewActivationWindowPreflightSummary()?.Report,
            ScopedRuntimePreviewActivationWindowNoOpExecutionReport = TryLoadScopedRuntimePreviewActivationWindowNoOpExecutionSummary()?.Report,
            ScopedRuntimePreviewActivationLiveReadinessFreezeReport = TryLoadScopedRuntimePreviewActivationLiveReadinessFreezeSummary()?.Report,
            ScopedRuntimePreviewLiveActivationExecutionPlanReport = TryLoadScopedRuntimePreviewLiveActivationExecutionPlanSummary()?.Report,
            ScopedRuntimePreviewLiveActivationExecutionReport = TryLoadScopedRuntimePreviewLiveActivationExecutionSummary()?.Report,
            ScopedRuntimePreviewLiveActivationObservationReport = TryLoadScopedRuntimePreviewLiveActivationObservationSummary()?.Report,
            Limit = limit,
            Offset = offset
        };
    }

    public async Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            return await GetServiceClient()
                .SubmitLearningFeedbackAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        return await new LearningFeedbackService(_state.LearningFeedbackStore)
            .SubmitAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LearningFeedbackReviewResult> ReviewLearningFeedbackAsync(
        string feedbackId,
        FeedbackReviewStatus status,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            return status switch
            {
                FeedbackReviewStatus.ApprovedForDataset => await GetServiceClient()
                    .ApproveLearningFeedbackAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.Rejected => await GetServiceClient()
                    .RejectLearningFeedbackAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.NeedsRedaction => await GetServiceClient()
                    .MarkLearningFeedbackNeedsRedactionAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.NeedsMoreEvidence => await GetServiceClient()
                    .MarkLearningFeedbackNeedsEvidenceAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unsupported feedback review status: {status}", nameof(status))
            };
        }

        var service = new LearningFeedbackReviewService(_state.LearningFeedbackStore, _state.LearningFeedbackReviewStore);
        return status switch
        {
            FeedbackReviewStatus.ApprovedForDataset => await service.ApproveAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            FeedbackReviewStatus.Rejected => await service.RejectAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            FeedbackReviewStatus.NeedsRedaction => await service.NeedsRedactionAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            FeedbackReviewStatus.NeedsMoreEvidence => await service.NeedsMoreEvidenceAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentException($"Unsupported feedback review status: {status}", nameof(status))
        };
    }

    private static async Task<RouterIntentClassifierBaselineReport?> ReadRouterIntentBaselineReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            RouterIntentEvaluationRunner.DefaultOutputDirectory,
            RouterIntentEvaluationRunner.ReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterIntentClassifierBaselineReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningFeedbackFeatureCandidateReport?> ReadLearningFeedbackFeatureCandidateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "learning",
            "feedback",
            "learning-feedback-feature-candidates-report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningFeedbackFeatureCandidateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningFeedbackQualityReport?> ReadLearningFeedbackQualityReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "learning",
            "feedback",
            "learning-feedback-quality-report.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningFeedbackQualityReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningApprovedFeedbackDatasetGateReport?> ReadLearningApprovedFeedbackDatasetGateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "learning",
            "feedback",
            "learning-approved-feedback-dataset-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningApprovedFeedbackDatasetGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<RouterShadowTraceQualityReport?> ReadRouterShadowTraceQualityReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            RouterIntentShadowReportBuilder.DefaultOutputDirectory,
            RouterIntentShadowReportBuilder.TraceQualityReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterShadowTraceQualityReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<RouterDisagreementTriageReport?> ReadRouterDisagreementTriageReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            RouterDisagreementTriageRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterDisagreementTriageReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<int> ReadRouterHardNegativeCountAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            RouterDisagreementTriageRunner.DefaultOutputDirectory,
            RouterDisagreementTriageRunner.HardNegativesFileName);
        if (!File.Exists(path))
        {
            return 0;
        }

        try
        {
            var count = 0;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    count++;
                }
            }

            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static async Task<RouterGuardedOptInReadinessGateReport?> ReadRouterGuardedOptInReadinessGateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            RouterGuardedOptInReadinessGateRunner.DefaultOutputDirectory,
            RouterGuardedOptInReadinessGateRunner.ReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RouterGuardedOptInReadinessGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerShadowEvalReport?> ReadCandidateRerankerShadowEvalReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerShadowEvalRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerShadowEvalReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerFeatureCompletenessReport?> ReadCandidateRerankerFeatureCompletenessReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerFeatureCompletenessRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerFeatureCompletenessReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerShadowFailureAuditReport?> ReadCandidateRerankerShadowFailureAuditReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerShadowFailureAuditRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerShadowFailureAuditReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerScoreDistributionReport?> ReadCandidateRerankerScoreDistributionReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerScoreDistributionRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerScoreDistributionReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerListwiseCalibrationReport?> ReadCandidateRerankerListwiseCalibrationReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerListwiseCalibrationRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerListwiseCalibrationReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerFormalPriorityAlignmentReport?> ReadCandidateRerankerFormalPriorityAlignmentReportAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerFormalPriorityAlignmentRunner.DefaultOutputDirectory,
            fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerFormalPriorityAlignmentReport>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CandidateRerankerShadowTraceQualityReport?> ReadCandidateRerankerShadowTraceQualityReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            CandidateRerankerShadowTraceQualityReportBuilder.DefaultOutputDirectory,
            CandidateRerankerShadowTraceQualityReportBuilder.ReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<CandidateRerankerShadowTraceQualityReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningReadinessRegistry?> ReadLearningReadinessRegistryAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            LearningReadinessFreezeRunner.DefaultOutputDirectory,
            LearningReadinessFreezeRunner.FreezeReportFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningReadinessRegistry>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<LearningRuntimeChangeReadinessGateReport?> ReadLearningRuntimeChangeReadinessGateReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            LearningReadinessFreezeRunner.DefaultOutputDirectory,
            LearningReadinessFreezeRunner.RuntimeGateFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LearningRuntimeChangeReadinessGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task<ServiceVectorIndexSnapshot> GetServiceVectorIndexSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var client = GetServiceClient();
        var status = await client.GetVectorStatusAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        var diagnostics = await client.GetVectorDiagnosticsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken).ConfigureAwait(false);
        var preview = await client.PreviewVectorReindexAsync(new VectorReindexPreviewRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = 50,
            IncludeContextItems = true,
            IncludeMemoryItems = true
        }, cancellationToken).ConfigureAwait(false);
        var plan = await client.CreateVectorReindexPlanAsync(new VectorReindexRequest
        {
            OperationId = $"vector-coverage-controlroom-{Guid.NewGuid():N}",
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            DryRun = true,
            Apply = false,
            MaxItems = 10_000,
            IncludeContextItems = true,
            IncludeMemoryItems = true,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "controlroom_vector_coverage"
            }
        }, cancellationToken).ConfigureAwait(false);
        var coverage = VectorIndexCoverageReportBuilder.Build(plan, diagnostics, status);

        return new ServiceVectorIndexSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Status = status,
            Diagnostics = diagnostics,
            ReindexPreview = preview,
            Coverage = coverage,
            ShadowQuality = LoadVectorShadowQualitySummary()
        };
    }

    private static ServiceVectorShadowQualitySummary LoadVectorShadowQualitySummary()
    {
        var candidates = new[]
        {
            Path.Combine("eval", "vector-query-profile-sweep-extended.json"),
            Path.Combine("eval", "vector-query-profile-sweep-a3.json")
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var report = JsonSerializer.Deserialize<VectorQueryProfileSweepReport>(
                    File.ReadAllText(path),
                    JsonOptions);
                var best = report?.BestResult;
                var residual = TryLoadVectorResidualRiskReport();
                var lifecycleCoverage = TryLoadVectorLifecycleMetadataCoverageReport();
                var lifecycleBackfill = TryLoadVectorLifecycleMetadataBackfillPlan();
                var recallLoss = TryLoadVectorRecallLossReports();
                var safeRecovery = TryLoadVectorSafeRecallRecoveryReports();
                var fusionShadow = TryLoadVectorRankerFusionShadowReports();
                var representation = TryLoadVectorRepresentationBenchmarkReports();
                var queryExpansion = TryLoadVectorQueryExpansionShadowReports();
                var readinessGate = TryLoadVectorReadinessGateReport();
                var providerComparison = TryLoadVectorProviderComparisonReport();
                var qwen3ReadinessGate = TryLoadVectorQwen3ReadinessGateReport();
                var providerComparisonFreeze = TryLoadEmbeddingProviderComparisonFreezeReport();
                var hybridPreview = TryLoadVectorHybridPreviewReport();
                var hybridGate = TryLoadVectorHybridReadinessGateReport();
                var hybridAudit = TryLoadVectorHybridRecallRegressionAuditReport();
                var hybridFreeze = TryLoadVectorHybridFreezeReport();
                var alignmentAudit = TryLoadVectorRetrievalDatasetAlignmentAuditSummaryReport();
                var eligibilityTriage = TryLoadVectorEligibilityRecallLossTriageSummaryReport();
                var lifecycleRepairPlan = TryLoadVectorLifecycleMetadataRepairPlanSummaryReport();
                var lifecycleReviewCandidates = TryLoadVectorLifecycleMetadataReviewCandidateReport();
                var lifecycleReviewSummary = TryLoadVectorLifecycleMetadataReviewSummaryReport();
                var lifecycleSidecarPreview = TryLoadVectorLifecycleMetadataSidecarPreviewReport();
                var sidecarEligibility = TryLoadVectorSidecarEligibilityQualityReport();
                var reviewBatch = TryLoadVectorLifecycleMetadataReviewBatchSummary();
                var evidenceBackfill = TryLoadVectorLifecycleMetadataEvidenceBackfillReport();
                var datasetV2Generation = TryLoadRetrievalDatasetV2GenerationSummary();
                var datasetV2Materialization = TryLoadRetrievalDatasetV2MaterializationSummary();
                var datasetV2ShadowEval = TryLoadRetrievalDatasetV2ShadowEvalSummary();
                var datasetV2Stress = TryLoadRetrievalDatasetV2StressSummary();
                var datasetV2StressTriage = TryLoadRetrievalDatasetV2StressFailureTriageSummary();
                var datasetV2HybridRepair = TryLoadRetrievalDatasetV2HybridScoringRepairSummary();
                var datasetV2HybridRiskTriage = TryLoadRetrievalDatasetV2HybridScoringRiskTriageSummary();
                var datasetV2StressFreeze = TryLoadRetrievalDatasetV2StressFreezeSummary();
                var vectorV4Recheck = TryLoadVectorV4ReadinessRecheckSummary();
                var guardedFormalPreview = TryLoadGuardedFormalRetrievalPreviewSummary();
                var shadowPackageComparison = TryLoadVectorShadowPackageComparisonSummary();
                var scopedFormalPreviewOptIn = TryLoadScopedFormalPreviewOptInSummary();
                var limitedFormalPreviewObservation = TryLoadLimitedFormalPreviewObservationSummary();
                var formalPreviewFreeze = TryLoadVectorFormalPreviewFreezeSummary();
                var explicitRuntimeExperimentPlan = TryLoadExplicitScopedRuntimeExperimentPlanSummary();
                var scopedRuntimeExperimentDryRunObservation = TryLoadScopedRuntimeExperimentDryRunObservationSummary();
                var scopedRuntimeExperimentDesignFreeze = TryLoadScopedRuntimeExperimentDesignFreezeSummary();
                var scopedRuntimeExperimentProposal = TryLoadScopedRuntimeExperimentProposalSummary();
                var scopedRuntimeExperimentApproval = TryLoadScopedRuntimeExperimentApprovalSummary();
                var scopedRuntimeExperimentNoOpHarness = TryLoadScopedRuntimeExperimentNoOpHarnessSummary();
                var scopedRuntimeExperimentHarnessFreeze = TryLoadScopedRuntimeExperimentHarnessFreezeSummary();
                var guardedScopedRuntimeExperimentPlan = TryLoadGuardedScopedRuntimeExperimentPlanSummary();
                var scopedRuntimeExperimentRuntimeApproval = TryLoadScopedRuntimeExperimentRuntimeApprovalSummary();
                var scopedRuntimeExperimentActivationPreflight = TryLoadScopedRuntimeExperimentActivationPreflightSummary();
                var guardedScopedRuntimeExperiment = TryLoadGuardedScopedRuntimeExperimentSummary();
                var scopedRuntimeExperimentObservationWindow = TryLoadScopedRuntimeExperimentObservationWindowSummary();
                var scopedRuntimeExperimentObservationFreeze = TryLoadScopedRuntimeExperimentObservationFreezeSummary();
                var formalRetrievalIntegrationPlan = TryLoadFormalRetrievalIntegrationPlanSummary();
                var formalRetrievalIntegrationDecision = TryLoadFormalRetrievalIntegrationDecisionSummary();
                var shadowFormalRetrievalAdapterPlan = TryLoadShadowFormalRetrievalAdapterPlanSummary();
                var shadowFormalRetrievalAdapter = TryLoadShadowFormalRetrievalAdapterSummary();
                var formalAdapterPackageShadowComparison = TryLoadFormalAdapterPackageShadowComparisonSummary();
                var graphVectorRetrievalQualityAudit = TryLoadGraphVectorRetrievalQualityAuditSummary();
                var retrievalQualityRepairPreview = TryLoadRetrievalQualityRepairPreviewSummary();
                var runtimeObservableFeatureContract = TryLoadRuntimeObservableFeatureContractSummary();
                var runtimeRetrievalFeatureDerivation = TryLoadRuntimeRetrievalFeatureDerivationSummary();
                var runtimeRetrievalFeatureDerivationRepair = TryLoadRuntimeRetrievalFeatureDerivationRepairSummary();
                var featureDerivationFailureFreeze = TryLoadRuntimeFeatureDerivationFailureFreezeSummary();
                var graphHubNoiseControl = TryLoadGraphHubNoiseControlSummary();
                var retrievalEvalProtocol = TryLoadRetrievalEvalProtocolSummary();
                var inputMetadataEnrichment = TryLoadInputMetadataEnrichmentSummary();
                var enrichedCandidateSourceRepairRecheck = TryLoadEnrichedCandidateSourceRepairRecheckSummary();
                var sourceAwareRankingRepair = TryLoadSourceAwareRankingRepairSummary();
                var outputTokenPriorityShadow = TryLoadOutputTokenPriorityShadowSummary();
                var formalAdapterInputContract = TryLoadFormalAdapterInputContractSummary();
                var sourceDiverseShadowAdapterValidation = TryLoadSourceDiverseShadowAdapterValidationSummary();
                var shadowCandidateMergePreview = TryLoadShadowCandidateMergePreviewSummary();
                var shadowCandidateMergePreviewObservation = TryLoadShadowCandidateMergePreviewObservationSummary();
                var shadowMergeStabilityFreeze = TryLoadShadowMergeStabilityFreezeSummary();
                var shadowMergePromotionDecision = TryLoadShadowMergePromotionDecisionSummary();
                var controlledShadowMergeProposal = TryLoadControlledShadowMergeProposalSummary();
                var controlledShadowMergeDryRun = TryLoadControlledShadowMergeDryRunSummary();
                var controlledShadowMergeObservationWindow = TryLoadControlledShadowMergeObservationWindowSummary();
                var controlledShadowMergeFreeze = TryLoadControlledShadowMergeFreezeSummary();
                var controlledAppliedMergeProposal = TryLoadControlledAppliedMergeProposalSummary();
                var formalRetrievalIntegrationFreeze = TryLoadFormalRetrievalIntegrationFreezeSummary();
                if (report is null || best is null)
                {
                    continue;
                }

                return new ServiceVectorShadowQualitySummary
                {
                    Available = true,
                    SourcePath = path,
                    CurrentRecommendation = report.Recommendation,
                    BestProfile = best.ProfileId,
                    BestTopK = best.TopK,
                    BestMinSimilarity = best.MinSimilarity,
                    RiskAfterPolicy = best.RiskAfterPolicy,
                    SimilaritySeparation = best.SimilaritySeparation,
                    ResidualRiskSourcePath = residual?.SourcePath ?? string.Empty,
                    ResidualRiskCount = residual?.Report.ResidualRiskCount ?? 0,
                    TopResidualRiskTypes = residual?.Report.RiskAfterPolicyByType ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    TopWhyPolicyAllowed = residual?.Report.Risks
                        .Select(item => item.WhyPolicyAllowed)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray() ?? Array.Empty<string>(),
                    TopExpectedActions = residual?.Report.Risks
                        .Select(item => item.ExpectedAction)
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToArray() ?? Array.Empty<string>(),
                    LifecycleMetadataCoverageSourcePath = lifecycleCoverage?.SourcePath ?? string.Empty,
                    LifecycleMetadataCoverageRate = lifecycleCoverage?.Report.LifecycleCoverageRate ?? 0,
                    UnknownLifecycleCount = lifecycleCoverage?.Report.UnknownLifecycleCount ?? 0,
                    MissingReviewStatusCount = lifecycleCoverage?.Report.MissingReviewStatusCount ?? 0,
                    MissingReplacementInfoCount = lifecycleCoverage?.Report.MissingReplacementInfoCount ?? 0,
                    BlockedByLifecycleMetadataGate = residual?.Report.BlockedByLifecycleMetadataGate ?? 0,
                    LifecycleBackfillPlanSourcePath = lifecycleBackfill?.SourcePath ?? string.Empty,
                    BackfillUnknownLifecycleBefore = lifecycleBackfill?.Plan.UnknownLifecycleBefore ?? 0,
                    BackfillAutoResolvableCount = lifecycleBackfill?.Plan.AutoResolvableCount ?? 0,
                    BackfillManualReviewRequiredCount = lifecycleBackfill?.Plan.ManualReviewRequiredCount ?? 0,
                    BackfillExpectedCoverageAfter = lifecycleBackfill?.Plan.ExpectedCoverageAfter ?? 0,
                    RecallLossA3SourcePath = recallLoss.A3SourcePath,
                    RecallLossExtendedSourcePath = recallLoss.ExtendedSourcePath,
                    A3RecallAfterPolicy = recallLoss.A3?.MustHitRecallAfterPolicy ?? 0,
                    ExtendedRecallAfterPolicy = recallLoss.Extended?.MustHitRecallAfterPolicy ?? 0,
                    A3RecallRecommendation = recallLoss.A3?.Recommendation ?? string.Empty,
                    ExtendedRecallRecommendation = recallLoss.Extended?.Recommendation ?? string.Empty,
                    TopRecallMissReasons = MergeMissReasons(recallLoss.A3, recallLoss.Extended),
                    IntentReadinessRecommendations = BuildIntentReadinessSummary(recallLoss.A3, recallLoss.Extended),
                    SafeRecallRecoveryA3SourcePath = safeRecovery.A3SourcePath,
                    SafeRecallRecoveryExtendedSourcePath = safeRecovery.ExtendedSourcePath,
                    SafeRecoveryA3RecallAfterPolicy = safeRecovery.A3?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                    SafeRecoveryExtendedRecallAfterPolicy = safeRecovery.Extended?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                    SafeRecoveryA3BestConfiguration = safeRecovery.A3?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                    SafeRecoveryExtendedBestConfiguration = safeRecovery.Extended?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                    SafeRecoveryA3RecoveredBelowTopK = safeRecovery.A3?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                    SafeRecoveryExtendedRecoveredBelowTopK = safeRecovery.Extended?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                    BlockedMustHitClassificationCounts = MergeBlockedMustHitClassifications(safeRecovery.A3, safeRecovery.Extended),
                    FusionShadowA3SourcePath = fusionShadow.A3SourcePath,
                    FusionShadowExtendedSourcePath = fusionShadow.ExtendedSourcePath,
                    FusionBestStrategy = SelectFusionBestStrategy(fusionShadow.A3, fusionShadow.Extended),
                    FusionA3RecallAfterPolicy = fusionShadow.A3?.BestResult?.MustHitRecallFusion ?? 0,
                    FusionExtendedRecallAfterPolicy = fusionShadow.Extended?.BestResult?.MustHitRecallFusion ?? 0,
                    FusionRiskAfterPolicy = BuildFusionRiskSummary(fusionShadow.A3, fusionShadow.Extended),
                    FusionRecallGain = BuildFusionRecallGainSummary(fusionShadow.A3, fusionShadow.Extended),
                    FusionReadinessGateSatisfied = IsFusionReadinessSatisfied(fusionShadow.A3, fusionShadow.Extended),
                    RepresentationBenchmarkA3SourcePath = representation.A3SourcePath,
                    RepresentationBenchmarkExtendedSourcePath = representation.ExtendedSourcePath,
                    RepresentationBestDocumentProfile = SelectRepresentationBestDocumentProfile(representation.A3, representation.Extended),
                    RepresentationBestQueryProfile = SelectRepresentationBestQueryProfile(representation.A3, representation.Extended),
                    RepresentationA3RecallAfterPolicy = representation.A3?.BestResult?.Recall ?? 0,
                    RepresentationExtendedRecallAfterPolicy = representation.Extended?.BestResult?.Recall ?? 0,
                    RepresentationRiskAfterPolicy = BuildRepresentationRiskSummary(representation.A3, representation.Extended),
                    RepresentationRecoveredMissCount = BuildRepresentationRecoveredMissSummary(representation.A3, representation.Extended),
                    RepresentationV4GateSatisfied = IsRepresentationReadinessSatisfied(representation.A3, representation.Extended),
                    QueryExpansionShadowA3SourcePath = queryExpansion.A3SourcePath,
                    QueryExpansionShadowExtendedSourcePath = queryExpansion.ExtendedSourcePath,
                    QueryExpansionBestProfile = SelectQueryExpansionBestProfile(queryExpansion.A3, queryExpansion.Extended),
                    QueryExpansionA3RecallBefore = queryExpansion.A3?.BestResult?.RecallBeforeExpansion ?? 0,
                    QueryExpansionA3RecallAfter = queryExpansion.A3?.BestResult?.RecallAfterExpansion ?? 0,
                    QueryExpansionExtendedRecallBefore = queryExpansion.Extended?.BestResult?.RecallBeforeExpansion ?? 0,
                    QueryExpansionExtendedRecallAfter = queryExpansion.Extended?.BestResult?.RecallAfterExpansion ?? 0,
                    QueryExpansionRecoveredMissCount = BuildQueryExpansionRecoveredMissSummary(queryExpansion.A3, queryExpansion.Extended),
                    QueryExpansionRiskAfterPolicy = BuildQueryExpansionRiskSummary(queryExpansion.A3, queryExpansion.Extended),
                    QueryExpansionV4GateSatisfied = IsQueryExpansionReadinessSatisfied(queryExpansion.A3, queryExpansion.Extended),
                    V4ReadinessGateSourcePath = readinessGate?.SourcePath ?? string.Empty,
                    V4ReadinessGatePassed = readinessGate?.Report.Passed ?? false,
                                        V4ReadinessGateFailReasons = readinessGate?.Report.FailReasons ?? Array.Empty<string>(),
                    ProviderComparisonSourcePath = providerComparison?.SourcePath ?? string.Empty,
                    ProviderComparisonResults = providerComparison?.Report.Providers ?? Array.Empty<VectorProviderComparisonV310Result>(),
                    Qwen3ReadinessGateSourcePath = qwen3ReadinessGate?.SourcePath ?? string.Empty,
                    Qwen3ReadinessGatePassed = qwen3ReadinessGate?.Report.Passed ?? false,
                    Qwen3Recommendation = qwen3ReadinessGate?.Report.Recommendation ?? string.Empty,
                    Qwen3BlockedReasons = qwen3ReadinessGate?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ProviderComparisonFreezeSourcePath = providerComparisonFreeze?.SourcePath ?? string.Empty,
                    ProviderPromotionStatus = providerComparisonFreeze?.Report.PromotionStatus ?? string.Empty,
                    ProviderConfigurationSanityPassed = false,
                    ProviderComparisonStatus = (providerComparisonFreeze?.Report.Passed ?? false) ? "Conclusive" : (providerComparisonFreeze is not null ? "Inconclusive" : string.Empty),
                    VectorV4RecheckAllowed = providerComparisonFreeze?.Report.VectorV4RecheckAllowed ?? false,
                    ProviderPromotionBlockedReasons = providerComparisonFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    HybridPreviewSourcePath = hybridPreview?.SourcePath ?? string.Empty,
                    HybridFullA3Recall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                    HybridFullExtendedRecall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                    HybridFullRiskAfterPolicy = Math.Max(hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0, hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0),
                    HybridReadinessRecommendation = hybridPreview?.Report.Recommendation ?? string.Empty,
                    HybridReadinessGatePassed = hybridGate?.Report.Passed ?? false,
                    HybridAuditSourcePath = hybridAudit?.SourcePath ?? string.Empty,
                    HybridAuditPassed = hybridAudit?.Report.Passed ?? false,
                    HybridAuditRecommendation = hybridAudit?.Report.Recommendation ?? string.Empty,
                    HybridAuditDenseDroppedCount = hybridAudit?.Report.DenseCandidateDroppedCount ?? 0,
                    HybridAuditEligibilityMismatchCount = hybridAudit?.Report.EligibilityMismatchCount ?? 0,
                    HybridAuditDedupOverwriteCount = hybridAudit?.Report.DedupOverwriteCount ?? 0,
                    HybridFreezeSourcePath = hybridFreeze?.SourcePath ?? string.Empty,
                    HybridFreezePassed = hybridFreeze?.Report.FreezePassed ?? false,
                    HybridFreezeStatus = hybridFreeze?.Report.HybridRetrievalStatus ?? string.Empty,
                    HybridFreezeRecommendation = hybridFreeze?.Report.Recommendation ?? string.Empty,
                    HybridV4RecheckAllowed = hybridFreeze?.Report.V4RecheckAllowed ?? false,
                    HybridFreezeBlockedReasons = hybridFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    DatasetAlignmentAuditSourcePath = alignmentAudit?.SourcePath ?? string.Empty,
                    DatasetAlignmentRecommendation = alignmentAudit?.Report.Recommendation ?? string.Empty,
                    DatasetAlignmentIssueCount = alignmentAudit?.Report.AlignmentIssueCount ?? 0,
                    DatasetAlignmentA3MustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: false),
                    DatasetAlignmentExtendedMustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: false),
                    DatasetAlignmentA3ProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: true),
                    DatasetAlignmentExtendedProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: true),
                    DatasetAlignmentEligibilityBlockCount = alignmentAudit?.Report.Reports.Sum(item => item.MustHitBlockedByEligibilityCount) ?? 0,
                    DatasetAlignmentAnchorCoverageRate = ResolveAlignmentAnchorCoverage(alignmentAudit?.Report),
                    DatasetAlignmentTopIssues = alignmentAudit?.Report.IssueBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    EligibilityRecallLossTriageSourcePath = eligibilityTriage?.SourcePath ?? string.Empty,
                    EligibilityFilteredMustHitCount = eligibilityTriage?.Report.TotalFilteredMustHit ?? 0,
                    EligibilityCorrectlyBlockedCount = eligibilityTriage?.Report.CorrectlyBlockedCount ?? 0,
                    EligibilityRouteToHistoricalCount = eligibilityTriage?.Report.RouteToHistoricalCount ?? 0,
                    EligibilityRouteToAuditCount = eligibilityTriage?.Report.RouteToAuditCount ?? 0,
                    EligibilityMetadataRepairNeededCount = eligibilityTriage?.Report.MetadataRepairNeededCount ?? 0,
                    EligibilityEvalExpectationReviewNeededCount = eligibilityTriage?.Report.EvalExpectationReviewNeededCount ?? 0,
                    EligibilityUnsafeToRecoverCount = eligibilityTriage?.Report.UnsafeToRecoverCount ?? 0,
                    EligibilityRecallLossRecommendation = eligibilityTriage?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataRepairPlanSourcePath = lifecycleRepairPlan?.SourcePath ?? string.Empty,
                    LifecycleMetadataRepairCandidateCount = lifecycleRepairPlan?.Report.CandidateCount ?? 0,
                    LifecycleMetadataRepairAutoRepairableCount = lifecycleRepairPlan?.Report.AutoRepairableCount ?? 0,
                    LifecycleMetadataRepairHumanReviewRequiredCount = lifecycleRepairPlan?.Report.HumanReviewRequiredCount ?? 0,
                    LifecycleMetadataRepairForbiddenCount = lifecycleRepairPlan?.Report.ForbiddenRepairCount ?? 0,
                    LifecycleMetadataRepairEstimatedRecallRecovery = lifecycleRepairPlan?.Report.EstimatedRecallRecovery ?? 0,
                    LifecycleMetadataRepairRiskEstimate = lifecycleRepairPlan?.Report.RiskAfterRepairEstimate ?? 0,
                    LifecycleMetadataRepairRecommendation = lifecycleRepairPlan?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataReviewCandidatesSourcePath = lifecycleReviewCandidates?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewCandidateCount = lifecycleReviewCandidates?.Report.CandidateCount ?? 0,
                    LifecycleMetadataReviewPendingCount = lifecycleReviewCandidates?.Report.PendingCount ?? 0,
                    LifecycleMetadataReviewCorrectlyBlockedSkippedCount = lifecycleReviewCandidates?.Report.CorrectlyBlockedSkippedCount ?? 0,
                    LifecycleMetadataReviewCountByLayer = lifecycleReviewCandidates?.Report.CountByLayer ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    LifecycleMetadataReviewCountByItemKind = lifecycleReviewCandidates?.Report.CountByItemKind ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    LifecycleMetadataReviewRecentCandidates = lifecycleReviewCandidates?.Report.RecentCandidates ?? Array.Empty<VectorLifecycleMetadataReviewCandidate>(),
                    LifecycleMetadataReviewRecommendation = lifecycleReviewCandidates?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataReviewSummarySourcePath = lifecycleReviewSummary?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewApprovedForSidecarCount = lifecycleReviewSummary?.Report.ApprovedForSidecarCount ?? 0,
                    LifecycleMetadataReviewRejectedCount = lifecycleReviewSummary?.Report.RejectedCount ?? 0,
                    LifecycleMetadataReviewNeedsEvidenceCount = lifecycleReviewSummary?.Report.NeedsEvidenceCount ?? 0,
                    LifecycleMetadataReviewSupersededCount = lifecycleReviewSummary?.Report.SupersededCount ?? 0,
                    LifecycleMetadataReviewSidecarEntryCount = lifecycleReviewSummary?.Report.SidecarEntryCount ?? lifecycleSidecarPreview?.Report.SidecarEntryCount ?? 0,
                    LifecycleMetadataReviewUnsafeApprovalBlockedCount = lifecycleReviewSummary?.Report.UnsafeApprovalBlockedCount ?? 0,
                    LifecycleMetadataReviewSidecarPreviewSourcePath = lifecycleSidecarPreview?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewNormalContextApprovalCount = lifecycleReviewSummary?.Report.NormalContextApprovalCount ?? lifecycleSidecarPreview?.Report.NormalContextEntryCount ?? 0,
                    LifecycleMetadataReviewAuditContextApprovalCount = lifecycleReviewSummary?.Report.AuditContextApprovalCount ?? lifecycleSidecarPreview?.Report.AuditContextEntryCount ?? 0,
                    LifecycleMetadataReviewHistoricalContextApprovalCount = lifecycleReviewSummary?.Report.HistoricalContextApprovalCount ?? lifecycleSidecarPreview?.Report.HistoricalContextEntryCount ?? 0,
                    LifecycleMetadataReviewDiagnosticsOnlyApprovalCount = lifecycleReviewSummary?.Report.DiagnosticsOnlyApprovalCount ?? lifecycleSidecarPreview?.Report.DiagnosticsOnlyEntryCount ?? 0,
                    SidecarEligibilityPreviewSourcePath = sidecarEligibility?.SourcePath ?? string.Empty,
                    SidecarEligibilityCandidateCount = sidecarEligibility?.Report.CandidateCount ?? 0,
                    SidecarEligibilitySidecarEntryCount = sidecarEligibility?.Report.SidecarEntryCount ?? 0,
                    SidecarEligibilityApprovedSidecarCount = sidecarEligibility?.Report.ApprovedSidecarCount ?? 0,
                    SidecarEligibilityPendingReviewCount = sidecarEligibility?.Report.PendingReviewCount ?? 0,
                    SidecarEligibilityEffectiveMetadataChangedCount = sidecarEligibility?.Report.EffectiveMetadataChangedCount ?? 0,
                    SidecarEligibilityUnsafeBlockedCount = sidecarEligibility?.Report.UnsafeSidecarBlockedCount ?? 0,
                    SidecarEligibilityConflictBlockedCount = sidecarEligibility?.Report.ConflictSidecarBlockedCount ?? 0,
                    SidecarEligibilitySourceItemUnchanged = sidecarEligibility?.Report.SourceItemUnchanged ?? true,
                    SidecarEligibilityRecommendation = sidecarEligibility?.Report.Recommendation ?? string.Empty,
                    LifecycleMetadataReviewBatchSourcePath = reviewBatch?.SourcePath ?? string.Empty,
                    LifecycleMetadataReviewBatchId = reviewBatch?.Batch.BatchId ?? string.Empty,
                    LifecycleMetadataReviewBatchStatus = reviewBatch?.Batch.Status ?? string.Empty,
                    LifecycleMetadataReviewBatchCandidateCount = reviewBatch?.Batch.CandidateCount ?? 0,
                    LifecycleMetadataReviewBatchValidationErrorCount = reviewBatch?.Validation?.ValidationErrorCount ?? 0,
                    LifecycleMetadataReviewBatchWouldWriteSidecarCount = reviewBatch?.ApplyPreview?.WouldWriteSidecarEntryCount ?? 0,
                    LifecycleMetadataReviewBatchUnsafeBlockedCount = reviewBatch?.ApplyPreview?.UnsafeBlockedCount ?? reviewBatch?.Validation?.UnsafeDecisionCount ?? 0,
                    LifecycleMetadataReviewBatchRecommendation = reviewBatch?.ApplyPreview?.Recommendation ?? reviewBatch?.Validation?.Recommendation ?? (reviewBatch is null ? string.Empty : "ReadyForManualReview"),
                    LifecycleMetadataEvidenceBackfillSourcePath = evidenceBackfill?.SourcePath ?? string.Empty,
                    LifecycleMetadataEvidenceBackfillCandidateCount = evidenceBackfill?.Report.CandidateCount ?? 0,
                    LifecycleMetadataEvidenceFoundCount = evidenceBackfill?.Report.EvidenceFoundCount ?? 0,
                    LifecycleMetadataSourceRefFoundCount = evidenceBackfill?.Report.SourceRefFoundCount ?? 0,
                    LifecycleMetadataProvenanceFoundCount = evidenceBackfill?.Report.ProvenanceFoundCount ?? 0,
                    LifecycleMetadataAutoRepairableAfterBackfillCount = evidenceBackfill?.Report.AutoRepairableAfterBackfillCount ?? 0,
                    LifecycleMetadataNeedsEvidenceAfterBackfillCount = evidenceBackfill?.Report.NeedsEvidenceCount ?? 0,
                    LifecycleMetadataEvidenceBackfillRecommendation = evidenceBackfill?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2GenerationSourcePath = datasetV2Generation?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2CorpusItemCount = datasetV2Generation?.CorpusItemCount ?? 0,
                    RetrievalDatasetV2SampleCount = datasetV2Generation?.SampleCount ?? 0,
                    RetrievalDatasetV2ValidationIssueCount = datasetV2Generation?.ValidationIssueCount ?? 0,
                    RetrievalDatasetV2MissingEvidenceCount = datasetV2Generation?.MissingEvidenceCount ?? 0,
                    RetrievalDatasetV2MissingProvenanceCount = datasetV2Generation?.MissingProvenanceCount ?? 0,
                    RetrievalDatasetV2DifficultyBreakdown = datasetV2Generation?.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2SplitBreakdown = datasetV2Generation?.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2Recommendation = datasetV2Generation?.Recommendation ?? string.Empty,
                    RetrievalDatasetV2MaterializationSourcePath = datasetV2Materialization?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2DatasetId = datasetV2Materialization?.Report.DatasetId ?? string.Empty,
                    RetrievalDatasetV2CorpusHash = datasetV2Materialization?.Report.CorpusHash ?? string.Empty,
                    RetrievalDatasetV2SamplesHash = datasetV2Materialization?.Report.SamplesHash ?? string.Empty,
                    RetrievalDatasetV2MaterializationGatePassed = datasetV2Materialization?.Report.GatePassed ?? false,
                    RetrievalDatasetV2MaterializationCorpusHashStable = datasetV2Materialization?.Report.CorpusHashStable ?? false,
                    RetrievalDatasetV2MaterializationSamplesHashStable = datasetV2Materialization?.Report.SamplesHashStable ?? false,
                    RetrievalDatasetV2MaterializationRecommendation = datasetV2Materialization?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalSourcePath = datasetV2ShadowEval?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalDatasetId = datasetV2ShadowEval?.Summary.DatasetId ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalBestProfileName = datasetV2ShadowEval?.Summary.BestProfileName ?? string.Empty,
                    RetrievalDatasetV2ShadowEvalBestRecallAfterPolicy = datasetV2ShadowEval?.Summary.BestRecallAfterPolicy ?? 0,
                    RetrievalDatasetV2ShadowEvalBestMrrAfterPolicy = datasetV2ShadowEval?.Summary.BestMrrAfterPolicy ?? 0,
                    RetrievalDatasetV2ShadowEvalBestRiskAfterPolicy = datasetV2ShadowEval?.Summary.BestRiskAfterPolicy ?? 0,
                    RetrievalDatasetV2ShadowEvalPgVectorParityPassed = datasetV2ShadowEval?.Summary.PgVectorParityPassed ?? false,
                    RetrievalDatasetV2ShadowEvalRecommendation = datasetV2ShadowEval?.Gate?.Recommendation ?? datasetV2ShadowEval?.Summary.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressSourcePath = datasetV2Stress?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2StressDatasetId = datasetV2Stress?.Report.DatasetId ?? string.Empty,
                    RetrievalDatasetV2StressCorpusItemCount = datasetV2Stress?.Report.CorpusItemCount ?? 0,
                    RetrievalDatasetV2StressSampleCount = datasetV2Stress?.Report.SampleCount ?? 0,
                    RetrievalDatasetV2StressSplitBreakdown = datasetV2Stress?.Report.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressDifficultyBreakdown = datasetV2Stress?.Report.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressLeakageIssueCount = datasetV2Stress?.Report.LeakageIssueCount ?? 0,
                    RetrievalDatasetV2StressAnchorDominanceScore = datasetV2Stress?.Report.AnchorDominanceScore ?? 0,
                    RetrievalDatasetV2StressDenseRecall = datasetV2Stress?.Report.DenseRecall ?? 0,
                    RetrievalDatasetV2StressLexicalRecall = datasetV2Stress?.Report.LexicalRecall ?? 0,
                    RetrievalDatasetV2StressAnchorRecall = datasetV2Stress?.Report.AnchorRecall ?? 0,
                    RetrievalDatasetV2StressHybridRecall = datasetV2Stress?.Report.HybridRecall ?? 0,
                    RetrievalDatasetV2StressHoldoutHybridRecall = datasetV2Stress?.Report.HoldoutHybridRecall ?? 0,
                    RetrievalDatasetV2StressRecommendation = datasetV2Stress?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressTriageSourcePath = datasetV2StressTriage?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2StressFailureCount = datasetV2StressTriage?.Report.FailureCount ?? 0,
                    RetrievalDatasetV2StressHoldoutFailureCount = datasetV2StressTriage?.Report.HoldoutFailureCount ?? 0,
                    RetrievalDatasetV2StressFailureCountBySplit = datasetV2StressTriage?.Report.FailureCountBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressFailureCountByDifficulty = datasetV2StressTriage?.Report.FailureCountByDifficulty ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressFailureCountByReason = datasetV2StressTriage?.Report.FailureCountByReason ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2StressDenseOnlyWinCount = datasetV2StressTriage?.Report.DenseOnlyWinCount ?? 0,
                    RetrievalDatasetV2StressHybridWinCount = datasetV2StressTriage?.Report.HybridWinCount ?? 0,
                    RetrievalDatasetV2StressAnchorRegressionCount = datasetV2StressTriage?.Report.AnchorRegressionCount ?? 0,
                    RetrievalDatasetV2StressProfileComparisonSummary = FormatDatasetV2StressProfileComparisons(datasetV2StressTriage?.Report),
                    RetrievalDatasetV2StressTriageRecommendation = datasetV2StressTriage?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2HybridRepairSourcePath = datasetV2HybridRepair?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2HybridRepairBestProfileName = datasetV2HybridRepair?.BestProfile?.ProfileName ?? datasetV2HybridRepair?.Report.BestProfileName ?? string.Empty,
                    RetrievalDatasetV2HybridRepairRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.RecallAfterPolicy ?? 0,
                    RetrievalDatasetV2HybridRepairHoldoutRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.HoldoutRecallAfterPolicy ?? 0,
                    RetrievalDatasetV2HybridRepairDenseWinnerLostCount = datasetV2HybridRepair?.BestProfile?.DenseWinnerLostCount ?? 0,
                    RetrievalDatasetV2HybridRepairMustHitBelowTopKCount = datasetV2HybridRepair?.BestProfile?.MustHitBelowTopKCount ?? 0,
                    RetrievalDatasetV2HybridRepairNegativeDistractorCount = datasetV2HybridRepair?.BestProfile?.NegativeDistractorOutranksMustHitCount ?? 0,
                    RetrievalDatasetV2HybridRepairRiskAfterPolicy = datasetV2HybridRepair?.BestProfile?.RiskAfterPolicy ?? 0,
                    RetrievalDatasetV2HybridRepairRecommendation = datasetV2HybridRepair?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2HybridRiskTriageSourcePath = datasetV2HybridRiskTriage?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2HybridRiskTriageProfileName = datasetV2HybridRiskTriage?.Report.ProfileName ?? string.Empty,
                    RetrievalDatasetV2HybridRiskCandidateCount = datasetV2HybridRiskTriage?.Report.RiskCandidateCount ?? 0,
                    RetrievalDatasetV2HybridRiskByType = datasetV2HybridRiskTriage?.Report.RiskByType ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2HybridRiskBySplit = datasetV2HybridRiskTriage?.Report.RiskBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                    RetrievalDatasetV2HybridMustNotPromotedCount = datasetV2HybridRiskTriage?.Report.MustNotCandidatePromotedCount ?? 0,
                    RetrievalDatasetV2HybridEligibilityBypassCount = datasetV2HybridRiskTriage?.Report.EligibilityBypassCount ?? 0,
                    RetrievalDatasetV2HybridRiskProjectionMismatchCount = datasetV2HybridRiskTriage?.Report.RiskProjectionMismatchCount ?? 0,
                    RetrievalDatasetV2HybridRiskTriageRecommendation = datasetV2HybridRiskTriage?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressFreezeSourcePath = datasetV2StressFreeze?.SourcePath ?? string.Empty,
                    RetrievalDatasetV2StressFreezePassed = datasetV2StressFreeze?.Report.FreezePassed ?? false,
                    RetrievalDatasetV2StressFreezeStatus = datasetV2StressFreeze?.Report.DatasetV2Stress ?? string.Empty,
                    RetrievalDatasetV2StressFreezeRecommendation = datasetV2StressFreeze?.Report.Recommendation ?? string.Empty,
                    RetrievalDatasetV2StressFreezeBestProfile = datasetV2StressFreeze?.Report.BestPreviewProfile ?? string.Empty,
                    RetrievalDatasetV2StressFreezeStressRecall = datasetV2StressFreeze?.Report.StressRecall ?? 0,
                    RetrievalDatasetV2StressFreezeHoldoutRecall = datasetV2StressFreeze?.Report.HoldoutRecall ?? 0,
                    RetrievalDatasetV2StressFreezeRiskAfterPolicy = datasetV2StressFreeze?.Report.RiskAfterPolicy ?? 0,
                    RetrievalDatasetV2StressFreezeMustNotHitRiskAfterPolicy = datasetV2StressFreeze?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    RetrievalDatasetV2StressFreezeLifecycleRiskAfterPolicy = datasetV2StressFreeze?.Report.LifecycleRiskAfterPolicy ?? 0,
                    RetrievalDatasetV2StressFreezeFormalOutputChanged = datasetV2StressFreeze?.Report.FormalOutputChanged ?? 0,
                    RetrievalDatasetV2StressFreezeLeakageIssueCount = datasetV2StressFreeze?.Report.LeakageIssueCount ?? 0,
                    RetrievalDatasetV2StressFreezeAnchorDominanceScore = datasetV2StressFreeze?.Report.AnchorDominanceScore ?? 0,
                    RetrievalDatasetV2StressFreezeV4RecheckAllowed = datasetV2StressFreeze?.Report.V4RecheckAllowed ?? false,
                    RetrievalDatasetV2StressFreezeReadyForFormalRetrieval = datasetV2StressFreeze?.Report.ReadyForFormalRetrieval ?? false,
                    RetrievalDatasetV2StressFreezeFormalRetrievalAllowed = datasetV2StressFreeze?.Report.FormalRetrievalAllowed ?? false,
                    RetrievalDatasetV2StressFreezeBlockedReasons = datasetV2StressFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    VectorV4ReadinessRecheckSourcePath = vectorV4Recheck?.SourcePath ?? string.Empty,
                    VectorV4ReadinessRecheckPassed = vectorV4Recheck?.Report.RecheckPassed ?? false,
                    VectorV4ReadinessRecheckRecommendation = vectorV4Recheck?.Report.Recommendation ?? string.Empty,
                    VectorV4ReadinessLegacyStatus = vectorV4Recheck?.Report.LegacyVectorStatus ?? string.Empty,
                    VectorV4ReadinessSmallStatus = vectorV4Recheck?.Report.DatasetV2SmallStatus ?? string.Empty,
                    VectorV4ReadinessStressStatus = vectorV4Recheck?.Report.DatasetV2StressStatus ?? string.Empty,
                    VectorV4ReadinessPgVectorStatus = vectorV4Recheck?.Report.PgVectorProviderStatus ?? string.Empty,
                    VectorV4ReadinessHybridScoringStatus = vectorV4Recheck?.Report.HybridScoringRepairStatus ?? string.Empty,
                    VectorV4ReadinessRuntimeGateStatus = vectorV4Recheck?.Report.RuntimeChangeGateStatus ?? string.Empty,
                    VectorV4ReadinessBestProfile = vectorV4Recheck?.Report.BestPreviewProfile ?? string.Empty,
                    VectorV4ReadinessStressRecall = vectorV4Recheck?.Report.DatasetV2StressRecall ?? 0,
                    VectorV4ReadinessHoldoutRecall = vectorV4Recheck?.Report.DatasetV2HoldoutRecall ?? 0,
                    VectorV4ReadinessRiskAfterPolicy = vectorV4Recheck?.Report.RiskAfterPolicy ?? 0,
                    VectorV4ReadinessFormalOutputChanged = vectorV4Recheck?.Report.FormalOutputChanged ?? 0,
                    VectorV4ReadinessReadyForGuardedFormalPreview = vectorV4Recheck?.Report.ReadyForGuardedFormalPreview ?? false,
                    VectorV4ReadinessReadyForRuntimeSwitch = vectorV4Recheck?.Report.ReadyForRuntimeSwitch ?? false,
                    VectorV4ReadinessFormalRetrievalAllowed = vectorV4Recheck?.Report.FormalRetrievalAllowed ?? false,
                    VectorV4ReadinessBlockedReasons = vectorV4Recheck?.Report.BlockedReasons ?? Array.Empty<string>(),
                    GuardedFormalRetrievalPreviewSourcePath = guardedFormalPreview?.SourcePath ?? string.Empty,
                    GuardedFormalRetrievalPreviewGatePassed = guardedFormalPreview?.Report.GatePassed ?? false,
                    GuardedFormalRetrievalPreviewRecommendation = guardedFormalPreview?.Report.Recommendation ?? string.Empty,
                    GuardedFormalRetrievalPreviewProfileName = guardedFormalPreview?.Report.ProfileName ?? string.Empty,
                    GuardedFormalRetrievalPreviewV4RecheckPassed = guardedFormalPreview?.Report.V4RecheckPassed ?? false,
                    GuardedFormalRetrievalPreviewWouldAddCount = guardedFormalPreview?.Report.WouldAddCount ?? 0,
                    GuardedFormalRetrievalPreviewWouldRemoveCount = guardedFormalPreview?.Report.WouldRemoveCount ?? 0,
                    GuardedFormalRetrievalPreviewWouldRerankCount = guardedFormalPreview?.Report.WouldRerankCount ?? 0,
                    GuardedFormalRetrievalPreviewWouldChangeTargetSectionCount = guardedFormalPreview?.Report.WouldChangeTargetSectionCount ?? 0,
                    GuardedFormalRetrievalPreviewRiskAfterPolicy = guardedFormalPreview?.Report.RiskAfterPolicy ?? 0,
                    GuardedFormalRetrievalPreviewMustNotHitRiskAfterPolicy = guardedFormalPreview?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    GuardedFormalRetrievalPreviewLifecycleRiskAfterPolicy = guardedFormalPreview?.Report.LifecycleRiskAfterPolicy ?? 0,
                    GuardedFormalRetrievalPreviewFormalOutputChanged = guardedFormalPreview?.Report.FormalOutputChanged ?? 0,
                    GuardedFormalRetrievalPreviewPackingPolicyChanged = guardedFormalPreview?.Report.PackingPolicyChanged ?? false,
                    GuardedFormalRetrievalPreviewPackageOutputChanged = guardedFormalPreview?.Report.PackageOutputChanged ?? false,
                    GuardedFormalRetrievalPreviewReadyForRuntimeSwitch = guardedFormalPreview?.Report.ReadyForRuntimeSwitch ?? false,
                    GuardedFormalRetrievalPreviewFormalRetrievalAllowed = guardedFormalPreview?.Report.FormalRetrievalAllowed ?? false,
                    GuardedFormalRetrievalPreviewBlockedReasons = guardedFormalPreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                    VectorShadowPackageComparisonSourcePath = shadowPackageComparison?.SourcePath ?? string.Empty,
                    VectorShadowPackageComparisonGatePassed = shadowPackageComparison?.Report.GatePassed ?? false,
                    VectorShadowPackageComparisonRecommendation = shadowPackageComparison?.Report.Recommendation ?? string.Empty,
                    VectorShadowPackageComparisonProfileName = shadowPackageComparison?.Report.ProfileName ?? string.Empty,
                    VectorShadowPackageCandidateAddCount = shadowPackageComparison?.Report.CandidateAddCount ?? 0,
                    VectorShadowPackageCandidateRemoveCount = shadowPackageComparison?.Report.CandidateRemoveCount ?? 0,
                    VectorShadowPackageCandidateUnchangedCount = shadowPackageComparison?.Report.CandidateUnchangedCount ?? 0,
                    VectorShadowPackageSectionChangedCount = shadowPackageComparison?.Report.SectionChangedCount ?? 0,
                    VectorShadowPackageTokenDeltaTotal = shadowPackageComparison?.Report.TokenDeltaTotal ?? 0,
                    VectorShadowPackageTokenDeltaMax = shadowPackageComparison?.Report.TokenDeltaMax ?? 0,
                    VectorShadowPackageConstraintCoverageDelta = shadowPackageComparison?.Report.ConstraintCoverageDelta ?? 0,
                    VectorShadowPackageRelationCoverageDelta = shadowPackageComparison?.Report.RelationCoverageDelta ?? 0,
                    VectorShadowPackageRiskAfterPolicy = shadowPackageComparison?.Report.RiskAfterPolicy ?? 0,
                    VectorShadowPackageMustNotHitRiskAfterPolicy = shadowPackageComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    VectorShadowPackageLifecycleRiskAfterPolicy = shadowPackageComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                    VectorShadowPackageFormalOutputChanged = shadowPackageComparison?.Report.FormalOutputChanged ?? 0,
                    VectorShadowPackagePackageOutputChanged = shadowPackageComparison?.Report.PackageOutputChanged ?? false,
                    VectorShadowPackagePackingPolicyChanged = shadowPackageComparison?.Report.PackingPolicyChanged ?? false,
                    VectorShadowPackageRuntimeMutated = shadowPackageComparison?.Report.RuntimeMutated ?? false,
                    VectorShadowPackageReadyForRuntimeSwitch = shadowPackageComparison?.Report.ReadyForRuntimeSwitch ?? false,
                    VectorShadowPackageFormalRetrievalAllowed = shadowPackageComparison?.Report.FormalRetrievalAllowed ?? false,
                    VectorShadowPackageBlockedReasons = shadowPackageComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedFormalPreviewOptInSourcePath = scopedFormalPreviewOptIn?.SourcePath ?? string.Empty,
                    ScopedFormalPreviewOptInGatePassed = scopedFormalPreviewOptIn?.Report.GatePassed ?? false,
                    ScopedFormalPreviewOptInRecommendation = scopedFormalPreviewOptIn?.Report.Recommendation ?? string.Empty,
                    ScopedFormalPreviewOptInMode = scopedFormalPreviewOptIn?.Report.Mode ?? string.Empty,
                    ScopedFormalPreviewOptInProfileName = scopedFormalPreviewOptIn?.Report.ProfileName ?? string.Empty,
                    ScopedFormalPreviewOptInWorkspaceAllowlist = scopedFormalPreviewOptIn?.Report.WorkspaceAllowlist ?? Array.Empty<string>(),
                    ScopedFormalPreviewOptInCollectionAllowlist = scopedFormalPreviewOptIn?.Report.CollectionAllowlist ?? Array.Empty<string>(),
                    ScopedFormalPreviewOptInEvalScopeAllowlist = scopedFormalPreviewOptIn?.Report.EvalScopeAllowlist ?? Array.Empty<string>(),
                    ScopedFormalPreviewOptInPreviewPackageCount = scopedFormalPreviewOptIn?.Report.PreviewPackageCount ?? 0,
                    ScopedFormalPreviewOptInBaselinePackageCount = scopedFormalPreviewOptIn?.Report.BaselinePackageCount ?? 0,
                    ScopedFormalPreviewOptInNonAllowlistedScopeChecked = scopedFormalPreviewOptIn?.Report.NonAllowlistedScopeChecked ?? false,
                    ScopedFormalPreviewOptInNonAllowlistedScopeLeakCount = scopedFormalPreviewOptIn?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    ScopedFormalPreviewOptInRiskAfterPolicy = scopedFormalPreviewOptIn?.Report.RiskAfterPolicy ?? 0,
                    ScopedFormalPreviewOptInFormalOutputChanged = scopedFormalPreviewOptIn?.Report.FormalOutputChanged ?? 0,
                    ScopedFormalPreviewOptInPackageOutputChanged = scopedFormalPreviewOptIn?.Report.PackageOutputChanged ?? false,
                    ScopedFormalPreviewOptInPackingPolicyChanged = scopedFormalPreviewOptIn?.Report.PackingPolicyChanged ?? false,
                    ScopedFormalPreviewOptInFormalPackageWritten = scopedFormalPreviewOptIn?.Report.FormalPackageWritten ?? false,
                    ScopedFormalPreviewOptInRuntimeMutated = scopedFormalPreviewOptIn?.Report.RuntimeMutated ?? false,
                    ScopedFormalPreviewOptInRollbackInstruction = scopedFormalPreviewOptIn?.Report.RollbackInstruction ?? string.Empty,
                    ScopedFormalPreviewOptInBlockedReasons = scopedFormalPreviewOptIn?.Report.BlockedReasons ?? Array.Empty<string>(),
                    LimitedFormalPreviewObservationSourcePath = limitedFormalPreviewObservation?.SourcePath ?? string.Empty,
                    LimitedFormalPreviewObservationGatePassed = limitedFormalPreviewObservation?.Report.GatePassed ?? false,
                    LimitedFormalPreviewObservationRecommendation = limitedFormalPreviewObservation?.Report.Recommendation ?? string.Empty,
                    LimitedFormalPreviewObservationMode = limitedFormalPreviewObservation?.Report.Mode ?? string.Empty,
                    LimitedFormalPreviewObservationProfileName = limitedFormalPreviewObservation?.Report.ProfileName ?? string.Empty,
                    LimitedFormalPreviewObservationRunCount = limitedFormalPreviewObservation?.Report.ObservationRunCount ?? 0,
                    LimitedFormalPreviewObservationPreviewPackageCount = limitedFormalPreviewObservation?.Report.PreviewPackageCount ?? 0,
                    LimitedFormalPreviewObservationBaselinePackageCount = limitedFormalPreviewObservation?.Report.BaselinePackageCount ?? 0,
                    LimitedFormalPreviewObservationCandidateAddCount = limitedFormalPreviewObservation?.Report.CandidateAddCount ?? 0,
                    LimitedFormalPreviewObservationCandidateRemoveCount = limitedFormalPreviewObservation?.Report.CandidateRemoveCount ?? 0,
                    LimitedFormalPreviewObservationSectionChangedCount = limitedFormalPreviewObservation?.Report.SectionChangedCount ?? 0,
                    LimitedFormalPreviewObservationTokenDeltaTotal = limitedFormalPreviewObservation?.Report.TokenDeltaTotal ?? 0,
                    LimitedFormalPreviewObservationTokenDeltaMax = limitedFormalPreviewObservation?.Report.TokenDeltaMax ?? 0,
                    LimitedFormalPreviewObservationTokenDeltaP95 = limitedFormalPreviewObservation?.Report.TokenDeltaP95 ?? 0,
                    LimitedFormalPreviewObservationRiskAfterPolicy = limitedFormalPreviewObservation?.Report.RiskAfterPolicy ?? 0,
                    LimitedFormalPreviewObservationFormalOutputChanged = limitedFormalPreviewObservation?.Report.FormalOutputChanged ?? 0,
                    LimitedFormalPreviewObservationPackageOutputChanged = limitedFormalPreviewObservation?.Report.PackageOutputChanged ?? false,
                    LimitedFormalPreviewObservationPackingPolicyChanged = limitedFormalPreviewObservation?.Report.PackingPolicyChanged ?? false,
                    LimitedFormalPreviewObservationFormalPackageWritten = limitedFormalPreviewObservation?.Report.FormalPackageWritten ?? false,
                    LimitedFormalPreviewObservationRuntimeMutated = limitedFormalPreviewObservation?.Report.RuntimeMutated ?? false,
                    LimitedFormalPreviewObservationNonAllowlistedScopeLeakCount = limitedFormalPreviewObservation?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    LimitedFormalPreviewObservationBlockedReasons = limitedFormalPreviewObservation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    VectorFormalPreviewFreezeSourcePath = formalPreviewFreeze?.SourcePath ?? string.Empty,
                    VectorFormalPreviewFreezePassed = formalPreviewFreeze?.Report.FreezePassed ?? false,
                    VectorFormalPreviewFreezeStatus = formalPreviewFreeze?.Report.VectorFormalPreview ?? string.Empty,
                    VectorFormalPreviewFreezeRecommendation = formalPreviewFreeze?.Report.Recommendation ?? string.Empty,
                    VectorFormalPreviewAllowedMode = formalPreviewFreeze?.Report.AllowedMode ?? string.Empty,
                    VectorFormalPreviewFormalRetrievalAllowed = formalPreviewFreeze?.Report.FormalRetrievalAllowed ?? false,
                    VectorFormalPreviewReadyForRuntimeSwitch = formalPreviewFreeze?.Report.ReadyForRuntimeSwitch ?? false,
                    VectorFormalPreviewUseForRuntime = formalPreviewFreeze?.Report.UseForRuntime ?? false,
                    VectorFormalPreviewRuntimeSwitchAllowed = formalPreviewFreeze?.Report.RuntimeSwitchAllowed ?? false,
                    VectorFormalPreviewRiskAfterPolicy = formalPreviewFreeze?.Report.RiskAfterPolicy ?? 0,
                    VectorFormalPreviewFormalOutputChanged = formalPreviewFreeze?.Report.FormalOutputChanged ?? 0,
                    VectorFormalPreviewPackageOutputChanged = formalPreviewFreeze?.Report.PackageOutputChanged ?? false,
                    VectorFormalPreviewPackingPolicyChanged = formalPreviewFreeze?.Report.PackingPolicyChanged ?? false,
                    VectorFormalPreviewFormalPackageWritten = formalPreviewFreeze?.Report.FormalPackageWritten ?? false,
                    VectorFormalPreviewRuntimeMutated = formalPreviewFreeze?.Report.RuntimeMutated ?? false,
                    VectorFormalPreviewScopeLeakCount = formalPreviewFreeze?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    VectorFormalPreviewForbiddenChanges = formalPreviewFreeze?.Report.ForbiddenChanges ?? Array.Empty<string>(),
                    VectorFormalPreviewBlockedReasons = formalPreviewFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ExplicitScopedRuntimeExperimentSourcePath = explicitRuntimeExperimentPlan?.SourcePath ?? string.Empty,
                    ExplicitScopedRuntimeExperimentPlanPassed = explicitRuntimeExperimentPlan?.Report.PlanPassed ?? false,
                    ExplicitScopedRuntimeExperimentRecommendation = explicitRuntimeExperimentPlan?.Report.Recommendation ?? string.Empty,
                    ExplicitScopedRuntimeExperimentMode = explicitRuntimeExperimentPlan?.Report.Mode ?? string.Empty,
                    ExplicitScopedRuntimeExperimentProfileName = explicitRuntimeExperimentPlan?.Report.ProfileName ?? string.Empty,
                    ExplicitScopedRuntimeExperimentWorkspaceAllowlist = explicitRuntimeExperimentPlan?.Report.WorkspaceAllowlist ?? Array.Empty<string>(),
                    ExplicitScopedRuntimeExperimentCollectionAllowlist = explicitRuntimeExperimentPlan?.Report.CollectionAllowlist ?? Array.Empty<string>(),
                    ExplicitScopedRuntimeExperimentEvalScopeAllowlist = explicitRuntimeExperimentPlan?.Report.EvalScopeAllowlist ?? Array.Empty<string>(),
                    ExplicitScopedRuntimeExperimentDryRunSupported = explicitRuntimeExperimentPlan?.Report.DryRunSupported ?? false,
                    ExplicitScopedRuntimeExperimentRuntimeSwitchAllowed = explicitRuntimeExperimentPlan?.Report.RuntimeSwitchAllowed ?? false,
                    ExplicitScopedRuntimeExperimentFormalRetrievalAllowed = explicitRuntimeExperimentPlan?.Report.FormalRetrievalAllowed ?? false,
                    ExplicitScopedRuntimeExperimentReadyForRuntimeSwitch = explicitRuntimeExperimentPlan?.Report.ReadyForRuntimeSwitch ?? false,
                    ExplicitScopedRuntimeExperimentUseForRuntime = explicitRuntimeExperimentPlan?.Report.UseForRuntime ?? false,
                    ExplicitScopedRuntimeExperimentFormalPackageWritten = explicitRuntimeExperimentPlan?.Report.FormalPackageWritten ?? false,
                    ExplicitScopedRuntimeExperimentRuntimeMutated = explicitRuntimeExperimentPlan?.Report.RuntimeMutated ?? false,
                    ExplicitScopedRuntimeExperimentPackingPolicyChanged = explicitRuntimeExperimentPlan?.Report.PackingPolicyChanged ?? false,
                    ExplicitScopedRuntimeExperimentPackageOutputChanged = explicitRuntimeExperimentPlan?.Report.PackageOutputChanged ?? false,
                    ExplicitScopedRuntimeExperimentScopeLeakCount = explicitRuntimeExperimentPlan?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    ExplicitScopedRuntimeExperimentAllowedActions = explicitRuntimeExperimentPlan?.Report.AllowedActions ?? Array.Empty<string>(),
                    ExplicitScopedRuntimeExperimentForbiddenActions = explicitRuntimeExperimentPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                    ExplicitScopedRuntimeExperimentRequiredGateSummary = explicitRuntimeExperimentPlan?.Report.RequiredGateSummary ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ExplicitScopedRuntimeExperimentRollbackPlan = explicitRuntimeExperimentPlan?.Report.RollbackPlan ?? string.Empty,
                    ExplicitScopedRuntimeExperimentBlockedReasons = explicitRuntimeExperimentPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentDryRunObservationSourcePath = scopedRuntimeExperimentDryRunObservation?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentDryRunObservationGatePassed = scopedRuntimeExperimentDryRunObservation?.Report.GatePassed ?? false,
                    ScopedRuntimeExperimentDryRunObservationRecommendation = scopedRuntimeExperimentDryRunObservation?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentDryRunObservationMode = scopedRuntimeExperimentDryRunObservation?.Report.Mode ?? string.Empty,
                    ScopedRuntimeExperimentDryRunObservationProfileName = scopedRuntimeExperimentDryRunObservation?.Report.ProfileName ?? string.Empty,
                    ScopedRuntimeExperimentDryRunObservationRunCount = scopedRuntimeExperimentDryRunObservation?.Report.ObservationRunCount ?? 0,
                    ScopedRuntimeExperimentDryRunObservationWorkspaceAllowlist = scopedRuntimeExperimentDryRunObservation?.Report.WorkspaceAllowlist ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentDryRunObservationCollectionAllowlist = scopedRuntimeExperimentDryRunObservation?.Report.CollectionAllowlist ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentDryRunObservationEvalScopeAllowlist = scopedRuntimeExperimentDryRunObservation?.Report.EvalScopeAllowlist ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentDryRunObservationDryRunPackageCount = scopedRuntimeExperimentDryRunObservation?.Report.DryRunPackageCount ?? 0,
                    ScopedRuntimeExperimentDryRunObservationBaselinePackageCount = scopedRuntimeExperimentDryRunObservation?.Report.BaselinePackageCount ?? 0,
                    ScopedRuntimeExperimentDryRunObservationCandidateAddCount = scopedRuntimeExperimentDryRunObservation?.Report.CandidateAddCount ?? 0,
                    ScopedRuntimeExperimentDryRunObservationCandidateRemoveCount = scopedRuntimeExperimentDryRunObservation?.Report.CandidateRemoveCount ?? 0,
                    ScopedRuntimeExperimentDryRunObservationTokenDeltaTotal = scopedRuntimeExperimentDryRunObservation?.Report.TokenDeltaTotal ?? 0,
                    ScopedRuntimeExperimentDryRunObservationTokenDeltaMax = scopedRuntimeExperimentDryRunObservation?.Report.TokenDeltaMax ?? 0,
                    ScopedRuntimeExperimentDryRunObservationRiskAfterPolicy = scopedRuntimeExperimentDryRunObservation?.Report.RiskAfterPolicy ?? 0,
                    ScopedRuntimeExperimentDryRunObservationFormalOutputChanged = scopedRuntimeExperimentDryRunObservation?.Report.FormalOutputChanged ?? 0,
                    ScopedRuntimeExperimentDryRunObservationFormalPackageWritten = scopedRuntimeExperimentDryRunObservation?.Report.FormalPackageWritten ?? false,
                    ScopedRuntimeExperimentDryRunObservationRuntimeMutated = scopedRuntimeExperimentDryRunObservation?.Report.RuntimeMutated ?? false,
                    ScopedRuntimeExperimentDryRunObservationVectorStoreBindingChanged = scopedRuntimeExperimentDryRunObservation?.Report.VectorStoreBindingChanged ?? false,
                    ScopedRuntimeExperimentDryRunObservationPackingPolicyChanged = scopedRuntimeExperimentDryRunObservation?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentDryRunObservationPackageOutputChanged = scopedRuntimeExperimentDryRunObservation?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentDryRunObservationNonAllowlistedScopeLeakCount = scopedRuntimeExperimentDryRunObservation?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    ScopedRuntimeExperimentDryRunObservationRollbackPlanAvailable = scopedRuntimeExperimentDryRunObservation?.Report.RollbackPlanAvailable ?? false,
                    ScopedRuntimeExperimentDryRunObservationBlockedReasons = scopedRuntimeExperimentDryRunObservation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentDesignFreezeSourcePath = scopedRuntimeExperimentDesignFreeze?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentDesignFreezePassed = scopedRuntimeExperimentDesignFreeze?.Report.FreezePassed ?? false,
                    ScopedRuntimeExperimentDesignFreezeStatus = scopedRuntimeExperimentDesignFreeze?.Report.DesignStatus ?? string.Empty,
                    ScopedRuntimeExperimentDesignFreezeRecommendation = scopedRuntimeExperimentDesignFreeze?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentDesignFreezeAllowedMode = scopedRuntimeExperimentDesignFreeze?.Report.AllowedMode ?? string.Empty,
                    ScopedRuntimeExperimentDesignFreezeAllowlistedScopeCount = scopedRuntimeExperimentDesignFreeze?.Report.AllowlistedScopeCount ?? 0,
                    ScopedRuntimeExperimentDesignFreezeObservationRunCount = scopedRuntimeExperimentDesignFreeze?.Report.ObservationRunCount ?? 0,
                    ScopedRuntimeExperimentDesignFreezeRiskAfterPolicy = scopedRuntimeExperimentDesignFreeze?.Report.RiskAfterPolicy ?? 0,
                    ScopedRuntimeExperimentDesignFreezeFormalOutputChanged = scopedRuntimeExperimentDesignFreeze?.Report.FormalOutputChanged ?? 0,
                    ScopedRuntimeExperimentDesignFreezeRuntimeMutated = scopedRuntimeExperimentDesignFreeze?.Report.RuntimeMutated ?? false,
                    ScopedRuntimeExperimentDesignFreezeVectorStoreBindingChanged = scopedRuntimeExperimentDesignFreeze?.Report.VectorStoreBindingChanged ?? false,
                    ScopedRuntimeExperimentDesignFreezePackingPolicyChanged = scopedRuntimeExperimentDesignFreeze?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentDesignFreezePackageOutputChanged = scopedRuntimeExperimentDesignFreeze?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentDesignFreezeFormalPackageWritten = scopedRuntimeExperimentDesignFreeze?.Report.FormalPackageWritten ?? false,
                    ScopedRuntimeExperimentDesignFreezeScopeLeakCount = scopedRuntimeExperimentDesignFreeze?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    ScopedRuntimeExperimentDesignFreezeRollbackPlanAvailable = scopedRuntimeExperimentDesignFreeze?.Report.RollbackPlanAvailable ?? false,
                    ScopedRuntimeExperimentDesignFreezeReadyForRuntimeExperimentProposal = scopedRuntimeExperimentDesignFreeze?.Report.ReadyForRuntimeExperimentProposal ?? false,
                    ScopedRuntimeExperimentDesignFreezeReadyForRuntimeSwitch = scopedRuntimeExperimentDesignFreeze?.Report.ReadyForRuntimeSwitch ?? false,
                    ScopedRuntimeExperimentDesignFreezeForbiddenActions = scopedRuntimeExperimentDesignFreeze?.Report.ForbiddenActions ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentDesignFreezeBlockedReasons = scopedRuntimeExperimentDesignFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentProposalSourcePath = scopedRuntimeExperimentProposal?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentProposalId = scopedRuntimeExperimentProposal?.Report.ProposalId ?? string.Empty,
                    ScopedRuntimeExperimentProposalPassed = scopedRuntimeExperimentProposal?.Report.ProposalPassed ?? false,
                    ScopedRuntimeExperimentProposalRecommendation = scopedRuntimeExperimentProposal?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentProposalWorkspaceId = scopedRuntimeExperimentProposal?.Report.WorkspaceId ?? string.Empty,
                    ScopedRuntimeExperimentProposalCollectionId = scopedRuntimeExperimentProposal?.Report.CollectionId ?? string.Empty,
                    ScopedRuntimeExperimentProposalEvalScopeId = scopedRuntimeExperimentProposal?.Report.EvalScopeId ?? string.Empty,
                    ScopedRuntimeExperimentProposalProfileName = scopedRuntimeExperimentProposal?.Report.ProfileName ?? string.Empty,
                    ScopedRuntimeExperimentProposalApprovalRequired = scopedRuntimeExperimentProposal?.Report.ApprovalRequired ?? false,
                    ScopedRuntimeExperimentProposalApproved = scopedRuntimeExperimentProposal?.Report.Approved ?? false,
                    ScopedRuntimeExperimentProposalRuntimeSwitchAllowed = scopedRuntimeExperimentProposal?.Report.RuntimeSwitchAllowed ?? false,
                    ScopedRuntimeExperimentProposalFormalRetrievalAllowed = scopedRuntimeExperimentProposal?.Report.FormalRetrievalAllowed ?? false,
                    ScopedRuntimeExperimentProposalReadyForRuntimeSwitch = scopedRuntimeExperimentProposal?.Report.ReadyForRuntimeSwitch ?? false,
                    ScopedRuntimeExperimentProposalUseForRuntime = scopedRuntimeExperimentProposal?.Report.UseForRuntime ?? false,
                    ScopedRuntimeExperimentProposalWriteFormalPackage = scopedRuntimeExperimentProposal?.Report.WriteFormalPackage ?? false,
                    ScopedRuntimeExperimentProposalConfigPatchWritten = scopedRuntimeExperimentProposal?.Report.ConfigPatchWritten ?? false,
                    ScopedRuntimeExperimentProposalDiBindingChanged = scopedRuntimeExperimentProposal?.Report.DiBindingChanged ?? false,
                    ScopedRuntimeExperimentProposalPackingPolicyChanged = scopedRuntimeExperimentProposal?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentProposalPackageOutputChanged = scopedRuntimeExperimentProposal?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentProposalRollbackPlan = scopedRuntimeExperimentProposal?.Report.RollbackPlan ?? string.Empty,
                    ScopedRuntimeExperimentProposalKillSwitchPlan = scopedRuntimeExperimentProposal?.Report.KillSwitchPlan ?? string.Empty,
                    ScopedRuntimeExperimentProposalBlockedReasons = scopedRuntimeExperimentProposal?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentApprovalSummarySourcePath = scopedRuntimeExperimentApproval?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentApprovalProposalId = scopedRuntimeExperimentApproval?.Report.ProposalId ?? string.Empty,
                    ScopedRuntimeExperimentApprovalCount = scopedRuntimeExperimentApproval?.Report.ApprovalCount ?? 0,
                    ScopedRuntimeExperimentApprovalRecordExists = scopedRuntimeExperimentApproval?.Report.ApprovalRecordExists ?? false,
                    ScopedRuntimeExperimentApprovalId = scopedRuntimeExperimentApproval?.Report.LatestApprovalId ?? string.Empty,
                    ScopedRuntimeExperimentApprovalMode = scopedRuntimeExperimentApproval?.Report.ApprovalMode ?? string.Empty,
                    ScopedRuntimeExperimentApprovalExpired = scopedRuntimeExperimentApproval?.Report.Expired ?? false,
                    ScopedRuntimeExperimentApprovalRevoked = scopedRuntimeExperimentApproval?.Report.Revoked ?? false,
                    ScopedRuntimeExperimentApprovalRecommendation = scopedRuntimeExperimentApproval?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentApprovalBlockedReasons = scopedRuntimeExperimentApproval?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentNoOpHarnessSourcePath = scopedRuntimeExperimentNoOpHarness?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentNoOpHarnessPassed = scopedRuntimeExperimentNoOpHarness?.Report.HarnessPassed ?? false,
                    ScopedRuntimeExperimentNoOpHarnessRecommendation = scopedRuntimeExperimentNoOpHarness?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentNoOpHarnessTraceCount = scopedRuntimeExperimentNoOpHarness?.Report.NoOpTraceCount ?? 0,
                    ScopedRuntimeExperimentNoOpHarnessRuntimeMutated = scopedRuntimeExperimentNoOpHarness?.Report.RuntimeMutated ?? false,
                    ScopedRuntimeExperimentNoOpHarnessVectorStoreBindingChanged = scopedRuntimeExperimentNoOpHarness?.Report.VectorStoreBindingChanged ?? false,
                    ScopedRuntimeExperimentNoOpHarnessFormalPackageWritten = scopedRuntimeExperimentNoOpHarness?.Report.FormalPackageWritten ?? false,
                    ScopedRuntimeExperimentNoOpHarnessPackingPolicyChanged = scopedRuntimeExperimentNoOpHarness?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentNoOpHarnessPackageOutputChanged = scopedRuntimeExperimentNoOpHarness?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentNoOpHarnessFormalRetrievalAllowed = scopedRuntimeExperimentNoOpHarness?.Report.FormalRetrievalAllowed ?? false,
                    ScopedRuntimeExperimentNoOpHarnessRuntimeSwitchAllowed = scopedRuntimeExperimentNoOpHarness?.Report.RuntimeSwitchAllowed ?? false,
                    ScopedRuntimeExperimentNoOpHarnessReadyForRuntimeSwitch = scopedRuntimeExperimentNoOpHarness?.Report.ReadyForRuntimeSwitch ?? false,
                    ScopedRuntimeExperimentNoOpHarnessBlockedReasons = scopedRuntimeExperimentNoOpHarness?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentHarnessFreezeSourcePath = scopedRuntimeExperimentHarnessFreeze?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezePassed = scopedRuntimeExperimentHarnessFreeze?.Report.FreezePassed ?? false,
                    ScopedRuntimeExperimentHarnessFreezeRecommendation = scopedRuntimeExperimentHarnessFreeze?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeProposalId = scopedRuntimeExperimentHarnessFreeze?.Report.ProposalId ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeApprovalId = scopedRuntimeExperimentHarnessFreeze?.Report.ApprovalId ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeApprovalMode = scopedRuntimeExperimentHarnessFreeze?.Report.ApprovalMode ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeHarnessStatus = scopedRuntimeExperimentHarnessFreeze?.Report.HarnessStatus ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeAllowedMode = scopedRuntimeExperimentHarnessFreeze?.Report.AllowedMode ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeNextAllowedPhase = scopedRuntimeExperimentHarnessFreeze?.Report.NextAllowedPhase ?? string.Empty,
                    ScopedRuntimeExperimentHarnessFreezeRuntimeMutated = scopedRuntimeExperimentHarnessFreeze?.Report.RuntimeMutated ?? false,
                    ScopedRuntimeExperimentHarnessFreezeVectorStoreBindingChanged = scopedRuntimeExperimentHarnessFreeze?.Report.VectorStoreBindingChanged ?? false,
                    ScopedRuntimeExperimentHarnessFreezeFormalPackageWritten = scopedRuntimeExperimentHarnessFreeze?.Report.FormalPackageWritten ?? false,
                    ScopedRuntimeExperimentHarnessFreezePackingPolicyChanged = scopedRuntimeExperimentHarnessFreeze?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentHarnessFreezePackageOutputChanged = scopedRuntimeExperimentHarnessFreeze?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentHarnessFreezeFormalRetrievalAllowed = scopedRuntimeExperimentHarnessFreeze?.Report.FormalRetrievalAllowed ?? false,
                    ScopedRuntimeExperimentHarnessFreezeRuntimeSwitchAllowed = scopedRuntimeExperimentHarnessFreeze?.Report.RuntimeSwitchAllowed ?? false,
                    ScopedRuntimeExperimentHarnessFreezeReadyForRuntimeSwitch = scopedRuntimeExperimentHarnessFreeze?.Report.ReadyForRuntimeSwitch ?? false,
                    ScopedRuntimeExperimentHarnessFreezeForbiddenActions = scopedRuntimeExperimentHarnessFreeze?.Report.ForbiddenActions ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentHarnessFreezeBlockedReasons = scopedRuntimeExperimentHarnessFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentPlanSourcePath = guardedScopedRuntimeExperimentPlan?.SourcePath ?? string.Empty,
                    GuardedScopedRuntimeExperimentPlanPassed = guardedScopedRuntimeExperimentPlan?.Report.PlanPassed ?? false,
                    GuardedScopedRuntimeExperimentPlanRecommendation = guardedScopedRuntimeExperimentPlan?.Report.Recommendation ?? string.Empty,
                    GuardedScopedRuntimeExperimentProposalId = guardedScopedRuntimeExperimentPlan?.Report.ProposalId ?? string.Empty,
                    GuardedScopedRuntimeExperimentRequiredApprovalMode = guardedScopedRuntimeExperimentPlan?.Report.RequiredApprovalMode ?? string.Empty,
                    GuardedScopedRuntimeExperimentSelectedScopes = guardedScopedRuntimeExperimentPlan?.Report.SelectedScopes ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentMaxRequestCount = guardedScopedRuntimeExperimentPlan?.Report.MaxRequestCount ?? 0,
                    GuardedScopedRuntimeExperimentMaxDurationMinutes = guardedScopedRuntimeExperimentPlan?.Report.MaxDurationMinutes ?? 0,
                    GuardedScopedRuntimeExperimentKillSwitchPlan = guardedScopedRuntimeExperimentPlan?.Report.KillSwitchPlan ?? string.Empty,
                    GuardedScopedRuntimeExperimentRollbackPlan = guardedScopedRuntimeExperimentPlan?.Report.RollbackPlan ?? string.Empty,
                    GuardedScopedRuntimeExperimentObservationPlan = guardedScopedRuntimeExperimentPlan?.Report.ObservationPlan ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentStopConditions = guardedScopedRuntimeExperimentPlan?.Report.StopConditions ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentForbiddenActions = guardedScopedRuntimeExperimentPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentBlockedReasons = guardedScopedRuntimeExperimentPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentRuntimeApprovalSourcePath = scopedRuntimeExperimentRuntimeApproval?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentRuntimeApprovalGatePassed = scopedRuntimeExperimentRuntimeApproval?.Report.GatePassed ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalRecommendation = scopedRuntimeExperimentRuntimeApproval?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentRuntimeApprovalProposalId = scopedRuntimeExperimentRuntimeApproval?.Report.ProposalId ?? string.Empty,
                    ScopedRuntimeExperimentRuntimeApprovalId = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalId ?? string.Empty,
                    ScopedRuntimeExperimentRuntimeApprovalMode = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalMode ?? string.Empty,
                    ScopedRuntimeExperimentRuntimeApprovalApprovedBy = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovedBy ?? string.Empty,
                    ScopedRuntimeExperimentRuntimeApprovalExists = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalExists ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalExpired = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalExpired ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalRevoked = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalRevoked ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalAcknowledgementsPresent = scopedRuntimeExperimentRuntimeApproval?.Report.RequiredAcknowledgementsPresent ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalRuntimeSwitchAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.RuntimeSwitchAllowed ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalFormalRetrievalAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.FormalRetrievalAllowed ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalReadyForRuntimeSwitch = scopedRuntimeExperimentRuntimeApproval?.Report.ReadyForRuntimeSwitch ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalUseForRuntime = scopedRuntimeExperimentRuntimeApproval?.Report.UseForRuntime ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalFormalPackageWriteAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.FormalPackageWriteAllowed ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalPackingPolicyIntegrationAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.PackingPolicyIntegrationAllowed ?? false,
                    ScopedRuntimeExperimentRuntimeApprovalBlockedReasons = scopedRuntimeExperimentRuntimeApproval?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentActivationPreflightSourcePath = scopedRuntimeExperimentActivationPreflight?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentActivationPreflightPassed = scopedRuntimeExperimentActivationPreflight?.Report.PreflightPassed ?? false,
                    ScopedRuntimeExperimentActivationPreflightRecommendation = scopedRuntimeExperimentActivationPreflight?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentActivationProposalId = scopedRuntimeExperimentActivationPreflight?.Report.ProposalId ?? string.Empty,
                    ScopedRuntimeExperimentActivationApprovalId = scopedRuntimeExperimentActivationPreflight?.Report.ApprovalId ?? string.Empty,
                    ScopedRuntimeExperimentActivationMode = scopedRuntimeExperimentActivationPreflight?.Report.Mode ?? string.Empty,
                    ScopedRuntimeExperimentActivationSelectedScopes = scopedRuntimeExperimentActivationPreflight?.Report.SelectedScopes ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentActivationKillSwitchAvailable = scopedRuntimeExperimentActivationPreflight?.Report.KillSwitchAvailable ?? false,
                    ScopedRuntimeExperimentActivationRollbackPlanAvailable = scopedRuntimeExperimentActivationPreflight?.Report.RollbackPlanAvailable ?? false,
                    ScopedRuntimeExperimentActivationTraceSinkAvailable = scopedRuntimeExperimentActivationPreflight?.Report.TraceSinkAvailable ?? false,
                    ScopedRuntimeExperimentActivationConfigPatchPreviewed = scopedRuntimeExperimentActivationPreflight?.Report.ConfigPatchPreviewed ?? false,
                    ScopedRuntimeExperimentActivationConfigPatchWritten = scopedRuntimeExperimentActivationPreflight?.Report.ConfigPatchWritten ?? false,
                    ScopedRuntimeExperimentActivationDryRunRouteExecuted = scopedRuntimeExperimentActivationPreflight?.Report.RuntimeRouteDryRunExecuted ?? false,
                    ScopedRuntimeExperimentActivationDryRunRouteHitCount = scopedRuntimeExperimentActivationPreflight?.Report.DryRunRouteHitCount ?? 0,
                    ScopedRuntimeExperimentActivationNonAllowlistedScopeChecked = scopedRuntimeExperimentActivationPreflight?.Report.NonAllowlistedScopeChecked ?? false,
                    ScopedRuntimeExperimentActivationScopeLeakCount = scopedRuntimeExperimentActivationPreflight?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    ScopedRuntimeExperimentActivationRuntimeMutated = scopedRuntimeExperimentActivationPreflight?.Report.RuntimeMutated ?? false,
                    ScopedRuntimeExperimentActivationVectorStoreBindingChanged = scopedRuntimeExperimentActivationPreflight?.Report.VectorStoreBindingChanged ?? false,
                    ScopedRuntimeExperimentActivationFormalPackageWritten = scopedRuntimeExperimentActivationPreflight?.Report.FormalPackageWritten ?? false,
                    ScopedRuntimeExperimentActivationPackingPolicyChanged = scopedRuntimeExperimentActivationPreflight?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentActivationPackageOutputChanged = scopedRuntimeExperimentActivationPreflight?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentActivationFormalRetrievalAllowed = scopedRuntimeExperimentActivationPreflight?.Report.FormalRetrievalAllowed ?? false,
                    ScopedRuntimeExperimentActivationRuntimeSwitchAllowed = scopedRuntimeExperimentActivationPreflight?.Report.RuntimeSwitchAllowed ?? false,
                    ScopedRuntimeExperimentActivationReadyForRuntimeSwitch = scopedRuntimeExperimentActivationPreflight?.Report.ReadyForRuntimeSwitch ?? false,
                    ScopedRuntimeExperimentActivationRiskAfterPolicy = scopedRuntimeExperimentActivationPreflight?.Report.RiskAfterPolicy ?? 0,
                    ScopedRuntimeExperimentActivationFormalOutputChanged = scopedRuntimeExperimentActivationPreflight?.Report.FormalOutputChanged ?? 0,
                    ScopedRuntimeExperimentActivationBlockedReasons = scopedRuntimeExperimentActivationPreflight?.Report.BlockedReasons ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentRunSourcePath = guardedScopedRuntimeExperiment?.SourcePath ?? string.Empty,
                    GuardedScopedRuntimeExperimentRunPassed = guardedScopedRuntimeExperiment?.Report.ExperimentPassed ?? false,
                    GuardedScopedRuntimeExperimentRunRecommendation = guardedScopedRuntimeExperiment?.Report.Recommendation ?? string.Empty,
                    GuardedScopedRuntimeExperimentRunProposalId = guardedScopedRuntimeExperiment?.Report.ProposalId ?? string.Empty,
                    GuardedScopedRuntimeExperimentRunApprovalId = guardedScopedRuntimeExperiment?.Report.ApprovalId ?? string.Empty,
                    GuardedScopedRuntimeExperimentRunMode = guardedScopedRuntimeExperiment?.Report.Mode ?? string.Empty,
                    GuardedScopedRuntimeExperimentRunSelectedScopes = guardedScopedRuntimeExperiment?.Report.SelectedScopes ?? Array.Empty<string>(),
                    GuardedScopedRuntimeExperimentRunRequestCount = guardedScopedRuntimeExperiment?.Report.RequestCount ?? 0,
                    GuardedScopedRuntimeExperimentRunRouteHitCount = guardedScopedRuntimeExperiment?.Report.ExperimentRouteHitCount ?? 0,
                    GuardedScopedRuntimeExperimentRunNonAllowlistedLeakCount = guardedScopedRuntimeExperiment?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    GuardedScopedRuntimeExperimentRunRiskAfterPolicy = guardedScopedRuntimeExperiment?.Report.RiskAfterPolicy ?? 0,
                    GuardedScopedRuntimeExperimentRunFormalOutputChanged = guardedScopedRuntimeExperiment?.Report.FormalOutputChanged ?? 0,
                    GuardedScopedRuntimeExperimentRunPackageOutputChanged = guardedScopedRuntimeExperiment?.Report.PackageOutputChanged ?? false,
                    GuardedScopedRuntimeExperimentRunPackingPolicyChanged = guardedScopedRuntimeExperiment?.Report.PackingPolicyChanged ?? false,
                    GuardedScopedRuntimeExperimentRunRuntimeMutated = guardedScopedRuntimeExperiment?.Report.RuntimeMutated ?? false,
                    GuardedScopedRuntimeExperimentRunVectorStoreBindingChanged = guardedScopedRuntimeExperiment?.Report.VectorStoreBindingChanged ?? false,
                    GuardedScopedRuntimeExperimentRunFormalPackageWritten = guardedScopedRuntimeExperiment?.Report.FormalPackageWritten ?? false,
                    GuardedScopedRuntimeExperimentRunKillSwitchAvailable = guardedScopedRuntimeExperiment?.Report.KillSwitchAvailable ?? false,
                    GuardedScopedRuntimeExperimentRunKillSwitchTriggered = guardedScopedRuntimeExperiment?.Report.KillSwitchTriggered ?? false,
                    GuardedScopedRuntimeExperimentRunRollbackVerified = guardedScopedRuntimeExperiment?.Report.RollbackVerified ?? false,
                    GuardedScopedRuntimeExperimentRunErrorCount = guardedScopedRuntimeExperiment?.Report.ErrorCount ?? 0,
                    GuardedScopedRuntimeExperimentRunBlockedReasons = guardedScopedRuntimeExperiment?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentObservationWindowSourcePath = scopedRuntimeExperimentObservationWindow?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentObservationWindowId = scopedRuntimeExperimentObservationWindow?.Report.ObservationWindowId ?? string.Empty,
                    ScopedRuntimeExperimentObservationWindowPassed = scopedRuntimeExperimentObservationWindow?.Report.ObservationPassed ?? false,
                    ScopedRuntimeExperimentObservationWindowRecommendation = scopedRuntimeExperimentObservationWindow?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentObservationWindowRunCount = scopedRuntimeExperimentObservationWindow?.Report.ObservationRunCount ?? 0,
                    ScopedRuntimeExperimentObservationWindowRequestCount = scopedRuntimeExperimentObservationWindow?.Report.RequestCount ?? 0,
                    ScopedRuntimeExperimentObservationWindowRouteHitCount = scopedRuntimeExperimentObservationWindow?.Report.ExperimentRouteHitCount ?? 0,
                    ScopedRuntimeExperimentObservationWindowScopeLeakCount = scopedRuntimeExperimentObservationWindow?.Report.NonAllowlistedScopeLeakCount ?? 0,
                    ScopedRuntimeExperimentObservationWindowRiskAfterPolicy = scopedRuntimeExperimentObservationWindow?.Report.RiskAfterPolicy ?? 0,
                    ScopedRuntimeExperimentObservationWindowFormalOutputChanged = scopedRuntimeExperimentObservationWindow?.Report.FormalOutputChanged ?? 0,
                    ScopedRuntimeExperimentObservationWindowPackageOutputChanged = scopedRuntimeExperimentObservationWindow?.Report.PackageOutputChanged ?? false,
                    ScopedRuntimeExperimentObservationWindowPackingPolicyChanged = scopedRuntimeExperimentObservationWindow?.Report.PackingPolicyChanged ?? false,
                    ScopedRuntimeExperimentObservationWindowRuntimeMutated = scopedRuntimeExperimentObservationWindow?.Report.RuntimeMutated ?? false,
                    ScopedRuntimeExperimentObservationWindowVectorStoreBindingChanged = scopedRuntimeExperimentObservationWindow?.Report.VectorStoreBindingChanged ?? false,
                    ScopedRuntimeExperimentObservationWindowFormalPackageWritten = scopedRuntimeExperimentObservationWindow?.Report.FormalPackageWritten ?? false,
                    ScopedRuntimeExperimentObservationWindowKillSwitchAvailable = scopedRuntimeExperimentObservationWindow?.Report.KillSwitchAvailable ?? false,
                    ScopedRuntimeExperimentObservationWindowKillSwitchSmokePassed = scopedRuntimeExperimentObservationWindow?.Report.KillSwitchSmokePassed ?? false,
                    ScopedRuntimeExperimentObservationWindowRollbackVerified = scopedRuntimeExperimentObservationWindow?.Report.RollbackVerified ?? false,
                    ScopedRuntimeExperimentObservationWindowTraceCompleteness = scopedRuntimeExperimentObservationWindow?.Report.TraceCompleteness ?? 0,
                    ScopedRuntimeExperimentObservationWindowErrorCount = scopedRuntimeExperimentObservationWindow?.Report.ErrorCount ?? 0,
                    ScopedRuntimeExperimentObservationWindowLatencyP95 = scopedRuntimeExperimentObservationWindow?.Report.LatencyP95 ?? 0,
                    ScopedRuntimeExperimentObservationWindowBlockedReasons = scopedRuntimeExperimentObservationWindow?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ScopedRuntimeExperimentObservationFreezeSourcePath = scopedRuntimeExperimentObservationFreeze?.SourcePath ?? string.Empty,
                    ScopedRuntimeExperimentObservationFreezePassed = scopedRuntimeExperimentObservationFreeze?.Report.FreezePassed ?? false,
                    ScopedRuntimeExperimentPromotionDecision = scopedRuntimeExperimentObservationFreeze?.Report.PromotionDecision ?? string.Empty,
                    ScopedRuntimeExperimentObservationFreezeRecommendation = scopedRuntimeExperimentObservationFreeze?.Report.Recommendation ?? string.Empty,
                    ScopedRuntimeExperimentObservationFreezeWindowId = scopedRuntimeExperimentObservationFreeze?.Report.ObservationWindowId ?? string.Empty,
                    ScopedRuntimeExperimentObservationFreezeRequestCount = scopedRuntimeExperimentObservationFreeze?.Report.RequestCount ?? 0,
                    ScopedRuntimeExperimentObservationFreezeRouteHitCount = scopedRuntimeExperimentObservationFreeze?.Report.ExperimentRouteHitCount ?? 0,
                    ScopedRuntimeExperimentObservationFreezeRiskAfterPolicy = scopedRuntimeExperimentObservationFreeze?.Report.RiskAfterPolicy ?? 0,
                    ScopedRuntimeExperimentObservationFreezeFormalOutputChanged = scopedRuntimeExperimentObservationFreeze?.Report.FormalOutputChanged ?? 0,
                    ScopedRuntimeExperimentObservationFreezeTraceCompleteness = scopedRuntimeExperimentObservationFreeze?.Report.TraceCompleteness ?? 0,
                    ScopedRuntimeExperimentObservationFreezeFormalRetrievalAllowed = scopedRuntimeExperimentObservationFreeze?.Report.FormalRetrievalAllowed ?? false,
                    ScopedRuntimeExperimentObservationFreezeRuntimeSwitchAllowed = scopedRuntimeExperimentObservationFreeze?.Report.RuntimeSwitchAllowed ?? false,
                    ScopedRuntimeExperimentObservationFreezeBlockedReasons = scopedRuntimeExperimentObservationFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationPlanSourcePath = formalRetrievalIntegrationPlan?.SourcePath ?? string.Empty,
                    FormalRetrievalIntegrationPlanPassed = formalRetrievalIntegrationPlan?.Report.PlanPassed ?? false,
                    FormalRetrievalIntegrationPlanRecommendation = formalRetrievalIntegrationPlan?.Report.Recommendation ?? string.Empty,
                    FormalRetrievalIntegrationPlanAllowedMode = formalRetrievalIntegrationPlan?.Report.AllowedMode ?? string.Empty,
                    FormalRetrievalIntegrationPlanRequiredNextPhase = formalRetrievalIntegrationPlan?.Report.RequiredNextPhase ?? string.Empty,
                    FormalRetrievalIntegrationPlanFormalRetrievalAllowed = formalRetrievalIntegrationPlan?.Report.FormalRetrievalAllowed ?? false,
                    FormalRetrievalIntegrationPlanRuntimeSwitchAllowed = formalRetrievalIntegrationPlan?.Report.RuntimeSwitchAllowed ?? false,
                    FormalRetrievalIntegrationPlanReadyForRuntimeSwitch = formalRetrievalIntegrationPlan?.Report.ReadyForRuntimeSwitch ?? false,
                    FormalRetrievalIntegrationPlanIntegrationPoints = formalRetrievalIntegrationPlan?.Report.IntegrationPoints ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationPlanBlockedReasons = formalRetrievalIntegrationPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationDecisionSourcePath = formalRetrievalIntegrationDecision?.SourcePath ?? string.Empty,
                    FormalRetrievalIntegrationDecisionPassed = formalRetrievalIntegrationDecision?.Report.DecisionPassed ?? false,
                    FormalRetrievalIntegrationDecisionGatePassed = formalRetrievalIntegrationDecision?.Report.GatePassed ?? false,
                    FormalRetrievalIntegrationDecisionRecommendation = formalRetrievalIntegrationDecision?.Report.Recommendation ?? string.Empty,
                    FormalRetrievalIntegrationDecisionValue = formalRetrievalIntegrationDecision?.Report.IntegrationDecision ?? string.Empty,
                    FormalRetrievalIntegrationDecisionNextAllowedPhase = formalRetrievalIntegrationDecision?.Report.NextAllowedPhase ?? string.Empty,
                    FormalRetrievalIntegrationDecisionReadyForFreeze = formalRetrievalIntegrationDecision?.Report.ReadyForFormalRetrievalIntegrationFreeze ?? false,
                    FormalRetrievalIntegrationDecisionReadyForNoOpBindingPlan = formalRetrievalIntegrationDecision?.Report.ReadyForAdapterNoOpBindingPlan ?? false,
                    FormalRetrievalIntegrationDecisionFormalRetrievalAllowed = formalRetrievalIntegrationDecision?.Report.FormalRetrievalAllowed ?? false,
                    FormalRetrievalIntegrationDecisionRuntimeSwitchAllowed = formalRetrievalIntegrationDecision?.Report.RuntimeSwitchAllowed ?? false,
                    FormalRetrievalIntegrationDecisionReadyForRuntimeSwitch = formalRetrievalIntegrationDecision?.Report.ReadyForRuntimeSwitch ?? false,
                    FormalRetrievalIntegrationDecisionRiskAfterPolicy = formalRetrievalIntegrationDecision?.Report.RiskAfterPolicy ?? 0,
                    FormalRetrievalIntegrationDecisionFormalOutputChanged = formalRetrievalIntegrationDecision?.Report.FormalOutputChanged ?? 0,
                    FormalRetrievalIntegrationDecisionPackageOutputChanged = formalRetrievalIntegrationDecision?.Report.PackageOutputChanged ?? false,
                    FormalRetrievalIntegrationDecisionPackingPolicyChanged = formalRetrievalIntegrationDecision?.Report.PackingPolicyChanged ?? false,
                    FormalRetrievalIntegrationDecisionRuntimeMutated = formalRetrievalIntegrationDecision?.Report.RuntimeMutated ?? false,
                    FormalRetrievalIntegrationDecisionVectorStoreBindingChanged = formalRetrievalIntegrationDecision?.Report.VectorStoreBindingChanged ?? false,
                    FormalRetrievalIntegrationDecisionBlockedReasons = formalRetrievalIntegrationDecision?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowFormalRetrievalAdapterPlanSourcePath = shadowFormalRetrievalAdapterPlan?.SourcePath ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanPassed = shadowFormalRetrievalAdapterPlan?.Report.PlanPassed ?? false,
                    ShadowFormalRetrievalAdapterPlanRecommendation = shadowFormalRetrievalAdapterPlan?.Report.Recommendation ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanAllowedMode = shadowFormalRetrievalAdapterPlan?.Report.AllowedMode ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanVectorProviderSource = shadowFormalRetrievalAdapterPlan?.Report.VectorProviderSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanGraphCandidateSource = shadowFormalRetrievalAdapterPlan?.Report.GraphCandidateSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterPlanFormalRetrievalAllowed = shadowFormalRetrievalAdapterPlan?.Report.FormalRetrievalAllowed ?? false,
                    ShadowFormalRetrievalAdapterPlanRuntimeSwitchAllowed = shadowFormalRetrievalAdapterPlan?.Report.RuntimeSwitchAllowed ?? false,
                    ShadowFormalRetrievalAdapterPlanForbiddenActions = shadowFormalRetrievalAdapterPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                    ShadowFormalRetrievalAdapterPlanBlockedReasons = shadowFormalRetrievalAdapterPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowFormalRetrievalAdapterSourcePath = shadowFormalRetrievalAdapter?.SourcePath ?? string.Empty,
                    ShadowFormalRetrievalAdapterPassed = shadowFormalRetrievalAdapter?.Report.AdapterPassed ?? false,
                    ShadowFormalRetrievalAdapterGatePassed = shadowFormalRetrievalAdapter?.Report.GatePassed ?? false,
                    ShadowFormalRetrievalAdapterRecommendation = shadowFormalRetrievalAdapter?.Report.Recommendation ?? string.Empty,
                    ShadowFormalRetrievalAdapterAllowedMode = shadowFormalRetrievalAdapter?.Report.AllowedMode ?? string.Empty,
                    ShadowFormalRetrievalAdapterVectorProviderSource = shadowFormalRetrievalAdapter?.Report.VectorProviderSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterGraphCandidateSource = shadowFormalRetrievalAdapter?.Report.GraphCandidateSource ?? string.Empty,
                    ShadowFormalRetrievalAdapterSampleCount = shadowFormalRetrievalAdapter?.Report.SampleCount ?? 0,
                    ShadowFormalRetrievalAdapterRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.RiskAfterPolicy ?? 0,
                    ShadowFormalRetrievalAdapterMustNotHitRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    ShadowFormalRetrievalAdapterLifecycleRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.LifecycleRiskAfterPolicy ?? 0,
                    ShadowFormalRetrievalAdapterFormalOutputChanged = shadowFormalRetrievalAdapter?.Report.FormalOutputChanged ?? 0,
                    ShadowFormalRetrievalAdapterFormalSelectedSetChanged = shadowFormalRetrievalAdapter?.Report.FormalSelectedSetChanged ?? false,
                    ShadowFormalRetrievalAdapterPackageOutputChanged = shadowFormalRetrievalAdapter?.Report.PackageOutputChanged ?? false,
                    ShadowFormalRetrievalAdapterPackingPolicyChanged = shadowFormalRetrievalAdapter?.Report.PackingPolicyChanged ?? false,
                    ShadowFormalRetrievalAdapterRuntimeMutated = shadowFormalRetrievalAdapter?.Report.RuntimeMutated ?? false,
                    ShadowFormalRetrievalAdapterVectorStoreBindingChanged = shadowFormalRetrievalAdapter?.Report.VectorStoreBindingChanged ?? false,
                    ShadowFormalRetrievalAdapterBlockedReasons = shadowFormalRetrievalAdapter?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalAdapterPackageShadowComparisonSourcePath = formalAdapterPackageShadowComparison?.SourcePath ?? string.Empty,
                    FormalAdapterPackageShadowComparisonPassed = formalAdapterPackageShadowComparison?.Report.ComparisonPassed ?? false,
                    FormalAdapterPackageShadowComparisonGatePassed = formalAdapterPackageShadowComparison?.Report.GatePassed ?? false,
                    FormalAdapterPackageShadowComparisonRecommendation = formalAdapterPackageShadowComparison?.Report.Recommendation ?? string.Empty,
                    FormalAdapterPackageShadowComparisonAllowedMode = formalAdapterPackageShadowComparison?.Report.AllowedMode ?? string.Empty,
                    FormalAdapterPackageShadowComparisonSampleCount = formalAdapterPackageShadowComparison?.Report.SampleCount ?? 0,
                    FormalAdapterPackageShadowComparisonRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.RiskAfterPolicy ?? 0,
                    FormalAdapterPackageShadowComparisonMustNotHitRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    FormalAdapterPackageShadowComparisonLifecycleRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaTotal ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaMax = formalAdapterPackageShadowComparison?.Report.TokenDeltaMax ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaBudgetTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetTotal ?? 0,
                    FormalAdapterPackageShadowComparisonTokenDeltaBudgetPerSample = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetPerSample ?? 0,
                    FormalAdapterPackageShadowComparisonFormalOutputChanged = formalAdapterPackageShadowComparison?.Report.FormalOutputChanged ?? 0,
                    FormalAdapterPackageShadowComparisonFormalSelectedSetChanged = formalAdapterPackageShadowComparison?.Report.FormalSelectedSetChanged ?? false,
                    FormalAdapterPackageShadowComparisonPackageOutputChanged = formalAdapterPackageShadowComparison?.Report.PackageOutputChanged ?? false,
                    FormalAdapterPackageShadowComparisonPackingPolicyChanged = formalAdapterPackageShadowComparison?.Report.PackingPolicyChanged ?? false,
                    FormalAdapterPackageShadowComparisonRuntimeMutated = formalAdapterPackageShadowComparison?.Report.RuntimeMutated ?? false,
                    FormalAdapterPackageShadowComparisonVectorStoreBindingChanged = formalAdapterPackageShadowComparison?.Report.VectorStoreBindingChanged ?? false,
                    FormalAdapterPackageShadowComparisonBlockedReasons = formalAdapterPackageShadowComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                    GraphVectorRetrievalQualityAuditSourcePath = graphVectorRetrievalQualityAudit?.SourcePath ?? string.Empty,
                    GraphVectorRetrievalQualityAuditPassed = graphVectorRetrievalQualityAudit?.Report.AuditPassed ?? false,
                    GraphVectorRetrievalQualityAuditGatePassed = graphVectorRetrievalQualityAudit?.Report.GatePassed ?? false,
                    GraphVectorRetrievalQualityAuditRecommendation = graphVectorRetrievalQualityAudit?.Report.Recommendation ?? string.Empty,
                    GraphVectorRetrievalQualityAuditAllowedMode = graphVectorRetrievalQualityAudit?.Report.AllowedMode ?? string.Empty,
                    GraphVectorRetrievalQualityAuditSampleCount = graphVectorRetrievalQualityAudit?.Report.SampleCount ?? 0,
                    GraphVectorRetrievalQualityAuditRecall = graphVectorRetrievalQualityAudit?.Report.Recall ?? 0,
                    GraphVectorRetrievalQualityAuditPrecision = graphVectorRetrievalQualityAudit?.Report.Precision ?? 0,
                    GraphVectorRetrievalQualityAuditMrr = graphVectorRetrievalQualityAudit?.Report.MeanReciprocalRank ?? 0,
                    GraphVectorRetrievalQualityAuditGraphNoiseCount = graphVectorRetrievalQualityAudit?.Report.GraphNoiseCount ?? 0,
                    GraphVectorRetrievalQualityAuditVectorNoiseCount = graphVectorRetrievalQualityAudit?.Report.VectorNoiseCount ?? 0,
                    GraphVectorRetrievalQualityAuditRankingRegressionCount = graphVectorRetrievalQualityAudit?.Report.RankingRegressionCount ?? 0,
                    GraphVectorRetrievalQualityAuditMustHitBelowTopKCount = graphVectorRetrievalQualityAudit?.Report.MustHitBelowTopKCount ?? 0,
                    GraphVectorRetrievalQualityAuditRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.RiskAfterPolicy ?? 0,
                    GraphVectorRetrievalQualityAuditMustNotHitRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    GraphVectorRetrievalQualityAuditLifecycleRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.LifecycleRiskAfterPolicy ?? 0,
                    GraphVectorRetrievalQualityAuditSectionMismatchCount = graphVectorRetrievalQualityAudit?.Report.SectionMismatchCount ?? 0,
                    GraphVectorRetrievalQualityAuditMetadataEvidenceGapCount = graphVectorRetrievalQualityAudit?.Report.MetadataEvidenceGapCount ?? 0,
                    GraphVectorRetrievalQualityAuditFailureClusterIds = graphVectorRetrievalQualityAudit?.Report.FailureClusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>(),
                    GraphVectorRetrievalQualityAuditFormalOutputChanged = graphVectorRetrievalQualityAudit?.Report.FormalOutputChanged ?? 0,
                    GraphVectorRetrievalQualityAuditFormalSelectedSetChanged = graphVectorRetrievalQualityAudit?.Report.FormalSelectedSetChanged ?? false,
                    GraphVectorRetrievalQualityAuditPackageOutputChanged = graphVectorRetrievalQualityAudit?.Report.PackageOutputChanged ?? false,
                    GraphVectorRetrievalQualityAuditPackingPolicyChanged = graphVectorRetrievalQualityAudit?.Report.PackingPolicyChanged ?? false,
                    GraphVectorRetrievalQualityAuditRuntimeMutated = graphVectorRetrievalQualityAudit?.Report.RuntimeMutated ?? false,
                    GraphVectorRetrievalQualityAuditVectorStoreBindingChanged = graphVectorRetrievalQualityAudit?.Report.VectorStoreBindingChanged ?? false,
                    GraphVectorRetrievalQualityAuditBlockedReasons = graphVectorRetrievalQualityAudit?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RetrievalQualityRepairPreviewSourcePath = retrievalQualityRepairPreview?.SourcePath ?? string.Empty,
                    RetrievalQualityRepairPreviewPassed = retrievalQualityRepairPreview?.Report.PreviewPassed ?? false,
                    RetrievalQualityRepairPreviewGatePassed = retrievalQualityRepairPreview?.Report.GatePassed ?? false,
                    RetrievalQualityRepairPreviewRecommendation = retrievalQualityRepairPreview?.Report.Recommendation ?? string.Empty,
                    RetrievalQualityRepairPreviewAllowedMode = retrievalQualityRepairPreview?.Report.AllowedMode ?? string.Empty,
                    RetrievalQualityRepairPreviewBestProfileId = retrievalQualityRepairPreview?.Report.BestProfileId ?? string.Empty,
                    RetrievalQualityRepairPreviewBaselineRecall = retrievalQualityRepairPreview?.Report.Baseline.Recall ?? 0d,
                    RetrievalQualityRepairPreviewBaselinePrecision = retrievalQualityRepairPreview?.Report.Baseline.Precision ?? 0d,
                    RetrievalQualityRepairPreviewBaselineMrr = retrievalQualityRepairPreview?.Report.Baseline.MeanReciprocalRank ?? 0d,
                    RetrievalQualityRepairPreviewBestRecall = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Recall ?? 0d,
                    RetrievalQualityRepairPreviewBestPrecision = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Precision ?? 0d,
                    RetrievalQualityRepairPreviewBestMrr = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MeanReciprocalRank ?? 0d,
                    RetrievalQualityRepairPreviewRecallDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.RecallDelta ?? 0d,
                    RetrievalQualityRepairPreviewMrrDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MrrDelta ?? 0d,
                    RetrievalQualityRepairPreviewMustHitBelowTopKBaseline = retrievalQualityRepairPreview?.Report.Baseline.MustHitBelowTopKCount ?? 0,
                    RetrievalQualityRepairPreviewMustHitBelowTopKBest = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MustHitBelowTopKCount ?? 0,
                    RetrievalQualityRepairPreviewProfileEvaluatedCount = retrievalQualityRepairPreview?.Report.Profiles.Count ?? 0,
                    RetrievalQualityRepairPreviewRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.RiskAfterPolicy ?? 0,
                    RetrievalQualityRepairPreviewMustNotHitRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.MustNotHitRiskAfterPolicy ?? 0,
                    RetrievalQualityRepairPreviewLifecycleRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.LifecycleRiskAfterPolicy ?? 0,
                    RetrievalQualityRepairPreviewSectionMismatchCount = retrievalQualityRepairPreview?.Report.Baseline.SectionMismatchCount ?? 0,
                    RetrievalQualityRepairPreviewGraphNoiseCount = retrievalQualityRepairPreview?.Report.Baseline.GraphNoiseCount ?? 0,
                    RetrievalQualityRepairPreviewRankingRegressionCount = retrievalQualityRepairPreview?.Report.Baseline.RankingRegressionCount ?? 0,
                    RetrievalQualityRepairPreviewTokenDeltaTotal = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDelta ?? 0,
                    RetrievalQualityRepairPreviewTokenDeltaMax = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDeltaAbsolute ?? 0,
                    RetrievalQualityRepairPreviewFormalOutputChanged = retrievalQualityRepairPreview?.Report.FormalOutputChanged ?? 0,
                    RetrievalQualityRepairPreviewFormalSelectedSetChanged = retrievalQualityRepairPreview?.Report.FormalSelectedSetChanged ?? false,
                    RetrievalQualityRepairPreviewPackageOutputChanged = retrievalQualityRepairPreview?.Report.PackageOutputChanged ?? false,
                    RetrievalQualityRepairPreviewPackingPolicyChanged = retrievalQualityRepairPreview?.Report.PackingPolicyChanged ?? false,
                    RetrievalQualityRepairPreviewRuntimeMutated = retrievalQualityRepairPreview?.Report.RuntimeMutated ?? false,
                    RetrievalQualityRepairPreviewVectorStoreBindingChanged = retrievalQualityRepairPreview?.Report.VectorStoreBindingChanged ?? false,
                    RetrievalQualityRepairPreviewBlockedReasons = retrievalQualityRepairPreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RuntimeObservableFeatureContractSourcePath = runtimeObservableFeatureContract?.SourcePath ?? string.Empty,
                    RuntimeObservableFeatureContractPassed = runtimeObservableFeatureContract?.Report.ContractPassed ?? false,
                    RuntimeObservableFeatureContractGatePassed = runtimeObservableFeatureContract?.Report.GatePassed ?? false,
                    RuntimeObservableFeatureContractRecommendation = runtimeObservableFeatureContract?.Report.Recommendation ?? string.Empty,
                    RuntimeObservableFeatureContractAllowedMode = runtimeObservableFeatureContract?.Report.AllowedMode ?? string.Empty,
                    RuntimeObservableFeatureContractBestProfileId = runtimeObservableFeatureContract?.Report.BestProfileId ?? string.Empty,
                    RuntimeObservableFeatureContractBestProfileContractStatus = runtimeObservableFeatureContract?.Report.BestProfileContractStatus ?? string.Empty,
                    RuntimeObservableFeatureContractForbiddenForScoringCount = runtimeObservableFeatureContract?.Report.ForbiddenForScoringCount ?? 0,
                    RuntimeObservableFeatureContractEvalOnlyCount = runtimeObservableFeatureContract?.Report.EvalOnlyCount ?? 0,
                    RuntimeObservableFeatureContractDerivedAtRuntimeCount = runtimeObservableFeatureContract?.Report.DerivedAtRuntimeCount ?? 0,
                    RuntimeObservableFeatureContractRuntimeObservableCount = runtimeObservableFeatureContract?.Report.RuntimeObservableCount ?? 0,
                    RuntimeObservableFeatureContractScoringFeatureCount = runtimeObservableFeatureContract?.Report.ScoringFeatureCount ?? 0,
                    RuntimeObservableFeatureContractFilteringFeatureCount = runtimeObservableFeatureContract?.Report.FilteringFeatureCount ?? 0,
                    RuntimeObservableFeatureContractCandidateExpansionFeatureCount = runtimeObservableFeatureContract?.Report.CandidateExpansionFeatureCount ?? 0,
                    RuntimeObservableFeatureContractSourceScanFiles = runtimeObservableFeatureContract?.Report.SourceScan.ScannedFileCount ?? 0,
                    RuntimeObservableFeatureContractFixtureTokenHitCount = runtimeObservableFeatureContract?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                    RuntimeObservableFeatureContractFlaggedTokens = runtimeObservableFeatureContract?.Report.SourceScan.FlaggedTokens ?? Array.Empty<string>(),
                    RuntimeObservableFeatureContractFormalOutputChanged = runtimeObservableFeatureContract?.Report.FormalOutputChanged ?? 0,
                    RuntimeObservableFeatureContractFormalSelectedSetChanged = runtimeObservableFeatureContract?.Report.FormalSelectedSetChanged ?? false,
                    RuntimeObservableFeatureContractPackageOutputChanged = runtimeObservableFeatureContract?.Report.PackageOutputChanged ?? false,
                    RuntimeObservableFeatureContractPackingPolicyChanged = runtimeObservableFeatureContract?.Report.PackingPolicyChanged ?? false,
                    RuntimeObservableFeatureContractRuntimeMutated = runtimeObservableFeatureContract?.Report.RuntimeMutated ?? false,
                    RuntimeObservableFeatureContractVectorStoreBindingChanged = runtimeObservableFeatureContract?.Report.VectorStoreBindingChanged ?? false,
                    RuntimeObservableFeatureContractBlockedReasons = runtimeObservableFeatureContract?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RuntimeRetrievalFeatureDerivationSourcePath = runtimeRetrievalFeatureDerivation?.SourcePath ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationPassed = runtimeRetrievalFeatureDerivation?.Report.PreviewPassed ?? false,
                    RuntimeRetrievalFeatureDerivationGatePassed = runtimeRetrievalFeatureDerivation?.Report.GatePassed ?? false,
                    RuntimeRetrievalFeatureDerivationRecommendation = runtimeRetrievalFeatureDerivation?.Report.Recommendation ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationAllowedMode = runtimeRetrievalFeatureDerivation?.Report.AllowedMode ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationSampleCount = runtimeRetrievalFeatureDerivation?.Report.SampleCount ?? 0,
                    RuntimeRetrievalFeatureDerivationTargetSectionMatchRate = runtimeRetrievalFeatureDerivation?.Report.TargetSectionMatchRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRequiredRelationCoverageRate = runtimeRetrievalFeatureDerivation?.Report.RequiredRelationCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationEvidenceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.EvidenceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationSourceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.SourceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivationCompletenessRate = runtimeRetrievalFeatureDerivation?.Report.DerivationCompletenessRate ?? 0,
                    RuntimeRetrievalFeatureDerivationBaselineRecall = runtimeRetrievalFeatureDerivation?.Report.BaselineRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationBaselineMrr = runtimeRetrievalFeatureDerivation?.Report.BaselineMeanReciprocalRank ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedRecall = runtimeRetrievalFeatureDerivation?.Report.DerivedRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedMrr = runtimeRetrievalFeatureDerivation?.Report.DerivedMeanReciprocalRank ?? 0,
                    RuntimeRetrievalFeatureDerivationEvalDrivenRecall = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationEvalDrivenMrr = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenMeanReciprocalRank ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedRecallDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedRecallDelta ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedMrrDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedMrrDelta ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedMustNotHitRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedMustNotHitRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedLifecycleRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedLifecycleRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationDerivedSectionMismatchCount = runtimeRetrievalFeatureDerivation?.Report.DerivedSectionMismatchCount ?? 0,
                    RuntimeRetrievalFeatureDerivationForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivation?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                    RuntimeRetrievalFeatureDerivationSourceScanFiles = runtimeRetrievalFeatureDerivation?.Report.SourceScan.ScannedFileCount ?? 0,
                    RuntimeRetrievalFeatureDerivationFixtureTokenHitCount = runtimeRetrievalFeatureDerivation?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                    RuntimeRetrievalFeatureDerivationFormalOutputChanged = runtimeRetrievalFeatureDerivation?.Report.FormalOutputChanged ?? 0,
                    RuntimeRetrievalFeatureDerivationFormalSelectedSetChanged = runtimeRetrievalFeatureDerivation?.Report.FormalSelectedSetChanged ?? false,
                    RuntimeRetrievalFeatureDerivationPackageOutputChanged = runtimeRetrievalFeatureDerivation?.Report.PackageOutputChanged ?? false,
                    RuntimeRetrievalFeatureDerivationPackingPolicyChanged = runtimeRetrievalFeatureDerivation?.Report.PackingPolicyChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRuntimeMutated = runtimeRetrievalFeatureDerivation?.Report.RuntimeMutated ?? false,
                    RuntimeRetrievalFeatureDerivationVectorStoreBindingChanged = runtimeRetrievalFeatureDerivation?.Report.VectorStoreBindingChanged ?? false,
                    RuntimeRetrievalFeatureDerivationBlockedReasons = runtimeRetrievalFeatureDerivation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RuntimeRetrievalFeatureDerivationRepairSourcePath = runtimeRetrievalFeatureDerivationRepair?.SourcePath ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationRepairPassed = runtimeRetrievalFeatureDerivationRepair?.Report.PreviewPassed ?? false,
                    RuntimeRetrievalFeatureDerivationRepairGatePassed = runtimeRetrievalFeatureDerivationRepair?.Report.GatePassed ?? false,
                    RuntimeRetrievalFeatureDerivationRepairRecommendation = runtimeRetrievalFeatureDerivationRepair?.Report.Recommendation ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationRepairAllowedMode = runtimeRetrievalFeatureDerivationRepair?.Report.AllowedMode ?? string.Empty,
                    RuntimeRetrievalFeatureDerivationRepairTrainSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.TrainSampleCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutSampleCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairTrainDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedRecall ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedMrr ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairCanonicalRelationCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalRequiredRelationCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairCanonicalEvidenceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalEvidenceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairCanonicalSourceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalSourceAnchorCoverageRate ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivationRepair?.Report.DerivedRiskAfterPolicy ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivationRepair?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairSourceScanFiles = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.ScannedFileCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairFixtureTokenHitCount = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairFormalOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalOutputChanged ?? 0,
                    RuntimeRetrievalFeatureDerivationRepairFormalSelectedSetChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalSelectedSetChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairPackageOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackageOutputChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairPackingPolicyChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackingPolicyChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairRuntimeMutated = runtimeRetrievalFeatureDerivationRepair?.Report.RuntimeMutated ?? false,
                    RuntimeRetrievalFeatureDerivationRepairVectorStoreBindingChanged = runtimeRetrievalFeatureDerivationRepair?.Report.VectorStoreBindingChanged ?? false,
                    RuntimeRetrievalFeatureDerivationRepairBlockedReasons = runtimeRetrievalFeatureDerivationRepair?.Report.BlockedReasons ?? Array.Empty<string>(),
                    InputMetadataEnrichmentSourcePath = inputMetadataEnrichment?.SourcePath ?? string.Empty,
                    InputMetadataEnrichmentPreviewPassed = inputMetadataEnrichment?.Report.PreviewPassed ?? false,
                    InputMetadataEnrichmentGatePassed = inputMetadataEnrichment?.Report.GatePassed ?? false,
                    InputMetadataEnrichmentRecommendation = inputMetadataEnrichment?.Report.Recommendation ?? string.Empty,
                    InputMetadataEnrichmentCoverageDelta = inputMetadataEnrichment?.Report.MetadataCoverageDelta ?? 0,
                    InputMetadataEnrichmentBeforeRecall = inputMetadataEnrichment?.Report.BeforeRecall ?? 0,
                    InputMetadataEnrichmentAfterRecall = inputMetadataEnrichment?.Report.AfterRecall ?? 0,
                    InputMetadataEnrichmentIndependentNonDenseSourceCount = inputMetadataEnrichment?.Report.IndependentNonDenseSourceCount ?? 0,
                    InputMetadataEnrichmentRiskAfterPolicy = inputMetadataEnrichment?.Report.RiskAfterPolicy ?? 0,
                    InputMetadataEnrichmentMustNotHitRiskAfterPolicy = inputMetadataEnrichment?.Report.MustNotHitRiskAfterPolicy ?? 0,
                    InputMetadataEnrichmentLifecycleRiskAfterPolicy = inputMetadataEnrichment?.Report.LifecycleRiskAfterPolicy ?? 0,
                    InputMetadataEnrichmentPackageOutputChanged = inputMetadataEnrichment?.Report.PackageOutputChanged ?? false,
                    InputMetadataEnrichmentPackingPolicyChanged = inputMetadataEnrichment?.Report.PackingPolicyChanged ?? false,
                    InputMetadataEnrichmentRuntimeMutated = inputMetadataEnrichment?.Report.RuntimeMutated ?? false,
                    InputMetadataEnrichmentVectorStoreBindingChanged = inputMetadataEnrichment?.Report.VectorStoreBindingChanged ?? false,
                    InputMetadataEnrichmentBlockedReasons = inputMetadataEnrichment?.Report.BlockedReasons ?? Array.Empty<string>(),
                    EnrichedCandidateSourceRepairRecheckSourcePath = enrichedCandidateSourceRepairRecheck?.SourcePath ?? string.Empty,
                    EnrichedCandidateSourceRepairRecheckPassed = enrichedCandidateSourceRepairRecheck?.Report.RecheckPassed ?? false,
                    EnrichedCandidateSourceRepairRecheckGatePassed = enrichedCandidateSourceRepairRecheck?.Report.GatePassed ?? false,
                    EnrichedCandidateSourceRepairRecheckRecommendation = enrichedCandidateSourceRepairRecheck?.Report.Recommendation ?? string.Empty,
                    EnrichedCandidateSourceRepairQualityImproved = enrichedCandidateSourceRepairRecheck?.Report.QualityImproved ?? false,
                    EnrichedCandidateSourceRepairTrainRecallDelta = enrichedCandidateSourceRepairRecheck?.Report.TrainDerivedRecallDelta ?? 0,
                    EnrichedCandidateSourceRepairHoldoutRecallDelta = enrichedCandidateSourceRepairRecheck?.Report.HoldoutDerivedRecallDelta ?? 0,
                    EnrichedCandidateSourceRepairMustHitBelowTopKDelta = enrichedCandidateSourceRepairRecheck?.Report.MustHitBelowTopKDelta ?? 0,
                    EnrichedCandidateSourceRepairRiskAfterPolicy = enrichedCandidateSourceRepairRecheck?.Report.RiskAfterPolicy ?? 0,
                    EnrichedCandidateSourceRepairPackageOutputChanged = enrichedCandidateSourceRepairRecheck?.Report.PackageOutputChanged ?? false,
                    EnrichedCandidateSourceRepairPackingPolicyChanged = enrichedCandidateSourceRepairRecheck?.Report.PackingPolicyChanged ?? false,
                    EnrichedCandidateSourceRepairRuntimeMutated = enrichedCandidateSourceRepairRecheck?.Report.RuntimeMutated ?? false,
                    EnrichedCandidateSourceRepairVectorStoreBindingChanged = enrichedCandidateSourceRepairRecheck?.Report.VectorStoreBindingChanged ?? false,
                    EnrichedCandidateSourceRepairBlockedReasons = enrichedCandidateSourceRepairRecheck?.Report.BlockedReasons ?? Array.Empty<string>(),
                    EnrichedCandidateSourceRepairQualityBlockedReasons = enrichedCandidateSourceRepairRecheck?.Report.QualityBlockedReasons ?? Array.Empty<string>(),
                    SourceAwareRankingRepairSourcePath = sourceAwareRankingRepair?.SourcePath ?? string.Empty,
                    SourceAwareRankingRepairPassed = sourceAwareRankingRepair?.Report.ReportPassed ?? false,
                    SourceAwareRankingRepairGatePassed = sourceAwareRankingRepair?.Report.GatePassed ?? false,
                    SourceAwareRankingRepairRecommendation = sourceAwareRankingRepair?.Report.Recommendation ?? string.Empty,
                    SourceAwareRankingRepairSelectedProfileId = sourceAwareRankingRepair?.Report.SelectedProfileId ?? string.Empty,
                    SourceAwareRankingRepairTrainDevRecallDelta = sourceAwareRankingRepair?.Report.TrainDevRecallDelta ?? 0,
                    SourceAwareRankingRepairTestRecallDelta = sourceAwareRankingRepair?.Report.TestRecallDelta ?? 0,
                    SourceAwareRankingRepairHoldoutRecallDelta = sourceAwareRankingRepair?.Report.HoldoutRecallDelta ?? 0,
                    SourceAwareRankingRepairBlindHoldoutRecallDelta = sourceAwareRankingRepair?.Report.BlindHoldoutRecallDelta ?? 0,
                    SourceAwareRankingRepairDenseWinnerLostCount = sourceAwareRankingRepair?.Report.DenseWinnerLostCount ?? 0,
                    SourceAwareRankingRepairUniqueSourceRecoveryCount = sourceAwareRankingRepair?.Report.UniqueSourceRecoveryCount ?? 0,
                    SourceAwareRankingRepairSourceNoiseCount = sourceAwareRankingRepair?.Report.SourceNoiseCount ?? 0,
                    SourceAwareRankingRepairFallbackRate = sourceAwareRankingRepair?.Report.FallbackRate ?? 0,
                    SourceAwareRankingRepairRiskAfterPolicy = sourceAwareRankingRepair?.Report.RiskAfterPolicy ?? 0,
                    SourceAwareRankingRepairPackageOutputChanged = sourceAwareRankingRepair?.Report.PackageOutputChanged ?? false,
                    SourceAwareRankingRepairPackingPolicyChanged = sourceAwareRankingRepair?.Report.PackingPolicyChanged ?? false,
                    SourceAwareRankingRepairRuntimeMutated = sourceAwareRankingRepair?.Report.RuntimeMutated ?? false,
                    SourceAwareRankingRepairVectorStoreBindingChanged = sourceAwareRankingRepair?.Report.VectorStoreBindingChanged ?? false,
                    SourceAwareRankingRepairBlockedReasons = sourceAwareRankingRepair?.Report.BlockedReasons ?? Array.Empty<string>(),
                    OutputTokenPriorityShadowSourcePath = outputTokenPriorityShadow?.SourcePath ?? string.Empty,
                    OutputTokenPriorityShadowPassed = outputTokenPriorityShadow?.Report.ShadowPassed ?? false,
                    OutputTokenPriorityShadowGatePassed = outputTokenPriorityShadow?.Report.GatePassed ?? false,
                    OutputTokenPriorityShadowRecommendation = outputTokenPriorityShadow?.Report.Recommendation ?? string.Empty,
                    OutputTokenPriorityShadowProfileName = outputTokenPriorityShadow?.Report.ProfileName ?? string.Empty,
                    OutputTokenPriorityShadowTokenDeltaTotal = outputTokenPriorityShadow?.Report.TokenDeltaTotal ?? 0,
                    OutputTokenPriorityShadowTokenDeltaMax = outputTokenPriorityShadow?.Report.TokenDeltaMax ?? 0,
                    OutputTokenPriorityShadowTokenDeltaP95 = outputTokenPriorityShadow?.Report.TokenDeltaP95 ?? 0,
                    OutputTokenPriorityShadowTokenBudgetExceededCount = outputTokenPriorityShadow?.Report.TokenBudgetExceededCount ?? 0,
                    OutputTokenPriorityShadowPriorityInversionCount = outputTokenPriorityShadow?.Report.PriorityInversionCount ?? 0,
                    OutputTokenPriorityShadowDroppedRequiredCandidateCount = outputTokenPriorityShadow?.Report.DroppedRequiredCandidateCount ?? 0,
                    OutputTokenPriorityShadowSectionMismatchCount = outputTokenPriorityShadow?.Report.SectionMismatchCount ?? 0,
                    OutputTokenPriorityShadowRiskAfterPolicy = outputTokenPriorityShadow?.Report.RiskAfterPolicy ?? 0,
                    OutputTokenPriorityShadowFormalSelectedSetChanged = outputTokenPriorityShadow?.Report.FormalSelectedSetChanged ?? false,
                    OutputTokenPriorityShadowPackageOutputChanged = outputTokenPriorityShadow?.Report.PackageOutputChanged ?? false,
                    OutputTokenPriorityShadowPackingPolicyChanged = outputTokenPriorityShadow?.Report.PackingPolicyChanged ?? false,
                    OutputTokenPriorityShadowRuntimeMutated = outputTokenPriorityShadow?.Report.RuntimeMutated ?? false,
                    OutputTokenPriorityShadowVectorStoreBindingChanged = outputTokenPriorityShadow?.Report.VectorStoreBindingChanged ?? false,
                    OutputTokenPriorityShadowBlockedReasons = outputTokenPriorityShadow?.Report.BlockedReasons ?? Array.Empty<string>(),
                    FormalAdapterInputContractSourcePath = formalAdapterInputContract?.SourcePath ?? string.Empty,
                    FormalAdapterInputContractPassed = formalAdapterInputContract?.Report.ContractPassed ?? false,
                    FormalAdapterInputContractGatePassed = formalAdapterInputContract?.Report.GatePassed ?? false,
                    FormalAdapterInputContractRecommendation = formalAdapterInputContract?.Report.Recommendation ?? string.Empty,
                    FormalAdapterInputContractVersion = formalAdapterInputContract?.Report.ContractVersion ?? string.Empty,
                    FormalAdapterInputContractRuntimeInputFieldCount = formalAdapterInputContract?.Report.RuntimeInputFieldCount ?? 0,
                    FormalAdapterInputContractDeniedFieldCount = formalAdapterInputContract?.Report.DeniedFieldCount ?? 0,
                    FormalAdapterInputContractForbiddenPropertyCount = formalAdapterInputContract?.Report.ContractForbiddenPropertyCount ?? 0,
                    FormalAdapterInputContractFormalSourceForbiddenReadCount = formalAdapterInputContract?.Report.FormalSourceForbiddenReadCount ?? 0,
                    FormalAdapterInputContractEvalOnlyForbiddenReadCount = formalAdapterInputContract?.Report.EvalOnlyForbiddenReadCount ?? 0,
                    FormalAdapterInputContractDatasetEvalFieldsBlocked = formalAdapterInputContract?.Report.DatasetEvalFieldsBlocked ?? false,
                    FormalAdapterInputContractGoldLabelsBlocked = formalAdapterInputContract?.Report.GoldLabelsBlocked ?? false,
                    FormalAdapterInputContractSampleMetadataBlocked = formalAdapterInputContract?.Report.SampleMetadataBlocked ?? false,
                    FormalAdapterInputContractShadowArtifactFieldsBlocked = formalAdapterInputContract?.Report.ShadowArtifactFieldsBlocked ?? false,
                    FormalAdapterInputContractFormalRetrievalAllowed = formalAdapterInputContract?.Report.FormalRetrievalAllowed ?? false,
                    FormalAdapterInputContractRuntimeSwitchAllowed = formalAdapterInputContract?.Report.RuntimeSwitchAllowed ?? false,
                    FormalAdapterInputContractRuntimeMutated = formalAdapterInputContract?.Report.RuntimeMutated ?? false,
                    FormalAdapterInputContractPackageOutputChanged = formalAdapterInputContract?.Report.PackageOutputChanged ?? false,
                    FormalAdapterInputContractPackingPolicyChanged = formalAdapterInputContract?.Report.PackingPolicyChanged ?? false,
                    FormalAdapterInputContractVectorStoreBindingChanged = formalAdapterInputContract?.Report.VectorStoreBindingChanged ?? false,
                    FormalAdapterInputContractBlockedReasons = formalAdapterInputContract?.Report.BlockedReasons ?? Array.Empty<string>(),
                    SourceDiverseShadowAdapterValidationSourcePath = sourceDiverseShadowAdapterValidation?.SourcePath ?? string.Empty,
                    SourceDiverseShadowAdapterValidationPassed = sourceDiverseShadowAdapterValidation?.Report.ValidationPassed ?? false,
                    SourceDiverseShadowAdapterValidationGatePassed = sourceDiverseShadowAdapterValidation?.Report.GatePassed ?? false,
                    SourceDiverseShadowAdapterValidationRecommendation = sourceDiverseShadowAdapterValidation?.Report.Recommendation ?? string.Empty,
                    SourceDiverseShadowAdapterValidationSetSourceDiverse = sourceDiverseShadowAdapterValidation?.Report.ValidationSetSourceDiverse ?? false,
                    SourceDiverseShadowAdapterValidationScopeMetadataPresent = sourceDiverseShadowAdapterValidation?.Report.AllowlistedScopeMetadataPresent ?? false,
                    SourceDiverseShadowAdapterValidationSampleCount = sourceDiverseShadowAdapterValidation?.Report.SampleCount ?? 0,
                    SourceDiverseShadowAdapterValidationOverlapRate = sourceDiverseShadowAdapterValidation?.Report.OverlapRate ?? 0,
                    SourceDiverseShadowAdapterValidationShadowOnlyCount = sourceDiverseShadowAdapterValidation?.Report.ShadowOnlyCount ?? 0,
                    SourceDiverseShadowAdapterValidationHypotheticalAddCount = sourceDiverseShadowAdapterValidation?.Report.HypotheticalAddCount ?? 0,
                    SourceDiverseShadowAdapterValidationHypotheticalRemoveCount = sourceDiverseShadowAdapterValidation?.Report.HypotheticalRemoveCount ?? 0,
                    SourceDiverseShadowAdapterValidationAppliedAddCount = sourceDiverseShadowAdapterValidation?.Report.AppliedAddCount ?? 0,
                    SourceDiverseShadowAdapterValidationAppliedRemoveCount = sourceDiverseShadowAdapterValidation?.Report.AppliedRemoveCount ?? 0,
                    SourceDiverseShadowAdapterValidationUniqueSourceRecoveryCount = sourceDiverseShadowAdapterValidation?.Report.UniqueSourceRecoveryCount ?? 0,
                    SourceDiverseShadowAdapterValidationRiskAfterPolicy = sourceDiverseShadowAdapterValidation?.Report.RiskAfterPolicy ?? 0,
                    SourceDiverseShadowAdapterValidationTokenDeltaTotal = sourceDiverseShadowAdapterValidation?.Report.TokenDeltaTotal ?? 0,
                    SourceDiverseShadowAdapterValidationTokenDeltaMax = sourceDiverseShadowAdapterValidation?.Report.TokenDeltaMax ?? 0,
                    SourceDiverseShadowAdapterValidationSectionDeltaCount = sourceDiverseShadowAdapterValidation?.Report.SectionDeltaCount ?? 0,
                    SourceDiverseShadowAdapterValidationPackageOutputChanged = sourceDiverseShadowAdapterValidation?.Report.PackageOutputChanged ?? false,
                    SourceDiverseShadowAdapterValidationPackingPolicyChanged = sourceDiverseShadowAdapterValidation?.Report.PackingPolicyChanged ?? false,
                    SourceDiverseShadowAdapterValidationRuntimeMutated = sourceDiverseShadowAdapterValidation?.Report.RuntimeMutated ?? false,
                    SourceDiverseShadowAdapterValidationVectorStoreBindingChanged = sourceDiverseShadowAdapterValidation?.Report.VectorStoreBindingChanged ?? false,
                    SourceDiverseShadowAdapterValidationBlockedReasons = sourceDiverseShadowAdapterValidation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowCandidateMergePreviewSourcePath = shadowCandidateMergePreview?.SourcePath ?? string.Empty,
                    ShadowCandidateMergePreviewPassed = shadowCandidateMergePreview?.Report.PreviewPassed ?? false,
                    ShadowCandidateMergePreviewGatePassed = shadowCandidateMergePreview?.Report.GatePassed ?? false,
                    ShadowCandidateMergePreviewRecommendation = shadowCandidateMergePreview?.Report.Recommendation ?? string.Empty,
                    ShadowCandidateMergePreviewMergedSetGenerated = shadowCandidateMergePreview?.Report.PreviewMergedSetGenerated ?? false,
                    ShadowCandidateMergePreviewSampleCount = shadowCandidateMergePreview?.Report.SampleCount ?? 0,
                    ShadowCandidateMergePreviewBaselineCandidateCount = shadowCandidateMergePreview?.Report.BaselineCandidateCount ?? 0,
                    ShadowCandidateMergePreviewShadowAdapterCandidateCount = shadowCandidateMergePreview?.Report.ShadowAdapterCandidateCount ?? 0,
                    ShadowCandidateMergePreviewMergedPreviewCandidateCount = shadowCandidateMergePreview?.Report.MergedPreviewCandidateCount ?? 0,
                    ShadowCandidateMergePreviewPreviewAddCount = shadowCandidateMergePreview?.Report.PreviewAddCount ?? 0,
                    ShadowCandidateMergePreviewPreviewRemoveCount = shadowCandidateMergePreview?.Report.PreviewRemoveCount ?? 0,
                    ShadowCandidateMergePreviewAppliedAddCount = shadowCandidateMergePreview?.Report.AppliedAddCount ?? 0,
                    ShadowCandidateMergePreviewAppliedRemoveCount = shadowCandidateMergePreview?.Report.AppliedRemoveCount ?? 0,
                    ShadowCandidateMergePreviewTokenDeltaTotal = shadowCandidateMergePreview?.Report.TokenDeltaTotal ?? 0,
                    ShadowCandidateMergePreviewTokenDeltaMax = shadowCandidateMergePreview?.Report.TokenDeltaMax ?? 0,
                    ShadowCandidateMergePreviewPriorityOrderDeltaCount = shadowCandidateMergePreview?.Report.PriorityOrderDeltaCount ?? 0,
                    ShadowCandidateMergePreviewPriorityInversionCount = shadowCandidateMergePreview?.Report.PriorityInversionCount ?? 0,
                    ShadowCandidateMergePreviewDroppedRequiredCandidateCount = shadowCandidateMergePreview?.Report.DroppedRequiredCandidateCount ?? 0,
                    ShadowCandidateMergePreviewSectionMismatchCount = shadowCandidateMergePreview?.Report.SectionMismatchCount ?? 0,
                    ShadowCandidateMergePreviewRiskAfterPolicy = shadowCandidateMergePreview?.Report.RiskAfterPolicy ?? 0,
                    ShadowCandidateMergePreviewFormalSelectedSetChanged = shadowCandidateMergePreview?.Report.FormalSelectedSetChanged ?? false,
                    ShadowCandidateMergePreviewPackageOutputChanged = shadowCandidateMergePreview?.Report.PackageOutputChanged ?? false,
                    ShadowCandidateMergePreviewPackingPolicyChanged = shadowCandidateMergePreview?.Report.PackingPolicyChanged ?? false,
                    ShadowCandidateMergePreviewRuntimeMutated = shadowCandidateMergePreview?.Report.RuntimeMutated ?? false,
                    ShadowCandidateMergePreviewVectorStoreBindingChanged = shadowCandidateMergePreview?.Report.VectorStoreBindingChanged ?? false,
                    ShadowCandidateMergePreviewBlockedReasons = shadowCandidateMergePreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowCandidateMergePreviewObservationSourcePath = shadowCandidateMergePreviewObservation?.SourcePath ?? string.Empty,
                    ShadowCandidateMergePreviewObservationPassed = shadowCandidateMergePreviewObservation?.Report.ObservationPassed ?? false,
                    ShadowCandidateMergePreviewObservationGatePassed = shadowCandidateMergePreviewObservation?.Report.GatePassed ?? false,
                    ShadowCandidateMergePreviewObservationRecommendation = shadowCandidateMergePreviewObservation?.Report.Recommendation ?? string.Empty,
                    ShadowCandidateMergePreviewObservationRunCount = shadowCandidateMergePreviewObservation?.Report.ObservationRunCount ?? 0,
                    ShadowCandidateMergePreviewObservationSampleCount = shadowCandidateMergePreviewObservation?.Report.SampleObservationCount ?? 0,
                    ShadowCandidateMergePreviewObservationDeterministicStable = shadowCandidateMergePreviewObservation?.Report.DeterministicPreviewStable ?? false,
                    ShadowCandidateMergePreviewObservationPreviewAddRemoveStable = shadowCandidateMergePreviewObservation?.Report.PreviewAddRemoveStable ?? false,
                    ShadowCandidateMergePreviewObservationPreviewAddCountMin = shadowCandidateMergePreviewObservation?.Report.PreviewAddCountMin ?? 0,
                    ShadowCandidateMergePreviewObservationPreviewAddCountMax = shadowCandidateMergePreviewObservation?.Report.PreviewAddCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationPreviewRemoveCountMin = shadowCandidateMergePreviewObservation?.Report.PreviewRemoveCountMin ?? 0,
                    ShadowCandidateMergePreviewObservationPreviewRemoveCountMax = shadowCandidateMergePreviewObservation?.Report.PreviewRemoveCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationAppliedAddCountMax = shadowCandidateMergePreviewObservation?.Report.AppliedAddCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationAppliedRemoveCountMax = shadowCandidateMergePreviewObservation?.Report.AppliedRemoveCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationRiskAfterPolicyMax = shadowCandidateMergePreviewObservation?.Report.RiskAfterPolicyMax ?? 0,
                    ShadowCandidateMergePreviewObservationTokenDeltaTotalMax = shadowCandidateMergePreviewObservation?.Report.TokenDeltaTotalMax ?? 0,
                    ShadowCandidateMergePreviewObservationTokenDeltaMaxMax = shadowCandidateMergePreviewObservation?.Report.TokenDeltaMaxMax ?? 0,
                    ShadowCandidateMergePreviewObservationPriorityInversionCountTotal = shadowCandidateMergePreviewObservation?.Report.PriorityInversionCountTotal ?? 0,
                    ShadowCandidateMergePreviewObservationSectionMismatchCountTotal = shadowCandidateMergePreviewObservation?.Report.SectionMismatchCountTotal ?? 0,
                    ShadowCandidateMergePreviewObservationFormalOutputChangedMax = shadowCandidateMergePreviewObservation?.Report.FormalOutputChangedMax ?? 0,
                    ShadowCandidateMergePreviewObservationPackageOutputChanged = shadowCandidateMergePreviewObservation?.Report.PackageOutputChanged ?? false,
                    ShadowCandidateMergePreviewObservationPackingPolicyChanged = shadowCandidateMergePreviewObservation?.Report.PackingPolicyChanged ?? false,
                    ShadowCandidateMergePreviewObservationRuntimeMutated = shadowCandidateMergePreviewObservation?.Report.RuntimeMutated ?? false,
                    ShadowCandidateMergePreviewObservationVectorStoreBindingChanged = shadowCandidateMergePreviewObservation?.Report.VectorStoreBindingChanged ?? false,
                    ShadowCandidateMergePreviewObservationBlockedReasons = shadowCandidateMergePreviewObservation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowMergeStabilityFreezeSourcePath = shadowMergeStabilityFreeze?.SourcePath ?? string.Empty,
                    ShadowMergeStabilityFreezePassed = shadowMergeStabilityFreeze?.Report.FreezePassed ?? false,
                    ShadowMergeStabilityFreezeRecommendation = shadowMergeStabilityFreeze?.Report.Recommendation ?? string.Empty,
                    ShadowMergePromotionDecisionSourcePath = shadowMergePromotionDecision?.SourcePath ?? string.Empty,
                    ShadowMergePromotionDecisionPassed = shadowMergePromotionDecision?.Report.PromotionDecisionPassed ?? false,
                    ShadowMergePromotionDecision = shadowMergePromotionDecision?.Report.PromotionDecision ?? string.Empty,
                    ShadowMergeNextAllowedPhase = shadowMergePromotionDecision?.Report.NextAllowedPhase ?? shadowMergeStabilityFreeze?.Report.NextAllowedPhase ?? string.Empty,
                    ShadowMergeObservationRunCount = shadowMergePromotionDecision?.Report.ObservationRunCount ?? shadowMergeStabilityFreeze?.Report.ObservationRunCount ?? 0,
                    ShadowMergeSampleObservationCount = shadowMergePromotionDecision?.Report.SampleObservationCount ?? shadowMergeStabilityFreeze?.Report.SampleObservationCount ?? 0,
                    ShadowMergeDeterministicPreviewStable = shadowMergePromotionDecision?.Report.DeterministicPreviewStable ?? shadowMergeStabilityFreeze?.Report.DeterministicPreviewStable ?? false,
                    ShadowMergePreviewAddCountMin = shadowMergePromotionDecision?.Report.PreviewAddCountMin ?? shadowMergeStabilityFreeze?.Report.PreviewAddCountMin ?? 0,
                    ShadowMergePreviewAddCountMax = shadowMergePromotionDecision?.Report.PreviewAddCountMax ?? shadowMergeStabilityFreeze?.Report.PreviewAddCountMax ?? 0,
                    ShadowMergePreviewRemoveCountMin = shadowMergePromotionDecision?.Report.PreviewRemoveCountMin ?? shadowMergeStabilityFreeze?.Report.PreviewRemoveCountMin ?? 0,
                    ShadowMergePreviewRemoveCountMax = shadowMergePromotionDecision?.Report.PreviewRemoveCountMax ?? shadowMergeStabilityFreeze?.Report.PreviewRemoveCountMax ?? 0,
                    ShadowMergeAppliedAddCountMax = shadowMergePromotionDecision?.Report.AppliedAddCountMax ?? shadowMergeStabilityFreeze?.Report.AppliedAddCountMax ?? 0,
                    ShadowMergeAppliedRemoveCountMax = shadowMergePromotionDecision?.Report.AppliedRemoveCountMax ?? shadowMergeStabilityFreeze?.Report.AppliedRemoveCountMax ?? 0,
                    ShadowMergeRiskAfterPolicyMax = shadowMergePromotionDecision?.Report.RiskAfterPolicyMax ?? shadowMergeStabilityFreeze?.Report.RiskAfterPolicyMax ?? 0,
                    ShadowMergeTokenDeltaTotalMax = shadowMergePromotionDecision?.Report.TokenDeltaTotalMax ?? shadowMergeStabilityFreeze?.Report.TokenDeltaTotalMax ?? 0,
                    ShadowMergePriorityInversionCountTotal = shadowMergePromotionDecision?.Report.PriorityInversionCountTotal ?? shadowMergeStabilityFreeze?.Report.PriorityInversionCountTotal ?? 0,
                    ShadowMergeSectionMismatchCountTotal = shadowMergePromotionDecision?.Report.SectionMismatchCountTotal ?? shadowMergeStabilityFreeze?.Report.SectionMismatchCountTotal ?? 0,
                    ShadowMergeFormalOutputChangedMax = shadowMergePromotionDecision?.Report.FormalOutputChangedMax ?? shadowMergeStabilityFreeze?.Report.FormalOutputChangedMax ?? 0,
                    ShadowMergePackageOutputChanged = shadowMergePromotionDecision?.Report.PackageOutputChanged ?? shadowMergeStabilityFreeze?.Report.PackageOutputChanged ?? false,
                    ShadowMergePackingPolicyChanged = shadowMergePromotionDecision?.Report.PackingPolicyChanged ?? shadowMergeStabilityFreeze?.Report.PackingPolicyChanged ?? false,
                    ShadowMergeRuntimeMutated = shadowMergePromotionDecision?.Report.RuntimeMutated ?? shadowMergeStabilityFreeze?.Report.RuntimeMutated ?? false,
                    ShadowMergeVectorStoreBindingChanged = shadowMergePromotionDecision?.Report.VectorStoreBindingChanged ?? shadowMergeStabilityFreeze?.Report.VectorStoreBindingChanged ?? false,
                    ShadowMergeBlockedReasons = shadowMergePromotionDecision?.Report.BlockedReasons ?? shadowMergeStabilityFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeProposalSourcePath = controlledShadowMergeProposal?.SourcePath ?? string.Empty,
                    ControlledShadowMergeProposalPassed = controlledShadowMergeProposal?.Report.ProposalPassed ?? false,
                    ControlledShadowMergeProposalGatePassed = controlledShadowMergeProposal?.Report.GatePassed ?? false,
                    ControlledShadowMergeProposalRecommendation = controlledShadowMergeProposal?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergeProposalId = controlledShadowMergeProposal?.Report.ProposalId ?? string.Empty,
                    ControlledShadowMergeProposalScopeCount = controlledShadowMergeProposal?.Report.ScopeCount ?? 0,
                    ControlledShadowMergeProposalSelectedScopes = controlledShadowMergeProposal?.Report.SelectedScopes ?? Array.Empty<string>(),
                    ControlledShadowMergeProposalMaxRequestCount = controlledShadowMergeProposal?.Report.MaxRequestCount ?? 0,
                    ControlledShadowMergeProposalMaxDurationMinutes = controlledShadowMergeProposal?.Report.MaxDurationMinutes ?? 0,
                    ControlledShadowMergeProposalMaxPreviewAddCount = controlledShadowMergeProposal?.Report.MaxPreviewAddCount ?? 0,
                    ControlledShadowMergeProposalMaxPreviewRemoveCount = controlledShadowMergeProposal?.Report.MaxPreviewRemoveCount ?? 0,
                    ControlledShadowMergeProposalRollbackPlanPresent = controlledShadowMergeProposal?.Report.RollbackPlanPresent ?? false,
                    ControlledShadowMergeProposalKillSwitchPlanPresent = controlledShadowMergeProposal?.Report.KillSwitchPlanPresent ?? false,
                    ControlledShadowMergeProposalObservationConditionCount = controlledShadowMergeProposal?.Report.ObservationConditions.Count ?? 0,
                    ControlledShadowMergeProposalStopConditionCount = controlledShadowMergeProposal?.Report.StopConditions.Count ?? 0,
                    ControlledShadowMergeProposalFormalRetrievalAllowed = controlledShadowMergeProposal?.Report.FormalRetrievalAllowed ?? false,
                    ControlledShadowMergeProposalRuntimeSwitchAllowed = controlledShadowMergeProposal?.Report.RuntimeSwitchAllowed ?? false,
                    ControlledShadowMergeProposalRuntimeMutated = controlledShadowMergeProposal?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeProposalBlockedReasons = controlledShadowMergeProposal?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeDryRunSourcePath = controlledShadowMergeDryRun?.SourcePath ?? string.Empty,
                    ControlledShadowMergeDryRunPassed = controlledShadowMergeDryRun?.Report.DryRunPassed ?? false,
                    ControlledShadowMergeDryRunGatePassed = controlledShadowMergeDryRun?.Report.GatePassed ?? false,
                    ControlledShadowMergeDryRunRecommendation = controlledShadowMergeDryRun?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergeDryRunProposalConstraintsApplied = controlledShadowMergeDryRun?.Report.ProposalConstraintsApplied ?? false,
                    ControlledShadowMergeDryRunAddRemoveLimitEnforced = controlledShadowMergeDryRun?.Report.AddRemoveLimitEnforced ?? false,
                    ControlledShadowMergeDryRunTokenSectionPriorityGatePassed = controlledShadowMergeDryRun?.Report.TokenSectionPriorityGatePassed ?? false,
                    ControlledShadowMergeDryRunRollbackVerified = controlledShadowMergeDryRun?.Report.RollbackVerified ?? false,
                    ControlledShadowMergeDryRunKillSwitchVerified = controlledShadowMergeDryRun?.Report.KillSwitchVerified ?? false,
                    ControlledShadowMergeDryRunPreviewAddCount = controlledShadowMergeDryRun?.Report.PreviewAddCount ?? 0,
                    ControlledShadowMergeDryRunPreviewRemoveCount = controlledShadowMergeDryRun?.Report.PreviewRemoveCount ?? 0,
                    ControlledShadowMergeDryRunAppliedAddCount = controlledShadowMergeDryRun?.Report.AppliedAddCount ?? 0,
                    ControlledShadowMergeDryRunAppliedRemoveCount = controlledShadowMergeDryRun?.Report.AppliedRemoveCount ?? 0,
                    ControlledShadowMergeDryRunTokenDeltaTotal = controlledShadowMergeDryRun?.Report.TokenDeltaTotal ?? 0,
                    ControlledShadowMergeDryRunTokenDeltaMax = controlledShadowMergeDryRun?.Report.TokenDeltaMax ?? 0,
                    ControlledShadowMergeDryRunPriorityInversionCount = controlledShadowMergeDryRun?.Report.PriorityInversionCount ?? 0,
                    ControlledShadowMergeDryRunSectionMismatchCount = controlledShadowMergeDryRun?.Report.SectionMismatchCount ?? 0,
                    ControlledShadowMergeDryRunFormalOutputChanged = controlledShadowMergeDryRun?.Report.FormalOutputChanged ?? 0,
                    ControlledShadowMergeDryRunPackageOutputChanged = controlledShadowMergeDryRun?.Report.PackageOutputChanged ?? false,
                    ControlledShadowMergeDryRunPackingPolicyChanged = controlledShadowMergeDryRun?.Report.PackingPolicyChanged ?? false,
                    ControlledShadowMergeDryRunRuntimeMutated = controlledShadowMergeDryRun?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeDryRunVectorStoreBindingChanged = controlledShadowMergeDryRun?.Report.VectorStoreBindingChanged ?? false,
                    ControlledShadowMergeDryRunBlockedReasons = controlledShadowMergeDryRun?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeObservationWindowSourcePath = controlledShadowMergeObservationWindow?.SourcePath ?? string.Empty,
                    ControlledShadowMergeObservationWindowPassed = controlledShadowMergeObservationWindow?.Report.ObservationPassed ?? false,
                    ControlledShadowMergeObservationWindowGatePassed = controlledShadowMergeObservationWindow?.Report.GatePassed ?? false,
                    ControlledShadowMergeObservationWindowRecommendation = controlledShadowMergeObservationWindow?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergeObservationWindowProposalConstraintsApplied = controlledShadowMergeObservationWindow?.Report.ProposalConstraintsApplied ?? false,
                    ControlledShadowMergeObservationWindowRunCount = controlledShadowMergeObservationWindow?.Report.ObservationRunCount ?? 0,
                    ControlledShadowMergeObservationWindowRequestCountTotal = controlledShadowMergeObservationWindow?.Report.RequestCountTotal ?? 0,
                    ControlledShadowMergeObservationWindowMaxRequestCount = controlledShadowMergeObservationWindow?.Report.MaxRequestCount ?? 0,
                    ControlledShadowMergeObservationWindowPreviewAddCountMin = controlledShadowMergeObservationWindow?.Report.PreviewAddCountMin ?? 0,
                    ControlledShadowMergeObservationWindowPreviewAddCountMax = controlledShadowMergeObservationWindow?.Report.PreviewAddCountMax ?? 0,
                    ControlledShadowMergeObservationWindowPreviewRemoveCountMin = controlledShadowMergeObservationWindow?.Report.PreviewRemoveCountMin ?? 0,
                    ControlledShadowMergeObservationWindowPreviewRemoveCountMax = controlledShadowMergeObservationWindow?.Report.PreviewRemoveCountMax ?? 0,
                    ControlledShadowMergeObservationWindowAppliedAddCountMax = controlledShadowMergeObservationWindow?.Report.AppliedAddCountMax ?? 0,
                    ControlledShadowMergeObservationWindowAppliedRemoveCountMax = controlledShadowMergeObservationWindow?.Report.AppliedRemoveCountMax ?? 0,
                    ControlledShadowMergeObservationWindowRiskAfterPolicyMax = controlledShadowMergeObservationWindow?.Report.RiskAfterPolicyMax ?? 0,
                    ControlledShadowMergeObservationWindowTokenDeltaTotalMax = controlledShadowMergeObservationWindow?.Report.TokenDeltaTotalMax ?? 0,
                    ControlledShadowMergeObservationWindowTokenDeltaMaxMax = controlledShadowMergeObservationWindow?.Report.TokenDeltaMaxMax ?? 0,
                    ControlledShadowMergeObservationWindowPriorityInversionCountTotal = controlledShadowMergeObservationWindow?.Report.PriorityInversionCountTotal ?? 0,
                    ControlledShadowMergeObservationWindowSectionMismatchCountTotal = controlledShadowMergeObservationWindow?.Report.SectionMismatchCountTotal ?? 0,
                    ControlledShadowMergeObservationWindowFormalOutputChangedMax = controlledShadowMergeObservationWindow?.Report.FormalOutputChangedMax ?? 0,
                    ControlledShadowMergeObservationWindowPackageOutputChanged = controlledShadowMergeObservationWindow?.Report.PackageOutputChanged ?? false,
                    ControlledShadowMergeObservationWindowPackingPolicyChanged = controlledShadowMergeObservationWindow?.Report.PackingPolicyChanged ?? false,
                    ControlledShadowMergeObservationWindowRuntimeMutated = controlledShadowMergeObservationWindow?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeObservationWindowVectorStoreBindingChanged = controlledShadowMergeObservationWindow?.Report.VectorStoreBindingChanged ?? false,
                    ControlledShadowMergeObservationWindowBlockedReasons = controlledShadowMergeObservationWindow?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeFreezeSourcePath = controlledShadowMergeFreeze?.SourcePath ?? string.Empty,
                    ControlledShadowMergeFreezePassed = controlledShadowMergeFreeze?.Report.FreezePassed ?? false,
                    ControlledShadowMergePromotionDecisionPassed = controlledShadowMergeFreeze?.Report.PromotionDecisionPassed ?? false,
                    ControlledShadowMergeFreezeRecommendation = controlledShadowMergeFreeze?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergePromotionDecision = controlledShadowMergeFreeze?.Report.PromotionDecision ?? string.Empty,
                    ControlledShadowMergeNextAllowedPhase = controlledShadowMergeFreeze?.Report.NextAllowedPhase ?? string.Empty,
                    ControlledShadowMergeFreezeProposalId = controlledShadowMergeFreeze?.Report.ProposalId ?? string.Empty,
                    ControlledShadowMergeFreezeObservationRunCount = controlledShadowMergeFreeze?.Report.ObservationRunCount ?? 0,
                    ControlledShadowMergeFreezeRequestCountTotal = controlledShadowMergeFreeze?.Report.RequestCountTotal ?? 0,
                    ControlledShadowMergeFreezePreviewAddCountMin = controlledShadowMergeFreeze?.Report.PreviewAddCountMin ?? 0,
                    ControlledShadowMergeFreezePreviewAddCountMax = controlledShadowMergeFreeze?.Report.PreviewAddCountMax ?? 0,
                    ControlledShadowMergeFreezePreviewRemoveCountMin = controlledShadowMergeFreeze?.Report.PreviewRemoveCountMin ?? 0,
                    ControlledShadowMergeFreezePreviewRemoveCountMax = controlledShadowMergeFreeze?.Report.PreviewRemoveCountMax ?? 0,
                    ControlledShadowMergeFreezeAppliedAddCountMax = controlledShadowMergeFreeze?.Report.AppliedAddCountMax ?? 0,
                    ControlledShadowMergeFreezeAppliedRemoveCountMax = controlledShadowMergeFreeze?.Report.AppliedRemoveCountMax ?? 0,
                    ControlledShadowMergeFreezeRiskAfterPolicyMax = controlledShadowMergeFreeze?.Report.RiskAfterPolicyMax ?? 0,
                    ControlledShadowMergeFreezeFormalOutputChangedMax = controlledShadowMergeFreeze?.Report.FormalOutputChangedMax ?? 0,
                    ControlledShadowMergeFreezeFormalPackageWritten = controlledShadowMergeFreeze?.Report.FormalPackageWritten ?? false,
                    ControlledShadowMergeFreezePackageOutputChanged = controlledShadowMergeFreeze?.Report.PackageOutputChanged ?? false,
                    ControlledShadowMergeFreezePackingPolicyChanged = controlledShadowMergeFreeze?.Report.PackingPolicyChanged ?? false,
                    ControlledShadowMergeFreezeRuntimeMutated = controlledShadowMergeFreeze?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeFreezeVectorStoreBindingChanged = controlledShadowMergeFreeze?.Report.VectorStoreBindingChanged ?? false,
                    ControlledShadowMergeFreezeBlockedReasons = controlledShadowMergeFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledAppliedMergeProposalSourcePath = controlledAppliedMergeProposal?.SourcePath ?? string.Empty,
                    ControlledAppliedMergeProposalPassed = controlledAppliedMergeProposal?.Report.ProposalPassed ?? false,
                    ControlledAppliedMergeProposalGatePassed = controlledAppliedMergeProposal?.Report.GatePassed ?? false,
                    ControlledAppliedMergeProposalRecommendation = controlledAppliedMergeProposal?.Report.Recommendation ?? string.Empty,
                    ControlledAppliedMergeProposalId = controlledAppliedMergeProposal?.Report.ProposalId ?? string.Empty,
                    ControlledAppliedMergeProposalApprovalMode = controlledAppliedMergeProposal?.Report.RequiredApprovalMode ?? string.Empty,
                    ControlledAppliedMergeProposalNextAllowedPhase = controlledAppliedMergeProposal?.Report.NextAllowedPhase ?? string.Empty,
                    ControlledAppliedMergeProposalScopeCount = controlledAppliedMergeProposal?.Report.ScopeCount ?? 0,
                    ControlledAppliedMergeProposalSelectedScopes = controlledAppliedMergeProposal?.Report.SelectedScopes ?? Array.Empty<string>(),
                    ControlledAppliedMergeProposalMaxAppliedAddCount = controlledAppliedMergeProposal?.Report.MaxAppliedAddCount ?? 0,
                    ControlledAppliedMergeProposalMaxAppliedRemoveCount = controlledAppliedMergeProposal?.Report.MaxAppliedRemoveCount ?? 0,
                    ControlledAppliedMergeProposalStablePreviewAddCount = controlledAppliedMergeProposal?.Report.StablePreviewAddCount ?? 0,
                    ControlledAppliedMergeProposalStablePreviewRemoveCount = controlledAppliedMergeProposal?.Report.StablePreviewRemoveCount ?? 0,
                    ControlledAppliedMergeProposalAppliedAddCount = controlledAppliedMergeProposal?.Report.AppliedAddCount ?? 0,
                    ControlledAppliedMergeProposalAppliedRemoveCount = controlledAppliedMergeProposal?.Report.AppliedRemoveCount ?? 0,
                    ControlledAppliedMergeProposalApprovalPlanPresent = controlledAppliedMergeProposal?.Report.ApprovalPlanPresent ?? false,
                    ControlledAppliedMergeProposalRollbackPlanPresent = controlledAppliedMergeProposal?.Report.RollbackPlanPresent ?? false,
                    ControlledAppliedMergeProposalKillSwitchPlanPresent = controlledAppliedMergeProposal?.Report.KillSwitchPlanPresent ?? false,
                    ControlledAppliedMergeProposalRiskAfterPolicy = controlledAppliedMergeProposal?.Report.RiskAfterPolicy ?? 0,
                    ControlledAppliedMergeProposalFormalOutputChanged = controlledAppliedMergeProposal?.Report.FormalOutputChanged ?? 0,
                    ControlledAppliedMergeProposalFormalPackageWritten = controlledAppliedMergeProposal?.Report.FormalPackageWritten ?? false,
                    ControlledAppliedMergeProposalPackageOutputChanged = controlledAppliedMergeProposal?.Report.PackageOutputChanged ?? false,
                    ControlledAppliedMergeProposalPackingPolicyChanged = controlledAppliedMergeProposal?.Report.PackingPolicyChanged ?? false,
                    ControlledAppliedMergeProposalRuntimeMutated = controlledAppliedMergeProposal?.Report.RuntimeMutated ?? false,
                    ControlledAppliedMergeProposalVectorStoreBindingChanged = controlledAppliedMergeProposal?.Report.VectorStoreBindingChanged ?? false,
                    ControlledAppliedMergeProposalAppliedMergeAllowed = controlledAppliedMergeProposal?.Report.AppliedMergeAllowed ?? false,
                    ControlledAppliedMergeProposalBlockedReasons = controlledAppliedMergeProposal?.Report.BlockedReasons ?? Array.Empty<string>(),
                    RetrievalEvalProtocolGateSourcePath = retrievalEvalProtocol?.GateSourcePath ?? string.Empty,
                    RetrievalEvalProtocolSourceAuditPath = retrievalEvalProtocol?.SourceAuditPath ?? string.Empty,
                    RetrievalEvalProtocolGatePassed = retrievalEvalProtocol?.Gate?.GatePassed ?? false,
                    RetrievalEvalProtocolRecommendation = retrievalEvalProtocol?.Gate?.Recommendation ?? string.Empty,
                    RetrievalEvalProtocolVersion = retrievalEvalProtocol?.Gate?.Protocol.ProtocolVersion ?? string.Empty,
                    RetrievalEvalProtocolVectorTopK = retrievalEvalProtocol?.Gate?.Protocol.VectorTopK ?? 0,
                    RetrievalEvalProtocolMergedTopK = retrievalEvalProtocol?.Gate?.Protocol.MergedTopK ?? 0,
                    RetrievalEvalProtocolFinalTopK = retrievalEvalProtocol?.Gate?.Protocol.FinalTopK ?? 0,
                    RetrievalEvalProtocolHashOrderSensitivityCount = retrievalEvalProtocol?.Gate?.HashOrderSensitivityCount ?? 0,
                    RetrievalEvalProtocolTieBreakDeterministic = retrievalEvalProtocol?.Gate?.TieBreakDeterministic ?? false,
                    RetrievalEvalProtocolSourceNonDiscriminativeDetected = retrievalEvalProtocol?.Gate?.SourceNonDiscriminativeDetected ?? false,
                    RetrievalEvalProtocolTemplateHomogeneityDetected = retrievalEvalProtocol?.Gate?.TemplateHomogeneityDetected ?? false,
                    RetrievalEvalProtocolRuntimeChangeGatePassed = retrievalEvalProtocol?.Gate?.RuntimeChangeGatePassed ?? false,
                    RetrievalEvalProtocolRiskAfterPolicy = retrievalEvalProtocol?.Gate?.RiskAfterPolicy ?? 0,
                    RetrievalEvalProtocolMustNotHitRiskAfterPolicy = retrievalEvalProtocol?.Gate?.MustNotHitRiskAfterPolicy ?? 0,
                    RetrievalEvalProtocolLifecycleRiskAfterPolicy = retrievalEvalProtocol?.Gate?.LifecycleRiskAfterPolicy ?? 0,
                    RetrievalEvalProtocolNonDiscriminativeSourceCount = retrievalEvalProtocol?.SourceAudit?.NonDiscriminativeSourceCount ?? 0,
                    RetrievalEvalProtocolTemplateHomogeneityScore = retrievalEvalProtocol?.SourceAudit?.TemplateHomogeneityScore ?? 0,
                    RetrievalEvalProtocolBaselineRecall = retrievalEvalProtocol?.SourceAudit?.BaselineRecall ?? 0,
                    RetrievalEvalProtocolMergedRecall = retrievalEvalProtocol?.SourceAudit?.MergedRecall ?? 0,
                    RetrievalEvalProtocolBlockedReasons = retrievalEvalProtocol?.Gate?.BlockedReasons ?? Array.Empty<string>(),
                    FormalRetrievalIntegrationFreezeSourcePath = formalRetrievalIntegrationFreeze?.SourcePath ?? string.Empty,
                    FormalRetrievalIntegrationFreezePassed = formalRetrievalIntegrationFreeze?.Report.FreezePassed ?? false,
                    FormalRetrievalIntegrationFreezeRecommendation = formalRetrievalIntegrationFreeze?.Report.Recommendation ?? string.Empty,
                    FormalRetrievalIntegrationFreezeSelectedProfile = formalRetrievalIntegrationFreeze?.Report.SelectedProfile ?? string.Empty,
                    FormalRetrievalIntegrationFreezeFrozenArtifactCount = formalRetrievalIntegrationFreeze?.Report.FrozenArtifactPaths.Count ?? 0,
                    V4GateSatisfied = readinessGate?.Report.Passed ?? IsVectorV4GateSatisfied(recallLoss.A3, recallLoss.Extended)
                };
            }
            catch (JsonException)
            {
                return new ServiceVectorShadowQualitySummary
                {
                    Available = false,
                    SourcePath = path,
                    CurrentRecommendation = "InvalidReport"
                };
            }
        }

        var residualOnly = TryLoadVectorResidualRiskReport();
        if (residualOnly is not null)
        {
            var lifecycleCoverage = TryLoadVectorLifecycleMetadataCoverageReport();
            var lifecycleBackfill = TryLoadVectorLifecycleMetadataBackfillPlan();
            var recallLoss = TryLoadVectorRecallLossReports();
            var safeRecovery = TryLoadVectorSafeRecallRecoveryReports();
            var fusionShadow = TryLoadVectorRankerFusionShadowReports();
            var representation = TryLoadVectorRepresentationBenchmarkReports();
            var queryExpansion = TryLoadVectorQueryExpansionShadowReports();
            var readinessGate = TryLoadVectorReadinessGateReport();
            var providerComparison = TryLoadVectorProviderComparisonReport();
            var qwen3ReadinessGate = TryLoadVectorQwen3ReadinessGateReport();
            var providerComparisonFreeze = TryLoadEmbeddingProviderComparisonFreezeReport();
            var hybridPreview = TryLoadVectorHybridPreviewReport();
            var hybridGate = TryLoadVectorHybridReadinessGateReport();
            var hybridAudit = TryLoadVectorHybridRecallRegressionAuditReport();
            var hybridFreeze = TryLoadVectorHybridFreezeReport();
            var alignmentAudit = TryLoadVectorRetrievalDatasetAlignmentAuditSummaryReport();
            var eligibilityTriage = TryLoadVectorEligibilityRecallLossTriageSummaryReport();
            var lifecycleRepairPlan = TryLoadVectorLifecycleMetadataRepairPlanSummaryReport();
            var lifecycleReviewCandidates = TryLoadVectorLifecycleMetadataReviewCandidateReport();
            var lifecycleReviewSummary = TryLoadVectorLifecycleMetadataReviewSummaryReport();
            var lifecycleSidecarPreview = TryLoadVectorLifecycleMetadataSidecarPreviewReport();
            var sidecarEligibility = TryLoadVectorSidecarEligibilityQualityReport();
            var reviewBatch = TryLoadVectorLifecycleMetadataReviewBatchSummary();
            var evidenceBackfill = TryLoadVectorLifecycleMetadataEvidenceBackfillReport();
            var datasetV2Generation = TryLoadRetrievalDatasetV2GenerationSummary();
            var datasetV2Materialization = TryLoadRetrievalDatasetV2MaterializationSummary();
            var datasetV2ShadowEval = TryLoadRetrievalDatasetV2ShadowEvalSummary();
            var datasetV2Stress = TryLoadRetrievalDatasetV2StressSummary();
            var datasetV2StressTriage = TryLoadRetrievalDatasetV2StressFailureTriageSummary();
            var datasetV2HybridRepair = TryLoadRetrievalDatasetV2HybridScoringRepairSummary();
            var datasetV2HybridRiskTriage = TryLoadRetrievalDatasetV2HybridScoringRiskTriageSummary();
            var datasetV2StressFreeze = TryLoadRetrievalDatasetV2StressFreezeSummary();
            var vectorV4Recheck = TryLoadVectorV4ReadinessRecheckSummary();
            var guardedFormalPreview = TryLoadGuardedFormalRetrievalPreviewSummary();
            var shadowPackageComparison = TryLoadVectorShadowPackageComparisonSummary();
            var scopedFormalPreviewOptIn = TryLoadScopedFormalPreviewOptInSummary();
            var limitedFormalPreviewObservation = TryLoadLimitedFormalPreviewObservationSummary();
            var formalPreviewFreeze = TryLoadVectorFormalPreviewFreezeSummary();
            var explicitRuntimeExperimentPlan = TryLoadExplicitScopedRuntimeExperimentPlanSummary();
            var scopedRuntimeExperimentDryRunObservation = TryLoadScopedRuntimeExperimentDryRunObservationSummary();
            var scopedRuntimeExperimentDesignFreeze = TryLoadScopedRuntimeExperimentDesignFreezeSummary();
            var scopedRuntimeExperimentProposal = TryLoadScopedRuntimeExperimentProposalSummary();
            var scopedRuntimeExperimentApproval = TryLoadScopedRuntimeExperimentApprovalSummary();
            var scopedRuntimeExperimentNoOpHarness = TryLoadScopedRuntimeExperimentNoOpHarnessSummary();
            var scopedRuntimeExperimentHarnessFreeze = TryLoadScopedRuntimeExperimentHarnessFreezeSummary();
            var guardedScopedRuntimeExperimentPlan = TryLoadGuardedScopedRuntimeExperimentPlanSummary();
            var scopedRuntimeExperimentRuntimeApproval = TryLoadScopedRuntimeExperimentRuntimeApprovalSummary();
            var scopedRuntimeExperimentActivationPreflight = TryLoadScopedRuntimeExperimentActivationPreflightSummary();
            var guardedScopedRuntimeExperiment = TryLoadGuardedScopedRuntimeExperimentSummary();
                var scopedRuntimeExperimentObservationWindow = TryLoadScopedRuntimeExperimentObservationWindowSummary();
                var scopedRuntimeExperimentObservationFreeze = TryLoadScopedRuntimeExperimentObservationFreezeSummary();
                var formalRetrievalIntegrationPlan = TryLoadFormalRetrievalIntegrationPlanSummary();
                var formalRetrievalIntegrationDecision = TryLoadFormalRetrievalIntegrationDecisionSummary();
                var shadowFormalRetrievalAdapterPlan = TryLoadShadowFormalRetrievalAdapterPlanSummary();
            var shadowFormalRetrievalAdapter = TryLoadShadowFormalRetrievalAdapterSummary();
            var formalAdapterPackageShadowComparison = TryLoadFormalAdapterPackageShadowComparisonSummary();
            var graphVectorRetrievalQualityAudit = TryLoadGraphVectorRetrievalQualityAuditSummary();
            var retrievalQualityRepairPreview = TryLoadRetrievalQualityRepairPreviewSummary();
            var runtimeObservableFeatureContract = TryLoadRuntimeObservableFeatureContractSummary();
            var runtimeRetrievalFeatureDerivation = TryLoadRuntimeRetrievalFeatureDerivationSummary();
            var runtimeRetrievalFeatureDerivationRepair = TryLoadRuntimeRetrievalFeatureDerivationRepairSummary();
            var featureDerivationFailureFreeze = TryLoadRuntimeFeatureDerivationFailureFreezeSummary();
            var graphHubNoiseControl = TryLoadGraphHubNoiseControlSummary();
            var retrievalEvalProtocol = TryLoadRetrievalEvalProtocolSummary();
            var inputMetadataEnrichment = TryLoadInputMetadataEnrichmentSummary();
            var enrichedCandidateSourceRepairRecheck = TryLoadEnrichedCandidateSourceRepairRecheckSummary();
            var sourceAwareRankingRepair = TryLoadSourceAwareRankingRepairSummary();
            var outputTokenPriorityShadow = TryLoadOutputTokenPriorityShadowSummary();
            var formalAdapterInputContract = TryLoadFormalAdapterInputContractSummary();
            var sourceDiverseShadowAdapterValidation = TryLoadSourceDiverseShadowAdapterValidationSummary();
                var shadowCandidateMergePreview = TryLoadShadowCandidateMergePreviewSummary();
                var shadowCandidateMergePreviewObservation = TryLoadShadowCandidateMergePreviewObservationSummary();
                var shadowMergeStabilityFreeze = TryLoadShadowMergeStabilityFreezeSummary();
                var shadowMergePromotionDecision = TryLoadShadowMergePromotionDecisionSummary();
                var controlledShadowMergeProposal = TryLoadControlledShadowMergeProposalSummary();
                var controlledShadowMergeDryRun = TryLoadControlledShadowMergeDryRunSummary();
                var controlledShadowMergeObservationWindow = TryLoadControlledShadowMergeObservationWindowSummary();
                var controlledShadowMergeFreeze = TryLoadControlledShadowMergeFreezeSummary();
                var controlledAppliedMergeProposal = TryLoadControlledAppliedMergeProposalSummary();
                var formalRetrievalIntegrationFreeze = TryLoadFormalRetrievalIntegrationFreezeSummary();
            return new ServiceVectorShadowQualitySummary
            {
                Available = true,
                SourcePath = residualOnly.Value.SourcePath,
                CurrentRecommendation = residualOnly.Value.Report.Recommendation,
                ResidualRiskSourcePath = residualOnly.Value.SourcePath,
                ResidualRiskCount = residualOnly.Value.Report.ResidualRiskCount,
                TopResidualRiskTypes = residualOnly.Value.Report.RiskAfterPolicyByType,
                TopWhyPolicyAllowed = residualOnly.Value.Report.Risks
                    .Select(item => item.WhyPolicyAllowed)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray(),
                TopExpectedActions = residualOnly.Value.Report.Risks
                    .Select(item => item.ExpectedAction)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray(),
                LifecycleMetadataCoverageSourcePath = lifecycleCoverage?.SourcePath ?? string.Empty,
                LifecycleMetadataCoverageRate = lifecycleCoverage?.Report.LifecycleCoverageRate ?? 0,
                UnknownLifecycleCount = lifecycleCoverage?.Report.UnknownLifecycleCount ?? 0,
                MissingReviewStatusCount = lifecycleCoverage?.Report.MissingReviewStatusCount ?? 0,
                MissingReplacementInfoCount = lifecycleCoverage?.Report.MissingReplacementInfoCount ?? 0,
                BlockedByLifecycleMetadataGate = residualOnly.Value.Report.BlockedByLifecycleMetadataGate,
                LifecycleBackfillPlanSourcePath = lifecycleBackfill?.SourcePath ?? string.Empty,
                BackfillUnknownLifecycleBefore = lifecycleBackfill?.Plan.UnknownLifecycleBefore ?? 0,
                BackfillAutoResolvableCount = lifecycleBackfill?.Plan.AutoResolvableCount ?? 0,
                BackfillManualReviewRequiredCount = lifecycleBackfill?.Plan.ManualReviewRequiredCount ?? 0,
                BackfillExpectedCoverageAfter = lifecycleBackfill?.Plan.ExpectedCoverageAfter ?? 0,
                RecallLossA3SourcePath = recallLoss.A3SourcePath,
                RecallLossExtendedSourcePath = recallLoss.ExtendedSourcePath,
                A3RecallAfterPolicy = recallLoss.A3?.MustHitRecallAfterPolicy ?? 0,
                ExtendedRecallAfterPolicy = recallLoss.Extended?.MustHitRecallAfterPolicy ?? 0,
                A3RecallRecommendation = recallLoss.A3?.Recommendation ?? string.Empty,
                ExtendedRecallRecommendation = recallLoss.Extended?.Recommendation ?? string.Empty,
                TopRecallMissReasons = MergeMissReasons(recallLoss.A3, recallLoss.Extended),
                IntentReadinessRecommendations = BuildIntentReadinessSummary(recallLoss.A3, recallLoss.Extended),
                SafeRecallRecoveryA3SourcePath = safeRecovery.A3SourcePath,
                SafeRecallRecoveryExtendedSourcePath = safeRecovery.ExtendedSourcePath,
                SafeRecoveryA3RecallAfterPolicy = safeRecovery.A3?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                SafeRecoveryExtendedRecallAfterPolicy = safeRecovery.Extended?.BestSafeSweep?.MustHitRecallAfterPolicy ?? 0,
                SafeRecoveryA3BestConfiguration = safeRecovery.A3?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                SafeRecoveryExtendedBestConfiguration = safeRecovery.Extended?.BestSafeSweep?.ConfigurationId ?? string.Empty,
                SafeRecoveryA3RecoveredBelowTopK = safeRecovery.A3?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                SafeRecoveryExtendedRecoveredBelowTopK = safeRecovery.Extended?.BestSafeSweep?.RecoveredBelowTopKCount ?? 0,
                BlockedMustHitClassificationCounts = MergeBlockedMustHitClassifications(safeRecovery.A3, safeRecovery.Extended),
                FusionShadowA3SourcePath = fusionShadow.A3SourcePath,
                FusionShadowExtendedSourcePath = fusionShadow.ExtendedSourcePath,
                FusionBestStrategy = SelectFusionBestStrategy(fusionShadow.A3, fusionShadow.Extended),
                FusionA3RecallAfterPolicy = fusionShadow.A3?.BestResult?.MustHitRecallFusion ?? 0,
                FusionExtendedRecallAfterPolicy = fusionShadow.Extended?.BestResult?.MustHitRecallFusion ?? 0,
                FusionRiskAfterPolicy = BuildFusionRiskSummary(fusionShadow.A3, fusionShadow.Extended),
                FusionRecallGain = BuildFusionRecallGainSummary(fusionShadow.A3, fusionShadow.Extended),
                FusionReadinessGateSatisfied = IsFusionReadinessSatisfied(fusionShadow.A3, fusionShadow.Extended),
                RepresentationBenchmarkA3SourcePath = representation.A3SourcePath,
                RepresentationBenchmarkExtendedSourcePath = representation.ExtendedSourcePath,
                RepresentationBestDocumentProfile = SelectRepresentationBestDocumentProfile(representation.A3, representation.Extended),
                RepresentationBestQueryProfile = SelectRepresentationBestQueryProfile(representation.A3, representation.Extended),
                RepresentationA3RecallAfterPolicy = representation.A3?.BestResult?.Recall ?? 0,
                RepresentationExtendedRecallAfterPolicy = representation.Extended?.BestResult?.Recall ?? 0,
                RepresentationRiskAfterPolicy = BuildRepresentationRiskSummary(representation.A3, representation.Extended),
                RepresentationRecoveredMissCount = BuildRepresentationRecoveredMissSummary(representation.A3, representation.Extended),
                RepresentationV4GateSatisfied = IsRepresentationReadinessSatisfied(representation.A3, representation.Extended),
                QueryExpansionShadowA3SourcePath = queryExpansion.A3SourcePath,
                QueryExpansionShadowExtendedSourcePath = queryExpansion.ExtendedSourcePath,
                QueryExpansionBestProfile = SelectQueryExpansionBestProfile(queryExpansion.A3, queryExpansion.Extended),
                QueryExpansionA3RecallBefore = queryExpansion.A3?.BestResult?.RecallBeforeExpansion ?? 0,
                QueryExpansionA3RecallAfter = queryExpansion.A3?.BestResult?.RecallAfterExpansion ?? 0,
                QueryExpansionExtendedRecallBefore = queryExpansion.Extended?.BestResult?.RecallBeforeExpansion ?? 0,
                QueryExpansionExtendedRecallAfter = queryExpansion.Extended?.BestResult?.RecallAfterExpansion ?? 0,
                QueryExpansionRecoveredMissCount = BuildQueryExpansionRecoveredMissSummary(queryExpansion.A3, queryExpansion.Extended),
                QueryExpansionRiskAfterPolicy = BuildQueryExpansionRiskSummary(queryExpansion.A3, queryExpansion.Extended),
                QueryExpansionV4GateSatisfied = IsQueryExpansionReadinessSatisfied(queryExpansion.A3, queryExpansion.Extended),
                V4ReadinessGateSourcePath = readinessGate?.SourcePath ?? string.Empty,
                V4ReadinessGatePassed = readinessGate?.Report.Passed ?? false,
                                V4ReadinessGateFailReasons = readinessGate?.Report.FailReasons ?? Array.Empty<string>(),
                ProviderComparisonSourcePath = providerComparison?.SourcePath ?? string.Empty,
                ProviderComparisonResults = providerComparison?.Report.Providers ?? Array.Empty<VectorProviderComparisonV310Result>(),
                Qwen3ReadinessGateSourcePath = qwen3ReadinessGate?.SourcePath ?? string.Empty,
                Qwen3ReadinessGatePassed = qwen3ReadinessGate?.Report.Passed ?? false,
                Qwen3Recommendation = qwen3ReadinessGate?.Report.Recommendation ?? string.Empty,
                Qwen3BlockedReasons = qwen3ReadinessGate?.Report.BlockedReasons ?? Array.Empty<string>(),
                ProviderComparisonFreezeSourcePath = providerComparisonFreeze?.SourcePath ?? string.Empty,
                ProviderPromotionStatus = providerComparisonFreeze?.Report.PromotionStatus ?? string.Empty,
                ProviderConfigurationSanityPassed = false,
                ProviderComparisonStatus = (providerComparisonFreeze?.Report.Passed ?? false) ? "Conclusive" : (providerComparisonFreeze is not null ? "Inconclusive" : string.Empty),
                VectorV4RecheckAllowed = providerComparisonFreeze?.Report.VectorV4RecheckAllowed ?? false,
                ProviderPromotionBlockedReasons = providerComparisonFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                HybridPreviewSourcePath = hybridPreview?.SourcePath ?? string.Empty,
                HybridFullA3Recall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                HybridFullExtendedRecall = (hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RecallAfterPolicy ?? 0).ToString("P2"),
                HybridFullRiskAfterPolicy = Math.Max(hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "A3" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0, hybridPreview?.Report.Variants.FirstOrDefault(v => v.DatasetName == "Extended" && v.Variant == HybridRetrievalVariant.DenseLexicalAnchor)?.RiskAfterPolicy ?? 0),
                HybridReadinessRecommendation = hybridPreview?.Report.Recommendation ?? string.Empty,
                HybridReadinessGatePassed = hybridGate?.Report.Passed ?? false,
                HybridAuditSourcePath = hybridAudit?.SourcePath ?? string.Empty,
                HybridAuditPassed = hybridAudit?.Report.Passed ?? false,
                HybridAuditRecommendation = hybridAudit?.Report.Recommendation ?? string.Empty,
                HybridAuditDenseDroppedCount = hybridAudit?.Report.DenseCandidateDroppedCount ?? 0,
                HybridAuditEligibilityMismatchCount = hybridAudit?.Report.EligibilityMismatchCount ?? 0,
                HybridAuditDedupOverwriteCount = hybridAudit?.Report.DedupOverwriteCount ?? 0,
                HybridFreezeSourcePath = hybridFreeze?.SourcePath ?? string.Empty,
                HybridFreezePassed = hybridFreeze?.Report.FreezePassed ?? false,
                HybridFreezeStatus = hybridFreeze?.Report.HybridRetrievalStatus ?? string.Empty,
                HybridFreezeRecommendation = hybridFreeze?.Report.Recommendation ?? string.Empty,
                HybridV4RecheckAllowed = hybridFreeze?.Report.V4RecheckAllowed ?? false,
                HybridFreezeBlockedReasons = hybridFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                DatasetAlignmentAuditSourcePath = alignmentAudit?.SourcePath ?? string.Empty,
                DatasetAlignmentRecommendation = alignmentAudit?.Report.Recommendation ?? string.Empty,
                DatasetAlignmentIssueCount = alignmentAudit?.Report.AlignmentIssueCount ?? 0,
                DatasetAlignmentA3MustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: false),
                DatasetAlignmentExtendedMustHitCorpusCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: false),
                DatasetAlignmentA3ProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "A3", providerScope: true),
                DatasetAlignmentExtendedProviderScopeCoverage = ResolveAlignmentCoverage(alignmentAudit?.Report, "Extended", providerScope: true),
                DatasetAlignmentEligibilityBlockCount = alignmentAudit?.Report.Reports.Sum(item => item.MustHitBlockedByEligibilityCount) ?? 0,
                DatasetAlignmentAnchorCoverageRate = ResolveAlignmentAnchorCoverage(alignmentAudit?.Report),
                DatasetAlignmentTopIssues = alignmentAudit?.Report.IssueBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                EligibilityRecallLossTriageSourcePath = eligibilityTriage?.SourcePath ?? string.Empty,
                EligibilityFilteredMustHitCount = eligibilityTriage?.Report.TotalFilteredMustHit ?? 0,
                EligibilityCorrectlyBlockedCount = eligibilityTriage?.Report.CorrectlyBlockedCount ?? 0,
                EligibilityRouteToHistoricalCount = eligibilityTriage?.Report.RouteToHistoricalCount ?? 0,
                EligibilityRouteToAuditCount = eligibilityTriage?.Report.RouteToAuditCount ?? 0,
                EligibilityMetadataRepairNeededCount = eligibilityTriage?.Report.MetadataRepairNeededCount ?? 0,
                EligibilityEvalExpectationReviewNeededCount = eligibilityTriage?.Report.EvalExpectationReviewNeededCount ?? 0,
                EligibilityUnsafeToRecoverCount = eligibilityTriage?.Report.UnsafeToRecoverCount ?? 0,
                EligibilityRecallLossRecommendation = eligibilityTriage?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataRepairPlanSourcePath = lifecycleRepairPlan?.SourcePath ?? string.Empty,
                LifecycleMetadataRepairCandidateCount = lifecycleRepairPlan?.Report.CandidateCount ?? 0,
                LifecycleMetadataRepairAutoRepairableCount = lifecycleRepairPlan?.Report.AutoRepairableCount ?? 0,
                LifecycleMetadataRepairHumanReviewRequiredCount = lifecycleRepairPlan?.Report.HumanReviewRequiredCount ?? 0,
                LifecycleMetadataRepairForbiddenCount = lifecycleRepairPlan?.Report.ForbiddenRepairCount ?? 0,
                LifecycleMetadataRepairEstimatedRecallRecovery = lifecycleRepairPlan?.Report.EstimatedRecallRecovery ?? 0,
                LifecycleMetadataRepairRiskEstimate = lifecycleRepairPlan?.Report.RiskAfterRepairEstimate ?? 0,
                LifecycleMetadataRepairRecommendation = lifecycleRepairPlan?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataReviewCandidatesSourcePath = lifecycleReviewCandidates?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewCandidateCount = lifecycleReviewCandidates?.Report.CandidateCount ?? 0,
                LifecycleMetadataReviewPendingCount = lifecycleReviewCandidates?.Report.PendingCount ?? 0,
                LifecycleMetadataReviewCorrectlyBlockedSkippedCount = lifecycleReviewCandidates?.Report.CorrectlyBlockedSkippedCount ?? 0,
                LifecycleMetadataReviewCountByLayer = lifecycleReviewCandidates?.Report.CountByLayer ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                LifecycleMetadataReviewCountByItemKind = lifecycleReviewCandidates?.Report.CountByItemKind ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                LifecycleMetadataReviewRecentCandidates = lifecycleReviewCandidates?.Report.RecentCandidates ?? Array.Empty<VectorLifecycleMetadataReviewCandidate>(),
                LifecycleMetadataReviewRecommendation = lifecycleReviewCandidates?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataReviewSummarySourcePath = lifecycleReviewSummary?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewApprovedForSidecarCount = lifecycleReviewSummary?.Report.ApprovedForSidecarCount ?? 0,
                LifecycleMetadataReviewRejectedCount = lifecycleReviewSummary?.Report.RejectedCount ?? 0,
                LifecycleMetadataReviewNeedsEvidenceCount = lifecycleReviewSummary?.Report.NeedsEvidenceCount ?? 0,
                LifecycleMetadataReviewSupersededCount = lifecycleReviewSummary?.Report.SupersededCount ?? 0,
                LifecycleMetadataReviewSidecarEntryCount = lifecycleReviewSummary?.Report.SidecarEntryCount ?? lifecycleSidecarPreview?.Report.SidecarEntryCount ?? 0,
                LifecycleMetadataReviewUnsafeApprovalBlockedCount = lifecycleReviewSummary?.Report.UnsafeApprovalBlockedCount ?? 0,
                LifecycleMetadataReviewSidecarPreviewSourcePath = lifecycleSidecarPreview?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewNormalContextApprovalCount = lifecycleReviewSummary?.Report.NormalContextApprovalCount ?? lifecycleSidecarPreview?.Report.NormalContextEntryCount ?? 0,
                LifecycleMetadataReviewAuditContextApprovalCount = lifecycleReviewSummary?.Report.AuditContextApprovalCount ?? lifecycleSidecarPreview?.Report.AuditContextEntryCount ?? 0,
                LifecycleMetadataReviewHistoricalContextApprovalCount = lifecycleReviewSummary?.Report.HistoricalContextApprovalCount ?? lifecycleSidecarPreview?.Report.HistoricalContextEntryCount ?? 0,
                LifecycleMetadataReviewDiagnosticsOnlyApprovalCount = lifecycleReviewSummary?.Report.DiagnosticsOnlyApprovalCount ?? lifecycleSidecarPreview?.Report.DiagnosticsOnlyEntryCount ?? 0,
                SidecarEligibilityPreviewSourcePath = sidecarEligibility?.SourcePath ?? string.Empty,
                SidecarEligibilityCandidateCount = sidecarEligibility?.Report.CandidateCount ?? 0,
                SidecarEligibilitySidecarEntryCount = sidecarEligibility?.Report.SidecarEntryCount ?? 0,
                SidecarEligibilityApprovedSidecarCount = sidecarEligibility?.Report.ApprovedSidecarCount ?? 0,
                SidecarEligibilityPendingReviewCount = sidecarEligibility?.Report.PendingReviewCount ?? 0,
                SidecarEligibilityEffectiveMetadataChangedCount = sidecarEligibility?.Report.EffectiveMetadataChangedCount ?? 0,
                SidecarEligibilityUnsafeBlockedCount = sidecarEligibility?.Report.UnsafeSidecarBlockedCount ?? 0,
                SidecarEligibilityConflictBlockedCount = sidecarEligibility?.Report.ConflictSidecarBlockedCount ?? 0,
                SidecarEligibilitySourceItemUnchanged = sidecarEligibility?.Report.SourceItemUnchanged ?? true,
                SidecarEligibilityRecommendation = sidecarEligibility?.Report.Recommendation ?? string.Empty,
                LifecycleMetadataReviewBatchSourcePath = reviewBatch?.SourcePath ?? string.Empty,
                LifecycleMetadataReviewBatchId = reviewBatch?.Batch.BatchId ?? string.Empty,
                LifecycleMetadataReviewBatchStatus = reviewBatch?.Batch.Status ?? string.Empty,
                LifecycleMetadataReviewBatchCandidateCount = reviewBatch?.Batch.CandidateCount ?? 0,
                LifecycleMetadataReviewBatchValidationErrorCount = reviewBatch?.Validation?.ValidationErrorCount ?? 0,
                LifecycleMetadataReviewBatchWouldWriteSidecarCount = reviewBatch?.ApplyPreview?.WouldWriteSidecarEntryCount ?? 0,
                LifecycleMetadataReviewBatchUnsafeBlockedCount = reviewBatch?.ApplyPreview?.UnsafeBlockedCount ?? reviewBatch?.Validation?.UnsafeDecisionCount ?? 0,
                LifecycleMetadataReviewBatchRecommendation = reviewBatch?.ApplyPreview?.Recommendation ?? reviewBatch?.Validation?.Recommendation ?? (reviewBatch is null ? string.Empty : "ReadyForManualReview"),
                LifecycleMetadataEvidenceBackfillSourcePath = evidenceBackfill?.SourcePath ?? string.Empty,
                LifecycleMetadataEvidenceBackfillCandidateCount = evidenceBackfill?.Report.CandidateCount ?? 0,
                LifecycleMetadataEvidenceFoundCount = evidenceBackfill?.Report.EvidenceFoundCount ?? 0,
                LifecycleMetadataSourceRefFoundCount = evidenceBackfill?.Report.SourceRefFoundCount ?? 0,
                LifecycleMetadataProvenanceFoundCount = evidenceBackfill?.Report.ProvenanceFoundCount ?? 0,
                LifecycleMetadataAutoRepairableAfterBackfillCount = evidenceBackfill?.Report.AutoRepairableAfterBackfillCount ?? 0,
                LifecycleMetadataNeedsEvidenceAfterBackfillCount = evidenceBackfill?.Report.NeedsEvidenceCount ?? 0,
                LifecycleMetadataEvidenceBackfillRecommendation = evidenceBackfill?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2GenerationSourcePath = datasetV2Generation?.SourcePath ?? string.Empty,
                RetrievalDatasetV2CorpusItemCount = datasetV2Generation?.CorpusItemCount ?? 0,
                RetrievalDatasetV2SampleCount = datasetV2Generation?.SampleCount ?? 0,
                RetrievalDatasetV2ValidationIssueCount = datasetV2Generation?.ValidationIssueCount ?? 0,
                RetrievalDatasetV2MissingEvidenceCount = datasetV2Generation?.MissingEvidenceCount ?? 0,
                RetrievalDatasetV2MissingProvenanceCount = datasetV2Generation?.MissingProvenanceCount ?? 0,
                RetrievalDatasetV2DifficultyBreakdown = datasetV2Generation?.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2SplitBreakdown = datasetV2Generation?.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2Recommendation = datasetV2Generation?.Recommendation ?? string.Empty,
                RetrievalDatasetV2MaterializationSourcePath = datasetV2Materialization?.SourcePath ?? string.Empty,
                RetrievalDatasetV2DatasetId = datasetV2Materialization?.Report.DatasetId ?? string.Empty,
                RetrievalDatasetV2CorpusHash = datasetV2Materialization?.Report.CorpusHash ?? string.Empty,
                RetrievalDatasetV2SamplesHash = datasetV2Materialization?.Report.SamplesHash ?? string.Empty,
                RetrievalDatasetV2MaterializationGatePassed = datasetV2Materialization?.Report.GatePassed ?? false,
                RetrievalDatasetV2MaterializationCorpusHashStable = datasetV2Materialization?.Report.CorpusHashStable ?? false,
                RetrievalDatasetV2MaterializationSamplesHashStable = datasetV2Materialization?.Report.SamplesHashStable ?? false,
                RetrievalDatasetV2MaterializationRecommendation = datasetV2Materialization?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2ShadowEvalSourcePath = datasetV2ShadowEval?.SourcePath ?? string.Empty,
                RetrievalDatasetV2ShadowEvalDatasetId = datasetV2ShadowEval?.Summary.DatasetId ?? string.Empty,
                RetrievalDatasetV2ShadowEvalBestProfileName = datasetV2ShadowEval?.Summary.BestProfileName ?? string.Empty,
                RetrievalDatasetV2ShadowEvalBestRecallAfterPolicy = datasetV2ShadowEval?.Summary.BestRecallAfterPolicy ?? 0,
                RetrievalDatasetV2ShadowEvalBestMrrAfterPolicy = datasetV2ShadowEval?.Summary.BestMrrAfterPolicy ?? 0,
                RetrievalDatasetV2ShadowEvalBestRiskAfterPolicy = datasetV2ShadowEval?.Summary.BestRiskAfterPolicy ?? 0,
                RetrievalDatasetV2ShadowEvalPgVectorParityPassed = datasetV2ShadowEval?.Summary.PgVectorParityPassed ?? false,
                RetrievalDatasetV2ShadowEvalRecommendation = datasetV2ShadowEval?.Gate?.Recommendation ?? datasetV2ShadowEval?.Summary.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressSourcePath = datasetV2Stress?.SourcePath ?? string.Empty,
                RetrievalDatasetV2StressDatasetId = datasetV2Stress?.Report.DatasetId ?? string.Empty,
                RetrievalDatasetV2StressCorpusItemCount = datasetV2Stress?.Report.CorpusItemCount ?? 0,
                RetrievalDatasetV2StressSampleCount = datasetV2Stress?.Report.SampleCount ?? 0,
                RetrievalDatasetV2StressSplitBreakdown = datasetV2Stress?.Report.SplitBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressDifficultyBreakdown = datasetV2Stress?.Report.DifficultyBreakdown ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressLeakageIssueCount = datasetV2Stress?.Report.LeakageIssueCount ?? 0,
                RetrievalDatasetV2StressAnchorDominanceScore = datasetV2Stress?.Report.AnchorDominanceScore ?? 0,
                RetrievalDatasetV2StressDenseRecall = datasetV2Stress?.Report.DenseRecall ?? 0,
                RetrievalDatasetV2StressLexicalRecall = datasetV2Stress?.Report.LexicalRecall ?? 0,
                RetrievalDatasetV2StressAnchorRecall = datasetV2Stress?.Report.AnchorRecall ?? 0,
                RetrievalDatasetV2StressHybridRecall = datasetV2Stress?.Report.HybridRecall ?? 0,
                RetrievalDatasetV2StressHoldoutHybridRecall = datasetV2Stress?.Report.HoldoutHybridRecall ?? 0,
                RetrievalDatasetV2StressRecommendation = datasetV2Stress?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressTriageSourcePath = datasetV2StressTriage?.SourcePath ?? string.Empty,
                RetrievalDatasetV2StressFailureCount = datasetV2StressTriage?.Report.FailureCount ?? 0,
                RetrievalDatasetV2StressHoldoutFailureCount = datasetV2StressTriage?.Report.HoldoutFailureCount ?? 0,
                RetrievalDatasetV2StressFailureCountBySplit = datasetV2StressTriage?.Report.FailureCountBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressFailureCountByDifficulty = datasetV2StressTriage?.Report.FailureCountByDifficulty ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressFailureCountByReason = datasetV2StressTriage?.Report.FailureCountByReason ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2StressDenseOnlyWinCount = datasetV2StressTriage?.Report.DenseOnlyWinCount ?? 0,
                RetrievalDatasetV2StressHybridWinCount = datasetV2StressTriage?.Report.HybridWinCount ?? 0,
                RetrievalDatasetV2StressAnchorRegressionCount = datasetV2StressTriage?.Report.AnchorRegressionCount ?? 0,
                RetrievalDatasetV2StressProfileComparisonSummary = FormatDatasetV2StressProfileComparisons(datasetV2StressTriage?.Report),
                RetrievalDatasetV2StressTriageRecommendation = datasetV2StressTriage?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2HybridRepairSourcePath = datasetV2HybridRepair?.SourcePath ?? string.Empty,
                RetrievalDatasetV2HybridRepairBestProfileName = datasetV2HybridRepair?.BestProfile?.ProfileName ?? datasetV2HybridRepair?.Report.BestProfileName ?? string.Empty,
                RetrievalDatasetV2HybridRepairRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.RecallAfterPolicy ?? 0,
                RetrievalDatasetV2HybridRepairHoldoutRecallAfterPolicy = datasetV2HybridRepair?.BestProfile?.HoldoutRecallAfterPolicy ?? 0,
                RetrievalDatasetV2HybridRepairDenseWinnerLostCount = datasetV2HybridRepair?.BestProfile?.DenseWinnerLostCount ?? 0,
                RetrievalDatasetV2HybridRepairMustHitBelowTopKCount = datasetV2HybridRepair?.BestProfile?.MustHitBelowTopKCount ?? 0,
                RetrievalDatasetV2HybridRepairNegativeDistractorCount = datasetV2HybridRepair?.BestProfile?.NegativeDistractorOutranksMustHitCount ?? 0,
                RetrievalDatasetV2HybridRepairRiskAfterPolicy = datasetV2HybridRepair?.BestProfile?.RiskAfterPolicy ?? 0,
                RetrievalDatasetV2HybridRepairRecommendation = datasetV2HybridRepair?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2HybridRiskTriageSourcePath = datasetV2HybridRiskTriage?.SourcePath ?? string.Empty,
                RetrievalDatasetV2HybridRiskTriageProfileName = datasetV2HybridRiskTriage?.Report.ProfileName ?? string.Empty,
                RetrievalDatasetV2HybridRiskCandidateCount = datasetV2HybridRiskTriage?.Report.RiskCandidateCount ?? 0,
                RetrievalDatasetV2HybridRiskByType = datasetV2HybridRiskTriage?.Report.RiskByType ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2HybridRiskBySplit = datasetV2HybridRiskTriage?.Report.RiskBySplit ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                RetrievalDatasetV2HybridMustNotPromotedCount = datasetV2HybridRiskTriage?.Report.MustNotCandidatePromotedCount ?? 0,
                RetrievalDatasetV2HybridEligibilityBypassCount = datasetV2HybridRiskTriage?.Report.EligibilityBypassCount ?? 0,
                RetrievalDatasetV2HybridRiskProjectionMismatchCount = datasetV2HybridRiskTriage?.Report.RiskProjectionMismatchCount ?? 0,
                RetrievalDatasetV2HybridRiskTriageRecommendation = datasetV2HybridRiskTriage?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressFreezeSourcePath = datasetV2StressFreeze?.SourcePath ?? string.Empty,
                RetrievalDatasetV2StressFreezePassed = datasetV2StressFreeze?.Report.FreezePassed ?? false,
                RetrievalDatasetV2StressFreezeStatus = datasetV2StressFreeze?.Report.DatasetV2Stress ?? string.Empty,
                RetrievalDatasetV2StressFreezeRecommendation = datasetV2StressFreeze?.Report.Recommendation ?? string.Empty,
                RetrievalDatasetV2StressFreezeBestProfile = datasetV2StressFreeze?.Report.BestPreviewProfile ?? string.Empty,
                RetrievalDatasetV2StressFreezeStressRecall = datasetV2StressFreeze?.Report.StressRecall ?? 0,
                RetrievalDatasetV2StressFreezeHoldoutRecall = datasetV2StressFreeze?.Report.HoldoutRecall ?? 0,
                RetrievalDatasetV2StressFreezeRiskAfterPolicy = datasetV2StressFreeze?.Report.RiskAfterPolicy ?? 0,
                RetrievalDatasetV2StressFreezeMustNotHitRiskAfterPolicy = datasetV2StressFreeze?.Report.MustNotHitRiskAfterPolicy ?? 0,
                RetrievalDatasetV2StressFreezeLifecycleRiskAfterPolicy = datasetV2StressFreeze?.Report.LifecycleRiskAfterPolicy ?? 0,
                RetrievalDatasetV2StressFreezeFormalOutputChanged = datasetV2StressFreeze?.Report.FormalOutputChanged ?? 0,
                RetrievalDatasetV2StressFreezeLeakageIssueCount = datasetV2StressFreeze?.Report.LeakageIssueCount ?? 0,
                RetrievalDatasetV2StressFreezeAnchorDominanceScore = datasetV2StressFreeze?.Report.AnchorDominanceScore ?? 0,
                RetrievalDatasetV2StressFreezeV4RecheckAllowed = datasetV2StressFreeze?.Report.V4RecheckAllowed ?? false,
                RetrievalDatasetV2StressFreezeReadyForFormalRetrieval = datasetV2StressFreeze?.Report.ReadyForFormalRetrieval ?? false,
                RetrievalDatasetV2StressFreezeFormalRetrievalAllowed = datasetV2StressFreeze?.Report.FormalRetrievalAllowed ?? false,
                RetrievalDatasetV2StressFreezeBlockedReasons = datasetV2StressFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                VectorV4ReadinessRecheckSourcePath = vectorV4Recheck?.SourcePath ?? string.Empty,
                VectorV4ReadinessRecheckPassed = vectorV4Recheck?.Report.RecheckPassed ?? false,
                VectorV4ReadinessRecheckRecommendation = vectorV4Recheck?.Report.Recommendation ?? string.Empty,
                VectorV4ReadinessLegacyStatus = vectorV4Recheck?.Report.LegacyVectorStatus ?? string.Empty,
                VectorV4ReadinessSmallStatus = vectorV4Recheck?.Report.DatasetV2SmallStatus ?? string.Empty,
                VectorV4ReadinessStressStatus = vectorV4Recheck?.Report.DatasetV2StressStatus ?? string.Empty,
                VectorV4ReadinessPgVectorStatus = vectorV4Recheck?.Report.PgVectorProviderStatus ?? string.Empty,
                VectorV4ReadinessHybridScoringStatus = vectorV4Recheck?.Report.HybridScoringRepairStatus ?? string.Empty,
                VectorV4ReadinessRuntimeGateStatus = vectorV4Recheck?.Report.RuntimeChangeGateStatus ?? string.Empty,
                VectorV4ReadinessBestProfile = vectorV4Recheck?.Report.BestPreviewProfile ?? string.Empty,
                VectorV4ReadinessStressRecall = vectorV4Recheck?.Report.DatasetV2StressRecall ?? 0,
                VectorV4ReadinessHoldoutRecall = vectorV4Recheck?.Report.DatasetV2HoldoutRecall ?? 0,
                VectorV4ReadinessRiskAfterPolicy = vectorV4Recheck?.Report.RiskAfterPolicy ?? 0,
                VectorV4ReadinessFormalOutputChanged = vectorV4Recheck?.Report.FormalOutputChanged ?? 0,
                VectorV4ReadinessReadyForGuardedFormalPreview = vectorV4Recheck?.Report.ReadyForGuardedFormalPreview ?? false,
                VectorV4ReadinessReadyForRuntimeSwitch = vectorV4Recheck?.Report.ReadyForRuntimeSwitch ?? false,
                VectorV4ReadinessFormalRetrievalAllowed = vectorV4Recheck?.Report.FormalRetrievalAllowed ?? false,
                VectorV4ReadinessBlockedReasons = vectorV4Recheck?.Report.BlockedReasons ?? Array.Empty<string>(),
                GuardedFormalRetrievalPreviewSourcePath = guardedFormalPreview?.SourcePath ?? string.Empty,
                GuardedFormalRetrievalPreviewGatePassed = guardedFormalPreview?.Report.GatePassed ?? false,
                GuardedFormalRetrievalPreviewRecommendation = guardedFormalPreview?.Report.Recommendation ?? string.Empty,
                GuardedFormalRetrievalPreviewProfileName = guardedFormalPreview?.Report.ProfileName ?? string.Empty,
                GuardedFormalRetrievalPreviewV4RecheckPassed = guardedFormalPreview?.Report.V4RecheckPassed ?? false,
                GuardedFormalRetrievalPreviewWouldAddCount = guardedFormalPreview?.Report.WouldAddCount ?? 0,
                GuardedFormalRetrievalPreviewWouldRemoveCount = guardedFormalPreview?.Report.WouldRemoveCount ?? 0,
                GuardedFormalRetrievalPreviewWouldRerankCount = guardedFormalPreview?.Report.WouldRerankCount ?? 0,
                GuardedFormalRetrievalPreviewWouldChangeTargetSectionCount = guardedFormalPreview?.Report.WouldChangeTargetSectionCount ?? 0,
                GuardedFormalRetrievalPreviewRiskAfterPolicy = guardedFormalPreview?.Report.RiskAfterPolicy ?? 0,
                GuardedFormalRetrievalPreviewMustNotHitRiskAfterPolicy = guardedFormalPreview?.Report.MustNotHitRiskAfterPolicy ?? 0,
                GuardedFormalRetrievalPreviewLifecycleRiskAfterPolicy = guardedFormalPreview?.Report.LifecycleRiskAfterPolicy ?? 0,
                GuardedFormalRetrievalPreviewFormalOutputChanged = guardedFormalPreview?.Report.FormalOutputChanged ?? 0,
                GuardedFormalRetrievalPreviewPackingPolicyChanged = guardedFormalPreview?.Report.PackingPolicyChanged ?? false,
                GuardedFormalRetrievalPreviewPackageOutputChanged = guardedFormalPreview?.Report.PackageOutputChanged ?? false,
                GuardedFormalRetrievalPreviewReadyForRuntimeSwitch = guardedFormalPreview?.Report.ReadyForRuntimeSwitch ?? false,
                GuardedFormalRetrievalPreviewFormalRetrievalAllowed = guardedFormalPreview?.Report.FormalRetrievalAllowed ?? false,
                GuardedFormalRetrievalPreviewBlockedReasons = guardedFormalPreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                VectorShadowPackageComparisonSourcePath = shadowPackageComparison?.SourcePath ?? string.Empty,
                VectorShadowPackageComparisonGatePassed = shadowPackageComparison?.Report.GatePassed ?? false,
                VectorShadowPackageComparisonRecommendation = shadowPackageComparison?.Report.Recommendation ?? string.Empty,
                VectorShadowPackageComparisonProfileName = shadowPackageComparison?.Report.ProfileName ?? string.Empty,
                VectorShadowPackageCandidateAddCount = shadowPackageComparison?.Report.CandidateAddCount ?? 0,
                VectorShadowPackageCandidateRemoveCount = shadowPackageComparison?.Report.CandidateRemoveCount ?? 0,
                VectorShadowPackageCandidateUnchangedCount = shadowPackageComparison?.Report.CandidateUnchangedCount ?? 0,
                VectorShadowPackageSectionChangedCount = shadowPackageComparison?.Report.SectionChangedCount ?? 0,
                VectorShadowPackageTokenDeltaTotal = shadowPackageComparison?.Report.TokenDeltaTotal ?? 0,
                VectorShadowPackageTokenDeltaMax = shadowPackageComparison?.Report.TokenDeltaMax ?? 0,
                VectorShadowPackageConstraintCoverageDelta = shadowPackageComparison?.Report.ConstraintCoverageDelta ?? 0,
                VectorShadowPackageRelationCoverageDelta = shadowPackageComparison?.Report.RelationCoverageDelta ?? 0,
                VectorShadowPackageRiskAfterPolicy = shadowPackageComparison?.Report.RiskAfterPolicy ?? 0,
                VectorShadowPackageMustNotHitRiskAfterPolicy = shadowPackageComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                VectorShadowPackageLifecycleRiskAfterPolicy = shadowPackageComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                VectorShadowPackageFormalOutputChanged = shadowPackageComparison?.Report.FormalOutputChanged ?? 0,
                VectorShadowPackagePackageOutputChanged = shadowPackageComparison?.Report.PackageOutputChanged ?? false,
                VectorShadowPackagePackingPolicyChanged = shadowPackageComparison?.Report.PackingPolicyChanged ?? false,
                VectorShadowPackageRuntimeMutated = shadowPackageComparison?.Report.RuntimeMutated ?? false,
                VectorShadowPackageReadyForRuntimeSwitch = shadowPackageComparison?.Report.ReadyForRuntimeSwitch ?? false,
                VectorShadowPackageFormalRetrievalAllowed = shadowPackageComparison?.Report.FormalRetrievalAllowed ?? false,
                VectorShadowPackageBlockedReasons = shadowPackageComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedFormalPreviewOptInSourcePath = scopedFormalPreviewOptIn?.SourcePath ?? string.Empty,
                ScopedFormalPreviewOptInGatePassed = scopedFormalPreviewOptIn?.Report.GatePassed ?? false,
                ScopedFormalPreviewOptInRecommendation = scopedFormalPreviewOptIn?.Report.Recommendation ?? string.Empty,
                ScopedFormalPreviewOptInMode = scopedFormalPreviewOptIn?.Report.Mode ?? string.Empty,
                ScopedFormalPreviewOptInProfileName = scopedFormalPreviewOptIn?.Report.ProfileName ?? string.Empty,
                ScopedFormalPreviewOptInWorkspaceAllowlist = scopedFormalPreviewOptIn?.Report.WorkspaceAllowlist ?? Array.Empty<string>(),
                ScopedFormalPreviewOptInCollectionAllowlist = scopedFormalPreviewOptIn?.Report.CollectionAllowlist ?? Array.Empty<string>(),
                ScopedFormalPreviewOptInEvalScopeAllowlist = scopedFormalPreviewOptIn?.Report.EvalScopeAllowlist ?? Array.Empty<string>(),
                ScopedFormalPreviewOptInPreviewPackageCount = scopedFormalPreviewOptIn?.Report.PreviewPackageCount ?? 0,
                ScopedFormalPreviewOptInBaselinePackageCount = scopedFormalPreviewOptIn?.Report.BaselinePackageCount ?? 0,
                ScopedFormalPreviewOptInNonAllowlistedScopeChecked = scopedFormalPreviewOptIn?.Report.NonAllowlistedScopeChecked ?? false,
                ScopedFormalPreviewOptInNonAllowlistedScopeLeakCount = scopedFormalPreviewOptIn?.Report.NonAllowlistedScopeLeakCount ?? 0,
                ScopedFormalPreviewOptInRiskAfterPolicy = scopedFormalPreviewOptIn?.Report.RiskAfterPolicy ?? 0,
                ScopedFormalPreviewOptInFormalOutputChanged = scopedFormalPreviewOptIn?.Report.FormalOutputChanged ?? 0,
                ScopedFormalPreviewOptInPackageOutputChanged = scopedFormalPreviewOptIn?.Report.PackageOutputChanged ?? false,
                ScopedFormalPreviewOptInPackingPolicyChanged = scopedFormalPreviewOptIn?.Report.PackingPolicyChanged ?? false,
                ScopedFormalPreviewOptInFormalPackageWritten = scopedFormalPreviewOptIn?.Report.FormalPackageWritten ?? false,
                ScopedFormalPreviewOptInRuntimeMutated = scopedFormalPreviewOptIn?.Report.RuntimeMutated ?? false,
                ScopedFormalPreviewOptInRollbackInstruction = scopedFormalPreviewOptIn?.Report.RollbackInstruction ?? string.Empty,
                ScopedFormalPreviewOptInBlockedReasons = scopedFormalPreviewOptIn?.Report.BlockedReasons ?? Array.Empty<string>(),
                LimitedFormalPreviewObservationSourcePath = limitedFormalPreviewObservation?.SourcePath ?? string.Empty,
                LimitedFormalPreviewObservationGatePassed = limitedFormalPreviewObservation?.Report.GatePassed ?? false,
                LimitedFormalPreviewObservationRecommendation = limitedFormalPreviewObservation?.Report.Recommendation ?? string.Empty,
                LimitedFormalPreviewObservationMode = limitedFormalPreviewObservation?.Report.Mode ?? string.Empty,
                LimitedFormalPreviewObservationProfileName = limitedFormalPreviewObservation?.Report.ProfileName ?? string.Empty,
                LimitedFormalPreviewObservationRunCount = limitedFormalPreviewObservation?.Report.ObservationRunCount ?? 0,
                LimitedFormalPreviewObservationPreviewPackageCount = limitedFormalPreviewObservation?.Report.PreviewPackageCount ?? 0,
                LimitedFormalPreviewObservationBaselinePackageCount = limitedFormalPreviewObservation?.Report.BaselinePackageCount ?? 0,
                LimitedFormalPreviewObservationCandidateAddCount = limitedFormalPreviewObservation?.Report.CandidateAddCount ?? 0,
                LimitedFormalPreviewObservationCandidateRemoveCount = limitedFormalPreviewObservation?.Report.CandidateRemoveCount ?? 0,
                LimitedFormalPreviewObservationSectionChangedCount = limitedFormalPreviewObservation?.Report.SectionChangedCount ?? 0,
                LimitedFormalPreviewObservationTokenDeltaTotal = limitedFormalPreviewObservation?.Report.TokenDeltaTotal ?? 0,
                LimitedFormalPreviewObservationTokenDeltaMax = limitedFormalPreviewObservation?.Report.TokenDeltaMax ?? 0,
                LimitedFormalPreviewObservationTokenDeltaP95 = limitedFormalPreviewObservation?.Report.TokenDeltaP95 ?? 0,
                LimitedFormalPreviewObservationRiskAfterPolicy = limitedFormalPreviewObservation?.Report.RiskAfterPolicy ?? 0,
                LimitedFormalPreviewObservationFormalOutputChanged = limitedFormalPreviewObservation?.Report.FormalOutputChanged ?? 0,
                LimitedFormalPreviewObservationPackageOutputChanged = limitedFormalPreviewObservation?.Report.PackageOutputChanged ?? false,
                LimitedFormalPreviewObservationPackingPolicyChanged = limitedFormalPreviewObservation?.Report.PackingPolicyChanged ?? false,
                LimitedFormalPreviewObservationFormalPackageWritten = limitedFormalPreviewObservation?.Report.FormalPackageWritten ?? false,
                LimitedFormalPreviewObservationRuntimeMutated = limitedFormalPreviewObservation?.Report.RuntimeMutated ?? false,
                LimitedFormalPreviewObservationNonAllowlistedScopeLeakCount = limitedFormalPreviewObservation?.Report.NonAllowlistedScopeLeakCount ?? 0,
                LimitedFormalPreviewObservationBlockedReasons = limitedFormalPreviewObservation?.Report.BlockedReasons ?? Array.Empty<string>(),
                VectorFormalPreviewFreezeSourcePath = formalPreviewFreeze?.SourcePath ?? string.Empty,
                VectorFormalPreviewFreezePassed = formalPreviewFreeze?.Report.FreezePassed ?? false,
                VectorFormalPreviewFreezeStatus = formalPreviewFreeze?.Report.VectorFormalPreview ?? string.Empty,
                VectorFormalPreviewFreezeRecommendation = formalPreviewFreeze?.Report.Recommendation ?? string.Empty,
                VectorFormalPreviewAllowedMode = formalPreviewFreeze?.Report.AllowedMode ?? string.Empty,
                VectorFormalPreviewFormalRetrievalAllowed = formalPreviewFreeze?.Report.FormalRetrievalAllowed ?? false,
                VectorFormalPreviewReadyForRuntimeSwitch = formalPreviewFreeze?.Report.ReadyForRuntimeSwitch ?? false,
                VectorFormalPreviewUseForRuntime = formalPreviewFreeze?.Report.UseForRuntime ?? false,
                VectorFormalPreviewRuntimeSwitchAllowed = formalPreviewFreeze?.Report.RuntimeSwitchAllowed ?? false,
                VectorFormalPreviewRiskAfterPolicy = formalPreviewFreeze?.Report.RiskAfterPolicy ?? 0,
                VectorFormalPreviewFormalOutputChanged = formalPreviewFreeze?.Report.FormalOutputChanged ?? 0,
                VectorFormalPreviewPackageOutputChanged = formalPreviewFreeze?.Report.PackageOutputChanged ?? false,
                VectorFormalPreviewPackingPolicyChanged = formalPreviewFreeze?.Report.PackingPolicyChanged ?? false,
                VectorFormalPreviewFormalPackageWritten = formalPreviewFreeze?.Report.FormalPackageWritten ?? false,
                VectorFormalPreviewRuntimeMutated = formalPreviewFreeze?.Report.RuntimeMutated ?? false,
                VectorFormalPreviewScopeLeakCount = formalPreviewFreeze?.Report.NonAllowlistedScopeLeakCount ?? 0,
                VectorFormalPreviewForbiddenChanges = formalPreviewFreeze?.Report.ForbiddenChanges ?? Array.Empty<string>(),
                VectorFormalPreviewBlockedReasons = formalPreviewFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                ExplicitScopedRuntimeExperimentSourcePath = explicitRuntimeExperimentPlan?.SourcePath ?? string.Empty,
                ExplicitScopedRuntimeExperimentPlanPassed = explicitRuntimeExperimentPlan?.Report.PlanPassed ?? false,
                ExplicitScopedRuntimeExperimentRecommendation = explicitRuntimeExperimentPlan?.Report.Recommendation ?? string.Empty,
                ExplicitScopedRuntimeExperimentMode = explicitRuntimeExperimentPlan?.Report.Mode ?? string.Empty,
                ExplicitScopedRuntimeExperimentProfileName = explicitRuntimeExperimentPlan?.Report.ProfileName ?? string.Empty,
                ExplicitScopedRuntimeExperimentWorkspaceAllowlist = explicitRuntimeExperimentPlan?.Report.WorkspaceAllowlist ?? Array.Empty<string>(),
                ExplicitScopedRuntimeExperimentCollectionAllowlist = explicitRuntimeExperimentPlan?.Report.CollectionAllowlist ?? Array.Empty<string>(),
                ExplicitScopedRuntimeExperimentEvalScopeAllowlist = explicitRuntimeExperimentPlan?.Report.EvalScopeAllowlist ?? Array.Empty<string>(),
                ExplicitScopedRuntimeExperimentDryRunSupported = explicitRuntimeExperimentPlan?.Report.DryRunSupported ?? false,
                ExplicitScopedRuntimeExperimentRuntimeSwitchAllowed = explicitRuntimeExperimentPlan?.Report.RuntimeSwitchAllowed ?? false,
                ExplicitScopedRuntimeExperimentFormalRetrievalAllowed = explicitRuntimeExperimentPlan?.Report.FormalRetrievalAllowed ?? false,
                ExplicitScopedRuntimeExperimentReadyForRuntimeSwitch = explicitRuntimeExperimentPlan?.Report.ReadyForRuntimeSwitch ?? false,
                ExplicitScopedRuntimeExperimentUseForRuntime = explicitRuntimeExperimentPlan?.Report.UseForRuntime ?? false,
                ExplicitScopedRuntimeExperimentFormalPackageWritten = explicitRuntimeExperimentPlan?.Report.FormalPackageWritten ?? false,
                ExplicitScopedRuntimeExperimentRuntimeMutated = explicitRuntimeExperimentPlan?.Report.RuntimeMutated ?? false,
                ExplicitScopedRuntimeExperimentPackingPolicyChanged = explicitRuntimeExperimentPlan?.Report.PackingPolicyChanged ?? false,
                ExplicitScopedRuntimeExperimentPackageOutputChanged = explicitRuntimeExperimentPlan?.Report.PackageOutputChanged ?? false,
                ExplicitScopedRuntimeExperimentScopeLeakCount = explicitRuntimeExperimentPlan?.Report.NonAllowlistedScopeLeakCount ?? 0,
                ExplicitScopedRuntimeExperimentAllowedActions = explicitRuntimeExperimentPlan?.Report.AllowedActions ?? Array.Empty<string>(),
                ExplicitScopedRuntimeExperimentForbiddenActions = explicitRuntimeExperimentPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                ExplicitScopedRuntimeExperimentRequiredGateSummary = explicitRuntimeExperimentPlan?.Report.RequiredGateSummary ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                ExplicitScopedRuntimeExperimentRollbackPlan = explicitRuntimeExperimentPlan?.Report.RollbackPlan ?? string.Empty,
                ExplicitScopedRuntimeExperimentBlockedReasons = explicitRuntimeExperimentPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentDryRunObservationSourcePath = scopedRuntimeExperimentDryRunObservation?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentDryRunObservationGatePassed = scopedRuntimeExperimentDryRunObservation?.Report.GatePassed ?? false,
                ScopedRuntimeExperimentDryRunObservationRecommendation = scopedRuntimeExperimentDryRunObservation?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentDryRunObservationMode = scopedRuntimeExperimentDryRunObservation?.Report.Mode ?? string.Empty,
                ScopedRuntimeExperimentDryRunObservationProfileName = scopedRuntimeExperimentDryRunObservation?.Report.ProfileName ?? string.Empty,
                ScopedRuntimeExperimentDryRunObservationRunCount = scopedRuntimeExperimentDryRunObservation?.Report.ObservationRunCount ?? 0,
                ScopedRuntimeExperimentDryRunObservationWorkspaceAllowlist = scopedRuntimeExperimentDryRunObservation?.Report.WorkspaceAllowlist ?? Array.Empty<string>(),
                ScopedRuntimeExperimentDryRunObservationCollectionAllowlist = scopedRuntimeExperimentDryRunObservation?.Report.CollectionAllowlist ?? Array.Empty<string>(),
                ScopedRuntimeExperimentDryRunObservationEvalScopeAllowlist = scopedRuntimeExperimentDryRunObservation?.Report.EvalScopeAllowlist ?? Array.Empty<string>(),
                ScopedRuntimeExperimentDryRunObservationDryRunPackageCount = scopedRuntimeExperimentDryRunObservation?.Report.DryRunPackageCount ?? 0,
                ScopedRuntimeExperimentDryRunObservationBaselinePackageCount = scopedRuntimeExperimentDryRunObservation?.Report.BaselinePackageCount ?? 0,
                ScopedRuntimeExperimentDryRunObservationCandidateAddCount = scopedRuntimeExperimentDryRunObservation?.Report.CandidateAddCount ?? 0,
                ScopedRuntimeExperimentDryRunObservationCandidateRemoveCount = scopedRuntimeExperimentDryRunObservation?.Report.CandidateRemoveCount ?? 0,
                ScopedRuntimeExperimentDryRunObservationTokenDeltaTotal = scopedRuntimeExperimentDryRunObservation?.Report.TokenDeltaTotal ?? 0,
                ScopedRuntimeExperimentDryRunObservationTokenDeltaMax = scopedRuntimeExperimentDryRunObservation?.Report.TokenDeltaMax ?? 0,
                ScopedRuntimeExperimentDryRunObservationRiskAfterPolicy = scopedRuntimeExperimentDryRunObservation?.Report.RiskAfterPolicy ?? 0,
                ScopedRuntimeExperimentDryRunObservationFormalOutputChanged = scopedRuntimeExperimentDryRunObservation?.Report.FormalOutputChanged ?? 0,
                ScopedRuntimeExperimentDryRunObservationFormalPackageWritten = scopedRuntimeExperimentDryRunObservation?.Report.FormalPackageWritten ?? false,
                ScopedRuntimeExperimentDryRunObservationRuntimeMutated = scopedRuntimeExperimentDryRunObservation?.Report.RuntimeMutated ?? false,
                ScopedRuntimeExperimentDryRunObservationVectorStoreBindingChanged = scopedRuntimeExperimentDryRunObservation?.Report.VectorStoreBindingChanged ?? false,
                ScopedRuntimeExperimentDryRunObservationPackingPolicyChanged = scopedRuntimeExperimentDryRunObservation?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentDryRunObservationPackageOutputChanged = scopedRuntimeExperimentDryRunObservation?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentDryRunObservationNonAllowlistedScopeLeakCount = scopedRuntimeExperimentDryRunObservation?.Report.NonAllowlistedScopeLeakCount ?? 0,
                ScopedRuntimeExperimentDryRunObservationRollbackPlanAvailable = scopedRuntimeExperimentDryRunObservation?.Report.RollbackPlanAvailable ?? false,
                ScopedRuntimeExperimentDryRunObservationBlockedReasons = scopedRuntimeExperimentDryRunObservation?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentDesignFreezeSourcePath = scopedRuntimeExperimentDesignFreeze?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentDesignFreezePassed = scopedRuntimeExperimentDesignFreeze?.Report.FreezePassed ?? false,
                ScopedRuntimeExperimentDesignFreezeStatus = scopedRuntimeExperimentDesignFreeze?.Report.DesignStatus ?? string.Empty,
                ScopedRuntimeExperimentDesignFreezeRecommendation = scopedRuntimeExperimentDesignFreeze?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentDesignFreezeAllowedMode = scopedRuntimeExperimentDesignFreeze?.Report.AllowedMode ?? string.Empty,
                ScopedRuntimeExperimentDesignFreezeAllowlistedScopeCount = scopedRuntimeExperimentDesignFreeze?.Report.AllowlistedScopeCount ?? 0,
                ScopedRuntimeExperimentDesignFreezeObservationRunCount = scopedRuntimeExperimentDesignFreeze?.Report.ObservationRunCount ?? 0,
                ScopedRuntimeExperimentDesignFreezeRiskAfterPolicy = scopedRuntimeExperimentDesignFreeze?.Report.RiskAfterPolicy ?? 0,
                ScopedRuntimeExperimentDesignFreezeFormalOutputChanged = scopedRuntimeExperimentDesignFreeze?.Report.FormalOutputChanged ?? 0,
                ScopedRuntimeExperimentDesignFreezeRuntimeMutated = scopedRuntimeExperimentDesignFreeze?.Report.RuntimeMutated ?? false,
                ScopedRuntimeExperimentDesignFreezeVectorStoreBindingChanged = scopedRuntimeExperimentDesignFreeze?.Report.VectorStoreBindingChanged ?? false,
                ScopedRuntimeExperimentDesignFreezePackingPolicyChanged = scopedRuntimeExperimentDesignFreeze?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentDesignFreezePackageOutputChanged = scopedRuntimeExperimentDesignFreeze?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentDesignFreezeFormalPackageWritten = scopedRuntimeExperimentDesignFreeze?.Report.FormalPackageWritten ?? false,
                ScopedRuntimeExperimentDesignFreezeScopeLeakCount = scopedRuntimeExperimentDesignFreeze?.Report.NonAllowlistedScopeLeakCount ?? 0,
                ScopedRuntimeExperimentDesignFreezeRollbackPlanAvailable = scopedRuntimeExperimentDesignFreeze?.Report.RollbackPlanAvailable ?? false,
                ScopedRuntimeExperimentDesignFreezeReadyForRuntimeExperimentProposal = scopedRuntimeExperimentDesignFreeze?.Report.ReadyForRuntimeExperimentProposal ?? false,
                ScopedRuntimeExperimentDesignFreezeReadyForRuntimeSwitch = scopedRuntimeExperimentDesignFreeze?.Report.ReadyForRuntimeSwitch ?? false,
                ScopedRuntimeExperimentDesignFreezeForbiddenActions = scopedRuntimeExperimentDesignFreeze?.Report.ForbiddenActions ?? Array.Empty<string>(),
                ScopedRuntimeExperimentDesignFreezeBlockedReasons = scopedRuntimeExperimentDesignFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentProposalSourcePath = scopedRuntimeExperimentProposal?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentProposalId = scopedRuntimeExperimentProposal?.Report.ProposalId ?? string.Empty,
                ScopedRuntimeExperimentProposalPassed = scopedRuntimeExperimentProposal?.Report.ProposalPassed ?? false,
                ScopedRuntimeExperimentProposalRecommendation = scopedRuntimeExperimentProposal?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentProposalWorkspaceId = scopedRuntimeExperimentProposal?.Report.WorkspaceId ?? string.Empty,
                ScopedRuntimeExperimentProposalCollectionId = scopedRuntimeExperimentProposal?.Report.CollectionId ?? string.Empty,
                ScopedRuntimeExperimentProposalEvalScopeId = scopedRuntimeExperimentProposal?.Report.EvalScopeId ?? string.Empty,
                ScopedRuntimeExperimentProposalProfileName = scopedRuntimeExperimentProposal?.Report.ProfileName ?? string.Empty,
                ScopedRuntimeExperimentProposalApprovalRequired = scopedRuntimeExperimentProposal?.Report.ApprovalRequired ?? false,
                ScopedRuntimeExperimentProposalApproved = scopedRuntimeExperimentProposal?.Report.Approved ?? false,
                ScopedRuntimeExperimentProposalRuntimeSwitchAllowed = scopedRuntimeExperimentProposal?.Report.RuntimeSwitchAllowed ?? false,
                ScopedRuntimeExperimentProposalFormalRetrievalAllowed = scopedRuntimeExperimentProposal?.Report.FormalRetrievalAllowed ?? false,
                ScopedRuntimeExperimentProposalReadyForRuntimeSwitch = scopedRuntimeExperimentProposal?.Report.ReadyForRuntimeSwitch ?? false,
                ScopedRuntimeExperimentProposalUseForRuntime = scopedRuntimeExperimentProposal?.Report.UseForRuntime ?? false,
                ScopedRuntimeExperimentProposalWriteFormalPackage = scopedRuntimeExperimentProposal?.Report.WriteFormalPackage ?? false,
                ScopedRuntimeExperimentProposalConfigPatchWritten = scopedRuntimeExperimentProposal?.Report.ConfigPatchWritten ?? false,
                ScopedRuntimeExperimentProposalDiBindingChanged = scopedRuntimeExperimentProposal?.Report.DiBindingChanged ?? false,
                ScopedRuntimeExperimentProposalPackingPolicyChanged = scopedRuntimeExperimentProposal?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentProposalPackageOutputChanged = scopedRuntimeExperimentProposal?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentProposalRollbackPlan = scopedRuntimeExperimentProposal?.Report.RollbackPlan ?? string.Empty,
                ScopedRuntimeExperimentProposalKillSwitchPlan = scopedRuntimeExperimentProposal?.Report.KillSwitchPlan ?? string.Empty,
                ScopedRuntimeExperimentProposalBlockedReasons = scopedRuntimeExperimentProposal?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentApprovalSummarySourcePath = scopedRuntimeExperimentApproval?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentApprovalProposalId = scopedRuntimeExperimentApproval?.Report.ProposalId ?? string.Empty,
                ScopedRuntimeExperimentApprovalCount = scopedRuntimeExperimentApproval?.Report.ApprovalCount ?? 0,
                ScopedRuntimeExperimentApprovalRecordExists = scopedRuntimeExperimentApproval?.Report.ApprovalRecordExists ?? false,
                ScopedRuntimeExperimentApprovalId = scopedRuntimeExperimentApproval?.Report.LatestApprovalId ?? string.Empty,
                ScopedRuntimeExperimentApprovalMode = scopedRuntimeExperimentApproval?.Report.ApprovalMode ?? string.Empty,
                ScopedRuntimeExperimentApprovalExpired = scopedRuntimeExperimentApproval?.Report.Expired ?? false,
                ScopedRuntimeExperimentApprovalRevoked = scopedRuntimeExperimentApproval?.Report.Revoked ?? false,
                ScopedRuntimeExperimentApprovalRecommendation = scopedRuntimeExperimentApproval?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentApprovalBlockedReasons = scopedRuntimeExperimentApproval?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentNoOpHarnessSourcePath = scopedRuntimeExperimentNoOpHarness?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentNoOpHarnessPassed = scopedRuntimeExperimentNoOpHarness?.Report.HarnessPassed ?? false,
                ScopedRuntimeExperimentNoOpHarnessRecommendation = scopedRuntimeExperimentNoOpHarness?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentNoOpHarnessTraceCount = scopedRuntimeExperimentNoOpHarness?.Report.NoOpTraceCount ?? 0,
                ScopedRuntimeExperimentNoOpHarnessRuntimeMutated = scopedRuntimeExperimentNoOpHarness?.Report.RuntimeMutated ?? false,
                ScopedRuntimeExperimentNoOpHarnessVectorStoreBindingChanged = scopedRuntimeExperimentNoOpHarness?.Report.VectorStoreBindingChanged ?? false,
                ScopedRuntimeExperimentNoOpHarnessFormalPackageWritten = scopedRuntimeExperimentNoOpHarness?.Report.FormalPackageWritten ?? false,
                ScopedRuntimeExperimentNoOpHarnessPackingPolicyChanged = scopedRuntimeExperimentNoOpHarness?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentNoOpHarnessPackageOutputChanged = scopedRuntimeExperimentNoOpHarness?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentNoOpHarnessFormalRetrievalAllowed = scopedRuntimeExperimentNoOpHarness?.Report.FormalRetrievalAllowed ?? false,
                ScopedRuntimeExperimentNoOpHarnessRuntimeSwitchAllowed = scopedRuntimeExperimentNoOpHarness?.Report.RuntimeSwitchAllowed ?? false,
                ScopedRuntimeExperimentNoOpHarnessReadyForRuntimeSwitch = scopedRuntimeExperimentNoOpHarness?.Report.ReadyForRuntimeSwitch ?? false,
                ScopedRuntimeExperimentNoOpHarnessBlockedReasons = scopedRuntimeExperimentNoOpHarness?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentHarnessFreezeSourcePath = scopedRuntimeExperimentHarnessFreeze?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezePassed = scopedRuntimeExperimentHarnessFreeze?.Report.FreezePassed ?? false,
                ScopedRuntimeExperimentHarnessFreezeRecommendation = scopedRuntimeExperimentHarnessFreeze?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeProposalId = scopedRuntimeExperimentHarnessFreeze?.Report.ProposalId ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeApprovalId = scopedRuntimeExperimentHarnessFreeze?.Report.ApprovalId ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeApprovalMode = scopedRuntimeExperimentHarnessFreeze?.Report.ApprovalMode ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeHarnessStatus = scopedRuntimeExperimentHarnessFreeze?.Report.HarnessStatus ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeAllowedMode = scopedRuntimeExperimentHarnessFreeze?.Report.AllowedMode ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeNextAllowedPhase = scopedRuntimeExperimentHarnessFreeze?.Report.NextAllowedPhase ?? string.Empty,
                ScopedRuntimeExperimentHarnessFreezeRuntimeMutated = scopedRuntimeExperimentHarnessFreeze?.Report.RuntimeMutated ?? false,
                ScopedRuntimeExperimentHarnessFreezeVectorStoreBindingChanged = scopedRuntimeExperimentHarnessFreeze?.Report.VectorStoreBindingChanged ?? false,
                ScopedRuntimeExperimentHarnessFreezeFormalPackageWritten = scopedRuntimeExperimentHarnessFreeze?.Report.FormalPackageWritten ?? false,
                ScopedRuntimeExperimentHarnessFreezePackingPolicyChanged = scopedRuntimeExperimentHarnessFreeze?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentHarnessFreezePackageOutputChanged = scopedRuntimeExperimentHarnessFreeze?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentHarnessFreezeFormalRetrievalAllowed = scopedRuntimeExperimentHarnessFreeze?.Report.FormalRetrievalAllowed ?? false,
                ScopedRuntimeExperimentHarnessFreezeRuntimeSwitchAllowed = scopedRuntimeExperimentHarnessFreeze?.Report.RuntimeSwitchAllowed ?? false,
                ScopedRuntimeExperimentHarnessFreezeReadyForRuntimeSwitch = scopedRuntimeExperimentHarnessFreeze?.Report.ReadyForRuntimeSwitch ?? false,
                ScopedRuntimeExperimentHarnessFreezeForbiddenActions = scopedRuntimeExperimentHarnessFreeze?.Report.ForbiddenActions ?? Array.Empty<string>(),
                ScopedRuntimeExperimentHarnessFreezeBlockedReasons = scopedRuntimeExperimentHarnessFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentPlanSourcePath = guardedScopedRuntimeExperimentPlan?.SourcePath ?? string.Empty,
                GuardedScopedRuntimeExperimentPlanPassed = guardedScopedRuntimeExperimentPlan?.Report.PlanPassed ?? false,
                GuardedScopedRuntimeExperimentPlanRecommendation = guardedScopedRuntimeExperimentPlan?.Report.Recommendation ?? string.Empty,
                GuardedScopedRuntimeExperimentProposalId = guardedScopedRuntimeExperimentPlan?.Report.ProposalId ?? string.Empty,
                GuardedScopedRuntimeExperimentRequiredApprovalMode = guardedScopedRuntimeExperimentPlan?.Report.RequiredApprovalMode ?? string.Empty,
                GuardedScopedRuntimeExperimentSelectedScopes = guardedScopedRuntimeExperimentPlan?.Report.SelectedScopes ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentMaxRequestCount = guardedScopedRuntimeExperimentPlan?.Report.MaxRequestCount ?? 0,
                GuardedScopedRuntimeExperimentMaxDurationMinutes = guardedScopedRuntimeExperimentPlan?.Report.MaxDurationMinutes ?? 0,
                GuardedScopedRuntimeExperimentKillSwitchPlan = guardedScopedRuntimeExperimentPlan?.Report.KillSwitchPlan ?? string.Empty,
                GuardedScopedRuntimeExperimentRollbackPlan = guardedScopedRuntimeExperimentPlan?.Report.RollbackPlan ?? string.Empty,
                GuardedScopedRuntimeExperimentObservationPlan = guardedScopedRuntimeExperimentPlan?.Report.ObservationPlan ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentStopConditions = guardedScopedRuntimeExperimentPlan?.Report.StopConditions ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentForbiddenActions = guardedScopedRuntimeExperimentPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentBlockedReasons = guardedScopedRuntimeExperimentPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentRuntimeApprovalSourcePath = scopedRuntimeExperimentRuntimeApproval?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentRuntimeApprovalGatePassed = scopedRuntimeExperimentRuntimeApproval?.Report.GatePassed ?? false,
                ScopedRuntimeExperimentRuntimeApprovalRecommendation = scopedRuntimeExperimentRuntimeApproval?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentRuntimeApprovalProposalId = scopedRuntimeExperimentRuntimeApproval?.Report.ProposalId ?? string.Empty,
                ScopedRuntimeExperimentRuntimeApprovalId = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalId ?? string.Empty,
                ScopedRuntimeExperimentRuntimeApprovalMode = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalMode ?? string.Empty,
                ScopedRuntimeExperimentRuntimeApprovalApprovedBy = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovedBy ?? string.Empty,
                ScopedRuntimeExperimentRuntimeApprovalExists = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalExists ?? false,
                ScopedRuntimeExperimentRuntimeApprovalExpired = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalExpired ?? false,
                ScopedRuntimeExperimentRuntimeApprovalRevoked = scopedRuntimeExperimentRuntimeApproval?.Report.ApprovalRevoked ?? false,
                ScopedRuntimeExperimentRuntimeApprovalAcknowledgementsPresent = scopedRuntimeExperimentRuntimeApproval?.Report.RequiredAcknowledgementsPresent ?? false,
                ScopedRuntimeExperimentRuntimeApprovalRuntimeSwitchAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.RuntimeSwitchAllowed ?? false,
                ScopedRuntimeExperimentRuntimeApprovalFormalRetrievalAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.FormalRetrievalAllowed ?? false,
                ScopedRuntimeExperimentRuntimeApprovalReadyForRuntimeSwitch = scopedRuntimeExperimentRuntimeApproval?.Report.ReadyForRuntimeSwitch ?? false,
                ScopedRuntimeExperimentRuntimeApprovalUseForRuntime = scopedRuntimeExperimentRuntimeApproval?.Report.UseForRuntime ?? false,
                ScopedRuntimeExperimentRuntimeApprovalFormalPackageWriteAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.FormalPackageWriteAllowed ?? false,
                ScopedRuntimeExperimentRuntimeApprovalPackingPolicyIntegrationAllowed = scopedRuntimeExperimentRuntimeApproval?.Report.PackingPolicyIntegrationAllowed ?? false,
                ScopedRuntimeExperimentRuntimeApprovalBlockedReasons = scopedRuntimeExperimentRuntimeApproval?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentActivationPreflightSourcePath = scopedRuntimeExperimentActivationPreflight?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentActivationPreflightPassed = scopedRuntimeExperimentActivationPreflight?.Report.PreflightPassed ?? false,
                ScopedRuntimeExperimentActivationPreflightRecommendation = scopedRuntimeExperimentActivationPreflight?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentActivationProposalId = scopedRuntimeExperimentActivationPreflight?.Report.ProposalId ?? string.Empty,
                ScopedRuntimeExperimentActivationApprovalId = scopedRuntimeExperimentActivationPreflight?.Report.ApprovalId ?? string.Empty,
                ScopedRuntimeExperimentActivationMode = scopedRuntimeExperimentActivationPreflight?.Report.Mode ?? string.Empty,
                ScopedRuntimeExperimentActivationSelectedScopes = scopedRuntimeExperimentActivationPreflight?.Report.SelectedScopes ?? Array.Empty<string>(),
                ScopedRuntimeExperimentActivationKillSwitchAvailable = scopedRuntimeExperimentActivationPreflight?.Report.KillSwitchAvailable ?? false,
                ScopedRuntimeExperimentActivationRollbackPlanAvailable = scopedRuntimeExperimentActivationPreflight?.Report.RollbackPlanAvailable ?? false,
                ScopedRuntimeExperimentActivationTraceSinkAvailable = scopedRuntimeExperimentActivationPreflight?.Report.TraceSinkAvailable ?? false,
                ScopedRuntimeExperimentActivationConfigPatchPreviewed = scopedRuntimeExperimentActivationPreflight?.Report.ConfigPatchPreviewed ?? false,
                ScopedRuntimeExperimentActivationConfigPatchWritten = scopedRuntimeExperimentActivationPreflight?.Report.ConfigPatchWritten ?? false,
                ScopedRuntimeExperimentActivationDryRunRouteExecuted = scopedRuntimeExperimentActivationPreflight?.Report.RuntimeRouteDryRunExecuted ?? false,
                ScopedRuntimeExperimentActivationDryRunRouteHitCount = scopedRuntimeExperimentActivationPreflight?.Report.DryRunRouteHitCount ?? 0,
                ScopedRuntimeExperimentActivationNonAllowlistedScopeChecked = scopedRuntimeExperimentActivationPreflight?.Report.NonAllowlistedScopeChecked ?? false,
                ScopedRuntimeExperimentActivationScopeLeakCount = scopedRuntimeExperimentActivationPreflight?.Report.NonAllowlistedScopeLeakCount ?? 0,
                ScopedRuntimeExperimentActivationRuntimeMutated = scopedRuntimeExperimentActivationPreflight?.Report.RuntimeMutated ?? false,
                ScopedRuntimeExperimentActivationVectorStoreBindingChanged = scopedRuntimeExperimentActivationPreflight?.Report.VectorStoreBindingChanged ?? false,
                ScopedRuntimeExperimentActivationFormalPackageWritten = scopedRuntimeExperimentActivationPreflight?.Report.FormalPackageWritten ?? false,
                ScopedRuntimeExperimentActivationPackingPolicyChanged = scopedRuntimeExperimentActivationPreflight?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentActivationPackageOutputChanged = scopedRuntimeExperimentActivationPreflight?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentActivationFormalRetrievalAllowed = scopedRuntimeExperimentActivationPreflight?.Report.FormalRetrievalAllowed ?? false,
                ScopedRuntimeExperimentActivationRuntimeSwitchAllowed = scopedRuntimeExperimentActivationPreflight?.Report.RuntimeSwitchAllowed ?? false,
                ScopedRuntimeExperimentActivationReadyForRuntimeSwitch = scopedRuntimeExperimentActivationPreflight?.Report.ReadyForRuntimeSwitch ?? false,
                ScopedRuntimeExperimentActivationRiskAfterPolicy = scopedRuntimeExperimentActivationPreflight?.Report.RiskAfterPolicy ?? 0,
                ScopedRuntimeExperimentActivationFormalOutputChanged = scopedRuntimeExperimentActivationPreflight?.Report.FormalOutputChanged ?? 0,
                ScopedRuntimeExperimentActivationBlockedReasons = scopedRuntimeExperimentActivationPreflight?.Report.BlockedReasons ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentRunSourcePath = guardedScopedRuntimeExperiment?.SourcePath ?? string.Empty,
                GuardedScopedRuntimeExperimentRunPassed = guardedScopedRuntimeExperiment?.Report.ExperimentPassed ?? false,
                GuardedScopedRuntimeExperimentRunRecommendation = guardedScopedRuntimeExperiment?.Report.Recommendation ?? string.Empty,
                GuardedScopedRuntimeExperimentRunProposalId = guardedScopedRuntimeExperiment?.Report.ProposalId ?? string.Empty,
                GuardedScopedRuntimeExperimentRunApprovalId = guardedScopedRuntimeExperiment?.Report.ApprovalId ?? string.Empty,
                GuardedScopedRuntimeExperimentRunMode = guardedScopedRuntimeExperiment?.Report.Mode ?? string.Empty,
                GuardedScopedRuntimeExperimentRunSelectedScopes = guardedScopedRuntimeExperiment?.Report.SelectedScopes ?? Array.Empty<string>(),
                GuardedScopedRuntimeExperimentRunRequestCount = guardedScopedRuntimeExperiment?.Report.RequestCount ?? 0,
                GuardedScopedRuntimeExperimentRunRouteHitCount = guardedScopedRuntimeExperiment?.Report.ExperimentRouteHitCount ?? 0,
                GuardedScopedRuntimeExperimentRunNonAllowlistedLeakCount = guardedScopedRuntimeExperiment?.Report.NonAllowlistedScopeLeakCount ?? 0,
                GuardedScopedRuntimeExperimentRunRiskAfterPolicy = guardedScopedRuntimeExperiment?.Report.RiskAfterPolicy ?? 0,
                GuardedScopedRuntimeExperimentRunFormalOutputChanged = guardedScopedRuntimeExperiment?.Report.FormalOutputChanged ?? 0,
                GuardedScopedRuntimeExperimentRunPackageOutputChanged = guardedScopedRuntimeExperiment?.Report.PackageOutputChanged ?? false,
                GuardedScopedRuntimeExperimentRunPackingPolicyChanged = guardedScopedRuntimeExperiment?.Report.PackingPolicyChanged ?? false,
                GuardedScopedRuntimeExperimentRunRuntimeMutated = guardedScopedRuntimeExperiment?.Report.RuntimeMutated ?? false,
                GuardedScopedRuntimeExperimentRunVectorStoreBindingChanged = guardedScopedRuntimeExperiment?.Report.VectorStoreBindingChanged ?? false,
                GuardedScopedRuntimeExperimentRunFormalPackageWritten = guardedScopedRuntimeExperiment?.Report.FormalPackageWritten ?? false,
                GuardedScopedRuntimeExperimentRunKillSwitchAvailable = guardedScopedRuntimeExperiment?.Report.KillSwitchAvailable ?? false,
                GuardedScopedRuntimeExperimentRunKillSwitchTriggered = guardedScopedRuntimeExperiment?.Report.KillSwitchTriggered ?? false,
                GuardedScopedRuntimeExperimentRunRollbackVerified = guardedScopedRuntimeExperiment?.Report.RollbackVerified ?? false,
                GuardedScopedRuntimeExperimentRunErrorCount = guardedScopedRuntimeExperiment?.Report.ErrorCount ?? 0,
                GuardedScopedRuntimeExperimentRunBlockedReasons = guardedScopedRuntimeExperiment?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentObservationWindowSourcePath = scopedRuntimeExperimentObservationWindow?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentObservationWindowId = scopedRuntimeExperimentObservationWindow?.Report.ObservationWindowId ?? string.Empty,
                ScopedRuntimeExperimentObservationWindowPassed = scopedRuntimeExperimentObservationWindow?.Report.ObservationPassed ?? false,
                ScopedRuntimeExperimentObservationWindowRecommendation = scopedRuntimeExperimentObservationWindow?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentObservationWindowRunCount = scopedRuntimeExperimentObservationWindow?.Report.ObservationRunCount ?? 0,
                ScopedRuntimeExperimentObservationWindowRequestCount = scopedRuntimeExperimentObservationWindow?.Report.RequestCount ?? 0,
                ScopedRuntimeExperimentObservationWindowRouteHitCount = scopedRuntimeExperimentObservationWindow?.Report.ExperimentRouteHitCount ?? 0,
                ScopedRuntimeExperimentObservationWindowScopeLeakCount = scopedRuntimeExperimentObservationWindow?.Report.NonAllowlistedScopeLeakCount ?? 0,
                ScopedRuntimeExperimentObservationWindowRiskAfterPolicy = scopedRuntimeExperimentObservationWindow?.Report.RiskAfterPolicy ?? 0,
                ScopedRuntimeExperimentObservationWindowFormalOutputChanged = scopedRuntimeExperimentObservationWindow?.Report.FormalOutputChanged ?? 0,
                ScopedRuntimeExperimentObservationWindowPackageOutputChanged = scopedRuntimeExperimentObservationWindow?.Report.PackageOutputChanged ?? false,
                ScopedRuntimeExperimentObservationWindowPackingPolicyChanged = scopedRuntimeExperimentObservationWindow?.Report.PackingPolicyChanged ?? false,
                ScopedRuntimeExperimentObservationWindowRuntimeMutated = scopedRuntimeExperimentObservationWindow?.Report.RuntimeMutated ?? false,
                ScopedRuntimeExperimentObservationWindowVectorStoreBindingChanged = scopedRuntimeExperimentObservationWindow?.Report.VectorStoreBindingChanged ?? false,
                ScopedRuntimeExperimentObservationWindowFormalPackageWritten = scopedRuntimeExperimentObservationWindow?.Report.FormalPackageWritten ?? false,
                ScopedRuntimeExperimentObservationWindowKillSwitchAvailable = scopedRuntimeExperimentObservationWindow?.Report.KillSwitchAvailable ?? false,
                ScopedRuntimeExperimentObservationWindowKillSwitchSmokePassed = scopedRuntimeExperimentObservationWindow?.Report.KillSwitchSmokePassed ?? false,
                ScopedRuntimeExperimentObservationWindowRollbackVerified = scopedRuntimeExperimentObservationWindow?.Report.RollbackVerified ?? false,
                ScopedRuntimeExperimentObservationWindowTraceCompleteness = scopedRuntimeExperimentObservationWindow?.Report.TraceCompleteness ?? 0,
                ScopedRuntimeExperimentObservationWindowErrorCount = scopedRuntimeExperimentObservationWindow?.Report.ErrorCount ?? 0,
                ScopedRuntimeExperimentObservationWindowLatencyP95 = scopedRuntimeExperimentObservationWindow?.Report.LatencyP95 ?? 0,
                ScopedRuntimeExperimentObservationWindowBlockedReasons = scopedRuntimeExperimentObservationWindow?.Report.BlockedReasons ?? Array.Empty<string>(),
                ScopedRuntimeExperimentObservationFreezeSourcePath = scopedRuntimeExperimentObservationFreeze?.SourcePath ?? string.Empty,
                ScopedRuntimeExperimentObservationFreezePassed = scopedRuntimeExperimentObservationFreeze?.Report.FreezePassed ?? false,
                ScopedRuntimeExperimentPromotionDecision = scopedRuntimeExperimentObservationFreeze?.Report.PromotionDecision ?? string.Empty,
                ScopedRuntimeExperimentObservationFreezeRecommendation = scopedRuntimeExperimentObservationFreeze?.Report.Recommendation ?? string.Empty,
                ScopedRuntimeExperimentObservationFreezeWindowId = scopedRuntimeExperimentObservationFreeze?.Report.ObservationWindowId ?? string.Empty,
                ScopedRuntimeExperimentObservationFreezeRequestCount = scopedRuntimeExperimentObservationFreeze?.Report.RequestCount ?? 0,
                ScopedRuntimeExperimentObservationFreezeRouteHitCount = scopedRuntimeExperimentObservationFreeze?.Report.ExperimentRouteHitCount ?? 0,
                ScopedRuntimeExperimentObservationFreezeRiskAfterPolicy = scopedRuntimeExperimentObservationFreeze?.Report.RiskAfterPolicy ?? 0,
                ScopedRuntimeExperimentObservationFreezeFormalOutputChanged = scopedRuntimeExperimentObservationFreeze?.Report.FormalOutputChanged ?? 0,
                ScopedRuntimeExperimentObservationFreezeTraceCompleteness = scopedRuntimeExperimentObservationFreeze?.Report.TraceCompleteness ?? 0,
                ScopedRuntimeExperimentObservationFreezeFormalRetrievalAllowed = scopedRuntimeExperimentObservationFreeze?.Report.FormalRetrievalAllowed ?? false,
                ScopedRuntimeExperimentObservationFreezeRuntimeSwitchAllowed = scopedRuntimeExperimentObservationFreeze?.Report.RuntimeSwitchAllowed ?? false,
                ScopedRuntimeExperimentObservationFreezeBlockedReasons = scopedRuntimeExperimentObservationFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalRetrievalIntegrationPlanSourcePath = formalRetrievalIntegrationPlan?.SourcePath ?? string.Empty,
                FormalRetrievalIntegrationPlanPassed = formalRetrievalIntegrationPlan?.Report.PlanPassed ?? false,
                FormalRetrievalIntegrationPlanRecommendation = formalRetrievalIntegrationPlan?.Report.Recommendation ?? string.Empty,
                FormalRetrievalIntegrationPlanAllowedMode = formalRetrievalIntegrationPlan?.Report.AllowedMode ?? string.Empty,
                FormalRetrievalIntegrationPlanRequiredNextPhase = formalRetrievalIntegrationPlan?.Report.RequiredNextPhase ?? string.Empty,
                FormalRetrievalIntegrationPlanFormalRetrievalAllowed = formalRetrievalIntegrationPlan?.Report.FormalRetrievalAllowed ?? false,
                FormalRetrievalIntegrationPlanRuntimeSwitchAllowed = formalRetrievalIntegrationPlan?.Report.RuntimeSwitchAllowed ?? false,
                FormalRetrievalIntegrationPlanReadyForRuntimeSwitch = formalRetrievalIntegrationPlan?.Report.ReadyForRuntimeSwitch ?? false,
                FormalRetrievalIntegrationPlanIntegrationPoints = formalRetrievalIntegrationPlan?.Report.IntegrationPoints ?? Array.Empty<string>(),
                FormalRetrievalIntegrationPlanBlockedReasons = formalRetrievalIntegrationPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalRetrievalIntegrationDecisionSourcePath = formalRetrievalIntegrationDecision?.SourcePath ?? string.Empty,
                FormalRetrievalIntegrationDecisionPassed = formalRetrievalIntegrationDecision?.Report.DecisionPassed ?? false,
                FormalRetrievalIntegrationDecisionGatePassed = formalRetrievalIntegrationDecision?.Report.GatePassed ?? false,
                FormalRetrievalIntegrationDecisionRecommendation = formalRetrievalIntegrationDecision?.Report.Recommendation ?? string.Empty,
                FormalRetrievalIntegrationDecisionValue = formalRetrievalIntegrationDecision?.Report.IntegrationDecision ?? string.Empty,
                FormalRetrievalIntegrationDecisionNextAllowedPhase = formalRetrievalIntegrationDecision?.Report.NextAllowedPhase ?? string.Empty,
                FormalRetrievalIntegrationDecisionReadyForFreeze = formalRetrievalIntegrationDecision?.Report.ReadyForFormalRetrievalIntegrationFreeze ?? false,
                FormalRetrievalIntegrationDecisionReadyForNoOpBindingPlan = formalRetrievalIntegrationDecision?.Report.ReadyForAdapterNoOpBindingPlan ?? false,
                FormalRetrievalIntegrationDecisionFormalRetrievalAllowed = formalRetrievalIntegrationDecision?.Report.FormalRetrievalAllowed ?? false,
                FormalRetrievalIntegrationDecisionRuntimeSwitchAllowed = formalRetrievalIntegrationDecision?.Report.RuntimeSwitchAllowed ?? false,
                FormalRetrievalIntegrationDecisionReadyForRuntimeSwitch = formalRetrievalIntegrationDecision?.Report.ReadyForRuntimeSwitch ?? false,
                FormalRetrievalIntegrationDecisionRiskAfterPolicy = formalRetrievalIntegrationDecision?.Report.RiskAfterPolicy ?? 0,
                FormalRetrievalIntegrationDecisionFormalOutputChanged = formalRetrievalIntegrationDecision?.Report.FormalOutputChanged ?? 0,
                FormalRetrievalIntegrationDecisionPackageOutputChanged = formalRetrievalIntegrationDecision?.Report.PackageOutputChanged ?? false,
                FormalRetrievalIntegrationDecisionPackingPolicyChanged = formalRetrievalIntegrationDecision?.Report.PackingPolicyChanged ?? false,
                FormalRetrievalIntegrationDecisionRuntimeMutated = formalRetrievalIntegrationDecision?.Report.RuntimeMutated ?? false,
                FormalRetrievalIntegrationDecisionVectorStoreBindingChanged = formalRetrievalIntegrationDecision?.Report.VectorStoreBindingChanged ?? false,
                FormalRetrievalIntegrationDecisionBlockedReasons = formalRetrievalIntegrationDecision?.Report.BlockedReasons ?? Array.Empty<string>(),
                ShadowFormalRetrievalAdapterPlanSourcePath = shadowFormalRetrievalAdapterPlan?.SourcePath ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanPassed = shadowFormalRetrievalAdapterPlan?.Report.PlanPassed ?? false,
                ShadowFormalRetrievalAdapterPlanRecommendation = shadowFormalRetrievalAdapterPlan?.Report.Recommendation ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanAllowedMode = shadowFormalRetrievalAdapterPlan?.Report.AllowedMode ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanVectorProviderSource = shadowFormalRetrievalAdapterPlan?.Report.VectorProviderSource ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanGraphCandidateSource = shadowFormalRetrievalAdapterPlan?.Report.GraphCandidateSource ?? string.Empty,
                ShadowFormalRetrievalAdapterPlanFormalRetrievalAllowed = shadowFormalRetrievalAdapterPlan?.Report.FormalRetrievalAllowed ?? false,
                ShadowFormalRetrievalAdapterPlanRuntimeSwitchAllowed = shadowFormalRetrievalAdapterPlan?.Report.RuntimeSwitchAllowed ?? false,
                ShadowFormalRetrievalAdapterPlanForbiddenActions = shadowFormalRetrievalAdapterPlan?.Report.ForbiddenActions ?? Array.Empty<string>(),
                ShadowFormalRetrievalAdapterPlanBlockedReasons = shadowFormalRetrievalAdapterPlan?.Report.BlockedReasons ?? Array.Empty<string>(),
                ShadowFormalRetrievalAdapterSourcePath = shadowFormalRetrievalAdapter?.SourcePath ?? string.Empty,
                ShadowFormalRetrievalAdapterPassed = shadowFormalRetrievalAdapter?.Report.AdapterPassed ?? false,
                ShadowFormalRetrievalAdapterGatePassed = shadowFormalRetrievalAdapter?.Report.GatePassed ?? false,
                ShadowFormalRetrievalAdapterRecommendation = shadowFormalRetrievalAdapter?.Report.Recommendation ?? string.Empty,
                ShadowFormalRetrievalAdapterAllowedMode = shadowFormalRetrievalAdapter?.Report.AllowedMode ?? string.Empty,
                ShadowFormalRetrievalAdapterVectorProviderSource = shadowFormalRetrievalAdapter?.Report.VectorProviderSource ?? string.Empty,
                ShadowFormalRetrievalAdapterGraphCandidateSource = shadowFormalRetrievalAdapter?.Report.GraphCandidateSource ?? string.Empty,
                ShadowFormalRetrievalAdapterSampleCount = shadowFormalRetrievalAdapter?.Report.SampleCount ?? 0,
                ShadowFormalRetrievalAdapterRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.RiskAfterPolicy ?? 0,
                ShadowFormalRetrievalAdapterMustNotHitRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.MustNotHitRiskAfterPolicy ?? 0,
                ShadowFormalRetrievalAdapterLifecycleRiskAfterPolicy = shadowFormalRetrievalAdapter?.Report.LifecycleRiskAfterPolicy ?? 0,
                ShadowFormalRetrievalAdapterFormalOutputChanged = shadowFormalRetrievalAdapter?.Report.FormalOutputChanged ?? 0,
                ShadowFormalRetrievalAdapterFormalSelectedSetChanged = shadowFormalRetrievalAdapter?.Report.FormalSelectedSetChanged ?? false,
                ShadowFormalRetrievalAdapterPackageOutputChanged = shadowFormalRetrievalAdapter?.Report.PackageOutputChanged ?? false,
                ShadowFormalRetrievalAdapterPackingPolicyChanged = shadowFormalRetrievalAdapter?.Report.PackingPolicyChanged ?? false,
                ShadowFormalRetrievalAdapterRuntimeMutated = shadowFormalRetrievalAdapter?.Report.RuntimeMutated ?? false,
                ShadowFormalRetrievalAdapterVectorStoreBindingChanged = shadowFormalRetrievalAdapter?.Report.VectorStoreBindingChanged ?? false,
                ShadowFormalRetrievalAdapterBlockedReasons = shadowFormalRetrievalAdapter?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalAdapterPackageShadowComparisonSourcePath = formalAdapterPackageShadowComparison?.SourcePath ?? string.Empty,
                FormalAdapterPackageShadowComparisonPassed = formalAdapterPackageShadowComparison?.Report.ComparisonPassed ?? false,
                FormalAdapterPackageShadowComparisonGatePassed = formalAdapterPackageShadowComparison?.Report.GatePassed ?? false,
                FormalAdapterPackageShadowComparisonRecommendation = formalAdapterPackageShadowComparison?.Report.Recommendation ?? string.Empty,
                FormalAdapterPackageShadowComparisonAllowedMode = formalAdapterPackageShadowComparison?.Report.AllowedMode ?? string.Empty,
                FormalAdapterPackageShadowComparisonSampleCount = formalAdapterPackageShadowComparison?.Report.SampleCount ?? 0,
                FormalAdapterPackageShadowComparisonRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.RiskAfterPolicy ?? 0,
                FormalAdapterPackageShadowComparisonMustNotHitRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.MustNotHitRiskAfterPolicy ?? 0,
                FormalAdapterPackageShadowComparisonLifecycleRiskAfterPolicy = formalAdapterPackageShadowComparison?.Report.LifecycleRiskAfterPolicy ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaTotal ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaMax = formalAdapterPackageShadowComparison?.Report.TokenDeltaMax ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaBudgetTotal = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetTotal ?? 0,
                FormalAdapterPackageShadowComparisonTokenDeltaBudgetPerSample = formalAdapterPackageShadowComparison?.Report.TokenDeltaBudgetPerSample ?? 0,
                FormalAdapterPackageShadowComparisonFormalOutputChanged = formalAdapterPackageShadowComparison?.Report.FormalOutputChanged ?? 0,
                FormalAdapterPackageShadowComparisonFormalSelectedSetChanged = formalAdapterPackageShadowComparison?.Report.FormalSelectedSetChanged ?? false,
                FormalAdapterPackageShadowComparisonPackageOutputChanged = formalAdapterPackageShadowComparison?.Report.PackageOutputChanged ?? false,
                FormalAdapterPackageShadowComparisonPackingPolicyChanged = formalAdapterPackageShadowComparison?.Report.PackingPolicyChanged ?? false,
                FormalAdapterPackageShadowComparisonRuntimeMutated = formalAdapterPackageShadowComparison?.Report.RuntimeMutated ?? false,
                FormalAdapterPackageShadowComparisonVectorStoreBindingChanged = formalAdapterPackageShadowComparison?.Report.VectorStoreBindingChanged ?? false,
                FormalAdapterPackageShadowComparisonBlockedReasons = formalAdapterPackageShadowComparison?.Report.BlockedReasons ?? Array.Empty<string>(),
                GraphVectorRetrievalQualityAuditSourcePath = graphVectorRetrievalQualityAudit?.SourcePath ?? string.Empty,
                GraphVectorRetrievalQualityAuditPassed = graphVectorRetrievalQualityAudit?.Report.AuditPassed ?? false,
                GraphVectorRetrievalQualityAuditGatePassed = graphVectorRetrievalQualityAudit?.Report.GatePassed ?? false,
                GraphVectorRetrievalQualityAuditRecommendation = graphVectorRetrievalQualityAudit?.Report.Recommendation ?? string.Empty,
                GraphVectorRetrievalQualityAuditAllowedMode = graphVectorRetrievalQualityAudit?.Report.AllowedMode ?? string.Empty,
                GraphVectorRetrievalQualityAuditSampleCount = graphVectorRetrievalQualityAudit?.Report.SampleCount ?? 0,
                GraphVectorRetrievalQualityAuditRecall = graphVectorRetrievalQualityAudit?.Report.Recall ?? 0,
                GraphVectorRetrievalQualityAuditPrecision = graphVectorRetrievalQualityAudit?.Report.Precision ?? 0,
                GraphVectorRetrievalQualityAuditMrr = graphVectorRetrievalQualityAudit?.Report.MeanReciprocalRank ?? 0,
                GraphVectorRetrievalQualityAuditGraphNoiseCount = graphVectorRetrievalQualityAudit?.Report.GraphNoiseCount ?? 0,
                GraphVectorRetrievalQualityAuditVectorNoiseCount = graphVectorRetrievalQualityAudit?.Report.VectorNoiseCount ?? 0,
                GraphVectorRetrievalQualityAuditRankingRegressionCount = graphVectorRetrievalQualityAudit?.Report.RankingRegressionCount ?? 0,
                GraphVectorRetrievalQualityAuditMustHitBelowTopKCount = graphVectorRetrievalQualityAudit?.Report.MustHitBelowTopKCount ?? 0,
                GraphVectorRetrievalQualityAuditRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.RiskAfterPolicy ?? 0,
                GraphVectorRetrievalQualityAuditMustNotHitRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.MustNotHitRiskAfterPolicy ?? 0,
                GraphVectorRetrievalQualityAuditLifecycleRiskAfterPolicy = graphVectorRetrievalQualityAudit?.Report.LifecycleRiskAfterPolicy ?? 0,
                GraphVectorRetrievalQualityAuditSectionMismatchCount = graphVectorRetrievalQualityAudit?.Report.SectionMismatchCount ?? 0,
                GraphVectorRetrievalQualityAuditMetadataEvidenceGapCount = graphVectorRetrievalQualityAudit?.Report.MetadataEvidenceGapCount ?? 0,
                GraphVectorRetrievalQualityAuditFailureClusterIds = graphVectorRetrievalQualityAudit?.Report.FailureClusters.Select(c => c.ClusterId).ToArray() ?? Array.Empty<string>(),
                GraphVectorRetrievalQualityAuditFormalOutputChanged = graphVectorRetrievalQualityAudit?.Report.FormalOutputChanged ?? 0,
                GraphVectorRetrievalQualityAuditFormalSelectedSetChanged = graphVectorRetrievalQualityAudit?.Report.FormalSelectedSetChanged ?? false,
                GraphVectorRetrievalQualityAuditPackageOutputChanged = graphVectorRetrievalQualityAudit?.Report.PackageOutputChanged ?? false,
                GraphVectorRetrievalQualityAuditPackingPolicyChanged = graphVectorRetrievalQualityAudit?.Report.PackingPolicyChanged ?? false,
                GraphVectorRetrievalQualityAuditRuntimeMutated = graphVectorRetrievalQualityAudit?.Report.RuntimeMutated ?? false,
                GraphVectorRetrievalQualityAuditVectorStoreBindingChanged = graphVectorRetrievalQualityAudit?.Report.VectorStoreBindingChanged ?? false,
                GraphVectorRetrievalQualityAuditBlockedReasons = graphVectorRetrievalQualityAudit?.Report.BlockedReasons ?? Array.Empty<string>(),
                RetrievalQualityRepairPreviewSourcePath = retrievalQualityRepairPreview?.SourcePath ?? string.Empty,
                RetrievalQualityRepairPreviewPassed = retrievalQualityRepairPreview?.Report.PreviewPassed ?? false,
                RetrievalQualityRepairPreviewGatePassed = retrievalQualityRepairPreview?.Report.GatePassed ?? false,
                RetrievalQualityRepairPreviewRecommendation = retrievalQualityRepairPreview?.Report.Recommendation ?? string.Empty,
                RetrievalQualityRepairPreviewAllowedMode = retrievalQualityRepairPreview?.Report.AllowedMode ?? string.Empty,
                RetrievalQualityRepairPreviewBestProfileId = retrievalQualityRepairPreview?.Report.BestProfileId ?? string.Empty,
                RetrievalQualityRepairPreviewBaselineRecall = retrievalQualityRepairPreview?.Report.Baseline.Recall ?? 0d,
                RetrievalQualityRepairPreviewBaselinePrecision = retrievalQualityRepairPreview?.Report.Baseline.Precision ?? 0d,
                RetrievalQualityRepairPreviewBaselineMrr = retrievalQualityRepairPreview?.Report.Baseline.MeanReciprocalRank ?? 0d,
                RetrievalQualityRepairPreviewBestRecall = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Recall ?? 0d,
                RetrievalQualityRepairPreviewBestPrecision = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.Precision ?? 0d,
                RetrievalQualityRepairPreviewBestMrr = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MeanReciprocalRank ?? 0d,
                RetrievalQualityRepairPreviewRecallDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.RecallDelta ?? 0d,
                RetrievalQualityRepairPreviewMrrDelta = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MrrDelta ?? 0d,
                RetrievalQualityRepairPreviewMustHitBelowTopKBaseline = retrievalQualityRepairPreview?.Report.Baseline.MustHitBelowTopKCount ?? 0,
                RetrievalQualityRepairPreviewMustHitBelowTopKBest = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.MustHitBelowTopKCount ?? 0,
                RetrievalQualityRepairPreviewProfileEvaluatedCount = retrievalQualityRepairPreview?.Report.Profiles.Count ?? 0,
                RetrievalQualityRepairPreviewRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.RiskAfterPolicy ?? 0,
                RetrievalQualityRepairPreviewMustNotHitRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.MustNotHitRiskAfterPolicy ?? 0,
                RetrievalQualityRepairPreviewLifecycleRiskAfterPolicy = retrievalQualityRepairPreview?.Report.Baseline.LifecycleRiskAfterPolicy ?? 0,
                RetrievalQualityRepairPreviewSectionMismatchCount = retrievalQualityRepairPreview?.Report.Baseline.SectionMismatchCount ?? 0,
                RetrievalQualityRepairPreviewGraphNoiseCount = retrievalQualityRepairPreview?.Report.Baseline.GraphNoiseCount ?? 0,
                RetrievalQualityRepairPreviewRankingRegressionCount = retrievalQualityRepairPreview?.Report.Baseline.RankingRegressionCount ?? 0,
                RetrievalQualityRepairPreviewTokenDeltaTotal = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDelta ?? 0,
                RetrievalQualityRepairPreviewTokenDeltaMax = SelectBestProfile(retrievalQualityRepairPreview?.Report)?.TokenDeltaAbsolute ?? 0,
                RetrievalQualityRepairPreviewFormalOutputChanged = retrievalQualityRepairPreview?.Report.FormalOutputChanged ?? 0,
                RetrievalQualityRepairPreviewFormalSelectedSetChanged = retrievalQualityRepairPreview?.Report.FormalSelectedSetChanged ?? false,
                RetrievalQualityRepairPreviewPackageOutputChanged = retrievalQualityRepairPreview?.Report.PackageOutputChanged ?? false,
                RetrievalQualityRepairPreviewPackingPolicyChanged = retrievalQualityRepairPreview?.Report.PackingPolicyChanged ?? false,
                RetrievalQualityRepairPreviewRuntimeMutated = retrievalQualityRepairPreview?.Report.RuntimeMutated ?? false,
                RetrievalQualityRepairPreviewVectorStoreBindingChanged = retrievalQualityRepairPreview?.Report.VectorStoreBindingChanged ?? false,
                RetrievalQualityRepairPreviewBlockedReasons = retrievalQualityRepairPreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                RuntimeObservableFeatureContractSourcePath = runtimeObservableFeatureContract?.SourcePath ?? string.Empty,
                RuntimeObservableFeatureContractPassed = runtimeObservableFeatureContract?.Report.ContractPassed ?? false,
                RuntimeObservableFeatureContractGatePassed = runtimeObservableFeatureContract?.Report.GatePassed ?? false,
                RuntimeObservableFeatureContractRecommendation = runtimeObservableFeatureContract?.Report.Recommendation ?? string.Empty,
                RuntimeObservableFeatureContractAllowedMode = runtimeObservableFeatureContract?.Report.AllowedMode ?? string.Empty,
                RuntimeObservableFeatureContractBestProfileId = runtimeObservableFeatureContract?.Report.BestProfileId ?? string.Empty,
                RuntimeObservableFeatureContractBestProfileContractStatus = runtimeObservableFeatureContract?.Report.BestProfileContractStatus ?? string.Empty,
                RuntimeObservableFeatureContractForbiddenForScoringCount = runtimeObservableFeatureContract?.Report.ForbiddenForScoringCount ?? 0,
                RuntimeObservableFeatureContractEvalOnlyCount = runtimeObservableFeatureContract?.Report.EvalOnlyCount ?? 0,
                RuntimeObservableFeatureContractDerivedAtRuntimeCount = runtimeObservableFeatureContract?.Report.DerivedAtRuntimeCount ?? 0,
                RuntimeObservableFeatureContractRuntimeObservableCount = runtimeObservableFeatureContract?.Report.RuntimeObservableCount ?? 0,
                RuntimeObservableFeatureContractScoringFeatureCount = runtimeObservableFeatureContract?.Report.ScoringFeatureCount ?? 0,
                RuntimeObservableFeatureContractFilteringFeatureCount = runtimeObservableFeatureContract?.Report.FilteringFeatureCount ?? 0,
                RuntimeObservableFeatureContractCandidateExpansionFeatureCount = runtimeObservableFeatureContract?.Report.CandidateExpansionFeatureCount ?? 0,
                RuntimeObservableFeatureContractSourceScanFiles = runtimeObservableFeatureContract?.Report.SourceScan.ScannedFileCount ?? 0,
                RuntimeObservableFeatureContractFixtureTokenHitCount = runtimeObservableFeatureContract?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                RuntimeObservableFeatureContractFlaggedTokens = runtimeObservableFeatureContract?.Report.SourceScan.FlaggedTokens ?? Array.Empty<string>(),
                RuntimeObservableFeatureContractFormalOutputChanged = runtimeObservableFeatureContract?.Report.FormalOutputChanged ?? 0,
                RuntimeObservableFeatureContractFormalSelectedSetChanged = runtimeObservableFeatureContract?.Report.FormalSelectedSetChanged ?? false,
                RuntimeObservableFeatureContractPackageOutputChanged = runtimeObservableFeatureContract?.Report.PackageOutputChanged ?? false,
                RuntimeObservableFeatureContractPackingPolicyChanged = runtimeObservableFeatureContract?.Report.PackingPolicyChanged ?? false,
                RuntimeObservableFeatureContractRuntimeMutated = runtimeObservableFeatureContract?.Report.RuntimeMutated ?? false,
                RuntimeObservableFeatureContractVectorStoreBindingChanged = runtimeObservableFeatureContract?.Report.VectorStoreBindingChanged ?? false,
                RuntimeObservableFeatureContractBlockedReasons = runtimeObservableFeatureContract?.Report.BlockedReasons ?? Array.Empty<string>(),
                RuntimeRetrievalFeatureDerivationSourcePath = runtimeRetrievalFeatureDerivation?.SourcePath ?? string.Empty,
                RuntimeRetrievalFeatureDerivationPassed = runtimeRetrievalFeatureDerivation?.Report.PreviewPassed ?? false,
                RuntimeRetrievalFeatureDerivationGatePassed = runtimeRetrievalFeatureDerivation?.Report.GatePassed ?? false,
                RuntimeRetrievalFeatureDerivationRecommendation = runtimeRetrievalFeatureDerivation?.Report.Recommendation ?? string.Empty,
                RuntimeRetrievalFeatureDerivationAllowedMode = runtimeRetrievalFeatureDerivation?.Report.AllowedMode ?? string.Empty,
                RuntimeRetrievalFeatureDerivationSampleCount = runtimeRetrievalFeatureDerivation?.Report.SampleCount ?? 0,
                RuntimeRetrievalFeatureDerivationTargetSectionMatchRate = runtimeRetrievalFeatureDerivation?.Report.TargetSectionMatchRate ?? 0,
                RuntimeRetrievalFeatureDerivationRequiredRelationCoverageRate = runtimeRetrievalFeatureDerivation?.Report.RequiredRelationCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationEvidenceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.EvidenceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationSourceAnchorCoverageRate = runtimeRetrievalFeatureDerivation?.Report.SourceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationDerivationCompletenessRate = runtimeRetrievalFeatureDerivation?.Report.DerivationCompletenessRate ?? 0,
                RuntimeRetrievalFeatureDerivationBaselineRecall = runtimeRetrievalFeatureDerivation?.Report.BaselineRecall ?? 0,
                RuntimeRetrievalFeatureDerivationBaselineMrr = runtimeRetrievalFeatureDerivation?.Report.BaselineMeanReciprocalRank ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedRecall = runtimeRetrievalFeatureDerivation?.Report.DerivedRecall ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedMrr = runtimeRetrievalFeatureDerivation?.Report.DerivedMeanReciprocalRank ?? 0,
                RuntimeRetrievalFeatureDerivationEvalDrivenRecall = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenRecall ?? 0,
                RuntimeRetrievalFeatureDerivationEvalDrivenMrr = runtimeRetrievalFeatureDerivation?.Report.EvalDrivenMeanReciprocalRank ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedRecallDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedRecallDelta ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedMrrDelta = runtimeRetrievalFeatureDerivation?.Report.DerivedMrrDelta ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedMustNotHitRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedMustNotHitRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedLifecycleRiskAfterPolicy = runtimeRetrievalFeatureDerivation?.Report.DerivedLifecycleRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationDerivedSectionMismatchCount = runtimeRetrievalFeatureDerivation?.Report.DerivedSectionMismatchCount ?? 0,
                RuntimeRetrievalFeatureDerivationForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivation?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                RuntimeRetrievalFeatureDerivationSourceScanFiles = runtimeRetrievalFeatureDerivation?.Report.SourceScan.ScannedFileCount ?? 0,
                RuntimeRetrievalFeatureDerivationFixtureTokenHitCount = runtimeRetrievalFeatureDerivation?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                RuntimeRetrievalFeatureDerivationFormalOutputChanged = runtimeRetrievalFeatureDerivation?.Report.FormalOutputChanged ?? 0,
                RuntimeRetrievalFeatureDerivationFormalSelectedSetChanged = runtimeRetrievalFeatureDerivation?.Report.FormalSelectedSetChanged ?? false,
                RuntimeRetrievalFeatureDerivationPackageOutputChanged = runtimeRetrievalFeatureDerivation?.Report.PackageOutputChanged ?? false,
                RuntimeRetrievalFeatureDerivationPackingPolicyChanged = runtimeRetrievalFeatureDerivation?.Report.PackingPolicyChanged ?? false,
                RuntimeRetrievalFeatureDerivationRuntimeMutated = runtimeRetrievalFeatureDerivation?.Report.RuntimeMutated ?? false,
                RuntimeRetrievalFeatureDerivationVectorStoreBindingChanged = runtimeRetrievalFeatureDerivation?.Report.VectorStoreBindingChanged ?? false,
                RuntimeRetrievalFeatureDerivationBlockedReasons = runtimeRetrievalFeatureDerivation?.Report.BlockedReasons ?? Array.Empty<string>(),
                RuntimeRetrievalFeatureDerivationRepairSourcePath = runtimeRetrievalFeatureDerivationRepair?.SourcePath ?? string.Empty,
                RuntimeRetrievalFeatureDerivationRepairPassed = runtimeRetrievalFeatureDerivationRepair?.Report.PreviewPassed ?? false,
                RuntimeRetrievalFeatureDerivationRepairGatePassed = runtimeRetrievalFeatureDerivationRepair?.Report.GatePassed ?? false,
                RuntimeRetrievalFeatureDerivationRepairRecommendation = runtimeRetrievalFeatureDerivationRepair?.Report.Recommendation ?? string.Empty,
                RuntimeRetrievalFeatureDerivationRepairAllowedMode = runtimeRetrievalFeatureDerivationRepair?.Report.AllowedMode ?? string.Empty,
                RuntimeRetrievalFeatureDerivationRepairTrainSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.TrainSampleCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutSampleCount = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutSampleCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainBaselineMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairTrainDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.TrainDerivedMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutBaselineMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutBaselineMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedRecall = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedRecall ?? 0,
                RuntimeRetrievalFeatureDerivationRepairHoldoutDerivedMrr = runtimeRetrievalFeatureDerivationRepair?.Report.HoldoutDerivedMrr ?? 0,
                RuntimeRetrievalFeatureDerivationRepairCanonicalRelationCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalRequiredRelationCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationRepairCanonicalEvidenceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalEvidenceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationRepairCanonicalSourceCoverageRate = runtimeRetrievalFeatureDerivationRepair?.Report.CanonicalSourceAnchorCoverageRate ?? 0,
                RuntimeRetrievalFeatureDerivationRepairDerivedRiskAfterPolicy = runtimeRetrievalFeatureDerivationRepair?.Report.DerivedRiskAfterPolicy ?? 0,
                RuntimeRetrievalFeatureDerivationRepairForbiddenSampleAnnotationReadCount = runtimeRetrievalFeatureDerivationRepair?.Report.ForbiddenSampleAnnotationReadCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairSourceScanFiles = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.ScannedFileCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairFixtureTokenHitCount = runtimeRetrievalFeatureDerivationRepair?.Report.SourceScan.FixtureTokenHitCount ?? 0,
                RuntimeRetrievalFeatureDerivationRepairFormalOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalOutputChanged ?? 0,
                RuntimeRetrievalFeatureDerivationRepairFormalSelectedSetChanged = runtimeRetrievalFeatureDerivationRepair?.Report.FormalSelectedSetChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairPackageOutputChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackageOutputChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairPackingPolicyChanged = runtimeRetrievalFeatureDerivationRepair?.Report.PackingPolicyChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairRuntimeMutated = runtimeRetrievalFeatureDerivationRepair?.Report.RuntimeMutated ?? false,
                RuntimeRetrievalFeatureDerivationRepairVectorStoreBindingChanged = runtimeRetrievalFeatureDerivationRepair?.Report.VectorStoreBindingChanged ?? false,
                RuntimeRetrievalFeatureDerivationRepairBlockedReasons = runtimeRetrievalFeatureDerivationRepair?.Report.BlockedReasons ?? Array.Empty<string>(),
                FeatureDerivationFailureFreezeSourcePath = featureDerivationFailureFreeze?.SourcePath ?? string.Empty,
                FeatureDerivationFailureFreezePassed = featureDerivationFailureFreeze?.Report.FreezePassed ?? false,
                FeatureDerivationFailureFreezeStatus = featureDerivationFailureFreeze?.Report.FrozenStatus ?? string.Empty,
                FeatureDerivationFailureFreezeRecommendation = featureDerivationFailureFreeze?.Report.Recommendation ?? string.Empty,
                FeatureDerivationFailureFreezeCanonicalResolverReusable = featureDerivationFailureFreeze?.Report.CanonicalAnchorResolverReusable ?? false,
                FeatureDerivationFailureFreezeRelationDeriverReady = featureDerivationFailureFreeze?.Report.RuntimeRelationIntentDeriverReady ?? false,
                FeatureDerivationFailureFreezeDisabledCapabilities = featureDerivationFailureFreeze?.Report.DisabledCapabilities ?? Array.Empty<string>(),
                FeatureDerivationFailureFreezeRecommendedNextPhases = featureDerivationFailureFreeze?.Report.RecommendedNextPhases ?? Array.Empty<string>(),
                GraphHubNoiseControlSourcePath = graphHubNoiseControl?.SourcePath ?? string.Empty,
                GraphHubNoiseControlPassed = graphHubNoiseControl?.Report.PreviewPassed ?? false,
                GraphHubNoiseControlGatePassed = graphHubNoiseControl?.Report.GatePassed ?? false,
                GraphHubNoiseControlRecommendation = graphHubNoiseControl?.Report.Recommendation ?? string.Empty,
                GraphHubNoiseControlHubItemCount = graphHubNoiseControl?.Report.HubItemCount ?? 0,
                GraphHubNoiseControlAvgDominance = graphHubNoiseControl?.Report.AvgHubDominanceRatio ?? 0,
                GraphHubNoiseControlBaselineRecall = graphHubNoiseControl?.Report.Baseline.Recall ?? 0,
                GraphHubNoiseControlHubCtrlRecall = graphHubNoiseControl?.Report.HubControlled.Recall ?? 0,
                GraphHubNoiseControlRecallDelta = graphHubNoiseControl?.Report.HubControlledRecallDelta ?? 0,
                InputMetadataEnrichmentSourcePath = inputMetadataEnrichment?.SourcePath ?? string.Empty,
                InputMetadataEnrichmentPreviewPassed = inputMetadataEnrichment?.Report.PreviewPassed ?? false,
                InputMetadataEnrichmentGatePassed = inputMetadataEnrichment?.Report.GatePassed ?? false,
                InputMetadataEnrichmentRecommendation = inputMetadataEnrichment?.Report.Recommendation ?? string.Empty,
                InputMetadataEnrichmentCoverageDelta = inputMetadataEnrichment?.Report.MetadataCoverageDelta ?? 0,
                InputMetadataEnrichmentBeforeRecall = inputMetadataEnrichment?.Report.BeforeRecall ?? 0,
                InputMetadataEnrichmentAfterRecall = inputMetadataEnrichment?.Report.AfterRecall ?? 0,
                InputMetadataEnrichmentIndependentNonDenseSourceCount = inputMetadataEnrichment?.Report.IndependentNonDenseSourceCount ?? 0,
                InputMetadataEnrichmentRiskAfterPolicy = inputMetadataEnrichment?.Report.RiskAfterPolicy ?? 0,
                InputMetadataEnrichmentMustNotHitRiskAfterPolicy = inputMetadataEnrichment?.Report.MustNotHitRiskAfterPolicy ?? 0,
                InputMetadataEnrichmentLifecycleRiskAfterPolicy = inputMetadataEnrichment?.Report.LifecycleRiskAfterPolicy ?? 0,
                InputMetadataEnrichmentPackageOutputChanged = inputMetadataEnrichment?.Report.PackageOutputChanged ?? false,
                InputMetadataEnrichmentPackingPolicyChanged = inputMetadataEnrichment?.Report.PackingPolicyChanged ?? false,
                InputMetadataEnrichmentRuntimeMutated = inputMetadataEnrichment?.Report.RuntimeMutated ?? false,
                InputMetadataEnrichmentVectorStoreBindingChanged = inputMetadataEnrichment?.Report.VectorStoreBindingChanged ?? false,
                InputMetadataEnrichmentBlockedReasons = inputMetadataEnrichment?.Report.BlockedReasons ?? Array.Empty<string>(),
                EnrichedCandidateSourceRepairRecheckSourcePath = enrichedCandidateSourceRepairRecheck?.SourcePath ?? string.Empty,
                EnrichedCandidateSourceRepairRecheckPassed = enrichedCandidateSourceRepairRecheck?.Report.RecheckPassed ?? false,
                EnrichedCandidateSourceRepairRecheckGatePassed = enrichedCandidateSourceRepairRecheck?.Report.GatePassed ?? false,
                EnrichedCandidateSourceRepairRecheckRecommendation = enrichedCandidateSourceRepairRecheck?.Report.Recommendation ?? string.Empty,
                EnrichedCandidateSourceRepairQualityImproved = enrichedCandidateSourceRepairRecheck?.Report.QualityImproved ?? false,
                EnrichedCandidateSourceRepairTrainRecallDelta = enrichedCandidateSourceRepairRecheck?.Report.TrainDerivedRecallDelta ?? 0,
                EnrichedCandidateSourceRepairHoldoutRecallDelta = enrichedCandidateSourceRepairRecheck?.Report.HoldoutDerivedRecallDelta ?? 0,
                EnrichedCandidateSourceRepairMustHitBelowTopKDelta = enrichedCandidateSourceRepairRecheck?.Report.MustHitBelowTopKDelta ?? 0,
                EnrichedCandidateSourceRepairRiskAfterPolicy = enrichedCandidateSourceRepairRecheck?.Report.RiskAfterPolicy ?? 0,
                EnrichedCandidateSourceRepairPackageOutputChanged = enrichedCandidateSourceRepairRecheck?.Report.PackageOutputChanged ?? false,
                EnrichedCandidateSourceRepairPackingPolicyChanged = enrichedCandidateSourceRepairRecheck?.Report.PackingPolicyChanged ?? false,
                EnrichedCandidateSourceRepairRuntimeMutated = enrichedCandidateSourceRepairRecheck?.Report.RuntimeMutated ?? false,
                EnrichedCandidateSourceRepairVectorStoreBindingChanged = enrichedCandidateSourceRepairRecheck?.Report.VectorStoreBindingChanged ?? false,
                EnrichedCandidateSourceRepairBlockedReasons = enrichedCandidateSourceRepairRecheck?.Report.BlockedReasons ?? Array.Empty<string>(),
                EnrichedCandidateSourceRepairQualityBlockedReasons = enrichedCandidateSourceRepairRecheck?.Report.QualityBlockedReasons ?? Array.Empty<string>(),
                SourceAwareRankingRepairSourcePath = sourceAwareRankingRepair?.SourcePath ?? string.Empty,
                SourceAwareRankingRepairPassed = sourceAwareRankingRepair?.Report.ReportPassed ?? false,
                SourceAwareRankingRepairGatePassed = sourceAwareRankingRepair?.Report.GatePassed ?? false,
                SourceAwareRankingRepairRecommendation = sourceAwareRankingRepair?.Report.Recommendation ?? string.Empty,
                SourceAwareRankingRepairSelectedProfileId = sourceAwareRankingRepair?.Report.SelectedProfileId ?? string.Empty,
                SourceAwareRankingRepairTrainDevRecallDelta = sourceAwareRankingRepair?.Report.TrainDevRecallDelta ?? 0,
                SourceAwareRankingRepairTestRecallDelta = sourceAwareRankingRepair?.Report.TestRecallDelta ?? 0,
                SourceAwareRankingRepairHoldoutRecallDelta = sourceAwareRankingRepair?.Report.HoldoutRecallDelta ?? 0,
                SourceAwareRankingRepairBlindHoldoutRecallDelta = sourceAwareRankingRepair?.Report.BlindHoldoutRecallDelta ?? 0,
                SourceAwareRankingRepairDenseWinnerLostCount = sourceAwareRankingRepair?.Report.DenseWinnerLostCount ?? 0,
                SourceAwareRankingRepairUniqueSourceRecoveryCount = sourceAwareRankingRepair?.Report.UniqueSourceRecoveryCount ?? 0,
                SourceAwareRankingRepairSourceNoiseCount = sourceAwareRankingRepair?.Report.SourceNoiseCount ?? 0,
                SourceAwareRankingRepairFallbackRate = sourceAwareRankingRepair?.Report.FallbackRate ?? 0,
                SourceAwareRankingRepairRiskAfterPolicy = sourceAwareRankingRepair?.Report.RiskAfterPolicy ?? 0,
                SourceAwareRankingRepairPackageOutputChanged = sourceAwareRankingRepair?.Report.PackageOutputChanged ?? false,
                SourceAwareRankingRepairPackingPolicyChanged = sourceAwareRankingRepair?.Report.PackingPolicyChanged ?? false,
                SourceAwareRankingRepairRuntimeMutated = sourceAwareRankingRepair?.Report.RuntimeMutated ?? false,
                SourceAwareRankingRepairVectorStoreBindingChanged = sourceAwareRankingRepair?.Report.VectorStoreBindingChanged ?? false,
                SourceAwareRankingRepairBlockedReasons = sourceAwareRankingRepair?.Report.BlockedReasons ?? Array.Empty<string>(),
                OutputTokenPriorityShadowSourcePath = outputTokenPriorityShadow?.SourcePath ?? string.Empty,
                OutputTokenPriorityShadowPassed = outputTokenPriorityShadow?.Report.ShadowPassed ?? false,
                OutputTokenPriorityShadowGatePassed = outputTokenPriorityShadow?.Report.GatePassed ?? false,
                OutputTokenPriorityShadowRecommendation = outputTokenPriorityShadow?.Report.Recommendation ?? string.Empty,
                OutputTokenPriorityShadowProfileName = outputTokenPriorityShadow?.Report.ProfileName ?? string.Empty,
                OutputTokenPriorityShadowTokenDeltaTotal = outputTokenPriorityShadow?.Report.TokenDeltaTotal ?? 0,
                OutputTokenPriorityShadowTokenDeltaMax = outputTokenPriorityShadow?.Report.TokenDeltaMax ?? 0,
                OutputTokenPriorityShadowTokenDeltaP95 = outputTokenPriorityShadow?.Report.TokenDeltaP95 ?? 0,
                OutputTokenPriorityShadowTokenBudgetExceededCount = outputTokenPriorityShadow?.Report.TokenBudgetExceededCount ?? 0,
                OutputTokenPriorityShadowPriorityInversionCount = outputTokenPriorityShadow?.Report.PriorityInversionCount ?? 0,
                OutputTokenPriorityShadowDroppedRequiredCandidateCount = outputTokenPriorityShadow?.Report.DroppedRequiredCandidateCount ?? 0,
                OutputTokenPriorityShadowSectionMismatchCount = outputTokenPriorityShadow?.Report.SectionMismatchCount ?? 0,
                OutputTokenPriorityShadowRiskAfterPolicy = outputTokenPriorityShadow?.Report.RiskAfterPolicy ?? 0,
                OutputTokenPriorityShadowFormalSelectedSetChanged = outputTokenPriorityShadow?.Report.FormalSelectedSetChanged ?? false,
                OutputTokenPriorityShadowPackageOutputChanged = outputTokenPriorityShadow?.Report.PackageOutputChanged ?? false,
                OutputTokenPriorityShadowPackingPolicyChanged = outputTokenPriorityShadow?.Report.PackingPolicyChanged ?? false,
                OutputTokenPriorityShadowRuntimeMutated = outputTokenPriorityShadow?.Report.RuntimeMutated ?? false,
                OutputTokenPriorityShadowVectorStoreBindingChanged = outputTokenPriorityShadow?.Report.VectorStoreBindingChanged ?? false,
                OutputTokenPriorityShadowBlockedReasons = outputTokenPriorityShadow?.Report.BlockedReasons ?? Array.Empty<string>(),
                FormalAdapterInputContractSourcePath = formalAdapterInputContract?.SourcePath ?? string.Empty,
                FormalAdapterInputContractPassed = formalAdapterInputContract?.Report.ContractPassed ?? false,
                FormalAdapterInputContractGatePassed = formalAdapterInputContract?.Report.GatePassed ?? false,
                FormalAdapterInputContractRecommendation = formalAdapterInputContract?.Report.Recommendation ?? string.Empty,
                FormalAdapterInputContractVersion = formalAdapterInputContract?.Report.ContractVersion ?? string.Empty,
                FormalAdapterInputContractRuntimeInputFieldCount = formalAdapterInputContract?.Report.RuntimeInputFieldCount ?? 0,
                FormalAdapterInputContractDeniedFieldCount = formalAdapterInputContract?.Report.DeniedFieldCount ?? 0,
                FormalAdapterInputContractForbiddenPropertyCount = formalAdapterInputContract?.Report.ContractForbiddenPropertyCount ?? 0,
                FormalAdapterInputContractFormalSourceForbiddenReadCount = formalAdapterInputContract?.Report.FormalSourceForbiddenReadCount ?? 0,
                FormalAdapterInputContractEvalOnlyForbiddenReadCount = formalAdapterInputContract?.Report.EvalOnlyForbiddenReadCount ?? 0,
                FormalAdapterInputContractDatasetEvalFieldsBlocked = formalAdapterInputContract?.Report.DatasetEvalFieldsBlocked ?? false,
                FormalAdapterInputContractGoldLabelsBlocked = formalAdapterInputContract?.Report.GoldLabelsBlocked ?? false,
                FormalAdapterInputContractSampleMetadataBlocked = formalAdapterInputContract?.Report.SampleMetadataBlocked ?? false,
                FormalAdapterInputContractShadowArtifactFieldsBlocked = formalAdapterInputContract?.Report.ShadowArtifactFieldsBlocked ?? false,
                FormalAdapterInputContractFormalRetrievalAllowed = formalAdapterInputContract?.Report.FormalRetrievalAllowed ?? false,
                FormalAdapterInputContractRuntimeSwitchAllowed = formalAdapterInputContract?.Report.RuntimeSwitchAllowed ?? false,
                FormalAdapterInputContractRuntimeMutated = formalAdapterInputContract?.Report.RuntimeMutated ?? false,
                FormalAdapterInputContractPackageOutputChanged = formalAdapterInputContract?.Report.PackageOutputChanged ?? false,
                FormalAdapterInputContractPackingPolicyChanged = formalAdapterInputContract?.Report.PackingPolicyChanged ?? false,
                FormalAdapterInputContractVectorStoreBindingChanged = formalAdapterInputContract?.Report.VectorStoreBindingChanged ?? false,
                FormalAdapterInputContractBlockedReasons = formalAdapterInputContract?.Report.BlockedReasons ?? Array.Empty<string>(),
                    SourceDiverseShadowAdapterValidationSourcePath = sourceDiverseShadowAdapterValidation?.SourcePath ?? string.Empty,
                    SourceDiverseShadowAdapterValidationPassed = sourceDiverseShadowAdapterValidation?.Report.ValidationPassed ?? false,
                    SourceDiverseShadowAdapterValidationGatePassed = sourceDiverseShadowAdapterValidation?.Report.GatePassed ?? false,
                    SourceDiverseShadowAdapterValidationRecommendation = sourceDiverseShadowAdapterValidation?.Report.Recommendation ?? string.Empty,
                    SourceDiverseShadowAdapterValidationSetSourceDiverse = sourceDiverseShadowAdapterValidation?.Report.ValidationSetSourceDiverse ?? false,
                    SourceDiverseShadowAdapterValidationScopeMetadataPresent = sourceDiverseShadowAdapterValidation?.Report.AllowlistedScopeMetadataPresent ?? false,
                    SourceDiverseShadowAdapterValidationSampleCount = sourceDiverseShadowAdapterValidation?.Report.SampleCount ?? 0,
                    SourceDiverseShadowAdapterValidationOverlapRate = sourceDiverseShadowAdapterValidation?.Report.OverlapRate ?? 0,
                    SourceDiverseShadowAdapterValidationShadowOnlyCount = sourceDiverseShadowAdapterValidation?.Report.ShadowOnlyCount ?? 0,
                    SourceDiverseShadowAdapterValidationHypotheticalAddCount = sourceDiverseShadowAdapterValidation?.Report.HypotheticalAddCount ?? 0,
                    SourceDiverseShadowAdapterValidationHypotheticalRemoveCount = sourceDiverseShadowAdapterValidation?.Report.HypotheticalRemoveCount ?? 0,
                    SourceDiverseShadowAdapterValidationAppliedAddCount = sourceDiverseShadowAdapterValidation?.Report.AppliedAddCount ?? 0,
                    SourceDiverseShadowAdapterValidationAppliedRemoveCount = sourceDiverseShadowAdapterValidation?.Report.AppliedRemoveCount ?? 0,
                    SourceDiverseShadowAdapterValidationUniqueSourceRecoveryCount = sourceDiverseShadowAdapterValidation?.Report.UniqueSourceRecoveryCount ?? 0,
                    SourceDiverseShadowAdapterValidationRiskAfterPolicy = sourceDiverseShadowAdapterValidation?.Report.RiskAfterPolicy ?? 0,
                    SourceDiverseShadowAdapterValidationTokenDeltaTotal = sourceDiverseShadowAdapterValidation?.Report.TokenDeltaTotal ?? 0,
                    SourceDiverseShadowAdapterValidationTokenDeltaMax = sourceDiverseShadowAdapterValidation?.Report.TokenDeltaMax ?? 0,
                    SourceDiverseShadowAdapterValidationSectionDeltaCount = sourceDiverseShadowAdapterValidation?.Report.SectionDeltaCount ?? 0,
                    SourceDiverseShadowAdapterValidationPackageOutputChanged = sourceDiverseShadowAdapterValidation?.Report.PackageOutputChanged ?? false,
                    SourceDiverseShadowAdapterValidationPackingPolicyChanged = sourceDiverseShadowAdapterValidation?.Report.PackingPolicyChanged ?? false,
                    SourceDiverseShadowAdapterValidationRuntimeMutated = sourceDiverseShadowAdapterValidation?.Report.RuntimeMutated ?? false,
                    SourceDiverseShadowAdapterValidationVectorStoreBindingChanged = sourceDiverseShadowAdapterValidation?.Report.VectorStoreBindingChanged ?? false,
                    SourceDiverseShadowAdapterValidationBlockedReasons = sourceDiverseShadowAdapterValidation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowCandidateMergePreviewSourcePath = shadowCandidateMergePreview?.SourcePath ?? string.Empty,
                    ShadowCandidateMergePreviewPassed = shadowCandidateMergePreview?.Report.PreviewPassed ?? false,
                    ShadowCandidateMergePreviewGatePassed = shadowCandidateMergePreview?.Report.GatePassed ?? false,
                    ShadowCandidateMergePreviewRecommendation = shadowCandidateMergePreview?.Report.Recommendation ?? string.Empty,
                    ShadowCandidateMergePreviewMergedSetGenerated = shadowCandidateMergePreview?.Report.PreviewMergedSetGenerated ?? false,
                    ShadowCandidateMergePreviewSampleCount = shadowCandidateMergePreview?.Report.SampleCount ?? 0,
                    ShadowCandidateMergePreviewBaselineCandidateCount = shadowCandidateMergePreview?.Report.BaselineCandidateCount ?? 0,
                    ShadowCandidateMergePreviewShadowAdapterCandidateCount = shadowCandidateMergePreview?.Report.ShadowAdapterCandidateCount ?? 0,
                    ShadowCandidateMergePreviewMergedPreviewCandidateCount = shadowCandidateMergePreview?.Report.MergedPreviewCandidateCount ?? 0,
                    ShadowCandidateMergePreviewPreviewAddCount = shadowCandidateMergePreview?.Report.PreviewAddCount ?? 0,
                    ShadowCandidateMergePreviewPreviewRemoveCount = shadowCandidateMergePreview?.Report.PreviewRemoveCount ?? 0,
                    ShadowCandidateMergePreviewAppliedAddCount = shadowCandidateMergePreview?.Report.AppliedAddCount ?? 0,
                    ShadowCandidateMergePreviewAppliedRemoveCount = shadowCandidateMergePreview?.Report.AppliedRemoveCount ?? 0,
                    ShadowCandidateMergePreviewTokenDeltaTotal = shadowCandidateMergePreview?.Report.TokenDeltaTotal ?? 0,
                    ShadowCandidateMergePreviewTokenDeltaMax = shadowCandidateMergePreview?.Report.TokenDeltaMax ?? 0,
                    ShadowCandidateMergePreviewPriorityOrderDeltaCount = shadowCandidateMergePreview?.Report.PriorityOrderDeltaCount ?? 0,
                    ShadowCandidateMergePreviewPriorityInversionCount = shadowCandidateMergePreview?.Report.PriorityInversionCount ?? 0,
                    ShadowCandidateMergePreviewDroppedRequiredCandidateCount = shadowCandidateMergePreview?.Report.DroppedRequiredCandidateCount ?? 0,
                    ShadowCandidateMergePreviewSectionMismatchCount = shadowCandidateMergePreview?.Report.SectionMismatchCount ?? 0,
                    ShadowCandidateMergePreviewRiskAfterPolicy = shadowCandidateMergePreview?.Report.RiskAfterPolicy ?? 0,
                    ShadowCandidateMergePreviewFormalSelectedSetChanged = shadowCandidateMergePreview?.Report.FormalSelectedSetChanged ?? false,
                    ShadowCandidateMergePreviewPackageOutputChanged = shadowCandidateMergePreview?.Report.PackageOutputChanged ?? false,
                    ShadowCandidateMergePreviewPackingPolicyChanged = shadowCandidateMergePreview?.Report.PackingPolicyChanged ?? false,
                    ShadowCandidateMergePreviewRuntimeMutated = shadowCandidateMergePreview?.Report.RuntimeMutated ?? false,
                    ShadowCandidateMergePreviewVectorStoreBindingChanged = shadowCandidateMergePreview?.Report.VectorStoreBindingChanged ?? false,
                    ShadowCandidateMergePreviewBlockedReasons = shadowCandidateMergePreview?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowCandidateMergePreviewObservationSourcePath = shadowCandidateMergePreviewObservation?.SourcePath ?? string.Empty,
                    ShadowCandidateMergePreviewObservationPassed = shadowCandidateMergePreviewObservation?.Report.ObservationPassed ?? false,
                    ShadowCandidateMergePreviewObservationGatePassed = shadowCandidateMergePreviewObservation?.Report.GatePassed ?? false,
                    ShadowCandidateMergePreviewObservationRecommendation = shadowCandidateMergePreviewObservation?.Report.Recommendation ?? string.Empty,
                    ShadowCandidateMergePreviewObservationRunCount = shadowCandidateMergePreviewObservation?.Report.ObservationRunCount ?? 0,
                    ShadowCandidateMergePreviewObservationSampleCount = shadowCandidateMergePreviewObservation?.Report.SampleObservationCount ?? 0,
                    ShadowCandidateMergePreviewObservationDeterministicStable = shadowCandidateMergePreviewObservation?.Report.DeterministicPreviewStable ?? false,
                    ShadowCandidateMergePreviewObservationPreviewAddRemoveStable = shadowCandidateMergePreviewObservation?.Report.PreviewAddRemoveStable ?? false,
                    ShadowCandidateMergePreviewObservationPreviewAddCountMin = shadowCandidateMergePreviewObservation?.Report.PreviewAddCountMin ?? 0,
                    ShadowCandidateMergePreviewObservationPreviewAddCountMax = shadowCandidateMergePreviewObservation?.Report.PreviewAddCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationPreviewRemoveCountMin = shadowCandidateMergePreviewObservation?.Report.PreviewRemoveCountMin ?? 0,
                    ShadowCandidateMergePreviewObservationPreviewRemoveCountMax = shadowCandidateMergePreviewObservation?.Report.PreviewRemoveCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationAppliedAddCountMax = shadowCandidateMergePreviewObservation?.Report.AppliedAddCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationAppliedRemoveCountMax = shadowCandidateMergePreviewObservation?.Report.AppliedRemoveCountMax ?? 0,
                    ShadowCandidateMergePreviewObservationRiskAfterPolicyMax = shadowCandidateMergePreviewObservation?.Report.RiskAfterPolicyMax ?? 0,
                    ShadowCandidateMergePreviewObservationTokenDeltaTotalMax = shadowCandidateMergePreviewObservation?.Report.TokenDeltaTotalMax ?? 0,
                    ShadowCandidateMergePreviewObservationTokenDeltaMaxMax = shadowCandidateMergePreviewObservation?.Report.TokenDeltaMaxMax ?? 0,
                    ShadowCandidateMergePreviewObservationPriorityInversionCountTotal = shadowCandidateMergePreviewObservation?.Report.PriorityInversionCountTotal ?? 0,
                    ShadowCandidateMergePreviewObservationSectionMismatchCountTotal = shadowCandidateMergePreviewObservation?.Report.SectionMismatchCountTotal ?? 0,
                    ShadowCandidateMergePreviewObservationFormalOutputChangedMax = shadowCandidateMergePreviewObservation?.Report.FormalOutputChangedMax ?? 0,
                    ShadowCandidateMergePreviewObservationPackageOutputChanged = shadowCandidateMergePreviewObservation?.Report.PackageOutputChanged ?? false,
                    ShadowCandidateMergePreviewObservationPackingPolicyChanged = shadowCandidateMergePreviewObservation?.Report.PackingPolicyChanged ?? false,
                    ShadowCandidateMergePreviewObservationRuntimeMutated = shadowCandidateMergePreviewObservation?.Report.RuntimeMutated ?? false,
                    ShadowCandidateMergePreviewObservationVectorStoreBindingChanged = shadowCandidateMergePreviewObservation?.Report.VectorStoreBindingChanged ?? false,
                    ShadowCandidateMergePreviewObservationBlockedReasons = shadowCandidateMergePreviewObservation?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ShadowMergeStabilityFreezeSourcePath = shadowMergeStabilityFreeze?.SourcePath ?? string.Empty,
                    ShadowMergeStabilityFreezePassed = shadowMergeStabilityFreeze?.Report.FreezePassed ?? false,
                    ShadowMergeStabilityFreezeRecommendation = shadowMergeStabilityFreeze?.Report.Recommendation ?? string.Empty,
                    ShadowMergePromotionDecisionSourcePath = shadowMergePromotionDecision?.SourcePath ?? string.Empty,
                    ShadowMergePromotionDecisionPassed = shadowMergePromotionDecision?.Report.PromotionDecisionPassed ?? false,
                    ShadowMergePromotionDecision = shadowMergePromotionDecision?.Report.PromotionDecision ?? string.Empty,
                    ShadowMergeNextAllowedPhase = shadowMergePromotionDecision?.Report.NextAllowedPhase ?? shadowMergeStabilityFreeze?.Report.NextAllowedPhase ?? string.Empty,
                    ShadowMergeObservationRunCount = shadowMergePromotionDecision?.Report.ObservationRunCount ?? shadowMergeStabilityFreeze?.Report.ObservationRunCount ?? 0,
                    ShadowMergeSampleObservationCount = shadowMergePromotionDecision?.Report.SampleObservationCount ?? shadowMergeStabilityFreeze?.Report.SampleObservationCount ?? 0,
                    ShadowMergeDeterministicPreviewStable = shadowMergePromotionDecision?.Report.DeterministicPreviewStable ?? shadowMergeStabilityFreeze?.Report.DeterministicPreviewStable ?? false,
                    ShadowMergePreviewAddCountMin = shadowMergePromotionDecision?.Report.PreviewAddCountMin ?? shadowMergeStabilityFreeze?.Report.PreviewAddCountMin ?? 0,
                    ShadowMergePreviewAddCountMax = shadowMergePromotionDecision?.Report.PreviewAddCountMax ?? shadowMergeStabilityFreeze?.Report.PreviewAddCountMax ?? 0,
                    ShadowMergePreviewRemoveCountMin = shadowMergePromotionDecision?.Report.PreviewRemoveCountMin ?? shadowMergeStabilityFreeze?.Report.PreviewRemoveCountMin ?? 0,
                    ShadowMergePreviewRemoveCountMax = shadowMergePromotionDecision?.Report.PreviewRemoveCountMax ?? shadowMergeStabilityFreeze?.Report.PreviewRemoveCountMax ?? 0,
                    ShadowMergeAppliedAddCountMax = shadowMergePromotionDecision?.Report.AppliedAddCountMax ?? shadowMergeStabilityFreeze?.Report.AppliedAddCountMax ?? 0,
                    ShadowMergeAppliedRemoveCountMax = shadowMergePromotionDecision?.Report.AppliedRemoveCountMax ?? shadowMergeStabilityFreeze?.Report.AppliedRemoveCountMax ?? 0,
                    ShadowMergeRiskAfterPolicyMax = shadowMergePromotionDecision?.Report.RiskAfterPolicyMax ?? shadowMergeStabilityFreeze?.Report.RiskAfterPolicyMax ?? 0,
                    ShadowMergeTokenDeltaTotalMax = shadowMergePromotionDecision?.Report.TokenDeltaTotalMax ?? shadowMergeStabilityFreeze?.Report.TokenDeltaTotalMax ?? 0,
                    ShadowMergePriorityInversionCountTotal = shadowMergePromotionDecision?.Report.PriorityInversionCountTotal ?? shadowMergeStabilityFreeze?.Report.PriorityInversionCountTotal ?? 0,
                    ShadowMergeSectionMismatchCountTotal = shadowMergePromotionDecision?.Report.SectionMismatchCountTotal ?? shadowMergeStabilityFreeze?.Report.SectionMismatchCountTotal ?? 0,
                    ShadowMergeFormalOutputChangedMax = shadowMergePromotionDecision?.Report.FormalOutputChangedMax ?? shadowMergeStabilityFreeze?.Report.FormalOutputChangedMax ?? 0,
                    ShadowMergePackageOutputChanged = shadowMergePromotionDecision?.Report.PackageOutputChanged ?? shadowMergeStabilityFreeze?.Report.PackageOutputChanged ?? false,
                    ShadowMergePackingPolicyChanged = shadowMergePromotionDecision?.Report.PackingPolicyChanged ?? shadowMergeStabilityFreeze?.Report.PackingPolicyChanged ?? false,
                    ShadowMergeRuntimeMutated = shadowMergePromotionDecision?.Report.RuntimeMutated ?? shadowMergeStabilityFreeze?.Report.RuntimeMutated ?? false,
                    ShadowMergeVectorStoreBindingChanged = shadowMergePromotionDecision?.Report.VectorStoreBindingChanged ?? shadowMergeStabilityFreeze?.Report.VectorStoreBindingChanged ?? false,
                    ShadowMergeBlockedReasons = shadowMergePromotionDecision?.Report.BlockedReasons ?? shadowMergeStabilityFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeProposalSourcePath = controlledShadowMergeProposal?.SourcePath ?? string.Empty,
                    ControlledShadowMergeProposalPassed = controlledShadowMergeProposal?.Report.ProposalPassed ?? false,
                    ControlledShadowMergeProposalGatePassed = controlledShadowMergeProposal?.Report.GatePassed ?? false,
                    ControlledShadowMergeProposalRecommendation = controlledShadowMergeProposal?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergeProposalId = controlledShadowMergeProposal?.Report.ProposalId ?? string.Empty,
                    ControlledShadowMergeProposalScopeCount = controlledShadowMergeProposal?.Report.ScopeCount ?? 0,
                    ControlledShadowMergeProposalSelectedScopes = controlledShadowMergeProposal?.Report.SelectedScopes ?? Array.Empty<string>(),
                    ControlledShadowMergeProposalMaxRequestCount = controlledShadowMergeProposal?.Report.MaxRequestCount ?? 0,
                    ControlledShadowMergeProposalMaxDurationMinutes = controlledShadowMergeProposal?.Report.MaxDurationMinutes ?? 0,
                    ControlledShadowMergeProposalMaxPreviewAddCount = controlledShadowMergeProposal?.Report.MaxPreviewAddCount ?? 0,
                    ControlledShadowMergeProposalMaxPreviewRemoveCount = controlledShadowMergeProposal?.Report.MaxPreviewRemoveCount ?? 0,
                    ControlledShadowMergeProposalRollbackPlanPresent = controlledShadowMergeProposal?.Report.RollbackPlanPresent ?? false,
                    ControlledShadowMergeProposalKillSwitchPlanPresent = controlledShadowMergeProposal?.Report.KillSwitchPlanPresent ?? false,
                    ControlledShadowMergeProposalObservationConditionCount = controlledShadowMergeProposal?.Report.ObservationConditions.Count ?? 0,
                    ControlledShadowMergeProposalStopConditionCount = controlledShadowMergeProposal?.Report.StopConditions.Count ?? 0,
                    ControlledShadowMergeProposalFormalRetrievalAllowed = controlledShadowMergeProposal?.Report.FormalRetrievalAllowed ?? false,
                    ControlledShadowMergeProposalRuntimeSwitchAllowed = controlledShadowMergeProposal?.Report.RuntimeSwitchAllowed ?? false,
                    ControlledShadowMergeProposalRuntimeMutated = controlledShadowMergeProposal?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeProposalBlockedReasons = controlledShadowMergeProposal?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeDryRunSourcePath = controlledShadowMergeDryRun?.SourcePath ?? string.Empty,
                    ControlledShadowMergeDryRunPassed = controlledShadowMergeDryRun?.Report.DryRunPassed ?? false,
                    ControlledShadowMergeDryRunGatePassed = controlledShadowMergeDryRun?.Report.GatePassed ?? false,
                    ControlledShadowMergeDryRunRecommendation = controlledShadowMergeDryRun?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergeDryRunProposalConstraintsApplied = controlledShadowMergeDryRun?.Report.ProposalConstraintsApplied ?? false,
                    ControlledShadowMergeDryRunAddRemoveLimitEnforced = controlledShadowMergeDryRun?.Report.AddRemoveLimitEnforced ?? false,
                    ControlledShadowMergeDryRunTokenSectionPriorityGatePassed = controlledShadowMergeDryRun?.Report.TokenSectionPriorityGatePassed ?? false,
                    ControlledShadowMergeDryRunRollbackVerified = controlledShadowMergeDryRun?.Report.RollbackVerified ?? false,
                    ControlledShadowMergeDryRunKillSwitchVerified = controlledShadowMergeDryRun?.Report.KillSwitchVerified ?? false,
                    ControlledShadowMergeDryRunPreviewAddCount = controlledShadowMergeDryRun?.Report.PreviewAddCount ?? 0,
                    ControlledShadowMergeDryRunPreviewRemoveCount = controlledShadowMergeDryRun?.Report.PreviewRemoveCount ?? 0,
                    ControlledShadowMergeDryRunAppliedAddCount = controlledShadowMergeDryRun?.Report.AppliedAddCount ?? 0,
                    ControlledShadowMergeDryRunAppliedRemoveCount = controlledShadowMergeDryRun?.Report.AppliedRemoveCount ?? 0,
                    ControlledShadowMergeDryRunTokenDeltaTotal = controlledShadowMergeDryRun?.Report.TokenDeltaTotal ?? 0,
                    ControlledShadowMergeDryRunTokenDeltaMax = controlledShadowMergeDryRun?.Report.TokenDeltaMax ?? 0,
                    ControlledShadowMergeDryRunPriorityInversionCount = controlledShadowMergeDryRun?.Report.PriorityInversionCount ?? 0,
                    ControlledShadowMergeDryRunSectionMismatchCount = controlledShadowMergeDryRun?.Report.SectionMismatchCount ?? 0,
                    ControlledShadowMergeDryRunFormalOutputChanged = controlledShadowMergeDryRun?.Report.FormalOutputChanged ?? 0,
                    ControlledShadowMergeDryRunPackageOutputChanged = controlledShadowMergeDryRun?.Report.PackageOutputChanged ?? false,
                    ControlledShadowMergeDryRunPackingPolicyChanged = controlledShadowMergeDryRun?.Report.PackingPolicyChanged ?? false,
                    ControlledShadowMergeDryRunRuntimeMutated = controlledShadowMergeDryRun?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeDryRunVectorStoreBindingChanged = controlledShadowMergeDryRun?.Report.VectorStoreBindingChanged ?? false,
                    ControlledShadowMergeDryRunBlockedReasons = controlledShadowMergeDryRun?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeObservationWindowSourcePath = controlledShadowMergeObservationWindow?.SourcePath ?? string.Empty,
                    ControlledShadowMergeObservationWindowPassed = controlledShadowMergeObservationWindow?.Report.ObservationPassed ?? false,
                    ControlledShadowMergeObservationWindowGatePassed = controlledShadowMergeObservationWindow?.Report.GatePassed ?? false,
                    ControlledShadowMergeObservationWindowRecommendation = controlledShadowMergeObservationWindow?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergeObservationWindowProposalConstraintsApplied = controlledShadowMergeObservationWindow?.Report.ProposalConstraintsApplied ?? false,
                    ControlledShadowMergeObservationWindowRunCount = controlledShadowMergeObservationWindow?.Report.ObservationRunCount ?? 0,
                    ControlledShadowMergeObservationWindowRequestCountTotal = controlledShadowMergeObservationWindow?.Report.RequestCountTotal ?? 0,
                    ControlledShadowMergeObservationWindowMaxRequestCount = controlledShadowMergeObservationWindow?.Report.MaxRequestCount ?? 0,
                    ControlledShadowMergeObservationWindowPreviewAddCountMin = controlledShadowMergeObservationWindow?.Report.PreviewAddCountMin ?? 0,
                    ControlledShadowMergeObservationWindowPreviewAddCountMax = controlledShadowMergeObservationWindow?.Report.PreviewAddCountMax ?? 0,
                    ControlledShadowMergeObservationWindowPreviewRemoveCountMin = controlledShadowMergeObservationWindow?.Report.PreviewRemoveCountMin ?? 0,
                    ControlledShadowMergeObservationWindowPreviewRemoveCountMax = controlledShadowMergeObservationWindow?.Report.PreviewRemoveCountMax ?? 0,
                    ControlledShadowMergeObservationWindowAppliedAddCountMax = controlledShadowMergeObservationWindow?.Report.AppliedAddCountMax ?? 0,
                    ControlledShadowMergeObservationWindowAppliedRemoveCountMax = controlledShadowMergeObservationWindow?.Report.AppliedRemoveCountMax ?? 0,
                    ControlledShadowMergeObservationWindowRiskAfterPolicyMax = controlledShadowMergeObservationWindow?.Report.RiskAfterPolicyMax ?? 0,
                    ControlledShadowMergeObservationWindowTokenDeltaTotalMax = controlledShadowMergeObservationWindow?.Report.TokenDeltaTotalMax ?? 0,
                    ControlledShadowMergeObservationWindowTokenDeltaMaxMax = controlledShadowMergeObservationWindow?.Report.TokenDeltaMaxMax ?? 0,
                    ControlledShadowMergeObservationWindowPriorityInversionCountTotal = controlledShadowMergeObservationWindow?.Report.PriorityInversionCountTotal ?? 0,
                    ControlledShadowMergeObservationWindowSectionMismatchCountTotal = controlledShadowMergeObservationWindow?.Report.SectionMismatchCountTotal ?? 0,
                    ControlledShadowMergeObservationWindowFormalOutputChangedMax = controlledShadowMergeObservationWindow?.Report.FormalOutputChangedMax ?? 0,
                    ControlledShadowMergeObservationWindowPackageOutputChanged = controlledShadowMergeObservationWindow?.Report.PackageOutputChanged ?? false,
                    ControlledShadowMergeObservationWindowPackingPolicyChanged = controlledShadowMergeObservationWindow?.Report.PackingPolicyChanged ?? false,
                    ControlledShadowMergeObservationWindowRuntimeMutated = controlledShadowMergeObservationWindow?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeObservationWindowVectorStoreBindingChanged = controlledShadowMergeObservationWindow?.Report.VectorStoreBindingChanged ?? false,
                    ControlledShadowMergeObservationWindowBlockedReasons = controlledShadowMergeObservationWindow?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledShadowMergeFreezeSourcePath = controlledShadowMergeFreeze?.SourcePath ?? string.Empty,
                    ControlledShadowMergeFreezePassed = controlledShadowMergeFreeze?.Report.FreezePassed ?? false,
                    ControlledShadowMergePromotionDecisionPassed = controlledShadowMergeFreeze?.Report.PromotionDecisionPassed ?? false,
                    ControlledShadowMergeFreezeRecommendation = controlledShadowMergeFreeze?.Report.Recommendation ?? string.Empty,
                    ControlledShadowMergePromotionDecision = controlledShadowMergeFreeze?.Report.PromotionDecision ?? string.Empty,
                    ControlledShadowMergeNextAllowedPhase = controlledShadowMergeFreeze?.Report.NextAllowedPhase ?? string.Empty,
                    ControlledShadowMergeFreezeProposalId = controlledShadowMergeFreeze?.Report.ProposalId ?? string.Empty,
                    ControlledShadowMergeFreezeObservationRunCount = controlledShadowMergeFreeze?.Report.ObservationRunCount ?? 0,
                    ControlledShadowMergeFreezeRequestCountTotal = controlledShadowMergeFreeze?.Report.RequestCountTotal ?? 0,
                    ControlledShadowMergeFreezePreviewAddCountMin = controlledShadowMergeFreeze?.Report.PreviewAddCountMin ?? 0,
                    ControlledShadowMergeFreezePreviewAddCountMax = controlledShadowMergeFreeze?.Report.PreviewAddCountMax ?? 0,
                    ControlledShadowMergeFreezePreviewRemoveCountMin = controlledShadowMergeFreeze?.Report.PreviewRemoveCountMin ?? 0,
                    ControlledShadowMergeFreezePreviewRemoveCountMax = controlledShadowMergeFreeze?.Report.PreviewRemoveCountMax ?? 0,
                    ControlledShadowMergeFreezeAppliedAddCountMax = controlledShadowMergeFreeze?.Report.AppliedAddCountMax ?? 0,
                    ControlledShadowMergeFreezeAppliedRemoveCountMax = controlledShadowMergeFreeze?.Report.AppliedRemoveCountMax ?? 0,
                    ControlledShadowMergeFreezeRiskAfterPolicyMax = controlledShadowMergeFreeze?.Report.RiskAfterPolicyMax ?? 0,
                    ControlledShadowMergeFreezeFormalOutputChangedMax = controlledShadowMergeFreeze?.Report.FormalOutputChangedMax ?? 0,
                    ControlledShadowMergeFreezeFormalPackageWritten = controlledShadowMergeFreeze?.Report.FormalPackageWritten ?? false,
                    ControlledShadowMergeFreezePackageOutputChanged = controlledShadowMergeFreeze?.Report.PackageOutputChanged ?? false,
                    ControlledShadowMergeFreezePackingPolicyChanged = controlledShadowMergeFreeze?.Report.PackingPolicyChanged ?? false,
                    ControlledShadowMergeFreezeRuntimeMutated = controlledShadowMergeFreeze?.Report.RuntimeMutated ?? false,
                    ControlledShadowMergeFreezeVectorStoreBindingChanged = controlledShadowMergeFreeze?.Report.VectorStoreBindingChanged ?? false,
                    ControlledShadowMergeFreezeBlockedReasons = controlledShadowMergeFreeze?.Report.BlockedReasons ?? Array.Empty<string>(),
                    ControlledAppliedMergeProposalSourcePath = controlledAppliedMergeProposal?.SourcePath ?? string.Empty,
                    ControlledAppliedMergeProposalPassed = controlledAppliedMergeProposal?.Report.ProposalPassed ?? false,
                    ControlledAppliedMergeProposalGatePassed = controlledAppliedMergeProposal?.Report.GatePassed ?? false,
                    ControlledAppliedMergeProposalRecommendation = controlledAppliedMergeProposal?.Report.Recommendation ?? string.Empty,
                    ControlledAppliedMergeProposalId = controlledAppliedMergeProposal?.Report.ProposalId ?? string.Empty,
                    ControlledAppliedMergeProposalApprovalMode = controlledAppliedMergeProposal?.Report.RequiredApprovalMode ?? string.Empty,
                    ControlledAppliedMergeProposalNextAllowedPhase = controlledAppliedMergeProposal?.Report.NextAllowedPhase ?? string.Empty,
                    ControlledAppliedMergeProposalScopeCount = controlledAppliedMergeProposal?.Report.ScopeCount ?? 0,
                    ControlledAppliedMergeProposalSelectedScopes = controlledAppliedMergeProposal?.Report.SelectedScopes ?? Array.Empty<string>(),
                    ControlledAppliedMergeProposalMaxAppliedAddCount = controlledAppliedMergeProposal?.Report.MaxAppliedAddCount ?? 0,
                    ControlledAppliedMergeProposalMaxAppliedRemoveCount = controlledAppliedMergeProposal?.Report.MaxAppliedRemoveCount ?? 0,
                    ControlledAppliedMergeProposalStablePreviewAddCount = controlledAppliedMergeProposal?.Report.StablePreviewAddCount ?? 0,
                    ControlledAppliedMergeProposalStablePreviewRemoveCount = controlledAppliedMergeProposal?.Report.StablePreviewRemoveCount ?? 0,
                    ControlledAppliedMergeProposalAppliedAddCount = controlledAppliedMergeProposal?.Report.AppliedAddCount ?? 0,
                    ControlledAppliedMergeProposalAppliedRemoveCount = controlledAppliedMergeProposal?.Report.AppliedRemoveCount ?? 0,
                    ControlledAppliedMergeProposalApprovalPlanPresent = controlledAppliedMergeProposal?.Report.ApprovalPlanPresent ?? false,
                    ControlledAppliedMergeProposalRollbackPlanPresent = controlledAppliedMergeProposal?.Report.RollbackPlanPresent ?? false,
                    ControlledAppliedMergeProposalKillSwitchPlanPresent = controlledAppliedMergeProposal?.Report.KillSwitchPlanPresent ?? false,
                    ControlledAppliedMergeProposalRiskAfterPolicy = controlledAppliedMergeProposal?.Report.RiskAfterPolicy ?? 0,
                    ControlledAppliedMergeProposalFormalOutputChanged = controlledAppliedMergeProposal?.Report.FormalOutputChanged ?? 0,
                    ControlledAppliedMergeProposalFormalPackageWritten = controlledAppliedMergeProposal?.Report.FormalPackageWritten ?? false,
                    ControlledAppliedMergeProposalPackageOutputChanged = controlledAppliedMergeProposal?.Report.PackageOutputChanged ?? false,
                    ControlledAppliedMergeProposalPackingPolicyChanged = controlledAppliedMergeProposal?.Report.PackingPolicyChanged ?? false,
                    ControlledAppliedMergeProposalRuntimeMutated = controlledAppliedMergeProposal?.Report.RuntimeMutated ?? false,
                    ControlledAppliedMergeProposalVectorStoreBindingChanged = controlledAppliedMergeProposal?.Report.VectorStoreBindingChanged ?? false,
                    ControlledAppliedMergeProposalAppliedMergeAllowed = controlledAppliedMergeProposal?.Report.AppliedMergeAllowed ?? false,
                    ControlledAppliedMergeProposalBlockedReasons = controlledAppliedMergeProposal?.Report.BlockedReasons ?? Array.Empty<string>(),
                RetrievalEvalProtocolGateSourcePath = retrievalEvalProtocol?.GateSourcePath ?? string.Empty,
                RetrievalEvalProtocolSourceAuditPath = retrievalEvalProtocol?.SourceAuditPath ?? string.Empty,
                RetrievalEvalProtocolGatePassed = retrievalEvalProtocol?.Gate?.GatePassed ?? false,
                RetrievalEvalProtocolRecommendation = retrievalEvalProtocol?.Gate?.Recommendation ?? string.Empty,
                RetrievalEvalProtocolVersion = retrievalEvalProtocol?.Gate?.Protocol.ProtocolVersion ?? string.Empty,
                RetrievalEvalProtocolVectorTopK = retrievalEvalProtocol?.Gate?.Protocol.VectorTopK ?? 0,
                RetrievalEvalProtocolMergedTopK = retrievalEvalProtocol?.Gate?.Protocol.MergedTopK ?? 0,
                RetrievalEvalProtocolFinalTopK = retrievalEvalProtocol?.Gate?.Protocol.FinalTopK ?? 0,
                RetrievalEvalProtocolHashOrderSensitivityCount = retrievalEvalProtocol?.Gate?.HashOrderSensitivityCount ?? 0,
                RetrievalEvalProtocolTieBreakDeterministic = retrievalEvalProtocol?.Gate?.TieBreakDeterministic ?? false,
                RetrievalEvalProtocolSourceNonDiscriminativeDetected = retrievalEvalProtocol?.Gate?.SourceNonDiscriminativeDetected ?? false,
                RetrievalEvalProtocolTemplateHomogeneityDetected = retrievalEvalProtocol?.Gate?.TemplateHomogeneityDetected ?? false,
                RetrievalEvalProtocolRuntimeChangeGatePassed = retrievalEvalProtocol?.Gate?.RuntimeChangeGatePassed ?? false,
                RetrievalEvalProtocolRiskAfterPolicy = retrievalEvalProtocol?.Gate?.RiskAfterPolicy ?? 0,
                RetrievalEvalProtocolMustNotHitRiskAfterPolicy = retrievalEvalProtocol?.Gate?.MustNotHitRiskAfterPolicy ?? 0,
                RetrievalEvalProtocolLifecycleRiskAfterPolicy = retrievalEvalProtocol?.Gate?.LifecycleRiskAfterPolicy ?? 0,
                RetrievalEvalProtocolNonDiscriminativeSourceCount = retrievalEvalProtocol?.SourceAudit?.NonDiscriminativeSourceCount ?? 0,
                RetrievalEvalProtocolTemplateHomogeneityScore = retrievalEvalProtocol?.SourceAudit?.TemplateHomogeneityScore ?? 0,
                RetrievalEvalProtocolBaselineRecall = retrievalEvalProtocol?.SourceAudit?.BaselineRecall ?? 0,
                RetrievalEvalProtocolMergedRecall = retrievalEvalProtocol?.SourceAudit?.MergedRecall ?? 0,
                RetrievalEvalProtocolBlockedReasons = retrievalEvalProtocol?.Gate?.BlockedReasons ?? Array.Empty<string>(),
                FormalRetrievalIntegrationFreezeSourcePath = formalRetrievalIntegrationFreeze?.SourcePath ?? string.Empty,
                FormalRetrievalIntegrationFreezePassed = formalRetrievalIntegrationFreeze?.Report.FreezePassed ?? false,
                FormalRetrievalIntegrationFreezeRecommendation = formalRetrievalIntegrationFreeze?.Report.Recommendation ?? string.Empty,
                FormalRetrievalIntegrationFreezeSelectedProfile = formalRetrievalIntegrationFreeze?.Report.SelectedProfile ?? string.Empty,
                FormalRetrievalIntegrationFreezeFrozenArtifactCount = formalRetrievalIntegrationFreeze?.Report.FrozenArtifactPaths.Count ?? 0,
                V4GateSatisfied = readinessGate?.Report.Passed ?? IsVectorV4GateSatisfied(recallLoss.A3, recallLoss.Extended)
            };
        }

        return new ServiceVectorShadowQualitySummary
        {
            Available = false,
            CurrentRecommendation = "NoSweepReport"
        };
    }

    private static (VectorResidualRiskAuditReport Report, string SourcePath)? TryLoadVectorResidualRiskReport()
    {
        var candidates = new[]
        {
            Path.Combine("eval", "vector-residual-risk-audit-extended.json"),
            Path.Combine("eval", "vector-residual-risk-audit-a3.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var report = JsonSerializer.Deserialize<VectorResidualRiskAuditReport>(
                    File.ReadAllText(path),
                    JsonOptions);
                if (report is not null)
                {
                    return (report, path);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static (VectorProviderComparisonV310Report Report, string SourcePath)? TryLoadVectorProviderComparisonReport()
    {
        var path = Path.Combine("vector", "providers", "qwen3", "vector-provider-comparison.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorProviderComparisonV310Report>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private static (VectorQwen3ReadinessGateReport Report, string SourcePath)? TryLoadVectorQwen3ReadinessGateReport()
    {
        var path = Path.Combine("vector", "providers", "qwen3", "vector-qwen3-readiness-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorQwen3ReadinessGateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (EmbeddingProviderComparisonFreezeReport Report, string SourcePath)? TryLoadEmbeddingProviderComparisonFreezeReport()
    {
        var path = Path.Combine("vector", "providers", "qwen3", "vector-provider-comparison-freeze.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<EmbeddingProviderComparisonFreezeReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (HybridRetrievalPreviewReport Report, string SourcePath)? TryLoadVectorHybridPreviewReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-preview.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalPreviewReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (HybridRetrievalReadinessGateReport Report, string SourcePath)? TryLoadVectorHybridReadinessGateReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-readiness-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalReadinessGateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (HybridRetrievalRecallRegressionAuditReport Report, string SourcePath)? TryLoadVectorHybridRecallRegressionAuditReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-recall-regression-audit.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalRecallRegressionAuditReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ContextCoreFoundationFreezeReport?> ReadFoundationFreezeReportAsync(
        CancellationToken cancellationToken)
    {
        foreach (var fileName in new[]
        {
            "foundation-release-candidate-gate.json",
            "foundation-freeze-report.json"
        })
        {
            var path = Path.Combine(
                Directory.GetCurrentDirectory(),
                ContextCoreFoundationFreezeRunner.DefaultOutputDirectory,
                fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var report = JsonSerializer.Deserialize<ContextCoreFoundationFreezeReport>(json, JsonOptions);
                if (report is not null)
                {
                    return report;
                }
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<FoundationServiceStatusResponse?> ReadFoundationServiceStatusAsync(
        CancellationToken cancellationToken)
    {
        if (_state.IsServiceMode)
        {
            try
            {
                return await GetServiceClient()
                    .GetFoundationStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ContextCoreApiException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        try
        {
            return await new FoundationStatusService(Directory.GetCurrentDirectory())
                .GetStatusAsync("foundation/status", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<FoundationReportNavigationResponse?> ReadFoundationReportNavigationAsync(
        CancellationToken cancellationToken)
    {
        if (_state.IsServiceMode)
        {
            try
            {
                return await GetServiceClient()
                    .GetFoundationReportsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ContextCoreApiException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        try
        {
            return await new FoundationStatusService(Directory.GetCurrentDirectory())
                .GetReportNavigationAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationApiSecurityDiagnosticsReport?> ReadFoundationApiSecurityDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-api-security-diagnostics.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationApiSecurityDiagnosticsReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationApiContractReport?> ReadFoundationApiContractReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-api-contract-freeze-gate.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("service", "service-api-contract-report.json");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationApiContractReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationServiceAuthDiagnosticsReport?> ReadFoundationServiceAuthDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-auth-diagnostics.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationServiceAuthDiagnosticsReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationServiceDeploymentProfileGateReport?> ReadFoundationServiceDeploymentProfileGateAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-deployment-profile-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationServiceDeploymentProfileGateReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<FoundationOpenApiContractReport?> ReadFoundationOpenApiContractReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "openapi", "service-api-contract-drift-gate.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("service", "openapi", "service-openapi-contract-report.json");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FoundationOpenApiContractReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<HostedServiceSmokeReport?> ReadHostedServiceSmokeReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "hosted", "service-hosted-deployment-smoke.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("service", "hosted", "service-readonly-runtime-smoke.json");
        }

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<HostedServiceSmokeReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<ServiceFoundationFreezeReport?> ReadServiceFoundationFreezeReportAsync(
        CancellationToken cancellationToken)
    {
        var path = Path.Combine("service", "service-foundation-freeze-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ServiceFoundationFreezeReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static (HybridRetrievalPreviewFreezeReport Report, string SourcePath)? TryLoadVectorHybridFreezeReport()
    {
        var path = Path.Combine("vector", "hybrid", "vector-hybrid-freeze-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<HybridRetrievalPreviewFreezeReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (RetrievalDatasetAlignmentAuditSummaryReport Report, string SourcePath)? TryLoadVectorRetrievalDatasetAlignmentAuditSummaryReport()
    {
        var path = Path.Combine("vector", "alignment", "vector-retrieval-dataset-alignment-audit-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<RetrievalDatasetAlignmentAuditSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorEligibilityRecallLossTriageSummaryReport Report, string SourcePath)? TryLoadVectorEligibilityRecallLossTriageSummaryReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-eligibility-recall-loss-triage-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorEligibilityRecallLossTriageSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataRepairPlanSummaryReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataRepairPlanSummaryReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-repair-plan-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataRepairPlanSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataReviewCandidateReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataReviewCandidateReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-review-candidates.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewCandidateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataReviewSummaryReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataReviewSummaryReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-review-summary.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewSummaryReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataSidecarPreviewReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataSidecarPreviewReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-sidecar-preview.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataSidecarPreviewReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorSidecarEligibilityPreviewReport Report, string SourcePath)? TryLoadVectorSidecarEligibilityQualityReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-sidecar-eligibility-quality.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("vector", "eligibility", "vector-sidecar-eligibility-preview.json");
            if (!File.Exists(path))
            {
                return null;
            }
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorSidecarEligibilityPreviewReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorLifecycleMetadataEvidenceBackfillReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataEvidenceBackfillReport()
    {
        var path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-evidence-backfill-audit.json");
        if (!File.Exists(path))
        {
            path = Path.Combine("vector", "eligibility", "vector-lifecycle-metadata-evidence-backfill-preview.json");
            if (!File.Exists(path))
            {
                return null;
            }
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataEvidenceBackfillReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (int CorpusItemCount, int SampleCount, int ValidationIssueCount, int MissingEvidenceCount, int MissingProvenanceCount, IReadOnlyDictionary<string, int> DifficultyBreakdown, IReadOnlyDictionary<string, int> SplitBreakdown, string Recommendation, string SourcePath)? TryLoadRetrievalDatasetV2GenerationSummary()
    {
        var qualityPath = Path.Combine("vector", "dataset-v2", "generated", "quality-report.json");
        var generationPath = Path.Combine("vector", "dataset-v2", "generated", "generation-report.json");
        var quality = TryReadJson<RetrievalDatasetV2QualityReport>(qualityPath);
        if (quality is not null)
        {
            return (
                quality.CorpusItemCount,
                quality.SampleCount,
                quality.ValidationIssueCount,
                quality.MissingEvidenceCount,
                quality.MissingProvenanceCount,
                quality.DifficultyBreakdown,
                quality.SplitBreakdown,
                quality.Recommendation,
                qualityPath);
        }

        var generation = TryReadJson<RetrievalDatasetV2GenerationReport>(generationPath);
        if (generation is null)
        {
            return null;
        }

        return (
            generation.CorpusItemCount,
            generation.SampleCount,
            generation.ValidationIssueCount,
            generation.MissingEvidenceCount,
            generation.MissingProvenanceCount,
            generation.DifficultyBreakdown,
            generation.SplitBreakdown,
            generation.Recommendation,
            generationPath);
    }

    private static (RetrievalDatasetV2MaterializationReport Report, string SourcePath)? TryLoadRetrievalDatasetV2MaterializationSummary()
    {
        var gatePath = Path.Combine("vector", "dataset-v2", "generated", "materialization-gate.json");
        var report = TryReadJson<RetrievalDatasetV2MaterializationReport>(gatePath);
        if (report is not null)
        {
            return (report, gatePath);
        }

        var materializationPath = Path.Combine("vector", "dataset-v2", "generated", "materialization-report.json");
        report = TryReadJson<RetrievalDatasetV2MaterializationReport>(materializationPath);
        return report is null ? null : (report, materializationPath);
    }

    private static (RetrievalDatasetV2ShadowEvalSummaryReport Summary, RetrievalDatasetV2ReadinessGateReport? Gate, string SourcePath)? TryLoadRetrievalDatasetV2ShadowEvalSummary()
    {
        var summaryPath = Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-shadow-eval-summary.json");
        var summary = TryReadJson<RetrievalDatasetV2ShadowEvalSummaryReport>(summaryPath);
        if (summary is null)
        {
            return null;
        }

        var gatePath = Path.Combine("vector", "dataset-v2", "eval", "dataset-v2-readiness-gate.json");
        var gate = TryReadJson<RetrievalDatasetV2ReadinessGateReport>(gatePath);
        return (summary, gate, gate is null ? summaryPath : gatePath);
    }

    private static (RetrievalDatasetV2StressReport Report, string SourcePath)? TryLoadRetrievalDatasetV2StressSummary()
    {
        var gatePath = Path.Combine("vector", "dataset-v2", "stress", "stress-readiness-gate.json");
        var report = TryReadJson<RetrievalDatasetV2StressReport>(gatePath);
        if (report is not null)
        {
            return (report, gatePath);
        }

        var shadowPath = Path.Combine("vector", "dataset-v2", "stress", "stress-shadow-eval.json");
        report = TryReadJson<RetrievalDatasetV2StressReport>(shadowPath);
        if (report is not null)
        {
            return (report, shadowPath);
        }

        var leakagePath = Path.Combine("vector", "dataset-v2", "stress", "leakage-audit.json");
        report = TryReadJson<RetrievalDatasetV2StressReport>(leakagePath);
        return report is null ? null : (report, leakagePath);
    }

    private static (RetrievalDatasetV2StressRecallFailureTriageReport Report, string SourcePath)? TryLoadRetrievalDatasetV2StressFailureTriageSummary()
    {
        var triagePath = Path.Combine("vector", "dataset-v2", "stress", "stress-failure-triage.json");
        var report = TryReadJson<RetrievalDatasetV2StressRecallFailureTriageReport>(triagePath);
        if (report is not null)
        {
            return (report, triagePath);
        }

        var clustersPath = Path.Combine("vector", "dataset-v2", "stress", "stress-failure-clusters.json");
        report = TryReadJson<RetrievalDatasetV2StressRecallFailureTriageReport>(clustersPath);
        return report is null ? null : (report, clustersPath);
    }

    private static (HybridUnionScoringRepairReport Report, HybridUnionScoringRepairProfileReport? BestProfile, string SourcePath)? TryLoadRetrievalDatasetV2HybridScoringRepairSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-gate.json"),
            Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-shadow-eval.json"),
            Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-repair-preview.json")
        };

        foreach (var path in candidates)
        {
            var report = TryReadJson<HybridUnionScoringRepairReport>(path);
            if (report is null)
            {
                continue;
            }

            var best = report.Profiles
                .FirstOrDefault(profile => string.Equals(profile.ProfileName, report.BestProfileName, StringComparison.OrdinalIgnoreCase));
            if (best is null && report.Profiles.Count > 0)
            {
                best = report.Profiles
                    .Where(static profile => !string.Equals(profile.ProfileName, HybridUnionScoringRepairProfiles.BaselineHybridFull, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(static profile => profile.RecallAfterPolicy)
                    .ThenByDescending(static profile => profile.HoldoutRecallAfterPolicy)
                    .ThenBy(static profile => profile.RiskAfterPolicy)
                    .FirstOrDefault();
            }

            return (report, best, path);
        }

        return null;
    }

    private static (HybridScoringRiskRegressionTriageReport Report, string SourcePath)? TryLoadRetrievalDatasetV2HybridScoringRiskTriageSummary()
    {
        var triagePath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage.json");
        var report = TryReadJson<HybridScoringRiskRegressionTriageReport>(triagePath);
        if (report is not null)
        {
            return (report, triagePath);
        }

        var holdoutPath = Path.Combine("vector", "dataset-v2", "stress", "hybrid-scoring-risk-triage-holdout.json");
        report = TryReadJson<HybridScoringRiskRegressionTriageReport>(holdoutPath);
        return report is null ? null : (report, holdoutPath);
    }

    private static (RetrievalDatasetV2StressFreezeReport Report, string SourcePath)? TryLoadRetrievalDatasetV2StressFreezeSummary()
    {
        var path = Path.Combine("vector", "dataset-v2", "stress", "stress-freeze-gate.json");
        var report = TryReadJson<RetrievalDatasetV2StressFreezeReport>(path);
        return report is null ? null : (report, path);
    }

    private static (VectorV4ReadinessRecheckReport Report, string SourcePath)? TryLoadVectorV4ReadinessRecheckSummary()
    {
        var path = Path.Combine("vector", "v4", "vector-v4-readiness-recheck.json");
        var report = TryReadJson<VectorV4ReadinessRecheckReport>(path);
        return report is null ? null : (report, path);
    }

    private static (GuardedFormalRetrievalPreviewReport Report, string SourcePath)? TryLoadGuardedFormalRetrievalPreviewSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-guarded-formal-retrieval-preview-gate.json"),
            Path.Combine("vector", "v4", "vector-guarded-formal-retrieval-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GuardedFormalRetrievalPreviewReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (VectorShadowPackageComparisonReport Report, string SourcePath)? TryLoadVectorShadowPackageComparisonSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-shadow-package-comparison-gate.json"),
            Path.Combine("vector", "v4", "vector-shadow-package-comparison.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<VectorShadowPackageComparisonReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedFormalPreviewOptInReport Report, string SourcePath)? TryLoadScopedFormalPreviewOptInSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-scoped-formal-preview-optin-gate.json"),
            Path.Combine("vector", "v4", "vector-scoped-formal-preview-optin-smoke.json"),
            Path.Combine("vector", "v4", "vector-scoped-formal-preview-optin-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedFormalPreviewOptInReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (LimitedFormalPreviewObservationReport Report, string SourcePath)? TryLoadLimitedFormalPreviewObservationSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-limited-formal-preview-observation-gate.json"),
            Path.Combine("vector", "v4", "vector-limited-formal-preview-observation.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<LimitedFormalPreviewObservationReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (VectorFormalPreviewFreezeReport Report, string SourcePath)? TryLoadVectorFormalPreviewFreezeSummary()
    {
        var path = Path.Combine("vector", "v4", "vector-formal-preview-freeze-gate.json");
        var report = TryReadJson<VectorFormalPreviewFreezeReport>(path);
        return report is null ? null : (report, path);
    }

    private static (ExplicitScopedRuntimeExperimentPlanReport Report, string SourcePath)? TryLoadExplicitScopedRuntimeExperimentPlanSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-gate.json"),
            Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-dry-run.json"),
            Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ExplicitScopedRuntimeExperimentPlanReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentDryRunObservationReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentDryRunObservationSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-dry-run-observation-gate.json"),
            Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-dry-run-observation.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedRuntimeExperimentDryRunObservationReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentDesignFreezeReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentDesignFreezeSummary()
    {
        var path = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-design-freeze-gate.json");
        var report = TryReadJson<ScopedRuntimeExperimentDesignFreezeReport>(path);
        return report is null ? null : (report, path);
    }

    private static (ExplicitScopedRuntimeExperimentProposalReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentProposalSummary()
    {
        var path = Path.Combine("vector", "v4", "vector-scoped-runtime-experiment-proposal-gate.json");
        var report = TryReadJson<ExplicitScopedRuntimeExperimentProposalReport>(path);
        return report is null ? null : (report, path);
    }

    private static (ScopedRuntimeExperimentApprovalSummaryReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentApprovalSummary()
    {
        var path = Path.Combine("vector", "v4", "runtime-experiment", "approval-summary.json");
        var report = TryReadJson<ScopedRuntimeExperimentApprovalSummaryReport>(path);
        return report is null ? null : (report, path);
    }

    private static (ScopedRuntimeExperimentNoOpHarnessReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentNoOpHarnessSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "noop-harness-gate.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "noop-harness-report.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedRuntimeExperimentNoOpHarnessReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentHarnessFreezeReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentHarnessFreezeSummary()
    {
        var path = Path.Combine("vector", "v4", "runtime-experiment", "harness-freeze-gate.json");
        var report = TryReadJson<ScopedRuntimeExperimentHarnessFreezeReport>(path);
        return report is null ? null : (report, path);
    }

    private static (GuardedScopedRuntimeExperimentPlanReport Report, string SourcePath)? TryLoadGuardedScopedRuntimeExperimentPlanSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-plan-gate.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GuardedScopedRuntimeExperimentPlanReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentApprovalGateReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentRuntimeApprovalSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "runtime-approval-gate.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "runtime-approval-summary.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedRuntimeExperimentApprovalGateReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentActivationPreflightReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentActivationPreflightSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "activation-gate.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "dry-run-route-report.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "activation-preflight.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedRuntimeExperimentActivationPreflightReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (GuardedScopedRuntimeExperimentReport Report, string SourcePath)? TryLoadGuardedScopedRuntimeExperimentSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-gate.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-observation.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "guarded-runtime-experiment-report.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GuardedScopedRuntimeExperimentReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentObservationWindowReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentObservationWindowSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "observation-window-gate.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "observation-window-summary.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "observation-window.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedRuntimeExperimentObservationWindowReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ScopedRuntimeExperimentObservationFreezeReport Report, string SourcePath)? TryLoadScopedRuntimeExperimentObservationFreezeSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v4", "runtime-experiment", "promotion-decision.json"),
            Path.Combine("vector", "v4", "runtime-experiment", "observation-freeze.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ScopedRuntimeExperimentObservationFreezeReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (FormalRetrievalIntegrationPlanReport Report, string SourcePath)? TryLoadFormalRetrievalIntegrationPlanSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "formal-retrieval-integration-plan-gate.json"),
            Path.Combine("vector", "v5", "formal-retrieval-integration-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<FormalRetrievalIntegrationPlanReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (FormalRetrievalIntegrationDecisionReport Report, string SourcePath)? TryLoadFormalRetrievalIntegrationDecisionSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "formal-retrieval-integration-decision-gate.json"),
            Path.Combine("vector", "v5", "formal-retrieval-integration-decision.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<FormalRetrievalIntegrationDecisionReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ShadowFormalRetrievalAdapterPlanReport Report, string SourcePath)? TryLoadShadowFormalRetrievalAdapterPlanSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan-gate.json"),
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-plan.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ShadowFormalRetrievalAdapterPlanReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (ShadowFormalRetrievalAdapterReport Report, string SourcePath)? TryLoadShadowFormalRetrievalAdapterSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter-gate.json"),
            Path.Combine("vector", "v5", "shadow-formal-retrieval-adapter.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<ShadowFormalRetrievalAdapterReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (FormalAdapterPackageShadowComparisonReport Report, string SourcePath)? TryLoadFormalAdapterPackageShadowComparisonSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison-gate.json"),
            Path.Combine("vector", "v5", "formal-adapter-package-shadow-comparison.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<FormalAdapterPackageShadowComparisonReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (GraphVectorRetrievalQualityAuditReport Report, string SourcePath)? TryLoadGraphVectorRetrievalQualityAuditSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "graph-vector-retrieval-quality-gate.json"),
            Path.Combine("vector", "v5", "graph-vector-retrieval-quality-audit.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GraphVectorRetrievalQualityAuditReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RetrievalQualityRepairPreviewReport Report, string SourcePath)? TryLoadRetrievalQualityRepairPreviewSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "retrieval-quality-repair-gate.json"),
            Path.Combine("vector", "v5", "retrieval-quality-repair-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RetrievalQualityRepairPreviewReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RuntimeObservableFeatureContractReport Report, string SourcePath)? TryLoadRuntimeObservableFeatureContractSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "runtime-observable-feature-contract-gate.json"),
            Path.Combine("vector", "v5", "runtime-observable-feature-contract.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RuntimeObservableFeatureContractReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RuntimeRetrievalFeatureDerivationReport Report, string SourcePath)? TryLoadRuntimeRetrievalFeatureDerivationSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "runtime-feature-derivation-gate.json"),
            Path.Combine("vector", "v5", "runtime-feature-derivation-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RuntimeRetrievalFeatureDerivationReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (RuntimeFeatureDerivationFailureFreezeReport Report, string SourcePath)? TryLoadRuntimeFeatureDerivationFailureFreezeSummary()
    {
        return TryLoadSummaryReport<RuntimeFeatureDerivationFailureFreezeReport>(
            VectorReportPath("v5", "runtime-feature-derivation-failure-freeze.json"));
    }

    private static (GraphHubNoiseControlReport Report, string SourcePath)? TryLoadGraphHubNoiseControlSummary()
    {
        var candidates = new[] {
            Path.Combine("vector", "v5", "graph-hub-noise-control-gate.json"),
            Path.Combine("vector", "v5", "graph-hub-noise-control-preview.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<GraphHubNoiseControlReport>(path);
            if (report is not null) return (report, path);
        }
        return null;
    }

    private static (RetrievalEvalProtocolGateReport? Gate, CandidateSourceDiscriminabilityAuditReport? SourceAudit, string GateSourcePath, string SourceAuditPath)? TryLoadRetrievalEvalProtocolSummary()
    {
        return TryLoadSummaryPair<RetrievalEvalProtocolGateReport, CandidateSourceDiscriminabilityAuditReport>(
            VectorReportPath("v5", "retrieval-eval-protocol-gate.json"),
            VectorReportPath("v5", "candidate-source-discriminability-audit.json"));
    }

    private static (InputMetadataEnrichmentPreviewReport Report, string SourcePath)? TryLoadInputMetadataEnrichmentSummary()
    {
        return TryLoadSummaryReport<InputMetadataEnrichmentPreviewReport>(
            VectorReportPath("v5", "input-metadata-enrichment-gate.json"),
            VectorReportPath("v5", "input-metadata-enrichment-preview.json"));
    }

    private static (EnrichedCandidateSourceRepairRecheckReport Report, string SourcePath)? TryLoadEnrichedCandidateSourceRepairRecheckSummary()
    {
        return TryLoadSummaryReport<EnrichedCandidateSourceRepairRecheckReport>(
            VectorReportPath("v5", "enriched-candidate-source-repair-recheck-gate.json"),
            VectorReportPath("v5", "enriched-candidate-source-repair-recheck.json"));
    }

    private static (SourceAwareRankingRepairReport Report, string SourcePath)? TryLoadSourceAwareRankingRepairSummary()
    {
        return TryLoadSummaryReport<SourceAwareRankingRepairReport>(
            VectorReportPath("v5", "source-aware-ranking-repair-gate.json"),
            VectorReportPath("v5", "source-aware-ranking-repair.json"));
    }

    private static (OutputTokenPriorityShadowGateReport Report, string SourcePath)? TryLoadOutputTokenPriorityShadowSummary()
    {
        return TryLoadSummaryReport<OutputTokenPriorityShadowGateReport>(
            VectorReportPath("v5", "output-token-priority-shadow-gate.json"),
            VectorReportPath("v5", "output-token-priority-shadow.json"));
    }

    private static (FormalAdapterInputContractReport Report, string SourcePath)? TryLoadFormalAdapterInputContractSummary()
    {
        return TryLoadSummaryReport<FormalAdapterInputContractReport>(
            VectorReportPath("v5", "formal-adapter-input-contract-gate.json"),
            VectorReportPath("v5", "formal-adapter-input-contract.json"));
    }

    private static (SourceDiverseShadowAdapterValidationReport Report, string SourcePath)? TryLoadSourceDiverseShadowAdapterValidationSummary()
        => TryLoadFromDescriptor<SourceDiverseShadowAdapterValidationReport>(ReportSummaryRegistry.V6SourceDiverseShadowAdapter);

    private static (ShadowCandidateMergePreviewReport Report, string SourcePath)? TryLoadShadowCandidateMergePreviewSummary()
        => TryLoadFromDescriptor<ShadowCandidateMergePreviewReport>(ReportSummaryRegistry.V6ShadowCandidateMergePreview);

    private static (ShadowCandidateMergePreviewObservationReport Report, string SourcePath)? TryLoadShadowCandidateMergePreviewObservationSummary()
        => TryLoadFromDescriptor<ShadowCandidateMergePreviewObservationReport>(ReportSummaryRegistry.V6ShadowCandidateMergeObservation);

    private static (ShadowMergeStabilityFreezeReport Report, string SourcePath)? TryLoadShadowMergeStabilityFreezeSummary()
        => TryLoadFromDescriptor<ShadowMergeStabilityFreezeReport>(ReportSummaryRegistry.V6ShadowMergeStabilityFreeze);

    private static (ShadowMergeStabilityFreezeReport Report, string SourcePath)? TryLoadShadowMergePromotionDecisionSummary()
        => TryLoadSummaryReport<ShadowMergeStabilityFreezeReport>(
            VectorReportPath("v6", "shadow-merge-promotion-decision.json"));

    private static (ControlledShadowMergeProposalReport Report, string SourcePath)? TryLoadControlledShadowMergeProposalSummary()
        => TryLoadFromDescriptor<ControlledShadowMergeProposalReport>(ReportSummaryRegistry.V6ControlledShadowMergeProposal);

    private static (ControlledShadowMergeDryRunGateReport Report, string SourcePath)? TryLoadControlledShadowMergeDryRunSummary()
        => TryLoadFromDescriptor<ControlledShadowMergeDryRunGateReport>(ReportSummaryRegistry.V6ControlledShadowMergeDryRun);

    private static (ControlledShadowMergeObservationWindowReport Report, string SourcePath)? TryLoadControlledShadowMergeObservationWindowSummary()
        => TryLoadFromDescriptor<ControlledShadowMergeObservationWindowReport>(ReportSummaryRegistry.V6ControlledShadowMergeObservationWindow);

    private static (ControlledShadowMergeFreezeReport Report, string SourcePath)? TryLoadControlledShadowMergeFreezeSummary()
        => TryLoadFromDescriptor<ControlledShadowMergeFreezeReport>(ReportSummaryRegistry.V6ControlledShadowMergeFreeze);

    private static (ControlledAppliedMergeDryRunObservationReport Report, string SourcePath)? TryLoadControlledAppliedMergeDryRunSummary()
        => TryLoadSummaryReport<ControlledAppliedMergeDryRunObservationReport>(
            VectorReportPath("v6", "controlled-applied-merge-dry-run-decision.json"),
            VectorReportPath("v6", "controlled-applied-merge-dry-run-observation.json"));

    private static (ControlledAppliedMergePreviewFreezeReport Report, string SourcePath)? TryLoadControlledAppliedMergePreviewFreezeSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergePreviewFreezeReport>(ReportSummaryRegistry.V6ControlledAppliedMergePreviewFreeze);

    private static (ControlledAppliedMergeScopedPreviewReport Report, string SourcePath)? TryLoadControlledAppliedMergeScopedPreviewSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeScopedPreviewReport>(ReportSummaryRegistry.V6ControlledAppliedMergeScopedPreview);

    private static (ControlledAppliedMergeProposalReport Report, string SourcePath)? TryLoadControlledAppliedMergeProposalSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeProposalReport>(ReportSummaryRegistry.V6ControlledAppliedMergeProposal);
    private static (FormalRetrievalIntegrationFreezeReport Report, string SourcePath)? TryLoadFormalRetrievalIntegrationFreezeSummary()
    {
        return TryLoadSummaryReport<FormalRetrievalIntegrationFreezeReport>(
            VectorReportPath("v5", "formal-retrieval-integration-freeze-gate.json"),
            VectorReportPath("v5", "formal-retrieval-integration-freeze.json"));
    }

    // OPT loaders

    private static (ArchitectureCleanupFreezeReport Report, string SourcePath)? TryLoadArchitectureCleanupFreezeSummary()
        => TryLoadFromDescriptor<ArchitectureCleanupFreezeReport>(ReportSummaryRegistry.OPTArchitectureCleanupFreeze);

    private static (ArchitectureCleanupFreezeGateReport Report, string SourcePath)? TryLoadArchitectureCleanupFreezeGateSummary()
        => TryLoadFromDescriptor<ArchitectureCleanupFreezeGateReport>(ReportSummaryRegistry.OPTArchitectureCleanupFreezeGate);

    // V7 loaders

    private static (ControlledAppliedMergeRuntimePreviewPlanReport Report, string SourcePath)? TryLoadControlledAppliedMergeRuntimePreviewPlanSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeRuntimePreviewPlanReport>(ReportSummaryRegistry.V7ControlledAppliedMergeRuntimePreviewPlan);

    private static (ControlledAppliedMergeRuntimePreviewDryRunReport Report, string SourcePath)? TryLoadControlledAppliedMergeRuntimePreviewDryRunSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeRuntimePreviewDryRunReport>(ReportSummaryRegistry.V7ControlledAppliedMergeRuntimePreviewDryRun);

    private static (ControlledAppliedMergeRuntimePreviewActivationPreflightReport Report, string SourcePath)? TryLoadControlledAppliedMergeRuntimePreviewActivationPreflightSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeRuntimePreviewActivationPreflightReport>(ReportSummaryRegistry.V7ControlledAppliedMergeRuntimePreviewActivationPreflight);

    private static (ControlledAppliedMergeRuntimePreviewObservationWindowReport Report, string SourcePath)? TryLoadControlledAppliedMergeRuntimePreviewObservationWindowSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeRuntimePreviewObservationWindowReport>(ReportSummaryRegistry.V7ControlledAppliedMergeRuntimePreviewObservationWindow);

    private static (ControlledAppliedMergeRuntimePreviewObservationHardeningReport Report, string SourcePath)? TryLoadControlledAppliedMergeRuntimePreviewObservationHardeningSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeRuntimePreviewObservationHardeningReport>(ReportSummaryRegistry.V7ControlledAppliedMergeRuntimePreviewObservationHardening);

    private static (ControlledAppliedMergeRuntimePreviewObservationFreezeReport Report, string SourcePath)? TryLoadControlledAppliedMergeRuntimePreviewObservationFreezeSummary()
        => TryLoadFromDescriptor<ControlledAppliedMergeRuntimePreviewObservationFreezeReport>(ReportSummaryRegistry.V7ControlledAppliedMergeRuntimePreviewObservationFreeze);

    private static (ScopedRuntimePreviewApprovalPlanReport Report, string SourcePath)? TryLoadScopedRuntimePreviewApprovalPlanSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewApprovalPlanReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewApprovalPlan);

    private static (ScopedRuntimePreviewAuthorizationReport Report, string SourcePath)? TryLoadScopedRuntimePreviewAuthorizationSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewAuthorizationReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewAuthorization);

    private static (ScopedRuntimePreviewAuthorizationHardeningReport Report, string SourcePath)? TryLoadScopedRuntimePreviewAuthorizationHardeningSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewAuthorizationHardeningReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewAuthorizationHardening);

    private static (ScopedRuntimePreviewActivationPreparationReport Report, string SourcePath)? TryLoadScopedRuntimePreviewActivationPreparationSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewActivationPreparationReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewActivationPreparation);

    private static (ScopedRuntimePreviewActivationDryRunReport Report, string SourcePath)? TryLoadScopedRuntimePreviewActivationDryRunSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewActivationDryRunReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewActivationDryRun);

    private static (ScopedRuntimePreviewActivationWindowPreflightReport Report, string SourcePath)? TryLoadScopedRuntimePreviewActivationWindowPreflightSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewActivationWindowPreflightReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewActivationWindowPreflight);

    private static (ScopedRuntimePreviewActivationWindowNoOpExecutionReport Report, string SourcePath)? TryLoadScopedRuntimePreviewActivationWindowNoOpExecutionSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewActivationWindowNoOpExecutionReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewActivationWindowNoOpExecution);

    private static (ScopedRuntimePreviewActivationLiveReadinessFreezeReport Report, string SourcePath)? TryLoadScopedRuntimePreviewActivationLiveReadinessFreezeSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewActivationLiveReadinessFreezeReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewActivationLiveReadinessFreeze);

    private static (ScopedRuntimePreviewLiveActivationExecutionPlanReport Report, string SourcePath)? TryLoadScopedRuntimePreviewLiveActivationExecutionPlanSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewLiveActivationExecutionPlanReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewLiveActivationExecutionPlan);

    private static (ScopedRuntimePreviewLiveActivationExecutionReport Report, string SourcePath)? TryLoadScopedRuntimePreviewLiveActivationExecutionSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewLiveActivationExecutionReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewLiveActivationExecution);

    private static (ScopedRuntimePreviewLiveActivationObservationReport Report, string SourcePath)? TryLoadScopedRuntimePreviewLiveActivationObservationSummary()
        => TryLoadFromDescriptor<ScopedRuntimePreviewLiveActivationObservationReport>(ReportSummaryRegistry.V7ScopedRuntimePreviewLiveActivationObservation);

    private static string VectorReportPath(string phase, string fileName)
    {
        return Path.Combine("vector", phase, fileName);
    }

    private static (T Report, string SourcePath)? TryLoadSummaryReport<T>(params string[] candidatePaths)
    {
        foreach (var path in candidatePaths)
        {
            var report = TryReadJson<T>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static (T Report, string SourcePath)? TryLoadFromDescriptor<T>(ControlRoomReportDescriptor descriptor) where T : class
    {
        return TryLoadSummaryReport<T>(descriptor.AllPaths());
    }

    private static (TPrimary? Primary, TSecondary? Secondary, string PrimarySourcePath, string SecondarySourcePath)? TryLoadSummaryPair<TPrimary, TSecondary>(
        string primaryPath,
        string secondaryPath)
        where TPrimary : class
        where TSecondary : class
    {
        var primary = TryReadJson<TPrimary>(primaryPath);
        var secondary = TryReadJson<TSecondary>(secondaryPath);
        if (primary is null && secondary is null)
        {
            return null;
        }

        return (
            primary,
            secondary,
            primary is null ? string.Empty : primaryPath,
            secondary is null ? string.Empty : secondaryPath);
    }

    private static (RuntimeRetrievalFeatureDerivationRepairReport Report, string SourcePath)? TryLoadRuntimeRetrievalFeatureDerivationRepairSummary()
    {
        var candidates = new[]
        {
            Path.Combine("vector", "v5", "runtime-feature-derivation-repair-gate.json"),
            Path.Combine("vector", "v5", "runtime-feature-derivation-repair.json")
        };
        foreach (var path in candidates)
        {
            var report = TryReadJson<RuntimeRetrievalFeatureDerivationRepairReport>(path);
            if (report is not null)
            {
                return (report, path);
            }
        }

        return null;
    }

    private static RetrievalQualityRepairProfileResult? SelectBestProfile(RetrievalQualityRepairPreviewReport? report)
    {
        if (report is null || string.IsNullOrEmpty(report.BestProfileId))
        {
            return null;
        }

        return report.Profiles.FirstOrDefault(p => string.Equals(p.ProfileId, report.BestProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDatasetV2StressProfileComparisons(RetrievalDatasetV2StressRecallFailureTriageReport? report)
    {
        if (report?.ProfileComparisons is null || report.ProfileComparisons.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", report.ProfileComparisons
            .Take(4)
            .Select(static comparison =>
                $"{comparison.LeftProfileName}:{comparison.LeftRecall:P2}->{comparison.RightProfileName}:{comparison.RightRecall:P2}"));
    }

    private static (VectorLifecycleMetadataReviewBatch Batch, VectorLifecycleMetadataReviewBatchValidationReport? Validation, VectorLifecycleMetadataReviewBatchApplyPreviewReport? ApplyPreview, string SourcePath)? TryLoadVectorLifecycleMetadataReviewBatchSummary()
    {
        var root = Path.Combine("vector", "eligibility", "review-batches");
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var batches = Directory.EnumerateFiles(root, "batch.json", SearchOption.AllDirectories)
                .Select(path =>
                {
                    try
                    {
                        var batch = JsonSerializer.Deserialize<VectorLifecycleMetadataReviewBatch>(
                            File.ReadAllText(path),
                            JsonOptions);
                        return batch is null ? null : (Batch: batch, Path: path);
                    }
                    catch (JsonException)
                    {
                        return ((VectorLifecycleMetadataReviewBatch Batch, string Path)?)null;
                    }
                })
                .Where(static item => item is not null)
                .OrderByDescending(static item => item!.Value.Batch.CreatedAt)
                .ToArray();
            var latest = batches.FirstOrDefault();
            if (latest is null)
            {
                return null;
            }

            var directory = Path.GetDirectoryName(latest.Value.Path) ?? root;
            var validation = TryReadJson<VectorLifecycleMetadataReviewBatchValidationReport>(
                Path.Combine(directory, "validation-report.json"));
            var applyPreview = TryReadJson<VectorLifecycleMetadataReviewBatchApplyPreviewReport>(
                Path.Combine(directory, "apply-preview.json"));
            return (latest.Value.Batch, validation, applyPreview, latest.Value.Path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static T? TryReadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static double ResolveAlignmentCoverage(
        RetrievalDatasetAlignmentAuditSummaryReport? summary,
        string datasetName,
        bool providerScope)
    {
        var report = summary?.Reports.FirstOrDefault(item =>
            string.Equals(item.DatasetName, datasetName, StringComparison.OrdinalIgnoreCase));
        if (report is null)
        {
            return 0;
        }

        if (report.MustHitCount == 0)
        {
            return 1;
        }

        var covered = providerScope
            ? report.MustHitPresentInProviderScopeCount
            : report.MustHitPresentInCorpusCount;
        return covered / (double)report.MustHitCount;
    }

    private static double ResolveAlignmentAnchorCoverage(RetrievalDatasetAlignmentAuditSummaryReport? summary)
    {
        var reports = summary?.Reports ?? Array.Empty<RetrievalDatasetAlignmentAuditReport>();
        var mustHitCount = reports.Sum(report => report.MustHitCount);
        if (mustHitCount == 0)
        {
            return 0;
        }

        return reports.Sum(report => report.AnchorCoverageRate * report.MustHitCount) / mustHitCount;
    }

    private static (VectorLifecycleMetadataCoverageReport Report, string SourcePath)? TryLoadVectorLifecycleMetadataCoverageReport()
    {
        var path = Path.Combine("eval", "vector-lifecycle-metadata-coverage.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorLifecycleMetadataCoverageReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<VectorReindexPlan> CreateServiceVectorReindexPlanAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request = NormalizeVectorReindexRequest(request, apply: false);
        return GetServiceClient().CreateVectorReindexPlanAsync(request, cancellationToken);
    }

    public Task<VectorReindexSubmitResponse> SubmitServiceVectorReindexAsync(
        VectorReindexRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        request = NormalizeVectorReindexRequest(request, apply: true);
        return GetServiceClient().SubmitVectorReindexAsync(request, cancellationToken);
    }

    public Task<VectorReindexReportQueryResponse> GetServiceVectorReindexReportsAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetVectorReindexReportsAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task<VectorReindexResult> GetServiceVectorReindexReportAsync(
        string reportId,
        CancellationToken cancellationToken = default)
    {
        return GetServiceClient().GetVectorReindexReportAsync(reportId, cancellationToken);
    }

    public Task<VectorQueryPreviewResult> PreviewServiceVectorQueryAsync(
        VectorQueryPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = new VectorQueryPreviewRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"vector-query-controlroom-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? _state.WorkspaceId : request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? _state.CollectionId : request.CollectionId,
            QueryText = request.QueryText,
            TopK = request.TopK > 0 ? request.TopK : 10,
            ProfileId = string.IsNullOrWhiteSpace(request.ProfileId)
                ? VectorQueryProfileIds.NormalV1
                : request.ProfileId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            MinSimilarity = request.MinSimilarity,
            IncludeVector = request.IncludeVector,
            Metadata = request.Metadata
        };

        return GetServiceClient().PreviewVectorQueryAsync(normalized, cancellationToken);
    }

    private VectorReindexRequest NormalizeVectorReindexRequest(
        VectorReindexRequest? request,
        bool apply)
    {
        request ??= new VectorReindexRequest();
        return new VectorReindexRequest
        {
            OperationId = string.IsNullOrWhiteSpace(request.OperationId)
                ? $"vector-reindex-controlroom-{Guid.NewGuid():N}"
                : request.OperationId,
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? _state.WorkspaceId : request.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(request.CollectionId) ? _state.CollectionId : request.CollectionId,
            Layer = request.Layer,
            ItemKind = request.ItemKind,
            Layers = request.Layers,
            DryRun = !apply,
            Apply = apply,
            ConfirmApply = apply && request.ConfirmApply,
            Force = request.Force,
            BatchSize = request.BatchSize > 0 ? request.BatchSize : 50,
            MaxItems = request.MaxItems > 0 ? request.MaxItems : 200,
            IncludeContextItems = request.IncludeContextItems,
            IncludeMemoryItems = request.IncludeMemoryItems,
            Metadata = request.Metadata
        };
    }

    public async Task<ServicePlanningSnapshot> GetServicePlanningSnapshotAsync(
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetServiceClient()
            .GetPlanningSnapshotAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);

        return new ServicePlanningSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Snapshot = snapshot
        };
    }

    public async Task<ServicePlanningProposalSnapshot> ProposeServiceRetrievalPlanAsync(
        string currentInput,
        string? sessionId = null,
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await GetServiceClient()
            .ProposeRetrievalPlanAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                sessionId,
                currentInput,
                mode,
                cancellationToken)
            .ConfigureAwait(false);

        return new ServicePlanningProposalSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            CurrentInput = currentInput,
            Proposal = proposal
        };
    }

    public async Task<ServiceRankerShadowDebugSnapshot> DebugServiceLifecycleAwareRankerAsync(
        string query,
        string? mode = null,
        IReadOnlyList<string>? candidateIds = null,
        bool includeLifecycleDetails = true,
        CancellationToken cancellationToken = default)
    {
        var client = GetServiceClient();
        var response = await client
            .DebugLifecycleAwareRankerAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                query,
                mode,
                candidateIds,
                includeLifecycleDetails,
                cancellationToken)
            .ConfigureAwait(false);
        var recentTraces = await client
            .GetRankerShadowTracesAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                take: 50,
                cancellationToken)
            .ConfigureAwait(false);
        var qualitySummary = new RankerShadowTraceQualityReportBuilder()
            .Build(recentTraces, _state.WorkspaceId, _state.CollectionId);

        return new ServiceRankerShadowDebugSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Response = response,
            TraceQualitySummary = qualitySummary,
            RecentShadowTraces = recentTraces.Take(5).ToArray()
        };
    }

    private static ContextLearningCaseStatusUpdateRequest CreateLearningCaseStatusRequest(string reason)
    {
        return new ContextLearningCaseStatusUpdateRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Reviewer = "controlroom",
            Reason = string.IsNullOrWhiteSpace(reason) ? "controlroom" : reason.Trim(),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = "ControlRoom"
            }
        };
    }

    public string FormatServiceError(ContextCoreApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder();
        builder.AppendLine("Service 调用失败");
        builder.AppendLine($"状态码 : {(int)exception.StatusCode}");
        builder.AppendLine($"错误码 : {exception.ErrorResponse.ErrorCode}");
        builder.AppendLine($"目标   : {exception.ErrorResponse.Target}");
        builder.AppendLine($"消息   : {exception.ErrorResponse.Message}");
        builder.AppendLine($"操作   : {exception.ErrorResponse.OperationId}");
        builder.AppendLine($"Trace  : {exception.ErrorResponse.TraceId}");

        if (exception.ErrorResponse.Details.Count > 0)
        {
            builder.AppendLine("详情");
            foreach (var detail in exception.ErrorResponse.Details)
            {
                builder.AppendLine($"- [{detail.Code}] {detail.Field ?? detail.Target ?? "n/a"}: {detail.Message}");
            }
        }

        if (exception.ErrorResponse.Warnings.Count > 0)
        {
            builder.AppendLine("警告");
            foreach (var warning in exception.ErrorResponse.Warnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static LocalReadiness BuildLocalReadiness(string storageKind, string rootPath)
    {
        // ControlRoom 当前主要是 Direct File Mode，本地 readiness 只做低成本判断。
        // 深度读写探针后续会进入 Service /api/health/ready，避免控制台刷新时产生重 IO。
        if (string.Equals(storageKind, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalReadiness(
                "NotProductionReady",
                "memory 存储仅用于测试、Demo 和临时验证，进程重启后数据会丢失。",
                "TestOnly",
                ProductionReady: false);
        }

        if (string.Equals(storageKind, "filesystem", StringComparison.OrdinalIgnoreCase))
        {
            var rootReady = !string.IsNullOrWhiteSpace(rootPath) && Directory.Exists(rootPath);
            return rootReady
                ? new LocalReadiness(
                    "Ready",
                    "FileSystem 存储目录存在；当前为 Alpha 推荐持久化模式。",
                    "ServiceReadyAlpha",
                    ProductionReady: false)
                : new LocalReadiness(
                    "Degraded",
                    "FileSystem 存储目录不存在或尚未初始化。",
                    "ServiceReadyAlpha",
                    ProductionReady: false);
        }

        if (string.Equals(storageKind, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storageKind, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalReadiness(
                "ExperimentalProvider",
                "PostgreSQL provider 当前仍为 Experimental/Partial，不能作为完整 Service 后端。",
                "ExperimentalPartial",
                ProductionReady: false);
        }

        return new LocalReadiness(
            "Degraded",
            $"未知存储类型：{storageKind}",
            "Unsupported",
            ProductionReady: false);
    }

    private async Task<DashboardSnapshot> GetServiceModeDashboardAsync(CancellationToken cancellationToken)
    {
        var runtimeStatus = await GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
        var runtimeReadiness = await GetRuntimeReadinessAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new DashboardSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            Mode = ControlRoomMode.Service,
            ServiceBaseUrl = _state.ServiceBaseUrl,
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            StorageKind = $"service/{runtimeStatus.Storage.Provider}",
            RootPath = runtimeStatus.Storage.RootPath ?? string.Empty,
            WorkspaceDataFound = true,
            Health = BuildServiceSystemHealth(runtimeReadiness),
            Memory = new MemoryLayerSummary(),
            Jobs = new JobsSummary
            {
                Queued = runtimeStatus.Jobs.Queued,
                Running = runtimeStatus.Jobs.Running
            },
            RecentOperations = [],
            RecentCompressionQuality = [],
            LatestPackage = null,
            Alerts = BuildServiceAlerts(runtimeReadiness)
        };

        return snapshot;
    }

    private static IReadOnlyList<SystemHealthItem> BuildServiceSystemHealth(RuntimeReadinessResponse readiness)
    {
        return
        [
            HealthFromProbe("storage", readiness.Checks, "storage-root"),
            HealthFromProbe("operation logs", readiness.Checks, "event-sink"),
            HealthFromProbe("index", readiness.Checks, "retrieval-baseline"),
            HealthFromProbe("job queue", readiness.Checks, "job-queue"),
            HealthFromProbe("model gateway", readiness.Checks, "model-gateway")
        ];
    }

    private static IReadOnlyList<string> BuildServiceAlerts(RuntimeReadinessResponse readiness)
    {
        var alerts = new List<string>();

        if (!string.Equals(readiness.Status, "ready", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add($"Service readiness={readiness.Status}");
        }

        alerts.AddRange(readiness.Warnings);
        return alerts.Count == 0 ? [] : alerts.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static SystemHealthItem HealthFromProbe(
        string name,
        IReadOnlyList<RuntimeProbeCheckResponse> checks,
        string probeName)
    {
        var check = checks.FirstOrDefault(item =>
            string.Equals(item.Name, probeName, StringComparison.OrdinalIgnoreCase));

        return new SystemHealthItem
        {
            Name = name,
            Status = check?.Status ?? "missing",
            Detail = check?.Message ?? "无对应探针"
        };
    }

    private ContextPackagePolicy CreateDefaultServicePolicy()
    {
        return new ContextPackagePolicy
        {
            Id = "runtime-default",
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Name = "Runtime Default Policy",
            Description = "ControlRoom Service Mode 默认只读展示策略。",
            TokenBudget = 1200,
            IncludeGlobalContext = true,
            IncludeHardConstraints = true,
            IncludeSoftConstraints = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            IncludeRecentRawContext = true,
            MaxRecentItems = 20
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

    public async Task<MemoryStatusBreakdown> GetMemoryStatusBreakdownAsync(
        CancellationToken cancellationToken = default)
    {
        var allMemory = await QueryMemoryAsync(null, null, int.MaxValue, cancellationToken).ConfigureAwait(false);

        return new MemoryStatusBreakdown
        {
            Total = allMemory.Count,
            WorkingLayer = allMemory.Count(item => item.Layer == ContextMemoryLayer.Working),
            StructuredLayer = allMemory.Count(item => item.Layer == ContextMemoryLayer.Structured),
            StableLayer = allMemory.Count(item => item.Layer == ContextMemoryLayer.Stable),
            Candidate = allMemory.Count(item => item.Status == ContextMemoryStatus.Candidate),
            Verified = allMemory.Count(item => item.Status == ContextMemoryStatus.Verified),
            Stable = allMemory.Count(item => item.Status == ContextMemoryStatus.Stable),
            Deprecated = allMemory.Count(item => item.Status == ContextMemoryStatus.Deprecated),
            Rejected = allMemory.Count(item => item.Status == ContextMemoryStatus.Rejected)
        };
    }

    public async Task<IReadOnlyList<ControlRoomListItem>> ListAsync(
        string layer,
        string? type,
        string? tag,
        string? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        layer = layer.ToLowerInvariant();

        switch (layer)
        {
            case "raw":
            {
                var rawItems = await QueryRawAsync(take, cancellationToken).ConfigureAwait(false);
                return rawItems
                    .Where(item => string.IsNullOrWhiteSpace(type) || string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
                    .Where(item => string.IsNullOrWhiteSpace(tag) || item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    .Select(item => new ControlRoomListItem
                    {
                        Id = item.Id,
                        Kind = "raw",
                        Layer = "Raw",
                        Type = item.Type,
                        Status = "",
                        Tags = string.Join(",", item.Tags),
                        UpdatedAt = item.UpdatedAt,
                        Preview = Preview(item.Content)
                    })
                    .ToArray();
            }
            case "constraints" or "constraint":
            {
                ConstraintLevel? level = null;
                if (Enum.TryParse<ConstraintLevel>(status, ignoreCase: true, out var parsedLevel))
                {
                    level = parsedLevel;
                }

                var constraints = await QueryConstraintsAsync(level, take, cancellationToken).ConfigureAwait(false);
                return constraints.Select(item => new ControlRoomListItem
                {
                    Id = item.Id,
                    Kind = "constraint",
                    Layer = "Constraint",
                    Type = item.Level.ToString(),
                    Status = item.Status.ToString(),
                    Tags = item.Scope.ToString(),
                    UpdatedAt = item.UpdatedAt,
                    Preview = Preview(item.Content)
                }).ToArray();
            }
            case "relations" or "relation":
            {
                var relations = await QueryRelationsAsync(take, cancellationToken).ConfigureAwait(false);
                return relations.Select(item => new ControlRoomListItem
                {
                    Id = item.Id,
                    Kind = "relation",
                    Layer = "Relation",
                    Type = item.RelationType,
                    Status = item.Confidence.ToString("0.00"),
                    Tags = $"{item.SourceId} -> {item.TargetId}",
                    UpdatedAt = item.CreatedAt,
                    Preview = string.Join(",", item.SourceRefs)
                }).ToArray();
            }
        }

        var memoryLayer = ParseMemoryLayer(layer);
        var memoryStatus = ParseMemoryStatus(status);
        var memories = await QueryMemoryAsync(memoryLayer, memoryStatus, take, cancellationToken).ConfigureAwait(false);

        return memories
            .Where(item => string.IsNullOrWhiteSpace(type) || string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(tag) || item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Select(item => new ControlRoomListItem
            {
                Id = item.Id,
                Kind = "memory",
                Layer = item.Layer.ToString(),
                Type = item.Type,
                Status = item.Status.ToString(),
                Tags = string.Join(",", item.Tags),
                UpdatedAt = item.UpdatedAt,
                Preview = Preview(item.Content)
            })
            .ToArray();
    }

    public async Task<ControlRoomDetail?> ShowAsync(string id, CancellationToken cancellationToken = default)
    {
        var raw = await _state.ContextStore.GetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken).ConfigureAwait(false);

        if (raw is not null)
        {
            var relations = await GetRelationsForIdAsync(id, cancellationToken).ConfigureAwait(false);
            return DetailFromRaw(raw, relations);
        }

        var memory = await _state.MemoryStore.GetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken).ConfigureAwait(false);

        if (memory is not null)
        {
            var relations = await GetRelationsForIdAsync(id, cancellationToken).ConfigureAwait(false);
            return DetailFromMemory(memory, relations);
        }

        var constraints = await QueryConstraintsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var constraint = constraints.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (constraint is not null)
        {
            return DetailFromConstraint(constraint);
        }

        var relationsAll = await QueryRelationsAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var relation = relationsAll.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (relation is not null)
        {
            return DetailFromRelation(relation);
        }

        var jobs = await QueryJobsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var job = jobs.FirstOrDefault(item => string.Equals(item.JobId, id, StringComparison.OrdinalIgnoreCase));
        return job is null ? null : DetailFromJob(job);
    }

    public async Task<ContextPackage> BuildPackagePreviewAsync(
        int tokenBudget,
        bool usePolicy,
        CancellationToken cancellationToken = default)
    {
        return await BuildPackagePreviewAsync(tokenBudget, usePolicy, policyId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContextPackage> BuildPackagePreviewAsync(
        int tokenBudget,
        bool usePolicy,
        string? policyId,
        CancellationToken cancellationToken = default)
    {
        var result = await _state.PackageBuilder
            .BuildDetailedAsync(
                await CreatePackagePreviewRequestAsync(tokenBudget, usePolicy, policyId, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        _state.LastPackage = result.Package;
        return result.Package;
    }

    public async Task<PackagePreviewDetails> BuildPackagePreviewDetailsAsync(
        int tokenBudget,
        bool usePolicy,
        CancellationToken cancellationToken = default)
    {
        return await BuildPackagePreviewDetailsAsync(tokenBudget, usePolicy, policyId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PackagePreviewDetails> BuildPackagePreviewDetailsAsync(
        int tokenBudget,
        bool usePolicy,
        string? policyId,
        CancellationToken cancellationToken = default)
    {
        var result = await _state.PackageBuilder
            .BuildDetailedAsync(
                await CreatePackagePreviewRequestAsync(tokenBudget, usePolicy, policyId, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        _state.LastPackage = result.Package;
        var recentTrace = _state.RetrievalTraceStore is null
            ? null
            : (await _state.RetrievalTraceStore.QueryRecentAsync(
                    _state.WorkspaceId,
                    _state.CollectionId,
                    1,
                    cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();

        return new PackagePreviewDetails
        {
            Package = result.Package,
            SelectedItems = result.SelectedItems.Select(PackageCandidateItem.FromDecision).ToArray(),
            DroppedItems = result.DroppedItems.Select(PackageCandidateItem.FromDropped).ToArray(),
            Uncertainties = result.Uncertainties,
            Budget = result.Budget,
            AttentionRerankComparison = recentTrace?.AttentionRerankComparison ?? new AttentionRerankComparisonReport(),
            PlanningMetadata = recentTrace?.Metadata ?? new Dictionary<string, string>()
        };
    }

    public Task<IReadOnlyList<ContextPackagePolicy>> ListPoliciesAsync(
        string? queryText = null,
        CancellationToken cancellationToken = default)
    {
        return _state.PackagePolicyStore.QueryAsync(new ContextPackagePolicyQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            QueryText = queryText,
            Take = int.MaxValue
        }, cancellationToken);
    }

    public Task<ContextPackagePolicy?> GetPolicyAsync(
        string policyId,
        CancellationToken cancellationToken = default)
    {
        return _state.PackagePolicyStore.GetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            policyId,
            cancellationToken);
    }

    public async Task SavePolicyAsync(ContextPackagePolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = new ContextPackagePolicy
        {
            Id = policy.Id,
            WorkspaceId = string.IsNullOrWhiteSpace(policy.WorkspaceId) ? _state.WorkspaceId : policy.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(policy.CollectionId) ? _state.CollectionId : policy.CollectionId,
            Name = policy.Name,
            Description = policy.Description,
            TokenBudget = policy.TokenBudget,
            IncludeGlobalContext = policy.IncludeGlobalContext,
            IncludeHardConstraints = policy.IncludeHardConstraints,
            IncludeSoftConstraints = policy.IncludeSoftConstraints,
            IncludeWorkingMemory = policy.IncludeWorkingMemory,
            IncludeStableMemory = policy.IncludeStableMemory,
            IncludeRecentRawContext = policy.IncludeRecentRawContext,
            MaxRecentItems = policy.MaxRecentItems,
            SectionOrder = policy.SectionOrder.ToArray(),
            SectionPriorities = new Dictionary<string, int>(policy.SectionPriorities),
            SectionTokenBudgets = new Dictionary<string, int>(policy.SectionTokenBudgets),
            Metadata = new Dictionary<string, string>(policy.Metadata)
        };

        await _state.PackagePolicyStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContextPackageRequest> CreatePackagePreviewRequestAsync(
        int tokenBudget,
        bool usePolicy,
        string? policyId,
        CancellationToken cancellationToken)
    {
        ContextPackagePolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(policyId))
        {
            policy = await _state.PackagePolicyStore.GetAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                policyId,
                cancellationToken).ConfigureAwait(false);
            if (policy is null)
            {
                throw new InvalidOperationException($"未找到策略：{policyId}");
            }
        }
        else if (usePolicy)
        {
            policy = new ContextPackagePolicy
            {
                Id = "control-room-preview",
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                TokenBudget = tokenBudget,
                IncludeGlobalContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 20
            };
        }

        return new ContextPackageRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            TokenBudget = tokenBudget,
            Policy = policy,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "control-room",
                ["tokenBudget"] = tokenBudget.ToString(),
                ["policyId"] = policy?.Id ?? string.Empty
            }
        };
    }

    public async Task<RetrievalDebugDetails> BuildRetrievalDebugAsync(
        string queryText,
        string? rewrittenQueryText = null,
        IReadOnlyList<float>? queryVector = null,
        int topK = 10,
        int tokenBudget = 1200,
        int candidateTake = 50,
        int vectorTopK = 20,
        bool includeKeywordRecall = true,
        bool includeVectorRecall = true,
        bool includeRelationExpansion = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _state.Retriever.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            QueryText = queryText,
            RewrittenQueryText = rewrittenQueryText,
            QueryVector = queryVector ?? Array.Empty<float>(),
            TopK = topK,
            TokenBudget = tokenBudget,
            CandidateTake = candidateTake,
            VectorTopK = vectorTopK,
            IncludeKeywordRecall = includeKeywordRecall,
            IncludeVectorRecall = includeVectorRecall,
            IncludeRelationExpansion = includeRelationExpansion,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "ControlRoom",
                ["debug"] = "true"
            }
        }, cancellationToken).ConfigureAwait(false);
        var package = BuildRetrievalDebugPackage(result, tokenBudget);
        _state.LastPackage = package;

        return new RetrievalDebugDetails
        {
            Result = result,
            Package = package,
            RecentTraces = await _state.RetrievalTraceStore.QueryRecentAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                10,
                cancellationToken).ConfigureAwait(false)
        };
    }

    private static ContextPackage BuildRetrievalDebugPackage(
        ContextRetrievalResult result,
        int tokenBudget)
    {
        var sections = result.SelectedItems
            .Select((item, index) => new ContextPackageSection
            {
                Name = $"{item.Kind}:{item.SourceId}",
                Priority = 100 - index,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : [item.SourceId],
                ItemRefs = [item.SourceId],
                EstimatedTokens = item.EstimatedTokens
            })
            .ToArray();

        return new ContextPackage
        {
            PackageId = $"retrieval-debug-{result.OperationId}",
            WorkspaceId = result.Trace.WorkspaceId,
            CollectionId = result.Trace.CollectionId,
            Sections = sections,
            EstimatedTokens = sections.Sum(section => section.EstimatedTokens),
            SourceRefs = result.SelectedItems.Select(item => item.SourceId).ToArray(),
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "ControlRoom Retrieval Debug",
                ["retrievalId"] = result.Trace.RetrievalId,
                ["tokenBudget"] = tokenBudget.ToString(),
                ["queryText"] = result.Trace.QueryText ?? "",
                ["rewrittenQueryText"] = result.Trace.RewrittenQueryText ?? ""
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public Task<IReadOnlyList<ContextJob>> QueryJobsAsync(
        ContextJobState? state,
        int take,
        CancellationToken cancellationToken = default)
    {
        return _state.JobQueryStore.QueryAsync(new ContextJobQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            State = state,
            Take = take
        }, cancellationToken);
    }

    public async Task<ContextPromotionRecord> PromoteAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return await _state.PromotionService.PromoteAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            memoryId,
            "control-room",
            "由 ControlRoom 晋升。",
            1.0,
            cancellationToken,
            Environment.UserName).ConfigureAwait(false);
    }

    public async Task<ContextPromotionRecord> RejectAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return await _state.PromotionService.RejectAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            memoryId,
            "control-room",
            "由 ControlRoom 拒绝。",
            1.0,
            cancellationToken,
            Environment.UserName).ConfigureAwait(false);
    }

    public async Task<ContextPromotionRecord> DeprecateAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return await _state.PromotionService.DeprecateAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            memoryId,
            "control-room",
            "由 ControlRoom 标记废弃。",
            1.0,
            cancellationToken,
            Environment.UserName).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PromotionCandidate>> ListPromotionCandidatesAsync(
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        return _state.PromotionCandidateStore.QueryPromotionCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status,
            take,
            cancellationToken);
    }

    public Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return _state.PromotionCandidateStore.GetPromotionCandidateAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            cancellationToken);
    }

    public Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string candidateId,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return _state.PromotionCandidateStore.UpdatePromotionCandidateStatusAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            status,
            reviewer,
            reason,
            cancellationToken);
    }

    /// <summary>
    /// 接受 Promotion 候选项并执行实际记忆写入：
    /// - SourceKind="memory"：对已有记忆条目调用 PromoteAsync 晋升到 Stable 层，并生成审计日志。
    /// - 其他 SourceKind：将候选内容写入工作记忆 (WorkingMemoryItem)，供后续晋升使用。
    /// </summary>
    public async Task<(PromotionCandidate? Candidate, string PromotionDetail)> ExecuteAcceptAsync(
        string candidateId,
        string? reviewer,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _state.PromotionCandidateStore.GetPromotionCandidateAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            cancellationToken).ConfigureAwait(false);

        if (candidate is null)
        {
            return (null, string.Empty);
        }

        // 先更新候选项状态为 Accepted
        var updated = await _state.PromotionCandidateStore.UpdatePromotionCandidateStatusAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            PromotionCandidateStatus.Accepted,
            reviewer,
            reason,
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
        {
            return (null, string.Empty);
        }

        var detail = new StringBuilder();
        var effectiveReason = reason ?? "候选项已接受";
        var effectiveReviewer = reviewer ?? Environment.UserName;

        if (!string.IsNullOrWhiteSpace(candidate.SourceId) &&
            string.Equals(candidate.SourceKind, "memory", StringComparison.OrdinalIgnoreCase))
        {
            // 已有记忆条目：通过 PromotionService 晋升并生成审计日志
            try
            {
                var record = await _state.PromotionService.PromoteAsync(
                    _state.WorkspaceId,
                    _state.CollectionId,
                    candidate.SourceId,
                    "manual-accept",
                    effectiveReason,
                    candidate.Confidence,
                    cancellationToken,
                    effectiveReviewer).ConfigureAwait(false);

                var targetLayerName = record.TargetLayer.ToString();
                detail.AppendLine($"记忆条目已晋升：{record.SourceMemoryId} → {targetLayerName} 层");
                detail.AppendLine($"审计记录：{record.Id}");
            }
            catch (Exception ex)
            {
                detail.AppendLine($"记忆晋升失败（候选状态已更新）：{ex.Message}");
            }
        }
        else
        {
            // 无已有记忆条目（来源为 context / external）：写入工作记忆
            var now = DateTimeOffset.UtcNow;
            var newItemId = $"mem:promoted-{candidateId}";
            var newItem = new WorkingMemoryItem
            {
                Id = newItemId,
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                Type = candidate.Category.Length > 0 ? candidate.Category : "promoted",
                Content = candidate.Content,
                Tags = candidate.MatchedRules.Take(5).ToList(),
                SourceRefs = candidate.SourceRefs,
                Importance = candidate.Confidence,
                Confidence = candidate.Confidence,
                Metadata = new Dictionary<string, string>
                {
                    ["promotionCandidateId"] = candidateId,
                    ["promotedBy"] = effectiveReviewer,
                    ["promotedAt"] = now.ToString("O"),
                    ["promotionReason"] = effectiveReason,
                    ["sourceKind"] = candidate.SourceKind,
                },
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _state.WorkingMemory.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
            detail.AppendLine($"已写入工作记忆：{newItemId}");
            detail.AppendLine($"来源类型：{candidate.SourceKind}");
        }

        return (updated, detail.ToString().TrimEnd());
    }

    public Task<IReadOnlyList<WorkingMemoryItem>> GetRecentWorkingMemoryAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory.GetRecentAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task ClearWorkingMemoryAsync(CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory.ClearAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory.GetActiveContextAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory.SetActiveContextAsync(activeContext, cancellationToken);
    }

    public Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory.GetCurrentTaskAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory.SetCurrentTaskAsync(currentTask, cancellationToken);
    }

    public async Task<RelationGraph> GetRelationGraphAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var upstream = await _state.RelationStore.QueryByTargetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken).ConfigureAwait(false);
        var downstream = await _state.RelationStore.QueryBySourceAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken).ConfigureAwait(false);

        return new RelationGraph
        {
            Id = id,
            Upstream = upstream,
            Downstream = downstream
        };
    }

    public async Task<IReadOnlyList<IndexSearchResult>> SearchIndexAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        var entries = await _state.Index.SearchAsync(new IndexQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Key = keyword,
            Take = 50
        }, cancellationToken).ConfigureAwait(false);

        var results = new List<IndexSearchResult>();
        foreach (var entry in entries)
        {
            var items = new List<ContextItem>();
            foreach (var contextRef in entry.ContextRefs)
            {
                var item = await _state.ContextStore.GetAsync(
                    _state.WorkspaceId,
                    _state.CollectionId,
                    contextRef,
                    cancellationToken).ConfigureAwait(false);

                if (item is not null)
                {
                    items.Add(item);
                }
            }

            results.Add(new IndexSearchResult { Entry = entry, Items = items });
        }

        return results;
    }

    public async Task<ControlRoomModelStatus> GetModelStatusAsync(
        int recentTake = 20,
        CancellationToken cancellationToken = default)
    {
        var modelOptions = ModelGatewayOptionsMaterializer.Materialize(_state.ModelGatewayOptions);
        var health = new List<ModelHealthResult>();
        foreach (var model in modelOptions.Models)
        {
            health.Add(await _state.ModelHealthService.CheckAsync(model.Name, cancellationToken)
                .ConfigureAwait(false));
        }

        var usageLogs = await _state.ModelUsageLogStore.QueryRecentAsync(recentTake, cancellationToken)
            .ConfigureAwait(false);
        var apiKeyResolver = new ApiKeyResolver();
        var configuration = ModelGatewayConfigurationInspector.Inspect(modelOptions, apiKeyResolver);

        return new ControlRoomModelStatus
        {
            Options = modelOptions,
            Configuration = configuration,
            Health = health,
            UsageLogs = usageLogs,
            FallbackCount = usageLogs.Count(log => log.FallbackUsed)
        };
    }

    public async Task<string> BuildMarkdownReportAsync(CancellationToken cancellationToken = default)
    {
        var dashboard = await GetDashboardAsync(cancellationToken).ConfigureAwait(false);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var candidateMemory = await QueryMemoryAsync(null, ContextMemoryStatus.Candidate, 50, cancellationToken).ConfigureAwait(false);
        var stableMemory = await QueryMemoryAsync(ContextMemoryLayer.Stable, ContextMemoryStatus.Stable, 50, cancellationToken).ConfigureAwait(false);
        var constraints = await QueryConstraintsAsync(null, 100, cancellationToken).ConfigureAwait(false);
        var relations = await QueryRelationsAsync(100, cancellationToken).ConfigureAwait(false);
        var validation = await new CollectionValidationService(_state.ContextStore, _state.RelationStore)
            .ValidateAsync(_state.WorkspaceId, _state.CollectionId, cancellationToken)
            .ConfigureAwait(false);
        var failedJobs = await QueryJobsAsync(ContextJobState.Failed, 50, cancellationToken).ConfigureAwait(false);
        var indexEntries = await _state.Index.SearchAsync(new IndexQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = 100
        }, cancellationToken).ConfigureAwait(false);

        return Rendering.MarkdownReportRenderer.Render(
            dashboard,
            status,
            candidateMemory,
            stableMemory,
            constraints,
            relations,
            validation,
            failedJobs,
            indexEntries);
    }

    private IReadOnlyList<SystemHealthItem> BuildSystemHealth(
        ControlRoomStatus status,
        IReadOnlyList<RecentOperation> recentOperations,
        ControlRoomModelStatus modelStatus)
    {
        var rootExists = Directory.Exists(_state.RootPath);
        var logsPath = Path.Combine(_state.RootPath, "logs");
        var modelAvailable = modelStatus.Health.Any(item => item.Availability == ModelAvailability.Available);

        var isPostgres = string.Equals(_state.StorageKind, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_state.StorageKind, "postgresql", StringComparison.OrdinalIgnoreCase);

        var storageStatus = "missing";
        var storageDetail = _state.StorageKind == "memory" ? "in-memory" : Path.GetFullPath(_state.RootPath);

        if (_state.StorageKind == "memory" || rootExists)
        {
            storageStatus = "ok";
        }
        else if (isPostgres)
        {
            storageStatus = "ok";
            storageDetail = "PostgreSQL Database";

            // 尝试通过反射进行 ping 探测
            var prop = _state.ContextStore.GetType().GetProperty("ConnectionFactory", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (prop != null)
            {
                var factory = prop.GetValue(_state.ContextStore);
                if (factory != null)
                {
                    var pingMethod = factory.GetType().GetMethod("PingAsync", new[] { typeof(CancellationToken) });
                    if (pingMethod != null)
                    {
                        try
                        {
                            var pingTask = pingMethod.Invoke(factory, new object?[] { CancellationToken.None }) as Task;
                            if (pingTask != null)
                            {
                                pingTask.GetAwaiter().GetResult();
                                var result = pingTask.GetType().GetProperty("Result")?.GetValue(pingTask);
                                if (result != null)
                                {
                                    var okProp = result.GetType().GetProperty("Item1"); // ValueTuple<bool, string>
                                    var errProp = result.GetType().GetProperty("Item2");
                                    var ok = (bool?)okProp?.GetValue(result) ?? false;
                                    var err = errProp?.GetValue(result) as string;
                                    if (ok)
                                    {
                                        storageStatus = "ok";
                                        var optionsProp = factory.GetType().GetProperty("Options");
                                        var options = optionsProp?.GetValue(factory);
                                        if (options != null)
                                        {
                                            var connStrProp = options.GetType().GetProperty("ConnectionString");
                                            var connStr = connStrProp?.GetValue(options) as string;
                                            if (!string.IsNullOrWhiteSpace(connStr))
                                            {
                                                var host = "localhost";
                                                var db = "default";
                                                foreach (var part in connStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                                                {
                                                    var kv = part.Split('=', 2);
                                                    if (kv.Length == 2)
                                                    {
                                                        var k = kv[0].Trim().ToLowerInvariant();
                                                        var v = kv[1].Trim();
                                                        if (k == "host" || k == "server") host = v;
                                                        else if (k == "database" || k == "db") db = v;
                                                    }
                                                }
                                                storageDetail = $"pg://{host}/{db}";
                                            }
                                        }
                                    }
                                    else
                                    {
                                        storageStatus = "error";
                                        storageDetail = $"pg connection failed: {err}";
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 忽略探测异常
                        }
                    }
                }
            }
        }

        return
        [
            new SystemHealthItem
            {
                Name = "storage",
                Status = storageStatus,
                Detail = storageDetail
            },
            new SystemHealthItem
            {
                Name = "operation logs",
                Status = recentOperations.Count > 0 ? "ok" : "empty",
                Detail = isPostgres ? "cc_context_operation_events Table" : Directory.Exists(logsPath) ? logsPath : "logs directory not found"
            },
            new SystemHealthItem
            {
                Name = "index",
                Status = status.IndexEntryCount > 0 ? "ok" : "empty",
                Detail = $"{status.IndexEntryCount} entries"
            },
            new SystemHealthItem
            {
                Name = "job queue",
                Status = status.FailedJobCount > 0 ? "attention" : "ok",
                Detail = $"{status.QueuedJobCount} queued, {status.RunningJobCount} running, {status.FailedJobCount} failed"
            },
            new SystemHealthItem
            {
                Name = "model gateway",
                Status = modelAvailable ? "ok" : "unavailable",
                Detail = modelAvailable
                    ? "at least one configured model is available"
                    : "no configured model responded successfully"
            }
        ];
    }

    private async Task<IReadOnlyList<RecentOperation>> ReadRecentOperationsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        if (_state.StorageKind == "memory")
        {
            return [];
        }

        var isPostgres = string.Equals(_state.StorageKind, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_state.StorageKind, "postgresql", StringComparison.OrdinalIgnoreCase);

        if (isPostgres)
        {
            var prop = _state.ContextStore.GetType().GetProperty("ConnectionFactory", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (prop != null)
            {
                var factory = prop.GetValue(_state.ContextStore);
                if (factory != null)
                {
                    var optionsProp = factory.GetType().GetProperty("Options");
                    var tablePrefix = "cc_";
                    if (optionsProp != null)
                    {
                        var options = optionsProp.GetValue(factory);
                        if (options != null)
                        {
                            var prefixProp = options.GetType().GetProperty("TablePrefix");
                            if (prefixProp != null)
                            {
                                tablePrefix = prefixProp.GetValue(options) as string ?? "cc_";
                            }
                        }
                    }

                    var openMethod = factory.GetType().GetMethod("OpenConnectionAsync", new[] { typeof(CancellationToken) });
                    if (openMethod != null)
                    {
                        try
                        {
                            var connTask = openMethod.Invoke(factory, new object?[] { cancellationToken }) as Task;
                            if (connTask != null)
                            {
                                await connTask.ConfigureAwait(false);
                                var conn = connTask.GetType().GetProperty("Result")?.GetValue(connTask) as IDisposable;
                                if (conn != null)
                                {
                                    using (conn)
                                    {
                                        var createCmdMethod = conn.GetType().GetMethod("CreateCommand");
                                        var cmd = createCmdMethod?.Invoke(conn, null) as IDisposable;
                                        if (cmd != null)
                                        {
                                            using (cmd)
                                            {
                                                var cmdTextProp = cmd.GetType().GetProperty("CommandText");
                                                if (cmdTextProp != null)
                                                {
                                                    cmdTextProp.SetValue(cmd, $"SELECT data FROM {tablePrefix}context_operation_events WHERE workspace_id = @workspace_id ORDER BY created_at DESC LIMIT {take};");
                                                }

                                                var paramsProp = cmd.GetType().GetProperty("Parameters");
                                                var parameters = paramsProp?.GetValue(cmd);
                                                if (parameters != null)
                                                {
                                                    var addMethod = parameters.GetType().GetMethod("AddWithValue", new[] { typeof(string), typeof(object) });
                                                    addMethod?.Invoke(parameters, new object?[] { "workspace_id", _state.WorkspaceId });
                                                }

                                                var execReaderMethod = cmd.GetType().GetMethod("ExecuteReaderAsync", new[] { typeof(CancellationToken) });
                                                var readerTask = execReaderMethod?.Invoke(cmd, new object?[] { cancellationToken }) as Task;
                                                if (readerTask != null)
                                                {
                                                    await readerTask.ConfigureAwait(false);
                                                    var reader = readerTask.GetType().GetProperty("Result")?.GetValue(readerTask) as IDisposable;
                                                    if (reader != null)
                                                    {
                                                        using (reader)
                                                        {
                                                            var readMethod = reader.GetType().GetMethod("ReadAsync", new[] { typeof(CancellationToken) });
                                                            var getStringMethod = reader.GetType().GetMethod("GetString", new[] { typeof(int) });
                                                            var list = new List<RecentOperation>();

                                                            while (true)
                                                            {
                                                                var readTask = readMethod?.Invoke(reader, new object?[] { cancellationToken }) as Task<bool>;
                                                                if (readTask == null) break;
                                                                var hasRow = await readTask.ConfigureAwait(false);
                                                                if (!hasRow) break;

                                                                var json = getStringMethod?.Invoke(reader, new object[] { 0 }) as string;
                                                                if (json != null)
                                                                {
                                                                    var operation = JsonSerializer.Deserialize<ContextOperationEvent>(json, JsonOptions);
                                                                    if (operation is not null)
                                                                    {
                                                                        list.Add(new RecentOperation
                                                                        {
                                                                            Time = operation.CreatedAt,
                                                                            OperationName = operation.OperationName,
                                                                            Level = operation.Level.ToString(),
                                                                            Duration = operation.Duration,
                                                                            Message = operation.Message
                                                                        });
                                                                    }
                                                                }
                                                            }
                                                            return list;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 降级返回空
                        }
                    }
                }
            }
        }

        var logsPath = Path.Combine(_state.RootPath, "logs");
        if (!Directory.Exists(logsPath))
        {
            return [];
        }

        var logFiles = new List<string>();
        var operationsPath = Path.Combine(logsPath, "operations.jsonl");
        if (File.Exists(operationsPath))
        {
            logFiles.Add(operationsPath);
        }

        logFiles.AddRange(Directory.EnumerateFiles(logsPath, "*.jsonl", SearchOption.AllDirectories));

        var operations = new List<RecentOperation>();
        foreach (var file in logFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                try
                {
                    var operation = JsonSerializer.Deserialize<ContextOperationEvent>(line, JsonOptions);
                    if (operation is null)
                    {
                        continue;
                    }

                    operations.Add(new RecentOperation
                    {
                        Time = operation.CreatedAt,
                        OperationName = operation.OperationName,
                        Level = operation.Level.ToString(),
                        Duration = operation.Duration,
                        Message = operation.Message
                    });
                }
                catch (JsonException)
                {
                }
            }
        }

        return operations
            .OrderByDescending(operation => operation.Time)
            .Take(take > 0 ? take : 10)
            .ToArray();
    }

    public async Task<IReadOnlyList<CompressionQualityReport>> GetRecentCompressionQualityAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        var items = await _state.ContextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Types = ["summary", "compressed", "key_points", "merged", "normalized", "audit"],
            IncludeDerived = true,
            IncludeContent = false,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);

        return items
            .Select(item => CompressionQualityEvaluator.TryReadFromMetadata(item, out var report) ? report : null)
            .Where(report => report is not null)
            .Cast<CompressionQualityReport>()
            .OrderByDescending(report => report.CreatedAt)
            .Take(take > 0 ? take : 5)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildAlerts(
        DashboardSnapshot snapshot,
        ControlRoomStatus status,
        ControlRoomModelStatus modelStatus)
    {
        var alerts = new List<string>();

        if (!Directory.Exists(snapshot.RootPath))
        {
            alerts.Add("存储根目录不存在");
        }

        if (!snapshot.WorkspaceDataFound)
        {
            alerts.Add("当前根目录下没有工作区数据");
        }

        if (snapshot.Memory.RawItems == 0)
        {
            alerts.Add("没有原始条目");
        }

        if (status.FailedJobCount > 0)
        {
            alerts.Add("存在失败任务");
        }

        if (snapshot.Memory.IndexEntries == 0)
        {
            alerts.Add("没有索引项");
        }

        if (snapshot.Memory.Relations == 0)
        {
            alerts.Add("没有关系数据");
        }

        if (snapshot.LatestPackage is null)
        {
            alerts.Add("缺少最近上下文包");
        }

        if (snapshot.RecentCompressionQuality.Any(report => report.RequiresReview))
        {
            alerts.Add("压缩质量需要复核");
        }

        if (!modelStatus.Health.Any(item => item.Availability == ModelAvailability.Available))
        {
            alerts.Add("模型网关不可用");
        }

        return alerts;
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

    private Task<IReadOnlyList<ContextItem>> QueryRawAsync(int take, CancellationToken cancellationToken)
    {
        return _state.ContextStore.QueryAsync(new ContextQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = take,
            IncludeContent = true
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<PackageCandidateItem>> GetPackageCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<PackageCandidateItem>();
        var rawItems = await QueryRawAsync(200, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(rawItems.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = "raw",
            Type = item.Type,
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        var memories = await QueryMemoryAsync(null, null, 200, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(memories.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = item.Layer.ToString(),
            Type = item.Type,
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        var constraints = await QueryConstraintsAsync(null, 200, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(constraints.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = item.Level.ToString(),
            Type = "constraint",
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        var globals = await _state.GlobalContextStore.QueryAsync(new ContextGlobalQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(globals.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = "global",
            Type = item.Type,
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        return candidates;
    }

    private Task<IReadOnlyList<ContextMemoryItem>> QueryMemoryAsync(
        ContextMemoryLayer? layer,
        ContextMemoryStatus? status,
        int take,
        CancellationToken cancellationToken)
    {
        return _state.MemoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = layer,
            Status = status,
            Take = take
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ContextConstraint>> QueryConstraintsAsync(
        ConstraintLevel? level,
        int take,
        CancellationToken cancellationToken)
    {
        return _state.ConstraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Level = level,
            Take = take
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ContextRelation>> QueryRelationsAsync(
        int take,
        CancellationToken cancellationToken)
    {
        return _state.RelationStore.QueryAsync(new ContextRelationQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = take
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ContextRelation>> GetRelationsForIdAsync(
        string id,
        CancellationToken cancellationToken)
    {
        return _state.RelationStore.QueryForItemAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken);
    }

    private static ContextMemoryLayer? ParseMemoryLayer(string layer)
    {
        if (layer is "candidate")
        {
            return null;
        }

        return Enum.TryParse<ContextMemoryLayer>(layer, ignoreCase: true, out var parsed)
            ? parsed
            : ContextMemoryLayer.Working;
    }

    private static ContextMemoryStatus? ParseMemoryStatus(string? status)
    {
        if (Enum.TryParse<ContextMemoryStatus>(status, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static (VectorLifecycleMetadataBackfillPlan Plan, string SourcePath)? TryLoadVectorLifecycleMetadataBackfillPlan()
    {
        var path = Path.Combine("eval", "vector-lifecycle-metadata-backfill-plan.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plan = JsonSerializer.Deserialize<VectorLifecycleMetadataBackfillPlan>(
                File.ReadAllText(path),
                JsonOptions);
            return plan is null ? null : (plan, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorRecallLossAuditReport? A3, string A3SourcePath, VectorRecallLossAuditReport? Extended, string ExtendedSourcePath) TryLoadVectorRecallLossReports()
    {
        var a3Path = Path.Combine("eval", "vector-recall-loss-audit-a3.json");
        var extendedPath = Path.Combine("eval", "vector-recall-loss-audit-extended.json");
        return (
            TryLoadVectorRecallLossReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorRecallLossReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorRecallLossAuditReport? TryLoadVectorRecallLossReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRecallLossAuditReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorSafeRecallRecoveryReport? A3, string A3SourcePath, VectorSafeRecallRecoveryReport? Extended, string ExtendedSourcePath) TryLoadVectorSafeRecallRecoveryReports()
    {
        var a3Path = Path.Combine("eval", "vector-safe-recall-recovery-a3.json");
        var extendedPath = Path.Combine("eval", "vector-safe-recall-recovery-extended.json");
        return (
            TryLoadVectorSafeRecallRecoveryReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorSafeRecallRecoveryReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorSafeRecallRecoveryReport? TryLoadVectorSafeRecallRecoveryReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorSafeRecallRecoveryReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorRankerFusionShadowReport? A3, string A3SourcePath, VectorRankerFusionShadowReport? Extended, string ExtendedSourcePath) TryLoadVectorRankerFusionShadowReports()
    {
        var a3Path = Path.Combine("eval", "vector-ranker-fusion-shadow-a3.json");
        var extendedPath = Path.Combine("eval", "vector-ranker-fusion-shadow-extended.json");
        return (
            TryLoadVectorRankerFusionShadowReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorRankerFusionShadowReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorRankerFusionShadowReport? TryLoadVectorRankerFusionShadowReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRankerFusionShadowReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorRepresentationBenchmarkReport? A3, string A3SourcePath, VectorRepresentationBenchmarkReport? Extended, string ExtendedSourcePath) TryLoadVectorRepresentationBenchmarkReports()
    {
        var a3Path = Path.Combine("eval", "vector-representation-benchmark-a3.json");
        var extendedPath = Path.Combine("eval", "vector-representation-benchmark-extended.json");
        return (
            TryLoadVectorRepresentationBenchmarkReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorRepresentationBenchmarkReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorRepresentationBenchmarkReport? TryLoadVectorRepresentationBenchmarkReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorRepresentationBenchmarkReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (VectorQueryExpansionShadowReport? A3, string A3SourcePath, VectorQueryExpansionShadowReport? Extended, string ExtendedSourcePath) TryLoadVectorQueryExpansionShadowReports()
    {
        var a3Path = Path.Combine("eval", "vector-query-expansion-shadow-a3.json");
        var extendedPath = Path.Combine("eval", "vector-query-expansion-shadow-extended.json");
        return (
            TryLoadVectorQueryExpansionShadowReport(a3Path),
            File.Exists(a3Path) ? a3Path : string.Empty,
            TryLoadVectorQueryExpansionShadowReport(extendedPath),
            File.Exists(extendedPath) ? extendedPath : string.Empty);
    }

    private static VectorQueryExpansionShadowReport? TryLoadVectorQueryExpansionShadowReport(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorQueryExpansionShadowReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SelectQueryExpansionBestProfile(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return SelectBestQueryExpansionResult(a3, extended)?.ExpansionProfile ?? string.Empty;
    }

    private static VectorQueryExpansionShadowResult? SelectBestQueryExpansionResult(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorQueryExpansionShadowResult>()
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.RiskAfterPolicy)
            .ThenBy(item => item.MustNotHitRiskAfterPolicy)
            .ThenBy(item => item.LifecycleRiskAfterPolicy)
            .ThenBy(item => item.NewRiskCount)
            .ThenByDescending(item => item.RecallAfterExpansion)
            .ThenByDescending(item => item.MrrAfterExpansion)
            .FirstOrDefault();
    }

    private static int BuildQueryExpansionRiskSummary(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorQueryExpansionShadowResult>()
            .Sum(item => item.RiskAfterPolicy > 0
                         || item.MustNotHitRiskAfterPolicy > 0
                         || item.LifecycleRiskAfterPolicy > 0
                         || item.NewRiskCount > 0 ? 1 : 0);
    }

    private static int BuildQueryExpansionRecoveredMissSummary(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorQueryExpansionShadowResult>()
            .Sum(item => item.RecoveredMissCount);
    }

    private static bool IsQueryExpansionReadinessSatisfied(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        var a3Best = a3?.BestResult;
        var extendedBest = extended?.BestResult;
        return a3Best is not null
               && extendedBest is not null
               && a3Best.RecallAfterExpansion >= 0.80
               && extendedBest.RecallAfterExpansion >= 0.80
               && a3Best.RiskAfterPolicy == 0
               && extendedBest.RiskAfterPolicy == 0
               && a3Best.MustNotHitRiskAfterPolicy == 0
               && extendedBest.MustNotHitRiskAfterPolicy == 0
               && a3Best.LifecycleRiskAfterPolicy == 0
               && extendedBest.LifecycleRiskAfterPolicy == 0
               && a3Best.NewRiskCount == 0
               && extendedBest.NewRiskCount == 0
               && (a3?.FormalOutputChanged ?? 0) == 0
               && (extended?.FormalOutputChanged ?? 0) == 0;
    }

    private static string SelectRepresentationBestDocumentProfile(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return SelectBestRepresentationResult(a3, extended)?.DocumentRepresentationProfile ?? string.Empty;
    }

    private static string SelectRepresentationBestQueryProfile(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return SelectBestRepresentationResult(a3, extended)?.QueryRepresentationProfile ?? string.Empty;
    }

    private static VectorRepresentationBenchmarkResult? SelectBestRepresentationResult(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRepresentationBenchmarkResult>()
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.RiskAfterPolicy)
            .ThenBy(item => item.MustNotHitRisk)
            .ThenBy(item => item.LifecycleRisk)
            .ThenByDescending(item => item.Recall)
            .ThenByDescending(item => item.Mrr)
            .FirstOrDefault();
    }

    private static int BuildRepresentationRiskSummary(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRepresentationBenchmarkResult>()
            .Sum(item => item.RiskAfterPolicy > 0 || item.MustNotHitRisk > 0 || item.LifecycleRisk > 0 || item.NewRiskCount > 0 ? 1 : 0);
    }

    private static int BuildRepresentationRecoveredMissSummary(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRepresentationBenchmarkResult>()
            .Sum(item => item.RecoveredMissCount);
    }

    private static bool IsRepresentationReadinessSatisfied(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        var a3Best = a3?.BestResult;
        var extendedBest = extended?.BestResult;
        return a3Best is not null
               && extendedBest is not null
               && a3Best.Recall >= 0.80
               && extendedBest.Recall >= 0.80
               && a3Best.RiskAfterPolicy == 0
               && extendedBest.RiskAfterPolicy == 0
               && a3Best.MustNotHitRisk == 0
               && extendedBest.MustNotHitRisk == 0
               && a3Best.LifecycleRisk == 0
               && extendedBest.LifecycleRisk == 0
               && a3Best.NewRiskCount == 0
               && extendedBest.NewRiskCount == 0
               && (a3?.FormalOutputChanged ?? 0) == 0
               && (extended?.FormalOutputChanged ?? 0) == 0;
    }

    private static string SelectFusionBestStrategy(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        var candidates = new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRankerFusionStrategyResult>()
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.MustNotHitRiskFusion)
            .ThenBy(item => item.LifecycleRiskFusion)
            .ThenByDescending(item => item.MustHitRecallFusion)
            .ThenByDescending(item => item.MustHitMrrFusion)
            .ToArray();
        return candidates.FirstOrDefault()?.Strategy ?? string.Empty;
    }

    private static int BuildFusionRiskSummary(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRankerFusionStrategyResult>()
            .Sum(item => item.MustNotHitRiskFusion > 0 || item.LifecycleRiskFusion > 0 || item.NewlyRiskySamples.Count > 0 ? 1 : 0);
    }

    private static double BuildFusionRecallGainSummary(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        var gains = new[] { a3?.BestResult?.RecallGain, extended?.BestResult?.RecallGain }
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToArray();
        return gains.Length == 0 ? 0 : gains.Average();
    }

    private static bool IsFusionReadinessSatisfied(
        VectorRankerFusionShadowReport? a3,
        VectorRankerFusionShadowReport? extended)
    {
        var a3Best = a3?.BestResult;
        var extendedBest = extended?.BestResult;
        return a3Best is not null
               && extendedBest is not null
               && a3Best.MustHitRecallFusion >= 0.80
               && extendedBest.MustHitRecallFusion >= 0.80
               && a3Best.MustNotHitRiskFusion == 0
               && extendedBest.MustNotHitRiskFusion == 0
               && a3Best.LifecycleRiskFusion == 0
               && extendedBest.LifecycleRiskFusion == 0
               && a3Best.NewlyRiskySamples.Count == 0
               && extendedBest.NewlyRiskySamples.Count == 0
               && (a3?.FormalOutputChanged ?? 0) == 0
               && (extended?.FormalOutputChanged ?? 0) == 0;
    }

    private static (VectorRetrievalShadowReadinessGateReport Report, string SourcePath)? TryLoadVectorReadinessGateReport()
    {
        var path = Path.Combine("eval", "vector-retrieval-shadow-readiness-gate.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var report = JsonSerializer.Deserialize<VectorRetrievalShadowReadinessGateReport>(
                File.ReadAllText(path),
                JsonOptions);
            return report is null ? null : (report, path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, int> MergeMissReasons(
        VectorRecallLossAuditReport? a3,
        VectorRecallLossAuditReport? extended)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in new[] { a3, extended }.Where(item => item is not null))
        {
            foreach (var pair in report!.MissReasonCounts)
            {
                merged[pair.Key] = merged.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        return merged
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildIntentReadinessSummary(
        VectorRecallLossAuditReport? a3,
        VectorRecallLossAuditReport? extended)
    {
        return new[] { ("A3", a3), ("Extended", extended) }
            .Where(item => item.Item2 is not null)
            .SelectMany(item => item.Item2!.IntentReadiness.Buckets
                .OrderBy(bucket => bucket.Key, StringComparer.OrdinalIgnoreCase)
                .Select(bucket => $"{item.Item1}:{bucket.Key}={bucket.Recommendation}"))
            .Take(8)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, int> MergeBlockedMustHitClassifications(
        VectorSafeRecallRecoveryReport? a3,
        VectorSafeRecallRecoveryReport? extended)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in new[] { a3, extended }.Where(item => item is not null))
        {
            foreach (var pair in report!.BlockedClassificationCounts)
            {
                merged[pair.Key] = merged.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        return merged
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsVectorV4GateSatisfied(
        VectorRecallLossAuditReport? a3,
        VectorRecallLossAuditReport? extended)
    {
        return a3 is not null
               && extended is not null
               && a3.RiskAfterPolicy == 0
               && extended.RiskAfterPolicy == 0
               && string.Equals(a3.Recommendation, VectorQueryShadowRecommendations.ReadyForRetrievalShadow, StringComparison.OrdinalIgnoreCase)
               && string.Equals(extended.Recommendation, VectorQueryShadowRecommendations.ReadyForRetrievalShadow, StringComparison.OrdinalIgnoreCase);
    }

    private static string Preview(string? content, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var normalized = content.ReplaceLineEndings(" ");
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static ControlRoomDetail DetailFromRaw(
        ContextItem item,
        IReadOnlyList<ContextRelation> relations)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextItem {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "raw",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["type"] = item.Type,
                ["format"] = item.ContentFormat.ToString(),
                ["importance"] = item.Importance.ToString("0.00"),
                ["version"] = item.Version.ToString(),
                ["created"] = item.CreatedAt.ToString("u"),
                ["updated"] = item.UpdatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            Tags = item.Tags,
            SourceRefs = item.SourceRefs,
            Relations = relations,
            Content = item.Content
        };
    }

    private static ControlRoomDetail DetailFromMemory(
        ContextMemoryItem item,
        IReadOnlyList<ContextRelation> relations)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextMemoryItem {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "memory",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["layer"] = item.Layer.ToString(),
                ["status"] = item.Status.ToString(),
                ["type"] = item.Type,
                ["format"] = item.ContentFormat.ToString(),
                ["importance"] = item.Importance.ToString("0.00"),
                ["confidence"] = item.Confidence.ToString("0.00"),
                ["version"] = item.Version.ToString(),
                ["created"] = item.CreatedAt.ToString("u"),
                ["updated"] = item.UpdatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            Tags = item.Tags,
            SourceRefs = item.SourceRefs,
            Relations = relations,
            Content = item.Content
        };
    }

    private static ControlRoomDetail DetailFromConstraint(ContextConstraint item)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextConstraint {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "constraint",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId ?? "",
                ["scope"] = item.Scope.ToString(),
                ["level"] = item.Level.ToString(),
                ["status"] = item.Status.ToString(),
                ["confidence"] = item.Confidence.ToString("0.00"),
                ["created"] = item.CreatedAt.ToString("u"),
                ["updated"] = item.UpdatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            Tags = item.AppliesToRefs,
            SourceRefs = item.SourceRefs,
            Content = item.Content
        };
    }

    private static ControlRoomDetail DetailFromRelation(ContextRelation item)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextRelation {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "relation",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["source"] = item.SourceId,
                ["target"] = item.TargetId,
                ["type"] = item.RelationType,
                ["weight"] = item.Weight.ToString("0.00"),
                ["confidence"] = item.Confidence.ToString("0.00"),
                ["created"] = item.CreatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            SourceRefs = item.SourceRefs,
            Content = $"{item.SourceId} --{item.RelationType}--> {item.TargetId}"
        };
    }

    private static ControlRoomDetail DetailFromJob(ContextJob item)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextJob {item.JobId}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "job",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["jobKind"] = item.Kind.ToString(),
                ["state"] = item.State.ToString(),
                ["priority"] = item.Priority.ToString(),
                ["retry"] = $"{item.RetryCount}/{item.MaxRetryCount}",
                ["created"] = item.CreatedAt.ToString("u"),
                ["started"] = item.StartedAt?.ToString("u") ?? "",
                ["completed"] = item.CompletedAt?.ToString("u") ?? "",
                ["error"] = item.ErrorMessage ?? ""
            },
            Content = item.PayloadJson
        };
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

    public ControlledAppliedMergeRuntimePreviewPlanReport? ControlledAppliedMergeRuntimePreviewPlanReport { get; init; }

    public ControlledAppliedMergeRuntimePreviewDryRunReport? ControlledAppliedMergeRuntimePreviewDryRunReport { get; init; }

    public ControlledAppliedMergeRuntimePreviewActivationPreflightReport? ControlledAppliedMergeRuntimePreviewActivationPreflightReport { get; init; }

    public ControlledAppliedMergeRuntimePreviewObservationWindowReport? ControlledAppliedMergeRuntimePreviewObservationWindowReport { get; init; }

    public ControlledAppliedMergeRuntimePreviewObservationHardeningReport? ControlledAppliedMergeRuntimePreviewObservationHardeningReport { get; init; }

    public ControlledAppliedMergeRuntimePreviewObservationFreezeReport? ControlledAppliedMergeRuntimePreviewObservationFreezeReport { get; init; }

    public ScopedRuntimePreviewApprovalPlanReport? ScopedRuntimePreviewApprovalPlanReport { get; init; }

    public ScopedRuntimePreviewAuthorizationReport? ScopedRuntimePreviewAuthorizationReport { get; init; }

    public ScopedRuntimePreviewAuthorizationHardeningReport? ScopedRuntimePreviewAuthorizationHardeningReport { get; init; }

    public ScopedRuntimePreviewActivationPreparationReport? ScopedRuntimePreviewActivationPreparationReport { get; init; }

    public ScopedRuntimePreviewActivationDryRunReport? ScopedRuntimePreviewActivationDryRunReport { get; init; }

    public ScopedRuntimePreviewActivationWindowPreflightReport? ScopedRuntimePreviewActivationWindowPreflightReport { get; init; }

    public ScopedRuntimePreviewActivationWindowNoOpExecutionReport? ScopedRuntimePreviewActivationWindowNoOpExecutionReport { get; init; }

    public ScopedRuntimePreviewActivationLiveReadinessFreezeReport? ScopedRuntimePreviewActivationLiveReadinessFreezeReport { get; init; }

    public ScopedRuntimePreviewLiveActivationExecutionPlanReport? ScopedRuntimePreviewLiveActivationExecutionPlanReport { get; init; }

    public ScopedRuntimePreviewLiveActivationExecutionReport? ScopedRuntimePreviewLiveActivationExecutionReport { get; init; }

    public ScopedRuntimePreviewLiveActivationObservationReport? ScopedRuntimePreviewLiveActivationObservationReport { get; init; }

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

    public string GuardedFormalRetrievalPreviewSourcePath { get; init; } = string.Empty;
    public bool GuardedFormalRetrievalPreviewGatePassed { get; init; }
    public string GuardedFormalRetrievalPreviewRecommendation { get; init; } = string.Empty;
    public string GuardedFormalRetrievalPreviewProfileName { get; init; } = string.Empty;
    public bool GuardedFormalRetrievalPreviewV4RecheckPassed { get; init; }
    public int GuardedFormalRetrievalPreviewWouldAddCount { get; init; }
    public int GuardedFormalRetrievalPreviewWouldRemoveCount { get; init; }
    public int GuardedFormalRetrievalPreviewWouldRerankCount { get; init; }
    public int GuardedFormalRetrievalPreviewWouldChangeTargetSectionCount { get; init; }
    public int GuardedFormalRetrievalPreviewRiskAfterPolicy { get; init; }
    public int GuardedFormalRetrievalPreviewMustNotHitRiskAfterPolicy { get; init; }
    public int GuardedFormalRetrievalPreviewLifecycleRiskAfterPolicy { get; init; }
    public int GuardedFormalRetrievalPreviewFormalOutputChanged { get; init; }
    public bool GuardedFormalRetrievalPreviewPackingPolicyChanged { get; init; }
    public bool GuardedFormalRetrievalPreviewPackageOutputChanged { get; init; }
    public bool GuardedFormalRetrievalPreviewReadyForRuntimeSwitch { get; init; }
    public bool GuardedFormalRetrievalPreviewFormalRetrievalAllowed { get; init; }
    public IReadOnlyList<string> GuardedFormalRetrievalPreviewBlockedReasons { get; init; } = Array.Empty<string>();

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

    public string ScopedFormalPreviewOptInSourcePath { get; init; } = string.Empty;
    public bool ScopedFormalPreviewOptInGatePassed { get; init; }
    public string ScopedFormalPreviewOptInRecommendation { get; init; } = string.Empty;
    public string ScopedFormalPreviewOptInMode { get; init; } = string.Empty;
    public string ScopedFormalPreviewOptInProfileName { get; init; } = string.Empty;
    public IReadOnlyList<string> ScopedFormalPreviewOptInWorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedFormalPreviewOptInCollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedFormalPreviewOptInEvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public int ScopedFormalPreviewOptInPreviewPackageCount { get; init; }
    public int ScopedFormalPreviewOptInBaselinePackageCount { get; init; }
    public bool ScopedFormalPreviewOptInNonAllowlistedScopeChecked { get; init; }
    public int ScopedFormalPreviewOptInNonAllowlistedScopeLeakCount { get; init; }
    public int ScopedFormalPreviewOptInRiskAfterPolicy { get; init; }
    public int ScopedFormalPreviewOptInFormalOutputChanged { get; init; }
    public bool ScopedFormalPreviewOptInPackageOutputChanged { get; init; }
    public bool ScopedFormalPreviewOptInPackingPolicyChanged { get; init; }
    public bool ScopedFormalPreviewOptInFormalPackageWritten { get; init; }
    public bool ScopedFormalPreviewOptInRuntimeMutated { get; init; }
    public string ScopedFormalPreviewOptInRollbackInstruction { get; init; } = string.Empty;
    public IReadOnlyList<string> ScopedFormalPreviewOptInBlockedReasons { get; init; } = Array.Empty<string>();

    public string LimitedFormalPreviewObservationSourcePath { get; init; } = string.Empty;
    public bool LimitedFormalPreviewObservationGatePassed { get; init; }
    public string LimitedFormalPreviewObservationRecommendation { get; init; } = string.Empty;
    public string LimitedFormalPreviewObservationMode { get; init; } = string.Empty;
    public string LimitedFormalPreviewObservationProfileName { get; init; } = string.Empty;
    public int LimitedFormalPreviewObservationRunCount { get; init; }
    public int LimitedFormalPreviewObservationPreviewPackageCount { get; init; }
    public int LimitedFormalPreviewObservationBaselinePackageCount { get; init; }
    public int LimitedFormalPreviewObservationCandidateAddCount { get; init; }
    public int LimitedFormalPreviewObservationCandidateRemoveCount { get; init; }
    public int LimitedFormalPreviewObservationSectionChangedCount { get; init; }
    public int LimitedFormalPreviewObservationTokenDeltaTotal { get; init; }
    public int LimitedFormalPreviewObservationTokenDeltaMax { get; init; }
    public int LimitedFormalPreviewObservationTokenDeltaP95 { get; init; }
    public int LimitedFormalPreviewObservationRiskAfterPolicy { get; init; }
    public int LimitedFormalPreviewObservationFormalOutputChanged { get; init; }
    public bool LimitedFormalPreviewObservationPackageOutputChanged { get; init; }
    public bool LimitedFormalPreviewObservationPackingPolicyChanged { get; init; }
    public bool LimitedFormalPreviewObservationFormalPackageWritten { get; init; }
    public bool LimitedFormalPreviewObservationRuntimeMutated { get; init; }
    public int LimitedFormalPreviewObservationNonAllowlistedScopeLeakCount { get; init; }
    public IReadOnlyList<string> LimitedFormalPreviewObservationBlockedReasons { get; init; } = Array.Empty<string>();

    public string VectorFormalPreviewFreezeSourcePath { get; init; } = string.Empty;
    public bool VectorFormalPreviewFreezePassed { get; init; }
    public string VectorFormalPreviewFreezeStatus { get; init; } = string.Empty;
    public string VectorFormalPreviewFreezeRecommendation { get; init; } = string.Empty;
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

    public string ExplicitScopedRuntimeExperimentSourcePath { get; init; } = string.Empty;
    public bool ExplicitScopedRuntimeExperimentPlanPassed { get; init; }
    public string ExplicitScopedRuntimeExperimentRecommendation { get; init; } = string.Empty;
    public string ExplicitScopedRuntimeExperimentMode { get; init; } = string.Empty;
    public string ExplicitScopedRuntimeExperimentProfileName { get; init; } = string.Empty;
    public IReadOnlyList<string> ExplicitScopedRuntimeExperimentWorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExplicitScopedRuntimeExperimentCollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExplicitScopedRuntimeExperimentEvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public bool ExplicitScopedRuntimeExperimentDryRunSupported { get; init; }
    public bool ExplicitScopedRuntimeExperimentRuntimeSwitchAllowed { get; init; }
    public bool ExplicitScopedRuntimeExperimentFormalRetrievalAllowed { get; init; }
    public bool ExplicitScopedRuntimeExperimentReadyForRuntimeSwitch { get; init; }
    public bool ExplicitScopedRuntimeExperimentUseForRuntime { get; init; }
    public bool ExplicitScopedRuntimeExperimentFormalPackageWritten { get; init; }
    public bool ExplicitScopedRuntimeExperimentRuntimeMutated { get; init; }
    public bool ExplicitScopedRuntimeExperimentPackingPolicyChanged { get; init; }
    public bool ExplicitScopedRuntimeExperimentPackageOutputChanged { get; init; }
    public int ExplicitScopedRuntimeExperimentScopeLeakCount { get; init; }
    public IReadOnlyList<string> ExplicitScopedRuntimeExperimentAllowedActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExplicitScopedRuntimeExperimentForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> ExplicitScopedRuntimeExperimentRequiredGateSummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string ExplicitScopedRuntimeExperimentRollbackPlan { get; init; } = string.Empty;
    public IReadOnlyList<string> ExplicitScopedRuntimeExperimentBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentDryRunObservationSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentDryRunObservationGatePassed { get; init; }
    public string ScopedRuntimeExperimentDryRunObservationRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentDryRunObservationMode { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentDryRunObservationProfileName { get; init; } = string.Empty;
    public int ScopedRuntimeExperimentDryRunObservationRunCount { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentDryRunObservationWorkspaceAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedRuntimeExperimentDryRunObservationCollectionAllowlist { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedRuntimeExperimentDryRunObservationEvalScopeAllowlist { get; init; } = Array.Empty<string>();
    public int ScopedRuntimeExperimentDryRunObservationDryRunPackageCount { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationBaselinePackageCount { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationCandidateAddCount { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationCandidateRemoveCount { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationTokenDeltaTotal { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationTokenDeltaMax { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationRiskAfterPolicy { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationFormalOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentDryRunObservationFormalPackageWritten { get; init; }
    public bool ScopedRuntimeExperimentDryRunObservationRuntimeMutated { get; init; }
    public bool ScopedRuntimeExperimentDryRunObservationVectorStoreBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentDryRunObservationPackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentDryRunObservationPackageOutputChanged { get; init; }
    public int ScopedRuntimeExperimentDryRunObservationNonAllowlistedScopeLeakCount { get; init; }
    public bool ScopedRuntimeExperimentDryRunObservationRollbackPlanAvailable { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentDryRunObservationBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentDesignFreezeSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentDesignFreezePassed { get; init; }
    public string ScopedRuntimeExperimentDesignFreezeStatus { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentDesignFreezeRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentDesignFreezeAllowedMode { get; init; } = string.Empty;
    public int ScopedRuntimeExperimentDesignFreezeAllowlistedScopeCount { get; init; }
    public int ScopedRuntimeExperimentDesignFreezeObservationRunCount { get; init; }
    public int ScopedRuntimeExperimentDesignFreezeRiskAfterPolicy { get; init; }
    public int ScopedRuntimeExperimentDesignFreezeFormalOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezeRuntimeMutated { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezeVectorStoreBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezePackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezePackageOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezeFormalPackageWritten { get; init; }
    public int ScopedRuntimeExperimentDesignFreezeScopeLeakCount { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezeRollbackPlanAvailable { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezeReadyForRuntimeExperimentProposal { get; init; }
    public bool ScopedRuntimeExperimentDesignFreezeReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentDesignFreezeForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedRuntimeExperimentDesignFreezeBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentProposalSourcePath { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentProposalId { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentProposalPassed { get; init; }
    public string ScopedRuntimeExperimentProposalRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentProposalWorkspaceId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentProposalCollectionId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentProposalEvalScopeId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentProposalProfileName { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentProposalApprovalRequired { get; init; }
    public bool ScopedRuntimeExperimentProposalApproved { get; init; }
    public bool ScopedRuntimeExperimentProposalRuntimeSwitchAllowed { get; init; }
    public bool ScopedRuntimeExperimentProposalFormalRetrievalAllowed { get; init; }
    public bool ScopedRuntimeExperimentProposalReadyForRuntimeSwitch { get; init; }
    public bool ScopedRuntimeExperimentProposalUseForRuntime { get; init; }
    public bool ScopedRuntimeExperimentProposalWriteFormalPackage { get; init; }
    public bool ScopedRuntimeExperimentProposalConfigPatchWritten { get; init; }
    public bool ScopedRuntimeExperimentProposalDiBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentProposalPackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentProposalPackageOutputChanged { get; init; }
    public string ScopedRuntimeExperimentProposalRollbackPlan { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentProposalKillSwitchPlan { get; init; } = string.Empty;
    public IReadOnlyList<string> ScopedRuntimeExperimentProposalBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentApprovalSummarySourcePath { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentApprovalProposalId { get; init; } = string.Empty;
    public int ScopedRuntimeExperimentApprovalCount { get; init; }
    public bool ScopedRuntimeExperimentApprovalRecordExists { get; init; }
    public string ScopedRuntimeExperimentApprovalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentApprovalMode { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentApprovalExpired { get; init; }
    public bool ScopedRuntimeExperimentApprovalRevoked { get; init; }
    public string ScopedRuntimeExperimentApprovalRecommendation { get; init; } = string.Empty;
    public IReadOnlyList<string> ScopedRuntimeExperimentApprovalBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentNoOpHarnessSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentNoOpHarnessPassed { get; init; }
    public string ScopedRuntimeExperimentNoOpHarnessRecommendation { get; init; } = string.Empty;
    public int ScopedRuntimeExperimentNoOpHarnessTraceCount { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessRuntimeMutated { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessVectorStoreBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessFormalPackageWritten { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessPackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessPackageOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessFormalRetrievalAllowed { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessRuntimeSwitchAllowed { get; init; }
    public bool ScopedRuntimeExperimentNoOpHarnessReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentNoOpHarnessBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentHarnessFreezeSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentHarnessFreezePassed { get; init; }
    public string ScopedRuntimeExperimentHarnessFreezeRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentHarnessFreezeProposalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentHarnessFreezeApprovalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentHarnessFreezeApprovalMode { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentHarnessFreezeHarnessStatus { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentHarnessFreezeAllowedMode { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentHarnessFreezeNextAllowedPhase { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentHarnessFreezeRuntimeMutated { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezeVectorStoreBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezeFormalPackageWritten { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezePackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezePackageOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezeFormalRetrievalAllowed { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezeRuntimeSwitchAllowed { get; init; }
    public bool ScopedRuntimeExperimentHarnessFreezeReadyForRuntimeSwitch { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentHarnessFreezeForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScopedRuntimeExperimentHarnessFreezeBlockedReasons { get; init; } = Array.Empty<string>();

    public string GuardedScopedRuntimeExperimentPlanSourcePath { get; init; } = string.Empty;
    public bool GuardedScopedRuntimeExperimentPlanPassed { get; init; }
    public string GuardedScopedRuntimeExperimentPlanRecommendation { get; init; } = string.Empty;
    public string GuardedScopedRuntimeExperimentProposalId { get; init; } = string.Empty;
    public string GuardedScopedRuntimeExperimentRequiredApprovalMode { get; init; } = string.Empty;
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentSelectedScopes { get; init; } = Array.Empty<string>();
    public int GuardedScopedRuntimeExperimentMaxRequestCount { get; init; }
    public int GuardedScopedRuntimeExperimentMaxDurationMinutes { get; init; }
    public string GuardedScopedRuntimeExperimentKillSwitchPlan { get; init; } = string.Empty;
    public string GuardedScopedRuntimeExperimentRollbackPlan { get; init; } = string.Empty;
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentObservationPlan { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentStopConditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentForbiddenActions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentRuntimeApprovalSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentRuntimeApprovalGatePassed { get; init; }
    public string ScopedRuntimeExperimentRuntimeApprovalRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentRuntimeApprovalProposalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentRuntimeApprovalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentRuntimeApprovalMode { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentRuntimeApprovalApprovedBy { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentRuntimeApprovalExists { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalExpired { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalRevoked { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalAcknowledgementsPresent { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalRuntimeSwitchAllowed { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalFormalRetrievalAllowed { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalReadyForRuntimeSwitch { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalUseForRuntime { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalFormalPackageWriteAllowed { get; init; }
    public bool ScopedRuntimeExperimentRuntimeApprovalPackingPolicyIntegrationAllowed { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentRuntimeApprovalBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentActivationPreflightSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentActivationPreflightPassed { get; init; }
    public string ScopedRuntimeExperimentActivationPreflightRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentActivationProposalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentActivationApprovalId { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentActivationMode { get; init; } = string.Empty;
    public IReadOnlyList<string> ScopedRuntimeExperimentActivationSelectedScopes { get; init; } = Array.Empty<string>();
    public bool ScopedRuntimeExperimentActivationKillSwitchAvailable { get; init; }
    public bool ScopedRuntimeExperimentActivationRollbackPlanAvailable { get; init; }
    public bool ScopedRuntimeExperimentActivationTraceSinkAvailable { get; init; }
    public bool ScopedRuntimeExperimentActivationConfigPatchPreviewed { get; init; }
    public bool ScopedRuntimeExperimentActivationConfigPatchWritten { get; init; }
    public bool ScopedRuntimeExperimentActivationDryRunRouteExecuted { get; init; }
    public int ScopedRuntimeExperimentActivationDryRunRouteHitCount { get; init; }
    public bool ScopedRuntimeExperimentActivationNonAllowlistedScopeChecked { get; init; }
    public int ScopedRuntimeExperimentActivationScopeLeakCount { get; init; }
    public bool ScopedRuntimeExperimentActivationRuntimeMutated { get; init; }
    public bool ScopedRuntimeExperimentActivationVectorStoreBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentActivationFormalPackageWritten { get; init; }
    public bool ScopedRuntimeExperimentActivationPackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentActivationPackageOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentActivationFormalRetrievalAllowed { get; init; }
    public bool ScopedRuntimeExperimentActivationRuntimeSwitchAllowed { get; init; }
    public bool ScopedRuntimeExperimentActivationReadyForRuntimeSwitch { get; init; }
    public int ScopedRuntimeExperimentActivationRiskAfterPolicy { get; init; }
    public int ScopedRuntimeExperimentActivationFormalOutputChanged { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentActivationBlockedReasons { get; init; } = Array.Empty<string>();

    public string GuardedScopedRuntimeExperimentRunSourcePath { get; init; } = string.Empty;
    public bool GuardedScopedRuntimeExperimentRunPassed { get; init; }
    public string GuardedScopedRuntimeExperimentRunRecommendation { get; init; } = string.Empty;
    public string GuardedScopedRuntimeExperimentRunProposalId { get; init; } = string.Empty;
    public string GuardedScopedRuntimeExperimentRunApprovalId { get; init; } = string.Empty;
    public string GuardedScopedRuntimeExperimentRunMode { get; init; } = string.Empty;
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentRunSelectedScopes { get; init; } = Array.Empty<string>();
    public int GuardedScopedRuntimeExperimentRunRequestCount { get; init; }
    public int GuardedScopedRuntimeExperimentRunRouteHitCount { get; init; }
    public int GuardedScopedRuntimeExperimentRunNonAllowlistedLeakCount { get; init; }
    public int GuardedScopedRuntimeExperimentRunRiskAfterPolicy { get; init; }
    public int GuardedScopedRuntimeExperimentRunFormalOutputChanged { get; init; }
    public bool GuardedScopedRuntimeExperimentRunPackageOutputChanged { get; init; }
    public bool GuardedScopedRuntimeExperimentRunPackingPolicyChanged { get; init; }
    public bool GuardedScopedRuntimeExperimentRunRuntimeMutated { get; init; }
    public bool GuardedScopedRuntimeExperimentRunVectorStoreBindingChanged { get; init; }
    public bool GuardedScopedRuntimeExperimentRunFormalPackageWritten { get; init; }
    public bool GuardedScopedRuntimeExperimentRunKillSwitchAvailable { get; init; }
    public bool GuardedScopedRuntimeExperimentRunKillSwitchTriggered { get; init; }
    public bool GuardedScopedRuntimeExperimentRunRollbackVerified { get; init; }
    public int GuardedScopedRuntimeExperimentRunErrorCount { get; init; }
    public IReadOnlyList<string> GuardedScopedRuntimeExperimentRunBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentObservationWindowSourcePath { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentObservationWindowId { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentObservationWindowPassed { get; init; }
    public string ScopedRuntimeExperimentObservationWindowRecommendation { get; init; } = string.Empty;
    public int ScopedRuntimeExperimentObservationWindowRunCount { get; init; }
    public int ScopedRuntimeExperimentObservationWindowRequestCount { get; init; }
    public int ScopedRuntimeExperimentObservationWindowRouteHitCount { get; init; }
    public int ScopedRuntimeExperimentObservationWindowScopeLeakCount { get; init; }
    public int ScopedRuntimeExperimentObservationWindowRiskAfterPolicy { get; init; }
    public int ScopedRuntimeExperimentObservationWindowFormalOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowPackageOutputChanged { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowPackingPolicyChanged { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowRuntimeMutated { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowVectorStoreBindingChanged { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowFormalPackageWritten { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowKillSwitchAvailable { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowKillSwitchSmokePassed { get; init; }
    public bool ScopedRuntimeExperimentObservationWindowRollbackVerified { get; init; }
    public double ScopedRuntimeExperimentObservationWindowTraceCompleteness { get; init; }
    public int ScopedRuntimeExperimentObservationWindowErrorCount { get; init; }
    public int ScopedRuntimeExperimentObservationWindowLatencyP95 { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentObservationWindowBlockedReasons { get; init; } = Array.Empty<string>();

    public string ScopedRuntimeExperimentObservationFreezeSourcePath { get; init; } = string.Empty;
    public bool ScopedRuntimeExperimentObservationFreezePassed { get; init; }
    public string ScopedRuntimeExperimentPromotionDecision { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentObservationFreezeRecommendation { get; init; } = string.Empty;
    public string ScopedRuntimeExperimentObservationFreezeWindowId { get; init; } = string.Empty;
    public int ScopedRuntimeExperimentObservationFreezeRequestCount { get; init; }
    public int ScopedRuntimeExperimentObservationFreezeRouteHitCount { get; init; }
    public int ScopedRuntimeExperimentObservationFreezeRiskAfterPolicy { get; init; }
    public int ScopedRuntimeExperimentObservationFreezeFormalOutputChanged { get; init; }
    public double ScopedRuntimeExperimentObservationFreezeTraceCompleteness { get; init; }
    public bool ScopedRuntimeExperimentObservationFreezeFormalRetrievalAllowed { get; init; }
    public bool ScopedRuntimeExperimentObservationFreezeRuntimeSwitchAllowed { get; init; }
    public IReadOnlyList<string> ScopedRuntimeExperimentObservationFreezeBlockedReasons { get; init; } = Array.Empty<string>();

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
    public string SourceDiverseShadowAdapterValidationSourcePath { get; init; } = string.Empty;
    public bool SourceDiverseShadowAdapterValidationPassed { get; init; }
    public bool SourceDiverseShadowAdapterValidationGatePassed { get; init; }
    public string SourceDiverseShadowAdapterValidationRecommendation { get; init; } = string.Empty;
    public bool SourceDiverseShadowAdapterValidationSetSourceDiverse { get; init; }
    public bool SourceDiverseShadowAdapterValidationScopeMetadataPresent { get; init; }
    public int SourceDiverseShadowAdapterValidationSampleCount { get; init; }
    public double SourceDiverseShadowAdapterValidationOverlapRate { get; init; }
    public int SourceDiverseShadowAdapterValidationShadowOnlyCount { get; init; }
    public int SourceDiverseShadowAdapterValidationHypotheticalAddCount { get; init; }
    public int SourceDiverseShadowAdapterValidationHypotheticalRemoveCount { get; init; }
    public int SourceDiverseShadowAdapterValidationAppliedAddCount { get; init; }
    public int SourceDiverseShadowAdapterValidationAppliedRemoveCount { get; init; }
    public int SourceDiverseShadowAdapterValidationUniqueSourceRecoveryCount { get; init; }
    public int SourceDiverseShadowAdapterValidationRiskAfterPolicy { get; init; }
    public int SourceDiverseShadowAdapterValidationTokenDeltaTotal { get; init; }
    public int SourceDiverseShadowAdapterValidationTokenDeltaMax { get; init; }
    public int SourceDiverseShadowAdapterValidationSectionDeltaCount { get; init; }
    public bool SourceDiverseShadowAdapterValidationPackageOutputChanged { get; init; }
    public bool SourceDiverseShadowAdapterValidationPackingPolicyChanged { get; init; }
    public bool SourceDiverseShadowAdapterValidationRuntimeMutated { get; init; }
    public bool SourceDiverseShadowAdapterValidationVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> SourceDiverseShadowAdapterValidationBlockedReasons { get; init; } = Array.Empty<string>();
    public string ShadowCandidateMergePreviewSourcePath { get; init; } = string.Empty;
    public bool ShadowCandidateMergePreviewPassed { get; init; }
    public bool ShadowCandidateMergePreviewGatePassed { get; init; }
    public string ShadowCandidateMergePreviewRecommendation { get; init; } = string.Empty;
    public bool ShadowCandidateMergePreviewMergedSetGenerated { get; init; }
    public int ShadowCandidateMergePreviewSampleCount { get; init; }
    public int ShadowCandidateMergePreviewBaselineCandidateCount { get; init; }
    public int ShadowCandidateMergePreviewShadowAdapterCandidateCount { get; init; }
    public int ShadowCandidateMergePreviewMergedPreviewCandidateCount { get; init; }
    public int ShadowCandidateMergePreviewPreviewAddCount { get; init; }
    public int ShadowCandidateMergePreviewPreviewRemoveCount { get; init; }
    public int ShadowCandidateMergePreviewAppliedAddCount { get; init; }
    public int ShadowCandidateMergePreviewAppliedRemoveCount { get; init; }
    public int ShadowCandidateMergePreviewTokenDeltaTotal { get; init; }
    public int ShadowCandidateMergePreviewTokenDeltaMax { get; init; }
    public int ShadowCandidateMergePreviewPriorityOrderDeltaCount { get; init; }
    public int ShadowCandidateMergePreviewPriorityInversionCount { get; init; }
    public int ShadowCandidateMergePreviewDroppedRequiredCandidateCount { get; init; }
    public int ShadowCandidateMergePreviewSectionMismatchCount { get; init; }
    public int ShadowCandidateMergePreviewRiskAfterPolicy { get; init; }
    public bool ShadowCandidateMergePreviewFormalSelectedSetChanged { get; init; }
    public bool ShadowCandidateMergePreviewPackageOutputChanged { get; init; }
    public bool ShadowCandidateMergePreviewPackingPolicyChanged { get; init; }
    public bool ShadowCandidateMergePreviewRuntimeMutated { get; init; }
    public bool ShadowCandidateMergePreviewVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ShadowCandidateMergePreviewBlockedReasons { get; init; } = Array.Empty<string>();
    public string ShadowCandidateMergePreviewObservationSourcePath { get; init; } = string.Empty;
    public bool ShadowCandidateMergePreviewObservationPassed { get; init; }
    public bool ShadowCandidateMergePreviewObservationGatePassed { get; init; }
    public string ShadowCandidateMergePreviewObservationRecommendation { get; init; } = string.Empty;
    public int ShadowCandidateMergePreviewObservationRunCount { get; init; }
    public int ShadowCandidateMergePreviewObservationSampleCount { get; init; }
    public bool ShadowCandidateMergePreviewObservationDeterministicStable { get; init; }
    public bool ShadowCandidateMergePreviewObservationPreviewAddRemoveStable { get; init; }
    public int ShadowCandidateMergePreviewObservationPreviewAddCountMin { get; init; }
    public int ShadowCandidateMergePreviewObservationPreviewAddCountMax { get; init; }
    public int ShadowCandidateMergePreviewObservationPreviewRemoveCountMin { get; init; }
    public int ShadowCandidateMergePreviewObservationPreviewRemoveCountMax { get; init; }
    public int ShadowCandidateMergePreviewObservationAppliedAddCountMax { get; init; }
    public int ShadowCandidateMergePreviewObservationAppliedRemoveCountMax { get; init; }
    public int ShadowCandidateMergePreviewObservationRiskAfterPolicyMax { get; init; }
    public int ShadowCandidateMergePreviewObservationTokenDeltaTotalMax { get; init; }
    public int ShadowCandidateMergePreviewObservationTokenDeltaMaxMax { get; init; }
    public int ShadowCandidateMergePreviewObservationPriorityInversionCountTotal { get; init; }
    public int ShadowCandidateMergePreviewObservationSectionMismatchCountTotal { get; init; }
    public int ShadowCandidateMergePreviewObservationFormalOutputChangedMax { get; init; }
    public bool ShadowCandidateMergePreviewObservationPackageOutputChanged { get; init; }
    public bool ShadowCandidateMergePreviewObservationPackingPolicyChanged { get; init; }
    public bool ShadowCandidateMergePreviewObservationRuntimeMutated { get; init; }
    public bool ShadowCandidateMergePreviewObservationVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ShadowCandidateMergePreviewObservationBlockedReasons { get; init; } = Array.Empty<string>();
    public string ShadowMergeStabilityFreezeSourcePath { get; init; } = string.Empty;
    public bool ShadowMergeStabilityFreezePassed { get; init; }
    public string ShadowMergeStabilityFreezeRecommendation { get; init; } = string.Empty;
    public string ShadowMergePromotionDecisionSourcePath { get; init; } = string.Empty;
    public bool ShadowMergePromotionDecisionPassed { get; init; }
    public string ShadowMergePromotionDecision { get; init; } = string.Empty;
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
    public string ControlledShadowMergeProposalSourcePath { get; init; } = string.Empty;
    public bool ControlledShadowMergeProposalPassed { get; init; }
    public bool ControlledShadowMergeProposalGatePassed { get; init; }
    public string ControlledShadowMergeProposalRecommendation { get; init; } = string.Empty;
    public string ControlledShadowMergeProposalId { get; init; } = string.Empty;
    public int ControlledShadowMergeProposalScopeCount { get; init; }
    public IReadOnlyList<string> ControlledShadowMergeProposalSelectedScopes { get; init; } = Array.Empty<string>();
    public int ControlledShadowMergeProposalMaxRequestCount { get; init; }
    public int ControlledShadowMergeProposalMaxDurationMinutes { get; init; }
    public int ControlledShadowMergeProposalMaxPreviewAddCount { get; init; }
    public int ControlledShadowMergeProposalMaxPreviewRemoveCount { get; init; }
    public bool ControlledShadowMergeProposalRollbackPlanPresent { get; init; }
    public bool ControlledShadowMergeProposalKillSwitchPlanPresent { get; init; }
    public int ControlledShadowMergeProposalObservationConditionCount { get; init; }
    public int ControlledShadowMergeProposalStopConditionCount { get; init; }
    public bool ControlledShadowMergeProposalFormalRetrievalAllowed { get; init; }
    public bool ControlledShadowMergeProposalRuntimeSwitchAllowed { get; init; }
    public bool ControlledShadowMergeProposalRuntimeMutated { get; init; }
    public IReadOnlyList<string> ControlledShadowMergeProposalBlockedReasons { get; init; } = Array.Empty<string>();
    public string ControlledShadowMergeDryRunSourcePath { get; init; } = string.Empty;
    public bool ControlledShadowMergeDryRunPassed { get; init; }
    public bool ControlledShadowMergeDryRunGatePassed { get; init; }
    public string ControlledShadowMergeDryRunRecommendation { get; init; } = string.Empty;
    public bool ControlledShadowMergeDryRunProposalConstraintsApplied { get; init; }
    public bool ControlledShadowMergeDryRunAddRemoveLimitEnforced { get; init; }
    public bool ControlledShadowMergeDryRunTokenSectionPriorityGatePassed { get; init; }
    public bool ControlledShadowMergeDryRunRollbackVerified { get; init; }
    public bool ControlledShadowMergeDryRunKillSwitchVerified { get; init; }
    public int ControlledShadowMergeDryRunPreviewAddCount { get; init; }
    public int ControlledShadowMergeDryRunPreviewRemoveCount { get; init; }
    public int ControlledShadowMergeDryRunAppliedAddCount { get; init; }
    public int ControlledShadowMergeDryRunAppliedRemoveCount { get; init; }
    public int ControlledShadowMergeDryRunTokenDeltaTotal { get; init; }
    public int ControlledShadowMergeDryRunTokenDeltaMax { get; init; }
    public int ControlledShadowMergeDryRunPriorityInversionCount { get; init; }
    public int ControlledShadowMergeDryRunSectionMismatchCount { get; init; }
    public int ControlledShadowMergeDryRunFormalOutputChanged { get; init; }
    public bool ControlledShadowMergeDryRunPackageOutputChanged { get; init; }
    public bool ControlledShadowMergeDryRunPackingPolicyChanged { get; init; }
    public bool ControlledShadowMergeDryRunRuntimeMutated { get; init; }
    public bool ControlledShadowMergeDryRunVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ControlledShadowMergeDryRunBlockedReasons { get; init; } = Array.Empty<string>();
    public string ControlledShadowMergeObservationWindowSourcePath { get; init; } = string.Empty;
    public bool ControlledShadowMergeObservationWindowPassed { get; init; }
    public bool ControlledShadowMergeObservationWindowGatePassed { get; init; }
    public string ControlledShadowMergeObservationWindowRecommendation { get; init; } = string.Empty;
    public bool ControlledShadowMergeObservationWindowProposalConstraintsApplied { get; init; }
    public int ControlledShadowMergeObservationWindowRunCount { get; init; }
    public int ControlledShadowMergeObservationWindowRequestCountTotal { get; init; }
    public int ControlledShadowMergeObservationWindowMaxRequestCount { get; init; }
    public int ControlledShadowMergeObservationWindowPreviewAddCountMin { get; init; }
    public int ControlledShadowMergeObservationWindowPreviewAddCountMax { get; init; }
    public int ControlledShadowMergeObservationWindowPreviewRemoveCountMin { get; init; }
    public int ControlledShadowMergeObservationWindowPreviewRemoveCountMax { get; init; }
    public int ControlledShadowMergeObservationWindowAppliedAddCountMax { get; init; }
    public int ControlledShadowMergeObservationWindowAppliedRemoveCountMax { get; init; }
    public int ControlledShadowMergeObservationWindowRiskAfterPolicyMax { get; init; }
    public int ControlledShadowMergeObservationWindowTokenDeltaTotalMax { get; init; }
    public int ControlledShadowMergeObservationWindowTokenDeltaMaxMax { get; init; }
    public int ControlledShadowMergeObservationWindowPriorityInversionCountTotal { get; init; }
    public int ControlledShadowMergeObservationWindowSectionMismatchCountTotal { get; init; }
    public int ControlledShadowMergeObservationWindowFormalOutputChangedMax { get; init; }
    public bool ControlledShadowMergeObservationWindowPackageOutputChanged { get; init; }
    public bool ControlledShadowMergeObservationWindowPackingPolicyChanged { get; init; }
    public bool ControlledShadowMergeObservationWindowRuntimeMutated { get; init; }
    public bool ControlledShadowMergeObservationWindowVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ControlledShadowMergeObservationWindowBlockedReasons { get; init; } = Array.Empty<string>();

    public string ControlledShadowMergeFreezeSourcePath { get; init; } = string.Empty;
    public bool ControlledShadowMergeFreezePassed { get; init; }
    public bool ControlledShadowMergePromotionDecisionPassed { get; init; }
    public string ControlledShadowMergeFreezeRecommendation { get; init; } = string.Empty;
    public string ControlledShadowMergePromotionDecision { get; init; } = string.Empty;
    public string ControlledShadowMergeNextAllowedPhase { get; init; } = string.Empty;
    public string ControlledShadowMergeFreezeProposalId { get; init; } = string.Empty;
    public int ControlledShadowMergeFreezeObservationRunCount { get; init; }
    public int ControlledShadowMergeFreezeRequestCountTotal { get; init; }
    public int ControlledShadowMergeFreezePreviewAddCountMin { get; init; }
    public int ControlledShadowMergeFreezePreviewAddCountMax { get; init; }
    public int ControlledShadowMergeFreezePreviewRemoveCountMin { get; init; }
    public int ControlledShadowMergeFreezePreviewRemoveCountMax { get; init; }
    public int ControlledShadowMergeFreezeAppliedAddCountMax { get; init; }
    public int ControlledShadowMergeFreezeAppliedRemoveCountMax { get; init; }
    public int ControlledShadowMergeFreezeRiskAfterPolicyMax { get; init; }
    public int ControlledShadowMergeFreezeFormalOutputChangedMax { get; init; }
    public bool ControlledShadowMergeFreezeFormalPackageWritten { get; init; }
    public bool ControlledShadowMergeFreezePackageOutputChanged { get; init; }
    public bool ControlledShadowMergeFreezePackingPolicyChanged { get; init; }
    public bool ControlledShadowMergeFreezeRuntimeMutated { get; init; }
    public bool ControlledShadowMergeFreezeVectorStoreBindingChanged { get; init; }
    public IReadOnlyList<string> ControlledShadowMergeFreezeBlockedReasons { get; init; } = Array.Empty<string>();
    public string ControlledAppliedMergeProposalSourcePath { get; init; } = string.Empty;
    public bool ControlledAppliedMergeProposalPassed { get; init; }
    public bool ControlledAppliedMergeProposalGatePassed { get; init; }
    public string ControlledAppliedMergeProposalRecommendation { get; init; } = string.Empty;
    public string ControlledAppliedMergeProposalId { get; init; } = string.Empty;
    public string ControlledAppliedMergeProposalApprovalMode { get; init; } = string.Empty;
    public string ControlledAppliedMergeProposalNextAllowedPhase { get; init; } = string.Empty;
    public int ControlledAppliedMergeProposalScopeCount { get; init; }
    public IReadOnlyList<string> ControlledAppliedMergeProposalSelectedScopes { get; init; } = Array.Empty<string>();
    public int ControlledAppliedMergeProposalMaxAppliedAddCount { get; init; }
    public int ControlledAppliedMergeProposalMaxAppliedRemoveCount { get; init; }
    public int ControlledAppliedMergeProposalStablePreviewAddCount { get; init; }
    public int ControlledAppliedMergeProposalStablePreviewRemoveCount { get; init; }
    public int ControlledAppliedMergeProposalAppliedAddCount { get; init; }
    public int ControlledAppliedMergeProposalAppliedRemoveCount { get; init; }
    public bool ControlledAppliedMergeProposalApprovalPlanPresent { get; init; }
    public bool ControlledAppliedMergeProposalRollbackPlanPresent { get; init; }
    public bool ControlledAppliedMergeProposalKillSwitchPlanPresent { get; init; }
    public int ControlledAppliedMergeProposalRiskAfterPolicy { get; init; }
    public int ControlledAppliedMergeProposalFormalOutputChanged { get; init; }
    public bool ControlledAppliedMergeProposalFormalPackageWritten { get; init; }
    public bool ControlledAppliedMergeProposalPackageOutputChanged { get; init; }
    public bool ControlledAppliedMergeProposalPackingPolicyChanged { get; init; }
    public bool ControlledAppliedMergeProposalRuntimeMutated { get; init; }
    public bool ControlledAppliedMergeProposalVectorStoreBindingChanged { get; init; }
    public bool ControlledAppliedMergeProposalAppliedMergeAllowed { get; init; }
    public IReadOnlyList<string> ControlledAppliedMergeProposalBlockedReasons { get; init; } = Array.Empty<string>();
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

    public PostgresLearningFeedbackDiagnosticsReport PostgresLearningFeedbackDiagnosticsReport { get; init; } = new();

    public PostgresLearningFeedbackParityReport PostgresLearningFeedbackParityReport { get; init; } = new();

    public LearningFeedbackPostgresReadinessGateReport PostgresLearningFeedbackReadinessGateReport { get; init; } = new();

    public LearningFeedbackDualWriteSmokeReport PostgresLearningFeedbackDualWriteSmokeReport { get; init; } = new();

    public LearningFeedbackShadowReadSmokeReport PostgresLearningFeedbackShadowReadSmokeReport { get; init; } = new();

    public LearningFeedbackProviderQualityReport PostgresLearningFeedbackProviderQualityReport { get; init; } = new();

    public LearningFeedbackScopedServiceModeSmokeReport PostgresLearningFeedbackScopedServiceModeSmokeReport { get; init; } = new();

    public LearningFeedbackScopedServiceModeGateReport PostgresLearningFeedbackScopedServiceModeGateReport { get; init; } = new();

    public LearningFeedbackSelectedNormalScopeCanaryReport PostgresLearningFeedbackSelectedNormalScopeCanaryReport { get; init; } = new();

    public LearningFeedbackLimitedScopeObservationReport PostgresLearningFeedbackLimitedScopeObservationReport { get; init; } = new();

    public LearningFeedbackLimitedScopeQualityReport PostgresLearningFeedbackLimitedScopeQualityReport { get; init; } = new();

    public LearningFeedbackPostgresFreezeGateReport PostgresLearningFeedbackFreezeGateReport { get; init; } = new();

    public PostgresJobQueueDiagnosticsReport PostgresJobQueueDiagnosticsReport { get; init; } = new();

    public PostgresJobQueueParityReport PostgresJobQueueParityReport { get; init; } = new();

    public PostgresJobQueueLeaseSmokeReport PostgresJobQueueLeaseSmokeReport { get; init; } = new();

    public PostgresJobQueueDualWriteSmokeReport PostgresJobQueueDualWriteSmokeReport { get; init; } = new();

    public PostgresJobQueueShadowReadSmokeReport PostgresJobQueueShadowReadSmokeReport { get; init; } = new();

    public PostgresJobQueueProviderQualityReport PostgresJobQueueProviderQualityReport { get; init; } = new();

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





