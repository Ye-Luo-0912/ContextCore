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

    // Postgres 原生注册的接口（不含 17 个 Unsupported 占位）
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
        typeof(IContextPackageBuildTraceStore),
        typeof(IContextPackagePolicyStore),
        typeof(IContextJobQueue),
        typeof(IContextJobQueryStore),
        typeof(IContextEventSink),
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

    [TestMethod]
    public async Task Postgres_UnsupportedStore_ThrowsNotSupportedExceptionOnUse()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions
        {
            Provider = "postgres",
            PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
        };
        services.AddContextStorage(options);

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IShortTermMemoryStore>();
        Assert.IsTrue(IsUnsupportedPlaceholder(store), "IShortTermMemoryStore 应为 Unsupported 占位");

        Assert.ThrowsException<NotSupportedException>(() =>
            store.AppendRawEventAsync(new ShortTermRawEvent(), default).GetAwaiter().GetResult());
    }
}
