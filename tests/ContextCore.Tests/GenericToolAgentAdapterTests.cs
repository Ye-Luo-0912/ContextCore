using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;

namespace ContextCore.Tests;

/// <summary>
/// GenericToolAgentAdapter + GenericToolAgentSession + IAgentEventStream 实现测试。
///
/// 覆盖：
/// 1. Adapter 基本属性 / CreateSessionAsync / CloseSessionAsync / IsSessionActiveAsync
/// 2. Session.StartTurnAsync / CompleteTurnAsync / RecordToolCallResultAsync
/// 3. EventStream.SubscribeAsync（push 模型 + 历史事件 + cancellation）
/// 4. EventStream.QueryAsync（按 Kind / Level / TurnId / Since/Until / Take 过滤）
/// 5. Closed session 写操作抛 InvalidOperationException
/// 6. SessionId mismatch 校验
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class GenericToolAgentAdapterTests
{
    // =========================================================================
    // 1. Adapter 基本属性
    // =========================================================================

    [TestMethod]
    public void Adapter_RuntimeId_IsGenericV1()
    {
        var adapter = new GenericToolAgentAdapter();
        Assert.AreEqual("generic-v1", adapter.RuntimeId);
        Assert.AreEqual(AgentRuntimeKind.GenericTool, adapter.RuntimeKind);
    }

    [TestMethod]
    public void Adapter_SessionCount_StartsAtZero()
    {
        var adapter = new GenericToolAgentAdapter();
        Assert.AreEqual(0, adapter.SessionCount);
    }

    // =========================================================================
    // 2. CreateSessionAsync
    // =========================================================================

    [TestMethod]
    public async Task CreateSessionAsync_NullRequest_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => adapter.CreateSessionAsync(null!));
    }

    [TestMethod]
    public async Task CreateSessionAsync_EmptyWorkspaceId_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var request = new AgentSessionRequest { WorkspaceId = "" };
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => adapter.CreateSessionAsync(request));
    }

    [TestMethod]
    public async Task CreateSessionAsync_Valid_ReturnsSessionId()
    {
        var adapter = new GenericToolAgentAdapter();
        var request = new AgentSessionRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };

        var sessionId = await adapter.CreateSessionAsync(request);

        Assert.IsNotNull(sessionId);
        Assert.IsTrue(sessionId.Value.StartsWith("session-", StringComparison.Ordinal));
        Assert.AreEqual(AgentRuntimeKind.GenericTool, sessionId.RuntimeKind);
        Assert.AreEqual("ws-1", sessionId.WorkspaceId);
        Assert.AreEqual("col-1", sessionId.CollectionId);
        Assert.AreEqual(1, adapter.SessionCount);
    }

    [TestMethod]
    public async Task CreateSessionAsync_WritesSessionCreatedEvent()
    {
        var adapter = new GenericToolAgentAdapter();
        var request = new AgentSessionRequest { WorkspaceId = "ws-1" };

        var sessionId = await adapter.CreateSessionAsync(request);

        var record = adapter.GetSessionState(sessionId);
        Assert.IsNotNull(record);
        var createdEvent = record!.Events.FirstOrDefault(e => e.Kind == AgentEventKind.SessionCreated);
        Assert.IsNotNull(createdEvent);
        Assert.AreEqual(sessionId.Value, createdEvent!.Session.Value);
    }

    [TestMethod]
    public async Task CreateSessionAsync_WithMetadata_PreservedInSession()
    {
        var adapter = new GenericToolAgentAdapter();
        var request = new AgentSessionRequest
        {
            WorkspaceId = "ws-1",
            Metadata = new Dictionary<string, string> { ["key"] = "value" }
        };

        var sessionId = await adapter.CreateSessionAsync(request);

        var record = adapter.GetSessionState(sessionId);
        Assert.IsNotNull(record);
        Assert.AreEqual("value", record!.Metadata["key"]);
    }

    // =========================================================================
    // 3. CloseSessionAsync + IsSessionActiveAsync
    // =========================================================================

    [TestMethod]
    public async Task CloseSessionAsync_NullSessionId_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => adapter.CloseSessionAsync(null!));
    }

    [TestMethod]
    public async Task CloseSessionAsync_UnknownSession_ReturnsFalse()
    {
        var adapter = new GenericToolAgentAdapter();
        var unknownSession = new AgentSessionId
        {
            Value = "session-unknown",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await adapter.CloseSessionAsync(unknownSession);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task CloseSessionAsync_AlreadyClosed_ReturnsFalse()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var first = await adapter.CloseSessionAsync(sessionId);
        var second = await adapter.CloseSessionAsync(sessionId);

        Assert.IsTrue(first);
        Assert.IsFalse(second);
    }

    [TestMethod]
    public async Task CloseSessionAsync_Valid_WritesSessionClosedEvent()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        await adapter.CloseSessionAsync(sessionId);

        var record = adapter.GetSessionState(sessionId);
        Assert.IsNotNull(record);
        Assert.IsTrue(record!.IsClosed);
        Assert.IsNotNull(record.ClosedAt);
        var closedEvent = record.Events.FirstOrDefault(e => e.Kind == AgentEventKind.SessionClosed);
        Assert.IsNotNull(closedEvent);
    }

    [TestMethod]
    public async Task IsSessionActiveAsync_UnknownSession_ReturnsFalse()
    {
        var adapter = new GenericToolAgentAdapter();
        var unknownSession = new AgentSessionId
        {
            Value = "session-unknown",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await adapter.IsSessionActiveAsync(unknownSession);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task IsSessionActiveAsync_ActiveSession_ReturnsTrue()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var result = await adapter.IsSessionActiveAsync(sessionId);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task IsSessionActiveAsync_ClosedSession_ReturnsFalse()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        await adapter.CloseSessionAsync(sessionId);

        var result = await adapter.IsSessionActiveAsync(sessionId);
        Assert.IsFalse(result);
    }

    // =========================================================================
    // 4. TryCreateSessionView
    // =========================================================================

    [TestMethod]
    public async Task TryCreateSessionView_UnknownSession_ReturnsNull()
    {
        var adapter = new GenericToolAgentAdapter();
        var unknownSession = new AgentSessionId
        {
            Value = "session-unknown",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var view = adapter.TryCreateSessionView(unknownSession);
        Assert.IsNull(view);
    }

    [TestMethod]
    public async Task TryCreateSessionView_ValidSession_ReturnsView()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });

        var view = adapter.TryCreateSessionView(sessionId);
        Assert.IsNotNull(view);
        Assert.AreEqual(sessionId.Value, view!.SessionId.Value);
        Assert.IsTrue(ReferenceEquals(view, view.Events)); // IAgentSession.Events 返回同一对象
    }

    // =========================================================================
    // 5. Session: StartTurnAsync
    // =========================================================================

    [TestMethod]
    public async Task StartTurnAsync_NoTurnId_GeneratesTurnId()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        var turnId = await session.StartTurnAsync();

        Assert.IsFalse(string.IsNullOrEmpty(turnId));
        Assert.IsTrue(turnId.StartsWith("turn-", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task StartTurnAsync_WithTurnId_UsesProvided()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        var turnId = await session.StartTurnAsync("custom-turn-1");

        Assert.AreEqual("custom-turn-1", turnId);
    }

    [TestMethod]
    public async Task StartTurnAsync_WritesTurnStartedEvent()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await session.StartTurnAsync("turn-1");

        var record = adapter.GetSessionState(sessionId)!;
        var startedEvent = record.Events.FirstOrDefault(e => e.Kind == AgentEventKind.TurnStarted);
        Assert.IsNotNull(startedEvent);
        Assert.AreEqual("turn-1", startedEvent!.TurnId);
    }

    [TestMethod]
    public async Task StartTurnAsync_OnClosedSession_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await adapter.CloseSessionAsync(sessionId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => session.StartTurnAsync("turn-1"));
    }

    // =========================================================================
    // 6. Session: CompleteTurnAsync
    // =========================================================================

    [TestMethod]
    public async Task CompleteTurnAsync_NullTurnId_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => session.CompleteTurnAsync(""));
    }

    [TestMethod]
    public async Task CompleteTurnAsync_WritesTurnCompletedEvent()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");

        await session.CompleteTurnAsync("turn-1");

        var record = adapter.GetSessionState(sessionId)!;
        var completedEvent = record.Events.FirstOrDefault(e => e.Kind == AgentEventKind.TurnCompleted);
        Assert.IsNotNull(completedEvent);
        Assert.AreEqual("turn-1", completedEvent!.TurnId);
    }

    [TestMethod]
    public async Task CompleteTurnAsync_OnClosedSession_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await adapter.CloseSessionAsync(sessionId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => session.CompleteTurnAsync("turn-1"));
    }

    // =========================================================================
    // 7. Session: RecordToolCallResultAsync
    // =========================================================================

    [TestMethod]
    public async Task RecordToolCallResultAsync_NullToolCallId_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => session.RecordToolCallResultAsync("", "tool", "{}"));
    }

    [TestMethod]
    public async Task RecordToolCallResultAsync_NullToolName_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => session.RecordToolCallResultAsync("call-1", "", "{}"));
    }

    [TestMethod]
    public async Task RecordToolCallResultAsync_NullResultJson_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => session.RecordToolCallResultAsync("call-1", "tool", null!));
    }

    [TestMethod]
    public async Task RecordToolCallResultAsync_Valid_PersistsAndWritesEvent()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await session.RecordToolCallResultAsync("call-1", "search", "{\"result\":\"ok\"}");

        var record = adapter.GetSessionState(sessionId)!;
        Assert.AreEqual(1, record.ToolResults.Count);
        Assert.AreEqual("call-1", record.ToolResults[0].ToolCallId);
        Assert.AreEqual("search", record.ToolResults[0].ToolName);
        Assert.AreEqual("{\"result\":\"ok\"}", record.ToolResults[0].ResultJson);

        var completedEvent = record.Events.FirstOrDefault(e => e.Kind == AgentEventKind.ToolCallCompleted);
        Assert.IsNotNull(completedEvent);
        Assert.AreEqual("{\"result\":\"ok\"}", completedEvent!.PayloadJson);
        Assert.AreEqual("call-1", completedEvent.Metadata["toolCallId"]);
    }

    [TestMethod]
    public async Task RecordToolCallResultAsync_OnClosedSession_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await adapter.CloseSessionAsync(sessionId);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => session.RecordToolCallResultAsync("call-1", "tool", "{}"));
    }

    // =========================================================================
    // 8. EventStream: QueryAsync
    // =========================================================================

    [TestMethod]
    public async Task QueryAsync_NullQuery_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => session.Events.QueryAsync(null!));
    }

    [TestMethod]
    public async Task QueryAsync_NoFilter_ReturnsAllEvents()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");
        await session.CompleteTurnAsync("turn-1");

        var result = await session.Events.QueryAsync(new AgentEventQuery { SessionId = sessionId });

        // 至少 3 个事件：SessionCreated + TurnStarted + TurnCompleted
        Assert.IsTrue(result.Count >= 3);
    }

    [TestMethod]
    public async Task QueryAsync_FilterByKind_ReturnsMatchingEvents()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");

        var result = await session.Events.QueryAsync(new AgentEventQuery
        {
            SessionId = sessionId,
            Kind = AgentEventKind.TurnStarted
        });

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(AgentEventKind.TurnStarted, result[0].Kind);
    }

    [TestMethod]
    public async Task QueryAsync_FilterByTurnId_ReturnsMatchingEvents()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");
        await session.StartTurnAsync("turn-2");

        var result = await session.Events.QueryAsync(new AgentEventQuery
        {
            SessionId = sessionId,
            TurnId = "turn-1"
        });

        Assert.IsTrue(result.All(e => e.TurnId == "turn-1"));
        Assert.IsTrue(result.Count >= 1);
    }

    [TestMethod]
    public async Task QueryAsync_FilterByLevel_ReturnsMatchingEvents()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        var result = await session.Events.QueryAsync(new AgentEventQuery
        {
            SessionId = sessionId,
            Level = AgentEventLevel.Information
        });

        Assert.IsTrue(result.All(e => e.Level == AgentEventLevel.Information));
    }

    [TestMethod]
    public async Task QueryAsync_FilterBySince_ReturnsEventsAfterTimestamp()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var result = await session.Events.QueryAsync(new AgentEventQuery
        {
            SessionId = sessionId,
            Since = since
        });

        Assert.IsTrue(result.All(e => e.OccurredAt >= since));
    }

    [TestMethod]
    public async Task QueryAsync_TakeLimitsResults()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");
        await session.CompleteTurnAsync("turn-1");

        var result = await session.Events.QueryAsync(new AgentEventQuery
        {
            SessionId = sessionId,
            Take = 1
        });

        Assert.AreEqual(1, result.Count);
    }

    [TestMethod]
    public async Task QueryAsync_TakeZero_ReturnsAllEvents()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");

        var result = await session.Events.QueryAsync(new AgentEventQuery
        {
            SessionId = sessionId,
            Take = 0
        });

        Assert.IsTrue(result.Count >= 2); // SessionCreated + TurnStarted
    }

    // =========================================================================
    // 9. EventStream: SubscribeAsync
    // =========================================================================

    [TestMethod]
    public async Task SubscribeAsync_SessionIdMismatch_Throws()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        var otherSession = new AgentSessionId
        {
            Value = "session-other",
            RuntimeKind = AgentRuntimeKind.GenericTool,
            WorkspaceId = "ws-1",
            CreatedAt = DateTimeOffset.UtcNow
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => ConsumeAllAsync(session.Events.SubscribeAsync(otherSession), TimeSpan.FromMilliseconds(100)));
    }

    [TestMethod]
    public async Task SubscribeAsync_PushesHistoricalEvents()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;
        await session.StartTurnAsync("turn-1");

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var events = new List<AgentEvent>();
        await foreach (var evt in session.Events.SubscribeAsync(sessionId, cts.Token))
        {
            events.Add(evt);
            if (events.Count >= 2) // SessionCreated + TurnStarted
            {
                cts.Cancel();
                break;
            }
        }

        Assert.IsTrue(events.Count >= 2);
        Assert.IsTrue(events.Any(e => e.Kind == AgentEventKind.SessionCreated));
        Assert.IsTrue(events.Any(e => e.Kind == AgentEventKind.TurnStarted));
    }

    [TestMethod]
    public async Task SubscribeAsync_PushesNewEventsAfterSubscribe()
    {
        var adapter = new GenericToolAgentAdapter();
        var sessionId = await adapter.CreateSessionAsync(new AgentSessionRequest { WorkspaceId = "ws-1" });
        var session = adapter.TryCreateSessionView(sessionId)!;

        var cts = new CancellationTokenSource();
        var consumeTask = Task.Run(async () =>
        {
            var events = new List<AgentEvent>();
            await foreach (var evt in session.Events.SubscribeAsync(sessionId, cts.Token))
            {
                events.Add(evt);
                if (evt.Kind == AgentEventKind.TurnCompleted)
                {
                    return events;
                }
            }
            return events;
        });

        // 等订阅建立（短暂延迟）
        await Task.Delay(100);

        // 触发新事件（在订阅后写入）
        await session.StartTurnAsync("turn-1");
        await session.CompleteTurnAsync("turn-1");

        var result = await consumeTask;
        Assert.IsTrue(result.Any(e => e.Kind == AgentEventKind.TurnStarted));
        Assert.IsTrue(result.Any(e => e.Kind == AgentEventKind.TurnCompleted));
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static async Task<List<AgentEvent>> ConsumeAllAsync(
        IAsyncEnumerable<AgentEvent> source,
        TimeSpan timeout)
    {
        var cts = new CancellationTokenSource(timeout);
        var events = new List<AgentEvent>();
        try
        {
            await foreach (var evt in source.WithCancellation(cts.Token))
            {
                events.Add(evt);
            }
        }
        catch (OperationCanceledException)
        {
            // 超时正常退出
        }
        return events;
    }
}
