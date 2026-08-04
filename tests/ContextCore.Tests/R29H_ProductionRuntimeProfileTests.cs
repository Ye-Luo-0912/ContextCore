using System.Net.Http.Json;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ContextCore.Tests;

// ===========================================================================
// Production Runtime Profile 完整性测试
//
// 目标：验证 Production Runtime Profile 的完整功能：
// 1. 统一 Worker 注册（ProductionRuntimeWorkerRegistry 在各 Profile 下正确捕获 Worker 类型名）
// 2. Readiness Service 行为（CheckReadinessAsync / GetRegisteredWorkers / GetCanaryStatus /
// GetModelActivationStatus）
// 3. Readiness Endpoint（/health/ready）— 通过 WebApplicationFactory<Program> 真实 HTTP 调用验证
// 4. 当前激活组件报告 Endpoint（/api/runtime/status）— 同上
// 5. 不合法组合 fail-fast 补充边界场景
//
// 与 Service Composition E2E 测试的区别：
// - Service Composition E2E 测试验证 DI 容器中的服务描述符绑定（类型注册是否正确）。
// - 本测试验证 WorkerRegistry 内容、ReadinessService 运行时行为、以及 HTTP 端点的端到端响应。
//
// 设计原则：
// - Worker 注册与 ReadinessService 行为测试使用 ServiceCollection 直接构建（无需 Web 服务器）。
// - Endpoint E2E 测试使用 WebApplicationFactory<Program>（Development profile + filesystem，无需 Postgres）。
// - ProductionHA 的 ReadinessService 行为测试使用 stub 连接字符串（不连接真实 DB；仅验证服务解析）。
// - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("ProductionRuntimeProfile")]
public sealed class R29H_ProductionRuntimeProfileTests
{
    // ── 1. Worker 注册测试（ProductionRuntimeWorkerRegistry）──────────────

    /// <summary>
    /// Development profile 下 WorkerRegistry 应包含预期 Worker：
    /// AgentRunRecoveryWorker / LearningMaterializationWorker / CanaryProgressionHostedService /
    /// CanaryLeaderHostedService / ModelStateReconcilerWorker。
    /// 不应包含任何旧平面 Durable Transport 专属 Worker（pump / loop / replay / reaper / metrics）。
    /// </summary>
    [TestMethod]
    public void Development_Profile_WorkerRegistry_ContainsExpectedWorkers()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:EnableAgentRunRecovery"] = "true"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ProductionRuntimeWorkerRegistry>();
        var workerNames = registry.WorkerTypeNames.ToList();

        // 断言：包含 Development profile 预期 Worker
        CollectionAssert.Contains(workerNames, nameof(AgentRunRecoveryWorker),
            "WorkerRegistry 应包含 AgentRunRecoveryWorker。");
        CollectionAssert.Contains(workerNames, nameof(LearningMaterializationWorker),
            "WorkerRegistry 应包含 LearningMaterializationWorker。");
        CollectionAssert.Contains(workerNames, nameof(CanaryProgressionHostedService),
            "WorkerRegistry 应包含 CanaryProgressionHostedService。");
        CollectionAssert.Contains(workerNames, nameof(CanaryLeaderHostedService),
            "WorkerRegistry 应包含 CanaryLeaderHostedService（registry 统一记录，实际注册按 Profile 互斥）。");
        CollectionAssert.Contains(workerNames, nameof(ModelStateReconcilerWorker),
            "WorkerRegistry 应包含 ModelStateReconcilerWorker（registry 统一记录，实际注册仅 ProductionHA）。");

