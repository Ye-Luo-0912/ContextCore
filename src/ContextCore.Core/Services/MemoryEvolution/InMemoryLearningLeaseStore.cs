using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

// ===========================================================================
// InMemoryLearningLeaseStore — ILearningLeaseStore 的进程内实现。
//
// 目标（WP-D：Learning Durability）：
//   1. 实现 Learning Materialization worker 池级租约的进程内版本（开发/测试/单机）。
//   2. 线程安全：ConcurrentDictionary + lease_token CAS（Renew/Release 校验持有者）。
//   3. 语义与 PostgresLearningLeaseStore 对齐：获取（无行或过期可抢占）/ 续约 / 释放 /
//      过期清理 / 活跃查询，便于测试复用。
//
// 与 InMemoryAgentRunLease 设计模式对齐（进程内默认实现；Postgres provider 覆盖）。
// ===========================================================================

/// <summary>
/// <see cref="ILearningLeaseStore"/> 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 适用于测试 / 演示 / 单机开发场景。生产多实例部署需替换为持久化实现。
/// </remarks>
public sealed class InMemoryLearningLeaseStore : ILearningLeaseStore
{
    private readonly ConcurrentDictionary<string, LearningLease> _leases
        = new(StringComparer.Ordinal);

    private static string NormalizeLeaseId(string leaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        return leaseId;
    }

    /// <inheritdoc />
    public ValueTask<LearningLease?> TryAcquireAsync(
        string leaseId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(leaseDuration);
        var token = Guid.NewGuid().ToString("N");
        var candidate = new LearningLease
        {
            LeaseId = leaseId,
            Owner = owner,
            LeaseToken = token,
            AcquiredAt = now,
            ExpiresAt = expiresAt
        };

        var acquired = _leases.AddOrUpdate(
            leaseId,
            candidate,
            (_, existing) => existing.ExpiresAt > now ? existing : candidate);

        // 若 AddOrUpdate 保留了 existing（未过期），则本次获取失败。
        if (!string.Equals(acquired.LeaseToken, token, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<LearningLease?>(null);
        }
        return ValueTask.FromResult<LearningLease?>(acquired);
    }

    /// <inheritdoc />
    public ValueTask<bool> RenewAsync(
        string leaseId,
        string leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "续约时间必须为正。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!_leases.TryGetValue(leaseId, out var current))
        {
            return ValueTask.FromResult(false);
        }

        var now = DateTimeOffset.UtcNow;
        if (!string.Equals(current.LeaseToken, leaseToken, StringComparison.Ordinal)
            || current.ExpiresAt <= now)
        {
            // 非持有者或已过期：拒绝续约（fencing 安全边界）
            return ValueTask.FromResult(false);
        }

        var renewed = current with { ExpiresAt = now.Add(leaseDuration) };
        // CAS：仅当持有者仍是 current（token 未变）时更新；否则返回 false。
        var updated = _leases.TryUpdate(leaseId, renewed, current);
        return ValueTask.FromResult(updated);
    }

    /// <inheritdoc />
    public ValueTask<bool> ReleaseAsync(
        string leaseId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_leases.TryGetValue(leaseId, out var current))
        {
            return ValueTask.FromResult(false);
        }
        if (!string.Equals(current.LeaseToken, leaseToken, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(false);
        }
        return ValueTask.FromResult(_leases.TryRemove(
            new KeyValuePair<string, LearningLease>(leaseId, current)));
    }

    /// <inheritdoc />
    public ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var reaped = 0;
        foreach (var (leaseId, lease) in _leases)
        {
            if (lease.ExpiresAt <= now && _leases.TryRemove(
                new KeyValuePair<string, LearningLease>(leaseId, lease)))
            {
                reaped++;
            }
        }
        return ValueTask.FromResult(reaped);
    }

    /// <inheritdoc />
    public ValueTask<bool> HasActiveLeaseAsync(string leaseId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _leases.TryGetValue(leaseId, out var lease) && lease.ExpiresAt > DateTimeOffset.UtcNow);
    }
}
