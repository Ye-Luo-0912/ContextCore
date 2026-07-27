using System.Collections.Immutable;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;
using ContextCore.Inference.Onnx;
using ContextCore.Service.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Infrastructure;

// ===========================================================================
// Production Runtime Readiness Service
//
// 目标：
//   1. 为 /health/ready 端点提供就绪检查（Worker 启动 / Postgres / Durable Transport / Model Activation）。
//   2. 为 /api/runtime/status 端点提供当前激活组件报告（Profile / Worker 列表 / Model / Transport / Canary）。
//
// 设计原则：
//   - Worker 启动状态通过 IHostApplicationLifetime.ApplicationStarted 判断：
//     ApplicationStarted 触发表示所有 IHostedService.StartAsync 已完成。
//   - Worker 注册状态通过 ProductionRuntimeWorkerRegistry（注册阶段捕获的类型名列表）判断。
//   - Postgres 连接检查复用 PostgresConnectionFactory.PingAsync（与 Program.cs 启动验证一致）。
//   - 不缓存就绪结果——每次调用实时检查（端点本身有调用频率限制）。
// ===========================================================================

/// <summary>
/// 注册阶段捕获的 Worker 类型名列表。
/// 在 AddContextCoreProductionRuntime 中由各 Add*Services 方法填充。
/// 供 ProductionRuntimeReadinessService 在运行时查询已注册的 Worker。
/// </summary>
public sealed class ProductionRuntimeWorkerRegistry
{
    private readonly List<string> _workerTypeNames = new();

    /// <summary>已注册的 Worker 实现类型全名列表。</summary>
    public IReadOnlyList<string> WorkerTypeNames => _workerTypeNames;

    /// <summary>注册一个 Worker 类型。</summary>
    /// <typeparam name="TWorker">Worker 类型。</typeparam>
    internal void Add<TWorker>() where TWorker : class => _workerTypeNames.Add(typeof(TWorker).Name);
}

/// <summary>
/// Production Runtime 就绪检查与状态报告服务。
/// 供 /health/ready 和 /api/runtime/status 端点使用。
/// </summary>
public sealed class ProductionRuntimeReadinessService
{
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ProductionRuntimeOptions _runtimeOptions;
    private readonly ProductionRuntimeWorkerRegistry _workerRegistry;

    /// <summary>构造函数。</summary>
    /// <param name="services">DI 根容器（用于运行时解析服务检查注册状态）。</param>
    /// <param name="lifetime">应用生命周期（判断 ApplicationStarted）。</param>
    /// <param name="runtimeOptions">当前 ProductionRuntimeOptions（含 Profile 等）。</param>
    /// <param name="workerRegistry">注册阶段捕获的 Worker 类型名列表。</param>
    public ProductionRuntimeReadinessService(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        ProductionRuntimeOptions runtimeOptions,
        ProductionRuntimeWorkerRegistry workerRegistry)
    {
        _services = services;
        _lifetime = lifetime;
        _runtimeOptions = runtimeOptions;
        _workerRegistry = workerRegistry;
    }

    /// <summary>
    /// 当前 RuntimeProfile（Development / SingleNode / ProductionHA）。
    /// </summary>
    public RuntimeProfile Profile => _runtimeOptions.Profile;

    /// <summary>
    /// 应用是否已启动（所有 IHostedService.StartAsync 已完成）。
    /// </summary>
    public bool IsApplicationStarted => _lifetime.ApplicationStarted.IsCancellationRequested;

    /// <summary>
    /// 执行就绪检查，返回各组件的就绪状态与整体就绪判定。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>就绪检查结果（含各检查项明细）。</returns>
    public async Task<ProductionRuntimeReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<ReadinessCheckItem>();

        // 1. 检查应用是否已启动（所有 Worker 的 StartAsync 已完成）
        var appStarted = IsApplicationStarted;
        checks.Add(new ReadinessCheckItem(
            Name: "application-started",
            Status: appStarted ? "ready" : "starting",
            Message: appStarted
                ? "所有 HostedService 已启动。"
                : "应用尚未完成启动（部分 HostedService 可能仍在 StartAsync 中）。"));

        // 2. 检查 Postgres 连接（SingleNode / ProductionHA）
        if (_runtimeOptions.Profile == RuntimeProfile.SingleNode
            || _runtimeOptions.Profile == RuntimeProfile.ProductionHA)
        {
            var pgFactory = _services.GetService<PostgresConnectionFactory>();
            if (pgFactory is null)
            {
                checks.Add(new ReadinessCheckItem(
                    Name: "postgres-connection",
                    Status: "error",
                    Message: "PostgresConnectionFactory 未注册（Storage:Provider 非 postgres?）。"));
            }
            else
            {
                try
                {
                    var (success, error) = await pgFactory.PingAsync(cancellationToken).ConfigureAwait(false);
                    checks.Add(new ReadinessCheckItem(
                        Name: "postgres-connection",
                        Status: success ? "ready" : "error",
                        Message: success ? "PostgreSQL 连接正常。" : $"PostgreSQL 连接失败：{error}"));
                }
                catch (Exception ex)
                {
                    checks.Add(new ReadinessCheckItem(
                        Name: "postgres-connection",
                        Status: "error",
                        Message: $"PostgreSQL 连接检查异常：{ex.Message}"));
                }
            }
        }

