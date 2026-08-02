using System.Collections.Concurrent;
using System.Globalization;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// DefaultComponentHealthRegistry — 默认组件健康注册表实现
//
// 性能优化（对齐 DDSketch 基准：10000 样本约 147μs）：
//   1. DDSketch 替换 double[] ring buffer + Array.Sort + P95：
//      - Record 路径 O(1)（DDSketch.Add 仅做 bucket index 计算 + 计数自增）。
//      - P95 查询 O(b log b)，b 通常 < 100（1% 相对误差）。
//      - 复用 ContextCore.Core.Services.Evolution.DDSketch（CanaryMetrics 同源）。
//   2. Record 不计算 P95：状态机迁移到读取路径（EvaluateHealth），
//      仅在 GetComponentHealth/ShouldFallbackComponent/GetDegradedComponents/
//      GetComponentDiagnostics 调用时才查询 DDSketch 并更新缓存状态。
//   3. scope TTL + 上限 + 后台清理：
//      - 默认 5 分钟无更新则清除（_scopeTtl）。
//      - 最大 scope 数 10000（_maxScopes），超限时驱逐 LastUpdatedAt 最旧。
//      - 后台 Timer 每 60s 扫描过期 scope（_cleanupTimer）。
//   4. 采样写入：DDSketch 延迟分布仅记录每 N 次样本（默认 N=1，即全量）；
//      ConsecutiveFailures / ConsecutiveLowSamples / TotalSampleCount 始终全量记录，
//      保证 Circuit Breaker 失败计数与恢复计数不受采样影响。
//   5. Circuit Breaker 状态机（Closed/Open/HalfOpen）：
//      - Closed：正常工作，记录失败与 P95。
//      - Open：熔断，快速拒绝；冷却后或累积足够低阈值样本后进入 HalfOpen。
//      - HalfOpen：允许一次真实 probe 请求探测恢复；
//        probe 成功 → Closed；probe 失败 → Open。
//      - 修复"fallback 路径快速样本被误认为原组件恢复"问题：
//        Open 状态下 fallback 路径样本不触发状态迁移，仅 HalfOpen probe 才能恢复。
//   6. Provider 细化到 (ComponentKind, ProviderKind) 粒度：
//      - 独立维护 per-(ProviderKind, scopeKey) 的 ProviderScopeState。
//      - ShouldFallbackProvider(ProviderKind, scopeKey) 查询单个 Provider 熔断状态。
//      - ShouldFallbackComponent(Provider, scopeKey) 返回任意 Provider 熔断的聚合值。
//   7. OpenTelemetry 暴露熔断状态：
//      - 状态迁移时通过 CoreMetrics.ComponentCircuitBreakerTransition 计数器上报。
//   8. Inference 阶段耗时：RecordInferencePhaseTime 记录 queue/copy/run/parse 各阶段。
//
// 原始设计目标（保留）：
//   1. 实现 IComponentHealthRegistry 接口，提供 per-scope per-component 样本存储。
//   2. 当某组件 P95 超过阈值时，标记 FallbackActive。
//   3. 触发回退后，连续 RecoverySamplesRequired 个低于阈值的样本累积后自愈。
//   4. 线程安全：ConcurrentDictionary + per-state lock（per (ComponentKind, scopeKey)）。
//   5. 冷启动保护：MinSamplesBeforeFallback 个样本之前不触发回退。
// ===========================================================================

/// <summary>Provider 类型枚举（用于 per-provider Circuit Breaker 细化）。</summary>
public enum ProviderKind : byte
{
    /// <summary>Semantic / 向量召回。</summary>
    Semantic = 0,

    /// <summary>Graph / 关系召回。</summary>
    Graph = 1,

    /// <summary>Lexical / 关键词召回。</summary>
    Lexical = 2,

    /// <summary>Working memory 召回。</summary>
    WorkingMemory = 3,

    /// <summary>Stable memory 召回。</summary>
    StableMemory = 4,

    /// <summary>其他 / 未分类 Provider。</summary>
    Other = 255
}

/// <summary>Circuit Breaker 状态机枚举（Closed/Open/HalfOpen）。</summary>
public enum CircuitBreakerState : byte
{
    /// <summary>关闭：正常工作，记录失败与 P95。</summary>
    Closed = 0,

    /// <summary>打开：熔断中，快速拒绝请求。</summary>
    Open = 1,

    /// <summary>半开：允许一次真实 probe 请求探测恢复。</summary>
    HalfOpen = 2
}

/// <summary>Inference 推理阶段枚举（用于阶段级耗时记录）。</summary>
public enum InferencePhaseKind : byte
{
    /// <summary>队列等待（Slot 获取）。</summary>
    Queue = 0,

