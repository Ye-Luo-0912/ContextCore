using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

/// <summary>
/// R21-3：Memory Evolution 实现测试（InMemorySupersededItemStore +
/// DefaultConsolidationETL + InMemoryUtilityLedgerStore + InMemoryConflictSetStore +
/// UtilityLedgerMaterializer）。
///
/// 验证目标：
///   1. InMemorySupersededItemStore：append-only / 查询 / 最新状态 / 最近事件
///   2. InMemorySupersededItemStore：EventId �一性 + NewState != Active
///   3. DefaultConsolidationETL：DryRun / 状态机推进 Superseded→Replaced→Archived / 幂等
///   4. DefaultConsolidationETL：ItemType 过滤 / BatchSize 限制
///   5. InMemoryUtilityLedgerStore：append + query + latest + expert contributions
///   6. InMemoryConflictSetStore：append + query + get + conflicts-for-candidate
///   7. UtilityLedgerMaterializer：selected + dropped envelopes 物化
///   8. UtilityLedgerMaterializer：ConflictSet 检测（Duplicate / SectionConflict / BudgetConflict）
///   9. UtilityLedgerMaterializer：UtilityContribution 计算
///  10. 端到端：DecisionResult → Materializer → Stores 查询验证
/// </summary>
[TestClass]
[TestCategory("R21")]
public sealed class MemoryEvolutionImplementationTests
{
    // =========================================================================
    // 1. InMemorySupersededItemStore 基础功能
    // =========================================================================

