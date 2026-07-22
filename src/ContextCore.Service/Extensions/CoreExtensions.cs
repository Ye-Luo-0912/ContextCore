using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Jobs;
using ContextCore.Core.Services;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Promotion;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Core.Services.Retrieval;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Runtime;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Extensions;

/// <summary>Core 服务层与模型网关 of DI 注册扩展。</summary>
internal static class CoreExtensions
{
	/// <summary>注册 Core 业务服务（摄取、打包、校验、晋升、工作记忆）。</summary>
	public static IServiceCollection AddContextCore(this IServiceCollection services)
	{
		// R11-P6：ContextStateCache 基础设施。InMemoryContextStateCache 同时实现
		// IContextStateCache（读路径可选缓存）和 IStateCacheInvalidator（写入边界失效信号接收）。
		// Store Decorator 在写入成功后调用 InvalidateAsync，移除受影响的缓存项。
		// 读路径不自动使用缓存——仅在调用方显式注入 ContextStateCacheAccessor 时生效。
		// R13-F：Cache 容量与 TTL 由 PackageTemplateCacheOptions 配置（默认值与 R11-P6 一致）。
		// canary 关闭时仍注册此 singleton——store decorators 需要 IStateCacheInvalidator 实例（即使为空操作）。
		services.AddSingleton(sp =>
		{
			var opts = sp.GetService<IOptions<PackageTemplateCacheOptions>>()?.Value;
			var versionStore = sp.GetService<IContextStateVersionStore>();
			// canary 关闭时使用默认容量/TTL；canary 启用时使用配置值（MaxEntries<=0 回退默认）
			var maxEntries = opts is { Enabled: true, MaxEntries: > 0 } ? opts.MaxEntries : InMemoryContextStateCache.DefaultMaxEntries;
			var ttl = opts is { Enabled: true } ? opts?.Ttl : null;
			return new InMemoryContextStateCache(maxEntries, versionStore, ttl);
		});
		services.AddSingleton<IContextStateCache>(sp => sp.GetRequiredService<InMemoryContextStateCache>());
		services.AddSingleton<IStateCacheInvalidator>(sp => sp.GetRequiredService<InMemoryContextStateCache>());
		// R13-F：ContextStateCacheAccessor 注册为 canary-aware。
		// canary 关闭（Enabled=false）或 AllowedWorkspaces 为空时 gate 返回 false——所有请求绕过缓存。
		// canary 启用时 gate 仅对 AllowedWorkspaces 列出的工作空间返回 true——其余工作空间仍走全量流水线。
		// 此 singleton 仅由 RuntimeBuildOptions.CacheAccessor 路径使用；测试代码直接 new ContextStateCacheAccessor。
		services.AddSingleton(sp =>
		{
			var cache = sp.GetRequiredService<InMemoryContextStateCache>();
			var versionStore = sp.GetService<IContextStateVersionStore>();
			var opts = sp.GetService<IOptions<PackageTemplateCacheOptions>>()?.Value ?? new PackageTemplateCacheOptions();
			var allowed = opts.Enabled && opts.AllowedWorkspaces.Count > 0
				? new HashSet<string>(opts.AllowedWorkspaces, StringComparer.Ordinal)
				: null;
			Func<DependencyScopeSet, bool>? canaryGate = allowed is null
				? null  // canary 关闭或未配置允许列表——gate 为 null 时 GetOrAddAsync 走原缓存路径（但因 Enabled=false 此 accessor 不会被注入）
				: scopes => CacheCanaryGateWorkspaceAllowed(scopes, allowed);
			return new ContextStateCacheAccessor(cache, versionStore, opts.FactoryTimeout, canaryGate);
		});
		// R10-2 P3：状态版本存储（进程内单调递增）。Decorator 在写入成功后 bump 版本，
		// ContextStateCache 据版本号判断是否命中。多实例场景需替换为持久化实现。
		services.AddSingleton<IContextStateVersionStore, InMemoryContextStateVersionStore>();
		services.AddSingleton<BasicContextIngestionService>();
		services.AddSingleton<ContextInputNormalizer>();
		services.AddSingleton<ContextInputValidator>();
		services.AddSingleton<ContextInputHasher>();
		services.AddSingleton<ContextInputSequencer>();
		// P1-3：使用工厂委托让 DI 解析 IWriteTransactionScopeFactory（Postgres provider 注册时非空，
		// InMemory/FileSystem 不注册时为 null）。null 时 BasicContextIngestionService 自动回退到非事务路径。
		services.AddSingleton<ContextInputIngestionService>(sp => new ContextInputIngestionService(
			sp.GetRequiredService<IContextStore>(),
			sp.GetRequiredService<ContextInputNormalizer>(),
			sp.GetRequiredService<ContextInputValidator>(),
			sp.GetRequiredService<ContextInputHasher>(),
			sp.GetRequiredService<ContextInputSequencer>(),
			sp.GetService<IShortTermMemoryStore>(),
			sp.GetService<IShortTermWorkingItemExtractor>(),
			sp.GetService<ShortTermMemoryPolicy>(),
			sp.GetService<IRelationProjector>(),
			sp.GetService<IRelationStore>(),
			sp.GetService<IRelationProjectionWriter>(),
			sp.GetService<IWriteTransactionScopeFactory>()));
		services.AddSingleton<ShortTermMemoryPolicy>();
		services.AddSingleton<ShortTermMemoryCompactionPolicy>();
		services.AddSingleton<IShortTermWorkingItemExtractor, RuleBasedShortTermWorkingItemExtractor>();
		services.AddSingleton<ShortTermMemoryCompactionService>();
		services.AddSingleton<IContextLearningCaseGenerator, RuleBasedContextLearningCaseGenerator>();
		services.AddSingleton(sp => new ShortTermPromotionCandidateService(
			sp.GetRequiredService<IShortTermMemoryStore>(),
			sp.GetRequiredService<IShortTermPromotionCandidateStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IRelationStore>(),
			sp.GetService<IContextLearningStore>(),
			sp.GetService<IContextLearningCaseGenerator>(),
			sp.GetService<IRelationProjector>(),
			sp.GetService<IRelationProjectionWriter>()));
		services.AddSingleton(sp => new StableReviewCandidateService(
			sp.GetRequiredService<IShortTermPromotionCandidateStore>(),
			sp.GetRequiredService<IStableReviewCandidateStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IContextLearningStore>()));
		services.AddSingleton(sp => new ConstraintGapCandidateService(
			sp.GetRequiredService<IConstraintGapCandidateStore>(),
			sp.GetRequiredService<IConstraintStore>()));
		services.AddSingleton(sp => new CandidateConstraintReviewService(
			sp.GetRequiredService<IConstraintStore>(),
			sp.GetRequiredService<ICandidateConstraintReviewStore>()));
		services.AddSingleton(sp => new CandidateMemoryReviewService(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<ICandidateMemoryReviewStore>()));
		services.AddSingleton(sp => new ContextProvenanceService(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IStableReviewCandidateStore>(),
			sp.GetService<IShortTermPromotionCandidateStore>(),
			sp.GetService<IContextLearningStore>(),
			sp.GetService<IShortTermMemoryStore>()));
		services.AddSingleton(sp => new CandidateMemorySnapshotService(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IShortTermPromotionCandidateStore>(),
			sp.GetService<IStableReviewCandidateStore>(),
			sp.GetService<IConstraintGapCandidateStore>(),
			sp.GetService<IContextLearningStore>(),
			sp.GetService<ICandidateConstraintReviewStore>(),
			sp.GetService<ICandidateMemoryReviewStore>()));
		services.AddSingleton(sp => new StableMemoryGovernanceService(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IGlobalContextStore>(),
			sp.GetService<IRelationStore>(),
			sp.GetService<ContextProvenanceService>()));
		services.AddSingleton(sp => new StableLifecycleReviewService(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IGlobalContextStore>(),
			sp.GetService<IStableLifecycleReviewStore>(),
			sp.GetService<IRelationStore>(),
			sp.GetRequiredService<StableMemoryGovernanceService>(),
			sp.GetService<IRelationProjector>(),
			sp.GetService<IRelationProjectionWriter>()));
		services.AddSingleton(sp => new RelationReviewService(
			sp.GetService<IRelationStore>(),
			sp.GetService<IRelationReviewStore>(),
			sp.GetRequiredService<RelationTypeRegistry>(),
			sp.GetRequiredService<RelationGraphValidationService>()));
		services.AddSingleton<LearningFeedbackService>();
		services.AddSingleton<LearningFeedbackReviewService>();
		services.AddSingleton<LearningFeedbackFeatureCandidateBuilder>();
	// Embedding provider 注册由 AddEmbeddingProviders 扩展方法在 Program.cs 中显式调用，
	// 根据 EmbeddingProviderOptions.ProviderType 条件注册 IEmbeddingGenerator / IEmbeddingProvider。
	// - DeterministicHash: 仅注册 IEmbeddingGenerator（基础设施测试/预览），不注册 IEmbeddingProvider，IsSemanticRetrieval=false
	// - OnnxLocal: 注册 IEmbeddingGenerator + IEmbeddingProvider（真正语义检索），需配置模型路径
	// - Disabled: 不注册任何 embedding 服务
	services.AddSingleton(sp => new VectorReindexPlanner(
			sp.GetService<IContextStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IVectorIndexStore>(),
			sp.GetService<IEmbeddingGenerator>()));
		services.AddSingleton(sp => new VectorReindexExecutor(
			sp.GetRequiredService<VectorReindexPlanner>(),
			sp.GetService<IEmbeddingGenerator>(),
			sp.GetService<IVectorIndexStore>(),
			sp.GetService<IVectorReindexReportStore>()));
		services.AddSingleton(sp => new VectorIndexService(
			sp.GetService<IVectorIndexStore>(),
			sp.GetService<IEmbeddingGenerator>(),
			sp.GetService<IContextStore>(),
			sp.GetService<IMemoryStore>()));
		services.AddSingleton<VectorQueryProfileRegistry>();
		services.AddSingleton<VectorSourceLifecycleMetadataResolver>();
		services.AddSingleton<VectorCandidateEligibilityPolicy>();
		services.AddSingleton(sp => new VectorQueryPreviewService(
			sp.GetService<IVectorIndexStore>(),
			sp.GetService<IEmbeddingGenerator>(),
			sp.GetRequiredService<VectorIndexService>(),
			sp.GetRequiredService<VectorQueryProfileRegistry>(),
			sp.GetRequiredService<VectorCandidateEligibilityPolicy>()));
		services.AddSingleton(sp => new VectorLifecycleMetadataReviewCandidateService(
			sp.GetRequiredService<IVectorLifecycleMetadataReviewCandidateStore>()));
		services.AddSingleton(sp => new VectorLifecycleMetadataReviewService(
			sp.GetRequiredService<IVectorLifecycleMetadataReviewCandidateStore>(),
			sp.GetRequiredService<IVectorLifecycleMetadataReviewStore>(),
			sp.GetRequiredService<IVectorLifecycleSidecarMetadataStore>()));
		services.AddSingleton<ContextValidationService>();
		services.AddSingleton<IContextValidationService>(sp => sp.GetRequiredService<ContextValidationService>());
		services.AddSingleton<CollectionValidationService>();
		services.AddSingleton<IRelationProjector, RelationProjector>();
		services.AddSingleton<RelationTypeRegistry>();
		// 4.4：RelationProjectorOutputValidator / RelationProjectionWriter 统一写入边界注册。
		// 使用 Singleton 生命周期：writer 依赖 IRelationStore(Singleton) 和 validator(Singleton)，
		// 且被 BasicContextIngestionService/CompressionJobProcessor 等 Singleton 服务消费，
		// 使用 Scoped 会产生 captive dependency。
		services.AddSingleton<RelationTypeNormalizer>();
		services.AddSingleton<RelationProjectorOutputValidator>();
		// P1-5：注册 RelationProjectionWriter 具体类型作为 inner writer。
		// IRelationProjectionWriter 通过工厂委托返回：当 IRelationOutboxStore 可用（Postgres provider）时
		// 包装为 OutboxAwareRelationProjectionWriter；否则返回裸 RelationProjectionWriter。
		// 两者均同时实现 IRelationProjectionWriter + ITransactionalRelationProjectionWriter，
		// BasicContextIngestionService 通过 (ITransactionalRelationProjectionWriter)_projectionWriter 转型可正常工作。
		services.AddSingleton<RelationProjectionWriter>();
		services.AddSingleton<IRelationProjectionWriter>(sp =>
		{
			var inner = sp.GetRequiredService<RelationProjectionWriter>();
			var outboxStore = sp.GetService<IRelationOutboxStore>();
			return outboxStore is null
				? inner
				: new OutboxAwareRelationProjectionWriter(inner, outboxStore);
		});
		// P3-04：生产 Service 不注入 IRelationBackfillPolicy（eval 特判只在 ControlRoom 使用）。
		// RelationGraphValidationService 接受 null，CanBackfillDeterministicEvidence 返回 false。
		services.AddSingleton(sp => new RelationGraphValidationService(
			sp.GetService<IRelationStore>(),
			sp.GetService<IContextStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IGlobalContextStore>(),
			sp.GetRequiredService<RelationTypeRegistry>(),
			backfillPolicy: null));
		services.AddSingleton<IContextTokenizerResolver, DefaultContextTokenizerResolver>();
		services.AddSingleton<IContextCompressor>(sp =>
		{
			var options = sp.GetRequiredService<CompressionProviderOptions>();
			return options.Provider.ToLowerInvariant() switch
			{
				"mock" => new MockContextCompressor(),
				"llm" => new LlmContextCompressor(sp.GetRequiredService<IModelGateway>()),
				_ => throw new InvalidOperationException(
					$"Unknown compression provider '{options.Provider}'. Supported: mock, llm.")
			};
		});

		services.AddSingleton<IPromotionPolicyEvaluator, BasicPromotionPolicyEvaluator>();
		services.AddSingleton<IPromotionCandidateFactory, BasicPromotionCandidateFactory>();

		services.AddSingleton<LoggingContextEventSink>();
		services.AddSingleton<IContextEventSink>(sp =>
		{
			var sinks = new List<IContextEventSink>
			{
				new InMemoryContextEventSink(),
				sp.GetRequiredService<LoggingContextEventSink>()
			};
			// 若已注册 FileContextEventSink，则一并加入
			var fileSink = sp.GetService<FileContextEventSink>();
			if (fileSink is not null)
			{
				sinks.Add(fileSink);
			}

			// 若已注册 PostgresContextEventSink，则一并加入
			var postgresSink = sp.GetService<PostgresContextEventSink>();
			if (postgresSink is not null)
			{
				sinks.Add(postgresSink);
			}

			// P0-8 + R13.4 #1：CompositeContextEventSink.Kind 取最严格值——
			// 当 FileContextEventSink / PostgresContextEventSink（审计 sink，Kind=Required）存在时，
			// Composite.Kind = Required，外层 BoundedChannelContextEventSink 绕过通道、直接同步调用，
			// 审计事件不被通道满丢弃。
			// 当无 Required 子 sink（仅 InMemory + Logging 测试场景）时，Composite.Kind = BestEffort，
			// 走有界通道 + 后台批量消费路径，启用批量 I/O 与背压丢弃。
			var composite = new CompositeContextEventSink(sinks);
			return new BoundedChannelContextEventSink(composite);
		});

		services.AddSingleton<ContextRuntimeService>();
		services.AddSingleton<IContextRuntimeService>(sp =>
			sp.GetRequiredService<ContextRuntimeService>());
		// P0-7：注册 TraceBackedDecisionEvidenceProvider 作为生产 IDecisionEvidenceProvider 实现。
		// 仅当 trace store 实际可用时才会完成证据解析（IsComplete=true），否则返回 Incomplete。
		// 未注册 IDecisionEvidenceProvider 时审计报告标记 NotConfigured（保留 null-safe 语义）。
		services.AddSingleton<IDecisionEvidenceProvider>(sp => new TraceBackedDecisionEvidenceProvider(
			sp.GetService<IRetrievalTraceStore>(),
			sp.GetService<IContextPackageBuildTraceStore>()));
		services.AddSingleton<ContextDecisionAuditRunner>(sp => new ContextDecisionAuditRunner(
			sp.GetRequiredService<IDecisionTraceStore>(),
			sp.GetService<IDecisionEvidenceProvider>()));
		services.AddSingleton<ShortTermMaintenanceRuntimeState>(sp =>
		{
			var state = new ShortTermMaintenanceRuntimeState();
			state.Configure(sp.GetRequiredService<IOptions<ShortTermMaintenanceOptions>>().Value);
			return state;
		});
		services.AddSingleton<ServiceAlphaRuntimeInspector>();

		services.AddSingleton<IContextJobProcessor, CompressionJobProcessor>();
		services.AddSingleton<IContextJobProcessor, VectorIndexingJobProcessor>();
		services.AddSingleton<IContextJobProcessor>(_ => new UnsupportedJobProcessor(ContextJobKind.IndexBuild));
		services.AddSingleton<IContextJobProcessor>(_ => new UnsupportedJobProcessor(ContextJobKind.PackageRefresh));
		services.AddSingleton<ContextJobDispatcher>();
		services.AddSingleton<IContextJobDispatcher>(sp => sp.GetRequiredService<ContextJobDispatcher>());

		// --- 统一主链组装（Full profile：传入生产 trace sinks）---
		services.AddSingleton(sp => ContextRuntimeBuilder.Build(new RuntimeBuildOptions
		{
			ContextStore = sp.GetRequiredService<IContextStore>(),
			MemoryStore = sp.GetRequiredService<IMemoryStore>(),
			ConstraintStore = sp.GetRequiredService<IConstraintStore>(),
			RelationStore = sp.GetRequiredService<IRelationStore>(),
			GlobalContextStore = sp.GetRequiredService<IGlobalContextStore>(),
			VectorStore = sp.GetRequiredService<IVectorStore>(),
			EmbeddingProvider = sp.GetService<IEmbeddingProvider>(),
			RetrievalTraceStore = sp.GetRequiredService<IRetrievalTraceStore>(),
			TokenizerResolver = sp.GetRequiredService<IContextTokenizerResolver>(),
			PromotionRecordStore = sp.GetRequiredService<IPromotionRecordStore>(),
			WorkingMemoryService = sp.GetRequiredService<IWorkingMemoryService>(),
			// P0-10.4: 注入 DI singleton RelationTypeRegistry，保证 Runtime 主链与
			// RelationReviewService / RelationGraphValidationService 共用同一份 taxonomy。
			RelationTypeRegistry = sp.GetRequiredService<RelationTypeRegistry>(),
			PackageBuildTraceStore = sp.GetService<IContextPackageBuildTraceStore>(),
			DecisionTraceStore = sp.GetService<IDecisionTraceStore>(),
			RuntimeCandidateTraceSink = sp.GetService<IRuntimeCandidateTraceSink>(),
			// R13-F：Cache Canary Freeze。生产默认关闭（PackageTemplateCacheOptions.Enabled=false → null）。
			// 启用前置条件：Enabled=true + AllowedWorkspaces 非空 + 单实例（FileSystem provider 时检测
			// FileSystemInstanceGuard.IsMultiProcessDetected；RequireSingleInstance=false 可绕过）。
			// 启用后 ContextStateCacheAccessor.canaryGate 仅对 AllowedWorkspaces 列出的工作空间走缓存路径，
			// 其余工作空间仍走全量流水线。R13.0 正确性测试（ContextStateCacheTests）覆盖 Cold 与 Hit 等价性、
			// 版本失效、对象隔离、poisoned key、shutdown 行为——canary 启用时不需重新验证。
			CacheAccessor = BuildPackageTemplateCacheAccessorOrNull(sp),
			// R13.3 #2：注入 IStoreRuntimeCapabilities 以驱动 Retrieval fanout（替代 namespace 字符串推断）
			Capabilities = sp.GetService<IStoreRuntimeCapabilities>()
		}));

		// 主链服务从 RuntimeServices 获取（保证对象图一致性）
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationExpansionProfileRegistry);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationExpansionPolicyValidator);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationTraversalEngine);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationExpansionPreviewService);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PromotionService);
		services.AddSingleton<IMemoryPromotionService>(sp => sp.GetRequiredService<RuntimeServices>().PromotionService);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PackageBuilder);
		services.AddSingleton<IContextPackageBuilder>(sp => sp.GetRequiredService<RuntimeServices>().PackageBuilder);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().Retriever);
		services.AddSingleton<IContextRetriever>(sp => sp.GetRequiredService<RuntimeServices>().Retriever);

		// R28-B B-1：Unified Decision Runtime 骨架注册（不改生产行为）。
		// 这些注册仅让新契约可被 DI 解析；主链（IContextRetriever / IContextPackageBuilder）
		// 仍走 RuntimeServices 路径。B-2 阶段才将 IContextDecisionRuntime 接入真实编排。
		// DefaultContextDecisionEngine 依赖可选 IPolicyRegistry（Postgres provider 注册时非空）。
		services.AddSingleton<DefaultContextDecisionEngine>(sp => new DefaultContextDecisionEngine(
			sp.GetService<IPolicyRegistry>()));
		services.AddSingleton<IContextDecisionEngine>(sp => sp.GetRequiredService<DefaultContextDecisionEngine>());
		services.AddSingleton<IResolvedPolicyProvider, DefaultResolvedPolicyProvider>();
		services.AddSingleton<IExpertCatalog, DefaultExpertCatalog>();
		services.AddSingleton<ContextCore.Abstractions.IRouter, DefaultRouter>();
		services.AddSingleton<ICanonicalCandidateMerger, DefaultCanonicalCandidateMerger>();
		services.AddSingleton<IEarlyAdmissionGate, DefaultEarlyAdmissionGate>();
		services.AddSingleton<IFeaturePipeline, DefaultFeaturePipeline>();
		services.AddSingleton<ISafetyGate, DefaultSafetyGate>();
		services.AddSingleton<ILifecycleGate, DefaultLifecycleGate>();
		services.AddSingleton<IUtilityScorer, DefaultUtilityScorer>();
		services.AddSingleton<IGlobalAllocator, DefaultGlobalAllocator>();
		services.AddSingleton<IAgentContextProjector, AgentContextProjector>();
		services.AddSingleton<IContextDecisionRuntime, DefaultContextDecisionRuntime>();

		return services;
	}

	/// <summary>
	/// R13-F：根据 PackageTemplateCacheOptions 构建可空的生产 Package Template 缓存访问器。
	/// 返回 null 表示生产缓存关闭——BasicContextPackageBuilder 走全量流水线（无缓存命中）。
	/// 返回非 null 表示 canary 启用——ContextStateCacheAccessor.canaryGate 控制按工作空间粒度缓存。
	/// 启用前置条件全部满足时才返回非 null：
	/// 1. options.Enabled = true
	/// 2. options.AllowedWorkspaces 非空（否则 canary 形同关闭）
	/// 3. 单实例检查通过（RequireSingleInstance=true 时检测 FileSystemInstanceGuard.IsMultiProcessDetected；
	///    非 FileSystem provider 不检查——operator 需自行确保单实例）
	/// </summary>
	private static ContextStateCacheAccessor? BuildPackageTemplateCacheAccessorOrNull(IServiceProvider sp)
	{
		var opts = sp.GetService<IOptions<PackageTemplateCacheOptions>>()?.Value;
		if (opts is not { Enabled: true } || opts.AllowedWorkspaces.Count == 0)
		{
			return null;
		}

		// 单实例检查：仅 FileSystem provider 检测多进程（advisory，不阻断——失败回退 null）。
		// 多进程下启用 canary 会导致 InMemory version store 跨进程不一致——返回 null 关闭缓存。
		if (opts.RequireSingleInstance)
		{
			var storageOpts = sp.GetService<StorageOptions>();
			if (storageOpts is { IsFileSystem: true } fsOpts)
			{
				// FileSystemInstanceGuard.GetOrCreate 已在 FileStorage 注册时被调用过；
				// 此处再次调用返回缓存的进程内单例（不会重复尝试获取 sentinel 锁）。
				var guard = FileSystemInstanceGuard.GetOrCreate(fsOpts.ResolvedRootPath);
				if (guard.IsMultiProcessDetected)
				{
					return null;
				}
			}
		}

		// canary-aware ContextStateCacheAccessor 已注册为 singleton——返回该实例。
		return sp.GetRequiredService<ContextStateCacheAccessor>();
	}

	/// <summary>
	/// R13-F：canary gate 谓词——检查请求的依赖 scope 集合对应的工作空间是否在 allowlist 中。
	/// 所有 scope 共享同一 WorkspaceId（由 PackageRequestFingerprintBuilder.BuildDependencyScopes 保证）。
	/// 取首个 scope 的 WorkspaceId 进行判断；空 scope 集合（不应发生）保守返回 false。
	/// </summary>
	private static bool CacheCanaryGateWorkspaceAllowed(DependencyScopeSet scopes, IReadOnlySet<string> allowedWorkspaces)
	{
		foreach (var scope in scopes.Scopes)
		{
			return allowedWorkspaces.Contains(scope.WorkspaceId);
		}
		return false;
	}

	/// <summary>注册模型网关，绑定 <c>ModelGateway</c> 配置节。</summary>
	public static IServiceCollection AddContextModelGateway(
		this IServiceCollection services,
		IConfiguration configuration)
	{
		var options = new ModelGatewayOptions();
		configuration.GetSection("ModelGateway").Bind(options);
		options = ModelGatewayOptionsMaterializer.Materialize(options);
		var apiKeyResolver = new ApiKeyResolver();

		// 若未配置任何模型，回退到 mock 模式
		if (options.Models.Count == 0)
		{
			var mockOptions = new ModelGatewayOptions
			{
				Models =
				[
					new ModelEndpointOptions
					{
						Name = "mock",
						Provider = "mock",
						Enabled = true
					}
				]
			};
			services.AddSingleton(apiKeyResolver);
			services.AddSingleton(mockOptions);
			services.AddSingleton<IModelGateway>(_ =>
				new BasicModelGateway([new MockModelAdapter()]));
			services.AddSingleton<IModelHealthService>(_ =>
				new ModelHealthService(mockOptions, [new MockModelAdapter()], apiKeyResolver));
			return services;
		}

		ModelGatewayConfigurationValidator.ThrowIfInvalid(options, apiKeyResolver);
		services.AddSingleton(apiKeyResolver);
		services.AddSingleton(options);
		services.AddSingleton<IModelGateway>(sp =>
		{
			var gatewayOptions = sp.GetRequiredService<ModelGatewayOptions>();
			var resolver = sp.GetRequiredService<ApiKeyResolver>();
			return new ConfigurableModelGateway(
				gatewayOptions,
				ModelAdapterFactory.CreateAdapters(gatewayOptions, resolver));
		});
		services.AddSingleton<IModelHealthService>(sp =>
		{
			var gatewayOptions = sp.GetRequiredService<ModelGatewayOptions>();
			var resolver = sp.GetRequiredService<ApiKeyResolver>();
			var adapters = ModelAdapterFactory.CreateAdapters(gatewayOptions, resolver);
			return new ModelHealthService(gatewayOptions, adapters, resolver);
		});

		return services;
	}

	/// <summary>
	/// 根据 EmbeddingProviderOptions.ProviderType 显式注册 embedding provider。
	/// - DeterministicHash: 仅注册 IEmbeddingGenerator（基础设施测试/预览），不注册 IEmbeddingProvider，IsSemanticRetrieval=false
	/// - OnnxLocal: 注册 IEmbeddingGenerator + IEmbeddingProvider（真正语义检索），需配置模型路径
	/// - Disabled: 不注册任何 embedding 服务
	/// 通过条件注册避免 nullable 工厂返回值，确保 GetService&lt;T&gt; 在未注册时返回 null。
	/// </summary>
	public static IServiceCollection AddEmbeddingProviders(this IServiceCollection services, EmbeddingProviderOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (!options.Enabled || string.Equals(options.ProviderType, EmbeddingProviderTypes.Disabled, StringComparison.OrdinalIgnoreCase))
		{
			return services;
		}

		if (string.Equals(options.ProviderType, EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase))
		{
			// OnnxEmbeddingGenerator 接收 EmbeddingProviderOptions 并内部转换为 EmbeddingOptions
			services.AddSingleton<IEmbeddingGenerator>(new ContextCore.Embedding.OnnxEmbeddingGenerator(options));

			if (options.IsSemanticRetrieval)
			{
				// 检索路径 IEmbeddingProvider 需要独立的 EmbeddingOptions（含缓存上限）
				var embeddingOptions = new ContextCore.Embedding.EmbeddingOptions
				{
					ModelName = string.IsNullOrWhiteSpace(options.EmbeddingModel)
						? ContextCore.Embedding.EmbeddingModelPaths.DefaultModelName
						: options.EmbeddingModel,
					Dimensions = Math.Max(0, options.Dimension),
					MaxBatchSize = options.BatchSize > 0 ? options.BatchSize : 32,
					Normalize = options.Normalize,
					ModelPath = options.ModelPath,
					VocabularyPath = options.TokenizerPath,
					MaxSequenceLength = options.MaxTokens > 0 ? options.MaxTokens : 256,
					PoolingStrategy = Enum.TryParse<ContextCore.Embedding.EmbeddingPoolingStrategy>(options.PoolingStrategy, ignoreCase: true, out var pooling)
						? pooling
						: null,
					EnableContentHashCache = true
				};
				var cacheMax = options.CacheMaxEntries > 0 ? options.CacheMaxEntries : 10000;
				services.AddSingleton<IEmbeddingProvider>(new ContextCore.Embedding.OnnxEmbeddingProvider(embeddingOptions, cacheMax));
			}
			return services;
		}

		// 默认：DeterministicHash（仅用于可重复基础设施测试和预览，不是语义检索）
		var dimension = options.Dimension > 0 ? options.Dimension : 16;
		services.AddSingleton<IEmbeddingGenerator>(new DeterministicHashEmbeddingGenerator(dimension));
		return services;
	}
}




