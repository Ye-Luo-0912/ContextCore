using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Hosting;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// Workspace 配额持久化（ProductionHA）验收测试
//
// 目标：配额真相源从单进程字典收敛到数据库，且准入与结算原子化：
// 1. AdmitRunAtomicallyAsync：配额预留 + Run 创建 + 推进 Queued 同一事务——
//    配额充足 → Queued + 预留落库；配额不足 → AdmissionRejected（审计）+ 拒绝；
//    配额未启用 → 直接 Queued；幂等重放 → 返回既有 Run；
// 2. PostgresWorkspaceQuotaService：Reserve / Release / Actualize 生命周期语义正确，
//    且状态跨服务实例持久（重启不丢失预留与已用量）；
// 3. 终态结算：Run 推进终态时写入结算 outbox（仅当预留存在），
//    结算 worker 消费后按终态执行 Actualize（执行类）或 Release（未执行类），exactly-once。
// ===========================================================================

[TestClass]
[TestCategory("Integration")]
[TestCategory("R30")]
public sealed class R30Z_WorkspaceQuotaDurabilityTests
{
    private const string Ws = "ws-quota-durable";

    // ── 1. 原子准入 ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task AdmitRunAtomically_QuotaOk_CreatesQueuedRun_AndReservesQuota()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "admit_ok_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                var result = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), BuildAdmission(100, 100), default);

                Assert.IsTrue(result.Created, "新 Run 应创建成功。");
                Assert.IsFalse(result.QuotaDenied, "配额充足不应拒绝。");
                Assert.AreEqual(AgentRunState.Queued, result.Run.State, "准入后应推进 Queued。");

                var stored = await store.GetAsync(Ws, result.Run.RunId, default);
                Assert.AreEqual(AgentRunState.Queued, stored!.State, "数据库状态应为 Queued。");

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(100, quota.ReservedTokens, "预留应落库（reservationId = runId）。");
                Assert.AreEqual(0, quota.TokensUsed, "预留只锁定容量，不计入已消耗。");

