using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Service;
using ContextCore.Service.Extensions;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>
/// DI 架构测试。验证以下 5 项不变量：
/// 1. 每个 provider 的最终解析类型（Final resolved type per provider）
/// 2. Postgres provider 无 Unsupported 占位（No Unsupported placeholder in production）
/// 3. 不存在意外重复覆盖（No accidental duplicate override）
/// 4. Data Plane / Control Plane 正确分离（Data Plane 必须包装 Invalidating*Decorator）
/// 5. 不存在 Singleton 捕获 Scoped 依赖（src/ 扩展方法内无 AddScoped 调用）
/// </summary>
/// <remarks>
/// 这些测试是架构守卫，捕获 DI 注册层面的回归（如新增 Unsupported 占位、
/// 误用 AddScoped、Control Plane 被错误包装失效 Decorator、Provider 切换导致解析类型回退）。
/// 与 <see cref="StorageProviderRegistrationTests"/>（仅检查 descriptor 存在性）和
/// <see cref="StorageProviderBehaviorContractTests"/>（检查实例是否可解析）不同，
/// 本测试聚焦于"最终解析类型 + 跨注册期的覆盖语义 + 架构不变量"。
/// </remarks>
[TestClass]
[TestCategory("Architecture")]
public sealed class ContextCoreDiArchitectureTests
{
    // Data Plane 接口集合（热路径，写路径接入 IStateCacheInvalidator，必须包装 Invalidating*Decorator）
    private static readonly Type[] DataPlaneInterfaces = new[]
    {
        typeof(IContextStore),
        typeof(IContextIndex),
        typeof(IMemoryStore),
        typeof(IConstraintStore),
        typeof(IRelationStore),
        typeof(IGlobalContextStore),
        typeof(IWorkingMemoryService),
        typeof(IVectorStore),
    };

    // Control Plane 接口集合（审计/治理/学习路径，读路径未接入缓存，不应包装 Invalidating*Decorator）
    private static readonly Type[] ControlPlaneInterfaces = new[]
    {
        typeof(IDecisionTraceStore),
        typeof(IRetrievalTraceStore),
        typeof(IContextPackageBuildTraceStore),
        typeof(IContextPackagePolicyStore),
        typeof(IContextLearningStore),
        typeof(ILearningFeedbackStore),
        typeof(ILearningFeedbackReviewStore),
        typeof(IShortTermMemoryStore),
        typeof(IShortTermPromotionCandidateStore),
        typeof(ICandidateMemoryReviewStore),
        typeof(IStableReviewCandidateStore),
        typeof(IStableLifecycleReviewStore),
        typeof(ICandidateConstraintReviewStore),
        typeof(IConstraintGapCandidateStore),
        typeof(IVectorReindexReportStore),
        typeof(IVectorLifecycleMetadataReviewStore),
        typeof(IVectorLifecycleMetadataReviewCandidateStore),
        typeof(IVectorLifecycleSidecarMetadataStore),
        typeof(IArtifactStore),
        typeof(IContextJobQueue),
        typeof(IContextJobQueryStore),
    };

    // 已知的非装饰器模式重复注册 whitelist（生产组合 AddContextStorage + AddContextCore 下）。
    // 装饰器模式重复（AddContextCorePostgresStorage 注册 forward + StorageExtensions.RegisterPostgres 注册 decorator）
    // 不在 whitelist 内，由 Data Plane / Control Plane 分离测试单独验证。
    //
    // Whitelist 分类：
    // 1. Composite 模式：AddContextCore 包装 AddContextStorage 注册的底层实现
    // 2. 已知缺陷：AddContextCore 覆盖 AddContextStorage 的 Postgres 实现（见 Ignore 测试）
    // 3. Forward 冗余：PostgresExt 注册 forward + StorageExt 再次 forward 到同一 impl（无害，最后注册胜出仍为同一类型）
    // 4. 多实现注册：按能力注册多个实现（如 IContextJobProcessor 的 Compression/VectorIndexing/UnsupportedJobProcessor）
    private static readonly HashSet<Type> KnownNonDecoratorDuplicates = new()
    {
        // 1. Composite 模式
        typeof(IContextEventSink),
        // 2. 已知缺陷（见 ProductionComposition_Postgres_IContextStateVersionStore_ResolvesToPostgresImplementation）
        typeof(IContextStateVersionStore),
        // 3. Forward 冗余（PostgresExt + StorageExt 两次 forward 到同一 Postgres*Store impl）
        typeof(IContextCollectionStore),
        typeof(IPromotionRecordStore),
        typeof(IPromotionCandidateStore),
        typeof(IRelationReviewStore),
        typeof(IContextPackageBuildTraceStore),
        typeof(IContextPackagePolicyStore),
        // 4. 多实现注册（按能力多注册，非覆盖语义）
        typeof(IContextJobProcessor),
        // 7 个 ICandidateProvider（每个 ExpertKind 一个）按能力多注册
        typeof(ICandidateProvider),
    };

