using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Jobs;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Evolution;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Core.Services.Promotion;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Inference.Onnx;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Runtime;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Extensions;

/// <summary>Core 服务层与模型网关 of DI 注册扩展。</summary>
internal static class CoreExtensions
{
	/// <summary>注册 Core 业务服务（摄取、打包、校验、晋升、工作记忆）。</summary>
	/// <remarks>
	/// 子问题5：使用默认 ModelExecutionOptions（Deterministic 模式），向后兼容。
	/// P0-1：[Obsolete] 此重载强制选择 Deterministic 模式，与 ProductionHA Profile 真实运行模式分裂。
	/// 新代码应使用 <see cref="ProductionRuntimeExtensions.AddContextCoreRuntime"/> 单一入口，
	/// 由该方法按 ContextCoreRuntime:ModelMode 配置选择正确的 ModelExecutionOptions。
	/// 旧调用方（测试 / 已弃用路径）继续工作，但生产 Program.cs 已切换到新入口。
	/// </remarks>
	[Obsolete("P0-1: 此重载强制 Deterministic 模式。新代码应使用 AddContextCoreRuntime(IConfiguration)。"
		+ " 详见 ContextCoreRuntimeOptions.ModelMode。")]
	public static IServiceCollection AddContextCore(this IServiceCollection services)
		=> AddContextCore(services, ModelExecutionOptions.Default);

