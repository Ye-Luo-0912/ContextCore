using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.Evolution;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Extensions;

// ===========================================================================
// 生产 Composition Root — 统一所有生产服务的注册入口
//
// 目标：
// 1. 提供 AddContextCoreRuntime 扩展方法（新入口），作为生产服务的唯一显式入口。
// 该方法一次性决定 ModelMode / AgentModelMode / ToolMode / Store / Transport /
// Canary / HostedServices，避免 AddContextCore() 无参数重载强制 Deterministic
// 导致的 Profile 与真实运行模式分裂。
// 2. 根据 RuntimeProfile（Development / SingleNode / ProductionHA）完成所有生产服务
// 注册（Run Recovery worker、Canary Progression / Leader 模式切换等）。
// 3. 启动时验证配置组合，不允许出现静默半配置状态。
//
// 调用顺序（Program.cs）：
// AddContextStorage → AddContextModelGateway → AddEmbeddingProviders
// → AddContextCoreRuntime（唯一入口，按 Profile + ModelMode 分发）
//
// 修复：
// CanarySchedulerOptions / CanaryLeaderOptions 统一通过 IOptionsMonitor<T> 消费。
// ProductionHA 模式通过 PostConfigure 覆盖 Enabled 标志，而非 RemoveService + AddSingleton
// （后者不进入 Options Pipeline，IOptionsMonitor 读不到覆盖值）。
// ===========================================================================

/// <summary>
/// 生产 Composition Root DI 注册扩展。
/// </summary>
internal static class ProductionRuntimeExtensions
{
    // ── 统一入口 ──────────────────────────────────────────────────

    /// <summary>
    /// 统一注册 Core 服务 + 生产 Runtime 服务，作为生产 Composition Root 的唯一入口。
    /// </summary>
    /// <param name="services">DI 容器。</param>
    /// <param name="configuration">应用配置（读取 <c>ContextCoreRuntime</c> 节）。</param>
    /// <param name="sectionName">配置节名（默认 <c>ContextCoreRuntime</c>）。</param>
    /// <returns>DI 容器（链式调用）。</returns>
    /// <exception cref="InvalidOperationException">配置组合不合法时抛出（fail-fast）。</exception>
    /// <remarks>
    /// 此方法替代旧 <c>AddContextCore()</c> 与 Runtime 服务分开注册的双步调用。
    /// 它一次性完成：
    /// <list type="bullet">
    /// <item>绑定 <see cref="ContextCoreRuntimeOptions"/>（Profile / ModelMode / AgentModelMode / ToolMode）。</item>
    /// <item>按 <see cref="ContextCoreRuntimeOptions.ModelMode"/> 选择 <see cref="ModelExecutionOptions"/>，
    /// 调用 <c>AddContextCore(services, modelExecutionOptions)</c> 注册 Core 服务。</item>
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

        // 绑定 ContextCoreRuntimeOptions（唯一配置节，不再回退旧 ProductionRuntime 节）
        var runtimeOptions = BindContextCoreRuntimeOptions(configuration, sectionName);

        // ProductionHA 强制真实运行模式（AgentModelMode=RealModel, ToolMode=RealDispatch）。
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
        // 核心修复：不再使用无参数 AddContextCore()（强制 Deterministic），
        // 而是根据 ContextCoreRuntime:ModelMode 显式选择 RealModel / Deterministic。
        var modelExecutionOptions = BuildModelExecutionOptions(runtimeOptions);
        CoreExtensions.AddContextCore(services, modelExecutionOptions);

        // 注册 ContextCoreRuntimeOptions 单例（供诊断端点 / HostedService 查询当前运行模式）
        services.AddSingleton(runtimeOptions);

        // 执行共享的 Profile 注册逻辑
        AddProductionRuntimeProfileServices(services, runtimeOptions, configuration);

        // 按 AgentModelMode / ToolMode 覆盖 AddContextCore 的默认注册。
        // AddContextCore 使用 TryAddSingleton 注册 DeterministicAgentModelTransport / EchoToolDispatcher，
        // 此处按运行配置覆盖为真实实现（RealModel → ModelGatewayAgentModelTransport，
        // RealDispatch → RealToolDispatcher）。
        ApplyAgentModelModeOverride(services, runtimeOptions.AgentModelMode);
        ApplyToolModeOverride(services, runtimeOptions.ToolMode);