                var reservation = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}workspace_quota_reservations WHERE reservation_id = '{result.Run.RunId}';");
                Assert.AreEqual("1", reservation, "预留行应持久化。");
            }
        }
    }

    [TestMethod]
    public async Task AdmitRunAtomically_QuotaExhausted_ReturnsDenied_AndPersistsAdmissionRejected()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "admit_den_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);
                // 先占满配额。
                await quotaService.ReserveAsync(Ws, "other-res", 100, 0, default);

                var result = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), BuildAdmission(100, 100), default);

                Assert.IsTrue(result.Created, "配额失败的 Run 行应保留（审计）。");
                Assert.IsTrue(result.QuotaDenied, "配额不足应返回拒绝。");
                Assert.AreEqual(AgentRunState.AdmissionRejected, result.Run.State, "配额失败应推进 AdmissionRejected。");
                Assert.IsTrue(AgentRunStateMachine.IsTerminalState(result.Run.State));

                var stored = await store.GetAsync(Ws, result.Run.RunId, default);
                Assert.AreEqual(AgentRunState.AdmissionRejected, stored!.State, "AdmissionRejected 应持久化。");
                Assert.IsNotNull(stored.FinishedAt, "终态应记录 finished_at。");

                var reservation = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}workspace_quota_reservations WHERE reservation_id = '{result.Run.RunId}';");
                Assert.AreEqual("0", reservation, "配额失败的 Run 不应产生预留。");
            }
        }
    }

    [TestMethod]
    public async Task AdmitRunAtomically_QuotaDisabled_CreatesQueuedRun_WithoutReservation()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, _) = await ResolveAsync(container, "admit_dis_");
            await using (provider)
            {
                var result = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), quotaAdmission: null, default);

                Assert.IsTrue(result.Created, "配额未启用应创建成功。");
                Assert.AreEqual(AgentRunState.Queued, result.Run.State, "配额未启用直接推进 Queued。");

                var reservation = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}workspace_quota_reservations WHERE reservation_id = '{result.Run.RunId}';");
                Assert.AreEqual("0", reservation, "配额未启用不应产生预留。");
            }
        }
    }

    [TestMethod]
    public async Task AdmitRunAtomically_ExistingRun_ReturnsWasExisting()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, _) = await ResolveAsync(container, "admit_idem_");
            await using (provider)
            {
                var run = BuildPendingAdmissionRun(tokensUsed: 0);
                var first = await store.AdmitRunAtomicallyAsync(run, quotaAdmission: null, default);
                Assert.IsTrue(first.Created, "首次创建成功。");

                // 幂等重放：同一 run_id → 返回既有 Run，不重复创建。
                var replay = await store.AdmitRunAtomicallyAsync(run, quotaAdmission: null, default);
                Assert.IsFalse(replay.Created, "重放不应创建新 Run。");
                Assert.IsTrue(replay.WasExisting, "应命中既有 Run。");
                Assert.AreEqual(first.Run.RunId, replay.Run.RunId);

                var count = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}agent_runs WHERE run_id = '{run.RunId}';");
                Assert.AreEqual("1", count, "重放不应产生重复行。");
            }
        }
    }

    [TestMethod]
    public async Task AdmitRunAtomically_SameIdempotencyKey_ReturnsExistingRun()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, _) = await ResolveAsync(container, "admit_key_");
            await using (provider)
            {
                // 同一 idempotency_key、不同 run_id → 幂等重放命中既有 Run（事务内幂等键冲突路径）。
                var run1 = BuildPendingAdmissionRun(tokensUsed: 0) with { IdempotencyKey = "idem-key-1" };
                var run2 = BuildPendingAdmissionRun(tokensUsed: 0) with { IdempotencyKey = "idem-key-1" };

                var first = await store.AdmitRunAtomicallyAsync(run1, quotaAdmission: null, default);
                Assert.IsTrue(first.Created, "首次创建成功。");

                var replay = await store.AdmitRunAtomicallyAsync(run2, quotaAdmission: null, default);
                Assert.IsFalse(replay.Created, "同 idempotency_key 重放不应创建新 Run。");
                Assert.IsTrue(replay.WasExisting, "应按 idempotency_key 命中既有 Run。");
                Assert.AreEqual(first.Run.RunId, replay.Run.RunId, "重放应返回首次创建的 Run。");

                var count = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}agent_runs WHERE idempotency_key = 'idem-key-1';");
                Assert.AreEqual("1", count, "同 idempotency_key 不应产生重复行。");
            }
        }
    }

    // ── 2. 配额服务生命周期 + 持久化 ────────────────────────────────────

    [TestMethod]
    public async Task QuotaService_ReserveReleaseActualize_Roundtrip()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, _, quotaService) = await ResolveAsync(container, "quota_life_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                var reserve = await quotaService.ReserveAsync(Ws, "run-1", 60, 0, default);
                Assert.IsTrue(reserve.Allowed);
                Assert.AreEqual(60, reserve.UpdatedQuota.ReservedTokens);

                // 释放 → 容量退回。
                await quotaService.ReleaseAsync(Ws, "run-1", default);
                var afterRelease = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(0, afterRelease.ReservedTokens, "释放后预留应退回。");

                // 再预留 → 结算转正（按实际用量 40，多退 20）。
                await quotaService.ReserveAsync(Ws, "run-2", 60, 0, default);
                var actualize = await quotaService.ActualizeAsync(Ws, "run-2", 40, 0, default);
                Assert.IsTrue(actualize.Allowed);
                Assert.AreEqual(40, actualize.UpdatedQuota.TokensUsed, "结算按实际用量计入消耗。");
                Assert.AreEqual(0, actualize.UpdatedQuota.ReservedTokens, "结算后预留释放。");
                Assert.AreEqual(60, actualize.UpdatedQuota.RemainingTokens, "剩余容量 = 上限 - 实际消耗。");
            }
        }
    }

    [TestMethod]
    public async Task QuotaService_StateSurvivesServiceRestart()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var prefix = "quota_restart_";
            // 第一"代"服务：设置限额 + 预留。
            var (provider1, _, quotaService1) = await ResolveAsync(container, prefix);
            await using (provider1)
            {
                await quotaService1.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);
                await quotaService1.ReserveAsync(Ws, "run-1", 60, 0, default);
            }

            // 第二"代"服务（同数据库，模拟节点重启）：状态应保留。
            var (provider2, _, quotaService2) = await ResolveAsync(container, prefix);
            await using (provider2)
            {
                var quota = await quotaService2.GetQuotaAsync(Ws, default);
                Assert.AreEqual(60, quota.ReservedTokens, "重启后预留不丢失。");
                Assert.AreEqual(100, quota.MaxTokens, "重启后限额不丢失。");

                // 幂等预留跨节点有效：另一节点对同一 reservationId 预留 → 幂等成功不重复占容量。
                var again = await quotaService2.ReserveAsync(Ws, "run-1", 60, 0, default);
                Assert.IsTrue(again.Allowed, "跨节点幂等预留应成功。");
                Assert.AreEqual(60, again.UpdatedQuota.ReservedTokens, "幂等预留不重复占容量。");
            }
        }
    }

    // ── 2b. 周期轮转 / SetLimit 语义（P0-5）────────────────────────────

    /// <summary>
    /// 验证：周期轮转只清零"已用"，已预留必须按现存 reservation 行重新求和——
    /// 跨周期长 Run 的预留保留并继续计入新周期容量（过度放行阻断），
    /// 而非把 reserved 直接置 0。
    /// </summary>
    [TestMethod]
    public async Task PeriodRollover_KeepsActiveReservations_NoOverAdmission()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 周期轮转测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, _, quotaService) = await ResolveAsync(container, "quota_roll_");
            await using (provider)
            {
                // 短周期（1 秒）便于触发惰性轮转。
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromSeconds(1), default);

                // 周期 1：Run A 预留 80。
                var reserveA = await quotaService.ReserveAsync(Ws, "run-A", 80, 0, default);
                Assert.IsTrue(reserveA.Allowed);
                Assert.AreEqual(80, reserveA.UpdatedQuota.ReservedTokens);

                // 等待周期过期（惰性轮转在下次预留时触发）。
                await Task.Delay(TimeSpan.FromSeconds(1.5));

                // 周期 2：Run B 再预留 40 → 必须被拒：Run A 的 80 仍在（reserved 按 SUM 恢复），
                // 80 + 40 > 100。若旧实现把 reserved 置 0，这里会错误放行（过度放行）。
                var reserveB = await quotaService.ReserveAsync(Ws, "run-B", 40, 0, default);
                Assert.IsFalse(reserveB.Allowed, "周期轮转后 Run A 的预留必须保留并计入新周期容量（80+40>100 拒绝）。");

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(80, quota.ReservedTokens,
                    "轮转后 reserved 应按现存 reservation 行重新求和（=80），不得置 0。");
                Assert.AreEqual(0, quota.TokensUsed, "轮转后新周期已用归零。");
            }
        }
    }

    /// <summary>
    /// 验证：SetLimitAsync 只更新上限（max / period），绝不清零 usage 与 reservation——
    /// 它是"改上限"不是 Reset；旧实现把 usage/reserved 清零但保留 reservation 行，
    /// 同时产生过度放行与跨周期错误归属（不完整的 Reset）。
    /// </summary>
    [TestMethod]
    public async Task SetLimit_UpdatesLimitsOnly_DoesNotResetUsageOrReservation()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — SetLimit 语义测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, _, quotaService) = await ResolveAsync(container, "quota_setlim_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                // 制造消耗：Run A 结算 40（usage），Run B 预留 50（reservation）。
                await quotaService.ReserveAsync(Ws, "run-A", 40, 0, default);
                await quotaService.ActualizeAsync(Ws, "run-A", 30, 0, default);
                await quotaService.ReserveAsync(Ws, "run-B", 50, 0, default);
                var before = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(30, before.TokensUsed);
                Assert.AreEqual(50, before.ReservedTokens);

                // 修改上限（不换周期、不重置）→ usage / reservation 必须保留。
                await quotaService.SetLimitAsync(Ws, 200, 0, TimeSpan.FromHours(2), default);

                var after = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(200, after.MaxTokens, "上限应更新。");
                Assert.AreEqual(TimeSpan.FromHours(2), after.Period, "周期应更新。");
                Assert.AreEqual(30, after.TokensUsed, "SetLimit 不得清零已用（改上限 ≠ Reset）。");
                Assert.AreEqual(50, after.ReservedTokens, "SetLimit 不得清除预留（旧 reservation 行保留）。");

                // 预留容量仍然生效：Run B 的 50 继续占容量（200 - 30 - 50 = 120 剩余）。
                Assert.AreEqual(120, after.RemainingTokens, "SetLimit 后剩余容量 = 上限 - 已用 - 已预留。");
            }
        }
    }

    // ── 3. 终态结算 outbox ──────────────────────────────────────────────

    [TestMethod]
    public async Task TerminalTransition_WritesOutbox_AndSettlementActualizes()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_act_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                // 创建 Run（预留 100，实际执行消耗 40）。
                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 40), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                // 推进终态 Completed → 结算 outbox 写入（与状态转换同事务）。
                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Completed, default);

                var outboxCount = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}' AND status = 0;");
                Assert.AreEqual("1", outboxCount, "终态转换应写入待结算 outbox。");

                // 结算 worker 消费：Claim → Actualize（执行类终态）→ MarkProcessed。
                await RunSettlementWorkerOnceAsync(provider);

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(40, quota.TokensUsed, "结算应按实际用量转正。");
                Assert.AreEqual(0, quota.ReservedTokens, "结算后预留释放。");

                var processed = await QueryScalarAsync(
                    container, provider,
                    $"SELECT status FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("1", processed, "outbox 应标记为已结算。");
            }
        }
    }

    [TestMethod]
    public async Task TerminalTransition_Cancelled_SettlementActualizes()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_rel_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                // 取消前可能已产生消费（模拟已用 40k token）——不因终态名字而退回全部预留。
                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 40), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                // 取消 → Cancelled 终态 → outbox。
                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Cancelled, default);

                await RunSettlementWorkerOnceAsync(provider);

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(40, quota.TokensUsed, "取消类终态应按实际用量转正（不无条件退回）。");
                Assert.AreEqual(0, quota.ReservedTokens, "结算后预留释放。");
            }
        }
    }

    [TestMethod]
    public async Task TerminalTransition_WithoutReservation_NoOutbox()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, _) = await ResolveAsync(container, "settle_none_");
            await using (provider)
            {
                // 配额未启用（无预留）→ 终态转换不应写 outbox。
                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), quotaAdmission: null, default);
                Assert.IsTrue(admitted.Created);

                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Completed, default);

                var outboxCount = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("0", outboxCount, "无预留的 Run 终态不应写 outbox（无需结算）。");
            }
        }
    }

    [TestMethod]
    public async Task NonTerminalTransition_NoOutbox()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_nt_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);
                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), BuildAdmission(100, 100), default);

                // 非终态转换（Queued → Running）→ 不写 outbox。
                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Running, default);

                var outboxCount = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("0", outboxCount, "非终态转换不应写 outbox。");
            }
        }
    }

    [TestMethod]
    public async Task EventStoreAppendBatch_TerminalState_WritesOutbox_AndSettlementActualizes()
    {
        // Agent Actor 正常主路径：事件 + Run 状态 CAS + outbox 经 AppendBatchAsync 同事务提交，
        // 与 Run Store 的 TransitionStateAsync 保持一致的结算语义（仅预留存在才入队，exactly-once）。
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_evt_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                // 创建 Run（预留 100，实际执行消耗 40）。
                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 40), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);
                Assert.AreEqual(AgentRunState.Queued, admitted.Run.State);

                // 事件链（RunCreated → RunCompleted）随终态提交。
                var eventStore = provider.GetRequiredService<PostgresAgentRunEventStore>();
                var seq0 = AgentRunEventChain.BuildEvent(
                    admitted.Run.RunId, Ws, sequence: 0,
                    type: AgentRunEventType.RunCreated,
                    state: AgentRunState.Queued,
                    payload: """{"runId":"admit"}""",
                    prevChainHash: null);
                var seq1 = AgentRunEventChain.BuildEvent(
                    admitted.Run.RunId, Ws, sequence: 1,
                    type: AgentRunEventType.RunCompleted,
                    state: AgentRunState.Completed,
                    payload: """{"from":"Queued","to":"Completed"}""",
                    prevChainHash: seq0.ContentHash);

                var runStateUpdate = new AgentRunStateUpdate
                {
                    WorkspaceId = Ws,
                    RunId = admitted.Run.RunId,
                    ExpectedCurrentState = AgentRunState.Queued,
                    NewState = AgentRunState.Completed,
                    RunSnapshot = admitted.Run with
                    {
                        State = AgentRunState.Completed,
                        UpdatedAt = DateTimeOffset.UtcNow
                    }
                };
                await eventStore.AppendBatchAsync([seq0, seq1], runStateUpdate, null, null, default);

                var outboxCount = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}' AND status = 0;");
                Assert.AreEqual("1", outboxCount, "Agent 主路径（AppendBatchAsync）终态应写入待结算 outbox。");

                // 结算 worker 消费：Actualize（执行类终态）→ MarkProcessed。
                await RunSettlementWorkerOnceAsync(provider);

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(40, quota.TokensUsed, "结算应按实际用量转正。");
                Assert.AreEqual(0, quota.ReservedTokens, "结算后预留释放。");

                var processed = await QueryScalarAsync(
                    container, provider,
                    $"SELECT status FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("1", processed, "outbox 应标记为已结算。");
            }
        }
    }

    [TestMethod]
    public async Task Committer_TerminalCommit_WritesOutbox_AndSettlementActualizes()
    {
        // 提交器直测：AgentRunCommit 单事务提交（事件 + 状态 CAS + 结算 outbox），
        // 验证统一提交入口的终态语义（finished_at + outbox + 结算转正）与 Actor 主路径一致。
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_cmt_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 40), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                var committer = provider.GetRequiredService<IPersistentAgentRunCommitter>();
                var seq0 = AgentRunEventChain.BuildEvent(
                    admitted.Run.RunId, Ws, sequence: 0,
                    type: AgentRunEventType.RunCreated,
                    state: AgentRunState.Queued,
                    payload: """{"runId":"admit"}""",
                    prevChainHash: null);
                var seq1 = AgentRunEventChain.BuildEvent(
                    admitted.Run.RunId, Ws, sequence: 1,
                    type: AgentRunEventType.RunCompleted,
                    state: AgentRunState.Completed,
                    payload: """{"from":"Queued","to":"Completed"}""",
                    prevChainHash: seq0.ContentHash);

                var commit = new AgentRunCommit
                {
                    Key = new TenantRunKey(Ws, admitted.Run.RunId),
                    Events = new[] { seq0, seq1 },
                    ExpectedCurrentState = AgentRunState.Queued,
                    NewRunSnapshot = admitted.Run with
                    {
                        State = AgentRunState.Completed,
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    UsageSnapshot = admitted.Run.CostBudget
                };
                await committer.CommitAsync(commit, default);

                var outboxCount = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}' AND status = 0;");
                Assert.AreEqual("1", outboxCount, "提交器终态提交应写入待结算 outbox。");

                var stored = await store.GetAsync(Ws, admitted.Run.RunId, default);
                Assert.AreEqual(AgentRunState.Completed, stored!.State, "状态应 CAS 到 Completed。");
                Assert.IsNotNull(stored.FinishedAt, "终态应记录 finished_at。");

                var events = await provider.GetRequiredService<IAgentRunEventStore>().ReadAsync(Ws, admitted.Run.RunId, default);
                Assert.AreEqual(2, events.Count, "事件流应落库两事件。");

                // 结算 worker 消费后预留释放（提交器写入的 outbox 与 Run Store 路径同源）。
                await RunSettlementWorkerOnceAsync(provider);
                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(40, quota.TokensUsed, "结算应按实际用量转正。");
                Assert.AreEqual(0, quota.ReservedTokens, "结算后预留释放。");
            }
        }
    }

    [TestMethod]
    public async Task Committer_EventOnlyCommit_NoStateChange_NoOutbox()
    {
        // 纯事件提交（NewRunSnapshot null）：只插事件，不推进状态、不写 outbox。
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_cevt_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                var committer = provider.GetRequiredService<IPersistentAgentRunCommitter>();
                var seq0 = AgentRunEventChain.BuildEvent(
                    admitted.Run.RunId, Ws, sequence: 0,
                    type: AgentRunEventType.RunCreated,
                    state: AgentRunState.Queued,
                    payload: """{"runId":"admit"}""",
                    prevChainHash: null);

                var commit = new AgentRunCommit
                {
                    Key = new TenantRunKey(Ws, admitted.Run.RunId),
                    Events = new[] { seq0 }
                };
                await committer.CommitAsync(commit, default);

                var stored = await store.GetAsync(Ws, admitted.Run.RunId, default);
                Assert.AreEqual(AgentRunState.Queued, stored!.State, "纯事件提交不应推进状态。");

                var outboxCount = await QueryScalarAsync(
                    container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("0", outboxCount, "纯事件提交不应写 outbox。");

                var events = await provider.GetRequiredService<IAgentRunEventStore>().ReadAsync(Ws, admitted.Run.RunId, default);
                Assert.AreEqual(1, events.Count, "事件应落库。");
            }
        }
    }

    [TestMethod]
    public async Task Reservation_CompositeKey_IsolatesWorkspaces()
    {
        // 预留表复合主键 (workspace_id, reservation_id)：不同工作区使用相同预留 id
        // （即相同 run id）互不干扰——与 agent_runs 的 (workspace_id, run_id) 身份模型一致。
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "resv_key_");
            await using (provider)
            {
                const string wsA = "ws-resv-key-a";
                const string wsB = "ws-resv-key-b";
                const string sharedRunId = "run-same-id";
                await quotaService.SetLimitAsync(wsA, 100, 0, TimeSpan.FromHours(1), default);
                await quotaService.SetLimitAsync(wsB, 100, 0, TimeSpan.FromHours(1), default);

                var runA = BuildPendingAdmissionRun(tokensUsed: 0) with { WorkspaceId = wsA, RunId = sharedRunId };
                var runB = BuildPendingAdmissionRun(tokensUsed: 0) with { WorkspaceId = wsB, RunId = sharedRunId };

                var admittedA = await store.AdmitRunAtomicallyAsync(runA, BuildAdmission(100, 100), default);
                var admittedB = await store.AdmitRunAtomicallyAsync(runB, BuildAdmission(100, 100), default);
                Assert.IsTrue(admittedA.Created, "工作区 A 准入应成功。");
                Assert.IsTrue(admittedB.Created, "工作区 B 同 run id 准入应成功（复合键隔离）。");

                var quotaA = await quotaService.GetQuotaAsync(wsA, default);
                var quotaB = await quotaService.GetQuotaAsync(wsB, default);
                Assert.AreEqual(100, quotaA.ReservedTokens, "A 预留 100 应落库。");
                Assert.AreEqual(100, quotaB.ReservedTokens, "B 预留 100 应落库（与 A 互不覆盖）。");

                // 结算 A（Completed）不影响 B 的预留。
                await store.TransitionStateAsync(
                    wsA, sharedRunId, AgentRunState.Queued, AgentRunState.Completed, default);
                await RunSettlementWorkerOnceAsync(provider);

                quotaA = await quotaService.GetQuotaAsync(wsA, default);
                quotaB = await quotaService.GetQuotaAsync(wsB, default);
                Assert.AreEqual(0, quotaA.ReservedTokens, "A 结算后预留释放。");
                Assert.AreEqual(100, quotaB.ReservedTokens, "B 预留不受 A 结算影响。");
            }
        }
    }

    [TestMethod]
    public async Task Settlement_ExhaustedAttempts_StuckThenRecovers()
    {
        // 结算连续失败达到阈值后不放弃：转入卡住（低频重试闸门），闸门过后仍可被领取，
        // 尝试次数无上限，最终在底层故障恢复后完成结算——配额不被永久锁死。
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_stuck_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 40), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Completed, default);

                // 模拟已达阈值（10 次）的失败现场：结算中 + 有效租约。
                // 用普通插值字符串而非原始字符串：原始字符串的单美元下不允许 {{ 转义。
                await ExecuteAsync(container, provider,
                    $"UPDATE {{prefix}}terminal_run_settlement_outbox SET status = 2, attempts = 10, " +
                    $"lease_owner = 'node-stuck', lease_token = 'tok-stuck', " +
                    $"lease_expires_at = now() + interval '1 hour', last_error = 'boom' " +
                    $"WHERE run_id = '{admitted.Run.RunId}';");

                var settlementStore = provider.GetRequiredService<ITerminalRunSettlementStore>();
                var outboxId = long.Parse(await QueryScalarAsync(container, provider,
                    $"SELECT outbox_id FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';"));

                // 再次失败：尝试达到阈值 → 转入卡住（不进入终止死信）。
                var failed = await settlementStore.MarkFailedAsync(outboxId, "tok-stuck", "boom", default);
                Assert.IsTrue(failed, "持有租约的失败标记应生效。");

                var statusAfterFail = await QueryScalarAsync(container, provider,
                    $"SELECT status FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("3", statusAfterFail, "尝试达到阈值后应转入卡住（结算永不放弃）。");

                // 低频闸门：卡住退避期内不可被领取。
                var claimedDuringBackoff = await settlementStore.ClaimBatchAsync(
                    10, "node-x", TimeSpan.FromMinutes(5), default);
                Assert.AreEqual(0, claimedDuringBackoff.Count, "卡住退避期内不应被领取（低频重试）。");

                // 闸门过后（模拟等待或运维修复）：仍可领取并完成结算，尝试次数无上限。
                await ExecuteAsync(container, provider,
                    $"UPDATE {{prefix}}terminal_run_settlement_outbox SET lease_expires_at = now() - interval '1 minute' WHERE run_id = '{admitted.Run.RunId}';");

                await RunSettlementWorkerOnceAsync(provider);

                var attempts = await QueryScalarAsync(container, provider,
                    $"SELECT attempts FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("11", attempts, "尝试次数应继续累加（不设上限）。");

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(40, quota.TokensUsed, "恢复后结算应按实际用量转正。");
                Assert.AreEqual(0, quota.ReservedTokens, "恢复后预留释放（配额不被锁死）。");

                var finalStatus = await QueryScalarAsync(container, provider,
                    $"SELECT status FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("1", finalStatus, "恢复后应最终标记已结算。");
            }
        }
    }

    [TestMethod]
    public async Task Settlement_Reconciler_RepairsMissingOutboxEntry()
    {
        // 周期对账：终态 Run + 有效预留 + 无结算记录 → 补写待结算条目，
        // 兜底覆盖状态转换事务漏写 outbox / 结算记录丢失等缺口，配额最终仍被结算。
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 配额持久化测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store, quotaService) = await ResolveAsync(container, "settle_rcn_");
            await using (provider)
            {
                await quotaService.SetLimitAsync(Ws, 100, 0, TimeSpan.FromHours(1), default);

                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 40), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                // 推进终态后删除结算记录，模拟主事务漏写 / 记录丢失缺口。
                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Completed, default);
                await ExecuteAsync(container, provider,
                    $"DELETE FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}';");

                var reservation = await QueryScalarAsync(container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}workspace_quota_reservations WHERE reservation_id = '{admitted.Run.RunId}';");
                Assert.AreEqual("1", reservation, "预留应仍存在（结算缺口）。");

                // 对账补写 + 幂等。
                var settlementStore = provider.GetRequiredService<ITerminalRunSettlementStore>();
                var repaired = await settlementStore.ReconcileSettlementGapsAsync(default);
                Assert.AreEqual(1, repaired, "对账应补写 1 条缺失的结算记录。");
                var repairedAgain = await settlementStore.ReconcileSettlementGapsAsync(default);
                Assert.AreEqual(0, repairedAgain, "对账应幂等——已有记录不再补写。");

                var outboxCount = await QueryScalarAsync(container, provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE run_id = '{admitted.Run.RunId}' AND status = 0;");
                Assert.AreEqual("1", outboxCount, "对账后应有待结算条目。");

                // 补写条目被正常结算。
                await RunSettlementWorkerOnceAsync(provider);

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(40, quota.TokensUsed, "对账补写后结算应按实际用量转正。");
                Assert.AreEqual(0, quota.ReservedTokens, "对账补写后预留释放。");
            }
        }
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildPendingAdmissionRun(int tokensUsed) => new()
    {
        RunId = Guid.NewGuid().ToString("N"),
        WorkspaceId = Ws,
        SessionId = "session-quota-durable",
        Task = "配额持久化测试",
        State = AgentRunState.PendingAdmission,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10),
        CostBudget = new AgentCostBudget
        {
            MaxTokens = 100,
            TokensUsed = tokensUsed,
            MaxCostUsd = 10.0,
            CostUsedUsd = 0
        },
        MaxRetries = 0,
        Priority = 0
    };

    private static QuotaAdmissionRequest BuildAdmission(long reserveTokens, long maxTokens) => new()
    {
        Tokens = reserveTokens,
        CostUsd = 0,
        MaxTokens = maxTokens,
        MaxCostUsd = 0,
        PeriodSeconds = 3600
    };

    /// <summary>驱动结算 worker 执行一轮（领取 + 结算 + 标记）。等待全部条目最终已结算
    /// （含卡住条目——卡住不是终点，闸门过后仍被领取直至已结算）。</summary>
    private static async Task RunSettlementWorkerOnceAsync(ServiceProvider provider)
    {
        var store = provider.GetRequiredService<PostgresAgentRunStore>();
        var quotaService = provider.GetRequiredService<IWorkspaceQuotaService>();
        var options = new ContextCoreRuntimeOptions { RunRecoveryInterval = TimeSpan.FromMilliseconds(50) };
        var worker = new TerminalRunSettlementWorker(
            provider, quotaService, store, options, NullLogger<TerminalRunSettlementWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // 等待 outbox 全部消费（首轮立即执行）：待结算 / 结算中 / 卡住均须收敛到已结算。
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var pending = await QueryScalarAsync(
                    provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE status IN (0, 2, 3);");
                if (pending == "0")
                {
                    break;
                }
                await Task.Delay(100);
            }

            var remaining = await QueryScalarAsync(
                provider,
                $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE status IN (0, 2, 3);");
            Assert.AreEqual("0", remaining, "结算 worker 应在超时前消费全部 outbox 条目。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static async Task<(ServiceProvider Provider, PostgresAgentRunStore Store, PostgresWorkspaceQuotaService QuotaService)> ResolveAsync(
        PostgreSqlContainer container, string tablePrefix)
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = container.GetConnectionString(),
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        });
        var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<PostgresAgentRunStore>();
        var quotaService = provider.GetRequiredService<PostgresWorkspaceQuotaService>();
        return (provider, store, quotaService);
    }

    private static async Task<string> QueryScalarAsync(
        PostgreSqlContainer container, ServiceProvider provider, string sqlTemplate)
    {
        var options = provider.GetRequiredService<PostgresOptions>();
        var prefix = options.TablePrefix ?? string.Empty;
        var sql = sqlTemplate.Replace("{prefix}", prefix);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result?.ToString() ?? string.Empty;
    }

    private static async Task ExecuteAsync(
        PostgreSqlContainer container, ServiceProvider provider, string sqlTemplate)
    {
        var options = provider.GetRequiredService<PostgresOptions>();
        var prefix = options.TablePrefix ?? string.Empty;
        var sql = sqlTemplate.Replace("{prefix}", prefix);
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> QueryScalarAsync(ServiceProvider provider, string sqlTemplate)
    {
        var options = provider.GetRequiredService<PostgresOptions>();
        var prefix = options.TablePrefix ?? string.Empty;
        var sql = sqlTemplate.Replace("{prefix}", prefix);
        var connectionFactory = provider.GetRequiredService<PostgresConnectionFactory>();
        await using var connection = await connectionFactory.OpenConnectionAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return result?.ToString() ?? string.Empty;
    }

    private static async Task<PostgreSqlContainer?> TryStartPostgresAsync()
    {
        const string pgVectorImage = "pgvector/pgvector:pg17";
        try
        {
            var container = new PostgreSqlBuilder(pgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await container.StartAsync(cts.Token);
            return container;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R30Z_WorkspaceQuotaDurabilityTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
