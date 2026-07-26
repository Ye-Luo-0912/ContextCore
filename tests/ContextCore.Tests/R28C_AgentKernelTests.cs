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

        // 两者 StateJson 都应含 CommittedResults 数组（shape 一致）
        Assert.IsNotNull(manualCp!.StateJson);
        Assert.IsNotNull(autoCp!.StateJson);
        using var manualDoc = JsonDocument.Parse(manualCp.StateJson!);
        using var autoDoc = JsonDocument.Parse(autoCp.StateJson!);
        Assert.IsTrue(manualDoc.RootElement.TryGetProperty("CommittedResults", out var manualArr));
        Assert.IsTrue(autoDoc.RootElement.TryGetProperty("CommittedResults", out var autoArr));

        // R28-G P1-5：Manual checkpoint（首次）为 Full 模式 → 含全部 1 条 committed result。
        Assert.IsTrue(manualDoc.RootElement.TryGetProperty("Mode", out var manualModeProp));
        Assert.AreEqual((int)DefaultAgentCheckpointFactory.CheckpointMode.Full, manualModeProp.GetInt32());
        Assert.AreEqual(1, manualArr.GetArrayLength());

        // R28-G P1-5：AutoCheckpoint（取消触发，在 Manual 之后）为 Delta 模式：
        //   - Manual 已推进 cursor 至 Sequence=1
        //   - Auto 与 Manual 之间无新 tool 提交 → Delta CommittedResults 为空
        //   - BaseCheckpointId 应链接 Manual checkpoint
        //   - LastSequence 应等于 Manual 的 LastSequence（无新增）
        // 这是 delta checkpoint 的预期行为：避免每次 auto 都重复序列化全部历史。
        Assert.IsTrue(autoDoc.RootElement.TryGetProperty("Mode", out var autoModeProp));
        Assert.AreEqual((int)DefaultAgentCheckpointFactory.CheckpointMode.Delta, autoModeProp.GetInt32());
        Assert.AreEqual(0, autoArr.GetArrayLength());
        Assert.IsTrue(autoDoc.RootElement.TryGetProperty("BaseCheckpointId", out var baseProp));
        Assert.AreEqual("ckpt-shared-manual", baseProp.GetString());
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
// R28-G P1-5：Agent Checkpoint 容量上限 + Delta Checkpoint 验收测试
//
// 覆盖：
//   1. FIFO 容量淘汰：超过 maxCommittedResults 时最旧条目被移除。
//   2. 首次 Checkpoint 走 Full 模式（Mode=Full, BaseCheckpointId=null）。
//   3. 后续 Checkpoint 走 Delta 模式（Mode=Delta, BaseCheckpointId=上次 checkpointId）。
//   4. Delta checkpoint StateJson 仅包含新增条目（小于全量）。
//   5. ResumeAsync 从 Delta 递归加载 BaseCheckpoint，重建完整状态。
// ===========================================================================
[TestClass]
[TestCategory("R28-G")]
public sealed class R28G_AgentKernelCheckpointTests
{
    private static CancellationTokenSource CreateTestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10));

    private static bool WaitForProcessed(DefaultAgentKernel kernel, int target, TimeSpan timeout)
        => SpinWait.SpinUntil(() => kernel.GetStatus().ProcessedCount >= target, timeout);

    private static async Task<(DefaultAgentKernel kernel, InProcessTransport transport, InMemoryAgentCheckpointStore store, Task runTask)> StartKernelAsync(
        int? maxCommittedResults = null, CancellationToken cancellationToken = default)
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var store = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, store, maxCommittedResults: maxCommittedResults);
        var runTask = Task.Run(() => kernel.RunAsync(cancellationToken).AsTask(), cancellationToken);
        // 等待 kernel 进入 Running 状态
        SpinWait.SpinUntil(() => kernel.GetStatus().State == AgentKernelState.Running, TimeSpan.FromSeconds(2));
        return (kernel, transport, store, runTask);
    }

    private static async Task SubmitAndWaitAsync(DefaultAgentKernel kernel, InProcessTransport transport, string instructionId, string payload, CancellationToken ct)
    {
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = instructionId,
            Kind = AgentKernelInstructionKind.Execute,
            Payload = payload,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p1-5",
                ["workspaceId"] = "ws-p1-5",
                ["tool"] = "echo"
            }
        }, ct);
        Assert.IsTrue(WaitForProcessed(kernel, kernel.GetStatus().ProcessedCount + 1, TimeSpan.FromSeconds(3)),
            $"Kernel 未在超时内处理 instruction {instructionId}。");
        await transport.ReceiveResultAsync(ct);
    }

    private static async Task<string> SubmitCheckpointAsync(DefaultAgentKernel kernel, InProcessTransport transport, string checkpointId, CancellationToken ct)
    {
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = checkpointId,
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p1-5",
                ["workspaceId"] = "ws-p1-5"
            }
        }, ct);
        Assert.IsTrue(WaitForProcessed(kernel, kernel.GetStatus().ProcessedCount + 1, TimeSpan.FromSeconds(3)));
        var result = await transport.ReceiveResultAsync(ct);
        return result?.Output ?? string.Empty;
    }

    // -----------------------------------------------------------------------
    // P1-5.1: FIFO 容量淘汰
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task FIFO_Eviction_OldestRemovedWhenCapacityExceeded()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, _, runTask) = await StartKernelAsync(maxCommittedResults: 3, testCt.Token);

        try
        {
            // 提交 4 个 tool 调用（容量 3），第 1 个应被淘汰
            await SubmitAndWaitAsync(kernel, transport, "exec-1", "p1", testCt.Token);
            await SubmitAndWaitAsync(kernel, transport, "exec-2", "p2", testCt.Token);
            await SubmitAndWaitAsync(kernel, transport, "exec-3", "p3", testCt.Token);
            await SubmitAndWaitAsync(kernel, transport, "exec-4", "p4", testCt.Token);

            // 再次提交 exec-1：因已被淘汰，应重新执行（DispatchCount=4 -> 5）
            // 若未被淘汰，exec-1 走缓存路径，DispatchCount 不变
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "exec-1",
                Kind = AgentKernelInstructionKind.Execute,
                Payload = "p1-replay",
                Metadata = new Dictionary<string, string>
                {
                    ["sessionId"] = "session-p1-5",
                    ["workspaceId"] = "ws-p1-5",
                    ["tool"] = "echo"
                }
            }, testCt.Token);
            Assert.IsTrue(WaitForProcessed(kernel, 5, TimeSpan.FromSeconds(3)));
            await transport.ReceiveResultAsync(testCt.Token);

            Assert.AreEqual(5, ((CountingToolDispatcher)typeof(DefaultAgentKernel)
                .GetField("_toolDispatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(kernel)!).DispatchCount,
                "exec-1 应被 FIFO 淘汰后重新执行（DispatchCount 从 4 增至 5）。");
        }
        finally
        {
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown",
                Kind = AgentKernelInstructionKind.Shutdown
            }, testCt.Token);
            await runTask;
        }
    }

    // -----------------------------------------------------------------------
    // P1-5.2: 首次 Checkpoint 走 Full 模式
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task FirstCheckpoint_IsFullMode_NoBaseCheckpointId()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(cancellationToken: testCt.Token);

        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-full-1", "p1", testCt.Token);
            await SubmitAndWaitAsync(kernel, transport, "exec-full-2", "p2", testCt.Token);

            var checkpointId = await SubmitCheckpointAsync(kernel, transport, "cp-1", testCt.Token);
            Assert.IsFalse(string.IsNullOrEmpty(checkpointId), "Checkpoint ID 不应为空。");

            var checkpoint = await store.GetAsync("ws-p1-5", checkpointId, testCt.Token);
            Assert.IsNotNull(checkpoint, "Checkpoint 应已持久化。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(checkpoint!.StateJson));

            var state = System.Text.Json.JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(checkpoint.StateJson);
            Assert.IsNotNull(state);
            Assert.AreEqual(DefaultAgentCheckpointFactory.CheckpointMode.Full, state!.Mode,
                "首次 checkpoint 应为 Full 模式。");
            Assert.IsNull(state.BaseCheckpointId, "Full 模式 BaseCheckpointId 应为 null。");
            Assert.AreEqual(2, state.CommittedResults.Count,
                "Full 模式应包含全部 2 条 committed results。");
            Assert.IsTrue(state.LastSequence > 0, "LastSequence 应大于 0。");
        }
        finally
        {
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown",
                Kind = AgentKernelInstructionKind.Shutdown
            }, testCt.Token);
            await runTask;
        }
    }

    // -----------------------------------------------------------------------
    // P1-5.3: 后续 Checkpoint 走 Delta 模式
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task SecondCheckpoint_IsDeltaMode_ContainsOnlyNewEntries()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(cancellationToken: testCt.Token);

        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-delta-1", "p1", testCt.Token);
            var firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-delta-1", testCt.Token);

            // 新增 1 条 tool 调用
            await SubmitAndWaitAsync(kernel, transport, "exec-delta-2", "p2", testCt.Token);
            var secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-delta-2", testCt.Token);

            var secondCp = await store.GetAsync("ws-p1-5", secondCpId, testCt.Token);
            Assert.IsNotNull(secondCp);
            var state = System.Text.Json.JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(secondCp!.StateJson);

            Assert.IsNotNull(state);
            Assert.AreEqual(DefaultAgentCheckpointFactory.CheckpointMode.Delta, state!.Mode,
                "第二次 checkpoint 应为 Delta 模式。");
            Assert.AreEqual(firstCpId, state.BaseCheckpointId,
                "Delta 模式 BaseCheckpointId 应链接到首次 checkpoint。");
            Assert.AreEqual(1, state.CommittedResults.Count,
                "Delta 模式应仅包含新增条目（exec-delta-2）。");
            Assert.AreEqual("exec-delta-2", state.CommittedResults[0].RequestId);
        }
        finally
        {
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown",
                Kind = AgentKernelInstructionKind.Shutdown
            }, testCt.Token);
            await runTask;
        }
    }

    // -----------------------------------------------------------------------
    // P1-5.4: ResumeAsync 从 Delta 重建完整状态
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ResumeAsync_FromDelta_RebuildsFullStateViaBaseCheckpoint()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(cancellationToken: testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-resume-1", "p1", testCt.Token);
            await SubmitAndWaitAsync(kernel, transport, "exec-resume-2", "p2", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-resume-1", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-resume-3", "p3", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-resume-2", testCt.Token);

            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown",
                Kind = AgentKernelInstructionKind.Shutdown
            }, testCt.Token);
            await runTask;
        }
        finally
        {
            if (kernel.GetStatus().State != AgentKernelState.Stopped)
            {
                await kernel.SubmitAsync(new AgentKernelInstruction
                {
                    InstructionId = "shutdown",
                    Kind = AgentKernelInstructionKind.Shutdown
                }, testCt.Token);
                await runTask;
            }
        }

        // 新 kernel，从 delta checkpoint resume
        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);
        var deltaCheckpoint = await store.GetAsync("ws-p1-5", secondCpId, testCt.Token);
        Assert.IsNotNull(deltaCheckpoint);

        await kernel2.ResumeAsync(deltaCheckpoint!, testCt.Token);

        // 验证：exec-resume-1/2/3 都应走缓存（DispatchCount=0）
        // 若 Delta resume 失败（仅加载 delta 条目），exec-resume-1/2 会被重新执行
        var runTask2 = Task.Run(() => kernel2.RunAsync(testCt.Token).AsTask(), testCt.Token);
        SpinWait.SpinUntil(() => kernel2.GetStatus().State == AgentKernelState.Running, TimeSpan.FromSeconds(2));

        await kernel2.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-resume-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "p1-replay",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p1-5",
                ["workspaceId"] = "ws-p1-5",
                ["tool"] = "echo"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel2, 1, TimeSpan.FromSeconds(3)));
        var r1 = await transport2.ReceiveResultAsync(testCt.Token);
        Assert.AreEqual("p1", r1?.Output,
            "Delta resume 应通过 BaseCheckpoint 重建 exec-resume-1 的缓存结果。");

        await kernel2.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-resume-3",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "p3-replay",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p1-5",
                ["workspaceId"] = "ws-p1-5",
                ["tool"] = "echo"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel2, 2, TimeSpan.FromSeconds(3)));
        var r3 = await transport2.ReceiveResultAsync(testCt.Token);
        Assert.AreEqual("p3", r3?.Output,
            "Delta 条目（exec-resume-3）应通过 delta resume 重建缓存。");

        Assert.AreEqual(0, dispatcher2.DispatchCount,
            "所有 tool 调用应走缓存，DispatchCount 应保持 0。");

        await kernel2.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);
        await runTask2;
    }
}

