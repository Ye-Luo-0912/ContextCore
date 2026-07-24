using System.Globalization;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// R28-G P1-6：DDSketch — 相对误差分位数草图
//
// 用于 Canary 样本的 P95 latency 估计，替代全量排序。
//
// 算法（基于 Cormode et al. "DDSketch: A Fast and Fully-Mergeable
// Quantile Sketch with Relative-Error Guarantees"）：
//   1. 每个非负值 v 映射到 bucket index = ceil(log(v) / log(1 + α))，
//      其中 α 为相对误差（如 0.01 表示 1% 相对误差）。
//   2. 每个 bucket 维护一个计数（落入该 bucket 的样本数）。
//   3. 分位数查询（如 P95）：按 bucket index 升序遍历，累积计数，
//      达到目标 quantile × total_count 时返回该 bucket 的代表值。
//   4. 代表值取 bucket 下界（保守估计；真值落在 [bucket_lower, bucket_lower × (1 + α)]）。
//
// 设计权衡：
//   - 使用 Dictionary<int, long> 稀疏存储 bucket（典型延迟分布仅几十个 bucket）。
//   - 排序仅在查询时进行（O(b log b)，b 通常 < 100）。
//   - 不合并 sketch（canary 每个 runId 独立）；merge API 未实现。
//   - 仅支持非负值（延迟 ms 不会为负）。
//   - 容量上限：超过 maxBuckets（默认 4096）时拒绝新 bucket，避免极端值炸桶。
//     此时退化精度但仍可查询（极端长尾值会被合并到最末 bucket）。
// ===========================================================================

/// <summary>
/// R28-G P1-6：相对误差分位数草图（DDSketch）。
/// 用于 Canary 样本的 P95 latency 估计，替代全量排序。
/// </summary>
/// <remarks>
/// 线程安全性：本类型不是线程安全的；调用方需自行加锁（CanaryMetricsCollector 的 ObservationBucket 已加锁）。
/// </remarks>
internal sealed class DDSketch
{
    private readonly double _logGamma; // log(1 + α)
    private readonly int _maxBuckets;
    private readonly Dictionary<int, long> _buckets = new();
    private long _totalCount;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;

    /// <summary>构造 DDSketch。</summary>
    /// <param name="relativeAccuracy">相对误差 α（默认 0.01 = 1%）。范围 (0, 1)。</param>
    /// <param name="maxBuckets">bucket 数量上限（默认 4096），防止极端值炸桶。</param>
    public DDSketch(double relativeAccuracy = 0.01, int maxBuckets = 4096)
    {
        if (relativeAccuracy <= 0 || relativeAccuracy >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(relativeAccuracy),
                "relativeAccuracy 必须在 (0, 1) 区间。");
        }
        if (maxBuckets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBuckets), "maxBuckets 必须 > 0。");
        }
        _logGamma = Math.Log(1 + relativeAccuracy);
        _maxBuckets = maxBuckets;
    }

    /// <summary>累计样本数。</summary>
    public long TotalCount => _totalCount;

    /// <summary>最小值（无样本时为 +∞）。</summary>
    public double Min => _totalCount == 0 ? 0.0 : _min;

    /// <summary>最大值（无样本时为 -∞）。</summary>
    public double Max => _totalCount == 0 ? 0.0 : _max;

    /// <summary>添加一个非负值到 sketch。</summary>
    /// <param name="value">非负值（延迟 ms）。</param>
    public void Add(double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            return; // 忽略 NaN/负值
        }
        if (value == 0)
        {
            // 0 值特殊处理：归到 index = int.MinValue（避免 log(0) = -∞）
            const int ZeroBucket = int.MinValue;
            IncrementBucket(ZeroBucket);
            _totalCount++;
            if (0 < _min) _min = 0;
            if (0 > _max) _max = 0;
            return;
        }

        var index = ComputeBucketIndex(value);
        IncrementBucket(index);
        _totalCount++;
        if (value < _min) _min = value;
        if (value > _max) _max = value;
    }

    /// <summary>查询分位数。</summary>
    /// <param name="quantile">分位数 [0, 1]（如 0.95 = P95）。</param>
    /// <returns>估计的分位数值；无样本时返回 0。</returns>
    public double GetQuantile(double quantile)
    {
        if (_totalCount == 0)
        {
            return 0.0;
        }
        if (quantile <= 0)
        {
            return _min;
        }
        if (quantile >= 1)
        {
            return _max;
        }

        var target = (long)Math.Ceiling(quantile * _totalCount);
        if (target <= 0) target = 1;
        if (target > _totalCount) target = _totalCount;

        // 按 bucket index 升序遍历，累积计数到 target
        var orderedKeys = _buckets.Keys.OrderBy(k => k, Comparer<int>.Default).ToList();
        long cumulative = 0;
        foreach (var key in orderedKeys)
        {
            cumulative += _buckets[key];
            if (cumulative >= target)
            {
                return BucketIndexToValue(key);
            }
        }
        return _max; // 防御性 fallback
    }

    /// <summary>重置 sketch（清空所有 bucket 与计数）。</summary>
    public void Reset()
    {
        _buckets.Clear();
        _totalCount = 0;
        _min = double.PositiveInfinity;
        _max = double.NegativeInfinity;
    }

    private int ComputeBucketIndex(double value)
    {
        // index = ceil(log(value) / log(1 + α))
        var rawIndex = Math.Log(value) / _logGamma;
        return (int)Math.Ceiling(rawIndex);
    }

    private double BucketIndexToValue(int index)
    {
        if (index == int.MinValue)
        {
            return 0.0; // zero bucket
        }
        // bucket 下界 = exp(index * log(1 + α))
        return Math.Exp(index * _logGamma);
    }

    private void IncrementBucket(int index)
    {
        // 容量保护：极端值可能产生大量稀疏 bucket；超过上限时归并到最末 bucket。
        if (!_buckets.ContainsKey(index) && _buckets.Count >= _maxBuckets)
        {
            // 找到最大 bucket index，把当前样本归并进去（牺牲精度，保持有界内存）
            var maxKey = _buckets.Keys.Max();
            index = maxKey;
        }
        _buckets[index] = _buckets.TryGetValue(index, out var c) ? c + 1 : 1;
    }
}
