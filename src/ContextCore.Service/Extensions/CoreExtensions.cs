using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Jobs;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Attention;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Learning.V14_0;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Promotion;
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
		services.AddSingleton<BasicContextIngestionService>();
		services.AddSingleton<ContextInputNormalizer>();
		services.AddSingleton<ContextInputValidator>();
		services.AddSingleton<ContextInputHasher>();
		services.AddSingleton<ContextInputSequencer>();
		services.AddSingleton<ContextInputIngestionService>();
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
		services.AddSingleton(sp => new PolicyFeedbackDatasetService(
			sp.GetService<IShortTermPromotionCandidateStore>(),
			sp.GetService<IStableReviewCandidateStore>(),
			sp.GetService<IConstraintGapCandidateStore>(),
			sp.GetService<ICandidateConstraintReviewStore>(),
			sp.GetService<IConstraintStore>()));
		services.AddSingleton(sp => new LearningFeatureDatasetService(
			sp.GetRequiredService<PolicyFeedbackDatasetService>(),
			sp.GetRequiredService<PlanningIntentDetector>()));
		services.AddSingleton<LearningFeedbackService>();
		services.AddSingleton<LearningFeedbackReviewService>();
		services.AddSingleton<LearningFeedbackFeatureCandidateBuilder>();
		services.AddSingleton<LearningDatasetQualityReportBuilder>();
		services.AddSingleton<RouterIntentShadowReportBuilder>();
		services.AddSingleton<IRouterIntentDatasetProvider, FileRouterIntentDatasetProvider>();
		services.AddSingleton(sp => new RouterIntentShadowService(
			sp.GetRequiredService<RouterShadowOptions>(),
			sp.GetService<IRouterIntentShadowTraceStore>(),
			sp.GetRequiredService<PlanningIntentDetector>(),
			sp.GetService<IRouterIntentDatasetProvider>()));
		services.AddSingleton<LifecycleAwareRankerShadowScorer>();
		services.AddSingleton<LifecycleAwareRankerTraceBuilder>();
		services.AddSingleton<LifecycleAwareRankerDebugService>();
		services.AddSingleton(sp => new RankerShadowTraceExportService(
			sp.GetService<IRetrievalTraceStore>()));
		services.AddSingleton(sp => new GraphExpansionShadowTraceExportService(
			sp.GetService<IRetrievalTraceStore>()));
		services.AddSingleton<GraphExpansionShadowTraceQualityReportBuilder>();
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
		services.AddSingleton<IRelationProjectionWriter, RelationProjectionWriter>();
		services.AddSingleton<RelationExpansionProfileShadowReportBuilder>();
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
		services.AddSingleton(ContextAttentionProfile.CreateDefaultShadowV1());

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

			return new CompositeContextEventSink(sinks);
		});

		services.AddSingleton<ContextRuntimeService>();
		services.AddSingleton<IContextRuntimeService>(sp =>
			sp.GetRequiredService<ContextRuntimeService>());
		services.AddSingleton<ContextDecisionAuditRunner>(sp => new ContextDecisionAuditRunner(
			sp.GetRequiredService<IDecisionTraceStore>()));
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

		// --- 统一主链组装（Full profile：传入生产 shadow/trace sinks）---
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
			ShortTermMemoryStore = sp.GetRequiredService<IShortTermMemoryStore>(),
			LearningStore = sp.GetRequiredService<IContextLearningStore>(),
			GraphExpansionApplyOptions = sp.GetService<GraphExpansionApplyOptions>(),
			AttentionRerankOptions = sp.GetService<RetrievalAttentionRerankOptions>(),
			RetrievalPlanningOptions = sp.GetService<RetrievalPlanningOptions>(),
			PackageBuildTraceStore = sp.GetService<IContextPackageBuildTraceStore>(),
			DecisionTraceStore = sp.GetService<IDecisionTraceStore>(),
			RuntimeCandidateTraceSink = sp.GetService<IRuntimeCandidateTraceSink>(),
			AttentionProfileExperiments = ContextAttentionProfile.CreateShadowExperimentProfiles(),
			AttentionLearningStore = sp.GetService<IContextLearningStore>(),
			AttentionProfile = sp.GetService<ContextAttentionProfile>(),
			LifecycleAwareRankerShadowOptions = sp.GetService<LifecycleAwareRankerShadowOptions>(),
			LifecycleAwareRankerTraceBuilder = sp.GetService<LifecycleAwareRankerTraceBuilder>(),
			GraphExpansionShadowOptions = sp.GetService<GraphExpansionShadowOptions>()
		}));

		// 主链服务从 RuntimeServices 获取（保证对象图一致性）
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PlanningSnapshotService);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PlanningIntentDetector);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().SafetyProfile);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PlanningProposalService);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PlanningValidator);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PlanningShadowExecutor);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationExpansionProfileRegistry);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationExpansionPolicyValidator);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationTraversalEngine);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().RelationExpansionPreviewService);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().GraphExpansionApplyPolicy);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PromotionService);
		services.AddSingleton<IMemoryPromotionService>(sp => sp.GetRequiredService<RuntimeServices>().PromotionService);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().PackageBuilder);
		services.AddSingleton<IContextPackageBuilder>(sp => sp.GetRequiredService<RuntimeServices>().PackageBuilder);
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().Retriever);
		services.AddSingleton<IContextRetriever>(sp => sp.GetRequiredService<RuntimeServices>().Retriever);
		services.AddSingleton<IContextAttentionScorer>(sp => sp.GetRequiredService<RuntimeServices>().AttentionScorer);
		// GraphExpansionShadowTraceBuilder 依赖主链中间服务，由 ContextRuntimeBuilder 内部构造后转发
		services.AddSingleton(sp => sp.GetRequiredService<RuntimeServices>().GraphExpansionShadowTraceBuilder);

		return services;
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




