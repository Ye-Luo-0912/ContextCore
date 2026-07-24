using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using System.Text.Json;
using System.Threading.Channels;

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

        // R28-E P1-1：StateJson 现为 KernelCheckpointStateDto（SnapshotId + CommittedResults），
        // 不再直接保存 instruction.Payload。手动 Payload 转存到 Metadata["manualPayload"]。
        Assert.IsNotNull(saved.StateJson);
        using (var doc = JsonDocument.Parse(saved.StateJson!))
        {
            Assert.IsTrue(doc.RootElement.TryGetProperty("SnapshotId", out _),
                "StateJson 应包含 KernelCheckpointState.SnapshotId 字段。");
            Assert.IsTrue(doc.RootElement.TryGetProperty("CommittedResults", out var arr),
                "StateJson 应包含 KernelCheckpointState.CommittedResults 字段。");
            Assert.AreEqual(0, arr.GetArrayLength(),
                "无 tool 执行时 CommittedResults 应为空数组。");
        }
        Assert.IsTrue(saved.Metadata.TryGetValue("manualPayload", out var manualPayload),
            "手动 Checkpoint 的 Payload 应转存到 Metadata[\"manualPayload\"]。");
        Assert.AreEqual("{\"state\":\"running\"}", manualPayload);
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

// ===========================================================================
// 测试 Stub（R28-C WP-E 验收测试用）
// ===========================================================================

/// <summary>
/// 计数 ToolDispatcher：原样返回 payload（echo 语义），统计 DispatchAsync 调用次数，
/// 可配置 SideEffect（默认 None=自动提交）。
/// SideEffectSelector（可选）按请求动态决定副作用分类，优先于 SideEffect 默认值。
/// </summary>
internal sealed class CountingToolDispatcher : IToolDispatcher
{
    private static readonly IReadOnlySet<string> s_supportedTools =
        new HashSet<string>(StringComparer.Ordinal) { "echo" };

    private int _dispatchCount;

    public int DispatchCount => _dispatchCount;
    public ToolSideEffect SideEffect { get; init; } = ToolSideEffect.None;
    public Func<ToolDispatchRequest, ToolSideEffect>? SideEffectSelector { get; init; }

    public IReadOnlySet<string> SupportedTools => s_supportedTools;

    public ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _dispatchCount);
        var se = SideEffectSelector?.Invoke(request) ?? SideEffect;
        return ValueTask.FromResult(new ToolDispatchResult
        {
            Succeeded = true,
            Result = request.Payload,
            Duration = TimeSpan.Zero,
            SideEffect = se
        });
    }
}

/// <summary>
/// 不稳定 Transport：前 N 次 SendResultAsync 抛出指定异常，之后正常写入 outbox。
/// 用于验证 TransportFailurePolicy（FailFast/Retry/FallbackToDeterministic）。
/// ReceiveAsync 不被 DefaultAgentKernel 调用（Kernel 自带 inbox），返回 null。
/// </summary>
internal sealed class FlakyTransport : IAgentKernelTransport
{
    private readonly int _failFirstN;
    private readonly Exception _exception;
    private int _sendCount;
    private readonly Channel<AgentKernelResult?> _outbox =
        Channel.CreateBounded<AgentKernelResult?>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public FlakyTransport(int failFirstN, Exception exception)
    {
        if (failFirstN < 0) throw new ArgumentOutOfRangeException(nameof(failFirstN));
        _failFirstN = failFirstN;
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public int SendCount => _sendCount;
    public int PendingResultCount => _outbox.Reader.Count;

    public ValueTask<AgentKernelInstruction?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        // DefaultAgentKernel 不调用此方法（使用自身 inbox）；返回 null 表示无远程指令
        return ValueTask.FromResult<AgentKernelInstruction?>(null);
    }

    public async ValueTask SendResultAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var n = Interlocked.Increment(ref _sendCount);
        if (n <= _failFirstN)
        {
            throw _exception;
        }
        await _outbox.Writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AgentKernelResult?> ReceiveResultAsync(CancellationToken cancellationToken = default)
        => await _outbox.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    public void Complete()
    {
        _outbox.Writer.TryComplete();
    }
}

/// <summary>
/// 桩 AgentContextProjector：忽略输入，返回预设 AgentContextSnapshot（含可配置 TokenBudget/ActualTokens）。
/// 用于验证 BuildContext 指令的 TokenBudget 传播与最终快照合规性。
/// </summary>
internal sealed class StubAgentContextProjector : IAgentContextProjector
{
    public ContextDecisionExecutionResult? LastExecution { get; private set; }
    public ProjectionContext? LastContext { get; private set; }
    public int TokenBudget { get; init; }
    public int ActualTokens { get; init; }

    public AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet)
        => BuildSnapshot();

    public AgentContextSnapshot Project(ContextDecisionResult result, CandidateWorkingSet workingSet, ProjectionContext context)
        => BuildSnapshot();

    public AgentContextSnapshot Project(ContextDecisionExecutionResult execution)
    {
        LastExecution = execution;
        return BuildSnapshot();
    }

    public AgentContextSnapshot Project(ContextDecisionExecutionResult execution, ProjectionContext context)
    {
        LastExecution = execution;
        LastContext = context;
        return BuildSnapshot();
    }

    private AgentContextSnapshot BuildSnapshot()
    {
        return new AgentContextSnapshot
        {
            SnapshotId = "snap-stub",
            Session = new AgentSessionId
            {
                Value = "session-stub",
                WorkspaceId = "ws-stub",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            TokenBudget = TokenBudget,
            ActualTokens = ActualTokens
        };
    }
}

