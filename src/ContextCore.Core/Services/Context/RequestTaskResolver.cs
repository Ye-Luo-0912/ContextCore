using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// 解析请求元数据中的当前任务和运行时约束（request-metadata 投影为 working-memory / constraint 领域对象）。
/// 这些方法不访问任何实例状态，仅从 <see cref="ContextPackageRequest"/> 的元数据字典中读取约定键。
/// </summary>
internal static class RequestTaskResolver
{
    /// <summary>
    /// 判断请求元数据是否包含任何 currentTask 相关键。
    /// </summary>
    internal static bool HasRequestCurrentTaskMetadata(ContextPackageRequest request)
    {
        return PackageUncertaintyBuilder.TryReadMetadata(
            request.Metadata,
            out _,
            "currentTaskId",
            "taskId",
            "current_task.id",
            "currentTaskTitle",
            "taskTitle",
            "current_task.title",
            "currentTaskDescription",
            "taskDescription",
            "current_task.description",
            "currentTaskStatus",
            "taskStatus",
            "current_task.status");
    }

    /// <summary>
    /// 从请求元数据构建当前任务对象；当所有字段为空且无 QueryText 时返回 null。
    /// </summary>
    internal static WorkingMemoryCurrentTask? CreateRequestCurrentTask(
        ContextPackageRequest request,
        string collectionId)
    {
        var taskId = ReadRequestMetadata(request, "currentTaskId", "taskId", "current_task.id");
        var title = ReadRequestMetadata(request, "currentTaskTitle", "taskTitle", "current_task.title");
        var description = ReadRequestMetadata(
            request,
            "currentTaskDescription",
            "taskDescription",
            "current_task.description");
        var status = ReadRequestMetadata(request, "currentTaskStatus", "taskStatus", "current_task.status");

        if (string.IsNullOrWhiteSpace(taskId)
            && string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(description)
            && string.IsNullOrWhiteSpace(status)
            && string.IsNullOrWhiteSpace(request.QueryText))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        return new WorkingMemoryCurrentTask
        {
            TaskId = string.IsNullOrWhiteSpace(taskId) ? "request-current-task" : taskId,
            WorkspaceId = request.WorkspaceId,
            CollectionId = collectionId,
            Title = string.IsNullOrWhiteSpace(title) ? request.QueryText ?? "当前任务" : title,
            Description = string.IsNullOrWhiteSpace(description) ? request.QueryText ?? string.Empty : description,
            Status = string.IsNullOrWhiteSpace(status) ? "active" : status,
            Tags = request.RequiredTags.ToArray(),
            Metadata = new Dictionary<string, string>(request.Metadata),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// 从请求元数据按多个候选键读取首个非空值。
    /// </summary>
    internal static string? ReadRequestMetadata(
        ContextPackageRequest request,
        params string[] keys)
    {
        return PackageUncertaintyBuilder.TryReadMetadata(request.Metadata, out var value, keys)
            ? value
            : null;
    }

    /// <summary>
    /// 从请求元数据构建运行时约束列表（scope=Task, level=Runtime）。
    /// </summary>
    internal static IReadOnlyList<ContextConstraint> CreateRequestConstraints(
        ContextPackageRequest request,
        string collectionId)
    {
        var values = ReadRequestConstraintValues(request.Metadata)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0)
        {
            return Array.Empty<ContextConstraint>();
        }

        var now = DateTimeOffset.UtcNow;
        return values
            .Select((value, index) => new ContextConstraint
            {
                Id = $"request-constraint-{index + 1}",
                WorkspaceId = request.WorkspaceId,
                CollectionId = collectionId,
                Scope = ContextScope.Task,
                Level = ConstraintLevel.Runtime,
                Content = value,
                SourceRefs = ["request:metadata"],
                Status = ContextMemoryStatus.Verified,
                Confidence = 1.0,
                Metadata = new Dictionary<string, string>
                {
                    ["origin"] = "request-metadata",
                    ["scope"] = "current-input",
                    ["priorityScope"] = "current-input"
                },
                CreatedAt = now,
                UpdatedAt = now
            })
            .ToArray();
    }

    /// <summary>
    /// 读取请求元数据中的约束值，按换行/分号切分后逐段返回。
    /// </summary>
    internal static IEnumerable<string> ReadRequestConstraintValues(
        IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in new[]
        {
            "currentConstraint",
            "currentConstraints",
            "requestConstraint",
            "requestConstraints",
            "runtimeConstraint",
            "runtimeConstraints"
        })
        {
            if (!PackageUncertaintyBuilder.TryReadMetadata(metadata, out var value, key))
            {
                continue;
            }

            foreach (var part in value.Split(
                ['\r', '\n', ';', '；'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    yield return part;
                }
            }
        }
    }
}
