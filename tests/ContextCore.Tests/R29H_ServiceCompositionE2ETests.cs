using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// Service Composition E2E 验收测试
//
// 目标：验证 AddContextCoreRuntime 在三种 RuntimeProfile
// （Development / SingleNode / ProductionHA）下的服务注册组合正确性
// （执行平面已收敛到 AgentRunStore → AgentKernelHost → AgentRunActor，
// 无独立 Durable Transport hosted services）：
// 1. Development：InMemory/FileSystem 存储；注册 Run Recovery + 单节点
// Canary Progression；不强制 Run Lease。
// 2. SingleNode：要求 Postgres 存储；注册 Run Recovery + 单节点 Canary；
// Run Lease 默认 false。
// 3. ProductionHA：要求 Postgres 存储；注册 Run Recovery + HA Canary Leader
// + ModelStateReconciler；强制 Run Lease；Canary 切换到 HA 模式。
//
// 设计原则：
// - 使用真实组件（非 mock）验证 DI 容器内容；Postgres 不可用时仅跳过
// 需要真实 DB 连接的测试（Assert.Inconclusive）。
// - 服务注册组合测试不需要真实 DB 连接——AddContextCorePostgresStorage 仅注册
// 服务描述符，连接在服务实例被解析/调用时才发生。本测试只验证类型绑定。
// - 配置组合验证（fail-fast）通过构造无效配置触发 InvalidOperationException。
// - 全部使用 ServiceCollection + ConfigurationManager 直接构建，无 WebApplicationFactory。
// - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Service-Composition")]
public sealed class R29H_ServiceCompositionE2ETests
{
    // ── Development Profile ──────────────────────────────────────────────

