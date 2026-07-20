using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// R23-3：InMemoryAgentCheckpointStore 实现测试。
///
/// 覆盖：
///   1. SaveAsync null / GetAsync null / ListAsync null 抛异常
///   2. Save + Get 往返
///   3. GetAsync 不存在返回 null
///   4. ListAsync 按 session 过滤 + 按 CreatedAt 倒序
///   5. ListAsync take 限制 + take<0 抛异常
///   6. DeleteAsync 存在/不存在
///   7. Count 属性
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
    public async Task SaveAsync_SameId_Overwrites()
    {
        var store = new InMemoryAgentCheckpointStore();
        var cp1 = MakeCheckpoint("ckpt-1", "session-1", stateJson: "v1");
        var cp2 = MakeCheckpoint("ckpt-1", "session-1", stateJson: "v2");

        await store.SaveAsync(cp1);
        await store.SaveAsync(cp2);

        Assert.AreEqual(1, store.Count);
        var fetched = await store.GetAsync("ckpt-1");
        Assert.AreEqual("v2", fetched!.StateJson);
    }

    // =========================================================================
    // 2. GetAsync
    // =========================================================================

    [TestMethod]
    public async Task GetAsync_NullId_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.GetAsync(""));
    }

    [TestMethod]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var store = new InMemoryAgentCheckpointStore();
        var cp = await store.GetAsync("nonexistent");
        Assert.IsNull(cp);
    }

    [TestMethod]
    public async Task GetAsync_AfterSave_ReturnsCheckpoint()
    {
        var store = new InMemoryAgentCheckpointStore();
        var cp = MakeCheckpoint("ckpt-1", "session-1", stateJson: "{\"x\":1}");
        await store.SaveAsync(cp);

        var fetched = await store.GetAsync("ckpt-1");

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
    public async Task DeleteAsync_NullId_Throws()
    {
        var store = new InMemoryAgentCheckpointStore();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => store.DeleteAsync(""));
    }

    [TestMethod]
    public async Task DeleteAsync_Existing_ReturnsTrue()
    {
        var store = new InMemoryAgentCheckpointStore();
        await store.SaveAsync(MakeCheckpoint("ckpt-1", "session-1"));

        var result = await store.DeleteAsync("ckpt-1");

        Assert.IsTrue(result);
        Assert.AreEqual(0, store.Count);
        Assert.IsNull(await store.GetAsync("ckpt-1"));
    }

    [TestMethod]
    public async Task DeleteAsync_Nonexistent_ReturnsFalse()
    {
        var store = new InMemoryAgentCheckpointStore();
        var result = await store.DeleteAsync("nonexistent");
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
    // 辅助方法
    // =========================================================================

    private static AgentSessionId MakeSessionId(string value) => new()
    {
        Value = value,
        RuntimeKind = AgentRuntimeKind.GenericTool,
        WorkspaceId = "ws-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AgentCheckpoint MakeCheckpoint(
        string id,
        string sessionValue,
        string? stateJson = null,
        DateTimeOffset? createdAt = null) => new()
    {
        CheckpointId = id,
        Session = MakeSessionId(sessionValue),
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        StateJson = stateJson ?? "{}"
    };
}
