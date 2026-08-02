namespace ContextCore.Abstractions;

// ===========================================================================
// P0-1：统一运行配置入口契约
//
// 目标：
//   把 ProductionHA Profile 与真实运行模式（Model / AgentModel / Tool）的分裂问题
//   收敛到单一配置入口 ContextCoreRuntimeOptions。Program.cs 仅调用
//   AddContextCoreRuntime(builder.Configuration)，由该方法一次性决定：
//     - Store（根据 Profile 选择 InMemory/Postgres）
//     - Transport（Development=InMemory, ProductionHA=Durable）
//     - Agent Model Transport（根据 AgentModelMode 选择 Deterministic/RealModel）
//     - Tool Registry（根据 ToolMode 选择 Echo/RealDispatch）
//     - ONNX Activation Manager（ModelMode=RealModel 时注册）
//     - Canary Mode（ProductionHA=Leader, SingleNode/Development=Progression）
//     - HostedServices（根据 Profile 注册相应 Worker）
//     - Readiness requirements
//
// 设计原则：
//   1. 默认值保持向后兼容（Development profile + Deterministic 全部）。
//   2. ModelMode 仅控制 IBatchInferenceEngine 注册选择；AgentModelMode 控制
//      IAgentModelTransport；ToolMode 控制 IToolDispatcher。
//   3. ProductionHA profile 强制 Postgres + Durable Transport + HA Canary Leader。
//
// 注：RuntimeProfile 枚举从 ContextCore.Service.Extensions 命名空间迁移到
//   ContextCore.Abstractions，让 Abstractions 层的 ContextCoreRuntimeOptions
//   能直接引用。ProductionRuntimeExtensions.cs 中保留同名别名以向后兼容。
// ===========================================================================

/// <summary>
/// 运行时配置文件（profile）。决定生产服务注册组合。
/// </summary>
/// <remarks>
/// P0-1：从 ContextCore.Service.Extensions 命名空间迁移到 Abstractions，
/// 让 ContextCoreRuntimeOptions 能引用。原 ProductionRuntimeExtensions.cs
/// 中的 RuntimeProfile 已删除，使用本类型。
/// </remarks>
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
/// P0-1：Agent 模型调用模式。控制 IAgentModelTransport 的注册选择。
/// </summary>
/// <remarks>
/// 与 <see cref="ModelExecutionMode"/> 区别：
/// - <see cref="ModelExecutionMode"/> 控制 IBatchInferenceEngine（决策评分模型）。
/// - <see cref="AgentModelMode"/> 控制 IAgentModelTransport（Agent 对话模型，可能为 LLM）。
/// 两者独立配置，因为决策评分模型与 Agent 对话模型通常为不同模型。
/// </remarks>
public enum AgentModelMode : byte
{
    /// <summary>
    /// 确定性回放模式（默认）。
    /// 注册 IAgentModelTransport 为 DeterministicAgentModelTransport，
    /// 基于关键词匹配产出确定性响应，不调用真实 LLM。
    /// </summary>
    Deterministic = 0,

    /// <summary>
    /// 真实模型模式。
    /// 期望调用方已注册真实 IAgentModelTransport 实现（如 OpenAI / Anthropic / ModelGateway adapter）。
    /// 若未注册则回退到 DeterministicAgentModelTransport（fail-safe）。
    /// </summary>
    RealModel = 1
}

/// <summary>
/// P0-1：Tool 执行模式。控制 IToolDispatcher 的注册选择。
/// </summary>
public enum ToolExecutionMode : byte
{
    /// <summary>
    /// Echo 模式（默认，测试用）。
    /// 注册 IToolDispatcher 为 EchoToolDispatcher，仅支持 "echo" tool。
    /// </summary>
    Echo = 0,

    /// <summary>
    /// 真实分发模式。
    /// 期望调用方已注册真实 IToolDispatcher 实现（如 MCP tool bridge）。
    /// 若未注册则回退到 EchoToolDispatcher（fail-safe）。
    /// </summary>
    RealDispatch = 1
}