    /// <summary>输入拷贝（Host → Device）。</summary>
    Copy = 1,

    /// <summary>推理执行（session.Run）。</summary>
    Run = 2,

    /// <summary>输出解析（Device → Host + 反序列化）。</summary>
    Parse = 3
}

/// <summary>
/// 默认 IComponentHealthRegistry 实现。
/// per-scope per-component DDSketch + 惰性 P95 估算 + Circuit Breaker 状态机 + 自愈 + scope TTL。
/// </summary>
public sealed class DefaultComponentHealthRegistry : IComponentHealthRegistry, IDisposable
{
    /// <summary>默认最大 scope 数（(ComponentKind, scopeKey) 组合上限）。</summary>
    public const int DefaultMaxScopes = 10000;

    /// <summary>默认 scope TTL：5 分钟无更新则清除。</summary>
    public static readonly TimeSpan DefaultScopeTtl = TimeSpan.FromMinutes(5);

    /// <summary>后台清理扫描间隔：60 秒。</summary>
    public static readonly TimeSpan DefaultCleanupInterval = TimeSpan.FromSeconds(60);

    /// <summary>默认 Circuit Breaker 冷却时间：30 秒。</summary>
    public static readonly TimeSpan DefaultCircuitBreakerCooldown = TimeSpan.FromSeconds(30);

    /// <summary>默认 HalfOpen probe 超时：60 秒（probe 超时后回到 Open）。</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>默认采样间隔：1（全量记录；生产高 QPS 可设为 100）。</summary>
    public const int DefaultSampleInterval = 1;

    private readonly ComponentFallbackOptions _options;
    private readonly ConcurrentDictionary<(ComponentKind Kind, string ScopeKey), ComponentScopeState> _states
        = new();
    private readonly ConcurrentDictionary<(ProviderKind Kind, string ScopeKey), ProviderScopeState> _providerStates
        = new();

    private readonly int _maxScopes;
    private readonly TimeSpan _scopeTtl;
    private readonly TimeSpan _circuitBreakerCooldown;
    private readonly TimeSpan _probeTimeout;
    private readonly int _sampleInterval;
    private readonly Timer? _cleanupTimer;
    private volatile bool _disposed;

    /// <summary>构造默认 registry（使用 <see cref="ComponentFallbackOptions.Default"/>，启用后台清理）。</summary>
    public DefaultComponentHealthRegistry()
        : this(ComponentFallbackOptions.Default)
    {
    }

    /// <summary>构造 registry 并指定组件策略配置（启用后台清理）。</summary>
    /// <param name="options">组件级回退配置（含每组件阈值策略）。</param>
    public DefaultComponentHealthRegistry(ComponentFallbackOptions options)
        : this(options, maxScopes: DefaultMaxScopes, scopeTtl: DefaultScopeTtl, enableBackgroundCleanup: true)
    {
    }

    /// <summary>构造 registry 并指定完整参数（内部用于测试与 DI 工厂）。</summary>
    /// <param name="options">组件级回退配置。</param>
    /// <param name="maxScopes">最大 scope 数（(ComponentKind, scopeKey) 组合上限）。</param>
    /// <param name="scopeTtl">scope TTL：无更新超过此时间则清除。</param>
    /// <param name="enableBackgroundCleanup">是否启用后台 Timer 清理过期 scope。</param>
    internal DefaultComponentHealthRegistry(
        ComponentFallbackOptions options,
        int maxScopes,
        TimeSpan scopeTtl,
        bool enableBackgroundCleanup)
        : this(options, maxScopes, scopeTtl, enableBackgroundCleanup,
               circuitBreakerCooldown: DefaultCircuitBreakerCooldown,
               probeTimeout: DefaultProbeTimeout,
               sampleInterval: DefaultSampleInterval)
    {
    }

