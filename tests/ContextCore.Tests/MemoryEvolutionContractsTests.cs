using System.Reflection;
using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// Memory Evolution 统一契约测试。
///
/// 验证目标：
/// 1. MemoryState 枚举 8 值（byte 底层）
/// 2. MemoryStateEventRecord 必填字段 + 默认值
/// 3. MemoryStateEventQuery 查询条件字段
/// 4. IMemoryStateStore 接口最小化（4 方法）
/// 5. ConsolidationRequest 默认值
/// 6. ConsolidationRunResult 字段 + IsSuccess / Duration
/// 7. IConsolidationETL 接口最小化（仅 RunAsync）
/// 8. MemoryStateExtensions：IsTerminal / CanTransitionTo / NeedsConsolidation / IsDecaying / IsActiveOrFresh / CanReheat
/// 9. 状态机转换合法性（Fresh→Active→Cooling→Dormant→Archived 等）
/// 10. 终态 Archived 不可推进
/// 11. 回温转换合法性（Cooling→Active / Dormant→Active）
/// </summary>
[TestClass]
[TestCategory("R21")]
public sealed class MemoryEvolutionContractsTests
{
    // =========================================================================
    // 1. MemoryState 枚举 8 值
    // =========================================================================

    [TestMethod]
    public void MemoryState_Has8Values()
    {
        var values = Enum.GetValues<MemoryState>();
        Assert.AreEqual(8, values.Length);
        Assert.IsTrue(values.Contains(MemoryState.Fresh));
        Assert.IsTrue(values.Contains(MemoryState.Active));
        Assert.IsTrue(values.Contains(MemoryState.Cooling));
        Assert.IsTrue(values.Contains(MemoryState.Dormant));
        Assert.IsTrue(values.Contains(MemoryState.Superseded));
        Assert.IsTrue(values.Contains(MemoryState.Replaced));
        Assert.IsTrue(values.Contains(MemoryState.Archived));
        Assert.IsTrue(values.Contains(MemoryState.Rejected));
    }

    [TestMethod]
    public void MemoryState_ValuesAreUnique()
    {
        var values = Enum.GetValues<MemoryState>().Select(v => (byte)v).ToList();
        Assert.AreEqual(values.Count, values.Distinct().Count());
    }

