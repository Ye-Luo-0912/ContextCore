using System.Collections.Concurrent;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service;
using ContextCore.Service.Endpoints;
using ContextCore.Service.Security;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContextCore.Tests;

// ===========================================================================
// Scheduler Claim Lease + Admission 边界验收测试（P0-6 / P0-7 / P0-8）
//
// 覆盖：
// 1. 迁移契约：v58→v59 迁移步骤注册（0006_agent_run_claim_lease）+ 基线 DDL
//    claim 列（claim_owner / claim_token / claim_expires_at）+ 幂等 ALTER；
// 2. 状态机：PendingAdmission → Queued/AdmissionRejected、Queued → Claimed、
//    Claimed → Running、AdmissionRejected 为终态（Claimed → Queued 非法——释放由 store 层完成）；
// 3. Claim 契约：TryClaimSingleAsync 写入完整 Claim Lease（owner/token/expiry）、
//    重复领取返回 null、ReleaseClaimAsync 按 claim_token fencing、ClaimPendingBatchAsync
//    永不领取 Created/PendingAdmission/AdmissionRejected、过期 Claim 可重新领取；
// 4. 端点 Admission 语义：配额失败 → 429 + AdmissionRejected（Claimer 不可领取）；
//    配额成功 → 201（Claimed 并执行到终态）；队列满 → 202（释放 Claim 回 Queued）；
//    Claim 被 Claimer 抢先 → 202（本地不重复入队）。
// ===========================================================================

[TestClass]
[TestCategory("R30")]
public sealed class R30Y_SchedulerClaimAdmissionTests
{
    private const string Ws = "ws-claim";

    // ── 1. 迁移契约 ──────────────────────────────────────────────────────

    /// <summary>
    /// 验证：v58→v59 Scheduler Claim Lease 迁移步骤已注册且元数据正确。
    /// </summary>
    [TestMethod]
    public void MigrationStepRegistry_ClaimLease_DeclaresV58ToV59()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationAgentRunClaimLease>()
            .Single();

