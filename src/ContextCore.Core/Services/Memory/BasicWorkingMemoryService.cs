using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core;

/// <summary>
/// <see cref="IWorkingMemoryService"/> 的基础实现，将工作记忆层的增删查操作委托给 <see cref="IMemoryStore"/>。
/// </summary>
public sealed class BasicWorkingMemoryService : IWorkingMemoryService
{
    private readonly ConcurrentDictionary<string, WorkingMemoryActiveContext> _activeContexts = new();
    private readonly ConcurrentDictionary<string, WorkingMemoryCurrentTask> _currentTasks = new();
    private readonly IMemoryStore _memoryStore;

    public BasicWorkingMemoryService(IMemoryStore memoryStore)
    {
        _memoryStore = memoryStore;
    }

    /// <summary>规范化并添加一条工作记忆条目，自动设置层级为 <see cref="ContextMemoryLayer.Working"/>。</summary>
    public async Task<WorkingMemoryItem> AddAsync(
        WorkingMemoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var normalized = Normalize(item);

        await _memoryStore.SaveAsync(ToMemoryItem(normalized), cancellationToken).ConfigureAwait(false);

        return normalized;
    }

    /// <summary>查询指定集合中最近的工作记忆条目。</summary>
    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var items = await _memoryStore.QueryAsync(
            new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Layer = ContextMemoryLayer.Working,
                Take = take
            },
            cancellationToken).ConfigureAwait(false);

        return items.Select(FromMemoryItem).ToArray();
    }

    /// <summary>将指定集合中所有工作记忆条目标记为 <see cref="ContextMemoryStatus.Deprecated"/>。</summary>
    public async Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var items = await _memoryStore.QueryAsync(
            new ContextMemoryQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Layer = ContextMemoryLayer.Working,
                Take = int.MaxValue
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _memoryStore.UpdateStatusAsync(
                workspaceId,
                collectionId,
                item.Id,
                ContextMemoryStatus.Deprecated,
                cancellationToken).ConfigureAwait(false);
        }

        _activeContexts.TryRemove(Key(workspaceId, collectionId), out _);
        _currentTasks.TryRemove(Key(workspaceId, collectionId), out _);
    }

    public Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _activeContexts.TryGetValue(Key(workspaceId, collectionId), out var activeContext)
                ? Clone(activeContext)
                : null);
    }

    public Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeContext);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Clone(activeContext, updatedAt: DateTimeOffset.UtcNow);
        _activeContexts[Key(normalized.WorkspaceId, normalized.CollectionId)] = normalized;

        return Task.FromResult(Clone(normalized));
    }

    public Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _currentTasks.TryGetValue(Key(workspaceId, collectionId), out var currentTask)
                ? Clone(currentTask)
                : null);
    }

    public Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTask);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Clone(currentTask, updatedAt: DateTimeOffset.UtcNow);
        _currentTasks[Key(normalized.WorkspaceId, normalized.CollectionId)] = normalized;

        return Task.FromResult(Clone(normalized));
    }

    private static WorkingMemoryItem Normalize(WorkingMemoryItem item)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkingMemoryItem
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
    }

    private static WorkingMemoryItem FromMemoryItem(ContextMemoryItem item)
    {
        return new WorkingMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static ContextMemoryItem ToMemoryItem(WorkingMemoryItem item)
    {
        return new ContextMemoryItem
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = ContextMemoryLayer.Working,
            Status = ContextMemoryStatus.Candidate,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = 1,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static WorkingMemoryActiveContext Clone(
        WorkingMemoryActiveContext item,
        DateTimeOffset? updatedAt = null)
    {
        return new WorkingMemoryActiveContext
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CurrentTaskId = item.CurrentTaskId,
            Summary = item.Summary,
            MemoryRefs = item.MemoryRefs.ToArray(),
            ContextRefs = item.ContextRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            UpdatedAt = updatedAt ?? item.UpdatedAt
        };
    }

    private static WorkingMemoryCurrentTask Clone(
        WorkingMemoryCurrentTask item,
        DateTimeOffset? updatedAt = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkingMemoryCurrentTask
        {
            TaskId = string.IsNullOrWhiteSpace(item.TaskId) ? Guid.NewGuid().ToString("N") : item.TaskId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            Tags = item.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = updatedAt ?? (item.UpdatedAt == default ? now : item.UpdatedAt)
        };
    }

    private static string Key(string workspaceId, string collectionId)
    {
        return $"{workspaceId}\u001f{collectionId}";
    }
}