        // 断言：不包含旧平面 Durable Transport 专属 Worker（按名称守卫，防止回归）
        foreach (var retiredWorker in new[]
                 {
                     "DurableTransportInstructionPumpService",
                     "AgentKernelLoopHostedService",
                     "ResultOutboxReplayService",
                     "LeaseReaperService",
                     "PendingCountMetricsService"
                 })
        {
            CollectionAssert.DoesNotContain(workerNames, retiredWorker,
                $"Development profile 不应注册已退役的旧平面 worker：{retiredWorker}。");
        }
    }

    /// <summary>
    /// ProductionHA profile 下 WorkerRegistry 应包含 HA 平面 Worker：
    /// AgentRunRecoveryWorker / ModelStateReconcilerWorker / LearningMaterializationWorker /
    /// CanaryLeaderHostedService（以及 registry 统一记录的 CanaryProgressionHostedService）。
    /// 旧平面 Durable Transport 专属 Worker 全部退役（执行平面收敛到 AgentKernelHost/AgentRunActor）。
    /// </summary>
    [TestMethod]
    public void ProductionHA_Profile_WorkerRegistry_ContainsHAPlaneWorkers()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ContextCoreRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_wr_ha_"));
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ProductionRuntimeWorkerRegistry>();
        var workerNames = registry.WorkerTypeNames.ToList();

        // 断言：不包含旧平面 Durable Transport 专属 Worker
        foreach (var retiredWorker in new[]
                 {
                     "DurableTransportInstructionPumpService",
                     "AgentKernelLoopHostedService",
                     "ResultOutboxReplayService",
                     "LeaseReaperService",
                     "PendingCountMetricsService"
                 })
        {
            CollectionAssert.DoesNotContain(workerNames, retiredWorker,
                $"ProductionHA profile 不应注册已退役的旧平面 worker：{retiredWorker}。");
        }

        // 断言：包含 HA 平面通用 Worker
        CollectionAssert.Contains(workerNames, nameof(AgentRunRecoveryWorker),
            "ProductionHA profile 应注册 AgentRunRecoveryWorker。");
        CollectionAssert.Contains(workerNames, nameof(ModelStateReconcilerWorker),
            "ProductionHA profile 应注册 ModelStateReconcilerWorker。");
        CollectionAssert.Contains(workerNames, nameof(LearningMaterializationWorker),
            "ProductionHA profile 应注册 LearningMaterializationWorker。");
        CollectionAssert.Contains(workerNames, nameof(CanaryProgressionHostedService),
            "ProductionHA profile 应记录 CanaryProgressionHostedService（即使 Enabled=false）。");
        CollectionAssert.Contains(workerNames, nameof(CanaryLeaderHostedService),
            "ProductionHA profile 应记录 CanaryLeaderHostedService。");
    }

    /// <summary>
    /// Development profile 下禁用 Run Recovery 时，WorkerRegistry 不应包含 AgentRunRecoveryWorker。
    /// </summary>
    [TestMethod]
    public void Development_Profile_DisableRunRecovery_WorkerRegistry_ExcludesRecoveryWorker()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:EnableAgentRunRecovery"] = "false"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<ProductionRuntimeWorkerRegistry>();
        var workerNames = registry.WorkerTypeNames.ToList();

        // 断言：不包含被禁用的 Worker
        CollectionAssert.DoesNotContain(workerNames, nameof(AgentRunRecoveryWorker),
            "EnableAgentRunRecovery=false 时不应注册 AgentRunRecoveryWorker。");

        // 断言：仍包含 Canary 和 LearningMaterialization（不受 EnableAgentRunRecovery 开关控制）
        CollectionAssert.Contains(workerNames, nameof(CanaryProgressionHostedService),
            "CanaryProgressionHostedService 不受 EnableAgentRunRecovery 影响。");
        CollectionAssert.Contains(workerNames, nameof(CanaryLeaderHostedService),
            "CanaryLeaderHostedService 不受 EnableAgentRunRecovery 影响。");
    }

    // ── 2. ReadinessService 行为测试 ─────────────────────────────────────

    /// <summary>
    /// Development profile 下，ApplicationStarted 触发后 CheckReadinessAsync 应返回 "ready"。
    /// </summary>
    [TestMethod]
    public async Task ReadinessService_Development_CheckReadiness_ReturnsReadyAfterAppStart()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);

        var lifetime = new TestHostApplicationLifetime();
        lifetime.TriggerApplicationStarted();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var provider = services.BuildServiceProvider();
        var readinessService = provider.GetRequiredService<ProductionRuntimeReadinessService>();

        var result = await readinessService.CheckReadinessAsync();

        Assert.AreEqual("ready", result.OverallStatus,
            "Development profile + ApplicationStarted 触发后整体状态应为 ready。");
        Assert.AreEqual("Development", result.Profile,
            "Profile 应为 Development。");
        Assert.IsTrue(result.Checks.Count > 0,
            "应至少有一个检查项。");

        // application-started 检查项应为 ready
        var appStartedCheck = result.Checks.FirstOrDefault(c => c.Name == "application-started");
        Assert.IsNotNull(appStartedCheck, "应包含 application-started 检查项。");
        Assert.AreEqual("ready", appStartedCheck!.Status,
            "application-started 检查项状态应为 ready。");
    }

    /// <summary>
    /// Development profile 下，ApplicationStarted 未触发时 CheckReadinessAsync 应返回 "starting"。
    /// </summary>
    [TestMethod]
    public async Task ReadinessService_Development_CheckReadiness_ReturnsStartingBeforeAppStart()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);

        // 不触发 ApplicationStarted
        var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var provider = services.BuildServiceProvider();
        var readinessService = provider.GetRequiredService<ProductionRuntimeReadinessService>();

        var result = await readinessService.CheckReadinessAsync();

        Assert.AreEqual("starting", result.OverallStatus,
            "ApplicationStarted 未触发时整体状态应为 starting。");
    }

    /// <summary>
    /// Development profile 下 GetRegisteredWorkers 应返回预期 Worker 列表，
    /// 且各 Worker 的 Enabled / Registered 状态正确。
    /// </summary>
    [TestMethod]
    public void ReadinessService_Development_GetRegisteredWorkers_ReturnsExpectedList()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:EnableAgentRunRecovery"] = "true"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);

        var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var provider = services.BuildServiceProvider();
        var readinessService = provider.GetRequiredService<ProductionRuntimeReadinessService>();

        var workers = readinessService.GetRegisteredWorkers();

        // 断言：返回 5 个预期 Worker 定义（旧平面 9 个 → 收敛后 5 个，含 PendingRunClaimer）
        Assert.AreEqual(5, workers.Count,
            "GetExpectedWorkerDefinitions 应返回 5 个 Worker。");

        // 断言：AgentRunRecovery — Enabled=true, Registered=true
        var recovery = workers.FirstOrDefault(w => w.Name == "AgentRunRecovery");
        Assert.IsNotNull(recovery, "应包含 AgentRunRecovery Worker。");
        Assert.IsTrue(recovery!.Enabled, "Development profile 下 AgentRunRecovery 应 Enabled=true。");
        Assert.IsTrue(recovery.Registered, "AgentRunRecovery 应已注册。");

        // 断言：PendingRunClaimer — Enabled=true（EnableAgentRunRecovery 打开时随 Recovery 一起启用）, Registered=true
        var pendingClaimer = workers.FirstOrDefault(w => w.Name == "PendingRunClaimer");
        Assert.IsNotNull(pendingClaimer, "应包含 PendingRunClaimer Worker。");
        Assert.IsTrue(pendingClaimer!.Enabled, "EnableAgentRunRecovery=true 时 PendingRunClaimer 应 Enabled=true。");
        Assert.IsTrue(pendingClaimer.Registered, "PendingRunClaimer 应已注册。");

        // 断言：LearningMaterialization — Enabled=true, Registered=true
        var learning = workers.FirstOrDefault(w => w.Name == "LearningMaterialization");
        Assert.IsNotNull(learning, "应包含 LearningMaterialization Worker。");
        Assert.IsTrue(learning!.Enabled, "LearningMaterialization 应 Enabled=true。");
        Assert.IsTrue(learning.Registered, "LearningMaterialization 应已注册。");

        // 断言：CanaryProgression — Enabled=true, Registered=true
        var canaryProg = workers.FirstOrDefault(w => w.Name == "CanaryProgression");
        Assert.IsNotNull(canaryProg, "应包含 CanaryProgression Worker。");
        Assert.IsTrue(canaryProg!.Enabled, "Development profile 下 CanaryProgression 应 Enabled=true。");
        Assert.IsTrue(canaryProg.Registered, "CanaryProgression 应已注册。");

        // 断言：CanaryLeader — Enabled=false, Registered=true（registry 统一记录，Development 实际未注册为 HostedService）
        var canaryLeader = workers.FirstOrDefault(w => w.Name == "CanaryLeader");
        Assert.IsNotNull(canaryLeader, "应包含 CanaryLeader Worker。");
        Assert.IsFalse(canaryLeader!.Enabled, "Development profile 下 CanaryLeader 应 Enabled=false。");
        Assert.IsTrue(canaryLeader.Registered, "CanaryLeader 应已注册（registry 统一记录）。");

        // 断言：Started=false（ApplicationStarted 未触发）
        Assert.IsFalse(recovery.Started, "ApplicationStarted 未触发时 Started 应为 false。");
    }

    /// <summary>
    /// Development profile 下 GetCanaryStatus 应返回 Single-Node-Progression 模式。
    /// </summary>
    [TestMethod]
    public void ReadinessService_Development_GetCanaryStatus_ReturnsSingleNodeProgression()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);

        var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var provider = services.BuildServiceProvider();
        var readinessService = provider.GetRequiredService<ProductionRuntimeReadinessService>();

        var canary = readinessService.GetCanaryStatus();

        Assert.AreEqual("Single-Node-Progression", canary.Mode,
            "Development profile Canary 模式应为 Single-Node-Progression。");
        Assert.IsTrue(canary.ProgressionEnabled,
            "Development profile 下 CanaryProgression 应启用（CanarySchedulerOptions.Enabled 默认 true）。");
        Assert.IsFalse(canary.LeaderEnabled,
            "Development profile 下 CanaryLeader 应禁用。");
    }

    /// <summary>
    /// ProductionHA profile 下 GetCanaryStatus 应返回 HA-Leader 模式。
    /// </summary>
    [TestMethod]
    public void ReadinessService_ProductionHA_GetCanaryStatus_ReturnsHALeader()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "postgres",
            ["Storage:PostgresConnectionString"] = "Host=localhost;Database=stub;Username=stub;Password=stub",
            ["ContextCoreRuntime:Profile"] = "ProductionHA"
        });

        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(BuildPostgresOptions("stub_canary_ha_"));
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);

        var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var provider = services.BuildServiceProvider();
        var readinessService = provider.GetRequiredService<ProductionRuntimeReadinessService>();

        var canary = readinessService.GetCanaryStatus();

        Assert.AreEqual("HA-Leader", canary.Mode,
            "ProductionHA profile Canary 模式应为 HA-Leader。");
        Assert.IsFalse(canary.ProgressionEnabled,
            "ProductionHA profile 应禁用 CanaryProgression（CanarySchedulerOptions.Enabled=false）。");
        Assert.IsTrue(canary.LeaderEnabled,
            "ProductionHA profile 应启用 CanaryLeader（CanaryLeaderOptions.Enabled=true）。");
    }

    /// <summary>
    /// EnableModelActivation=false 时 GetModelActivationStatus 应返回 null。
    /// </summary>
    [TestMethod]
    public void ReadinessService_GetModelActivationStatus_ReturnsNullWhenDisabled()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:EnableModelActivation"] = "false"
        });

        var services = new ServiceCollection();
        services.AddContextCore(ContextCore.Abstractions.ModelExecutionOptions.Default);
        services.AddContextCoreRuntime(config);

        var lifetime = new TestHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);

        var provider = services.BuildServiceProvider();
        var readinessService = provider.GetRequiredService<ProductionRuntimeReadinessService>();

        var status = readinessService.GetModelActivationStatus();
        Assert.IsNull(status,
            "EnableModelActivation=false 时 GetModelActivationStatus 应返回 null。");
    }

    // ── 3. Endpoint E2E 测试（WebApplicationFactory<Program>）─────────────

    /// <summary>
    /// E2E：/health/ready 端点在 Development profile + filesystem 下应返回 200 OK。
    /// </summary>
    [TestMethod]
    public async Task HealthReady_Endpoint_Development_Returns200()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new ProductionRuntimeFactory(rootPath);
            using var http = factory.CreateClient();

            var response = await http.GetAsync("/health/ready");

            // Development + filesystem + 应用已启动 → 200 OK
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode,
                "/health/ready 在 Development profile 下应返回 200 OK。状态码：{0}", response.StatusCode);

            // 验证响应体包含 ProductionRuntimeReadinessResult 结构
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.IsTrue(json.TryGetProperty("overallStatus", out var statusProp),
                "响应应包含 overallStatus 字段。");
            Assert.AreEqual("ready", statusProp.GetString(),
                "整体状态应为 ready。");
            Assert.IsTrue(json.TryGetProperty("profile", out var profileProp),
                "响应应包含 profile 字段。");
            Assert.AreEqual("Development", profileProp.GetString(),
                "Profile 应为 Development。");
            Assert.IsTrue(json.TryGetProperty("checks", out _),
                "响应应包含 checks 数组。");
        }
        finally
        {
            TryCleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// E2E：/api/runtime/status 端点应返回 200 OK 并包含 Profile / Workers / Canary 等字段。
    /// </summary>
    [TestMethod]
    public async Task RuntimeStatus_Endpoint_Development_Returns200WithProfile()
    {
        var rootPath = CreateTestRootPath();
        try
        {
            using var factory = new ProductionRuntimeFactory(rootPath);
            using var http = factory.CreateClient();

            var response = await http.GetAsync("/api/runtime/status");

            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode,
                "/api/runtime/status 应返回 200 OK。状态码：{0}", response.StatusCode);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            // 验证 profile 字段
            Assert.IsTrue(json.TryGetProperty("profile", out var profileProp),
                "响应应包含 profile 字段。");
            Assert.AreEqual("Development", profileProp.GetString(),
                "Profile 应为 Development。");

            // 验证 applicationStarted 字段
            Assert.IsTrue(json.TryGetProperty("applicationStarted", out var startedProp),
                "响应应包含 applicationStarted 字段。");
            Assert.IsTrue(startedProp.GetBoolean(),
                "应用应已启动（applicationStarted=true）。");

            // 验证 workers 数组
            Assert.IsTrue(json.TryGetProperty("workers", out var workersProp),
                "响应应包含 workers 数组。");
            Assert.AreEqual(JsonValueKind.Array, workersProp.ValueKind,
                "workers 应为数组。");
            Assert.IsTrue(workersProp.GetArrayLength() >= 4,
                "workers 数组应至少包含 4 个 Worker 定义（旧平面 9 个 → 收敛后 4 个）。");

            // 验证 canary 字段
            Assert.IsTrue(json.TryGetProperty("canary", out var canaryProp),
                "响应应包含 canary 字段。");
            Assert.IsTrue(canaryProp.TryGetProperty("mode", out var modeProp),
                "canary 应包含 mode 字段。");
            Assert.AreEqual("Single-Node-Progression", modeProp.GetString(),
                "Canary 模式应为 Single-Node-Progression。");

            // 验证 checkedAt 字段
            Assert.IsTrue(json.TryGetProperty("checkedAt", out _),
                "响应应包含 checkedAt 字段。");

            // 验证 modelActivation 字段（EnableModelActivation=false 时为 null）
            Assert.IsTrue(json.TryGetProperty("modelActivation", out var maProp),
                "响应应包含 modelActivation 字段。");
            Assert.AreEqual(JsonValueKind.Null, maProp.ValueKind,
                "EnableModelActivation=false 时 modelActivation 应为 null。");
        }
        finally
        {
            TryCleanupDirectory(rootPath);
        }
    }

    // ── 4. 补充 Fail-fast 测试 ──────────────────────────────────────────

    /// <summary>
    /// EnableModelActivation=true 但未注册 IModelArtifactRegistry 时应 fail-fast。
    /// （Development profile + filesystem 不注册 IModelArtifactRegistry）
    /// </summary>
    [TestMethod]
    public void EnableModelActivation_WithoutRegistry_ThrowsFailFast()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:EnableModelActivation"] = "true"
        });

        var services = new ServiceCollection();

        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreRuntime(config),
            "EnableModelActivation=true 但未注册 IModelArtifactRegistry 应 fail-fast。");
    }

    /// <summary>
    /// 未知 Profile 值应 fail-fast（通过 switch default 分支抛异常）。
    /// </summary>
    [TestMethod]
    public void UnknownProfile_ThrowsFailFast()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "999"
        });

        var services = new ServiceCollection();

        Assert.ThrowsException<InvalidOperationException>(() =>
            services.AddContextCoreRuntime(config),
            "未知 Profile 值应 fail-fast。");
    }

    // ── 辅助方法 ──────────────────────────────────────────────────────────

    /// <summary>从键值对字典构建 IConfiguration。</summary>
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>构建 PostgresOptions（仅用于服务描述符注册，不实际连接 DB）。</summary>
    private static PostgresOptions BuildPostgresOptions(string tablePrefix) => new()
    {
        ConnectionString = "Host=localhost;Database=stub;Username=stub;Password=stub",
        AutoMigrate = false,
        EnablePgVectorExtension = false,
        TablePrefix = tablePrefix
    };

    /// <summary>创建唯一的临时测试目录路径。</summary>
    private static string CreateTestRootPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "cc_runtime_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>尝试清理临时目录（best-effort，失败不阻断测试）。</summary>
    private static void TryCleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort：临时目录清理失败不影响测试结果。
        }
    }

    // ── 测试用 IHostApplicationLifetime 实现 ──────────────────────────────

    /// <summary>
    /// 测试用 IHostApplicationLifetime 实现。
    /// 通过 TriggerApplicationStarted() 模拟应用启动完成事件。
    /// </summary>
    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _startedCts = new();
        private readonly CancellationTokenSource _stoppingCts = new();
        private readonly CancellationTokenSource _stoppedCts = new();

        public CancellationToken ApplicationStarted => _startedCts.Token;
        public CancellationToken ApplicationStopping => _stoppingCts.Token;
        public CancellationToken ApplicationStopped => _stoppedCts.Token;

        public void StopApplication()
        {
            _stoppingCts.Cancel();
            _stoppedCts.Cancel();
        }

        /// <summary>模拟 ApplicationStarted 事件触发（所有 HostedService.StartAsync 完成）。</summary>
        public void TriggerApplicationStarted() => _startedCts.Cancel();
    }

    // ── WebApplicationFactory ──────────────────────────────────────────────

    /// <summary>
    /// Production Runtime 端点 E2E 测试用 WebApplicationFactory。
    /// 使用 Development profile + filesystem 存储（无需 Postgres）。
    /// </summary>
    private sealed class ProductionRuntimeFactory : WebApplicationFactory<Program>
    {
        private readonly string _rootPath;

        public ProductionRuntimeFactory(string rootPath)
        {
            _rootPath = rootPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Storage:Provider", "filesystem");
            builder.UseSetting("Storage:RootPath", _rootPath);
            builder.UseSetting("Compression:Provider", "mock");
            builder.UseSetting("JobWorker:Enabled", "false");
            builder.UseSetting("ContextCoreRuntime:Profile", "Development");
            builder.UseSetting("ContextCoreRuntime:EnableAgentRunRecovery", "false");
            // Development 环境默认 ValidateOnBuild=true，会验证所有服务描述符。
            // 但部分服务（ICanaryLeaderLease / ILearningEventOutboxStore）仅在 Postgres provider 下注册，
            // filesystem 下无法解析。本测试验证 HTTP 端点响应，非 DI 容器完整性，故关闭构建时验证。
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = false;
                options.ValidateOnBuild = false;
            });
            // 移除所有 IHostedService 注册（E2E 测试只需 HTTP 端点响应，不需要后台 Worker）。
            // filesystem 模式下部分 HostedService 依赖 Postgres-only 服务（ICanaryLeaderLease /
            // ILearningEventOutboxStore），无法激活。移除后 web 服务器仍正常启动。
            builder.ConfigureServices(services =>
            {
                for (var i = services.Count - 1; i >= 0; i--)
                {
                    if (services[i].ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                    {
                        services.RemoveAt(i);
                    }
                }
            });
        }
    }
}
