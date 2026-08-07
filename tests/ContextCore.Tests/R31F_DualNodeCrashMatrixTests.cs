using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

/// <summary>
/// 双节点崩溃矩阵验收：两个独立 provider（共享同一 Postgres 容器，模拟两个进程
/// 竞争同一数据库）在 Claim / Quota / Reconciliation 三类关键路径上的节点崩溃语义。
///
/// 每个场景的模式：节点 A 完成关键写入（领取租约 / 预留配额 / 领取对账租约）后
/// 「崩溃」（关闭连接，不释放任何资源），验证节点 B 能正确观察、接管或清理——
/// 状态不丢失、不双写、不被未过期租约抢占。
/// </summary>
[TestClass]
[TestCategory("Integration")]
[TestCategory("R31")]
public sealed class R31F_DualNodeCrashMatrixTests
{
    // ── Claim：节点 A 领取 Run 租约后崩溃 → B 受租约保护，过期后接管 ──

    [TestMethod]
    public async Task Claim_NodeACrashesAfterAcquire_NodeBTakesOverAfterLeaseExpiry()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 双节点崩溃矩阵测试已跳过。");
            return;
        }

        await using (container)
        {
            var (providerA, leaseA) = ResolveLease(container, "cm_claim_");
            var runId = "run-cm-" + Guid.NewGuid().ToString("N")[..8];
            const string ws = "ws-cm-claim";

            var acquired = await leaseA.TryAcquireAsync(ws, runId, TimeSpan.FromSeconds(1), "node-a", default);
            Assert.IsNotNull(acquired, "节点 A 应能领取 Run 租约。");

            // 节点 A 崩溃：关闭连接，租约留在数据库中（不释放）
            await providerA.DisposeAsync();

            var (providerB, leaseB) = ResolveLease(container, "cm_claim_");
            await using (providerB)
            {
                // 租约未过期：B 不得抢占（ClaimToken 保护）
                var blocked = await leaseB.TryAcquireAsync(ws, runId, TimeSpan.FromSeconds(5), "node-b", default);
                Assert.IsNull(blocked, "A 租约未过期时节点 B 不得抢占。");

                // 等待 A 的短租约过期，然后 B 接管
                await Task.Delay(1600);
                var taken = await leaseB.TryAcquireAsync(ws, runId, TimeSpan.FromSeconds(5), "node-b", default);
                Assert.IsNotNull(taken, "A 崩溃且租约过期后节点 B 应能接管。");
                Assert.AreEqual("node-b", taken!.Owner);

                var renewed = await leaseB.RenewAsync(ws, runId, taken.LeaseToken, TimeSpan.FromSeconds(5), default);
                Assert.IsTrue(renewed, "接管后 B 续租应成功。");

                await leaseB.ReleaseAsync(ws, runId, taken.LeaseToken, default);
                var stillActive = await leaseB.HasActiveLeaseAsync(ws, runId, default);
                Assert.IsFalse(stillActive, "B 释放后租约应消失。");
            }
        }
    }

    // ── Quota：节点 A 预留后崩溃 → B 看到预留不丢失，可清理并重新预留 ──

    [TestMethod]
    public async Task Quota_NodeACrashesAfterReserve_ReservationSurvivesAndNodeBCleansUp()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 双节点崩溃矩阵测试已跳过。");
            return;
        }

        await using (container)
        {
            var (providerA, quotaA) = ResolveQuota(container, "cm_quota_");
            const string ws = "ws-cm-quota";

            await quotaA.SetLimitAsync(ws, 100, 0, TimeSpan.FromHours(1), default);
            var reserved = await quotaA.ReserveAsync(ws, "res-a", 100, 0, default);
            Assert.IsTrue(reserved.Allowed, "节点 A 应能预留满容量。");

            // 节点 A 崩溃：关闭连接，预留留在数据库中（不释放）
            await providerA.DisposeAsync();

            var (providerB, quotaB) = ResolveQuota(container, "cm_quota_");
            await using (providerB)
            {
                var after = await quotaB.GetQuotaAsync(ws, default);
                Assert.AreEqual(100, after.ReservedTokens, "A 崩溃后预留不得丢失（跨节点持久）。");

                var denied = await quotaB.ReserveAsync(ws, "res-b", 100, 0, default);
                Assert.IsFalse(denied.Allowed, "A 残留预留占满容量时 B 应被拒绝。");

                // B 清理 A 留下的残留预留（幂等释放）
                await quotaB.ReleaseAsync(ws, "res-a", default);
                var freed = await quotaB.ReserveAsync(ws, "res-c", 100, 0, default);
                Assert.IsTrue(freed.Allowed, "清理 A 残留预留后 B 应能重新预留。");
            }
        }
    }

    // ── Reconciliation：节点 A 领取对账租约后崩溃 → B 租约过期后接管并裁决 ──

    [TestMethod]
    public async Task Reconciliation_NodeACrashesAfterBegin_NodeBTakesOverAndResolves()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — 双节点崩溃矩阵测试已跳过。");
            return;
        }

        await using (container)
        {
            var (providerA, reconA) = ResolveRecon(container, "cm_recon_");
            var recId = "rec-cm-" + Guid.NewGuid().ToString("N")[..8];

            await reconA.CreateAsync(BuildRecord(recId), default);
            var beginA = await reconA.TryBeginAsync(recId, "node-a", TimeSpan.FromSeconds(1), default);
            Assert.IsNotNull(beginA, "节点 A 应能领取对账租约。");

            // 节点 A 崩溃：关闭连接，对账记录停在 Running 且租约留在数据库中
            await providerA.DisposeAsync();

            var (providerB, reconB) = ResolveRecon(container, "cm_recon_");
            await using (providerB)
            {
                var blocked = await reconB.TryBeginAsync(recId, "node-b", TimeSpan.FromSeconds(5), default);
                Assert.IsNull(blocked, "A 租约未过期时 B 不得接管对账记录。");

                // 等待 A 的短租约过期
                await Task.Delay(1600);
                var pending = await reconB.ListPendingAsync(10, default);
                Assert.IsTrue(pending.Any(r => r.ReconciliationId == recId),
                    "A 崩溃且租约过期后记录应重新可接管。");

                var taken = await reconB.TryBeginAsync(recId, "node-b", TimeSpan.FromSeconds(5), default);
                Assert.IsNotNull(taken, "租约过期后 B 应能接管对账记录。");

                var resolved = await reconB.MarkResolvedAsync(
                    recId,
                    taken!.LeaseToken,
                    new ToolReconciliationOutcome { SideEffectOccurred = true, Result = "confirmed" },
                    default);
                Assert.IsTrue(resolved, "B 应能持有租约完成裁决。");

                var final = await reconB.GetAsync(recId, default);
                Assert.AreEqual(ToolReconciliationStatus.Resolved, final!.Status, "记录应落入终态 Resolved。");
            }
        }
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static (ServiceProvider Provider, IAgentRunLease Lease) ResolveLease(
        PostgreSqlContainer container, string tablePrefix)
    {
        var provider = BuildProvider(container, tablePrefix);
        var lease = provider.GetRequiredService<IAgentRunLease>();
        Assert.IsInstanceOfType(lease, typeof(PostgresAgentRunLease),
            "AddContextCorePostgresStorage 必须把 IAgentRunLease 解析为 Postgres 实现。");
        return (provider, lease);
    }

    private static (ServiceProvider Provider, IWorkspaceQuotaService Quota) ResolveQuota(
        PostgreSqlContainer container, string tablePrefix)
    {
        var provider = BuildProvider(container, tablePrefix);
        var quota = provider.GetRequiredService<IWorkspaceQuotaService>();
        Assert.IsInstanceOfType(quota, typeof(PostgresWorkspaceQuotaService),
            "AddContextCorePostgresStorage 必须把 IWorkspaceQuotaService 解析为 Postgres 实现。");
        return (provider, quota);
    }

    private static (ServiceProvider Provider, IToolReconciliationStore Store) ResolveRecon(
        PostgreSqlContainer container, string tablePrefix)
    {
        var provider = BuildProvider(container, tablePrefix);
        var store = provider.GetRequiredService<IToolReconciliationStore>();
        Assert.IsInstanceOfType(store, typeof(PostgresToolReconciliationStore),
            "AddContextCorePostgresStorage 必须把 IToolReconciliationStore 解析为 Postgres 实现。");
        return (provider, store);
    }

    private static ServiceProvider BuildProvider(PostgreSqlContainer container, string tablePrefix)
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = container.GetConnectionString(),
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = tablePrefix
        });
        return services.BuildServiceProvider();
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
            Console.WriteLine($"[R31F_DualNodeCrashMatrixTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static ToolReconciliationRecord BuildRecord(string reconciliationId) => new()
    {
        ReconciliationId = reconciliationId,
        RunId = "run-cm-recon",
        WorkspaceId = "ws-cm-recon",
        RequestId = "req-cm",
        ToolName = "bank-transfer",
        ReconciliationHandler = "bank-query",
        Status = ToolReconciliationStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
