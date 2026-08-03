using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Dispatcher Error 修正 —— 结构化错误分类验收
//
// Dispatcher 错误修正：ToolDispatchResult / ToolExecutionResult 携带类型化
// DispatchErrorKind（替代纯字符串错误语义），策略层 / 对账 / 审计可按类别处理：
// - 未注册 Tool → UnregisteredTool（默认 RequiresReconciliation，不静默重放）；
// - Handler 异常 → HandlerException（外部副作用状态未知 → RequiresReconciliation）；
// - LeaseFence 缺失/过期 → LeaseFenceViolation（fail-closed，不触碰外部副作用）；
// - 策略拒绝 → PolicyRejected。
// ===========================================================================

[TestClass]
[TestCategory("Kill-Point")]
[TestCategory("External-Effect-Truth")]
public sealed class R30A_DispatcherErrorTests
{
    private const string Ws = "ws-r30a-err";
    private const string RunId = "run-r30a-err";

    // ── Dispatcher 层：结构化错误 ────────────────────────────────────────────

    /// <summary>
    /// 验证：未注册 Tool → Succeeded=false + ErrorKind=UnregisteredTool
    /// + SideEffect=RequiresReconciliation（不抛异常、不静默重放）。
    /// </summary>
    [TestMethod]
    public async Task Dispatcher_UnregisteredTool_ReturnsStructuredError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatcher = new RealToolDispatcher(Array.Empty<IToolHandler>());

        var result = await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "ghost-tool",
            Payload = "{}",
            RequestId = "req-ghost",
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DispatchErrorKind.UnregisteredTool, result.ErrorKind, "未注册 Tool 应分类为 UnregisteredTool。");
        Assert.AreEqual(ToolSideEffect.RequiresReconciliation, result.SideEffect, "未注册 Tool 应保守按 RequiresReconciliation 处置。");
        StringAssert.Contains(result.Result!, "未注册");
    }

    /// <summary>
    /// 验证：Handler 抛异常 → Succeeded=false + ErrorKind=HandlerException
    /// + SideEffect=RequiresReconciliation（异常不向上传播，不静默重放）。
    /// </summary>
    [TestMethod]
    public async Task Dispatcher_HandlerException_ReturnsStructuredError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new ThrowingToolHandler("explode");
        var dispatcher = new RealToolDispatcher(new IToolHandler[] { handler });

        var result = await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "explode",
            Payload = "{}",
            RequestId = "req-explode",
            WorkspaceId = Ws,
            RunId = RunId
        }, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DispatchErrorKind.HandlerException, result.ErrorKind, "Handler 异常应分类为 HandlerException。");
        Assert.AreEqual(ToolSideEffect.RequiresReconciliation, result.SideEffect, "异常意味着副作用状态未知，应保守处置。");
        StringAssert.Contains(result.Result!, "处理异常");
    }

    // ── 执行器层：结构化错误透传 ────────────────────────────────────────────

    /// <summary>
    /// 验证：声明 RequiresLeaseFence 但未携带 fence → fail-closed，
    /// ErrorKind=LeaseFenceViolation，外部副作用零调用。
    /// </summary>
    [TestMethod]
    public async Task Executor_MissingLeaseFence_ErrorKindLeaseFenceViolation()
    {
        var handler = CreateHandler("write-file", new ToolDescriptor
        {
            Name = "write-file",
            DeclaredSideEffect = ToolSideEffect.FencedWrite,
            RequiresLeaseFence = true,
            RecoveryStrategy = ToolRecoveryStrategy.UseCachedResult
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (_, executor, _, _) = CreateExecutor(handler);

        var toolCall = BuildToolCall("write-file", "arg-fence");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DispatchErrorKind.LeaseFenceViolation, result.ErrorKind, "缺少必需 LeaseFence 应分类为 LeaseFenceViolation。");
        Assert.AreEqual(0, handler.InvocationCount, "fail-closed 时不得触碰外部副作用。");
    }

    /// <summary>
    /// 验证：未注册 Tool 经执行器完整路径 → ErrorKind=UnregisteredTool 透传到
    /// ToolExecutionResult（调用方 / 审计可按类别处理）。
    /// </summary>
    [TestMethod]
    public async Task Executor_UnregisteredTool_ErrorKindPropagated()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatcher = new RealToolDispatcher(Array.Empty<IToolHandler>());
        var journal = new InMemoryToolDispatchJournal();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal, new InMemoryDurableToolResultStore());

        var toolCall = BuildToolCall("ghost-tool", "arg-ghost");
        var result = await executor.ExecuteAsync(RunId, Ws, toolCall, 0, cts.Token);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(DispatchErrorKind.UnregisteredTool, result.ErrorKind, "UnregisteredTool 应透传到执行结果。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static RecordingToolHandler CreateHandler(string toolName, ToolDescriptor descriptor) => new(toolName, descriptor);

    private static (RealToolDispatcher Dispatcher, DefaultDurableToolExecutor Executor, InMemoryToolDispatchJournal Journal, InMemoryDurableToolResultStore ResultStore) CreateExecutor(
        RecordingToolHandler handler)
    {
        var dispatcher = new RealToolDispatcher(new[] { handler });
        var journal = new InMemoryToolDispatchJournal();
        var resultStore = new InMemoryDurableToolResultStore();
        var executor = new DefaultDurableToolExecutor(dispatcher, journal, resultStore);
        return (dispatcher, executor, journal, resultStore);
    }

    private static AgentToolCallRequest BuildToolCall(string toolName, string args) => new()
    {
        ToolName = toolName,
        Arguments = args,
        IdempotencyKey = "idem-r30a-err",
        ToolCallId = $"toolcall-{toolName}-0"
    };

    private sealed class RecordingToolHandler : IToolHandler
    {
        private int _invocationCount;

        public RecordingToolHandler(string toolName, ToolDescriptor descriptor)
        {
            ToolName = toolName;
            Descriptor = descriptor;
        }

        public string ToolName { get; }
        public ToolDescriptor Descriptor { get; }
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = "ok",
                SideEffect = ToolSideEffect.None
            });
        }
    }

    private sealed class ThrowingToolHandler : IToolHandler
    {
        public ThrowingToolHandler(string toolName)
        {
            ToolName = toolName;
        }

        public string ToolName { get; }
        public ToolDescriptor Descriptor => new()
        {
            Name = ToolName,
            DeclaredSideEffect = ToolSideEffect.Write,
            RecoveryStrategy = ToolRecoveryStrategy.UseCachedResult
        };
        public string? Description => $"Throwing tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";

        public ValueTask<ToolHandlerResult> HandleAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("handler boom");
        }
    }
}
