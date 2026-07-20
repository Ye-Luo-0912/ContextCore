using System.Reflection;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// R21-1：Memory Evolution Engine 契约测试（Superseded 状态 + Consolidation ETL）。
///
/// 验证目标：
///   1. SupersededItemState 枚举 5 值（Unknown/Active/Superseded/Replaced/Archived）
///   2. SupersedeEventRecord 必填字段 + 默认值
///   3. SupersedeEventQuery 查询条件字段
///   4. ISupersededItemStore 接口最小化（4 个方法）
///   5. ConsolidationRequest 默认值（OlderThan=UtcNow, BatchSize=100, DryRun=false）
///   6. ConsolidationRunResult 字段 + IsSuccess / Duration 计算属性
///   7. IConsolidationETL 接口最小化（仅 RunAsync）
///   8. SupersededItemStateExtensions.IsTerminal / CanTransitionTo / NeedsConsolidation
///   9. 状态机转换合法性（Active→Superseded→Replaced→Archived 单向推进）
///  10. 终态 Archived 不可推进
///  11. 契约无存储 I/O（反射验证接口方法签名）
/// </summary>
[TestClass]
[TestCategory("R21")]
public sealed class MemoryEvolutionContractsTests
{
    // =========================================================================
    // 1. SupersededItemState 枚举
    // =========================================================================

    [TestMethod]
    public void SupersededItemState_Has5Values()
    {
        var values = Enum.GetValues<SupersededItemState>();
        Assert.AreEqual(5, values.Length);
        Assert.IsTrue(values.Contains(SupersededItemState.Unknown));
        Assert.IsTrue(values.Contains(SupersededItemState.Active));
        Assert.IsTrue(values.Contains(SupersededItemState.Superseded));
        Assert.IsTrue(values.Contains(SupersededItemState.Replaced));
        Assert.IsTrue(values.Contains(SupersededItemState.Archived));
    }

    [TestMethod]
    public void SupersededItemState_ValuesAreUnique()
    {
        var values = Enum.GetValues<SupersededItemState>().Select(v => (byte)v).ToList();
        Assert.AreEqual(values.Count, values.Distinct().Count());
    }

