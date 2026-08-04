using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// PostgresToolReconciliationStore 验收测试（-B1 Tool Reconciliation Control Plane）
//
// 验证对账记录跨进程持久化真相源（ProductionHA 组合根下的 IToolReconciliationStore）：
// 1. DI 注册：AddContextCorePostgresStorage 解析 IToolReconciliationStore →
// PostgresToolReconciliationStore（非 InMemory）；
// 2. 幂等创建：按 (workspace_id, run_id, request_id) UNIQUE 幂等（P0-5 完整租户键）；
// 3. 裁决租约（P0-4）：TryBeginAsync 领取租约（Pending → Running + lease/fencing），
// 互斥且幂等；TryResetToPending / MarkResolved / MarkRejected 必须持有有效租约
// （lease_token 匹配 + 未过期）；
// 4. 未裁决门：HasUnresolvedForRunAsync 仅对 Pending/Running 返回 true
// （未决高风险副作用阻止 Run Completed 的数据库门）；
// 5. ExternalOperationId 反查：按 journal 外部操作 ID 跨 Run 查询；
// 6. ControlRoom 列表：分页 + 过期未决高亮（deadline_utc < now）+
// 告警计数（OverdueCount）+ OverdueOnly 过滤。
//
// Docker 不可用时 Assert.Inconclusive 跳过（CI integration-postgres job 中 Docker 始终可用）。
// ===========================================================================

[TestClass]
[TestCategory("Integration")]
[TestCategory("R29-Hard-Gate")]
[TestCategory("ToolReconciliation-ControlPlane")]
public sealed class R29H_PostgresToolReconciliationStoreTests
{
    private const string Ws = "ws-recon-pg";
    private const string RunId = "run-recon-pg";

    // ── 1. DI 注册 + 幂等创建 ──────────────────────────────────────────────

