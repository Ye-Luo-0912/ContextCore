using System.Net;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
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
// Production Admission Controller（实时探针 + TTL 缓存）与请求阶段准入中间件测试
//
// 覆盖：
// 1. 控制器：非 ProductionHA 不执行实时探针；ProductionHA 全配置下 9 项静态
// 强制项 + 3 项实时探针全部通过。
// 2. 缓存语义：TTL 内复用缓存（不重复查询 Model Slot）；TTL 到期 / force=true
// 时重新执行全量校验（以 CountingClusterModelSlotStore.GetAsync 调用次数断言）。
// 3. 实时探针逐项失败：Postgres 不可达 / IPostgresConnectionFactory 未注册 /
// Model Slot 停用（刷新后）/ 应用未启动——对应探针 Fail 且 AllPassed=false。
// 4. 请求阶段中间件：非 ProductionHA 透传；准入通过放行；准入失败返回 503 JSON；
// 健康检查 / 准入状态路径豁免。
//
// 说明：中间件测试通过 UseTestServer 启动最小 Web 应用（不依赖 Program.cs 完整
// 组合），仅注册 admission 栈与测试端点；控制器单元测试直接构造实例。
// ===========================================================================

[TestClass]
[TestCategory("Service")]
[TestCategory("Production")]
[TestCategory("Admission")]
public sealed class R29R_ProductionAdmissionControllerTests
{
    // ── 控制器单元测试 ─────────────────────────────────────────────────

