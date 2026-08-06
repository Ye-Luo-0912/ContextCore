using System.Net;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Inference.Onnx;
using ContextCore.Service;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.InMemory.Stores;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ContextCore.Tests;

// ===========================================================================
// P0-14 节点级 Admission：Applied State + 真实 IModelActivationManager
//
// 问题：Production Admission 的 model-slot-applied 只验证集群 Desired 状态
// （DesiredStatus==Active && ActiveModelArtifactId 非空），未验证当前节点是否
// 真正应用并加载了期望模型——"集群期望 A、本节点尚未加载模型或仍运行 B" 的
// 节点会通过准入并开始接流量。
//
// 修复：ProductionAdmissionController 的实时探针 model-slot-live 在集群 Desired
// 正常之后，追加 VerifyNodeModelAppliedAsync 节点级校验：
//   1. IModelActivationManager 未注册（ModelMode=Deterministic）→ 保持集群语义 Pass；
//   2. 已启用模型激活时，要求 IModelNodeAppliedStateStore 已注册；
//   3. 节点已上报 Applied State（无记录 = Fail）；
//   4. 节点未隔离（Isolated = Fail）；
//   5. 节点 AppliedRevision 与集群期望 Revision 一致（!= 即 Fail，前后落后均拒绝）；
//   6. 本地引擎 ActiveEngine 非空（可推理）；
//   7. 引擎加载的 ModelArtifactId 与集群期望一致；
//   8. 集群 ContentHash 非空时引擎 ContentHash 必须一致（旧数据为空则跳过）。
// 任一不满足 → model-slot-live Fail → 中间件 503，节点不接流量。
// ===========================================================================

[TestClass]
[TestCategory("Service")]
[TestCategory("Production")]
[TestCategory("Admission")]
public sealed class R30H_AdmissionNodeAppliedStateTests
{
    // ── 控制器单元测试 ─────────────────────────────────────────────────

    [TestMethod]
    public async Task ActivationManagerNotRegistered_NodeChecksSkipped_AllPassed()
    {
        // ModelMode=Deterministic（未注册 IModelActivationManager）：保持集群 Desired 语义。
        var harness = await BuildControllerHarnessAsync(registerActivationManager: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsTrue(report.AdmissionRequired);
        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(12, report.Checks.Count, "9 项静态强制项 + 3 项实时探针。");
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Pass, live.Status);
        StringAssert.Contains(live.Message, "未启用模型激活");
    }

    [TestMethod]
    public async Task NodeAppliedAndEngineReady_AllPassed()
    {
        // 集群期望 + 节点已应用 + 引擎已加载期望模型：全部通过。
        var harness = await BuildControllerHarnessAsync();
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsTrue(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Pass, live.Status);
        StringAssert.Contains(live.Message, "已应用并加载期望模型");
    }

    [TestMethod]
    public async Task NoAppliedStateRecord_AdmissionDenied()
    {
        // 节点尚未上报已应用状态（模型可能仍在加载或从未应用）→ Fail。
        var harness = await BuildControllerHarnessAsync(seedAppliedState: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "尚未上报已应用状态");
    }