// ===========================================================================
// R28-C WP-E：Workstream C 验收测试（6 项硬验收）
//
// 覆盖：
//   1. CancelledAgentRunProducesResumableCheckpoint — 取消时自动产出可恢复 checkpoint
//   2. ResumeDoesNotDuplicateCommittedToolResult — resume 后已提交 tool 结果不重复执行
//   3. UnknownSideEffectIsNotAutomaticallyReplayed — Unknown 副作用不自动提交/重放
//   4. BoundedStreamMaintainsMemoryCeiling — bounded inbox（256）内存上限
//   5. ModelTransportFailureFallsBackAccordingToPolicy — 三种 Transport 失败策略
//   6. AgentFinalContextNeverExceedsTokenBudget — BuildContext token 预算传播与合规
// ===========================================================================

[TestClass]
[TestCategory("R28-C")]
[TestCategory("R28-C-WP-E")]
public sealed class R28CWorkstreamCAcceptanceTests
{
    private static CancellationTokenSource CreateTestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10));

    /// <summary>等待 kernel ProcessedCount 达到目标值（带超时）。</summary>
    private static bool WaitForProcessed(DefaultAgentKernel kernel, int target, TimeSpan timeout)
        => SpinWait.SpinUntil(() => kernel.GetStatus().ProcessedCount >= target, timeout);

    // -----------------------------------------------------------------------
    // 1. CancelledAgentRunProducesResumableCheckpoint
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task CancelledAgentRunProducesResumableCheckpoint()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runTask = Task.Run(() => kernel.RunAsync(runCts.Token).AsTask(), runCts.Token);

        // 提交一个 Write 副作用的 exec（会自动提交到 _committedToolResults）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-cancel-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "will-be-checkpointed",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-cancel",
                ["workspaceId"] = "ws-cancel"
            }
        }, runCts.Token);

        // 等待 exec 被处理并提交
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)),
            "exec-cancel-1 未在超时内处理。");

        // 取消 RunAsync（非 graceful）→ 触发 AutoCheckpoint
        runCts.Cancel();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // 预期：外部取消
        }

        // 验证 checkpoint 已持久化
        Assert.AreEqual(1, checkpointStore.Count,
            "取消后应自动产出 1 个 checkpoint。");

        var session = new AgentSessionId
        {
            Value = "session-cancel",
            WorkspaceId = "ws-cancel",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var checkpoints = await checkpointStore.ListAsync(session, take: 10);
        Assert.AreEqual(1, checkpoints.Count, "应能按 session 列出该 checkpoint。");

        var cp = checkpoints[0];
        Assert.AreEqual("ws-cancel", cp.Session.WorkspaceId);
        Assert.IsNotNull(cp.StateJson);

        // 验证 StateJson 含已提交 tool 结果（RequestId == exec-cancel-1）
        using var doc = JsonDocument.Parse(cp.StateJson!);
        Assert.IsTrue(doc.RootElement.TryGetProperty("CommittedResults", out var arr));
        Assert.AreEqual(1, arr.GetArrayLength());
        Assert.IsTrue(arr[0].TryGetProperty("RequestId", out var rid));
        Assert.AreEqual("exec-cancel-1", rid.GetString());
        Assert.IsTrue(arr[0].TryGetProperty("SideEffect", out var se));
        Assert.AreEqual((byte)ToolSideEffect.Write, se.GetByte());
    }

    // -----------------------------------------------------------------------
    // 2. ResumeDoesNotDuplicateCommittedToolResult
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ResumeDoesNotDuplicateCommittedToolResult()
    {
        // 共享 dispatcher 跨两个 kernel 实例，以统计总 dispatch 次数
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var checkpointStore = new InMemoryAgentCheckpointStore();

        // --- Kernel1：处理 exec-resume-1 并取消以产出 checkpoint ---
        var transport1 = new InProcessTransport();
        var kernel1 = new DefaultAgentKernel(transport1, dispatcher, checkpointStore);
        var runCts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runTask1 = Task.Run(() => kernel1.RunAsync(runCts1.Token).AsTask(), runCts1.Token);

        await kernel1.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-resume-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "committed-payload",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-resume",
                ["workspaceId"] = "ws-resume"
            }
        }, runCts1.Token);

        Assert.IsTrue(WaitForProcessed(kernel1, 1, TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, dispatcher.DispatchCount, "exec-resume-1 应被 dispatch 一次。");

        runCts1.Cancel();
        try { await runTask1; }
        catch (OperationCanceledException) { }

        // 取回 checkpoint
        var session = new AgentSessionId
        {
            Value = "session-resume",
            WorkspaceId = "ws-resume",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var checkpoints = await checkpointStore.ListAsync(session, take: 10);
        Assert.AreEqual(1, checkpoints.Count);
        var checkpoint = checkpoints[0];

        // --- Kernel2：从 checkpoint 恢复，重放相同 InstructionId ---
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        await kernel2.ResumeAsync(checkpoint, testCt.Token);

        var runTask2 = Task.Run(() => kernel2.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 重放相同 InstructionId —— 应命中缓存，不重新 dispatch
        await kernel2.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-resume-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "should-not-reach-dispatcher"
        }, testCt.Token);
        await kernel2.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask2;

        // dispatch 次数应仍为 1（Kernel2 命中缓存未调用 dispatcher）
        Assert.AreEqual(1, dispatcher.DispatchCount,
            "resume 后已提交 tool 结果不应重新 dispatch（幂等去重）。");

        var result = await transport2.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.AreEqual("exec-resume-1", result!.InstructionId);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("committed-payload", result.Output,
            "应返回缓存的原始 payload，而非重放后的 payload。");
    }

    // -----------------------------------------------------------------------
    // 3. UnknownSideEffectIsNotAutomaticallyReplayed
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownSideEffectIsNotAutomaticallyReplayed()
    {
        // 混合副作用：exec-write-1 → Write（提交），exec-unknown-1 → Unknown（不提交）
        var dispatcher = new CountingToolDispatcher
        {
            SideEffectSelector = req => req.RequestId == "exec-unknown-1"
                ? ToolSideEffect.Unknown
                : ToolSideEffect.Write
        };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runTask = Task.Run(() => kernel.RunAsync(runCts.Token).AsTask(), runCts.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-write-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "will-be-committed",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-unknown",
                ["workspaceId"] = "ws-unknown"
            }
        }, runCts.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-unknown-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "will-not-be-committed",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-unknown",
                ["workspaceId"] = "ws-unknown"
            }
        }, runCts.Token);

        // 等待两条均处理完毕
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));

        runCts.Cancel();
        try { await runTask; }
        catch (OperationCanceledException) { }

        // AutoCheckpoint 因 exec-write-1 已提交而触发（至少有一条提交结果）
        Assert.AreEqual(1, checkpointStore.Count,
            "存在已提交结果时应产出 checkpoint。");

        var session = new AgentSessionId
        {
            Value = "session-unknown",
            WorkspaceId = "ws-unknown",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var checkpoints = await checkpointStore.ListAsync(session, take: 10);
        var cp = checkpoints[0];

        // 验证 CommittedResults 含 exec-write-1 但不含 exec-unknown-1
        using var doc = JsonDocument.Parse(cp.StateJson!);
        Assert.IsTrue(doc.RootElement.TryGetProperty("CommittedResults", out var arr));
        var requestIds = new List<string>();
        for (var i = 0; i < arr.GetArrayLength(); i++)
        {
            Assert.IsTrue(arr[i].TryGetProperty("RequestId", out var rid));
            requestIds.Add(rid.GetString()!);
        }
        CollectionAssert.Contains(requestIds, "exec-write-1",
            "Write 副作用结果应被提交到 checkpoint。");
        CollectionAssert.DoesNotContain(requestIds, "exec-unknown-1",
            "Unknown 副作用结果不应出现在已提交结果中（不自动提交/重放）。");
    }

    // -----------------------------------------------------------------------
    // 4. BoundedStreamMaintainsMemoryCeiling
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task BoundedStreamMaintainsMemoryCeiling()
    {
        var (kernel, _, _) = CreateEchoKernelForBounded();
        var fillCt = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 提交 256 条指令填满 inbox（容量 256）
        for (var i = 0; i < 256; i++)
        {
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = $"exec-bounded-{i}",
                Kind = AgentKernelInstructionKind.Execute,
                Payload = "x"
            }, fillCt.Token);
        }

        Assert.AreEqual(256, kernel.GetStatus().PendingCount,
            "inbox 容量上限应为 256。");

        // 第 257 条应被阻塞（Wait 模式）；短超时 CTS 应触发 OCE
        var overflowCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "exec-bounded-256",
                Kind = AgentKernelInstructionKind.Execute,
                Payload = "overflow"
            }, overflowCts.Token).AsTask());

        // 仍为 256（第 257 条未入队）
        Assert.AreEqual(256, kernel.GetStatus().PendingCount);

        // 清理：启动 RunAsync 排空 inbox，待排空后提交 Shutdown
        var runCt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = Task.Run(() => kernel.RunAsync(runCt.Token).AsTask(), runCt.Token);

        // 等待 256 条全部处理完毕（inbox 排空，腾出容量）
        Assert.IsTrue(WaitForProcessed(kernel, 256, TimeSpan.FromSeconds(8)),
            "inbox 未在超时内排空。");

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, runCt.Token);
        await runTask;
    }

    private static (DefaultAgentKernel kernel, InProcessTransport transport, InMemoryAgentCheckpointStore checkpointStore)
        CreateEchoKernelForBounded()
    {
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, new EchoToolDispatcher(), checkpointStore);
        return (kernel, transport, checkpointStore);
    }

    // -----------------------------------------------------------------------
    // 5. ModelTransportFailureFallsBackAccordingToPolicy
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task FailFastPolicy_TransportFailure_TerminatesKernel()
    {
        var transport = new FlakyTransport(failFirstN: int.MaxValue,
            new InvalidOperationException("transport-down"));
        var dispatcher = new EchoToolDispatcher();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore,
            transportOptions: KernelTransportOptions.Default); // FailFast
        var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runTask = Task.Run(() => kernel.RunAsync(runCts.Token).AsTask(), runCts.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-failfast",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "x"
        }, runCts.Token);

        // FailFast：SendResultAsync 抛出 → RunAsync 抛出 InvalidOperationException
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runTask);
        Assert.IsTrue(ex.Message.Contains("transport-down", StringComparison.Ordinal));
        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
    }

    [TestMethod]
    public async Task RetryPolicy_TransportAlwaysFails_ThrowsAfterExhaustedRetries()
    {
        var transport = new FlakyTransport(failFirstN: int.MaxValue,
            new InvalidOperationException("transport-down"));
        var dispatcher = new EchoToolDispatcher();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var retryOpts = new KernelTransportOptions
        {
            FailurePolicy = TransportFailurePolicy.Retry,
            MaxRetries = 1,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore,
            transportOptions: retryOpts);
        var runCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runTask = Task.Run(() => kernel.RunAsync(runCts.Token).AsTask(), runCts.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-retry",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "x"
        }, runCts.Token);

        // Retry（MaxRetries=1）：2 次尝试均失败 → 抛 InvalidOperationException
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => runTask);
        Assert.IsTrue(ex.Message.Contains("2", StringComparison.Ordinal),
            $"异常消息应含尝试次数 2；实际: {ex.Message}");
        Assert.AreEqual(2, transport.SendCount, "应共尝试 2 次（1 初始 + 1 重试）。");
    }

    [TestMethod]
    public async Task FallbackToDeterministicPolicy_TransportFailureContinuesLoop()
    {
        // 前 1 次发送失败，之后成功
        var transport = new FlakyTransport(failFirstN: 1,
            new InvalidOperationException("transient"));
        var dispatcher = new EchoToolDispatcher();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var fallbackOpts = new KernelTransportOptions
        {
            FailurePolicy = TransportFailurePolicy.FallbackToDeterministic
        };
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore,
            transportOptions: fallbackOpts);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 第一条 exec 的发送失败 → fallback 降级（结果丢弃），Kernel 继续
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-fallback-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "dropped"
        }, testCt.Token);

        // 第二条 exec 的发送成功 → 结果进入 outbox
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-fallback-2",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "delivered"
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        // RunAsync 应正常完成（未抛出）
        await runTask;

        // outbox 应只有第二条的结果（第一条被 fallback 丢弃）
        Assert.AreEqual(1, transport.PendingResultCount);
        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.AreEqual("exec-fallback-2", result!.InstructionId);
        Assert.AreEqual("delivered", result.Output);

        // 两条 exec 均被处理（ProcessedCount=2，Shutdown 不计）
        Assert.AreEqual(2, kernel.GetStatus().ProcessedCount);
        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
    }

    // -----------------------------------------------------------------------
    // 6. AgentFinalContextNeverExceedsTokenBudget
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task AgentFinalContextNeverExceedsTokenBudget()
    {
        // 使用 RecordingDecisionRuntime（记录请求）+ StubAgentContextProjector（返回合规快照）
        var decisionResult = R28BTestHelpers.MakeResult("req-buildctx-budget");
        var runtime = new RecordingDecisionRuntime(decisionResult);
        var projector = new StubAgentContextProjector
        {
            TokenBudget = 500,
            ActualTokens = 480 // 严格 < TokenBudget（合规）
        };

        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, new EchoToolDispatcher(), checkpointStore,
            decisionRuntime: runtime, contextProjector: projector);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "buildctx-budget-1",
            Kind = AgentKernelInstructionKind.BuildContext,
            Metadata = new Dictionary<string, string>
            {
                ["workspaceId"] = "ws-budget",
                ["collectionId"] = "col-budget",
                ["sessionId"] = "session-budget",
                ["tokenBudget"] = "500"
            }
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        await runTask;

        // 验证 TokenBudget 从 instruction Metadata 传播到 runtime request
        Assert.IsNotNull(runtime.LastRequest);
        Assert.AreEqual(500, runtime.LastRequest!.TokenBudget,
            "TokenBudget 应从 Metadata 传播到 ContextDecisionRuntimeRequest。");
        Assert.AreEqual(ContextDecisionPurpose.AgentContext, runtime.LastRequest.Purpose);

        // 验证最终快照 token 合规（ActualTokens <= TokenBudget）
        var result = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Succeeded);
        Assert.IsNotNull(result.Snapshot);
        Assert.IsTrue(result.Snapshot!.ActualTokens <= result.Snapshot.TokenBudget,
            $"快照 ActualTokens({result.Snapshot.ActualTokens}) 不应超过 TokenBudget({result.Snapshot.TokenBudget})。");
        Assert.AreEqual(500, result.Snapshot.TokenBudget);
    }
}

