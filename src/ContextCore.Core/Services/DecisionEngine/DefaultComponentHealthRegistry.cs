using System.Collections.Concurrent;
using System.Globalization;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// P5：DefaultComponentHealthRegistry — 默认组件健康注册表实现
//
// 目标（对齐 P5 组件归因规格）：
//   1. 实现 IComponentHealthRegistry 接口，提供 per-scope per-component ring buffer
//      样本存储。每个 (ComponentKind, scopeKey) 维护独立的 ring buffer。
//   2. 当某组件最近样本 P95（或最坏样本）超过该组件阈值时，标记该组件进入
//      FallbackActive 状态。
//   3. 触发回退后，连续 RecoverySamplesRequired 个低于阈值的样本累积后自动解除
//      FallbackActive 状态（自愈）。
//   4. 线程安全：使用 ConcurrentDictionary + per-state lock（每个 (ComponentKind, scopeKey)
//      一个独立锁，避免跨组件争用）。
//
// 设计原则：
//   1. 极薄：仅依赖 ComponentFallbackOptions；不依赖 DDSketch / 外部 metric store。
//      生产可替换为 Prometheus / OpenTelemetry 实现。
//   2. 冷启动保护：MinSamplesBeforeFallback 个样本之前不触发回退（避免单次抖动误判）。
//   3. P95 估算：对窗口内样本排序取 95 分位（窗口容量小，O(n log n) 可接受），
//      与 DefaultPerformanceMonitor 的 P95 计算保持一致。
//   4. 诊断输出：GetDegradedComponents 返回所有非 Healthy 组件，供调用方写入
//      Result.Diagnostics。
//   5. 与 DefaultPerformanceMonitor 共存：两者监控不同维度（整体 vs 组件），
//      互不干扰；调用方可同时查询两者。
// ===========================================================================

/// <summary>
/// P5：默认 IComponentHealthRegistry 实现。
/// per-scope per-component ring buffer + P95 估算 + 自愈（连续低于阈值样本解除回退）。
/// </summary>
public sealed class DefaultComponentHealthRegistry : IComponentHealthRegistry
{
    private readonly ComponentFallbackOptions _options;
    private readonly ConcurrentDictionary<(ComponentKind Kind, string ScopeKey), ComponentScopeState> _states
        = new();

    /// <summary>构造默认 registry（使用 <see cref="ComponentFallbackOptions.Default"/>）。</summary>
    public DefaultComponentHealthRegistry()
        : this(ComponentFallbackOptions.Default)
    {
    }

    /// <summary>构造 registry 并指定组件策略配置。</summary>
    /// <param name="options">组件级回退配置（含每组件阈值策略）。</param>
    public DefaultComponentHealthRegistry(ComponentFallbackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SampleWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.SampleWindow, "SampleWindow must be > 0");

        _options = options;
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

        var policy = _options.GetPolicy(kind);
        var state = _states.GetOrAdd(
            (kind, scopeKey),
            _ => new ComponentScopeState(_options.SampleWindow));

