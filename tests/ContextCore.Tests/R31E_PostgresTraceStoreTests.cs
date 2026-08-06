using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace ContextCore.Tests;

/// <summary>
/// Postgres trace store 三件套（Decision / Retrieval / PackageBuild）round-trip
/// 与查询语义验收测试。
///
/// 与成员租约容器测试相同模式：每个测试独立启动 Testcontainers 容器
/// （pgvector/pgvector:pg17），Docker 不可用时 Inconclusive 跳过。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("R31")]
public sealed class R31E_PostgresTraceStoreTests
{
    [TestMethod]
    public async Task DecisionTraceStore_SaveAndQuery_Roundtrips()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres trace store 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, decision, _, _) = await ResolveStoresAsync(container, "trc_d_");
            await using (provider)
            {
                var now = DateTimeOffset.UtcNow;
                await decision.SaveAsync(MakeDecisionRecord("d-r1", "ws-d", "col-d", "决策查询", now));

                var results = await decision.QueryRecentAsync("ws-d", "col-d", 10);

                Assert.AreEqual(1, results.Count, "应查询到刚存入的决策 trace");
                var loaded = results[0];
                Assert.AreEqual("d-r1", loaded.DecisionId);
                Assert.AreEqual("决策查询", loaded.QueryText);
                Assert.AreEqual(ContextDecisionSource.Package, loaded.Source);
                Assert.AreEqual(2, loaded.Outcome.SelectedCount);
                Assert.AreEqual(1, loaded.Candidates.Count, "候选投影应随 data 完整往返");
                Assert.AreEqual("cand-1", loaded.Candidates[0].ItemId);
                Assert.AreEqual(ContextDecisionCandidateOutcome.Selected, loaded.Candidates[0].Outcome);
            }
        }
    }

    [TestMethod]
    public async Task RetrievalTraceStore_SaveAndQuery_Roundtrips()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres trace store 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, _, retrieval, _) = await ResolveStoresAsync(container, "trc_r_");
            await using (provider)
            {
                var now = DateTimeOffset.UtcNow;
                await retrieval.SaveAsync(MakeRetrievalTrace("rtr-1", "ws-r", "col-r", now));

                var results = await retrieval.QueryRecentAsync("ws-r", "col-r", 10);

                Assert.AreEqual(1, results.Count, "应查询到刚存入的检索 trace");
                var loaded = results[0];
                Assert.AreEqual("rtr-1", loaded.RetrievalId);
                Assert.AreEqual("检索查询", loaded.QueryText);
                Assert.AreEqual("改写后查询", loaded.RewrittenQueryText);
            }
        }
    }

    [TestMethod]
    public async Task PackageBuildTraceStore_SaveAndQuery_Roundtrips()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres trace store 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, _, _, package) = await ResolveStoresAsync(container, "trc_p_");
            await using (provider)
            {
                var now = DateTimeOffset.UtcNow;
                await package.SaveAsync(MakePackageBuildResult("build-1", "ws-p", "col-p", now));

                var results = await package.QueryRecentAsync("ws-p", "col-p", 10);

                Assert.AreEqual(1, results.Count, "应查询到刚存入的 package build trace");
                var loaded = results[0];
                Assert.AreEqual("build-1", loaded.BuildId);
                Assert.AreEqual("pkg-1", loaded.Package.PackageId);
                Assert.AreEqual("ws-p", loaded.Package.WorkspaceId);
            }
        }
    }

    [TestMethod]
    public async Task DecisionTraceStore_QueryOrdersNewestFirst_AndScopesByWorkspace()
    {
        var container = await TryStartPostgresAsync();
        if (container is null)
        {
            Assert.Inconclusive("Docker 不可用 — Postgres trace store 测试已跳过。");
            return;
        }

        await using (container)
        {
            var (provider, decision, _, _) = await ResolveStoresAsync(container, "trc_q_");
            await using (provider)
            {
                var now = DateTimeOffset.UtcNow;
                await decision.SaveAsync(MakeDecisionRecord("q-1", "ws-q", "col-q", "最早", now));
                await decision.SaveAsync(MakeDecisionRecord("q-2", "ws-q", "col-q", "中间", now.AddMinutes(1)));
                await decision.SaveAsync(MakeDecisionRecord("q-3", "ws-q", "col-q", "最新", now.AddMinutes(2)));
                await decision.SaveAsync(MakeDecisionRecord("q-x", "ws-other", "col-other", "其他工作区", now.AddMinutes(3)));

                var scoped = await decision.QueryRecentAsync("ws-q", "col-q", 10);
                Assert.AreEqual(3, scoped.Count, "查询应限定工作区与集合");
                Assert.AreEqual("q-3", scoped[0].DecisionId, "查询应按 created_at 新→旧排序");
                Assert.AreEqual("q-2", scoped[1].DecisionId);
                Assert.AreEqual("q-1", scoped[2].DecisionId);

                var other = await decision.QueryRecentAsync("ws-other", "col-other", 10);
                Assert.AreEqual(1, other.Count, "其他工作区的记录应独立可见");
                Assert.AreEqual("q-x", other[0].DecisionId);
            }
        }
    }

    // =========================================================================
    // 辅助
    // =========================================================================

    private static async Task<(ServiceProvider Provider, IDecisionTraceStore Decision, IRetrievalTraceStore Retrieval, IContextPackageBuildTraceStore Package)> ResolveStoresAsync(
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
        var decision = provider.GetRequiredService<IDecisionTraceStore>();
        var retrieval = provider.GetRequiredService<IRetrievalTraceStore>();
        var package = provider.GetRequiredService<IContextPackageBuildTraceStore>();
        Assert.IsInstanceOfType(decision, typeof(PostgresDecisionTraceStore),
            "AddContextCorePostgresStorage 必须把 IDecisionTraceStore 解析为 Postgres 实现。");
        Assert.IsInstanceOfType(retrieval, typeof(PostgresRetrievalTraceStore),
            "AddContextCorePostgresStorage 必须把 IRetrievalTraceStore 解析为 Postgres 实现。");
        Assert.IsInstanceOfType(package, typeof(PostgresContextPackageBuildTraceStore),
            "AddContextCorePostgresStorage 必须把 IContextPackageBuildTraceStore 解析为 Postgres 实现。");
        return (provider, decision, retrieval, package);
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
            Console.WriteLine($"[R31E_PostgresTraceStoreTests] Docker/Postgres 不可用：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static ContextDecisionRecord MakeDecisionRecord(string decisionId, string workspaceId, string collectionId, string queryText, DateTimeOffset createdAt) => new()
    {
        DecisionId = decisionId,
        Source = ContextDecisionSource.Package,
        WorkspaceId = workspaceId,
        CollectionId = collectionId,
        QueryText = queryText,
        Candidates =
        [
            new ContextDecisionCandidate
            {
                ItemId = "cand-1",
                Kind = "memory",
                Type = "note",
                Outcome = ContextDecisionCandidateOutcome.Selected,
                SectionName = "recent_context",
                Reason = "scored",
                Score = 0.9,
                EstimatedTokens = 100
            }
        ],
        Outcome = new ContextDecisionOutcome
        {
            SelectedCount = 2,
            DroppedCount = 0,
            EstimatedTokens = 100,
            TokenBudget = 1000,
            Sections = ["recent_context"]
        },
        Metadata = new Dictionary<string, string> { ["channel"] = "keyword" },
        CreatedAt = createdAt
    };

    private static ContextRetrievalTrace MakeRetrievalTrace(string retrievalId, string workspaceId, string collectionId, DateTimeOffset createdAt) => new()
    {
        RetrievalId = retrievalId,
        WorkspaceId = workspaceId,
        CollectionId = collectionId,
        QueryText = "检索查询",
        RewrittenQueryText = "改写后查询",
        Stages = [],
        Candidates = [],
        SelectedItems = [],
        DroppedItems = [],
        Metadata = new Dictionary<string, string> { ["channel"] = "keyword" },
        CreatedAt = createdAt
    };

    private static ContextPackageBuildResult MakePackageBuildResult(string buildId, string workspaceId, string collectionId, DateTimeOffset createdAt) => new()
    {
        BuildId = buildId,
        Package = new ContextPackage
        {
            PackageId = "pkg-1",
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Sections = [],
            EstimatedTokens = 100,
            CreatedAt = createdAt
        },
        CreatedAt = createdAt
    };
}
