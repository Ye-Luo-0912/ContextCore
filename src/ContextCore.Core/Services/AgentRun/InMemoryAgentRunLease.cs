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
    // P0-4：全局 fencing token 计数器（单调递增）。每次成功获取租约（含抢占过期）时递增。
    private long _globalFencingToken;
    // P0-7：可选的 Run Store，用于 MarkFailedIfLeaseExpiredAsync 原子转移（测试/开发用）。
    private readonly IAgentRunStore? _runStore;

    /// <summary>
    /// 初始化 InMemoryAgentRunLease（开发/测试用）。
    /// </summary>
    /// <param name="runStore">P0-7：可选的 Run Store，提供时 MarkFailedIfLeaseExpiredAsync 会执行状态转移；null = 仅检查租约。</param>
    public InMemoryAgentRunLease(IAgentRunStore? runStore = null)
    {
        _runStore = runStore;
    }

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

        // P0-4：预分配 fencing token（仅当实际获取成功时才生效）。
        // 使用 Interlocked.Increment 保证单调递增；即使并发获取失败也不会回退（可接受，fencing token 只需单调）。
        var fencingToken = Interlocked.Increment(ref _globalFencingToken);
        var acquired = false;

        _leases.AddOrUpdate(
            runId,
            // 不存在 → 直接获取
            _ =>
            {
                acquired = true;
                return new LeaseEntry(leaseToken, owner, expiresAt, fencingToken);
            },
            // 已存在 → 检查是否过期
            (_, existing) =>
            {
                if (existing.ExpiresAt > now)
                {
                    // 未过期 → 保留原租约（已被其他实例持有）
                    return existing;
                }
                // 已过期 → 抢占
                acquired = true;
                return new LeaseEntry(leaseToken, owner, expiresAt, fencingToken);
            });

        // 判断是否成功获取（acquired 标志 = 新获取；false = 被其他实例持有）
        if (acquired)
        {
            return ValueTask.FromResult<LeasedAgentRun?>(new LeasedAgentRun
            {
                RunId = runId,
                LeaseToken = leaseToken,
                Owner = owner,
                ExpiresAt = expiresAt,
                FencingToken = fencingToken
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
            _ => new LeaseEntry(string.Empty, string.Empty, now, 0),
            (_, existing) =>
            {
                if (existing.LeaseToken == leaseToken && existing.ExpiresAt > now)
                {
                    // token 匹配且未过期 → 续约（fencing token 不变，lease 连续性保持）
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

    /// <inheritdoc />
    /// <remarks>P0-6：查询指定 Run 是否存在未过期租约（InMemory 实现）。</remarks>
    public ValueTask<bool> HasActiveLeaseAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var now = DateTimeOffset.UtcNow;
        if (_leases.TryGetValue(runId, out var entry))
        {
            return ValueTask.FromResult(entry.ExpiresAt > now);
        }
        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// P0-7：InMemory 实现 — 检查无活跃租约后通过 IAgentRunStore 推进状态机到 Failed（CAS）。
    /// 注入 IAgentRunStore 时执行完整转移；未注入时仅返回"可以标记"信号（1=无活跃租约，0=有活跃租约）。
    /// 进程内单线程场景下无真实竞态，check-then-act 拆分可接受。
    /// </remarks>
    public async ValueTask<int> MarkFailedIfLeaseExpiredAsync(
        string workspaceId,
        string runId,
        AgentRunState expectedCurrentState,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var now = DateTimeOffset.UtcNow;
        if (_leases.TryGetValue(runId, out var entry) && entry.ExpiresAt > now)
        {
            return 0;
        }

        if (_runStore is null)
        {
            return 1;
        }

        try
        {
            await _runStore.TransitionStateAsync(
                workspaceId, runId, expectedCurrentState, AgentRunState.Failed, ct).ConfigureAwait(false);
            return 1;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>当前持有的租约数量（诊断/监控用）。</summary>
    public int ActiveLeaseCount => _leases.Count;

    /// <summary>租约内部条目（P0-4：含 FencingToken 用于副作用校验）。</summary>
    private sealed record LeaseEntry(string LeaseToken, string Owner, DateTimeOffset ExpiresAt, long FencingToken);
}
