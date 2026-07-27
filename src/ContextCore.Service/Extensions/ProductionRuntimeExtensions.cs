using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Extensions;

// ===========================================================================
// 生产 Composition Root — 统一所有生产服务的注册入口
//
// 目标：
//   1. 提供 AddContextCoreProductionRuntime 扩展方法，作为生产服务的唯一显式入口。
//   2. 根据 RuntimeProfile（Development / SingleNode / ProductionHA）一次性完成
//      所有生产服务注册（Durable Transport hosted services、AgentKernel loop、
//      Run Recovery worker、Canary Leader 模式切换等）。
//   3. 启动时验证配置组合，不允许出现静默半配置状态（如 ProductionHA 未配置 postgres、
//      Durable Transport 启用但 pump 未注册等）。
//
// 调用顺序（Program.cs）：
//   AddContextStorage → AddContextCore → AddContextModelGateway → AddEmbeddingProviders
//   → AddContextCoreProductionRuntime（本方法在最后调用，覆盖 profile 专属注册）
//
// 设计原则：
//   1. 本方法不重复注册基础服务（Storage / Core 业务服务）；仅注册 profile 专属的
//      托管服务与覆盖项。
//   2. 配置验证在方法体顶部完成（fail-fast）：不合法的组合直接抛异常，阻止服务启动。
//   3. Development profile 保持最小依赖（InMemory / FileSystem + InProcessTransport）；
//      ProductionHA profile 启用 Durable Transport + HA 模式。
// ===========================================================================

/// <summary>
/// 运行时配置文件（profile）。决定生产服务注册组合。
/// </summary>
public enum RuntimeProfile
{
    /// <summary>
    /// 开发环境：InMemory/FileSystem 存储 + InProcessTransport + Deterministic 推理。
    /// 不启用 Durable Transport hosted services；不启用 Run Recovery（InMemory store 无需恢复）。
    /// Canary 走单节点 CanaryProgressionHostedService（CanarySchedulerOptions.Enabled 默认 true）。
    /// </summary>
    Development = 0,

    /// <summary>
    /// 单节点生产：Postgres 存储 + InProcessTransport（非 durable）。
    /// 不启用 Durable Transport（单实例无需跨进程持久化指令）。
    /// 启用 Run Recovery（Postgres 持久化 IAgentRunStore，崩溃后可恢复未完成 Run）。
    /// Canary 走单节点 CanaryProgressionHostedService。
    /// </summary>
    SingleNode = 1,

    /// <summary>
    /// 生产 HA：Postgres 存储 + Durable Transport + HA Leader 模式。
    /// 启用 Durable Transport hosted services（pump / replay / reaper / metrics）。
    /// 启用 Run Recovery + Agent Run Lease（多实例竞争租约，单 leader 处理）。
    /// Canary 走 CanaryLeaderHostedService（CanarySchedulerOptions.Enabled 强制 false）。
    /// </summary>
    ProductionHA = 2
}

/// <summary>
/// 生产 Composition Root 配置选项，对应 appsettings.json 中的 <c>ProductionRuntime</c> 节。
/// </summary>
public sealed class ProductionRuntimeOptions
{
    /// <summary>
    /// 运行时配置文件。默认 <see cref="RuntimeProfile.Development"/>。
    /// </summary>
    public RuntimeProfile Profile { get; set; } = RuntimeProfile.Development;

    /// <summary>
    /// 是否启用 AgentKernel 主循环 HostedService（<see cref="AgentKernelLoopHostedService"/>）。
    /// 默认 true。设为 false 可在测试场景中手动控制 Kernel 循环。
    /// </summary>
    public bool EnableAgentKernelLoop { get; set; } = true;

    /// <summary>
    /// 是否启用 AgentRun Recovery Worker（<see cref="AgentRunRecoveryWorker"/>）。
    /// 默认 true。Development profile 下 worker 检测到非持久化 store 时自动退出（no-op）。
    /// </summary>
    public bool EnableRunRecovery { get; set; } = true;

