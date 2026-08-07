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
    public async Task TerminalTransition_Cancelled_SettlementReleases()
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

                var admitted = await store.AdmitRunAtomicallyAsync(
                    BuildPendingAdmissionRun(tokensUsed: 0), BuildAdmission(100, 100), default);
                Assert.IsTrue(admitted.Created);

                // 取消 → Cancelled 终态 → outbox。
                await store.TransitionStateAsync(
                    Ws, admitted.Run.RunId, AgentRunState.Queued, AgentRunState.Cancelled, default);

                await RunSettlementWorkerOnceAsync(provider);

                var quota = await quotaService.GetQuotaAsync(Ws, default);
                Assert.AreEqual(0, quota.ReservedTokens, "取消类终态应退回容量。");
                Assert.AreEqual(0, quota.TokensUsed, "取消类终态不计入消耗。");
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

    /// <summary>驱动结算 worker 执行一轮（领取 + 结算 + 标记）。</summary>
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
            // 等待 outbox 被消费（首轮立即执行）。
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var pending = await QueryScalarAsync(
                    provider,
                    $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE status IN (0, 2);");
                if (pending == "0")
                {
                    break;
                }
                await Task.Delay(100);
            }

            var remaining = await QueryScalarAsync(
                provider,
                $"SELECT COUNT(1) FROM {{prefix}}terminal_run_settlement_outbox WHERE status IN (0, 2);");
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
