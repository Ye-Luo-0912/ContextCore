using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// R23-3：DefaultAgentWorkspaceContextProvider 实现测试。
///
/// 覆盖：
///   1. 构造函数 null adapter 抛异常
///   2. GetContextSnapshotAsync null session / tokenBudget <= 0 / 不存在 session 抛异常
///   3. GetContextSnapshotAsync 空状态 → 空 snapshot（仍有序号 + SchemaVersion）
///   4. GetContextSnapshotAsync 有 injection → snapshot 包含 injections section
///   5. GetContextSnapshotAsync 有 tool result → snapshot 包含 tool-results section
///   6. GetContextSnapshotAsync token 截断
///   7. GetContextSnapshotAsync 返回 SnapshotRef（ContentJson 非空 + Metadata）
///   8. GetContextSnapshotAsync 保存到 session state.Snapshots + GetLastSnapshot 缓存
///   9. InjectAsync null / 不存在 / closed session 抛异常
///  10. InjectAsync 保存到 Injections + 写入 ContextInjected event
///  11. IngestToolResultAsync null / 不存在 / closed session 抛异常
///  12. IngestToolResultAsync 保存到 ToolResults + 写入 ToolCallCompleted event
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class DefaultAgentWorkspaceContextProviderTests
{
    // =========================================================================
    // 1. 构造函数
    // =========================================================================

    [TestMethod]
    public void Constructor_NullAdapter_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => new DefaultAgentWorkspaceContextProvider(null!));
    }

    // =========================================================================
    // 2. GetContextSnapshotAsync — 错误路径
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_NullSession_Throws()
    {
        var provider = MakeProvider();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.GetContextSnapshotAsync(null!, 1000));
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_ZeroTokenBudget_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => provider.GetContextSnapshotAsync(sessionId, 0));
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_NegativeTokenBudget_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => provider.GetContextSnapshotAsync(sessionId, -1));
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_UnknownSession_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var unknown = new AgentSessionId
        {
            Value = "session-unknown",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.GetContextSnapshotAsync(unknown, 1000));
    }

    // =========================================================================
    // 3. GetContextSnapshotAsync — 空状态
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_EmptyState_ReturnsEmptySnapshot()
    {
        var (adapter, provider, sessionId) = await MakeSession();

        var snap = await provider.GetContextSnapshotAsync(sessionId, 1000);

        Assert.IsNotNull(snap);
        Assert.AreEqual(1000, snap.TokenBudget);
        Assert.AreEqual(0, snap.ActualTokens); // 空 sections → 0 tokens
        Assert.IsFalse(string.IsNullOrEmpty(snap.ContentJson));
        Assert.IsFalse(string.IsNullOrEmpty(snap.SnapshotId));
        Assert.AreEqual("0", snap.Metadata["sectionCount"]);
    }

    // =========================================================================
    // 4. GetContextSnapshotAsync — 有 injection
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_WithInjection_ReturnsInjectionsSection()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            DecisionRequestIds = new[] { "dec-1", "dec-2" },
            ConstraintIds = new[] { "con-1" },
            FreeText = "user preferences",
            InjectedAt = DateTimeOffset.UtcNow
        });

        var snap = await provider.GetContextSnapshotAsync(sessionId, 100000);

        var snapshot = DeserializeSnapshot(snap.ContentJson);
        Assert.AreEqual(1, snapshot.Sections.Count);
        Assert.AreEqual("injections", snapshot.Sections[0].SectionName);
        CollectionAssert.AreEqual(new[] { "dec-1", "dec-2" }, snapshot.DecisionRequestIds.ToList());
        CollectionAssert.AreEqual(new[] { "con-1" }, snapshot.ConstraintIds.ToList());
    }

    // =========================================================================
    // 5. GetContextSnapshotAsync — 有 tool result
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_WithToolResult_ReturnsToolResultsSection()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await provider.IngestToolResultAsync(sessionId, "call-1", "search", "{\"hits\":3}");

        var snap = await provider.GetContextSnapshotAsync(sessionId, 100000);

        var snapshot = DeserializeSnapshot(snap.ContentJson);
        Assert.AreEqual(1, snapshot.Sections.Count);
        Assert.AreEqual("tool-results", snapshot.Sections[0].SectionName);
        Assert.AreEqual("search", snapshot.ToolCallRefs["call-1"]);
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_WithBothInjectionAndToolResult_ReturnsBothSections()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            FreeText = "test",
            InjectedAt = DateTimeOffset.UtcNow
        });
        await provider.IngestToolResultAsync(sessionId, "call-1", "search", "{}");

        var snap = await provider.GetContextSnapshotAsync(sessionId, 100000);

        var snapshot = DeserializeSnapshot(snap.ContentJson);
        Assert.AreEqual(2, snapshot.Sections.Count);
        Assert.AreEqual("injections", snapshot.Sections[0].SectionName);
        Assert.AreEqual("tool-results", snapshot.Sections[1].SectionName);
    }

    // =========================================================================
    // 6. GetContextSnapshotAsync — token 截断
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_TokenBudget_TriggersTruncation()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        // 注入大文本 — 至少 1000 字符 → 250 tokens
        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            FreeText = new string('a', 1000),
            InjectedAt = DateTimeOffset.UtcNow
        });

        // 请求 100 tokens（约 400 字符）
        var snap = await provider.GetContextSnapshotAsync(sessionId, 100);

        Assert.AreEqual(100, snap.TokenBudget);
        Assert.IsTrue(snap.ActualTokens <= 100);
        var snapshot = DeserializeSnapshot(snap.ContentJson);
        Assert.AreEqual(1, snapshot.Sections.Count);
        var section = snapshot.Sections[0];
        Assert.IsTrue(section.ActualTokens <= 100);
        Assert.IsTrue(section.Content.Length <= 100 * DefaultAgentWorkspaceContextProvider.CharsPerToken);
    }

    // =========================================================================
    // 7. GetContextSnapshotAsync — 返回 SnapshotRef 字段
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_ReturnsValidSnapshotRef()
    {
        var (adapter, provider, sessionId) = await MakeSession();

        var snap = await provider.GetContextSnapshotAsync(sessionId, 1000);

        Assert.IsFalse(string.IsNullOrEmpty(snap.SnapshotId));
        Assert.AreEqual(sessionId.Value, snap.Session.Value);
        Assert.AreEqual(1000, snap.TokenBudget);
        Assert.IsFalse(string.IsNullOrEmpty(snap.ContentJson));
        Assert.IsTrue(snap.Metadata.ContainsKey("schemaVersion"));
        Assert.IsTrue(snap.Metadata.ContainsKey("sectionCount"));
    }

    // =========================================================================
    // 8. GetContextSnapshotAsync — 保存到 session state + GetLastSnapshot 缓存
    // =========================================================================

    [TestMethod]
    public async Task GetContextSnapshotAsync_PersistsSnapshotToSessionState()
    {
        var (adapter, provider, sessionId) = await MakeSession();

        await provider.GetContextSnapshotAsync(sessionId, 1000);

        var record = adapter.GetSessionState(sessionId)!;
        Assert.AreEqual(1, record.Snapshots.Count);
    }

    [TestMethod]
    public async Task GetContextSnapshotAsync_CachesLastSnapshot()
    {
        var (adapter, provider, sessionId) = await MakeSession();

        var snap = await provider.GetContextSnapshotAsync(sessionId, 1000);
        var cached = provider.GetLastSnapshot(sessionId);

        Assert.IsNotNull(cached);
        Assert.AreEqual(snap.SnapshotId, cached!.SnapshotId);
    }

    // =========================================================================
    // 9. InjectAsync — 错误路径
    // =========================================================================

    [TestMethod]
    public async Task InjectAsync_NullSession_Throws()
    {
        var provider = MakeProvider();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.InjectAsync(null!, MakeInjection()));
    }

    [TestMethod]
    public async Task InjectAsync_NullInjection_Throws()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.InjectAsync(sessionId, null!));
    }

    [TestMethod]
    public async Task InjectAsync_UnknownSession_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var unknown = new AgentSessionId
        {
            Value = "session-unknown",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.InjectAsync(unknown, MakeInjection()));
    }

    [TestMethod]
    public async Task InjectAsync_OnClosedSession_Throws()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await adapter.CloseSessionAsync(sessionId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.InjectAsync(sessionId, MakeInjection()));
    }

    // =========================================================================
    // 10. InjectAsync — 成功路径
    // =========================================================================

    [TestMethod]
    public async Task InjectAsync_PersistsInjectionAndWritesEvent()
    {
        var (adapter, provider, sessionId) = await MakeSession();

        await provider.InjectAsync(sessionId, new AgentContextInjection
        {
            InjectionId = "inj-1",
            DecisionRequestIds = new[] { "dec-1" },
            ConstraintIds = new[] { "con-1" },
            FreeText = "pref",
            InjectedAt = DateTimeOffset.UtcNow
        });

        var record = adapter.GetSessionState(sessionId)!;
        Assert.AreEqual(1, record.Injections.Count);
        Assert.AreEqual("inj-1", record.Injections[0].InjectionId);

        var evt = record.Events.FirstOrDefault(e => e.Kind == AgentEventKind.ContextInjected);
        Assert.IsNotNull(evt);
        Assert.AreEqual("inj-1", evt!.Metadata["injectionId"]);
    }

    // =========================================================================
    // 11. IngestToolResultAsync — 错误路径
    // =========================================================================

    [TestMethod]
    public async Task IngestToolResultAsync_NullSession_Throws()
    {
        var provider = MakeProvider();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.IngestToolResultAsync(null!, "call-1", "tool", "{}"));
    }

    [TestMethod]
    public async Task IngestToolResultAsync_EmptyToolCallId_Throws()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => provider.IngestToolResultAsync(sessionId, "", "tool", "{}"));
    }

    [TestMethod]
    public async Task IngestToolResultAsync_EmptyToolName_Throws()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => provider.IngestToolResultAsync(sessionId, "call-1", "", "{}"));
    }

    [TestMethod]
    public async Task IngestToolResultAsync_NullResultJson_Throws()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => provider.IngestToolResultAsync(sessionId, "call-1", "tool", null!));
    }

    [TestMethod]
    public async Task IngestToolResultAsync_UnknownSession_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var unknown = new AgentSessionId
        {
            Value = "session-unknown",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.IngestToolResultAsync(unknown, "call-1", "tool", "{}"));
    }

    [TestMethod]
    public async Task IngestToolResultAsync_OnClosedSession_Throws()
    {
        var (adapter, provider, sessionId) = await MakeSession();
        await adapter.CloseSessionAsync(sessionId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => provider.IngestToolResultAsync(sessionId, "call-1", "tool", "{}"));
    }

    // =========================================================================
    // 12. IngestToolResultAsync — 成功路径
    // =========================================================================

    [TestMethod]
    public async Task IngestToolResultAsync_PersistsToolResultAndWritesEvent()
    {
        var (adapter, provider, sessionId) = await MakeSession();

        await provider.IngestToolResultAsync(sessionId, "call-1", "search", "{\"hits\":3}");

        var record = adapter.GetSessionState(sessionId)!;
        Assert.AreEqual(1, record.ToolResults.Count);
        Assert.AreEqual("call-1", record.ToolResults[0].ToolCallId);
        Assert.AreEqual("search", record.ToolResults[0].ToolName);
        Assert.AreEqual("{\"hits\":3}", record.ToolResults[0].ResultJson);

        var evt = record.Events.FirstOrDefault(e => e.Kind == AgentEventKind.ToolCallCompleted);
        Assert.IsNotNull(evt);
        Assert.AreEqual("call-1", evt!.Metadata["toolCallId"]);
        Assert.AreEqual("{\"hits\":3}", evt.PayloadJson);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static DefaultAgentWorkspaceContextProvider MakeProvider()
    {
        var adapter = new GenericToolAgentAdapter();
        return new DefaultAgentWorkspaceContextProvider(adapter);
    }

    private static async Task<(GenericToolAgentAdapter adapter, DefaultAgentWorkspaceContextProvider provider, AgentSessionId sessionId)> MakeSession()
    {
        var adapter = new GenericToolAgentAdapter();
        var provider = new DefaultAgentWorkspaceContextProvider(adapter);
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        return (adapter, provider, sessionId);
    }

    private static AgentContextInjection MakeInjection() => new()
    {
        InjectionId = $"inj-{Guid.NewGuid():N}",
        InjectedAt = DateTimeOffset.UtcNow
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static AgentContextSnapshot DeserializeSnapshot(string json)
    {
        return JsonSerializer.Deserialize<AgentContextSnapshot>(json, DeserializeOptions)
            ?? throw new InvalidOperationException("Failed to deserialize snapshot");
    }
}
