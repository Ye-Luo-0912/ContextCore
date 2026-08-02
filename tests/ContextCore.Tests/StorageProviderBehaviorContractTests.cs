using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// 验证三套存储 Provider 的行为契约：构建 ServiceProvider 并解析服务，
/// 确保原生实现可以真正被构造（而非仅存在 descriptor），且 Unsupported 占位在调用方法时抛出 NotSupportedException。
/// </summary>
[TestClass]
[TestCategory("Storage")]
public sealed class StorageProviderBehaviorContractTests
{
    // FileSystem 原生注册的接口（全部为真实实现，无 Unsupported 占位）
    private static readonly Type[] FileSystemNativeInterfaces = new[]
    {
        typeof(IContextStore),
        typeof(IContextCollectionStore),
        typeof(IContextIndex),
        typeof(IVectorStore),
        typeof(IVectorIndexStore),
        typeof(IVectorReindexReportStore),
        typeof(IVectorLifecycleMetadataReviewCandidateStore),
        typeof(IVectorLifecycleMetadataReviewStore),
        typeof(IVectorLifecycleSidecarMetadataStore),
        typeof(IContextPackagePolicyStore),
        typeof(IRetrievalTraceStore),
        typeof(IDecisionTraceStore),
        typeof(IShortTermMemoryStore),
        typeof(IShortTermPromotionCandidateStore),
        typeof(IContextLearningStore),
        typeof(ILearningFeedbackStore),
        typeof(ILearningFeedbackReviewStore),
        typeof(IStableReviewCandidateStore),
        typeof(IConstraintGapCandidateStore),
        typeof(ICandidateConstraintReviewStore),
        typeof(ICandidateMemoryReviewStore),
        typeof(IStableLifecycleReviewStore),
        typeof(IRelationReviewStore),
        typeof(IMemoryStore),
        typeof(IWorkingMemoryService),
        typeof(IPromotionRecordStore),
        typeof(IPromotionCandidateStore),
        typeof(IConstraintStore),
        typeof(IRelationStore),
        typeof(IGlobalContextStore),
        typeof(IContextJobQueue),
        typeof(IContextJobQueryStore),
        typeof(IArtifactStore),
        typeof(IContextPathResolver),
        typeof(IContextPackageBuildTraceStore),
    };

    // InMemory 原生注册的接口（除 IArtifactStore 外全部为真实实现）
    private static readonly Type[] InMemoryNativeInterfaces = new[]
    {
        typeof(IContextStore),
        typeof(IContextCollectionStore),
        typeof(IContextIndex),
        typeof(IVectorStore),
        typeof(IVectorIndexStore),
        typeof(IVectorReindexReportStore),
        typeof(IVectorLifecycleMetadataReviewCandidateStore),
        typeof(IVectorLifecycleMetadataReviewStore),
        typeof(IVectorLifecycleSidecarMetadataStore),
        typeof(IContextPackagePolicyStore),
        typeof(IRetrievalTraceStore),
        typeof(IDecisionTraceStore),
        typeof(IShortTermMemoryStore),
        typeof(IShortTermPromotionCandidateStore),
        typeof(IContextLearningStore),
        typeof(ILearningFeedbackStore),
        typeof(ILearningFeedbackReviewStore),
        typeof(IStableReviewCandidateStore),
        typeof(IConstraintGapCandidateStore),
        typeof(ICandidateConstraintReviewStore),
        typeof(ICandidateMemoryReviewStore),
        typeof(IStableLifecycleReviewStore),
        typeof(IRelationReviewStore),
        typeof(IMemoryStore),
        typeof(IWorkingMemoryService),
        typeof(IPromotionRecordStore),
        typeof(IPromotionCandidateStore),
        typeof(IConstraintStore),
        typeof(IRelationStore),
        typeof(IGlobalContextStore),
        typeof(IContextJobQueue),
        typeof(IContextJobQueryStore),
    };

