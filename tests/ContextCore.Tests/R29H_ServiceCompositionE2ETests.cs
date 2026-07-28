using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.Evolution;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Service Composition E2E 验收测试
//
// 目标：验证 AddContextCoreProductionRuntime 在三种 RuntimeProfile
//   （Development / SingleNode / ProductionHA）下的服务注册组合正确性：
//   1. Development：InMemory/FileSystem + InProcessTransport；不启用 Durable Transport
//      hosted services；不强制 Run Lease。
//   2. SingleNode：要求 Postgres 存储；不启用 Durable Transport hosted services；
//      Run Lease 默认 false。
//   3. ProductionHA：要求 Postgres 存储；启用 Durable Transport hosted services
//      （pump / replay / reaper / metrics）；强制 Run Lease；Canary 切换到 HA 模式。
//
// 设计原则：
//   - 使用真实组件（非 mock）验证 DI 容器内容；Postgres 不可用时仅跳过
//     需要真实 DB 连接的测试（Assert.Inconclusive）。
//   - 服务注册组合测试不需要真实 DB 连接——AddContextCorePostgresStorage 仅注册
//     服务描述符，连接在服务实例被解析/调用时才发生。本测试只验证类型绑定。
//   - 配置组合验证（fail-fast）通过构造无效配置触发 InvalidOperationException。
//   - 全部使用 ServiceCollection + ConfigurationManager 直接构建，无 WebApplicationFactory。
//   - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Service-Composition")]
public sealed class R29H_ServiceCompositionE2ETests
{
    // ── Development Profile ──────────────────────────────────────────────

