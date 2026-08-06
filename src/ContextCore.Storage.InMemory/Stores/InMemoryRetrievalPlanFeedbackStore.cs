using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IRetrievalPlanFeedbackStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresRetrievalPlanFeedbackStore 实现同一契约，让 FileSystem / InMemory provider
/// 下自适应检索规划器的反馈记录仍可用（数据在进程重启后丢失）。
/// 按 (工作区, 计划签名) 分组保存反馈——工作区为隔离边界：跨工作区的相同签名
/// 不共享反馈；ListRecentAsync 返回按记录时间倒序的最新条目。
/// 幂等（P0-16）：同一 (PlanSignature, IdempotencyKey) 只保留首条——与 Postgres
/// 部分唯一索引 + INSERT ... ON CONFLICT DO NOTHING 语义保持一致。
/// </remarks>
public sealed class InMemoryRetrievalPlanFeedbackStore : IRetrievalPlanFeedbackStore
{
    private const char KeySeparator = '\u001F';

    private readonly ConcurrentDictionary<string, SignatureBucket> _byKey = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask RecordAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.PlanSignature);

        var key = Key(feedback.WorkspaceId ?? string.Empty, feedback.PlanSignature);
        var bucket = _byKey.GetOrAdd(key, _ => new SignatureBucket());
        bucket.Add(feedback);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListRecentAsync(string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(planSignature))
        {
            return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());
        }

        var key = Key(workspaceId ?? string.Empty, planSignature);
        if (!_byKey.TryGetValue(key, out var bucket))
        {
            return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());
        }

        return new ValueTask<IReadOnlyList<RetrievalPlanFeedback>>(bucket.Recent(Math.Max(1, limit)));
    }

    /// <inheritdoc />
    public ValueTask<int> ClearAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 全局重置：清除全部工作区的反馈。
        if (workspaceId is null)
        {
            var total = _byKey.Values.Sum(b => b.Count);
            _byKey.Clear();
            return new ValueTask<int>(total);
        }

        var normalizedWorkspace = workspaceId.Trim();

        // 按签名清除：仅移除该工作区内的该签名。
        if (!string.IsNullOrWhiteSpace(planSignature))
        {
            if (_byKey.TryRemove(Key(normalizedWorkspace, planSignature), out var removed))
            {
                return new ValueTask<int>(removed.Count);
            }
            return new ValueTask<int>(0);
        }

        // 按工作区清除：移除该工作区全部签名桶。
        var prefix = normalizedWorkspace + KeySeparator;
        var cleared = 0;
        foreach (var key in _byKey.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            if (_byKey.TryRemove(key, out var bucket))
            {
                cleared += bucket.Count;
            }
        }
        return new ValueTask<int>(cleared);
    }

    private static string Key(string workspaceId, string planSignature) => workspaceId + KeySeparator + planSignature;

    /// <summary>单个 (工作区, 计划签名) 下的反馈桶（有序追加 + 幂等键去重，线程安全）。</summary>
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