	/// <summary>
	/// 注册 Core 业务服务，并按 <paramref name="modelExecutionOptions"/> 选择 IBatchInferenceEngine 注册方式。
	/// </summary>
	/// <param name="services">DI 容器。</param>
	/// <param name="modelExecutionOptions">模型执行配置（子问题5：控制 Deterministic / RealModel 模式）。</param>
	public static IServiceCollection AddContextCore(this IServiceCollection services, ModelExecutionOptions modelExecutionOptions)
	{
		// 子问题5：注册 ModelExecutionOptions 单例（供 HostedService / 运行时查询当前模式）。
		services.AddSingleton(modelExecutionOptions ?? ModelExecutionOptions.Default);

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
		// R29 WP-E-5：用户反馈服务（thumbs up/down + 评分修正 + 文本反馈 → IUserFeedbackLedger）。
		// 生产路径 IUserFeedbackLedger 由 PostgresServiceCollectionExtensions 注册；
		// Service 端 StorageExtensions 在 FileSystem / InMemory 模式下注册 InMemoryUserFeedbackLedgerStore。
		services.AddSingleton<UserFeedbackService>();
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
		// P0-1：Legacy 具体类型仍注册为 concrete type（供 Authoritative Runtime 注入）
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PackageBuilder);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().Retriever);

		// R28-B B-2：Unified Decision Runtime — pure Runtime + Shadow tee 注册。
		// P0-1：主链（IContextRetriever / IContextPackageBuilder）已切换为 Authoritative Runtime（装饰器模式）。
		// IContextDecisionRuntime 已升级为真实编排（EarlyGate → Feature → Safety → Score → Engine → Allocator）。
		// ShadowDecisionRuntime 编排 Legacy + Tee + V2 + Parity，产出 Diagnostic parity 报告（B-3 升级为 Hard）。
		// P0-3：注册 IPolicyRegistry 默认实现（in-memory DefaultPolicyRegistry）。
		// 使用 TryAdd 避免 Postgres provider 扩展已注册 PostgresPolicyRegistry 时产生重复注册。
		// 调用顺序：AddContextStorage(Postgres) 先注册 → AddContextCore 的 TryAdd 跳过。
		// 未配置 Postgres 时 TryAdd 生效，确保 PostgresResolvedPolicyProvider 可解析策略。
		services.TryAddSingleton<DefaultPolicyRegistry>();
		services.TryAddSingleton<IPolicyRegistry>(sp => sp.GetRequiredService<DefaultPolicyRegistry>());
		// R28-B.6：Engine 注入全部 V2 决策抽象（SafetyGate/LifecycleGate/UtilityScorer/GlobalAllocator）。
		// Engine 是唯一决策点：Runtime 不再在 Engine 前执行 Safety/Lifecycle/Score。
		// R29 WP-D-1：注入 IAllocatorV2_1，使 Engine 在 DiversityOptions 非空时走 V2.1 AllocateWithDiversity。
		// R29 WP-F-3：注入 IPerformanceMonitor，使 Engine 在 V2 路径执行超过阈值时自动回退到 V2.0 Allocator。
		// P5：注入 IComponentHealthRegistry，使 Engine/Runtime 按组件归因耗时并支持组件级回退。
		services.TryAddSingleton<DefaultPerformanceMonitor>();
		services.TryAddSingleton<IPerformanceMonitor>(sp => sp.GetRequiredService<DefaultPerformanceMonitor>());
		// P5：组件健康注册表（Singleton，与 DefaultPerformanceMonitor 同生命周期）。
		// 参考 DefaultPerformanceMonitor 注册模式：TryAddSingleton 避免重复注册；默认使用 ComponentFallbackOptions.Default。
		services.TryAddSingleton<DefaultComponentHealthRegistry>();
		services.TryAddSingleton<IComponentHealthRegistry>(sp => sp.GetRequiredService<DefaultComponentHealthRegistry>());
		services.AddSingleton<DefaultContextDecisionEngine>(sp => new DefaultContextDecisionEngine(
			sp.GetService<IPolicyRegistry>(),
			safetyGate: sp.GetService<ISafetyGate>(),
			lifecycleGate: sp.GetService<ILifecycleGate>(),
			utilityScorer: sp.GetService<IUtilityScorer>(),
			globalAllocator: sp.GetService<IGlobalAllocator>(),
			allocatorV2_1: sp.GetService<IAllocatorV2_1>(),
			performanceMonitor: sp.GetService<IPerformanceMonitor>(),
			componentHealthRegistry: sp.GetService<IComponentHealthRegistry>()));
		services.AddSingleton<IContextDecisionEngine>(sp => sp.GetRequiredService<DefaultContextDecisionEngine>());
		// P0-3：将 IResolvedPolicyProvider 从 B-1 骨架 DefaultResolvedPolicyProvider 替换为
		// PostgresResolvedPolicyProvider，接入 IPolicyRegistry（CAS epoch + content hash +
		// activation override + request override）。IPolicyRegistry 由 DefaultPolicyRegistry
		// （in-memory）或 PostgresPolicyRegistry（生产）提供。
		services.AddSingleton<PostgresResolvedPolicyProvider>();
		services.AddSingleton<IResolvedPolicyProvider>(sp => sp.GetRequiredService<PostgresResolvedPolicyProvider>());
		services.AddSingleton<IExpertCatalog, DefaultExpertCatalog>();
		services.AddSingleton<ContextCore.Abstractions.IRouter, DefaultRouter>();
		services.AddSingleton<ICanonicalCandidateMerger, DefaultCanonicalCandidateMerger>();
		// R28-B.6：真实 ICandidateProvider 注册。每个 Provider 对应一个 ExpertKind，
		// 注入对应 Store（可选 Store 为 null 时 Provider 返回空结果，不抛异常）。
		// R29 WP-D-3：所有 Provider 注入 IContextTokenizerResolver（fail-fast：内容非空但 tokenizer 不可用时抛异常）。
		services.AddSingleton<ICandidateProvider>(sp => new MandatoryCandidateProvider(
			sp.GetRequiredService<IContextStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		services.AddSingleton<ICandidateProvider>(sp => new ConstraintCandidateProvider(
			sp.GetService<IConstraintStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		services.AddSingleton<ICandidateProvider>(sp => new LexicalCandidateProvider(
			sp.GetRequiredService<IContextStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		services.AddSingleton<ICandidateProvider>(sp => new SemanticCandidateProvider(
			sp.GetRequiredService<IContextStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IEmbeddingProvider>(),
			sp.GetService<IVectorStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		services.AddSingleton<ICandidateProvider>(sp => new WorkingMemoryCandidateProvider(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		services.AddSingleton<ICandidateProvider>(sp => new StableMemoryCandidateProvider(
			sp.GetService<IMemoryStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		services.AddSingleton<ICandidateProvider>(sp => new GraphCandidateProvider(
			sp.GetRequiredService<IContextStore>(),
			sp.GetService<IRelationStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IContextTokenizerResolver>()));
		// 收集所有 ICandidateProvider 到 IReadOnlyList（DI 容器不自动处理 IReadOnlyList<T>）
		services.AddSingleton<IReadOnlyList<ICandidateProvider>>(
			sp => sp.GetServices<ICandidateProvider>().ToList());
		services.AddSingleton<IEarlyAdmissionGate, DefaultEarlyAdmissionGate>();
		services.AddSingleton<IFeaturePipeline, DefaultFeaturePipeline>();
		services.AddSingleton<ISafetyGate, DefaultSafetyGate>();
		services.AddSingleton<ILifecycleGate, DefaultLifecycleGate>();
		// R28-D：DefaultUtilityScorer 注入模型推理 + 校准 + 特征 schema（可选）。
		// null 时强制 rule-only（EnableModelScoring=true 也不触发模型路径）。
		// 子问题6：IFeatureSchemaValidator 为必须依赖（非 null），推理前强制校验输入特征与 schema 一致性；
		// IInferenceResultValidator 可选（未注册时 Scorer 内部回退 DefaultInferenceResultValidator）。
		services.AddSingleton<IUtilityScorer>(sp => new DefaultUtilityScorer(
			sp.GetRequiredService<IFeatureSchemaValidator>(),
			sp.GetService<IBatchInferenceEngine>(),
			sp.GetService<ICalibrationService>(),
			sp.GetService<IFeatureRegistry>(),
			sp.GetService<IInferenceResultValidator>()));
		services.AddSingleton<IGlobalAllocator, DefaultGlobalAllocator>();
		// R28-B.8.1：Allocator V2.1（section rollover + MMR diversity）。
		// 默认不替换 IGlobalAllocator（仍为 V2.0 DefaultGlobalAllocator）；
		// 需 diversity 的调用方可显式注入 IAllocatorV2_1 / DefaultAllocatorV2_1。
		// 委托给已注册的 IGlobalAllocator 作为 base allocator；IContentTruncator 可选注入。
		services.TryAddSingleton<DefaultAllocatorV2_1>(sp => new DefaultAllocatorV2_1(
			sp.GetRequiredService<IGlobalAllocator>(),
			sp.GetService<IContentTruncator>()));
		services.TryAddSingleton<IAllocatorV2_1>(sp => sp.GetRequiredService<DefaultAllocatorV2_1>());
		services.AddSingleton<IAgentContextProjector, AgentContextProjector>();
		// R28-B.7-Final：Artifact 真实化服务注册（可被测试/生产覆盖；默认使用无状态单例实现）
		services.TryAddSingleton<IRuntimeRequestNormalizer>(DefaultRuntimeRequestNormalizer.Instance);
		services.TryAddSingleton<IRequestSemanticHasher>(DefaultRequestSemanticHasher.Instance);
		services.TryAddSingleton<IExecutionArtifactFactory>(DefaultExecutionArtifactFactory.Instance);
		// R29 WP-E-2：Utility Ledger Materializer（依赖 IUtilityLedger + IConflictSetLedger）。
		// TryAddSingleton：若测试路径注入 mock materializer 则跳过；生产路径 Postgres / 开发路径 InMemory
		// 均已注册 IUtilityLedger / IConflictSetLedger（PostgresServiceCollectionExtensions / RegisterInMemory）。
		// DefaultContextDecisionRuntime 通过 nullable 参数注入；未注册时跳过物化（保持向后兼容）。
		services.TryAddSingleton<UtilityLedgerMaterializer>(sp => new UtilityLedgerMaterializer(
			sp.GetRequiredService<IUtilityLedger>(),
			sp.GetRequiredService<IConflictSetLedger>()));
		// R29 WP-E-3：训练数据导出器（依赖 IUtilityLedgerStore；Postgres / InMemory 均已注册）。
		// TryAddSingleton：若测试路径注入 mock exporter 则跳过；生产路径通过 IUtilityLedgerStore 抽象读取 ledger。
		services.TryAddSingleton<ITrainingDataExporter>(sp => new TrainingDataExporter(
			sp.GetRequiredService<IUtilityLedgerStore>()));
		// R29 WP-E-4：校准数据导出器（依赖 IUtilityLedgerStore；同 TrainingDataExporter）。
		// 输出 predicted / observed / weight 三段式 JSONL，供 Platt / Temperature / Isotonic 校准拟合消费。
		services.TryAddSingleton<ICalibrationDataExporter>(sp => new CalibrationDataExporter(
			sp.GetRequiredService<IUtilityLedgerStore>()));

		// Learning Event Pipeline 补齐：非 decision 事件统一 sink + 数据质量闸门链。
		// 依赖 IUtilityLedgerStore（Postgres / InMemory 均已注册）+ IUserFeedbackLedger（同 UserFeedbackService）。
		// TryAddSingleton：测试路径注入 mock 实现时跳过默认实现。
		services.TryAddSingleton<ILearningPipelineSink>(sp => new LearningPipelineSink(
			logger: sp.GetService<Microsoft.Extensions.Logging.ILogger<LearningPipelineSink>>()));
		services.TryAddSingleton<ILabelQualityScorer>(sp => new LabelQualityScorer(
			sp.GetRequiredService<IUtilityLedgerStore>(),
			sp.GetRequiredService<IUserFeedbackLedger>()));
		services.TryAddSingleton<ILeakageDetector>(sp => new LeakageDetector(
			sp.GetRequiredService<IUtilityLedgerStore>(),
			sp.GetService<IUserFeedbackLedger>()));
		services.TryAddSingleton<ILearningDatasetSplitter>(sp => new LearningDatasetSplitter(
			sp.GetRequiredService<IUtilityLedgerStore>()));
		services.TryAddSingleton<IOfflineReplayGate>(sp => new OfflineReplayGate(
			sp.GetRequiredService<ILabelQualityScorer>(),
			sp.GetRequiredService<ILeakageDetector>(),
			sp.GetRequiredService<ILearningDatasetSplitter>()));
		// 延迟用户反馈服务（依赖 UserFeedbackService + ILearningPipelineSink；两者均已注册）。
		services.TryAddSingleton<DelayedUserFeedbackService>();

		// Learning Loop Durable Outbox：替代 fire-and-forget Task.Run 物化路径。
		// 配置从 "LearningMaterialization" 节绑定（Program.cs 中 Configure<LearningMaterializationOptions>）。
		// 这里注册为直接的 LearningMaterializationOptions 单例（从 IOptions 解包），让 Dispatcher / Worker
		// 构造函数可直接注入（无需依赖 IOptions<T> 抽象）。
		// Metrics 单例：线程安全 Interlocked 计数器 + 延迟环形缓冲，供 dispatcher / worker / 诊断端点共享。
		// Dispatcher 单例 + IHostedService：构造时根据 ILearningEventOutboxStore 是否注册自动选择路径——
		//   - Postgres provider：Durable Outbox（持久化，进程崩溃不丢数据）；
		//   - FileSystem/InMemory：in-memory bounded Channel + 固定 worker（消除 Task.Run，但非持久）。
		// 注入到 DefaultContextDecisionRuntime 后，主决策流通过 EnqueueAsync 入队（无 Task.Run）。
		services.AddSingleton<LearningMaterializationOptions>(sp =>
		{
			// 优先从 IOptions<LearningMaterializationOptions> 解包（Program.cs 已 Configure 绑定配置节）。
			// 兼容路径：IOptions 未注册时使用默认值（Enabled=false）。
			var opts = sp.GetService<Microsoft.Extensions.Options.IOptions<LearningMaterializationOptions>>()?.Value;
			if (opts is not null) return opts;
			var fallback = new LearningMaterializationOptions();
			sp.GetService<IConfiguration>()?.GetSection("LearningMaterialization").Bind(fallback);
			return fallback;
		});
		services.AddSingleton<LearningMaterializationMetrics>();
		services.AddSingleton<LearningMaterializationDispatcher>();
		services.AddHostedService<LearningMaterializationDispatcher>(sp => sp.GetRequiredService<LearningMaterializationDispatcher>());

		// DefaultContextDecisionRuntime 注册改为工厂：注入 LearningMaterializationDispatcher（替代 Task.Run 热路径）。
	// dispatcher null 时（测试容器未注册）回退到 materializer 直接调用路径（保持向后兼容）。
	// Perf-1：注入 ISelectedCandidateHydrator（Late Hydration）。
	// hydrator 依赖 IContextStoreBatchLookup / IMemoryStoreBatchLookup（均由 storage provider 可选注册）。
	// 两个 batch lookup 都未注册时 hydrator 退化为 no-op，Runtime 保持旧行为（IncludeContent=true）。
		services.AddSingleton<DefaultSelectedCandidateHydrator>(sp => new DefaultSelectedCandidateHydrator(
			sp.GetService<IContextStoreBatchLookup>(),
			sp.GetService<IMemoryStoreBatchLookup>()));
		services.AddSingleton<ISelectedCandidateHydrator>(sp => sp.GetRequiredService<DefaultSelectedCandidateHydrator>());
		services.AddSingleton<IContextDecisionRuntime>(sp =>
		{
			var engine = sp.GetRequiredService<IContextDecisionEngine>();
			var policyProvider = sp.GetRequiredService<IResolvedPolicyProvider>();
			var router = sp.GetRequiredService<ContextCore.Abstractions.IRouter>();
			var expertCatalog = sp.GetRequiredService<IExpertCatalog>();
			var candidateProviders = sp.GetServices<ICandidateProvider>().ToArray();
			var canonicalMerger = sp.GetRequiredService<ICanonicalCandidateMerger>();
			var earlyAdmissionGate = sp.GetRequiredService<IEarlyAdmissionGate>();
			var featurePipeline = sp.GetRequiredService<IFeaturePipeline>();
			var safetyGate = sp.GetRequiredService<ISafetyGate>();
			var lifecycleGate = sp.GetRequiredService<ILifecycleGate>();
			var utilityScorer = sp.GetRequiredService<IUtilityScorer>();
			var requestNormalizer = sp.GetService<IRuntimeRequestNormalizer>();
			var requestSemanticHasher = sp.GetService<IRequestSemanticHasher>();
			var executionArtifactFactory = sp.GetService<IExecutionArtifactFactory>();
			var utilityLedgerMaterializer = sp.GetService<UtilityLedgerMaterializer>();
			var componentHealthRegistry = sp.GetService<IComponentHealthRegistry>();
			var materializationDispatcher = sp.GetService<LearningMaterializationDispatcher>();
			var selectedCandidateHydrator = sp.GetService<ISelectedCandidateHydrator>();
			return new DefaultContextDecisionRuntime(
				engine, policyProvider, router, expertCatalog, candidateProviders,
				canonicalMerger, earlyAdmissionGate, featurePipeline, safetyGate, lifecycleGate,
				utilityScorer,
				requestNormalizer: requestNormalizer,
				requestSemanticHasher: requestSemanticHasher,
				executionArtifactFactory: executionArtifactFactory,
				utilityLedgerMaterializer: utilityLedgerMaterializer,
				componentHealthRegistry: componentHealthRegistry,
				materializationDispatcher: materializationDispatcher,
				selectedCandidateHydrator: selectedCandidateHydrator);
		});
		services.AddSingleton<DecisionExperimentPlane>();
		services.AddSingleton<ShadowDecisionRuntime>();
		services.AddSingleton<ShadowGate>();
		services.AddSingleton<ShadowGateEvaluator>();
		// B-5：CutoverConfiguration 从环境变量读取（默认 0% = Legacy only）
		services.AddSingleton(CutoverConfiguration.FromEnvironment());
		services.AddSingleton<CutoverController>(sp =>
		{
			var config = sp.GetRequiredService<CutoverConfiguration>();
			var controller = new CutoverController(config.CutoverPercentage);
			return controller;
		});
		// R28-B.8 工作包 B：Per-run CutoverController 隔离。
		// CutoverControllerRegistry 包装默认控制器（CutoverPercentage 从环境变量读取），
		// 并为每个 canary run 维护独立的 CutoverController 实例，避免多 run 百分比互相覆盖。
		// ICutoverControllerResolver 供 AuthoritativeRuntime 按请求 metadata 中的 canaryRunId 路由。
		services.AddSingleton<CutoverControllerRegistry>(sp =>
		{
			var defaultController = sp.GetRequiredService<CutoverController>();
			return new CutoverControllerRegistry(defaultController);
		});
		services.AddSingleton<ICutoverControllerResolver, DefaultCutoverControllerResolver>();
		services.AddSingleton<RetrievalResultProjector>();
		services.AddSingleton<PackageResultProjector>();
		services.AddSingleton<AuthoritativeRetrievalRuntime>();
		services.AddSingleton<AuthoritativePackageRuntime>();
		services.AddSingleton<AuthoritativeAgentContextRuntime>();
		// P0-1：主链接口注册为 Authoritative Runtime（装饰器模式）。
		// IContextRetriever → AuthoritativeRetrievalRuntime（注入 HybridContextRetriever 具体类型，无 DI 循环）。
		// IContextPackageBuilder → AuthoritativePackageRuntime（注入 BasicContextPackageBuilder 具体类型，无 DI 循环）。
		// Legacy 具体类型仍注册为 concrete type（上方 RuntimeServices.PackageBuilder / .Retriever），
		// 供 Authoritative Runtime 作为 fallback 路径注入。普通消费者通过接口获取的是 V2 装饰器。
		services.AddSingleton<IContextRetriever>(sp => sp.GetRequiredService<AuthoritativeRetrievalRuntime>());
		services.AddSingleton<IContextPackageBuilder>(sp => sp.GetRequiredService<AuthoritativePackageRuntime>());
		// B-5：DecisionExperimentPlane 长期保留（sampled shadow + replay fixtures）
		// P0-9：注册 IExperimentRecorder（默认 in-memory；可替换为持久化实现）
		services.TryAddSingleton<IExperimentRecorder, InMemoryExperimentRecorder>();
		services.AddSingleton<DecisionExperimentPlaneIntegration>();

		// R28-B.8 工作包 C：Canary Metrics 采集器。从 shadow/parity 报告聚合 divergence_rate /
		// error_rate / p95_latency_ms，供 CanaryProgressionService.EvaluateAsync 消费。
		services.AddSingleton<ICanaryMetricsCollector, DefaultCanaryMetricsCollector>();

		// R28-B.8 工作包 D：Canary Progression HostedService。
		// P0-2：CanarySchedulerOptions 改用 Configure<T>() 注册（Options Pipeline），
		// 让 IOptionsMonitor<CanarySchedulerOptions> 消费者能感知后续 PostConfigure 覆盖
		// （如 ProductionHA 强制 Enabled=false）。原 AddSingleton POCO 模式不读取
		// Options Pipeline，导致 HA 模式下 Progression 仍 Enabled。
		// 配置从 "CanaryScheduler" 节绑定（未配置时使用默认 60 秒轮询 + 启用）。
		// 使用 Configure<IServiceProvider> + GetService<IConfiguration> 而非 Configure<IConfiguration>，
		// 让未注册 IConfiguration 的测试容器（raw ServiceCollection）也能解析（回退到默认值）。
		services.AddOptions<CanarySchedulerOptions>().Configure<IServiceProvider>((opts, sp) =>
		{
			sp.GetService<IConfiguration>()?.GetSection("CanaryScheduler").Bind(opts);
		});
		// CanaryProgressionService 注册为 Singleton（依赖均为 Singleton）。
		// 注入 CutoverControllerRegistry 实现 per-run 控制器隔离；注入 CutoverConfiguration 读取默认百分比。
		services.AddSingleton<CanaryProgressionService>(sp =>
		{
			var store = sp.GetService<IPipelineRunStore>() ?? new InMemoryPipelineRunStore();
			var defaultController = sp.GetService<CutoverController>() ?? new CutoverController(0);
			var registry = sp.GetService<CutoverControllerRegistry>();
			var timeProvider = sp.GetService<TimeProvider>();
			return new CanaryProgressionService(
				store, defaultController,
				options: CanaryGateOptions.FromEnvironment(),
			 timeProvider: timeProvider,
				registry: registry);
		});
		// P0-2：HostedService 注册从 AddContextCore 移除——由 AddContextCoreRuntime /
		// AddContextCoreProductionRuntime 按 Profile 选择性注册（避免单节点 + HA 双推进器）。
		// services.AddHostedService<CanaryProgressionHostedService>(); // 已迁移到 Runtime 入口

		// 任务 C：默认外部指标采集源（ICanaryExternalMetricsSource 实现）。
		// 从 Tool 执行结果、用户反馈、安全审计等外部信号采集 ground truth 指标，
		// 替代仅依赖 token budget + FinalScore 的 quality_score。
		// 注册为 Singleton：进程内 in-memory 计数器，线程安全（lock 保护）。
		// 注入 IToolDispatchJournal（可选）用于 RegisterToolResultAsync 诊断校验。
		services.AddSingleton<ICanaryExternalMetricsSource>(sp =>
			new DefaultCanaryExternalMetricsSource(sp.GetService<IToolDispatchJournal>()));

		// 任务 D：Canary HA Leader HostedService（多实例部署模式）。
		// P0-2：CanaryLeaderOptions 改用 Configure<T>() 注册（Options Pipeline），
		// 让 IOptionsMonitor<CanaryLeaderOptions> 消费者能感知后续 PostConfigure 覆盖
		// （如 ProductionHA 强制 Enabled=true）。原 AddSingleton<IOptions<T>> 手工注册
		// 会被 AddContextCoreProductionRuntime 的 RemoveService 移除后重新注册，
		// 但 IOptionsMonitor 路径仍读旧值——导致 HA Leader 实际仍 Disabled。
		// 配置从 "CanaryLeader" 节绑定（未配置时 Enabled=false，单节点模式）。
		// 使用 Configure<IServiceProvider> + GetService<IConfiguration> 而非 Configure<IConfiguration>，
		// 让未注册 IConfiguration 的测试容器（raw ServiceCollection）也能解析（回退到默认值）。
		services.AddOptions<CanaryLeaderOptions>().Configure<IServiceProvider>((opts, sp) =>
		{
			sp.GetService<IConfiguration>()?.GetSection("CanaryLeader").Bind(opts);
		});
		// P0-2：HostedService 注册从 AddContextCore 移除——由 AddContextCoreRuntime /
		// AddContextCoreProductionRuntime 按 Profile 选择性注册（避免单节点 + HA 双推进器）。
		// services.AddHostedService<CanaryLeaderHostedService>(); // 已迁移到 Runtime 入口

		// R28-D：Model Execution Runtime 默认实现。
		// - IFeatureRegistry：in-memory 特征 schema 注册表（生产可替换为持久化实现）
		// - IBatchInferenceEngine：按 ModelExecutionMode 选择注册方式（子问题5）
		// - ICalibrationService：Platt scaling 默认 A=1 B=0（identity 的 sigmoid 形式）
		// 三者均为 Singleton 生命周期：无状态/线程安全，可被多个请求共享。
		// R28-D WP-D：IFeatureRegistry 预注册 default schema 匹配 DeterministicBatchInferenceEngine.ModelVersion，
		// 使 EnableModelScoring=true 时模型路径可实际执行（否则 Get(modelVersion) 返回 null 导致回退 rule-only）。
		services.TryAddSingleton<IFeatureRegistry>(sp =>
		{
			var registry = new DefaultFeatureRegistry();
			registry.Register(new FeatureSchema
			{
				Version = "deterministic-hash-v1",
				CreatedAt = DateTimeOffset.UtcNow,
				Features = new[]
				{
					new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
					new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
					new FeatureDefinition { Name = "recency_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
					new FeatureDefinition { Name = "relation_boost", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
					new FeatureDefinition { Name = "mandatory_weight", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
					new FeatureDefinition { Name = "deterministic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" }
				}
			});
			return registry;
		});

		// 子问题5：按 ModelExecutionMode 选择 IBatchInferenceEngine 注册方式。
		// 默认 Deterministic 模式：注册 DeterministicBatchInferenceEngine（feature hash 确定性分数）。
		// RealModel 模式：注册 ModelActivationManager，以 DeterministicBatchInferenceEngine 为 fallback，
		// 运行时通过 IModelActivationManager.ActivateAsync 切换到真实 ONNX 模型。
		// 两种模式都注册 DeterministicBatchInferenceEngine 为具体类型（供 fallback / 直接消费方使用）。
		services.TryAddSingleton<DeterministicBatchInferenceEngine>();
		// 子问题1：同时注册 DeterministicBatchInferenceEngine 为 IFallbackInferenceEngine，
		// 供 ModelActivationManager 构造函数注入（避免与 IBatchInferenceEngine 注册冲突导致循环依赖）。
		services.TryAddSingleton<IFallbackInferenceEngine>(sp => sp.GetRequiredService<DeterministicBatchInferenceEngine>());
		if (modelExecutionOptions.Mode == ModelExecutionMode.RealModel)
		{
			// RealModel 模式：注册 ModelActivationManager 为 IBatchInferenceEngine。
			// 前置条件：调用方需注册 IModelArtifactRegistry（由 PostgresServiceCollectionExtensions 提供）。
			// IOnnxInferenceSessionFactory 默认使用 OnnxRuntimeInferenceSessionFactory（TryAdd 不覆盖调用方注册）。
			// 子问题1：ModelActivationManager 构造函数注入 IFallbackInferenceEngine（而非 IBatchInferenceEngine），
			// 避免解析 IBatchInferenceEngine 时回到 ModelActivationManager 自身（循环依赖）。
			services.TryAddSingleton<IOnnxInferenceSessionFactory, OnnxRuntimeInferenceSessionFactory>();
			services.AddSingleton<ModelActivationManager>(sp => new ModelActivationManager(
			sp.GetRequiredService<IModelArtifactRegistry>(),
			sp.GetRequiredService<ICalibrationValidator>(),
			sp.GetRequiredService<IFeatureRegistry>(),
			sp.GetRequiredService<IOnnxInferenceSessionFactory>(),
			sp.GetRequiredService<IFallbackInferenceEngine>(),
			sp.GetService<ICalibrationService>()));
		services.AddSingleton<IModelActivationManager>(sp => sp.GetRequiredService<ModelActivationManager>());
		services.AddSingleton<IBatchInferenceEngine>(sp => sp.GetRequiredService<ModelActivationManager>());

		// P0-6：ShadowModelManager — Champion/Challenger 影子模式支持。
		// 维护独立于 ActiveEngine 的 Challenger 引擎，让控制平面能在不替换 active 模型的前提下
		// 加载并验证候选模型（Challenger 推理结果不返回给用户，仅用于对比）。
		// 仅 RealModel 模式注册（依赖 IOnnxInferenceSessionFactory）。
		services.AddSingleton<ShadowModelManager>(sp => new ShadowModelManager(
			sp.GetRequiredService<IOnnxInferenceSessionFactory>(),
			sp.GetRequiredService<IFeatureRegistry>(),
			sp.GetRequiredService<ICalibrationValidator>(),
			sp.GetService<ICalibrationService>()));
		}
		else
		{
			// Deterministic 模式（默认）：注册 DeterministicBatchInferenceEngine 为 IBatchInferenceEngine。
			services.TryAddSingleton<IBatchInferenceEngine, DeterministicBatchInferenceEngine>();
		}
		services.TryAddSingleton<ICalibrationService, PlattCalibrationService>();

		// R29 WP-A-3：ICalibrationValidator — 模型加载时校准参数的统计有效性验证。
		// 不抛异常：返回结构化 CalibrationValidationResult（Error / Warning / Info），
		// 让 ModelArtifactRegistry 加载 descriptor 后能拒绝在统计上不合理的校准配置，
		// 或退化为 Identity。与 ICalibrationService 互补：前者验证参数本身，后者应用参数。
		services.TryAddSingleton<ICalibrationValidator, DefaultCalibrationValidator>();

		// R29 WP-A-4：IFeatureSchemaValidator — 推理前输入特征与 FeatureSchema 的严格匹配验证。
		// 检查 SchemaVersion / 必填 / 未知特征 / 类型可转换性 / 默认值回退。
		// 不抛异常：返回结构化 FeatureSchemaValidationResult，让 Scorer 在推理前 fail-fast。
		// 与 IInferenceResultValidator 互补：前者关心输入 vs schema，后者关心输出 vs 输入约束。
		services.TryAddSingleton<IFeatureSchemaValidator, DefaultFeatureSchemaValidator>();

		// 子问题6：IInferenceResultValidator — 推理输出严格验证（NaN/Infinity/Confidence 范围/Count 一致性）。
		// 由 DefaultUtilityScorer 在推理后调用，验证失败时降级到 deterministic（fail-safe）。
		// TryAddSingleton 避免覆盖调用方注册的自定义验证器。
		services.TryAddSingleton<IInferenceResultValidator, DefaultInferenceResultValidator>();

		// R28-C：Agent Kernel — .NET 决策循环（Transport + ToolDispatcher + Kernel + V2 Runtime）。
		// 默认实现：InProcessTransport（进程内 Channel）+ EchoToolDispatcher（测试用 echo）+
		// DefaultAgentKernel（编排 Transport → ToolDispatcher → CheckpointStore → IContextDecisionRuntime）。
		// R28-C WP-A：DefaultAgentKernel 额外注入 IContextDecisionRuntime + IAgentContextProjector
		//（均已在前注册，DI 自动解析）；BuildContext 指令经 V2 路径产出 AgentContextSnapshot。
		// 生产部署可替换为自定义 IAgentKernelTransport（如 gRPC / WebSocket）和
		// 自定义 IToolDispatcher（如 MCP tool bridge）。
		// IAgentCheckpointStore 默认注册 InMemoryAgentCheckpointStore（TryAdd 不覆盖 Postgres 已注册的持久化实现；
		// Postgres provider 在 AddContextCore 之前注册，故 TryAdd 跳过，PostgresAgentCheckpointStore 生效）。
		services.TryAddSingleton<IAgentCheckpointStore, InMemoryAgentCheckpointStore>();
		services.TryAddSingleton<IAgentKernelTransport, InProcessTransport>();
		services.TryAddSingleton<IToolDispatcher, EchoToolDispatcher>();
		services.TryAddSingleton<IAgentKernel, DefaultAgentKernel>();

		// 子问题 8：Agent Run Actor 生产化注册（模型驱动的 Agent 执行循环）。
		// 注册顺序：底层依赖（Store / Journal / Executor）→ 策略 / 校验 / 审批 → ModelTransport → Host。
		// 所有注册使用 TryAdd 不覆盖调用方已注册的自定义实现；保留旧 IAgentKernel/DefaultAgentKernel
		// 注册以向后兼容（旧路径仍可用，新路径通过 AgentKernelHost 启动）。

		// 子问题 8：Agent Run 元数据 Store（进程内默认实现；Postgres provider 可覆盖）
		services.TryAddSingleton<IAgentRunStore, InMemoryAgentRunStore>();
		// 子问题 8：Agent Run 事件流 Store（进程内默认实现；Postgres provider 可覆盖）
		services.TryAddSingleton<IAgentRunEventStore, InMemoryAgentRunEventStore>();

		// 子问题 8：循环策略 + Tool 校验 + 审批门（默认实现，可被调用方覆盖）
		services.TryAddSingleton<IAgentLoopPolicy, DefaultAgentLoopPolicy>();
		services.TryAddSingleton<IAgentToolCallValidator, DefaultAgentToolCallValidator>();
		services.TryAddSingleton<IAgentApprovalGate, DefaultAgentApprovalGate>();

		// 子问题 8：IAgentCheckpointFactory（默认实现，依赖 IAgentRunEventStore / IAgentRunStore）
		services.TryAddSingleton<IAgentCheckpointFactory, DefaultAgentCheckpointFactory>();

		// 子问题 7：IAgentModelTransport fallback 实现（确定性响应，不调用真实 LLM）。
		// 生产部署应替换为真实 LLM adapter（OpenAI / Anthropic / ModelGateway）。
		services.TryAddSingleton<IAgentModelTransport, DeterministicAgentModelTransport>();

		// P0-3：IAgentModelContextProjector（从 WorkingSet.Materials 取正文 + Token 预算控制）。
		services.TryAddSingleton<IAgentModelContextProjector, DefaultAgentModelContextProjector>();

		// 子问题 5：IDurableToolExecutor（封装 Tool 调用的 durable 流程：journal + dispatch）。
		// 依赖 IToolDispatcher（已注册）+ 可选 IToolDispatchJournal（Postgres provider 可注入持久化实现）。
		services.TryAddSingleton<IDurableToolExecutor, DefaultDurableToolExecutor>();

		// 子问题 8：IToolDispatchJournal（进程内默认实现；Postgres provider 可覆盖）。
		// 注册为 singleton 让 DefaultDurableToolExecutor 与 DefaultAgentKernel 共享同一 journal 实例。
		services.TryAddSingleton<IToolDispatchJournal, InMemoryToolDispatchJournal>();

		// 子问题 9：IAgentRunLease（进程内默认实现；Postgres provider 可覆盖为持久化实现）。
		services.TryAddSingleton<IAgentRunLease, InMemoryAgentRunLease>();

		// 子问题 9：AgentHostOptions 配置（默认单节点模式；生产部署通过配置覆盖）。
		services.TryAddSingleton(AgentHostOptionsDefaultFactory);

		// 子问题 8：AgentKernelHost（Singleton，per-run Actor 通过 IServiceProvider 解析）。
		services.TryAddSingleton<AgentKernelHost>();

		return services;
	}

	/// <summary>
	/// 子问题 9：AgentHostOptions 默认工厂。
	/// 从 IConfiguration 读取 "AgentHost" 段；未配置时返回默认值（单节点模式）。
	/// </summary>
	private static AgentHostOptions AgentHostOptionsDefaultFactory(IServiceProvider sp)
	{
		// 尝试从 IConfiguration 读取配置；未配置时返回默认值
		var configuration = sp.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))
			as Microsoft.Extensions.Configuration.IConfiguration;
		if (configuration is null)
		{
			return new AgentHostOptions();
		}

		var opts = new AgentHostOptions();
		var section = configuration.GetSection("AgentHost");
		if (section.Exists())
		{
			opts.LeaseEnabled = section.GetValue("LeaseEnabled", opts.LeaseEnabled);
			opts.MaxGlobalRuns = section.GetValue("MaxGlobalRuns", opts.MaxGlobalRuns);
			opts.MaxWorkspaceRuns = section.GetValue("MaxWorkspaceRuns", opts.MaxWorkspaceRuns);

			var leaseDurationStr = section["LeaseDuration"];
			if (TimeSpan.TryParse(leaseDurationStr, out var ld))
			{
				opts.LeaseDuration = ld;
			}

			var heartbeatStr = section["HeartbeatInterval"];
			if (TimeSpan.TryParse(heartbeatStr, out var hb))
			{
				opts.HeartbeatInterval = hb;
			}

			var owner = section["Owner"];
			if (!string.IsNullOrWhiteSpace(owner))
			{
				opts.Owner = owner;
			}
		}
		return opts;
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




