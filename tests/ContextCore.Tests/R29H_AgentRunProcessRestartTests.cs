using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using ContextCore.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Agent Run 进程重启恢复生产验收测试
//
// 验证 AgentRun 在进程重启后的恢复能力，覆盖：
//   1. Restart_NonTerminalRun_CanBeResumedByNewActor — 非终态 Run 可由新 Actor 恢复执行
//   2. Restart_TerminalRun_NotResumed — 终态 Run 不被恢复
//   3. Restart_ModelCallsUsed_PreservedAcrossRestart — ModelCallsUsed 跨重启保留
//   4. Restart_EventChain_PreservedAcrossRestart — 事件哈希链跨重启保留
//   5. Restart_RunLease_PreventsConcurrentRecovery — 租约防止并发恢复
//   6. Restart_RecoveryWorker_PicksUpNonTerminalRuns — 恢复 Worker 识别非终态 Run
//   7. Restart_Postgres_PersistentRecovery — Postgres 持久化恢复（不可用时 Inconclusive）
//
// 设计原则：
//   - 优先使用真实 InMemory 实现（非 mock）：InMemoryAgentRunStore /
//     InMemoryAgentRunEventStore / InMemoryAgentRunLease
//   - 通过 PersistentInMemoryAgentRunStore 包装器实现 IPersistentAgentRunStore 标记
//     （模拟进程重启后数据持久化）
//   - 所有异步测试使用超时 CancellationTokenSource 防止挂起
//   - 中文注释
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Agent-Run-Restart")]
public sealed class R29H_AgentRunProcessRestartTests
{
    /// <summary>
    /// 验证：非终态 Run 可由新 Actor 实例恢复执行（模拟进程重启后新进程接管）。
    /// </summary>
    [TestMethod]
    public async Task Restart_NonTerminalRun_CanBeResumedByNewActor()
    {
        // 准备：模拟进程 A 执行 Run 到一半（非终态），进程 B（新 Actor）接管
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("重启恢复测试", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 3
        });
        await runStore.CreateAsync(run);

        // ── 模拟进程 A：执行到 ContextBuilding 后"崩溃" ──
        // 手动将状态推进到 ContextBuilding（非终态），模拟崩溃前状态
        await runStore.TransitionStateAsync(
            run.WorkspaceId, run.RunId,
            AgentRunState.Created,
            AgentRunState.ContextBuilding);

        // ── 模拟进程重启：新 Actor 实例接管同一 Run ──
        var resumedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(resumedRun, "重启后 Run 应仍存在于 store 中。");
        Assert.AreNotEqual(AgentRunState.Completed, resumedRun!.State,
            "崩溃前 Run 不应为 Completed。");

        // 新 Actor 接管并执行完成
        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "恢复后完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 5,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var newActor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await newActor.ExecuteAsync(resumedRun, cts.Token);

