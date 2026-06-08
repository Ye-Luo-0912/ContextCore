using System.Diagnostics.Metrics;

namespace ContextCore.Service.Infrastructure;

/// <summary>
/// ContextCore.Service 遥测中心。
/// <para>
/// 同时维护一个内存滚动窗口（最近 <see cref="WindowSize"/> 次请求），
/// 供 <c>GET /api/admin/metrics</c> 快速查询，无需 OTLP 管道即可获取 P50/P95/P99 统计。
/// </para>
/// </summary>
public sealed class ContextCoreMetrics
{
    public const int WindowSize = 2000;

    private static readonly Meter _meter = new("ContextCore.Service", "1.0");

    /// <summary>HTTP API 请求耗时直方图（带 http.method / status_code 标签）。</summary>
    public static readonly Histogram<double> ApiDuration =
        _meter.CreateHistogram<double>(
            "contextcore.api.request.duration",
            unit: "ms",
            description: "HTTP API 请求端到端耗时");

    /// <summary>HTTP API 错误计数（status >= 400）。</summary>
    public static readonly Counter<long> ApiErrors =
        _meter.CreateCounter<long>(
            "contextcore.api.errors",
            unit: "{errors}",
            description: "HTTP API 4xx / 5xx 错误计数");

    // ── In-process rolling window ────────────────────────────────────────────

    private readonly LatencyRing _ring = new(WindowSize);
    private long _totalRequests;
    private long _totalErrors;
    private long _totalErrors4xx;
    private long _totalErrors5xx;

    /// <summary>
    /// 记录一次 HTTP 请求的延迟和状态码，同时推送到 OTel Meter 并更新内存滚动窗口。
    /// </summary>
    public void RecordRequest(double durationMs, int statusCode, string method, string path)
    {
        var methodTag = new KeyValuePair<string, object?>("http.method", method);
        var statusTag = new KeyValuePair<string, object?>("status_code", statusCode.ToString());

        ApiDuration.Record(durationMs, methodTag, statusTag);
        _ring.Add(durationMs);
        Interlocked.Increment(ref _totalRequests);

        if (statusCode >= 400)
        {
            ApiErrors.Add(1, methodTag, statusTag);
            Interlocked.Increment(ref _totalErrors);
            if (statusCode < 500) Interlocked.Increment(ref _totalErrors4xx);
            else Interlocked.Increment(ref _totalErrors5xx);
        }
    }

    /// <summary>返回当前内存滚动窗口快照，包含 P50/P95/P99 与错误率。</summary>
    public MetricsSnapshot GetSnapshot() =>
        new(
            TotalRequests: Interlocked.Read(ref _totalRequests),
            TotalErrors: Interlocked.Read(ref _totalErrors),
            TotalErrors4xx: Interlocked.Read(ref _totalErrors4xx),
            TotalErrors5xx: Interlocked.Read(ref _totalErrors5xx),
            ErrorRate: ComputeErrorRate(),
            WindowSize: WindowSize,
            ApiLatency: _ring.GetStats());

    private double ComputeErrorRate()
    {
        var total = Interlocked.Read(ref _totalRequests);
        var errors = Interlocked.Read(ref _totalErrors);
        return total > 0 ? Math.Round((double)errors / total * 100, 2) : 0;
    }
}

/// <summary>固定大小循环缓冲区，维护最近 N 次延迟样本并按需计算百分位数。</summary>
internal sealed class LatencyRing(int capacity)
{
    private readonly double[] _buf = new double[capacity];
    private int _idx;
    private long _count;
    private readonly object _lock = new();

    public void Add(double value)
    {
        lock (_lock)
        {
            _buf[_idx % _buf.Length] = value;
            _idx++;
            _count++;
        }
    }

    public LatencyStats GetStats()
    {
        double[] samples;
        long count;

        lock (_lock)
        {
            count = _count;
            var filled = (int)Math.Min(count, _buf.Length);
            samples = new double[filled];
            Array.Copy(_buf, samples, filled);
        }

        if (samples.Length == 0)
            return new LatencyStats(0, 0, 0, 0, 0, count);

        Array.Sort(samples);

        return new LatencyStats(
            P50: Pct(samples, 0.50),
            P95: Pct(samples, 0.95),
            P99: Pct(samples, 0.99),
            Min: samples[0],
            Max: samples[^1],
            SampleCount: count);
    }

    private static double Pct(double[] sorted, double p)
    {
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return Math.Round(sorted[Math.Max(0, Math.Min(idx, sorted.Length - 1))], 1);
    }
}

public sealed record LatencyStats(
    double P50, double P95, double P99,
    double Min, double Max, long SampleCount);

public sealed record MetricsSnapshot(
    long TotalRequests,
    long TotalErrors,
    long TotalErrors4xx,
    long TotalErrors5xx,
    double ErrorRate,
    int WindowSize,
    LatencyStats ApiLatency);