    /// <summary>
    /// Development profile 在 filesystem 存储下应成功注册：
    /// - 注册 AgentRunRecoveryWorker + CanaryProgressionHostedService + LearningMaterializationWorker
    /// - 不注册 CanaryLeaderHostedService / ModelStateReconcilerWorker（Postgres-only / HA-only）
    /// - 不注册任何旧平面 Durable Transport hosted services（双执行平面已收敛）
    /// </summary>
    [TestMethod]
    public void Development_Profile_RegistersRunRecoveryAndCanaryHostedServices()
    {
        // 安排：filesystem 存储配置 + Development profile
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:EnableAgentRunRecovery"] = "true"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言 1：ContextCoreRuntimeOptions 已注册并绑定 Profile=Development
        var runtimeOptions = provider.GetRequiredService<ContextCoreRuntimeOptions>();
        Assert.AreEqual(RuntimeProfile.Development, runtimeOptions.Profile,
            "ContextCoreRuntimeOptions.Profile 应为 Development。");

        // 断言 2：HostedService 描述符包含 Run Recovery + 单节点 Canary + Learning
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentRunRecoveryWorker).FullName,
            "Development profile 应注册 AgentRunRecoveryWorker。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(CanaryProgressionHostedService).FullName,
            "Development profile 应注册 CanaryProgressionHostedService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(LearningMaterializationWorker).FullName,
            "Development profile 应注册 LearningMaterializationWorker。");

        // 断言 3：HA-only / Postgres-only hosted services 未注册
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(CanaryLeaderHostedService).FullName,
            "Development profile 不应注册 CanaryLeaderHostedService（依赖 Postgres-only lease）。");
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(ModelStateReconcilerWorker).FullName,
            "Development profile 不应注册 ModelStateReconcilerWorker（ProductionHA 专属）。");

        // 断言 4：旧平面 Durable Transport hosted services 全部退役（按名称守卫，防止回归）
        foreach (var retiredWorker in new[]
                 {
                     "DurableTransportInstructionPumpService",
                     "AgentKernelLoopHostedService",
                     "ResultOutboxReplayService",
                     "LeaseReaperService",
                     "PendingCountMetricsService"
                 })
        {
            CollectionAssert.DoesNotContain(hostedServiceTypes, retiredWorker,
                $"Development profile 不应注册已退役的旧平面 worker：{retiredWorker}。");
        }
    }

    /// <summary>
    /// Development profile 应保持 AgentHostOptions.LeaseEnabled = false（单节点无需租约竞争）。
    /// </summary>
    [TestMethod]
    public void Development_Profile_LeaseDisabled_CanarySingle()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言：AgentHostOptions.LeaseEnabled = false
        var agentHostOptions = provider.GetService<AgentHostOptions>();
        Assert.IsNotNull(agentHostOptions, "AgentHostOptions 应已注册。");
        Assert.IsFalse(agentHostOptions!.LeaseEnabled,
            "Development profile 应保持 LeaseEnabled=false（单节点无需租约竞争）。");
    }

    // ── SingleNode Profile ───────────────────────────────────────────────

    /// <summary>
    /// SingleNode profile 在 Postgres 存储下应成功注册：
    /// - IAgentRunStore 解析为持久化实现（Postgres）
    /// - AgentHostOptions.LeaseEnabled = false（单实例无需租约竞争）
    /// - 注册 AgentRunRecoveryWorker + CanaryProgressionHostedService + LearningMaterializationWorker
    /// - 不注册旧平面 Durable Transport hosted services
    /// </summary>
    /// <remarks>
    /// 本测试不连接真实 Postgres；仅验证 DI 容器中的服务描述符绑定。
    /// PostgresConnectionFactory 在被解析时才创建 NpgsqlDataSource，本测试不触发该路径。
    /// </remarks>
    [TestMethod]
    public void SingleNode_Profile_RegistersPostgresStoresAndRunRecovery()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ContextCoreRuntime:Profile"] = "SingleNode"
        });

        var services = new ServiceCollection();
        // 注册 Postgres 存储服务（仅描述符，不连接 DB）
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_sn_"));
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言 1：IAgentRunStore 解析为持久化实现（IPersistentAgentRunStore）
        var runStore = provider.GetService<IAgentRunStore>();
        Assert.IsNotNull(runStore, "IAgentRunStore 应已注册。");
        Assert.IsInstanceOfType(runStore, typeof(IPersistentAgentRunStore),
            "SingleNode profile 应使用持久化 IAgentRunStore（Postgres）。");

        // 断言 2：AgentHostOptions.LeaseEnabled = false
        var agentHostOptions = provider.GetService<AgentHostOptions>();
        Assert.IsNotNull(agentHostOptions);
        Assert.IsFalse(agentHostOptions!.LeaseEnabled,
            "SingleNode profile 应保持 LeaseEnabled=false。");

        // 断言 3：Run Recovery + 单节点 Canary + Learning hosted services 注册
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentRunRecoveryWorker).FullName,
            "SingleNode profile 应注册 AgentRunRecoveryWorker。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(CanaryProgressionHostedService).FullName,
            "SingleNode profile 应注册 CanaryProgressionHostedService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(LearningMaterializationWorker).FullName,
            "SingleNode profile 应注册 LearningMaterializationWorker。");

        // 断言 4：HA-only / 旧平面 hosted services 未注册
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(CanaryLeaderHostedService).FullName,
            "SingleNode profile 不应注册 CanaryLeaderHostedService（HA 专属）。");
        foreach (var retiredWorker in new[]
                 {
                     "DurableTransportInstructionPumpService",
                     "AgentKernelLoopHostedService",
                     "ResultOutboxReplayService",
                     "LeaseReaperService",
                     "PendingCountMetricsService"
                 })
        {
            CollectionAssert.DoesNotContain(hostedServiceTypes, retiredWorker,
                $"SingleNode profile 不应注册已退役的旧平面 worker：{retiredWorker}。");
        }
    }

    // ── ProductionHA Profile ─────────────────────────────────────────────

    /// <summary>
    /// ProductionHA profile 在 Postgres 存储下应成功注册：
    /// - 注册 Run Recovery + HA Canary Leader + ModelStateReconciler + Learning 等 hosted services
    /// - 不注册任何旧平面 Durable Transport hosted services（双执行平面已收敛）
    /// - AgentHostOptions.LeaseEnabled = true（强制启用 HA 租约竞争）
    /// - CanarySchedulerOptions.Enabled = false（禁用单节点 progression）
    /// - IOptions&lt;CanaryLeaderOptions&gt;.Enabled = true（启用 HA Canary Leader）
    /// </summary>
    /// <remarks>
    /// 本测试不连接真实 Postgres；仅验证 DI 容器中的服务描述符绑定。
    /// </remarks>
    [TestMethod]
    public void ProductionHA_Profile_RegistersRunLeaseAndHAHostedServices()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ContextCoreRuntime:Profile"] = "ProductionHA",
            ["ContextCoreRuntime:EnableAgentRunRecovery"] = "true"
        });

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_ha_"));
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言 1：HA 平面 hosted services 注册（旧平面 pump/replay/reaper/metrics 已退役）
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentRunRecoveryWorker).FullName,
            "ProductionHA profile 应注册 AgentRunRecoveryWorker。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(CanaryLeaderHostedService).FullName,
            "ProductionHA profile 应注册 CanaryLeaderHostedService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(ModelStateReconcilerWorker).FullName,
            "ProductionHA profile 应注册 ModelStateReconcilerWorker。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(LearningMaterializationWorker).FullName,
            "ProductionHA profile 应注册 LearningMaterializationWorker。");

        // 单节点 Canary Progression 不应注册（HA 模式互斥）
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(CanaryProgressionHostedService).FullName,
            "ProductionHA profile 不应注册 CanaryProgressionHostedService（HA 模式互斥）。");

        // 旧平面 Durable Transport hosted services 全部退役（按名称守卫，防止回归）
        foreach (var retiredWorker in new[]
                 {
                     "DurableTransportInstructionPumpService",
                     "AgentKernelLoopHostedService",
                     "ResultOutboxReplayService",
                     "LeaseReaperService",
                     "PendingCountMetricsService"
                 })
        {
            CollectionAssert.DoesNotContain(hostedServiceTypes, retiredWorker,
                $"ProductionHA profile 不应注册已退役的旧平面 worker：{retiredWorker}。");
        }

        // 断言 2：AgentHostOptions.LeaseEnabled 强制为 true（HA 多实例租约竞争）
        var agentHostOptions = provider.GetService<AgentHostOptions>();
        Assert.IsNotNull(agentHostOptions);
        Assert.IsTrue(agentHostOptions!.LeaseEnabled,
            "ProductionHA profile 应强制 LeaseEnabled=true（HA 多实例租约竞争）。");

        // 断言 3：CanarySchedulerOptions.Enabled = false（禁用单节点 progression）
        // CanarySchedulerOptions 改用 AddOptions<>() 注册（Options Pipeline），
        // 不再注册为 POCO singleton，需通过 IOptions<T> 解析。
        var canarySchedulerOptions = provider.GetService<IOptions<CanarySchedulerOptions>>()?.Value;
        Assert.IsNotNull(canarySchedulerOptions);
        Assert.IsFalse(canarySchedulerOptions!.Enabled,
            "ProductionHA profile 应禁用单节点 CanaryProgressionHostedService。");

        // 断言 4：IOptions<CanaryLeaderOptions>.Enabled = true（启用 HA Canary Leader）
        var canaryLeaderOptions = provider.GetService<IOptions<CanaryLeaderOptions>>();
        Assert.IsNotNull(canaryLeaderOptions);
        Assert.IsTrue(canaryLeaderOptions!.Value.Enabled,
            "ProductionHA profile 应启用 CanaryLeaderHostedService。");
    }

    /// <summary>
    /// ProductionHA profile 应注册 IAgentRunLease / ICanaryLeaderLease / ICanaryMetricsAggregator
    /// 等持久化 HA 组件（由 AddContextCorePostgresStorage 注册）。
    /// </summary>
    [TestMethod]
    public void ProductionHA_Profile_RegistersHAInfrastructure()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ContextCoreRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_hainfra_"));
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言：HA 基础设施组件均注册
        Assert.IsNotNull(provider.GetService<IAgentRunLease>(),
            "IAgentRunLease 应已注册（HA Run Lease 用于多实例竞争）。");
        Assert.IsNotNull(provider.GetService<ICanaryLeaderLease>(),
            "ICanaryLeaderLease 应已注册（Canary Leader 租约）。");
        Assert.IsNotNull(provider.GetService<ICanaryMetricsAggregator>(),
            "ICanaryMetricsAggregator 应已注册（跨实例 Canary 指标聚合）。");

        // 断言：IPersistentAgentRunStore / IPersistentAgentRunEventStore 标记接口注册
        Assert.IsNotNull(provider.GetService<IPersistentAgentRunStore>(),
            "IPersistentAgentRunStore 应已注册（HA 需要持久化 Run 元数据）。");
        Assert.IsNotNull(provider.GetService<IPersistentAgentRunEventStore>(),
            "IPersistentAgentRunEventStore 应已注册（HA 需要持久化事件流）。");
    }

    // ── 配置组合验证（fail-fast）──────────────────────────────────────────

    /// <summary>
    /// SingleNode profile 要求 Storage:Provider=postgres；
    /// 配置为 filesystem 时应抛 InvalidOperationException（fail-fast）。
    /// </summary>
    [TestMethod]
    public void SingleNode_Profile_WithFilesystemStorage_ThrowsFailFast()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "SingleNode"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        // 断言：调用 AddContextCoreRuntime 抛 InvalidOperationException
        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreRuntime(config),
            "SingleNode profile 配合 filesystem 存储应 fail-fast 抛 InvalidOperationException。");
    }

    /// <summary>
    /// ProductionHA profile 要求 Storage:Provider=postgres 且 PostgresConnectionString 已配置；
    /// 缺少连接字符串时应抛 InvalidOperationException。
    /// </summary>
    [TestMethod]
    public void ProductionHA_Profile_MissingConnectionString_ThrowsFailFast()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            // 故意不设置 Storage:PostgresConnectionString
            ["ContextCoreRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreRuntime(config),
            "ProductionHA profile 缺少 PostgresConnectionString 应 fail-fast 抛 InvalidOperationException。");
    }

    /// <summary>
    /// ProductionHA profile 配合 filesystem 存储应抛 InvalidOperationException。
    /// </summary>
    [TestMethod]
    public void ProductionHA_Profile_WithFilesystemStorage_ThrowsFailFast()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);

        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreRuntime(config),
            "ProductionHA profile 配合 filesystem 存储应 fail-fast 抛 InvalidOperationException。");
    }

    // ── ProductionHA Real Postgres E2E（Testcontainers）───────────────────

    /// <summary>
    /// E2E：ProductionHA profile 在真实 Postgres（Testcontainers）下应解析所有关键服务
    /// 并能成功创建 NpgsqlDataSource（验证连接字符串有效性）。
    /// Postgres 不可用时用 Assert.Inconclusive 跳过。
    /// </summary>
    [TestMethod]
    public async Task ProductionHA_Profile_RealPostgres_AllServicesResolvable()
    {
        // 启动 Testcontainers Postgres 容器
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ProductionHA 真实 Postgres E2E 测试已跳过。此结果不证明 ProductionHA 真实 Postgres 通过。");
            return;
        }

        await using (container)
        {
            var connectionString = container.GetConnectionString();
            var config = BuildConfiguration(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "postgres",
                ["Storage:PostgresConnectionString"] = connectionString,
                ["ContextCoreRuntime:Profile"] = "ProductionHA"
            });

            var services = new ServiceCollection();
            // 使用真实连接字符串注册 Postgres 服务
            var pgOptions = new PostgresOptions
            {
                ConnectionString = connectionString,
                AutoMigrate = false, // 不触发迁移；本测试只验证服务解析
                EnablePgVectorExtension = true,
                TablePrefix = "ha_e2e_"
            };
            services.AddContextCorePostgresStorage(pgOptions);
            services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
            services.AddContextCoreRuntime(config);

            var provider = services.BuildServiceProvider();

            // 断言 1：所有关键 HA 服务可解析（不抛异常）
            var runStore = provider.GetRequiredService<IAgentRunStore>();
            Assert.IsInstanceOfType(runStore, typeof(IPersistentAgentRunStore),
                "ProductionHA + 真实 Postgres 应解析持久化 IAgentRunStore。");

            var eventStore = provider.GetRequiredService<IAgentRunEventStore>();
            Assert.IsInstanceOfType(eventStore, typeof(IPersistentAgentRunEventStore),
                "ProductionHA + 真实 Postgres 应解析持久化 IAgentRunEventStore。");

            var runLease = provider.GetRequiredService<IAgentRunLease>();
            Assert.IsNotNull(runLease, "IAgentRunLease 应可解析。");

            var canaryLeaderLease = provider.GetRequiredService<ICanaryLeaderLease>();
            Assert.IsNotNull(canaryLeaderLease, "ICanaryLeaderLease 应可解析。");

            // 断言 2：PostgresConnectionFactory 可 ping 通（验证连接字符串）
            var factory = provider.GetRequiredService<PostgresConnectionFactory>();
            var (success, error) = await factory.PingAsync();
            Assert.IsTrue(success, $"Ping 真实 Postgres 应成功。错误：{error}");

            await factory.DisposeAsync();
        }
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────

    /// <summary>从键值对字典构建 IConfiguration。</summary>
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>构建 PostgresOptions（仅用于服务描述符注册，不实际连接 DB）。</summary>
    private static PostgresOptions BuildPostgresOptions(string tablePrefix) => new()
    {
        ConnectionString = "Host=localhost;Database=stub;Username=stub;Password=stub",
        AutoMigrate = false,
        EnablePgVectorExtension = false,
        TablePrefix = tablePrefix
    };

    /// <summary>
    /// 尝试启动 Postgres Testcontainers；Docker 不可用时返回 null。
    /// </summary>
    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R29H_ServiceCompositionE2ETests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
