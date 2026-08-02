using System.Reflection;
using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// Utility Ledger + ConflictSet 契约测试。
///
/// 验证目标：
///   1. UtilityLedgerEntry 必填字段 + 默认值
///   2. UtilityLedgerQuery 默认值 + 全字段
///   3. IUtilityLedgerStore 接口最小化（3 方法 read-only）
///   4. ConflictSetKind 枚举 7 值（byte 底层）
///   5. ConflictSetEntry 必填字段 + 可空字段默认
///   6. ConflictSet 必填字段 + 默认值
///   7. ConflictSetQuery 默认值 + 全字段
///   8. IConflictSetStore 接口最小化（3 方法 read-only）
///   9. read-only 反射验证（接口无 WriteAsync / AppendAsync / SaveAsync）
///  10. sealed record / static / no async void 反射验证
/// </summary>
[TestClass]
[TestCategory("R21")]
public sealed class UtilityLedgerContractsTests
{
    // =========================================================================
    // 1. UtilityLedgerEntry 必填字段 + 默认值
    // =========================================================================

    [TestMethod]
    public void UtilityLedgerEntry_RequiredFields_AreEnforced()
    {
        var entry = MakeLedgerEntry();

        Assert.AreEqual("ledger-1", entry.EntryId);
        Assert.AreEqual("ws-test", entry.WorkspaceId);
        Assert.AreEqual("col-test", entry.CollectionId);
        Assert.AreEqual("item-1", entry.CandidateItemId);
        Assert.AreEqual(RetrievalExpert.Semantic, entry.Expert);
        Assert.AreEqual(0.4, entry.UtilityContribution);
        Assert.AreEqual(0.8, entry.DeterministicScore);
        Assert.AreEqual(0.9, entry.FinalScore);
        Assert.IsTrue(entry.IsSelected);
        Assert.AreEqual("decision-1", entry.DecisionId);
        Assert.AreEqual("decision-schema/2.0", entry.PolicyVersion);
        Assert.IsTrue(entry.MaterializedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void UtilityLedgerEntry_OptionalFields_DefaultValues()
    {
        var entry = MakeLedgerEntry();

        // ModelScore 默认 null（model failure 场景）
        Assert.IsNull(entry.ModelScore);
        // DropReasonCode 默认 null（selected 时）
        Assert.IsNull(entry.DropReasonCode);
        // RouterId 默认 null（未启用 Router）
        Assert.IsNull(entry.RouterId);
        // MaterializationBatchId 默认 null
        Assert.IsNull(entry.MaterializationBatchId);
        // Metadata 默认空字典
        Assert.AreEqual(0, entry.Metadata.Count);
    }

    [TestMethod]
    public void UtilityLedgerEntry_WithExpression_ProducesNewInstance()
    {
        var entry = MakeLedgerEntry();
        var updated = entry with { UtilityContribution = 0.6, IsSelected = false, DropReasonCode = "duplicate-suppressed" };

        Assert.AreEqual(0.4, entry.UtilityContribution);
        Assert.IsTrue(entry.IsSelected);
        Assert.AreEqual(0.6, updated.UtilityContribution);
        Assert.IsFalse(updated.IsSelected);
        Assert.AreEqual("duplicate-suppressed", updated.DropReasonCode);
        Assert.AreNotSame(entry, updated);
    }

    [TestMethod]
    public void UtilityLedgerEntry_DroppedCandidate_HasDropReasonCode()
    {
        var entry = MakeLedgerEntry() with { IsSelected = false, DropReasonCode = "token-budget-exceeded" };

        Assert.IsFalse(entry.IsSelected);
        Assert.AreEqual("token-budget-exceeded", entry.DropReasonCode);
    }

    // =========================================================================
    // 2. UtilityLedgerQuery 默认值 + 全字段
    // =========================================================================

    [TestMethod]
    public void UtilityLedgerQuery_DefaultValues()
    {
        var query = new UtilityLedgerQuery { WorkspaceId = "ws-test" };

        Assert.AreEqual("ws-test", query.WorkspaceId);
        Assert.IsNull(query.CollectionId);
        Assert.IsNull(query.CandidateItemId);
        Assert.IsNull(query.Expert);
        Assert.IsNull(query.DecisionId);
        Assert.IsNull(query.IsSelected);
        Assert.IsNull(query.Since);
        Assert.IsNull(query.Until);
        Assert.AreEqual(100, query.Take);
    }

    [TestMethod]
    public void UtilityLedgerQuery_AllFieldsCanBeSet()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var until = DateTimeOffset.UtcNow;
        var query = new UtilityLedgerQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            CandidateItemId = "item-1",
            Expert = RetrievalExpert.Semantic,
            DecisionId = "decision-1",
            IsSelected = true,
            Since = since,
            Until = until,
            Take = 50
        };

        Assert.AreEqual("col-test", query.CollectionId);
        Assert.AreEqual("item-1", query.CandidateItemId);
        Assert.AreEqual(RetrievalExpert.Semantic, query.Expert);
        Assert.AreEqual("decision-1", query.DecisionId);
        Assert.IsTrue(query.IsSelected.Value);
        Assert.AreEqual(since, query.Since);
        Assert.AreEqual(until, query.Until);
        Assert.AreEqual(50, query.Take);
    }

