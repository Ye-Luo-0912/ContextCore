using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// InMemoryAgentTaskStateStore 实现测试。
///
/// 覆盖：
/// 1. SaveAsync null 抛异常
/// 2. SaveAsync 新增 + GetAsync 往返
/// 3. SaveAsync 同 (workspaceId, TaskId) 覆盖
/// 4. GetAsync 不存在返回 null
/// 5. GetAsync 空 workspaceId/TaskId 抛异常
/// 6. ListBySessionAsync 按 session 过滤 + UpdatedAt 倒序
/// 7. ListBySessionAsync null session 抛异常
/// 8. DeleteAsync 存在/不存在
/// 9. DeleteAsync 空 workspaceId/TaskId 抛异常
/// 10. Count 属性
/// 11. CancellationToken 传递
/// 12. 跨 workspace 隔离
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
    public async Task SaveAsync_SameWorkspaceAndTaskId_Overwrites()
    {
        // 主键 (workspace_id, task_id) — 同 workspace 同 task id 覆盖
        var store = new InMemoryAgentTaskStateStore();
        var task1 = MakeTask("task-1", status: "Running");
        var task2 = MakeTask("task-1", status: "Completed");

        await store.SaveAsync(task1);
        await store.SaveAsync(task2);

        Assert.AreEqual(1, store.Count);
        var fetched = await store.GetAsync("ws-1", "task-1");
        Assert.AreEqual("Completed", fetched!.Status);
    }

    // =========================================================================
    // 2. GetAsync
    // =========================================================================

    [TestMethod]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryAgentTaskStateStore();
        var result = await store.GetAsync("ws-1", "nonexistent");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetAsync_EmptyWorkspaceId_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("", "task-1"));
    }

    [TestMethod]
    public async Task GetAsync_EmptyTaskId_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task GetAsync_AfterSave_ReturnsTask()
    {
        var store = new InMemoryAgentTaskStateStore();
        var task = MakeTask("task-1", description: "do something");
        await store.SaveAsync(task);

        var fetched = await store.GetAsync("ws-1", "task-1");

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

        var result = await store.DeleteAsync("ws-1", "task-1");

        Assert.IsTrue(result);
        Assert.AreEqual(0, store.Count);
        Assert.IsNull(await store.GetAsync("ws-1", "task-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_Nonexistent_ReturnsFalse()
    {
        var store = new InMemoryAgentTaskStateStore();
        var result = await store.DeleteAsync("ws-1", "nonexistent");
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyWorkspaceId_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("", "task-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_EmptyTaskId_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("ws-1", ""));
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
            () => store.GetAsync("ws-1", "task-1", cts.Token));
    }

    [TestMethod]
    public async Task DeleteAsync_CancelledToken_Throws()
    {
        var store = new InMemoryAgentTaskStateStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => store.DeleteAsync("ws-1", "task-1", cts.Token));
    }

    // =========================================================================
    // 7. 跨 workspace 隔离
    // =========================================================================

    [TestMethod]
    public async Task CrossWorkspace_SameTaskId_RemainsIsolated()
    {
        // 两个 workspace 各自保存同 ID task，应互不可见
        var store = new InMemoryAgentTaskStateStore();
        var ws1Task = MakeTask("task-shared", sessionValue: "session-ws1", status: "Running", workspaceId: "ws-1");
        var ws2Task = MakeTask("task-shared", sessionValue: "session-ws2", status: "Completed", workspaceId: "ws-2");

        await store.SaveAsync(ws1Task);
        await store.SaveAsync(ws2Task);

        // 两条记录共存（不同主键）
        Assert.AreEqual(2, store.Count);

        var ws1Fetched = await store.GetAsync("ws-1", "task-shared");
        Assert.IsNotNull(ws1Fetched);
        Assert.AreEqual("Running", ws1Fetched!.Status);
        Assert.AreEqual("ws-1", ws1Fetched.Session.WorkspaceId);

        var ws2Fetched = await store.GetAsync("ws-2", "task-shared");
        Assert.IsNotNull(ws2Fetched);
        Assert.AreEqual("Completed", ws2Fetched!.Status);
        Assert.AreEqual("ws-2", ws2Fetched.Session.WorkspaceId);
    }

    [TestMethod]
    public async Task CrossWorkspace_GetAsync_UnknownWorkspace_ReturnsNull()
    {
        // ws-1 的 task，ws-2 看不到
        var store = new InMemoryAgentTaskStateStore();
        await store.SaveAsync(MakeTask("task-1", workspaceId: "ws-1"));

        var fetched = await store.GetAsync("ws-2", "task-1");
        Assert.IsNull(fetched, "跨 workspace 读取应返回 null");
    }

    [TestMethod]
    public async Task CrossWorkspace_DeleteAsync_DoesNotAffectOtherWorkspace()
    {
        // 删除 ws-1 的 task 不影响 ws-2 的同 ID task
        var store = new InMemoryAgentTaskStateStore();
        await store.SaveAsync(MakeTask("task-shared", sessionValue: "session-ws1", workspaceId: "ws-1"));
        await store.SaveAsync(MakeTask("task-shared", sessionValue: "session-ws2", workspaceId: "ws-2"));

        var deleted = await store.DeleteAsync("ws-1", "task-shared");
        Assert.IsTrue(deleted);

        Assert.AreEqual(1, store.Count);
        var ws2Fetched = await store.GetAsync("ws-2", "task-shared");
        Assert.IsNotNull(ws2Fetched);
        Assert.AreEqual("ws-2", ws2Fetched!.Session.WorkspaceId);
    }

    [TestMethod]
    public async Task CrossWorkspace_DeleteAsync_UnknownWorkspace_ReturnsFalse()
    {
        // 尝试用 ws-2 删除 ws-1 的 task 应返回 false
        var store = new InMemoryAgentTaskStateStore();
        await store.SaveAsync(MakeTask("task-1", workspaceId: "ws-1"));

        var deleted = await store.DeleteAsync("ws-2", "task-1");
        Assert.IsFalse(deleted, "跨 workspace 删除应返回 false");

        Assert.AreEqual(1, store.Count);
        var ws1Fetched = await store.GetAsync("ws-1", "task-1");
        Assert.IsNotNull(ws1Fetched);
    }

    [TestMethod]
    public async Task CrossWorkspace_DuplicateIdAcrossWorkspaces_RemainsIsolated()
    {
        // 边界场景 — 两个 workspace 保存相同 ID，应共存而不互相覆盖
        var store = new InMemoryAgentTaskStateStore();
        var ws1Task = MakeTask("task-dup", sessionValue: "session-1", status: "ws1", workspaceId: "ws-1");
        var ws2Task = MakeTask("task-dup", sessionValue: "session-2", status: "ws2", workspaceId: "ws-2");

        await store.SaveAsync(ws1Task);
        await store.SaveAsync(ws2Task);

        Assert.AreEqual(2, store.Count);
        Assert.AreEqual("ws1", (await store.GetAsync("ws-1", "task-dup"))!.Status);
        Assert.AreEqual("ws2", (await store.GetAsync("ws-2", "task-dup"))!.Status);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static AgentSessionId MakeSessionId(string value, string workspaceId = "ws-1") => new()
    {
        Value = value,
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = workspaceId,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentTaskState MakeTask(
        string taskId,
        string sessionValue = "session-1",
        string status = "Pending",
        string description = "",
        DateTimeOffset? updatedAt = null,
        string workspaceId = "ws-1") => new()
        {
            TaskId = taskId,
            Session = MakeSessionId(sessionValue, workspaceId),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
            Status = status,
            Description = description
        };
}
