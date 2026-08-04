using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IModelNodeMembershipStore 的 in-memory 实现（P0-15）。
/// </summary>
/// <remarks>
/// 与 PostgresModelNodeMembershipStore 实现同一契约，让 FileSystem / InMemory provider
/// 下 Reconciler 的节点成员租约仍可用（数据在进程重启后丢失——单进程部署无跨节点问题）。
/// 领取/续租通过 ConcurrentDictionary CAS 循环保证原子性；被其他活跃实例持有（租约未过期且
/// instance_id 不同）时返回 null；过期接管生成新令牌（fencing 旧持有者）。
/// </remarks>
public sealed class InMemoryModelNodeMembershipStore : IModelNodeMembershipStore
{
    private readonly ConcurrentDictionary<string, ModelNodeMembership> _memberships = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ModelNodeMembership?> TryAcquireOrRenewLeaseAsync(
        string nodeId,
        string instanceId,
        TimeSpan leaseDuration,
        bool servingEnabled,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + leaseDuration;

        while (true)
        {
            _memberships.TryGetValue(nodeId, out var existing);

            // 被其他活跃实例持有（租约未过期且 instance 不同）→ 拒绝。
            if (existing is not null
                && existing.LeaseExpiresAt > now
                && !string.Equals(existing.InstanceId, instanceId, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<ModelNodeMembership?>(null);
            }

            // 同实例续租保持令牌；过期接管（或首次领取）生成新令牌 fencing 旧持有者。
            var token = existing is not null
                && existing.LeaseExpiresAt > now
                && string.Equals(existing.InstanceId, instanceId, StringComparison.Ordinal)
                ? existing.LeaseToken
                : NewToken();

            var updated = new ModelNodeMembership
            {
                NodeId = nodeId,
                InstanceId = instanceId,
                LeaseToken = token,
                LeaseExpiresAt = expiresAt,
                LastHeartbeat = now,
                ServingEnabled = servingEnabled
            };

            if (existing is null)
            {
                if (_memberships.TryAdd(nodeId, updated))
                {
                    return ValueTask.FromResult<ModelNodeMembership?>(updated);
                }
            }
            else if (_memberships.TryUpdate(nodeId, updated, existing))
            {
                return ValueTask.FromResult<ModelNodeMembership?>(updated);
            }
            // CAS 失败（并发写入）：重试。
        }
    }

    /// <inheritdoc />
    public ValueTask<ModelNodeMembership?> GetAsync(string nodeId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return default;
        }

        _memberships.TryGetValue(nodeId, out var membership);
        return new ValueTask<ModelNodeMembership?>(membership);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelNodeMembership>> GetActiveMembersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var entries = _memberships.Values
            .Where(m => m.LeaseExpiresAt > now)
            .OrderBy(m => m.NodeId, StringComparer.Ordinal)
            .ToArray();
        return new ValueTask<IReadOnlyList<ModelNodeMembership>>(entries);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetServingEnabledAsync(
        string nodeId,
        string instanceId,
        string leaseToken,
        bool servingEnabled,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        while (true)
        {
            if (!_memberships.TryGetValue(nodeId, out var existing))
            {
                return ValueTask.FromResult(false);
            }

            // 校验 lease_token 且租约未过期（fencing：过期持有者不得篡改状态）。
            if (!string.Equals(existing.LeaseToken, leaseToken, StringComparison.Ordinal)
                || existing.LeaseExpiresAt <= DateTimeOffset.UtcNow)
            {
                return ValueTask.FromResult(false);
            }

            var updated = existing with
            {
                ServingEnabled = servingEnabled,
                LastHeartbeat = DateTimeOffset.UtcNow
            };
            if (_memberships.TryUpdate(nodeId, updated, existing))
            {
                return ValueTask.FromResult(true);
            }
            // CAS 失败（并发写入）：重试。
        }
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");
}
