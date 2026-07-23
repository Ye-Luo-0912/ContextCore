using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;

namespace ContextCore.Tests;

// ===========================================================================
// R28-C：.NET Agent Kernel 验收测试
//
// 覆盖范围（3 个组件 + 端到端编排）：
//   1. EchoToolDispatcher — 默认 Tool 分派器（仅 echo，原样返回 payload）
//   2. InProcessTransport — 进程内 Transport（Channel inbox/outbox）
//   3. DefaultAgentKernel — 极薄决策循环（Transport → ToolDispatcher → CheckpointStore）
//
// 设计原则：
//   - 优先使用真实默认实现（InProcessTransport / EchoToolDispatcher / InMemoryAgentCheckpointStore）
//     验证端到端行为，避免过度 mock。
//   - 仅在需要注入异常/不受支持 tool 时使用手写 Stub（ThrowingToolDispatcher）。
//   - 所有异步测试使用超时 CancellationTokenSource 防止挂起。
//   - 所有代码注释使用中文。
// ===========================================================================

// ===========================================================================
// 1. EchoToolDispatcher 单元测试
// ===========================================================================

[TestClass]
[TestCategory("R28-C")]
[TestCategory("R28-C-Component")]
public sealed class EchoToolDispatcherTests
{
    [TestMethod]
    public void SupportedTools_ContainsOnlyEcho()
    {
        var dispatcher = new EchoToolDispatcher();

        CollectionAssert.AreEquivalent(new[] { "echo" }, dispatcher.SupportedTools.ToList());
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsPayloadAsResult()
    {
        var dispatcher = new EchoToolDispatcher();

        var result = await dispatcher.DispatchAsync(new ToolDispatchRequest
        {
            ToolName = "echo",
            Payload = "hello-kernel",
            RequestId = "req-1"
        });

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("hello-kernel", result.Result);
        Assert.IsNull(result.Error);
        Assert.AreEqual(TimeSpan.Zero, result.Duration);
    }

    [TestMethod]
    public void DispatchAsync_NullRequest_Throws()
    {
        var dispatcher = new EchoToolDispatcher();

        Assert.ThrowsException<ArgumentNullException>(() =>
            dispatcher.DispatchAsync(null!).AsTask());
    }

    [TestMethod]
    public void DispatchAsync_CancellationRequested_Throws()
    {
        var dispatcher = new EchoToolDispatcher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(new ToolDispatchRequest
            {
                ToolName = "echo",
                Payload = "x",
                RequestId = "req-c"
            }, cts.Token).AsTask());
    }
}

// ===========================================================================
// 2. InProcessTransport 单元测试
// ===========================================================================