    /// <summary>
    /// Run Recovery 轮询间隔（默认 60 秒）。
    /// 仅当 <see cref="EnableRunRecovery"/> = true 且 IAgentRunStore 为持久化实现时生效。
    /// </summary>
    public TimeSpan RunRecoveryInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Run 最大执行时长（超时后 RecoveryWorker 将其标记为 Failed）。
    /// 默认 1 小时；&lt;= TimeSpan.Zero 表示不启用超时检测。
    /// 用于处理进程崩溃后 Run 卡在非终态的情况（CTS 随进程消失，Run 永远不会自动取消）。
    /// </summary>
    public TimeSpan RunExecutionTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 是否启用 Model Activation（权威模型推理激活）。
    /// 默认 false。设为 true 时启动验证会检查 IModelArtifactRegistry 已注册
    /// （需 Postgres provider 或显式注册），readiness 端点会报告激活状态。
    /// 实际激活仍需调用方通过 AddContextCore(ModelExecutionMode.RealModel) 注册 ModelActivationManager。
    /// </summary>
    public bool EnableModelActivation { get; set; } = false;
}

/// <summary>
/// 生产 Composition Root DI 注册扩展。
/// </summary>
internal static class ProductionRuntimeExtensions
{
    /// <summary>
    /// 统一注册所有生产服务，按 <see cref="RuntimeProfile"/> 一次性完成。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configuration">应用配置（读取 <c>ProductionRuntime</c> 节）。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    /// <exception cref="InvalidOperationException">配置组合不合法时抛出（fail-fast）。</exception>
    public static IServiceCollection AddContextCoreProductionRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 绑定 ProductionRuntimeOptions
        var runtimeOptions = new ProductionRuntimeOptions();
        configuration.GetSection("ProductionRuntime").Bind(runtimeOptions);

        // 注册 ProductionRuntimeOptions 单例（供 HostedService / 诊断端点查询当前 profile）
        services.AddSingleton(runtimeOptions);

        // 绑定 LearningMaterializationOptions（LearningMaterializationWorker 依赖）
        services.Configure<LearningMaterializationOptions>(configuration.GetSection("LearningMaterialization"));

        // 注册 IValidateOptions<ProductionRuntimeOptions>（ValidateOnStart 二次防线）
        services.AddSingleton<IValidateOptions<ProductionRuntimeOptions>, ProductionRuntimeOptionsValidator>();
        services.AddOptions<ProductionRuntimeOptions>()
            .Bind(configuration.GetSection("ProductionRuntime"))
            .ValidateOnStart();

        // 读取 Storage provider 用于跨配置验证（Storage:Provider 必须与 profile 一致）
        var storageProvider = configuration["Storage:Provider"] ?? "filesystem";
        var storageIsPostgres = string.Equals(storageProvider, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storageProvider, "postgresql", StringComparison.OrdinalIgnoreCase);

        // ── 配置组合验证（fail-fast）─────────────────────────────────────
        ValidateConfigurationCombination(runtimeOptions, storageIsPostgres, configuration, services);

        // 创建 Worker 注册表（在注册阶段捕获已注册的 Worker 类型名，供 readiness 端点查询）
        var workerRegistry = new ProductionRuntimeWorkerRegistry();

        // ── Profile 专属注册 ──────────────────────────────────────────────
        switch (runtimeOptions.Profile)
        {
            case RuntimeProfile.Development:
                AddDevelopmentServices(services, runtimeOptions, workerRegistry);
                break;
            case RuntimeProfile.SingleNode:
                AddSingleNodeServices(services, runtimeOptions, workerRegistry);
                break;
            case RuntimeProfile.ProductionHA:
                AddProductionHAServices(services, runtimeOptions, configuration, workerRegistry);
                break;
            default:
                throw new InvalidOperationException(
                    $"未知的 RuntimeProfile 值：{runtimeOptions.Profile}。" +
                    $"支持的值：Development, SingleNode, ProductionHA。");
        }

        // CanaryProgressionHostedService 和 CanaryLeaderHostedService 由 AddContextCore 注册（所有 profile）
        // 此处仅记录到 registry 供 readiness 端点查询（实际启用由 options 控制）。
        workerRegistry.Add<CanaryProgressionHostedService>();
        workerRegistry.Add<CanaryLeaderHostedService>();

