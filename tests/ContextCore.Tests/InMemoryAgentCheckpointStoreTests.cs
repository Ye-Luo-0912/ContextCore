using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// InMemoryAgentCheckpointStore 实现测试。
///
/// 覆盖：
/// 1. SaveAsync null / GetAsync null / ListAsync null 抛异常
/// 2. Save + Get 往返
/// 3. GetAsync 不存在返回 null
/// 4. ListAsync 按 session 过滤 + 按 CreatedAt 倒序
/// 5. ListAsync take 限制 + take<0 抛异常
/// 6. DeleteAsync 存在/不存在
/// 7. Count 属性
/// 8. 跨 workspace 隔离（相同 checkpointId 在不同 workspace 互不可见）
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class InMemoryAgentCheckpointStoreTests
{
    // =========================================================================
    // 1. SaveAsync
    // =========================================================================

    [TestMethod]
    public async Task SaveAsync_NullCheckpoint_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.SaveAsync(null!));
    }

    [TestMethod]
    public async Task SaveAsync_NewCheckpoint_IncrementsCount()
    {
        var store = new InMemoryAgentCheckpointStore();
        var cp = MakeCheckpoint("ckpt-1", "session-1");

        await store.SaveAsync(cp);

        Assert.AreEqual(1, store.Count);
    }

    [TestMethod]
    public async Task SaveAsync_SameWorkspaceAndId_Overwrites()
    {
        // 主键 (workspace_id, checkpoint_id) — 同 workspace 同 id 覆盖
        var store = new InMemoryAgentCheckpointStore();
        var cp1 = MakeCheckpoint("ckpt-1", "session-1", stateJson: "v1");
        var cp2 = MakeCheckpoint("ckpt-1", "session-1", stateJson: "v2");

        await store.SaveAsync(cp1);
        await store.SaveAsync(cp2);

        Assert.AreEqual(1, store.Count);
        var fetched = await store.GetAsync("ws-1", "ckpt-1");
        Assert.AreEqual("v2", fetched!.StateJson);
    }

    // =========================================================================
    // 2. GetAsync
    // =========================================================================

    [TestMethod]
    public async Task GetAsync_NullWorkspaceId_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("", "ckpt-1"));
    }

    [TestMethod]
    public async Task GetAsync_NullCheckpointId_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryAgentCheckpointStore();
        var cp = await store.GetAsync("ws-1", "nonexistent");
        Assert.IsNull(cp);
    }

    [TestMethod]
    public async Task GetAsync_AfterSave_ReturnsCheckpoint()
    {
        var store = new InMemoryAgentCheckpointStore();
        var cp = MakeCheckpoint("ckpt-1", "session-1", stateJson: "{\"x\":1}");
        await store.SaveAsync(cp);

        var fetched = await store.GetAsync("ws-1", "ckpt-1");

        Assert.IsNotNull(fetched);
        Assert.AreEqual("ckpt-1", fetched!.CheckpointId);
        Assert.AreEqual("{\"x\":1}", fetched.StateJson);
        Assert.AreEqual("session-1", fetched.Session.Value);
    }

    // =========================================================================
    // 3. ListAsync
    // =========================================================================

    [TestMethod]
    public async Task ListAsync_NullSession_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => store.ListAsync(null!));
    }

    [TestMethod]
    public async Task ListAsync_NegativeTake_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        var sessionId = MakeSessionId("session-1");
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => store.ListAsync(sessionId, -1));
    }

    [TestMethod]
    public async Task ListAsync_FiltersBySession()
    {
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-1", "session-1"));
        await store.SaveAsync(MakeCheckpoint("ckpt-2", "session-1"));
        await store.SaveAsync(MakeCheckpoint("ckpt-3", "session-2"));

        var result = await store.ListAsync(MakeSessionId("session-1"));

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(c => c.Session.Value == "session-1"));
    }

    [TestMethod]
    public async Task ListAsync_OrdersByCreatedAtDescending()
    {
        var store = new InMemoryAgentCheckpointStore();
        var t1 = DateTimeOffset.UtcNow;
        var t2 = t1.AddSeconds(1);
        var t3 = t1.AddSeconds(2);

        await store.SaveAsync(MakeCheckpoint("ckpt-old", "session-1", createdAt: t1));
        await store.SaveAsync(MakeCheckpoint("ckpt-new", "session-1", createdAt: t3));
        await store.SaveAsync(MakeCheckpoint("ckpt-mid", "session-1", createdAt: t2));

        var result = await store.ListAsync(MakeSessionId("session-1"));

        Assert.AreEqual("ckpt-new", result[0].CheckpointId);
        Assert.AreEqual("ckpt-mid", result[1].CheckpointId);
        Assert.AreEqual("ckpt-old", result[2].CheckpointId);
    }

    [TestMethod]
    public async Task ListAsync_TakeLimitsResults()
    {
        var store = new InMemoryAgentCheckpointStore();
        for (var i = 0; i < 5; i++)
        {
            await store.SaveAsync(MakeCheckpoint($"ckpt-{i}", "session-1", createdAt: DateTimeOffset.UtcNow.AddSeconds(i)));
        }

        var result = await store.ListAsync(MakeSessionId("session-1"), take: 2);

        Assert.AreEqual(2, result.Count);
    }

    [TestMethod]
    public async Task ListAsync_TakeZero_ReturnsAll()
    {
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-1", "session-1"));
        await store.SaveAsync(MakeCheckpoint("ckpt-2", "session-1"));

        var result = await store.ListAsync(MakeSessionId("session-1"), take: 0);

        Assert.AreEqual(2, result.Count);
    }

    // =========================================================================
    // 4. DeleteAsync
    // =========================================================================

    [TestMethod]
    public async Task DeleteAsync_NullWorkspaceId_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("", "ckpt-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_NullCheckpointId_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync("ws-1", ""));
    }

    [TestMethod]
    public async Task DeleteAsync_Existing_ReturnsTrue()
    {
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-1", "session-1"));

        var result = await store.DeleteAsync("ws-1", "ckpt-1");

        Assert.IsTrue(result);
        Assert.AreEqual(0, store.Count);
        Assert.IsNull(await store.GetAsync("ws-1", "ckpt-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_Nonexistent_ReturnsFalse()
    {
        var store = new InMemoryAgentCheckpointStore();
        var result = await store.DeleteAsync("ws-1", "nonexistent");
        Assert.IsFalse(result);
    }

    // =========================================================================
    // 5. Count
    // =========================================================================

    [TestMethod]
    public void Count_StartsAtZero()
    {
        var store = new InMemoryAgentCheckpointStore();
        Assert.AreEqual(0, store.Count);
    }

    // =========================================================================
    // 6. 跨 workspace 隔离
    // =========================================================================

    [TestMethod]
    public async Task CrossWorkspace_SameCheckpointId_RemainsIsolated()
    {
        // 两个 workspace 各自保存同 ID checkpoint，应互不可见
        var store = new InMemoryAgentCheckpointStore();
        var ws1Cp = MakeCheckpoint("ckpt-shared", "session-ws1", stateJson: "ws1-state", workspaceId: "ws-1");
        var ws2Cp = MakeCheckpoint("ckpt-shared", "session-ws2", stateJson: "ws2-state", workspaceId: "ws-2");

        await store.SaveAsync(ws1Cp);
        await store.SaveAsync(ws2Cp);

        // 两条记录共存（不同主键）
        Assert.AreEqual(2, store.Count);

        // ws-1 看到自己的 state
        var ws1Fetched = await store.GetAsync("ws-1", "ckpt-shared");
        Assert.IsNotNull(ws1Fetched);
        Assert.AreEqual("ws1-state", ws1Fetched!.StateJson);
        Assert.AreEqual("ws-1", ws1Fetched.Session.WorkspaceId);

        // ws-2 看到自己的 state（不会误读 ws-1 的）
        var ws2Fetched = await store.GetAsync("ws-2", "ckpt-shared");
        Assert.IsNotNull(ws2Fetched);
        Assert.AreEqual("ws2-state", ws2Fetched!.StateJson);
        Assert.AreEqual("ws-2", ws2Fetched.Session.WorkspaceId);
    }

    [TestMethod]
    public async Task CrossWorkspace_GetAsync_UnknownWorkspace_ReturnsNull()
    {
        // ws-1 的 checkpoint，ws-2 看不到（不会误读）
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-1", "session-ws1", workspaceId: "ws-1"));

        var fetched = await store.GetAsync("ws-2", "ckpt-1");
        Assert.IsNull(fetched, "跨 workspace 读取应返回 null");
    }

    [TestMethod]
    public async Task CrossWorkspace_DeleteAsync_DoesNotAffectOtherWorkspace()
    {
        // 删除 ws-1 的 checkpoint 不影响 ws-2 的同 ID checkpoint
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-shared", "session-ws1", workspaceId: "ws-1"));
        await store.SaveAsync(MakeCheckpoint("ckpt-shared", "session-ws2", workspaceId: "ws-2"));

        var deleted = await store.DeleteAsync("ws-1", "ckpt-shared");
        Assert.IsTrue(deleted);

        // ws-2 的 checkpoint 应仍存在
        Assert.AreEqual(1, store.Count);
        var ws2Fetched = await store.GetAsync("ws-2", "ckpt-shared");
        Assert.IsNotNull(ws2Fetched);
        Assert.AreEqual("ws-2", ws2Fetched!.Session.WorkspaceId);
    }

    [TestMethod]
    public async Task CrossWorkspace_DeleteAsync_UnknownWorkspace_ReturnsFalse()
    {
        // 尝试用 ws-2 删除 ws-1 的 checkpoint 应返回 false（未删除任何记录）
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-1", "session-ws1", workspaceId: "ws-1"));

        var deleted = await store.DeleteAsync("ws-2", "ckpt-1");
        Assert.IsFalse(deleted, "跨 workspace 删除应返回 false");

        // ws-1 的 checkpoint 仍存在
        Assert.AreEqual(1, store.Count);
        var ws1Fetched = await store.GetAsync("ws-1", "ckpt-1");
        Assert.IsNotNull(ws1Fetched);
    }

    [TestMethod]
    public async Task CrossWorkspace_DuplicateIdAcrossWorkspaces_RemainsIsolated()
    {
        // 边界场景 — 在两个 workspace 中保存相同 ID，应共存而不互相覆盖
        var store = new InMemoryAgentCheckpointStore();
        var ws1Cp = MakeCheckpoint("ckpt-dup", "session-1", stateJson: "ws1", workspaceId: "ws-1");
        var ws2Cp = MakeCheckpoint("ckpt-dup", "session-2", stateJson: "ws2", workspaceId: "ws-2");

        await store.SaveAsync(ws1Cp);
        await store.SaveAsync(ws2Cp);

        // 两条都存在
        Assert.AreEqual(2, store.Count);
        Assert.AreEqual("ws1", (await store.GetAsync("ws-1", "ckpt-dup"))!.StateJson);
        Assert.AreEqual("ws2", (await store.GetAsync("ws-2", "ckpt-dup"))!.StateJson);
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

    private static AgentCheckpoint MakeCheckpoint(
        string id,
        string sessionValue,
        string? stateJson = null,
        DateTimeOffset? createdAt = null,
        string workspaceId = "ws-1") => new()
    {
        CheckpointId = id,
        Session = MakeSessionId(sessionValue, workspaceId),
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        StateJson = stateJson ?? "{}"
    };
}