[TestClass]
[TestCategory("R28-C")]
[TestCategory("R28-C-Component")]
public sealed class InProcessTransportTests
{
    [TestMethod]
    public void Constructor_ZeroOrNegativeCapacity_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InProcessTransport(0));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new InProcessTransport(-1));
    }

    [TestMethod]
    public async Task SubmitAndReceive_RoundtripsInstruction()
    {
        var transport = new InProcessTransport();
        var instruction = new AgentKernelInstruction
        {
            InstructionId = "i-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "payload-1"
        };

        await transport.SubmitAsync(instruction);
        var received = await transport.ReceiveAsync();

        Assert.IsNotNull(received);
        Assert.AreEqual("i-1", received!.InstructionId);
        Assert.AreEqual(AgentKernelInstructionKind.Execute, received.Kind);
        Assert.AreEqual("payload-1", received.Payload);
    }

    [TestMethod]
    public async Task SendResultAndReceiveResult_RoundtripsResult()
    {
        var transport = new InProcessTransport();
        var result = new AgentKernelResult
        {
            InstructionId = "i-1",
            Succeeded = true,
            Output = "out-1"
        };

        await transport.SendResultAsync(result);
        var received = await transport.ReceiveResultAsync();

        Assert.IsNotNull(received);
        Assert.AreEqual("i-1", received!.InstructionId);
        Assert.IsTrue(received.Succeeded);
        Assert.AreEqual("out-1", received.Output);
    }

    [TestMethod]
    public async Task PendingCounts_TrackCorrectly()
    {
        var transport = new InProcessTransport();

        Assert.AreEqual(0, transport.PendingInstructionCount);
        Assert.AreEqual(0, transport.PendingResultCount);

        await transport.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "i-1",
            Kind = AgentKernelInstructionKind.Execute
        });
        await transport.SendResultAsync(new AgentKernelResult
        {
            InstructionId = "i-1",
            Succeeded = true
        });

        Assert.AreEqual(1, transport.PendingInstructionCount);
        Assert.AreEqual(1, transport.PendingResultCount);

        await transport.ReceiveAsync();
        await transport.ReceiveResultAsync();

        Assert.AreEqual(0, transport.PendingInstructionCount);
        Assert.AreEqual(0, transport.PendingResultCount);
    }

    [TestMethod]
    public async Task Complete_StopsChannelSoReceiveReturnsNull()
    {
        var transport = new InProcessTransport();

        transport.Complete();

        // 通道写入端完成后，ReceiveAsync 不阻塞，返回 null
        var received = await transport.ReceiveAsync();
        Assert.IsNull(received);

        var result = await transport.ReceiveResultAsync();
        Assert.IsNull(result);
    }

    [TestMethod]
    public void SubmitAsync_NullInstruction_Throws()
    {
        var transport = new InProcessTransport();

        Assert.ThrowsException<ArgumentNullException>(() =>
            transport.SubmitAsync(null!).AsTask());
    }

    [TestMethod]
    public void SendResultAsync_NullResult_Throws()
    {
        var transport = new InProcessTransport();

        Assert.ThrowsException<ArgumentNullException>(() =>
            transport.SendResultAsync(null!).AsTask());
    }
}

// ===========================================================================
// 3. DefaultAgentKernel 验收测试
// ===========================================================================

