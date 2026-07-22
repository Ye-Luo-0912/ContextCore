using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Runtime;
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

	// 统一注册帮助器：消除 60+ 次 (impl 注册 + Decorator/转发) 样板，避免运行时反射代理。
	// DecoratorFactory/ImplFactory 均为显式 lambda，编译期类型安全，与手写代码等价。

	/// <summary>注册 TImpl 默认 DI 构造 + Decorator 包装为 TService。</summary>
	private static IServiceCollection AddInvalidating<TService, TImpl>(
		this IServiceCollection services,
		Func<TImpl, IStateCacheInvalidator, IContextStateVersionStore?, TService> decoratorFactory)
		where TService : class
		where TImpl : class
	{
		services.AddSingleton<TImpl>();
		services.AddSingleton<TService>(sp =>
			decoratorFactory(sp.GetRequiredService<TImpl>(), GetInvalidator(sp), GetVersionStore(sp)));
		return services;
	}

	/// <summary>注册 TImpl 工厂 + Decorator 包装为 TService（用于需要 DI 参数构造的 File* 实现）。</summary>
	private static IServiceCollection AddInvalidating<TService, TImpl>(
		this IServiceCollection services,
		Func<IServiceProvider, TImpl> implFactory,
		Func<TImpl, IStateCacheInvalidator, IContextStateVersionStore?, TService> decoratorFactory)
		where TService : class
		where TImpl : class
	{
		services.AddSingleton(implFactory);
		services.AddSingleton<TService>(sp =>
			decoratorFactory(sp.GetRequiredService<TImpl>(), GetInvalidator(sp), GetVersionStore(sp)));
		return services;
	}

	/// <summary>仅注册 Decorator 服务（TImpl 已通过前一次 AddInvalidating 注册，用于多 service 共享 impl）。</summary>
	private static IServiceCollection AddDecoratedService<TService, TImpl>(
		this IServiceCollection services,
		Func<TImpl, IStateCacheInvalidator, IContextStateVersionStore?, TService> decoratorFactory)
		where TService : class
		where TImpl : class
	{
		services.AddSingleton<TService>(sp =>
			decoratorFactory(sp.GetRequiredService<TImpl>(), GetInvalidator(sp), GetVersionStore(sp)));
		return services;
	}

	/// <summary>注册 TImpl 默认 DI 构造 + 直接转发为 TService（无 Decorator）。</summary>
	private static IServiceCollection AddPlain<TService, TImpl>(this IServiceCollection services)
		where TService : class
		where TImpl : class, TService
	{
		services.AddSingleton<TImpl>();
		services.AddSingleton<TService>(sp => sp.GetRequiredService<TImpl>());
		return services;
	}

	/// <summary>注册 TImpl 工厂 + 直接转发为 TService（无 Decorator，用于 File* 实现）。</summary>
	private static IServiceCollection AddPlain<TService, TImpl>(
		this IServiceCollection services,
		Func<IServiceProvider, TImpl> implFactory)
		where TService : class
		where TImpl : class, TService
	{
		services.AddSingleton(implFactory);
		services.AddSingleton<TService>(sp => sp.GetRequiredService<TImpl>());
		return services;
	}

	/// <summary>仅注册转发服务（TImpl 已注册，多 service 共享 impl 时使用）。</summary>
	private static IServiceCollection AddForwardedService<TService, TImpl>(this IServiceCollection services)
		where TService : class
		where TImpl : class, TService
	{
		services.AddSingleton<TService>(sp => sp.GetRequiredService<TImpl>());
		return services;
	}

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
			RegisterCapabilities(services, StorageProviderKind.FileSystem);
		}
		else if (options.IsMemory)
		{
			RegisterInMemory(services);
			RegisterCapabilities(services, StorageProviderKind.InMemory);
		}
		else if (options.IsPostgres)
		{
			RegisterPostgres(services, options);
			RegisterCapabilities(services, StorageProviderKind.Postgres);
		}
		else
		{
			throw new InvalidOperationException(
				$"未知存储提供商 '{options.Provider}'。支持的 provider: filesystem, memory, postgres。");
		}

		// R28-B.6 阶段 E：按 provider 注册 IExperimentRecorder。
		// 显式 env CC_EXPERIMENT_RECORDER_BACKEND 可覆盖默认选择（memory/filesystem/postgres）。
		// 未注入时 CoreExtensions 的 TryAddSingleton<IExperimentRecorder, InMemoryExperimentRecorder> 回退。
		RegisterExperimentRecorder(services, options);

		return services;
	}

	/// <summary>
	/// R28-B.6 阶段 E：按 storage provider 注册 IExperimentRecorder。
	/// 默认映射：postgres → PostgresExperimentRecorder，filesystem → FileSystemExperimentRecorder，memory → 不注册（回退到 InMemory）。
	/// 显式 env CC_EXPERIMENT_RECORDER_BACKEND 可覆盖：memory=强制 InMemory，filesystem=强制 FileSystem，postgres=强制 Postgres。
	/// </summary>
	/// <remarks>
	/// 注册顺序：StorageExtensions.AddContextStorage 在 AddContextCore 之前调用，
	/// CoreExtensions 的 TryAddSingleton<IExperimentRecorder, InMemoryExperimentRecorder> 是 no-op（已注册时跳过）。
	/// PostgresExperimentRecorder 依赖 PostgresConnectionFactory（仅 postgres provider 注册时可用）。
	/// FileSystemExperimentRecorder 依赖 rootPath（仅 filesystem provider 解析后可用）。
	/// </remarks>
	private static void RegisterExperimentRecorder(IServiceCollection services, StorageOptions options)
	{
		var backend = Environment.GetEnvironmentVariable("CC_EXPERIMENT_RECORDER_BACKEND");
		if (!string.IsNullOrWhiteSpace(backend))
		{
			backend = backend.Trim();
		}

		// 显式指定 memory：跳过注册，让 CoreExtensions 的 TryAddSingleton 回退到 InMemory
		if (string.Equals(backend, "memory", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		// 显式指定 filesystem：只要 rootPath 可解析就注册
		if (string.Equals(backend, "filesystem", StringComparison.OrdinalIgnoreCase))
		{
			RegisterFileSystemExperimentRecorder(services, options.ResolvedRootPath);
			return;
		}

		// 显式指定 postgres：要求 Postgres provider 基础设施可用
		if (string.Equals(backend, "postgres", StringComparison.OrdinalIgnoreCase))
		{
			RegisterPostgresExperimentRecorder(services);
			return;
		}

		// 未指定 backend：按 storage provider 默认选择
		if (options.IsPostgres)
		{
			RegisterPostgresExperimentRecorder(services);
		}
		else if (options.IsFileSystem)
		{
			RegisterFileSystemExperimentRecorder(services, options.ResolvedRootPath);
		}
		// memory provider 不注册，回退到 InMemoryExperimentRecorder
	}

	private static void RegisterPostgresExperimentRecorder(IServiceCollection services)
	{
		// PostgresConnectionFactory / PostgresJsonSerializer / PostgresMigrationRunner 已由
		// AddContextCorePostgresStorage（RegisterPostgres 路径）注册，DI 可直接解析。
		services.AddSingleton<IExperimentRecorder>(sp =>
		{
			var connectionFactory = sp.GetRequiredService<PostgresConnectionFactory>();
			var serializer = sp.GetRequiredService<PostgresJsonSerializer>();
			var migrationRunner = sp.GetRequiredService<PostgresMigrationRunner>();
			return new PostgresExperimentRecorder(connectionFactory, serializer, migrationRunner);
		});
	}

	private static void RegisterFileSystemExperimentRecorder(IServiceCollection services, string rootPath)
	{
		// fixture 文件落在 {rootPath}/experiment_fixtures/ 下，与 logs/ 平级。
		services.AddSingleton<IExperimentRecorder>(_ => new FileSystemExperimentRecorder(rootPath));
	}

	/// <summary>
	/// R13.3 #1：注册 IStoreRuntimeCapabilities 单例，按当前 provider 提供能力描述。
	/// 替代各处对 "filesystem"/"postgres"/"memory" 字符串的判断，调用方一次 DI 注入即可查询能力。
	/// </summary>
	private static void RegisterCapabilities(IServiceCollection services, StorageProviderKind providerKind)
	{
		services.AddSingleton<IStoreRuntimeCapabilities>(_ => new StoreRuntimeCapabilities(providerKind));
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
		// R14-PG-1：ILearningFeedbackStore / ILearningFeedbackReviewStore 已由 PostgresServiceCollectionExtensions
		// 正式绑定 Postgres 实现，不再需要 Unsupported 覆盖。
		// R14-PG-2：IDecisionTraceStore 已由 PostgresServiceCollectionExtensions 正式绑定 PostgresDecisionTraceStore。
		// R14-PG-3：IShortTermMemoryStore / IShortTermPromotionCandidateStore / ICandidateMemoryReviewStore / IStableReviewCandidateStore 已由 PostgresServiceCollectionExtensions 正式绑定 Postgres 实现。
		// R14-PG-4：IContextLearningStore / IStableLifecycleReviewStore / ICandidateConstraintReviewStore / IConstraintGapCandidateStore 已由 PostgresServiceCollectionExtensions 正式绑定 Postgres 实现。
		// R14-PG-5 完成：全部 16 个 Postgres store 已正式绑定原生实现，无 Unsupported 覆盖。

		// R10-2：在 Postgres 实现之上叠加失效边界 Decorator（覆盖 AddContextCorePostgresStorage 的原始注册）。
	// 失效 Decorator 位于最外层，写入成功后向 IStateCacheInvalidator 发出失效信号。
	// 仅保留 Data Plane Store 的 Decorator（读路径可能被缓存的 Store）。
		services.AddDecoratedService<IContextStore, PostgresContextStore>((inner, inv, vs) => new InvalidatingContextStoreDecorator(inner, inv, vs));
		services.AddDecoratedService<IContextIndex, PostgresContextIndex>((inner, inv, vs) => new InvalidatingContextIndexDecorator(inner, inv, vs));
		services.AddDecoratedService<IMemoryStore, PostgresMemoryStore>((inner, inv, vs) => new InvalidatingMemoryStoreDecorator(inner, inv, vs));
		services.AddDecoratedService<IConstraintStore, PostgresConstraintStore>((inner, inv, vs) => new InvalidatingConstraintStoreDecorator(inner, inv, vs));
		services.AddDecoratedService<IRelationStore, PostgresRelationStore>((inner, inv, vs) => new InvalidatingRelationStoreDecorator(inner, inv, vs));
		services.AddDecoratedService<IGlobalContextStore, PostgresGlobalContextStore>((inner, inv, vs) => new InvalidatingGlobalContextStoreDecorator(inner, inv, vs));

		services.AddDecoratedService<IWorkingMemoryService, PostgresWorkingMemoryStore>((inner, inv, vs) => new InvalidatingWorkingMemoryServiceDecorator(inner, inv, vs));
		services.AddDecoratedService<IVectorStore, PostgresVectorStore>((inner, inv, vs) => new InvalidatingVectorStoreDecorator(inner, inv, vs));

		// 非 Data Plane Store 直接转发，不叠加失效 Decorator（读路径未接入缓存）。
		services.AddForwardedService<IContextCollectionStore, PostgresContextStore>();
		services.AddForwardedService<IPromotionRecordStore, PostgresWorkingMemoryStore>();
		services.AddForwardedService<IPromotionCandidateStore, PostgresWorkingMemoryStore>();
		services.AddForwardedService<IRelationReviewStore, PostgresRelationReviewStore>();
		services.AddForwardedService<IContextPackageBuildTraceStore, PostgresContextPackageBuildTraceStore>();
		services.AddForwardedService<IContextPackagePolicyStore, PostgresContextPackagePolicyStore>();
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
		services.AddInvalidating<IContextStore, FileContextStore>(
			sp => new FileContextStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()),
			(inner, inv, vs) => new InvalidatingContextStoreDecorator(inner, inv, vs));
		services.AddForwardedService<IContextCollectionStore, FileContextStore>();

		services.AddInvalidating<IContextIndex, FileContextIndex>(
			sp => new FileContextIndex(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()),
			(inner, inv, vs) => new InvalidatingContextIndexDecorator(inner, inv, vs));

		services.AddInvalidating<IVectorStore, FileVectorStore>(
			sp => new FileVectorStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()),
			(inner, inv, vs) => new InvalidatingVectorStoreDecorator(inner, inv, vs));

		services.AddPlain<IVectorIndexStore, FileVectorIndexStore>(
			sp => new FileVectorIndexStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<IVectorReindexReportStore, FileVectorReindexReportStore>(
			sp => new FileVectorReindexReportStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<IVectorLifecycleMetadataReviewCandidateStore, FileVectorLifecycleMetadataReviewCandidateStore>(
			sp => new FileVectorLifecycleMetadataReviewCandidateStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<IVectorLifecycleMetadataReviewStore, FileVectorLifecycleMetadataReviewStore>(
			sp => new FileVectorLifecycleMetadataReviewStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<IVectorLifecycleSidecarMetadataStore, FileVectorLifecycleSidecarMetadataStore>(
			sp => new FileVectorLifecycleSidecarMetadataStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));

		services.AddPlain<IContextPackageBuildTraceStore, FileContextPackageBuildTraceStore>(
			sp => new FileContextPackageBuildTraceStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<IContextPackagePolicyStore, FileContextPackagePolicyStore>(
			sp => new FileContextPackagePolicyStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));

		services.AddPlain<IRetrievalTraceStore, FileRetrievalTraceStore>(
			sp => new FileRetrievalTraceStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));

		services.AddPlain<IDecisionTraceStore, FileDecisionTraceStore>(
			sp => new FileDecisionTraceStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));

        // FileShortTermMemoryStore 需要额外 ShortTermMemoryPolicy 依赖，工厂参数特殊
        services.AddPlain<IShortTermMemoryStore, FileShortTermMemoryStore>(
            sp => new FileShortTermMemoryStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>(),
                sp.GetRequiredService<ShortTermMemoryPolicy>()));
        services.AddPlain<IShortTermPromotionCandidateStore, FileShortTermPromotionCandidateStore>(
            sp => new FileShortTermPromotionCandidateStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<IContextLearningStore, FileContextLearningStore>(
			sp => new FileContextLearningStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<ILearningFeedbackStore, FileLearningFeedbackStore>(
			sp => new FileLearningFeedbackStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddPlain<ILearningFeedbackReviewStore, FileLearningFeedbackReviewStore>(
			sp => new FileLearningFeedbackReviewStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddPlain<IStableReviewCandidateStore, FileStableReviewCandidateStore>(
            sp => new FileStableReviewCandidateStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddPlain<IConstraintGapCandidateStore, FileConstraintGapCandidateStore>(
            sp => new FileConstraintGapCandidateStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddPlain<ICandidateConstraintReviewStore, FileCandidateConstraintReviewStore>(
            sp => new FileCandidateConstraintReviewStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddPlain<ICandidateMemoryReviewStore, FileCandidateMemoryReviewStore>(
            sp => new FileCandidateMemoryReviewStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddPlain<IStableLifecycleReviewStore, FileStableLifecycleReviewStore>(
            sp => new FileStableLifecycleReviewStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<FileRelationReviewStore>(sp => new FileRelationReviewStore(
            sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<FileRelationDiagnosticsStore>(sp => new FileRelationDiagnosticsStore(
            sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IRelationReviewStore>(sp =>
        {
            var switchOptions = sp.GetService<RelationGovernanceProviderSwitchOptions>() ?? new RelationGovernanceProviderSwitchOptions();
            return !switchOptions.Enabled
                ? sp.GetRequiredService<FileRelationReviewStore>()
                : new ScopedRelationGovernanceReviewStore(
                    sp.GetRequiredService<FileRelationReviewStore>(),
                    sp.GetRequiredService<PostgresRelationReviewStore>(),
                    switchOptions,
                    sp.GetRequiredService<RelationGovernanceScopedServiceModeStatusService>());
        });

		services.AddInvalidating<IMemoryStore, FileMemoryStore>(
			sp => new FileMemoryStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()),
			(inner, inv, vs) => new InvalidatingMemoryStoreDecorator(inner, inv, vs));
		services.AddDecoratedService<IWorkingMemoryService, FileMemoryStore>((inner, inv, vs) => new InvalidatingWorkingMemoryServiceDecorator(inner, inv, vs));
		services.AddForwardedService<IPromotionRecordStore, FileMemoryStore>();
		services.AddForwardedService<IPromotionCandidateStore, FileMemoryStore>();

		services.AddInvalidating<IConstraintStore, FileConstraintStore>(
			sp => new FileConstraintStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()),
			(inner, inv, vs) => new InvalidatingConstraintStoreDecorator(inner, inv, vs));

		services.AddSingleton<FileRelationStore>(sp => new FileRelationStore(
			sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
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

		services.AddInvalidating<IGlobalContextStore, FileGlobalContextStore>(
			sp => new FileGlobalContextStore(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()),
			(inner, inv, vs) => new InvalidatingGlobalContextStoreDecorator(inner, inv, vs));

		services.AddPlain<IContextJobQueue, FileContextJobQueue>(
			sp => new FileContextJobQueue(sp.GetRequiredService<FilePathResolver>(), sp.GetRequiredService<FileFormatSerializer>()));
		services.AddForwardedService<IContextJobQueryStore, FileContextJobQueue>();

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
		services.AddInvalidating<IContextStore, InMemoryContextStore>((inner, inv, vs) => new InvalidatingContextStoreDecorator(inner, inv, vs));
		services.AddForwardedService<IContextCollectionStore, InMemoryContextStore>();
		services.AddInvalidating<IContextIndex, InMemoryContextIndex>((inner, inv, vs) => new InvalidatingContextIndexDecorator(inner, inv, vs));
        services.AddPlain<IShortTermMemoryStore, InMemoryShortTermMemoryStore>();
        services.AddPlain<IShortTermPromotionCandidateStore, InMemoryShortTermPromotionCandidateStore>();
        services.AddPlain<IContextLearningStore, InMemoryContextLearningStore>();
        services.AddPlain<ILearningFeedbackStore, InMemoryLearningFeedbackStore>();
        services.AddPlain<ILearningFeedbackReviewStore, InMemoryLearningFeedbackReviewStore>();
        services.AddPlain<IStableReviewCandidateStore, InMemoryStableReviewCandidateStore>();
        services.AddPlain<IConstraintGapCandidateStore, InMemoryConstraintGapCandidateStore>();
        services.AddPlain<ICandidateConstraintReviewStore, InMemoryCandidateConstraintReviewStore>();
        services.AddPlain<ICandidateMemoryReviewStore, InMemoryCandidateMemoryReviewStore>();
        services.AddPlain<IStableLifecycleReviewStore, InMemoryStableLifecycleReviewStore>();
        services.AddPlain<IRelationReviewStore, InMemoryRelationReviewStore>();

		services.AddInvalidating<IVectorStore, InMemoryVectorStore>((inner, inv, vs) => new InvalidatingVectorStoreDecorator(inner, inv, vs));
		services.AddPlain<IVectorIndexStore, InMemoryVectorIndexStore>();
		services.AddPlain<IVectorReindexReportStore, InMemoryVectorReindexReportStore>();
		services.AddPlain<IVectorLifecycleMetadataReviewCandidateStore, InMemoryVectorLifecycleMetadataReviewCandidateStore>();
		services.AddPlain<IVectorLifecycleMetadataReviewStore, InMemoryVectorLifecycleMetadataReviewStore>();
		services.AddPlain<IVectorLifecycleSidecarMetadataStore, InMemoryVectorLifecycleSidecarMetadataStore>();

		services.AddPlain<IRetrievalTraceStore, InMemoryRetrievalTraceStore>();
		services.AddPlain<IDecisionTraceStore, InMemoryDecisionTraceStore>();
		services.AddPlain<IContextPackagePolicyStore, InMemoryContextPackagePolicyStore>();

		services.AddInvalidating<IMemoryStore, InMemoryMemoryStore>((inner, inv, vs) => new InvalidatingMemoryStoreDecorator(inner, inv, vs));
		services.AddDecoratedService<IWorkingMemoryService, InMemoryMemoryStore>((inner, inv, vs) => new InvalidatingWorkingMemoryServiceDecorator(inner, inv, vs));
		services.AddForwardedService<IPromotionRecordStore, InMemoryMemoryStore>();
		services.AddForwardedService<IPromotionCandidateStore, InMemoryMemoryStore>();

		services.AddInvalidating<IConstraintStore, InMemoryConstraintStore>((inner, inv, vs) => new InvalidatingConstraintStoreDecorator(inner, inv, vs));
		services.AddInvalidating<IRelationStore, InMemoryRelationStore>((inner, inv, vs) => new InvalidatingRelationStoreDecorator(inner, inv, vs));
		services.AddInvalidating<IGlobalContextStore, InMemoryGlobalContextStore>((inner, inv, vs) => new InvalidatingGlobalContextStoreDecorator(inner, inv, vs));

		services.AddPlain<IContextJobQueue, InMemoryJobQueue>();
		services.AddForwardedService<IContextJobQueryStore, InMemoryJobQueue>();
	}
}


