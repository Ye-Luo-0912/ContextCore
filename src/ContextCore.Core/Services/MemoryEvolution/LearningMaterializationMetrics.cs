using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// Learning Loop 物化可观测性指标。使用 Interlocked 计数器 + 固定大小延迟环形缓冲区。
/// </summary>
/// <remarks>
/// 指标：
/// <list type="bullet">
/// <item><see cref="RecordMaterializationLag"/>：记录 materialization_lag（事件创建到物化完成的时间差）。</item>
/// <item><see cref="IncrementFailed"/>：递增 failed_events（重试失败次数）。</item>
/// <item><see cref="IncrementDeadLetter"/>：递增 dead_letter_count（死信事件数）。</item>
/// <item><see cref="RecordSuccess"/>：记录 last_success_at（最后成功物化时间）。</item>
/// </list>
/// 线程安全：所有计数器使用 Interlocked 操作，延迟环形缓冲区使用 lock。
/// </remarks>
public sealed class LearningMaterializationMetrics
{
    private const int LatencyWindowSize = 1024;

    private long _pendingEvents;
    private long _processingEvents;
    private long _ackedEvents;
    private long _failedEvents;
    private long _deadLetterCount;
    private long _lastSuccessAtTicks;

    private readonly object _latencyLock = new();
    private readonly double[] _latencyBuffer = new double[LatencyWindowSize];
    private int _latencyIdx;
    private long _latencyCount;

    /// <summary>递增 pending 事件计数。</summary>
    public void IncrementPending() => Interlocked.Increment(ref _pendingEvents);

    /// <summary>递减 pending 事件计数。</summary>
    public void DecrementPending() => Interlocked.Decrement(ref _pendingEvents);

    /// <summary>递增 processing 事件计数。</summary>
    public void IncrementProcessing() => Interlocked.Increment(ref _processingEvents);

    /// <summary>递减 processing 事件计数。</summary>
    public void DecrementProcessing() => Interlocked.Decrement(ref _processingEvents);

    /// <summary>递增 acked 事件计数 + 记录 last_success_at。</summary>
    public void RecordSuccess()
    {
        Interlocked.Increment(ref _ackedEvents);
        Interlocked.Exchange(ref _lastSuccessAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    /// <summary>递增 failed 事件计数。</summary>
    public void IncrementFailed() => Interlocked.Increment(ref _failedEvents);

    /// <summary>递增 dead letter 计数。</summary>
    public void IncrementDeadLetter() => Interlocked.Increment(ref _deadLetterCount);

    /// <summary>记录物化延迟样本（毫秒）。</summary>
    public void RecordMaterializationLag(double lagMs)
    {
        lock (_latencyLock)
        {
            _latencyBuffer[_latencyIdx % _latencyBuffer.Length] = lagMs;
            _latencyIdx++;
            _latencyCount++;
        }
    }

    /// <summary>从 outbox store 的 CountByStateAsync 结果更新 pending/processing 计数。</summary>
    public void UpdateFromStateCounts(IReadOnlyDictionary<string, int> stateCounts)
    {
        long pending = 0, processing = 0, acked = 0, deadLettered = 0;
        foreach (var (state, count) in stateCounts)
        {
            if (string.Equals(state, LearningEventOutboxStates.Pending, StringComparison.OrdinalIgnoreCase))
                pending = count;
            else if (string.Equals(state, LearningEventOutboxStates.Processing, StringComparison.OrdinalIgnoreCase))
                processing = count;
            else if (string.Equals(state, LearningEventOutboxStates.Acked, StringComparison.OrdinalIgnoreCase))
                acked = count;
            else if (string.Equals(state, LearningEventOutboxStates.DeadLettered, StringComparison.OrdinalIgnoreCase))
                deadLettered = count;
        }
        Interlocked.Exchange(ref _pendingEvents, pending);
        Interlocked.Exchange(ref _processingEvents, processing);
        Interlocked.Exchange(ref _ackedEvents, acked);
        Interlocked.Exchange(ref _deadLetterCount, deadLettered);
    }

    /// <summary>从 outbox store 的 GetLastSuccessAtAsync 结果更新 last_success_at。</summary>
    public void UpdateLastSuccessAt(DateTimeOffset? lastSuccessAt)
    {
        if (lastSuccessAt.HasValue)
        {
            Interlocked.Exchange(ref _lastSuccessAtTicks, lastSuccessAt.Value.UtcTicks);
        }
    }

    /// <summary>获取当前指标快照。</summary>
    public LearningMaterializationMetricsSnapshot GetSnapshot()
    {
        double[] samples;
        long sampleCount;
        lock (_latencyLock)
        {
            sampleCount = _latencyCount;
            var filled = (int)Math.Min(sampleCount, _latencyBuffer.Length);
            samples = new double[filled];
            Array.Copy(_latencyBuffer, samples, filled);
        }

        double p50 = 0, p95 = 0, p99 = 0;
        if (samples.Length > 0)
        {
            Array.Sort(samples);
            p50 = Percentile(samples, 0.50);
            p95 = Percentile(samples, 0.95);
            p99 = Percentile(samples, 0.99);
        }

        var lastSuccessTicks = Interlocked.Read(ref _lastSuccessAtTicks);
        var lastSuccessAt = lastSuccessTicks > 0
            ? new DateTimeOffset(lastSuccessTicks, TimeSpan.Zero)
            : (DateTimeOffset?)null;

        return new LearningMaterializationMetricsSnapshot
        {
            LastSuccessAt = lastSuccessAt,
            PendingEvents = Interlocked.Read(ref _pendingEvents),
            FailedEvents = Interlocked.Read(ref _failedEvents),
            DeadLetterCount = Interlocked.Read(ref _deadLetterCount),
            MaterializationLagSampleCount = sampleCount,
            MaterializationLagP50Ms = Math.Round(p50, 2),
            MaterializationLagP95Ms = Math.Round(p95, 2),
            MaterializationLagP99Ms = Math.Round(p99, 2),
            ProcessingEvents = Interlocked.Read(ref _processingEvents),
            AckedEvents = Interlocked.Read(ref _ackedEvents)
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(idx, sorted.Length - 1))];
    }
}
