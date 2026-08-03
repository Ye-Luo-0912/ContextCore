using System.Globalization;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// DDSketch — 相对误差分位数草图
//
// 用于 Canary 样本的 P95 latency 估计，替代全量排序。
//
// 算法（基于 Cormode et al. "DDSketch: A Fast and Fully-Mergeable
// Quantile Sketch with Relative-Error Guarantees"）：
// 1. 每个非负值 v 映射到 bucket index = ceil(log(v) / log(1 + α))，
// 其中 α 为相对误差（如 0.01 表示 1% 相对误差）。
// 2. 每个 bucket 维护一个计数（落入该 bucket 的样本数）。
// 3. 分位数查询（如 P95）：按 bucket index 升序遍历，累积计数，
// 达到目标 quantile × total_count 时返回该 bucket 的代表值。
// 4. 代表值取 bucket 下界（保守估计；真值落在 [bucket_lower, bucket_lower × (1 + α)]）。
//
// 设计权衡：
// - 使用 Dictionary<int, long> 稀疏存储 bucket（典型延迟分布仅几十个 bucket）。
// - 排序仅在查询时进行（O(b log b)，b 通常 < 100）。
// - 修复：支持 MergeFrom（跨实例 DDSketch 合并）与 Serialize/Deserialize（持久化到 bytea）。
// - 仅支持非负值（延迟 ms 不会为负）。
// - 容量上限：超过 maxBuckets（默认 4096）时拒绝新 bucket，避免极端值炸桶。
// 此时退化精度但仍可查询（极端长尾值会被合并到最末 bucket）。
// ===========================================================================

/// <summary>
/// 相对误差分位数草图（DDSketch）。
/// 用于 Canary 样本的 P95 latency 估计，替代全量排序。
/// </summary>
/// <remarks>
/// 线程安全性：本类型不是线程安全的；调用方需自行加锁（CanaryMetricsCollector 的 ObservationBucket 已加锁）。
/// <para>
/// 修复：支持跨实例合并（<see cref="MergeFrom"/>）与二进制序列化（<see cref="Serialize"/>/<see cref="Deserialize"/>）。
/// 各实例持久化自己的 DDSketch 字节到 canary_metrics_samples.v2_latency_sketch / legacy_latency_sketch bytea 列，
/// Leader 聚合时读取所有实例的 sketch 字节，反序列化后 MergeFrom 合并，再从合并后的 sketch 查询 P95，
/// 替代原来对单实例 P95 做加权平均的错误做法（加权平均会低估尾延迟）。
/// </para>
/// </remarks>
public sealed class DDSketch
{
    private readonly double _logGamma; // log(1 + α)
    private readonly int _maxBuckets;
    private readonly double _relativeAccuracy; // 保留原始 α，用于序列化与合并校验
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
        _relativeAccuracy = relativeAccuracy;
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

    // -----------------------------------------------------------------------
    // 修复：跨实例 DDSketch 合并 + 二进制序列化
    // -----------------------------------------------------------------------

    /// <summary>
    /// 将另一个 DDSketch 的 bucket 计数合并到当前 sketch。
    /// </summary>
    /// <param name="other">被合并的源 sketch（不会被修改）。</param>
    /// <remarks>
    /// 合并语义：相同 bucket index 的计数相加，totalCount / min / max 同步更新。
    /// 要求两个 sketch 的 <c>relativeAccuracy</c> 相同（误差阈值内），否则跳过合并（防御性）。
    /// 合并后的 sketch 可用 <see cref="GetQuantile"/> 查询全局分位数，等价于所有请求的总体 P95。
    /// </remarks>
    public void MergeFrom(DDSketch other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other._totalCount == 0)
        {
            return; // 空 sketch 无贡献
        }
        // 相对误差必须一致（浮点等值比较；构造时已确定，同一进程内的 sketch 总是一致）
        if (Math.Abs(other._relativeAccuracy - _relativeAccuracy) > 1e-15)
        {
            return; // 防御性：不兼容的 relativeAccuracy，跳过合并
        }

        foreach (var (key, count) in other._buckets)
        {
            _buckets[key] = _buckets.TryGetValue(key, out var existing) ? existing + count : count;
        }
        _totalCount += other._totalCount;
        if (other._min < _min) _min = other._min;
        if (other._max > _max) _max = other._max;
    }

    /// <summary>
    /// 将 DDSketch 状态序列化为二进制字节，用于持久化到 bytea 列。
    /// </summary>
    /// <returns>二进制表示；空 sketch（totalCount=0）返回空数组。</returns>
    /// <remarks>
    /// 格式（小端序）：
    /// <code>
    /// [1 byte: format version = 1]
    /// [8 bytes: relativeAccuracy (double)]
    /// [8 bytes: totalCount (long)]
    /// [8 bytes: min (double)]
    /// [8 bytes: max (double)]
    /// [4 bytes: bucket count (int)]
    /// [for each bucket: 4 bytes (int key) + 8 bytes (long count)]
    /// </code>
    /// </remarks>
    public byte[] Serialize()
    {
        // 空 sketch 返回空数组（反序列化时识别为空 sketch）
        if (_totalCount == 0)
        {
            return Array.Empty<byte>();
        }

        using var stream = new System.IO.MemoryStream(1 + 8 * 4 + 4 + _buckets.Count * 12);
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write((byte)1); // format version
        writer.Write(_relativeAccuracy);
        writer.Write(_totalCount);
        writer.Write(_min);
        writer.Write(_max);
        writer.Write(_buckets.Count);
        foreach (var (key, count) in _buckets)
        {
            writer.Write(key);
            writer.Write(count);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// 从二进制字节反序列化 DDSketch。
    /// </summary>
    /// <param name="bytes">序列化字节（由 <see cref="Serialize"/> 产生）。</param>
    /// <returns>反序列化的 DDSketch；null/空数组返回 null（表示无 sketch 数据）。</returns>
    public static DDSketch? Deserialize(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        using var stream = new System.IO.MemoryStream(bytes);
        using var reader = new System.IO.BinaryReader(stream);
        var version = reader.ReadByte();
        if (version != 1)
        {
            return null; // 未知版本，跳过
        }
        var relativeAccuracy = reader.ReadDouble();
        var totalCount = reader.ReadInt64();
        var min = reader.ReadDouble();
        var max = reader.ReadDouble();
        var bucketCount = reader.ReadInt32();

        var sketch = new DDSketch(relativeAccuracy);
        for (var i = 0; i < bucketCount; i++)
        {
            var key = reader.ReadInt32();
            var count = reader.ReadInt64();
            sketch._buckets[key] = count;
        }
        sketch._totalCount = totalCount;
        sketch._min = min;
        sketch._max = max;
        return sketch;
    }
}
