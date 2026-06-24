using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.InMemory;

/// <summary>
/// 基于内存的记忆存储，同时实现 <see cref="IMemoryStore"/>、<see cref="IWorkingMemoryService"/> 和 <see cref="IPromotionRecordStore"/>，
/// 适用于测试和短生命周期场景。
/// </summary>
/// <remarks>
/// TODO-DEMO [P2-3]：内存存储仅用于测试，进程重启后数据全部丢失。
/// ControlRoom 的 <c>--storage memory</c> 选项应向用户显示明确警告。参见：TODO.md → P2-3
/// </remarks>
public sealed class InMemoryMemoryStore : IMemoryStore, IWorkingMemoryService, IPromotionRecordStore, IPromotionCandidateStore
{
    private readonly ConcurrentDictionary<string, ContextMemoryItem> _items = new();
    private readonly ConcurrentDictionary<string, PromotionCandidate> _promotionCandidates = new();
    private readonly ConcurrentDictionary<string, WorkingMemoryActiveContext> _activeContexts = new();
    private readonly ConcurrentDictionary<string, WorkingMemoryCurrentTask> _currentTasks = new();
    private readonly List<ContextPromotionRecord> _promotionRecords = new();
    private readonly Lock _promotionGate = new();

    public Task SaveAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Clone(item, string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id);
        _items[ItemKey(normalized.WorkspaceId, normalized.CollectionId, normalized.Id)] = normalized;

