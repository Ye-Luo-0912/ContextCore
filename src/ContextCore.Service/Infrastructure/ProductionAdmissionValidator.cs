using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Adapters;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Infrastructure;

/// <summary>生产准入检查结果状态。</summary>
public enum ProductionAdmissionCheckStatus
{
    /// <summary>通过。</summary>
    Pass = 0,

    /// <summary>不通过（ProductionHA 强制项缺失，阻断上线）。</summary>
    Fail = 1,

    /// <summary>跳过（非 ProductionHA profile 不执行准入校验）。</summary>
    Skipped = 2
}

/// <summary>单条生产准入检查结果。</summary>
public sealed record ProductionAdmissionCheck(string Name, ProductionAdmissionCheckStatus Status, string Message);

/// <summary>生产准入校验报告。</summary>
public sealed record ProductionAdmissionReport(
    bool AdmissionRequired,
    bool AllPassed,
    IReadOnlyList<ProductionAdmissionCheck> Checks,
    DateTimeOffset CheckedAt);

/// <summary>
/// 生产准入校验器：把 ProductionHA 强制项从 warning 升为 error。
/// 校验在 ApplicationStarted 之后执行（所有 Worker 启动后），任一强制项不满足时
/// <see cref="ValidateAsync"/> 返回 AllPassed=false，由调用方（Program.cs）记录
/// Critical 日志并中止进程，防止半配置生产环境静默上线。
/// 强制项：
/// 1. API Key 已配置（RequireApiKey=true 且 ApiKey 非空）
/// 2. 显式 Workspace（RequireExplicitWorkspace=true）
/// 3. RBAC 强制校验（Enforce=true）
/// 4. Approval Policy 启用且覆盖全部声明 RequiresApproval 的高风险工具
/// 5. 全部已注册工具 Schema 合法（名称 + JSON Schema 可解析）
/// 6. 至少一条原生 Tool Calling 路由（真实 transport + 已配置模型的网关）
/// 7. Cluster Model Slot 'primary' 已应用模型（DesiredStatus=Active）
/// 8. Late Hydration 管道完整（hydrator + batch lookup 已注册）
/// 9. ProductionHA Worker 集群已注册（随应用启动）
/// </summary>
public sealed class ProductionAdmissionValidator
{
    private readonly IServiceProvider _services;
    private readonly ContextCoreRuntimeOptions _runtimeOptions;
    private readonly ProductionRuntimeWorkerRegistry _workerRegistry;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<ProductionAdmissionValidator> _logger;
    private readonly TimeSpan _workerHeartbeatWindow;

    /// <summary>构造函数。</summary>
    /// <param name="services">DI 根容器。</param>
    /// <param name="runtimeOptions">运行时选项（含 Profile）。</param>
    /// <param name="workerRegistry">Worker 注册表（含集群心跳）。</param>
    /// <param name="securityOptions">安全选项。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="workerHeartbeatWindow">
    /// Worker 集群心跳新鲜度窗口；null 时使用
    /// <see cref="ProductionRuntimeWorkerRegistry.FleetHeartbeatFreshnessWindow"/>。
    /// </param>
    public ProductionAdmissionValidator(
        IServiceProvider services,
        ContextCoreRuntimeOptions runtimeOptions,
        ProductionRuntimeWorkerRegistry workerRegistry,
        SecurityOptions securityOptions,
        ILogger<ProductionAdmissionValidator> logger,
        TimeSpan? workerHeartbeatWindow = null)
    {
        _services = services;
        _runtimeOptions = runtimeOptions;
        _workerRegistry = workerRegistry;
        _securityOptions = securityOptions;
        _logger = logger;
        _workerHeartbeatWindow = workerHeartbeatWindow
            ?? ProductionRuntimeWorkerRegistry.FleetHeartbeatFreshnessWindow;
    }

    /// <summary>是否需要进行生产准入校验（仅 ProductionHA profile）。</summary>
    public bool AdmissionRequired => _runtimeOptions.Profile == RuntimeProfile.ProductionHA;

    /// <summary>
    /// 执行生产准入校验。非 ProductionHA profile 时跳过并返回 AllPassed=true。
    /// </summary>
    public async Task<ProductionAdmissionReport> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (!AdmissionRequired)
        {
            return new ProductionAdmissionReport(
                AdmissionRequired: false,
                AllPassed: true,
                Checks:
                [
                    new ProductionAdmissionCheck(
                        "production-admission",
                        ProductionAdmissionCheckStatus.Skipped,
                        $"Profile={_runtimeOptions.Profile}，不执行生产准入校验。")
                ],
                CheckedAt: DateTimeOffset.UtcNow);
        }