    // Postgres 原生注册的接口（R14-PG-5 完成后无 Unsupported 占位）
    // 新增 ILearningFeedbackStore / ILearningFeedbackReviewStore
    // 新增 IDecisionTraceStore
    // 新增 IShortTermMemoryStore / IShortTermPromotionCandidateStore / ICandidateMemoryReviewStore / IStableReviewCandidateStore
    // 新增 IContextLearningStore / IStableLifecycleReviewStore / ICandidateConstraintReviewStore / IConstraintGapCandidateStore
    // 新增 IVectorReindexReportStore / IVectorLifecycleMetadataReviewCandidateStore / IVectorLifecycleMetadataReviewStore / IVectorLifecycleSidecarMetadataStore / IArtifactStore
    private static readonly Type[] PostgresNativeInterfaces = new[]
    {
        typeof(IContextStore),
        typeof(IContextCollectionStore),
        typeof(IContextIndex),
        typeof(IMemoryStore),
        typeof(IWorkingMemoryService),
        typeof(IPromotionRecordStore),
        typeof(IPromotionCandidateStore),
        typeof(IRelationStore),
        typeof(IRelationReviewStore),
        typeof(IConstraintStore),
        typeof(IGlobalContextStore),
        typeof(IVectorStore),
        typeof(IVectorIndexStore),
        typeof(IRetrievalTraceStore),
        typeof(IDecisionTraceStore),
        typeof(IContextPackageBuildTraceStore),
        typeof(IContextPackagePolicyStore),
        typeof(IContextJobQueue),
        typeof(IContextJobQueryStore),
        typeof(IContextEventSink),
        typeof(ILearningFeedbackStore),
        typeof(ILearningFeedbackReviewStore),
        typeof(IShortTermMemoryStore),
        typeof(IShortTermPromotionCandidateStore),
        typeof(ICandidateMemoryReviewStore),
        typeof(IStableReviewCandidateStore),
        typeof(IContextLearningStore),
        typeof(IStableLifecycleReviewStore),
        typeof(ICandidateConstraintReviewStore),
        typeof(IConstraintGapCandidateStore),
        typeof(IVectorReindexReportStore),
        typeof(IVectorLifecycleMetadataReviewCandidateStore),
        typeof(IVectorLifecycleMetadataReviewStore),
        typeof(IVectorLifecycleSidecarMetadataStore),
        typeof(IArtifactStore),
    };

    private static bool IsUnsupportedPlaceholder(object instance)
    {
        var typeName = instance.GetType().Name;
        return typeName.StartsWith("Unsupported", StringComparison.Ordinal)
            && typeName.EndsWith("Store", StringComparison.Ordinal);
    }

    [TestMethod]
    public void FileSystem_NativeServicesCanBeResolvedFromServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        services.AddSingleton(new RelationGovernanceProviderSwitchOptions());
        var rootPath = Path.Combine(Path.GetTempPath(), "ctx-bc-" + Guid.NewGuid().ToString("N"));
        var options = new StorageOptions { Provider = "filesystem", RootPath = rootPath };
        services.AddContextStorage(options);