        // 3. 检查 Durable Transport（ProductionHA）
        if (_runtimeOptions.Profile == RuntimeProfile.ProductionHA)
        {
            var durableTransport = _services.GetService<IDurableTransport>();
            checks.Add(new ReadinessCheckItem(
                Name: "durable-transport",
                Status: durableTransport is not null ? "ready" : "error",
                Message: durableTransport is not null
                    ? $"Durable Transport 已注册（{durableTransport.GetType().Name}）。"
                    : "IDurableTransport 未注册——ProductionHA profile 要求 Durable Transport。"));
        }

        // 4. 检查 Model Activation（如果启用）
        if (_runtimeOptions.EnableModelActivation)
        {
            var registry = _services.GetService<IModelArtifactRegistry>();
            if (registry is null)
            {
                checks.Add(new ReadinessCheckItem(
                    Name: "model-activation",
                    Status: "error",
                    Message: "EnableModelActivation=true 但 IModelArtifactRegistry 未注册。"));
            }
            else
            {
                try
                {
                    var models = await registry.ListAllAsync(cancellationToken).ConfigureAwait(false);
                    checks.Add(new ReadinessCheckItem(
                        Name: "model-activation",
                        Status: models.Count > 0 ? "ready" : "warning",
                        Message: models.Count > 0
                            ? $"Model Artifact Registry 已注册 {models.Count} 个模型工件。"
                            : "Model Artifact Registry 为空——无已注册的模型工件（启动后可通过 API 注册）。"));
                }
                catch (Exception ex)
                {
                    checks.Add(new ReadinessCheckItem(
                        Name: "model-activation",
                        Status: "error",
                        Message: $"Model Artifact Registry 查询异常：{ex.Message}"));
                }
            }
        }

        // 整体就绪判定：所有检查项均为 ready 或 warning 时视为就绪（warning 不阻断流量）
        var hasError = checks.Any(c => string.Equals(c.Status, "error", StringComparison.OrdinalIgnoreCase));
        var allReady = !hasError && appStarted;

        return new ProductionRuntimeReadinessResult(
            OverallStatus: allReady ? "ready" : (hasError ? "error" : "starting"),
            Profile: _runtimeOptions.Profile.ToString(),
            Checks: checks.ToImmutableList());
    }

    /// <summary>
    /// 获取已注册的 Worker 列表及状态。
    /// 基于 ProductionRuntimeWorkerRegistry（注册阶段捕获的类型名列表）和 ApplicationStarted 判断。
    /// </summary>
    /// <returns>Worker 信息列表。</returns>
    public IReadOnlyList<WorkerStatus> GetRegisteredWorkers()
    {
        var registeredTypes = _workerRegistry.WorkerTypeNames;
        var workers = new List<WorkerStatus>();

        // 预期 Worker 定义表：类型名 → 显示名 + 当前 Profile 下是否应启用
        var expectedWorkers = GetExpectedWorkerDefinitions();

        foreach (var (typeName, displayName, expectedEnabled) in expectedWorkers)
        {
            var isRegistered = registeredTypes.Contains(typeName);
            workers.Add(new WorkerStatus(
                Name: displayName,
                Type: typeName,
                Enabled: expectedEnabled,
                Registered: isRegistered,
                Started: isRegistered && IsApplicationStarted));
        }

        return workers;
    }

    /// <summary>
    /// 获取 Durable Transport 状态。
    /// </summary>
    /// <returns>Durable Transport 状态信息（null = 未注册）。</returns>
    public DurableTransportStatus? GetDurableTransportStatus()
    {
        var transport = _services.GetService<IDurableTransport>();
        if (transport is null)
        {
            return null;
        }

        return new DurableTransportStatus(
            IsRegistered: true,
            ImplementationType: transport.GetType().Name,
            IsActive: _runtimeOptions.Profile == RuntimeProfile.ProductionHA);
    }

    /// <summary>
    /// 获取 Model Activation 状态。
    /// </summary>
    /// <returns>Model Activation 状态信息（null = 未启用）。</returns>
    public ModelActivationStatus? GetModelActivationStatus()
    {
        if (!_runtimeOptions.EnableModelActivation)
        {
            return null;
        }

        var registry = _services.GetService<IModelArtifactRegistry>();
        var activationManager = _services.GetService<IModelActivationManager>();

        return new ModelActivationStatus(
            Enabled: true,
            RegistryRegistered: registry is not null,
            ActivationManagerRegistered: activationManager is not null,
            ActiveModelArtifactId: activationManager?.ActiveDescriptor?.ModelArtifactId,
            ActiveModelName: activationManager?.ActiveDescriptor?.ModelName);
    }

    /// <summary>
    /// 获取 Canary 状态。
    /// </summary>
    /// <returns>Canary 状态信息。</returns>
    public CanaryStatus GetCanaryStatus()
    {
        var schedulerOptions = _services.GetService<CanarySchedulerOptions>();
        var canaryLeaderOptions = _services.GetService<IOptions<CanaryLeaderOptions>>()?.Value;

        return new CanaryStatus(
            ProgressionEnabled: schedulerOptions?.Enabled ?? false,
            LeaderEnabled: canaryLeaderOptions?.Enabled ?? false,
            Mode: _runtimeOptions.Profile == RuntimeProfile.ProductionHA ? "HA-Leader" : "Single-Node-Progression");
    }

    /// <summary>
    /// 返回各 Profile 预期注册的 Worker 定义表。
    /// </summary>
    /// <returns>(类型名, 显示名, 当前 Profile 下是否应启用) 元组列表。</returns>
    private List<(string TypeName, string DisplayName, bool ExpectedEnabled)> GetExpectedWorkerDefinitions()
    {
        var isHA = _runtimeOptions.Profile == RuntimeProfile.ProductionHA;
        return
        [
            (nameof(Hosting.AgentKernelLoopHostedService), "AgentKernelLoop", _runtimeOptions.EnableAgentKernelLoop),
            (nameof(Hosting.AgentRunRecoveryWorker), "AgentRunRecovery", _runtimeOptions.EnableRunRecovery),
            (nameof(Hosting.DurableTransportInstructionPumpService), "DurableTransportInstructionPump", isHA),
            (nameof(Hosting.ResultOutboxReplayService), "ResultOutboxReplay", isHA),
            (nameof(Hosting.LeaseReaperService), "LeaseReaper", isHA),
            (nameof(Hosting.PendingCountMetricsService), "PendingCountMetrics", isHA),
            (nameof(Hosting.LearningMaterializationWorker), "LearningMaterialization", true),
            (nameof(CanaryProgressionHostedService), "CanaryProgression", !isHA),
            (nameof(Hosting.CanaryLeaderHostedService), "CanaryLeader", isHA),
        ];
    }
}

