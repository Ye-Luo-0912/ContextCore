using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Embedding;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

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
        RetrievalPlanningOptions? retrievalPlanningOptions = null)
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
                    workingMemoryService: memoryStore),
                TokenizerResolver = tokenizerResolver,
                PackagePolicyStore = packagePolicyStore,
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
                workingMemoryService: fileMemoryStore),
            TokenizerResolver = fileTokenizerResolver,
            PackagePolicyStore = filePackagePolicyStore,
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

        return new ServiceAdminRuntimeSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Runtime = runtime,
            AdminStatus = adminStatus,
            BackupStatus = backupStatus,
            BackupValidate = backupValidate
        };
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
            Global = globals
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

        return new ServiceRelationsSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            ItemId = itemId ?? string.Empty,
            Relations = relations,
            RelationTypes = types,
            Diagnostics = diagnostics,
            ItemDiagnostics = itemDiagnostics
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

        return new ServiceLearningFeaturesSnapshot
        {
            CurrentTime = DateTimeOffset.Now,
            BaseUrl = _state.ServiceBaseUrl ?? string.Empty,
            Dataset = dataset,
            QualityReport = qualityReport,
            Limit = limit,
            Offset = offset
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

    public int Limit { get; init; } = 50;

    public int Offset { get; init; }
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






