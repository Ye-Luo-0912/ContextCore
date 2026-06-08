using System.Collections.Concurrent;
using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.ModelGateway;
using ContextCore.Service.Infrastructure;
using ContextCore.Storage.InMemory;
using Microsoft.Extensions.Logging;

namespace ContextCore.Tests;

/// <summary>覆盖 ContextCore 诊断链路：ILogger 事件、ActivitySource span 与模型网关标签。</summary>
[TestClass]
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
            contextStore,
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
}
