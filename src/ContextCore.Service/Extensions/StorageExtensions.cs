using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Service.Extensions;

/// <summary>存储层 DI 注册扩展，根据 <see cref="StorageOptions.Provider"/> 切换实现。</summary>
internal static class StorageExtensions
{
	/// <summary>
	/// 解析缓存失效器；若未注册（如仅调用 <c>AddContextStorage</c> 的隔离测试）则回退到空实现。
	/// 生产路径由 <c>AddContextCore</c> 注册 <see cref="IStateCacheInvalidator"/>，此处回退保证存储层可独立解析。
	/// </summary>
	private static IStateCacheInvalidator GetInvalidator(IServiceProvider sp)
		=> sp.GetService<IStateCacheInvalidator>() ?? NullStateCacheInvalidator.Instance;

	/// <summary>
	/// 解析状态版本存储；未注册时返回 null（Decorator 跳过 bump）。R10-2 P3。
	/// </summary>
	private static IContextStateVersionStore? GetVersionStore(IServiceProvider sp)
		=> sp.GetService<IContextStateVersionStore>();

	/// <summary>
	/// 根据配置注册存储服务。
	/// <list type="bullet">
	///   <item><c>filesystem</c>：使用 <see cref="FileContextStore"/> 等文件系统实现，当前推荐的 Alpha 持久化后端。</item>
	///   <item><c>memory</c>：使用 <see cref="InMemoryContextStore"/> 等内存实现（仅用于测试）。</item>
		///   <item><c>postgres</c>：需配置 <see cref="StorageOptions.PostgresConnectionString"/>，启动时自动建表（AutoMigrate）。</item>
	/// </list>
	/// </summary>
	public static IServiceCollection AddContextStorage(
		this IServiceCollection services,
		StorageOptions options)
	{
		if (options.IsFileSystem)
		{
			RegisterFileSystem(services, options);
		}
		else if (options.IsMemory)
		{
			RegisterInMemory(services);
		}
		else if (options.IsPostgres)
		{
			RegisterPostgres(services, options);
		}
		else
		{
			throw new InvalidOperationException(
				$"未知存储提供商 '{options.Provider}'。支持的 provider: filesystem, memory, postgres。");
		}

		return services;
	}

