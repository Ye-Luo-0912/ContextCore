using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// R23-6：DefaultAgentContextDeltaCalculator 实现测试。
///
/// 覆盖：
///   1. null 输入抛异常
///   2. Session mismatch 抛异常
///   3. 相同 snapshot → 空 delta（TokenDelta=0）
///   4. Added sections
///   5. Removed sections
///   6. Modified sections（Content 变化）
///   7. Added/Removed DecisionIds
///   8. Added/Removed ConstraintIds
///   9. AddedToolCallRefs
///  10. TokenDelta 正/负/零
///  11. 自定义 deltaId + source
///  12. 自动生成 deltaId
///  13. FromSnapshotId / ToSnapshotId 映射正确
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class DefaultAgentContextDeltaCalculatorTests
{
    // =========================================================================
    // 1. null 输入
    // =========================================================================

    [TestMethod]
    public void Calculate_NullFromSnapshot_Throws()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var to = MakeSnapshot("snap-2");
        Assert.ThrowsException<ArgumentNullException>(() => calc.Calculate(null!, to));
    }

    [TestMethod]
    public void Calculate_NullToSnapshot_Throws()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1");
        Assert.ThrowsException<ArgumentNullException>(() => calc.Calculate(from, null!));
    }

    // =========================================================================
    // 2. Session mismatch
    // =========================================================================

    [TestMethod]
    public void Calculate_SessionMismatch_Throws()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", sessionValue: "session-A");
        var to = MakeSnapshot("snap-2", sessionValue: "session-B");

        Assert.ThrowsException<ArgumentException>(() => calc.Calculate(from, to));
    }

    // =========================================================================
    // 3. 相同 snapshot → 空 delta
    // =========================================================================

    [TestMethod]
    public void Calculate_IdenticalSnapshots_ReturnsEmptyDelta()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", sections: new[] { MakeSection("a", "content") });
        var to = MakeSnapshot("snap-2", sections: new[] { MakeSection("a", "content") });

        var delta = calc.Calculate(from, to);

        Assert.AreEqual(0, delta.AddedSections.Count);
        Assert.AreEqual(0, delta.ModifiedSections.Count);
        Assert.AreEqual(0, delta.RemovedSections.Count);
        Assert.AreEqual(0, delta.AddedDecisionIds.Count);
        Assert.AreEqual(0, delta.RemovedDecisionIds.Count);
        Assert.AreEqual(0, delta.AddedConstraintIds.Count);
        Assert.AreEqual(0, delta.RemovedConstraintIds.Count);
        Assert.AreEqual(0, delta.AddedToolCallRefs.Count);
        Assert.AreEqual(0, delta.TokenDelta);
    }

    // =========================================================================
    // 4. Added sections
    // =========================================================================

    [TestMethod]
    public void Calculate_NewSectionInTo_ReturnsAddedSection()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1");
        var to = MakeSnapshot("snap-2", sections: new[] { MakeSection("new-section", "content") });

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEqual(new[] { "new-section" }, delta.AddedSections.ToList());
    }

    // =========================================================================
    // 5. Removed sections
    // =========================================================================

    [TestMethod]
    public void Calculate_SectionMissingInTo_ReturnsRemovedSection()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", sections: new[] { MakeSection("old-section", "content") });
        var to = MakeSnapshot("snap-2");

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEqual(new[] { "old-section" }, delta.RemovedSections.ToList());
    }

    // =========================================================================
    // 6. Modified sections
    // =========================================================================

    [TestMethod]
    public void Calculate_SectionContentChanged_ReturnsModifiedSection()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", sections: new[] { MakeSection("section", "old-content") });
        var to = MakeSnapshot("snap-2", sections: new[] { MakeSection("section", "new-content") });

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEqual(new[] { "section" }, delta.ModifiedSections.ToList());
        Assert.AreEqual(0, delta.AddedSections.Count);
        Assert.AreEqual(0, delta.RemovedSections.Count);
    }

    // =========================================================================
    // 7. DecisionIds
    // =========================================================================

    [TestMethod]
    public void Calculate_DecisionIdsAdded_ReturnsAddedDecisionIds()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", decisionIds: new[] { "dec-1" });
        var to = MakeSnapshot("snap-2", decisionIds: new[] { "dec-1", "dec-2", "dec-3" });

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEquivalent(new[] { "dec-2", "dec-3" }, delta.AddedDecisionIds.ToList());
        Assert.AreEqual(0, delta.RemovedDecisionIds.Count);
    }

    [TestMethod]
    public void Calculate_DecisionIdsRemoved_ReturnsRemovedDecisionIds()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", decisionIds: new[] { "dec-1", "dec-2" });
        var to = MakeSnapshot("snap-2", decisionIds: new[] { "dec-1" });

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEquivalent(new[] { "dec-2" }, delta.RemovedDecisionIds.ToList());
        Assert.AreEqual(0, delta.AddedDecisionIds.Count);
    }

    // =========================================================================
    // 8. ConstraintIds
    // =========================================================================

    [TestMethod]
    public void Calculate_ConstraintIdsAdded_ReturnsAddedConstraintIds()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", constraintIds: new[] { "con-1" });
        var to = MakeSnapshot("snap-2", constraintIds: new[] { "con-1", "con-2" });

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEquivalent(new[] { "con-2" }, delta.AddedConstraintIds.ToList());
    }

    [TestMethod]
    public void Calculate_ConstraintIdsRemoved_ReturnsRemovedConstraintIds()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", constraintIds: new[] { "con-1", "con-2" });
        var to = MakeSnapshot("snap-2");

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEquivalent(new[] { "con-1", "con-2" }, delta.RemovedConstraintIds.ToList());
    }

    // =========================================================================
    // 9. ToolCallRefs
    // =========================================================================

    [TestMethod]
    public void Calculate_NewToolCallRefsInTo_ReturnsAddedToolCallRefs()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1",
            toolCallRefs: new Dictionary<string, string> { ["call-1"] = "search" });
        var to = MakeSnapshot("snap-2",
            toolCallRefs: new Dictionary<string, string>
            {
                ["call-1"] = "search",
                ["call-2"] = "execute"
            });

        var delta = calc.Calculate(from, to);

        Assert.AreEqual(1, delta.AddedToolCallRefs.Count);
        Assert.AreEqual("execute", delta.AddedToolCallRefs["call-2"]);
    }

    // =========================================================================
    // 10. TokenDelta
    // =========================================================================

    [TestMethod]
    public void Calculate_TokenDeltaPositive_WhenToHasMoreTokens()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", actualTokens: 100);
        var to = MakeSnapshot("snap-2", actualTokens: 150);

        var delta = calc.Calculate(from, to);

        Assert.AreEqual(50, delta.TokenDelta);
    }

    [TestMethod]
    public void Calculate_TokenDeltaNegative_WhenToHasFewerTokens()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", actualTokens: 200);
        var to = MakeSnapshot("snap-2", actualTokens: 120);

        var delta = calc.Calculate(from, to);

        Assert.AreEqual(-80, delta.TokenDelta);
    }

    [TestMethod]
    public void Calculate_TokenDeltaZero_WhenEqual()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1", actualTokens: 100);
        var to = MakeSnapshot("snap-2", actualTokens: 100);

        var delta = calc.Calculate(from, to);

        Assert.AreEqual(0, delta.TokenDelta);
    }

    // =========================================================================
    // 11. 自定义 deltaId + source
    // =========================================================================

    [TestMethod]
    public void Calculate_CustomDeltaId_IsUsed()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1");
        var to = MakeSnapshot("snap-2");

        var delta = calc.Calculate(from, to, deltaId: "custom-delta-1");

        Assert.AreEqual("custom-delta-1", delta.DeltaId);
    }

    [TestMethod]
    public void Calculate_CustomSource_IsUsed()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1");
        var to = MakeSnapshot("snap-2");

        var delta = calc.Calculate(from, to, source: "agent-triggered");

        Assert.AreEqual("agent-triggered", delta.Source);
    }

    // =========================================================================
    // 12. 自动生成 deltaId
    // =========================================================================

    [TestMethod]
    public void Calculate_NullDeltaId_AutoGenerated()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1");
        var to = MakeSnapshot("snap-2");

        var delta = calc.Calculate(from, to);

        Assert.IsFalse(string.IsNullOrEmpty(delta.DeltaId));
        Assert.IsTrue(delta.DeltaId.StartsWith("delta-", StringComparison.Ordinal));
    }

    // =========================================================================
    // 13. SnapshotId 映射
    // =========================================================================

    [TestMethod]
    public void Calculate_SnapshotIdsMappedCorrectly()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-from");
        var to = MakeSnapshot("snap-to");

        var delta = calc.Calculate(from, to);

        Assert.AreEqual("snap-from", delta.FromSnapshotId);
        Assert.AreEqual("snap-to", delta.ToSnapshotId);
    }

    // =========================================================================
    // 14. Session 字段映射
    // =========================================================================

    [TestMethod]
    public void Calculate_SessionMappedFromToSnapshot()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var sessionId = MakeSessionId("session-1");
        var from = MakeSnapshot("snap-1", session: sessionId);
        var to = MakeSnapshot("snap-2", session: sessionId);

        var delta = calc.Calculate(from, to);

        Assert.AreEqual(sessionId.Value, delta.Session.Value);
    }

    // =========================================================================
    // 15. 综合场景：多种变更同时发生
    // =========================================================================

    [TestMethod]
    public void Calculate_MixedChanges_AllCaptured()
    {
        var calc = new DefaultAgentContextDeltaCalculator();
        var from = MakeSnapshot("snap-1",
            sections: new[]
            {
                MakeSection("unchanged", "same"),
                MakeSection("modified", "old"),
                MakeSection("removed", "data")
            },
            decisionIds: new[] { "dec-1" },
            constraintIds: new[] { "con-1" },
            actualTokens: 100);
        var to = MakeSnapshot("snap-2",
            sections: new[]
            {
                MakeSection("unchanged", "same"),
                MakeSection("modified", "new"),
                MakeSection("added", "new-section")
            },
            decisionIds: new[] { "dec-1", "dec-2" },
            constraintIds: Array.Empty<string>(),
            actualTokens: 130);

        var delta = calc.Calculate(from, to);

        CollectionAssert.AreEquivalent(new[] { "added" }, delta.AddedSections.ToList());
        CollectionAssert.AreEquivalent(new[] { "modified" }, delta.ModifiedSections.ToList());
        CollectionAssert.AreEquivalent(new[] { "removed" }, delta.RemovedSections.ToList());
        CollectionAssert.AreEquivalent(new[] { "dec-2" }, delta.AddedDecisionIds.ToList());
        CollectionAssert.AreEquivalent(new[] { "con-1" }, delta.RemovedConstraintIds.ToList());
        Assert.AreEqual(30, delta.TokenDelta);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static AgentSessionId MakeSessionId(string value) => new()
    {
        Value = value,
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = "ws-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentContextSnapshot MakeSnapshot(
        string snapshotId,
        string? sessionValue = null,
        AgentSessionId? session = null,
        IReadOnlyList<AgentContextSection>? sections = null,
        IReadOnlyList<string>? decisionIds = null,
        IReadOnlyList<string>? constraintIds = null,
        IReadOnlyDictionary<string, string>? toolCallRefs = null,
        int actualTokens = 0)
    {
        return new AgentContextSnapshot
        {
            SnapshotId = snapshotId,
            Session = session ?? MakeSessionId(sessionValue ?? "session-1"),
            CreatedAt = DateTimeOffset.UtcNow,
            TokenBudget = 1000,
            ActualTokens = actualTokens,
            Sections = sections ?? Array.Empty<AgentContextSection>(),
            DecisionRequestIds = decisionIds ?? Array.Empty<string>(),
            ConstraintIds = constraintIds ?? Array.Empty<string>(),
            ToolCallRefs = toolCallRefs ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };
    }

    private static AgentContextSection MakeSection(string name, string content) => new()
    {
        SectionName = name,
        SortOrder = 0,
        TokenBudget = 1000,
        ActualTokens = content.Length / 4,
        Content = content,
        Source = "test"
    };
}