        lock (state)
        {
            state.AddSample(durationMs, succeeded);

            var p95 = state.ComputeP95();
            var exceedsThreshold = p95 > policy.MaxP95Ms;

            if (!succeeded)
            {
                // 单次失败：累积到失败计数；连续失败 >= MinSamplesBeforeFallback 时也触发回退
                state.ConsecutiveFailures++;
                if (state.ConsecutiveFailures >= policy.MinSamplesBeforeFallback
                    && state.SampleCount >= policy.MinSamplesBeforeFallback)
                {
                    state.Health = ComponentHealthState.FallbackActive;
                    state.LastExceededMs = durationMs;
                }
                // 失败样本打破恢复序列
                state.ConsecutiveLowSamples = 0;
            }
            else if (exceedsThreshold)
            {
                // P95 超过阈值：触发或保持回退状态（仅当样本数 >= MinSamplesBeforeFallback）
                state.ConsecutiveFailures = 0;
                if (state.SampleCount >= policy.MinSamplesBeforeFallback)
                {
                    state.Health = ComponentHealthState.FallbackActive;
                    state.LastExceededMs = p95;
                }
                // 超阈值样本打破恢复序列
                state.ConsecutiveLowSamples = 0;
            }
            else if (state.Health == ComponentHealthState.FallbackActive)
            {
                // 已在回退状态：累积低于阈值样本，达到 RecoverySamplesRequired 后解除
                state.ConsecutiveFailures = 0;
                state.ConsecutiveLowSamples++;
                if (state.ConsecutiveLowSamples >= policy.RecoverySamplesRequired)
                {
                    state.Health = ComponentHealthState.Healthy;
                    state.LastExceededMs = 0;
                    state.ConsecutiveLowSamples = 0;
                }
            }
            else
            {
                // 健康或降级态：P95 接近阈值（>80%）时标记 Degraded
                state.ConsecutiveFailures = 0;
                var degradedBoundary = policy.MaxP95Ms * 0.8;
                state.Health = p95 > degradedBoundary
                    ? ComponentHealthState.Degraded
                    : ComponentHealthState.Healthy;
            }
        }
    }

    /// <inheritdoc />
    public ComponentHealthState GetComponentHealth(ComponentKind kind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_states.TryGetValue((kind, scopeKey), out var state))
            return ComponentHealthState.Healthy;

        lock (state)
        {
            return state.Health;
        }
    }

    /// <inheritdoc />
    public bool ShouldFallbackComponent(ComponentKind kind, string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_states.TryGetValue((kind, scopeKey), out var state))
            return false;

        var policy = _options.GetPolicy(kind);
        lock (state)
        {
            // 冷启动保护：样本不足时不触发回退
            if (state.SampleCount < policy.MinSamplesBeforeFallback)
                return false;
            return state.Health == ComponentHealthState.FallbackActive;
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

        var state = _states.GetOrAdd(
            (kind, scopeKey),
            _ => new ComponentScopeState(_options.SampleWindow));

        lock (state)
        {
            state.FallbackRecordedCount++;
            state.LastFallbackReason = reason ?? string.Empty;
            // 显式设置 FallbackActive 状态（即使 P95 未超阈值，调用方也可主动触发）
            state.Health = ComponentHealthState.FallbackActive;
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
                lock (state)
                {
                    if (state.Health != ComponentHealthState.Healthy)
                    {
                        degraded.Add(kind);
                    }
                }
            }
        }
        return degraded;
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
                [$"{prefix}.fallback_recorded_count"] = "0"
            };
        }

        lock (state)
        {
            var p95 = state.ComputeP95();
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [$"{prefix}.health"] = state.Health.ToString().ToLowerInvariant(),
                [$"{prefix}.threshold_ms"] = policy.MaxP95Ms.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.sample_count"] = state.SampleCount.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.p95_ms"] = p95.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.consecutive_low_samples"] = state.ConsecutiveLowSamples.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.consecutive_failures"] = state.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.fallback_recorded_count"] = state.FallbackRecordedCount.ToString(CultureInfo.InvariantCulture),
                [$"{prefix}.last_exceeded_ms"] = state.LastExceededMs.ToString("F2", CultureInfo.InvariantCulture),
                [$"{prefix}.last_fallback_reason"] = state.LastFallbackReason
            };
        }
    }

    /// <summary>内部：每个 (ComponentKind, scopeKey) 的状态（ring buffer + 健康标志）。</summary>
    private sealed class ComponentScopeState
    {
        private readonly double[] _buffer;
        private readonly bool[] _succeeded;
        private int _head;
        private int _count;

        internal ComponentHealthState Health { get; set; }
        internal int ConsecutiveLowSamples { get; set; }
        internal int ConsecutiveFailures { get; set; }
        internal int FallbackRecordedCount { get; set; }
        internal double LastExceededMs { get; set; }
        internal string LastFallbackReason { get; set; } = string.Empty;
        internal int SampleCount => _count;

        internal ComponentScopeState(int capacity)
        {
            _buffer = new double[capacity];
            _succeeded = new bool[capacity];
            _head = 0;
            _count = 0;
            Health = ComponentHealthState.Healthy;
        }

        internal void AddSample(double durationMs, bool succeeded)
        {
            _buffer[_head] = durationMs;
            _succeeded[_head] = succeeded;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        internal double ComputeP95()
        {
            if (_count == 0) return 0;
            var snapshot = new double[_count];
            for (int i = 0; i < _count; i++) snapshot[i] = _buffer[i];
            Array.Sort(snapshot);
            // P95 索引：使用 nearest-rank 方法（与 DefaultPerformanceMonitor 一致）
            var idx = (int)Math.Ceiling(0.95 * _count) - 1;
            if (idx < 0) idx = 0;
            if (idx >= _count) idx = _count - 1;
            return snapshot[idx];
        }
    }
}
