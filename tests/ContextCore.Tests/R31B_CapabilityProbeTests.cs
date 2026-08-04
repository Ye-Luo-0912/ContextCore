using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Service;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// 生产准入能力探针深度化测试
//
// 覆盖：
// 1. 原生 Tool Calling 路由探针的深度路径（ConfigurableModelGateway）：
//    路由解析（Resolved）→ API 密钥可解析（Reachable）→ 适配器支持原生
//    Tool Calling（Applied 能力）；任一步缺失即 Fail。
// 2. 深度路径各失败场景：密钥缺失 / 适配器不支持原生能力 / 解析模型已禁用。
// 3. Worker 集群「最近心跳」检查：心跳过期（超过窗口）时 worker-fleet-started Fail。
// 4. 非 ConfigurableModelGateway（测试/扩展实现）继续走浅探针路径，
//    由既有生产准入校验器测试用例覆盖，不在此重复。
//
// 说明：ConfigurableModelGateway 使用 ModelAdapterFactory 创建真实适配器实例
// （不发起网络请求）；准入校验不触发任何真实推理 / 分派。
// ===========================================================================

[TestClass]
[TestCategory("Service")]
[TestCategory("Production")]
[TestCategory("Admission")]
public sealed class R31B_CapabilityProbeTests
{
    [TestMethod]
    public async Task ConfigurableGateway_DeepProbe_Passes_WhenRouteResolvedReachableAndNative()
    {
        var report = await ValidateFullyConfiguredAsync(
            mutate: services => RegisterConfigurableGateway(
                services,
                BuildGatewayOptions(
                    modelName: "gpt-4o",
                    provider: "openai-compatible",
                    apiKey: "test-key",
                    enabled: true)));

        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Pass,
            GetCheck(report, "native-tool-calling-route").Status);
    }

    [TestMethod]
    public async Task ConfigurableGateway_DeepProbe_Fails_WhenApiKeyMissing()
    {
        var report = await ValidateFullyConfiguredAsync(
            mutate: services => RegisterConfigurableGateway(
                services,
                BuildGatewayOptions(
                    modelName: "gpt-4o",
                    provider: "openai-compatible",
                    apiKey: null,
                    enabled: true)));

        Assert.IsFalse(report.AllPassed);
        var check = GetCheck(report, "native-tool-calling-route");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, check.Status);
        StringAssert.Contains(check.Message, "API 密钥");
    }

    [TestMethod]
    public async Task ConfigurableGateway_DeepProbe_Fails_WhenAdapterNotNative()
    {
        // mock 提供商只生成普通适配器（不支持原生 Tool Calling）。
        var report = await ValidateFullyConfiguredAsync(
            mutate: services => RegisterConfigurableGateway(
                services,
                BuildGatewayOptions(
                    modelName: "mock-model",
                    provider: "mock",
                    apiKey: null,
                    enabled: true)));

        Assert.IsFalse(report.AllPassed);
        var check = GetCheck(report, "native-tool-calling-route");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, check.Status);
        StringAssert.Contains(check.Message, "不支持原生 Tool Calling");
    }

    [TestMethod]
    public async Task ConfigurableGateway_DeepProbe_Fails_WhenResolvedModelDisabled()
    {
        var report = await ValidateFullyConfiguredAsync(
            mutate: services => RegisterConfigurableGateway(
                services,
                BuildGatewayOptions(
                    modelName: "gpt-4o",
                    provider: "openai-compatible",
                    apiKey: "test-key",
                    enabled: false)));

        Assert.IsFalse(report.AllPassed);
        var check = GetCheck(report, "native-tool-calling-route");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, check.Status);
        // 唯一模型已禁用 → 启用模型数为零，深度探针在 Resolved 阶段即报告无可用端点。
        StringAssert.Contains(check.Message, "未配置任何启用模型端点");
    }

    [TestMethod]
    public async Task ConfigurableGateway_DeepProbe_Fails_WhenRouteTargetsUnknownModel()
    {
        var options = new ModelGatewayOptions
        {
            Models =
            [
                new ModelEndpointOptions
                {
                    Name = "gpt-4o",
                    Provider = "openai-compatible",
                    Endpoint = "http://localhost:9999/v1",
                    ApiKey = "test-key",
                    Enabled = true
                }
            ],
            Routes = [new ModelRoleRoute { Role = ModelRole.Fallback, PrimaryModelName = "missing-model" }]
        };

        var report = await ValidateFullyConfiguredAsync(
            mutate: services => RegisterConfigurableGateway(services, options));

        Assert.IsFalse(report.AllPassed);
        var check = GetCheck(report, "native-tool-calling-route");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, check.Status);
        StringAssert.Contains(check.Message, "模型路由解析失败");
    }

    [TestMethod]
    public async Task WorkerFleet_StaleHeartbeat_FailsAdmission()
    {
        // 心跳窗口为零：注册后必然已过期 → 集群虽已注册但心跳不新鲜 → Fail。
        var report = await ValidateFullyConfiguredAsync(
            mutate: null,
            workerHeartbeatWindow: TimeSpan.Zero);

        Assert.IsFalse(report.AllPassed);
        var check = GetCheck(report, "worker-fleet-started");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, check.Status);
        StringAssert.Contains(check.Message, "心跳");
    }

    [TestMethod]
    public async Task WorkerFleet_FreshHeartbeat_PassesAdmission()
    {
        // 默认窗口（10 分钟）：注册即初次心跳，校验紧随其后 → 新鲜 → Pass。
        var report = await ValidateFullyConfiguredAsync(mutate: null);

        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Pass,
            GetCheck(report, "worker-fleet-started").Status);
    }

    // ── 测试辅助 ────────────────────────────────────────────────────────

    private static ModelGatewayOptions BuildGatewayOptions(
        string modelName,
        string provider,
        string? apiKey,
        bool enabled)
        => new()
        {
            Models =
            [
                new ModelEndpointOptions
                {
                    Name = modelName,
                    Provider = provider,
                    Endpoint = "http://localhost:9999/v1",
                    ApiKey = apiKey,
                    Enabled = enabled
                }
            ],
            Routes = [new ModelRoleRoute { Role = ModelRole.Fallback, PrimaryModelName = modelName }]
        };

    private static void RegisterConfigurableGateway(
        IServiceCollection services,
        ModelGatewayOptions options)
    {
        var adapters = ModelAdapterFactory.CreateAdapters(options);
        services.AddSingleton(options);
        services.AddSingleton<IModelGateway>(new ConfigurableModelGateway(options, adapters));
    }

    private static async Task<ProductionAdmissionReport> ValidateFullyConfiguredAsync(
        Action<IServiceCollection>? mutate,
        TimeSpan? workerHeartbeatWindow = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var security = SecurityWith();
        services.AddSingleton(security);
        var runtime = new ContextCoreRuntimeOptions { Profile = RuntimeProfile.ProductionHA };
        services.AddSingleton(runtime);
        var registry = FullWorkerRegistry();
        services.AddSingleton(registry);
        AddFullProductionSetup(services);
        mutate?.Invoke(services);

        using var provider = services.BuildServiceProvider();
        var validator = new ProductionAdmissionValidator(
            provider,
            runtime,
            registry,
            security,
            NullLogger<ProductionAdmissionValidator>.Instance,
            workerHeartbeatWindow);
        return await validator.ValidateAsync();
    }

    private static ProductionRuntimeWorkerRegistry FullWorkerRegistry()
    {
        var registry = new ProductionRuntimeWorkerRegistry();
        registry.Add<AgentRunRecoveryWorker>();
        registry.Add<PostgresPendingRunClaimer>();
        registry.Add<LearningMaterializationWorker>();
        registry.Add<ToolReconciliationWorker>();
        registry.Add<CanaryLeaderHostedService>();
        registry.Add<ModelStateReconcilerWorker>();
        return registry;
    }

    private static void AddFullProductionSetup(IServiceCollection services)
    {
        services.AddSingleton<IToolDispatcher>(new FakeToolDispatcher(
            ("read_file", false),
            ("shell_exec", true)));
        services.AddSingleton<IToolCatalog>(new FakeToolCatalog(
            new AgentToolDefinition
            {
                Name = "read_file",
                ParametersJsonSchema = """{"type":"object"}"""
            },
            new AgentToolDefinition
            {
                Name = "shell_exec",
                ParametersJsonSchema = """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""
            }));
        services.AddSingleton<IModelGateway>(new FakeModelGateway());
        services.AddSingleton<IAgentModelTransport>(new FakeAgentModelTransport());
        services.AddSingleton(new ModelGatewayOptions
        {
            Models = [new ModelEndpointOptions { Name = "gpt-4o", Enabled = true }],
            Routes = [new ModelRoleRoute { Role = ModelRole.Fallback, PrimaryModelName = "gpt-4o" }]
        });
        services.AddSingleton<IClusterModelSlotStore>(new FakeClusterModelSlotStore(
            new ClusterModelSlot
            {
                SlotName = "primary",
                ActiveModelArtifactId = "artifact-7f3a",
                Revision = 3,
                DesiredStatus = ClusterModelSlotDesiredStatus.Active,
                UpdatedAt = DateTimeOffset.UtcNow
            }));
        services.AddSingleton<ISelectedCandidateHydrator>(new FakeSelectedCandidateHydrator());
        services.AddSingleton<IContextStoreBatchLookup>(new FakeContextBatchLookup());
    }

    private static SecurityOptions SecurityWith()
        => new()
        {
            RequireApiKey = true,
            ApiKey = "test-secret-key",
            Workspace = new WorkspaceContextOptions { RequireExplicitWorkspace = true },
            Rbac = new RbacOptions { Enforce = true },
            ApprovalPolicy = new ApprovalPolicyOptions
            {
                Enabled = true,
                ApprovalRequiredTools = ["shell_exec", "file_delete"]
            }
        };

    private static ProductionAdmissionCheck GetCheck(ProductionAdmissionReport report, string name)
        => report.Checks.Single(c => c.Name == name);

    // ── 内存假实现（不触发真实 IO / 推理） ───────────────────────────────

    private sealed class FakeToolDispatcher : IToolDispatcher
    {
        private readonly Dictionary<string, ToolDescriptor> _descriptors;

        public FakeToolDispatcher(params (string Name, bool RequiresApproval)[] tools)
        {
            _descriptors = tools.ToDictionary(
                t => t.Name,
                t => new ToolDescriptor { Name = t.Name, RequiresApproval = t.RequiresApproval },
                StringComparer.Ordinal);
        }

        public IReadOnlySet<string> SupportedTools
            => new HashSet<string>(_descriptors.Keys, StringComparer.Ordinal);

        public ToolDescriptor? GetDescriptor(string toolName)
            => _descriptors.TryGetValue(toolName, out var descriptor) ? descriptor : null;

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发实际 Tool 分派。");
    }

    private sealed class FakeToolCatalog : IToolCatalog
    {
        private readonly IReadOnlyList<AgentToolDefinition> _definitions;

        public FakeToolCatalog(params AgentToolDefinition[] definitions) => _definitions = definitions;

        public IReadOnlyList<AgentToolDefinition> GetToolDefinitions() => _definitions;
    }

    private sealed class FakeModelGateway : IModelGateway
    {
        public Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实推理。");

        public Task<ModelChatResponse> ChatWithToolsAsync(
            ModelChatRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实推理。");
    }

    private sealed class FakeAgentModelTransport : IAgentModelTransport
    {
        public ValueTask<AgentModelResponse> CallAsync(
            string runId,
            string context,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<AgentModelResponse> CallAsync(
            string runId,
            IReadOnlyList<AgentMessage> messages,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public ValueTask<AgentModelResponse> CallAsync(
            AgentModelRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeClusterModelSlotStore : IClusterModelSlotStore
    {
        private readonly ClusterModelSlot? _slot;

        public FakeClusterModelSlotStore(ClusterModelSlot? slot) => _slot = slot;

        public ValueTask<ClusterModelSlot?> GetAsync(string slotName, CancellationToken ct = default)
            => ValueTask.FromResult(_slot);

        public ValueTask<ClusterModelSlot?> TryUpdateAsync(
            string slotName,
            long expectedRevision,
            string? activeModelArtifactId,
            string? contentHash,
            ClusterModelSlotDesiredStatus desiredStatus,
            string? updatedBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public ValueTask<ClusterModelSlot> GetOrCreateAsync(string slotName, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSelectedCandidateHydrator : ISelectedCandidateHydrator
    {
        public ValueTask<HydrationResult> HydrateAsync(
            IReadOnlyList<ContextCandidateEnvelope> selectedEnvelopes,
            CandidateWorkingSet workingSet,
            int tokenBudget = 0,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实 hydrate。");
    }

    private sealed class FakeContextBatchLookup : IContextStoreBatchLookup
    {
        public Task<IReadOnlyList<ContextItem>> BatchGetAsync(
            string workspaceId,
            string collectionId,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
