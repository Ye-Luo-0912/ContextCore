using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.DecisionEngine;

// ===========================================================================
// R29 WP-F-3：DefaultPerformanceMonitor — 默认性能监控实现
//
// 目标（对齐 Workstream F 规格）：
//   1. 实现 IPerformanceMonitor 接口，提供 per-scope ring buffer 样本存储。
//   2. 当最近样本 P95（或最坏样本）超过阈值时，标记该 scope 需要 V2.0 回退。
//   3. 触发回退后，连续 RecoverySamples 个低于阈值的样本累积后自动解除回退状态。
//   4. 线程安全：使用 ConcurrentDictionary + lock-free ring buffer（每个 scope 一个）。
//
// 设计原则：
//   1. 极薄：仅依赖 PerformanceFallbackOptions；不依赖 DDSketch / 外部 metric store。
//      生产可替换为 Prometheus / OpenTelemetry 实现。
//   2. 冷启动保护：MinSamplesBeforeFallback 个样本之前不触发回退（避免单次抖动误判）。
//   3. P95 估算：对窗口内样本排序取 95 分位（窗口容量小，O(n log n) 可接受）。
//   4. 诊断输出：GetDiagnostics 返回 fallback_triggered / threshold_ms / recent_p95_ms /
//      sample_count / consecutive_low_samples 等字段供 Engine 写入 Result.Diagnostics。
// ===========================================================================

/// <summary>
/// R29 WP-F-3：默认 IPerformanceMonitor 实现。
/// per-scope ring buffer + P95 估算 + 自愈（连续低于阈值样本解除回退）。
/// </summary>
public sealed class DefaultPerformanceMonitor : IPerformanceMonitor
{
    private readonly PerformanceFallbackOptions _options;
    private readonly ConcurrentDictionary<string, ScopeState> _scopes = new(StringComparer.Ordinal);

    /// <summary>构造默认 monitor（使用 <see cref="PerformanceFallbackOptions.Default"/>）。</summary>
    public DefaultPerformanceMonitor()
        : this(PerformanceFallbackOptions.Default)
    {
    }

    /// <summary>构造 monitor 并指定阈值配置。</summary>
    public DefaultPerformanceMonitor(PerformanceFallbackOptions options)
    {
        if (options.ThresholdMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.ThresholdMs, "ThresholdMs must be > 0");
        if (options.SampleWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.SampleWindow, "SampleWindow must be > 0");
        if (options.MinSamplesBeforeFallback <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.MinSamplesBeforeFallback, "MinSamplesBeforeFallback must be > 0");
        if (options.RecoverySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.RecoverySamples, "RecoverySamples must be > 0");

        _options = options;
    }

    /// <inheritdoc />
    public PerformanceFallbackOptions Options => _options;

    /// <inheritdoc />
    public void RecordExecutionTime(string scopeKey, double durationMs, bool usedV21Path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (durationMs < 0) return; // 容忍 0（计时器精度），拒绝负值

        var state = _scopes.GetOrAdd(scopeKey, _ => new ScopeState(_options.SampleWindow));
        lock (state)
        {
            state.AddSample(durationMs);

            var exceedsThreshold = durationMs > _options.ThresholdMs;
            if (exceedsThreshold)
            {
                // 触发或保持回退状态：仅当样本数 >= MinSamplesBeforeFallback
                if (state.SampleCount >= _options.MinSamplesBeforeFallback)
                {
                    state.FallbackTriggered = true;
                    state.LastExceededMs = durationMs;
                }
                // 重置恢复计数：超阈值样本打破连续低样本序列
                state.ConsecutiveLowSamples = 0;
            }
            else if (state.FallbackTriggered)
            {
                // 已在回退状态：累积低于阈值样本，达到 RecoverySamples 后解除
                state.ConsecutiveLowSamples++;
                if (state.ConsecutiveLowSamples >= _options.RecoverySamples)
                {
                    state.FallbackTriggered = false;
                    state.LastExceededMs = 0;
                    state.ConsecutiveLowSamples = 0;
                }
            }
        }
    }

    /// <inheritdoc />
    public bool ShouldFallbackToV20(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_scopes.TryGetValue(scopeKey, out var state))
            return false;

        lock (state)
        {
            // 冷启动保护：样本不足时不触发回退
            if (state.SampleCount < _options.MinSamplesBeforeFallback)
                return false;
            return state.FallbackTriggered;
        }
    }

    /// <inheritdoc />
    public void RecordFallback(string scopeKey, string reason, double lastDurationMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_scopes.TryGetValue(scopeKey, out var state)) return;

        lock (state)
        {
            state.FallbackRecordedCount++;
            state.LastFallbackReason = reason ?? string.Empty;
            if (lastDurationMs > 0)
                state.LastExceededMs = lastDurationMs;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetDiagnostics(string scopeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (!_scopes.TryGetValue(scopeKey, out var state))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["performance.fallback_triggered"] = "false",
                ["performance.threshold_ms"] = _options.ThresholdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["performance.sample_count"] = "0",
                ["performance.recent_p95_ms"] = "0",
                ["performance.fallback_recorded_count"] = "0"
            };
        }

        lock (state)
        {
            var p95 = state.ComputeP95();
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["performance.fallback_triggered"] = state.FallbackTriggered.ToString().ToLowerInvariant(),
                ["performance.threshold_ms"] = _options.ThresholdMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["performance.sample_count"] = state.SampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["performance.recent_p95_ms"] = p95.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                ["performance.consecutive_low_samples"] = state.ConsecutiveLowSamples.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["performance.recovery_samples_required"] = _options.RecoverySamples.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["performance.fallback_recorded_count"] = state.FallbackRecordedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["performance.last_exceeded_ms"] = state.LastExceededMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                ["performance.last_fallback_reason"] = state.LastFallbackReason
            };
        }
    }

    /// <summary>内部：每个 scope 的状态（ring buffer + 回退标志）。</summary>
    private sealed class ScopeState
    {
        private readonly double[] _buffer;
        private int _head;
        private int _count;

        internal bool FallbackTriggered { get; set; }
        internal int ConsecutiveLowSamples { get; set; }
        internal int FallbackRecordedCount { get; set; }
        internal double LastExceededMs { get; set; }
        internal string LastFallbackReason { get; set; } = string.Empty;
        internal int SampleCount => _count;

        internal ScopeState(int capacity)
        {
            _buffer = new double[capacity];
            _head = 0;
            _count = 0;
        }

        internal void AddSample(double durationMs)
        {
            _buffer[_head] = durationMs;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        internal double ComputeP95()
        {
            if (_count == 0) return 0;
            var snapshot = new double[_count];
            for (int i = 0; i < _count; i++) snapshot[i] = _buffer[i];
            Array.Sort(snapshot);
            // P95 索引：使用 nearest-rank 方法
            var idx = (int)Math.Ceiling(0.95 * _count) - 1;
            if (idx < 0) idx = 0;
            if (idx >= _count) idx = _count - 1;
            return snapshot[idx];
        }
    }
}
