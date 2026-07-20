using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// R24-2：InMemoryAgentTaskStateStore 实现测试。
///
/// 覆盖：
///   1. SaveAsync null 抛异常
///   2. SaveAsync 新增 + GetAsync 往返
///   3. SaveAsync 同 TaskId 覆盖
///   4. GetAsync 不存在返回 null
///   5. GetAsync 空 TaskId 抛异常
///   6. ListBySessionAsync 按 session 过滤 + UpdatedAt 倒序
///   7. ListBySessionAsync null session 抛异常
///   8. DeleteAsync 存在/不存在
///   9. DeleteAsync 空 TaskId 抛异常
///  10. Count 属性
///  11. CancellationToken 传递
/// </summary>
[TestClass]
[TestCategory("R24")]
public sealed class InMemoryAgentTaskStateStoreTests
{
    // =========================================================================
    // 1. SaveAsync
    // =========================================================================

    [TestMethod]
    public async Task SaveAsync_NullTaskState_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveAsync(null!));
    }

    [TestMethod]
    public async Task SaveAsync_NewTask_IncrementsCount()
    {
        var store = new InMemoryAgentTaskStateStore();
        var task = MakeTask("task-1");

        await store.SaveAsync(task);

        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public async Task SaveAsync_SameTaskId_Overwrites()
    {
        var store = new InMemoryAgentTaskStateStore();
        var task1 = MakeTask("task-1", status: "Running");
        var task2 = MakeTask("task-1", status: "Completed");

        await store.SaveAsync(task1);
        await store.SaveAsync(task2);

        Assert.AreEqual(1, store.Count);
        var fetched = await store.GetAsync("task-1");
        Assert.AreEqual("Completed", fetched!.Status);
    }

    // =========================================================================
    // 2. GetAsync
    // =========================================================================

    [TestMethod]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryAgentTaskStateStore();
        var result = await store.GetAsync("nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetAsync_EmptyTaskId_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync(""));
    }

    [TestMethod]
    public async Task GetAsync_AfterSave_ReturnsTask()
    {
        var store = new InMemoryAgentTaskStateStore();
        var task = MakeTask("task-1", description: "do something");
        await store.SaveAsync(task);

        var fetched = await store.GetAsync("task-1");

        Assert.IsNotNull(fetched);
        Assert.AreEqual("task-1", fetched!.TaskId);
        Assert.AreEqual("do something", fetched.Description);
    }

    // =========================================================================
    // 3. ListBySessionAsync
    // =========================================================================

    [TestMethod]
    public async Task ListBySessionAsync_NullSession_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListBySessionAsync(null!));
    }

    [TestMethod]
    public async Task ListBySessionAsync_FiltersBySession()
    {
        var store = new InMemoryAgentTaskStateStore();
        await store.SaveAsync(MakeTask("task-1", sessionValue: "session-A"));
        await store.SaveAsync(MakeTask("task-2", sessionValue: "session-A"));
        await store.SaveAsync(MakeTask("task-3", sessionValue: "session-B"));

        var result = await store.ListBySessionAsync(MakeSessionId("session-A"));

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(t => t.Session.Value == "session-A"));
    }

    [TestMethod]
    public async Task ListBySessionAsync_OrdersByUpdatedAtDescending()
    {
        var store = new InMemoryAgentTaskStateStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);

        await store.SaveAsync(MakeTask("task-old", sessionValue: "session-1", updatedAt: t1));
        await store.SaveAsync(MakeTask("task-new", sessionValue: "session-1", updatedAt: t3));
        await store.SaveAsync(MakeTask("task-mid", sessionValue: "session-1", updatedAt: t2));

        var result = await store.ListBySessionAsync(MakeSessionId("session-1"));

        Assert.AreEqual("task-new", result[0].TaskId);
        Assert.AreEqual("task-mid", result[1].TaskId);
        Assert.AreEqual("task-old", result[2].TaskId);
    }

    [TestMethod]
    public async Task ListBySessionAsync_EmptySession_ReturnsEmptyList()
    {
        var store = new InMemoryAgentTaskStateStore();
        await store.SaveAsync(MakeTask("task-1", sessionValue: "session-A"));

        var result = await store.ListBySessionAsync(MakeSessionId("session-B"));

        Assert.AreEqual(0, result.Count);
    }

    // =========================================================================
    // 4. DeleteAsync
    // =========================================================================

    [TestMethod]
    public async Task DeleteAsync_Existing_ReturnsTrue()
    {
        var store = new InMemoryAgentTaskStateStore();
        await store.SaveAsync(MakeTask("task-1"));

        var result = await store.DeleteAsync("task-1");

        Assert.IsTrue(result);
        Assert.AreEqual(0, store.Count);
        Assert.IsNull(await store.GetAsync("task-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_Nonexistent_ReturnsFalse()
    {
        var store = new InMemoryAgentTaskStateStore();
        var result = await store.DeleteAsync("nonexistent");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyTaskId_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync(""));
    }

    // =========================================================================
    // 5. Count
    // =========================================================================

    [TestMethod]
    public void Count_StartsAtZero()
    {
        var store = new InMemoryAgentTaskStateStore();
        Assert.AreEqual(0, store.Count);
    }

    // =========================================================================
    // 6. CancellationToken
    // =========================================================================

    [TestMethod]
    public async Task SaveAsync_CancelledToken_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => store.SaveAsync(MakeTask("task-1"), cts.Token));
    }

    [TestMethod]
    public async Task GetAsync_CancelledToken_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => store.GetAsync("task-1", cts.Token));
    }

    [TestMethod]
    public async Task DeleteAsync_CancelledToken_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => store.DeleteAsync("task-1", cts.Token));
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

    private static AgentTaskState MakeTask(
        string taskId,
        string sessionValue = "session-1",
        string status = "Pending",
        string description = "",
        DateTimeOffset? updatedAt = null) => new()
        {
            TaskId = taskId,
            Session = MakeSessionId(sessionValue),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
            Status = status,
            Description = description
        };
}