/// <summary>
/// P0-1：统一运行配置入口。对应 appsettings.json 中的 <c>ContextCoreRuntime</c> 节。
/// </summary>
/// <remarks>
/// 替代旧的 <c>ProductionRuntimeOptions</c> + <c>ModelExecutionOptions</c> 分裂配置。
/// 单一入口让 Profile / ModelMode / AgentModelMode / ToolMode 在同一处决定，
/// 避免 AddContextCore() 无参数重载强制选择 Deterministic 导致的分裂。
/// </remarks>
public sealed record ContextCoreRuntimeOptions
{
    /// <summary>
    /// 运行时配置文件。默认 <see cref="RuntimeProfile.Development"/>。
    /// </summary>
    public RuntimeProfile Profile { get; init; } = RuntimeProfile.Development;

    /// <summary>
    /// 决策评分模型执行模式（IBatchInferenceEngine）。
    /// 默认 <see cref="ModelExecutionMode.Deterministic"/>。
    /// RealModel 模式下注册 ModelActivationManager（需 IModelArtifactRegistry）。
    /// </summary>
    public ModelExecutionMode ModelMode { get; init; } = ModelExecutionMode.Deterministic;

    /// <summary>
    /// Agent 对话模型调用模式（IAgentModelTransport）。
    /// 默认 <see cref="AgentModelMode.Deterministic"/>。
    /// </summary>
    public AgentModelMode AgentModelMode { get; init; } = AgentModelMode.Deterministic;

    /// <summary>
    /// Tool 执行模式（IToolDispatcher）。
    /// 默认 <see cref="ToolExecutionMode.Echo"/>。
    /// </summary>
    public ToolExecutionMode ToolMode { get; init; } = ToolExecutionMode.Echo;

    /// <summary>
    /// PostgreSQL 连接字符串（仅 SingleNode / ProductionHA profile 使用）。
    /// 留空时从 Storage:PostgresConnectionString 回退读取。
    /// </summary>
    public string PostgresConnectionString { get; init; } = "";

    /// <summary>
    /// 是否启用 AgentKernel 主循环 HostedService。
    /// 默认 true。ProductionHA profile 强制 true。
    /// </summary>
    /// <summary>
    /// P0-7：是否启动旧 AgentKernelLoop（DefaultAgentKernel 指令队列平面）。
    /// 默认 false——旧平面的 PG inbox 在生产代码中无写入者，已空转。
    /// AgentRun 生命周期统一由 AgentKernelHost + AgentRunActor 处理。
    /// 设为 true 仅用于向后兼容验证（不推荐）。
    /// </summary>
    public bool EnableAgentKernelLoop { get; init; } = false;

    /// <summary>
    /// 是否启用 AgentRun Recovery Worker。
    /// 默认 true。Development profile 下 worker 检测非持久化 store 时自动退出。
    /// </summary>
    public bool EnableAgentRunRecovery { get; init; } = true;

    /// <summary>
    /// 是否启用模型激活（ModelActivationManager）。
    /// 默认 false。设为 true 时等效于 ModelMode=RealModel。
    /// 保留此字段是为了向后兼容旧 ProductionRuntime:EnableModelActivation 配置。
    /// </summary>
    public bool EnableModelActivation { get; init; }

    /// <summary>
    /// 是否启用 HA Canary Leader（仅 ProductionHA profile 生效）。
    /// 默认 false。ProductionHA profile 强制 true。
    /// </summary>
    public bool EnableCanaryLeader { get; init; }

    /// <summary>
    /// 是否启用 AgentKernel Worker（后台循环）。
    /// 默认 true。设为 false 可在测试场景中手动控制 Kernel 循环。
    /// </summary>
    public bool EnableKernelWorker { get; init; } = true;

    /// <summary>
    /// Run Recovery 轮询间隔（默认 60 秒）。
    /// </summary>
    public TimeSpan RunRecoveryInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Run 最大执行时长（超时后 RecoveryWorker 将其标记为 Failed）。
    /// 默认 1 小时；&lt;= TimeSpan.Zero 表示不启用超时检测。
    /// </summary>
    public TimeSpan RunExecutionTimeout { get; init; } = TimeSpan.FromHours(1);
}