	private static void RegisterPostgres(IServiceCollection services, StorageOptions options)
	{
		if (string.IsNullOrWhiteSpace(options.ResolvedPostgresConnectionString))
		{
			throw new InvalidOperationException(
				"Storage:Provider 为 postgres，但 Storage:PostgresConnectionString 未配置。" +
				"请在 appsettings.json 或环境变量中设置连接字符串（支持 env:VAR_NAME 格式）。");
		}

		var pgOptions = new PostgresOptions
		{
			Enabled = true,
			ConnectionString = options.ResolvedPostgresConnectionString,
			AutoMigrate = false,
			EnablePgVectorExtension = true,
		};

		services.AddContextCorePostgresStorage(pgOptions);
		services.AddSingleton<ILearningFeedbackStore>(_ => new UnsupportedLearningFeedbackStore("postgres"));
		services.AddSingleton<ILearningFeedbackReviewStore>(_ => new UnsupportedLearningFeedbackReviewStore("postgres"));
		services.AddSingleton<IDecisionTraceStore>(_ => new UnsupportedDecisionTraceStore("postgres"));
		// Postgres 尚未实现的存储契约，显式注册为 Unsupported，避免运行时静默丢弃数据
		services.AddSingleton<IShortTermMemoryStore>(_ => new UnsupportedShortTermMemoryStore("postgres"));
		services.AddSingleton<IShortTermPromotionCandidateStore>(_ => new UnsupportedShortTermPromotionCandidateStore("postgres"));
		services.AddSingleton<ICandidateMemoryReviewStore>(_ => new UnsupportedCandidateMemoryReviewStore("postgres"));
		services.AddSingleton<IStableReviewCandidateStore>(_ => new UnsupportedStableReviewCandidateStore("postgres"));
		services.AddSingleton<IContextLearningStore>(_ => new UnsupportedContextLearningStore("postgres"));
		services.AddSingleton<IVectorReindexReportStore>(_ => new UnsupportedVectorReindexReportStore("postgres"));
		services.AddSingleton<IVectorLifecycleMetadataReviewCandidateStore>(_ => new UnsupportedVectorLifecycleMetadataReviewCandidateStore("postgres"));
		services.AddSingleton<IVectorLifecycleMetadataReviewStore>(_ => new UnsupportedVectorLifecycleMetadataReviewStore("postgres"));
		services.AddSingleton<IVectorLifecycleSidecarMetadataStore>(_ => new UnsupportedVectorLifecycleSidecarMetadataStore("postgres"));
		services.AddSingleton<IArtifactStore>(_ => new UnsupportedArtifactStore("postgres"));
		services.AddSingleton<IStableLifecycleReviewStore>(_ => new UnsupportedStableLifecycleReviewStore("postgres"));
		services.AddSingleton<ICandidateConstraintReviewStore>(_ => new UnsupportedCandidateConstraintReviewStore("postgres"));
		services.AddSingleton<IConstraintGapCandidateStore>(_ => new UnsupportedConstraintGapCandidateStore("postgres"));

		// R10-2：在 Postgres 实现之上叠加失效边界 Decorator（覆盖 AddContextCorePostgresStorage 的原始注册）。
		// 失效 Decorator 位于最外层，写入成功后向 IStateCacheInvalidator 发出失效信号。
		services.AddSingleton<IContextStore>(sp => new InvalidatingContextStoreDecorator(
			sp.GetRequiredService<PostgresContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IContextIndex>(sp => new InvalidatingContextIndexDecorator(
			sp.GetRequiredService<PostgresContextIndex>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IMemoryStore>(sp => new InvalidatingMemoryStoreDecorator(
			sp.GetRequiredService<PostgresMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IConstraintStore>(sp => new InvalidatingConstraintStoreDecorator(
			sp.GetRequiredService<PostgresConstraintStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IRelationStore>(sp => new InvalidatingRelationStoreDecorator(
			sp.GetRequiredService<PostgresRelationStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IGlobalContextStore>(sp => new InvalidatingGlobalContextStoreDecorator(
			sp.GetRequiredService<PostgresGlobalContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		// R11-P4：在 Postgres 实现之上叠加剩余 Store 的失效边界 Decorator（覆盖 AddContextCorePostgresStorage 的原始注册）。
		services.AddSingleton<IContextCollectionStore>(sp => new InvalidatingContextCollectionStoreDecorator(
			sp.GetRequiredService<PostgresContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IWorkingMemoryService>(sp => new InvalidatingWorkingMemoryServiceDecorator(
			sp.GetRequiredService<PostgresWorkingMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IPromotionRecordStore>(sp => new InvalidatingPromotionRecordStoreDecorator(
			sp.GetRequiredService<PostgresWorkingMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IPromotionCandidateStore>(sp => new InvalidatingPromotionCandidateStoreDecorator(
			sp.GetRequiredService<PostgresWorkingMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IRelationReviewStore>(sp => new InvalidatingRelationReviewStoreDecorator(
			sp.GetRequiredService<PostgresRelationReviewStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IVectorStore>(sp => new InvalidatingVectorStoreDecorator(
			sp.GetRequiredService<PostgresVectorStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IContextPackageBuildTraceStore>(sp => new InvalidatingContextPackageBuildTraceStoreDecorator(
			sp.GetRequiredService<PostgresContextPackageBuildTraceStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IContextPackagePolicyStore>(sp => new InvalidatingContextPackagePolicyStoreDecorator(
			sp.GetRequiredService<PostgresContextPackagePolicyStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
	}

	private static void RegisterFileSystem(IServiceCollection services, StorageOptions options)
	{
		// 使用 ResolvedRootPath：展开环境变量并转为绝对路径，确保与 AppHost/ControlRoom 指向同一目录
		var fsOptions = new FileStorageOptions { RootPath = options.ResolvedRootPath };
		Directory.CreateDirectory(fsOptions.ResolvedRootPath);

		services.AddSingleton(fsOptions);
		// FilePathResolver / FileFormatSerializer 各只有一个构造函数，DI 可直接解析
		services.AddSingleton<FilePathResolver>();
		services.AddSingleton<ContextCoreDataLayout>();
		services.AddSingleton<IContextPathResolver>(sp => sp.GetRequiredService<ContextCoreDataLayout>());
		services.AddSingleton<FileArtifactStore>();
		services.AddSingleton<IArtifactStore>(sp => sp.GetRequiredService<FileArtifactStore>());
		services.AddSingleton<FileFormatSerializer>();
		services.AddSingleton<FileJsonLineStore>();
		RegisterScopedRelationGovernancePostgresSupport(services, options);

		// 各 File*Store 存在两个构造函数（DI 注入版 + 直接 new 版），需通过工厂 lambda
		// 显式指定使用 (FilePathResolver, FileFormatSerializer) 版本，避免 DI 容器歧义
		services.AddSingleton<FileContextStore>(sp =>
			new FileContextStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextStore>(sp => new InvalidatingContextStoreDecorator(
			sp.GetRequiredService<FileContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IContextCollectionStore>(sp => new InvalidatingContextCollectionStoreDecorator(
			sp.GetRequiredService<FileContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileContextIndex>(sp =>
			new FileContextIndex(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextIndex>(sp => new InvalidatingContextIndexDecorator(
			sp.GetRequiredService<FileContextIndex>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileVectorStore>(sp =>
			new FileVectorStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorStore>(sp => new InvalidatingVectorStoreDecorator(
			sp.GetRequiredService<FileVectorStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileVectorIndexStore>(sp =>
			new FileVectorIndexStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorIndexStore>(sp => sp.GetRequiredService<FileVectorIndexStore>());
		services.AddSingleton<FileVectorReindexReportStore>(sp =>
			new FileVectorReindexReportStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorReindexReportStore>(sp => new InvalidatingVectorReindexReportStoreDecorator(
			sp.GetRequiredService<FileVectorReindexReportStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<FileVectorLifecycleMetadataReviewCandidateStore>(sp =>
			new FileVectorLifecycleMetadataReviewCandidateStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorLifecycleMetadataReviewCandidateStore>(sp => new InvalidatingVectorLifecycleMetadataReviewCandidateStoreDecorator(
			sp.GetRequiredService<FileVectorLifecycleMetadataReviewCandidateStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<FileVectorLifecycleMetadataReviewStore>(sp =>
			new FileVectorLifecycleMetadataReviewStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorLifecycleMetadataReviewStore>(sp => new InvalidatingVectorLifecycleMetadataReviewStoreDecorator(
			sp.GetRequiredService<FileVectorLifecycleMetadataReviewStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<FileVectorLifecycleSidecarMetadataStore>(sp =>
			new FileVectorLifecycleSidecarMetadataStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorLifecycleSidecarMetadataStore>(sp => new InvalidatingVectorLifecycleSidecarMetadataStoreDecorator(
			sp.GetRequiredService<FileVectorLifecycleSidecarMetadataStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileContextPackageBuildTraceStore>(sp =>
			new FileContextPackageBuildTraceStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextPackageBuildTraceStore>(sp => new InvalidatingContextPackageBuildTraceStoreDecorator(
			sp.GetRequiredService<FileContextPackageBuildTraceStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<FileContextPackagePolicyStore>(sp =>
			new FileContextPackagePolicyStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextPackagePolicyStore>(sp => new InvalidatingContextPackagePolicyStoreDecorator(
			sp.GetRequiredService<FileContextPackagePolicyStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileRetrievalTraceStore>(sp =>
			new FileRetrievalTraceStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IRetrievalTraceStore>(sp =>
			sp.GetRequiredService<FileRetrievalTraceStore>());

		services.AddSingleton<FileDecisionTraceStore>(sp =>
			new FileDecisionTraceStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IDecisionTraceStore>(sp => new InvalidatingDecisionTraceStoreDecorator(
			sp.GetRequiredService<FileDecisionTraceStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

        services.AddSingleton<FileShortTermMemoryStore>(sp =>
            new FileShortTermMemoryStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>(),
                sp.GetRequiredService<ShortTermMemoryPolicy>()));
        services.AddSingleton<IShortTermMemoryStore>(sp => sp.GetRequiredService<FileShortTermMemoryStore>());
        services.AddSingleton<FileShortTermPromotionCandidateStore>(sp =>
            new FileShortTermPromotionCandidateStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IShortTermPromotionCandidateStore>(sp => new InvalidatingShortTermPromotionCandidateStoreDecorator(
            sp.GetRequiredService<FileShortTermPromotionCandidateStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<FileContextLearningStore>(sp =>
			new FileContextLearningStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextLearningStore>(sp => sp.GetRequiredService<FileContextLearningStore>());
		services.AddSingleton<FileLearningFeedbackStore>(sp =>
			new FileLearningFeedbackStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<ILearningFeedbackStore>(sp => new InvalidatingLearningFeedbackStoreDecorator(
			sp.GetRequiredService<FileLearningFeedbackStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<FileLearningFeedbackReviewStore>(sp =>
			new FileLearningFeedbackReviewStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<ILearningFeedbackReviewStore>(sp => new InvalidatingLearningFeedbackReviewStoreDecorator(
			sp.GetRequiredService<FileLearningFeedbackReviewStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<FileStableReviewCandidateStore>(sp =>
            new FileStableReviewCandidateStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IStableReviewCandidateStore>(sp => sp.GetRequiredService<FileStableReviewCandidateStore>());
        services.AddSingleton<FileConstraintGapCandidateStore>(sp =>
            new FileConstraintGapCandidateStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IConstraintGapCandidateStore>(sp => new InvalidatingConstraintGapCandidateStoreDecorator(
            sp.GetRequiredService<FileConstraintGapCandidateStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<FileCandidateConstraintReviewStore>(sp =>
            new FileCandidateConstraintReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<ICandidateConstraintReviewStore>(sp => new InvalidatingCandidateConstraintReviewStoreDecorator(
            sp.GetRequiredService<FileCandidateConstraintReviewStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<FileCandidateMemoryReviewStore>(sp =>
            new FileCandidateMemoryReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<ICandidateMemoryReviewStore>(sp => sp.GetRequiredService<FileCandidateMemoryReviewStore>());
        services.AddSingleton<FileStableLifecycleReviewStore>(sp =>
            new FileStableLifecycleReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IStableLifecycleReviewStore>(sp => new InvalidatingStableLifecycleReviewStoreDecorator(
            sp.GetRequiredService<FileStableLifecycleReviewStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<FileRelationReviewStore>(sp =>
            new FileRelationReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<FileRelationDiagnosticsStore>(sp =>
            new FileRelationDiagnosticsStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IRelationReviewStore>(sp =>
        {
            var switchOptions = sp.GetService<RelationGovernanceProviderSwitchOptions>() ?? new RelationGovernanceProviderSwitchOptions();
            IRelationReviewStore inner = !switchOptions.Enabled
                ? sp.GetRequiredService<FileRelationReviewStore>()
                : new ScopedRelationGovernanceReviewStore(
                    sp.GetRequiredService<FileRelationReviewStore>(),
                    sp.GetRequiredService<PostgresRelationReviewStore>(),
                    switchOptions,
                    sp.GetRequiredService<RelationGovernanceScopedServiceModeStatusService>());
            return new InvalidatingRelationReviewStoreDecorator(inner, GetInvalidator(sp), GetVersionStore(sp));
        });

		services.AddSingleton<FileMemoryStore>(sp =>
			new FileMemoryStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IMemoryStore>(sp => new InvalidatingMemoryStoreDecorator(
			sp.GetRequiredService<FileMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IWorkingMemoryService>(sp => new InvalidatingWorkingMemoryServiceDecorator(
			sp.GetRequiredService<FileMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IPromotionRecordStore>(sp => new InvalidatingPromotionRecordStoreDecorator(
			sp.GetRequiredService<FileMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IPromotionCandidateStore>(sp => new InvalidatingPromotionCandidateStoreDecorator(
			sp.GetRequiredService<FileMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileConstraintStore>(sp =>
			new FileConstraintStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IConstraintStore>(sp => new InvalidatingConstraintStoreDecorator(
			sp.GetRequiredService<FileConstraintStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileRelationStore>(sp =>
			new FileRelationStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IRelationStore>(sp =>
		{
			var switchOptions = sp.GetService<RelationGovernanceProviderSwitchOptions>() ?? new RelationGovernanceProviderSwitchOptions();
			// 内层：未启用 scoped 治理时直接用 FileRelationStore；启用时用 dual-write ScopedRelationGovernanceStore。
			IRelationStore inner = !switchOptions.Enabled
				? sp.GetRequiredService<FileRelationStore>()
				: new ScopedRelationGovernanceStore(
					sp.GetRequiredService<FileRelationStore>(),
					sp.GetRequiredService<PostgresRelationStore>(),
					switchOptions,
					sp.GetRequiredService<RelationGovernanceScopedServiceModeStatusService>());
			// R10-2：失效边界 Decorator 位于最外层（在 dual-write 之上），写入成功后发出失效信号。
			return new InvalidatingRelationStoreDecorator(inner, GetInvalidator(sp), GetVersionStore(sp));
		});

		services.AddSingleton<FileGlobalContextStore>(sp =>
			new FileGlobalContextStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IGlobalContextStore>(sp => new InvalidatingGlobalContextStoreDecorator(
			sp.GetRequiredService<FileGlobalContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<FileContextJobQueue>(sp =>
			new FileContextJobQueue(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextJobQueue>(sp => sp.GetRequiredService<FileContextJobQueue>());
		services.AddSingleton<IContextJobQueryStore>(sp => sp.GetRequiredService<FileContextJobQueue>());

		services.AddSingleton<FileContextEventSink>(_ =>
		{
			// logs 子目录紧邻存储根目录，使用已解析的绝对路径
			var logsRoot = Path.Combine(fsOptions.ResolvedRootPath, "logs");
			return new FileContextEventSink(logsRoot);
		});
	}

	private static void RegisterScopedRelationGovernancePostgresSupport(IServiceCollection services, StorageOptions options)
	{
		services.AddSingleton(sp => new PostgresOptions
		{
			Enabled = !string.IsNullOrWhiteSpace(options.ResolvedPostgresConnectionString),
			ConnectionString = options.ResolvedPostgresConnectionString,
			AutoMigrate = false,
			EnablePgVectorExtension = true
		});
		services.AddSingleton<PostgresJsonSerializer>();
		services.AddSingleton<PostgresConnectionFactory>();
		services.AddSingleton<IPostgresConnectionFactory>(sp => sp.GetRequiredService<PostgresConnectionFactory>());
		services.AddSingleton<PostgresMigrationRunner>();
		services.AddSingleton<IStoreMigrationRunner>(sp => sp.GetRequiredService<PostgresMigrationRunner>());
		services.AddSingleton<PostgresRelationStore>();
		services.AddSingleton<PostgresRelationReviewStore>();
		services.AddSingleton<PostgresRelationDiagnosticsStore>();
		services.AddSingleton(sp => new RelationGovernanceScopedServiceModeStatusService(
			sp.GetService<RelationGovernanceProviderSwitchOptions>() ?? new RelationGovernanceProviderSwitchOptions()));
	}

	/// <summary>
	/// 注册内存存储服务到依赖注入容器中。
	/// 此方法主要用于测试环境，通过添加一系列基于内存的实现来模拟存储服务。
	/// </summary>
	/// <param name="services">IServiceCollection 对象，用于配置和注册服务。</param>
	private static void RegisterInMemory(IServiceCollection services)
	{
		services.AddSingleton<InMemoryContextStore>();
		services.AddSingleton<IContextStore>(sp => new InvalidatingContextStoreDecorator(
			sp.GetRequiredService<InMemoryContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IContextCollectionStore>(sp => new InvalidatingContextCollectionStoreDecorator(
			sp.GetRequiredService<InMemoryContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryContextIndex>();
		services.AddSingleton<IContextIndex>(sp => new InvalidatingContextIndexDecorator(
			sp.GetRequiredService<InMemoryContextIndex>(),
			GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryShortTermMemoryStore>();
        services.AddSingleton<IShortTermMemoryStore>(sp => sp.GetRequiredService<InMemoryShortTermMemoryStore>());
        services.AddSingleton<InMemoryShortTermPromotionCandidateStore>();
        services.AddSingleton<IShortTermPromotionCandidateStore>(sp => new InvalidatingShortTermPromotionCandidateStoreDecorator(
            sp.GetRequiredService<InMemoryShortTermPromotionCandidateStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryContextLearningStore>();
        services.AddSingleton<IContextLearningStore>(sp => sp.GetRequiredService<InMemoryContextLearningStore>());
        services.AddSingleton<InMemoryLearningFeedbackStore>();
        services.AddSingleton<ILearningFeedbackStore>(sp => new InvalidatingLearningFeedbackStoreDecorator(
            sp.GetRequiredService<InMemoryLearningFeedbackStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryLearningFeedbackReviewStore>();
        services.AddSingleton<ILearningFeedbackReviewStore>(sp => new InvalidatingLearningFeedbackReviewStoreDecorator(
            sp.GetRequiredService<InMemoryLearningFeedbackReviewStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryStableReviewCandidateStore>();
        services.AddSingleton<IStableReviewCandidateStore>(sp => sp.GetRequiredService<InMemoryStableReviewCandidateStore>());
        services.AddSingleton<InMemoryConstraintGapCandidateStore>();
        services.AddSingleton<IConstraintGapCandidateStore>(sp => new InvalidatingConstraintGapCandidateStoreDecorator(
            sp.GetRequiredService<InMemoryConstraintGapCandidateStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryCandidateConstraintReviewStore>();
        services.AddSingleton<ICandidateConstraintReviewStore>(sp => new InvalidatingCandidateConstraintReviewStoreDecorator(
            sp.GetRequiredService<InMemoryCandidateConstraintReviewStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryCandidateMemoryReviewStore>();
        services.AddSingleton<ICandidateMemoryReviewStore>(sp => sp.GetRequiredService<InMemoryCandidateMemoryReviewStore>());
        services.AddSingleton<InMemoryStableLifecycleReviewStore>();
        services.AddSingleton<IStableLifecycleReviewStore>(sp => new InvalidatingStableLifecycleReviewStoreDecorator(
            sp.GetRequiredService<InMemoryStableLifecycleReviewStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));
        services.AddSingleton<InMemoryRelationReviewStore>();
        services.AddSingleton<IRelationReviewStore>(sp => new InvalidatingRelationReviewStoreDecorator(
            sp.GetRequiredService<InMemoryRelationReviewStore>(),
            GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryVectorStore>();
		services.AddSingleton<IVectorStore>(sp => new InvalidatingVectorStoreDecorator(
			sp.GetRequiredService<InMemoryVectorStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<InMemoryVectorIndexStore>();
		services.AddSingleton<IVectorIndexStore>(sp => sp.GetRequiredService<InMemoryVectorIndexStore>());
		services.AddSingleton<InMemoryVectorReindexReportStore>();
		services.AddSingleton<IVectorReindexReportStore>(sp => new InvalidatingVectorReindexReportStoreDecorator(
			sp.GetRequiredService<InMemoryVectorReindexReportStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<InMemoryVectorLifecycleMetadataReviewCandidateStore>();
		services.AddSingleton<IVectorLifecycleMetadataReviewCandidateStore>(sp => new InvalidatingVectorLifecycleMetadataReviewCandidateStoreDecorator(
			sp.GetRequiredService<InMemoryVectorLifecycleMetadataReviewCandidateStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<InMemoryVectorLifecycleMetadataReviewStore>();
		services.AddSingleton<IVectorLifecycleMetadataReviewStore>(sp => new InvalidatingVectorLifecycleMetadataReviewStoreDecorator(
			sp.GetRequiredService<InMemoryVectorLifecycleMetadataReviewStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<InMemoryVectorLifecycleSidecarMetadataStore>();
		services.AddSingleton<IVectorLifecycleSidecarMetadataStore>(sp => new InvalidatingVectorLifecycleSidecarMetadataStoreDecorator(
			sp.GetRequiredService<InMemoryVectorLifecycleSidecarMetadataStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryRetrievalTraceStore>();
		services.AddSingleton<IRetrievalTraceStore>(sp => sp.GetRequiredService<InMemoryRetrievalTraceStore>());
		services.AddSingleton<InMemoryDecisionTraceStore>();
		services.AddSingleton<IDecisionTraceStore>(sp => new InvalidatingDecisionTraceStoreDecorator(
			sp.GetRequiredService<InMemoryDecisionTraceStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<InMemoryContextPackagePolicyStore>();
		services.AddSingleton<IContextPackagePolicyStore>(sp => new InvalidatingContextPackagePolicyStoreDecorator(
			sp.GetRequiredService<InMemoryContextPackagePolicyStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryMemoryStore>();
		services.AddSingleton<IMemoryStore>(sp => new InvalidatingMemoryStoreDecorator(
			sp.GetRequiredService<InMemoryMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IWorkingMemoryService>(sp => new InvalidatingWorkingMemoryServiceDecorator(
			sp.GetRequiredService<InMemoryMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IPromotionRecordStore>(sp => new InvalidatingPromotionRecordStoreDecorator(
			sp.GetRequiredService<InMemoryMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));
		services.AddSingleton<IPromotionCandidateStore>(sp => new InvalidatingPromotionCandidateStoreDecorator(
			sp.GetRequiredService<InMemoryMemoryStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryConstraintStore>();
		services.AddSingleton<IConstraintStore>(sp => new InvalidatingConstraintStoreDecorator(
			sp.GetRequiredService<InMemoryConstraintStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryRelationStore>();
		services.AddSingleton<IRelationStore>(sp => new InvalidatingRelationStoreDecorator(
			sp.GetRequiredService<InMemoryRelationStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryGlobalContextStore>();
		services.AddSingleton<IGlobalContextStore>(sp => new InvalidatingGlobalContextStoreDecorator(
			sp.GetRequiredService<InMemoryGlobalContextStore>(),
			GetInvalidator(sp), GetVersionStore(sp)));

		services.AddSingleton<InMemoryJobQueue>();
		services.AddSingleton<IContextJobQueue>(sp => sp.GetRequiredService<InMemoryJobQueue>());
		services.AddSingleton<IContextJobQueryStore>(sp => sp.GetRequiredService<InMemoryJobQueue>());
	}
}


