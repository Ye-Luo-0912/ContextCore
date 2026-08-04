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
/// 幂等（P0-16）：同一 (PlanSignature, IdempotencyKey) 只保留首条——与 Postgres
/// 部分唯一索引 + INSERT ... ON CONFLICT DO NOTHING 语义保持一致。
/// </remarks>
public sealed class InMemoryRetrievalPlanFeedbackStore : IRetrievalPlanFeedbackStore
{
    private readonly ConcurrentDictionary<string, SignatureBucket> _bySignature = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask RecordAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.PlanSignature);

        var bucket = _bySignature.GetOrAdd(feedback.PlanSignature, _ => new SignatureBucket());
        bucket.Add(feedback);
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

        if (!_bySignature.TryGetValue(planSignature, out var bucket))
        {
            return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());
        }

        return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(bucket.Recent(Math.Max(1, limit)));
    }

    /// <inheritdoc />
    public ValueTask<int> ClearAsync(string? planSignature = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(planSignature))
        {
            var total = _bySignature.Values.Sum(b => b.Count);
            _bySignature.Clear();
            return new ValueTask<int>(total);
        }

        if (_bySignature.TryRemove(planSignature, out var removed))
        {
            return new ValueTask<int>(removed.Count);
        }
        return new ValueTask<int>(0);
    }

    /// <summary>单个计划签名下的反馈桶（有序追加 + 幂等键去重，线程安全）。</summary>
    private sealed class SignatureBucket
    {
        private readonly object _gate = new();
        private readonly List<RetrievalPlanFeedback> _entries = new();
        private readonly HashSet<string> _idempotencyKeys = new(StringComparer.Ordinal);

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _entries.Count;
                }
            }
        }

        public void Add(RetrievalPlanFeedback feedback)
        {
            lock (_gate)
            {
                // 幂等去重：提供 IdempotencyKey 时，同一键只保留首条（重放无副作用）。
                if (!string.IsNullOrWhiteSpace(feedback.IdempotencyKey)
                    && !_idempotencyKeys.Add(feedback.IdempotencyKey))
                {
                    return;
                }
                _entries.Add(feedback);
            }
        }

        public IReadOnlyList<RetrievalPlanFeedback> Recent(int limit)
        {
            lock (_gate)
            {
                return _entries
                    .OrderByDescending(f => f.RecordedAtUtc)
                    .Take(limit)
                    .ToArray();
            }
        }
    }
}
