using System.Diagnostics;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// 将 ContextCore 结构化操作事件写入 ILogger，同时保留事件原始字段作为日志作用域。
/// </summary>
public sealed class LoggingContextEventSink : IContextEventSink
{
    private readonly ILogger<LoggingContextEventSink> _logger;

    public LoggingContextEventSink(ILogger<LoggingContextEventSink> logger)
    {
        _logger = logger;
    }

    public Task EmitAsync(
        ContextOperationEvent operationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationEvent);
        cancellationToken.ThrowIfCancellationRequested();

        ContextCoreDiagnostics.SetEventTags(Activity.Current, operationEvent);
        using var scope = _logger.BeginScope(CreateScope(operationEvent));
        _logger.Log(
            ToLogLevel(operationEvent.Level),
            "ContextCore 操作事件：{OperationName} {OperationId} {Message}",
            operationEvent.OperationName,
            operationEvent.OperationId,
            operationEvent.Message);

        return Task.CompletedTask;
    }

    private static Dictionary<string, object?> CreateScope(ContextOperationEvent operationEvent)
    {
        var scope = new Dictionary<string, object?>
        {
            ["contextcore.event_id"] = operationEvent.EventId,
            ["contextcore.operation_id"] = operationEvent.OperationId,
            ["contextcore.operation_name"] = operationEvent.OperationName,
            ["contextcore.workspace_id"] = operationEvent.WorkspaceId,
            ["contextcore.collection_id"] = operationEvent.CollectionId,
            ["contextcore.level"] = operationEvent.Level.ToString(),
            ["contextcore.duration_ms"] = operationEvent.Duration?.TotalMilliseconds,
            ["contextcore.created_at"] = operationEvent.CreatedAt
        };

        foreach (var (key, value) in operationEvent.Metadata)
        {
            scope[$"contextcore.metadata.{key}"] = value;
        }

        return scope;
    }

    private static LogLevel ToLogLevel(ContextEventLevel level)
    {
        return level switch
        {
            ContextEventLevel.Trace => LogLevel.Debug,
            ContextEventLevel.Information => LogLevel.Information,
            ContextEventLevel.Warning => LogLevel.Warning,
            ContextEventLevel.Error => LogLevel.Error,
            _ => LogLevel.Information
        };
    }
}
