using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IRetrievalPlanFeedbackStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresRetrievalPlanFeedbackStore 实现同一契约，让 FileSystem / InMemory provider
/// 下自适应检索规划器的反馈记录仍可用（数据在进程重启后丢失）。
/// 按计划签名分组保存反馈，ListRecentAsync 返回按记录时间倒序的最新条目。
/// </remarks>
public sealed class InMemoryRetrievalPlanFeedbackStore : IRetrievalPlanFeedbackStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RetrievalPlanFeedback>> _bySignature = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask RecordAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.PlanSignature);

        var queue = _bySignature.GetOrAdd(feedback.PlanSignature, _ => new ConcurrentQueue<RetrievalPlanFeedback>());
        queue.Enqueue(feedback);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListRecentAsync(string planSignature, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(planSignature))
        {
            return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());
        }

        if (!_bySignature.TryGetValue(planSignature, out var queue))
        {
            return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());
        }

        var entries = queue
            .OrderByDescending(f => f.RecordedAtUtc)
            .Take(Math.Max(1, limit))
            .ToArray();
        return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(entries);
    }

    /// <inheritdoc />
    public ValueTask<int> ClearAsync(string? planSignature = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(planSignature))
        {
            var total = _bySignature.Values.Sum(q => q.Count);
            _bySignature.Clear();
            return new ValueTask<int>(total);
        }

        if (_bySignature.TryRemove(planSignature, out var removed))
        {
            return new ValueTask<int>(removed.Count);
        }
        return new ValueTask<int>(0);
    }
}
