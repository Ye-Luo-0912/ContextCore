using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.MemoryEvolution;

namespace ContextCore.Tests;

/// <summary>
/// R21-4/R21-5：Memory Evolution 实现测试。
///
/// 覆盖：
///   1. InMemoryMemoryStateStore：append-only / 查询 / 最新状态 / 最近事件 / EventId 唯一性 / NewState != Fresh
///   2. DefaultConsolidationETL：DryRun / Superseded→Replaced→Archived / Dormant→Archived / 幂等 / ItemType 过滤 / BatchSize
///   3. DefaultMemoryDecayEvaluator：NoEffectiveContribution / LongTermNoHit（Cooling/Dormant/Archived）/ 终态不评估 / 状态机非法转换
///   4. InMemoryMemoryUtilityStatsStore：UpsertSnapshot / QueryAsync / GetAsync / 过滤条件
///   5. InMemoryConflictSetStore + ConflictResolutionStatus 过滤
///   6. UtilityLedgerMaterializer + ConflictSet ResolutionStatus 填充
/// </summary>
[TestClass]
[TestCategory("R21")]
public sealed class MemoryEvolutionImplementationTests
{
    // =========================================================================
    // 1. InMemoryMemoryStateStore
    // =========================================================================

    [TestMethod]
    public async Task MemoryStateStore_AppendEvent_StoresEvent()
    {
        var store = new InMemoryMemoryStateStore();
        var evt = MakeStateEvent("evt-1", "item-1", MemoryState.Superseded);

        await store.AppendEventAsync(evt);

        var events = await store.QueryEventsAsync(new MemoryStateEventQuery { WorkspaceId = "ws-test" });
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("evt-1", events[0].EventId);
    }

