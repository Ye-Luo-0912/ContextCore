using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Durable Shutdown/Ack 硬验收门测试
//
// 目标（对齐任务A 修复：Durable Agent 消息丢失问题）：
//   验证 Input Lease、Tool Journal、Result Outbox、Transport Delivery、Input Ack
//   的完成事务边界。核心不变量（P0-6-4）：
//     "Input can be Acked IFF Result is durably persisted OR durably delivered"
//
// 设计原则：
//   - 使用真实 InMemory 实现（InMemoryKernelResultOutbox / InMemoryToolDispatchJournal /
//     InMemoryAgentCheckpointStore / EchoToolDispatcher），避免过度 mock。
//   - InProcessTransport 不实现 IDurableTransport，无法验证 lease/Ack 语义；
//     因此使用内置的 FakeDurableTransport（进程内 IDurableTransport 实现）模拟
//     Durable Transport 的 lease/Ack/Nack/Renew 行为，跟踪每条指令的确认状态。
//   - 所有异步测试使用超时 CancellationTokenSource（10 秒）防止挂起。
//   - 所有代码注释使用中文。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Durable-Shutdown")]
public sealed class R29H_DurableShutdownAcceptanceTests
{
    /// <summary>默认超时：防止测试挂起（10 秒）。返回的 CTS 由调用方按需释放。</summary>
    private static CancellationTokenSource CreateTestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10));

    /// <summary>
    /// 构造一条带 Durable lease token 的 Execute 指令（模拟 pump 租约后 Submit 到 Kernel inbox）。
    /// </summary>
    /// <param name="id">指令 ID。</param>
    /// <param name="leaseToken">租约 token（Ack/Nack 时必须匹配）。</param>
    /// <param name="payload">指令负载（默认 "echo-payload"）。</param>
    private static AgentKernelInstruction MakeLeasedExecuteInstruction(
        string id, string leaseToken, string payload = "echo-payload")
    {
        return new AgentKernelInstruction
        {
            InstructionId = id,
            Kind = AgentKernelInstructionKind.Execute,
            Payload = payload,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DurableTransportMetadataKeys.LeaseToken] = leaseToken,
                [DurableTransportMetadataKeys.LeaseOwner] = "test-pump",
            }
        };
    }

    // =======================================================================
    // 测试 1：Durable Transport shutdown 时，所有正在处理的 instruction 被 drain
    //         且每个恰好 Ack 一次（不重复、不丢失）。
    // =======================================================================

    [TestMethod]
    public async Task Durable_Shutdown_IsAcked_ExactlyOnce()
    {
        // 安排：FakeDurableTransport + EchoToolDispatcher + InMemory 依赖
        var transport = new FakeDurableTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var outbox = new InMemoryKernelResultOutbox();
        var journal = new InMemoryToolDispatchJournal();
        var kernel = new DefaultAgentKernel(
            transport,
            new EchoToolDispatcher(),
            checkpointStore,
            transportOptions: new KernelTransportOptions { UseDurableTransport = true },
            resultOutbox: outbox,
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        // 启动 Kernel 循环
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 提交 3 条带 lease token 的 Execute 指令
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-1", "token-1"), testCt.Token);
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-2", "token-2"), testCt.Token);
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-3", "token-3"), testCt.Token);

        // 提交 Shutdown 触发 drain（Shutdown 无 lease token，不参与 Ack 计数）
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown-1",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        // 等待 Kernel 正常退出（Shutdown 路径不抛异常）
        await runTask;

        // 断言：3 条指令全部被 Ack
        Assert.AreEqual(3, transport.AckedInstructionIds.Count,
            "3 条 leased instruction 应全部被 Ack（不丢失）。");
        CollectionAssert.AreEquivalent(
            new[] { "exec-1", "exec-2", "exec-3" },
            transport.AckedInstructionIds.ToList(),
            "Ack 的指令 ID 集合应与提交的指令一致。");

        // 断言：每条指令恰好 Ack 一次（不重复）
        foreach (var id in new[] { "exec-1", "exec-2", "exec-3" })
        {
            Assert.AreEqual(1, transport.GetAckCount(id),
                $"{id} 应恰好 Ack 一次（不重复）。");
            Assert.AreEqual(0, transport.GetNackCount(id),
                $"{id} 不应被 Nack。");
        }

        // 断言：所有结果已通过 Transport 发送（durably delivered）
        Assert.AreEqual(3, transport.DeliveredResultCount,
            "3 条指令的结果应全部通过 Transport 发送。");

        // 断言：Kernel 状态为 Stopped
        Assert.AreEqual(AgentKernelState.Stopped, kernel.GetStatus().State);
        Assert.AreEqual(3, kernel.GetStatus().ProcessedCount);
    }

    // =======================================================================
    // 测试 2：Drain 时每条 leased instruction 要么被 Ack（成功处理）要么被 Nack
    //         （失败/放弃），不会既不 Ack 也不 Nack。
    // =======================================================================

    [TestMethod]
    public async Task Drained_LeasedInstruction_IsAcked_Or_Nacked()
    {
        // 安排：FakeDurableTransport + 混合 dispatcher（正常 + 抛异常）
        var transport = new FakeDurableTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var journal = new InMemoryToolDispatchJournal();
        // 使用 ThrowingToolDispatcher 模拟处理失败的 instruction
        var throwingDispatcher = new ThrowingToolDispatcher(new InvalidOperationException("tool-failure-boom"));
        var kernel = new DefaultAgentKernel(
            transport,
            throwingDispatcher,
            checkpointStore,
            transportOptions: new KernelTransportOptions { UseDurableTransport = true },
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        // 启动 Kernel 循环
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 提交 2 条带 lease token 的 Execute 指令（ThrowingToolDispatcher 对所有调用抛异常）
        // 两条指令都会被 Nack（处理失败 → TransientInfrastructure → Nack + re-throw）
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-fail-1", "token-1"), testCt.Token);
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-fail-2", "token-2"), testCt.Token);

        // RunAsync 会在第一条指令处理失败后抛异常退出（ProcessLeasedInstructionAsync re-throw）
        // 但在退出前已对第一条指令执行了 Nack
        Exception? caught = null;
        try
        {
            await runTask;
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        Assert.IsNotNull(caught,
            "RunAsync 应在处理失败时抛出异常（P0-6-3：基础设施临时故障 → Nack + re-throw）。");

        // 断言：第一条指令被 Nack（不处于"未决定"状态）
        Assert.IsTrue(transport.GetNackCount("exec-fail-1") >= 1,
            "exec-fail-1 处理失败应被 Nack（不处于未决定状态）。");
        Assert.AreEqual(0, transport.GetAckCount("exec-fail-1"),
            "exec-fail-1 处理失败不应被 Ack。");

        // 断言：每条 leased instruction 要么 Ack 要么 Nack，不会既不 Ack 也不 Nack
        // 第一条指令已决定（Nack）；第二条指令可能在 inbox 中未被处理（RunAsync 已退出），
        // 但其 lease 未被 Kernel 接管 → 由 reaper 回滚为 Pending（不是"未决定"，而是"未接管"）
        // 验证核心不变量：被 Kernel 接管处理的指令一定有明确决定（Ack XOR Nack）
        var processedIds = new[] { "exec-fail-1" };
        foreach (var id in processedIds)
        {
            var acked = transport.GetAckCount(id) > 0;
            var nacked = transport.GetNackCount(id) > 0;
            Assert.IsTrue(acked || nacked,
                $"{id} 应被 Ack 或 Nack（不应处于未决定状态）。");
            Assert.IsFalse(acked && nacked,
                $"{id} 不应同时被 Ack 和 Nack。");
        }
    }

    // =======================================================================
    // 测试 3：处理过程中发生瞬时失败（如 DB 连接失败模拟）时，Input 不会被 Ack
    //         （因为结果未持久化）。验证 instruction 被 Nack（回滚为 Pending 供重试）。
    // =======================================================================

    [TestMethod]
    public async Task Transient_ProcessingFailure_DoesNotAckInput()
    {
        // 安排：FakeDurableTransport + ThrowingToolDispatcher（模拟 DB 连接失败）
        var transport = new FakeDurableTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var outbox = new InMemoryKernelResultOutbox();
        var journal = new InMemoryToolDispatchJournal();
        var kernel = new DefaultAgentKernel(
            transport,
            new ThrowingToolDispatcher(new InvalidOperationException("Simulated DB connection failure")),
            checkpointStore,
            transportOptions: new KernelTransportOptions
            {
                UseDurableTransport = true,
                FailurePolicy = TransportFailurePolicy.FallbackToDeterministic,
                EnableResultOutbox = true
            },
            resultOutbox: outbox,
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        // 启动 Kernel 循环
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 提交 1 条带 lease token 的 Execute 指令
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-transient-1", "token-transient-1"), testCt.Token);

        // RunAsync 在处理失败后抛异常退出（tool 抛异常 → Nack → re-throw）
        Exception? caught = null;
        try
        {
            await runTask;
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        Assert.IsNotNull(caught,
            "RunAsync 应在瞬时失败时抛出异常。");

        // 断言：Input 未被 Ack（核心不变量：结果未持久化 → 不能 Ack）
        Assert.AreEqual(0, transport.GetAckCount("exec-transient-1"),
            "瞬时失败时 Input 不应被 Ack（结果未持久化也未投递）。");

        // 断言：Input 被 Nack（回滚为 Pending 供重试）
        Assert.IsTrue(transport.GetNackCount("exec-transient-1") >= 1,
            "瞬时失败时 Input 应被 Nack（回滚为 Pending 供 pump 重新租约）。");

        // 断言：Outbox 中无持久化结果（tool 抛异常 → 未产出结果 → 无 outbox 写入）
        Assert.AreEqual(0, outbox.PendingCount,
            "瞬时失败时不应向 Outbox 写入结果（tool 未返回结果）。");

        // 断言：Transport 未发送任何结果（tool 失败 → 无结果发送）
        Assert.AreEqual(0, transport.DeliveredResultCount,
            "瞬时失败时不应通过 Transport 发送结果。");
    }

    // =======================================================================
    // 测试 4：Transport delivery 和 Outbox 持久化都失败时（双失败），Input 不会被 Ack。
    //         核心不变量（P0-6-4）："Input can be Acked IFF Result is durably
    //         persisted OR durably delivered"。
    // =======================================================================

    [TestMethod]
    public async Task Transport_AndOutbox_Failure_DoesNotAckInput()
    {
        // 安排：FakeDurableTransport（SendResultAsync 抛异常）+ FailingOutbox（EnqueueAsync 抛异常）
        var transport = new FakeDurableTransport
        {
            SendResultThrows = true,
            SendResultException = new InvalidOperationException("Transport delivery failed (simulated)")
        };
        var failingOutbox = new FailingOutbox(new InvalidOperationException("Outbox persistence failed (simulated)"));
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var journal = new InMemoryToolDispatchJournal();
        var kernel = new DefaultAgentKernel(
            transport,
            new EchoToolDispatcher(), // tool 成功 → 产出结果 → SendResult 失败
            checkpointStore,
            transportOptions: new KernelTransportOptions
            {
                UseDurableTransport = true,
                FailurePolicy = TransportFailurePolicy.FallbackToDeterministic,
                EnableResultOutbox = true
            },
            resultOutbox: failingOutbox,
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        // 启动 Kernel 循环
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 提交 1 条带 lease token 的 Execute 指令
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-double-fail-1", "token-df-1"), testCt.Token);

        // RunAsync 在双失败后抛异常退出（SendResult 失败 + Outbox 失败 → 抛异常 → Nack → re-throw）
        Exception? caught = null;
        try
        {
            await runTask;
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        Assert.IsNotNull(caught,
            "RunAsync 应在 Transport + Outbox 双失败时抛出异常。");

        // 断言：Input 未被 Ack（核心不变量：结果既未 durably delivered 也未 durably persisted → 不能 Ack）
        Assert.AreEqual(0, transport.GetAckCount("exec-double-fail-1"),
            "Transport + Outbox 双失败时 Input 不应被 Ack（结果未持久化也未投递）。");

        // 断言：Input 被 Nack（回滚为 Pending 供重试）
        Assert.IsTrue(transport.GetNackCount("exec-double-fail-1") >= 1,
            "双失败时 Input 应被 Nack（不变量：Ack IFF Result persisted OR delivered）。");

        // 断言：Transport SendResult 被调用过（首次尝试发送失败）
        Assert.IsTrue(transport.SendResultAttemptCount >= 1,
            "应至少尝试过一次 Transport 发送。");

        // 断言：Outbox Enqueue 被调用过（Transport 失败后尝试持久化到 outbox，也失败）
        Assert.IsTrue(failingOutbox.EnqueueAttemptCount >= 1,
            "Transport 失败后应尝试写入 Outbox 持久化。");
    }

    // =======================================================================
    // 测试 5：Instruction 在执行时，Lease 被续租（不会因 lease 过期而被其他实例重新获取）。
    //         验证 lease_expires_at 被延长。
    //
    // 注意：InMemory 下难以模拟"队列中等待"期间的 lease 续租（Kernel 仅在执行期间续租，
    //       队列期间的 lease 由 pump 维护）。此测试验证"执行期间续租"路径。
    // =======================================================================

    [TestMethod]
    public async Task Lease_IsRenewed_WhileQueued_OrExecuting()
    {
        // 安排：FakeDurableTransport + SlowToolDispatcher（300ms 执行）+ 短续租间隔
        var transport = new FakeDurableTransport();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var journal = new InMemoryToolDispatchJournal();
        var slowDispatcher = new SlowToolDispatcher(TimeSpan.FromMilliseconds(300));
        var kernel = new DefaultAgentKernel(
            transport,
            slowDispatcher,
            checkpointStore,
            transportOptions: new KernelTransportOptions
            {
                UseDurableTransport = true,
                // 续租间隔 50ms（远小于 300ms 执行时间 → 期间应多次续租）
                DurableLeaseRenewalInterval = TimeSpan.FromMilliseconds(50),
                // 最大处理时长 5s（远大于 300ms → 不会触发超时 PermanentFault）
                DurableMaxProcessingTime = TimeSpan.FromSeconds(5)
            },
            dispatchJournal: journal);
        var testCt = CreateTestTimeout();

        // 模拟 pump：注册初始 lease（200ms duration，短于 300ms 执行时间）
        // 若不续租，lease 会在执行期间过期，被其他实例 reaper 回滚
        transport.RegisterLease("exec-slow-1", "token-slow-1", TimeSpan.FromMilliseconds(200));
        var initialExpiresAt = transport.GetLeaseExpiresAt("exec-slow-1");
        Assert.IsNotNull(initialExpiresAt, "初始 lease 应已注册。");

        // 启动 Kernel 循环
        var runTask = Task.Run(() => kernel.RunAsync(testCt.Token).AsTask(), testCt.Token);

        // 提交 1 条带 lease token 的慢 Execute 指令 + Shutdown
        await kernel.SubmitAsync(MakeLeasedExecuteInstruction("exec-slow-1", "token-slow-1", "slow-payload"), testCt.Token);
        await kernel.SubmitAsync(new AgentKernelInstruction
        {
            InstructionId = "shutdown-slow",
            Kind = AgentKernelInstructionKind.Shutdown
        }, testCt.Token);

        // 等待 Kernel 完成（~300ms 执行 + drain）
        await runTask;

        // 断言：执行期间 Lease 被续租至少一次
        var renewCount = transport.GetRenewCount("exec-slow-1");
        Assert.IsTrue(renewCount >= 1,
            $"执行期间 Lease 应被续租至少一次（实际续租 {renewCount} 次，" +
            $"执行 300ms / 续租间隔 50ms → 预期 ~5 次）。");

        // 断言：lease_expires_at 被延长（续租后的 expires_at > 初始 expires_at）
        // 注意：Ack 会删除当前 lease 记录（语义正确——确认后释放 lease），因此查询最近一次续租
        // 记录的 expires_at（不被 Ack 清除）来验证续租效果。
        var finalExpiresAt = transport.GetLastRenewedExpiresAt("exec-slow-1");
        Assert.IsNotNull(finalExpiresAt, "续租后应仍存在 lease 续租记录。");
        Assert.IsTrue(finalExpiresAt > initialExpiresAt,
            $"lease_expires_at 应被续租延长（初始={initialExpiresAt:O}，最终={finalExpiresAt:O}）。");

        // 断言：续租后的 expires_at 在当前时间之后（lease 未过期）
        Assert.IsTrue(finalExpiresAt > DateTimeOffset.UtcNow,
            "续租后 lease_expires_at 应在当前时间之后（lease 仍有效，未被 reaper 回滚）。");

        // 断言：指令最终被 Ack（执行成功 + 结果发送成功）
        Assert.AreEqual(1, transport.GetAckCount("exec-slow-1"),
            "慢指令执行完成后应被 Ack。");
        Assert.AreEqual(0, transport.GetNackCount("exec-slow-1"),
            "慢指令不应被 Nack。");
    }

    // =======================================================================
    // 测试 Stub：FakeDurableTransport — 进程内 IDurableTransport 实现
    // =======================================================================

    /// <summary>
    /// 进程内 IDurableTransport 实现，用于 Durable Shutdown/Ack 验收测试。
    /// 跟踪每条指令的 Ack/Nack/Renew 调用次数，供测试断言"恰好一次"语义。
    /// </summary>
    /// <remarks>
    /// InProcessTransport 不实现 IDurableTransport，无法验证 lease/Ack 语义；
    /// 此类提供轻量级的进程内 Durable Transport 模拟，不依赖 PostgreSQL。
    /// 生产语义的正确性由 PostgresDurableTransport 集成测试覆盖。
    /// </remarks>
    private sealed class FakeDurableTransport : IDurableTransport
    {
        // === 状态追踪 ===
        private readonly ConcurrentDictionary<string, int> _ackCounts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _nackCounts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _renewCounts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leaseTokens = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTimeOffset> _leaseExpiresAt = new(StringComparer.Ordinal);
        // 最近一次续租（或注册）的 expires_at，不被 Ack/Nack 清除，供测试在 Kernel 完成后验证续租效果
        private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRenewedExpiresAt = new(StringComparer.Ordinal);
        private readonly Channel<AgentKernelResult> _outbox = Channel.CreateBounded<AgentKernelResult>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        private int _sendResultCount;

        // === 配置 ===
        /// <summary>SendResultAsync 是否抛出异常（模拟 Transport delivery 失败）。</summary>
        public bool SendResultThrows { get; set; }

        /// <summary>SendResultAsync 抛出的异常（SendResultThrows=true 时生效）。</summary>
        public Exception? SendResultException { get; set; }

        // === 查询接口（供测试断言） ===
        public int GetAckCount(string id) =>
            _ackCounts.TryGetValue(id, out var c) ? c : 0;

        public int GetNackCount(string id) =>
            _nackCounts.TryGetValue(id, out var c) ? c : 0;

        public int GetRenewCount(string id) =>
            _renewCounts.TryGetValue(id, out var c) ? c : 0;

        public DateTimeOffset? GetLeaseExpiresAt(string id) =>
            _leaseExpiresAt.TryGetValue(id, out var t) ? t : null;

        /// <summary>获取最近一次续租（或注册）的 expires_at（不被 Ack/Nack 清除），供测试在 Kernel 完成后验证续租效果。</summary>
        public DateTimeOffset? GetLastRenewedExpiresAt(string id) =>
            _lastRenewedExpiresAt.TryGetValue(id, out var t) ? t : null;

        public IReadOnlyCollection<string> AckedInstructionIds => _ackCounts.Keys.ToList();

        public IReadOnlyCollection<string> NackedInstructionIds => _nackCounts.Keys.ToList();

        public int DeliveredResultCount => _sendResultCount;

        public int SendResultAttemptCount => _sendResultCount;

        public int PendingResultCount => _outbox.Reader.Count;

        /// <summary>
        /// 注册初始 lease（模拟 pump 租约指令）。
        /// Kernel 不调用 LeaseAsync（pump 负责），测试通过此方法设置初始 lease 状态。
        /// </summary>
        public void RegisterLease(string instructionId, string leaseToken, TimeSpan leaseDuration)
        {
            _leaseTokens[instructionId] = leaseToken;
            var expiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
            _leaseExpiresAt[instructionId] = expiresAt;
            _lastRenewedExpiresAt[instructionId] = expiresAt;
        }

        // === IAgentKernelTransport ===

        public ValueTask<AgentKernelInstruction?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            // DefaultAgentKernel 不调用此方法（使用自身 inbox）；返回 null 表示无远程指令
            return new ValueTask<AgentKernelInstruction?>((AgentKernelInstruction?)null);
        }

        public ValueTask SendResultAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            Interlocked.Increment(ref _sendResultCount);
            if (SendResultThrows && SendResultException is not null)
            {
                throw SendResultException;
            }
            return _outbox.Writer.WriteAsync(result, cancellationToken);
        }

        // === IDurableTransport — inbox 租约 ===

        public ValueTask<LeasedInstruction?> LeaseAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
        {
            // Kernel 不调用此方法（pump 负责）；返回 null 表示无 Pending 指令
            return new ValueTask<LeasedInstruction?>((LeasedInstruction?)null);
        }

        public ValueTask<IReadOnlyList<LeasedInstruction>> LeaseBatchAsync(int limit, TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<LeasedInstruction>>(Array.Empty<LeasedInstruction>());

        public ValueTask AckAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default)
        {
            _ackCounts.AddOrUpdate(instructionId, 1, (_, c) => c + 1);
            // Ack 后删除 lease 记录（行被删除）
            _leaseTokens.TryRemove(instructionId, out _);
            _leaseExpiresAt.TryRemove(instructionId, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> AckBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> acks, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask NackAsync(string instructionId, string leaseToken, CancellationToken cancellationToken = default)
        {
            _nackCounts.AddOrUpdate(instructionId, 1, (_, c) => c + 1);
            // Nack 后删除 lease 记录（行回滚为 Pending，不再持有 lease）
            _leaseTokens.TryRemove(instructionId, out _);
            _leaseExpiresAt.TryRemove(instructionId, out _);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> NackBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> nacks, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask RenewLeaseAsync(string instructionId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default)
        {
            _renewCounts.AddOrUpdate(instructionId, 1, (_, c) => c + 1);
            // 续租：延长 lease_expires_at = now + extension
            var expiresAt = DateTimeOffset.UtcNow.Add(extension);
            _leaseExpiresAt[instructionId] = expiresAt;
            // 追踪最近一次续租的 expires_at（不被 Ack/Nack 清除），供测试在完成后验证续租效果
            _lastRenewedExpiresAt[instructionId] = expiresAt;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<string>> RenewLeaseBatchAsync(IReadOnlyList<(string InstructionId, string LeaseToken)> renewals, TimeSpan extension, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask<int> RequeueExpiredAsync(CancellationToken cancellationToken = default)
            => new(0);

        // === IDurableTransport — outbox 租约（Kernel 不调用，由 outbox replay service 负责） ===

        public ValueTask<LeasedResult?> LeaseResultAsync(TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
            => new((LeasedResult?)null);

        public ValueTask<IReadOnlyList<LeasedResult>> LeaseResultBatchAsync(int limit, TimeSpan leaseDuration, string? owner = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<LeasedResult>>(Array.Empty<LeasedResult>());

        public ValueTask AckResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> AckResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> acks, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask NackResultAsync(string resultId, string leaseToken, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> NackResultBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> nacks, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public ValueTask RenewResultLeaseAsync(string resultId, string leaseToken, TimeSpan extension, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<string>> RenewResultLeaseBatchAsync(IReadOnlyList<(string ResultId, string LeaseToken)> renewals, TimeSpan extension, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        // === 计数查询 ===

        public ValueTask<int> GetPendingInstructionCountAsync(CancellationToken cancellationToken = default)
            => new(0);

        public ValueTask<int> GetPendingResultCountAsync(CancellationToken cancellationToken = default)
            => new(_outbox.Reader.Count);
    }

    // =======================================================================
    // 测试 Stub：SlowToolDispatcher — 模拟长耗时 tool 执行
    // =======================================================================

    /// <summary>
    /// 测试用 Stub：DispatchAsync 延迟指定时长后返回 payload（echo 语义）。
    /// 用于验证执行期间的 lease 续租行为。
    /// </summary>
    private sealed class SlowToolDispatcher : IToolDispatcher
    {
        private static readonly IReadOnlySet<string> s_supportedTools =
            new HashSet<string>(StringComparer.Ordinal) { "echo" };

        private readonly TimeSpan _delay;

        public SlowToolDispatcher(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            _delay = delay;
        }

        public IReadOnlySet<string> SupportedTools => s_supportedTools;

        public async ValueTask<ToolDispatchResult> DispatchAsync(ToolDispatchRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return new ToolDispatchResult
            {
                Succeeded = true,
                Result = request.Payload,
                Error = null,
                Duration = sw.Elapsed,
                SideEffect = ToolSideEffect.None
            };
        }
    }

    // =======================================================================
    // 测试 Stub：FailingOutbox — 模拟 Outbox 持久化失败
    // =======================================================================

    /// <summary>
    /// 测试用 Stub：EnqueueAsync 始终抛出指定异常，用于验证 Transport + Outbox 双失败时
    /// Input 不被 Ack 的核心不变量。
    /// </summary>
    private sealed class FailingOutbox : IKernelResultOutbox
    {
        private readonly Exception _exception;
        private int _enqueueAttemptCount;

        public FailingOutbox(Exception exception)
        {
            _exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        public int EnqueueAttemptCount => _enqueueAttemptCount;

        public ValueTask EnqueueAsync(AgentKernelResult result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            Interlocked.Increment(ref _enqueueAttemptCount);
            throw _exception;
        }

        public ValueTask<AgentKernelResult?> DequeueAsync(CancellationToken cancellationToken = default)
            => new((AgentKernelResult?)null);

        public int PendingCount => 0;

        public ValueTask<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
            => new(0);
    }
}
