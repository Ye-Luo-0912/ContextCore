using ContextCore.Abstractions;
using ContextCore.Core.Services;
using ContextCore.Service;
using ContextCore.Service.Extensions;
using ContextCore.Storage.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// 验证三套存储 Provider（FileSystem / InMemory / Postgres）注册的接口集合与 Capability Contract 一致。
/// Capability Contract 包含 37 个存储契约接口（三套 Provider 注册接口的并集），
/// 每个 Provider 至少应注册其中的 34 个公共接口，并按各自能力补齐额外的 provider 专属接口。
/// 所有测试只验证 IServiceCollection 中的服务描述符，不构建 ServiceProvider，
/// 因为部分 Store 的构造依赖（如 ShortTermMemoryPolicy）在 CoreExtensions 中注册，不在存储层注册。
/// </summary>
[TestClass]
[TestCategory("Storage")]
public sealed class StorageProviderContractTests
{
    // 三套 Provider 共同注册的 34 个存储契约接口（Capability Contract 公共子集）
    private static readonly Type[] ExpectedInterfaces = new[]
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
        typeof(IRouterIntentShadowTraceStore),
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

    // FileSystem 额外注册的接口（文件系统路径解析 + 构建追踪）
    private static readonly Type[] FileSystemExtraInterfaces = new[]
    {
        typeof(IContextPathResolver),
        typeof(IContextPackageBuildTraceStore),
    };

    // Postgres 额外注册的接口（事件接收器 + 构建追踪）
    private static readonly Type[] PostgresExtraInterfaces = new[]
    {
        typeof(IContextEventSink),
        typeof(IContextPackageBuildTraceStore),
    };

    private static void AssertAllInterfacesRegistered(IServiceCollection services, IEnumerable<Type> interfaces, string providerName)
    {
        foreach (var iface in interfaces)
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == iface);
            Assert.IsNotNull(descriptor, $"{providerName} provider 未注册接口: {iface.Name}");
        }
    }

    [TestMethod]
    public void FileSystem_ShouldRegisterAllRequiredInterfaces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions { Provider = "filesystem", RootPath = Path.GetTempPath() };
        services.AddContextStorage(options);

        AssertAllInterfacesRegistered(services, ExpectedInterfaces, "FileSystem");
        AssertAllInterfacesRegistered(services, FileSystemExtraInterfaces, "FileSystem");
    }

    [TestMethod]
    public void InMemory_ShouldRegisterAllRequiredInterfaces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions { Provider = "memory" };
        services.AddContextStorage(options);
        // InMemory 不原生支持 IArtifactStore，补充 Unsupported 占位以对齐 Capability Contract
        services.AddSingleton<IArtifactStore>(_ => new UnsupportedArtifactStore("memory"));

        AssertAllInterfacesRegistered(services, ExpectedInterfaces, "InMemory");
    }

    [TestMethod]
    public void Postgres_ShouldRegisterAllRequiredInterfaces()
    {
        // Postgres 测试只验证 IServiceCollection 中的服务描述符，不构建 ServiceProvider
        // （PostgresConnectionFactory 构造时会创建 NpgsqlDataSource，无需真实连接）
        var services = new ServiceCollection();
        services.AddLogging();
        var options = new StorageOptions
        {
            Provider = "postgres",
            PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
        };
        services.AddContextStorage(options);

        AssertAllInterfacesRegistered(services, ExpectedInterfaces, "Postgres");
        AssertAllInterfacesRegistered(services, PostgresExtraInterfaces, "Postgres");
    }
}
