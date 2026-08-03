using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Infrastructure;
using ContextCore.Service.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Workspace 配额强制验收测试
//
// 目标：IWorkspaceQuotaService.TryConsumeAsync 有实际调用方（此前零调用），
// workspace 配额在请求阶段与 Run 创建阶段都被强制。
//
// 覆盖：
// 1. 中间件：配额未启用 → 透传；配额已耗尽 → 429（快路径）；配额未耗尽 → 透传；
// 非配额边界路径 → 透传；无 workspace 上下文 → 透传；
// 2. 端点：配额启用时创建 Run 预留预算（TryConsumeAsync 实际调用），
// 预留失败 → 429；配额未启用 → 不扣减、正常创建。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
public sealed class R30B_QuotaAdmissionEnforcementTests
{
    private const string Ws = "ws-quota";

    // ── 1. 中间件 ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Middleware_QuotaDisabled_PassesThrough()
    {
        var options = new SecurityOptions { Quota = new WorkspaceQuotaOptions { Enabled = false } };
        var invoked = false;
        var middleware = new WorkspaceQuotaMiddleware(
            _ => { invoked = true; return Task.CompletedTask; },
            options,
            NullLogger<WorkspaceQuotaMiddleware>.Instance);

        await middleware.InvokeAsync(QuotaHttpContext(Ws, options, quotaService: null));

        Assert.IsTrue(invoked, "配额未启用时应透传到下一个中间件。");
    }

    [TestMethod]
    public async Task Middleware_QuotaExhausted_Returns429()
    {
        var options = new SecurityOptions
        {
            Quota = new WorkspaceQuotaOptions
            {
                Enabled = true,
                WorkspaceLimits = new Dictionary<string, WorkspaceQuotaLimit>
                {
                    [Ws] = new() { MaxTokens = 100, MaxCostUsd = 0, Period = "01:00:00" }
                }
            }
        };
        var quotaService = new InMemoryWorkspaceQuotaService(options, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await quotaService.TryConsumeAsync(Ws, 100, 0);

        var middleware = new WorkspaceQuotaMiddleware(
            _ => throw new InvalidOperationException("配额已耗尽时不应进入下一个中间件。"),
            options,
            NullLogger<WorkspaceQuotaMiddleware>.Instance);

        var context = QuotaHttpContext(Ws, options, quotaService);
        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.AreEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode,
            "配额已耗尽的 workspace 创建 Run 应返回 429。");
        StringAssert.Contains(body, "workspace_quota_exhausted");
    }

    [TestMethod]
    public async Task Middleware_QuotaAvailable_PassesThrough()
    {
        var options = new SecurityOptions
        {
            Quota = new WorkspaceQuotaOptions
            {
                Enabled = true,
                WorkspaceLimits = new Dictionary<string, WorkspaceQuotaLimit>
                {
                    [Ws] = new() { MaxTokens = 100, MaxCostUsd = 0, Period = "01:00:00" }
                }
            }
        };
        var quotaService = new InMemoryWorkspaceQuotaService(options, NullLogger<InMemoryWorkspaceQuotaService>.Instance);

        var invoked = false;
        var middleware = new WorkspaceQuotaMiddleware(
            _ => { invoked = true; return Task.CompletedTask; },
            options,
            NullLogger<WorkspaceQuotaMiddleware>.Instance);

        await middleware.InvokeAsync(QuotaHttpContext(Ws, options, quotaService));

        Assert.IsTrue(invoked, "配额未耗尽时应透传。");
    }