    /// <summary>
    /// 验证：AddContextCorePostgresStorage 解析 IToolReconciliationStore → Postgres 实现；
    /// CreateAsync 按 (run_id, request_id) 幂等——重复创建同一 Tool 调用只保留一条记录。
    /// </summary>
    [TestMethod]
    public async Task Store_ResolvesFromDi_AndCreateIsIdempotentByRunAndRequest()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — PostgresToolReconciliationStore 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveStoreAsync(container, "rec_store_idem_");
            await using (provider)
            {
                var created = await store.CreateAsync(BuildRecord("rec-1", "req-1", Deadline: null), default);
                Assert.AreEqual("rec-1", created.ReconciliationId);

                // 同一 RunId+RequestId 重复创建 → 返回既有记录（不新增）
                var duplicate = await store.CreateAsync(BuildRecord("rec-1-dup", "req-1", Deadline: null), default);
                Assert.AreEqual("rec-1", duplicate.ReconciliationId, "同一 Tool 调用只保留一条对账记录。");

                // 不同 RequestId → 独立记录
                await store.CreateAsync(BuildRecord("rec-2", "req-2", Deadline: null), default);
                var all = await store.ListByRunAsync(RunId, default);
                Assert.AreEqual(2, all.Count, "两条不同 RequestId 的记录应各自保留。");
            }
        }
    }

    // ── 2. CAS 推进 + 未裁决门 ─────────────────────────────────────────────

    /// <summary>
    /// 验证：TryBeginAsync（Pending→Running）互斥、TryResetToPendingAsync 回退、
    /// 终态裁决幂等；HasUnresolvedForRunAsync 仅对 Pending/Running 返回 true。
    /// </summary>
    [TestMethod]
    public async Task Store_CasTransitions_AndUnresolvedGate()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — PostgresToolReconciliationStore 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveStoreAsync(container, "rec_store_cas_");
            await using (provider)
            {
                await store.CreateAsync(BuildRecord("rec-1", "req-1", Deadline: null), default);

                Assert.IsTrue(await store.HasUnresolvedForRunAsync(RunId, default), "Pending 记录 → 未裁决。");

                var lease = await store.TryBeginAsync("rec-1", "test", TimeSpan.FromMinutes(1), default);
                Assert.IsNotNull(lease, "Pending → Running 首次接管成功。");
                Assert.IsNull(await store.TryBeginAsync("rec-1", "test", TimeSpan.FromMinutes(1), default), "Running 状态不可重复接管。");
                Assert.AreEqual(ToolReconciliationStatus.Running, (await store.GetAsync("rec-1", default))!.Status);
                Assert.IsTrue(await store.HasUnresolvedForRunAsync(RunId, default), "Running 记录仍属未裁决。");

                Assert.IsTrue(await store.TryResetToPendingAsync("rec-1", lease!.LeaseToken, null, null, default), "Running → Pending 回退成功。");
                Assert.IsFalse(await store.TryResetToPendingAsync("rec-1", lease.LeaseToken, null, null, default), "仅 Running 可回退。");

                // P0-4：终态裁决必须持有有效租约——回退后重新领取再裁决。
                var lease2 = await store.TryBeginAsync("rec-1", "test", TimeSpan.FromMinutes(1), default);
                Assert.IsNotNull(lease2, "回退后 Pending 可再次接管。");
                Assert.IsTrue(await store.MarkResolvedAsync(
                    "rec-1", lease2!.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "txn-1" }, default),
                    "持有有效租约 → Resolved 裁决成功。");
                Assert.IsFalse(await store.MarkResolvedAsync(
                    "rec-1", lease2.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = true }, default),
                    "已 Resolved 记录重复裁决必须幂等失败。");
                Assert.IsFalse(await store.MarkRejectedAsync(
                    "rec-1", lease2.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = false }, default),
                    "已 Resolved 记录不可再 Rejected。");
                Assert.IsFalse(await store.HasUnresolvedForRunAsync(RunId, default), "全部终态 → 无未裁决记录。");

                var resolved = await store.GetAsync("rec-1", default);
                Assert.IsNotNull(resolved, "应能取回已裁决记录。");
                Assert.AreEqual(ToolReconciliationStatus.Resolved, resolved!.Status);
                Assert.IsTrue(resolved.SideEffectOccurred, "裁决结果应持久化（side_effect_occurred=true）。");
                Assert.AreEqual("txn-1", resolved.Result, "裁决结果应持久化（result=txn-1）。");
                Assert.IsNotNull(resolved.ResolvedAt, "终态应记录裁决时间。");
            }
        }
    }

    /// <summary>验证：RenewHeartbeatBatchAsync 单次往返批量续约——仅 token 匹配且未过期的 Running 记录被续约。</summary>
    [TestMethod]
    public async Task Store_RenewHeartbeatBatch_OnlyRenewsMatchingToken()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — PostgresToolReconciliationStore 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveStoreAsync(container, "rec_store_hb_");
            await using (provider)
            {
                await store.CreateAsync(BuildRecord("rec-hb-1", "req-hb-1", Deadline: null), default);
                await store.CreateAsync(BuildRecord("rec-hb-2", "req-hb-2", Deadline: null), default);
                await store.CreateAsync(BuildRecord("rec-hb-3", "req-hb-3", Deadline: null), default);
                var lease1 = await store.TryBeginAsync("rec-hb-1", "worker-a", TimeSpan.FromMinutes(5), default);
                var lease2 = await store.TryBeginAsync("rec-hb-2", "worker-b", TimeSpan.FromMinutes(1), default);
                Assert.IsNotNull(lease1, "rec-hb-1 领取租约成功。");
                Assert.IsNotNull(lease2, "rec-hb-2 领取租约成功。");

                var failed = await store.RenewHeartbeatBatchAsync(
                    new[]
                    {
                        new ToolReconciliationHeartbeat { ReconciliationId = "rec-hb-1", LeaseToken = lease1!.LeaseToken },
                        new ToolReconciliationHeartbeat { ReconciliationId = "rec-hb-2", LeaseToken = "wrong-token" },
                        new ToolReconciliationHeartbeat { ReconciliationId = "rec-hb-3", LeaseToken = "no-lease" }
                    },
                    TimeSpan.FromMinutes(5),
                    default);

                CollectionAssert.AreEquivalent(new[] { "rec-hb-2", "rec-hb-3" }, failed.ToList(),
                    "仅 token 匹配且持有有效租约的记录被续约，其余返回失败。");
                var renewed = await store.GetAsync("rec-hb-1", default);
                Assert.IsNotNull(renewed!.LeaseExpiresAt, "rec-hb-1 租约应被延长。");
                Assert.IsTrue(renewed.LeaseExpiresAt!.Value > DateTimeOffset.UtcNow.AddMinutes(4),
                    "rec-hb-1 续约后过期时间在未来 5 分钟内。");
            }
        }
    }

    // ── 3. ExternalOperationId 反查（跨 Run）────────────────────────────────

    /// <summary>
    /// 验证：QueryByExternalOperationIdAsync 按 journal 外部操作 ID 跨 Run 反查，
    /// 未匹配时返回空列表。
    /// </summary>
    [TestMethod]
    public async Task Store_QueryByExternalOperationId_AcrossRuns()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — PostgresToolReconciliationStore 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveStoreAsync(container, "rec_store_extop_");
            await using (provider)
            {
                const string externalOp = "ext-op-42";
                await store.CreateAsync(BuildRecord("rec-1", "req-1", Deadline: null, externalOperationId: externalOp), default);
                await store.CreateAsync(BuildRecord("rec-2", "req-2", Deadline: null, externalOperationId: externalOp, runId: "run-other"), default);
                await store.CreateAsync(BuildRecord("rec-3", "req-3", Deadline: null, externalOperationId: "ext-op-other"), default);

                var matches = await store.QueryByExternalOperationIdAsync(externalOp, default);
                Assert.AreEqual(2, matches.Count, "应反查到两条同 externalOperationId 的记录（跨 Run）。");
                CollectionAssert.AreEquivalent(
                    new[] { "rec-1", "rec-2" },
                    matches.Select(m => m.ReconciliationId).ToList(),
                    "反查应覆盖跨 Run 的所有匹配记录。");

                var none = await store.QueryByExternalOperationIdAsync("ext-op-missing", default);
                Assert.AreEqual(0, none.Count, "未匹配的 externalOperationId 应返回空列表。");
            }
        }
    }

    // ── 4. ControlRoom 列表：分页 + 过期高亮 + 告警计数 ─────────────────────

    /// <summary>
    /// 验证：ListAsync 分页过滤（workspace/status/overdueOnly）+ 总数 +
    /// OverdueCount（deadline_utc &lt; now 且 Pending/Running）+ 过期条目高亮。
    /// </summary>
    [TestMethod]
    public async Task Store_ControlRoomList_Paging_OverdueHighlight_AlertCount()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — PostgresToolReconciliationStore 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveStoreAsync(container, "rec_store_room_");
            await using (provider)
            {
                var now = DateTimeOffset.UtcNow;

                // 3 条 Pending：2 条过期（deadline 已过）、1 条未过期
                await store.CreateAsync(BuildRecord("rec-old-1", "req-old-1", Deadline: now - TimeSpan.FromHours(2)), default);
                await store.CreateAsync(BuildRecord("rec-old-2", "req-old-2", Deadline: now - TimeSpan.FromMinutes(30)), default);
                await store.CreateAsync(BuildRecord("rec-fresh", "req-fresh", Deadline: now + TimeSpan.FromHours(2)), default);
                // 1 条已裁决（即使过期也不计告警）
                await store.CreateAsync(BuildRecord("rec-resolved", "req-resolved", Deadline: now - TimeSpan.FromHours(1)), default);
                var lease = await store.TryBeginAsync("rec-resolved", "test", TimeSpan.FromMinutes(1), default);
                Assert.IsNotNull(lease, "Pending → Running 接管成功。");
                await store.MarkRejectedAsync(
                    "rec-resolved", lease!.LeaseToken, new ToolReconciliationOutcome { SideEffectOccurred = false, Error = "未发生" }, default);

                // 全量列表：total=4；OverdueCount=2（仅 Pending 且 deadline 已过）
                var all = await store.ListAsync(new ReconciliationQuery { WorkspaceId = Ws, Limit = 50 }, default);
                Assert.AreEqual(4, all.Total, "列表应包含全部 4 条记录。");
                Assert.AreEqual(2, all.OverdueCount, "告警计数 = 过期未决（Pending 且 deadline<now）2 条。");
                Assert.AreEqual(4, all.Items.Count, "limit=50 应返回全部条目。");

                // 过期高亮：items 按 CreatedAt 倒序；断言过期条目本身带 DeadlineUtc（列表消费方可高亮）
                var overdueIds = all.Items
                    .Where(r => r.DeadlineUtc.HasValue && r.DeadlineUtc.Value < DateTimeOffset.UtcNow
                                && r.Status == ToolReconciliationStatus.Pending)
                    .Select(r => r.ReconciliationId)
                    .ToList();
                CollectionAssert.AreEquivalent(new[] { "rec-old-1", "rec-old-2" }, overdueIds,
                    "过期未决条目应在列表中携带过去 DeadlineUtc（高亮依据）。");

                // OverdueOnly 过滤：只看过期未决
                var overdueOnly = await store.ListAsync(
                    new ReconciliationQuery { WorkspaceId = Ws, OverdueOnly = true, Limit = 50 }, default);
                Assert.AreEqual(2, overdueOnly.Total, "OverdueOnly 应只返回过期未决记录。");
                CollectionAssert.AreEquivalent(
                    new[] { "rec-old-1", "rec-old-2" },
                    overdueOnly.Items.Select(r => r.ReconciliationId).ToList(),
                    "OverdueOnly 条目应为两条过期未决记录。");

                // 状态过滤：仅 Pending
                var pending = await store.ListAsync(
                    new ReconciliationQuery { WorkspaceId = Ws, Status = ToolReconciliationStatus.Pending, Limit = 50 }, default);
                Assert.AreEqual(3, pending.Total, "Pending 状态过滤应返回 3 条。");

                // 分页：limit=2 第一页（最新两条）+ offset 翻页
                var page1 = await store.ListAsync(new ReconciliationQuery { WorkspaceId = Ws, Limit = 2, Offset = 0 }, default);
                var page2 = await store.ListAsync(new ReconciliationQuery { WorkspaceId = Ws, Limit = 2, Offset = 2 }, default);
                Assert.AreEqual(2, page1.Items.Count, "第一页应返回 2 条。");
                Assert.AreEqual(2, page2.Items.Count, "第二页应返回 2 条。");
                var page1Ids = page1.Items.Select(r => r.ReconciliationId).ToHashSet();
                var page2Ids = page2.Items.Select(r => r.ReconciliationId).ToHashSet();
                Assert.AreEqual(0, page1Ids.Intersect(page2Ids).Count(), "两页不应有重复条目。");
                Assert.AreEqual(4, page1Ids.Union(page2Ids).Count(), "两页应覆盖全部 4 条。");
            }
        }
    }

    // ── 辅助方法 ───────────────────────────────────────────────────────────

    /// <summary>最小 DI 注册（AddContextCorePostgresStorage），解析 IToolReconciliationStore 并断言为 Postgres 实现。</summary>
    private static async Task<(ServiceProvider Provider, IToolReconciliationStore Store)> ResolveStoreAsync(
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
        var store = provider.GetRequiredService<IToolReconciliationStore>();
        Assert.IsInstanceOfType(store, typeof(PostgresToolReconciliationStore),
            "AddContextCorePostgresStorage 必须把 IToolReconciliationStore 解析为 PostgresToolReconciliationStore。");
        Assert.IsNotInstanceOfType(store, typeof(InMemoryToolReconciliationStore),
            "Postgres provider 下不允许回退到 InMemoryToolReconciliationStore。");
        return (provider, store);
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
            Console.WriteLine($"[R29H_PostgresToolReconciliationStoreTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static ToolReconciliationRecord BuildRecord(
        string reconciliationId,
        string requestId,
        DateTimeOffset? Deadline,
        string? externalOperationId = null,
        string runId = RunId) => new()
    {
        ReconciliationId = reconciliationId,
        RunId = runId,
        WorkspaceId = Ws,
        RequestId = requestId,
        ToolName = "bank-transfer",
        ExternalOperationId = externalOperationId,
        ReconciliationHandler = "bank-query",
        DeadlineUtc = Deadline,
        Status = ToolReconciliationStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