    [TestMethod]
    public async Task NonProductionProfile_SkipsLiveProbes_AllPassed()
    {
        var harness = BuildControllerHarness(profile: RuntimeProfile.Development);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AdmissionRequired);
        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(1, report.Checks.Count);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Skipped, report.Checks[0].Status);
        Assert.IsFalse(report.Checks.Any(c => c.Name == "postgres-live"), "非 ProductionHA 不应执行实时探针。");
        Assert.AreEqual(0, harness.SlotStore.GetAsyncCalls, "非 ProductionHA 不应查询 Model Slot。");
    }

    [TestMethod]
    public async Task ProductionHA_FullyConfigured_AllPassed_WithLiveProbes()
    {
        var harness = BuildControllerHarness();
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsTrue(report.AdmissionRequired);
        Assert.IsTrue(report.AllPassed);
        Assert.AreEqual(12, report.Checks.Count, "9 项静态强制项 + 3 项实时探针。");
        Assert.AreEqual(ProductionAdmissionCheckStatus.Pass, GetCheck(report, "postgres-live").Status);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Pass, GetCheck(report, "model-slot-live").Status);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Pass, GetCheck(report, "application-started-live").Status);
    }

    [TestMethod]
    public async Task CachedReportWithinTtl_NoRevalidation()
    {
        var harness = BuildControllerHarness(); // ProbeInterval 默认 5 秒
        var first = await harness.Controller.GetOrRefreshAsync();
        var second = await harness.Controller.GetOrRefreshAsync();

        Assert.IsTrue(first.AllPassed);
        Assert.AreSame(first, second, "TTL 内应复用同一缓存报告实例。");
        Assert.AreEqual(2, harness.SlotStore.GetAsyncCalls,
            "静态强制项 + 实时探针各查询一次；缓存命中不应新增查询。");
    }

    [TestMethod]
    public async Task TtlExpired_Revalidates()
    {
        var harness = BuildControllerHarness(
            options: new ProductionAdmissionOptions { ProbeInterval = TimeSpan.Zero });
        await harness.Controller.GetOrRefreshAsync();
        await harness.Controller.GetOrRefreshAsync();

        Assert.AreEqual(4, harness.SlotStore.GetAsyncCalls, "TTL=0 时每次调用都重新执行全量校验。");
    }

    [TestMethod]
    public async Task ForceRefresh_BypassesTtl()
    {
        var harness = BuildControllerHarness();
        await harness.Controller.GetOrRefreshAsync(forceRefresh: true);
        await harness.Controller.GetOrRefreshAsync(forceRefresh: true);

        Assert.AreEqual(4, harness.SlotStore.GetAsyncCalls, "force=true 忽略 TTL 强制刷新。");
    }

    [TestMethod]
    public async Task PostgresUnavailable_LiveProbeFails_AdmissionDenied()
    {
        var harness = BuildControllerHarness(pingSuccess: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "postgres-live").Status);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Pass, GetCheck(report, "model-slot-live").Status);
    }

    [TestMethod]
    public async Task PostgresFactoryNotRegistered_LiveProbeFails()
    {
        var harness = BuildControllerHarness(registerPingFactory: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "postgres-live").Status);
    }

    [TestMethod]
    public async Task ModelSlotDeactivatedOnRefresh_AdmissionDenied()
    {
        var mutable = new MutableSlot { Slot = ActiveSlot() };
        var harness = BuildControllerHarness(slotFactory: () => mutable.Slot);

        var first = await harness.Controller.GetOrRefreshAsync();
        Assert.IsTrue(first.AllPassed);

        mutable.Slot = ActiveSlot() with
        {
            DesiredStatus = ClusterModelSlotDesiredStatus.Inactive,
            ActiveModelArtifactId = null
        };
        var refreshed = await harness.Controller.GetOrRefreshAsync(forceRefresh: true);

        Assert.IsFalse(refreshed.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(refreshed, "model-slot-live").Status);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(refreshed, "model-slot-applied").Status);
    }

    [TestMethod]
    public async Task ApplicationNotStarted_LiveProbeFails()
    {
        var harness = BuildControllerHarness(markStarted: false);
        var report = await harness.Controller.GetOrRefreshAsync();

        Assert.IsFalse(report.AllPassed);
        Assert.AreEqual(ProductionAdmissionCheckStatus.Fail, GetCheck(report, "application-started-live").Status);
    }

    // ── 请求阶段中间件测试（UseTestServer） ─────────────────────────────

    [TestMethod]
    public async Task Middleware_NonProductionHA_PassesThrough()
    {
        var app = await BuildWebAppAsync(profile: RuntimeProfile.Development);
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
    public async Task Middleware_AdmissionPassing_RequestSucceeds()
    {
        var app = await BuildWebAppAsync();
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
    public async Task Middleware_AdmissionFailing_Returns503Json()
    {
        var app = await BuildWebAppAsync(pingSuccess: false);
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
                failed.EnumerateArray().Any(e => e.GetString() == "postgres-live"),
                "失败检查列表应包含 postgres-live。");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Middleware_ExemptPaths_BypassAdmission()
    {
        var app = await BuildWebAppAsync(pingSuccess: false); // 准入失败
        try
        {
            var client = app.GetTestServer().CreateClient();

            var business = await client.GetAsync("/api/business/echo");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, business.StatusCode);

            var health = await client.GetAsync("/health");
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);

            var admission = await client.GetAsync("/api/admission/status");
            Assert.AreEqual(HttpStatusCode.OK, admission.StatusCode);
            var body = await admission.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.IsFalse(document.RootElement.GetProperty("allPassed").GetBoolean());
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
        public required FakeApplicationLifetime Lifetime { get; init; }
    }

    private static ControllerHarness BuildControllerHarness(
        RuntimeProfile profile = RuntimeProfile.ProductionHA,
        ProductionAdmissionOptions? options = null,
        bool pingSuccess = true,
        bool registerPingFactory = true,
        bool markStarted = true,
        Func<ClusterModelSlot?>? slotFactory = null)
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
            Lifetime = lifetime
        };
    }

    private static async Task<WebApplication> BuildWebAppAsync(
        RuntimeProfile profile = RuntimeProfile.ProductionHA,
        bool pingSuccess = true)
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
            Revision = 3,
            DesiredStatus = ClusterModelSlotDesiredStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ProductionAdmissionCheck GetCheck(ProductionAdmissionReport report, string name)
        => report.Checks.Single(c => c.Name == name);

    // ── 内存假实现（不触发真实 IO / 推理） ───────────────────────────────

    private sealed class MutableSlot
    {
        public ClusterModelSlot? Slot { get; set; }
    }

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
}