        Assert.AreEqual("0006_agent_run_claim_lease", step.MigrationId);
        Assert.AreEqual("cc-schema-v58", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v59", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[] { PostgresMigrationStage.Online, PostgresMigrationStage.ConstraintValidate },
            step.Stages.ToArray(),
            "v58→v59 应为 Online（补列 + Created→Queued 转换）+ ConstraintValidate（无约束变更）两阶段。");
    }

    /// <summary>
    /// 验证：BuildMigrationSql 的 agent_runs 基线 DDL 包含 Scheduler Claim Lease 三列，
    /// 且包含对已有表的幂等 ALTER（旧库升级路径）。
    /// </summary>
    [TestMethod]
    public void MigrationSql_AgentRuns_ContainsClaimLeaseColumns()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false,
            TablePrefix = "cc_"
        });

        StringAssert.Contains(sql, "claim_owner text NULL", "基线 DDL 应含 claim_owner 列（Scheduler Claim 持有者）。");
        StringAssert.Contains(sql, "claim_token text NULL", "基线 DDL 应含 claim_token 列（Release/接管 fencing）。");
        StringAssert.Contains(sql, "claim_expires_at timestamptz NULL", "基线 DDL 应含 claim_expires_at 列（领取过期时间）。");
        StringAssert.Contains(sql, "claim_attempt integer NOT NULL DEFAULT 0", "基线 DDL 应含 claim_attempt 列（领取尝试计数）。");
        StringAssert.Contains(sql, "ADD COLUMN IF NOT EXISTS claim_owner text NULL",
            "基线 DDL 应含 v58→v59 幂等 ALTER（已有表补列）。");
        StringAssert.Contains(sql, "ADD COLUMN IF NOT EXISTS claim_attempt integer NOT NULL DEFAULT 0",
            "基线 DDL 应含 v62→v63 幂等 ALTER（已有表补领取尝试计数列）。");
    }

    /// <summary>验证：v62→v63 迁移声明（agent_runs 追加 claim_attempt 列）。</summary>
    [TestMethod]
    public void MigrationStepRegistry_ClaimAttempt_DeclaresV62ToV63()
    {
        var step = PostgresMigrationStepRegistry.Steps
            .OfType<PostgresMigrationAgentRunClaimAttempt>()
            .Single();

        Assert.AreEqual("0010_agent_run_claim_attempt", step.MigrationId);
        Assert.AreEqual("cc-schema-v62", step.FromSchemaVersion);
        Assert.AreEqual("cc-schema-v63", step.ToSchemaVersion);
        CollectionAssert.AreEqual(
            new[] { PostgresMigrationStage.Online },
            step.Stages.ToArray(),
            "v62→v63 应为 Online（补列，非破坏性）。");
    }

    // ── 2. 状态机 ────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：P0-6/P0-8 新增状态流转合法、AdmissionRejected 为终态、
    /// Claimed → Queued 非法（释放只能由 store 层的 ReleaseClaimAsync 完成，不走状态机）。
    /// </summary>
    [TestMethod]
    public void StateMachine_AdmissionAndClaimFlow_ValidTransitions()
    {
        // P0-6 Admission：PendingAdmission → Queued（配额通过）/ AdmissionRejected（配额失败）
        AgentRunStateMachine.ValidateTransition(AgentRunState.PendingAdmission, AgentRunState.Queued);
        AgentRunStateMachine.ValidateTransition(AgentRunState.PendingAdmission, AgentRunState.AdmissionRejected);

        // P0-8 Scheduler Claim：Queued → Claimed → Running（执行权确立）
        AgentRunStateMachine.ValidateTransition(AgentRunState.Queued, AgentRunState.Claimed);
        AgentRunStateMachine.ValidateTransition(AgentRunState.Claimed, AgentRunState.Running);
        AgentRunStateMachine.ValidateTransition(AgentRunState.Running, AgentRunState.ContextBuilding);

        // Claim 过期闭环：Claimed → ClaimExpired（显式失效）→ Claimed（其他节点重领）
        AgentRunStateMachine.ValidateTransition(AgentRunState.Claimed, AgentRunState.ClaimExpired);
        AgentRunStateMachine.ValidateTransition(AgentRunState.ClaimExpired, AgentRunState.Claimed);

        // 本地调度：Claimed → ScheduledLocally（入队成功即消费 Claim）→ Running（出队执行）
        // / Queued（节点崩溃后由 Recovery Worker 回退重新调度）。
        AgentRunStateMachine.ValidateTransition(AgentRunState.Claimed, AgentRunState.ScheduledLocally);
        AgentRunStateMachine.ValidateTransition(AgentRunState.ScheduledLocally, AgentRunState.Running);
        AgentRunStateMachine.ValidateTransition(AgentRunState.ScheduledLocally, AgentRunState.Queued);

        // 终态：AdmissionRejected 不可再推进；Claimed → Queued 非法（释放是 store 层操作）
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(AgentRunState.AdmissionRejected),
            "AdmissionRejected 应为终态（配额失败的 Run 永不进入调度队列）。");
        Assert.IsFalse(AgentRunStateMachine.IsTerminalState(AgentRunState.ClaimExpired),
            "ClaimExpired 不是终态（可被其他节点重新领取）。");
        Assert.ThrowsException<InvalidOperationException>(
            () => AgentRunStateMachine.ValidateTransition(AgentRunState.AdmissionRejected, AgentRunState.Queued));
        Assert.ThrowsException<InvalidOperationException>(
            () => AgentRunStateMachine.ValidateTransition(AgentRunState.Claimed, AgentRunState.Queued));
        Assert.ThrowsException<InvalidOperationException>(
            () => AgentRunStateMachine.ValidateTransition(AgentRunState.ClaimExpired, AgentRunState.Running),
            "ClaimExpired 不能直接执行——必须先被重新领取（Claimed）。");
        Assert.ThrowsException<InvalidOperationException>(
            () => AgentRunStateMachine.ValidateTransition(AgentRunState.ScheduledLocally, AgentRunState.Claimed),
            "ScheduledLocally 的 Claim 已消费——不能直接重领，必须先回退 Queued 重新调度。");
    }

    // ── 3. Claim 契约（忠实模拟 PostgresAgentRunStore 语义的 InMemory 持久化 store）────

    /// <summary>
    /// 验证：TryClaimSingleAsync 把 Queued 领取为 Claimed，并写入完整 Claim Lease
    /// （claim_owner / claim_token / claim_expires_at）——Scheduler Claim 真正落库。
    /// </summary>
    [TestMethod]
    public async Task ClaimSingle_ClaimsQueued_WritesClaimLease()
    {
        var store = new ClaimAwareInMemoryRunStore(new InMemoryAgentRunStore());
        var run = await CreateQueuedRunAsync(store, "claim-1").ConfigureAwait(false);

        var claimed = await store.TryClaimSingleAsync(Ws, run.RunId, "node-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        Assert.IsNotNull(claimed, "Queued Run 应可被领取。");
        Assert.AreEqual(AgentRunState.Claimed, claimed!.State, "领取后状态应为 Claimed。");
        Assert.AreEqual("node-1", claimed.ClaimOwner, "应写入 Claim 持有者。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(claimed.ClaimToken), "应写入唯一 Claim token。");
        Assert.IsNotNull(claimed.ClaimExpiresAtUtc, "应写入 Claim 过期时间。");
        Assert.IsTrue(claimed.ClaimExpiresAtUtc > DateTimeOffset.UtcNow, "Claim 过期时间应在未来。");
        Assert.AreEqual(1, claimed.ClaimAttempt, "首次领取 ClaimAttempt 应为 1。");

        var persisted = await store.GetAsync(Ws, run.RunId).ConfigureAwait(false);
        Assert.AreEqual(AgentRunState.Claimed, persisted!.State, "领取后存储中的状态应为 Claimed。");
    }

    /// <summary>
    /// 验证：已领取（Claimed）的 Run 再次领取返回 null（Scheduler Claim 单持有者）。
    /// </summary>
    [TestMethod]
    public async Task ClaimSingle_AlreadyClaimed_ReturnsNull()
    {
        var store = new ClaimAwareInMemoryRunStore(new InMemoryAgentRunStore());
        var run = await CreateQueuedRunAsync(store, "claim-2").ConfigureAwait(false);

        var first = await store.TryClaimSingleAsync(Ws, run.RunId, "node-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        var second = await store.TryClaimSingleAsync(Ws, run.RunId, "node-2", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        Assert.IsNotNull(first, "首次领取应成功。");
        Assert.IsNull(second, "已持有 Claim 的 Run 不得被其他节点重复领取。");
    }

    /// <summary>
    /// 验证：ReleaseClaimAsync 按 claim_token fencing——错误 token 释放失败，
    /// 正确 token 释放成功并回 Queued（其他节点可重新领取）。
    /// </summary>
    [TestMethod]
    public async Task ReleaseClaim_FencedByToken_OnlyOwnerReleases()
    {
        var store = new ClaimAwareInMemoryRunStore(new InMemoryAgentRunStore());
        var run = await CreateQueuedRunAsync(store, "claim-3").ConfigureAwait(false);
        var claimed = await store.TryClaimSingleAsync(Ws, run.RunId, "node-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        // 错误 token（过期节点）→ 释放失败
        var wrongToken = await store.ReleaseClaimAsync(Ws, run.RunId, "stale-token").ConfigureAwait(false);
        Assert.IsFalse(wrongToken, "claim_token 不匹配时不得释放（过期节点不得释放新持有者的 claim）。");
        var stillClaimed = await store.GetAsync(Ws, run.RunId).ConfigureAwait(false);
        Assert.AreEqual(AgentRunState.Claimed, stillClaimed!.State, "释放失败后应保持 Claimed。");

        // 正确 token → 释放成功，回 Queued
        var released = await store.ReleaseClaimAsync(Ws, run.RunId, claimed!.ClaimToken!).ConfigureAwait(false);
        Assert.IsTrue(released, "持有者使用正确 claim_token 应释放成功。");
        var backToQueued = await store.GetAsync(Ws, run.RunId).ConfigureAwait(false);
        Assert.AreEqual(AgentRunState.Queued, backToQueued!.State, "释放后应回 Queued，等待其他节点领取。");

        // 回 Queued 后可重新领取
        var reClaimed = await store.TryClaimSingleAsync(Ws, run.RunId, "node-2", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        Assert.IsNotNull(reClaimed, "释放后其他节点应可重新领取。");
    }

    /// <summary>
    /// 验证：ClaimPendingBatchAsync 只领取 Queued；Created / PendingAdmission /
    /// AdmissionRejected 永不进入 Claimer 候选集（P0-6 Admission 边界）。
    /// </summary>
    [TestMethod]
    public async Task ClaimBatch_NeverClaimsCreatedPendingAdmissionOrAdmissionRejected()
    {
        var store = new ClaimAwareInMemoryRunStore(new InMemoryAgentRunStore());

        var created = await CreateRunAsync(store, "never-created", AgentRunState.Created).ConfigureAwait(false);
        var pendingAdmission = await CreateRunAsync(store, "never-pending", AgentRunState.PendingAdmission).ConfigureAwait(false);
        var rejected = await CreateRunAsync(store, "never-rejected", AgentRunState.AdmissionRejected).ConfigureAwait(false);
        var queued = await CreateQueuedRunAsync(store, "claim-batch-1").ConfigureAwait(false);

        var claimed = await store.ClaimPendingBatchAsync(
            100, 100, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), "claimer-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        Assert.AreEqual(1, claimed.Count, "仅 Queued Run 应被领取。");
        Assert.AreEqual(queued.RunId, claimed[0].RunId, "被领取的应是 Queued Run。");
        Assert.AreEqual(AgentRunState.Claimed, claimed[0].State);
        Assert.IsNotNull(claimed[0].ClaimToken);

        Assert.AreEqual(AgentRunState.Created, (await store.GetAsync(Ws, created.RunId).ConfigureAwait(false))!.State,
            "Created 不得被领取（v59 迁移后 Created 只属于 InMemory/FileSystem provider）。");
        Assert.AreEqual(AgentRunState.PendingAdmission, (await store.GetAsync(Ws, pendingAdmission.RunId).ConfigureAwait(false))!.State,
            "PendingAdmission 不得被领取（配额判定前不得进入可调度状态）。");
        Assert.AreEqual(AgentRunState.AdmissionRejected, (await store.GetAsync(Ws, rejected.RunId).ConfigureAwait(false))!.State,
            "AdmissionRejected 不得被领取（配额失败终态）。");
    }

    /// <summary>
    /// 验证：Claimed 且 Claim 未过期 → 不重新领取；Claim 过期（节点领取后崩溃）→ 重新领取（新 token）。
    /// </summary>
    [TestMethod]
    public async Task ClaimBatch_ExpiredClaim_Reclaimable_UnexpiredNot()
    {
        var store = new ClaimAwareInMemoryRunStore(new InMemoryAgentRunStore());
        var run = await CreateQueuedRunAsync(store, "claim-expiry").ConfigureAwait(false);
        var claimed = await store.TryClaimSingleAsync(Ws, run.RunId, "node-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        Assert.IsNotNull(claimed);

        // 未过期：批次领取不得重复领取
        var unexpired = await store.ClaimPendingBatchAsync(
            100, 100, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), "claimer-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        Assert.AreEqual(0, unexpired.Count, "Claim 未过期时不得重复领取（单持有者）。");

        // 模拟节点崩溃后 Claim 过期 → 其他节点重新领取（claim_token 轮换）
        store.ExpireClaim(Ws, run.RunId);
        var reclaimed = await store.ClaimPendingBatchAsync(
            100, 100, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), "claimer-2", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        Assert.AreEqual(1, reclaimed.Count, "Claim 过期后应可重新领取（崩溃恢复路径）。");
        Assert.AreEqual(AgentRunState.Claimed, reclaimed[0].State);
        Assert.AreNotEqual(claimed!.ClaimToken, reclaimed[0].ClaimToken, "重新领取应轮换 claim_token（fencing）。");
        Assert.AreEqual(2, reclaimed[0].ClaimAttempt, "过期重领后 ClaimAttempt 应为 2（首次领取 1 + 接管 1）。");
    }

    /// <summary>
    /// 验证：ClaimExpired 状态可被直接领取（TryClaimSingleAsync 认 ClaimExpired），
    /// 且领取后 ClaimAttempt +1——Claim 过期闭环 Claimed → ClaimExpired → Claimed。
    /// </summary>
    [TestMethod]
    public async Task ClaimSingle_ClaimExpired_ReclaimableWithAttemptIncrement()
    {
        var store = new ClaimAwareInMemoryRunStore(new InMemoryAgentRunStore());
        var run = await CreateQueuedRunAsync(store, "claim-expired-direct").ConfigureAwait(false);

        // 直接构造 ClaimExpired 状态（等价于 Postgres 前置标记的结果）
        await store.TransitionStateAsync(Ws, run.RunId, AgentRunState.Queued, AgentRunState.Claimed)
            .ConfigureAwait(false);
        await store.TransitionStateAsync(Ws, run.RunId, AgentRunState.Claimed, AgentRunState.ClaimExpired)
            .ConfigureAwait(false);

        var reclaimed = await store.TryClaimSingleAsync(Ws, run.RunId, "node-2", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);

        Assert.IsNotNull(reclaimed, "ClaimExpired 状态应可被重新领取。");
        Assert.AreEqual(AgentRunState.Claimed, reclaimed!.State, "重领后应回到 Claimed。");
        Assert.AreEqual(1, reclaimed.ClaimAttempt, "从 ClaimExpired 领取 ClaimAttempt 应为 1。");
    }

    // ── 4. 端点 Admission 语义（P0-6 / P0-7）─────────────────────────────

    /// <summary>
    /// 验证：配额耗尽 → 429，且 Run 推进为 AdmissionRejected（终态）——
    /// Claimer 无法领取它，Admission 边界不再失效（配额拒绝的 Run 永远不会被执行）。
    /// </summary>
    [TestMethod]
    public async Task Endpoint_QuotaExhausted_Returns429_RunInAdmissionRejected()
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
        await quotaService.ReserveAsync(Ws, "res-1", 100, 0).ConfigureAwait(false);
        await using var harness = await EndpointHarness.CreateAsync(securityOptions, quotaService).ConfigureAwait(false);

        var (status, body) = await CreateRunAsync(harness, maxTokens: 100).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status429TooManyRequests, status, "配额耗尽应返回 429。");
        StringAssert.Contains(body, "workspace_quota_exhausted");

        // Run 已持久化为 AdmissionRejected 终态（保留行作审计），Claimer 永不领取
        var runs = await harness.RunStore.ListByStateAsync(AgentRunState.AdmissionRejected).ConfigureAwait(false);
        Assert.AreEqual(1, runs.Count, "配额失败的 Run 应持久化为 AdmissionRejected（审计）。");
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(runs[0].State));

        var claimed = await harness.RunStore.ClaimPendingBatchAsync(
            100, 100, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), "claimer-1", TimeSpan.FromSeconds(60))
            .ConfigureAwait(false);
        Assert.AreEqual(0, claimed.Count, "AdmissionRejected 不得被 Claimer 领取（Admission 边界生效）。");
    }

    /// <summary>
    /// 验证：配额充足 → 201（已持久化并成功排入执行队列），Run 经
    /// PendingAdmission → Queued → Claimed → Running 执行到终态。
    /// </summary>
    [TestMethod]
    public async Task Endpoint_QuotaOk_Returns201_ClaimedAndExecutesToTerminal()
    {
        var securityOptions = new SecurityOptions
        {
            Quota = new WorkspaceQuotaOptions
            {
                Enabled = true,
                WorkspaceLimits = new Dictionary<string, WorkspaceQuotaLimit>
                {
                    [Ws] = new() { MaxTokens = 10000, MaxCostUsd = 0, Period = "01:00:00" }
                }
            }
        };
        var quotaService = new InMemoryWorkspaceQuotaService(securityOptions, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await using var harness = await EndpointHarness.CreateAsync(securityOptions, quotaService).ConfigureAwait(false);

        var (status, body) = await CreateRunAsync(harness, maxTokens: 100).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status201Created, status, "配额充足且入队成功应返回 201。");
        using var doc = JsonDocument.Parse(body);
        var runId = doc.RootElement.GetProperty("runId").GetString()!;

        // 执行到终态（Scheduler Claim → Execution Lease 交接后 Actor 全新启动）
        var terminal = await WaitForTerminalAsync(harness.RunStore, Ws, runId, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Assert.IsNotNull(terminal, "Run 应在超时前执行到终态。");
        Assert.IsTrue(AgentRunStateMachine.IsTerminalState(terminal!.State),
            $"Run 应执行到终态，实际状态 {terminal.State}。");

        // 配额已预留（reservationId = runId，预留只锁定容量不计入消耗）
        var quota = await quotaService.GetQuotaAsync(Ws).ConfigureAwait(false);
        Assert.AreEqual(100, quota.ReservedTokens, "创建 Run 后应预留配额。");
        Assert.AreEqual(0, quota.TokensUsed, "预留不计入已消耗。");
    }

    /// <summary>
    /// 验证：本地队列饱和 → 202（已持久化、等待后台调度），且 Scheduler Claim
    /// 被释放（回 Queued，其他节点/下周期可接管）——不再与 429 混用语义（P0-7）。
    /// </summary>
    [TestMethod]
    public async Task Endpoint_QueueFull_Returns202_ReleasesClaim()
    {
        var securityOptions = new SecurityOptions { Quota = new WorkspaceQuotaOptions { Enabled = false } };
        var quotaService = new InMemoryWorkspaceQuotaService(securityOptions, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await using var harness = await EndpointHarness.CreateAsync(
            securityOptions, quotaService, channelCapacity: 1, workerCount: 1, blockModelCalls: true).ConfigureAwait(false);

        // Run A：worker 拾取后阻塞在 transport（占住唯一 worker）
        var runA = BuildRun("队列满测试 A");
        await harness.RunStore.CreateAsync(runA).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runA, CancellationToken.None).ConfigureAwait(false)).Status);
        await harness.WaitForTransportCallAsync().ConfigureAwait(false);

        // Run B：填满唯一队列槽位（worker 忙，B 排队）
        var runB = BuildRun("队列满测试 B");
        await harness.RunStore.CreateAsync(runB).ConfigureAwait(false);
        Assert.AreEqual(AgentRunEnqueueStatus.Accepted,
            (await harness.Host.TryEnqueueAsync(runB, CancellationToken.None).ConfigureAwait(false)).Status);

        // Run C（经端点持久化路径）：入队 → QueueFull → 202 + 释放 Claim
        var (status, body) = await CreateRunAsync(harness).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status202Accepted, status,
            "队列饱和时应返回 202（已持久化、等待后台调度），而非 429（请求未持久化）。");
        using var doc = JsonDocument.Parse(body);
        var runCId = doc.RootElement.GetProperty("runId").GetString()!;

        var runC = await harness.RunStore.GetAsync(Ws, runCId).ConfigureAwait(false);
        Assert.IsNotNull(runC);
        Assert.AreEqual(AgentRunState.Queued, runC!.State,
            "队列满时 Scheduler Claim 应被释放，Run 回 Queued（其他节点/下周期接管）。");
        Assert.IsNull(runC.ClaimToken, "释放后 Claim token 应清空。");
        Assert.IsNull(runC.ClaimOwner, "释放后 Claim 持有者应清空。");

        // 释放阻塞 transport 让 worker 排空，避免 dispose 悬挂
        harness.Release();
        await WaitForTerminalAsync(harness.RunStore, runA.WorkspaceId, runA.RunId, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        await WaitForTerminalAsync(harness.RunStore, runB.WorkspaceId, runB.RunId, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    /// <summary>
    /// 验证：Scheduler Claim 已被 Claimer 抢先取得时，端点返回 202（Run 已持久化、
    /// 由持有 Claim 的节点/后台调度接管），本地不重复入队（避免双调度真源）。
    /// </summary>
    [TestMethod]
    public async Task Endpoint_ClaimTakenByClaimer_Returns202()
    {
        var securityOptions = new SecurityOptions { Quota = new WorkspaceQuotaOptions { Enabled = false } };
        var quotaService = new InMemoryWorkspaceQuotaService(securityOptions, NullLogger<InMemoryWorkspaceQuotaService>.Instance);
        await using var harness = await EndpointHarness.CreateAsync(securityOptions, quotaService).ConfigureAwait(false);

        // 模拟 Claimer 抢先：单点领取被拒绝（另一节点已持有 claim）
        harness.RunStore.ClaimSingleDenied = true;

        var (status, body) = await CreateRunAsync(harness).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status202Accepted, status,
            "Claim 已被抢占时返回 202（Run 已持久化，等待后台调度接管）。");
        using var doc = JsonDocument.Parse(body);
        var runId = doc.RootElement.GetProperty("runId").GetString()!;

        var run = await harness.RunStore.GetAsync(Ws, runId).ConfigureAwait(false);
        Assert.IsNotNull(run);
        Assert.AreEqual(AgentRunState.Queued, run!.State,
            "Claim 抢占时 Run 保持 Queued（由 Claimer 负责领取并入队）。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────

    private static async Task<AgentRun> CreateQueuedRunAsync(ClaimAwareInMemoryRunStore store, string task)
        => await CreateRunAsync(store, task, AgentRunState.Queued).ConfigureAwait(false);

    private static async Task<AgentRun> CreateRunAsync(
        ClaimAwareInMemoryRunStore store, string task, AgentRunState state)
    {
        var run = BuildRun(task) with { State = state };
        await store.CreateAsync(run).ConfigureAwait(false);
        return run;
    }

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-r30y",
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
        },
        MaxRetries = 0
    };

    private static async Task<(int Status, string Body)> CreateRunAsync(
        EndpointHarness harness, string? idempotencyKey = null, int? maxTokens = null)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = harness.RequestServices,
            Response = { Body = new MemoryStream() }
        };
        var request = new CreateRunRequest
        {
            Task = "Admission 语义测试任务",
            WorkspaceId = Ws,
            IdempotencyKey = idempotencyKey,
            CostBudget = maxTokens is null ? null : new CostBudgetRequest { MaxTokens = maxTokens.Value }
        };

        var result = await AgentExecutionEndpoints.CreateAgentRunHandlerAsync(
            request, harness.RunStore, harness.Host, workspaceContextAccessor: null!, httpContext, CancellationToken.None)
            .ConfigureAwait(false);
        await result.ExecuteAsync(httpContext).ConfigureAwait(false);

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        return (httpContext.Response.StatusCode, body);
    }

    private static async Task<AgentRun?> WaitForTerminalAsync(
        ClaimAwareInMemoryRunStore store, string workspaceId, string runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var run = await store.GetAsync(workspaceId, runId).ConfigureAwait(false);
            if (run is not null && AgentRunStateMachine.IsTerminalState(run.State))
            {
                return run;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        return await store.GetAsync(workspaceId, runId).ConfigureAwait(false);
    }

    /// <summary>
    /// 忠实模拟 PostgresAgentRunStore 的 Scheduler Claim 语义（P0-8）：
    /// 内嵌 InMemoryAgentRunStore + 原子 claim 状态机（Queued→Claimed 带 claim 字段、
    /// ReleaseClaim 按 claim_token fencing、Claimed 过期可重新领取、批次领取只认
    /// Queued/过期-Claimed/可重试 Failed/RecoveryDependencyUnavailable）。
    /// 供端点与状态机测试验证 Admission 边界（P0-6）与 429/202/201 语义（P0-7）。
    /// </summary>
    private sealed class ClaimAwareInMemoryRunStore : IPersistentAgentRunStore
    {
        private readonly InMemoryAgentRunStore _inner;
        private readonly IWorkspaceQuotaService? _quotaService;
        private readonly TimeSpan _claimDuration;
        private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _claims = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _claimAttempts = new(StringComparer.Ordinal);

        /// <summary>模拟 Claimer 已抢先持有 Claim（TryClaimSingleAsync 一律返回 null）。</summary>
        public bool ClaimSingleDenied { get; set; }

        public ClaimAwareInMemoryRunStore(InMemoryAgentRunStore inner, IWorkspaceQuotaService? quotaService = null, TimeSpan? claimDuration = null)
        {
            _inner = inner;
            _quotaService = quotaService;
            _claimDuration = claimDuration ?? TimeSpan.FromSeconds(60);
        }

        private static string Key(string workspaceId, string runId) => workspaceId + "|" + runId;

        /// <summary>测试钩子：模拟节点崩溃后 Claim 过期（ClaimPendingBatchAsync 可重新领取）。</summary>
        public void ExpireClaim(string workspaceId, string runId)
        {
            var key = Key(workspaceId, runId);
            if (_claims.TryGetValue(key, out var claim))
            {
                _claims[key] = (claim.Token, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
            }
        }

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

            // 配额启用且注入配额服务时按预留语义执行：容量不足 → AdmissionRejected + QuotaDenied。
            if (quotaAdmission is not null && _quotaService is not null)
            {
                var reservation = await _quotaService.ReserveAsync(
                    run.WorkspaceId, run.RunId, quotaAdmission.Tokens, quotaAdmission.CostUsd, cancellationToken)
                    .ConfigureAwait(false);
                if (!reservation.Allowed)
                {
                    try
                    {
                        await _inner.TransitionStateAsync(
                            run.WorkspaceId, run.RunId,
                            AgentRunState.PendingAdmission, AgentRunState.AdmissionRejected, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // 已被并发路径推进 → 以现有状态为准
                    }
                    return new AgentRunAdmitResult
                    {
                        Created = true,
                        WasExisting = false,
                        QuotaDenied = true,
                        QuotaFailureReason = reservation.FailureReason,
                        Run = created.Run with
                        {
                            State = AgentRunState.AdmissionRejected,
                            FinishedAt = DateTimeOffset.UtcNow
                        }
                    };
                }
            }

            // 预留成功（或配额未启用）→ 推进 Queued。
            try
            {
                await _inner.TransitionStateAsync(
                    run.WorkspaceId, run.RunId,
                    AgentRunState.PendingAdmission, AgentRunState.Queued, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 已被并发路径推进 → 以现有状态为准
            }
            return new AgentRunAdmitResult
            {
                Created = true,
                WasExisting = false,
                Run = created.Run with { State = AgentRunState.Queued, UpdatedAt = DateTimeOffset.UtcNow }
            };
        }

        public ValueTask TransitionStateAsync(
            string workspaceId, string runId, AgentRunState expectedState, AgentRunState newState,
            CancellationToken cancellationToken = default, string? leaseToken = null, long? fencingToken = null)
            => _inner.TransitionStateAsync(workspaceId, runId, expectedState, newState, cancellationToken, leaseToken, fencingToken);

        public ValueTask UpdateAsync(AgentRun run, CancellationToken cancellationToken = default)
            => _inner.UpdateAsync(run, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListBySessionAsync(string workspaceId, string sessionId, CancellationToken cancellationToken = default)
            => _inner.ListBySessionAsync(workspaceId, sessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentRun>> ListByStateAsync(
            AgentRunState state, int take = 100,
            DateTimeOffset? afterUpdatedAt = null, string? afterRunId = null,
            CancellationToken cancellationToken = default)
            => _inner.ListByStateAsync(state, take, afterUpdatedAt, afterRunId, cancellationToken);

        public async ValueTask<AgentRun?> TryClaimSingleAsync(
            string workspaceId, string runId, string claimOwner, TimeSpan claimDuration,
            CancellationToken cancellationToken = default)
        {
            if (ClaimSingleDenied)
            {
                return null; // 模拟 Claimer 已抢先持有
            }

            var now = DateTimeOffset.UtcNow;
            var run = await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
            if (run is null || (run.State != AgentRunState.Queued && run.State != AgentRunState.ClaimExpired))
            {
                return null; // 非 Queued / ClaimExpired / 不存在 → 不可领取
            }
            if (run.NextRetryAtUtc is not null && run.NextRetryAtUtc > now)
            {
                return null; // 退避门未通过
            }

            var token = Guid.NewGuid().ToString("N");
            var expiresAt = now + (claimDuration > TimeSpan.Zero ? claimDuration : _claimDuration);
            try
            {
                // 原子 CAS：Queued/ClaimExpired → Claimed（模拟 Postgres UPDATE ... WHERE state IN (21, 24)）
                await _inner.TransitionStateAsync(
                    workspaceId, runId, run.State, AgentRunState.Claimed, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return null; // 已被并发领取
            }

            var claimed = (await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false))!;
            var attempt = (_claimAttempts.TryGetValue(Key(workspaceId, runId), out var prior) ? prior : 0) + 1;
            _claimAttempts[Key(workspaceId, runId)] = attempt;
            _claims[Key(workspaceId, runId)] = (token, expiresAt);
            return claimed with
            {
                State = AgentRunState.Claimed,
                ClaimOwner = claimOwner,
                ClaimToken = token,
                ClaimExpiresAtUtc = expiresAt,
                ClaimAttempt = attempt
            };
        }

        public async ValueTask<bool> ReleaseClaimAsync(
            string workspaceId, string runId, string claimToken,
            CancellationToken cancellationToken = default)
        {
            var key = Key(workspaceId, runId);
            if (!_claims.TryGetValue(key, out var claim) || !string.Equals(claim.Token, claimToken, StringComparison.Ordinal))
            {
                return false; // claim_token 不匹配（过期节点）→ fencing 拒绝
            }

            var run = await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
            if (run is null || run.State != AgentRunState.Claimed)
            {
                _claims.TryRemove(key, out _);
                return false;
            }

            try
            {
                await _inner.TransitionStateAsync(
                    workspaceId, runId, AgentRunState.Claimed, AgentRunState.Queued, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                _claims.TryRemove(key, out _);
                return false;
            }

            _claims.TryRemove(key, out _);
            return true;
        }

        public async ValueTask<AgentRun> ConsumeClaimAsync(
            string workspaceId, string runId, string? expectedClaimToken, string? expectedClaimOwner,
            string? executionLeaseToken, long? executionFencingToken,
            CancellationToken cancellationToken = default)
        {
            var key = Key(workspaceId, runId);
            var run = await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new InvalidOperationException($"Run 不存在：{workspaceId}/{runId}。");
            }
            if (run.State != AgentRunState.Claimed)
            {
                throw new InvalidOperationException(
                    $"Claim 消费失败：期望 Claimed，实际 {run.State}。");
            }
            var claim = _claims.TryGetValue(key, out var existing) ? existing : default;
            if (claim.Token is null
                || !string.Equals(claim.Token, expectedClaimToken, StringComparison.Ordinal)
                || !string.Equals(run.ClaimOwner, expectedClaimOwner, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Scheduler Claim 已被接管（claim_token/owner 不匹配）。");
            }
            if (claim.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("Scheduler Claim 已过期。");
            }

            await _inner.TransitionStateAsync(
                workspaceId, runId, AgentRunState.Claimed, AgentRunState.Running, cancellationToken)
                .ConfigureAwait(false);
            _claims.TryRemove(key, out _);

            var consumed = (await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false))!;
            return consumed with
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
            var key = Key(workspaceId, runId);
            var run = await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
            if (run is null)
            {
                throw new InvalidOperationException($"Run 不存在：{workspaceId}/{runId}。");
            }
            if (run.State != AgentRunState.Claimed)
            {
                throw new InvalidOperationException(
                    $"本地调度失败：期望 Claimed，实际 {run.State}。");
            }
            var claim = _claims.TryGetValue(key, out var existing) ? existing : default;
            if (claim.Token is null
                || !string.Equals(claim.Token, expectedClaimToken, StringComparison.Ordinal)
                || !string.Equals(run.ClaimOwner, expectedClaimOwner, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Scheduler Claim 已被接管（claim_token/owner 不匹配）。");
            }
            if (claim.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("Scheduler Claim 已过期。");
            }

            await _inner.TransitionStateAsync(
                workspaceId, runId, AgentRunState.Claimed, AgentRunState.ScheduledLocally, cancellationToken)
                .ConfigureAwait(false);
            _claims.TryRemove(key, out _);

            var scheduled = (await _inner.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false))!;
            return scheduled with
            {
                State = AgentRunState.ScheduledLocally,
                ClaimOwner = null,
                ClaimToken = null,
                ClaimExpiresAtUtc = null
            };
        }

        public async ValueTask<IReadOnlyList<AgentRun>> ClaimPendingBatchAsync(
            int take, int perWorkspace, TimeSpan retryBackoffBase, TimeSpan retryBackoffMax,
            string claimOwner, TimeSpan claimDuration,
            CancellationToken cancellationToken = default)
        {
            if (take <= 0)
            {
                return Array.Empty<AgentRun>();
            }

            var claimed = new List<AgentRun>();
            var now = DateTimeOffset.UtcNow;

            // 1. Queued（退避门通过）→ 领取
            var queued = await _inner.ListByStateAsync(AgentRunState.Queued, take: 1000, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var run in queued)
            {
                if (claimed.Count >= take)
                {
                    break;
                }
                var single = await TryClaimSingleAsync(
                    run.WorkspaceId, run.RunId, claimOwner, claimDuration, cancellationToken).ConfigureAwait(false);
                if (single is not null)
                {
                    claimed.Add(single);
                }
            }

            // 2. Claimed 且 Claim 已过期（节点领取后崩溃）→ 先标记 ClaimExpired（显式失效状态，
            //    与 Postgres 前置标记语义一致），再由 ClaimExpired 重新领取（claim_attempt +1）
            var claimedRuns = await _inner.ListByStateAsync(AgentRunState.Claimed, take: 1000, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var run in claimedRuns)
            {
                if (claimed.Count >= take)
                {
                    break;
                }
                var key = Key(run.WorkspaceId, run.RunId);
                if (_claims.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
                {
                    continue; // 未过期 → 不重复领取
                }
                try
                {
                    await _inner.TransitionStateAsync(
                        run.WorkspaceId, run.RunId, AgentRunState.Claimed, AgentRunState.ClaimExpired, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue; // 已被并发接管
                }
                var single = await TryClaimSingleAsync(
                    run.WorkspaceId, run.RunId, claimOwner, claimDuration, cancellationToken).ConfigureAwait(false);
                if (single is not null)
                {
                    claimed.Add(single);
                }
            }

            // 3. RetryPending 且配置了重试且未耗尽（退避门通过）→ 重置为 Queued 再领取
            //    （事件流清空等重试语义由 Postgres 实现承担，此处仅验证 Claim 契约）
            var retryPending = await _inner.ListByStateAsync(AgentRunState.RetryPending, take: 1000, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var run in retryPending)
            {
                if (claimed.Count >= take)
                {
                    break;
                }
                if (run.MaxRetries <= 0 || run.RetryCount >= run.MaxRetries)
                {
                    continue;
                }
                if (run.NextRetryAtUtc is not null && run.NextRetryAtUtc > now)
                {
                    continue; // 退避门未通过
                }
                try
                {
                    await _inner.TransitionStateAsync(
                        run.WorkspaceId, run.RunId, AgentRunState.RetryPending, AgentRunState.Queued, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                var single = await TryClaimSingleAsync(
                    run.WorkspaceId, run.RunId, claimOwner, claimDuration, cancellationToken).ConfigureAwait(false);
                if (single is not null)
                {
                    claimed.Add(single);
                }
            }

            // 4. RecoveryDependencyUnavailable（退避门通过）→ 领取
            var recovery = await _inner.ListByStateAsync(AgentRunState.RecoveryDependencyUnavailable, take: 1000, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (var run in recovery)
            {
                if (claimed.Count >= take)
                {
                    break;
                }
                if (run.NextRetryAtUtc is not null && run.NextRetryAtUtc > now)
                {
                    continue;
                }
                var single = await TryClaimSingleAsync(
                    run.WorkspaceId, run.RunId, claimOwner, claimDuration, cancellationToken).ConfigureAwait(false);
                if (single is not null)
                {
                    claimed.Add(single);
                }
            }

            return claimed;
        }

        public ValueTask<IReadOnlyList<AgentRun>> DeadLetterExhaustedRunsAsync(
            int take, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());
    }

    /// <summary>端点测试夹具：ClaimAware 持久化 store + 真实 AgentKernelHost。</summary>
    private sealed class EndpointHarness : IAsyncDisposable
    {
        public ClaimAwareInMemoryRunStore RunStore { get; }
        public AgentKernelHost Host { get; }
        public ServiceProvider RequestServices { get; }
        private readonly BlockingModelTransport? _blockingTransport;

        private EndpointHarness(
            ClaimAwareInMemoryRunStore runStore,
            AgentKernelHost host,
            ServiceProvider requestServices,
            BlockingModelTransport? blockingTransport)
        {
            RunStore = runStore;
            Host = host;
            RequestServices = requestServices;
            _blockingTransport = blockingTransport;
        }

        public async Task WaitForTransportCallAsync()
        {
            if (_blockingTransport is null)
            {
                throw new InvalidOperationException("本夹具未启用阻塞 transport。");
            }
            await _blockingTransport.WaitForCallAsync().ConfigureAwait(false);
        }

        public void Release() => _blockingTransport?.Complete();

        public static async Task<EndpointHarness> CreateAsync(
            SecurityOptions securityOptions,
            IWorkspaceQuotaService quotaService,
            int channelCapacity = 8,
            int workerCount = 2,
            bool blockModelCalls = false)
        {
            var inner = new InMemoryAgentRunStore();
            var runStore = new ClaimAwareInMemoryRunStore(inner, quotaService);
            var eventStore = new InMemoryAgentRunEventStore(inner);
            var blocking = blockModelCalls ? new BlockingModelTransport() : null;
            IAgentModelTransport transport = (IAgentModelTransport?)blocking ?? new DeterministicAgentModelTransport();

            var hostServices = new ServiceCollection();
            hostServices.AddSingleton<IAgentRunStore>(runStore);
            hostServices.AddSingleton<IPersistentAgentRunStore>(runStore);
            hostServices.AddSingleton<IAgentRunEventStore>(eventStore);
            hostServices.AddSingleton<IToolDispatcher>(new EchoToolDispatcher());
            hostServices.AddSingleton<IAgentModelTransport>(transport);
            hostServices.AddSingleton<AgentKernelHost>();
            hostServices.AddSingleton(new AgentHostOptions
            {
                ChannelCapacity = channelCapacity,
                WorkerCount = workerCount,
                DrainTimeout = TimeSpan.FromSeconds(5)
            });
            hostServices.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
            var hostProvider = hostServices.BuildServiceProvider();
            var host = hostProvider.GetRequiredService<AgentKernelHost>();

            var requestServices = new ServiceCollection();
            requestServices.AddSingleton(securityOptions);
            requestServices.AddSingleton<IWorkspaceQuotaService>(quotaService);
            requestServices.AddLogging();

            return new EndpointHarness(runStore, host, requestServices.BuildServiceProvider(), blocking);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>transport stub：首次调用阻塞在 TCS，直到测试主动 Complete。</summary>
    private sealed class BlockingModelTransport : IAgentModelTransport
    {
        private readonly TaskCompletionSource<AgentModelResponse> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => CallCore(cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => CallCore(cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallCore(cancellationToken);

        private ValueTask<AgentModelResponse> CallCore(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            _firstCall.TrySetResult(true);
            return new ValueTask<AgentModelResponse>(_gate.Task);
        }

        public Task WaitForCallAsync() => _firstCall.Task;

        public void Complete()
        {
            _gate.TrySetResult(new AgentModelResponse
            {
                Content = "已处理任务：阻塞测试完成。",
                ToolCalls = Array.Empty<AgentToolCallRequest>(),
                IsFinalAnswer = true,
                TokensConsumed = 0,
                Duration = TimeSpan.Zero
            });
        }
    }
}
