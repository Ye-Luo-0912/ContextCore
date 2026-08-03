using System.Reflection;
using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// Agent Context 数据契约测试。
///
/// 验证目标：
/// 1. AgentContextSnapshot record 必填字段 + 默认值 + with 表达式
/// 2. AgentContextSection record 必填字段 + 默认值
/// 3. AgentTaskState record 必填字段 + 默认值 + with 表达式
/// 4. AgentContextDelta record 必填字段 + 默认值 + with 表达式
/// 5. AgentContextSchemaVersions 常量定义
/// 6. sealed record 反射验证
/// 7. 默认集合字段为空（非 null）
/// 8. 不可变性：with 表达式产生新实例
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class AgentContextDataContractsTests
{
    // =========================================================================
    // 1. AgentContextSnapshot record
    // =========================================================================

    [TestMethod]
    public void AgentContextSnapshot_RequiredFields_AreEnforced()
    {
        var snapshot = MakeSnapshot();

        Assert.AreEqual("snap-1", snapshot.SnapshotId);
        Assert.IsTrue(snapshot.CreatedAt > DateTimeOffset.MinValue);
        Assert.AreEqual(500, snapshot.TokenBudget);
        Assert.AreEqual(420, snapshot.ActualTokens);
    }

    [TestMethod]
    public void AgentContextSnapshot_DefaultCollections_Empty()
    {
        var snapshot = MakeSnapshot();

        Assert.AreEqual(0, snapshot.Sections.Count);
        Assert.AreEqual(0, snapshot.DecisionRequestIds.Count);
        Assert.AreEqual(0, snapshot.ConstraintIds.Count);
        Assert.AreEqual(0, snapshot.ToolCallRefs.Count);
        Assert.AreEqual(0, snapshot.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextSnapshot_DefaultSchemaVersion_IsV1()
    {
        var snapshot = MakeSnapshot();

        Assert.AreEqual(AgentContextSchemaVersions.SnapshotV1, snapshot.SchemaVersion);
    }

    [TestMethod]
    public void AgentContextSnapshot_AllFieldsCanBeSet()
    {
        var session = MakeSessionId();
        var sections = new[] { MakeSection("system"), MakeSection("task-context") };
        var snapshot = new AgentContextSnapshot
        {
            SnapshotId = "snap-1",
            Session = session,
            CreatedAt = DateTimeOffset.UtcNow,
            TokenBudget = 1000,
            ActualTokens = 800,
            Sections = sections,
            DecisionRequestIds = new[] { "req-1", "req-2" },
            ConstraintIds = new[] { "cstr-1" },
            ToolCallRefs = new Dictionary<string, string> { ["tc-1"] = "search" },
            Metadata = new Dictionary<string, string> { ["source"] = "test" },
            SchemaVersion = "custom/2.0"
        };

        Assert.AreEqual(2, snapshot.Sections.Count);
        Assert.AreEqual(2, snapshot.DecisionRequestIds.Count);
        Assert.AreEqual(1, snapshot.ConstraintIds.Count);
        Assert.AreEqual(1, snapshot.ToolCallRefs.Count);
        Assert.AreEqual("search", snapshot.ToolCallRefs["tc-1"]);
        Assert.AreEqual(1, snapshot.Metadata.Count);
        Assert.AreEqual("custom/2.0", snapshot.SchemaVersion);
    }

    [TestMethod]
    public void AgentContextSnapshot_WithExpression_ProducesNewInstance()
    {
        var original = MakeSnapshot();
        var updated = original with { ActualTokens = 450 };

        Assert.AreEqual(420, original.ActualTokens);
        Assert.AreEqual(450, updated.ActualTokens);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void AgentContextSnapshot_IsSealedRecord()
    {
        var type = typeof(AgentContextSnapshot);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 2. AgentContextSection record
    // =========================================================================

    [TestMethod]
    public void AgentContextSection_RequiredFields_AreEnforced()
    {
        var section = MakeSection("system");

        Assert.AreEqual("system", section.SectionName);
        Assert.AreEqual("System prompt content", section.Content);
    }

    [TestMethod]
    public void AgentContextSection_DefaultFields()
    {
        var section = MakeSection("system");

        Assert.AreEqual(0, section.SortOrder);
        Assert.AreEqual(0, section.TokenBudget);
        Assert.AreEqual(0, section.ActualTokens);
        Assert.AreEqual(string.Empty, section.Source);
        Assert.AreEqual(0, section.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextSection_AllFieldsCanBeSet()
    {
        var section = new AgentContextSection
        {
            SectionName = "task-context",
            SortOrder = 2,
            TokenBudget = 300,
            ActualTokens = 280,
            Content = "Task description here",
            Source = "ContextCore.PackageBuilder",
            Metadata = new Dictionary<string, string> { ["itemId"] = "item-1" }
        };

        Assert.AreEqual(2, section.SortOrder);
        Assert.AreEqual(300, section.TokenBudget);
        Assert.AreEqual(280, section.ActualTokens);
        Assert.AreEqual("ContextCore.PackageBuilder", section.Source);
        Assert.AreEqual(1, section.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextSection_WithExpression_ProducesNewInstance()
    {
        var original = MakeSection("system");
        var updated = original with { Content = "Updated content" };

        Assert.AreEqual("System prompt content", original.Content);
        Assert.AreEqual("Updated content", updated.Content);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void AgentContextSection_IsSealedRecord()
    {
        var type = typeof(AgentContextSection);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 3. AgentTaskState record
    // =========================================================================

    [TestMethod]
    public void AgentTaskState_RequiredFields_AreEnforced()
    {
        var task = MakeTaskState();

        Assert.AreEqual("task-1", task.TaskId);
        Assert.AreEqual("executing", task.Status);
        Assert.IsTrue(task.CreatedAt > DateTimeOffset.MinValue);
        Assert.IsTrue(task.UpdatedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void AgentTaskState_DefaultFields()
    {
        var task = MakeTaskState();

        Assert.AreEqual(string.Empty, task.Description);
        Assert.IsNull(task.CurrentTurnId);
        Assert.AreEqual(0, task.CompletedSteps);
        Assert.AreEqual(0, task.EstimatedSteps);
        Assert.AreEqual(0, task.ConsumedTokens);
        Assert.IsNull(task.LastSnapshotId);
        Assert.IsNull(task.ErrorMessage);
        Assert.AreEqual(0, task.Metadata.Count);
    }

    [TestMethod]
    public void AgentTaskState_AllFieldsCanBeSet()
    {
        var task = new AgentTaskState
        {
            TaskId = "task-1",
            Session = MakeSessionId(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = "failed",
            Description = "Search and summarize",
            CurrentTurnId = "turn-3",
            CompletedSteps = 2,
            EstimatedSteps = 5,
            ConsumedTokens = 1200,
            LastSnapshotId = "snap-2",
            ErrorMessage = "Tool call timed out",
            Metadata = new Dictionary<string, string> { ["reason"] = "timeout" }
        };

        Assert.AreEqual("failed", task.Status);
        Assert.AreEqual("Search and summarize", task.Description);
        Assert.AreEqual("turn-3", task.CurrentTurnId);
        Assert.AreEqual(2, task.CompletedSteps);
        Assert.AreEqual(5, task.EstimatedSteps);
        Assert.AreEqual(1200, task.ConsumedTokens);
        Assert.AreEqual("snap-2", task.LastSnapshotId);
        Assert.AreEqual("Tool call timed out", task.ErrorMessage);
        Assert.AreEqual(1, task.Metadata.Count);
    }

    [TestMethod]
    public void AgentTaskState_WithExpression_ProducesNewInstance()
    {
        var original = MakeTaskState();
        var updated = original with { Status = "completed", CompletedSteps = 5 };

        Assert.AreEqual("executing", original.Status);
        Assert.AreEqual(0, original.CompletedSteps);
        Assert.AreEqual("completed", updated.Status);
        Assert.AreEqual(5, updated.CompletedSteps);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void AgentTaskState_IsSealedRecord()
    {
        var type = typeof(AgentTaskState);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 4. AgentContextDelta record
    // =========================================================================

    [TestMethod]
    public void AgentContextDelta_RequiredFields_AreEnforced()
    {
        var delta = MakeDelta();

        Assert.AreEqual("delta-1", delta.DeltaId);
        Assert.AreEqual("snap-1", delta.FromSnapshotId);
        Assert.AreEqual("snap-2", delta.ToSnapshotId);
        Assert.IsTrue(delta.CreatedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void AgentContextDelta_DefaultCollections_Empty()
    {
        var delta = MakeDelta();

        Assert.AreEqual(0, delta.AddedSections.Count);
        Assert.AreEqual(0, delta.ModifiedSections.Count);
        Assert.AreEqual(0, delta.RemovedSections.Count);
        Assert.AreEqual(0, delta.AddedDecisionIds.Count);
        Assert.AreEqual(0, delta.RemovedDecisionIds.Count);
        Assert.AreEqual(0, delta.AddedConstraintIds.Count);
        Assert.AreEqual(0, delta.RemovedConstraintIds.Count);
        Assert.AreEqual(0, delta.AddedToolCallRefs.Count);
        Assert.AreEqual(0, delta.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextDelta_DefaultFields()
    {
        var delta = MakeDelta();

        Assert.AreEqual(0, delta.TokenDelta);
        Assert.AreEqual(string.Empty, delta.Source);
    }

    [TestMethod]
    public void AgentContextDelta_AllFieldsCanBeSet()
    {
        var delta = new AgentContextDelta
        {
            DeltaId = "delta-1",
            Session = MakeSessionId(),
            FromSnapshotId = "snap-1",
            ToSnapshotId = "snap-2",
            CreatedAt = DateTimeOffset.UtcNow,
            AddedSections = new[] { "tool-results" },
            ModifiedSections = new[] { "task-context" },
            RemovedSections = new[] { "obsolete" },
            AddedDecisionIds = new[] { "req-2" },
            RemovedDecisionIds = new[] { "req-1" },
            AddedConstraintIds = new[] { "cstr-2" },
            RemovedConstraintIds = new[] { "cstr-1" },
            AddedToolCallRefs = new Dictionary<string, string> { ["tc-2"] = "calculator" },
            TokenDelta = 150,
            Source = "tool-result-ingestion",
            Metadata = new Dictionary<string, string> { ["toolCallId"] = "tc-2" }
        };

        Assert.AreEqual(1, delta.AddedSections.Count);
        Assert.AreEqual("tool-results", delta.AddedSections[0]);
        Assert.AreEqual(1, delta.ModifiedSections.Count);
        Assert.AreEqual("task-context", delta.ModifiedSections[0]);
        Assert.AreEqual(1, delta.RemovedSections.Count);
        Assert.AreEqual("obsolete", delta.RemovedSections[0]);
        Assert.AreEqual(1, delta.AddedDecisionIds.Count);
        Assert.AreEqual("req-2", delta.AddedDecisionIds[0]);
        Assert.AreEqual(1, delta.RemovedDecisionIds.Count);
        Assert.AreEqual("req-1", delta.RemovedDecisionIds[0]);
        Assert.AreEqual(1, delta.AddedConstraintIds.Count);
        Assert.AreEqual(1, delta.RemovedConstraintIds.Count);
        Assert.AreEqual(1, delta.AddedToolCallRefs.Count);
        Assert.AreEqual("calculator", delta.AddedToolCallRefs["tc-2"]);
        Assert.AreEqual(150, delta.TokenDelta);
        Assert.AreEqual("tool-result-ingestion", delta.Source);
        Assert.AreEqual(1, delta.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextDelta_WithExpression_ProducesNewInstance()
    {
        var original = MakeDelta();
        var updated = original with { TokenDelta = 100 };

        Assert.AreEqual(0, original.TokenDelta);
        Assert.AreEqual(100, updated.TokenDelta);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void AgentContextDelta_IsSealedRecord()
    {
        var type = typeof(AgentContextDelta);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 5. AgentContextSchemaVersions 常量
    // =========================================================================

    [TestMethod]
    public void AgentContextSchemaVersions_AllConstantsDefined()
    {
        Assert.AreEqual("agent-context-snapshot/1.0", AgentContextSchemaVersions.SnapshotV1);
        Assert.AreEqual("agent-task-state/1.0", AgentContextSchemaVersions.TaskStateV1);
        Assert.AreEqual("agent-context-delta/1.0", AgentContextSchemaVersions.DeltaV1);
    }

    [TestMethod]
    public void AgentContextSchemaVersions_AreConstStrings()
    {
        var type = typeof(AgentContextSchemaVersions);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        Assert.IsTrue(fields.Length >= 3);
        foreach (var field in fields)
        {
            Assert.IsTrue(field.IsLiteral, $"{field.Name} should be const");
            Assert.IsFalse(field.IsInitOnly, $"{field.Name} should be const (not readonly)");
            Assert.AreEqual(typeof(string), field.FieldType);
        }
    }

    [TestMethod]
    public void AgentContextSchemaVersions_IsStaticClass()
    {
        var type = typeof(AgentContextSchemaVersions);
        Assert.IsTrue(type.IsAbstract);
        Assert.IsTrue(type.IsSealed);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static AgentSessionId MakeSessionId()
    {
        return new AgentSessionId
        {
            Value = "session-1",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentContextSnapshot MakeSnapshot()
    {
        return new AgentContextSnapshot
        {
            SnapshotId = "snap-1",
            Session = MakeSessionId(),
            CreatedAt = DateTimeOffset.UtcNow,
            TokenBudget = 500,
            ActualTokens = 420
        };
    }

    private static AgentContextSection MakeSection(string name)
    {
        return new AgentContextSection
        {
            SectionName = name,
            Content = "System prompt content"
        };
    }

    private static AgentTaskState MakeTaskState()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentTaskState
        {
            TaskId = "task-1",
            Session = MakeSessionId(),
            CreatedAt = now,
            UpdatedAt = now,
            Status = "executing"
        };
    }

    private static AgentContextDelta MakeDelta()
    {
        return new AgentContextDelta
        {
            DeltaId = "delta-1",
            Session = MakeSessionId(),
            FromSnapshotId = "snap-1",
            ToSnapshotId = "snap-2",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