// ===========================================================================
// P0-5：Checkpoint Delta Chain 完整性治理测试
//
// 覆盖：
//   1. ContentHash 计算 + 校验（Full/Delta 模式均生成 ContentHash）。
//   2. PrevChainHash 链接（Delta 的 PrevChainHash == base 的 ContentHash）。
//   3. ChainSessionId 绑定（防跨 session 链接）。
//   4. BaseLastSequence 校验（base 匹配）。
//   5. Delta 条目 Sequence > base.LastSequence（无重叠/回退）。
//   6. 链深度限制（MaxCheckpointChainDepth）。
//   7. 向后兼容：旧 checkpoint（无 ContentHash）跳过校验。
//   8. 篡改检测：修改 StateJson 内容后 ContentHash 校验失败。
// ===========================================================================
[TestClass]
[TestCategory("P0-5")]
[TestCategory("R28-G")]
public sealed class P0_5_CheckpointChainIntegrityTests
{
    private static CancellationTokenSource CreateTestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10));

    private static bool WaitForProcessed(DefaultAgentKernel kernel, int target, TimeSpan timeout)
        => SpinWait.SpinUntil(() => kernel.GetStatus().ProcessedCount >= target, timeout);

    private static async Task<(DefaultAgentKernel kernel, InProcessTransport transport, InMemoryAgentCheckpointStore store, Task runTask)> StartKernelAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var store = new InMemoryAgentCheckpointStore();
        var kernel = new DefaultAgentKernel(transport, dispatcher, store);
        var runTask = Task.Run(() => kernel.RunAsync(cancellationToken).AsTask(), cancellationToken);
        SpinWait.SpinUntil(() => kernel.GetStatus().State == AgentKernelState.Running, TimeSpan.FromSeconds(2));
        return (kernel, transport, store, runTask);
    }

    private static async Task SubmitAndWaitAsync(DefaultAgentKernel kernel, InProcessTransport transport, string instructionId, string payload, CancellationToken ct)
    {
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = instructionId,
            Kind = AgentKernelInstructionKind.Execute,
            Payload = payload,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p0-5",
                ["workspaceId"] = "ws-p0-5",
                ["tool"] = "echo"
            }
        }, ct);
        Assert.IsTrue(WaitForProcessed(kernel, kernel.GetStatus().ProcessedCount + 1, TimeSpan.FromSeconds(3)),
            $"Kernel 未在超时内处理 instruction {instructionId}。");
        await transport.ReceiveResultAsync(ct);
    }

    private static async Task<string> SubmitCheckpointAsync(DefaultAgentKernel kernel, InProcessTransport transport, string checkpointId, CancellationToken ct)
    {
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = checkpointId,
            Kind = AgentKernelInstructionKind.Checkpoint,
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p0-5",
                ["workspaceId"] = "ws-p0-5"
            }
        }, ct);
        Assert.IsTrue(WaitForProcessed(kernel, kernel.GetStatus().ProcessedCount + 1, TimeSpan.FromSeconds(3)));
        var result = await transport.ReceiveResultAsync(ct);
        return result?.Output ?? string.Empty;
    }

    private static async Task ShutdownKernelAsync(DefaultAgentKernel kernel, Task runTask, CancellationToken ct)
    {
        if (kernel.GetStatus().State != AgentKernelState.Stopped)
        {
            await kernel.SubmitAsync(new AgentKernelInstruction
            {
                InstructionId = "shutdown",
                Kind = AgentKernelInstructionKind.Shutdown
            }, ct);
            await runTask;
        }
    }

    // -----------------------------------------------------------------------
    // P0-5.1: ContentHash 生成与校验（单元测试）
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ComputeContentHash_Deterministic_SameInputSameHash()
    {
        var json = """{"SnapshotId":"s1","Mode":0,"LastSequence":1}""";
        var hash1 = DefaultAgentCheckpointFactory.ComputeContentHash(json);
        var hash2 = DefaultAgentCheckpointFactory.ComputeContentHash(json);

        Assert.AreEqual(64, hash1.Length, "SHA-256 hex 应为 64 字符。");
        Assert.AreEqual(hash1, hash2, "相同输入应产出相同哈希。");
    }

    [TestMethod]
    public void ComputeContentHash_DifferentInput_DifferentHash()
    {
        var json1 = """{"SnapshotId":"s1","Mode":0,"LastSequence":1}""";
        var json2 = """{"SnapshotId":"s2","Mode":0,"LastSequence":1}""";

        var hash1 = DefaultAgentCheckpointFactory.ComputeContentHash(json1);
        var hash2 = DefaultAgentCheckpointFactory.ComputeContentHash(json2);

        Assert.AreNotEqual(hash1, hash2, "不同输入应产出不同哈希。");
    }

    [TestMethod]
    public void VerifyContentHash_LegacyCheckpoint_NoContentHash_ReturnsTrue()
    {
        // 旧 checkpoint（P0-5 之前）无 ContentHash 字段 → 跳过校验
        var legacyJson = """{"SnapshotId":"s1","Mode":0,"LastSequence":1,"CommittedResults":[],"PendingResults":[]}""";

        Assert.IsTrue(DefaultAgentCheckpointFactory.VerifyContentHash(legacyJson),
            "旧 checkpoint 无 ContentHash 应跳过校验返回 true。");
    }

    [TestMethod]
    public void VerifyContentHash_ValidCheckpoint_ReturnsTrue()
    {
        // 通过工厂构建真实 checkpoint → ContentHash 应校验通过
        var accessor = new DefaultAgentCheckpointFactory.KernelStateAccessor(
            getLastSnapshotId: () => "snap-1",
            getCommittedResults: () => new Dictionary<string, ToolDispatchResult>(),
            getCommittedResultSequences: () => new Dictionary<string, long>(),
            getPendingResults: () => new Dictionary<string, ToolDispatchResult>(),
            getLastCheckpointSequence: () => 0,
            getLastCheckpointId: () => null,
            getLastCheckpointContentHash: () => null);
        var factory = new DefaultAgentCheckpointFactory(accessor);

        var checkpoint = factory.CreateCheckpointAsync("ckpt-1", "session-1", "ws-1").Result;

        Assert.IsTrue(DefaultAgentCheckpointFactory.VerifyContentHash(checkpoint.StateJson),
            "工厂产出的 checkpoint ContentHash 应校验通过。");
    }

    [TestMethod]
    public void VerifyContentHash_TamperedStateJson_ReturnsFalse()
    {
        var accessor = new DefaultAgentCheckpointFactory.KernelStateAccessor(
            getLastSnapshotId: () => "snap-1",
            getCommittedResults: () => new Dictionary<string, ToolDispatchResult>(),
            getCommittedResultSequences: () => new Dictionary<string, long>(),
            getPendingResults: () => new Dictionary<string, ToolDispatchResult>(),
            getLastCheckpointSequence: () => 0,
            getLastCheckpointId: () => null,
            getLastCheckpointContentHash: () => null);
        var factory = new DefaultAgentCheckpointFactory(accessor);

        var checkpoint = factory.CreateCheckpointAsync("ckpt-1", "session-1", "ws-1").Result;

        // 篡改 StateJson：修改 SnapshotId 但保留旧 ContentHash
        var tamperedJson = checkpoint.StateJson.Replace("snap-1", "snap-tampered");

        Assert.IsFalse(DefaultAgentCheckpointFactory.VerifyContentHash(tamperedJson),
            "篡改后的 StateJson ContentHash 校验应失败。");
    }

    // -----------------------------------------------------------------------
    // P0-5.2: Full/Delta 模式 ContentHash + PrevChainHash 生成
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task FullCheckpoint_HasContentHash_NoPrevChainHash()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string cpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-1", "p1", testCt.Token);
            cpId = await SubmitCheckpointAsync(kernel, transport, "cp-full-1", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var cp = await store.GetAsync("ws-p0-5", cpId, testCt.Token);
        Assert.IsNotNull(cp);
        var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(cp!.StateJson);

        Assert.AreEqual(DefaultAgentCheckpointFactory.CheckpointMode.Full, state!.Mode);
        Assert.IsFalse(string.IsNullOrEmpty(state.ContentHash), "Full checkpoint 应有 ContentHash。");
        Assert.IsNull(state.PrevChainHash, "Full checkpoint 无前驱 → PrevChainHash 应为 null。");
        Assert.IsFalse(string.IsNullOrEmpty(state.ChainSessionId), "Full checkpoint 应有 ChainSessionId。");
        Assert.AreEqual(0, state.BaseLastSequence, "Full checkpoint BaseLastSequence 应为 0。");
    }

    [TestMethod]
    public async Task DeltaCheckpoint_HasContentHash_PrevChainHash_MatchesBaseContentHash()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-1", "p1", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-full-1", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-2", "p2", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-delta-1", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var fullCp = await store.GetAsync("ws-p0-5", firstCpId, testCt.Token);
        var deltaCp = await store.GetAsync("ws-p0-5", secondCpId, testCt.Token);
        Assert.IsNotNull(fullCp);
        Assert.IsNotNull(deltaCp);

        var fullState = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(fullCp!.StateJson)!;
        var deltaState = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(deltaCp!.StateJson)!;

        Assert.AreEqual(DefaultAgentCheckpointFactory.CheckpointMode.Delta, deltaState.Mode);
        Assert.IsFalse(string.IsNullOrEmpty(deltaState.ContentHash), "Delta checkpoint 应有 ContentHash。");
        Assert.IsFalse(string.IsNullOrEmpty(deltaState.PrevChainHash), "Delta checkpoint 应有 PrevChainHash。");
        Assert.AreEqual(fullState.ContentHash, deltaState.PrevChainHash,
            "Delta 的 PrevChainHash 必须等于 base 的 ContentHash。");
        Assert.AreEqual(fullState.LastSequence, deltaState.BaseLastSequence,
            "Delta 的 BaseLastSequence 必须等于 base 的 LastSequence。");
        Assert.AreEqual(fullState.ChainSessionId, deltaState.ChainSessionId,
            "Delta 与 base 的 ChainSessionId 必须一致。");
    }

    // -----------------------------------------------------------------------
    // P0-5.3: ResumeAsync 完整性校验
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task ResumeAsync_ValidDeltaChain_SucceedsAndRebuildsState()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-valid-1", "p1", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-valid-full", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-valid-2", "p2", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-valid-delta", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        // 新 kernel 从 delta checkpoint resume（含完整性校验）
        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);
        var deltaCp = await store.GetAsync("ws-p0-5", secondCpId, testCt.Token);

        Assert.IsNotNull(deltaCp);
        await kernel2.ResumeAsync(deltaCp!, testCt.Token); // 不抛异常 = 校验通过

        // 验证状态重建：exec-valid-1 应走缓存
        var runTask2 = Task.Run(() => kernel2.RunAsync(testCt.Token).AsTask(), testCt.Token);
        SpinWait.SpinUntil(() => kernel2.GetStatus().State == AgentKernelState.Running, TimeSpan.FromSeconds(2));

        await kernel2.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "exec-valid-1",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "p1-replay",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-p0-5",
                ["workspaceId"] = "ws-p0-5",
                ["tool"] = "echo"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel2, 1, TimeSpan.FromSeconds(3)));
        var r = await transport2.ReceiveResultAsync(testCt.Token);
        Assert.AreEqual("p1", r?.Output, "Valid delta chain resume 应重建缓存。");
        Assert.AreEqual(0, dispatcher2.DispatchCount, "应走缓存，DispatchCount=0。");

        await ShutdownKernelAsync(kernel2, runTask2, testCt.Token);
    }

    [TestMethod]
    public async Task ResumeAsync_TamperedContentHash_ThrowsInvalidOperationException()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string cpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-tamper-1", "p1", testCt.Token);
            cpId = await SubmitCheckpointAsync(kernel, transport, "cp-tamper", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var cp = await store.GetAsync("ws-p0-5", cpId, testCt.Token);
        Assert.IsNotNull(cp);

        // 篡改 StateJson：修改内容但保留旧 ContentHash
        var tamperedJson = cp!.StateJson.Replace("p1", "p1-tampered");
        var tamperedCp = cp with { StateJson = tamperedJson };

        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await kernel2.ResumeAsync(tamperedCp, testCt.Token),
            "篡改 StateJson 后 ContentHash 校验应失败并抛出 InvalidOperationException。");
    }

    [TestMethod]
    public async Task ResumeAsync_PrevChainHashMismatch_ThrowsInvalidOperationException()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-prev-1", "p1", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-prev-full", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-prev-2", "p2", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-prev-delta", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var deltaCp = await store.GetAsync("ws-p0-5", secondCpId, testCt.Token);
        Assert.IsNotNull(deltaCp);

        // 篡改 delta 的 PrevChainHash 为错误值
        var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(deltaCp!.StateJson)!;
        var tamperedState = new DefaultAgentCheckpointFactory.KernelCheckpointStateDto
        {
            SnapshotId = state.SnapshotId,
            Mode = state.Mode,
            BaseCheckpointId = state.BaseCheckpointId,
            LastSequence = state.LastSequence,
            CommittedResults = state.CommittedResults,
            PendingResults = state.PendingResults,
            BaseLastSequence = state.BaseLastSequence,
            PrevChainHash = "deadbeef0000000000000000000000000000000000000000000000000000ffff", // 错误的 PrevChainHash
            ChainSessionId = state.ChainSessionId,
            ContentHash = state.ContentHash // 保留旧 ContentHash（但因为 PrevChainHash 变了，实际 ContentHash 也不匹配）
        };
        var tamperedJson = JsonSerializer.Serialize(tamperedState);
        var tamperedCp = deltaCp with { StateJson = tamperedJson };

        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await kernel2.ResumeAsync(tamperedCp, testCt.Token),
            "PrevChainHash 与 base ContentHash 不匹配应抛出 InvalidOperationException。");
    }

    [TestMethod]
    public async Task ResumeAsync_CrossSessionChain_ThrowsInvalidOperationException()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-xsession-1", "p1", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-xsession-full", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-xsession-2", "p2", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-xsession-delta", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var deltaCp = await store.GetAsync("ws-p0-5", secondCpId, testCt.Token);
        Assert.IsNotNull(deltaCp);

        // 篡改 delta 的 ChainSessionId 为不同 session
        var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(deltaCp!.StateJson)!;
        var tamperedState = new DefaultAgentCheckpointFactory.KernelCheckpointStateDto
        {
            SnapshotId = state.SnapshotId,
            Mode = state.Mode,
            BaseCheckpointId = state.BaseCheckpointId,
            LastSequence = state.LastSequence,
            CommittedResults = state.CommittedResults,
            PendingResults = state.PendingResults,
            BaseLastSequence = state.BaseLastSequence,
            PrevChainHash = state.PrevChainHash,
            ChainSessionId = "different-session-id", // 错误的 session
            ContentHash = null // 设为 null 跳过 ContentHash 校验，专注于 ChainSessionId 校验
        };
        var tamperedJson = JsonSerializer.Serialize(tamperedState);
        var tamperedCp = deltaCp with { StateJson = tamperedJson };

        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await kernel2.ResumeAsync(tamperedCp, testCt.Token),
            "跨 session 链接应抛出 InvalidOperationException。");
    }

    [TestMethod]
    public async Task ResumeAsync_BaseLastSequenceMismatch_ThrowsInvalidOperationException()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-basels-1", "p1", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-basels-full", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-basels-2", "p2", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-basels-delta", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var deltaCp = await store.GetAsync("ws-p0-5", secondCpId, testCt.Token);
        Assert.IsNotNull(deltaCp);

        // 篡改 delta 的 BaseLastSequence 为错误值
        var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(deltaCp!.StateJson)!;
        var tamperedState = new DefaultAgentCheckpointFactory.KernelCheckpointStateDto
        {
            SnapshotId = state.SnapshotId,
            Mode = state.Mode,
            BaseCheckpointId = state.BaseCheckpointId,
            LastSequence = state.LastSequence,
            CommittedResults = state.CommittedResults,
            PendingResults = state.PendingResults,
            BaseLastSequence = state.BaseLastSequence + 999, // 错误的 BaseLastSequence
            PrevChainHash = state.PrevChainHash,
            ChainSessionId = state.ChainSessionId,
            ContentHash = null // 跳过 ContentHash 校验，专注于 BaseLastSequence 校验
        };
        var tamperedJson = JsonSerializer.Serialize(tamperedState);
        var tamperedCp = deltaCp with { StateJson = tamperedJson };

        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await kernel2.ResumeAsync(tamperedCp, testCt.Token),
            "BaseLastSequence 与 base.LastSequence 不匹配应抛出 InvalidOperationException。");
    }

    [TestMethod]
    public async Task ResumeAsync_DeltaEntrySequenceOverlap_ThrowsInvalidOperationException()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        string firstCpId;
        string secondCpId;
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-overlap-1", "p1", testCt.Token);
            firstCpId = await SubmitCheckpointAsync(kernel, transport, "cp-overlap-full", testCt.Token);

            await SubmitAndWaitAsync(kernel, transport, "exec-overlap-2", "p2", testCt.Token);
            secondCpId = await SubmitCheckpointAsync(kernel, transport, "cp-overlap-delta", testCt.Token);
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        var deltaCp = await store.GetAsync("ws-p0-5", secondCpId, testCt.Token);
        Assert.IsNotNull(deltaCp);

        // 篡改 delta 条目的 Sequence 为 <= base.LastSequence 的值
        var state = JsonSerializer.Deserialize<DefaultAgentCheckpointFactory.KernelCheckpointStateDto>(deltaCp!.StateJson)!;
        var tamperedResults = state.CommittedResults.Select(r => new DefaultAgentCheckpointFactory.CommittedToolResultDto
        {
            RequestId = r.RequestId,
            Succeeded = r.Succeeded,
            Result = r.Result,
            Error = r.Error,
            SideEffect = r.SideEffect,
            Sequence = 0 // 故意设为 0，<= base.LastSequence
        }).ToList();

        var tamperedState = new DefaultAgentCheckpointFactory.KernelCheckpointStateDto
        {
            SnapshotId = state.SnapshotId,
            Mode = state.Mode,
            BaseCheckpointId = state.BaseCheckpointId,
            LastSequence = state.LastSequence,
            CommittedResults = tamperedResults,
            PendingResults = state.PendingResults,
            BaseLastSequence = state.BaseLastSequence,
            PrevChainHash = state.PrevChainHash,
            ChainSessionId = state.ChainSessionId,
            ContentHash = null // 跳过 ContentHash 校验
        };
        var tamperedJson = JsonSerializer.Serialize(tamperedState);
        var tamperedCp = deltaCp with { StateJson = tamperedJson };

        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await kernel2.ResumeAsync(tamperedCp, testCt.Token),
            "Delta 条目 Sequence <= base.LastSequence 应抛出 InvalidOperationException。");
    }

    [TestMethod]
    public async Task ResumeAsync_ChainDepthExceeded_ThrowsInvalidOperationException()
    {
        var testCt = CreateTestTimeout();
        var (kernel, transport, store, runTask) = await StartKernelAsync(testCt.Token);

        // 创建超过 MaxCheckpointChainDepth 个 delta checkpoint
        var checkpointIds = new List<string>();
        try
        {
            await SubmitAndWaitAsync(kernel, transport, "exec-depth-0", "p0", testCt.Token);
            checkpointIds.Add(await SubmitCheckpointAsync(kernel, transport, "cp-depth-0", testCt.Token));

            for (int i = 1; i <= DefaultAgentKernel.MaxCheckpointChainDepth + 1; i++)
            {
                await SubmitAndWaitAsync(kernel, transport, $"exec-depth-{i}", $"p{i}", testCt.Token);
                checkpointIds.Add(await SubmitCheckpointAsync(kernel, transport, $"cp-depth-{i}", testCt.Token));
            }
        }
        finally
        {
            await ShutdownKernelAsync(kernel, runTask, testCt.Token);
        }

        // 从最深的 delta checkpoint resume — 链深度超过 MaxCheckpointChainDepth
        var lastCpId = checkpointIds[^1];
        var lastCp = await store.GetAsync("ws-p0-5", lastCpId, testCt.Token);
        Assert.IsNotNull(lastCp);

        var dispatcher2 = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport2 = new InProcessTransport();
        var kernel2 = new DefaultAgentKernel(transport2, dispatcher2, store);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            async () => await kernel2.ResumeAsync(lastCp!, testCt.Token),
            $"链深度超过 {DefaultAgentKernel.MaxCheckpointChainDepth} 应抛出 InvalidOperationException。");
    }

    [TestMethod]
    public async Task ResumeAsync_LegacyCheckpoint_NoContentHash_Succeeds()
    {
        var testCt = CreateTestTimeout();
        var store = new InMemoryAgentCheckpointStore();

        // 构造旧格式 checkpoint（无 ContentHash/PrevChainHash/ChainSessionId/BaseLastSequence）
        var legacyStateJson = """{"SnapshotId":"snap-legacy","Mode":0,"BaseCheckpointId":null,"LastSequence":1,"CommittedResults":[{"RequestId":"r-legacy","Succeeded":true,"Result":"legacy-payload","Error":null,"SideEffect":0,"Sequence":1}],"PendingResults":[]}""";
        var legacyCheckpoint = new AgentCheckpoint
        {
            CheckpointId = "cp-legacy",
            Session = new AgentSessionId
            {
                Value = "session-legacy",
                WorkspaceId = "ws-legacy",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            SnapshotId = "snap-legacy",
            StateJson = legacyStateJson
        };
        await store.SaveAsync(legacyCheckpoint);

        // 新 kernel resume 旧 checkpoint — 应跳过 ContentHash 校验（向后兼容）
        var dispatcher = new CountingToolDispatcher { SideEffect = ToolSideEffect.Write };
        var transport = new InProcessTransport();
        var kernel = new DefaultAgentKernel(transport, dispatcher, store);

        await kernel.ResumeAsync(legacyCheckpoint, testCt.Token); // 不抛异常 = 向后兼容

        // 验证旧 checkpoint 的 committed result 已恢复
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);
        SpinWait.SpinUntil(() => kernel.GetStatus().State == AgentKernelState.Running, TimeSpan.FromSeconds(2));

        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "r-legacy",
            Kind = AgentKernelInstructionKind.Execute,
            Payload = "replay",
            Metadata = new Dictionary<string, string>
            {
                ["sessionId"] = "session-legacy",
                ["workspaceId"] = "ws-legacy",
                ["tool"] = "echo"
            }
        }, testCt.Token);
        Assert.IsTrue(WaitForProcessed(kernel, 1, TimeSpan.FromSeconds(3)));
        var r = await transport.ReceiveResultAsync(testCt.Token);
        Assert.AreEqual("legacy-payload", r?.Output, "旧 checkpoint 的 committed result 应恢复为缓存。");
        Assert.AreEqual(0, dispatcher.DispatchCount, "应走缓存，DispatchCount=0。");

        await ShutdownKernelAsync(kernel, runTask, testCt.Token);
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