        // 注册 ProductionRuntimeWorkerRegistry 和 ProductionRuntimeReadinessService
        // （供 /health/ready 和 /api/runtime/status 端点查询）
        services.AddSingleton(workerRegistry);
        services.TryAddSingleton<ProductionRuntimeReadinessService>();

        return services;
    }

    // ── Development profile ─────────────────────────────────────────────

    private static void AddDevelopmentServices(
        IServiceCollection services,
        ProductionRuntimeOptions options,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // Development：InMemory/FileSystem + InProcessTransport + Deterministic 推理。
        // 不启用 Durable Transport hosted services（InProcessTransport 无需 pump/replay/reaper）。
        // 不强制 Run Recovery（InMemory store 无持久化数据可恢复；FileSystem 也无 IAgentRunStore 持久化实现）。
        // Canary 走单节点 CanaryProgressionHostedService（CanarySchedulerOptions.Enabled 默认 true）。
        // CanaryLeaderHostedService 已由 AddContextCore 注册（Enabled 默认 false，不启动）。

        if (options.EnableAgentKernelLoop)
        {
            services.AddHostedService<AgentKernelLoopHostedService>();
            workerRegistry.Add<AgentKernelLoopHostedService>();
        }

        if (options.EnableRunRecovery)
        {
            // Recovery worker 会自检 IAgentRunStore 是否持久化；非持久化时退出（no-op）。
            services.AddHostedService<AgentRunRecoveryWorker>();
            workerRegistry.Add<AgentRunRecoveryWorker>();
        }

        // LearningMaterializationWorker：profile-agnostic worker（非 Postgres 时自退出 no-op）。
        // 统一在 ProductionRuntimeExtensions 注册，避免分散在 Program.cs。
        services.AddHostedService<LearningMaterializationWorker>();
        workerRegistry.Add<LearningMaterializationWorker>();
    }

    // ── SingleNode profile ──────────────────────────────────────────────

    private static void AddSingleNodeServices(
        IServiceCollection services,
        ProductionRuntimeOptions options,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // SingleNode：Postgres 存储 + InProcessTransport（非 durable）。
        // 启用 Run Recovery（Postgres IAgentRunStore 持久化，崩溃后可恢复未完成 Run）。
        // 不启用 Durable Transport hosted services（单实例无需跨进程持久化指令）。
        // Canary 走单节点 CanaryProgressionHostedService。
        // AgentHostOptions.LeaseEnabled 保持默认 false（单实例无需租约竞争）。

        if (options.EnableAgentKernelLoop)
        {
            services.AddHostedService<AgentKernelLoopHostedService>();
            workerRegistry.Add<AgentKernelLoopHostedService>();
        }

        if (options.EnableRunRecovery)
        {
            services.AddHostedService<AgentRunRecoveryWorker>();
            workerRegistry.Add<AgentRunRecoveryWorker>();
        }

        // LearningMaterializationWorker：Postgres provider 时激活 durable outbox 物化。
        services.AddHostedService<LearningMaterializationWorker>();
        workerRegistry.Add<LearningMaterializationWorker>();
    }

    // ── ProductionHA profile ────────────────────────────────────────────

    private static void AddProductionHAServices(
        IServiceCollection services,
        ProductionRuntimeOptions options,
        IConfiguration configuration,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // ProductionHA：Postgres 存储 + Durable Transport + HA Leader 模式。
        // 1. 启用 Durable Transport（替换 IAgentKernelTransport 绑定为 PostgresDurableTransport）。
        // 2. 注册 Durable Transport hosted services（pump / replay / reaper / metrics）。
        // 3. 启用 Run Recovery + Agent Run Lease（多实例竞争租约）。
        // 4. Canary 切换到 HA 模式（CanaryLeaderHostedService Enabled=true，CanaryProgressionHostedService Enabled=false）。

        // 1. 启用 Durable Transport：替换 IAgentKernelTransport 绑定
        // UsePostgresDurableTransport 会移除 InProcessTransport 默认绑定并替换为 PostgresDurableTransport。
        // 前置条件：AddContextStorage(postgres) 已注册 PostgresDurableTransport 为 IDurableTransport。
        services.UsePostgresDurableTransport();

        // 2. 注册 Durable Transport hosted services
        // DurableTransportHostingOptions 从 "DurableTransport" 配置节绑定（未配置时使用默认值）。
        services.AddDurableTransportHostedServices(opts =>
        {
            configuration.GetSection("DurableTransport").Bind(opts);
            // ProductionHA 强制启用 hosted services
            opts.Enabled = true;
        });
        workerRegistry.Add<DurableTransportInstructionPumpService>();
        workerRegistry.Add<ResultOutboxReplayService>();
        workerRegistry.Add<LeaseReaperService>();
        workerRegistry.Add<PendingCountMetricsService>();

        // 3. 覆盖 AgentHostOptions：启用 Run Lease
        // AddContextCore 已通过 TryAddSingleton 注册 AgentHostOptions（从 "AgentHost" 配置节绑定）。
        // 这里移除并重新注册，强制 LeaseEnabled=true（HA 多实例需要租约竞争）。
        RemoveService(services, typeof(ContextCore.Abstractions.AgentHostOptions));
        services.AddSingleton(sp =>
        {
            var opts = new ContextCore.Abstractions.AgentHostOptions();
            sp.GetService<IConfiguration>()?.GetSection("AgentHost").Bind(opts);
            opts.LeaseEnabled = true; // ProductionHA 强制启用
            return opts;
        });

        // 4. Canary 模式切换：
        // - CanarySchedulerOptions.Enabled = false（禁用单节点 CanaryProgressionHostedService）
        // - CanaryLeaderOptions.Enabled = true（启用 HA CanaryLeaderHostedService）
        RemoveService(services, typeof(CanarySchedulerOptions));
        services.AddSingleton(sp =>
        {
            var opts = new CanarySchedulerOptions();
            sp.GetService<IConfiguration>()?.GetSection("CanaryScheduler").Bind(opts);
            opts.Enabled = false; // ProductionHA: 禁用单节点 progression
            return opts;
        });

        RemoveService(services, typeof(IOptions<CanaryLeaderOptions>));
        services.AddSingleton<IOptions<CanaryLeaderOptions>>(sp =>
        {
            var opts = new CanaryLeaderOptions();
            sp.GetService<IConfiguration>()?.GetSection("CanaryLeader").Bind(opts);
            opts.Enabled = true; // ProductionHA: 强制启用 HA leader
            return Options.Create(opts);
        });

        if (options.EnableAgentKernelLoop)
        {
            services.AddHostedService<AgentKernelLoopHostedService>();
            workerRegistry.Add<AgentKernelLoopHostedService>();
        }

        if (options.EnableRunRecovery)
        {
            services.AddHostedService<AgentRunRecoveryWorker>();
            workerRegistry.Add<AgentRunRecoveryWorker>();
        }

        // LearningMaterializationWorker：HA 模式下多实例竞争 durable outbox 物化。
        services.AddHostedService<LearningMaterializationWorker>();
        workerRegistry.Add<LearningMaterializationWorker>();
    }

    // ── 配置组合验证 ─────────────────────────────────────────────────────

    /// <summary>
    /// 验证 RuntimeProfile 与其他配置项的组合合法性。
    /// 不合法时抛 <see cref="InvalidOperationException"/>（fail-fast，阻止服务启动）。
    /// </summary>
    /// <param name="options">已绑定的 ProductionRuntimeOptions（含 EnableAgentKernelLoop / EnableModelActivation 等）。</param>
    /// <param name="storageIsPostgres">Storage:Provider 是否为 postgres。</param>
    /// <param name="configuration">应用配置（读取连接字符串等）。</param>
    /// <param name="services">DI 容器（检查服务是否已注册，如 IModelArtifactRegistry）。</param>
    private static void ValidateConfigurationCombination(
        ProductionRuntimeOptions options,
        bool storageIsPostgres,
        IConfiguration configuration,
        IServiceCollection services)
    {
        var profile = options.Profile;

        // SingleNode / ProductionHA 要求 Postgres 存储
        if ((profile == RuntimeProfile.SingleNode || profile == RuntimeProfile.ProductionHA)
            && !storageIsPostgres)
        {
            throw new InvalidOperationException(
                $"[FATAL] ProductionRuntime:Profile={profile} 要求 Storage:Provider=postgres，" +
                $"但当前 Storage:Provider='{configuration["Storage:Provider"] ?? "filesystem"}'。" +
                $"{profile} profile 依赖 Postgres 持久化存储" +
                (profile == RuntimeProfile.ProductionHA
                    ? "（Durable Transport、AgentRunStore、CanaryLeaderLease 等）。"
                    : "（IAgentRunStore 持久化、Run Recovery）。") +
                "请将 Storage:Provider 改为 postgres 并配置 PostgresConnectionString，或切换到 Development profile。");
        }

        // ProductionHA 额外要求 PostgresConnectionString 已配置
        if (profile == RuntimeProfile.ProductionHA)
        {
            var connStr = configuration["Storage:PostgresConnectionString"];
            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new InvalidOperationException(
                    "[FATAL] ProductionRuntime:Profile=ProductionHA 要求 Storage:PostgresConnectionString 已配置，" +
                    "但当前为空。请配置连接字符串（支持 env:VAR_NAME 格式）。");
            }

            // ProductionHA 要求 EnableAgentKernelLoop=true（HA 模式下 Kernel 循环是核心组件）
            if (!options.EnableAgentKernelLoop)
            {
                throw new InvalidOperationException(
                    "[FATAL] ProductionRuntime:Profile=ProductionHA 要求 ProductionRuntime:EnableAgentKernelLoop=true，" +
                    "但当前为 false。ProductionHA 依赖 AgentKernelLoopHostedService 处理 Durable Transport 中的指令。" +
                    "如需禁用 Kernel 循环（如测试场景），请切换到 Development 或 SingleNode profile。");
            }
        }

        // EnableModelActivation=true 时要求 IModelArtifactRegistry 已注册
        // （IModelArtifactRegistry 由 PostgresServiceCollectionExtensions 或调用方显式注册）
        if (options.EnableModelActivation)
        {
            var hasRegistry = services.Any(s => s.ServiceType == typeof(IModelArtifactRegistry));
            if (!hasRegistry)
            {
                throw new InvalidOperationException(
                    "[FATAL] ProductionRuntime:EnableModelActivation=true 要求 IModelArtifactRegistry 已注册，" +
                    "但当前未注册。请将 Storage:Provider 设为 postgres（PostgresServiceCollectionExtensions 自动注册），" +
                    "或显式调用 services.AddSingleton<IModelArtifactRegistry>(...)。" +
                    "如不需要模型激活，请将 EnableModelActivation 设为 false。");
            }
        }
    }

    /// <summary>从 DI 容器中移除指定服务类型的所有注册。</summary>
    private static void RemoveService(IServiceCollection services, Type serviceType)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == serviceType)
            {
                services.RemoveAt(i);
            }
        }
    }
}