    [TestMethod]
    public async Task MemoryStateStore_DuplicateEventId_Throws()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded));

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.AppendEventAsync(MakeStateEvent("evt-1", "item-2", MemoryState.Superseded)));
    }

    [TestMethod]
    public async Task MemoryStateStore_FreshNewState_Throws()
    {
        var store = new InMemoryMemoryStateStore();
        var evt = MakeStateEvent("evt-1", "item-1", MemoryState.Fresh);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => store.AppendEventAsync(evt));
    }

    [TestMethod]
    public async Task MemoryStateStore_GetLatestState_ReturnsLatestEvent()
    {
        var store = new InMemoryMemoryStateStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);

        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded, occurredAt: t1));
        await store.AppendEventAsync(MakeStateEvent("evt-2", "item-1", MemoryState.Replaced, occurredAt: t2));

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");

        Assert.IsNotNull(latest);
        Assert.AreEqual(MemoryState.Replaced, latest.NewState);
    }

    [TestMethod]
    public async Task MemoryStateStore_GetLatestState_NoEvents_ReturnsNull()
    {
        var store = new InMemoryMemoryStateStore();

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-never");

        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task MemoryStateStore_GetRecent_ReturnsLatestN()
    {
        var store = new InMemoryMemoryStateStore();
        for (int i = 1; i <= 5; i++)
        {
            await store.AppendEventAsync(MakeStateEvent(
                $"evt-{i}", $"item-{i}", occurredAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var recent = await store.GetRecentAsync("ws-test", "col-test", take: 3);

        Assert.AreEqual(3, recent.Count);
        Assert.AreEqual("evt-5", recent[0].EventId);
    }

    [TestMethod]
    public async Task MemoryStateStore_QueryEvents_FiltersByNewState()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded));
        await store.AppendEventAsync(MakeStateEvent("evt-2", "item-2", MemoryState.Rejected));

        var results = await store.QueryEventsAsync(new MemoryStateEventQuery
        {
            WorkspaceId = "ws-test",
            NewState = MemoryState.Rejected
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(MemoryState.Rejected, results[0].NewState);
    }

    // =========================================================================
    // 2. DefaultConsolidationETL
    // =========================================================================

    [TestMethod]
    public async Task ConsolidationETL_DryRun_DoesNotMutate()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10),
            DryRun = true
        });

        Assert.AreEqual(1, result.ExtractedCount);
        Assert.AreEqual(0, result.TransformedCount);
        Assert.IsTrue(result.IsSuccess);

        var allEvents = await store.QueryEventsAsync(new MemoryStateEventQuery
        {
            WorkspaceId = "ws-test",
            Take = 0
        });
        Assert.AreEqual(1, allEvents.Count); // 没有写入新事件
    }

    [TestMethod]
    public async Task ConsolidationETL_Transform_PushesSupersededToReplaced()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded,
            occurredAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10)
        });

        Assert.AreEqual(1, result.ExtractedCount);
        Assert.AreEqual(1, result.TransformedCount);

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(latest);
        Assert.AreEqual(MemoryState.Replaced, latest.NewState);
    }

    [TestMethod]
    public async Task ConsolidationETL_Load_PushesReplacedToArchived()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Replaced,
            occurredAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10)
        });

        Assert.AreEqual(1, result.LoadedCount);

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(latest);
        Assert.AreEqual(MemoryState.Archived, latest.NewState);
    }

    [TestMethod]
    public async Task ConsolidationETL_DormantToArchived_TerminalDecay()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Dormant,
            occurredAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10)
        });

        Assert.AreEqual(1, result.LoadedCount);

        var latest = await store.GetLatestStateAsync("ws-test", "col-test", "item-1");
        Assert.IsNotNull(latest);
        Assert.AreEqual(MemoryState.Archived, latest.NewState);
        Assert.AreEqual("consolidation-etl-dormant", latest.Reason);
    }

    [TestMethod]
    public async Task ConsolidationETL_Idempotent_DoesNotReProcessArchived()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded,
            occurredAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var etl = new DefaultConsolidationETL(store);

        // 第一次：Superseded → Replaced
        await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10)
        });

        // 第二次：Replaced → Archived
        await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10)
        });

        // 第三次：没有可处理的 item
        var thirdResult = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10)
        });
        Assert.AreEqual(0, thirdResult.ExtractedCount);
    }

    [TestMethod]
    public async Task ConsolidationETL_FilterByItemType()
    {
        var store = new InMemoryMemoryStateStore();
        await store.AppendEventAsync(MakeStateEvent("evt-1", "item-1", MemoryState.Superseded, itemType: "memory",
            occurredAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.AppendEventAsync(MakeStateEvent("evt-2", "item-2", MemoryState.Superseded, itemType: "constraint",
            occurredAt: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var etl = new DefaultConsolidationETL(store);
        var result = await etl.RunAsync(new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = DateTimeOffset.UtcNow.AddSeconds(10),
            ItemTypes = new[] { "memory" }
        });

        Assert.AreEqual(1, result.ExtractedCount);
        Assert.AreEqual(1, result.TransformedCount);
    }

    // =========================================================================
    // 3. DefaultMemoryDecayEvaluator
    // =========================================================================

    [TestMethod]
    public async Task DecayEvaluator_NoEffectiveContribution_ActiveToCooling()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();
        var stats = MakeStats(selectedCount: 6, usefulFeedbackCount: 0);

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Active, stats);

        Assert.AreEqual(MemoryState.Cooling, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.NoEffectiveContribution, assessment.DecayFactor);
        Assert.IsTrue(assessment.NeedsTransition);
    }

    [TestMethod]
    public async Task DecayEvaluator_LongTermNoHit_ActiveToCooling()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();
        var stats = MakeStats(lastRecallTime: DateTimeOffset.UtcNow.AddDays(-10));

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Active, stats);

        Assert.AreEqual(MemoryState.Cooling, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.LongTermNoHit, assessment.DecayFactor);
    }

    [TestMethod]
    public async Task DecayEvaluator_LongTermNoHit_CoolingToDormant()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();
        var stats = MakeStats(lastRecallTime: DateTimeOffset.UtcNow.AddDays(-35));

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Cooling, stats);

        Assert.AreEqual(MemoryState.Dormant, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.LongTermNoHit, assessment.DecayFactor);
    }

    [TestMethod]
    public async Task DecayEvaluator_LongTermNoHit_DormantToArchived()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();
        var stats = MakeStats(lastRecallTime: DateTimeOffset.UtcNow.AddDays(-95));

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Dormant, stats);

        Assert.AreEqual(MemoryState.Archived, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.LongTermNoHit, assessment.DecayFactor);
    }

    [TestMethod]
    public async Task DecayEvaluator_RecentHit_NoDecay()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();
        var stats = MakeStats(
            lastRecallTime: DateTimeOffset.UtcNow.AddDays(-1),
            selectedCount: 10,
            usefulFeedbackCount: 5);

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Active, stats);

        Assert.AreEqual(MemoryState.Active, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.Unknown, assessment.DecayFactor);
        Assert.IsFalse(assessment.NeedsTransition);
    }

    [TestMethod]
    public async Task DecayEvaluator_ArchivedTerminal_NoAssessment()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();
        var stats = MakeStats(lastRecallTime: DateTimeOffset.UtcNow.AddDays(-365));

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Archived, stats);

        Assert.AreEqual(MemoryState.Archived, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.Unknown, assessment.DecayFactor);
        Assert.IsFalse(assessment.NeedsTransition);
    }

    [TestMethod]
    public async Task DecayEvaluator_NoStats_NoDecay()
    {
        var evaluator = new DefaultMemoryDecayEvaluator();

        var assessment = await evaluator.EvaluateAsync(
            "item-1", "ws-test", "col-test", MemoryState.Active, stats: null);

        Assert.AreEqual(MemoryState.Active, assessment.TargetState);
        Assert.AreEqual(MemoryDecayFactor.Unknown, assessment.DecayFactor);
    }

    // =========================================================================
    // 4. InMemoryMemoryUtilityStatsStore
    // =========================================================================

    [TestMethod]
    public async Task StatsStore_UpsertSnapshot_GetAsync()
    {
        var store = new InMemoryMemoryUtilityStatsStore();
        var stats = MakeStats(sourceItemId: "item-1", selectedCount: 5);
        store.UpsertSnapshot(stats);

        var retrieved = await store.GetAsync("ws-test", "col-test", "item-1");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(5, retrieved.SelectedCount);
    }

    [TestMethod]
    public async Task StatsStore_UpsertSnapshot_ReplacesExisting()
    {
        var store = new InMemoryMemoryUtilityStatsStore();
        store.UpsertSnapshot(MakeStats(sourceItemId: "item-1", selectedCount: 5));
        store.UpsertSnapshot(MakeStats(sourceItemId: "item-1", selectedCount: 10));

        var retrieved = await store.GetAsync("ws-test", "col-test", "item-1");

        Assert.IsNotNull(retrieved);
        Assert.AreEqual(10, retrieved.SelectedCount);
    }

    [TestMethod]
    public async Task StatsStore_QueryAsync_FiltersByMinSelectedCount()
    {
        var store = new InMemoryMemoryUtilityStatsStore();
        store.UpsertSnapshot(MakeStats(sourceItemId: "item-1", selectedCount: 3));
        store.UpsertSnapshot(MakeStats(sourceItemId: "item-2", selectedCount: 10));

        var results = await store.QueryAsync(new MemoryUtilityStatsQuery
        {
            WorkspaceId = "ws-test",
            MinSelectedCount = 5
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("item-2", results[0].SourceItemId);
    }

    [TestMethod]
    public async Task StatsStore_QueryAsync_FiltersByBeforeLastUsefulTime()
    {
        var store = new InMemoryMemoryUtilityStatsStore();
        store.UpsertSnapshot(MakeStats(sourceItemId: "item-1",
            lastUsefulTime: DateTimeOffset.UtcNow.AddDays(-30)));
        store.UpsertSnapshot(MakeStats(sourceItemId: "item-2",
            lastUsefulTime: DateTimeOffset.UtcNow.AddDays(-1)));

        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var results = await store.QueryAsync(new MemoryUtilityStatsQuery
        {
            WorkspaceId = "ws-test",
            BeforeLastUsefulTime = cutoff
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("item-1", results[0].SourceItemId);
    }

    [TestMethod]
    public void MemoryUtilityStats_SelectionRate_CalculatedCorrectly()
    {
        var stats = MakeStats(recallCount: 10, selectedCount: 5);
        Assert.AreEqual(0.5, stats.SelectionRate, 0.001);
    }

    [TestMethod]
    public void MemoryUtilityStats_UsefulRate_CalculatedCorrectly()
    {
        var stats = MakeStats(selectedCount: 10, usefulFeedbackCount: 3);
        Assert.AreEqual(0.3, stats.UsefulRate, 0.001);
    }

    [TestMethod]
    public void MemoryUtilityStats_AverageTokenCost_CalculatedCorrectly()
    {
        var stats = MakeStats(selectedCount: 10, tokenCost: 5000);
        Assert.AreEqual(500.0, stats.AverageTokenCost, 0.001);
    }

    // =========================================================================
    // 5. InMemoryConflictSetStore + ConflictResolutionStatus
    // =========================================================================

    [TestMethod]
    public async Task ConflictSetStore_QueryAsync_FiltersByResolutionStatus()
    {
        var store = new InMemoryConflictSetStore();
        var now = DateTimeOffset.UtcNow;
        store.AppendConflictSets(new[]
        {
            new ConflictSet
            {
                ConflictSetId = "c-1",
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Kind = ConflictSetKind.Duplicate,
                Entries = Array.Empty<ConflictSetEntry>(),
                DecisionId = "d-1",
                ResolutionStatus = ConflictResolutionStatus.AutoResolved,
                MaterializedAt = now
            },
            new ConflictSet
            {
                ConflictSetId = "c-2",
                WorkspaceId = "ws-test",
                CollectionId = "col-test",
                Kind = ConflictSetKind.SectionConflict,
                Entries = Array.Empty<ConflictSetEntry>(),
                DecisionId = "d-2",
                ResolutionStatus = ConflictResolutionStatus.Unresolved,
                MaterializedAt = now
            }
        });

        var results = await store.QueryAsync(new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            ResolutionStatus = ConflictResolutionStatus.AutoResolved
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("c-1", results[0].ConflictSetId);
    }

    // =========================================================================
    // 6. UtilityLedgerMaterializer + ConflictResolutionStatus
    // =========================================================================

    [TestMethod]
    public async Task Materializer_DuplicateConflictSet_WithResolvedItem_AutoResolved()
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
        Assert.AreEqual(ConflictResolutionStatus.AutoResolved, conflicts[0].ResolutionStatus);
        Assert.AreEqual("item-1", conflicts[0].ResolvedItemId);
        Assert.AreEqual("highest-score", conflicts[0].ChosenAuthority);
        Assert.IsNotNull(conflicts[0].ResolvedAt);
    }

    [TestMethod]
    public async Task Materializer_SectionConflict_WithoutResolvedItem_Unresolved()
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
        Assert.AreEqual(ConflictResolutionStatus.Unresolved, conflicts[0].ResolutionStatus);
        Assert.IsNull(conflicts[0].ResolvedItemId);
        Assert.IsNull(conflicts[0].ChosenAuthority);
        Assert.IsNull(conflicts[0].ResolvedAt);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static MemoryStateEventRecord MakeStateEvent(
        string eventId = "evt-test",
        string sourceItemId = "item-source",
        MemoryState newState = MemoryState.Superseded,
        string itemType = "memory",
        string reason = "lifecycle-review",
        DateTimeOffset? occurredAt = null)
    {
        return new MemoryStateEventRecord
        {
            EventId = eventId,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            SourceItemId = sourceItemId,
            ItemType = itemType,
            NewState = newState,
            Reason = reason,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };
    }

    private static MemoryUtilityStats MakeStats(
        string sourceItemId = "item-1",
        string itemType = "memory",
        int recallCount = 0,
        int selectedCount = 0,
        int usefulFeedbackCount = 0,
        int tokenCost = 0,
        DateTimeOffset? lastRecallTime = null,
        DateTimeOffset? lastUsefulTime = null)
    {
        return new MemoryUtilityStats
        {
            SourceItemId = sourceItemId,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            ItemType = itemType,
            RecallCount = recallCount,
            SelectedCount = selectedCount,
            UsefulFeedbackCount = usefulFeedbackCount,
            TokenCost = tokenCost,
            LastRecallTime = lastRecallTime,
            LastUsefulTime = lastUsefulTime,
            UpdatedAt = DateTimeOffset.UtcNow
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
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: "test-ws",
                collectionId: "test-col",
                entityKind: "test-entity",
                entityId: candidateId,
                entityVersion: "v1"),
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