        return Task.CompletedTask;
    }

    public Task<ContextMemoryItem?> GetAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _items.TryGetValue(ItemKey(workspaceId, collectionId, id), out var item)
                ? Clone(item)
                : null);
    }

    public Task<IReadOnlyList<ContextMemoryItem>> QueryAsync(
        ContextMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var take = query.Take > 0 ? query.Take : 50;
        var results = _items.Values
            .Where(item => string.Equals(item.WorkspaceId, query.WorkspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(query.CollectionId)
                || string.Equals(item.CollectionId, query.CollectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.Layer is null || item.Layer == query.Layer)
            .Where(item => query.Status is null || item.Status == query.Status)
            .Where(item => query.Tags.Count == 0
                || query.Tags.All(tag => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .Where(item => query.Types.Count == 0
                || query.Types.Any(type => string.Equals(type, item.Type, StringComparison.OrdinalIgnoreCase)))
            .Where(item => query.SourceRefs.Count == 0
                || query.SourceRefs.Any(sourceRef => item.SourceRefs
                    .Append(item.Id)
                    .Contains(sourceRef, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .Skip(Math.Max(0, query.Skip))
            .Take(take)
            .Select(item => Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<ContextMemoryItem>>(results);
    }

    public async Task UpdateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        ContextMemoryStatus status,
        CancellationToken cancellationToken = default)
    {
        var item = await GetAsync(workspaceId, collectionId, id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        await SaveAsync(Clone(item, item.Id, status: status, updatedAt: DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContextMemoryItem> AddAsync(ContextMemoryItem item, CancellationToken cancellationToken = default)
    {
        var working = Clone(
            item,
            string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            layer: ContextMemoryLayer.Working);
        await SaveAsync(working, cancellationToken).ConfigureAwait(false);
        return working;
    }

    public async Task<WorkingMemoryItem> AddAsync(WorkingMemoryItem item, CancellationToken cancellationToken = default)
    {
        var working = Normalize(item);
        await SaveAsync(ToMemoryItem(working), cancellationToken).ConfigureAwait(false);
        return working;
    }

    public async Task<IReadOnlyList<WorkingMemoryItem>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var items = await QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Layer = ContextMemoryLayer.Working,
            Take = take
        }, cancellationToken).ConfigureAwait(false);

        return items.Select(FromMemoryItem).ToArray();
    }

    public Task ClearAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var key in _items
            .Where(pair => string.Equals(pair.Value.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Value.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)
                && pair.Value.Layer == ContextMemoryLayer.Working)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _items.TryRemove(key, out _);
        }

        _activeContexts.TryRemove(ScopeKey(workspaceId, collectionId), out _);
        _currentTasks.TryRemove(ScopeKey(workspaceId, collectionId), out _);

        return Task.CompletedTask;
    }

    public Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _activeContexts.TryGetValue(ScopeKey(workspaceId, collectionId), out var activeContext)
                ? Clone(activeContext)
                : null);
    }

    public Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeContext);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(activeContext);
        _activeContexts[ScopeKey(normalized.WorkspaceId, normalized.CollectionId)] = normalized;

        return Task.FromResult(Clone(normalized));
    }

    public Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _currentTasks.TryGetValue(ScopeKey(workspaceId, collectionId), out var currentTask)
                ? Clone(currentTask)
                : null);
    }

    public Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentTask);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(currentTask);
        _currentTasks[ScopeKey(normalized.WorkspaceId, normalized.CollectionId)] = normalized;

        return Task.FromResult(Clone(normalized));
    }

    public Task SavePromotionRecordAsync(
        ContextPromotionRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_promotionGate)
        {
            _promotionRecords.Add(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ContextPromotionRecord>> QueryPromotionRecordsAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_promotionGate)
        {
            var count = take > 0 ? take : 50;
            return Task.FromResult<IReadOnlyList<ContextPromotionRecord>>([
                .. _promotionRecords
                    .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
                    .Where(item => string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(count)
                    .Select(item => Clone(item))
            ]);
        }
    }

    /// <summary>
    /// 保存一个促销候选项。当候选项的状态或其他属性发生变化时，应调用此方法进行更新。
    /// </summary>
    /// <param name="candidate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public Task SavePromotionCandidateAsync(
        PromotionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = Normalize(candidate);
        _promotionCandidates[CandidateKey(normalized.WorkspaceId, normalized.CollectionId, normalized.Id)] = normalized;

        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取指定促销候选项的详细信息，包括当前状态、审核记录等。当候选项不存在时返回 null。
    /// </summary>
    /// <param name="workspaceId"></param>
    /// <param name="collectionId"></param>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string workspaceId,
        string collectionId,
        string id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(
            _promotionCandidates.TryGetValue(CandidateKey(workspaceId, collectionId, id), out var candidate)
                ? Clone(candidate)
                : null);
    }

    public Task<IReadOnlyList<PromotionCandidate>> QueryPromotionCandidatesAsync(
        string workspaceId,
        string collectionId,
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var count = take > 0 ? take : 50;
        var candidates = _promotionCandidates.Values
            .Where(item => string.Equals(item.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.Equals(item.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase))
            .Where(item => status is null || item.Status == status.Value)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(count)
            .Select(item => Clone(item))
            .ToArray();

        return Task.FromResult<IReadOnlyList<PromotionCandidate>>(candidates);
    }

    public async Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string workspaceId,
        string collectionId,
        string id,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetPromotionCandidateAsync(workspaceId, collectionId, id, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        var updated = Clone(
            existing,
            status: status,
            reviewer: reviewer,
            reason: reason,
            updatedAt: DateTimeOffset.UtcNow);
        await SavePromotionCandidateAsync(updated, cancellationToken).ConfigureAwait(false);

        return updated;
    }

    private static ContextMemoryItem Clone(
        ContextMemoryItem item,
        string? id = null,
        ContextMemoryLayer? layer = null,
        ContextMemoryStatus? status = null,
        DateTimeOffset? updatedAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextMemoryItem
        {
            Id = id ?? item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Layer = layer ?? item.Layer,
            Status = status ?? item.Status,
            Type = item.Type,
            Content = item.Content,
            ContentFormat = item.ContentFormat,
            Tags = item.Tags.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Importance = item.Importance,
            Confidence = item.Confidence,
            Version = item.Version <= 0 ? 1 : item.Version,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = updatedAt ?? (item.UpdatedAt == default ? now : item.UpdatedAt)
        };
    }

    private static PromotionCandidate Normalize(PromotionCandidate item)
    {
        var now = DateTimeOffset.UtcNow;

        return new PromotionCandidate
        {
            Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.SourceId,
            SourceKind = item.SourceKind,
            Content = item.Content,
            TargetLayer = item.TargetLayer,
            Status = item.Status,
            Decision = item.Decision,
            Category = item.Category,
            Reason = item.Reason,
            Confidence = item.Confidence,
            MatchedRules = item.MatchedRules.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Reviewer = item.Reviewer,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt == default ? now : item.CreatedAt,
            UpdatedAt = item.UpdatedAt == default ? now : item.UpdatedAt
        };
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

    private static WorkingMemoryActiveContext Normalize(WorkingMemoryActiveContext item)
    {
        return new WorkingMemoryActiveContext
        {
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            CurrentTaskId = string.IsNullOrWhiteSpace(item.CurrentTaskId) ? null : item.CurrentTaskId,
            Summary = item.Summary,
            MemoryRefs = item.MemoryRefs.ToArray(),
            ContextRefs = item.ContextRefs.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            UpdatedAt = item.UpdatedAt == default ? DateTimeOffset.UtcNow : item.UpdatedAt
        };
    }

    private static WorkingMemoryCurrentTask Normalize(WorkingMemoryCurrentTask item)
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

    private static WorkingMemoryActiveContext Clone(WorkingMemoryActiveContext item)
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
            UpdatedAt = item.UpdatedAt
        };
    }

    private static WorkingMemoryCurrentTask Clone(WorkingMemoryCurrentTask item)
    {
        return new WorkingMemoryCurrentTask
        {
            TaskId = item.TaskId,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            Title = item.Title,
            Description = item.Description,
            Status = item.Status,
            Tags = item.Tags.ToArray(),
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static ContextPromotionRecord Clone(ContextPromotionRecord item)
    {
        return new ContextPromotionRecord
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceMemoryId = item.SourceMemoryId,
            FromStatus = item.FromStatus,
            ToStatus = item.ToStatus,
            Strategy = item.Strategy,
            Reviewer = item.Reviewer,
            TargetLayer = item.TargetLayer,
            SourceRefs = item.SourceRefs.ToArray(),
            RelationRefs = item.RelationRefs.ToArray(),
            Reason = item.Reason,
            Confidence = item.Confidence,
            CreatedAt = item.CreatedAt
        };
    }

    private static PromotionCandidate Clone(
        PromotionCandidate item,
        PromotionCandidateStatus? status = null,
        string? reviewer = null,
        string? reason = null,
        DateTimeOffset? updatedAt = null)
    {
        return new PromotionCandidate
        {
            Id = item.Id,
            WorkspaceId = item.WorkspaceId,
            CollectionId = item.CollectionId,
            SourceId = item.SourceId,
            SourceKind = item.SourceKind,
            Content = item.Content,
            TargetLayer = item.TargetLayer,
            Status = status ?? item.Status,
            Decision = item.Decision,
            Category = item.Category,
            Reason = reason ?? item.Reason,
            Confidence = item.Confidence,
            MatchedRules = item.MatchedRules.ToArray(),
            SourceRefs = item.SourceRefs.ToArray(),
            Reviewer = reviewer ?? item.Reviewer,
            Metadata = new Dictionary<string, string>(item.Metadata),
            CreatedAt = item.CreatedAt,
            UpdatedAt = updatedAt ?? item.UpdatedAt
        };
    }

    private static string ItemKey(string workspaceId, string collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001f{id}";
    }

    private static string CandidateKey(string workspaceId, string collectionId, string id)
    {
        return $"{workspaceId}\u001f{collectionId}\u001fpromotion-candidate\u001f{id}";
    }

    private static string ScopeKey(string workspaceId, string collectionId)
    {
        return $"{workspaceId}\u001f{collectionId}";
    }
}
