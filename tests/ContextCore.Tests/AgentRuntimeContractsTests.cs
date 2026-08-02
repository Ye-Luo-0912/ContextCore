using System.Reflection;
using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// Agent Runtime Integration 契约测试。
///
/// 验证目标：
///   1. AgentRuntimeKind 枚举（5 值，byte 底层，Unknown=0）
///   2. AgentEventKind 枚举（14 值，byte 底层，Unknown=0）
///   3. AgentEventLevel 枚举（4 值，byte 底层，Trace=0）
///   4. AgentSessionId record 必填字段 + with 表达式
///   5. AgentSessionRequest record 必填字段 + 默认值
///   6. AgentEvent record 必填字段 + 默认值
///   7. AgentEventQuery record 必填字段 + 默认值
///   8. IAgentRuntime 接口最小化（3 方法 + StoreOperation 属性）
///   9. IAgentSession 接口最小化（4 成员）
///  10. IAgentEventStream 接口最小化（2 方法）
///  11. IAgentWorkspaceContextProvider 接口最小化（3 方法）
///  12. IAgentCheckpointStore 接口最小化（4 方法）
///  13. AgentContextSnapshotRef record 必填字段 + 默认值
///  14. AgentContextInjection record 必填字段 + 默认值
///  15. AgentCheckpoint record 必填字段 + 默认值
///  16. sealed record / interface / no async void 反射验证
///  17. 不依赖具体 Agent SDK 对象模型（无 SDK 类型引用）
/// </summary>
[TestClass]
[TestCategory("R23")]
public sealed class AgentRuntimeContractsTests
{
    // =========================================================================
    // 1. AgentRuntimeKind 枚举
    // =========================================================================

    [TestMethod]
    public void AgentRuntimeKind_Has5Values()
    {
        var values = Enum.GetValues<AgentRuntimeKind>();
        Assert.AreEqual(5, values.Length);
        Assert.IsTrue(values.Contains(AgentRuntimeKind.Unknown));
        Assert.IsTrue(values.Contains(AgentRuntimeKind.GenericTool));
        Assert.IsTrue(values.Contains(AgentRuntimeKind.Codex));
        Assert.IsTrue(values.Contains(AgentRuntimeKind.ClaudeCode));
        Assert.IsTrue(values.Contains(AgentRuntimeKind.Custom));
    }

