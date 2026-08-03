using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Storage.InMemory.Stores;

/// <summary>
/// ICanaryEmergencyOverrideStore 的 in-memory 实现。
/// </summary>
/// <remarks>
/// 与 PostgresCanaryEmergencyOverrideStore 实现同一契约，让 FileSystem / InMemory provider
/// 下集群级 Kill Switch 仍可用（数据在进程重启后丢失，由运维重新触发）。
/// TrySetOverrideAsync 通过 per-runId 锁保证同一 runId 至多一条活跃覆盖。
/// </remarks>
public sealed class InMemoryCanaryEmergencyOverrideStore : ICanaryEmergencyOverrideStore
{
    private readonly ConcurrentDictionary<string, CanaryEmergencyOverride> _overrides = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<CanaryEmergencyOverride?> GetActiveAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(runId))
        {
            return default;
        }

        _overrides.TryGetValue(runId, out var existing);
        return new ValueTask<CanaryEmergencyOverride?>(
            existing is { ClearedAt: null } ? existing : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var active = _overrides.Values
            .Where(o => o.ClearedAt is null)
            .OrderBy(o => o.CreatedAt)
            .ToArray();
        return new ValueTask<IReadOnlyList<CanaryEmergencyOverride>>(active);
    }

    /// <inheritdoc />
    public ValueTask<bool> TrySetOverrideAsync(string runId, string reason, string operatorName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        var candidate = new CanaryEmergencyOverride
        {
            RunId = runId,
            Reason = reason,
            OperatorName = operatorName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // CAS：仅当不存在活跃覆盖时写入，否则保留原有覆盖（不覆盖、不报错）。
        var set = _overrides.AddOrUpdate(
            runId,
            candidate,
            (_, existing) => existing.ClearedAt is null ? existing : candidate);
        return new ValueTask<bool>(ReferenceEquals(set, candidate));
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClearOverrideAsync(string runId, string operatorName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorName);

        if (!_overrides.TryGetValue(runId, out var existing) || existing.ClearedAt is not null)
        {
            return new ValueTask<bool>(false);
        }

        var cleared = existing with { ClearedAt = DateTimeOffset.UtcNow, ClearedBy = operatorName };
        var replaced = _overrides.TryUpdate(runId, cleared, existing);
        return new ValueTask<bool>(replaced);
    }
}
