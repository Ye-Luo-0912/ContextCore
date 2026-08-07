using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// IModelNodeMembershipStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresModelNodeMembershipStore 实现同一契约，让 FileSystem / InMemory provider
/// 下 Reconciler 的节点成员租约仍可用（数据在进程重启后丢失——单进程部署无跨节点问题）。
/// 领取/续租通过 ConcurrentDictionary CAS 循环保证原子性；被其他活跃实例持有（租约未过期且
/// instance_id 不同）时返回 null；过期接管生成新令牌（fencing 旧持有者）。
/// </remarks>
public sealed class InMemoryModelNodeMembershipStore : IModelNodeMembershipStore
{
    private readonly ConcurrentDictionary<(string NodeGroupId, string InstanceId), ModelNodeMembership> _memberships = new();

    // 合并写（SetServingAndAppliedStateAsync）需要的已应用状态存储——DI 注册时注入
    // （InMemory provider 下与 InMemoryModelNodeAppliedStateStore 同驻）；直接构造（测试）可为 null。
    private readonly IModelNodeAppliedStateStore? _appliedStateStore;

    public InMemoryModelNodeMembershipStore(IModelNodeAppliedStateStore? appliedStateStore = null)
    {
        _appliedStateStore = appliedStateStore;
    }

    /// <inheritdoc />
    public ValueTask<ModelNodeMembership?> TryAcquireOrRenewLeaseAsync(
        string nodeGroupId,
        string instanceId,
        TimeSpan leaseDuration,
        bool servingEnabled,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeGroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var key = (nodeGroupId, instanceId);
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + leaseDuration;

        while (true)
        {
            _memberships.TryGetValue(key, out var existing);

            // 每 (NodeGroupId, InstanceId) 一行：同实例续租保持令牌；首次领取/过期后接管生成新令牌。
            var token = existing is not null
                && existing.LeaseExpiresAt > now
                ? existing.LeaseToken
                : NewToken();

            var updated = new ModelNodeMembership
            {
                NodeGroupId = nodeGroupId,
                InstanceId = instanceId,
                LeaseToken = token,
                LeaseExpiresAt = expiresAt,
                LastHeartbeat = now,
                ServingEnabled = servingEnabled
            };

            if (existing is null)
            {
                if (_memberships.TryAdd(key, updated))
                {
                    return ValueTask.FromResult<ModelNodeMembership?>(updated);
                }
            }
            else if (_memberships.TryUpdate(key, updated, existing))
            {
                return ValueTask.FromResult<ModelNodeMembership?>(updated);
            }
            // CAS 失败（并发写入）：重试。
        }
    }

    /// <inheritdoc />
    public ValueTask<ModelNodeMembership?> GetAsync(
        string nodeGroupId,
        string instanceId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nodeGroupId) || string.IsNullOrWhiteSpace(instanceId))
        {
            return default;
        }

        _memberships.TryGetValue((nodeGroupId, instanceId), out var membership);
        return new ValueTask<ModelNodeMembership?>(membership);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ModelNodeMembership>> GetActiveMembersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var entries = _memberships.Values
            .Where(m => m.LeaseExpiresAt > now)
            .OrderBy(m => m.NodeGroupId, StringComparer.Ordinal)
            .ThenBy(m => m.InstanceId, StringComparer.Ordinal)
            .ToArray();
        return new ValueTask<IReadOnlyList<ModelNodeMembership>>(entries);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetServingEnabledAsync(
        string nodeGroupId,
        string instanceId,
        string leaseToken,
        bool servingEnabled,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeGroupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        var key = (nodeGroupId, instanceId);
        while (true)
        {
            if (!_memberships.TryGetValue(key, out var existing))
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
            if (_memberships.TryUpdate(key, updated, existing))
            {
                return ValueTask.FromResult(true);
            }
            // CAS 失败（并发写入）：重试。
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetServingAndAppliedStateAsync(
        string nodeGroupId,
        string instanceId,
        string leaseToken,
        bool servingEnabled,
        ModelNodeAppliedState? appliedState,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 先更新 serving 开关（fencing 校验）——失败则整体返回 false（fail-closed）。
        var servingUpdated = await SetServingEnabledAsync(nodeGroupId, instanceId, leaseToken, servingEnabled, ct).ConfigureAwait(false);
        if (!servingUpdated)
        {
            return false;
        }

        // serving 更新成功后一并写入已应用状态（若提供且已注入 applied state store）。
        if (appliedState is not null && _appliedStateStore is not null)
        {
            await _appliedStateStore.UpsertAsync(appliedState, ct).ConfigureAwait(false);
        }
        return true;
    }

    private static string NewToken() => Guid.NewGuid().ToString("N");
}
