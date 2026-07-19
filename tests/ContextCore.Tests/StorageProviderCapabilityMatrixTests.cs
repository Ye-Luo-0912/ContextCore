using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// 验证三套存储 Provider 的能力矩阵：区分原生实现（Native）与显式占位（Unsupported）。
/// 与 <see cref="StorageProviderRegistrationTests"/> 不同，这组测试构建 ServiceProvider 并解析服务，
/// 检查运行时类型是否为 Unsupported*Store 占位实现。
/// </summary>
[TestClass]
[TestCategory("Storage")]
public sealed class StorageProviderCapabilityMatrixTests
{
    // 三套 Provider 共同注册的 34 个存储契约接口
    private static readonly Type[] CommonInterfaces = new[]
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
    };

    // Postgres provider 显式注册为 Unsupported 占位的 13 个接口
    // R14-PG-1：ILearningFeedbackStore / ILearningFeedbackReviewStore 已正式绑定 Postgres 实现，移出此集合
    // R14-PG-2：IDecisionTraceStore 已正式绑定 Postgres 实现（PostgresDecisionTraceStore），移出此集合
    private static readonly HashSet<Type> PostgresDeclaredUnsupported = new()
    {
        typeof(IShortTermMemoryStore),
        typeof(IShortTermPromotionCandidateStore),
        typeof(ICandidateMemoryReviewStore),
        typeof(IStableReviewCandidateStore),
        typeof(IContextLearningStore),
        typeof(IVectorReindexReportStore),
        typeof(IVectorLifecycleMetadataReviewCandidateStore),
        typeof(IVectorLifecycleMetadataReviewStore),
        typeof(IVectorLifecycleSidecarMetadataStore),
        typeof(IArtifactStore),
        typeof(IStableLifecycleReviewStore),
        typeof(ICandidateConstraintReviewStore),
        typeof(IConstraintGapCandidateStore),
    };

    // FileSystem 和 InMemory 不应注册任何 Unsupported 占位
    // InMemory 的 IArtifactStore 由测试显式补充为 Unsupported，不计入 provider 自身声明

    /// <summary>
    /// 判断运行时实例是否为 Unsupported*Store 占位实现。
    /// </summary>
    private static bool IsUnsupportedPlaceholder(object instance)
    {
        var typeName = instance.GetType().Name;
        return typeName.StartsWith("Unsupported", StringComparison.Ordinal)
            && typeName.EndsWith("Store", StringComparison.Ordinal);
    }

    private static ServiceProvider BuildFileSystemProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        services.AddSingleton(new RelationGovernanceProviderSwitchOptions());
        var options = new StorageOptions { Provider = "filesystem", RootPath = Path.Combine(Path.GetTempPath(), "ctx-cm-" + Guid.NewGuid().ToString("N")) };
        services.AddContextStorage(options);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildInMemoryProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        var options = new StorageOptions { Provider = "memory" };
        services.AddContextStorage(options);
        services.AddSingleton<IArtifactStore>(_ => new UnsupportedArtifactStore("memory"));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildPostgresProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions
        {
            Provider = "postgres",
            PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
        };
        services.AddContextStorage(options);
        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void FileSystem_NoInterfaceShouldBeUnsupportedPlaceholder()
    {
        using var sp = BuildFileSystemProvider();
        foreach (var iface in CommonInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"FileSystem 未注册接口: {iface.Name}");
            Assert.IsFalse(IsUnsupportedPlaceholder(service),
                $"FileSystem 接口 {iface.Name} 不应是 Unsupported 占位，实际类型: {service.GetType().Name}");
        }
    }

    [TestMethod]
    public void InMemory_OnlyArtifactStoreShouldBeUnsupportedPlaceholder()
    {
        using var sp = BuildInMemoryProvider();
        foreach (var iface in CommonInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"InMemory 未注册接口: {iface.Name}");
            if (iface == typeof(IArtifactStore))
            {
                Assert.IsTrue(IsUnsupportedPlaceholder(service),
                    $"InMemory IArtifactStore 应为 Unsupported 占位，实际类型: {service.GetType().Name}");
            }
            else
            {
                Assert.IsFalse(IsUnsupportedPlaceholder(service),
                    $"InMemory 接口 {iface.Name} 不应是 Unsupported 占位，实际类型: {service.GetType().Name}");
            }
        }
    }

    [TestMethod]
    public async Task Postgres_UnsupportedSetShouldMatchDeclaredMatrix()
    {
        await using var sp = BuildPostgresProvider();
        var actualUnsupported = new HashSet<Type>();

        foreach (var iface in CommonInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"Postgres 未注册接口: {iface.Name}");
            if (IsUnsupportedPlaceholder(service))
            {
                actualUnsupported.Add(iface);
            }
        }

        // 验证实际 Unsupported 集合与声明矩阵完全一致
        CollectionAssert.AreEquivalent(
            PostgresDeclaredUnsupported.OrderBy(t => t.Name).ToList(),
            actualUnsupported.OrderBy(t => t.Name).ToList(),
            $"Postgres Unsupported 集合不匹配。声明 {PostgresDeclaredUnsupported.Count} 个，实际 {actualUnsupported.Count} 个。" +
            $" 多余: [{string.Join(", ", actualUnsupported.Except(PostgresDeclaredUnsupported).Select(t => t.Name))}]" +
            $" 缺失: [{string.Join(", ", PostgresDeclaredUnsupported.Except(actualUnsupported).Select(t => t.Name))}]");
    }

    [TestMethod]
    public async Task Postgres_NativeInterfacesShouldNotBeUnsupportedPlaceholder()
    {
        await using var sp = BuildPostgresProvider();
        var nativeInterfaces = CommonInterfaces.Except(PostgresDeclaredUnsupported).ToArray();
        foreach (var iface in nativeInterfaces)
        {
            var service = sp.GetService(iface);
            Assert.IsNotNull(service, $"Postgres 未注册原生接口: {iface.Name}");
            Assert.IsFalse(IsUnsupportedPlaceholder(service),
                $"Postgres 原生接口 {iface.Name} 不应是 Unsupported 占位，实际类型: {service.GetType().Name}");
        }
    }
}