    [TestMethod]
    public void MemoryState_BackedByByte()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(MemoryState));
        Assert.AreEqual(typeof(byte), underlyingType);
    }

    [TestMethod]
    public void MemoryState_FreshIsZero()
    {
        Assert.AreEqual((byte)0, (byte)MemoryState.Fresh);
    }

    // =========================================================================
    // 2. MemoryStateEventRecord 必填字段 + 默认值
    // =========================================================================

    [TestMethod]
    public void MemoryStateEventRecord_RequiredFields_AreEnforced()
    {
        var record = MakeEventRecord();

        Assert.AreEqual("evt-1", record.EventId);
        Assert.AreEqual("ws-test", record.WorkspaceId);
        Assert.AreEqual("col-test", record.CollectionId);
        Assert.AreEqual("item-1", record.SourceItemId);
        Assert.AreEqual("memory", record.ItemType);
        Assert.AreEqual(MemoryState.Superseded, record.NewState);
        Assert.AreEqual("lifecycle-review", record.Reason);
        Assert.IsTrue(record.OccurredAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void MemoryStateEventRecord_OptionalFields_DefaultValues()
    {
        var record = MakeEventRecord();

        Assert.IsNull(record.TargetItemId);
        Assert.IsNull(record.Reviewer);
        Assert.IsNull(record.RelationId);
        Assert.IsNull(record.ConsolidationRunId);
        Assert.AreEqual(string.Empty, record.ReasonDetail);
        Assert.AreEqual(0, record.Metadata.Count);
    }

    [TestMethod]
    public void MemoryStateEventRecord_WithExpression_ProducesNewInstance()
    {
        var record = MakeEventRecord();
        var updated = record with { Reason = "manual", Reviewer = "user-1" };

        Assert.AreEqual("lifecycle-review", record.Reason);
        Assert.AreEqual("manual", updated.Reason);
        Assert.AreEqual("user-1", updated.Reviewer);
        Assert.AreNotSame(record, updated);
    }

    // =========================================================================
    // 3. MemoryStateEventQuery 默认值 + 全字段
    // =========================================================================

    [TestMethod]
    public void MemoryStateEventQuery_DefaultValues()
    {
        var query = new MemoryStateEventQuery { WorkspaceId = "ws-test" };

        Assert.AreEqual("ws-test", query.WorkspaceId);
        Assert.IsNull(query.CollectionId);
        Assert.IsNull(query.SourceItemId);
        Assert.IsNull(query.TargetItemId);
        Assert.IsNull(query.ItemType);
        Assert.IsNull(query.NewState);
        Assert.IsNull(query.Since);
        Assert.IsNull(query.Until);
        Assert.AreEqual(100, query.Take);
    }

    [TestMethod]
    public void MemoryStateEventQuery_AllFieldsCanBeSet()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var until = DateTimeOffset.UtcNow;
        var query = new MemoryStateEventQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            SourceItemId = "item-1",
            TargetItemId = "item-2",
            ItemType = "memory",
            NewState = MemoryState.Superseded,
            Since = since,
            Until = until,
            Take = 50
        };

        Assert.AreEqual("col-test", query.CollectionId);
        Assert.AreEqual("item-1", query.SourceItemId);
        Assert.AreEqual("item-2", query.TargetItemId);
        Assert.AreEqual("memory", query.ItemType);
        Assert.AreEqual(MemoryState.Superseded, query.NewState);
        Assert.AreEqual(since, query.Since);
        Assert.AreEqual(until, query.Until);
        Assert.AreEqual(50, query.Take);
    }

    // =========================================================================
    // 4. IMemoryStateStore 接口最小化
    // =========================================================================

    [TestMethod]
    public void IMemoryStateStore_Has4Methods()
    {
        var storeType = typeof(IMemoryStateStore);
        var methods = storeType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(4, methods.Length);
        var methodNames = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "AppendEventAsync", "GetLatestStateAsync", "GetRecentAsync", "QueryEventsAsync" },
            methodNames);
    }

    [TestMethod]
    public void IMemoryStateStore_IsInterface()
    {
        Assert.IsTrue(typeof(IMemoryStateStore).IsInterface);
    }

    [TestMethod]
    public void IMemoryStateStore_AllMethods_ReturnTask()
    {
        var storeType = typeof(IMemoryStateStore);
        foreach (var method in storeType.GetMethods())
        {
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"{method.Name} should return Task or Task<T>");
        }
    }

    // =========================================================================
    // 5. ConsolidationRequest 默认值
    // =========================================================================

    [TestMethod]
    public void ConsolidationRequest_DefaultValues()
    {
        var request = new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test"
        };

        Assert.IsTrue(request.OlderThan <= DateTimeOffset.UtcNow);
        Assert.AreEqual(0, request.ItemTypes.Count);
        Assert.AreEqual(100, request.BatchSize);
        Assert.IsFalse(request.DryRun);
        Assert.IsNull(request.TriggeredBy);
    }

    // =========================================================================
    // 6. ConsolidationRunResult 字段 + IsSuccess / Duration
    // =========================================================================

    [TestMethod]
    public void ConsolidationRunResult_DefaultValues()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new ConsolidationRunResult
        {
            RunId = "run-1",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            StartedAt = now,
            CompletedAt = now.AddSeconds(5)
        };

        Assert.AreEqual(0, result.ExtractedCount);
        Assert.AreEqual(0, result.TransformedCount);
        Assert.AreEqual(0, result.LoadedCount);
        Assert.AreEqual(0, result.SkippedCount);
        Assert.AreEqual(0, result.ProcessedItemIds.Count);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.IsNull(result.TriggeredBy);
        Assert.IsFalse(result.DryRun);
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(TimeSpan.FromSeconds(5), result.Duration);
    }

    [TestMethod]
    public void ConsolidationRunResult_IsSuccess_FalseWhenErrorsNonEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new ConsolidationRunResult
        {
            RunId = "run-1",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            StartedAt = now,
            CompletedAt = now,
            Errors = new[] { "store timeout", "item not found" }
        };

        Assert.IsFalse(result.IsSuccess);
    }

    // =========================================================================
    // 7. IConsolidationETL 接口最小化
    // =========================================================================

    [TestMethod]
    public void IConsolidationETL_HasOnlyRunAsyncMethod()
    {
        var etlType = typeof(IConsolidationETL);
        var methods = etlType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(1, methods.Length);
        Assert.AreEqual("RunAsync", methods[0].Name);
    }

    // =========================================================================
    // 8. MemoryStateExtensions
    // =========================================================================

    [TestMethod]
    public void IsTerminal_OnlyArchived_ReturnsTrue()
    {
        Assert.IsFalse(MemoryState.Fresh.IsTerminal());
        Assert.IsFalse(MemoryState.Active.IsTerminal());
        Assert.IsFalse(MemoryState.Cooling.IsTerminal());
        Assert.IsFalse(MemoryState.Dormant.IsTerminal());
        Assert.IsFalse(MemoryState.Superseded.IsTerminal());
        Assert.IsFalse(MemoryState.Replaced.IsTerminal());
        Assert.IsTrue(MemoryState.Archived.IsTerminal());
        Assert.IsFalse(MemoryState.Rejected.IsTerminal());
    }

    [TestMethod]
    public void NeedsConsolidation_OnlySupersededAndReplaced_ReturnTrue()
    {
        Assert.IsFalse(MemoryState.Fresh.NeedsConsolidation());
        Assert.IsFalse(MemoryState.Active.NeedsConsolidation());
        Assert.IsFalse(MemoryState.Cooling.NeedsConsolidation());
        Assert.IsFalse(MemoryState.Dormant.NeedsConsolidation());
        Assert.IsTrue(MemoryState.Superseded.NeedsConsolidation());
        Assert.IsTrue(MemoryState.Replaced.NeedsConsolidation());
        Assert.IsFalse(MemoryState.Archived.NeedsConsolidation());
        Assert.IsFalse(MemoryState.Rejected.NeedsConsolidation());
    }

    [TestMethod]
    public void IsDecaying_OnlyCoolingAndDormant_ReturnTrue()
    {
        Assert.IsFalse(MemoryState.Fresh.IsDecaying());
        Assert.IsFalse(MemoryState.Active.IsDecaying());
        Assert.IsTrue(MemoryState.Cooling.IsDecaying());
        Assert.IsTrue(MemoryState.Dormant.IsDecaying());
        Assert.IsFalse(MemoryState.Superseded.IsDecaying());
        Assert.IsFalse(MemoryState.Replaced.IsDecaying());
        Assert.IsFalse(MemoryState.Archived.IsDecaying());
        Assert.IsFalse(MemoryState.Rejected.IsDecaying());
    }

    [TestMethod]
    public void IsActiveOrFresh_FreshAndActive_ReturnTrue()
    {
        Assert.IsTrue(MemoryState.Fresh.IsActiveOrFresh());
        Assert.IsTrue(MemoryState.Active.IsActiveOrFresh());
        Assert.IsFalse(MemoryState.Cooling.IsActiveOrFresh());
        Assert.IsFalse(MemoryState.Dormant.IsActiveOrFresh());
    }

    [TestMethod]
    public void CanReheat_CoolingAndDormant_ReturnTrue()
    {
        Assert.IsTrue(MemoryState.Cooling.CanReheat());
        Assert.IsTrue(MemoryState.Dormant.CanReheat());
        Assert.IsFalse(MemoryState.Fresh.CanReheat());
        Assert.IsFalse(MemoryState.Active.CanReheat());
        Assert.IsFalse(MemoryState.Archived.CanReheat());
    }

    // =========================================================================
    // 9. 状态机转换合法性
    // =========================================================================

    [TestMethod]
    public void CanTransitionTo_FreshToActive_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Fresh.CanTransitionTo(MemoryState.Active));
    }

    [TestMethod]
    public void CanTransitionTo_FreshToRejected_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Fresh.CanTransitionTo(MemoryState.Rejected));
    }

    [TestMethod]
    public void CanTransitionTo_ActiveToCooling_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Active.CanTransitionTo(MemoryState.Cooling));
    }

    [TestMethod]
    public void CanTransitionTo_ActiveToSuperseded_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Active.CanTransitionTo(MemoryState.Superseded));
    }

    [TestMethod]
    public void CanTransitionTo_ActiveToRejected_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Active.CanTransitionTo(MemoryState.Rejected));
    }

    [TestMethod]
    public void CanTransitionTo_CoolingToDormant_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Cooling.CanTransitionTo(MemoryState.Dormant));
    }

    [TestMethod]
    public void CanTransitionTo_CoolingToActive_Reheat_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Cooling.CanTransitionTo(MemoryState.Active));
    }

    [TestMethod]
    public void CanTransitionTo_DormantToArchived_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Dormant.CanTransitionTo(MemoryState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_DormantToActive_Reheat_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Dormant.CanTransitionTo(MemoryState.Active));
    }

    [TestMethod]
    public void CanTransitionTo_SupersededToReplaced_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Superseded.CanTransitionTo(MemoryState.Replaced));
    }

    [TestMethod]
    public void CanTransitionTo_ReplacedToArchived_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Replaced.CanTransitionTo(MemoryState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_RejectedToArchived_ReturnsTrue()
    {
        Assert.IsTrue(MemoryState.Rejected.CanTransitionTo(MemoryState.Archived));
    }

    // =========================================================================
    // 10. 终态 Archived 不可推进
    // =========================================================================

    [TestMethod]
    public void CanTransitionTo_ArchivedToAny_ReturnsFalse()
    {
        Assert.IsFalse(MemoryState.Archived.CanTransitionTo(MemoryState.Active));
        Assert.IsFalse(MemoryState.Archived.CanTransitionTo(MemoryState.Cooling));
        Assert.IsFalse(MemoryState.Archived.CanTransitionTo(MemoryState.Superseded));
        Assert.IsFalse(MemoryState.Archived.CanTransitionTo(MemoryState.Rejected));
    }

    [TestMethod]
    public void CanTransitionTo_SameState_ReturnsFalse()
    {
        foreach (var state in Enum.GetValues<MemoryState>())
        {
            Assert.IsFalse(state.CanTransitionTo(state),
                $"{state} -> {state} should not be allowed");
        }
    }

    // =========================================================================
    // 11. 非法跳跃禁止
    // =========================================================================

    [TestMethod]
    public void CanTransitionTo_FreshToArchived_ForbiddenJump()
    {
        Assert.IsFalse(MemoryState.Fresh.CanTransitionTo(MemoryState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_ActiveToArchived_ForbiddenJump()
    {
        // Active 不能直接到 Archived（需经过 Cooling→Dormant→Archived 或 Superseded→Replaced→Archived）
        Assert.IsFalse(MemoryState.Active.CanTransitionTo(MemoryState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_CoolingToArchived_ForbiddenJump()
    {
        // Cooling 不能直接到 Archived（需经过 Dormant）
        Assert.IsFalse(MemoryState.Cooling.CanTransitionTo(MemoryState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_SupersededToArchived_ForbiddenJump()
    {
        // Superseded 不能直接到 Archived（需经过 Replaced）
        Assert.IsFalse(MemoryState.Superseded.CanTransitionTo(MemoryState.Archived));
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static MemoryStateEventRecord MakeEventRecord(
        string eventId = "evt-1",
        string sourceItemId = "item-1",
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
}
