using ContextCore.Abstractions;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;

namespace ContextCore.Service.Extensions;

/// <summary>存储层 DI 注册扩展，根据 <see cref="StorageOptions.Provider"/> 切换实现。</summary>
internal static class StorageExtensions
{
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
			ConnectionString = options.ResolvedPostgresConnectionString,
			AutoMigrate = true,
			EnablePgVectorExtension = true,
		};

		services.AddContextCorePostgresStorage(pgOptions);
	}

	private static void RegisterFileSystem(IServiceCollection services, StorageOptions options)
	{
		// 使用 ResolvedRootPath：展开环境变量并转为绝对路径，确保与 AppHost/ControlRoom 指向同一目录
		var fsOptions = new FileStorageOptions { RootPath = options.ResolvedRootPath };
		Directory.CreateDirectory(fsOptions.ResolvedRootPath);

		services.AddSingleton(fsOptions);
		// FilePathResolver / FileFormatSerializer 各只有一个构造函数，DI 可直接解析
		services.AddSingleton<FilePathResolver>();
		services.AddSingleton<FileFormatSerializer>();
		services.AddSingleton<FileJsonLineStore>();

		// 各 File*Store 存在两个构造函数（DI 注入版 + 直接 new 版），需通过工厂 lambda
		// 显式指定使用 (FilePathResolver, FileFormatSerializer) 版本，避免 DI 容器歧义
		services.AddSingleton<FileContextStore>(sp =>
			new FileContextStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextStore>(sp => sp.GetRequiredService<FileContextStore>());
		services.AddSingleton<IContextCollectionStore>(sp => sp.GetRequiredService<FileContextStore>());

		services.AddSingleton<FileContextIndex>(sp =>
			new FileContextIndex(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextIndex>(sp => sp.GetRequiredService<FileContextIndex>());

		services.AddSingleton<FileVectorStore>(sp =>
			new FileVectorStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<FileVectorStore>());

		services.AddSingleton<FileContextPackageBuildTraceStore>(sp =>
			new FileContextPackageBuildTraceStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextPackageBuildTraceStore>(sp =>
			sp.GetRequiredService<FileContextPackageBuildTraceStore>());
		services.AddSingleton<FileContextPackagePolicyStore>(sp =>
			new FileContextPackagePolicyStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IContextPackagePolicyStore>(sp =>
			sp.GetRequiredService<FileContextPackagePolicyStore>());

		services.AddSingleton<FileRetrievalTraceStore>(sp =>
			new FileRetrievalTraceStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IRetrievalTraceStore>(sp =>
			sp.GetRequiredService<FileRetrievalTraceStore>());

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
        services.AddSingleton<IShortTermPromotionCandidateStore>(sp => sp.GetRequiredService<FileShortTermPromotionCandidateStore>());
        services.AddSingleton<FileContextLearningStore>(sp =>
            new FileContextLearningStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IContextLearningStore>(sp => sp.GetRequiredService<FileContextLearningStore>());
        services.AddSingleton<FileStableReviewCandidateStore>(sp =>
            new FileStableReviewCandidateStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IStableReviewCandidateStore>(sp => sp.GetRequiredService<FileStableReviewCandidateStore>());
        services.AddSingleton<FileConstraintGapCandidateStore>(sp =>
            new FileConstraintGapCandidateStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IConstraintGapCandidateStore>(sp => sp.GetRequiredService<FileConstraintGapCandidateStore>());
        services.AddSingleton<FileCandidateConstraintReviewStore>(sp =>
            new FileCandidateConstraintReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<ICandidateConstraintReviewStore>(sp => sp.GetRequiredService<FileCandidateConstraintReviewStore>());
        services.AddSingleton<FileCandidateMemoryReviewStore>(sp =>
            new FileCandidateMemoryReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<ICandidateMemoryReviewStore>(sp => sp.GetRequiredService<FileCandidateMemoryReviewStore>());
        services.AddSingleton<FileStableLifecycleReviewStore>(sp =>
            new FileStableLifecycleReviewStore(
                sp.GetRequiredService<FilePathResolver>(),
                sp.GetRequiredService<FileFormatSerializer>()));
        services.AddSingleton<IStableLifecycleReviewStore>(sp => sp.GetRequiredService<FileStableLifecycleReviewStore>());

		services.AddSingleton<FileMemoryStore>(sp =>
			new FileMemoryStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<FileMemoryStore>());
		services.AddSingleton<IWorkingMemoryService>(sp => sp.GetRequiredService<FileMemoryStore>());
		services.AddSingleton<IPromotionRecordStore>(sp => sp.GetRequiredService<FileMemoryStore>());
		services.AddSingleton<IPromotionCandidateStore>(sp => sp.GetRequiredService<FileMemoryStore>());

		services.AddSingleton<FileConstraintStore>(sp =>
			new FileConstraintStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IConstraintStore>(sp => sp.GetRequiredService<FileConstraintStore>());

		services.AddSingleton<FileRelationStore>(sp =>
			new FileRelationStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IRelationStore>(sp => sp.GetRequiredService<FileRelationStore>());

		services.AddSingleton<FileGlobalContextStore>(sp =>
			new FileGlobalContextStore(
				sp.GetRequiredService<FilePathResolver>(),
				sp.GetRequiredService<FileFormatSerializer>()));
		services.AddSingleton<IGlobalContextStore>(sp => sp.GetRequiredService<FileGlobalContextStore>());

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

	/// <summary>
	/// 注册内存存储服务到依赖注入容器中。
	/// 此方法主要用于测试环境，通过添加一系列基于内存的实现来模拟存储服务。
	/// </summary>
	/// <param name="services">IServiceCollection 对象，用于配置和注册服务。</param>
	private static void RegisterInMemory(IServiceCollection services)
	{
		services.AddSingleton<InMemoryContextStore>();
		services.AddSingleton<IContextStore>(sp => sp.GetRequiredService<InMemoryContextStore>());
		services.AddSingleton<IContextCollectionStore>(sp => sp.GetRequiredService<InMemoryContextStore>());

		services.AddSingleton<InMemoryContextIndex>();
		services.AddSingleton<IContextIndex>(sp => sp.GetRequiredService<InMemoryContextIndex>());
        services.AddSingleton<InMemoryShortTermMemoryStore>();
        services.AddSingleton<IShortTermMemoryStore>(sp => sp.GetRequiredService<InMemoryShortTermMemoryStore>());
        services.AddSingleton<InMemoryShortTermPromotionCandidateStore>();
        services.AddSingleton<IShortTermPromotionCandidateStore>(sp => sp.GetRequiredService<InMemoryShortTermPromotionCandidateStore>());
        services.AddSingleton<InMemoryContextLearningStore>();
        services.AddSingleton<IContextLearningStore>(sp => sp.GetRequiredService<InMemoryContextLearningStore>());
        services.AddSingleton<InMemoryStableReviewCandidateStore>();
        services.AddSingleton<IStableReviewCandidateStore>(sp => sp.GetRequiredService<InMemoryStableReviewCandidateStore>());
        services.AddSingleton<InMemoryConstraintGapCandidateStore>();
        services.AddSingleton<IConstraintGapCandidateStore>(sp => sp.GetRequiredService<InMemoryConstraintGapCandidateStore>());
        services.AddSingleton<InMemoryCandidateConstraintReviewStore>();
        services.AddSingleton<ICandidateConstraintReviewStore>(sp => sp.GetRequiredService<InMemoryCandidateConstraintReviewStore>());
        services.AddSingleton<InMemoryCandidateMemoryReviewStore>();
        services.AddSingleton<ICandidateMemoryReviewStore>(sp => sp.GetRequiredService<InMemoryCandidateMemoryReviewStore>());
        services.AddSingleton<InMemoryStableLifecycleReviewStore>();
        services.AddSingleton<IStableLifecycleReviewStore>(sp => sp.GetRequiredService<InMemoryStableLifecycleReviewStore>());

		services.AddSingleton<InMemoryVectorStore>();
		services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<InMemoryVectorStore>());

		services.AddSingleton<InMemoryRetrievalTraceStore>();
		services.AddSingleton<IRetrievalTraceStore>(sp => sp.GetRequiredService<InMemoryRetrievalTraceStore>());
		services.AddSingleton<InMemoryContextPackagePolicyStore>();
		services.AddSingleton<IContextPackagePolicyStore>(sp => sp.GetRequiredService<InMemoryContextPackagePolicyStore>());

		services.AddSingleton<InMemoryMemoryStore>();
		services.AddSingleton<IMemoryStore>(sp => sp.GetRequiredService<InMemoryMemoryStore>());
		services.AddSingleton<IWorkingMemoryService>(sp => sp.GetRequiredService<InMemoryMemoryStore>());
		services.AddSingleton<IPromotionRecordStore>(sp => sp.GetRequiredService<InMemoryMemoryStore>());
		services.AddSingleton<IPromotionCandidateStore>(sp => sp.GetRequiredService<InMemoryMemoryStore>());

		services.AddSingleton<InMemoryConstraintStore>();
		services.AddSingleton<IConstraintStore>(sp => sp.GetRequiredService<InMemoryConstraintStore>());

		services.AddSingleton<InMemoryRelationStore>();
		services.AddSingleton<IRelationStore>(sp => sp.GetRequiredService<InMemoryRelationStore>());

		services.AddSingleton<InMemoryGlobalContextStore>();
		services.AddSingleton<IGlobalContextStore>(sp => sp.GetRequiredService<InMemoryGlobalContextStore>());

		services.AddSingleton<InMemoryJobQueue>();
		services.AddSingleton<IContextJobQueue>(sp => sp.GetRequiredService<InMemoryJobQueue>());
		services.AddSingleton<IContextJobQueryStore>(sp => sp.GetRequiredService<InMemoryJobQueue>());
	}
}


