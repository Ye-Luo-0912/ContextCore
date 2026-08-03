using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Production Admission Validator 单元测试
//
// 覆盖：
// 1. AdmissionRequired_IsTrueOnlyForProductionHA — 仅 ProductionHA profile
// 需要准入校验，Development / SingleNode 不要求。
// 2. NonProductionProfile_SkipsChecksAndAlwaysPasses — 非 ProductionHA
// 返回单条 Skipped 检查且 AllPassed=true。
// 3. ProductionHA_AllNineMandatoryChecksPass_WhenFullyConfigured — 完整
// 生产配置下 9 项强制项全部 Pass，且检查名称与顺序符合预期。
// 4. 逐项失败 — 每项强制项单独破坏时对应检查 Fail 且报告 AllPassed=false。
//
// 说明：所有依赖（IToolDispatcher / IToolCatalog / IModelGateway /
// IAgentModelTransport / IClusterModelSlotStore / hydrator / batch lookup）
// 均为内存假实现，不触发真实分派 / 推理 / 存储 IO；Worker 注册表直接
// 注册真实 Hosting 类型名（与 ProductionRuntimeExtensions 的注册一致）。
// ===========================================================================

[TestClass]
[TestCategory("Service")]
[TestCategory("Production")]
[TestCategory("Admission")]
public sealed class R29O_ProductionAdmissionValidatorTests
{
    [TestMethod]
    public void AdmissionRequired_IsTrueOnlyForProductionHA()
    {
        Assert.IsFalse(BuildValidator(RuntimeProfile.Development).AdmissionRequired);
        Assert.IsFalse(BuildValidator(RuntimeProfile.SingleNode).AdmissionRequired);
        Assert.IsTrue(BuildValidator(RuntimeProfile.ProductionHA).AdmissionRequired);
    }