    private static bool IsUnsupportedPlaceholder(object instance)
    {
        var typeName = instance.GetType().Name;
        return typeName.StartsWith("Unsupported", StringComparison.Ordinal)
            && typeName.EndsWith("Store", StringComparison.Ordinal);
    }

    private static StorageOptions MakePostgresOptions() => new()
    {
        Provider = "postgres",
        PostgresConnectionString = "Host=localhost;Database=fake;Username=fake;Password=fake",
    };

    private static StorageOptions MakeFileSystemOptions(string rootPath) => new()
    {
        Provider = "filesystem",
        RootPath = rootPath,
    };

    // =========================================================================
    // 1. 每个 provider 的最终解析类型
    // =========================================================================

    [TestMethod]
    public async Task AddContextStorage_Postgres_IContextStateVersionStore_ResolvesToPostgresImplementation()
    {
        // 验证：仅调用 AddContextStorage（无 AddContextCore）时，IContextStateVersionStore 应解析为 PostgresContextStateVersionStore
        // 此为存储层独立注册的预期行为（MultiInstanceCacheInvalidationTests 也验证此路径）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());

        await using var sp = services.BuildServiceProvider();
        var versionStore = sp.GetRequiredService<IContextStateVersionStore>();
        Assert.IsInstanceOfType(versionStore, typeof(PostgresContextStateVersionStore));
    }

    [Ignore("已知缺陷：CoreExtensions.cs:62 无条件 AddSingleton<IContextStateVersionStore, InMemoryContextStateVersionStore>()，" +
            "覆盖了 AddContextStorage 在 Postgres provider 下注册的 PostgresContextStateVersionStore。" +
            "Program.cs 调用顺序为 AddContextStorage → AddContextCore，故生产环境下" +
            "IContextStateVersionStore 实际解析为 InMemoryContextStateVersionStore（非 Postgres），" +
            "PostgresServiceCollectionExtensions.cs:134 注释声明的覆盖意图未生效。" +
            "修复方案：将 CoreExtensions.cs:62 改为 TryAddSingleton，或调整 Program.cs 调用顺序使 AddContextCore 先于 AddContextStorage。")]
    [TestMethod]
    public async Task ProductionComposition_Postgres_IContextStateVersionStore_ResolvesToPostgresImplementation()
    {
        // 完整生产组合：AddContextStorage → AddContextCore（与 Program.cs:87-88 一致）
        // 期望：Postgres 注册应胜出（符合 PostgresServiceCollectionExtensions.cs:134 注释意图）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        await using var sp = services.BuildServiceProvider();
        var versionStore = sp.GetRequiredService<IContextStateVersionStore>();
        Assert.IsInstanceOfType(versionStore, typeof(PostgresContextStateVersionStore),
            "生产组合下 IContextStateVersionStore 应解析为 PostgresContextStateVersionStore，实际为: " + versionStore.GetType().Name);
    }

    [TestMethod]
    public void AddContextCore_Alone_IContextStateVersionStore_ResolvesToInMemoryImplementation()
    {
        // 验证：仅调用 AddContextCore（无 AddContextStorage）时，IContextStateVersionStore 应解析为 InMemoryContextStateVersionStore
        // 此为 InMemory provider 默认路径（无 Postgres 覆盖）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        using var sp = services.BuildServiceProvider();
        var versionStore = sp.GetRequiredService<IContextStateVersionStore>();
        Assert.IsInstanceOfType(versionStore, typeof(ContextCore.Core.InMemoryContextStateVersionStore));
    }

    // =========================================================================
    // 2. Postgres provider 无 Unsupported 占位
    // =========================================================================