        using var sp = services.BuildServiceProvider();
        foreach (var iface in FileSystemNativeInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"FileSystem 无法从 ServiceProvider 解析接口: {iface.Name}");
            Assert.IsFalse(IsUnsupportedPlaceholder(service),
                $"FileSystem 接口 {iface.Name} 不应是 Unsupported 占位");
        }
    }

    [TestMethod]
    public void InMemory_NativeServicesCanBeResolvedFromServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        var options = new StorageOptions { Provider = "memory" };
        services.AddContextStorage(options);
        services.AddSingleton<IArtifactStore>(_ => new UnsupportedArtifactStore("memory"));

        using var sp = services.BuildServiceProvider();
        foreach (var iface in InMemoryNativeInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"InMemory 无法从 ServiceProvider 解析接口: {iface.Name}");
            Assert.IsFalse(IsUnsupportedPlaceholder(service),
                $"InMemory 接口 {iface.Name} 不应是 Unsupported 占位");
        }
    }

    [TestMethod]
    public void InMemory_UnsupportedArtifactStore_ThrowsNotSupportedExceptionOnUse()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        var options = new StorageOptions { Provider = "memory" };
        services.AddContextStorage(options);
        services.AddSingleton<IArtifactStore>(_ => new UnsupportedArtifactStore("memory"));

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IArtifactStore>();
        Assert.IsTrue(IsUnsupportedPlaceholder(store), "IArtifactStore 应为 Unsupported 占位");

        var ex = Assert.ThrowsException<NotSupportedException>(() =>
            store.WriteMarkdownAsync(new ArtifactDescriptor(), "test", default).GetAwaiter().GetResult());
        Assert.IsTrue(ex.Message.Contains("memory"), $"异常消息应包含 provider 名称 'memory'，实际: {ex.Message}");
    }

    [TestMethod]
    public async Task Postgres_NativeServicesCanBeResolvedFromServiceProvider()
    {
        // Postgres 的 PostgresConnectionFactory 不会在 BuildServiceProvider 时创建 NpgsqlDataSource，
        // 只有在 store 方法被调用时才会尝试连接。因此解析服务本身是安全的。
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions
        {
            Provider = "postgres",
            PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
        };
        services.AddContextStorage(options);

        await using var sp = services.BuildServiceProvider();
        foreach (var iface in PostgresNativeInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"Postgres 无法从 ServiceProvider 解析原生接口: {iface.Name}");
            Assert.IsFalse(IsUnsupportedPlaceholder(service),
                $"Postgres 原生接口 {iface.Name} 不应是 Unsupported 占位，实际类型: {service.GetType().Name}");
        }
    }

    /// <summary>
    /// 垂直闭环完成 sanity check。
    /// 替代已删除的 Postgres_UnsupportedStore_ThrowsNotSupportedExceptionOnUse 与 Postgres_All5UnsupportedStores_ThrowNotSupportedException，
    /// 断言 R14-PG-5 新绑定的 5 个接口在 Postgres provider 下均为原生实现，不再是 Unsupported 占位。
    /// </summary>
    [TestMethod]
    public async Task Postgres_NoUnsupportedStoresRemain()
    {
        // 垂直闭环完成，Postgres 无 Unsupported 占位。
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions
        {
            Provider = "postgres",
            PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
        };
        services.AddContextStorage(options);

        await using var sp = services.BuildServiceProvider();
        var recentlyBound = new[]
        {
            typeof(IVectorReindexReportStore),
            typeof(IVectorLifecycleMetadataReviewCandidateStore),
            typeof(IVectorLifecycleMetadataReviewStore),
            typeof(IVectorLifecycleSidecarMetadataStore),
            typeof(IArtifactStore),
        };

        foreach (var iface in recentlyBound)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"Postgres 未注册接口: {iface.Name}");
            Assert.IsFalse(IsUnsupportedPlaceholder(service),
                $"Postgres 接口 {iface.Name} 不应是 Unsupported 占位，实际类型: {service.GetType().Name}");
        }
    }

    /// <summary>
    /// 三套 provider 的 IContextStore 实例都应同时实现 IContextStoreBatchLookup，
    /// 让 Retrieval Mandatory 通道走单次 BatchGetAsync 而非 N 次并行 GetAsync。
    /// 通过 is 运算符验证 cast 路径与 RetrievalChannelExecutors 的运行时检测一致。
    /// </summary>
    [DataTestMethod]
    [DataRow("filesystem", DisplayName = "FileSystem")]
    [DataRow("memory", DisplayName = "InMemory")]
    [DataRow("postgres", DisplayName = "Postgres")]
    public async Task ContextStore_AllProvidersImplementBatchLookup(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        services.AddSingleton(new RelationGovernanceProviderSwitchOptions());

        if (provider == "filesystem")
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ctx-bc-batch-" + Guid.NewGuid().ToString("N"));
            services.AddContextStorage(new StorageOptions { Provider = provider, RootPath = rootPath });
        }
        else if (provider == "memory")
        {
            services.AddContextStorage(new StorageOptions { Provider = provider });
        }
        else
        {
            services.AddContextStorage(new StorageOptions
            {
                Provider = provider,
                PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
            });
        }

        // PostgresConnectionFactory 实现 IAsyncDisposable，必须使用 await using 才能正确释放。
        await using var sp = services.BuildServiceProvider();
        var contextStore = sp.GetRequiredService<IContextStore>();
        Assert.IsNotNull(contextStore);
        Assert.IsFalse(IsUnsupportedPlaceholder(contextStore), $"{provider} IContextStore 不应是 Unsupported 占位");

        // 关键断言 — IContextStore 实例必须同时实现 IContextStoreBatchLookup
        Assert.IsTrue(contextStore is IContextStoreBatchLookup,
            $"{provider} IContextStore 应实现 IContextStoreBatchLookup 以启用 Retrieval 批量查询路径");
    }

    /// <summary>
    /// 三套 provider 的 IMemoryStore 实例都应同时实现 IMemoryStoreBatchLookup。
    /// </summary>
    [DataTestMethod]
    [DataRow("filesystem", DisplayName = "FileSystem")]
    [DataRow("memory", DisplayName = "InMemory")]
    [DataRow("postgres", DisplayName = "Postgres")]
    public async Task MemoryStore_AllProvidersImplementBatchLookup(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        services.AddSingleton(new RelationGovernanceProviderSwitchOptions());

        if (provider == "filesystem")
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ctx-bc-batch-mem-" + Guid.NewGuid().ToString("N"));
            services.AddContextStorage(new StorageOptions { Provider = provider, RootPath = rootPath });
        }
        else if (provider == "memory")
        {
            services.AddContextStorage(new StorageOptions { Provider = provider });
        }
        else
        {
            services.AddContextStorage(new StorageOptions
            {
                Provider = provider,
                PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
            });
        }

        // PostgresConnectionFactory 实现 IAsyncDisposable，必须使用 await using 才能正确释放。
        await using var sp = services.BuildServiceProvider();
        var memoryStore = sp.GetRequiredService<IMemoryStore>();
        Assert.IsNotNull(memoryStore);
        Assert.IsFalse(IsUnsupportedPlaceholder(memoryStore), $"{provider} IMemoryStore 不应是 Unsupported 占位");

        // 关键断言 — IMemoryStore 实例必须同时实现 IMemoryStoreBatchLookup
        Assert.IsTrue(memoryStore is IMemoryStoreBatchLookup,
            $"{provider} IMemoryStore 应实现 IMemoryStoreBatchLookup 以启用 Retrieval 批量查询路径");
    }
}