    [TestMethod]
    public async Task NonProductionProfile_SkipsChecksAndAlwaysPasses()
    {
        var validator = BuildValidator(RuntimeProfile.Development);
        var report = await validator.ValidateAsync();

        Assert.IsFalse(report.AdmissionRequired);
        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(1, report.Checks.Count);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Skipped, report.Checks[0].Status);
    }

    [TestMethod]
    public async Task ProductionHA_AllNineMandatoryChecksPass_WhenFullyConfigured()
    {
        var report = await ValidateFullyConfiguredAsync(FullWorkerRegistry());

        Assert.IsTrue(report.AdmissionRequired);
        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(9, report.Checks.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                "api-key-configured",
                "explicit-workspace",
                "rbac-enforced",
                "approval-and-high-risk-tool-coverage",
                "tool-schema-valid",
                "native-tool-calling-route",
                "model-slot-applied",
                "hydration-pipeline-complete",
                "worker-fleet-started"
            },
            report.Checks.Select(c => c.Name).ToArray());
    }

    [TestMethod]
    public async Task ApiKeyMissing_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            security: SecurityWith(apiKey: "   "));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "api-key-configured").Status);
    }

    [TestMethod]
    public async Task ApiKeyAuthDisabled_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            security: SecurityWith(requireApiKey: false));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "api-key-configured").Status);
    }

    [TestMethod]
    public async Task ExplicitWorkspaceMissing_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            security: SecurityWith(explicitWorkspace: false));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "explicit-workspace").Status);
    }

    [TestMethod]
    public async Task RbacNotEnforced_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            security: SecurityWith(enforceRbac: false));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "rbac-enforced").Status);
    }

    [TestMethod]
    public async Task ApprovalPolicyDisabled_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            security: SecurityWith(approvalEnabled: false));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Fail,
            GetCheck(report, "approval-and-high-risk-tool-coverage").Status);
    }

    [TestMethod]
    public async Task HighRiskToolWithoutApprovalCoverage_FailsAdmission()
    {
        // ApprovalRequiredTools 只覆盖 read_file，未覆盖声明 RequiresApproval 的 shell_exec。
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            security: SecurityWith(approvalTools: ["read_file"]));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Fail,
            GetCheck(report, "approval-and-high-risk-tool-coverage").Status);
    }

    [TestMethod]
    public async Task InvalidToolSchema_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IToolCatalog>(
                new FakeToolCatalog(
                    new AgentToolDefinition
                    {
                        Name = "broken_tool",
                        ParametersJsonSchema = "{not-json"
                    })));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "tool-schema-valid").Status);
    }

    [TestMethod]
    public async Task EmptyToolCatalog_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IToolCatalog>(new FakeToolCatalog()));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "tool-schema-valid").Status);
    }

    [TestMethod]
    public async Task ModelGatewayMissing_FailsNativeToolCallingRoute()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IModelGateway>(_ => null!));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "native-tool-calling-route").Status);
    }

    [TestMethod]
    public async Task DeterministicTransport_FailsNativeToolCallingRoute()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport()));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "native-tool-calling-route").Status);
    }

    [TestMethod]
    public async Task ModelSlotMissing_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IClusterModelSlotStore>(new FakeClusterModelSlotStore(null)));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "model-slot-applied").Status);
    }

    [TestMethod]
    public async Task ModelSlotInactive_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IClusterModelSlotStore>(
                new FakeClusterModelSlotStore(
                    new ClusterModelSlot
                    {
                        SlotName = "primary",
                        Revision = 0,
                        DesiredStatus = ClusterModelSlotDesiredStatus.Inactive,
                        UpdatedAt = DateTimeOffset.UtcNow
                    })));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "model-slot-applied").Status);
    }

    [TestMethod]
    public async Task ModelSlotStoreThrows_FailsAdmissionInsteadOfPropagating()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<IClusterModelSlotStore>(new FakeThrowingSlotStore()));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "model-slot-applied").Status);
    }

    [TestMethod]
    public async Task HydratorMissing_FailsAdmission()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services => services.AddSingleton<ISelectedCandidateHydrator>(_ => null!));

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Fail,
            GetCheck(report, "hydration-pipeline-complete").Status);
    }

    [TestMethod]
    public async Task HydratorWithoutBatchLookup_FailsAdmission()
    {
        // hydrator 已注册但 context + memory lookup 均缺失 → hydrator 为 no-op。
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services =>
            {
                services.AddSingleton<IContextStoreBatchLookup>(_ => null!);
                services.AddSingleton<IMemoryStoreBatchLookup>(_ => null!);
            });

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Fail,
            GetCheck(report, "hydration-pipeline-complete").Status);
    }

    [TestMethod]
    public async Task MemoryOnlyBatchLookup_SatisfiesHydrationPipeline()
    {
        var report = await ValidateFullyConfiguredAsync(
            FullWorkerRegistry(),
            mutate: services =>
            {
                services.AddSingleton<IContextStoreBatchLookup>(_ => null!);
                services.AddSingleton<IMemoryStoreBatchLookup>(new FakeMemoryBatchLookup());
            });

        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(
            ProductionAdmissionCheckStatus.Pass,
            GetCheck(report, "hydration-pipeline-complete").Status);
    }

    [TestMethod]
    public async Task WorkerMissing_FailsAdmission()
    {
        var registry = new ProductionRuntimeWorkerRegistry();
        registry.Add<AgentRunRecoveryWorker>();
        registry.Add<LearningMaterializationWorker>();
        registry.Add<CanaryLeaderHostedService>();
        registry.Add<ModelStateReconcilerWorker>();
        // 故意漏掉 ToolReconciliationWorker，验证 Worker 集群检查能捕获缺失。

        var report = await ValidateFullyConfiguredAsync(registry);

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "worker-fleet-started").Status);
    }

    // ── 测试辅助 ────────────────────────────────────────────────────────

    private static ProductionAdmissionValidator BuildValidator(RuntimeProfile profile)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var security = new SecurityOptions();
        services.AddSingleton(security);
        var runtime = new ContextCoreRuntimeOptions { Profile = profile };
        services.AddSingleton(runtime);
        var registry = new ProductionRuntimeWorkerRegistry();
        services.AddSingleton(registry);

        var provider = services.BuildServiceProvider();
        return new ProductionAdmissionValidator(
            provider,
            runtime,
            registry,
            security,
            NullLogger<ProductionAdmissionValidator>.Instance);
    }

    /// <summary>以完整生产配置构建 ProductionHA validator 并执行准入校验。</summary>
    /// <param name="registry">Worker 注册表（已按预期填充）。</param>
    /// <param name="security">安全配置；null = 全通过配置。</param>
    /// <param name="mutate">在完整配置之后追加 / 覆盖注册的服务（用于构造单项失败场景）。</param>
    private static async Task<ProductionAdmissionReport> ValidateFullyConfiguredAsync(
        ProductionRuntimeWorkerRegistry registry,
        SecurityOptions? security = null,
        Action<IServiceCollection>? mutate = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var effectiveSecurity = security ?? SecurityWith();
        services.AddSingleton(effectiveSecurity);
        var runtime = new ContextCoreRuntimeOptions { Profile = RuntimeProfile.ProductionHA };
        services.AddSingleton(runtime);
        services.AddSingleton(registry);
        AddFullProductionSetup(services);
        mutate?.Invoke(services);

        using var provider = services.BuildServiceProvider();
        var validator = new ProductionAdmissionValidator(
            provider,
            runtime,
            registry,
            effectiveSecurity,
            NullLogger<ProductionAdmissionValidator>.Instance);
        return await validator.ValidateAsync();
    }

    private static ProductionRuntimeWorkerRegistry FullWorkerRegistry()
    {
        var registry = new ProductionRuntimeWorkerRegistry();
        registry.Add<AgentRunRecoveryWorker>();
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

    private static SecurityOptions SecurityWith(
        bool requireApiKey = true,
        string apiKey = "test-secret-key",
        bool explicitWorkspace = true,
        bool enforceRbac = true,
        bool approvalEnabled = true,
        IReadOnlyList<string>? approvalTools = null)
        => new()
        {
            RequireApiKey = requireApiKey,
            ApiKey = apiKey,
            Workspace = new WorkspaceContextOptions { RequireExplicitWorkspace = explicitWorkspace },
            Rbac = new RbacOptions { Enforce = enforceRbac },
            ApprovalPolicy = new ApprovalPolicyOptions
            {
                Enabled = approvalEnabled,
                ApprovalRequiredTools = approvalTools ?? ["shell_exec", "file_delete"]
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

    private sealed class FakeThrowingSlotStore : IClusterModelSlotStore
    {
        public ValueTask<ClusterModelSlot?> GetAsync(string slotName, CancellationToken ct = default)
            => throw new InvalidOperationException("模拟 slot store 故障。");

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

    private sealed class FakeMemoryBatchLookup : IMemoryStoreBatchLookup
    {
        public Task<IReadOnlyList<ContextMemoryItem>> BatchGetAsync(
            string workspaceId,
            string collectionId,
            IReadOnlyList<string> ids,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
