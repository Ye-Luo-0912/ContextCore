using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.Evolution;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Extensions;

// ===========================================================================
// 生产 Composition Root — 统一所有生产服务的注册入口
//
// 目标：
//   1. 提供 AddContextCoreRuntime 扩展方法（P0-1 新入口），作为生产服务的唯一显式入口。
//      该方法一次性决定 ModelMode / AgentModelMode / ToolMode / Store / Transport /
//      Canary / HostedServices，避免 AddContextCore() 无参数重载强制 Deterministic
//      导致的 Profile 与真实运行模式分裂。
//   2. 提供 AddContextCoreProductionRuntime 扩展方法（[Obsolete] 向后兼容），
//      委托到共享的 Profile 注册逻辑。旧调用方（测试）需先调用 AddContextCore()。
//   3. 根据 RuntimeProfile（Development / SingleNode / ProductionHA）完成所有生产服务
//      注册（Durable Transport hosted services、AgentKernel loop、Run Recovery worker、
//      Canary Progression / Leader 模式切换等）。
//   4. 启动时验证配置组合，不允许出现静默半配置状态。
//
// 调用顺序（Program.cs）：
//   AddContextStorage → AddContextModelGateway → AddEmbeddingProviders
//   → AddContextCoreRuntime（唯一入口，按 Profile + ModelMode 分发）
//
// P0-2 修复：
//   CanarySchedulerOptions / CanaryLeaderOptions 统一通过 IOptionsMonitor<T> 消费。
//   ProductionHA 模式通过 PostConfigure 覆盖 Enabled 标志，而非 RemoveService + AddSingleton
//   （后者不进入 Options Pipeline，IOptionsMonitor 读不到覆盖值）。
// ===========================================================================

/// <summary>
/// 生产 Composition Root 配置选项，对应 appsettings.json 中的 <c>ProductionRuntime</c> 节。
/// </summary>
/// <remarks>
/// P0-1：本类型保留用于向后兼容旧 <c>ProductionRuntime</c> 配置节及
/// <see cref="ProductionRuntimeReadinessService"/> 注入。新配置应使用
/// <see cref="ContextCoreRuntimeOptions"/> + <c>ContextCoreRuntime</c> 节。
/// </remarks>
public sealed class ProductionRuntimeOptions
{
    /// <summary>
    /// 运行时配置文件。默认 <see cref="RuntimeProfile.Development"/>。
    /// </summary>
    public RuntimeProfile Profile { get; set; } = RuntimeProfile.Development;

    /// <summary>
    /// 是否启用 AgentKernel 主循环 HostedService（<see cref="AgentKernelLoopHostedService"/>）。
    /// P0-7：默认 false。旧 AgentKernelLoop 平面已退役，AgentRun 统一由 AgentKernelHost 处理。
    /// 设为 true 仅用于向后兼容验证（不推荐）。
    /// </summary>
    public bool EnableAgentKernelLoop { get; set; } = false;

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
    /// </summary>
    public TimeSpan RunExecutionTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// 是否启用 Model Activation（权威模型推理激活）。
    /// 默认 false。设为 true 时等效于 ModelMode=RealModel。
    /// </summary>
    public bool EnableModelActivation { get; set; } = false;
}

/// <summary>
/// 生产 Composition Root DI 注册扩展。
/// </summary>
internal static class ProductionRuntimeExtensions
{
    // ── P0-1：统一入口 ──────────────────────────────────────────────────

