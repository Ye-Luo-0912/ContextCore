using System.Diagnostics;

namespace ContextCore.Abstractions;

/// <summary>
/// ContextCore 统一诊断入口。当前提供 ActivitySource，后续可直接接入 OpenTelemetry Collector 或 Seq。
/// </summary>
public static class ContextCoreDiagnostics
{
    public const string ActivitySourceName = "ContextCore";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "0.4.0");

    public static Activity? StartOperation(
        string operationName,
        string? operationId = null,
        string? workspaceId = null,
        string? collectionId = null)
    {
        var activity = ActivitySource.StartActivity(operationName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        SetCommonTags(activity, operationName, operationId, workspaceId, collectionId);
        return activity;
    }

    public static void SetCommonTags(
        Activity? activity,
        string operationName,
        string? operationId = null,
        string? workspaceId = null,
        string? collectionId = null)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("contextcore.operation.name", operationName);
        SetTagIfPresent(activity, "contextcore.operation.id", operationId);
        SetTagIfPresent(activity, "contextcore.workspace.id", workspaceId);
        SetTagIfPresent(activity, "contextcore.collection.id", collectionId);
    }

    public static void SetEventTags(Activity? activity, ContextOperationEvent operationEvent)
    {
        if (activity is null)
        {
            return;
        }

        SetCommonTags(
            activity,
            operationEvent.OperationName,
            operationEvent.OperationId,
            operationEvent.WorkspaceId,
            operationEvent.CollectionId);
        activity.SetTag("contextcore.event.id", operationEvent.EventId);
        activity.SetTag("contextcore.event.level", operationEvent.Level.ToString());
        if (operationEvent.Duration is not null)
        {
            activity.SetTag("contextcore.duration.ms", operationEvent.Duration.Value.TotalMilliseconds);
        }

        foreach (var (key, value) in operationEvent.Metadata)
        {
            SetTagIfPresent(activity, $"contextcore.metadata.{key}", value);
        }
    }

    public static void SetStatus(Activity? activity, bool succeeded, string? errorMessage = null)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("contextcore.succeeded", succeeded);
        if (succeeded)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, errorMessage);
        SetTagIfPresent(activity, "contextcore.error", errorMessage);
    }

    private static void SetTagIfPresent(Activity activity, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            activity.SetTag(key, value);
        }
    }
}
