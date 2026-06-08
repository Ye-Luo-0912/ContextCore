using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Jobs;
using ContextCore.Core.Services;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
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
			sp.GetService<IContextLearningCaseGenerator>()));
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
			sp.GetRequiredService<StableMemoryGovernanceService>()));
		services.AddSingleton(sp => new PolicyFeedbackDatasetService(
			sp.GetService<IShortTermPromotionCandidateStore>(),
			sp.GetService<IStableReviewCandidateStore>(),
			sp.GetService<IConstraintGapCandidateStore>(),
			sp.GetService<ICandidateConstraintReviewStore>(),
			sp.GetService<IConstraintStore>()));
		services.AddSingleton(sp => new LearningFeatureDatasetService(
			sp.GetRequiredService<PolicyFeedbackDatasetService>(),
			sp.GetRequiredService<PlanningIntentDetector>()));
		services.AddSingleton<LearningDatasetQualityReportBuilder>();
		services.AddSingleton<LifecycleAwareRankerShadowScorer>();
		services.AddSingleton<LifecycleAwareRankerTraceBuilder>();
		services.AddSingleton<LifecycleAwareRankerDebugService>();
		services.AddSingleton(sp => new RankerShadowTraceExportService(
			sp.GetService<IRetrievalTraceStore>()));
		services.AddSingleton<PlanningSnapshotService>();
		services.AddSingleton<PlanningIntentDetector>();
		services.AddSingleton(RetrievalPlanSafetyProfile.CreateDefault());
		services.AddSingleton<RetrievalPlanProposalService>();
		services.AddSingleton<RetrievalPlanProposalValidator>();
		services.AddSingleton(sp => new ShadowRetrievalPlanExecutor(
			sp.GetRequiredService<IContextStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IRelationStore>(),
			sp.GetRequiredService<RetrievalPlanProposalValidator>()));
		services.AddSingleton<ContextValidationService>();
		services.AddSingleton<IContextValidationService>(sp => sp.GetRequiredService<ContextValidationService>());
		services.AddSingleton<CollectionValidationService>();
		services.AddSingleton<RelationBuilder>();
		services.AddSingleton<RelationTypeRegistry>();
		services.AddSingleton(sp => new RelationGraphValidationService(
			sp.GetService<IRelationStore>(),
			sp.GetService<IContextStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IConstraintStore>(),
			sp.GetService<IGlobalContextStore>(),
			sp.GetRequiredService<RelationTypeRegistry>()));
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

		services.AddSingleton<BasicMemoryPromotionService>();
		services.AddSingleton<IMemoryPromotionService>(sp => sp.GetRequiredService<BasicMemoryPromotionService>());
		services.AddSingleton<IPromotionPolicyEvaluator, BasicPromotionPolicyEvaluator>();
		services.AddSingleton<IPromotionCandidateFactory, BasicPromotionCandidateFactory>();
		services.AddSingleton(ContextAttentionProfile.CreateDefaultShadowV1());
		services.AddSingleton<IContextAttentionScorer>(sp => new RuleBasedContextAttentionScorer(
			sp.GetRequiredService<ContextAttentionProfile>(),
			sp.GetService<IContextLearningStore>()));
		services.AddSingleton<BasicContextPackageBuilder>(sp => new BasicContextPackageBuilder(
			sp.GetRequiredService<IContextStore>(),
			sp.GetRequiredService<IConstraintStore>(),
			sp.GetRequiredService<IGlobalContextStore>(),
			sp.GetRequiredService<IMemoryStore>(),
			sp.GetRequiredService<IRelationStore>(),
			sp.GetService<IContextPackageBuildTraceStore>(),
			sp.GetRequiredService<IContextTokenizerResolver>(),
			sp.GetService<IWorkingMemoryService>()));
		services.AddSingleton<IContextPackageBuilder>(sp =>
			sp.GetRequiredService<BasicContextPackageBuilder>());

		services.AddSingleton<HybridContextRetriever>(sp => new HybridContextRetriever(
			sp.GetRequiredService<IContextStore>(),
			sp.GetService<IMemoryStore>(),
			sp.GetService<IRelationStore>(),
			sp.GetService<IEmbeddingProvider>(),
			sp.GetService<IVectorStore>(),
			sp.GetService<IRetrievalTraceStore>(),
			sp.GetService<IContextAttentionScorer>(),
			ContextAttentionProfile.CreateShadowExperimentProfiles(),
			sp.GetService<IContextLearningStore>(),
			sp.GetRequiredService<RetrievalAttentionRerankOptions>(),
			sp.GetRequiredService<RetrievalPlanningOptions>(),
			sp.GetRequiredService<RetrievalPlanProposalService>(),
			sp.GetRequiredService<ShadowRetrievalPlanExecutor>(),
			sp.GetRequiredService<LifecycleAwareRankerShadowOptions>(),
			sp.GetRequiredService<LifecycleAwareRankerTraceBuilder>()));
		services.AddSingleton<IContextRetriever>(sp => sp.GetRequiredService<HybridContextRetriever>());

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
		services.AddSingleton<ShortTermMaintenanceRuntimeState>(sp =>
		{
			var state = new ShortTermMaintenanceRuntimeState();
			state.Configure(sp.GetRequiredService<IOptions<ShortTermMaintenanceOptions>>().Value);
			return state;
		});
		services.AddSingleton<ServiceAlphaRuntimeInspector>();

		services.AddSingleton<IContextJobProcessor, CompressionJobProcessor>();
		services.AddSingleton<IContextJobProcessor>(_ => new UnsupportedJobProcessor(ContextJobKind.IndexBuild));
		services.AddSingleton<IContextJobProcessor>(_ => new UnsupportedJobProcessor(ContextJobKind.PackageRefresh));
		services.AddSingleton<ContextJobDispatcher>();
		services.AddSingleton<IContextJobDispatcher>(sp => sp.GetRequiredService<ContextJobDispatcher>());

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
}