        // P0-13：Canary Kill Switch 查询加进程内 TTL 缓存装饰器。
        // 在线请求路径（AuthoritativeRuntime.IsEmergencyOverrideActiveAsync）不再每请求访问
        // Override Store；本地 TrySet/TryClear 写穿后立即失效；存储异常原样传播，
        // 由运行时按「覆盖活跃」回退 V1 + 告警（fail-closed，请求不失败）。
        ApplyCanaryOverrideCacheDecorator(services, configuration);

        return services;
    }

    // ── 共享 Profile 注册逻辑 ───────────────────────────────────────────

    /// <summary>
    /// 按 <see cref="RuntimeProfile"/> 注册 Profile 专属服务（HostedService / Transport / Canary）。
    /// 由 <see cref="AddContextCoreRuntime"/> 调用。
    /// </summary>
    private static void AddProductionRuntimeProfileServices(
        IServiceCollection services,
        ContextCoreRuntimeOptions runtimeOptions,
        IConfiguration configuration)
    {
        // 绑定 LearningMaterializationOptions（LearningMaterializationWorker 依赖）
        services.Configure<LearningMaterializationOptions>(configuration.GetSection("LearningMaterialization"));

        // 绑定 AgentRunEventCompactionOptions（AgentRunEventCompactionWorker 依赖）
        services.Configure<AgentRunEventCompactionOptions>(configuration.GetSection("EventCompaction"));

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

        // Event 快照自动压缩 worker：profile-agnostic（非 Postgres provider 时
        // IAgentRunEventCompactor 未注册，worker 检测到 null 后自退出 no-op）。
        services.AddHostedService<AgentRunEventCompactionWorker>();
        workerRegistry.Add<AgentRunEventCompactionWorker>();

        // CanaryProgressionHostedService 和 CanaryLeaderHostedService 记录到 registry 供 readiness 端点查询。
        // 实际 HostedService 注册按 Profile 互斥（避免单节点 + HA 双推进器）。
        workerRegistry.Add<CanaryProgressionHostedService>();
        workerRegistry.Add<CanaryLeaderHostedService>();
        workerRegistry.Add<ModelStateReconcilerWorker>();

        // 注册 ProductionRuntimeWorkerRegistry 和 ProductionRuntimeReadinessService
        services.AddSingleton(workerRegistry);
        services.TryAddSingleton<ProductionRuntimeReadinessService>();

        // Worker 集群心跳：后台周期标记集群存活，供生产准入「最近心跳」检查消费。
        services.AddHostedService<ProductionWorkerFleetHeartbeatService>();

        // 注册生产准入校验器（ProductionHA 强制项从 warning 升为 error）。
        // SecurityOptions 在 Program.cs 注册为具体类型单例；测试容器未注册时回退到默认值。
        services.TryAddSingleton<ProductionAdmissionValidator>(sp =>
            new ProductionAdmissionValidator(
                sp,
                sp.GetRequiredService<ContextCoreRuntimeOptions>(),
                sp.GetRequiredService<ProductionRuntimeWorkerRegistry>(),
                sp.GetService<SecurityOptions>() ?? new SecurityOptions(),
                sp.GetService<ILogger<ProductionAdmissionValidator>>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProductionAdmissionValidator>.Instance));

        // 注册请求阶段生产准入（实时探针 + TTL 缓存）。
        // ProductionAdmissionOptions 从 "ProductionAdmission" 配置节绑定（ProbeInterval / ProbeTimeout）。
        var admissionOptions = new ProductionAdmissionOptions();
        configuration.GetSection("ProductionAdmission").Bind(admissionOptions);
        services.AddSingleton(admissionOptions);

        services.TryAddSingleton<ProductionAdmissionController>(sp =>
            new ProductionAdmissionController(
                sp.GetRequiredService<ProductionAdmissionValidator>(),
                sp,
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<ProductionAdmissionOptions>(),
                sp.GetService<ILogger<ProductionAdmissionController>>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProductionAdmissionController>.Instance));

        // 注册 HA 迁移协调器（Postgres 专属）：多实例并发启动时 schema 迁移只由一个
        // 实例执行（pg_advisory_lock），其余实例在锁上等待后复查版本短路通过。
        // MigrationCoordinatorOptions 从 "MigrationCoordinator" 配置节绑定
        // （StartupRunEnabled / StartupTimeoutSeconds / InstanceId）。
        // 仅 Postgres provider 注册；非 Postgres 时状态端点返回 Enabled=false。
        if (storageIsPostgres)
        {
            var coordinatorOptions = new MigrationCoordinatorOptions();
            configuration.GetSection("MigrationCoordinator").Bind(coordinatorOptions);
            services.AddSingleton(coordinatorOptions);

            services.TryAddSingleton<PostgresMigrationCoordinator>(sp =>
                new PostgresMigrationCoordinator(
                    sp.GetRequiredService<PostgresMigrationRunner>(),
                    sp.GetRequiredService<PostgresOptions>(),
                    sp.GetRequiredService<MigrationCoordinatorOptions>(),
                    sp.GetService<ILogger<PostgresMigrationCoordinator>>()
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresMigrationCoordinator>.Instance));
            services.TryAddSingleton<IMigrationCoordinator>(sp => sp.GetRequiredService<PostgresMigrationCoordinator>());

            // 启动协调：服务启动时主动执行一次 schema 迁移（单执行者 + 失败快速退出）。
            services.AddHostedService<MigrationCoordinatorStartupService>();
        }
    }

    // ── Development profile ─────────────────────────────────────────────

    private static void AddDevelopmentServices(
        IServiceCollection services,
        ContextCoreRuntimeOptions options,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // Development：InMemory/FileSystem + Deterministic 推理。
        // Canary 走单节点 CanaryProgressionHostedService（CanarySchedulerOptions.Enabled 默认 true）。
        // CanaryLeaderHostedService 不注册（依赖 Postgres-only 的 ICanaryLeaderLease / ICanaryMetricsAggregator）。

        if (options.EnableAgentRunRecovery)
        {
            services.AddHostedService<AgentRunRecoveryWorker>();
            workerRegistry.Add<AgentRunRecoveryWorker>();

            // B3 Durable Scheduler：PostgresPendingRunClaimer 周期性领取持久化 pending Run 入队
            // （优先级 + 重试 + 死信）。Development profile 使用 InMemory store 时 worker 自退出 no-op。
            services.AddHostedService<PostgresPendingRunClaimer>();
            workerRegistry.Add<PostgresPendingRunClaimer>();
        }

        // ToolReconciliationWorker：轮询未裁决 Tool 对账记录，确认外部副作用真相后重新入队 Run。
        // 无条件注册（进程内 store 始终可用；无记录时为 no-op）。
        services.AddHostedService<ToolReconciliationWorker>();
        workerRegistry.Add<ToolReconciliationWorker>();

        // TerminalRunSettlementWorker：消费 Run 终态结算 outbox（配额 Actualize / Release）。
        // 无条件注册；非 Postgres provider 时自退出 no-op。
        services.AddHostedService<TerminalRunSettlementWorker>();
        workerRegistry.Add<TerminalRunSettlementWorker>();

        // 单节点 Canary Progression HostedService 注册。
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
        ContextCoreRuntimeOptions options,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // SingleNode：Postgres 存储（非 durable transport）。
        // 启用 Run Recovery（Postgres IAgentRunStore 持久化）。
        // Canary 走单节点 CanaryProgressionHostedService。
        // AgentHostOptions.LeaseEnabled 保持默认 false（单实例无需租约竞争）。

        if (options.EnableAgentRunRecovery)
        {
            services.AddHostedService<AgentRunRecoveryWorker>();
            workerRegistry.Add<AgentRunRecoveryWorker>();

            // B3 Durable Scheduler：PostgresPendingRunClaimer（SingleNode profile 使用 Postgres 持久化）。
            services.AddHostedService<PostgresPendingRunClaimer>();
            workerRegistry.Add<PostgresPendingRunClaimer>();
        }

        // ToolReconciliationWorker：轮询未裁决 Tool 对账记录，确认外部副作用真相后重新入队 Run。
        services.AddHostedService<ToolReconciliationWorker>();
        workerRegistry.Add<ToolReconciliationWorker>();

        // Workspace 配额：替换默认进程内实现为 Postgres 持久化实现（多实例共享配额真相源）。
        // 未配置 workspace 的默认上限取 SecurityOptions.Quota.DefaultLimit（与进程内实现一致）；
        // 已配置 workspace 的上限由创建端点在原子准入请求中解析（WorkspaceLimits 覆盖优先）。
        RemoveService(services, typeof(IWorkspaceQuotaService));
        services.AddSingleton<IWorkspaceQuotaService>(sp =>
        {
            var securityOptions = sp.GetService<SecurityOptions>();
            var defaultLimit = securityOptions?.Quota?.DefaultLimit;
            return new PostgresWorkspaceQuotaService(
                sp.GetRequiredService<PostgresConnectionFactory>(),
                sp.GetRequiredService<PostgresJsonSerializer>(),
                sp.GetRequiredService<PostgresMigrationRunner>(),
                defaultLimit?.MaxTokens ?? 0,
                defaultLimit?.MaxCostUsd ?? 0,
                defaultLimit?.PeriodSpan ?? TimeSpan.FromHours(1));
        });

        // TerminalRunSettlementWorker：消费 Run 终态结算 outbox（配额 Actualize / Release）。
        services.AddHostedService<TerminalRunSettlementWorker>();
        workerRegistry.Add<TerminalRunSettlementWorker>();

        // 单节点 Canary Progression HostedService 注册。
        services.AddHostedService<CanaryProgressionHostedService>();

        // LearningMaterializationWorker：Postgres provider 时激活 durable outbox 物化。
        services.AddHostedService<LearningMaterializationWorker>();
        workerRegistry.Add<LearningMaterializationWorker>();
    }

    // ── ProductionHA profile ────────────────────────────────────────────

    private static void AddProductionHAServices(
        IServiceCollection services,
        ContextCoreRuntimeOptions options,
        IConfiguration configuration,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        // ProductionHA：Postgres 存储 + HA Leader 模式（执行平面已收敛到
        // AgentRunStore → AgentKernelHost → AgentRunActor，无独立 Durable Transport）。
        // 1. 启用 Run Recovery + Agent Run Lease（多实例竞争租约）。
        // 2. Canary 切换到 HA 模式：
        // 通过 PostConfigure 覆盖 Enabled 标志（进入 Options Pipeline），
        // 让 IOptionsMonitor<CanarySchedulerOptions> / IOptionsMonitor<CanaryLeaderOptions>
        // 消费者能读到覆盖值。原 RemoveService + AddSingleton 不进入 Options Pipeline。
        // 3. 注册 CanaryLeaderHostedService（HA 模式），不注册 CanaryProgressionHostedService。

        // 1. 覆盖 AgentHostOptions：启用 Run Lease
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

        // 2. Canary 模式切换——通过 PostConfigure 覆盖 Enabled 标志。
        // PostConfigure 在 Configure 之后执行，IOptionsMonitor<T>.CurrentValue 会反映覆盖值。
        // CanarySchedulerOptions.Enabled = false（禁用单节点 CanaryProgressionHostedService）
        services.PostConfigure<CanarySchedulerOptions>(o => o.Enabled = false);
        // CanaryLeaderOptions.Enabled = true（启用 HA CanaryLeaderHostedService）
        services.PostConfigure<CanaryLeaderOptions>(o => o.Enabled = true);

        if (options.EnableAgentRunRecovery)
        {
            services.AddHostedService<AgentRunRecoveryWorker>();
            workerRegistry.Add<AgentRunRecoveryWorker>();

            // B3 Durable Scheduler：PostgresPendingRunClaimer（ProductionHA 多实例竞争 SKIP LOCKED 领取）。
            services.AddHostedService<PostgresPendingRunClaimer>();
            workerRegistry.Add<PostgresPendingRunClaimer>();
        }

        // ToolReconciliationWorker：轮询未裁决 Tool 对账记录，确认外部副作用真相后重新入队 Run。
        services.AddHostedService<ToolReconciliationWorker>();
        workerRegistry.Add<ToolReconciliationWorker>();

        // Workspace 配额：替换默认进程内实现为 Postgres 持久化实现（多实例共享配额真相源）。
        // 未配置 workspace 的默认上限取 SecurityOptions.Quota.DefaultLimit（与进程内实现一致）；
        // 已配置 workspace 的上限由创建端点在原子准入请求中解析（WorkspaceLimits 覆盖优先）。
        RemoveService(services, typeof(IWorkspaceQuotaService));
        services.AddSingleton<IWorkspaceQuotaService>(sp =>
        {
            var securityOptions = sp.GetService<SecurityOptions>();
            var defaultLimit = securityOptions?.Quota?.DefaultLimit;
            return new PostgresWorkspaceQuotaService(
                sp.GetRequiredService<PostgresConnectionFactory>(),
                sp.GetRequiredService<PostgresJsonSerializer>(),
                sp.GetRequiredService<PostgresMigrationRunner>(),
                defaultLimit?.MaxTokens ?? 0,
                defaultLimit?.MaxCostUsd ?? 0,
                defaultLimit?.PeriodSpan ?? TimeSpan.FromHours(1));
        });

        // TerminalRunSettlementWorker：消费 Run 终态结算 outbox（配额 Actualize / Release）。
        services.AddHostedService<TerminalRunSettlementWorker>();
        workerRegistry.Add<TerminalRunSettlementWorker>();

        // 3. HA 模式注册 CanaryLeaderHostedService（互斥不注册 CanaryProgressionHostedService）。
        // CanaryLeaderHostedService 通过 IOptionsMonitor<CanaryLeaderOptions> 读取 Enabled=true。
        services.AddHostedService<CanaryLeaderHostedService>();

        // HA 模式注册 ModelStateReconcilerWorker（同步期望模型状态）。
        // ModelStateReconcilerWorker 通过 IOptionsMonitor<ModelStateReconcilerOptions> 读取 Enabled=true。
        services.PostConfigure<ModelStateReconcilerOptions>(o => o.Enabled = true);
        services.AddHostedService<ModelStateReconcilerWorker>();

        // LearningMaterializationWorker：HA 模式下多实例竞争 durable outbox 物化。
        services.AddHostedService<LearningMaterializationWorker>();
        workerRegistry.Add<LearningMaterializationWorker>();
    }

    // ── 辅助方法 ──────────────────────────────────────────────────

    /// <summary>
    /// 绑定 <see cref="ContextCoreRuntimeOptions"/>，从 <paramref name="sectionName"/> 节读取。
    /// </summary>
    private static ContextCoreRuntimeOptions BindContextCoreRuntimeOptions(
        IConfiguration configuration,
        string sectionName)
    {
        var options = new ContextCoreRuntimeOptions();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// 根据 <see cref="ContextCoreRuntimeOptions.ModelMode"/> 和 <see cref="ContextCoreRuntimeOptions.EnableModelActivation"/>
    /// 构建 <see cref="ModelExecutionOptions"/>。
    /// </summary>
    private static ModelExecutionOptions BuildModelExecutionOptions(ContextCoreRuntimeOptions runtimeOptions)
    {
        // EnableModelActivation=true 等效于 ModelMode=RealModel（向后兼容旧配置）
        var useRealModel = runtimeOptions.ModelMode == ModelExecutionMode.RealModel
            || runtimeOptions.EnableModelActivation;
        return new ModelExecutionOptions
        {
            Mode = useRealModel ? ModelExecutionMode.RealModel : ModelExecutionMode.Deterministic
        };
    }

    /// <summary>
    /// 按 <see cref="AgentModelMode"/> 覆盖 IAgentModelTransport 注册。
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
    /// 按 <see cref="ToolExecutionMode"/> 覆盖 IToolDispatcher 注册。
    /// AddContextCore 默认注册 EchoToolDispatcher（TryAddSingleton），
    /// RealDispatch 模式下移除默认注册并注册 RealToolDispatcher（真实分派器）。
    /// </summary>
    private static void ApplyToolModeOverride(IServiceCollection services, ToolExecutionMode mode)
    {
        if (mode != ToolExecutionMode.RealDispatch)
        {
            return;
        }

        // 移除 AddContextCore 注册的 EchoToolDispatcher（含 IToolCatalog 别名）
        RemoveService(services, typeof(IToolDispatcher));
        RemoveService(services, typeof(IToolCatalog));

        // 注册 RealToolDispatcher：通过 IToolHandler 注册表分派 Tool 调用。
        // 默认无注册 Handler——生产部署应通过 DI 注册所需 IToolHandler 实现。
        // 已注册的 IToolHandler 实例会通过 IEnumerable<IToolHandler> 自动注入。
        // 同一实例同时注册为 IToolDispatcher 与 IToolCatalog（Actor 经 IToolCatalog 读取
        // Tool 定义声明给模型，无需向下转型到具体 RealToolDispatcher）。
        services.AddSingleton(sp =>
        {
            var handlers = sp.GetServices<IToolHandler>();
            var logger = sp.GetService<ILogger<RealToolDispatcher>>();
            // 构造后立即冻结注册表，禁止运行时 AddHandler；
            // 同时物化 SupportedTools 缓存为不可变 FrozenSet。
            var dispatcher = new RealToolDispatcher(handlers, logger);
            dispatcher.Freeze();
            return dispatcher;
        });
        services.AddSingleton<IToolDispatcher>(sp => sp.GetRequiredService<RealToolDispatcher>());
        services.AddSingleton<IToolCatalog>(sp => sp.GetRequiredService<RealToolDispatcher>());
    }

    // ── 配置组合验证 ─────────────────────────────────────────────────────

    /// <summary>
    /// 验证 RuntimeProfile 与其他配置项的组合合法性。
    /// 不合法时抛 <see cref="InvalidOperationException"/>（fail-fast，阻止服务启动）。
    /// </summary>
    private static void ValidateConfigurationCombination(
        ContextCoreRuntimeOptions options,
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
                $"[FATAL] ContextCoreRuntime:Profile={profile} 要求 Storage:Provider=postgres，" +
                $"但当前 Storage:Provider='{configuration["Storage:Provider"] ?? "filesystem"}'。" +
                $"{profile} profile 依赖 Postgres 持久化存储" +
                (profile == RuntimeProfile.ProductionHA
                    ? "（AgentRunStore 事件溯源、Run Lease、CanaryLeaderLease 等）。"
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
                    "[FATAL] ContextCoreRuntime:Profile=ProductionHA 要求 Storage:PostgresConnectionString 已配置，" +
                    "但当前为空。请配置连接字符串（支持 env:VAR_NAME 格式）。");
            }

            // 旧平面已退役——EnableAgentKernelLoop 配置项已随双执行平面收敛删除，无需校验。
        }

        // EnableModelActivation=true 时要求 IModelArtifactRegistry 已注册
        if (options.EnableModelActivation)
        {
            var hasRegistry = services.Any(s => s.ServiceType == typeof(IModelArtifactRegistry));
            if (!hasRegistry)
            {
                throw new InvalidOperationException(
                    "[FATAL] ContextCoreRuntime:EnableModelActivation=true 要求 IModelArtifactRegistry 已注册，" +
                    "但当前未注册。请将 Storage:Provider 设为 postgres（PostgresServiceCollectionExtensions 自动注册），" +
                    "或显式调用 services.AddSingleton<IModelArtifactRegistry>(...)。" +
                    "如不需要模型激活，请将 EnableModelActivation 设为 false。");
            }
        }
    }

    /// <summary>
    /// 用 TTL 缓存装饰器替换 <see cref="ICanaryEmergencyOverrideStore"/> 注册（P0-13）。
    /// </summary>
    /// <remarks>
    /// 包装当前最后一个注册（Postgres 实现或 InMemory 默认实现）为装饰器的内层，并保持
    /// <b>单注册</b>：移除全部原注册后仅注册装饰器，避免组合测试中的 enumerable 重复
    /// （Microsoft DI 直接解析取最后一个注册，移除后再包装语义与之前等价）。
    /// 未注册任何实现时跳过（运行时 store 为 null，不拦截 canary 流量，无需缓存）。
    /// </remarks>
    private static void ApplyCanaryOverrideCacheDecorator(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var descriptors = services
            .Where(s => s.ServiceType == typeof(ICanaryEmergencyOverrideStore))
            .ToList();
        if (descriptors.Count == 0)
        {
            return;
        }

        var options = new CanaryOverrideCacheOptions();
        configuration.GetSection(CanaryOverrideCacheOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        var innerDescriptor = descriptors[^1];
        RemoveService(services, typeof(ICanaryEmergencyOverrideStore));
        services.AddSingleton<ICanaryEmergencyOverrideStore>(sp =>
            new CachedCanaryEmergencyOverrideStore(
                ResolveOverrideStoreInner(sp, innerDescriptor),
                options));
    }

    /// <summary>从原始 ServiceDescriptor 构建内层 Override Store 实现（instance / type / factory 三种形态）。</summary>
    private static ICanaryEmergencyOverrideStore ResolveOverrideStoreInner(
        IServiceProvider sp,
        ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ICanaryEmergencyOverrideStore instance)
        {
            return instance;
        }
        if (descriptor.ImplementationType is not null)
        {
            return (ICanaryEmergencyOverrideStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }
        if (descriptor.ImplementationFactory is not null)
        {
            return (ICanaryEmergencyOverrideStore)descriptor.ImplementationFactory(sp);
        }
        throw new InvalidOperationException(
            "无法解析 ICanaryEmergencyOverrideStore 内层实现（ServiceDescriptor 无有效实现载体）。");
    }

    /// <summary>从 DI 容器中移除指定服务类型的所有注册。</summary>
    internal static void RemoveService(IServiceCollection services, Type serviceType)
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
