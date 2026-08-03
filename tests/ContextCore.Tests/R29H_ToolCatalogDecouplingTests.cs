using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

// ===========================================================================
// Tool Catalog 解耦 Truth 测试
//
// 验证 ：AgentRunActor 不再向下转型到具体 RealToolDispatcher 读取 Tool 定义，
// 而是显式注入 IToolCatalog（与分派器解耦，装饰器 / MCP 适配器可独立暴露定义）：
// 1. DI：RealDispatch 模式下 IToolDispatcher 与 IToolCatalog 解析到同一 RealToolDispatcher
// 实例（同实例注册，避免两套 Tool 注册表漂移）；
// 2. DI：默认 Echo 模式下 IToolCatalog 可解析（EchoToolDispatcher 空定义）；
// 3. Actor：注入自定义 IToolCatalog 时，模型调用携带 Catalog 的定义（而非 Dispatcher 的）；
// 4. Actor：未注入 Catalog 时回退到 Dispatcher 的 IToolCatalog 实现（兼容旧构造方式）。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R29H_ToolCatalogDecouplingTests
{
    private const string Ws = "ws-catalog";
    private const string RunId = "run-catalog";

    // ── 1. DI 注册：同实例 + 可解析 ────────────────────────────────────────

    /// <summary>
    /// 验证：RealDispatch 模式下 IToolDispatcher 与 IToolCatalog 解析到同一 RealToolDispatcher
    /// 实例（目录与分派器同实例，注册表不漂移）。
    /// </summary>
    [TestMethod]
    public void DI_RealDispatchMode_DispatcherAndCatalog_AreSameInstance()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development",
            ["ContextCoreRuntime:ToolMode"] = "RealDispatch"
        });

        var services = new ServiceCollection();
        services.AddSingleton<IToolHandler>(new CatalogTestHandler("search", ToolSideEffect.ReadOnly));
        services.AddContextCore();
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<IToolDispatcher>();
        var catalog = provider.GetRequiredService<IToolCatalog>();

        Assert.IsInstanceOfType<RealToolDispatcher>(dispatcher, "RealDispatch 模式应注册 RealToolDispatcher。");
        Assert.IsInstanceOfType<RealToolDispatcher>(catalog, "IToolCatalog 应解析为 RealToolDispatcher。");
        Assert.IsTrue(ReferenceEquals(dispatcher, catalog),
            "IToolDispatcher 与 IToolCatalog 应为同一实例（目录与分派器同源）。");

        // 目录反映已注册 Handler 的 Tool 定义（供模型 function calling 声明）
        var definitions = catalog.GetToolDefinitions();
        Assert.AreEqual(1, definitions.Count);
        Assert.AreEqual("search", definitions[0].Name);
    }

    /// <summary>
    /// 验证：默认 Echo 模式下 IToolCatalog 可解析（EchoToolDispatcher 空定义，模型不感知 Tool）。
    /// </summary>
    [TestMethod]
    public void DI_DefaultEchoMode_CatalogResolvesWithEmptyDefinitions()
    {
        var config = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "filesystem",
            ["ContextCoreRuntime:Profile"] = "Development"
        });

        var services = new ServiceCollection();
        services.AddContextCore();
        services.AddContextCoreRuntime(config);
        var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<IToolCatalog>();
        Assert.IsInstanceOfType<EchoToolDispatcher>(catalog, "默认 Echo 模式 IToolCatalog 解析为 EchoToolDispatcher。");
        Assert.AreEqual(0, catalog.GetToolDefinitions().Count, "Echo 无 Tool 定义。");
    }

    // ── 2. Actor 解耦：注入 Catalog 优先于 Dispatcher 转型 ─────────────────

    /// <summary>
    /// 验证：Actor 注入自定义 IToolCatalog 时，模型调用携带 Catalog 的 Tool 定义
    /// （即便 Dispatcher 自身实现了 IToolCatalog 且返回不同定义）。
    /// </summary>
    [TestMethod]
    public async Task Actor_UsesInjectedCatalog_NotDispatcherCast()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("验证 Tool Catalog 注入");
        await runStore.CreateAsync(run);

        // Dispatcher 实现 IToolCatalog 但返回另一组定义（证明 Catalog 参数优先，不向下转型）
        var dispatcher = new DualCatalogDispatcher(new[]
        {
            new AgentToolDefinition { Name = "dispatcher-tool", ParametersJsonSchema = "{}" }
        });
        var catalog = new StaticToolCatalog(new[]
        {
            new AgentToolDefinition { Name = "catalog-tool", ParametersJsonSchema = "{}" }
        });
        var transport = new RecordingRequestTransport(new AgentModelResponse
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 3,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            dispatcher,
            toolCatalog: catalog);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.AreEqual(1, transport.Requests.Count, "应有 1 次模型调用。");
        var tools = transport.Requests[0].Tools;
        Assert.AreEqual(1, tools.Count);
        Assert.AreEqual("catalog-tool", tools[0].Name,
            "模型调用应携带注入 Catalog 的 Tool 定义（而非 Dispatcher 转型结果）。");
    }

    /// <summary>
    /// 验证：未注入 Catalog 时，Actor 回退到 Dispatcher 的 IToolCatalog 实现（兼容旧构造方式）。
    /// </summary>
    [TestMethod]
    public async Task Actor_FallsBackToDispatcherCatalog_WhenNoCatalogInjected()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("验证 Catalog 回退");
        await runStore.CreateAsync(run);

        var dispatcher = new DualCatalogDispatcher(new[]
        {
            new AgentToolDefinition { Name = "dispatcher-tool", ParametersJsonSchema = "{}" }
        });
        var transport = new RecordingRequestTransport(new AgentModelResponse
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 3,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        // 不注入 toolCatalog → 回退到 (toolDispatcher as IToolCatalog)
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            dispatcher);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.AreEqual(1, transport.Requests.Count);
        Assert.AreEqual("dispatcher-tool", transport.Requests[0].Tools[0].Name,
            "未注入 Catalog 时应回退到 Dispatcher 的 IToolCatalog 定义。");
    }

    /// <summary>
    /// 验证：Dispatcher 与 Catalog 均无定义时 → 空列表（模型不感知 Tool）。
    /// </summary>
    [TestMethod]
    public async Task Actor_NoCatalogNoDispatcherDefinitions_EmptyTools()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("验证无 Tool 定义");
        await runStore.CreateAsync(run);

        var dispatcher = new EchoToolDispatcher(); // 不实现 IToolCatalog
        var transport = new RecordingRequestTransport(new AgentModelResponse
        {
            Content = "完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 3,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            dispatcher);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.AreEqual(1, transport.Requests.Count);
        Assert.AreEqual(0, transport.Requests[0].Tools.Count, "无 Catalog 且 Dispatcher 无定义 → 空 Tool 列表。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-catalog",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 10
        }
    };

    /// <summary>记录 AgentModelRequest 的传输 stub（含 Tools 定义）。</summary>
    private sealed class RecordingRequestTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse _response;

        public RecordingRequestTransport(AgentModelResponse response)
        {
            _response = response;
        }

        public List<AgentModelRequest> Requests { get; } = new();

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用 AgentModelRequest 重载。");

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_response);
        }
    }

    /// <summary>静态 Tool 定义目录 stub。</summary>
    private sealed class StaticToolCatalog : IToolCatalog
    {
        private readonly IReadOnlyList<AgentToolDefinition> _definitions;

        public StaticToolCatalog(IReadOnlyList<AgentToolDefinition> definitions)
        {
            _definitions = definitions;
        }

        public IReadOnlyList<AgentToolDefinition> GetToolDefinitions() => _definitions;
    }

    /// <summary>同时实现 IToolDispatcher + IToolCatalog 的 stub（模拟 RealToolDispatcher 的旧行为）。</summary>
    private sealed class DualCatalogDispatcher : IToolDispatcher, IToolCatalog
    {
        private readonly IReadOnlyList<AgentToolDefinition> _definitions;

        public DualCatalogDispatcher(IReadOnlyList<AgentToolDefinition> definitions)
        {
            _definitions = definitions;
        }

        public IReadOnlySet<string> SupportedTools => new HashSet<string>(StringComparer.Ordinal) { "search" };

        public ToolDescriptor? GetDescriptor(string toolName) => null;

        public IReadOnlyList<AgentToolDefinition> GetToolDefinitions() => _definitions;

        public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = true,
                Result = request.Payload,
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
    }

    /// <summary>注册到 RealToolDispatcher 的 Tool Handler stub。</summary>
    private sealed class CatalogTestHandler : IToolHandler
    {
        public CatalogTestHandler(string toolName, ToolSideEffect sideEffect)
        {
            ToolName = toolName;
            DeclaredSideEffect = sideEffect;
        }

        public string ToolName { get; }
        public ToolSideEffect DeclaredSideEffect { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public ToolDescriptor Descriptor => new()
        {
            Name = ToolName,
            DeclaredSideEffect = DeclaredSideEffect
        };

        public ValueTask<ToolHandlerResult> HandleAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = DeclaredSideEffect
            });
    }
}
