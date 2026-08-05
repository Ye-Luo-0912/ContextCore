using ContextCore.Abstractions;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

// ===========================================================================
// Scheduler Claim 消费（执行交接）测试
//
// 验证 ConsumeClaimAsync 的仲裁失效防护：
// - 队列项 Claim Token/Owner 与数据库一致且未过期 → Claimed → Running + 清空 claim 字段；
// - Token / Owner 不匹配（Claim 过期后他节点重新领取）→ 抛 InvalidOperationException，
//   旧节点不得执行（杜绝"消费他人 Claim"的仲裁失效竞态）；
// - Claim 已过期 / 状态非 Claimed → 拒绝；
// - 执行租约 fencing：token/fencing 不匹配 → 拒绝。
// ===========================================================================

[TestClass]
[TestCategory("Integration")]
[TestCategory("R30")]
public sealed class R30Y_ConsumeClaimTests
{
    private const string Ws = "ws-consume-claim";
    private const string RunId = "run-consume-claim";

    [TestMethod]
    public async Task ConsumeClaim_TokenMatches_TransitionsToRunning_AndClearsClaim()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ConsumeClaim 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveRunStoreAsync(container, "consume_ok_");
            await using (provider)
            {
                await store.CreateAsync(BuildQueuedRun(), default);

                var claimed = await store.TryClaimSingleAsync(
                    Ws, RunId, "owner-a", TimeSpan.FromMinutes(5), default);
                Assert.IsNotNull(claimed, "Queued → Claimed 领取成功。");
                Assert.IsNotNull(claimed!.ClaimToken, "领取应返回 claim token。");

                // 携带正确 token + owner 消费 → Running + claim 字段清空。
                var consumed = await store.ConsumeClaimAsync(
                    Ws, RunId, claimed.ClaimToken, claimed.ClaimOwner, null, null, default);

                Assert.AreEqual(AgentRunState.Running, consumed.State, "消费后进入 Running。");
                Assert.IsNull(consumed.ClaimToken, "消费后 claim_token 清空。");
                Assert.IsNull(consumed.ClaimOwner, "消费后 claim_owner 清空。");
                Assert.IsNull(consumed.ClaimExpiresAtUtc, "消费后 claim_expires_at 清空。");

                var stored = await store.GetAsync(Ws, RunId, default);
                Assert.AreEqual(AgentRunState.Running, stored!.State, "数据库状态应为 Running。");
            }
        }
    }

    [TestMethod]
    public async Task ConsumeClaim_TokenMismatch_Throws_AndKeepsState()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ConsumeClaim 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveRunStoreAsync(container, "consume_tok_");
            await using (provider)
            {
                await store.CreateAsync(BuildQueuedRun(), default);
                var claimed = await store.TryClaimSingleAsync(
                    Ws, RunId, "owner-a", TimeSpan.FromSeconds(1), default);
                Assert.IsNotNull(claimed);

                // 等待 Claim 过期后由批次领取重新领取（owner-b，token 轮换）——
                // 模拟"节点 A 领取后崩溃，节点 B 重新领取"的真实竞态。
                await Task.Delay(TimeSpan.FromSeconds(1.5));

                var batch = await store.ClaimPendingBatchAsync(
                    10, 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
                    "owner-b", TimeSpan.FromMinutes(5), default);
                var reClaimed = batch.FirstOrDefault(r => r.RunId == RunId);
                Assert.IsNotNull(reClaimed, "过期 Claim 应由批次领取重新领取。");
                Assert.AreNotEqual(claimed!.ClaimToken, reClaimed!.ClaimToken, "重新领取 token 轮换。");

                // 旧节点（owner-a，旧 token）消费 → 拒绝（仲裁失效防护）。
                try
                {
                    await store.ConsumeClaimAsync(
                        Ws, RunId, claimed.ClaimToken, claimed.ClaimOwner, null, null, default);
                    Assert.Fail("旧 token 消费必须失败。");
                }
                catch (InvalidOperationException)
                {
                    // 期望：claim 已被接管（token 不匹配）。
                }

                // 新持有者的 token 可正常消费。
                var consumed = await store.ConsumeClaimAsync(
                    Ws, RunId, reClaimed.ClaimToken, reClaimed.ClaimOwner, null, null, default);
                Assert.AreEqual(AgentRunState.Running, consumed.State, "新持有者可消费其 Claim。");
            }
        }
    }

    [TestMethod]
    public async Task ConsumeClaim_OwnerMismatch_Throws()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ConsumeClaim 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveRunStoreAsync(container, "consume_own_");
            await using (provider)
            {
                await store.CreateAsync(BuildQueuedRun(), default);
                var claimed = await store.TryClaimSingleAsync(
                    Ws, RunId, "owner-a", TimeSpan.FromMinutes(5), default);
                Assert.IsNotNull(claimed);

                try
                {
                    // token 正确但 owner 不匹配 → 拒绝。
                    await store.ConsumeClaimAsync(
                        Ws, RunId, claimed!.ClaimToken, "owner-other", null, null, default);
                    Assert.Fail("owner 不匹配必须失败。");
                }
                catch (InvalidOperationException)
                {
                    // 期望。
                }

                var stored = await store.GetAsync(Ws, RunId, default);
                Assert.AreEqual(AgentRunState.Claimed, stored!.State, "消费失败后保持 Claimed。");
            }
        }
    }

    [TestMethod]
    public async Task ConsumeClaim_ExpiredClaim_Throws()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ConsumeClaim 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveRunStoreAsync(container, "consume_exp_");
            await using (provider)
            {
                await store.CreateAsync(BuildQueuedRun(), default);
                // 领取极短 Claim（1s），等待过期后消费 → 拒绝。
                var claimed = await store.TryClaimSingleAsync(
                    Ws, RunId, "owner-a", TimeSpan.FromSeconds(1), default);
                Assert.IsNotNull(claimed);

                await Task.Delay(TimeSpan.FromSeconds(1.5));

                try
                {
                    await store.ConsumeClaimAsync(
                        Ws, RunId, claimed!.ClaimToken, claimed.ClaimOwner, null, null, default);
                    Assert.Fail("已过期 Claim 消费必须失败。");
                }
                catch (InvalidOperationException)
                {
                    // 期望。
                }
            }
        }
    }

    [TestMethod]
    public async Task ConsumeClaim_StateNotClaimed_Throws()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ConsumeClaim 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveRunStoreAsync(container, "consume_st_");
            await using (provider)
            {
                await store.CreateAsync(BuildQueuedRun(), default);

                try
                {
                    // 尚未领取（Queued）直接消费 → 拒绝。
                    await store.ConsumeClaimAsync(Ws, RunId, "token", "owner", null, null, default);
                    Assert.Fail("非 Claimed 状态消费必须失败。");
                }
                catch (InvalidOperationException)
                {
                    // 期望。
                }
            }
        }
    }

    [TestMethod]
    public async Task ConsumeClaim_LeaseFencing_ValidatesExecutionLease()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — ConsumeClaim 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, store) = await ResolveRunStoreAsync(container, "consume_ls_");
            await using (provider)
            {
                var leaseStore = provider.GetRequiredService<IAgentRunLease>();
                await store.CreateAsync(BuildQueuedRun(), default);
                var claimed = await store.TryClaimSingleAsync(
                    Ws, RunId, "owner-a", TimeSpan.FromMinutes(5), default);
                Assert.IsNotNull(claimed);

                // 未取得执行租约时携带错误 lease → 拒绝。
                try
                {
                    await store.ConsumeClaimAsync(
                        Ws, RunId, claimed!.ClaimToken, claimed.ClaimOwner, "wrong-token", 99, default);
                    Assert.Fail("执行租约不匹配必须失败。");
                }
                catch (InvalidOperationException)
                {
                    // 期望。
                }

                // 取得执行租约 → 消费成功。
                var lease = await leaseStore.TryAcquireAsync(
                    RunId, TimeSpan.FromMinutes(5), "host-1", default);
                Assert.IsNotNull(lease, "执行租约获取成功。");

                var consumed = await store.ConsumeClaimAsync(
                    Ws, RunId, claimed.ClaimToken, claimed.ClaimOwner,
                    lease!.LeaseToken, lease.FencingToken, default);
                Assert.AreEqual(AgentRunState.Running, consumed.State, "持有有效执行租约 → 消费成功。");
            }
        }
    }

    // ── 辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildQueuedRun() => new()
    {
        RunId = RunId,
        WorkspaceId = Ws,
        SessionId = "session-consume-claim",
        Task = "ConsumeClaim 测试",
        State = AgentRunState.Queued,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10)
    };

    private static async Task<(ServiceProvider Provider, PostgresAgentRunStore Store)> ResolveRunStoreAsync(
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
        var store = provider.GetRequiredService<PostgresAgentRunStore>();
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
            Console.WriteLine($"[R30Y_ConsumeClaimTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
