using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// IMemoryStateStore 的 in-memory 实现。append-only 事件流；线程安全。
/// </summary>
/// <remarks>
/// 设计原则：
/// 1. 事件流不可变：AppendEventAsync 校验 EventId 唯一性，重复 EventId 抛 ArgumentException。
/// 2. NewState 校验：MemoryStateEventRecord.NewState 不允许为 Fresh
/// （Fresh 是初始态，不是事件目标）。
/// 3. GetLatestStateAsync：按 SourceItemId 过滤，取 OccurredAt 最晚的事件 NewState；
/// 从未记录返回 null（视为 Fresh）。
/// 4. 生产部署应替换为 PostgresMemoryStateStore 持久化实现。
/// </remarks>
public sealed class InMemoryMemoryStateStore : IMemoryStateStore
{
    private readonly ConcurrentDictionary<string, MemoryStateEventRecord> _eventsById = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<MemoryStateEventRecord> _events = new();

    /// <inheritdoc />
    public Task AppendEventAsync(
        MemoryStateEventRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (record.NewState == MemoryState.Fresh)
        {
            throw new ArgumentException(
                "MemoryStateEventRecord.NewState cannot be Fresh (Fresh is the initial state, not an event target).",
                nameof(record));
        }

        if (!_eventsById.TryAdd(record.EventId, record))
        {
            throw new ArgumentException(
                $"Duplicate EventId '{record.EventId}'. Memory state events are append-only and EventId must be unique.",
                nameof(record));
        }

        _events.Add(record);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryStateEventRecord>> QueryEventsAsync(
        MemoryStateEventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<MemoryStateEventRecord> results = _events;

        if (query.CollectionId is not null)
        {
            results = results.Where(e => e.CollectionId == query.CollectionId);
        }
        if (query.SourceItemId is not null)
        {
            results = results.Where(e => e.SourceItemId == query.SourceItemId);
        }
        if (query.TargetItemId is not null)
        {
            results = results.Where(e => e.TargetItemId == query.TargetItemId);
        }
        if (query.ItemType is not null)
        {
            results = results.Where(e => e.ItemType == query.ItemType);
        }
        if (query.NewState is not null)
        {
            results = results.Where(e => e.NewState == query.NewState.Value);
        }
        if (query.Since is not null)
        {
            results = results.Where(e => e.OccurredAt >= query.Since.Value);
        }
        if (query.Until is not null)
        {
            results = results.Where(e => e.OccurredAt <= query.Until.Value);
        }

        var ordered = results.OrderByDescending(e => e.OccurredAt).ToList();
        if (query.Take > 0 && ordered.Count > query.Take)
        {
            ordered = ordered.Take(query.Take).ToList();
        }

        return Task.FromResult<IReadOnlyList<MemoryStateEventRecord>>(ordered);
    }

    /// <inheritdoc />
    public Task<MemoryStateEventRecord?> GetLatestStateAsync(
        string workspaceId,
        string collectionId,
        string sourceItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemId);
        cancellationToken.ThrowIfCancellationRequested();

        var latest = _events
            .Where(e => e.WorkspaceId == workspaceId
                && e.CollectionId == collectionId
                && e.SourceItemId == sourceItemId)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefault();

        return Task.FromResult(latest);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemoryStateEventRecord>> GetRecentAsync(
        string workspaceId,
        string collectionId,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        if (take < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "take must be non-negative.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var query = new MemoryStateEventQuery
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            Take = take
        };

        return QueryEventsAsync(query, cancellationToken);
    }
}
