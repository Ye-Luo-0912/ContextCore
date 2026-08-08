using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// 未决对账阻止 Failed 自动重试测试
//
// 两层防护：
// 1. Actor：任何 Failed 转移前检查未决对账（Pending/Running）——有未决 → 停靠
//    AwaitingReconciliation（不写 Failed），无未决 → 正常 Failed；
// 2. Scheduler：ClaimPendingBatchAsync 领取 SQL 排除存在未决对账记录的 Run——
//    即使 Run 已 Failed，真相未确认前不得被自动重试。
// ===========================================================================

[TestClass]
[TestCategory("Agent-Actor")]
public sealed class R30X_UnresolvedReconciliationGateTests
{
    private const string Ws = "ws-unresolved-gate";

    [TestMethod]
    public async Task Actor_FailWithUnresolvedReconciliation_ParksNotFails()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var reconciliationStore = new InMemoryToolReconciliationStore();
        var run = BuildRun("未决对账门禁测试");
        await runStore.CreateAsync(run);

        // 该 Run 存在未决对账记录（外部副作用真相未确认）。
        await reconciliationStore.CreateAsync(new ToolReconciliationRecord
        {
            ReconciliationId = "rec:" + Ws + ":req-1",
            RunId = run.RunId,
            WorkspaceId = Ws,
            RequestId = "req-1",
            ToolName = "bank-transfer",
            ExternalOperationId = "ext-1",
            ReconciliationHandler = "bank-recon",
            Status = ToolReconciliationStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        // 模型传输抛异常 → 外层 catch → FailAsync → 门禁应停靠而非 Failed。
        var actor = new AgentRunActor(
            runStore, eventStore, new ThrowingModelTransport(),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            reconciliationStore: reconciliationStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        Assert.AreEqual(AgentRunState.AwaitingReconciliation, stored!.State,
            "存在未决对账记录 → 失败应停靠 AwaitingReconciliation，不得进入 Failed。");

        var events = await eventStore.ReadAsync(Ws, run.RunId);
        CollectionAssert.DoesNotContain(events.Select(e => e.EventType).ToList(), AgentRunEventType.RunFailed,
            "停靠路径不得写 RunFailed 事件（Run 未失败）。");
    }

    [TestMethod]
    public async Task Actor_FailWithoutUnresolvedReconciliation_Fails()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var reconciliationStore = new InMemoryToolReconciliationStore();
        var run = BuildRun("无未决对账测试");
        await runStore.CreateAsync(run);

        var actor = new AgentRunActor(
            runStore, eventStore, new ThrowingModelTransport(),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            reconciliationStore: reconciliationStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, run.RunId);
        Assert.AreEqual(AgentRunState.Failed, stored!.State, "无未决对账记录 → 正常 Failed。");
    }

    [TestMethod]
    public async Task Scheduler_ClaimSkipsFailedRunWithUnresolvedReconciliation()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Scheduler 未决对账排除测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, runStore, reconciliationStore) = await ResolveStoresAsync(container, "claim_recon_");
            await using (provider)
            {
                // 可重试的 Run（RetryPending，max_retries=1）+ 一条 Pending 对账记录。
                await runStore.CreateAsync(BuildRetryPendingRun(), default);
                await reconciliationStore.CreateAsync(new ToolReconciliationRecord
                {
                    ReconciliationId = "rec:" + Ws + ":req-1",
                    RunId = RunId,
                    WorkspaceId = Ws,
                    RequestId = "req-1",
                    ToolName = "bank-transfer",
                    ExternalOperationId = "ext-1",
                    ReconciliationHandler = "bank-recon",
                    Status = ToolReconciliationStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                }, default);

                // 未决对账存在 → 不得被领取（RetryPending 自动重试被真相边界阻断）。
                var batch1 = await runStore.ClaimPendingBatchAsync(
                    10, 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
                    "claimer-1", TimeSpan.FromMinutes(5), default);
                CollectionAssert.DoesNotContain(batch1.Select(r => r.RunId).ToList(), RunId,
                    "存在未决对账记录的 RetryPending Run 不得被领取。");

                // 对账裁决完毕（Resolved）→ 可被领取重试。
                var lease = await reconciliationStore.TryBeginAsync(
                    "rec:" + Ws + ":req-1", "worker:test", TimeSpan.FromMinutes(5), default);
                Assert.IsNotNull(lease);
                await reconciliationStore.MarkResolvedAsync(
                    "rec:" + Ws + ":req-1", lease!.LeaseToken,
                    new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-1" }, default);

                var batch2 = await runStore.ClaimPendingBatchAsync(
                    10, 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
                    "claimer-1", TimeSpan.FromMinutes(5), default);
                CollectionAssert.Contains(batch2.Select(r => r.RunId).ToList(), RunId,
                    "对账完成后 RetryPending Run 可被领取重试。");
            }
        }
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private const string RunId = "run-unresolved-gate";

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = RunId,
        WorkspaceId = Ws,
        SessionId = "session-unresolved-gate",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 10 }
    };

    private static AgentRun BuildRetryPendingRun() => new()
    {
        RunId = RunId,
        WorkspaceId = Ws,
        SessionId = "session-unresolved-gate",
        Task = "可重试失败 Run",
        State = AgentRunState.RetryPending,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget { MaxTurns = 10, TurnsUsed = 0, MaxModelCalls = 10 },
        MaxRetries = 1,
        RetryCount = 0,
        FailureReason = "模拟失败"
    };

    /// <summary>模型传输 stub：调用即抛异常（触发外层 catch → FailAsync）。</summary>
    private sealed class ThrowingModelTransport : IAgentModelTransport
    {
        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated model failure");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated model failure");

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated model failure");
    }

    private static async Task<(ServiceProvider Provider, PostgresAgentRunStore RunStore, PostgresToolReconciliationStore ReconciliationStore)> ResolveStoresAsync(
        PostgreSqlContainer container, string tablePrefix)
    {
        var connectionString = container.GetConnectionString();
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = connectionString,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        });
        var provider = services.BuildServiceProvider();
        return (provider,
            provider.GetRequiredService<PostgresAgentRunStore>(),
            provider.GetRequiredService<PostgresToolReconciliationStore>());
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
            Console.WriteLine($"[R30X_UnresolvedReconciliationGateTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
