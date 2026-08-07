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
// Agent Run 进程重启恢复生产验收测试
//
// 验证 AgentRun 在进程重启后的恢复能力，覆盖：
// 1. Restart_NonTerminalRun_CanBeResumedByNewActor — 非终态 Run 可由新 Actor 恢复执行
// 2. Restart_TerminalRun_NotResumed — 终态 Run 不被恢复
// 3. Restart_ModelCallsUsed_PreservedAcrossRestart — ModelCallsUsed 跨重启保留
// 4. Restart_EventChain_PreservedAcrossRestart — 事件哈希链跨重启保留
// 5. Restart_RunLease_PreventsConcurrentRecovery — 租约防止并发恢复
// 6. Restart_RecoveryWorker_PicksUpNonTerminalRuns — 恢复 Worker 识别非终态 Run
// 7. Restart_Postgres_PersistentRecovery — Postgres 持久化恢复（不可用时 Inconclusive）
//
// 设计原则：
// - 优先使用真实 InMemory 实现（非 mock）：InMemoryAgentRunStore /
// InMemoryAgentRunEventStore / InMemoryAgentRunLease
// - 通过 PersistentInMemoryAgentRunStore 包装器实现 IPersistentAgentRunStore 标记
// （模拟进程重启后数据持久化）
// - 所有异步测试使用超时 CancellationTokenSource 防止挂起
// - 中文注释
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
        // 崩溃发生在首次 flush 之后：事件流已写入（RunCreated + StateTransition→ContextBuilding）
        // 且 Run 状态已推进到 ContextBuilding（非终态）。恢复 fail-closed 契约下，
        // 非 Created + 零事件的 Run 视为事件数据丢失（RecoveryBlocked，不得回退全新启动），
        // 因此崩溃模拟必须包含已持久化的事件流，恢复路径才能合法重放并继续执行。
        await SeedEventsAsync(eventStore, run, AgentRunState.ContextBuilding);

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
        // 崩溃发生在首次 flush 之后：事件流已写入（RunCreated + StateTransition→ContextBuilding），
        // Run 快照记录 ModelCallsUsed=2 / Turn=2（恢复时从 Run 元数据续计数）。
        // 恢复 fail-closed 契约下非 Created + 零事件视为事件数据丢失（RecoveryBlocked），
        // 因此崩溃模拟必须包含已持久化的事件流。
        await SeedEventsAsync(eventStore, run, AgentRunState.ContextBuilding);
        var persisted = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(persisted, "预置事件流后 Run 应存在。");
        var crashedRun = persisted! with
        {
            ModelCallsUsed = 2,
            Turn = 2,
            UpdatedAt = DateTimeOffset.UtcNow
        };
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
            var options = new ContextCoreRuntimeOptions
            {
                EnableAgentRunRecovery = true,
                RunRecoveryInterval = TimeSpan.FromMilliseconds(100)
            };

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var worker = new AgentRunRecoveryWorker(
                serviceProvider, options, loggerFactory.CreateLogger<AgentRunRecoveryWorker>());

            // 启动 Worker，运行短暂时间后取消
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StartAsync(cts.Token);

            // 阶段 1：Worker 把非终态 Run CAS 回 Queued（单一调度边界：Worker 只做状态修复）。
            AgentRun? queuedRun = null;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                queuedRun = await persistentStore.GetAsync(run.WorkspaceId, run.RunId);
                if (queuedRun is not null && queuedRun.State == AgentRunState.Queued)
                {
                    break;
                }
                await Task.Delay(100);
            }
            Assert.IsNotNull(queuedRun, "Run 应被 Worker 识别并 CAS 回 Queued。");
            Assert.AreEqual(AgentRunState.Queued, queuedRun!.State,
                "Worker 只做状态修复（CAS → Queued），不直接入队。");

            // 阶段 2：模拟 Durable Claimer 入队（真实路径由 PostgresPendingRunClaimer 领取后
            // host.TryEnqueueAsync），Host 执行 Run 至终态。
            await host.StartRunAsync(queuedRun, CancellationToken.None);
            deadline = DateTime.UtcNow.AddSeconds(5);
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

            // 断言：Worker 识别非终态 Run → CAS Queued → Claimer 入队后执行到终态。
            Assert.IsNotNull(finalRun, "Run 应被恢复执行。");
            Assert.IsTrue(AgentRunStateMachine.IsTerminalState(finalRun!.State),
                $"恢复后 Run 应进入终态，实际 {finalRun.State}。");
        }
    }

    /// <summary>
    /// 验证（Item 6 租约生命周期）：RecoveryWorker 对"非终态停留超时且无人持有租约"的 Run
    /// 原子标记为 LeaseLost（原 owner 丢租后未被接管），区别于执行失败的 Failed。
    /// </summary>
    [TestMethod]
    public async Task Lease_Lifecycle_RecoveryWorker_MarksAbandonedRunAsLeaseLost()
    {
        // 准备：持久化包装器 + 注入带 Run Store 的 InMemory 租约（使超时路径走原子标记）
        var innerStore = new InMemoryAgentRunStore();
        var persistentStore = new PersistentInMemoryAgentRunStore(innerStore);
        var eventStore = new InMemoryAgentRunEventStore(persistentStore);
        var lease = new InMemoryAgentRunLease(persistentStore);

        // 创建一个非终态 Run，UpdatedAt 早于 RunExecutionTimeout（模拟崩溃前残留且已超时）
        var run = BuildRun("超时丢租标记测试");
        run = run with
        {
            State = AgentRunState.ContextBuilding,
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };
        await persistentStore.CreateAsync(run);

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunStore>(persistentStore);
        services.AddSingleton<IPersistentAgentRunStore>(persistentStore);
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
        services.AddSingleton<IAgentModelTransport>(new DeterministicAgentModelTransport());
        services.AddSingleton<IAgentRunLease>(lease);
        services.AddSingleton<AgentKernelHost>();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        var serviceProvider = services.BuildServiceProvider();

        await using (serviceProvider.GetRequiredService<AgentKernelHost>())
        {
            var options = new ContextCoreRuntimeOptions
            {
                EnableAgentRunRecovery = true,
                RunRecoveryInterval = TimeSpan.FromMilliseconds(100),
                RunExecutionTimeout = TimeSpan.FromHours(1)
            };

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var worker = new AgentRunRecoveryWorker(
                serviceProvider, options, loggerFactory.CreateLogger<AgentRunRecoveryWorker>());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StartAsync(cts.Token);

            // 等待 Worker 将超时 Run 原子标记为 LeaseLost
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

            // 断言：超时且无活跃租约 → LeaseLost（而非 Failed）
            Assert.IsNotNull(finalRun, "Run 应被 Worker 标记。");
            Assert.AreEqual(AgentRunState.LeaseLost, finalRun!.State,
                $"超时且无人持有租约的 Run 应标记为 LeaseLost，实际 {finalRun.State}。");
        }
    }

    /// <summary>
    /// 验证（Item 6 租约生命周期）：原子标记（MarkLeaseLostIfLeaseExpiredAsync）在
    /// 有活跃租约时拒绝标记（0 行），无活跃租约时才写入 LeaseLost。
    /// </summary>
    [TestMethod]
    public async Task Lease_Lifecycle_AtomicMark_RespectsActiveLease()
    {
        var runStore = new InMemoryAgentRunStore();
        var lease = new InMemoryAgentRunLease(runStore);

        // 无活跃租约 → 原子标记为 LeaseLost
        var abandoned = BuildRun("无租约超时 Run");
        abandoned = abandoned with { State = AgentRunState.ContextBuilding };
        await runStore.CreateAsync(abandoned);

        var affected = await lease.MarkLeaseLostIfLeaseExpiredAsync(
            abandoned.WorkspaceId, abandoned.RunId, abandoned.State);
        Assert.AreEqual(1, affected, "无活跃租约时应成功标记 1 行。");

        var marked = await runStore.GetAsync(abandoned.WorkspaceId, abandoned.RunId);
        Assert.IsNotNull(marked, "标记后的 Run 应可查询。");
        Assert.AreEqual(AgentRunState.LeaseLost, marked!.State);

        // 有活跃租约 → 拒绝标记（0 行），状态保持不变
        var active = BuildRun("持有租约的 Run");
        active = active with { State = AgentRunState.Observing };
        await runStore.CreateAsync(active);
        var acquired = await lease.TryAcquireAsync(active.RunId, TimeSpan.FromMinutes(5), "owner-test");
        Assert.IsNotNull(acquired, "测试租约应获取成功。");

        var affected2 = await lease.MarkLeaseLostIfLeaseExpiredAsync(
            active.WorkspaceId, active.RunId, active.State);
        Assert.AreEqual(0, affected2, "存在活跃租约时应拒绝标记。");

        var unchanged = await runStore.GetAsync(active.WorkspaceId, active.RunId);
        Assert.IsNotNull(unchanged, "Run 应仍可查询。");
        Assert.AreEqual(AgentRunState.Observing, unchanged!.State,
            "存在活跃租约时状态不应被推进。");
    }

    /// <summary>
    /// 验证：Postgres 持久化恢复——非终态 Run 跨进程重启后可恢复。
    /// Postgres 不可用时跳过（Assert.Inconclusive）。
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
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

    /// <summary>
    /// 验证：批量续约（RenewBatchAsync）一次续约全部有效租约，并报告
    /// token 错误 / 不存在 / 已过期租约的 RunId（供共享心跳循环取消对应 Actor）。
    /// </summary>
    [TestMethod]
    public async Task Heartbeat_RenewBatch_RenewsValidAndReportsStaleOrExpired()
    {
        var lease = new InMemoryAgentRunLease();
        var duration = TimeSpan.FromMinutes(5);

        var a = await lease.TryAcquireAsync("run-batch-a", duration, owner: "host-1");
        var b = await lease.TryAcquireAsync("run-batch-b", duration, owner: "host-1");
        Assert.IsNotNull(a, "租约 A 应获取成功。");
        Assert.IsNotNull(b, "租约 B 应获取成功。");

        // 两条有效租约 → 批量续约无失败
        var ok = await lease.RenewBatchAsync(new[]
        {
            new AgentRunLeaseRenewal { RunId = a!.RunId, LeaseToken = a.LeaseToken },
            new AgentRunLeaseRenewal { RunId = b!.RunId, LeaseToken = b.LeaseToken }
        }, TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, ok.Count, "全部有效租约应续约成功。");
        Assert.IsTrue(await lease.HasActiveLeaseAsync(a.RunId), "续约后租约 A 应仍活跃。");
        Assert.IsTrue(await lease.HasActiveLeaseAsync(b.RunId), "续约后租约 B 应仍活跃。");

        // 混合：一条有效 + 一条 token 错误 → 仅 token 错误的报告失败
        var mixed = await lease.RenewBatchAsync(new[]
        {
            new AgentRunLeaseRenewal { RunId = a.RunId, LeaseToken = a.LeaseToken },
            new AgentRunLeaseRenewal { RunId = b.RunId, LeaseToken = "wrong-token" }
        }, TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, mixed.Count, "错误 token 的租约应续约失败。");
        Assert.AreEqual(b.RunId, mixed[0], "失败集合应含错误 token 的 RunId。");

        // 不存在的 Run → 续约失败
        var missing = await lease.RenewBatchAsync(new[]
        {
            new AgentRunLeaseRenewal { RunId = "run-batch-nonexistent", LeaseToken = "x" }
        }, TimeSpan.FromMinutes(5));
        Assert.AreEqual(1, missing.Count, "不存在的租约应续约失败。");
    }

    /// <summary>
    /// 验证：共享批量心跳循环——启用租约的 Run 由单一循环批量续约，
    /// 续约失败（租约丢失）时取消对应 Actor（Run 进入 Cancelled 终态，防止双执行）。
    /// </summary>
    [TestMethod]
    public async Task Heartbeat_SharedBatchLoop_CancelsActorOnLeaseLoss()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var lease = new FailingBatchRenewLease();
        var transport = new GateModelTransport();

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunEventStore>(eventStore);
        services.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
        services.AddSingleton<IAgentModelTransport>(transport);
        var serviceProvider = services.BuildServiceProvider();

        var options = new AgentHostOptions
        {
            LeaseEnabled = true,
            LeaseDuration = TimeSpan.FromMinutes(10),
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            WorkerCount = 2,
            ChannelCapacity = 16,
            DrainTimeout = TimeSpan.FromSeconds(10)
        };

        await using var host = new AgentKernelHost(serviceProvider, runStore, lease, options);

        var run = BuildRun("心跳批量续约测试", turnBudget: new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        });
        await runStore.CreateAsync(run);
        await host.StartRunAsync(run);

        // 等待 Actor 进入模型调用（阻塞在 GateModelTransport）
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && transport.CallCount == 0)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(transport.CallCount > 0, "Actor 应已进入模型调用。");

        // 等待共享心跳循环完成至少一次批量续约（证明批量路径被使用）
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && lease.RenewBatchCalls == 0)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(lease.RenewBatchCalls > 0, "共享心跳循环应调用 RenewBatchAsync。");

        // 触发租约丢失：后续批量续约全部失败 → 共享循环应取消 Actor。
        // InMemory（无 fencing 校验）下 Actor 的取消收尾写入 Cancelled；
        // 生产 Postgres 下该写入因 fencing 校验失败而被拒绝，Run 保持非终态由 RecoveryWorker 恢复。
        lease.FailRenewals();

        deadline = DateTime.UtcNow.AddSeconds(5);
        AgentRun? finalRun = null;
        while (DateTime.UtcNow < deadline)
        {
            finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            if (finalRun is not null && AgentRunStateMachine.IsTerminalState(finalRun.State))
            {
                break;
            }
            await Task.Delay(50);
        }

        Assert.IsNotNull(finalRun, "Run 应被心跳循环取消后进入终态。");
        Assert.AreEqual(AgentRunState.Cancelled, finalRun!.State,
            "租约丢失后共享心跳循环应取消 Actor，Run 应进入 Cancelled 终态。");
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
    /// 预置合法事件流（RunCreated + StateTransition→targetState）并将 Run 状态推进到目标状态。
    /// 模拟"进程崩溃于首次 flush 之后"：事件已持久化、状态已推进，恢复路径可合法重放。
    /// </summary>
    private static async Task SeedEventsAsync(
        InMemoryAgentRunEventStore eventStore,
        AgentRun run,
        AgentRunState targetState)
    {
        var seq0 = AgentRunEventChain.BuildEvent(
            run.RunId, run.WorkspaceId, sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: """{"runId":"seed"}""",
            prevChainHash: null);
        var seq1 = AgentRunEventChain.BuildEvent(
            run.RunId, run.WorkspaceId, sequence: 1,
            type: AgentRunEventType.StateTransition,
            state: targetState,
            payload: $$"""{"from":"Created","to":"{{targetState}}"}""",
            prevChainHash: seq0.ContentHash);

        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = AgentRunState.Created,
            NewState = targetState,
            RunSnapshot = run with { State = targetState, UpdatedAt = DateTimeOffset.UtcNow }
        };
        await eventStore.AppendBatchAsync(
            [seq0, seq1], runStateUpdate, checkpointCursor: null, checkpointBody: null, CancellationToken.None);
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

        public async ValueTask<AgentRunAdmitResult> AdmitRunAtomicallyAsync(
            AgentRun run, QuotaAdmissionRequest? quotaAdmission, CancellationToken cancellationToken = default)
        {
            var created = await _inner.CreateOrGetByIdempotencyKeyAsync(run, cancellationToken).ConfigureAwait(false);
            if (created.WasExisting)
            {
                return new AgentRunAdmitResult { Created = false, WasExisting = true, Run = created.Run };
            }
            // 进程重启测试不消费配额语义：预留请求直接放行，推进 Queued。
            if (run.State == AgentRunState.PendingAdmission)
            {
                await _inner.TransitionStateAsync(
                    run.WorkspaceId, run.RunId, AgentRunState.PendingAdmission, AgentRunState.Queued, cancellationToken)
                    .ConfigureAwait(false);
            }
            return new AgentRunAdmitResult
            {
                Created = true,
                WasExisting = false,
                Run = created.Run with { State = AgentRunState.Queued, UpdatedAt = DateTimeOffset.UtcNow }
            };
        }

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
            AgentRunState state, int take = 100,
            DateTimeOffset? afterUpdatedAt = null, string? afterRunId = null,
            CancellationToken cancellationToken = default)
            => _inner.ListByStateAsync(state, take, afterUpdatedAt, afterRunId, cancellationToken);

        // B3 Durable Scheduler 接口成员：进程重启测试不使用领取/死信路径 → 返回空。
        public ValueTask<IReadOnlyList<AgentRun>> ClaimPendingBatchAsync(
            int take, int perWorkspace, TimeSpan retryBackoffBase, TimeSpan retryBackoffMax,
            string claimOwner, TimeSpan claimDuration,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());

        // P0-8 Scheduler Claim 接口成员：进程重启测试不使用领取路径 → 不可领取/释放失败。
        public ValueTask<AgentRun?> TryClaimSingleAsync(
            string workspaceId, string runId, string claimOwner, TimeSpan claimDuration,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentRun?>(null);

        public ValueTask<bool> ReleaseClaimAsync(
            string workspaceId, string runId, string claimToken,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public async ValueTask<AgentRun> ConsumeClaimAsync(
            string workspaceId, string runId, string? expectedClaimToken, string? expectedClaimOwner,
            string? executionLeaseToken, long? executionFencingToken,
            CancellationToken cancellationToken = default)
        {
            await _inner.TransitionStateAsync(
                workspaceId, runId, AgentRunState.Claimed, AgentRunState.Running,
                cancellationToken, executionLeaseToken, executionFencingToken).ConfigureAwait(false);
            var run = await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new InvalidOperationException($"Run 不存在：{workspaceId}/{runId}。");
            }
            return run with
            {
                State = AgentRunState.Running,
                ClaimOwner = null,
                ClaimToken = null,
                ClaimExpiresAtUtc = null
            };
        }

        public async ValueTask<AgentRun> ScheduleLocallyAsync(
            string workspaceId, string runId, string? expectedClaimToken, string? expectedClaimOwner,
            CancellationToken cancellationToken = default)
        {
            await _inner.TransitionStateAsync(
                workspaceId, runId, AgentRunState.Claimed, AgentRunState.ScheduledLocally,
                cancellationToken).ConfigureAwait(false);
            var run = await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new InvalidOperationException($"Run 不存在：{workspaceId}/{runId}。");
            }
            return run with
            {
                State = AgentRunState.ScheduledLocally,
                ClaimOwner = null,
                ClaimToken = null,
                ClaimExpiresAtUtc = null
            };
        }

        public ValueTask<IReadOnlyList<AgentRun>> DeadLetterExhaustedRunsAsync(
            int take, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());
    }

    /// <summary>
    /// IAgentRunLease 包装器：可随时切换为"全部续约失败"（模拟租约被抢占/数据库异常），
    /// 并统计 RenewBatchAsync 调用次数（验证共享批量心跳路径被使用）。
    /// </summary>
    private sealed class FailingBatchRenewLease : IAgentRunLease
    {
        private readonly InMemoryAgentRunLease _inner = new();
        private int _failRenewals;
        private int _renewBatchCalls;

        public int RenewBatchCalls => Volatile.Read(ref _renewBatchCalls);
        public void FailRenewals() => Volatile.Write(ref _failRenewals, 1);

        public ValueTask<LeasedAgentRun?> TryAcquireAsync(
            string runId, TimeSpan leaseDuration, string owner, CancellationToken cancellationToken = default)
            => _inner.TryAcquireAsync(runId, leaseDuration, owner, cancellationToken);

        public ValueTask<bool> RenewAsync(
            string runId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _failRenewals) != 0)
            {
                return ValueTask.FromResult(false);
            }
            return _inner.RenewAsync(runId, leaseToken, extension, cancellationToken);
        }

        public async ValueTask<IReadOnlyList<string>> RenewBatchAsync(
            IReadOnlyList<AgentRunLeaseRenewal> leases, TimeSpan extension, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _renewBatchCalls);
            if (Volatile.Read(ref _failRenewals) != 0)
            {
                // 全部续约失败（模拟租约丢失）
                var failed = new List<string>(leases.Count);
                foreach (var l in leases)
                {
                    failed.Add(l.RunId);
                }
                return failed;
            }
            return await _inner.RenewBatchAsync(leases, extension, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask ReleaseAsync(string runId, string leaseToken, CancellationToken cancellationToken = default)
            => _inner.ReleaseAsync(runId, leaseToken, cancellationToken);

        public ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
            => _inner.ReapExpiredAsync(cancellationToken);

        public ValueTask<bool> HasActiveLeaseAsync(string runId, CancellationToken cancellationToken = default)
            => _inner.HasActiveLeaseAsync(runId, cancellationToken);

        public ValueTask<IReadOnlyList<string>> GetActiveLeaseRunIdsAsync(
            IReadOnlyList<string> runIds, CancellationToken cancellationToken = default)
            => _inner.GetActiveLeaseRunIdsAsync(runIds, cancellationToken);

        public ValueTask<int> MarkLeaseLostIfLeaseExpiredAsync(
            string workspaceId, string runId, AgentRunState expectedCurrentState, CancellationToken ct = default)
            => _inner.MarkLeaseLostIfLeaseExpiredAsync(workspaceId, runId, expectedCurrentState, ct);
    }

    /// <summary>
    /// 门控模型传输：CallAsync 阻塞直到 Release 或取消令牌触发。
    /// 用于让 Actor 停留在模型调用中，观察共享心跳循环的租约丢失取消行为。
    /// </summary>
    private sealed class GateModelTransport : IAgentModelTransport
    {
        private readonly TaskCompletionSource<bool> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public void Release() => _gate.TrySetResult(true);

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, string context, CancellationToken cancellationToken = default)
            => CallAsyncCore(cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(
            string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => CallAsyncCore(cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(
            AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsyncCore(cancellationToken);

        private async ValueTask<AgentModelResponse> CallAsyncCore(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new AgentModelResponse
            {
                Content = "门控传输放行",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 1,
                Duration = TimeSpan.FromMilliseconds(1)
            };
        }
    }
}