// ===========================================================================
// R28-E：Agent Kernel 运行语义可靠性测试
//
// 覆盖 4 项关键修复：
//   P1-1: IAgentCheckpointFactory 统一手动/自动 checkpoint 格式
//   P1-2: ResumeAsync 通过 IAgentContextSnapshotStore 恢复 _lastSnapshot
//   P1-3: AcknowledgeToolResult / RejectToolResult / QueryToolDispatchState 指令
//   P1-4: IToolDispatchJournal 状态机 (Prepared→Dispatched→Committed→ResultDelivered)
// ===========================================================================

[TestClass]
[TestCategory("R28-E")]
public sealed class R28E_AgentKernelReliabilityTests
{
    private static CancellationTokenSource CreateTestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(5));

    private static bool WaitForProcessed(DefaultAgentKernel kernel, int target, TimeSpan timeout)
        => SpinWait.SpinUntil(() => kernel.GetStatus().ProcessedCount >= target, timeout);

    // -----------------------------------------------------------------------
    // P1-1: IAgentCheckpointFactory — 手动 Checkpoint 与 AutoCheckpoint 同格式
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ManualCheckpoint_IncludesCommittedResultsAndSnapshotId()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 先执行一条 tool（产生已提交结果）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-ack-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "p1",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-manual",
                ["workspaceId"] = "ws-manual"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token); // 取走 exec 结果

        // 发送手动 Checkpoint 指令
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-manual-1",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Payload = "{\"user\":\"data\"}",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-manual",
                ["workspaceId"] = "ws-manual"
            }
        }, testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;

        var cp = await checkpointStore.GetAsync("ws-manual", "ckpt-manual-1", testCt.Token);
        Assert.IsNotNull(cp);
        Assert.IsNotNull(cp!.StateJson);

        // StateJson 应为 KernelCheckpointStateDto（含 CommittedResults）
        using (var doc = JsonDocument.Parse(cp.StateJson!))
        {
            Assert.IsTrue(doc.RootElement.TryGetProperty("CommittedResults", out var arr));
            Assert.AreEqual(1, arr.GetArrayLength(),
                "手动 checkpoint 应包含已提交的 tool 结果。");
            Assert.IsTrue(arr[0].TryGetProperty("RequestId", out var rid));
            Assert.AreEqual("exec-ack-1", rid.GetString());
        }

        // 手动 Payload 应转存到 Metadata["manualPayload"]，不直接覆盖 StateJson
        Assert.IsTrue(cp.Metadata.TryGetValue("manualPayload", out var manualPayload));
        Assert.AreEqual("{\"user\":\"data\"}", manualPayload);
    }

    [TestMethod]
    public async Task ManualAndAutoCheckpoint_ProduceSameStateJsonShape()
    {
        // 同一 Kernel 状态下，手动 Checkpoint 与取消触发的 AutoCheckpoint
        // 都应产出包含 CommittedResults + SnapshotId 字段的 StateJson
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-shared-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "shared",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-shared",
                ["workspaceId"] = "ws-shared"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // 手动 checkpoint
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-shared-manual",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-shared",
                ["workspaceId"] = "ws-shared"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // 取消触发 AutoCheckpoint
        testCt.Cancel();
        try { await runTask; }
        catch (OperationCanceledException) { }

        Assert.AreEqual(2, checkpointStore.Count,
            "应存在 1 个手动 + 1 个自动 checkpoint。");

        var manualCp = await checkpointStore.GetAsync("ws-shared", "ckpt-shared-manual", CancellationToken.None);
        Assert.IsNotNull(manualCp);

        var session = new AgentSessionId
        {
            Value = "session-shared",
            WorkspaceId = "ws-shared",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var autoCheckpoints = await checkpointStore.ListAsync(session, take: 10);
        var autoCp = autoCheckpoints.FirstOrDefault(c => c.CheckpointId != "ckpt-shared-manual");
        Assert.IsNotNull(autoCp, "应存在一个 AutoCheckpoint。");

        // 两者 StateJson 都应含 CommittedResults 数组
        Assert.IsNotNull(manualCp!.StateJson);
        Assert.IsNotNull(autoCp!.StateJson);
        using var manualDoc = JsonDocument.Parse(manualCp.StateJson!);
        using var autoDoc = JsonDocument.Parse(autoCp.StateJson!);
        Assert.IsTrue(manualDoc.RootElement.TryGetProperty("CommittedResults", out var manualArr));
        Assert.IsTrue(autoDoc.RootElement.TryGetProperty("CommittedResults", out var autoArr));
        Assert.AreEqual(1, manualArr.GetArrayLength());
        Assert.AreEqual(1, autoArr.GetArrayLength());
    }

    // -----------------------------------------------------------------------
    // P1-3: AcknowledgeToolResult — Unknown 副作用结果从 pending 移到 committed
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task AcknowledgeToolResult_MovesPendingToCommitted()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Unknown };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 执行 Unknown 副作用 tool → 进入 pending
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-unknown-ack",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "unknown-1",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-ack",
                ["workspaceId"] = "ws-ack"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        var execResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(execResult);
        Assert.IsTrue(execResult!.Succeeded);
        Assert.AreEqual(1, dispatcher.DispatchCount, "tool 应被分派一次。");

        // Ack 指令缺少 requestId → 应失败
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-no-id",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        var ackNoIdResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(ackNoIdResult);
        Assert.IsFalse(ackNoIdResult!.Succeeded);
        Assert.IsTrue(ackNoIdResult.Error!.Contains("requestId", StringComparison.Ordinal));

        // Ack 不存在的 requestId → 应失败
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-missing",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "non-existent" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 3, TimeSpan.FromSeconds(3)));
        var ackMissingResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(ackMissingResult);
        Assert.IsFalse(ackMissingResult!.Succeeded);
        Assert.AreEqual("non-existent", ackMissingResult.AffectedRequestId);

        // Ack 正确的 requestId → 应成功
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-ok",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-unknown-ack" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 4, TimeSpan.FromSeconds(3)));
        var ackOkResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(ackOkResult);
        Assert.IsTrue(ackOkResult!.Succeeded);
        Assert.AreEqual("exec-unknown-ack", ackOkResult.AffectedRequestId);
        Assert.IsTrue((ackOkResult.Output ?? "").Contains("acknowledged", StringComparison.Ordinal));

        // 再次 Ack 同一 requestId → 应失败（已从 pending 移除）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-duplicate",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-unknown-ack" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 5, TimeSpan.FromSeconds(3)));
        var ackDupResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(ackDupResult);
        Assert.IsFalse(ackDupResult!.Succeeded);

        // 手动 Checkpoint 验证 ack 后的结果已进入 committed
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-verify-ack",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-ack",
                ["workspaceId"] = "ws-ack"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 6, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // Shutdown
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;

        // 验证 checkpoint StateJson 含已 ack 的 tool 结果
        var cp = await checkpointStore.GetAsync("ws-ack", "ckpt-verify-ack", CancellationToken.None);
        Assert.IsNotNull(cp);
        Assert.IsNotNull(cp!.StateJson);
        using var doc = JsonDocument.Parse(cp.StateJson!);
        Assert.IsTrue(doc.RootElement.TryGetProperty("CommittedResults", out var arr));
        Assert.AreEqual(1, arr.GetArrayLength(),
            "ack 后 tool 结果应进入 committed 并被 checkpoint 持久化。");
        Assert.IsTrue(arr[0].TryGetProperty("RequestId", out var rid));
        Assert.AreEqual("exec-unknown-ack", rid.GetString());
    }

    [TestMethod]
    public async Task AcknowledgedToolResult_IsCommittedAndNotReexecuted()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Unknown };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 1. Execute Unknown → pending
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-ack-commit",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "payload-x",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-ack-c",
                ["workspaceId"] = "ws-ack-c"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // 2. Ack → committed
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-commit",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-ack-commit" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // 3. 再次 Execute 同一 InstructionId → 应返回缓存（dispatch 不增加）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-ack-commit",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "payload-y",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-ack-c",
                ["workspaceId"] = "ws-ack-c"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 3, TimeSpan.FromSeconds(3)));
        var cachedResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsNotNull(cachedResult);
        Assert.IsTrue(cachedResult!.Succeeded);
        Assert.AreEqual("payload-x", cachedResult.Output,
            "应返回缓存的原始 payload-x，而非重放后的 payload-y。");
        Assert.AreEqual(1, dispatcher.DispatchCount, "已提交结果不应重新分派 tool。");

        // 4. Shutdown
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;
    }

    // -----------------------------------------------------------------------
    // P1-3: RejectToolResult — Unknown 副作用结果被丢弃
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task RejectToolResult_DropsPendingResult()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Unknown };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // Execute Unknown → pending
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-reject-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "to-reject",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-reject",
                ["workspaceId"] = "ws-reject"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // Reject 缺少 requestId → 失败
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "reject-no-id",
            Kind = AgentKernelInstructionKind.RejectToolResult
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        var rejectNoId = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsFalse(rejectNoId!.Succeeded);

        // Reject 正确 requestId → 成功
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "reject-ok",
            Kind = AgentKernelInstructionKind.RejectToolResult,
            Metadata = new Dictionary<string, string>
            {
                ["requestId"] = "exec-reject-1",
                ["reason"] = "external-validation-failed"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 3, TimeSpan.FromSeconds(3)));
        var rejectOk = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsTrue(rejectOk!.Succeeded);
        Assert.AreEqual("exec-reject-1", rejectOk.AffectedRequestId);
        Assert.IsTrue((rejectOk.Output ?? "").Contains("rejected", StringComparison.Ordinal));
        Assert.IsTrue((rejectOk.Output ?? "").Contains("external-validation-failed", StringComparison.Ordinal));

        // 再次 Reject 同一 requestId → 失败（已从 pending 移除）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "reject-dup",
            Kind = AgentKernelInstructionKind.RejectToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-reject-1" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 4, TimeSpan.FromSeconds(3)));
        var rejectDup = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsFalse(rejectDup!.Succeeded);

        // 再次 Execute 同一 InstructionId → 应重新分派（rejected 结果未提交）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-reject-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "retry-after-reject",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-reject",
                ["workspaceId"] = "ws-reject"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 5, TimeSpan.FromSeconds(3)));
        var retryResult = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsTrue(retryResult!.Succeeded);
        Assert.AreEqual("retry-after-reject", retryResult.Output,
            "rejected 后再次 Execute 应返回新结果（未缓存）。");
        Assert.AreEqual(2, dispatcher.DispatchCount, "rejected 结果不应缓存，应重新分派。");

        // Shutdown
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;
    }

    // -----------------------------------------------------------------------
    // P1-3: QueryToolDispatchState — 返回分派状态
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task QueryToolDispatchState_ReturnsCorrectStatePerLifecycle()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Unknown };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, checkpointStore);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // Query 不存在的 requestId → Prepared（无 journal 时返回 Prepared/not-found）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "query-pre",
            Kind = AgentKernelInstructionKind.QueryToolDispatchState,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-q-1" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        var q1 = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsTrue(q1!.Succeeded);
        Assert.AreEqual(ToolDispatchState.Prepared, q1.DispatchState,
            "未执行 tool 应返回 Prepared 状态。");
        Assert.AreEqual("exec-q-1", q1.AffectedRequestId);

        // Execute Unknown → pending (Dispatched)
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-q-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "q-payload",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-q",
                ["workspaceId"] = "ws-q"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // Query → Dispatched (in pending)
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "query-pending",
            Kind = AgentKernelInstructionKind.QueryToolDispatchState,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-q-1" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 3, TimeSpan.FromSeconds(3)));
        var q2 = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsTrue(q2!.Succeeded);
        Assert.AreEqual(ToolDispatchState.Dispatched, q2.DispatchState,
            "Unknown 副作用结果在 pending 时应返回 Dispatched 状态。");

        // Ack → committed
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-q",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-q-1" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 4, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // Query → Committed
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "query-committed",
            Kind = AgentKernelInstructionKind.QueryToolDispatchState,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-q-1" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 5, TimeSpan.FromSeconds(3)));
        var q3 = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsTrue(q3!.Succeeded);
        Assert.AreEqual(ToolDispatchState.Committed, q3.DispatchState,
            "ack 后应返回 Committed 状态。");

        // Query 缺少 requestId → 失败
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "query-no-id",
            Kind = AgentKernelInstructionKind.QueryToolDispatchState
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 6, TimeSpan.FromSeconds(3)));
        var qNoId = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsFalse(qNoId!.Succeeded);

        // Shutdown
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;
    }

    // -----------------------------------------------------------------------
    // P1-2: ResumeAsync 通过 IAgentContextSnapshotStore 恢复 _lastSnapshot
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ResumeAsync_WithSnapshotStore_RestoresLastSnapshot()
    {
        var snapshot = new AgentContextSnapshot
        {
            SnapshotId = "snap-resume-1",
            Session = new AgentSessionId
            {
                Value = "session-snap",
                WorkspaceId = "ws-snap",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            TokenBudget = 1000,
            ActualTokens = 500
        };
        var snapshotStore = new InMemoryAgentContextSnapshotStore();
        await snapshotStore.SaveAsync("ws-snap", snapshot, CancellationToken.None);

        // 构造一个含 SnapshotId 的 checkpoint（StateJson 可为空对象）
        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = "ckpt-snap-resume",
            Session = new AgentSessionId
            {
                Value = "session-snap",
                WorkspaceId = "ws-snap",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = snapshot.SnapshotId,
            StateJson = "{}"
        };

        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var dispatcher = new EchoToolDispatcher();
        var kernel = new DefaultAgentKernel(
            transport, dispatcher, checkpointStore,
            snapshotStore: snapshotStore);
        var testCt = CreateTestTimeout();

        // 恢复
        await kernel.ResumeAsync(checkpoint, CancellationToken.None);

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 发送 Checkpoint 指令，验证 LastSnapshotId 已恢复
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-after-resume",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-snap",
                ["workspaceId"] = "ws-snap"
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
        Assert.IsTrue(result!.Succeeded);
        Assert.AreEqual(snapshot.SnapshotId, result.LastSnapshotId,
            "ResumeAsync 应通过 snapshotStore 恢复 _lastSnapshot。");

        // 验证 checkpoint 也含 SnapshotId
        var savedCp = await checkpointStore.GetAsync("ws-snap", "ckpt-after-resume", testCt.Token);
        Assert.IsNotNull(savedCp);
        Assert.AreEqual(snapshot.SnapshotId, savedCp!.SnapshotId);
    }

    [TestMethod]
    public async Task ResumeAsync_WithoutSnapshotStore_LeavesLastSnapshotNull()
    {
        // 未注入 snapshotStore 时，即使 checkpoint 含 SnapshotId，_lastSnapshot 也保持 null
        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = "ckpt-no-store",
            Session = new AgentSessionId
            {
                Value = "session-no-store",
                WorkspaceId = "ws-no-store",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = "snap-should-not-load",
            StateJson = "{}"
        };

        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, new EchoToolDispatcher(), checkpointStore);
        var testCt = CreateTestTimeout();

        await kernel.ResumeAsync(checkpoint, CancellationToken.None);

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-verify",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-no-store",
                ["workspaceId"] = "ws-no-store"
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
        Assert.IsTrue(result!.Succeeded);
        Assert.IsNull(result.LastSnapshotId,
            "未注入 snapshotStore 时 _lastSnapshot 应保持 null。");
    }

    [TestMethod]
    public async Task ResumeAsync_SnapshotStoreMissingSnapshot_LeavesLastSnapshotNull()
    {
        // 注入了 store 但 snapshot 不存在时，应静默跳过（不抛异常）
        var snapshotStore = new InMemoryAgentContextSnapshotStore();
        // 不保存任何 snapshot → GetAsync 返回 null

        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = "ckpt-missing-snap",
            Session = new AgentSessionId
            {
                Value = "session-missing",
                WorkspaceId = "ws-missing",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = "snap-non-existent",
            StateJson = "{}"
        };

        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(
            transport, new EchoToolDispatcher(), checkpointStore,
            snapshotStore: snapshotStore);
        var testCt = CreateTestTimeout();

        // ResumeAsync 不应抛异常
        await kernel.ResumeAsync(checkpoint, CancellationToken.None);

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ckpt-verify-2",
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-missing",
                ["workspaceId"] = "ws-missing"
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
        Assert.IsTrue(result!.Succeeded);
        Assert.IsNull(result.LastSnapshotId,
            "snapshot 不存在时 _lastSnapshot 应保持 null（不抛异常）。");
    }

    // -----------------------------------------------------------------------
    // P1-4: IToolDispatchJournal 集成 — Kernel 推进 journal 状态机
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Kernel_WithDispatchJournal_AdvancesStateMachine()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var journal = new InMemoryToolDispatchJournal();
        var kernel = new DefaultAgentKernel(
            transport, dispatcher, checkpointStore,
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-journal-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "j-1",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-j",
                ["workspaceId"] = "ws-j",
                ["idempotencyKey"] = "idem-key-1"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // 验证 journal 状态：Write 副作用 → Committed → ResultDelivered（结果已发送）
        var entry = await journal.GetEntryAsync("exec-journal-1");
        Assert.IsNotNull(entry);
        Assert.AreEqual(ToolDispatchState.ResultDelivered, entry!.State,
            "Write 副作用 tool 完成且结果发送后 journal 应推进到 ResultDelivered。");
        Assert.AreEqual("idem-key-1", entry.IdempotencyKey,
            "journal 应保留 idempotencyKey。");
        Assert.AreEqual("echo", entry.ToolName);

        // Query 验证 journal 路径优先于进程内字典
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "query-journal-1",
            Kind = AgentKernelInstructionKind.QueryToolDispatchState,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-journal-1" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        var q = await transport.ReceiveResultAsync(testCt.Token);
        Assert.IsTrue(q!.Succeeded);
        Assert.AreEqual(ToolDispatchState.ResultDelivered, q.DispatchState,
            "Query 应从 journal 读取 ResultDelivered 状态。");

        // Shutdown
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;
    }

    [TestMethod]
    public async Task Kernel_WithDispatchJournal_UnknownSideEffectStopsAtDispatched()
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Unknown };
        var transport = new InProcessTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var journal = new InMemoryToolDispatchJournal();
        var kernel = new DefaultAgentKernel(
            transport, dispatcher, checkpointStore,
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-journal-unknown",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "u-1",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-ju",
                ["workspaceId"] = "ws-ju"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        // 验证 journal 状态：Unknown 副作用 → Dispatched（未 Committed）
        var entry = await journal.GetEntryAsync("exec-journal-unknown");
        Assert.IsNotNull(entry);
        Assert.AreEqual(ToolDispatchState.Dispatched, entry!.State,
            "Unknown 副作用 tool 完成后 journal 应停留在 Dispatched（未 Ack）。");

        // Ack 后 → Committed
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "ack-journal",
            Kind = AgentKernelInstructionKind.AcknowledgeToolResult,
            Metadata = new Dictionary<string, string> { ["requestId"] = "exec-journal-unknown" }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 2, TimeSpan.FromSeconds(3)));
        await transport.ReceiveResultAsync(testCt.Token);

        var entryAfterAck = await journal.GetEntryAsync("exec-journal-unknown");
        Assert.IsNotNull(entryAfterAck);
        Assert.AreEqual(ToolDispatchState.Committed, entryAfterAck!.State,
            "Ack 后 journal 应推进到 Committed。");

        // Shutdown
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask;
    }
}

// ===========================================================================
// 测试 Stub：InMemoryAgentContextSnapshotStore（P1-2 测试用）
// ===========================================================================

/// <summary>
/// 进程内 AgentContextSnapshotStore 测试 Stub。
/// 按 (workspaceId, snapshotId) 存储Snapshot；跨 workspace 不可见。
/// </summary>
internal sealed class InMemoryAgentContextSnapshotStore : IAgentContextSnapshotStore
{
    private readonly Dictionary<(string workspaceId, string snapshotId), AgentContextSnapshot> _store = new();

    public ValueTask SaveAsync(string workspaceId, AgentContextSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId 不能为空。", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotId))
            throw new ArgumentException("SnapshotId 不能为空。", nameof(snapshot));

        _store[(workspaceId, snapshot.SnapshotId)] = snapshot;
        return ValueTask.CompletedTask;
    }

    public ValueTask<AgentContextSnapshot?> GetAsync(
        string workspaceId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(snapshotId))
            return ValueTask.FromResult<AgentContextSnapshot?>(null);

        _store.TryGetValue((workspaceId, snapshotId), out var snapshot);
        return ValueTask.FromResult(snapshot);
    }
}