    /// <summary>
    /// Development profile 在 filesystem 存储下应成功注册：
    ///   - IAgentKernelTransport → InProcessTransport
    ///   - 不注册 IDurableTransport（Postgres 未注册）
    ///   - 注册 AgentKernelLoopHostedService + AgentRunRecoveryWorker
    /// </summary>
    [TestMethod]
    public void Development_Profile_RegistersInProcessTransportAndHostedServices()
    {
        // 安排：filesystem 存储配置 + Development profile
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ProductionRuntime:Profile"] = "Development",
            ["ProductionRuntime:EnableAgentKernelLoop"] = "true",
            ["ProductionRuntime:EnableRunRecovery"] = "true"
        });

        var services = new ServiceCollection();
        services.AddContextCore(); // 注册基础服务（InProcessTransport 等）
        services.AddContextCoreProductionRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言 1：IAgentKernelTransport 解析为 InProcessTransport（Development 默认）
        var transport = provider.GetService<IAgentKernelTransport>();
        Assert.IsNotNull(transport, "IAgentKernelTransport 应已注册。");
        Assert.IsInstanceOfType(transport, typeof(InProcessTransport),
            "Development profile 应使用 InProcessTransport。");

        // 断言 2：IDurableTransport 未注册（Postgres 未注册）
        var durableTransport = provider.GetService<IDurableTransport>();
        Assert.IsNull(durableTransport,
            "Development profile 不应注册 IDurableTransport（无 Postgres 后端）。");

        // 断言 3：ProductionRuntimeOptions 已注册并绑定 Profile=Development
        var runtimeOptions = provider.GetRequiredService<ProductionRuntimeOptions>();
        Assert.AreEqual(RuntimeProfile.Development, runtimeOptions.Profile,
            "ProductionRuntimeOptions.Profile 应为 Development。");

        // 断言 4：HostedService 描述符包含 AgentKernelLoopHostedService + AgentRunRecoveryWorker
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentKernelLoopHostedService).FullName,
            "Development profile 应注册 AgentKernelLoopHostedService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentRunRecoveryWorker).FullName,
            "Development profile 应注册 AgentRunRecoveryWorker。");

        // 断言 5：Durable Transport 专属 hosted services 未注册
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(DurableTransportInstructionPumpService).FullName,
            "Development profile 不应注册 DurableTransportInstructionPumpService。");
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(ResultOutboxReplayService).FullName,
            "Development profile 不应注册 ResultOutboxReplayService。");
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(LeaseReaperService).FullName,
            "Development profile 不应注册 LeaseReaperService。");
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
            ["ProductionRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore();
        services.AddContextCoreProductionRuntime(config);
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
    ///   - IAgentKernelTransport → InProcessTransport（非 durable，单实例无需跨进程持久化）
    ///   - IDurableTransport 已注册（由 AddContextCorePostgresStorage 注册）
    ///   - 不调用 UsePostgresDurableTransport（IAgentKernelTransport 不替换为 PostgresDurableTransport）
    ///   - AgentHostOptions.LeaseEnabled = false
    ///   - 注册 AgentKernelLoopHostedService + AgentRunRecoveryWorker
    /// </summary>
    /// <remarks>
    /// 本测试不连接真实 Postgres；仅验证 DI 容器中的服务描述符绑定。
    /// PostgresConnectionFactory 在被解析时才创建 NpgsqlDataSource，本测试不触发该路径。
    /// </remarks>
    [TestMethod]
    public void SingleNode_Profile_RegistersPostgresStoresButKeepsInProcessTransport()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ProductionRuntime:Profile"] = "SingleNode"
        });

        var services = new ServiceCollection();
        // 注册 Postgres 存储服务（仅描述符，不连接 DB）
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_sn_"));
        services.AddContextCore();
        services.AddContextCoreProductionRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言 1：IAgentKernelTransport 仍为 InProcessTransport（SingleNode 不启用 durable）
        var transport = provider.GetService<IAgentKernelTransport>();
        Assert.IsNotNull(transport, "IAgentKernelTransport 应已注册。");
        Assert.IsInstanceOfType(transport, typeof(InProcessTransport),
            "SingleNode profile 应使用 InProcessTransport（单实例无需 durable transport）。");

        // 断言 2：IDurableTransport 已注册（Postgres 存储自带）
        var durable = provider.GetService<IDurableTransport>();
        Assert.IsNotNull(durable, "IDurableTransport 应由 AddContextCorePostgresStorage 注册。");
        Assert.IsInstanceOfType(durable, typeof(PostgresDurableTransport),
            "IDurableTransport 应解析为 PostgresDurableTransport。");

        // 断言 3：IAgentRunStore 解析为持久化实现（IPersistentAgentRunStore）
        var runStore = provider.GetService<IAgentRunStore>();
        Assert.IsNotNull(runStore, "IAgentRunStore 应已注册。");
        Assert.IsInstanceOfType(runStore, typeof(IPersistentAgentRunStore),
            "SingleNode profile 应使用持久化 IAgentRunStore（Postgres）。");

        // 断言 4：AgentHostOptions.LeaseEnabled = false
        var agentHostOptions = provider.GetService<AgentHostOptions>();
        Assert.IsNotNull(agentHostOptions);
        Assert.IsFalse(agentHostOptions!.LeaseEnabled,
            "SingleNode profile 应保持 LeaseEnabled=false。");

        // 断言 5：Durable Transport hosted services 未注册
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(DurableTransportInstructionPumpService).FullName,
            "SingleNode profile 不应注册 DurableTransportInstructionPumpService（不启用 durable transport）。");
        CollectionAssert.DoesNotContain(hostedServiceTypes, typeof(LeaseReaperService).FullName,
            "SingleNode profile 不应注册 LeaseReaperService。");
        // Run Recovery 应注册
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentRunRecoveryWorker).FullName,
            "SingleNode profile 应注册 AgentRunRecoveryWorker。");
    }

    // ── ProductionHA Profile ─────────────────────────────────────────────

    /// <summary>
    /// ProductionHA profile 在 Postgres 存储下应成功注册：
    ///   - IAgentKernelTransport → PostgresDurableTransport（durable，HA 跨进程持久化）
    ///   - 注册 Durable Transport hosted services（pump / replay / reaper / metrics）
    ///   - AgentHostOptions.LeaseEnabled = true（强制启用 HA 租约竞争）
    ///   - CanarySchedulerOptions.Enabled = false（禁用单节点 progression）
    ///   - IOptions&lt;CanaryLeaderOptions&gt;.Enabled = true（启用 HA Canary Leader）
    /// </summary>
    /// <remarks>
    /// 本测试不连接真实 Postgres；仅验证 DI 容器中的服务描述符绑定。
    /// </remarks>
    [TestMethod]
    public void ProductionHA_Profile_RegistersDurableTransportAndHAHostedServices()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ProductionRuntime:Profile"] = "ProductionHA",
            ["ProductionRuntime:EnableAgentKernelLoop"] = "true",
            ["ProductionRuntime:EnableRunRecovery"] = "true"
        });

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_ha_"));
        services.AddContextCore();
        services.AddContextCoreProductionRuntime(config);
        var provider = services.BuildServiceProvider();

        // 断言 1：IAgentKernelTransport 替换为 PostgresDurableTransport
        var transport = provider.GetService<IAgentKernelTransport>();
        Assert.IsNotNull(transport, "IAgentKernelTransport 应已注册。");
        Assert.IsInstanceOfType(transport, typeof(PostgresDurableTransport),
            "ProductionHA profile 应使用 PostgresDurableTransport（durable transport for HA）。");

        // 断言 2：IDurableTransport 解析为 PostgresDurableTransport（与 IAgentKernelTransport 同一 singleton）
        var durable = provider.GetService<IDurableTransport>();
        Assert.IsNotNull(durable, "IDurableTransport 应已注册。");
        Assert.IsInstanceOfType(durable, typeof(PostgresDurableTransport));
        Assert.AreSame(transport, durable,
            "IAgentKernelTransport 与 IDurableTransport 应解析为同一 PostgresDurableTransport singleton。");

        // 断言 3：Durable Transport hosted services 全部注册
        var hostedServiceTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Select(t => t!.FullName)
            .ToList();
        CollectionAssert.Contains(hostedServiceTypes, typeof(DurableTransportInstructionPumpService).FullName,
            "ProductionHA profile 应注册 DurableTransportInstructionPumpService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(ResultOutboxReplayService).FullName,
            "ProductionHA profile 应注册 ResultOutboxReplayService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(LeaseReaperService).FullName,
            "ProductionHA profile 应注册 LeaseReaperService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentKernelLoopHostedService).FullName,
            "ProductionHA profile 应注册 AgentKernelLoopHostedService。");
        CollectionAssert.Contains(hostedServiceTypes, typeof(AgentRunRecoveryWorker).FullName,
            "ProductionHA profile 应注册 AgentRunRecoveryWorker。");

        // 断言 4：AgentHostOptions.LeaseEnabled 强制为 true（HA 多实例租约竞争）
        var agentHostOptions = provider.GetService<AgentHostOptions>();
        Assert.IsNotNull(agentHostOptions);
        Assert.IsTrue(agentHostOptions!.LeaseEnabled,
            "ProductionHA profile 应强制 LeaseEnabled=true（HA 多实例租约竞争）。");

        // 断言 5：CanarySchedulerOptions.Enabled = false（禁用单节点 progression）
        // P0-2：CanarySchedulerOptions 改用 AddOptions<>() 注册（Options Pipeline），
        // 不再注册为 POCO singleton，需通过 IOptions<T> 解析。
        var canarySchedulerOptions = provider.GetService<IOptions<CanarySchedulerOptions>>()?.Value;
        Assert.IsNotNull(canarySchedulerOptions);
        Assert.IsFalse(canarySchedulerOptions!.Enabled,
            "ProductionHA profile 应禁用单节点 CanaryProgressionHostedService。");

        // 断言 6：IOptions<CanaryLeaderOptions>.Enabled = true（启用 HA Canary Leader）
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
            ["ProductionRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_hainfra_"));
        services.AddContextCore();
        services.AddContextCoreProductionRuntime(config);
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
            ["ProductionRuntime:Profile"] = "SingleNode"
        });

        var services = new ServiceCollection();
        services.AddContextCore();

        // 断言：调用 AddContextCoreProductionRuntime 抛 InvalidOperationException
        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreProductionRuntime(config),
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
            ["ProductionRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCore();

        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreProductionRuntime(config),
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
            ["ProductionRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCore();

        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreProductionRuntime(config),
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
                ["ProductionRuntime:Profile"] = "ProductionHA"
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
            services.AddContextCore();
            services.AddContextCoreProductionRuntime(config);

            var provider = services.BuildServiceProvider();

            // 断言 1：所有关键 HA 服务可解析（不抛异常）
            var transport = provider.GetRequiredService<IAgentKernelTransport>();
            Assert.IsInstanceOfType(transport, typeof(PostgresDurableTransport),
                "ProductionHA + 真实 Postgres 应解析 PostgresDurableTransport。");

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