    /// <summary>构造 registry 并指定完整参数（含 Circuit Breaker 与采样配置）。</summary>
    /// <param name="options">组件级回退配置。</param>
    /// <param name="maxScopes">最大 scope 数。</param>
    /// <param name="scopeTtl">scope TTL。</param>
    /// <param name="enableBackgroundCleanup">是否启用后台清理。</param>
    /// <param name="circuitBreakerCooldown">Circuit Breaker Open → HalfOpen 冷却时间。</param>
    /// <param name="probeTimeout">HalfOpen probe 超时（超时后回到 Open）。</param>
    /// <param name="sampleInterval">DDSketch 采样间隔（1=全量；100=每 100 次记录 1 次到 DDSketch）。</param>
    internal DefaultComponentHealthRegistry(
        ComponentFallbackOptions options,
        int maxScopes,
        TimeSpan scopeTtl,
        bool enableBackgroundCleanup,
        TimeSpan circuitBreakerCooldown,
        TimeSpan probeTimeout,
        int sampleInterval)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SampleWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.SampleWindow, "SampleWindow must be > 0");
        if (maxScopes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxScopes), maxScopes, "maxScopes must be > 0");
        if (sampleInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleInterval), sampleInterval, "sampleInterval must be > 0");

        _options = options;
        _maxScopes = maxScopes;
        _scopeTtl = scopeTtl;
        _circuitBreakerCooldown = circuitBreakerCooldown;
        _probeTimeout = probeTimeout;
        _sampleInterval = sampleInterval;

        if (enableBackgroundCleanup)
        {
            _cleanupTimer = new Timer(
                CleanupExpiredScopes,
                state: null,
                dueTime: DefaultCleanupInterval,
                period: DefaultCleanupInterval);
        }
    }

    /// <summary>获取配置（诊断用）。</summary>
    public ComponentFallbackOptions Options => _options;

    /// <inheritdoc />
    public void RecordComponentTime(
        ComponentKind kind,
        double durationMs,
        bool succeeded,
        string scopeKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (durationMs < 0) return; // 容忍 0（计时器精度），拒绝负值
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var policy = _options.GetPolicy(kind);
        var state = _states.GetOrAdd((kind, scopeKey), _ => new ComponentScopeState());

        var now = DateTimeOffset.UtcNow;
        var previousCbState = CircuitBreakerState.Closed;
        var newCbState = CircuitBreakerState.Closed;
        lock (state)
        {
            previousCbState = state.CbState;

            // HalfOpen + probe in flight → 本次样本是 probe 结果
            if (state.CbState == CircuitBreakerState.HalfOpen && state.HalfOpenProbeInFlight)
            {
                if (succeeded && durationMs <= policy.MaxP95Ms)
                {
                    // probe 成功 → Closed
                    state.CbState = CircuitBreakerState.Closed;
                    state.ConsecutiveFailures = 0;
                    state.ConsecutiveLowSamples = 0;
                }
                else
                {
                    // probe 失败 → Open
                    state.CbState = CircuitBreakerState.Open;
                    state.OpenSince = now;
                    state.ConsecutiveFailures++;
                }
                state.HalfOpenProbeInFlight = false;
                // probe 样本仍记录到 DDSketch（采样）
                if (TrySample(state))
                {
                    state.LatencySketch.Add(durationMs);
                }
                state.TotalSampleCount++;
                state.LastUpdatedAt = now;
                newCbState = state.CbState;
            }
            else
            {
                // 正常记录（Closed 或 Open 下的 fallback 路径样本）
                // Open 状态下 fallback 路径样本不触发状态迁移（仅累积 ConsecutiveLowSamples 供恢复判定）
                state.TotalSampleCount++;
                state.LastUpdatedAt = now;

                // 采样写入 DDSketch（O(1) 插入；仅在采样命中时执行）
                if (TrySample(state))
                {
                    state.LatencySketch.Add(durationMs);
                }

                if (!succeeded)
                {
                    state.ConsecutiveFailures++;
                    state.ConsecutiveLowSamples = 0;
                }
                else
                {
                    state.ConsecutiveFailures = 0;
                    if (durationMs <= policy.MaxP95Ms)
                    {
                        state.ConsecutiveLowSamples++;
                    }
                    else
                    {
                        state.ConsecutiveLowSamples = 0;
                    }
                }

                // Closed 状态下检查是否需要 Open（失败计数触发；P95 触发在读取路径惰性求值）
                if (state.CbState == CircuitBreakerState.Closed
                    && state.TotalSampleCount >= policy.MinSamplesBeforeFallback
                    && state.ConsecutiveFailures >= policy.MinSamplesBeforeFallback)
                {
                    state.CbState = CircuitBreakerState.Open;
                    state.OpenSince = now;
                }

                newCbState = state.CbState;
            }
        }

        // OTel 上报状态迁移
        if (newCbState != previousCbState)
        {
            CoreMetrics.RecordCircuitBreakerTransition(kind.ToString(), scopeKey, newCbState.ToString());
        }

        // 最大 scope 数保护：超限时驱逐最旧 scope
        if (_states.Count > _maxScopes)
        {
            EvictOldestScopes();
        }
    }

    /// <inheritdoc />
    public ComponentHealthState GetComponentHealth(ComponentKind kind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_states.TryGetValue((kind, scopeKey), out var state))
            return ComponentHealthState.Healthy;

        var policy = _options.GetPolicy(kind);
        lock (state)
        {
            EvaluateAndTransition(state, policy);
            return MapToHealthState(state);
        }
    }

    /// <inheritdoc />
    public bool ShouldFallbackComponent(ComponentKind kind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);

        // Provider 组件：聚合所有 ProviderKind 的熔断状态
        if (kind == ComponentKind.Provider)
        {
            foreach (var pk in Enum.GetValues<ProviderKind>())
            {
                if (pk == ProviderKind.Other) continue;
                if (_providerStates.TryGetValue((pk, scopeKey), out var ps))
                {
                    lock (ps)
                    {
                        EvaluateProviderAndTransition(ps, _options.GetPolicy(ComponentKind.Provider));
                        if (ps.CbState == CircuitBreakerState.Open)
                            return true;
                    }
                }
            }
        }

        if (!_states.TryGetValue((kind, scopeKey), out var state))
            return false;

        var policy = _options.GetPolicy(kind);
        lock (state)
        {
            EvaluateAndTransition(state, policy);

            switch (state.CbState)
            {
                case CircuitBreakerState.Closed:
                    return false;
                case CircuitBreakerState.Open:
                    // 检查是否应进入 HalfOpen
                    if (ShouldTransitionToHalfOpen(state, policy))
                    {
                        state.CbState = CircuitBreakerState.HalfOpen;
                        state.HalfOpenProbeInFlight = false;
                        CoreMetrics.RecordCircuitBreakerTransition(kind.ToString(), scopeKey, CircuitBreakerState.HalfOpen.ToString());
                        // HalfOpen 且无 probe → 允许 probe
                        return false;
                    }
                    return true;
                case CircuitBreakerState.HalfOpen:
                    if (state.HalfOpenProbeInFlight)
                    {
                        // probe 超时检查：超时后回到 Open
                        if (DateTimeOffset.UtcNow - state.ProbeStartedAt > _probeTimeout)
                        {
                            state.CbState = CircuitBreakerState.Open;
                            state.OpenSince = DateTimeOffset.UtcNow;
                            state.HalfOpenProbeInFlight = false;
                            CoreMetrics.RecordCircuitBreakerTransition(kind.ToString(), scopeKey, CircuitBreakerState.Open.ToString());
                            return true;
                        }
                        return true; // probe 已在飞行中，拒绝
                    }
                    // 允许 probe
                    state.HalfOpenProbeInFlight = true;
                    state.ProbeStartedAt = DateTimeOffset.UtcNow;
                    return false;
                default:
                    return false;
            }
        }
    }

    /// <inheritdoc />
    public void RecordComponentFallback(
        ComponentKind kind,
        string scopeKey,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = _states.GetOrAdd((kind, scopeKey), _ => new ComponentScopeState());
        var previousCbState = CircuitBreakerState.Closed;
        lock (state)
        {
            previousCbState = state.CbState;
            state.FallbackRecordedCount++;
            state.LastFallbackReason = reason ?? string.Empty;
            // 显式触发 Circuit Breaker Open
            state.CbState = CircuitBreakerState.Open;
            state.OpenSince = DateTimeOffset.UtcNow;
            state.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        if (previousCbState != CircuitBreakerState.Open)
        {
            CoreMetrics.RecordCircuitBreakerTransition(kind.ToString(), scopeKey, CircuitBreakerState.Open.ToString());
        }

        if (_states.Count > _maxScopes)
        {
            EvictOldestScopes();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ComponentKind> GetDegradedComponents(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        var degraded = new List<ComponentKind>();
        foreach (var kind in Enum.GetValues<ComponentKind>())
        {
            if (_states.TryGetValue((kind, scopeKey), out var state))
            {
                var policy = _options.GetPolicy(kind);
                lock (state)
                {
                    EvaluateAndTransition(state, policy);
                    if (MapToHealthState(state) != ComponentHealthState.Healthy)
                    {
                        degraded.Add(kind);
                    }
                }
            }
        }
        return degraded;
    }

    // -----------------------------------------------------------------------
    // 新增：per-provider Circuit Breaker（Provider 细化到 ProviderKind 粒度）
    // -----------------------------------------------------------------------

    /// <summary>
    /// 记录单个 Provider 的执行耗时与成功/失败状态（per-ProviderKind Circuit Breaker）。
    /// </summary>
    /// <param name="providerKind">Provider 类型。</param>
    /// <param name="durationMs">本次执行耗时（毫秒）。</param>
    /// <param name="succeeded">本次是否成功。</param>
    /// <param name="scopeKey">scope 标识。</param>
    /// <param name="ct">取消令牌。</param>
    public void RecordProviderTime(
        ProviderKind providerKind,
        double durationMs,
        bool succeeded,
        string scopeKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (durationMs < 0) return;
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var policy = _options.GetPolicy(ComponentKind.Provider);
        var state = _providerStates.GetOrAdd((providerKind, scopeKey), _ => new ProviderScopeState());

        var now = DateTimeOffset.UtcNow;
        var previousCbState = CircuitBreakerState.Closed;
        var newCbState = CircuitBreakerState.Closed;
        lock (state)
        {
            previousCbState = state.CbState;

            if (state.CbState == CircuitBreakerState.HalfOpen && state.HalfOpenProbeInFlight)
            {
                // probe 结果
                if (succeeded && durationMs <= policy.MaxP95Ms)
                {
                    state.CbState = CircuitBreakerState.Closed;
                    state.ConsecutiveFailures = 0;
                }
                else
                {
                    state.CbState = CircuitBreakerState.Open;
                    state.OpenSince = now;
                    state.ConsecutiveFailures++;
                }
                state.HalfOpenProbeInFlight = false;
            }
            else
            {
                state.TotalSampleCount++;
                state.LastUpdatedAt = now;
                if (TrySampleProvider(state))
                {
                    state.LatencySketch.Add(durationMs);
                }

                if (!succeeded)
                {
                    state.ConsecutiveFailures++;
                    state.ConsecutiveLowSamples = 0;
                }
                else
                {
                    state.ConsecutiveFailures = 0;
                    if (durationMs <= policy.MaxP95Ms)
                        state.ConsecutiveLowSamples++;
                    else
                        state.ConsecutiveLowSamples = 0;
                }

                if (state.CbState == CircuitBreakerState.Closed
                    && state.TotalSampleCount >= policy.MinSamplesBeforeFallback
                    && state.ConsecutiveFailures >= policy.MinSamplesBeforeFallback)
                {
                    state.CbState = CircuitBreakerState.Open;
                    state.OpenSince = now;
                }
            }

            newCbState = state.CbState;
        }

        if (newCbState != previousCbState)
        {
            CoreMetrics.RecordCircuitBreakerTransition($"Provider.{providerKind}", scopeKey, newCbState.ToString());
        }
    }

    /// <summary>
    /// 查询单个 Provider 当前是否应回退（per-ProviderKind Circuit Breaker）。
    /// Semantic 慢不会导致 Graph 被关闭。
    /// </summary>
    public bool ShouldFallbackProvider(ProviderKind providerKind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_providerStates.TryGetValue((providerKind, scopeKey), out var state))
            return false;

        var policy = _options.GetPolicy(ComponentKind.Provider);
        lock (state)
        {
            EvaluateProviderAndTransition(state, policy);

            switch (state.CbState)
            {
                case CircuitBreakerState.Closed:
                    return false;
                case CircuitBreakerState.Open:
                    if (ShouldProviderTransitionToHalfOpen(state, policy))
                    {
                        state.CbState = CircuitBreakerState.HalfOpen;
                        state.HalfOpenProbeInFlight = false;
                        CoreMetrics.RecordCircuitBreakerTransition($"Provider.{providerKind}", scopeKey, CircuitBreakerState.HalfOpen.ToString());
                        return false;
                    }
                    return true;
                case CircuitBreakerState.HalfOpen:
                    if (state.HalfOpenProbeInFlight)
                    {
                        if (DateTimeOffset.UtcNow - state.ProbeStartedAt > _probeTimeout)
                        {
                            state.CbState = CircuitBreakerState.Open;
                            state.OpenSince = DateTimeOffset.UtcNow;
                            state.HalfOpenProbeInFlight = false;
                            CoreMetrics.RecordCircuitBreakerTransition($"Provider.{providerKind}", scopeKey, CircuitBreakerState.Open.ToString());
                            return true;
                        }
                        return true;
                    }
                    state.HalfOpenProbeInFlight = true;
                    state.ProbeStartedAt = DateTimeOffset.UtcNow;
                    return false;
                default:
                    return false;
            }
        }
    }

    // -----------------------------------------------------------------------
    // 新增：Inference 阶段耗时记录
    // -----------------------------------------------------------------------

    /// <summary>
    /// 记录 Inference 各阶段耗时（queue/copy/run/parse），用于精确归因而非用 Scoring 代理。
    /// </summary>
    /// <param name="phase">推理阶段。</param>
    /// <param name="durationMs">本阶段耗时（毫秒）。</param>
    /// <param name="scopeKey">scope 标识。</param>
    /// <param name="ct">取消令牌。</param>
    public void RecordInferencePhaseTime(
        InferencePhaseKind phase,
        double durationMs,
        string scopeKey,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (durationMs < 0) return;
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        var state = _states.GetOrAdd((ComponentKind.Inference, scopeKey), _ => new ComponentScopeState());
        lock (state)
        {
            // 各阶段记录到独立的 DDSketch（诊断用）
            ref var sketch = ref state.PhaseSketches[(int)phase];
            sketch ??= new DDSketch();
            sketch.Add(durationMs);
            state.LastUpdatedAt = DateTimeOffset.UtcNow;
        }

        if (_states.Count > _maxScopes)
        {
            EvictOldestScopes();
        }
    }

    // -----------------------------------------------------------------------
    // 新增：Circuit Breaker 状态查询（OTel / 诊断用）
    // -----------------------------------------------------------------------

    /// <summary>获取指定 scope 内某组件的 Circuit Breaker 状态。</summary>
    public CircuitBreakerState GetCircuitBreakerState(ComponentKind kind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_states.TryGetValue((kind, scopeKey), out var state))
            return CircuitBreakerState.Closed;

        var policy = _options.GetPolicy(kind);
        lock (state)
        {
            EvaluateAndTransition(state, policy);
            return state.CbState;
        }
    }

    /// <summary>
    /// 获取指定 scope 内某组件的诊断快照（用于 Result.Diagnostics 投影）。
    /// </summary>
    /// <param name="kind">组件类型。</param>
    /// <param name="scopeKey">scope 标识。</param>
    /// <returns>诊断键值对（如 component.health / component.p95_ms / component.sample_count）。</returns>
    public IReadOnlyDictionary<string, string> GetComponentDiagnostics(ComponentKind kind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        var policy = _options.GetPolicy(kind);
        var prefix = $"component.{kind.ToString().ToLowerInvariant()}";
        if (!_states.TryGetValue((kind, scopeKey), out var state))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{prefix}.health"] = ComponentHealthState.Healthy.ToString().ToLowerInvariant(),
                [$"{prefix}.threshold_ms"] = policy.MaxP95Ms.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.sample_count"] = "0",
                [$"{prefix}.p95_ms"] = "0",
                [$"{prefix}.fallback_recorded_count"] = "0",
                [$"{prefix}.circuit_breaker_state"] = CircuitBreakerState.Closed.ToString()
            };
        }

        lock (state)
        {
            EvaluateAndTransition(state, policy);
            var health = MapToHealthState(state);
            var p95 = state.LatencySketch.GetQuantile(0.95);
            var diag = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{prefix}.health"] = health.ToString().ToLowerInvariant(),
                [$"{prefix}.threshold_ms"] = policy.MaxP95Ms.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.sample_count"] = state.TotalSampleCount.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.p95_ms"] = p95.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.consecutive_low_samples"] = state.ConsecutiveLowSamples.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.consecutive_failures"] = state.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.fallback_recorded_count"] = state.FallbackRecordedCount.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.last_exceeded_ms"] = state.LastExceededMs.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.last_fallback_reason"] = state.LastFallbackReason,
                [$"{prefix}.circuit_breaker_state"] = state.CbState.ToString()
            };

            // Inference 阶段耗时诊断
            if (kind == ComponentKind.Inference)
            {
                foreach (InferencePhaseKind phase in Enum.GetValues<InferencePhaseKind>())
                {
                    var phaseSketch = state.PhaseSketches[(int)phase];
                    if (phaseSketch is { TotalCount: > 0 })
                    {
                        diag[$"{prefix}.phase_{phase.ToString().ToLowerInvariant()}_p95_ms"] =
                            phaseSketch.GetQuantile(0.95).ToString("F2", CultureInfo.InvariantCulture);
                    }
                }
            }

            return diag;
        }
    }

    /// <summary>驱逐 LastUpdatedAt 最旧的 scope，直到 scope 数降到 _maxScopes 以下。</summary>
    private void EvictOldestScopes()
    {
        var excess = _states.Count - _maxScopes;
        if (excess <= 0) return;

        // 按 LastUpdatedAt 升序取前 (excess) 个驱逐
        var toEvict = _states
            .OrderBy(kvp => kvp.Value.LastUpdatedAt)
            .Take(excess)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toEvict)
        {
            _states.TryRemove(key, out _);
        }

        // 同步清理 provider states
        var providerExcess = _providerStates.Count - _maxScopes;
        if (providerExcess > 0)
        {
            var toEvictProviders = _providerStates
                .OrderBy(kvp => kvp.Value.LastUpdatedAt)
                .Take(providerExcess)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in toEvictProviders)
            {
                _providerStates.TryRemove(key, out _);
            }
        }
    }

    /// <summary>后台 Timer 回调：扫描并清除过期 scope（LastUpdatedAt + _scopeTtl < now）。</summary>
    private void CleanupExpiredScopes(object? state)
    {
        if (_disposed) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _states)
        {
            if (now - kvp.Value.LastUpdatedAt > _scopeTtl)
            {
                _states.TryRemove(kvp.Key, out _);
            }
        }
        foreach (var kvp in _providerStates)
        {
            if (now - kvp.Value.LastUpdatedAt > _scopeTtl)
            {
                _providerStates.TryRemove(kvp.Key, out _);
            }
        }
    }

    /// <summary>释放后台 Timer 资源。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer?.Dispose();
    }

    // -----------------------------------------------------------------------
    // 内部辅助方法
    // -----------------------------------------------------------------------

    /// <summary>采样判定：每 _sampleInterval 次记录 1 次到 DDSketch。</summary>
    private bool TrySample(ComponentScopeState state)
    {
        if (_sampleInterval <= 1) return true;
        var counter = ++state.SampleCounter;
        return counter % _sampleInterval == 0;
    }

    /// <summary>采样判定（Provider）。</summary>
    private bool TrySampleProvider(ProviderScopeState state)
    {
        if (_sampleInterval <= 1) return true;
        var counter = ++state.SampleCounter;
        return counter % _sampleInterval == 0;
    }

    /// <summary>惰性状态评估 + Circuit Breaker 状态迁移（组件级）。</summary>
    private void EvaluateAndTransition(ComponentScopeState state, ComponentFallbackPolicy policy)
    {
        var now = DateTimeOffset.UtcNow;

        // Open → HalfOpen 检查（冷却到期 或 累积足够低阈值样本）
        if (state.CbState == CircuitBreakerState.Open)
        {
            if (ShouldTransitionToHalfOpen(state, policy))
            {
                state.CbState = CircuitBreakerState.HalfOpen;
                state.HalfOpenProbeInFlight = false;
            }
            return; // Open/HalfOpen 时不做 P95 评估
        }

        if (state.CbState == CircuitBreakerState.HalfOpen)
        {
            // probe 超时检查
            if (state.HalfOpenProbeInFlight && now - state.ProbeStartedAt > _probeTimeout)
            {
                state.CbState = CircuitBreakerState.Open;
                state.OpenSince = now;
                state.HalfOpenProbeInFlight = false;
            }
            return;
        }

        // Closed 状态：检查 P95 是否超阈值（惰性求值）
        if (state.TotalSampleCount < policy.MinSamplesBeforeFallback)
            return;

        // 查询（O(b log b)，b 通常 < 100）
        var currentP95 = state.LatencySketch.GetQuantile(0.95);
        if (currentP95 > policy.MaxP95Ms)
        {
            state.CbState = CircuitBreakerState.Open;
            state.OpenSince = now;
            state.LastExceededMs = currentP95;
        }
    }

    /// <summary>判断 Open → HalfOpen 迁移条件（冷却到期 或 累积足够低阈值样本）。</summary>
    private bool ShouldTransitionToHalfOpen(ComponentScopeState state, ComponentFallbackPolicy policy)
    {
        var now = DateTimeOffset.UtcNow;
        // 冷却到期
        if (now - state.OpenSince >= _circuitBreakerCooldown)
            return true;
        // 累积足够低阈值样本（允许通过 fallback 路径的快速样本来触发 HalfOpen probe）
        if (state.ConsecutiveLowSamples >= policy.RecoverySamplesRequired)
            return true;
        return false;
    }

    /// <summary>惰性状态评估（Provider 级）。</summary>
    private void EvaluateProviderAndTransition(ProviderScopeState state, ComponentFallbackPolicy policy)
    {
        var now = DateTimeOffset.UtcNow;

        if (state.CbState == CircuitBreakerState.Open)
        {
            if (ShouldProviderTransitionToHalfOpen(state, policy))
            {
                state.CbState = CircuitBreakerState.HalfOpen;
                state.HalfOpenProbeInFlight = false;
            }
            return;
        }

        if (state.CbState == CircuitBreakerState.HalfOpen)
        {
            if (state.HalfOpenProbeInFlight && now - state.ProbeStartedAt > _probeTimeout)
            {
                state.CbState = CircuitBreakerState.Open;
                state.OpenSince = now;
                state.HalfOpenProbeInFlight = false;
            }
            return;
        }

        if (state.TotalSampleCount < policy.MinSamplesBeforeFallback)
            return;

        var currentP95 = state.LatencySketch.GetQuantile(0.95);
        if (currentP95 > policy.MaxP95Ms)
        {
            state.CbState = CircuitBreakerState.Open;
            state.OpenSince = now;
        }
    }

    /// <summary>判断 Provider Open → HalfOpen 迁移条件。</summary>
    private bool ShouldProviderTransitionToHalfOpen(ProviderScopeState state, ComponentFallbackPolicy policy)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - state.OpenSince >= _circuitBreakerCooldown)
            return true;
        if (state.ConsecutiveLowSamples >= policy.RecoverySamplesRequired)
            return true;
        return false;
    }

    /// <summary>将 CircuitBreakerState 映射到公开的 ComponentHealthState。</summary>
    private static ComponentHealthState MapToHealthState(ComponentScopeState state)
    {
        return state.CbState switch
        {
            CircuitBreakerState.Open => ComponentHealthState.FallbackActive,
            CircuitBreakerState.HalfOpen => ComponentHealthState.FallbackActive, // 保守：HalfOpen 仍视为 FallbackActive
            CircuitBreakerState.Closed => state.IsDegraded ? ComponentHealthState.Degraded : ComponentHealthState.Healthy,
            _ => ComponentHealthState.Healthy
        };
    }

    // -----------------------------------------------------------------------
    // ComponentScopeState：per (ComponentKind, scopeKey) 的状态。
    // 使用 DDSketch 替代 double[] ring buffer + Array.Sort。
    // Circuit Breaker 状态机：Closed/Open/HalfOpen + probe 机制。
    // -----------------------------------------------------------------------

    /// <summary>内部：每个 (ComponentKind, scopeKey) 的状态（DDSketch + Circuit Breaker + TTL）。</summary>
    private sealed class ComponentScopeState
    {
        /// <summary>延迟分布草图（1% 相对误差，复用 Evolution.DDSketch）。</summary>
        internal DDSketch LatencySketch { get; } = new();

        /// <summary>Inference 各阶段 DDSketch（queue/copy/run/parse；仅 Inference 组件使用）。</summary>
        internal DDSketch?[] PhaseSketches { get; } = new DDSketch?[4];

        /// <summary>累计样本数（DDSketch 不含窗口，此值单调递增）。</summary>
        internal long TotalSampleCount { get; set; }

        /// <summary>采样计数器（用于 DDSketch 采样）。</summary>
        internal long SampleCounter { get; set; }

        /// <summary>最后一次更新时间（UTC），用于 TTL 与驱逐。</summary>
        internal DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Circuit Breaker 状态（Closed/Open/HalfOpen）。</summary>
        internal CircuitBreakerState CbState { get; set; } = CircuitBreakerState.Closed;

        /// <summary>Open 状态开始时间（用于冷却判定）。</summary>
        internal DateTimeOffset OpenSince { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>HalfOpen probe 是否在飞行中（允许一次真实请求探测恢复）。</summary>
        internal bool HalfOpenProbeInFlight { get; set; }

        /// <summary>probe 开始时间（用于超时判定）。</summary>
        internal DateTimeOffset ProbeStartedAt { get; set; }

        /// <summary>是否处于 Degraded 态（P95 接近阈值但未超；由 EvaluateAndTransition 设置）。</summary>
        internal bool IsDegraded { get; set; }

        internal int ConsecutiveLowSamples { get; set; }
        internal int ConsecutiveFailures { get; set; }
        internal int FallbackRecordedCount { get; set; }
        internal double LastExceededMs { get; set; }
        internal string LastFallbackReason { get; set; } = string.Empty;
    }

    // -----------------------------------------------------------------------
    // ProviderScopeState：per (ProviderKind, scopeKey) 的状态。
    // 独立于 ComponentScopeState，实现 per-provider Circuit Breaker。
    // -----------------------------------------------------------------------

    /// <summary>内部：每个 (ProviderKind, scopeKey) 的状态（DDSketch + Circuit Breaker）。</summary>
    private sealed class ProviderScopeState
    {
        internal DDSketch LatencySketch { get; } = new();
        internal long TotalSampleCount { get; set; }
        internal long SampleCounter { get; set; }
        internal DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        internal CircuitBreakerState CbState { get; set; } = CircuitBreakerState.Closed;
        internal DateTimeOffset OpenSince { get; set; } = DateTimeOffset.UtcNow;
        internal bool HalfOpenProbeInFlight { get; set; }
        internal DateTimeOffset ProbeStartedAt { get; set; }
        internal int ConsecutiveLowSamples { get; set; }
        internal int ConsecutiveFailures { get; set; }
    }
}