    [TestMethod]
    public async Task Postgres_NoStoreResolvesToUnsupportedPlaceholder()
    {
        // 完成后，Postgres provider 的所有原生注册接口都应解析为真实实现，
        // 不应有 Unsupported*Store 实例（包括 IArtifactStore 由 PostgresArtifactStore 实现）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());

        await using var sp = services.BuildServiceProvider();
        var unsupportedResolutions = new List<string>();

        // 仅检查 ContextCore.Abstractions.Contracts 命名空间下的存储接口（避免误伤 ModelGateway 等非存储接口）
        var storageInterfaces = services
            .Where(d => d.ServiceType.IsInterface
                && d.ServiceType.Namespace == "ContextCore.Abstractions")
            .GroupBy(d => d.ServiceType)
            .Select(g => g.Key)
            .ToList();

        foreach (var iface in storageInterfaces)
        {
            // 跳过可能因依赖缺失无法构造的服务（GetService 返回 null 表示未注册或构造失败）
            object? service;
            try
            {
                service = sp.GetService(iface);
            }
            catch (InvalidOperationException)
            {
                // 依赖缺失（如 IEmbeddingGenerator 未注册），跳过——本测试仅关注 Unsupported 占位
                continue;
            }

            if (service is not null && IsUnsupportedPlaceholder(service))
            {
                unsupportedResolutions.Add($"{iface.Name} → {service.GetType().Name}");
            }
        }

        Assert.AreEqual(0, unsupportedResolutions.Count,
            "Postgres provider 不应解析出任何 Unsupported*Store 占位。实际发现:\n" + string.Join("\n", unsupportedResolutions));
    }

    // =========================================================================
    // 3. 重复覆盖检查
    // =========================================================================

    [TestMethod]
    public void AddContextStorage_Postgres_IContextStateVersionStore_HasExactlyOneRegistration()
    {
        // AddContextStorage 单独调用时，IContextStateVersionStore 应仅有 1 次注册（来自 PostgresServiceCollectionExtensions）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());

        var registrations = services.Where(d => d.ServiceType == typeof(IContextStateVersionStore)).ToList();
        Assert.AreEqual(1, registrations.Count,
            "AddContextStorage（Postgres）应只注册 1 次 IContextStateVersionStore，实际: " + registrations.Count);
    }

    [TestMethod]
    public void AddContextCore_IContextStateVersionStore_HasExactlyOneRegistration()
    {
        // AddContextCore 单独调用时，IContextStateVersionStore 应仅有 1 次注册（InMemory 默认）
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        var registrations = services.Where(d => d.ServiceType == typeof(IContextStateVersionStore)).ToList();
        Assert.AreEqual(1, registrations.Count,
            "AddContextCore 应只注册 1 次 IContextStateVersionStore，实际: " + registrations.Count);
    }