        var checks = new List<ProductionAdmissionCheck>(9);
        CheckApiKeyConfigured(checks);
        CheckExplicitWorkspace(checks);
        CheckRbacEnforced(checks);
        CheckApprovalAndHighRiskToolCoverage(checks);
        CheckToolSchemasValid(checks);
        CheckNativeToolCallingRoute(checks);
        await CheckModelSlotAppliedAsync(checks, cancellationToken).ConfigureAwait(false);
        CheckHydrationPipelineComplete(checks);
        CheckWorkerFleetStarted(checks);

        var allPassed = checks.All(c => c.Status == ProductionAdmissionCheckStatus.Pass);
        return new ProductionAdmissionReport(
            AdmissionRequired: true,
            AllPassed: allPassed,
            Checks: checks,
            CheckedAt: DateTimeOffset.UtcNow);
    }

    // ── 强制项 1：API Key 已配置 ──────────────────────────────────────

    private void CheckApiKeyConfigured(List<ProductionAdmissionCheck> checks)
    {
        var configured = _securityOptions.RequireApiKey && !string.IsNullOrWhiteSpace(_securityOptions.ApiKey);
        checks.Add(configured
            ? Pass("api-key-configured", "API Key 认证已启用且已配置密钥（RequireApiKey=true，ApiKey 非空）。")
            : Fail("api-key-configured",
                "Security:RequireApiKey=false 或 Security:ApiKey 未配置——ProductionHA 要求 API Key 认证，"
                + "否则未认证请求可访问全部端点。请设置 Security:RequireApiKey=true 并配置 Security:ApiKey。"));
    }

    // ── 强制项 2：显式 Workspace ─────────────────────────────────────

    private void CheckExplicitWorkspace(List<ProductionAdmissionCheck> checks)
    {
        var explicitWorkspace = _securityOptions.Workspace.RequireExplicitWorkspace;
        checks.Add(explicitWorkspace
            ? Pass("explicit-workspace", "已要求请求显式携带 workspace_id（RequireExplicitWorkspace=true）。")
            : Fail("explicit-workspace",
                "Security:Workspace:RequireExplicitWorkspace=false——ProductionHA 多租户要求显式 workspace，"
                + "缺失时不得回退到全局默认 workspace。请设为 true。"));
    }

    // ── 强制项 3：RBAC 强制校验 ─────────────────────────────────────

    private void CheckRbacEnforced(List<ProductionAdmissionCheck> checks)
    {
        var enforced = _securityOptions.Rbac.Enforce;
        checks.Add(enforced
            ? Pass("rbac-enforced", "RBAC 强制校验已启用（Enforce=true）。")
            : Fail("rbac-enforced",
                "Security:Rbac:Enforce=false——ProductionHA 要求 RBAC 强制校验，否则权限不足的请求会被放行。请设为 true。"));
    }

    // ── 强制项 4：Approval Policy 启用且覆盖高风险工具 ───────────────

    private void CheckApprovalAndHighRiskToolCoverage(List<ProductionAdmissionCheck> checks)
    {
        var approval = _securityOptions.ApprovalPolicy;
        if (approval is null || !approval.Enabled)
        {
            checks.Add(Fail("approval-and-high-risk-tool-coverage",
                "Security:ApprovalPolicy:Enabled=false——ProductionHA 要求审批策略启用，否则所有 Tool 调用自动放行（autoApproveAll=true）。"));
            return;
        }

        var approvedSet = new HashSet<string>(
            approval.ApprovalRequiredTools ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var dispatcher = _services.GetService<IToolDispatcher>();
        if (dispatcher is null)
        {
            checks.Add(Fail("approval-and-high-risk-tool-coverage",
                "IToolDispatcher 未注册——无法校验高风险 Tool 覆盖。请注册真实 Tool 分派器（ToolMode=RealDispatch）。"));
            return;
        }

        var uncovered = new List<string>();
        foreach (var toolName in dispatcher.SupportedTools)
        {
            var descriptor = dispatcher.GetDescriptor(toolName);
            if (descriptor is { RequiresApproval: true } && !approvedSet.Contains(toolName))
            {
                uncovered.Add(toolName);
            }
        }

        checks.Add(uncovered.Count == 0
            ? Pass("approval-and-high-risk-tool-coverage",
                $"Approval Policy 已启用（{approvedSet.Count} 个需审批工具），全部声明 RequiresApproval 的已注册工具均已覆盖。")
            : Fail("approval-and-high-risk-tool-coverage",
                $"以下高风险工具声明 RequiresApproval 但未纳入 Security:ApprovalPolicy:ApprovalRequiredTools：{string.Join("，", uncovered)}。"
                + "请补充覆盖或移除工具的 RequiresApproval 声明。"));
    }

    // ── 强制项 5：Tool Schema 合法 ──────────────────────────────────

    private void CheckToolSchemasValid(List<ProductionAdmissionCheck> checks)
    {
        var catalog = _services.GetService<IToolCatalog>();
        if (catalog is null)
        {
            checks.Add(Fail("tool-schema-valid",
                "IToolCatalog 未注册——无法校验 Tool Schema。请注册工具目录（ToolMode=RealDispatch）。"));
            return;
        }

        var definitions = catalog.GetToolDefinitions();
        if (definitions.Count == 0)
        {
            checks.Add(Fail("tool-schema-valid",
                "工具目录为空——ProductionHA 要求至少注册一个工具。请通过 DI 注册 IToolHandler 实现。"));
            return;
        }

        var invalid = new List<string>();
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                invalid.Add("(空名称)");
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.ParametersJsonSchema))
            {
                invalid.Add($"{definition.Name}: 空 JSON Schema");
                continue;
            }

            // 复用运行时实际校验器（模型调用前发送 Tool Schema 时执行的同一校验），
            // 避免准入侧出现与运行时不一致的 Schema 判定（如未强制 type=object）。
            if (!HttpChatCompletionAdapterBase.IsValidJsonSchema(definition.ParametersJsonSchema, out var schemaError))
            {
                invalid.Add($"{definition.Name}: {schemaError}");
            }
        }

        checks.Add(invalid.Count == 0
            ? Pass("tool-schema-valid", $"全部 {definitions.Count} 个工具 Schema 合法（名称 + JSON Schema 通过运行时校验器）。")
            : Fail("tool-schema-valid", $"以下工具 Schema 非法：{string.Join("；", invalid)}。"));
    }

    // ── 强制项 6：至少一条原生 Tool Calling 路由 ─────────────────────

    private void CheckNativeToolCallingRoute(List<ProductionAdmissionCheck> checks)
    {
        var gateway = _services.GetService<IModelGateway>();
        if (gateway is null)
        {
            checks.Add(Fail("native-tool-calling-route",
                "IModelGateway 未注册——Agent 无法调用真实模型。请配置 ModelGateway 节并注册网关。"));
            return;
        }

        var transport = _services.GetService<IAgentModelTransport>();
        var transportIsReal = transport is not null
            && !transport.GetType().Name.Equals(nameof(DeterministicAgentModelTransport), StringComparison.Ordinal);

        var gaps = new List<string>();
        if (!transportIsReal)
        {
            gaps.Add("Agent 模型 transport 未注册或仍为 Deterministic 回退（ProductionHA 强制 AgentModelMode=RealModel）");
        }

        // ConfigurableModelGateway（真实部署）：深度能力探针——
        // Resolved（路由解析出模型）→ Reachable（模型启用 + API 密钥可解析）→
        // Applied（适配器已注册且支持原生 Tool Calling）。
        var gatewayOptions = _services.GetService<ModelGatewayOptions>();
        var detail = gateway is ConfigurableModelGateway configurable && gatewayOptions is not null
            ? ProbeConfigurableGatewayRoute(configurable, gatewayOptions, gaps)
            : ProbeGatewayConfigurationShallow(gatewayOptions, gaps);

        checks.Add(gaps.Count == 0
            ? Pass("native-tool-calling-route",
                $"原生 Tool Calling 路由就绪：{transport!.GetType().Name}，{detail}。")
            : Fail("native-tool-calling-route", string.Join("；", gaps) + "。"));
    }

    /// <summary>对可配置网关做深度能力探测（路由解析 + 密钥 + 适配器原生能力）。</summary>
    private static string ProbeConfigurableGatewayRoute(
        ConfigurableModelGateway gateway,
        ModelGatewayOptions options,
        List<string> gaps)
    {
        var effectiveOptions = ModelGatewayOptionsMaterializer.Materialize(options);
        var enabledCount = effectiveOptions.Models.Count(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Name));
        if (enabledCount == 0)
        {
            gaps.Add("ModelGateway 未配置任何启用模型端点");
            return "启用模型 0 个";
        }

        // Resolved：按 Agent 默认角色解析一条路由（与 ModelGatewayAgentModelTransport 的角色一致）。
        var resolution = ModelRouteResolver.Resolve(
            effectiveOptions,
            new ModelRequest { Role = ModelRole.Fallback });
        if (!resolution.Primary.Found || string.IsNullOrWhiteSpace(resolution.Primary.ModelName))
        {
            gaps.Add($"模型路由解析失败：{resolution.Primary.Reason}");
            return $"启用模型 {enabledCount} 个，路由 {effectiveOptions.Routes.Count} 条";
        }

        var modelName = resolution.Primary.ModelName!;
        var modelOptions = effectiveOptions.Models.FirstOrDefault(m =>
            string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (modelOptions is null)
        {
            gaps.Add($"路由解析到的模型 '{modelName}' 未在配置中找到");
            return $"启用模型 {enabledCount} 个，路由 {effectiveOptions.Routes.Count} 条";
        }

        // Reachable：模型启用 + API 密钥可解析（与 ModelHealthService 预检口径一致，不发起网络请求）。
        if (!modelOptions.Enabled)
        {
            gaps.Add($"路由解析到的模型 '{modelName}' 已禁用");
        }
        var apiKey = new ApiKeyResolver().Resolve(modelOptions);
        if (apiKey.Required && !apiKey.Configured)
        {
            var source = string.IsNullOrWhiteSpace(apiKey.EnvironmentVariableName)
                ? "ApiKey"
                : $"环境变量 '{apiKey.EnvironmentVariableName}'";
            gaps.Add($"模型 '{modelName}' 需要 API 密钥但未配置（{source}）");
        }

        // 能力：适配器已注册且支持原生 Tool Calling（IChatCompletionAdapter）。
        if (!gateway.HasAdapter(modelName))
        {
            gaps.Add($"模型 '{modelName}' 无对应适配器");
        }
        else if (!gateway.SupportsNativeToolCalling(modelName))
        {
            gaps.Add($"模型 '{modelName}' 的适配器不支持原生 Tool Calling");
        }

        return $"解析模型 '{modelName}'（{effectiveOptions.Routes.Count} 条路由，{enabledCount} 个启用模型）";
    }

    /// <summary>非可配置网关（测试/扩展实现）：退化为配置级浅探针。</summary>
    private static string ProbeGatewayConfigurationShallow(
        ModelGatewayOptions? gatewayOptions,
        List<string> gaps)
    {
        var enabledModels = gatewayOptions?.Models
            .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.Name))
            .ToList() ?? [];
        var routeCount = gatewayOptions?.Routes.Count ?? 0;
        if (enabledModels.Count == 0)
        {
            gaps.Add("ModelGateway 未配置任何启用模型端点");
        }
        if (routeCount == 0)
        {
            gaps.Add("ModelGateway 未配置任何角色路由");
        }
        return $"启用模型 {enabledModels.Count} 个，路由 {routeCount} 条";
    }

    // ── 强制项 7：Cluster Model Slot 已应用模型 ─────────────────────

    private async Task CheckModelSlotAppliedAsync(
        List<ProductionAdmissionCheck> checks,
        CancellationToken cancellationToken)
    {
        var slotStore = _services.GetService<IClusterModelSlotStore>();
        if (slotStore is null)
        {
            checks.Add(Fail("model-slot-applied",
                "IClusterModelSlotStore 未注册——ProductionHA 要求 Postgres 存储提供 cluster model slot。"));
            return;
        }

        try
        {
            var slot = await slotStore.GetAsync("primary", cancellationToken).ConfigureAwait(false);
            if (slot is null)
            {
                checks.Add(Fail("model-slot-applied",
                    "cluster model slot 'primary' 不存在——尚未设置期望模型。请通过 Model Control Plane 激活模型。"));
            }
            else if (slot.DesiredStatus != ClusterModelSlotDesiredStatus.Active
                || string.IsNullOrWhiteSpace(slot.ActiveModelArtifactId))
            {
                checks.Add(Fail("model-slot-applied",
                    $"cluster model slot 'primary' 未应用模型（DesiredStatus={slot.DesiredStatus}，"
                    + $"ActiveModelArtifactId='{slot.ActiveModelArtifactId ?? "(空)"}'）。请激活模型。"));
            }
            else
            {
                checks.Add(Pass("model-slot-applied",
                    $"cluster model slot 'primary' 已应用模型 {slot.ActiveModelArtifactId}（revision={slot.Revision}）。"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(Fail("model-slot-applied",
                $"cluster model slot 查询失败：{ex.GetType().Name}: {ex.Message}"));
        }
    }

    // ── 强制项 8：Late Hydration 管道完整 ───────────────────────────

    private void CheckHydrationPipelineComplete(List<ProductionAdmissionCheck> checks)
    {
        var hydrator = _services.GetService<ISelectedCandidateHydrator>();
        var contextLookup = _services.GetService<IContextStoreBatchLookup>();
        var memoryLookup = _services.GetService<IMemoryStoreBatchLookup>();

        if (hydrator is null)
        {
            checks.Add(Fail("hydration-pipeline-complete",
                "ISelectedCandidateHydrator 未注册——Late Hydration 管道缺失，IncludeContent=false 时候选正文为空。"));
        }
        else if (contextLookup is null && memoryLookup is null)
        {
            checks.Add(Fail("hydration-pipeline-complete",
                "ISelectedCandidateHydrator 已注册但 IContextStoreBatchLookup + IMemoryStoreBatchLookup 均未注册——hydrator 为 no-op，无法批量回填正文。"));
        }
        else
        {
            checks.Add(Pass("hydration-pipeline-complete",
                $"Late Hydration 管道完整（{hydrator.GetType().Name}，batch lookup: "
                + $"{(contextLookup is not null ? "context" : string.Empty)}"
                + $"{(contextLookup is not null && memoryLookup is not null ? "+" : string.Empty)}"
                + $"{(memoryLookup is not null ? "memory" : string.Empty)}）。"));
        }
    }

    // ── 强制项 9：ProductionHA Worker 集群已注册 ────────────────────

    private void CheckWorkerFleetStarted(List<ProductionAdmissionCheck> checks)
    {
        var expected = new List<string>
        {
            nameof(AgentRunRecoveryWorker),
            nameof(PostgresPendingRunClaimer),
            nameof(LearningMaterializationWorker),
            nameof(ToolReconciliationWorker),
            nameof(CanaryLeaderHostedService),
            nameof(ModelStateReconcilerWorker)
        };
        if (!_runtimeOptions.EnableAgentRunRecovery)
        {
            expected.Remove(nameof(AgentRunRecoveryWorker));
            expected.Remove(nameof(PostgresPendingRunClaimer));
        }

        var registered = _workerRegistry.WorkerTypeNames;
        var missing = expected.Where(name => !registered.Contains(name, StringComparer.Ordinal)).ToList();
        if (missing.Count > 0)
        {
            checks.Add(Fail("worker-fleet-started",
                $"以下预期 Worker 未注册：{string.Join("，", missing)}。"));
            return;
        }

        // 最近心跳：仅注册类型名不足以证明集群存活——后台心跳服务必须仍在周期
        // 报告存活（应用挂起/Worker 未随应用启动/托管服务停止时心跳过期即 Fail）。
        if (!_workerRegistry.IsFleetHeartbeatFresh(_workerHeartbeatWindow))
        {
            checks.Add(Fail("worker-fleet-started",
                $"ProductionHA Worker 集群已注册但心跳已过期（最近心跳 {DescribeHeartbeatAge()}）"
                + "——后台服务未在窗口内报告存活，集群可能挂起或未随应用启动，拒绝上线。"));
            return;
        }

        checks.Add(Pass("worker-fleet-started",
            $"ProductionHA Worker 集群已注册并随应用启动（{expected.Count} 个预期 Worker 全部就位，"
            + $"心跳在 {_workerHeartbeatWindow.TotalMinutes:0.#} 分钟窗口内）。"));
    }

    private string DescribeHeartbeatAge()
    {
        var last = _workerRegistry.LastHeartbeatUtc;
        if (last == DateTimeOffset.MinValue)
        {
            return "从未";
        }
        var age = DateTimeOffset.UtcNow - last;
        return age.TotalSeconds < 60 ? $"{age.TotalSeconds:0.#} 秒前" : $"{age.TotalMinutes:0.#} 分钟前";
    }

    // ── 结果构造辅助 ────────────────────────────────────────────────

    private static ProductionAdmissionCheck Pass(string name, string message)
        => new(name, ProductionAdmissionCheckStatus.Pass, message);

    private static ProductionAdmissionCheck Fail(string name, string message)
        => new(name, ProductionAdmissionCheckStatus.Fail, message);
}
