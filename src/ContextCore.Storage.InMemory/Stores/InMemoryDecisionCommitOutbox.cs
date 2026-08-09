using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>基于内存的 Decision Commit Outbox（决策提交可靠链），适用于测试与单节点。</summary>
public sealed class InMemoryDecisionCommitOutbox : IDecisionCommitOutbox
{
    private readonly Dictionary<long, DecisionCommitOutboxRecord> _records = new();
    private readonly Dictionary<string, long> _byKey = new(StringComparer.Ordinal);
    private long _nextId = 1;
    private readonly object _gate = new();

    /// <summary>尝试达到该次数后转入死信（与 Postgres 实现一致）。</summary>
    private const int MaxRetryCount = 5;

    public ValueTask EnqueueAsync(
        DecisionCommitOutboxRecord commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        cancellationToken.ThrowIfCancellationRequested();

        var key = $"{commit.WorkspaceId}\u001f{commit.DecisionId}";
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existingId))
            {
                // 幂等覆盖为待处理（重放语义）。
                _records[existingId] = commit with
                {
                    OutboxId = existingId,
                    State = 0,
                    LeaseToken = null,
                    LeaseExpiresAt = null
                };
            }
            else
            {
                var id = _nextId++;
                _records[id] = commit with { OutboxId = id, State = 0 };
                _byKey[key] = id;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<DecisionCommitOutboxRecord>> AcquirePendingAsync(
        int limit,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(leaseDuration > TimeSpan.Zero ? leaseDuration : TimeSpan.FromMinutes(5));
        var leaseToken = Guid.NewGuid().ToString("N");
        var results = new List<DecisionCommitOutboxRecord>();

        lock (_gate)
        {
            var pending = _records.Values
                .Where(r => r.State is 0 or 2
                            && (r.LeaseExpiresAt is null || r.LeaseExpiresAt <= now))
                .OrderBy(r => r.CreatedAt)
                .Take(limit)
                .ToList();

            foreach (var record in pending)
            {
                var updated = record with
                {
                    State = 2,
                    Attempts = record.Attempts + 1,
                    LeaseToken = leaseToken,
                    LeaseExpiresAt = leaseUntil
                };
                _records[record.OutboxId] = updated;
                results.Add(updated);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<DecisionCommitOutboxRecord>>(results);
    }

    public ValueTask<bool> AckAsync(
        long outboxId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_records.TryGetValue(outboxId, out var record))
            {
                return ValueTask.FromResult(false);
            }

            if (record.State != 2
                || !string.Equals(record.LeaseToken, leaseToken, StringComparison.Ordinal)
                || record.LeaseExpiresAt is null
                || record.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            {
                return ValueTask.FromResult(false);
            }

            _records[outboxId] = record with
            {
                State = 1,
                LeaseToken = null,
                LeaseExpiresAt = null
            };
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask MarkFailedAsync(
        long outboxId,
        string leaseToken,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_records.TryGetValue(outboxId, out var record))
            {
                return ValueTask.CompletedTask;
            }

            if (record.State != 2
                || !string.Equals(record.LeaseToken, leaseToken, StringComparison.Ordinal)
                || record.LeaseExpiresAt is null
                || record.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            {
                return ValueTask.CompletedTask;
            }

            // attempts 已在领取时递增（与 Postgres 语义一致：领取后 attempts = 本次尝试序号）。
            var attempts = record.Attempts;
            _records[outboxId] = record with
            {
                State = (short)(attempts >= MaxRetryCount ? 3 : 2),
                LastError = errorMessage,
                LeaseToken = null,
                LeaseExpiresAt = null
            };
        }

        return ValueTask.CompletedTask;
    }
}