    [TestMethod]
    public async Task SupersededItemStore_AppendEvent_StoresEvent()
    {
        var store = new InMemorySupersededItemStore();
        var evt = MakeSupersedeEvent("evt-1", "item-1", SupersededItemState.Superseded);

        await store.AppendEventAsync(evt);

        var events = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test"
        });
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("evt-1", events[0].EventId);
    }

    [TestMethod]
    public async Task SupersededItemStore_AppendEvent_DuplicateEventId_Throws()
    {
        var store = new InMemorySupersededItemStore();
        var evt1 = MakeSupersedeEvent("evt-1", "item-1", SupersededItemState.Superseded);
        var evt2 = MakeSupersedeEvent("evt-1", "item-2", SupersededItemState.Superseded);

        await store.AppendEventAsync(evt1);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.AppendEventAsync(evt2));
    }

    [TestMethod]
    public async Task SupersededItemStore_AppendEvent_ActiveNewState_Throws()
    {
        var store = new InMemorySupersededItemStore();
        var evt = MakeSupersedeEvent("evt-1", "item-1", SupersededItemState.Active);

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.AppendEventAsync(evt));
    }

    [TestMethod]
    public async Task SupersededItemStore_QueryEvents_FiltersByCollection()
    {
        var store = new InMemorySupersededItemStore();
        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", collectionId: "col-A"));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-2", "item-2", collectionId: "col-B"));

        var results = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-A"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("item-1", results[0].SourceItemId);
    }

    [TestMethod]
    public async Task SupersededItemStore_QueryEvents_FiltersBySince()
    {
        var store = new InMemorySupersededItemStore();
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var recent = DateTimeOffset.UtcNow.AddDays(-1);

        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", occurredAt: old));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-2", "item-2", occurredAt: recent));

        var results = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            Since = recent
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("item-2", results[0].SourceItemId);
    }

    [TestMethod]
    public async Task SupersededItemStore_QueryEvents_OrderByOccurredAtDesc()
    {
        var store = new InMemorySupersededItemStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);

        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", occurredAt: t1));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-2", "item-2", occurredAt: t2));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-3", "item-3", occurredAt: t3));

        var results = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            Take = 0
        });

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("evt-3", results[0].EventId);
        Assert.AreEqual("evt-2", results[1].EventId);
        Assert.AreEqual("evt-1", results[2].EventId);
    }

    [TestMethod]
    public async Task SupersededItemStore_GetLatestState_ReturnsLatestEvent()
    {
        var store = new InMemorySupersededItemStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);

        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", newState: SupersededItemState.Superseded, occurredAt: t1));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-2", "item-1", newState: SupersededItemState.Replaced, occurredAt: t2));

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");

        Assert.IsNotNull(latest);
        Assert.AreEqual(SupersededItemState.Replaced, latest.NewState);
    }

    [TestMethod]
    public async Task SupersededItemStore_GetLatestState_NoEvents_ReturnsNull()
    {
        var store = new InMemorySupersededItemStore();

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-never");

        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task SupersededItemStore_GetRecent_ReturnsLatestN()
    {
        var store = new InMemorySupersededItemStore();
        for (int i = 1; i <= 5; i++)
        {
            await store.AppendEventAsync(MakeSupersedeEvent(
                $"evt-{i}", $"item-{i}", occurredAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var recent = await store.GetRecentAsync("ws-test", "col-test", take: 3);

        Assert.AreEqual(3, recent.Count);
        Assert.AreEqual("evt-5", recent[0].EventId);
        Assert.AreEqual("evt-4", recent[1].EventId);
        Assert.AreEqual("evt-3", recent[2].EventId);
    }

    [TestMethod]
    public async Task SupersededItemStore_GetRecent_NegativeTake_Throws()
    {
        var store = new InMemorySupersededItemStore();

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => store.GetRecentAsync("ws-test", "col-test", take: -1));
    }

    // =========================================================================
    // 2. DefaultConsolidationETL
    // =========================================================================

    [TestMethod]
    public async Task ConsolidationETL_DryRun_ReturnsExtractedCountWithoutMutating()
    {
        var store = new InMemorySupersededItemStore();
        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", newState: SupersededItemState.Superseded));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-2", "item-2", newState: SupersededItemState.Superseded));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            DryRun = true
        });

        Assert.AreEqual(2, result.ExtractedCount);
        Assert.AreEqual(0, result.TransformedCount);
        Assert.AreEqual(0, result.LoadedCount);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(2, result.ProcessedItemIds.Count);

        // 没有写入新事件
        var allEvents = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            Take = 0
        });
        Assert.AreEqual(2, allEvents.Count);
    }

    [TestMethod]
    public async Task ConsolidationETL_Transform_PushesSupersededToReplaced()
    {
        var store = new InMemorySupersededItemStore();
        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", newState: SupersededItemState.Superseded));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            DryRun = false
        });

        Assert.AreEqual(1, result.ExtractedCount);
        Assert.AreEqual(1, result.TransformedCount);
        Assert.AreEqual(0, result.LoadedCount); // Replaced 状态不直接到 Archived，需要二次 ETL
        Assert.IsTrue(result.IsSuccess);

        var replaced = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(replaced);
        Assert.AreEqual(SupersededItemState.Replaced, replaced.NewState);
        Assert.AreEqual("consolidation-etl", replaced.Reason);
    }

    [TestMethod]
    public async Task ConsolidationETL_Load_PushesReplacedToArchived()
    {
        var store = new InMemorySupersededItemStore();
        // 直接写入 Replaced 状态事件（模拟 Transform 阶段已完成）
        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", newState: SupersededItemState.Replaced));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            DryRun = false
        });

        Assert.AreEqual(1, result.ExtractedCount);
        Assert.AreEqual(0, result.TransformedCount); // 没有 Superseded 状态需要推进
        Assert.AreEqual(1, result.LoadedCount);

        var archived = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(archived);
        Assert.AreEqual(SupersededItemState.Archived, archived.NewState);
    }

    [TestMethod]
    public async Task ConsolidationETL_Idempotent_DoesNotReProcessArchived()
    {
        var store = new InMemorySupersededItemStore();
        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", newState: SupersededItemState.Superseded));

        var etl = new DefaultConsolidationETL(store);
        await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test"
        });

        var firstRunEvents = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            Take = 0
        });

        // 第二次 ETL：从 Replaced 推进到 Archived
        await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test"
        });

        var secondRunEvents = await store.QueryEventsAsync(new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            Take = 0
        });

        // 第二次运行：新增 1 个 Archived 事件（Replaced → Archived）
        Assert.AreEqual(firstRunEvents.Count + 1, secondRunEvents.Count);

        // 第三次 ETL：所有 item 已 Archived，没有 Superseded/Replaced 状态可处理
        var thirdResult = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test"
        });
        Assert.AreEqual(0, thirdResult.ExtractedCount);
    }

    [TestMethod]
    public async Task ConsolidationETL_FilterByItemType()
    {
        var store = new InMemorySupersededItemStore();
        await store.AppendEventAsync(MakeSupersedeEvent("evt-1", "item-1", itemType: "memory", newState: SupersededItemState.Superseded));
        await store.AppendEventAsync(MakeSupersedeEvent("evt-2", "item-2", itemType: "constraint", newState: SupersededItemState.Superseded));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            ItemTypes = new[] { "memory" }
        });

        Assert.AreEqual(1, result.ExtractedCount);
        Assert.AreEqual(1, result.TransformedCount);
    }

    [TestMethod]
    public async Task ConsolidationETL_BatchSize_LimitsProcessing()
    {
        var store = new InMemorySupersededItemStore();
        for (int i = 1; i <= 5; i++)
        {
            await store.AppendEventAsync(MakeSupersedeEvent(
                $"evt-{i}", $"item-{i}", newState: SupersededItemState.Superseded,
                occurredAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            // OlderThan 默认 UtcNow（请求创建时）会漏掉稍后写入的事件，显式指定未来时间
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10),
            BatchSize = 2
        });

        Assert.AreEqual(2, result.ExtractedCount);
        Assert.AreEqual(2, result.TransformedCount);
    }

    // =========================================================================
    // 3. InMemoryUtilityLedgerStore
    // =========================================================================

    [TestMethod]
    public async Task UtilityLedgerStore_AppendEntries_QueryAsync()
    {
        var store = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("ledger-1", "item-1", RetrievalExpert.Semantic, isSelected: true, materializedAt: now),
            MakeLedgerEntry("ledger-2", "item-2", RetrievalExpert.Lexical, isSelected: false, materializedAt: now.AddSeconds(1))
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-test" });

        Assert.AreEqual(2, results.Count);
        // 降序：latest first
        Assert.AreEqual("ledger-2", results[0].EntryId);
        Assert.AreEqual("ledger-1", results[1].EntryId);
    }

    [TestMethod]
    public async Task UtilityLedgerStore_QueryAsync_FiltersByExpert()
    {
        var store = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("ledger-1", "item-1", RetrievalExpert.Semantic, materializedAt: now),
            MakeLedgerEntry("ledger-2", "item-2", RetrievalExpert.Lexical, materializedAt: now)
        });

        var results = await store.QueryAsync(new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            Expert = RetrievalExpert.Lexical
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(RetrievalExpert.Lexical, results[0].Expert);
    }

    [TestMethod]
    public async Task UtilityLedgerStore_GetLatestEntryAsync_ReturnsLatestByCandidate()
    {
        var store = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("ledger-1", "item-1", materializedAt: now),
            MakeLedgerEntry("ledger-2", "item-1", materializedAt: now.AddSeconds(1))
        });

        var latest = await store.GetLatestEntryAsync("ws-test", "col-test", "item-1");

        Assert.IsNotNull(latest);
        Assert.AreEqual("ledger-2", latest.EntryId);
    }

    [TestMethod]
    public async Task UtilityLedgerStore_GetLatestEntryAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryUtilityLedgerStore();

        var latest = await store.GetLatestEntryAsync("ws-test", "col-test", "item-never");

        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task UtilityLedgerStore_GetExpertContributionsAsync_GroupsByExpert()
    {
        var store = new InMemoryUtilityLedgerStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendEntries(new[]
        {
            MakeLedgerEntry("ledger-1", "item-1", RetrievalExpert.Semantic, utilityContribution: 0.4, materializedAt: now),
            MakeLedgerEntry("ledger-2", "item-1", RetrievalExpert.Semantic, utilityContribution: 0.6, materializedAt: now.AddSeconds(1)),
            MakeLedgerEntry("ledger-3", "item-1", RetrievalExpert.Lexical, utilityContribution: 0.2, materializedAt: now)
        });

        var contributions = await store.GetExpertContributionsAsync("ws-test", "col-test", "item-1");

        Assert.AreEqual(2, contributions.Count);
        // Semantic: (0.4 + 0.6) / 2 = 0.5
        Assert.AreEqual(0.5, contributions[RetrievalExpert.Semantic], 0.001);
        // Lexical: 0.2
        Assert.AreEqual(0.2, contributions[RetrievalExpert.Lexical], 0.001);
    }

    // =========================================================================
    // 4. InMemoryConflictSetStore
    // =========================================================================

    [TestMethod]
    public async Task ConflictSetStore_AppendConflictSets_QueryAsync()
    {
        var store = new InMemoryConflictSetStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("conflict-1", ConflictSetKind.Duplicate, materializedAt: now),
            MakeConflictSet("conflict-2", ConflictSetKind.SectionConflict, materializedAt: now.AddSeconds(1))
        });

        var results = await store.QueryAsync(new ConflictSetQuery { WorkspaceId = "ws-test" });

        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("conflict-2", results[0].ConflictSetId);
    }

    [TestMethod]
    public async Task ConflictSetStore_QueryAsync_FiltersByKind()
    {
        var store = new InMemoryConflictSetStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("conflict-1", ConflictSetKind.Duplicate, materializedAt: now),
            MakeConflictSet("conflict-2", ConflictSetKind.SectionConflict, materializedAt: now)
        });

        var results = await store.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            Kind = ConflictSetKind.SectionConflict
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(ConflictSetKind.SectionConflict, results[0].Kind);
    }

    [TestMethod]
    public async Task ConflictSetStore_GetAsync_ReturnsConflictSet()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("conflict-1", ConflictSetKind.Duplicate)
        });

        var set = await store.GetAsync("ws-test", "col-test", "conflict-1");

        Assert.IsNotNull(set);
        Assert.AreEqual(ConflictSetKind.Duplicate, set.Kind);
    }

    [TestMethod]
    public async Task ConflictSetStore_GetAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryConflictSetStore();

        var set = await store.GetAsync("ws-test", "col-test", "conflict-never");

        Assert.IsNull(set);
    }

    [TestMethod]
    public async Task ConflictSetStore_GetConflictsForCandidateAsync_ReturnsMatchingSets()
    {
        var store = new InMemoryConflictSetStore();
        store.AppendConflictSets(new[]
        {
            MakeConflictSet("conflict-1", ConflictSetKind.Duplicate, entries: new[]
            {
                new ConflictSetEntry { CandidateItemId = "item-1", Expert = RetrievalExpert.Semantic, Score = 0.9, IsSelected = true },
                new ConflictSetEntry { CandidateItemId = "item-2", Expert = RetrievalExpert.Lexical, Score = 0.8, IsSelected = false }
            }),
            MakeConflictSet("conflict-2", ConflictSetKind.SectionConflict, entries: new[]
            {
                new ConflictSetEntry { CandidateItemId = "item-3", Expert = RetrievalExpert.Graph, Score = 0.7, IsSelected = false }
            })
        });

        var results = await store.GetConflictsForCandidateAsync("ws-test", "col-test", "item-1");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("conflict-1", results[0].ConflictSetId);
    }

    // =========================================================================
    // 5. UtilityLedgerMaterializer 基础物化
    // =========================================================================

    [TestMethod]
    public async Task Materializer_MaterializeAsync_SelectedEnvelopes_WritesLedgerEntries()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: true),
                MakeEnvelope("item-2", ContextCandidateSource.Lexical, isSelected: true)
            }
        };

        var matResult = await materializer.MaterializeAsync(result, "ws-test", "col-test");

        Assert.AreEqual(2, matResult.LedgerEntryCount);
        Assert.AreEqual(0, matResult.ConflictSetCount); // 无 drop / 无冲突

        var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-test" });
        Assert.AreEqual(2, entries.Count);
        Assert.IsTrue(entries.All(e => e.IsSelected));
        Assert.IsTrue(entries.All(e => e.DecisionId == "decision-1"));
    }

    [TestMethod]
    public async Task Materializer_MaterializeAsync_DroppedEnvelopes_WriteLedgerEntriesWithDropReason()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Lexical,
                    isSelected: false, blockReasonCode: CandidateDecisionReasonCode.TokenBudgetExceeded),
                MakeEnvelope("item-2", ContextCandidateSource.Graph,
                    isSelected: false, blockReasonCode: CandidateDecisionReasonCode.SectionQuotaExceeded)
            }
        };

        var matResult = await materializer.MaterializeAsync(result, "ws-test", "col-test");

        Assert.AreEqual(2, matResult.LedgerEntryCount);

        var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-test" });
        Assert.IsTrue(entries.All(e => !e.IsSelected));
        Assert.IsTrue(entries.Any(e => e.DropReasonCode == "TokenBudgetExceeded"));
        Assert.IsTrue(entries.Any(e => e.DropReasonCode == "SectionQuotaExceeded"));
    }

    // =========================================================================
    // 6. ConflictSet 检测
    // =========================================================================

    [TestMethod]
    public async Task Materializer_DetectsDuplicateConflictSet()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: true, isDuplicate: true)
            },
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("item-2", ContextCandidateSource.Lexical, isSelected: false, isDuplicate: true,
                    blockReasonCode: CandidateDecisionReasonCode.DuplicateSuppressed)
            }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var conflicts = await conflictStore.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            Kind = ConflictSetKind.Duplicate
        });

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(2, conflicts[0].Entries.Count);
        Assert.AreEqual("item-1", conflicts[0].ResolvedItemId); // selected item wins
    }

    [TestMethod]
    public async Task Materializer_DetectsSectionConflictSet()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: false,
                    blockReasonCode: CandidateDecisionReasonCode.SectionQuotaExceeded),
                MakeEnvelope("item-2", ContextCandidateSource.Lexical, isSelected: false,
                    blockReasonCode: CandidateDecisionReasonCode.SectionQuotaExceeded)
            }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var conflicts = await conflictStore.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            Kind = ConflictSetKind.SectionConflict
        });

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(2, conflicts[0].Entries.Count);
        Assert.IsNull(conflicts[0].ResolvedItemId); // 全部 drop
    }

    [TestMethod]
    public async Task Materializer_DetectsBudgetConflictSet()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: true)
            },
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("item-2", ContextCandidateSource.Lexical, isSelected: false,
                    blockReasonCode: CandidateDecisionReasonCode.TokenBudgetExceeded),
                MakeEnvelope("item-3", ContextCandidateSource.Graph, isSelected: false,
                    blockReasonCode: CandidateDecisionReasonCode.TokenBudgetExceeded)
            }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var conflicts = await conflictStore.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            Kind = ConflictSetKind.BudgetConflict
        });

        Assert.AreEqual(1, conflicts.Count);
        Assert.AreEqual(2, conflicts[0].Entries.Count);
    }

    [TestMethod]
    public async Task Materializer_NoConflictSet_WhenSingleDropOnly()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Lexical, isSelected: false,
                    blockReasonCode: CandidateDecisionReasonCode.TokenBudgetExceeded)
            }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var conflicts = await conflictStore.QueryAsync(new ConflictSetQuery { WorkspaceId = "ws-test" });
        Assert.AreEqual(0, conflicts.Count); // 单条不构成冲突
    }

    // =========================================================================
    // 7. UtilityContribution 计算
    // =========================================================================

    [TestMethod]
    public async Task Materializer_ComputeUtilityContribution_FromScoreBreakdown()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        // Semantic Expert 贡献 = 0.6 / (0.6 + 0.4) = 0.6
        var envelope = MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: true);
        envelope = envelope with
        {
            Features = envelope.Features with
            {
                ScoreBreakdown = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { "semantic", 0.6 },
                    { "lexical", 0.4 }
                }
            }
        };

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = new[] { envelope }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var entry = await ledgerStore.GetLatestEntryAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(entry);
        Assert.AreEqual(0.6, entry.UtilityContribution, 0.001);
    }

    [TestMethod]
    public async Task Materializer_ComputeUtilityContribution_NoBreakdown_ReturnsOne()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var envelope = MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: true);
        // 不设 ScoreBreakdown（默认空字典）

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = new[] { envelope }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var entry = await ledgerStore.GetLatestEntryAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(entry);
        Assert.AreEqual(1.0, entry.UtilityContribution);
    }

    // =========================================================================
    // 8. 端到端：CandidateId source-expert 映射
    // =========================================================================

    [TestMethod]
    public async Task Materializer_MapsCandidateSourceToExpert()
    {
        var ledgerStore = new InMemoryUtilityLedgerStore();
        var conflictStore = new InMemoryConflictSetStore();
        var materializer = new UtilityLedgerMaterializer(ledgerStore, conflictStore);

        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.Semantic, isSelected: true),
                MakeEnvelope("item-2", ContextCandidateSource.Lexical, isSelected: true),
                MakeEnvelope("item-3", ContextCandidateSource.Graph, isSelected: true),
                MakeEnvelope("item-4", ContextCandidateSource.Mandatory, isSelected: true),
                MakeEnvelope("item-5", ContextCandidateSource.Constraint, isSelected: true)
            }
        };

        await materializer.MaterializeAsync(result, "ws-test", "col-test");

        var entries = await ledgerStore.QueryAsync(new UtilityLedgerQuery { WorkspaceId = "ws-test" });
        var byItemId = entries.ToDictionary(e => e.CandidateItemId);

        Assert.AreEqual(RetrievalExpert.Semantic, byItemId["item-1"].Expert);
        Assert.AreEqual(RetrievalExpert.Lexical, byItemId["item-2"].Expert);
        Assert.AreEqual(RetrievalExpert.Graph, byItemId["item-3"].Expert);
        Assert.AreEqual(RetrievalExpert.Mandatory, byItemId["item-4"].Expert);
        Assert.AreEqual(RetrievalExpert.Constraint, byItemId["item-5"].Expert);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static SupersedeEventRecord MakeSupersedeEvent(
        string eventId = "evt-test",
        string sourceItemId = "item-source",
        SupersededItemState newState = SupersededItemState.Superseded,
        string itemType = "memory",
        string reason = "lifecycle-review",
        string collectionId = "col-test",
        DateTimeOffset? occurredAt = null)
    {
        return new SupersedeEventRecord
        {
            EventId = eventId,
            WorkspaceId = "ws-test",
            CollectionId = collectionId,
            SourceItemId = sourceItemId,
            TargetItemId = null,
            ItemType = itemType,
            NewState = newState,
            Reason = reason,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };
    }

    private static UtilityLedgerEntry MakeLedgerEntry(
        string entryId = "ledger-1",
        string candidateItemId = "item-1",
        RetrievalExpert expert = RetrievalExpert.Semantic,
        bool isSelected = true,
        double utilityContribution = 0.5,
        DateTimeOffset? materializedAt = null)
    {
        return new UtilityLedgerEntry
        {
            EntryId = entryId,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            CandidateItemId = candidateItemId,
            Expert = expert,
            UtilityContribution = utilityContribution,
            DeterministicScore = 0.8,
            ModelScore = null,
            FinalScore = 0.9,
            IsSelected = isSelected,
            DecisionId = "decision-1",
            PolicyVersion = "decision-schema/2.0",
            MaterializedAt = materializedAt ?? DateTimeOffset.UtcNow
        };
    }

    private static ConflictSet MakeConflictSet(
        string conflictSetId = "conflict-1",
        ConflictSetKind kind = ConflictSetKind.Duplicate,
        ConflictSetEntry[]? entries = null,
        DateTimeOffset? materializedAt = null)
    {
        entries ??= new[]
        {
            new ConflictSetEntry
            {
                CandidateItemId = "item-1",
                Expert = RetrievalExpert.Semantic,
                Score = 0.9,
                IsSelected = true
            },
            new ConflictSetEntry
            {
                CandidateItemId = "item-2",
                Expert = RetrievalExpert.Lexical,
                Score = 0.8,
                IsSelected = false,
                DropReasonCode = "DuplicateSuppressed"
            }
        };

        return new ConflictSet
        {
            ConflictSetId = conflictSetId,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Kind = kind,
            Entries = entries,
            DecisionId = "decision-1",
            MaterializedAt = materializedAt ?? DateTimeOffset.UtcNow
        };
    }

    private static ContextCandidateEnvelope MakeEnvelope(
        string candidateId,
        ContextCandidateSource source,
        bool isSelected = true,
        bool isDuplicate = false,
        CandidateDecisionReasonCode blockReasonCode = CandidateDecisionReasonCode.Unknown)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            Source = source,
            Type = "memory",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Features = new CandidateFeatureVector
            {
                ScoreBreakdown = new Dictionary<string, double>(StringComparer.Ordinal)
            },
            Safety = new CandidateSafetyState
            {
                IsDuplicate = isDuplicate,
                PassesSafetyGate = isSelected,
                BlockReasonCode = blockReasonCode
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = 0.8,
                ModelScore = null,
                FinalScore = 0.9,
                ReasonCode = "deterministic-only"
            }
        };
    }
}