    [TestMethod]
    public async Task ProductionComposition_Postgres_DataPlaneDecoratorPatternDuplicatesAreIntentional()
    {
        // 生产组合（AddContextStorage + AddContextCore）下，Data Plane 接口会出现 2 次注册：
        // 1. AddContextCorePostgresStorage 注册 forward → Postgres*Store
        // 2. StorageExtensions.RegisterPostgres 注册 Invalidating*Decorator 包装
        // 这是装饰器模式的预期行为（最后一次注册胜出 = decorator）。
        // 本测试验证：所有 Data Plane 接口的重复注册都遵循此模式（最后一次解析为 Invalidating*Decorator）。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        var dataPlaneDuplicates = services
            .Where(d => DataPlaneInterfaces.Contains(d.ServiceType))
            .GroupBy(d => d.ServiceType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        // 验证：所有 Data Plane 接口的最终解析结果都是 Invalidating*Decorator
        await using var sp = services.BuildServiceProvider();
        var violations = new List<string>();
        foreach (var iface in dataPlaneDuplicates)
        {
            var service = sp.GetRequiredService(iface);
            var typeName = service.GetType().Name;
            if (!typeName.StartsWith("Invalidating", StringComparison.Ordinal))
            {
                violations.Add($"{iface.Name} → {typeName}（重复注册但最终未解析为 Invalidating*Decorator）");
            }
        }

        Assert.AreEqual(0, violations.Count,
            "Data Plane 接口的重复注册应遵循装饰器模式（最后一次注册应为 Invalidating*Decorator）。实际发现:\n" +
            string.Join("\n", violations));
    }

    [TestMethod]
    public async Task ProductionComposition_Postgres_IContextEventSink_CompositeIsIntentionalOverride()
    {
        // IContextEventSink 的双重注册是有意为之：
        // 1. AddContextStorage (Postgres) 注册 PostgresContextEventSink 作为底层实现（仍可单独解析）
        // 2. AddContextCore 注册 BoundedChannelContextEventSink（包装 CompositeContextEventSink，内含 PostgresContextEventSink）
        // 验证：解析出的 IContextEventSink 实例不是 PostgresContextEventSink（被 Composite 覆盖），且非 Unsupported
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        await using var sp = services.BuildServiceProvider();
        var eventSink = sp.GetRequiredService<IContextEventSink>();
        Assert.IsNotInstanceOfType(eventSink, typeof(PostgresContextEventSink),
            "生产组合下 IContextEventSink 应被 AddContextCore 的 Composite 包装覆盖");
        Assert.IsFalse(IsUnsupportedPlaceholder(eventSink));

        // 验证底层 PostgresContextEventSink 仍可单独解析（Composite 模式的关键特征）
        var underlyingPostgresSink = sp.GetService<PostgresContextEventSink>();
        Assert.IsNotNull(underlyingPostgresSink,
            "PostgresContextEventSink 应仍可单独解析（Composite 包装内部依赖）");
    }

    [TestMethod]
    public async Task ProductionComposition_Postgres_NoNonDecoratorUnexpectedDuplicates()
    {
        // 生产组合（AddContextStorage + AddContextCore）下，仅允许两类重复注册：
        // 1. 装饰器模式重复（Data Plane 接口：forward + decorator）——由前一测试验证
        // 2. 已知 whitelist 重复（IContextEventSink Composite、IContextStateVersionStore 已知缺陷）
        // 本测试验证：不存在 whitelist 外、非装饰器模式的意外重复。
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        var allDuplicates = services
            .Where(d => d.ServiceType.IsInterface)
            .GroupBy(d => d.ServiceType)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        // 排除装饰器模式重复：Data Plane 接口的重复是装饰器模式，由前一测试单独验证
        // 排除 whitelist：IContextEventSink + IContextStateVersionStore
        var unexpectedDuplicates = allDuplicates
            .Where(t => !DataPlaneInterfaces.Contains(t))
            .Where(t => !KnownNonDecoratorDuplicates.Contains(t))
            .ToList();

        Assert.AreEqual(0, unexpectedDuplicates.Count,
            "生产组合下不应存在非装饰器模式、非 whitelist 的意外重复注册。意外发现:\n" +
            string.Join("\n", unexpectedDuplicates.Select(t => $"  - {t.FullName}")));
    }

    // =========================================================================
    // 4. Data Plane / Control Plane 分离
    // =========================================================================

    [TestMethod]
    public async Task Postgres_DataPlaneInterfaces_WrappedInInvalidatingDecorator()
    {
        // Data Plane 接口（热路径，写路径接入 IStateCacheInvalidator）必须包装在 Invalidating*Decorator 中
        // 此为 StorageExtensions.cs:177-188 的明确架构约束
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());

        await using var sp = services.BuildServiceProvider();
        var violations = new List<string>();
        foreach (var iface in DataPlaneInterfaces)
        {
            var service = sp.GetService(iface);
            if (service is null)
            {
                violations.Add($"{iface.Name} 未注册");
                continue;
            }
            var typeName = service.GetType().Name;
            if (!typeName.StartsWith("Invalidating", StringComparison.Ordinal))
            {
                violations.Add($"{iface.Name} → {typeName}（应包装在 Invalidating*Decorator 中）");
            }
        }

        Assert.AreEqual(0, violations.Count,
            "Postgres Data Plane 接口必须包装在 Invalidating*Decorator 中。实际发现:\n" + string.Join("\n", violations));
    }

    [TestMethod]
    public async Task Postgres_ControlPlaneInterfaces_NotWrappedInInvalidatingDecorator()
    {
        // Control Plane 接口（审计/治理路径，读路径未接入缓存）不应包装 Invalidating*Decorator
        // 此为 StorageExtensions.cs:190 的明确架构约束："非 Data Plane Store 直接转发，不叠加失效 Decorator"
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddContextStorage(MakePostgresOptions());

        await using var sp = services.BuildServiceProvider();
        var violations = new List<string>();
        foreach (var iface in ControlPlaneInterfaces)
        {
            var service = sp.GetService(iface);
            if (service is null) continue;  // 未注册的接口跳过（不要求所有 Control Plane 都注册）

            var typeName = service.GetType().Name;
            if (typeName.StartsWith("Invalidating", StringComparison.Ordinal))
            {
                violations.Add($"{iface.Name} → {typeName}（Control Plane 不应包装 Invalidating*Decorator）");
            }
        }

        Assert.AreEqual(0, violations.Count,
            "Postgres Control Plane 接口不应包装 Invalidating*Decorator。实际发现:\n" + string.Join("\n", violations));
    }