    /// <summary>
    /// P0-1：统一注册 Core 服务 + 生产 Runtime 服务，作为生产 Composition Root 的唯一入口。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configuration">应用配置（读取 <c>ContextCoreRuntime</c> 节）。</param>
    /// <param name="sectionName">配置节名（默认 <c>ContextCoreRuntime</c>）。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    /// <exception cref="InvalidOperationException">配置组合不合法时抛出（fail-fast）。</exception>
    /// <remarks>
    /// 此方法替代旧 <see cref="AddContextCoreProductionRuntime"/> + <c>AddContextCore()</c> 双步调用。
    /// 它一次性完成：
    /// <list type="bullet">
    /// <item>绑定 <see cref="ContextCoreRuntimeOptions"/>（Profile / ModelMode / AgentModelMode / ToolMode）。</item>
    /// <item>按 <see cref="ContextCoreRuntimeOptions.ModelMode"/> 选择 <see cref="ModelExecutionOptions"/>，
    ///   调用 <c>AddContextCore(services, modelExecutionOptions)</c> 注册 Core 服务。</item>
    /// <item>按 <see cref="RuntimeProfile"/> 注册 Profile 专属 HostedService / Transport / Canary 模式。</item>
    /// <item>验证配置组合合法性（fail-fast）。</item>
    /// </list>
    /// 向后兼容：若配置中存在旧 <c>ProductionRuntime</c> 节但无 <c>ContextCoreRuntime</c> 节，
    /// 自动从 <c>ProductionRuntime</c> 节读取。
    /// </remarks>
    public static IServiceCollection AddContextCoreRuntime(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "ContextCoreRuntime")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 绑定 ContextCoreRuntimeOptions（优先从 ContextCoreRuntime 节，回退到 ProductionRuntime 节）
        var runtimeOptions = BindContextCoreRuntimeOptions(configuration, sectionName);

        // P0-3：ProductionHA 强制真实运行模式（AgentModelMode=RealModel, ToolMode=RealDispatch）。
        // ProductionHA 不允许 DeterministicAgentModelTransport / EchoToolDispatcher 静默生效——
        // 这些是测试/开发用 fallback，生产 HA 必须使用真实 transport / dispatcher。
        if (runtimeOptions.Profile == RuntimeProfile.ProductionHA)
        {
            runtimeOptions = runtimeOptions with
            {
                AgentModelMode = AgentModelMode.RealModel,
                ToolMode = ToolExecutionMode.RealDispatch
            };
        }

        // 按ModelMode 选择 ModelExecutionOptions，调用 AddContextCore 注册 Core 服务。
        // P0-1 核心修复：不再使用无参数 AddContextCore()（强制 Deterministic），
        // 而是根据 ContextCoreRuntime:ModelMode 显式选择 RealModel / Deterministic。
        var modelExecutionOptions = BuildModelExecutionOptions(runtimeOptions);
        CoreExtensions.AddContextCore(services, modelExecutionOptions);

        // 注册 ContextCoreRuntimeOptions 单例（供诊断端点 / HostedService 查询当前运行模式）
        services.AddSingleton(runtimeOptions);

        // 创建向后兼容的 ProductionRuntimeOptions（供 ProductionRuntimeReadinessService 注入）
        var legacyOptions = ToProductionRuntimeOptions(runtimeOptions);
        services.AddSingleton(legacyOptions);

        // 执行共享的 Profile 注册逻辑
        AddProductionRuntimeProfileServices(services, legacyOptions, configuration);

        // P0-3：按 AgentModelMode / ToolMode 覆盖 AddContextCore 的默认注册。
        // AddContextCore 使用 TryAddSingleton 注册 DeterministicAgentModelTransport / EchoToolDispatcher，
        // 此处按运行配置覆盖为真实实现（RealModel → ModelGatewayAgentModelTransport，
        // RealDispatch → RealToolDispatcher）。
        ApplyAgentModelModeOverride(services, runtimeOptions.AgentModelMode);
        ApplyToolModeOverride(services, runtimeOptions.ToolMode);