    [TestMethod]
    public async Task Middleware_NonQuotaBoundPath_PassesThrough()
    {
        var options = new SecurityOptions
        {
            Quota = new WorkspaceQuotaOptions
            {
                Enabled = true,
                WorkspaceLimits = new Dictionary<string, WorkspaceQuotaLimit>
                {
                    [Ws] = new() { MaxTokens = 100, MaxCostUsd = 0, Period = "01:00:00" }
                }
            }
        };
        var quotaService = new InMemoryWorkspaceQuotaService(options, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await quotaService.TryConsumeAsync(Ws, 100, 0);

        var invoked = false;
        var middleware = new WorkspaceQuotaMiddleware(
            _ => { invoked = true; return Task.CompletedTask; },
            options,
            NullLogger<WorkspaceQuotaMiddleware>.Instance);

        // GET（非 POST）与其它路径不受配额门禁影响
        var context = QuotaHttpContext(Ws, options, quotaService, method: "GET", path: "/api/agents/runs/run-1");
        await middleware.InvokeAsync(context);

        Assert.IsTrue(invoked, "非配额边界路径（GET 查询）应透传。");
    }

    [TestMethod]
    public async Task Middleware_NoWorkspaceContext_PassesThrough()
    {
        var options = new SecurityOptions
        {
            Quota = new WorkspaceQuotaOptions
            {
                Enabled = true,
                WorkspaceLimits = new Dictionary<string, WorkspaceQuotaLimit>
                {
                    [Ws] = new() { MaxTokens = 100, MaxCostUsd = 0, Period = "01:00:00" }
                }
            }
        };
        var quotaService = new InMemoryWorkspaceQuotaService(options, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await quotaService.TryConsumeAsync(Ws, 100, 0);

        var invoked = false;
        var middleware = new WorkspaceQuotaMiddleware(
            _ => { invoked = true; return Task.CompletedTask; },
            options,
            NullLogger<WorkspaceQuotaMiddleware>.Instance);

        // 未填充 WorkspaceContextItemsKey → 无 workspace 上下文 → 透传
        var context = QuotaHttpContext(workspaceId: null, options, quotaService);
        await middleware.InvokeAsync(context);

        Assert.IsTrue(invoked, "未解析到 workspace 时应透传（由端点按 RBAC/fallback 处理）。");
    }

    // ── 2. 端点：配额预留 ────────────────────────────────────────────────

    [TestMethod]
    public async Task Endpoint_QuotaEnabled_ConsumesAndRejectsWhenExhausted()
    {
        var securityOptions = new SecurityOptions
        {
            Quota = new WorkspaceQuotaOptions
            {
                Enabled = true,
                WorkspaceLimits = new Dictionary<string, WorkspaceQuotaLimit>
                {
                    [Ws] = new() { MaxTokens = 100, MaxCostUsd = 0, Period = "01:00:00" }
                }
            }
        };
        var quotaService = new InMemoryWorkspaceQuotaService(securityOptions, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await using var harness = await EndpointHarness.CreateAsync(securityOptions, quotaService);

        // 第一次：配额 100/100，Run 预算 100 → 预留成功 → 201
        var first = await CreateRunAsync(harness, quotaService, maxTokens: 100);
        Assert.AreEqual(StatusCodes.Status201Created, first.Status, "配额充足时创建 Run 应 201。");

        var quota = await quotaService.GetQuotaAsync(Ws);
        Assert.AreEqual(100, quota.TokensUsed, "创建 Run 后应实际扣减配额（TryConsumeAsync 有调用方）。");

        // 第二次：已用 100，Run 预算 100 → 200 > 100 → 预留失败 → 429
        var second = await CreateRunAsync(harness, quotaService, maxTokens: 100);
        Assert.AreEqual(StatusCodes.Status429TooManyRequests, second.Status,
            "配额耗尽后创建 Run 应返回 429（不无限等待、不伪装成功）。");
        StringAssert.Contains(second.Body, "配额耗尽");
    }

    [TestMethod]
    public async Task Endpoint_QuotaDisabled_NoConsumption_BothAccepted()
    {
        var securityOptions = new SecurityOptions { Quota = new WorkspaceQuotaOptions { Enabled = false } };
        var quotaService = new InMemoryWorkspaceQuotaService(securityOptions, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await using var harness = await EndpointHarness.CreateAsync(securityOptions, quotaService);

        var first = await CreateRunAsync(harness, quotaService, maxTokens: 100);
        var second = await CreateRunAsync(harness, quotaService, maxTokens: 100);

        Assert.AreEqual(StatusCodes.Status201Created, first.Status, "配额未启用时创建应正常。");
        Assert.AreEqual(StatusCodes.Status201Created, second.Status, "配额未启用时不受配额限制。");

        var quota = await quotaService.GetQuotaAsync(Ws);
        Assert.AreEqual(0, quota.TokensUsed, "配额未启用时不应扣减。");
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static async Task<(int Status, string Body)> CreateRunAsync(
        EndpointHarness harness,
        InMemoryWorkspaceQuotaService quotaService,
        int maxTokens)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = harness.RequestServices,
            Response = { Body = new MemoryStream() }
        };
        var request = new CreateRunRequest
        {
            Task = "配额强制测试任务",
            WorkspaceId = Ws,
            CostBudget = new CostBudgetRequest { MaxTokens = maxTokens }
        };

        var result = await AgentExecutionEndpoints.CreateAgentRunHandlerAsync(
            // 配额路径不消费 workspace 访问器（请求已显式携带 WorkspaceId），传 null 仅用于隔离测试。
            request, harness.RunStore, harness.Host, workspaceContextAccessor: null!, httpContext, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (httpContext.Response.StatusCode, body);
    }

    private static DefaultHttpContext QuotaHttpContext(
        string? workspaceId,
        SecurityOptions securityOptions,
        IWorkspaceQuotaService? quotaService,
        string method = "POST",
        string path = "/api/agents/runs")
    {
        var httpContext = new DefaultHttpContext();
        if (workspaceId is not null)
        {
            httpContext.Items[SecurityServiceCollectionExtensions.WorkspaceContextItemsKey] = workspaceId;
        }
        httpContext.Request.Method = method;
        httpContext.Request.Path = path;
        httpContext.Response.Body = new MemoryStream();

        var services = new ServiceCollection();
        services.AddSingleton(securityOptions);
        if (quotaService is not null)
        {
            services.AddSingleton<IWorkspaceQuotaService>(quotaService);
        }
        services.AddLogging();
        httpContext.RequestServices = services.BuildServiceProvider();
        return httpContext;
    }

    /// <summary>端点测试夹具：真实 InMemory 存储 + 真实 AgentKernelHost。</summary>
    private sealed class EndpointHarness : IAsyncDisposable
    {
        public InMemoryAgentRunStore RunStore { get; }
        public AgentKernelHost Host { get; }
        public ServiceProvider RequestServices { get; }

        private EndpointHarness(InMemoryAgentRunStore runStore, AgentKernelHost host, ServiceProvider requestServices)
        {
            RunStore = runStore;
            Host = host;
            RequestServices = requestServices;
        }

        public static async Task<EndpointHarness> CreateAsync(
            SecurityOptions securityOptions,
            IWorkspaceQuotaService quotaService)
        {
            var runStore = new InMemoryAgentRunStore();
            var eventStore = new InMemoryAgentRunEventStore(runStore);

            var hostServices = new ServiceCollection();
            hostServices.AddSingleton<IAgentRunStore>(runStore);
            hostServices.AddSingleton<IAgentRunEventStore>(eventStore);
            hostServices.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
            hostServices.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
            hostServices.AddSingleton<AgentKernelHost>();
            hostServices.AddSingleton(new AgentHostOptions
            {
                ChannelCapacity = 8,
                WorkerCount = 2,
                DrainTimeout = TimeSpan.FromSeconds(5)
            });
            hostServices.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            var hostProvider = hostServices.BuildServiceProvider();
            var host = hostProvider.GetRequiredService<AgentKernelHost>();

            var requestServices = new ServiceCollection();
            requestServices.AddSingleton(securityOptions);
            requestServices.AddSingleton<IWorkspaceQuotaService>(quotaService);
            requestServices.AddLogging();

            return new EndpointHarness(runStore, host, requestServices.BuildServiceProvider());
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
