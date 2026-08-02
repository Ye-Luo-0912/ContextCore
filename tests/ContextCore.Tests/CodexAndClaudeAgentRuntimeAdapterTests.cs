using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// CodexAgentRuntimeAdapter + ClaudeCodeAgentRuntimeAdapter 测试。
///
/// 覆盖：
///   1. RuntimeId / RuntimeKind 正确
///   2. CreateSessionAsync 创建的 sessionId.RuntimeKind 匹配 adapter 类型
///   3. CreateSessionAsync 写入 SessionCreated event
///   4. CloseSessionAsync / IsSessionActiveAsync 继承自 base 工作正常
///   5. TryCreateSessionView 返回有效 view
///   6. 与 DefaultAgentWorkspaceContextProvider 集成（Inject + GetContextSnapshot）
///   7. SessionCount 增长
///   8. Adapter 隔离：Codex session 不会出现在 Claude adapter 中
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class CodexAgentRuntimeAdapterTests
{
    // =========================================================================
    // 1. 基本属性
    // =========================================================================

    [TestMethod]
    public void CodexAdapter_RuntimeId_IsCodexV1()
    {
        var adapter = new CodexAgentRuntimeAdapter();
        Assert.AreEqual("codex-v1", adapter.RuntimeId);
        Assert.AreEqual(AgentRuntimeKind.Codex, adapter.RuntimeKind);
    }

    [TestMethod]
    public void ClaudeAdapter_RuntimeId_IsClaudeCodeV1()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();
        Assert.AreEqual("claude-code-v1", adapter.RuntimeId);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, adapter.RuntimeKind);
    }

    // =========================================================================
    // 2. CreateSessionAsync — RuntimeKind 一致性
    // =========================================================================

    [TestMethod]
    public async Task CodexAdapter_CreateSession_PreservesCodexRuntimeKind()
    {
        var adapter = new CodexAgentRuntimeAdapter();

        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        Assert.AreEqual(AgentRuntimeKind.Codex, sessionId.RuntimeKind);
    }

    [TestMethod]
    public async Task ClaudeAdapter_CreateSession_PreservesClaudeRuntimeKind()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();

        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, sessionId.RuntimeKind);
    }

    // =========================================================================
    // 3. CreateSessionAsync — 写入 SessionCreated event
    // =========================================================================

    [TestMethod]
    public async Task CodexAdapter_CreateSession_WritesSessionCreatedEvent()
    {
        var adapter = new CodexAgentRuntimeAdapter();

        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var record = adapter.GetSessionState(sessionId);
        Assert.IsNotNull(record);
        var evt = record!.Events.FirstOrDefault(e => e.Kind == AgentEventKind.SessionCreated);
        Assert.IsNotNull(evt);
        Assert.AreEqual(AgentRuntimeKind.Codex, evt!.Session.RuntimeKind);
    }

    [TestMethod]
    public async Task ClaudeAdapter_CreateSession_WritesSessionCreatedEvent()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();

        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var record = adapter.GetSessionState(sessionId);
        Assert.IsNotNull(record);
        var evt = record!.Events.FirstOrDefault(e => e.Kind == AgentEventKind.SessionCreated);
        Assert.IsNotNull(evt);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, evt!.Session.RuntimeKind);
    }

    // =========================================================================
    // 4. CloseSessionAsync + IsSessionActiveAsync
    // =========================================================================

    [TestMethod]
    public async Task CodexAdapter_CloseSession_Works()
    {
        var adapter = new CodexAgentRuntimeAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var closed = await adapter.CloseSessionAsync(sessionId);
        var active = await adapter.IsSessionActiveAsync(sessionId);

        Assert.IsTrue(closed);
        Assert.IsFalse(active);
    }

    [TestMethod]
    public async Task ClaudeAdapter_CloseSession_Works()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var closed = await adapter.CloseSessionAsync(sessionId);
        var active = await adapter.IsSessionActiveAsync(sessionId);

        Assert.IsTrue(closed);
        Assert.IsFalse(active);
    }

    // =========================================================================
    // 5. TryCreateSessionView
    // =========================================================================

    [TestMethod]
    public async Task CodexAdapter_TryCreateSessionView_ReturnsView()
    {
        var adapter = new CodexAgentRuntimeAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var view = adapter.TryCreateSessionView(sessionId);

        Assert.IsNotNull(view);
        Assert.AreEqual(sessionId.Value, view!.SessionId.Value);
    }

    [TestMethod]
    public async Task ClaudeAdapter_TryCreateSessionView_ReturnsView()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var view = adapter.TryCreateSessionView(sessionId);

        Assert.IsNotNull(view);
        Assert.AreEqual(sessionId.Value, view!.SessionId.Value);
    }

    // =========================================================================
    // 6. SessionCount
    // =========================================================================

    [TestMethod]
    public async Task CodexAdapter_SessionCount_GrowsWithSessions()
    {
        var adapter = new CodexAgentRuntimeAdapter();
        Assert.AreEqual(0, adapter.SessionCount);

        await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        Assert.AreEqual(1, adapter.SessionCount);

        await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        Assert.AreEqual(2, adapter.SessionCount);
    }

    [TestMethod]
    public async Task ClaudeAdapter_SessionCount_GrowsWithSessions()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();
        Assert.AreEqual(0, adapter.SessionCount);

        await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        Assert.AreEqual(1, adapter.SessionCount);
    }

    // =========================================================================
    // 7. Session 完整生命周期（StartTurn + Inject + RecordTool + GetSnapshot）
    // =========================================================================

    [TestMethod]
    public async Task CodexAdapter_FullSessionLifecycle_Works()
    {
        var adapter = new CodexAgentRuntimeAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await session.StartTurnAsync("turn-1");
        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            FreeText = "codex test",
            InjectedAt = DateTimeOffset.UtcNow
        });
        await provider.IngestToolResultAsync(sessionId, "call-1", "search", "{\"result\":\"ok\"}");
        await session.CompleteTurnAsync("turn-1");

        var snap = await provider.GetContextSnapshotAsync(sessionId, 10000);

        Assert.IsNotNull(snap);
        Assert.AreEqual(AgentRuntimeKind.Codex, snap.Session.RuntimeKind);
        Assert.IsFalse(string.IsNullOrEmpty(snap.ContentJson));

        var events = await session.Events.QueryAsync(new AgentEventQuery { SessionId = sessionId });
        // SessionCreated + TurnStarted + ContextInjected + ToolCallCompleted + TurnCompleted
        Assert.IsTrue(events.Count >= 5);
    }

    [TestMethod]
    public async Task ClaudeAdapter_FullSessionLifecycle_Works()
    {
        var adapter = new ClaudeCodeAgentRuntimeAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await session.StartTurnAsync("turn-1");
        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            FreeText = "claude test",
            InjectedAt = DateTimeOffset.UtcNow
        });
        await session.CompleteTurnAsync("turn-1");

        var snap = await provider.GetContextSnapshotAsync(sessionId, 10000);

        Assert.IsNotNull(snap);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, snap.Session.RuntimeKind);
        Assert.IsFalse(string.IsNullOrEmpty(snap.ContentJson));
    }

    // =========================================================================
    // 8. Adapter 隔离
    // =========================================================================

    [TestMethod]
    public async Task CodexAndClaudeAdapters_AreIsolated()
    {
        var codexAdapter = new CodexAgentRuntimeAdapter();
        var claudeAdapter = new ClaudeCodeAgentRuntimeAdapter();

        var codexSession = await codexAdapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var claudeSession = await claudeAdapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        // Codex session 不在 Claude adapter 中
        Assert.IsNull(claudeAdapter.GetSessionState(codexSession));
        // Claude session 不在 Codex adapter 中
        Assert.IsNull(codexAdapter.GetSessionState(claudeSession));

        // 各自的 SessionCount 独立
        Assert.AreEqual(1, codexAdapter.SessionCount);
        Assert.AreEqual(1, claudeAdapter.SessionCount);
    }

    // =========================================================================
    // 9. AgentRuntimeBase 抽象基类不能直接实例化
    // =========================================================================

    [TestMethod]
    public void AgentRuntimeBase_CannotBeInstantiatedDirectly()
    {
        // AgentRuntimeBase 是 abstract，不能 new
        var type = typeof(AgentRuntimeBase);
        Assert.IsTrue(type.IsAbstract);
    }

    // =========================================================================
    // 10. Three Adapters 共享相同 base 但有不同 RuntimeKind
    // =========================================================================

    [TestMethod]
    public void ThreeAdapters_HaveDistinctRuntimeKinds()
    {
        var generic = new GenericToolAgentAdapter();
        var codex = new CodexAgentRuntimeAdapter();
        var claude = new ClaudeCodeAgentRuntimeAdapter();

        Assert.AreEqual(AgentRuntimeKind.GenericTool, generic.RuntimeKind);
        Assert.AreEqual(AgentRuntimeKind.Codex, codex.RuntimeKind);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, claude.RuntimeKind);

        Assert.AreNotEqual(generic.RuntimeId, codex.RuntimeId);
        Assert.AreNotEqual(generic.RuntimeId, claude.RuntimeId);
        Assert.AreNotEqual(codex.RuntimeId, claude.RuntimeId);
    }
}