        return services;
    }

    // ── P0-1：旧入口（[Obsolete]，委托到共享逻辑）──────────────────────

    /// <summary>
    /// 统一注册所有生产服务，按 <see cref="RuntimeProfile"/> 一次性完成。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configuration">应用配置（读取 <c>ProductionRuntime</c> 节）。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    /// <exception cref="InvalidOperationException">配置组合不合法时抛出（fail-fast）。</exception>
    /// <remarks>
    /// P0-1：[Obsolete] 此方法不调用 AddContextCore，需调用方先调用 <c>AddContextCore()</c>。
    /// 新代码应使用 <see cref="AddContextCoreRuntime"/> 单一入口，它内部完成 AddContextCore + Profile 注册。
    /// </remarks>
    [Obsolete("P0-1: 此方法不调用 AddContextCore，需调用方先调用。新代码应使用 AddContextCoreRuntime(IConfiguration)。")]
    public static IServiceCollection AddContextCoreProductionRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // 绑定 ProductionRuntimeOptions（旧配置节）
        var runtimeOptions = new ProductionRuntimeOptions();
        configuration.GetSection("ProductionRuntime").Bind(runtimeOptions);
        services.AddSingleton(runtimeOptions);

        // 执行共享的 Profile 注册逻辑
        AddProductionRuntimeProfileServices(services, runtimeOptions, configuration);

        return services;
    }

    // ── 共享 Profile 注册逻辑 ───────────────────────────────────────────

    /// <summary>
    /// 按 <see cref="RuntimeProfile"/> 注册 Profile 专属服务（HostedService / Transport / Canary）。
    /// 由 <see cref="AddContextCoreRuntime"/> 和 <see cref="AddContextCoreProductionRuntime"/> 共享。
    /// </summary>
    private static void AddProductionRuntimeProfileServices(
        IServiceCollection services,
        ProductionRuntimeOptions runtimeOptions,
        IConfiguration configuration)
    {
        // 绑定 LearningMaterializationOptions（LearningMaterializationWorker 依赖）
        services.Configure<LearningMaterializationOptions>(configuration.GetSection("LearningMaterialization"));

        // 注册 IValidateOptions<ProductionRuntimeOptions>（ValidateOnStart 二次防线）
        services.AddSingleton<IValidateOptions<ProductionRuntimeOptions>, ProductionRuntimeOptionsValidator>();
        services.AddOptions<ProductionRuntimeOptions>()
            .Bind(configuration.GetSection("ProductionRuntime"))
            .ValidateOnStart();

        // 读取 Storage provider 用于跨配置验证
        var storageProvider = configuration["Storage:Provider"] ?? "filesystem";
        var storageIsPostgres = string.Equals(storageProvider, "postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storageProvider, "postgresql", StringComparison.OrdinalIgnoreCase);

        // ── 配置组合验证（fail-fast）─────────────────────────────────────
        ValidateConfigurationCombination(runtimeOptions, storageIsPostgres, configuration, services);

        // 创建 Worker 注册表
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

        // CanaryProgressionHostedService 和 CanaryLeaderHostedService 记录到 registry 供 readiness 端点查询。
        // 实际 HostedService 注册按 Profile 互斥（避免单节点 + HA 双推进器）。
        workerRegistry.Add<CanaryProgressionHostedService>();
        workerRegistry.Add<CanaryLeaderHostedService>();
        workerRegistry.Add<ModelStateReconcilerWorker>();

        // 注册 ProductionRuntimeWorkerRegistry 和 ProductionRuntimeReadinessService
        services.AddSingleton(workerRegistry);
        services.TryAddSingleton<ProductionRuntimeReadinessService>();
    }

    // ── Development profile ─────────────────────────────────────────────

    private static void AddDevelopmentServices(
        IServiceCollection services,
        ProductionRuntimeOptions options,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // Development：InMemory/FileSystem + InProcessTransport + Deterministic 推理。
        // Canary 走单节点 CanaryProgressionHostedService（CanarySchedulerOptions.Enabled 默认 true）。
        // CanaryLeaderHostedService 不注册（依赖 Postgres-only 的 ICanaryLeaderLease / ICanaryMetricsAggregator）。

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

        // P0-2：单节点 Canary Progression HostedService 注册。
        // CanaryProgressionHostedService 通过 IOptionsMonitor<CanarySchedulerOptions> 读取 Enabled 标志，
        // ProductionHA 模式的 PostConfigure(Enabled=false) 能被正确感知。
        services.AddHostedService<CanaryProgressionHostedService>();

        // LearningMaterializationWorker：profile-agnostic worker（非 Postgres 时自退出 no-op）。
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
        // 启用 Run Recovery（Postgres IAgentRunStore 持久化）。
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

        // P0-2：单节点 Canary Progression HostedService 注册。
        services.AddHostedService<CanaryProgressionHostedService>();

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
        // 4. Canary 切换到 HA 模式：
        //    P0-2：通过 PostConfigure 覆盖 Enabled 标志（进入 Options Pipeline），
        //    让 IOptionsMonitor<CanarySchedulerOptions> / IOptionsMonitor<CanaryLeaderOptions>
        //    消费者能读到覆盖值。原 RemoveService + AddSingleton 不进入 Options Pipeline。
        // 5. 注册 CanaryLeaderHostedService（HA 模式），不注册 CanaryProgressionHostedService。

        // 1. 启用 Durable Transport：替换 IAgentKernelTransport 绑定
        services.UsePostgresDurableTransport();

        // 2. 注册 Durable Transport hosted services
        services.AddDurableTransportHostedServices(opts =>
        {
            configuration.GetSection("DurableTransport").Bind(opts);
            opts.Enabled = true;
        });
        // P0-6：移除 DurableTransportInstructionPumpService 注册——指令不再通过 durable inbox → 旧 IAgentKernel
        // inbox 路径，统一走 AgentRunStore → AgentKernelHost → AgentRunActor。Pump 仍保留代码以备兼容参考。
        workerRegistry.Add<ResultOutboxReplayService>();
        workerRegistry.Add<LeaseReaperService>();
        workerRegistry.Add<PendingCountMetricsService>();

        // 3. 覆盖 AgentHostOptions：启用 Run Lease
        // AgentHostOptions 通过 TryAddSingleton 注册为 POCO（非 Options Pipeline），
        // 故仍使用 RemoveService + AddSingleton 覆盖（仅 Canary options 改用 PostConfigure）。
        RemoveService(services, typeof(ContextCore.Abstractions.AgentHostOptions));
        services.AddSingleton(sp =>
        {
            var opts = new ContextCore.Abstractions.AgentHostOptions();
            sp.GetService<IConfiguration>()?.GetSection("AgentHost").Bind(opts);
            opts.LeaseEnabled = true; // ProductionHA 强制启用
            return opts;
        });

        // 4. P0-2：Canary 模式切换——通过 PostConfigure 覆盖 Enabled 标志。
        // PostConfigure 在 Configure 之后执行，IOptionsMonitor<T>.CurrentValue 会反映覆盖值。
        // CanarySchedulerOptions.Enabled = false（禁用单节点 CanaryProgressionHostedService）
        services.PostConfigure<CanarySchedulerOptions>(o => o.Enabled = false);
        // CanaryLeaderOptions.Enabled = true（启用 HA CanaryLeaderHostedService）
        services.PostConfigure<CanaryLeaderOptions>(o => o.Enabled = true);

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

        // 5. P0-2：HA 模式注册 CanaryLeaderHostedService（互斥不注册 CanaryProgressionHostedService）。
        // CanaryLeaderHostedService 通过 IOptionsMonitor<CanaryLeaderOptions> 读取 Enabled=true。
        services.AddHostedService<CanaryLeaderHostedService>();

        // R29 WP-A-2：HA 模式注册 ModelStateReconcilerWorker（同步期望模型状态）。
        // ModelStateReconcilerWorker 通过 IOptionsMonitor<ModelStateReconcilerOptions> 读取 Enabled=true。
        services.PostConfigure<ModelStateReconcilerOptions>(o => o.Enabled = true);
        services.AddHostedService<ModelStateReconcilerWorker>();

        // LearningMaterializationWorker：HA 模式下多实例竞争 durable outbox 物化。
        services.AddHostedService<LearningMaterializationWorker>();
        workerRegistry.Add<LearningMaterializationWorker>();
    }

    // ── P0-1 辅助方法 ──────────────────────────────────────────────────

    /// <summary>
    /// 绑定 <see cref="ContextCoreRuntimeOptions"/>，优先从 <paramref name="sectionName"/> 节读取，
    /// 若该节不存在则回退到旧 <c>ProductionRuntime</c> 节（向后兼容）。
    /// </summary>
    private static ContextCoreRuntimeOptions BindContextCoreRuntimeOptions(
        IConfiguration configuration,
        string sectionName)
    {
        var section = configuration.GetSection(sectionName);
        if (section.Exists())
        {
            var options = new ContextCoreRuntimeOptions();
            section.Bind(options);
            return options;
        }

        // 回退：从旧 ProductionRuntime 节读取并映射到 ContextCoreRuntimeOptions
        var legacy = new ProductionRuntimeOptions();
        configuration.GetSection("ProductionRuntime").Bind(legacy);
        return ToContextCoreRuntimeOptions(legacy);
    }

    /// <summary>
    /// 根据 <see cref="ContextCoreRuntimeOptions.ModelMode"/> 和 <see cref="ContextCoreRuntimeOptions.EnableModelActivation"/>
    /// 构建 <see cref="ModelExecutionOptions"/>。
    /// </summary>
    private static ModelExecutionOptions BuildModelExecutionOptions(ContextCoreRuntimeOptions runtimeOptions)
    {
        // EnableModelActivation=true 等效于 ModelMode=RealModel（向后兼容旧 ProductionRuntime:EnableModelActivation）
        var useRealModel = runtimeOptions.ModelMode == ModelExecutionMode.RealModel
            || runtimeOptions.EnableModelActivation;
        return new ModelExecutionOptions
        {
            Mode = useRealModel ? ModelExecutionMode.RealModel : ModelExecutionMode.Deterministic
        };
    }

    /// <summary>
    /// P0-3：按 <see cref="AgentModelMode"/> 覆盖 IAgentModelTransport 注册。
    /// AddContextCore 默认注册 DeterministicAgentModelTransport（TryAddSingleton），
    /// RealModel 模式下移除默认注册并注册 ModelGatewayAgentModelTransport（真实 LLM transport）。
    /// </summary>
    private static void ApplyAgentModelModeOverride(IServiceCollection services, AgentModelMode mode)
    {
        if (mode != AgentModelMode.RealModel)
        {
            return;
        }

        // 移除 AddContextCore 注册的 DeterministicAgentModelTransport
        RemoveService(services, typeof(IAgentModelTransport));

        // 注册 ModelGatewayAgentModelTransport：通过 IModelGateway 调用真实 LLM。
        // IModelGateway 未注册时（测试场景）transport 返回错误响应（不抛异常）。
        services.AddSingleton<IAgentModelTransport>(sp =>
        {
            var gateway = sp.GetService<IModelGateway>();
            var logger = sp.GetService<ILogger<ModelGatewayAgentModelTransport>>();
            return new ModelGatewayAgentModelTransport(gateway, logger);
        });
    }

    /// <summary>
    /// P0-3：按 <see cref="ToolExecutionMode"/> 覆盖 IToolDispatcher 注册。
    /// AddContextCore 默认注册 EchoToolDispatcher（TryAddSingleton），
    /// RealDispatch 模式下移除默认注册并注册 RealToolDispatcher（真实分派器）。
    /// </summary>
    private static void ApplyToolModeOverride(IServiceCollection services, ToolExecutionMode mode)
    {
        if (mode != ToolExecutionMode.RealDispatch)
        {
            return;
        }

        // 移除 AddContextCore 注册的 EchoToolDispatcher
        RemoveService(services, typeof(IToolDispatcher));

        // 注册 RealToolDispatcher：通过 IToolHandler 注册表分派 Tool 调用。
        // 默认无注册 Handler——生产部署应通过 DI 注册所需 IToolHandler 实现。
        // 已注册的 IToolHandler 实例会通过 IEnumerable<IToolHandler> 自动注入。
        services.AddSingleton<IToolDispatcher>(sp =>
        {
            var handlers = sp.GetServices<IToolHandler>();
            var logger = sp.GetService<ILogger<RealToolDispatcher>>();
            // P0-4：构造后立即冻结注册表，禁止运行时 AddHandler；
            // 同时物化 SupportedTools 缓存为不可变 FrozenSet。
            var dispatcher = new RealToolDispatcher(handlers, logger);
            dispatcher.Freeze();
            return dispatcher;
        });
    }

    /// <summary>
    /// 将 <see cref="ContextCoreRuntimeOptions"/> 映射为向后兼容的 <see cref="ProductionRuntimeOptions"/>。
    /// </summary>
    private static ProductionRuntimeOptions ToProductionRuntimeOptions(ContextCoreRuntimeOptions runtime)
        => new()
        {
            Profile = runtime.Profile,
            EnableAgentKernelLoop = runtime.EnableAgentKernelLoop,
            EnableRunRecovery = runtime.EnableAgentRunRecovery,
            RunRecoveryInterval = runtime.RunRecoveryInterval,
            RunExecutionTimeout = runtime.RunExecutionTimeout,
            EnableModelActivation = runtime.EnableModelActivation
                || runtime.ModelMode == ModelExecutionMode.RealModel
        };

    /// <summary>
    /// 将旧 <see cref="ProductionRuntimeOptions"/> 映射为 <see cref="ContextCoreRuntimeOptions"/>。
    /// </summary>
    private static ContextCoreRuntimeOptions ToContextCoreRuntimeOptions(ProductionRuntimeOptions legacy)
        => new()
        {
            Profile = legacy.Profile,
            EnableAgentKernelLoop = legacy.EnableAgentKernelLoop,
            EnableAgentRunRecovery = legacy.EnableRunRecovery,
            RunRecoveryInterval = legacy.RunRecoveryInterval,
            RunExecutionTimeout = legacy.RunExecutionTimeout,
            EnableModelActivation = legacy.EnableModelActivation,
            ModelMode = legacy.EnableModelActivation ? ModelExecutionMode.RealModel : ModelExecutionMode.Deterministic
        };

    // ── 配置组合验证 ─────────────────────────────────────────────────────

    /// <summary>
    /// 验证 RuntimeProfile 与其他配置项的组合合法性。
    /// 不合法时抛 <see cref="InvalidOperationException"/>（fail-fast，阻止服务启动）。
    /// </summary>
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

            // P0-7：移除 ProductionHA 对 EnableAgentKernelLoop 的强制校验——旧平面已退役。
        }

        // EnableModelActivation=true 时要求 IModelArtifactRegistry 已注册
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
// ===========================================================================

/// <summary>
/// ProductionRuntimeOptions 启动验证器。验证 options 自身字段级约束。
/// </summary>
internal sealed class ProductionRuntimeOptionsValidator : IValidateOptions<ProductionRuntimeOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ProductionRuntimeOptions options)
    {
        if (!Enum.IsDefined(options.Profile))
        {
            return ValidateOptionsResult.Fail(
                $"ProductionRuntime:Profile 值 '{options.Profile}' 不合法。" +
                "支持的值：Development (0), SingleNode (1), ProductionHA (2)。");
        }

        // P0-7：移除 ProductionHA 对 EnableAgentKernelLoop 的强制校验。

        return ValidateOptionsResult.Success;
    }
}
