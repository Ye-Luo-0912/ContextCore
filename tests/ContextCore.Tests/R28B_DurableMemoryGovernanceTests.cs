using ContextCore.Abstractions;
using ContextCore.Core.Services.MemoryEvolution;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Extensions;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace ContextCore.Tests;

/// <summary>
/// R28-E：Durable Memory Governance Persistence 验收测试。
///
/// 覆盖：
///   1. InMemoryUtilityLedgerStore：写入 / 查询 / 过滤 / 排序 / Take / GetLatestEntry / GetExpertContributions / 参数校验
///   2. InMemoryConflictSetStore：写入 / 查询 / 过滤 / GetAsync / GetConflictsForCandidate / 参数校验
///   3. PostgresUtilityLedgerStore：构造 / 参数校验 / cancellation 透传 / DI 注册
///   4. PostgresConflictSetStore：构造 / 参数校验 / cancellation 透传 / DI 注册
///   5. Schema 迁移验证：v20 版本 + 新表/索引定义存在
///
/// 不连接真实 PostgreSQL 数据库；仅验证：
///   - InMemory store 的完整读写语义（含 internal AppendEntries / AppendConflictSets 写入路径）
///   - Postgres store 的参数校验在 EnsureMigrated 之前抛出（无需连接）
///   - DI 注册路径（PostgresServiceCollectionExtensions）
///   - Schema 版本与表/索引定义完整性
///
/// 端到端 Postgres 持久化语义（BulkInsert / Query / jsonb 包含查询）由
/// ContextCore.IntegrationTests 覆盖（需 Testcontainers），与
/// PostgresAgentCheckpointStoreTests / PostgresPipelineRunStoreTests 约定一致。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("R28")]
public sealed class R28B_DurableMemoryGovernanceTests
{
    // =========================================================================
    // Part 1: InMemoryUtilityLedgerStore 功能测试
    // =========================================================================

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_ReturnsEntriesInDescendingOrder()
    {
        var store = new InMemoryUtilityLedgerStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, materializedAt: t1),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Lexical, materializedAt: t3),
            MakeLedgerEntry("e-3", "item-1", RetrievalExpert.Lexical, materializedAt: t2)
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-test" });

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("e-2", results[0].EntryId); // t3 最新
        Assert.AreEqual("e-3", results[1].EntryId); // t2
        Assert.AreEqual("e-1", results[2].EntryId); // t1 最旧
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_FiltersByCollectionId()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, collectionId: "col-a"),
            MakeLedgerEntry("e-2", "item-2", RetrievalExpert.Lexical, collectionId: "col-b")
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-a"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("e-1", results[0].EntryId);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_FiltersByCandidateItemId()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical),
            MakeLedgerEntry("e-2", "item-2", RetrievalExpert.Lexical)
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            CandidateItemId = "item-2"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("e-2", results[0].EntryId);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_FiltersByExpert()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Semantic)
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            Expert = RetrievalExpert.Semantic
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("e-2", results[0].EntryId);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_FiltersByDecisionId()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, decisionId: "dec-a"),
            MakeLedgerEntry("e-2", "item-2", RetrievalExpert.Lexical, decisionId: "dec-b")
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            DecisionId = "dec-a"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("e-1", results[0].EntryId);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_FiltersByIsSelected()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, isSelected: true),
            MakeLedgerEntry("e-2", "item-2", RetrievalExpert.Lexical, isSelected: false)
        });

        var selected = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            IsSelected = true
        });
        var dropped = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            IsSelected = false
        });

        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual("e-1", selected[0].EntryId);
        Assert.AreEqual(1, dropped.Count);
        Assert.AreEqual("e-2", dropped[0].EntryId);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_FiltersBySinceUntil()
    {
        var store = new InMemoryUtilityLedgerStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(10);
        var t3 = t1.AddSeconds(20);
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, materializedAt: t1),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Lexical, materializedAt: t2),
            MakeLedgerEntry("e-3", "item-1", RetrievalExpert.Lexical, materializedAt: t3)
        });

        var sinceResults = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            Since = t2
        });
        var untilResults = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            Until = t2
        });

        Assert.AreEqual(2, sinceResults.Count); // t2, t3
        Assert.AreEqual(2, untilResults.Count); // t1, t2
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_RespectsTakeLimit()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Lexical),
            MakeLedgerEntry("e-3", "item-1", RetrievalExpert.Lexical)
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            Take = 2
        });

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_TakeZero_ReturnsAll()
    {
        var store = new InMemoryUtilityLedgerStore();
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Lexical),
            MakeLedgerEntry("e-3", "item-1", RetrievalExpert.Lexical)
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            Take = 0
        });

        Assert.AreEqual(3, results.Count);
    }

    [TestMethod]
    public async Task UtilityLedger_GetLatestEntryAsync_ReturnsLatestByMaterializedAt()
    {
        var store = new InMemoryUtilityLedgerStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(5);
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, materializedAt: t1),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Semantic, materializedAt: t2)
        });

        var latest = await store.GetLatestEntryAsync("ws-test", "col-test", "item-1");

        Assert.IsNotNull(latest);
        Assert.AreEqual("e-2", latest.EntryId);
        Assert.AreEqual(RetrievalExpert.Semantic, latest.Expert);
    }

    [TestMethod]
    public async Task UtilityLedger_GetLatestEntryAsync_NoEntries_ReturnsNull()
    {
        var store = new InMemoryUtilityLedgerStore();

        var latest = await store.GetLatestEntryAsync("ws-test", "col-test", "item-never");

        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task UtilityLedger_GetExpertContributionsAsync_ReturnsAveragePerExpert()
    {
        var store = new InMemoryUtilityLedgerStore();
        // Lexical: 0.4 + 0.6 = avg 0.5
        // Semantic: 0.8 = avg 0.8
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("e-1", "item-1", RetrievalExpert.Lexical, utilityContribution: 0.4),
            MakeLedgerEntry("e-2", "item-1", RetrievalExpert.Lexical, utilityContribution: 0.6),
            MakeLedgerEntry("e-3", "item-1", RetrievalExpert.Semantic, utilityContribution: 0.8)
        });

        var contributions = await store.GetExpertContributionsAsync("ws-test", "col-test", "item-1");

        Assert.AreEqual(2, contributions.Count);
        Assert.AreEqual(0.5, contributions[RetrievalExpert.Lexical], 0.001);
        Assert.AreEqual(0.8, contributions[RetrievalExpert.Semantic], 0.001);
    }

    [TestMethod]
    public async Task UtilityLedger_GetExpertContributionsAsync_NoEntries_ReturnsEmpty()
    {
        var store = new InMemoryUtilityLedgerStore();

        var contributions = await store.GetExpertContributionsAsync("ws-test", "col-test", "item-never");

        Assert.AreEqual(0, contributions.Count);
    }

    [TestMethod]
    public async Task UtilityLedger_QueryAsync_NullQuery_Throws()
    {
        var store = new InMemoryUtilityLedgerStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.QueryAsync(null!));
    }

    [TestMethod]
    public async Task UtilityLedger_GetLatestEntryAsync_NullWorkspaceId_Throws()
    {
        var store = new InMemoryUtilityLedgerStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetLatestEntryAsync(null!, "col", "item"));
    }

    [TestMethod]
    public async Task UtilityLedger_GetLatestEntryAsync_EmptyCollectionId_Throws()
    {
        var store = new InMemoryUtilityLedgerStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetLatestEntryAsync("ws", "", "item"));
    }

    [TestMethod]
    public async Task UtilityLedger_GetExpertContributionsAsync_WhitespaceCandidateItemId_Throws()
    {
        var store = new InMemoryUtilityLedgerStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetExpertContributionsAsync("ws", "col", "   "));
    }

    // =========================================================================
    // Part 2: InMemoryConflictSetStore 功能测试
    // =========================================================================

    [TestMethod]
    public async Task ConflictSet_QueryAsync_ReturnsConflictSetsInDescendingOrder()
    {
        var store = new InMemoryConflictSetStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1", materializedAt: t1),
            MakeConflictSet("c-2", materializedAt: t2)
        });

        var results = await store.QueryAsync(new ConflictSetQuery { WorkspaceId = "ws-test" });

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("c-2", results[0].ConflictSetId); // t2 最新
        Assert.AreEqual("c-1", results[1].ConflictSetId);
    }

    [TestMethod]
    public async Task ConflictSet_QueryAsync_FiltersByKind()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1", kind: ConflictSetKind.Duplicate),
            MakeConflictSet("c-2", kind: ConflictSetKind.SectionConflict)
        });

        var results = await store.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            Kind = ConflictSetKind.SectionConflict
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("c-2", results[0].ConflictSetId);
    }

    [TestMethod]
    public async Task ConflictSet_QueryAsync_FiltersByCandidateItemId()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1", entries: MakeEntries(("item-a", RetrievalExpert.Lexical), ("item-b", RetrievalExpert.Semantic))),
            MakeConflictSet("c-2", entries: MakeEntries(("item-c", RetrievalExpert.Graph), ("item-d", RetrievalExpert.Lexical)))
        });

        var results = await store.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            CandidateItemId = "item-b"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("c-1", results[0].ConflictSetId);
    }

    [TestMethod]
    public async Task ConflictSet_QueryAsync_FiltersByResolutionStatus()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1", resolutionStatus: ConflictResolutionStatus.AutoResolved),
            MakeConflictSet("c-2", resolutionStatus: ConflictResolutionStatus.Unresolved)
        });

        var results = await store.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            ResolutionStatus = ConflictResolutionStatus.AutoResolved
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("c-1", results[0].ConflictSetId);
    }

    [TestMethod]
    public async Task ConflictSet_GetAsync_ReturnsMatchingConflictSet()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1"),
            MakeConflictSet("c-2")
        });

        var result = await store.GetAsync("ws-test", "col-test", "c-2");

        Assert.IsNotNull(result);
        Assert.AreEqual("c-2", result.ConflictSetId);
    }

    [TestMethod]
    public async Task ConflictSet_GetAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryConflictSetStore();

        var result = await store.GetAsync("ws-test", "col-test", "c-missing");

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ConflictSet_GetConflictsForCandidateAsync_ReturnsRelatedConflicts()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1", entries: MakeEntries(("item-a", RetrievalExpert.Lexical), ("item-b", RetrievalExpert.Semantic))),
            MakeConflictSet("c-2", entries: MakeEntries(("item-c", RetrievalExpert.Graph), ("item-d", RetrievalExpert.Lexical))),
            MakeConflictSet("c-3", entries: MakeEntries(("item-a", RetrievalExpert.Semantic), ("item-c", RetrievalExpert.Lexical)))
        });

        var results = await store.GetConflictsForCandidateAsync("ws-test", "col-test", "item-a");

        Assert.AreEqual(2, results.Count); // c-1 和 c-3 都包含 item-a
        Assert.IsTrue(results.Any(c => c.ConflictSetId == "c-1"));
        Assert.IsTrue(results.Any(c => c.ConflictSetId == "c-3"));
    }

    [TestMethod]
    public async Task ConflictSet_GetConflictsForCandidateAsync_NoMatch_ReturnsEmpty()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("c-1", entries: MakeEntries(("item-a", RetrievalExpert.Lexical), ("item-b", RetrievalExpert.Semantic)))
        });

        var results = await store.GetConflictsForCandidateAsync("ws-test", "col-test", "item-z");

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task ConflictSet_QueryAsync_NullQuery_Throws()
    {
        var store = new InMemoryConflictSetStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.QueryAsync(null!));
    }

    [TestMethod]
    public async Task ConflictSet_GetAsync_NullConflictSetId_Throws()
    {
        var store = new InMemoryConflictSetStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetAsync("ws", "col", null!));
    }

    [TestMethod]
    public async Task ConflictSet_GetConflictsForCandidateAsync_EmptyWorkspaceId_Throws()
    {
        var store = new InMemoryConflictSetStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetConflictsForCandidateAsync("", "col", "item"));
    }

    // =========================================================================
    // Part 3: PostgresUtilityLedgerStore 参数校验 + DI
    // =========================================================================

    [TestMethod]
    public void PostgresUtilityLedger_Constructor_ValidArguments_CreatesInstance()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        var store = new PostgresUtilityLedgerStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));

        Assert.IsInstanceOfType<IUtilityLedgerStore>(store);
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_QueryAsync_NullQuery_Throws()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.QueryAsync(null!));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetLatestEntryAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetLatestEntryAsync(null!, "col", "item"));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetLatestEntryAsync_EmptyCollectionId_ThrowsArgumentException()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetLatestEntryAsync("ws", "", "item"));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetLatestEntryAsync_WhitespaceCandidateItemId_ThrowsArgumentException()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetLatestEntryAsync("ws", "col", "   "));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetExpertContributionsAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetExpertContributionsAsync(null!, "col", "item"));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetExpertContributionsAsync_EmptyCollectionId_ThrowsArgumentException()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetExpertContributionsAsync("ws", "", "item"));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetExpertContributionsAsync_WhitespaceCandidateItemId_ThrowsArgumentException()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetExpertContributionsAsync("ws", "col", "   "));
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_QueryAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        // 已取消 token 传入时，调用不应 hang；EnsureMigratedAsync 不检查 cancellation，
        // OpenConnectionAsync 在 cancellation 已取消时立即抛 OperationCanceledException（Npgsql 内部检查）。
        var store = CreateUtilityLedgerStoreWithoutConnection();
        var query = new UtilityLedgerQuery { WorkspaceId = "ws-test" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.QueryAsync(query, cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    [TestMethod]
    public async Task PostgresUtilityLedger_GetLatestEntryAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        var store = CreateUtilityLedgerStoreWithoutConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.GetLatestEntryAsync("ws", "col", "item", cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_RegistersPostgresUtilityLedgerStore()
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(PostgresUtilityLedgerStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IUtilityLedgerStore)));
        // R29 WP-E-1：写契约 IUtilityLedger 也绑定到同一 Postgres singleton。
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IUtilityLedger)));

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IUtilityLedgerStore>();
        Assert.IsInstanceOfType<PostgresUtilityLedgerStore>(store);
        // IUtilityLedger 与 IUtilityLedgerStore 解析到同一 singleton 实例。
        var ledger = sp.GetService<IUtilityLedger>();
        Assert.IsInstanceOfType<PostgresUtilityLedgerStore>(ledger);
        Assert.AreSame(store, ledger);
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_PostgresUtilityLedgerOverridesInMemory()
    {
        // 模拟完整启动顺序 — 先注册 InMemory，再 AddContextCorePostgresStorage，后注册者胜出。
        var services = new ServiceCollection();
        services.AddSingleton<IUtilityLedgerStore, InMemoryUtilityLedgerStore>();
        services.AddSingleton<IUtilityLedger, InMemoryUtilityLedgerStore>();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IUtilityLedgerStore>();
        Assert.IsInstanceOfType<PostgresUtilityLedgerStore>(store);
        // IUtilityLedger 同样被 Postgres 实现覆盖。
        var ledger = sp.GetService<IUtilityLedger>();
        Assert.IsInstanceOfType<PostgresUtilityLedgerStore>(ledger);
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_RegistersPostgresConflictSetStoreAndWriteLedger()
    {
        // R29 WP-E-1：验证 IConflictSetStore + IConflictSetLedger 均绑定到 PostgresConflictSetStore singleton。
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(PostgresConflictSetStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IConflictSetStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IConflictSetLedger)));

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IConflictSetStore>();
        Assert.IsInstanceOfType<PostgresConflictSetStore>(store);
        var ledger = sp.GetService<IConflictSetLedger>();
        Assert.IsInstanceOfType<PostgresConflictSetStore>(ledger);
        Assert.AreSame(store, ledger);
    }

    // =========================================================================
    // Part 4: PostgresConflictSetStore 参数校验 + DI
    // =========================================================================

    [TestMethod]
    public void PostgresConflictSet_Constructor_ValidArguments_CreatesInstance()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        var store = new PostgresConflictSetStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));

        Assert.IsInstanceOfType<IConflictSetStore>(store);
    }

    [TestMethod]
    public async Task PostgresConflictSet_QueryAsync_NullQuery_Throws()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.QueryAsync(null!));
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetAsync(null!, "col", "c-1"));
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetAsync_EmptyCollectionId_ThrowsArgumentException()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws", "", "c-1"));
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetAsync_WhitespaceConflictSetId_ThrowsArgumentException()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws", "col", "   "));
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetConflictsForCandidateAsync_NullWorkspaceId_ThrowsArgumentNullException()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.GetConflictsForCandidateAsync(null!, "col", "item"));
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetConflictsForCandidateAsync_EmptyCollectionId_ThrowsArgumentException()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetConflictsForCandidateAsync("ws", "", "item"));
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetConflictsForCandidateAsync_WhitespaceCandidateItemId_ThrowsArgumentException()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetConflictsForCandidateAsync("ws", "col", "   "));
    }

    [TestMethod]
    public async Task PostgresConflictSet_QueryAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        var query = new ConflictSetQuery { WorkspaceId = "ws-test" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.QueryAsync(query, cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    [TestMethod]
    public async Task PostgresConflictSet_GetAsync_AlreadyCanceled_PropagatesCancellationOrConnectionFailure()
    {
        var store = CreateConflictSetStoreWithoutConnection();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await store.GetAsync("ws", "col", "c-1", cts.Token);
            Assert.Fail("Expected exception was not thrown.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or Npgsql.PostgresException or Npgsql.NpgsqlException)
        {
            // 预期路径：cancellation 透传或连接失败
        }
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_RegistersPostgresConflictSetStore()
    {
        var services = new ServiceCollection();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(PostgresConflictSetStore)));
        Assert.IsTrue(services.Any(s => s.ServiceType == typeof(IConflictSetStore)));

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IConflictSetStore>();
        Assert.IsInstanceOfType<PostgresConflictSetStore>(store);
    }

    [TestMethod]
    public async Task AddContextCorePostgresStorage_PostgresConflictSetOverridesInMemory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConflictSetStore, InMemoryConflictSetStore>();
        services.AddContextCorePostgresStorage(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });

        await using var sp = services.BuildServiceProvider();
        var store = sp.GetService<IConflictSetStore>();
        Assert.IsInstanceOfType<PostgresConflictSetStore>(store);
    }

    // =========================================================================
    // Part 5: Schema 迁移验证
    // =========================================================================

    [TestMethod]
    public void SchemaVersion_IsV30()
    {
        // P0-2：v29 → v30，kernel_result_outbox 追加 lease_owner / lease_expires_at / lease_token 列与配套索引，
        // 将 DequeueAsync 的 Dispatched 终态改为租约模型（Pending → Leased → Acked），
        // 避免 consumer 崩溃后 Dispatched 行永久滞留。
        // 历史：v28 → v29（P0-1：kernel_transport_inbox/outbox 租约模型）；
        //       v27 → v28（P0-3：tool_dispatch_journal_entries.idempotency_key UNIQUE partial index）；
        //       v26 → v27（WP-E-5：user_feedback_entries 表）；
        //       v25 → v26（WP-E-4：vw_utility_ledger_calibration_data 视图）。
        Assert.AreEqual("cc-schema-v30", PostgresMigrationRunner.SchemaVersion);
    }

    [TestMethod]
    public void RequiredTables_IncludeUtilityLedgerEntries()
    {
        Assert.IsTrue(PostgresMigrationRunner.RequiredOperationalTableSuffixes.Contains("utility_ledger_entries"));
    }

    [TestMethod]
    public void RequiredTables_IncludeConflictSets()
    {
        Assert.IsTrue(PostgresMigrationRunner.RequiredOperationalTableSuffixes.Contains("conflict_sets"));
    }

    [TestMethod]
    public void RequiredIndexes_IncludeUtilityLedgerIndexes()
    {
        var indexSuffixes = PostgresMigrationRunner.RequiredOperationalIndexDefinitions
            .Where(d => d.TableSuffix == "utility_ledger_entries")
            .Select(d => d.IndexSuffix)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "workspace", "candidate", "decision", "materialized" },
            indexSuffixes);
    }

    [TestMethod]
    public void RequiredIndexes_IncludeConflictSetIndexes()
    {
        var indexSuffixes = PostgresMigrationRunner.RequiredOperationalIndexDefinitions
            .Where(d => d.TableSuffix == "conflict_sets")
            .Select(d => d.IndexSuffix)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "workspace", "status", "candidate" },
            indexSuffixes);
    }

    // R29 WP-E-5：User Feedback Ledger 表与索引验证
    [TestMethod]
    public void RequiredTables_IncludeUserFeedbackEntries()
    {
        Assert.IsTrue(PostgresMigrationRunner.RequiredOperationalTableSuffixes.Contains("user_feedback_entries"));
    }

    [TestMethod]
    public void RequiredIndexes_IncludeUserFeedbackEntriesIndexes()
    {
        var indexSuffixes = PostgresMigrationRunner.RequiredOperationalIndexDefinitions
            .Where(d => d.TableSuffix == "user_feedback_entries")
            .Select(d => d.IndexSuffix)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "workspace", "decision", "candidate", "given_by", "given_at", "idempotency" },
            indexSuffixes);
    }

    [TestMethod]
    public void PostgresMigrationSql_IncludesUserFeedbackEntriesTable()
    {
        var sql = PostgresMigrationRunner.BuildMigrationSql(new PostgresOptions
        {
            ConnectionString = "Host=localhost;Database=contextcore;Username=contextcore;Password=contextcore",
            TablePrefix = "cc_",
            EnablePgVectorExtension = false
        });

        StringAssert.Contains(sql, "CREATE TABLE IF NOT EXISTS cc_user_feedback_entries");
        StringAssert.Contains(sql, "feedback_entry_id text NOT NULL");
        StringAssert.Contains(sql, "idempotency_key text NOT NULL");
        StringAssert.Contains(sql, "given_at timestamptz NOT NULL");
        StringAssert.Contains(sql, "CREATE INDEX IF NOT EXISTS ix_cc_user_feedback_entries_workspace");
        StringAssert.Contains(sql, "CREATE UNIQUE INDEX IF NOT EXISTS ix_cc_user_feedback_entries_idempotency");
        // JOIN 视图
        StringAssert.Contains(sql, "CREATE OR REPLACE VIEW cc_vw_utility_ledger_with_user_feedback");
        StringAssert.Contains(sql, "LEFT JOIN LATERAL");
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static UtilityLedgerEntry MakeLedgerEntry(
        string entryId,
        string candidateItemId,
        RetrievalExpert expert,
        string workspaceId = "ws-test",
        string collectionId = "col-test",
        double utilityContribution = 0.5,
        double deterministicScore = 0.7,
        double finalScore = 0.8,
        bool isSelected = true,
        string decisionId = "dec-test",
        string policyVersion = "policy-v1",
        DateTimeOffset? materializedAt = null) => new()
    {
        EntryId = entryId,
        WorkspaceId = workspaceId,
        CollectionId = collectionId,
        CandidateItemId = candidateItemId,
        Expert = expert,
        UtilityContribution = utilityContribution,
        DeterministicScore = deterministicScore,
        FinalScore = finalScore,
        IsSelected = isSelected,
        DecisionId = decisionId,
        PolicyVersion = policyVersion,
        MaterializedAt = materializedAt ?? DateTimeOffset.UtcNow
    };

    private static ConflictSet MakeConflictSet(
        string conflictSetId,
        string workspaceId = "ws-test",
        string collectionId = "col-test",
        ConflictSetKind kind = ConflictSetKind.Duplicate,
        string decisionId = "dec-test",
        ConflictResolutionStatus resolutionStatus = ConflictResolutionStatus.Unresolved,
        DateTimeOffset? materializedAt = null,
        ConflictSetEntry[]? entries = null) => new()
    {
        ConflictSetId = conflictSetId,
        WorkspaceId = workspaceId,
        CollectionId = collectionId,
        Kind = kind,
        Entries = entries ?? MakeEntries(("item-a", RetrievalExpert.Lexical), ("item-b", RetrievalExpert.Semantic)),
        DecisionId = decisionId,
        ResolutionStatus = resolutionStatus,
        MaterializedAt = materializedAt ?? DateTimeOffset.UtcNow
    };

    private static ConflictSetEntry[] MakeEntries(params (string CandidateItemId, RetrievalExpert Expert)[] entries)
    {
        return entries.Select(e => new ConflictSetEntry
        {
            CandidateItemId = e.CandidateItemId,
            Expert = e.Expert,
            Score = 0.8,
            IsSelected = true
        }).ToArray();
    }

    private static PostgresUtilityLedgerStore CreateUtilityLedgerStoreWithoutConnection()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        return new PostgresUtilityLedgerStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));
    }

    private static PostgresConflictSetStore CreateConflictSetStoreWithoutConnection()
    {
        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            Enabled = false,
            ConnectionString = "Host=localhost;Database=x;Username=x;Password=x",
            AutoMigrate = false
        });
        return new PostgresConflictSetStore(factory, new PostgresJsonSerializer(), new PostgresMigrationRunner(factory));
    }
}
