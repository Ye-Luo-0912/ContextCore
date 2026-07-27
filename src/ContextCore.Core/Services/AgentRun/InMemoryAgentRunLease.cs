using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 子问题 9：InMemoryAgentRunLease — 进程内 Agent Run 租约实现（开发/测试用）
//
// 实现 IAgentRunLease 的进程内默认实现，复用 ICanaryLeaderLease 模式：
//   - ConcurrentDictionary 维护 runId → LeaseEntry 映射；
//   - TryAcquireAsync 原子 CAS：未持有或已过期 → 获取成功；
//   - RenewAsync 校验 leaseToken + 未过期 → 延长；
//   - ReleaseAsync 校验 leaseToken → 移除；
//   - ReapExpiredAsync 扫描过期条目并移除。
//
// 设计决策：
//   - 不持久化到磁盘：进程崩溃后租约丢失（多实例下其他实例可立即接管）。
//   - 线程安全：所有读写通过 ConcurrentDictionary 原子操作。
//   - 仅供开发/测试；生产部署应注入基于 Postgres FOR UPDATE SKIP LOCKED 的持久化实现。
// ===========================================================================

/// <summary>
/// 子问题 9：进程内 Agent Run 租约默认实现（开发/测试用）。
/// 确保同一进程内同一时刻仅一个 Host 处理同一 Run。
/// </summary>
/// <remarks>
/// <b>此实现不持久化</b>：进程崩溃后租约丢失。
/// 生产部署应注入基于 DB 的持久化实现（如 <c>PostgresAgentRunLease</c>）。
/// </remarks>
public sealed class InMemoryAgentRunLease : IAgentRunLease
{
    private readonly ConcurrentDictionary<string, LeaseEntry> _leases = new(StringComparer.Ordinal);
    private readonly object _reapLock = new();

    /// <inheritdoc />
    public ValueTask<LeasedAgentRun?> TryAcquireAsync(
        string runId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "租约有效期必须为正。");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + leaseDuration;
        var leaseToken = Guid.NewGuid().ToString("N");

        var acquired = _leases.AddOrUpdate(
            runId,
            // 不存在 → 直接获取
            _ => new LeaseEntry(leaseToken, owner, expiresAt),
            // 已存在 → 检查是否过期
            (_, existing) =>
            {
                if (existing.ExpiresAt > now)
                {
                    // 未过期 → 保留原租约（已被其他实例持有）
                    return existing;
                }
                // 已过期 → 抢占
                return new LeaseEntry(leaseToken, owner, expiresAt);
            });

        // 判断是否成功获取（leaseToken 匹配 = 新获取；不匹配 = 被其他实例持有）
        if (acquired.LeaseToken == leaseToken)
        {
            return ValueTask.FromResult<LeasedAgentRun?>(new LeasedAgentRun
            {
                RunId = runId,
                LeaseToken = acquired.LeaseToken,
                Owner = acquired.Owner,
                ExpiresAt = acquired.ExpiresAt
            });
        }

        return ValueTask.FromResult<LeasedAgentRun?>(null);
    }

    /// <inheritdoc />
    public ValueTask<bool> RenewAsync(
        string runId,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (extension <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(extension), "续约时间必须为正。");
        }

        var now = DateTimeOffset.UtcNow;
        var success = false;

        _leases.AddOrUpdate(
            runId,
            // 不存在 → 创建空条目（后续判断会失败）
            _ => new LeaseEntry(string.Empty, string.Empty, now),
            (_, existing) =>
            {
                if (existing.LeaseToken == leaseToken && existing.ExpiresAt > now)
                {
                    // token 匹配且未过期 → 续约
                    success = true;
                    return existing with { ExpiresAt = now + extension };
                }
                // token 不匹配或已过期 → 不续约
                return existing;
            });

        return ValueTask.FromResult(success);
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(
        string runId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        // 仅当 token 匹配时才移除（避免误释放他人租约）
        if (_leases.TryGetValue(runId, out var existing) && existing.LeaseToken == leaseToken)
        {
            _leases.TryRemove(runId, out _);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var reaped = 0;

        // 单线程回收避免并发冲突
        lock (_reapLock)
        {
            foreach (var kvp in _leases)
            {
                if (kvp.Value.ExpiresAt <= now)
                {
                    if (_leases.TryRemove(kvp.Key, out _))
                    {
                        reaped++;
                    }
                }
            }
        }

        return ValueTask.FromResult(reaped);
    }

    /// <summary>当前持有的租约数量（诊断/监控用）。</summary>
    public int ActiveLeaseCount => _leases.Count;

    /// <summary>租约内部条目。</summary>
    private sealed record LeaseEntry(string LeaseToken, string Owner, DateTimeOffset ExpiresAt);
}