// ── 响应模型 ───────────────────────────────────────────────────────────

/// <summary>就绪检查整体结果。</summary>
/// <param name="OverallStatus">整体状态（ready / starting / error）。</param>
/// <param name="Profile">当前 Profile 名称。</param>
/// <param name="Checks">各检查项明细。</param>
public sealed record ProductionRuntimeReadinessResult(
    string OverallStatus,
    string Profile,
    IReadOnlyList<ReadinessCheckItem> Checks);

/// <summary>单个就绪检查项。</summary>
/// <param name="Name">检查项名称（如 application-started / postgres-connection）。</param>
/// <param name="Status">状态（ready / starting / error / warning）。</param>
/// <param name="Message">描述信息。</param>
public sealed record ReadinessCheckItem(string Name, string Status, string Message);

/// <summary>Worker 状态信息。</summary>
/// <param name="Name">显示名称。</param>
/// <param name="Type">实现类型名称。</param>
/// <param name="Enabled">是否在当前 Profile 下应启用。</param>
/// <param name="Registered">是否已注册到 DI 容器。</param>
/// <param name="Started">是否已启动（已注册且应用已启动）。</param>
public sealed record WorkerStatus(string Name, string Type, bool Enabled, bool Registered, bool Started);

/// <summary>Durable Transport 状态信息。</summary>
/// <param name="IsRegistered">是否已注册。</param>
/// <param name="ImplementationType">实现类型名称。</param>
/// <param name="IsActive">是否在当前 Profile 下激活。</param>
public sealed record DurableTransportStatus(bool IsRegistered, string ImplementationType, bool IsActive);

/// <summary>Model Activation 状态信息。</summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="RegistryRegistered">IModelArtifactRegistry 是否已注册。</param>
/// <param name="ActivationManagerRegistered">IModelActivationManager 是否已注册。</param>
/// <param name="ActiveModelArtifactId">当前已激活的模型工件 ID（null = 未激活）。</param>
/// <param name="ActiveModelName">当前已激活的模型名（null = 未激活）。</param>
public sealed record ModelActivationStatus(
    bool Enabled,
    bool RegistryRegistered,
    bool ActivationManagerRegistered,
    string? ActiveModelArtifactId,
    string? ActiveModelName);

/// <summary>Canary 状态信息。</summary>
/// <param name="ProgressionEnabled">CanaryProgressionHostedService 是否启用（单节点模式）。</param>
/// <param name="LeaderEnabled">CanaryLeaderHostedService 是否启用（HA 模式）。</param>
/// <param name="Mode">当前 Canary 模式（Single-Node-Progression / HA-Leader）。</param>
public sealed record CanaryStatus(bool ProgressionEnabled, bool LeaderEnabled, string Mode);
