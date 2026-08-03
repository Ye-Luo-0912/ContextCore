namespace ContextCore.Service.Hosting;

/// <summary>
/// 后台 worker 指数退避公共计算（连续失败退避，成功间隔复位）。
/// </summary>
/// <remarks>
/// 供轮询型 worker（如 ModelStateReconcilerWorker、AgentRunEventCompactionWorker）
/// 复用同一套退避语义：成功轮询后按 successInterval 等待；连续失败按
/// base × 2^(n-1) 指数增长，封顶 maxDelay，指数在 maxRetryCount 后不再增长。
/// </remarks>
internal static class WorkerBackoff
{
    /// <summary>
    /// 计算退避延迟：连续失败 n 次 → min(base × 2^(n-1), max)；
    /// 指数在 maxRetryCount 后封顶（保持 max，不继续增长）；n ≤ 0 返回成功间隔。
    /// </summary>
    public static TimeSpan Compute(
        TimeSpan successInterval,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        int maxRetryCount,
        int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return successInterval;
        }

        var exponent = Math.Min(consecutiveFailures - 1, Math.Max(0, maxRetryCount - 1));
        var candidateMs = baseDelay.TotalMilliseconds * Math.Pow(2.0, exponent);
        var cappedMs = Math.Min(candidateMs, maxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(cappedMs);
    }
}