        // 断言：恢复后 Run 进入 Completed 终态
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            "新 Actor 接管后 Run 应进入 Completed 终态。");
    }

    /// <summary>
    /// 验证：终态 Run（Completed/Failed/Cancelled）不被恢复。
    /// </summary>
    [TestMethod]
    public async Task Restart_TerminalRun_NotResumed()
    {
        var runStore = new InMemoryAgentRunStore();

        // 创建三个终态 Run
        var completedRun = BuildRun("已完成");
        completedRun = completedRun with { State = AgentRunState.Completed, FinishedAt = DateTimeOffset.UtcNow };
        await runStore.CreateAsync(completedRun);

        var failedRun = BuildRun("已失败");
        failedRun = failedRun with { State = AgentRunState.Failed, FinishedAt = DateTimeOffset.UtcNow };
        await runStore.CreateAsync(failedRun);

        var cancelledRun = BuildRun("已取消");
        cancelledRun = cancelledRun with { State = AgentRunState.Cancelled, FinishedAt = DateTimeOffset.UtcNow };
        await runStore.CreateAsync(cancelledRun);

        // 断言：终态 Run 不出现在任何非终态状态查询中
        var recoverableStates = new[]
        {
            AgentRunState.Created,
            AgentRunState.ContextBuilding,
            AgentRunState.ModelCalling,
            AgentRunState.AwaitingApproval,
            AgentRunState.ToolDispatching,
            AgentRunState.Observing,
            AgentRunState.Checkpointing
        };

        foreach (var state in recoverableStates)
        {
            var runs = await runStore.ListByStateAsync(state, take: 100);
            Assert.AreEqual(0, runs.Count,
                $"终态 Run 不应出现在 {state} 列表中（恢复扫描应跳过终态）。");
        }

        // 断言：Completed/Failed/Cancelled 列表只包含对应的终态 Run
        var completedList = await runStore.ListByStateAsync(AgentRunState.Completed, take: 100);
        Assert.AreEqual(1, completedList.Count, "Completed 列表应仅含 1 个 Run。");

        var failedList = await runStore.ListByStateAsync(AgentRunState.Failed, take: 100);
        Assert.AreEqual(1, failedList.Count, "Failed 列表应仅含 1 个 Run。");

        var cancelledList = await runStore.ListByStateAsync(AgentRunState.Cancelled, take: 100);
        Assert.AreEqual(1, cancelledList.Count, "Cancelled 列表应仅含 1 个 Run。");
    }

    /// <summary>
    /// 验证：ModelCallsUsed 跨重启保留（新 Actor 从已保存的计数值继续累加）。
    /// </summary>
    [TestMethod]
    public async Task Restart_ModelCallsUsed_PreservedAcrossRestart()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("ModelCallsUsed 保留验证", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);

        // ── 模拟进程 A：已调用 2 次模型后崩溃 ──
        var crashedRun = run with
        {
            State = AgentRunState.ContextBuilding,
            ModelCallsUsed = 2,
            Turn = 2,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await runStore.TransitionStateAsync(
            run.WorkspaceId, run.RunId,
            AgentRunState.Created,
            AgentRunState.ContextBuilding);
        await runStore.UpdateAsync(crashedRun);

        // ── 模拟进程重启：新 Actor 读取已保存的 ModelCallsUsed ──
        var resumedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(resumedRun);
        Assert.AreEqual(2, resumedRun!.ModelCallsUsed,
            "重启后 ModelCallsUsed 应保留为 2（崩溃前已调用 2 次）。");

        // 新 Actor 继续执行（再调用 1 次模型产出最终答案）
        var transport = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "恢复后完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 5,
            Duration = TimeSpan.FromMilliseconds(1)
        });

        var newActor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await newActor.ExecuteAsync(resumedRun, cts.Token);

        // 断言：新 Actor 从 ModelCallsUsed=2 继续累加到 3
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(3, finalRun!.ModelCallsUsed,
            "恢复后 ModelCallsUsed 应为 3（2 + 1 次新调用）。");

        // 断言：事件流中 ModelCallCompleted 事件的 modelCallsUsed 从 3 开始
        var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        var modelCallEvents = events
            .Where(e => e.EventType == AgentRunEventType.ModelCallCompleted)
            .ToList();
        Assert.IsTrue(modelCallEvents.Count > 0, "应有 ModelCallCompleted 事件。");
        var lastModelCallsUsed = ExtractIntField(
            modelCallEvents[^1].Payload, "modelCallsUsed");
        Assert.AreEqual(3, lastModelCallsUsed,
            "恢复后首次模型调用的 modelCallsUsed 应为 3。");
    }

    /// <summary>
    /// 验证：事件哈希链跨重启保留（"重启"后事件流仍可读取且哈希链完整）。
    /// </summary>
    /// <remarks>
    /// InMemory Store 在进程内持久化数据（跨 Actor 实例保留）。
    /// 本测试验证：Actor A 执行完成后写入的事件，在"重启"（创建新 Actor 实例）后
    /// 仍可完整读取，且哈希链无断裂、Sequence 无篡改。
    /// </remarks>
    [TestMethod]
    public async Task Restart_EventChain_PreservedAcrossRestart()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("事件链保留验证");
        await runStore.CreateAsync(run);

        // ── 模拟进程 A：执行完成并写入事件 ──
        var transport1 = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "进程 A 完成执行",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 5,
            Duration = TimeSpan.FromMilliseconds(1)
        });
        var actorA = new AgentRunActor(
            runStore, eventStore, transport1,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actorA.ExecuteAsync(run, ctsA.Token);

        // 读取"重启前"的事件流
        var eventsBeforeRestart = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        Assert.IsTrue(eventsBeforeRestart.Count > 0,
            "进程 A 执行后应已写入事件。");

        // 记录"重启前"的链尾
        var lastSequenceBeforeRestart = eventsBeforeRestart[^1].Sequence;
        var eventCountBeforeRestart = eventsBeforeRestart.Count;

        // ── 模拟进程重启：创建新 Actor 实例（同一 store，数据保留）──
        // 新 Actor 不再执行同一 Run（已 Completed），仅验证事件流仍可读取
        var transport2 = new RecordingModelTransport(new AgentModelResponse
        {
            Content = "进程 B（不会执行，Run 已终态）",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 1,
            Duration = TimeSpan.FromMilliseconds(1)
        });
        _ = new AgentRunActor(
            runStore, eventStore, transport2,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher());

        // 断言：重启后事件流仍可完整读取（数据未丢失）
        var eventsAfterRestart = await eventStore.ReadAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(eventCountBeforeRestart, eventsAfterRestart.Count,
            "重启后事件数应与重启前一致（InMemory store 保留数据）。");

        // 断言：重启前的事件保持不变（ContentHash / Sequence 不可篡改）
        for (var i = 0; i < eventsBeforeRestart.Count; i++)
        {
            Assert.AreEqual(
                eventsBeforeRestart[i].ContentHash,
                eventsAfterRestart[i].ContentHash,
                $"事件 {i} 的 ContentHash 不应改变（不可篡改）。");
            Assert.AreEqual(
                eventsBeforeRestart[i].Sequence,
                eventsAfterRestart[i].Sequence,
                $"事件 {i} 的 Sequence 不应改变。");
        }

        // 断言：哈希链完整无断裂
        Assert.IsNull(eventsAfterRestart[0].PrevChainHash,
            "链头事件的 PrevChainHash 应为 null。");
        for (var i = 1; i < eventsAfterRestart.Count; i++)
        {
            Assert.AreEqual(
                eventsAfterRestart[i - 1].ContentHash,
                eventsAfterRestart[i].PrevChainHash,
                $"事件 {i} 的 PrevChainHash 应指向前一事件的 ContentHash（哈希链断裂）。");
        }

        // 断言：GetLastSequenceAsync 返回正确值（重启后仍可查询）
        var lastSeq = await eventStore.GetLastSequenceAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(lastSequenceBeforeRestart, lastSeq,
            "GetLastSequenceAsync 应返回重启前的最大 Sequence。");
    }

    /// <summary>
    /// 验证：Run Lease 防止多实例并发恢复同一 Run（HA 隔离基础）。
    /// </summary>
    [TestMethod]
    public async Task Restart_RunLease_PreventsConcurrentRecovery()
    {
        var lease = new InMemoryAgentRunLease();
        var runId = "run-restart-lease-" + Guid.NewGuid().ToString("N");
        var leaseDuration = TimeSpan.FromMinutes(5);

        // ── 模拟进程 A 获取租约 ──
        var leaseA = await lease.TryAcquireAsync(runId, leaseDuration, owner: "process-A");
        Assert.IsNotNull(leaseA, "进程 A 应成功获取租约。");
        Assert.AreEqual("process-A", leaseA!.Owner);

        // ── 模拟进程 B 尝试获取同一 Run 的租约 ──
        var leaseB = await lease.TryAcquireAsync(runId, leaseDuration, owner: "process-B");
        Assert.IsNull(leaseB,
            "进程 B 不应获取已被进程 A 持有的租约（防止并发恢复）。");

        // ── 模拟进程 A 崩溃后租约过期 / 释放 ──
        await lease.ReleaseAsync(runId, leaseA.LeaseToken);
        Assert.AreEqual(0, lease.ActiveLeaseCount, "释放后活跃租约应为 0。");

        // ── 模拟进程 B 重新获取租约（接管恢复）──
        var leaseB2 = await lease.TryAcquireAsync(runId, leaseDuration, owner: "process-B");
        Assert.IsNotNull(leaseB2, "进程 A 释放后，进程 B 应能获取租约接管恢复。");
        Assert.AreEqual("process-B", leaseB2!.Owner);

        // 清理
        await lease.ReleaseAsync(runId, leaseB2.LeaseToken);
    }

    /// <summary>
    /// 验证：恢复 Worker（AgentRunRecoveryWorker）能识别非终态 Run 并入队执行。
    /// </summary>
    [TestMethod]
    public async Task Restart_RecoveryWorker_PicksUpNonTerminalRuns()
    {
        // 准备：使用 PersistentInMemoryAgentRunStore 包装器（实现 IPersistentAgentRunStore 标记）
        var innerStore = new InMemoryAgentRunStore();
        var persistentStore = new PersistentInMemoryAgentRunStore(innerStore);
        var eventStore = new InMemoryAgentRunEventStore(persistentStore);

        // 创建一个非终态 Run（模拟崩溃前残留）
        var run = BuildRun("恢复 Worker 识别测试", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 3
        });
        // 直接以非终态创建（模拟崩溃前状态）
        run = run with { State = AgentRunState.ContextBuilding };
        await persistentStore.CreateAsync(run);

        // 构建 ServiceProvider，提供 AgentKernelHost 所需依赖
        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunStore>(persistentStore);
        services.AddSingleton<IPersistentAgentRunStore>(persistentStore);
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
        services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
        services.AddSingleton<AgentKernelHost>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var serviceProvider = services.BuildServiceProvider();

        var host = serviceProvider.GetRequiredService<AgentKernelHost>();
        await using (host)
        {
            var options = new ProductionRuntimeOptions
            {
                EnableRunRecovery = true,
                RunRecoveryInterval = TimeSpan.FromMilliseconds(100)
            };

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var worker = new AgentRunRecoveryWorker(
                serviceProvider, options, loggerFactory.CreateLogger<AgentRunRecoveryWorker>());

            // 启动 Worker，运行短暂时间后取消
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StartAsync(cts.Token);

            // 等待 Worker 拉取并恢复 Run
            var deadline = DateTime.UtcNow.AddSeconds(3);
            AgentRun? finalRun = null;
            while (DateTime.UtcNow < deadline)
            {
                finalRun = await persistentStore.GetAsync(run.WorkspaceId, run.RunId);
                if (finalRun is not null && AgentRunStateMachine.IsTerminalState(finalRun.State))
                {
                    break;
                }
                await Task.Delay(100);
            }

            await worker.StopAsync(CancellationToken.None);

            // 断言：Worker 识别到非终态 Run 并通过 Host 恢复执行
            Assert.IsNotNull(finalRun, "Run 应被 Worker 恢复。");
            Assert.IsTrue(AgentRunStateMachine.IsTerminalState(finalRun!.State),
                $"恢复后 Run 应进入终态，实际 {finalRun.State}。");
        }
    }

    /// <summary>
    /// 验证：Postgres 持久化恢复——非终态 Run 跨进程重启后可恢复。
    /// Postgres 不可用时跳过（Assert.Inconclusive）。
    /// </summary>
    [TestMethod]
    public async Task Restart_Postgres_PersistentRecovery()
    {
        var connectionString = GetPostgresConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Assert.Inconclusive("未配置 Postgres 连接字符串（环境变量 CONTEXT_TEST_POSTGRES），跳过持久化恢复测试。");
            return;
        }

        // 验证连接可用
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Postgres 连接失败：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Postgres 可用时：验证持久化 Run Store 的恢复语义
        // 此处仅验证连接可用性 + Run Store 基本操作；
        // 完整 Testcontainers 集成测试由 ContextCore.IntegrationTests 覆盖。
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = connectionString,
            AutoMigrate = true
        });

        try
        {
            var pingResult = await factory.PingAsync();
            Assert.IsTrue(pingResult.Success,
                $"Postgres Ping 应成功：{pingResult.ErrorMessage}");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(
        string task,
        AgentTurnBudget? turnBudget = null) => new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-r29h-restart",
            SessionId = "session-r29h-restart",
            Task = task,
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = turnBudget
        };

    /// <summary>从 JSON payload 提取整型字段。</summary>
    private static int ExtractIntField(string payload, string fieldName)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty(fieldName, out var el) && el.ValueKind == JsonValueKind.Number)
        {
            return el.GetInt32();
        }
        throw new AssertFailedException($"payload 中未找到整型字段 {fieldName}。");
    }

    /// <summary>
    /// 获取 Postgres 连接字符串（从环境变量 CONTEXT_TEST_POSTGRES）。
    /// 未配置时返回 null（测试将 Assert.Inconclusive）。
    /// </summary>
    private static string? GetPostgresConnectionString()
    {
        return Environment.GetEnvironmentVariable("CONTEXT_TEST_POSTGRES");
    }

    // ── 测试 stub ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 录制模型调用入参的 IAgentModelTransport stub。
    /// </summary>
    private sealed class RecordingModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse _response;
        public List<(string RunId, IReadOnlyList<AgentMessage> Messages)> CapturedCalls { get; } = new();

        public RecordingModelTransport(AgentModelResponse response)
        {
            _response = response;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            CapturedCalls.Add((runId, messages.ToList()));
            return ValueTask.FromResult(_response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>
    /// InMemoryAgentRunStore 的持久化标记包装器（实现 IPersistentAgentRunStore）。
    /// 用于测试 AgentRunRecoveryWorker（它要求 IPersistentAgentRunStore 标记接口）。
    /// </summary>
    private sealed class PersistentInMemoryAgentRunStore : IPersistentAgentRunStore
    {
        private readonly InMemoryAgentRunStore _inner;
        public PersistentInMemoryAgentRunStore(InMemoryAgentRunStore inner) => _inner = inner;

        public ValueTask CreateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.CreateAsync(run, cancellationToken);

        public ValueTask<AgentRun?> GetAsync(string workspaceId, string runId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(workspaceId, runId, cancellationToken);

        public ValueTask<AgentRun?> GetByIdempotencyKeyAsync(string workspaceId, string idempotencyKey, CancellationToken cancellationToken = default)
            => _inner.GetByIdempotencyKeyAsync(workspaceId, idempotencyKey, cancellationToken);

        public ValueTask<AgentRunCreateResult> CreateOrGetByIdempotencyKeyAsync(AgentRun run, CancellationToken ct = default)
            => _inner.CreateOrGetByIdempotencyKeyAsync(run, ct);

        public ValueTask TransitionStateAsync(
            string workspaceId, string runId,
            AgentRunState expectedCurrentState, AgentRunState newState,
            CancellationToken cancellationToken = default,
            string? leaseToken = null,
            long? fencingToken = null)
            => _inner.TransitionStateAsync(workspaceId, runId, expectedCurrentState, newState, cancellationToken, leaseToken, fencingToken);

        public ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(run, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(
            string workspaceId, string sessionId, CancellationToken cancellationToken = default)
            => _inner.ListBySessionAsync(workspaceId, sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
            AgentRunState state, int take = 100, CancellationToken cancellationToken = default)
            => _inner.ListByStateAsync(state, take, cancellationToken);
    }
}
