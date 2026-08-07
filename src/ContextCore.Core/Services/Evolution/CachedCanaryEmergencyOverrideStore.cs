using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// Canary Kill Switch（Emergency Override）查询的进程内 TTL 缓存配置。
/// </summary>
/// <remarks>
/// 配置节：<c>CanaryOverrideCache</c>（例如 <c>"CanaryOverrideCache": { "Ttl": "00:00:03" }</c>）。
/// 默认 TTL 为 5 秒：在线请求路径避免每个 Canary 请求访问 Override Store 的 DB 往返，
/// 同时把跨节点 Kill Switch 传播延迟界定在 TTL 内（fail-closed 语义，误判方向永远是回退 V1）。
/// </remarks>
public sealed class CanaryOverrideCacheOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "CanaryOverrideCache";

    /// <summary>缓存条目有效期。</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// <see cref="ICanaryEmergencyOverrideStore"/> 的进程内 TTL 缓存装饰器。
/// </summary>
/// <remarks>
/// 目的：Authoritative Runtime 在决定是否走 V2 时同步查询 Override Store，该查询位于
/// 在线请求路径。本装饰器按 runId 缓存「活跃覆盖」查询结果（含无覆盖的负缓存），
/// 避免每个 Canary 请求增加 DB 往返，同时保持写穿语义：
/// <list type="bullet">
/// <item><c>GetActiveAsync</c>：TTL 内命中直接返回；未命中/过期读真实存储后回填。
/// 只缓存 Positive Override（活跃覆盖），无覆盖结果不缓存——负缓存会放大
/// Kill Switch 的跨节点传播窗口（本节点刚触发覆盖后，其他节点可能在一个 TTL 内
/// 继续命中旧的「无覆盖」缓存而继续走 V2）。正缓存即使陈旧也只朝 V1 方向
/// （fail-closed 安全侧），由 TTL 界定陈旧上限。</item>
/// <item><c>TrySetOverrideAsync</c> / <c>TryClearOverrideAsync</c>：写穿真实存储，
/// 无论返回 true/false 都立即失效该 runId 的本地缓存（false 意味着真相已变化：
/// 已存在覆盖 / 覆盖已清除，缓存可能持有过期方向，必须作废重读）。</item>
/// <item><c>GetActiveOverridesAsync</c>：绕过缓存读全量（运维 / 对账路径，非热路径）。</item>
/// </list>
/// 异常语义：内层存储抛出的异常<b>原样传播</b>——不吞、不以过期缓存充当「无覆盖」应答。
/// fail-safe 由 <c>AuthoritativeRetrievalRuntime</c> / <c>AuthoritativePackageRuntime</c>
/// 承担：存储故障时按「覆盖活跃」处理强制回退 V1 并告警。
/// 跨节点传播：本地写穿立即失效；无覆盖不再缓存 → 新触发覆盖在下一请求即被其他节点感知。
/// 后续如需进一步即时跨节点失效，可在内层实现上叠加
/// PostgreSQL NOTIFY / 版本号失效（装饰器接口保持不变，无需改动路由层）。
/// </remarks>
public sealed class CachedCanaryEmergencyOverrideStore : ICanaryEmergencyOverrideStore
{
    private readonly ICanaryEmergencyOverrideStore _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>初始化 TTL 缓存装饰器。</summary>
    /// <param name="inner">真实 Override Store（Postgres 实现或 InMemory 默认实现）。</param>
    /// <param name="options">缓存选项；null 时使用默认 TTL 5 秒。</param>
    /// <param name="timeProvider">时间源（测试注入用）；null 时使用系统时钟。</param>
    public CachedCanaryEmergencyOverrideStore(
        ICanaryEmergencyOverrideStore inner,
        CanaryOverrideCacheOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = options is not null && options.Ttl > TimeSpan.Zero
            ? options.Ttl
            : new CanaryOverrideCacheOptions().Ttl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<CanaryEmergencyOverride?> GetActiveAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(runId, out var entry) && entry.ValidUntilUtc > now)
        {
            return entry.Override;
        }

        // 未命中 / 过期：读真实存储。异常原样传播（不吞、不以过期数据充当无覆盖应答）。
        var current = await _inner.GetActiveAsync(runId, cancellationToken).ConfigureAwait(false);
        // 只缓存 Positive Override：无覆盖结果不缓存（负缓存会放大 Kill Switch 的
        // 跨节点传播窗口——本节点刚触发覆盖后，其他节点可能在一个 TTL 内继续命中
        // 旧的「无覆盖」缓存而继续走 V2）。正缓存即使陈旧也只朝 V1 方向（fail-closed 安全侧）。
        if (current is not null)
        {
            _cache[runId] = new CacheEntry(current, now + _ttl);
        }
        else
        {
            _cache.TryRemove(runId, out _);
        }
        return current;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CanaryEmergencyOverride>> GetActiveOverridesAsync(
        CancellationToken cancellationToken = default)
        => _inner.GetActiveOverridesAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<bool> TrySetOverrideAsync(
        string runId,
        string reason,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        var applied = await _inner.TrySetOverrideAsync(runId, reason, operatorName, cancellationToken)
            .ConfigureAwait(false);
        // 无论 true/false 都失效：false 意味着已存在活跃覆盖（本地缓存可能持有相反的旧值）。
        _cache.TryRemove(runId, out _);
        return applied;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryClearOverrideAsync(
        string runId,
        string operatorName,
        CancellationToken cancellationToken = default)
    {
        var applied = await _inner.TryClearOverrideAsync(runId, operatorName, cancellationToken)
            .ConfigureAwait(false);
        // 无论 true/false 都失效：false 意味着本无活跃覆盖（本地缓存可能持有过期的覆盖值）。
        _cache.TryRemove(runId, out _);
        return applied;
    }

    /// <summary>进程内缓存条目；<see cref="Override"/> 为 null 表示「无活跃覆盖」负缓存。</summary>
    private sealed record CacheEntry(CanaryEmergencyOverride? Override, DateTimeOffset ValidUntilUtc);
}