// ===========================================================================
// ProductionRuntimeOptions 启动验证器（ValidateOnStart 二次防线）
//
// AddContextCoreProductionRuntime 中的 ValidateConfigurationCombination 已在注册阶段
// 完成 fail-fast 验证（跨配置组合：Profile vs Storage vs IModelArtifactRegistry）。
// 本验证器作为 ValidateOnStart 二次防线，验证 ProductionRuntimeOptions 自身的
// 字段级约束（如未知 Profile 值），防止通过 options pattern 延迟绑定绕过注册阶段验证。
// ===========================================================================

/// <summary>
/// ProductionRuntimeOptions 启动验证器。验证 options 自身字段级约束。
/// </summary>
internal sealed class ProductionRuntimeOptionsValidator : IValidateOptions<ProductionRuntimeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ProductionRuntimeOptions options)
    {
        // 验证 Profile 为已知枚举值
        if (!Enum.IsDefined(options.Profile))
        {
            return ValidateOptionsResult.Fail(
                $"ProductionRuntime:Profile 值 '{options.Profile}' 不合法。" +
                "支持的值：Development (0), SingleNode (1), ProductionHA (2)。");
        }

        // ProductionHA 要求 EnableAgentKernelLoop=true（与注册阶段验证一致）
        if (options.Profile == RuntimeProfile.ProductionHA && !options.EnableAgentKernelLoop)
        {
            return ValidateOptionsResult.Fail(
                "ProductionRuntime:Profile=ProductionHA 要求 EnableAgentKernelLoop=true。");
        }

        return ValidateOptionsResult.Success;
    }
}
