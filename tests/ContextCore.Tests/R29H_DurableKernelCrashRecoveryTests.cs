using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// R29-Hard-Gate：Durable Kernel Crash Recovery 验收测试
//
// 目标：验证 PostgresDurableTransport 的租约模型在崩溃恢复场景下的核心不变量：
//   1. Lease 过期 → RequeueExpired → 指令可被新实例重新租约（无丢失）
//   2. Ack 后指令永久删除（不会因 lease 过期被重新投递 → exactly-once）
//   3. 错误 lease token 的 Ack 抛异常（多实例并发安全）
//   4. Nack 回滚为 Pending，立即可重新租约（快速重试）
//   5. RenewLease 延长 lease_expires_at（长耗时任务续租）
//   6. Outbox 结果同样具备崩溃恢复能力
//   7. 跨实例 RequeueExpiredAsync 同时回收 inbox + outbox 过期租约
//
// 设计原则：
//   - 使用真实 PostgresDurableTransport（非 mock）+ Testcontainers Postgres。
//   - Docker/Postgres 不可用时用 Assert.Inconclusive 跳过；不修改测试逻辑。
//   - 每个测试使用独立的 tablePrefix 避免数据交叉污染。
//   - 所有异步测试使用 CancellationTokenSource 超时防止挂起。
//   - 中文注释。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Durable-CrashRecovery")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class R29H_DurableKernelCrashRecoveryTests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // 直接尝试启动容器（与 PostgresDurableTransportTests 一致），
        // 避免 IsDockerAvailableAsync 在 Windows named-pipe Docker Desktop 上误判。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R29H_DurableKernelCrashRecoveryTests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool ShouldSkip => _connectionString is null;

    /// <summary>构建测试用 Postgres 基础设施（factory + migrationRunner + serializer）。</summary>
    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    private static AgentKernelInstruction MakeInstruction(string instructionId, string payload = "payload")
        => new()
        {
            InstructionId = instructionId,
            Kind = AgentKernelInstructionKind.Execute,
            Payload = payload
        };

    private static AgentKernelResult MakeResult(string instructionId, string output = "ok")
        => new()
        {
            InstructionId = instructionId,
            Succeeded = true,
            Output = output
        };

    // =======================================================================
    // 测试 1：Lease 过期 → RequeueExpired → 指令可被新实例重新租约
    //         （核心崩溃恢复不变量：指令不丢失）
    // =======================================================================

    [TestMethod]
    public async Task Lease_ExpiresAndRequeues_InstructionCanBeReLeased()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明崩溃恢复通过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr1_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            await transport.SubmitAsync(MakeInstruction("cr-instr-1", "crash-recovery-1"));

            // 实例A：短租约（1 秒）→ 模拟拿到指令后立即"崩溃"（不 Ack）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var leasedA = await transport.LeaseAsync(TimeSpan.FromSeconds(1), owner: "instance-A", cts.Token);
            Assert.IsNotNull(leasedA, "实例A 应能租约到指令。");
            Assert.AreEqual("cr-instr-1", leasedA!.Instruction.InstructionId);
            var tokenA = leasedA.LeaseToken;

            // 等待 lease 过期（1.5 秒 > 1 秒 lease duration）
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cts.Token);

            // 实例A 的 lease 已过期；RequeueExpiredAsync 应回滚 1 条
            var requeued = await transport.RequeueExpiredAsync(cts.Token);
            Assert.AreEqual(1, requeued,
                "RequeueExpiredAsync 应回收 1 条过期 lease（inbox）。");

            // 实例B：应能重新租约同一条指令
            var leasedB = await transport.LeaseAsync(TimeSpan.FromMinutes(1), owner: "instance-B", cts.Token);
            Assert.IsNotNull(leasedB, "实例B 应能重新租约已回滚的指令。");
            Assert.AreEqual("cr-instr-1", leasedB!.Instruction.InstructionId);
            Assert.AreNotEqual(tokenA, leasedB.LeaseToken,
                "新租约的 lease_token 应不同于实例A（避免误 Ack）。");

            // 实例B 完成处理 → Ack
            await transport.AckAsync("cr-instr-1", leasedB.LeaseToken, cts.Token);

            // Ack 后指令已删除；再次 RequeueExpired 应返回 0（无可回收）
            var requeuedAfterAck = await transport.RequeueExpiredAsync(cts.Token);
            Assert.AreEqual(0, requeuedAfterAck,
                "Ack 后指令已删除，RequeueExpiredAsync 应返回 0。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 2：跨实例崩溃恢复 — 实例A 崩溃后，实例B 接管并完成处理
    //         （验证 HA 场景：进程崩溃不丢指令）
    // =======================================================================

    [TestMethod]
    public async Task CrossInstance_CrashBeforeAck_NewInstanceRequeuesAndCompletes()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明跨实例崩溃恢复通过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr2_");
        try
        {
            // ── 模拟实例A：租约指令后立即"崩溃"（不 Ack，丢弃 transport 实例）──
            var transportA = new PostgresDurableTransport(factory, serializer, migrationRunner);
            await transportA.SubmitAsync(MakeInstruction("cross-crash-1", "payload-A"));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var leasedA = await transportA.LeaseAsync(TimeSpan.FromSeconds(1), owner: "instance-A", cts.Token);
            Assert.IsNotNull(leasedA, "实例A 应能租约到指令。");
            // 模拟崩溃：transportA 不再使用，不调用 AckAsync

            // 等待 lease 过期
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cts.Token);

            // ── 模拟实例B：新 transport 实例（同 DB）──
            var transportB = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 实例B 调用 RequeueExpiredAsync 回收实例A 的过期租约
            var requeued = await transportB.RequeueExpiredAsync(cts.Token);
            Assert.IsTrue(requeued >= 1,
                $"实例B 的 RequeueExpiredAsync 应回收至少 1 条过期 lease（实际 {requeued}）。");

            // 实例B 重新租约同一条指令
            var leasedB = await transportB.LeaseAsync(TimeSpan.FromMinutes(1), owner: "instance-B", cts.Token);
            Assert.IsNotNull(leasedB, "实例B 应能重新租约已回滚的指令。");
            Assert.AreEqual("cross-crash-1", leasedB!.Instruction.InstructionId);
            Assert.AreEqual("payload-A", leasedB.Instruction.Payload,
                "重新租约的指令 payload 应保持原值（持久化未损坏）。");

            // 实例B 完成处理 → Ack
            await transportB.AckAsync("cross-crash-1", leasedB.LeaseToken, cts.Token);

            // 验证指令已删除（Pending=0）
            var pendingCount = await transportB.GetPendingInstructionCountAsync(cts.Token);
            Assert.AreEqual(0, pendingCount,
                "Ack 后 inbox Pending 指令数应为 0。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 3：Ack 后指令永久删除，不会因 lease 过期被重新投递
    //         （exactly-once 语义：成功处理的指令不被重复消费）
    // =======================================================================

    [TestMethod]
    public async Task Ack_AfterProcessing_PreventsReprocessing_ExactlyOnce()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr3_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await transport.SubmitAsync(MakeInstruction("exactly-once-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromSeconds(5), owner: "worker", cts.Token);
            Assert.IsNotNull(leased);

            // 正常处理 → Ack
            await transport.AckAsync("exactly-once-1", leased!.LeaseToken, cts.Token);

            // RequeueExpired 应返回 0（无过期 lease；已 Ack 的指令已删除）
            var requeued = await transport.RequeueExpiredAsync(cts.Token);
            Assert.AreEqual(0, requeued,
                "Ack 后 RequeueExpiredAsync 应返回 0（指令已删除，无过期 lease）。");

            // 后续 LeaseAsync 应返回 null（无 Pending 指令）
            var reLeased = await transport.LeaseAsync(TimeSpan.FromMinutes(1), cancellationToken: cts.Token);
            Assert.IsNull(reLeased,
                "Ack 后再次 LeaseAsync 应返回 null（指令已被删除，不会重复投递）。");

            // Pending 计数应为 0
            var pendingCount = await transport.GetPendingInstructionCountAsync(cts.Token);
            Assert.AreEqual(0, pendingCount,
                "Ack 后 Pending 计数应为 0。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 4：错误 lease token 的 Ack 抛异常，原 token 仍可 Ack
    //         （多实例并发安全：实例B 无法误 Ack 实例A 持有的指令）
    // =======================================================================

    [TestMethod]
    public async Task Ack_WithWrongToken_ThrowsAndPreservesLease()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr4_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await transport.SubmitAsync(MakeInstruction("token-safety-1"));
            var leased = await transport.LeaseAsync(TimeSpan.FromMinutes(1), owner: "instance-A", cts.Token);
            Assert.IsNotNull(leased);
            var correctToken = leased!.LeaseToken;
            var wrongToken = "deadbeef-wrong-token-not-matching";

            // 错误 token Ack 应抛 InvalidOperationException
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                async () => await transport.AckAsync("token-safety-1", wrongToken, cts.Token),
                "错误 lease token 的 Ack 应抛 InvalidOperationException。");

            // 原 token 仍能成功 Ack（lease 未被破坏）
            await transport.AckAsync("token-safety-1", correctToken, cts.Token);

            // 验证指令已删除
            var pendingCount = await transport.GetPendingInstructionCountAsync(cts.Token);
            Assert.AreEqual(0, pendingCount,
                "原 token Ack 后指令应已删除。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 5：Nack 回滚为 Pending，立即可重新租约（快速重试）
    // =======================================================================

    [TestMethod]
    public async Task Nack_ReturnsInstructionToPendingForImmediateRetry()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr5_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await transport.SubmitAsync(MakeInstruction("nack-retry-1"));
            var leased1 = await transport.LeaseAsync(TimeSpan.FromMinutes(1), owner: "worker-1", cts.Token);
            Assert.IsNotNull(leased1);

            // Nack 回滚（注意：Nack 后会有指数退避 next_attempt_at，立即可重试）
            await transport.NackAsync("nack-retry-1", leased1!.LeaseToken, cts.Token);

            // 重新租约应能拿到同一条指令（Nack 把 state 回滚为 Pending）
            // 注意：Nack 时 attempt_count + 1，next_attempt_at = now + backoff；
            // 首次 Nack 的 backoff = 1 秒，等待 1 秒后可重新租约。
            await Task.Delay(TimeSpan.FromMilliseconds(1100), cts.Token);
            var leased2 = await transport.LeaseAsync(TimeSpan.FromMinutes(1), owner: "worker-2", cts.Token);
            Assert.IsNotNull(leased2, "Nack 后应能重新租约同一条指令（快速重试）。");
            Assert.AreEqual("nack-retry-1", leased2!.Instruction.InstructionId);
            Assert.AreNotEqual(leased1.LeaseToken, leased2.LeaseToken,
                "新租约应使用新的 lease_token。");

            // 第二次处理成功 → Ack
            await transport.AckAsync("nack-retry-1", leased2.LeaseToken, cts.Token);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 6：RenewLease 延长 lease_expires_at（长耗时任务续租）
    //         验证续租后的 expires_at 在原 expires_at 之后
    // =======================================================================

    [TestMethod]
    public async Task RenewLease_ExtendsExpiresAt_PreventsExpirationDuringLongProcessing()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr6_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await transport.SubmitAsync(MakeInstruction("renew-1"));
            // 短租约（2 秒）
            var leased = await transport.LeaseAsync(TimeSpan.FromSeconds(2), owner: "long-worker", cts.Token);
            Assert.IsNotNull(leased);
            var originalExpiresAt = leased!.LeaseExpiresAt;
            var token = leased.LeaseToken;

            // 等待 500ms 后续租 5 秒（新 expires_at 应远在原 expires_at 之后）
            await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
            await transport.RenewLeaseAsync("renew-1", token, TimeSpan.FromSeconds(5), cts.Token);

            // 续租后等待 2.5 秒（已超过原 lease 的 2 秒，但续租后 lease 仍有效）
            await Task.Delay(TimeSpan.FromMilliseconds(2500), cts.Token);

            // RequeueExpired 应返回 0（lease 未过期，未被回滚）
            var requeued = await transport.RequeueExpiredAsync(cts.Token);
            Assert.AreEqual(0, requeued,
                "续租后 lease 未过期，RequeueExpiredAsync 应返回 0。");

            // 原 token 仍能 Ack（lease 仍由当前 worker 持有）
            await transport.AckAsync("renew-1", token, cts.Token);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 7：Outbox 结果的崩溃恢复 — Lease 过期 → RequeueExpired → 可重新租约
    //         （对称于 inbox 测试 1，验证 outbox 路径同样具备崩溃恢复）
    // =======================================================================

    [TestMethod]
    public async Task Outbox_LeaseExpiresAndRequeues_ResultCanBeReLeased()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr7_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await transport.SendResultAsync(MakeResult("outbox-crash-1", "result-payload"));

            // 实例A 短租约（1 秒）→ 模拟"崩溃"（不 AckResult）
            var leasedA = await transport.LeaseResultAsync(TimeSpan.FromSeconds(1), owner: "replayer-A", cts.Token);
            Assert.IsNotNull(leasedA, "实例A 应能租约到结果。");
            Assert.AreEqual("outbox-crash-1", leasedA!.Result.InstructionId);
            Assert.AreEqual("result-payload", leasedA.Result.Output);
            var tokenA = leasedA.LeaseToken;

            // 等待 lease 过期
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cts.Token);

            // RequeueExpiredAsync 回收过期 lease（inbox + outbox 总和 ≥ 1）
            var requeued = await transport.RequeueExpiredAsync(cts.Token);
            Assert.IsTrue(requeued >= 1,
                $"RequeueExpiredAsync 应回收至少 1 条过期 lease（实际 {requeued}）。");

            // 实例B 应能重新租约同一条结果
            var leasedB = await transport.LeaseResultAsync(TimeSpan.FromMinutes(1), owner: "replayer-B", cts.Token);
            Assert.IsNotNull(leasedB, "实例B 应能重新租约已回滚的结果。");
            Assert.AreEqual("outbox-crash-1", leasedB!.Result.InstructionId);
            Assert.AreEqual("result-payload", leasedB.Result.Output,
                "重新租约的结果 payload 应保持原值。");
            Assert.AreNotEqual(tokenA, leasedB.LeaseToken,
                "新租约的 lease_token 应不同于实例A。");

            // 实例B 完成 → AckResult
            await transport.AckResultAsync(leasedB.ResultId, leasedB.LeaseToken, cts.Token);

            // Ack 后 outbox Pending 计数应为 0
            var pendingResultCount = await transport.GetPendingResultCountAsync(cts.Token);
            Assert.AreEqual(0, pendingResultCount,
                "AckResult 后 outbox Pending 结果数应为 0。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 8：RequeueExpiredAsync 同时回收 inbox + outbox 过期租约
    //         （返回值为两者之和，覆盖 HA 单点故障场景）
    // =======================================================================

    [TestMethod]
    public async Task RequeueExpired_ReleasesBothInboxAndOutboxExpiredLeases()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr8_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            // 提交 2 条 inbox 指令 + 1 条 outbox 结果，全部租约（短 lease）
            await transport.SubmitAsync(MakeInstruction("both-1"));
            await transport.SubmitAsync(MakeInstruction("both-2"));
            await transport.SendResultAsync(MakeResult("both-r-1"));

            var leasedInstr1 = await transport.LeaseAsync(TimeSpan.FromSeconds(1), owner: "w1", cts.Token);
            var leasedInstr2 = await transport.LeaseAsync(TimeSpan.FromSeconds(1), owner: "w2", cts.Token);
            var leasedResult = await transport.LeaseResultAsync(TimeSpan.FromSeconds(1), owner: "r1", cts.Token);
            Assert.IsNotNull(leasedInstr1);
            Assert.IsNotNull(leasedInstr2);
            Assert.IsNotNull(leasedResult);

            // 等待全部 lease 过期
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cts.Token);

            // RequeueExpiredAsync 应回收全部 3 条过期 lease（2 inbox + 1 outbox）
            var requeued = await transport.RequeueExpiredAsync(cts.Token);
            Assert.AreEqual(3, requeued,
                $"RequeueExpiredAsync 应回收 3 条过期 lease（2 inbox + 1 outbox），实际 {requeued}。");

            // 验证 inbox Pending 数 = 2（回滚后可重新租约）
            var pendingInstructions = await transport.GetPendingInstructionCountAsync(cts.Token);
            Assert.AreEqual(2, pendingInstructions,
                "回滚后 inbox Pending 指令数应为 2。");

            // 验证 outbox Pending 数 = 1
            var pendingResults = await transport.GetPendingResultCountAsync(cts.Token);
            Assert.AreEqual(1, pendingResults,
                "回滚后 outbox Pending 结果数应为 1。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 9：CrashRecovery 持久化跨进程可见 — 进程A 写入后崩溃，进程B 可见全部数据
    //         （验证 PG 表数据的持久性，与内存 transport 形成对比）
    // =======================================================================

    [TestMethod]
    public async Task CrashRecovery_NewTransportInstanceSeesPersistedPendingState()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr9_");
        try
        {
            // ── 进程A：写入 3 条 Pending 指令（不租约），然后"崩溃"──
            var transportA = new PostgresDurableTransport(factory, serializer, migrationRunner);
            await transportA.SubmitAsync(MakeInstruction("persist-1"));
            await transportA.SubmitAsync(MakeInstruction("persist-2"));
            await transportA.SubmitAsync(MakeInstruction("persist-3"));
            // 模拟崩溃：丢弃 transportA 实例

            // ── 进程B：新实例（同 DB）──
            var transportB = new PostgresDurableTransport(factory, serializer, migrationRunner);

            // 进程B 应能看到进程A 写入的 3 条 Pending 指令
            var pendingCount = await transportB.GetPendingInstructionCountAsync();
            Assert.AreEqual(3, pendingCount,
                "进程B 应看到进程A 持久化的 3 条 Pending 指令。");

            // 进程B 按 FIFO 顺序租约并 Ack 全部指令
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var leased1 = await transportB.LeaseAsync(TimeSpan.FromMinutes(1), owner: "B", cts.Token);
            var leased2 = await transportB.LeaseAsync(TimeSpan.FromMinutes(1), owner: "B", cts.Token);
            var leased3 = await transportB.LeaseAsync(TimeSpan.FromMinutes(1), owner: "B", cts.Token);

            Assert.IsNotNull(leased1);
            Assert.IsNotNull(leased2);
            Assert.IsNotNull(leased3);

            // 验证 FIFO 顺序（按 created_at ASC）
            Assert.AreEqual("persist-1", leased1!.Instruction.InstructionId);
            Assert.AreEqual("persist-2", leased2!.Instruction.InstructionId);
            Assert.AreEqual("persist-3", leased3!.Instruction.InstructionId);

            // Ack 全部
            await transportB.AckAsync("persist-1", leased1.LeaseToken, cts.Token);
            await transportB.AckAsync("persist-2", leased2.LeaseToken, cts.Token);
            await transportB.AckAsync("persist-3", leased3.LeaseToken, cts.Token);

            // 全部 Ack 后 Pending = 0
            var finalPending = await transportB.GetPendingInstructionCountAsync(cts.Token);
            Assert.AreEqual(0, finalPending,
                "全部 Ack 后 Pending 指令数应为 0。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 10：LeaseAsync 在无 Pending 指令时返回 null（不阻塞）
    //         （验证 LeaseAsync 非阻塞语义，与破坏性 DELETE 出队区分）
    // =======================================================================

    [TestMethod]
    public async Task LeaseAsync_OnEmptyInbox_ReturnsNullImmediately()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("cr10_");
        try
        {
            var transport = new PostgresDurableTransport(factory, serializer, migrationRunner);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // 空 inbox → LeaseAsync 立即返回 null（不阻塞）
            var startTimestamp = DateTimeOffset.UtcNow;
            var leased = await transport.LeaseAsync(TimeSpan.FromSeconds(5), owner: "test", cts.Token);
            var elapsed = DateTimeOffset.UtcNow - startTimestamp;

            Assert.IsNull(leased, "空 inbox 时 LeaseAsync 应返回 null。");
            Assert.IsTrue(elapsed < TimeSpan.FromSeconds(2),
                $"空 inbox 时 LeaseAsync 应立即返回（实际耗时 {elapsed.TotalMilliseconds}ms，应 < 2s）。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }
}
