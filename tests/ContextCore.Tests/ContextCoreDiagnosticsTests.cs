using System.Collections.Concurrent;
using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ModelGateway;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>覆盖 ContextCore 诊断链路：ILogger 事件、ActivitySource span 与模型网关标签。</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ContextCoreDiagnosticsTests
{
    [TestMethod]
    public async Task LoggingContextEventSink_ShouldWriteILoggerAndActivityTags()
    {
        using var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        using var activityCapture = new ActivityCapture();
        using var activity = ContextCoreDiagnostics.ActivitySource.StartActivity("test.logging", ActivityKind.Internal);
        Assert.IsNotNull(activity);

        var sink = new LoggingContextEventSink(
            loggerFactory.CreateLogger<LoggingContextEventSink>());
        var operationEvent = new ContextOperationEvent
        {
            EventId = "event-1",
            OperationId = "operation-1",
            OperationName = "diagnostics.test",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Level = ContextEventLevel.Warning,
            Message = "测试日志事件。",
            Duration = TimeSpan.FromMilliseconds(42),
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "diagnostics-test"
            },
            CreatedAt = DateTimeOffset.UtcNow
        };

        await sink.EmitAsync(operationEvent);

        var log = loggerProvider.Logs.Single();
        Assert.AreEqual(LogLevel.Warning, log.Level);
        StringAssert.Contains(log.Message, "ContextCore 操作事件");
        StringAssert.Contains(log.Message, "diagnostics.test");

        var scope = log.Scopes
            .OfType<IReadOnlyDictionary<string, object?>>()
            .Single();
        Assert.AreEqual("event-1", scope["contextcore.event_id"]);
        Assert.AreEqual("operation-1", scope["contextcore.operation_id"]);
        Assert.AreEqual("diagnostics-test", scope["contextcore.metadata.source"]);

        Assert.AreEqual("event-1", activity.GetTagItem("contextcore.event.id"));
        Assert.AreEqual("Warning", activity.GetTagItem("contextcore.event.level"));
        Assert.AreEqual("diagnostics-test", activity.GetTagItem("contextcore.metadata.source"));
    }

    [TestMethod]
    public async Task ContextRuntimeService_ShouldCreateActivityForRuntimeOperation()
    {
        using var activityCapture = new ActivityCapture();
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var eventSink = new InMemoryContextEventSink();
        var packageBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore);
        var runtime = new ContextRuntimeService(
            memoryStore,
            new BasicMemoryPromotionService(memoryStore, memoryStore),
            packageBuilder,
            new ContextInputIngestionService(
                contextStore,
                new ContextInputNormalizer(),
                new ContextInputValidator(),
                new ContextInputHasher(),
                new ContextInputSequencer()),
            new ContextValidationService(),
            eventSink);

        await runtime.IngestAsync(new ContextItem
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Content = "运行时 Activity 测试内容。",
            ContentFormat = ContextContentFormat.PlainText,
            SourceRefs = new[] { "source:test" }
        });

        var contextActivity = activityCapture.Activities.Single(activity =>
            activity.OperationName == "context.ingest");
        Assert.AreEqual("context.ingest", ReadTag(contextActivity, "contextcore.operation.name"));
        Assert.AreEqual("workspace-test", ReadTag(contextActivity, "contextcore.workspace.id"));
        Assert.AreEqual("collection-test", ReadTag(contextActivity, "contextcore.collection.id"));
        Assert.AreEqual(true, contextActivity.Tags["contextcore.succeeded"]);
        Assert.AreEqual(2, eventSink.Events.Count);
    }

    [TestMethod]
    public async Task ConfigurableModelGateway_ShouldCreateActivityForRouteAndAttempt()
    {
        using var activityCapture = new ActivityCapture();
        var gateway = new ConfigurableModelGateway(
            new ModelGatewayOptions
            {
                Models = new[]
                {
                    new ModelEndpointOptions
                    {
                        Name = "mock",
                        Provider = "mock",
                        Enabled = true,
                        Metadata = new Dictionary<string, string>
                        {
                            ["apiProviderName"] = "mock-api",
                            ["model"] = "mock-model",
                            ["category"] = "fast",
                            ["capabilities"] = "summary,json-response-format"
                        }
                    }
                }
            },
            new[] { new MockModelAdapter("mock", "模型网关诊断测试响应。") });

        var response = await gateway.CompleteAsync(new ModelRequest
        {
            OperationId = "model-operation-1",
            Role = ModelRole.ShortSummary,
            Prompt = "请总结一段中文上下文。",
            Metadata = new Dictionary<string, string>
            {
                ["taskKind"] = "summary",
                ["thinkingMode"] = "fast"
            }
        });

        Assert.IsTrue(response.Succeeded);
        var routeActivity = activityCapture.Activities.Single(activity =>
            activity.OperationName == "model.complete");
        Assert.AreEqual("ShortSummary", ReadTag(routeActivity, "contextcore.model.role"));
        Assert.AreEqual("FirstEnabledModel", ReadTag(routeActivity, "contextcore.model.route_source"));
        Assert.AreEqual("mock", ReadTag(routeActivity, "contextcore.model.primary"));
        Assert.AreEqual(true, routeActivity.Tags["contextcore.succeeded"]);

        var attemptActivity = activityCapture.Activities.Single(activity =>
            activity.OperationName == "model.complete.attempt");
        Assert.AreEqual("mock", ReadTag(attemptActivity, "contextcore.model.name"));
        Assert.AreEqual("mock", ReadTag(attemptActivity, "contextcore.model.provider"));
        Assert.AreEqual("mock-api", ReadTag(attemptActivity, "contextcore.model.api_provider"));
        Assert.AreEqual("mock-model", ReadTag(attemptActivity, "contextcore.model.provider_model"));
        Assert.AreEqual("none", ReadTag(attemptActivity, "contextcore.model.failure_reason"));
        Assert.AreEqual(true, attemptActivity.Tags["contextcore.model.succeeded"]);
    }

    [TestMethod]
    public async Task CompositeContextEventSink_BestEffortSinkThrows_DoesNotBlockSubsequentSinks()
    {
        var throwing = new ThrowingContextEventSink(ContextEventSinkKind.BestEffort);
        var recording = new RecordingContextEventSink();
        var composite = new CompositeContextEventSink(new IContextEventSink[] { throwing, recording });

        var operationEvent = new ContextOperationEvent { EventId = "e1", OperationName = "test" };

        // BestEffort sink 失败应被吞掉（fail-open），不阻断后续 sink，也不向上抛出。
        await composite.EmitAsync(operationEvent, CancellationToken.None);

        Assert.AreEqual(1, recording.Events.Count);
        Assert.AreEqual("e1", recording.Events[0].EventId);
    }

    [TestMethod]
    public async Task CompositeContextEventSink_RequiredSinkThrows_AggregatesAndStillRunsSubsequentSinks()
    {
        var throwing = new ThrowingContextEventSink(ContextEventSinkKind.Required);
        var recording = new RecordingContextEventSink();
        var composite = new CompositeContextEventSink(new IContextEventSink[] { throwing, recording });

        var operationEvent = new ContextOperationEvent { EventId = "e2", OperationName = "test" };

        // Required sink 失败应聚合成 AggregateException 抛出（fail-closed），但遍历不中断。
        var ex = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => composite.EmitAsync(operationEvent, CancellationToken.None));
        Assert.IsTrue(ex.InnerExceptions.Count >= 1);
        Assert.AreEqual(1, recording.Events.Count);
        Assert.AreEqual("e2", recording.Events[0].EventId);
    }

    [TestMethod]
    public async Task ContextRuntimeService_SinkThrowsOnOperationStarted_BusinessStillExecutes()
    {
        // sink 在每次 Emit 都抛异常：验证 "Operation started." 失败不会阻断正式业务。
        var throwing = new ThrowingContextEventSink(ContextEventSinkKind.BestEffort);
        var runtime = BuildRuntimeWithSink(throwing);

        var item = await runtime.IngestAsync(new ContextItem
        {
            WorkspaceId = "ws-failopen",
            CollectionId = "col-failopen",
            Type = "note",
            Content = "sink 抛异常时业务仍应执行。",
            ContentFormat = ContextContentFormat.PlainText
        });

        Assert.IsNotNull(item);
        Assert.IsFalse(string.IsNullOrEmpty(item.Id));
    }

    [TestMethod]
    public async Task ContextRuntimeService_SinkThrowsOnErrorEvent_OriginalExceptionPreserved()
    {
        // 仅在 Error 级别事件抛异常的 sink：验证错误事件失败不会遮蔽业务原始异常。
        var errorThrowing = new ErrorThrowingContextEventSink();
        var runtime = BuildRuntimeWithSink(errorThrowing);

        // 触发业务异常：空 WorkspaceId 校验失败 → ThrowIfInvalid 抛 ArgumentException。
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => runtime.BuildPackageAsync(new ContextPackageRequest
            {
                WorkspaceId = "",
                CollectionId = "col",
                TokenBudget = 100
            }));
    }

    [TestMethod]
    public async Task ContextRuntimeService_PreCancelledToken_ErrorEventStillRecordedViaNone()
    {
        // InMemoryContextEventSink 在 token 已取消时 ThrowIfCancellationRequested：
        // - "Operation started." 使用已取消 token → 抛出 → 被 EmitBestEffortAsync 吞掉 → 不记录
        // - 业务 action 抛 ArgumentException（空 WorkspaceId 校验失败）
        // - 错误事件使用 CancellationToken.None → 不抛出 → 被记录
        // 若错误事件未切换到 None，则 Events.Count == 0；切换后 Events.Count == 1（仅 Error 级别）。
        var recording = new InMemoryContextEventSink();
        var runtime = BuildRuntimeWithSink(recording);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => runtime.BuildPackageAsync(new ContextPackageRequest
            {
                WorkspaceId = "",
                CollectionId = "col",
                TokenBudget = 100
            }, cts.Token));

        Assert.AreEqual(1, recording.Events.Count);
        Assert.AreEqual(ContextEventLevel.Error, recording.Events[0].Level);
    }

    private static ContextRuntimeService BuildRuntimeWithSink(IContextEventSink eventSink)
    {
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var constraintStore = new InMemoryConstraintStore();
        var globalStore = new InMemoryGlobalContextStore();
        var packageBuilder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore,
            globalStore,
            memoryStore,
            relationStore);
        return new ContextRuntimeService(
            memoryStore,
            new BasicMemoryPromotionService(memoryStore, memoryStore),
            packageBuilder,
            new ContextInputIngestionService(
                contextStore,
                new ContextInputNormalizer(),
                new ContextInputValidator(),
                new ContextInputHasher(),
                new ContextInputSequencer()),
            new ContextValidationService(),
            eventSink);
    }

    private static string? ReadTag(CapturedActivity activity, string key)
    {
        return activity.Tags.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private sealed class ActivityCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<CapturedActivity> _activities = new();

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ContextCoreDiagnostics.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _activities.Enqueue(CapturedActivity.From(activity))
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<CapturedActivity> Activities => _activities.ToArray();

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    private sealed record CapturedActivity(
        string OperationName,
        IReadOnlyDictionary<string, object?> Tags)
    {
        public static CapturedActivity From(Activity activity)
        {
            return new CapturedActivity(
                activity.OperationName,
                activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly AsyncLocal<Stack<object?>> _scopes = new();

        public ConcurrentQueue<CapturedLog> Logs { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, Logs, _scopes);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly ConcurrentQueue<CapturedLog> _logs;
        private readonly AsyncLocal<Stack<object?>> _scopes;

        public CapturingLogger(
            string categoryName,
            ConcurrentQueue<CapturedLog> logs,
            AsyncLocal<Stack<object?>> scopes)
        {
            _categoryName = categoryName;
            _logs = logs;
            _scopes = scopes;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            var scopes = _scopes.Value ??= new Stack<object?>();
            scopes.Push(state);
            return new Scope(scopes);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var scopes = _scopes.Value is null
                ? Array.Empty<object?>()
                : _scopes.Value.Reverse().ToArray();
            _logs.Enqueue(new CapturedLog(
                _categoryName,
                logLevel,
                formatter(state, exception),
                exception,
                scopes));
        }

        private sealed class Scope : IDisposable
        {
            private readonly Stack<object?> _scopes;
            private bool _disposed;

            public Scope(Stack<object?> scopes)
            {
                _scopes = scopes;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                if (_scopes.Count > 0)
                {
                    _scopes.Pop();
                }

                _disposed = true;
            }
        }
    }

    private sealed record CapturedLog(
        string CategoryName,
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyList<object?> Scopes);

    /// <summary>每次 Emit 都抛 InvalidOperationException 的 sink，可配置 Kind。</summary>
    private sealed class ThrowingContextEventSink : IContextEventSink
    {
        private readonly ContextEventSinkKind _kind;

        public ThrowingContextEventSink(ContextEventSinkKind kind)
        {
            _kind = kind;
        }

        public ContextEventSinkKind Kind => _kind;

        public Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("throwing-sink-test");
        }
    }

    /// <summary>仅在 Error 级别事件抛异常的 sink，用于验证错误事件失败不遮蔽原始业务异常。</summary>
    private sealed class ErrorThrowingContextEventSink : IContextEventSink
    {
        public Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
        {
            if (operationEvent.Level == ContextEventLevel.Error)
            {
                throw new InvalidOperationException("error-sink-test");
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>记录所有已发送事件的 sink，用于断言复合接收器的遍历行为。</summary>
    private sealed class RecordingContextEventSink : IContextEventSink
    {
        private readonly object _gate = new();
        private readonly List<ContextOperationEvent> _events = new();

        public IReadOnlyList<ContextOperationEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public Task EmitAsync(ContextOperationEvent operationEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operationEvent);
            lock (_gate)
            {
                _events.Add(operationEvent);
            }
            return Task.CompletedTask;
        }
    }
}