[TestClass]
[TestCategory("R28-C")]
[TestCategory("R28-C-Acceptance")]
public sealed class DefaultAgentKernelTests
{
    /// <summary>默认超时：防止测试挂起（5 秒）。返回的 CTS 由调用方按需释放。</summary>
    private static CancellationTokenSource CreateTestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(5));

    /// <summary>构造一个 EchoKernel 测试夹具：InProcessTransport + EchoToolDispatcher + InMemoryCheckpointStore。</summary>
    private static (DefaultAgentKernel kernel, InProcessTransport transport, InMemoryAgentCheckpointStore checkpointStore)
        CreateEchoKernel()
    {
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, new EchoToolDispatcher(), checkpointStore);
        return (kernel, transport, checkpointStore);
    }

    // -----------------------------------------------------------------------
    // GetStatus 初始状态
    // -----------------------------------------------------------------------

    [TestMethod]
    public void GetStatus_InitialState_IsIdleWithZeroCounts()
    {
        var (kernel, _, _) = CreateEchoKernel();

        var status = kernel.GetStatus();

        Assert.AreEqual(AgentKernelState.Idle, status.State);
        Assert.AreEqual(0, status.ProcessedCount);
        Assert.AreEqual(0, status.PendingCount);
        Assert.IsNull(status.LastProcessedAt);
    }

    // -----------------------------------------------------------------------
    // Execute 指令：通过 EchoToolDispatcher 分派，结果经 Transport 发出
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ExecuteInstruction_EchoTool_ReturnsPayloadViaTransport()
    {
        var (kernel, transport, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "echo-payload"
        }, testCt.Token);

        // 发送 Shutdown 停止循环
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown-1",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        // 从 outbox 读取 Execute 指令的结果（Shutdown 不产生结果）
        var result = await transport.ReceiveResultAsync(testCt.Token);

        Assert.IsNotNull(result);
        Assert.AreEqual("exec-1", result!.InstructionId);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("echo-payload", result.Output);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task ExecuteInstruction_MultipleInstructions_AllProcessedAndResultsSent()
    {
        var (kernel, transport, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 提交 3 条 Execute 指令
        for (var i = 1; i <= 3; i++)
        {
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = $"exec-{i}",
                Kind = AgentKernelInstructionKind.Execute,
                Payload = $"payload-{i}"
            }, testCt.Token);
        }

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        // 应收到 3 个结果（Shutdown 不产生结果）
        var ids = new List<string>();
        while (transport.PendingResultCount > 0)
        {
            var r = await transport.ReceiveResultAsync(testCt.Token);
            Assert.IsNotNull(r);
            ids.Add(r!.InstructionId);
        }

        CollectionAssert.AreEquivalent(new[] { "exec-1", "exec-2", "exec-3" }, ids);

        var status = kernel.GetStatus();
        Assert.AreEqual(AgentKernelState.Stopped, status.State);
        Assert.AreEqual(3, status.ProcessedCount);
    }

    [TestMethod]
    public async Task ExecuteInstruction_MetadataToolEcho_ExplicitToolNameWorks()
    {
        var (kernel, transport, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-explicit",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "explicit",
            Metadata = new Dictionary<string, string> { ["tool"] = "echo" }
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Succeeded);
        Assert.AreEqual("explicit", result.Output);
    }

    [TestMethod]
    public async Task ExecuteInstruction_UnsupportedTool_ReturnsFailedResult()
    {
        var (kernel, transport, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-unsupported",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "x",
            Metadata = new Dictionary<string, string> { ["tool"] = "nonexistent-tool" }
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.AreEqual("exec-unsupported", result!.InstructionId);
        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Error!.Contains("nonexistent-tool", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteInstruction_DispatcherThrows_KernelReturnsFailedResultWithMessage()
    {
        // 使用会抛异常的 dispatcher 验证 Kernel 不崩溃，返回失败结果
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, new ThrowingToolDispatcher(new InvalidOperationException("boom")), checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-throw",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "x"
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.AreEqual("exec-throw", result!.InstructionId);
        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Error!.Contains("boom", StringComparison.Ordinal));

        // Kernel 仍正常运行并停止（未崩溃）
        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
    }

    // -----------------------------------------------------------------------
    // Checkpoint 指令：通过 IAgentCheckpointStore 保存
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task CheckpointInstruction_SavesToCheckpointStore()
    {
        var (kernel, transport, checkpointStore) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-1",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Payload = "{\"state\":\"running\"}",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-abc",
                ["workspaceId"] = "ws-xyz"
            }
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.AreEqual("ckpt-1", result!.InstructionId);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("ckpt-1", result.Output);

        // 验证 checkpoint 已持久化到 store
        Assert.AreEqual(1, checkpointStore.Count);
        var saved = await checkpointStore.GetAsync("ws-xyz", "ckpt-1", testCt.Token);
        Assert.IsNotNull(saved);
        Assert.AreEqual("ckpt-1", saved!.CheckpointId);
        Assert.AreEqual("session-abc", saved.Session.Value);
        Assert.AreEqual("ws-xyz", saved.Session.WorkspaceId);
        Assert.AreEqual("{\"state\":\"running\"}", saved.StateJson);
    }

    [TestMethod]
    public async Task CheckpointInstruction_DefaultSessionWorkspace_WhenMetadataMissing()
    {
        var (kernel, transport, checkpointStore) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-default",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Payload = "{}"
            // 不提供 sessionId / workspaceId → 使用默认值
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Succeeded);

        // 默认 workspace 下应能查到 checkpoint
        var saved = await checkpointStore.GetAsync("kernel-default-workspace", "ckpt-default", testCt.Token);
        Assert.IsNotNull(saved);
        Assert.AreEqual("kernel-default-session", saved!.Session.Value);
    }

    // -----------------------------------------------------------------------
    // Shutdown 指令与排空
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ShutdownInstruction_StopsKernelLoop()
    {
        var (kernel, _, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
    }

    [TestMethod]
    public async Task Shutdown_DrainsPendingInstructionsBeforeStopping()
    {
        var (kernel, transport, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 先提交 2 条 Execute，再提交 Shutdown
        // Shutdown 触发排空：排空期间应处理已在 inbox 中的 Execute 指令
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "drain-1"
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-2",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "drain-2"
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        // 排空应处理 2 条 Execute 指令并发出结果
        var results = new List<AgentKernelResult>();
        while (transport.PendingResultCount > 0)
        {
            var r = await transport.ReceiveResultAsync(testCt.Token);
            if (r is not null) results.Add(r);
        }

        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(r => r.Succeeded));
        CollectionAssert.AreEquivalent(
            new[] { "drain-1", "drain-2" },
            results.Select(r => r.Output).ToList());

        Assert.AreEqual(2, kernel.GetStatus().ProcessedCount);
        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
    }

    // -----------------------------------------------------------------------
    // 生命周期错误场景
    // -----------------------------------------------------------------------

    [TestMethod]
    public void RunAsync_CalledTwice_ThrowsInvalidOperationException()
    {
        var (kernel, _, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 等待 kernel 进入 Running 状态
        Assert.IsTrue(SpinWait.SpinUntil(() => kernel.GetStatus().State == AgentKernelState.Running, TimeSpan.FromSeconds(2)));

        // 第二次调用应抛异常
        Assert.ThrowsException<InvalidOperationException>(() =>
            kernel.RunAsync(testCt.Token).AsTask().GetAwaiter().GetResult());

        // 清理：发送 Shutdown 让第一个循环退出
        kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token).AsTask().Wait(TimeSpan.FromSeconds(2));
        runTask.Wait(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task SubmitAsync_AfterStopped_ThrowsInvalidOperationException()
    {
        var (kernel, _, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;

        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "after-stop",
                Kind = AgentKernelInstructionKind.Execute
            }).AsTask());
    }

    [TestMethod]
    public void Constructor_NullArguments_Throws()
    {
        var transport = new InProcessTransport();
        var dispatcher = new EchoToolDispatcher();
        var store = new InMemoryAgentCheckpointStore();

        Assert.ThrowsException<ArgumentNullException>(() =>
            new DefaultAgentKernel(null!, dispatcher, store));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new DefaultAgentKernel(transport, null!, store));
        Assert.ThrowsException<ArgumentNullException>(() =>
            new DefaultAgentKernel(transport, dispatcher, null!));
    }

    [TestMethod]
    public void SubmitAsync_NullInstruction_Throws()
    {
        var (kernel, _, _) = CreateEchoKernel();

        Assert.ThrowsException<ArgumentNullException>(() =>
            kernel.SubmitAsync(null!).AsTask());
    }

    // -----------------------------------------------------------------------
    // 取消传播
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task RunAsync_ExternalCancellation_StopsLoop()
    {
        var (kernel, _, _) = CreateEchoKernel();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // RunAsync 在取消时应抛 OperationCanceledException 并停止
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            kernel.RunAsync(cts.Token).AsTask());

        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
    }

    // -----------------------------------------------------------------------
    // ProcessedCount / LastProcessedAt 追踪
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ProcessedCount_And_LastProcessedAt_TrackedAfterRun()
    {
        var (kernel, _, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        var before = DateTimeOffset.UtcNow;
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "x"
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        var status = kernel.GetStatus();
        Assert.AreEqual(1, status.ProcessedCount);
        Assert.IsNotNull(status.LastProcessedAt);
        Assert.IsTrue(status.LastProcessedAt >= before);
    }

    // -----------------------------------------------------------------------
    // 挂起指令计数追踪
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task PendingCount_TracksInboxBeforeProcessing()
    {
        var (kernel, _, _) = CreateEchoKernel();
        var testCt = CreateTestTimeout();

        // RunAsync 尚未启动：SubmitAsync 写入 inbox，PendingCount 应反映
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "x"
        }, testCt.Token);

        Assert.AreEqual(1, kernel.GetStatus().PendingCount);

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;

        // 处理完毕后 PendingCount 归零
        Assert.AreEqual(0, kernel.GetStatus().PendingCount);
    }
}

// ===========================================================================
// 测试 Stub：抛异常的 ToolDispatcher
// ===========================================================================

/// <summary>
/// 测试用 Stub：DispatchAsync 始终抛出指定异常，用于验证 Kernel 的异常隔离。
/// </summary>
internal sealed class ThrowingToolDispatcher : IToolDispatcher
{
    private static readonly IReadOnlySet<string> s_supportedTools =
        new HashSet<string>(StringComparer.Ordinal) { "echo" };

    private readonly Exception _exception;

    public ThrowingToolDispatcher(Exception exception)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public IReadOnlySet<string> SupportedTools => s_supportedTools;

    public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw _exception;
    }
}