    [TestMethod]
    public void SupersededItemState_BackedByByte()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(SupersededItemState));
        Assert.AreEqual(typeof(byte), underlyingType);
    }

    // =========================================================================
    // 2. SupersedeEventRecord 必填字段 + 默认值
    // =========================================================================

    [TestMethod]
    public void SupersedeEventRecord_RequiredFields_AreEnforced()
    {
        var record = MakeEventRecord(
            eventId: "evt-1",
            sourceItemId: "item-1",
            itemType: "memory",
            newState: SupersededItemState.Superseded,
            reason: "lifecycle-review",
            occurredAt: DateTimeOffset.UtcNow);

        Assert.AreEqual("evt-1", record.EventId);
        Assert.AreEqual("ws-test", record.WorkspaceId);
        Assert.AreEqual("col-test", record.CollectionId);
        Assert.AreEqual("item-1", record.SourceItemId);
        Assert.AreEqual("memory", record.ItemType);
        Assert.AreEqual(SupersededItemState.Superseded, record.NewState);
        Assert.AreEqual("lifecycle-review", record.Reason);
        Assert.IsTrue(record.OccurredAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void SupersedeEventRecord_OptionalFields_DefaultValues()
    {
        var record = MakeEventRecord();

        // TargetItemId 默认 null
        Assert.IsNull(record.TargetItemId);
        // Reviewer 默认 null
        Assert.IsNull(record.Reviewer);
        // RelationId 默认 null
        Assert.IsNull(record.RelationId);
        // ConsolidationRunId 默认 null
        Assert.IsNull(record.ConsolidationRunId);
        // ReasonDetail 默认空字符串
        Assert.AreEqual(string.Empty, record.ReasonDetail);
        // Metadata 默认空字典
        Assert.AreEqual(0, record.Metadata.Count);
    }

    [TestMethod]
    public void SupersedeEventRecord_WithExpression_ProducesNewInstance()
    {
        var record = MakeEventRecord();
        var updated = record with { Reason = "manual", Reviewer = "user-1" };

        Assert.AreEqual("lifecycle-review", record.Reason);
        Assert.AreEqual("manual", updated.Reason);
        Assert.AreEqual("user-1", updated.Reviewer);
        Assert.AreNotSame(record, updated);
    }

    // =========================================================================
    // 3. SupersedeEventQuery 查询条件字段
    // =========================================================================

    [TestMethod]
    public void SupersedeEventQuery_DefaultValues()
    {
        var query = new SupersedeEventQuery { WorkspaceId = "ws-test" };

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
    public void SupersedeEventQuery_AllFieldsCanBeSet()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var until = DateTimeOffset.UtcNow;
        var query = new SupersedeEventQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            SourceItemId = "item-1",
            TargetItemId = "item-2",
            ItemType = "memory",
            NewState = SupersededItemState.Superseded,
            Since = since,
            Until = until,
            Take = 50
        };

        Assert.AreEqual("col-test", query.CollectionId);
        Assert.AreEqual("item-1", query.SourceItemId);
        Assert.AreEqual("item-2", query.TargetItemId);
        Assert.AreEqual("memory", query.ItemType);
        Assert.AreEqual(SupersededItemState.Superseded, query.NewState);
        Assert.AreEqual(since, query.Since);
        Assert.AreEqual(until, query.Until);
        Assert.AreEqual(50, query.Take);
    }

    // =========================================================================
    // 4. ISupersededItemStore 接口最小化
    // =========================================================================

    [TestMethod]
    public void ISupersededItemStore_Has4Methods()
    {
        var storeType = typeof(ISupersededItemStore);
        var methods = storeType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(4, methods.Length);
        var methodNames = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "AppendEventAsync", "GetLatestStateAsync", "GetRecentAsync", "QueryEventsAsync" },
            methodNames);
    }

    [TestMethod]
    public void ISupersededItemStore_AllMethods_ReturnTask()
    {
        var storeType = typeof(ISupersededItemStore);
        foreach (var method in storeType.GetMethods())
        {
            // 允许 Task（无返回值）或 Task<T>
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"{method.Name} should return Task or Task<T>");
        }
    }

    [TestMethod]
    public void ISupersededItemStore_IsInterface()
    {
        Assert.IsTrue(typeof(ISupersededItemStore).IsInterface);
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

        Assert.AreEqual("ws-test", request.WorkspaceId);
        Assert.AreEqual("col-test", request.CollectionId);
        Assert.IsTrue(request.OlderThan <= DateTimeOffset.UtcNow);
        Assert.AreEqual(0, request.ItemTypes.Count);
        Assert.AreEqual(100, request.BatchSize);
        Assert.IsFalse(request.DryRun);
        Assert.IsNull(request.TriggeredBy);
    }

    [TestMethod]
    public void ConsolidationRequest_AllFieldsCanBeSet()
    {
        var olderThan = DateTimeOffset.UtcNow.AddDays(-1);
        var request = new ConsolidationRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            OlderThan = olderThan,
            ItemTypes = new[] { "memory", "context" },
            BatchSize = 50,
            DryRun = true,
            TriggeredBy = "agent-1"
        };

        Assert.AreEqual(olderThan, request.OlderThan);
        Assert.AreEqual(2, request.ItemTypes.Count);
        Assert.AreEqual(50, request.BatchSize);
        Assert.IsTrue(request.DryRun);
        Assert.AreEqual("agent-1", request.TriggeredBy);
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

    [TestMethod]
    public void ConsolidationRunResult_Duration_CalculatedFromStartEnd()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddMinutes(3);
        var result = new ConsolidationRunResult
        {
            RunId = "run-1",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            StartedAt = start,
            CompletedAt = end
        };

        Assert.AreEqual(TimeSpan.FromMinutes(3), result.Duration);
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

    [TestMethod]
    public void IConsolidationETL_IsInterface()
    {
        Assert.IsTrue(typeof(IConsolidationETL).IsInterface);
    }

    // =========================================================================
    // 8. SupersededItemStateExtensions
    // =========================================================================

    [TestMethod]
    public void IsTerminal_OnlyArchived_ReturnsTrue()
    {
        Assert.IsFalse(SupersededItemState.Unknown.IsTerminal());
        Assert.IsFalse(SupersededItemState.Active.IsTerminal());
        Assert.IsFalse(SupersededItemState.Superseded.IsTerminal());
        Assert.IsFalse(SupersededItemState.Replaced.IsTerminal());
        Assert.IsTrue(SupersededItemState.Archived.IsTerminal());
    }

    [TestMethod]
    public void NeedsConsolidation_OnlySupersededAndReplaced_ReturnTrue()
    {
        Assert.IsFalse(SupersededItemState.Unknown.NeedsConsolidation());
        Assert.IsFalse(SupersededItemState.Active.NeedsConsolidation());
        Assert.IsTrue(SupersededItemState.Superseded.NeedsConsolidation());
        Assert.IsTrue(SupersededItemState.Replaced.NeedsConsolidation());
        Assert.IsFalse(SupersededItemState.Archived.NeedsConsolidation());
    }

    // =========================================================================
    // 9. 状态机转换合法性（Active→Superseded→Replaced→Archived 单向推进）
    // =========================================================================

    [TestMethod]
    public void CanTransitionTo_ActiveToSuperseded_ReturnsTrue()
    {
        Assert.IsTrue(SupersededItemState.Active.CanTransitionTo(SupersededItemState.Superseded));
    }

    [TestMethod]
    public void CanTransitionTo_SupersededToReplaced_ReturnsTrue()
    {
        Assert.IsTrue(SupersededItemState.Superseded.CanTransitionTo(SupersededItemState.Replaced));
    }

    [TestMethod]
    public void CanTransitionTo_ReplacedToArchived_ReturnsTrue()
    {
        Assert.IsTrue(SupersededItemState.Replaced.CanTransitionTo(SupersededItemState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_SameState_ReturnsFalse()
    {
        // 自环不允许
        Assert.IsFalse(SupersededItemState.Active.CanTransitionTo(SupersededItemState.Active));
        Assert.IsFalse(SupersededItemState.Superseded.CanTransitionTo(SupersededItemState.Superseded));
        Assert.IsFalse(SupersededItemState.Replaced.CanTransitionTo(SupersededItemState.Replaced));
        Assert.IsFalse(SupersededItemState.Archived.CanTransitionTo(SupersededItemState.Archived));
    }

    [TestMethod]
    public void CanTransitionTo_ReverseTransition_ReturnsFalse()
    {
        // 不允许反向推进
        Assert.IsFalse(SupersededItemState.Superseded.CanTransitionTo(SupersededItemState.Active));
        Assert.IsFalse(SupersededItemState.Replaced.CanTransitionTo(SupersededItemState.Superseded));
        Assert.IsFalse(SupersededItemState.Archived.CanTransitionTo(SupersededItemState.Replaced));
    }

    [TestMethod]
    public void CanTransitionTo_SkipState_ReturnsFalse()
    {
        // 不允许跳跃（如 Active 直跳 Replaced）
        Assert.IsFalse(SupersededItemState.Active.CanTransitionTo(SupersededItemState.Replaced));
        Assert.IsFalse(SupersededItemState.Active.CanTransitionTo(SupersededItemState.Archived));
        Assert.IsFalse(SupersededItemState.Superseded.CanTransitionTo(SupersededItemState.Archived));
    }

    // =========================================================================
    // 10. 终态 Archived 不可推进
    // =========================================================================

    [TestMethod]
    public void CanTransitionTo_ArchivedToAny_ReturnsFalse()
    {
        // Archived 是终态，不允许推进到任何状态
        Assert.IsFalse(SupersededItemState.Archived.CanTransitionTo(SupersededItemState.Active));
        Assert.IsFalse(SupersededItemState.Archived.CanTransitionTo(SupersededItemState.Superseded));
        Assert.IsFalse(SupersededItemState.Archived.CanTransitionTo(SupersededItemState.Replaced));
    }

    // =========================================================================
    // 11. 契约无存储 I/O（反射验证）
    // =========================================================================

    [TestMethod]
    public void SupersedeEventRecord_IsSealedRecord()
    {
        Assert.IsTrue(typeof(SupersedeEventRecord).IsSealed);
        Assert.IsTrue(typeof(SupersedeEventRecord).IsValueType == false); // record class
    }

    [TestMethod]
    public void ConsolidationRequest_IsSealedRecord()
    {
        Assert.IsTrue(typeof(ConsolidationRequest).IsSealed);
    }

    [TestMethod]
    public void ConsolidationRunResult_IsSealedRecord()
    {
        Assert.IsTrue(typeof(ConsolidationRunResult).IsSealed);
    }

    [TestMethod]
    public void SupersededItemStateExtensions_IsStaticClass()
    {
        Assert.IsTrue(typeof(SupersededItemStateExtensions).IsAbstract);
        Assert.IsTrue(typeof(SupersededItemStateExtensions).IsSealed);
    }

    [TestMethod]
    public void MemoryEvolutionContracts_NoAsyncVoidMethods()
    {
        // 契约接口不应有 async void 方法（应是 Task<T>）
        var interfaces = new[] { typeof(ISupersededItemStore), typeof(IConsolidationETL) };
        foreach (var iface in interfaces)
        {
            foreach (var method in iface.GetMethods())
            {
                Assert.AreNotEqual(typeof(void), method.ReturnType,
                    $"{iface.Name}.{method.Name} should not return void");
                Assert.AreNotEqual("VoidTaskResult", method.ReturnType.Name,
                    $"{iface.Name}.{method.Name} should not return async void");
            }
        }
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static SupersedeEventRecord MakeEventRecord(
        string eventId = "evt-test",
        string sourceItemId = "item-source",
        string itemType = "memory",
        SupersededItemState newState = SupersededItemState.Superseded,
        string reason = "lifecycle-review",
        DateTimeOffset? occurredAt = null)
    {
        return new SupersedeEventRecord
        {
            EventId = eventId,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            SourceItemId = sourceItemId,
            TargetItemId = null,
            ItemType = itemType,
            NewState = newState,
            Reason = reason,
            // ReasonDetail / Metadata 留默认值，便于测试默认值
            Reviewer = null,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            RelationId = null,
            ConsolidationRunId = null
        };
    }
}