    [TestMethod]
    public async Task AppliedRevisionBehind_AdmissionDenied()
    {
        // 节点 AppliedRevision=2 < 集群期望 Revision=3 → Fail（节点仍运行旧模型）。
        var harness = await BuildControllerHarnessAsync(
            appliedRevision: 2);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "已应用 Revision=2");
    }

    [TestMethod]
    public async Task AppliedRevisionAhead_AdmissionDenied()
    {
        // AppliedRevision 用 != 比较（fail-closed）：节点超前于集群期望同样拒绝接流量。
        var harness = await BuildControllerHarnessAsync(
            appliedRevision: 4);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "已应用 Revision=4");
    }

    [TestMethod]
    public async Task NodeIsolated_AdmissionDenied()
    {
        // 节点已被漂移隔离 → Fail（隔离节点不得接流量）。
        var harness = await BuildControllerHarnessAsync(
            isolated: true,
            isolationReason: "content drift");
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "已被隔离");
        StringAssert.Contains(live.Message, "content drift");
    }

    [TestMethod]
    public async Task ActiveEngineNull_AdmissionDenied()
    {
        // 集群期望正常但本地引擎未激活（ActiveEngine=null）→ 无法推理，Fail。
        var harness = await BuildControllerHarnessAsync(activeEngineNull: true);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "本地引擎未激活");
    }

    [TestMethod]
    public async Task EngineModelIdMismatch_AdmissionDenied()
    {
        // 集群期望模型 A，本节点引擎仍运行模型 B → Fail。
        var harness = await BuildControllerHarnessAsync(engineModelArtifactId: "artifact-other");
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "引擎加载模型 'artifact-other'");
        StringAssert.Contains(live.Message, "期望 'artifact-7f3a'");
    }

    [TestMethod]
    public async Task EngineContentHashMismatch_AdmissionDenied()
    {
        // 集群期望 ContentHash 与本地引擎内容不一致 → Fail（内容漂移）。
        var harness = await BuildControllerHarnessAsync(engineContentHash: "sha256:drifted");
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "内容哈希 'sha256:drifted'");
        StringAssert.Contains(live.Message, "期望 'sha256:expected'");
    }

    [TestMethod]
    public async Task AppliedStateStoreMissing_AdmissionDenied()
    {
        // 已启用模型激活但 IModelNodeAppliedStateStore 未注册 → 无法校验节点，Fail-closed。
        var harness = await BuildControllerHarnessAsync(registerAppliedStateStore: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "IModelNodeAppliedStateStore 未注册");
    }

    [TestMethod]
    public async Task MembershipStoreMissing_AdmissionDenied()
    {
        // P0-15：已启用模型激活但 IModelNodeMembershipStore 未注册 → 无法校验成员资格，Fail-closed。
        var harness = await BuildControllerHarnessAsync(registerMembershipStore: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "IModelNodeMembershipStore 未注册");
    }

    [TestMethod]
    public async Task MembershipLeaseMissing_AdmissionDenied()
    {
        // P0-15：节点从未心跳（无成员租约记录）→ 不是活跃成员，拒绝接流量。
        var harness = await BuildControllerHarnessAsync(seedMembership: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "无活跃成员租约");
    }

    [TestMethod]
    public async Task MembershipLeaseExpired_AdmissionDenied()
    {
        // P0-15：成员租约已过期（stale cutoff）→ 节点视为已下线，拒绝接流量。
        var harness = await BuildControllerHarnessAsync(
            seedMembership: true,
            membershipLeaseDuration: TimeSpan.Zero);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "无活跃成员租约");
    }

    [TestMethod]
    public async Task MembershipServingDisabled_AdmissionDenied()
    {
        // P0-15：节点被漂移隔离（Reconciler 置 serving_enabled=false）→ 停止接流量，
        // 不能只写 Applied State 的 Isolated 标志。
        var harness = await BuildControllerHarnessAsync(membershipServingEnabled: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        var live = GetCheck(report, "model-slot-live");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, live.Status);
        StringAssert.Contains(live.Message, "已被标记停止服务");
    }

    // ── 请求阶段中间件测试（节点未就绪 → 503，不接流量） ────────────────

    [TestMethod]
    public async Task Middleware_NodeReady_RequestSucceeds()
    {
        var app = await BuildWebAppAsync(nodeReady: true);
        try
        {
            var client = app.GetTestServer().CreateClient();
            var response = await client.GetAsync("/api/business/echo");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Middleware_NodeNotReady_Returns503Json()
    {
        // 集群 Desired 正常但节点未上报 Applied State → 中间件 503，节点不接流量。
        var app = await BuildWebAppAsync(nodeReady: false);
        try
        {
            var client = app.GetTestServer().CreateClient();
            var response = await client.GetAsync("/api/business/echo");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.AreEqual("admission-denied", document.RootElement.GetProperty("status").GetString());
            var failed = document.RootElement.GetProperty("failedChecks");
            Assert.IsTrue(
                failed.EnumerateArray().Any(e => e.GetString() == "model-slot-live"),
                "失败检查列表应包含 model-slot-live（节点级未就绪）。");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // ── 测试辅助 ────────────────────────────────────────────────────────

    private sealed class ControllerHarness
    {
        public required ServiceProvider Provider { get; init; }
        public required ProductionAdmissionValidator Validator { get; init; }
        public required ProductionAdmissionController Controller { get; init; }
        public required CountingClusterModelSlotStore SlotStore { get; init; }
        public required InMemoryModelNodeAppliedStateStore AppliedStateStore { get; init; }
        public required InMemoryModelNodeMembershipStore MembershipStore { get; init; }
        public required FakeModelActivationManager ActivationManager { get; init; }
        public required FakeApplicationLifetime Lifetime { get; init; }
    }

    private static async Task<ControllerHarness> BuildControllerHarnessAsync(
        RuntimeProfile profile = RuntimeProfile.ProductionHA,
        ProductionAdmissionOptions? options = null,
        bool pingSuccess = true,
        bool registerPingFactory = true,
        bool markStarted = true,
        Func<ClusterModelSlot?>? slotFactory = null,
        bool registerActivationManager = true,
        bool registerAppliedStateStore = true,
        bool seedAppliedState = true,
        long appliedRevision = 3,
        bool isolated = false,
        string? isolationReason = null,
        bool activeEngineNull = false,
        string engineModelArtifactId = "artifact-7f3a",
        string engineContentHash = "sha256:expected",
        bool registerMembershipStore = true,
        bool seedMembership = true,
        TimeSpan? membershipLeaseDuration = null,
        bool membershipServingEnabled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var security = SecurityWith();
        services.AddSingleton(security);
        var runtime = new ContextCoreRuntimeOptions { Profile = profile };
        services.AddSingleton(runtime);
        var registry = FullWorkerRegistry();
        services.AddSingleton(registry);
        var slotStore = new CountingClusterModelSlotStore(slotFactory ?? (() => ActiveSlot()));
        services.AddSingleton<IClusterModelSlotStore>(slotStore);
        AddCoreFakes(services);
        if (registerPingFactory)
        {
            services.AddSingleton<IPostgresConnectionFactory>(new FakePingPostgresConnectionFactory(pingSuccess));
        }

        var appliedStateStore = new InMemoryModelNodeAppliedStateStore();
        if (registerAppliedStateStore)
        {
            if (seedAppliedState)
            {
                await appliedStateStore.UpsertAsync(MakeAppliedState(
                    revision: appliedRevision,
                    isolated: isolated,
                    isolationReason: isolationReason)).ConfigureAwait(false);
            }
            services.AddSingleton<IModelNodeAppliedStateStore>(appliedStateStore);
        }

        // P0-15：成员资格存储（默认注册并预置本节点租约；缺失/无租约/serving=false → Fail-closed）。
        var membershipStore = new InMemoryModelNodeMembershipStore();
        if (registerMembershipStore)
        {
            if (seedMembership)
            {
                var lease = membershipLeaseDuration ?? TimeSpan.FromMinutes(5);
                await membershipStore.TryAcquireOrRenewLeaseAsync(
                    NodeIdentity.ResolveNodeGroupId(),
                    NodeIdentity.ResolveInstanceId(),
                    lease,
                    membershipServingEnabled).ConfigureAwait(false);
            }
            services.AddSingleton<IModelNodeMembershipStore>(membershipStore);
        }

        var activationManager = new FakeModelActivationManager
        {
            ActiveEngine = activeEngineNull ? null : FakeEngine.Instance,
            ActiveDescriptor = MakeDescriptor(engineModelArtifactId, engineContentHash),
            ActiveGeneration = activeEngineNull ? null : 1,
            ContentHash = engineContentHash
        };
        if (registerActivationManager)
        {
            services.AddSingleton<IModelActivationManager>(activationManager);
        }

        var provider = services.BuildServiceProvider();
        var validator = new ProductionAdmissionValidator(
            provider,
            runtime,
            registry,
            security,
            NullLogger<ProductionAdmissionValidator>.Instance);
        var lifetime = new FakeApplicationLifetime();
        if (markStarted)
        {
            lifetime.MarkStarted();
        }
        var controller = new ProductionAdmissionController(
            validator,
            provider,
            lifetime,
            options ?? new ProductionAdmissionOptions(),
            NullLogger<ProductionAdmissionController>.Instance);

        return new ControllerHarness
        {
            Provider = provider,
            Validator = validator,
            Controller = controller,
            SlotStore = slotStore,
            AppliedStateStore = appliedStateStore,
            MembershipStore = membershipStore,
            ActivationManager = activationManager,
            Lifetime = lifetime
        };
    }

    private static async Task<WebApplication> BuildWebAppAsync(
        RuntimeProfile profile = RuntimeProfile.ProductionHA,
        bool pingSuccess = true,
        bool nodeReady = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();

        var security = SecurityWith();
        builder.Services.AddSingleton(security);
        var runtime = new ContextCoreRuntimeOptions { Profile = profile };
        builder.Services.AddSingleton(runtime);
        var registry = FullWorkerRegistry();
        builder.Services.AddSingleton(registry);
        var slotStore = new CountingClusterModelSlotStore(() => ActiveSlot());
        builder.Services.AddSingleton<IClusterModelSlotStore>(slotStore);
        AddCoreFakes(builder.Services);
        builder.Services.AddSingleton<IPostgresConnectionFactory>(new FakePingPostgresConnectionFactory(pingSuccess));

        // 节点级栈：Applied State + 真实 IModelActivationManager（nodeReady=false 时无 Applied State）。
        var appliedStateStore = new InMemoryModelNodeAppliedStateStore();
        if (nodeReady)
        {
            await appliedStateStore.UpsertAsync(MakeAppliedState()).ConfigureAwait(false);
        }
        builder.Services.AddSingleton<IModelNodeAppliedStateStore>(appliedStateStore);
        builder.Services.AddSingleton<IModelActivationManager>(new FakeModelActivationManager
        {
            ActiveEngine = FakeEngine.Instance,
            ActiveDescriptor = MakeDescriptor(),
            ActiveGeneration = 1,
            ContentHash = "sha256:expected"
        });

        // P0-15：成员资格租约（中间件场景恒注册；nodeReady 只影响 Applied State，租约恒有效）。
        var membershipStore = new InMemoryModelNodeMembershipStore();
        await membershipStore.TryAcquireOrRenewLeaseAsync(
            NodeIdentity.ResolveNodeGroupId(),
            NodeIdentity.ResolveInstanceId(),
            TimeSpan.FromMinutes(5),
            servingEnabled: true).ConfigureAwait(false);
        builder.Services.AddSingleton<IModelNodeMembershipStore>(membershipStore);

        builder.Services.AddSingleton<ProductionAdmissionOptions>();
        builder.Services.AddSingleton<ProductionAdmissionValidator>(sp =>
            new ProductionAdmissionValidator(
                sp,
                runtime,
                registry,
                security,
                NullLogger<ProductionAdmissionValidator>.Instance));
        builder.Services.AddSingleton<ProductionAdmissionController>(sp =>
            new ProductionAdmissionController(
                sp.GetRequiredService<ProductionAdmissionValidator>(),
                sp,
                sp.GetRequiredService<IHostApplicationLifetime>(),
                sp.GetRequiredService<ProductionAdmissionOptions>(),
                NullLogger<ProductionAdmissionController>.Instance));

        var app = builder.Build();
        app.UseMiddleware<ProductionAdmissionMiddleware>();
        app.MapGet("/api/admission/status", async Task<IResult> (
            ProductionAdmissionController controller,
            bool force = false,
            CancellationToken ct = default) => Results.Ok(await controller.GetOrRefreshAsync(force, ct)));
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/business/echo", () => Results.Ok(new { ok = true }));

        await app.StartAsync();
        return app;
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

    private static void AddCoreFakes(IServiceCollection services)
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

    private static ClusterModelSlot ActiveSlot()
        => new()
        {
            SlotName = "primary",
            ActiveModelArtifactId = "artifact-7f3a",
            ContentHash = "sha256:expected",
            Revision = 3,
            DesiredStatus = ClusterModelSlotDesiredStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ModelArtifactDescriptor MakeDescriptor(
        string modelArtifactId = "artifact-7f3a",
        string contentHash = "sha256:expected")
        => new()
        {
            ModelArtifactId = modelArtifactId,
            ModelName = "primary-model",
            ModelVersion = "1.0.0",
            FeatureSchemaVersion = "v1",
            CalibrationVersion = "default-v1",
            EngineKind = InferenceEngineKind.RealModel,
            ContentHash = contentHash,
            RegisteredAt = DateTimeOffset.UtcNow
        };

    private static ModelNodeAppliedState MakeAppliedState(
        long revision = 3,
        bool isolated = false,
        string? isolationReason = null)
        => new()
        {
            NodeGroupId = NodeIdentity.ResolveNodeGroupId(),
            InstanceId = NodeIdentity.ResolveInstanceId(),
            SlotName = "primary",
            AppliedRevision = revision,
            ModelArtifactId = "artifact-7f3a",
            ContentHash = "sha256:expected",
            AppliedAt = DateTimeOffset.UtcNow,
            Isolated = isolated,
            IsolationReason = isolationReason
        };

    private static ProductionAdmissionCheck GetCheck(ProductionAdmissionReport report, string name)
        => report.Checks.Single(c => c.Name == name);

    // ── 内存假实现（不触发真实 IO / 推理） ───────────────────────────────

    private sealed class CountingClusterModelSlotStore : IClusterModelSlotStore
    {
        private readonly Func<ClusterModelSlot?> _slotFactory;

        public CountingClusterModelSlotStore(Func<ClusterModelSlot?> slotFactory) => _slotFactory = slotFactory;

        public int GetAsyncCalls { get; private set; }

        public ValueTask<ClusterModelSlot?> GetAsync(string slotName, CancellationToken ct = default)
        {
            GetAsyncCalls++;
            return ValueTask.FromResult(_slotFactory());
        }

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

    private sealed class FakePingPostgresConnectionFactory : IPostgresConnectionFactory
    {
        private readonly bool _success;
        private readonly string? _error;

        public FakePingPostgresConnectionFactory(bool success, string? error = null)
        {
            _success = success;
            _error = error;
        }

        public PostgresOptions Options => new();

        public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException("测试不打开真实连接。");

        public Task<(bool Success, string? ErrorMessage)> PingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((_success, _error));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedCts = new();

        public CancellationToken ApplicationStarted => _startedCts.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }

        public void MarkStarted() => _startedCts.Cancel();
    }

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

    // ── 节点级假实现：FakeModelActivationManager + FakeEngine ─────────────

    private sealed class FakeModelActivationManager : IModelActivationManager
    {
        public IBatchInferenceEngine? ActiveEngine { get; set; }

        public ModelArtifactDescriptor? ActiveDescriptor { get; set; }

        public long? ActiveGeneration { get; set; }

        public string ContentHash { get; set; } = "";

        public string ModelVersion => "1.0.0";

        public InferenceEngineKind Kind => InferenceEngineKind.RealModel;

        public string CalibrationVersion => "default-v1";

        public ValueTask<BatchInferenceResult> InferAsync(
            BatchInferenceRequest request,
            CancellationToken ct = default)
            => throw new NotImplementedException("准入校验不触发真实推理。");

        public ValueTask<BatchInferenceResult> InferBatchAsync(
            FeatureBatch batch,
            CancellationToken ct = default)
            => throw new NotImplementedException("准入校验不触发真实推理。");

        public IInferenceEngineLease? AcquireEngineLease() => null;

        public IInferenceEngineLease AcquireFallbackEngineLease()
            => throw new NotImplementedException();

        public ValueTask<ModelActivationResult> ActivateAsync(
            string modelArtifactId,
            OnnxInferenceEngineOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实激活。");

        public ValueTask<ModelActivationResult> ActivateLatestAsync(
            string modelName,
            OnnxInferenceEngineOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实激活。");

        public ValueTask<StagedModelHandle> LoadAndWarmupAsync(
            string modelArtifactId,
            OnnxInferenceEngineOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实加载。");

        public ValueTask<ModelActivationResult> PromoteStagedAsync(
            string stagedHandleId,
            string? expectedModelArtifactId = null,
            string? expectedContentHash = null,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实发布。");

        public ValueTask<ModelActivationResult> DeactivateAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException("准入校验不触发真实停用。");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEngine : IBatchInferenceEngine
    {
        public static readonly FakeEngine Instance = new();

        public string ModelVersion => "1.0.0";

        public InferenceEngineKind Kind => InferenceEngineKind.RealModel;

        public string ContentHash => "sha256:expected";

        public string CalibrationVersion => "default-v1";

        public ValueTask<BatchInferenceResult> InferAsync(
            BatchInferenceRequest request,
            CancellationToken ct = default)
            => throw new NotImplementedException("准入校验不触发真实推理。");

        public ValueTask<BatchInferenceResult> InferBatchAsync(
            FeatureBatch batch,
            CancellationToken ct = default)
            => throw new NotImplementedException("准入校验不触发真实推理。");
    }
}