    [TestMethod]
    public void AgentRuntimeKind_BackedByByte()
    {
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(AgentRuntimeKind)));
    }

    [TestMethod]
    public void AgentRuntimeKind_UnknownIsZero()
    {
        Assert.AreEqual((byte)0, (byte)AgentRuntimeKind.Unknown);
    }

    [TestMethod]
    public void AgentRuntimeKind_ValuesAreUnique()
    {
        var values = Enum.GetValues<AgentRuntimeKind>().Select(v => (byte)v).ToList();
        Assert.AreEqual(values.Count, values.Distinct().Count());
    }

    // =========================================================================
    // 2. AgentEventKind 枚举
    // =========================================================================

    [TestMethod]
    public void AgentEventKind_Has14Values()
    {
        var values = Enum.GetValues<AgentEventKind>();
        Assert.AreEqual(14, values.Length);
    }

    [TestMethod]
    public void AgentEventKind_BackedByByte()
    {
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(AgentEventKind)));
    }

    [TestMethod]
    public void AgentEventKind_UnknownIsZero()
    {
        Assert.AreEqual((byte)0, (byte)AgentEventKind.Unknown);
    }

    [TestMethod]
    public void AgentEventKind_KeyValuesDefined()
    {
        // 关键事件类型必须存在
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.SessionCreated));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.SessionClosed));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.TurnStarted));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.TurnCompleted));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.ToolCallStarted));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.ToolCallCompleted));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.ToolCallFailed));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.ContextInjected));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.DecisionPoint));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.CheckpointCreated));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.CheckpointResumed));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.TokenBudgetWarning));
        Assert.IsTrue(Enum.IsDefined(AgentEventKind.TokenBudgetExhausted));
    }

    // =========================================================================
    // 3. AgentEventLevel 枚举
    // =========================================================================

    [TestMethod]
    public void AgentEventLevel_Has4Values()
    {
        var values = Enum.GetValues<AgentEventLevel>();
        Assert.AreEqual(4, values.Length);
        Assert.IsTrue(values.Contains(AgentEventLevel.Trace));
        Assert.IsTrue(values.Contains(AgentEventLevel.Information));
        Assert.IsTrue(values.Contains(AgentEventLevel.Warning));
        Assert.IsTrue(values.Contains(AgentEventLevel.Error));
    }

    [TestMethod]
    public void AgentEventLevel_BackedByByte()
    {
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(AgentEventLevel)));
    }

    [TestMethod]
    public void AgentEventLevel_TraceIsZero()
    {
        Assert.AreEqual((byte)0, (byte)AgentEventLevel.Trace);
    }

    // =========================================================================
    // 4. AgentSessionId record
    // =========================================================================

    [TestMethod]
    public void AgentSessionId_RequiredFields_AreEnforced()
    {
        var sessionId = MakeSessionId();

        Assert.AreEqual("session-1", sessionId.Value);
        Assert.AreEqual(AgentRuntimeKind.GenericTool, sessionId.RuntimeKind);
        Assert.AreEqual("ws-test", sessionId.WorkspaceId);
        Assert.AreEqual("col-test", sessionId.CollectionId);
        Assert.IsTrue(sessionId.CreatedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void AgentSessionId_OptionalCollectionId_DefaultNull()
    {
        var sessionId = new AgentSessionId
        {
            Value = "session-1",
            RuntimeKind = AgentRuntimeKind.Codex,
            WorkspaceId = "ws-test",
            CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.IsNull(sessionId.CollectionId);
    }

    [TestMethod]
    public void AgentSessionId_WithExpression_ProducesNewInstance()
    {
        var original = MakeSessionId();
        var updated = original with { RuntimeKind = AgentRuntimeKind.ClaudeCode };

        Assert.AreEqual(AgentRuntimeKind.GenericTool, original.RuntimeKind);
        Assert.AreEqual(AgentRuntimeKind.ClaudeCode, updated.RuntimeKind);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void AgentSessionId_IsSealedRecord()
    {
        var type = typeof(AgentSessionId);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 5. AgentSessionRequest record
    // =========================================================================

    [TestMethod]
    public void AgentSessionRequest_RequiredFields_AreEnforced()
    {
        var request = new AgentSessionRequest { WorkspaceId = "ws-test" };

        Assert.AreEqual("ws-test", request.WorkspaceId);
    }

    [TestMethod]
    public void AgentSessionRequest_OptionalFields_DefaultNull()
    {
        var request = new AgentSessionRequest { WorkspaceId = "ws-test" };

        Assert.IsNull(request.CollectionId);
        Assert.IsNull(request.InitialTurnId);
        Assert.AreEqual(0, request.Metadata.Count);
    }

    [TestMethod]
    public void AgentSessionRequest_AllFieldsCanBeSet()
    {
        var request = new AgentSessionRequest
        {
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            InitialTurnId = "turn-initial",
            Metadata = new Dictionary<string, string> { ["source"] = "test" }
        };

        Assert.AreEqual("col-test", request.CollectionId);
        Assert.AreEqual("turn-initial", request.InitialTurnId);
        Assert.AreEqual(1, request.Metadata.Count);
    }

    // =========================================================================
    // 6. AgentEvent record
    // =========================================================================

    [TestMethod]
    public void AgentEvent_RequiredFields_AreEnforced()
    {
        var evt = MakeEvent();

        Assert.AreEqual("evt-1", evt.EventId);
        Assert.AreEqual(AgentEventKind.TurnStarted, evt.Kind);
        Assert.IsTrue(evt.OccurredAt > DateTimeOffset.MinValue);
        Assert.AreSame(MakeSessionId().Value, evt.Session.Value); // 简单确认 Session 非空
    }

    [TestMethod]
    public void AgentEvent_OptionalFields_DefaultValues()
    {
        var evt = MakeEvent();

        Assert.AreEqual(AgentEventLevel.Information, evt.Level);
        Assert.IsNull(evt.CorrelationId);
        Assert.IsNull(evt.TurnId);
        Assert.IsNull(evt.PayloadJson);
        Assert.AreEqual(0, evt.Metadata.Count);
    }

    [TestMethod]
    public void AgentEvent_WithExpression_ProducesNewInstance()
    {
        var original = MakeEvent();
        var updated = original with { Kind = AgentEventKind.TurnCompleted };

        Assert.AreEqual(AgentEventKind.TurnStarted, original.Kind);
        Assert.AreEqual(AgentEventKind.TurnCompleted, updated.Kind);
        Assert.AreNotSame(original, updated);
    }

    [TestMethod]
    public void AgentEvent_IsSealedRecord()
    {
        var type = typeof(AgentEvent);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 7. AgentEventQuery record
    // =========================================================================

    [TestMethod]
    public void AgentEventQuery_RequiredSessionId()
    {
        var sessionId = MakeSessionId();
        var query = new AgentEventQuery { SessionId = sessionId };

        Assert.AreSame(sessionId, query.SessionId);
    }

    [TestMethod]
    public void AgentEventQuery_DefaultValues()
    {
        var query = new AgentEventQuery { SessionId = MakeSessionId() };

        Assert.IsNull(query.Kind);
        Assert.IsNull(query.Level);
        Assert.IsNull(query.TurnId);
        Assert.IsNull(query.CorrelationId);
        Assert.IsNull(query.Since);
        Assert.IsNull(query.Until);
        Assert.AreEqual(100, query.Take);
    }

    [TestMethod]
    public void AgentEventQuery_AllFieldsCanBeSet()
    {
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var until = DateTimeOffset.UtcNow;
        var query = new AgentEventQuery
        {
            SessionId = MakeSessionId(),
            Kind = AgentEventKind.ToolCallCompleted,
            Level = AgentEventLevel.Warning,
            TurnId = "turn-1",
            CorrelationId = "corr-1",
            Since = since,
            Until = until,
            Take = 50
        };

        Assert.AreEqual(AgentEventKind.ToolCallCompleted, query.Kind);
        Assert.AreEqual(AgentEventLevel.Warning, query.Level);
        Assert.AreEqual("turn-1", query.TurnId);
        Assert.AreEqual("corr-1", query.CorrelationId);
        Assert.AreEqual(since, query.Since);
        Assert.AreEqual(until, query.Until);
        Assert.AreEqual(50, query.Take);
    }

    // =========================================================================
    // 8. IAgentRuntime 接口
    // =========================================================================

    [TestMethod]
    public void IAgentRuntime_IsInterface()
    {
        Assert.IsTrue(typeof(IAgentRuntime).IsInterface);
    }

    [TestMethod]
    public void IAgentRuntime_Has3Methods()
    {
        var methods = typeof(IAgentRuntime).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .ToList();
        Assert.AreEqual(3, methods.Count);
        var names = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "CloseSessionAsync", "CreateSessionAsync", "IsSessionActiveAsync" },
            names);
    }

    [TestMethod]
    public void IAgentRuntime_AllMethods_ReturnTask()
    {
        foreach (var method in typeof(IAgentRuntime).GetMethods().Where(m => !m.IsSpecialName))
        {
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                method.ReturnType == typeof(Task<bool>) ||
                method.ReturnType == typeof(Task<AgentSessionId>) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"{method.Name} should return Task or Task<T>");
        }
    }

    [TestMethod]
    public void IAgentRuntime_HasRuntimeIdAndKind_Properties()
    {
        var runtimeIdProp = typeof(IAgentRuntime).GetProperty("RuntimeId");
        var runtimeKindProp = typeof(IAgentRuntime).GetProperty("RuntimeKind");
        Assert.IsNotNull(runtimeIdProp);
        Assert.IsNotNull(runtimeKindProp);
        Assert.AreEqual(typeof(string), runtimeIdProp!.PropertyType);
        Assert.AreEqual(typeof(AgentRuntimeKind), runtimeKindProp!.PropertyType);
    }

    [TestMethod]
    public void IAgentRuntime_CreateSessionAsync_HasStoreOperationWriteAttribute()
    {
        var method = typeof(IAgentRuntime).GetMethod("CreateSessionAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Write, attr!.Kind);
    }

    [TestMethod]
    public void IAgentRuntime_IsSessionActiveAsync_HasStoreOperationReadAttribute()
    {
        var method = typeof(IAgentRuntime).GetMethod("IsSessionActiveAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Read, attr!.Kind);
    }

    // =========================================================================
    // 9. IAgentSession 接口
    // =========================================================================

    [TestMethod]
    public void IAgentSession_IsInterface()
    {
        Assert.IsTrue(typeof(IAgentSession).IsInterface);
    }

    [TestMethod]
    public void IAgentSession_HasSessionIdProperty_AndEventsProperty()
    {
        Assert.IsNotNull(typeof(IAgentSession).GetProperty("SessionId"));
        Assert.IsNotNull(typeof(IAgentSession).GetProperty("Events"));
        Assert.AreEqual(typeof(AgentSessionId), typeof(IAgentSession).GetProperty("SessionId")!.PropertyType);
        Assert.AreEqual(typeof(IAgentEventStream), typeof(IAgentSession).GetProperty("Events")!.PropertyType);
    }

    [TestMethod]
    public void IAgentSession_Has3Methods()
    {
        // 过滤掉属性 getter（get_SessionId / get_Events）
        var methods = typeof(IAgentSession).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName)
            .ToList();
        var names = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "CompleteTurnAsync", "RecordToolCallResultAsync", "StartTurnAsync" },
            names);
        Assert.AreEqual(3, methods.Count);
    }

    [TestMethod]
    public void IAgentSession_AllMethods_ReturnTask()
    {
        foreach (var method in typeof(IAgentSession).GetMethods().Where(m => !m.IsSpecialName))
        {
            Assert.IsTrue(
                method.ReturnType == typeof(Task) ||
                (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)),
                $"{method.Name} should return Task or Task<T>");
        }
    }

    // =========================================================================
    // 10. IAgentEventStream 接口
    // =========================================================================

    [TestMethod]
    public void IAgentEventStream_IsInterface()
    {
        Assert.IsTrue(typeof(IAgentEventStream).IsInterface);
    }

    [TestMethod]
    public void IAgentEventStream_Has2Methods()
    {
        var methods = typeof(IAgentEventStream).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.AreEqual(2, methods.Length);
        var names = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(new[] { "QueryAsync", "SubscribeAsync" }, names);
    }

    [TestMethod]
    public void IAgentEventStream_SubscribeAsync_ReturnsIAsyncEnumerable()
    {
        var method = typeof(IAgentEventStream).GetMethod("SubscribeAsync");
        Assert.IsNotNull(method);
        Assert.IsTrue(method!.ReturnType.IsGenericType);
        Assert.AreEqual(typeof(IAsyncEnumerable<>), method.ReturnType.GetGenericTypeDefinition());
        Assert.AreEqual(typeof(AgentEvent), method.ReturnType.GetGenericArguments()[0]);
    }

    [TestMethod]
    public void IAgentEventStream_QueryAsync_ReturnsTaskOfReadOnlyList()
    {
        var method = typeof(IAgentEventStream).GetMethod("QueryAsync");
        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(Task<IReadOnlyList<AgentEvent>>), method!.ReturnType);
    }

    // =========================================================================
    // 11. IAgentWorkspaceContextProvider 接口
    // =========================================================================

    [TestMethod]
    public void IAgentWorkspaceContextProvider_IsInterface()
    {
        Assert.IsTrue(typeof(IAgentWorkspaceContextProvider).IsInterface);
    }

    [TestMethod]
    public void IAgentWorkspaceContextProvider_Has3Methods()
    {
        var methods = typeof(IAgentWorkspaceContextProvider).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.AreEqual(3, methods.Length);
        var names = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "GetContextSnapshotAsync", "IngestToolResultAsync", "InjectAsync" },
            names);
    }

    [TestMethod]
    public void IAgentWorkspaceContextProvider_GetContextSnapshotAsync_ReturnsTaskOfSnapshotRef()
    {
        var method = typeof(IAgentWorkspaceContextProvider).GetMethod("GetContextSnapshotAsync");
        Assert.IsNotNull(method);
        Assert.AreEqual(typeof(Task<AgentContextSnapshotRef>), method!.ReturnType);
    }

    // =========================================================================
    // 12. IAgentCheckpointStore 接口
    // =========================================================================

    [TestMethod]
    public void IAgentCheckpointStore_IsInterface()
    {
        Assert.IsTrue(typeof(IAgentCheckpointStore).IsInterface);
    }

    [TestMethod]
    public void IAgentCheckpointStore_Has4Methods()
    {
        var methods = typeof(IAgentCheckpointStore).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        Assert.AreEqual(4, methods.Length);
        var names = methods.Select(m => m.Name).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            new[] { "DeleteAsync", "GetAsync", "ListAsync", "SaveAsync" },
            names);
    }

    [TestMethod]
    public void IAgentCheckpointStore_SaveAsync_HasStoreOperationWriteAttribute()
    {
        var method = typeof(IAgentCheckpointStore).GetMethod("SaveAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Write, attr!.Kind);
    }

    [TestMethod]
    public void IAgentCheckpointStore_GetAsync_HasStoreOperationReadAttribute()
    {
        var method = typeof(IAgentCheckpointStore).GetMethod("GetAsync");
        Assert.IsNotNull(method);
        var attr = method!.GetCustomAttribute<StoreOperationAttribute>();
        Assert.IsNotNull(attr);
        Assert.AreEqual(StoreOperationKind.Read, attr!.Kind);
    }

    // =========================================================================
    // 13. AgentContextSnapshotRef record
    // =========================================================================

    [TestMethod]
    public void AgentContextSnapshotRef_RequiredFields_AreEnforced()
    {
        var snapshot = MakeSnapshotRef();

        Assert.AreEqual("snap-1", snapshot.SnapshotId);
        Assert.AreEqual(100, snapshot.ActualTokens);
        Assert.AreEqual(500, snapshot.TokenBudget);
        Assert.AreEqual("{}", snapshot.ContentJson);
        Assert.IsTrue(snapshot.CreatedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void AgentContextSnapshotRef_DefaultMetadata_Empty()
    {
        var snapshot = MakeSnapshotRef();

        Assert.AreEqual(0, snapshot.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextSnapshotRef_IsSealedRecord()
    {
        var type = typeof(AgentContextSnapshotRef);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 14. AgentContextInjection record
    // =========================================================================

    [TestMethod]
    public void AgentContextInjection_RequiredFields_AreEnforced()
    {
        var injection = new AgentContextInjection
        {
            InjectionId = "inj-1",
            InjectedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual("inj-1", injection.InjectionId);
        Assert.IsTrue(injection.InjectedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void AgentContextInjection_DefaultCollections_Empty()
    {
        var injection = new AgentContextInjection
        {
            InjectionId = "inj-1",
            InjectedAt = DateTimeOffset.UtcNow
        };

        Assert.AreEqual(0, injection.DecisionRequestIds.Count);
        Assert.AreEqual(0, injection.ConstraintIds.Count);
        Assert.IsNull(injection.FreeText);
        Assert.AreEqual(0, injection.Metadata.Count);
    }

    [TestMethod]
    public void AgentContextInjection_AllFieldsCanBeSet()
    {
        var injection = new AgentContextInjection
        {
            InjectionId = "inj-1",
            DecisionRequestIds = new[] { "req-1", "req-2" },
            ConstraintIds = new[] { "cstr-1" },
            FreeText = "user-preference",
            InjectedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string> { ["source"] = "test" }
        };

        Assert.AreEqual(2, injection.DecisionRequestIds.Count);
        Assert.AreEqual(1, injection.ConstraintIds.Count);
        Assert.AreEqual("user-preference", injection.FreeText);
        Assert.AreEqual(1, injection.Metadata.Count);
    }

    // =========================================================================
    // 15. AgentCheckpoint record
    // =========================================================================

    [TestMethod]
    public void AgentCheckpoint_RequiredFields_AreEnforced()
    {
        var checkpoint = MakeCheckpoint();

        Assert.AreEqual("ckpt-1", checkpoint.CheckpointId);
        Assert.AreEqual("{}", checkpoint.StateJson);
        Assert.IsTrue(checkpoint.CreatedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void AgentCheckpoint_DefaultOptionalFields_Null()
    {
        var checkpoint = MakeCheckpoint();

        Assert.IsNull(checkpoint.TurnId);
        Assert.IsNull(checkpoint.SnapshotId);
        Assert.AreEqual(0, checkpoint.Metadata.Count);
    }

    [TestMethod]
    public void AgentCheckpoint_AllFieldsCanBeSet()
    {
        var checkpoint = new AgentCheckpoint
        {
            CheckpointId = "ckpt-1",
            Session = MakeSessionId(),
            CreatedAt = DateTimeOffset.UtcNow,
            TurnId = "turn-1",
            SnapshotId = "snap-1",
            StateJson = "{\"state\":\"test\"}",
            Metadata = new Dictionary<string, string> { ["reason"] = "manual" }
        };

        Assert.AreEqual("turn-1", checkpoint.TurnId);
        Assert.AreEqual("snap-1", checkpoint.SnapshotId);
        Assert.AreEqual("{\"state\":\"test\"}", checkpoint.StateJson);
        Assert.AreEqual(1, checkpoint.Metadata.Count);
    }

    [TestMethod]
    public void AgentCheckpoint_IsSealedRecord()
    {
        var type = typeof(AgentCheckpoint);
        Assert.IsTrue(type.IsSealed);
        Assert.IsTrue(type.IsClass);
        Assert.IsFalse(type.IsValueType);
    }

    // =========================================================================
    // 16. 反射验证：no async void
    // =========================================================================

    [TestMethod]
    public void NoAsyncVoid_InAgentInterfaces()
    {
        var types = new[]
        {
            typeof(IAgentRuntime),
            typeof(IAgentSession),
            typeof(IAgentEventStream),
            typeof(IAgentWorkspaceContextProvider),
            typeof(IAgentCheckpointStore)
        };

        foreach (var type in types)
        {
            foreach (var method in type.GetMethods().Where(m => !m.IsSpecialName))
            {
                Assert.AreNotEqual(typeof(void), method.ReturnType,
                    $"{type.Name}.{method.Name} must not return void");
            }
        }
    }

    // =========================================================================
    // 17. 不依赖具体 Agent SDK 对象模型
    // =========================================================================

    [TestMethod]
    public void AgentContracts_DoNotReferenceSdkTypes()
    {
        // ContextCore.Abstractions 不应引用 OpenAI / Anthropic / Codex SDK 类型
        // 仅检查命名空间与类型名，不解析程序集依赖
        var assembly = typeof(AgentSessionId).Assembly;
        var allTypes = assembly.GetTypes();

        var forbiddenPatterns = new[]
        {
            "OpenAI", "Anthropic", "Codex", "ClaudeSdk", "ClaudeCodeSdk"
        };

        foreach (var type in allTypes)
        {
            foreach (var pattern in forbiddenPatterns)
            {
                Assert.IsFalse(
                    type.Namespace?.Contains(pattern, StringComparison.Ordinal) ?? false,
                    $"Type {type.FullName} should not be in namespace containing '{pattern}'");
                Assert.IsFalse(
                    type.Name.Contains(pattern, StringComparison.Ordinal),
                    $"Type {type.Name} should not contain '{pattern}' in name");
            }
        }
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

    private static AgentEvent MakeEvent()
    {
        return new AgentEvent
        {
            EventId = "evt-1",
            Session = MakeSessionId(),
            Kind = AgentEventKind.TurnStarted,
            OccurredAt = DateTimeOffset.UtcNow
        };
    }

    private static AgentContextSnapshotRef MakeSnapshotRef()
    {
        return new AgentContextSnapshotRef
        {
            SnapshotId = "snap-1",
            Session = MakeSessionId(),
            CreatedAt = DateTimeOffset.UtcNow,
            ActualTokens = 100,
            TokenBudget = 500,
            ContentJson = "{}"
        };
    }

    private static AgentCheckpoint MakeCheckpoint()
    {
        return new AgentCheckpoint
        {
            CheckpointId = "ckpt-1",
            Session = MakeSessionId(),
            CreatedAt = DateTimeOffset.UtcNow,
            StateJson = "{}"
        };
    }
}