    // =========================================================================
    // 3. IUtilityLedgerStore 接口最小化（3 方法 read-only）
    // =========================================================================

    [TestMethod]
    public void IUtilityLedgerStore_Has3Methods()
    {
        var storeType = typeof(IUtilityLedgerStore);
        var methods = storeType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(3, methods.Length);
        var methodNames = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "GetExpertContributionsAsync", "GetLatestEntryAsync", "QueryAsync" },
            methodNames);
    }

    [TestMethod]
    public void IUtilityLedgerStore_IsInterface()
    {
        Assert.IsTrue(typeof(IUtilityLedgerStore).IsInterface);
    }

    [TestMethod]
    public void IUtilityLedgerStore_AllMethods_ReturnTask()
    {
        var storeType = typeof(IUtilityLedgerStore);
        foreach (var method in storeType.GetMethods())
        {
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"{method.Name} should return Task or Task<T>");
        }
    }

    [TestMethod]
    public void IUtilityLedgerStore_NoWriteMethods()
    {
        // read-only 验证（澄清 #4）：不允许 WriteAsync / AppendAsync / SaveAsync / DeleteAsync
        var storeType = typeof(IUtilityLedgerStore);
        var forbiddenNames = new[] { "WriteAsync", "AppendAsync", "SaveAsync", "DeleteAsync", "BatchUpsertAsync", "RecordAsync" };

        foreach (var method in storeType.GetMethods())
        {
            Assert.IsFalse(
                forbiddenNames.Contains(method.Name),
                $"IUtilityLedgerStore should not expose write method: {method.Name}");
        }
    }

    // =========================================================================
    // 4. ConflictSetKind 枚举 7 值（byte 底层）
    // =========================================================================

    [TestMethod]
    public void ConflictSetKind_Has7Values()
    {
        var values = Enum.GetValues<ConflictSetKind>();
        Assert.AreEqual(7, values.Length);
        Assert.IsTrue(values.Contains(ConflictSetKind.Unknown));
        Assert.IsTrue(values.Contains(ConflictSetKind.Duplicate));
        Assert.IsTrue(values.Contains(ConflictSetKind.Contradicts));
        Assert.IsTrue(values.Contains(ConflictSetKind.SupersedeCycle));
        Assert.IsTrue(values.Contains(ConflictSetKind.SameItemMultipleSources));
        Assert.IsTrue(values.Contains(ConflictSetKind.SectionConflict));
        Assert.IsTrue(values.Contains(ConflictSetKind.BudgetConflict));
    }

    [TestMethod]
    public void ConflictSetKind_ValuesAreUnique()
    {
        var values = Enum.GetValues<ConflictSetKind>().Select(v => (byte)v).ToList();
        Assert.AreEqual(values.Count, values.Distinct().Count());
    }

    [TestMethod]
    public void ConflictSetKind_BackedByByte()
    {
        var underlyingType = Enum.GetUnderlyingType(typeof(ConflictSetKind));
        Assert.AreEqual(typeof(byte), underlyingType);
    }

    [TestMethod]
    public void ConflictSetKind_UnknownIsZero()
    {
        Assert.AreEqual((byte)0, (byte)ConflictSetKind.Unknown);
    }

    // =========================================================================
    // 5. ConflictSetEntry 必填字段 + 可空字段默认
    // =========================================================================

    [TestMethod]
    public void ConflictSetEntry_RequiredFields_AreEnforced()
    {
        var entry = new ConflictSetEntry
        {
            CandidateItemId = "item-1",
            Expert = RetrievalExpert.Lexical,
            Score = 0.85,
            IsSelected = true
        };

        Assert.AreEqual("item-1", entry.CandidateItemId);
        Assert.AreEqual(RetrievalExpert.Lexical, entry.Expert);
        Assert.AreEqual(0.85, entry.Score);
        Assert.IsTrue(entry.IsSelected);
    }

    [TestMethod]
    public void ConflictSetEntry_OptionalFields_DefaultNull()
    {
        var entry = new ConflictSetEntry
        {
            CandidateItemId = "item-1",
            Expert = RetrievalExpert.Lexical,
            Score = 0.85,
            IsSelected = false
        };

        Assert.IsNull(entry.DropReasonCode);
        Assert.IsNull(entry.ReasonDetail);
    }

    // =========================================================================
    // 6. ConflictSet 必填字段 + 默认值
    // =========================================================================

    [TestMethod]
    public void ConflictSet_RequiredFields_AreEnforced()
    {
        var set = MakeConflictSet();

        Assert.AreEqual("conflict-1", set.ConflictSetId);
        Assert.AreEqual("ws-test", set.WorkspaceId);
        Assert.AreEqual("col-test", set.CollectionId);
        Assert.AreEqual(ConflictSetKind.Duplicate, set.Kind);
        Assert.AreEqual(2, set.Entries.Count);
        Assert.AreEqual("decision-1", set.DecisionId);
        Assert.IsTrue(set.MaterializedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void ConflictSet_OptionalFields_DefaultValues()
    {
        var set = MakeConflictSet();

        Assert.IsNull(set.ResolvedItemId);
        Assert.IsNull(set.MemoryStateEventId);
        Assert.IsNull(set.RelationId);
        Assert.IsNull(set.MaterializationBatchId);
        Assert.AreEqual(0, set.Metadata.Count);
    }

    [TestMethod]
    public void ConflictSet_WithExpression_ProducesNewInstance()
    {
        var set = MakeConflictSet();
        var updated = set with { Kind = ConflictSetKind.Contradicts, ResolvedItemId = "item-1" };

        Assert.AreEqual(ConflictSetKind.Duplicate, set.Kind);
        Assert.IsNull(set.ResolvedItemId);
        Assert.AreEqual(ConflictSetKind.Contradicts, updated.Kind);
        Assert.AreEqual("item-1", updated.ResolvedItemId);
        Assert.AreNotSame(set, updated);
    }

    [TestMethod]
    public void ConflictSet_SupersedeCycleKind_HasMemoryStateEventId()
    {
        var set = MakeConflictSet() with
        {
            Kind = ConflictSetKind.SupersedeCycle,
            MemoryStateEventId = "evt-state-1"
        };

        Assert.AreEqual(ConflictSetKind.SupersedeCycle, set.Kind);
        Assert.AreEqual("evt-state-1", set.MemoryStateEventId);
    }

    // =========================================================================
    // 7. ConflictSetQuery 默认值 + 全字段
    // =========================================================================

    [TestMethod]
    public void ConflictSetQuery_DefaultValues()
    {
        var query = new ConflictSetQuery { WorkspaceId = "ws-test" };

        Assert.AreEqual("ws-test", query.WorkspaceId);
        Assert.IsNull(query.CollectionId);
        Assert.IsNull(query.Kind);
        Assert.IsNull(query.CandidateItemId);
        Assert.IsNull(query.DecisionId);
        Assert.IsNull(query.ResolutionStatus);
        Assert.IsNull(query.Since);
        Assert.IsNull(query.Until);
        Assert.AreEqual(100, query.Take);
    }

    [TestMethod]
    public void ConflictSetQuery_AllFieldsCanBeSet()
    {
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var until = DateTimeOffset.UtcNow;
        var query = new ConflictSetQuery
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Kind = ConflictSetKind.BudgetConflict,
            CandidateItemId = "item-1",
            DecisionId = "decision-1",
            ResolutionStatus = ConflictResolutionStatus.AutoResolved,
            Since = since,
            Until = until,
            Take = 50
        };

        Assert.AreEqual("col-test", query.CollectionId);
        Assert.AreEqual(ConflictSetKind.BudgetConflict, query.Kind);
        Assert.AreEqual("item-1", query.CandidateItemId);
        Assert.AreEqual("decision-1", query.DecisionId);
        Assert.AreEqual(since, query.Since);
        Assert.AreEqual(until, query.Until);
        Assert.AreEqual(50, query.Take);
    }

    // =========================================================================
    // 8. IConflictSetStore 接口最小化（3 方法 read-only）
    // =========================================================================

    [TestMethod]
    public void IConflictSetStore_Has3Methods()
    {
        var storeType = typeof(IConflictSetStore);
        var methods = storeType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.AreEqual(3, methods.Length);
        var methodNames = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "GetAsync", "GetConflictsForCandidateAsync", "QueryAsync" },
            methodNames);
    }

    [TestMethod]
    public void IConflictSetStore_IsInterface()
    {
        Assert.IsTrue(typeof(IConflictSetStore).IsInterface);
    }

    [TestMethod]
    public void IConflictSetStore_AllMethods_ReturnTask()
    {
        var storeType = typeof(IConflictSetStore);
        foreach (var method in storeType.GetMethods())
        {
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"{method.Name} should return Task or Task<T>");
        }
    }

    [TestMethod]
    public void IConflictSetStore_NoWriteMethods()
    {
        // read-only 验证（澄清 #4）：不允许 WriteAsync / AppendAsync / SaveAsync / DeleteAsync
        var storeType = typeof(IConflictSetStore);
        var forbiddenNames = new[] { "WriteAsync", "AppendAsync", "SaveAsync", "DeleteAsync", "BatchUpsertAsync", "RecordAsync" };

        foreach (var method in storeType.GetMethods())
        {
            Assert.IsFalse(
                forbiddenNames.Contains(method.Name),
                $"IConflictSetStore should not expose write method: {method.Name}");
        }
    }

    // =========================================================================
    // 9. read-only 反射验证（接口无写方法）
    // =========================================================================

    [TestMethod]
    public void UtilityLedgerAndConflictSetStores_BothReadOnly()
    {
        // 澄清 #4 硬边界：两个 store 都只能是 read-only
        var ledgerStoreType = typeof(IUtilityLedgerStore);
        var conflictSetStoreType = typeof(IConflictSetStore);

        foreach (var method in ledgerStoreType.GetMethods())
        {
            // 所有方法必须返回 Task<T>（不允许 void / 同步写入）
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"IUtilityLedgerStore.{method.Name} must return Task or Task<T>");
        }

        foreach (var method in conflictSetStoreType.GetMethods())
        {
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"IConflictSetStore.{method.Name} must return Task or Task<T>");
        }
    }

    // =========================================================================
    // 10. sealed record / static / no async void 反射验证
    // =========================================================================

    [TestMethod]
    public void UtilityLedgerEntry_IsSealedClassRecord()
    {
        var type = typeof(UtilityLedgerEntry);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType); // record class（reference type）
    }

    [TestMethod]
    public void UtilityLedgerQuery_IsSealedClassRecord()
    {
        var type = typeof(UtilityLedgerQuery);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    [TestMethod]
    public void ConflictSetEntry_IsSealedClassRecord()
    {
        var type = typeof(ConflictSetEntry);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    [TestMethod]
    public void ConflictSet_IsSealedClassRecord()
    {
        var type = typeof(ConflictSet);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    [TestMethod]
    public void ConflictSetQuery_IsSealedClassRecord()
    {
        var type = typeof(ConflictSetQuery);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    [TestMethod]
    public void IUtilityLedgerStore_And_IConflictSetStore_NoAsyncVoidMethods()
    {
        // 反射验证：契约接口不暴露 async void（违反 fire-and-forget 反模式）
        foreach (var method in typeof(IUtilityLedgerStore).GetMethods())
        {
            Assert.AreNotEqual(typeof(void), method.ReturnType,
                $"IUtilityLedgerStore.{method.Name} must not return void");
        }
        foreach (var method in typeof(IConflictSetStore).GetMethods())
        {
            Assert.AreNotEqual(typeof(void), method.ReturnType,
                $"IConflictSetStore.{method.Name} must not return void");
        }
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static UtilityLedgerEntry MakeLedgerEntry(
        string entryId = "ledger-1",
        RetrievalExpert expert = RetrievalExpert.Semantic,
        bool isSelected = true)
    {
        return new UtilityLedgerEntry
        {
            EntryId = entryId,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            CandidateItemId = "item-1",
            Expert = expert,
            UtilityContribution = 0.4,
            DeterministicScore = 0.8,
            ModelScore = null,
            FinalScore = 0.9,
            IsSelected = isSelected,
            DecisionId = "decision-1",
            PolicyVersion = "decision-schema/2.0",
            RouterId = null,
            MaterializedAt = DateTimeOffset.UtcNow,
            MaterializationBatchId = null
        };
    }

    private static ConflictSet MakeConflictSet()
    {
        return new ConflictSet
        {
            ConflictSetId = "conflict-1",
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Kind = ConflictSetKind.Duplicate,
            Entries = new[]
            {
                new ConflictSetEntry
                {
                    CandidateItemId = "item-1",
                    Expert = RetrievalExpert.Lexical,
                    Score = 0.85,
                    IsSelected = true
                },
                new ConflictSetEntry
                {
                    CandidateItemId = "item-2",
                    Expert = RetrievalExpert.Semantic,
                    Score = 0.80,
                    IsSelected = false,
                    DropReasonCode = "duplicate-suppressed"
                }
            },
            DecisionId = "decision-1",
            MaterializedAt = DateTimeOffset.UtcNow
        };
    }
}