    [TestMethod]
    public void FileSystem_DataPlaneInterfaces_WrappedInInvalidatingDecorator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        services.AddSingleton(new RelationGovernanceProviderSwitchOptions());
        var rootPath = Path.Combine(Path.GetTempPath(), "ctx-arch-" + Guid.NewGuid().ToString("N"));
        try
        {
            services.AddContextStorage(MakeFileSystemOptions(rootPath));

            using var sp = services.BuildServiceProvider();
            var violations = new List<string>();
            foreach (var iface in DataPlaneInterfaces)
            {
                var service = sp.GetService(iface);
                if (service is null)
                {
                    violations.Add($"{iface.Name} 未注册");
                    continue;
                }
                var typeName = service.GetType().Name;
                if (!typeName.StartsWith("Invalidating", StringComparison.Ordinal))
                {
                    violations.Add($"{iface.Name} → {typeName}（应包装在 Invalidating*Decorator 中）");
                }
            }

            Assert.AreEqual(0, violations.Count,
                "FileSystem Data Plane 接口必须包装在 Invalidating*Decorator 中。实际发现:\n" + string.Join("\n", violations));
        }
        finally
        {
            try { Directory.Delete(rootPath, recursive: true); } catch { /* 测试清理容忍失败 */ }
        }
    }

    [TestMethod]
    public void FileSystem_ControlPlaneInterfaces_NotWrappedInInvalidatingDecorator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ShortTermMemoryPolicy());
        services.AddSingleton(new RelationGovernanceProviderSwitchOptions());
        var rootPath = Path.Combine(Path.GetTempPath(), "ctx-arch-" + Guid.NewGuid().ToString("N"));
        try
        {
            services.AddContextStorage(MakeFileSystemOptions(rootPath));

            using var sp = services.BuildServiceProvider();
            var violations = new List<string>();
            foreach (var iface in ControlPlaneInterfaces)
            {
                var service = sp.GetService(iface);
                if (service is null) continue;

                var typeName = service.GetType().Name;
                if (typeName.StartsWith("Invalidating", StringComparison.Ordinal))
                {
                    violations.Add($"{iface.Name} → {typeName}（Control Plane 不应包装 Invalidating*Decorator）");
                }
            }

            Assert.AreEqual(0, violations.Count,
                "FileSystem Control Plane 接口不应包装 Invalidating*Decorator。实际发现:\n" + string.Join("\n", violations));
        }
        finally
        {
            try { Directory.Delete(rootPath, recursive: true); } catch { /* 测试清理容忍失败 */ }
        }
    }

    // =========================================================================
    // 5. Singleton 捕获 Scoped 依赖检查
    // =========================================================================

    [TestMethod]
    public void SourceExtensionFiles_DoNotUseAddScoped()
    {
        // 验证：src/ 下所有 DI 扩展方法文件不使用 AddScoped（避免 Singleton 捕获 Scoped 依赖）
        // 当前架构：所有存储/Core 服务均为 Singleton（仅 PostgresBackupRunner / PostgresPitrRunner 为 Transient）
        // 若未来引入 Scoped 服务，必须先评估是否被 Singleton 工厂委托捕获（captive dependency）
        var extensionFiles = new[]
        {
            Path.Combine("src", "ContextCore.Service", "Extensions", "CoreExtensions.cs"),
            Path.Combine("src", "ContextCore.Service", "Extensions", "StorageExtensions.cs"),
            Path.Combine("src", "ContextCore.Storage.Postgres", "Extensions", "PostgresServiceCollectionExtensions.cs"),
            Path.Combine("src", "ContextCore.Client", "Extensions", "ContextCoreClientServiceCollectionExtensions.cs"),
        };

        var repoRoot = FindRepoRoot();
        var violations = new List<string>();

        foreach (var relativePath in extensionFiles)
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            if (!File.Exists(fullPath))
            {
                violations.Add($"文件不存在: {relativePath}");
                continue;
            }

            var lines = File.ReadAllLines(fullPath);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // 跳过注释行（// 或 ///）
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

                // 检查 AddScoped< 调用（区分大小写）
                if (line.Contains("AddScoped<", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}:{i + 1} {trimmed.Trim()}");
                }
            }
        }

        Assert.AreEqual(0, violations.Count,
            "src/ 扩展方法不应使用 AddScoped（所有存储/Core 服务均为 Singleton，避免 captive dependency）。实际发现:\n" +
            string.Join("\n", violations));
    }

    private static string FindRepoRoot()
    {
        // 测试运行目录通常为 tests/ContextCore.Tests/bin/Debug/net10.0/
        // 向上查找直到找到包含 src/ 和 tests/ 的目录
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tests")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
