namespace ContextCore.Core.Services;

/// <summary>
/// 后台工作负载治理预算（BackgroundDrainBudget）：限制"满批续扫"型后台 Worker
/// （Reconciliation / Settlement / Recovery / Relation / Learning / Compaction）单次
/// 突发中的批次数与时长，并在 burst 边界让出 CPU / DB 连接——持续负载下不再
/// 无限追队尾，避免多个 Worker 持续争抢 Postgres Connection / I/O。
/// </summary>
/// <remarks>
/// 优先级（PriorityClass，0 = 最高）：
/// Tool Safety / Reconciliation (0) &gt; Quota Settlement (1) &gt; Agent Recovery (2)
/// &gt; Relation Projection (3) &gt; Learning Materialization (4) &gt; Compaction / Backfill (5)。
/// 当前实现以批次数 + 时长约束 burst；动态降速（DB Pool 利用率 / P95 / Queue Lag /
/// Worker Age）为后续演进点。
/// </remarks>
public sealed class BackgroundDrainBudget
{
    /// <summary>单次 burst 最多连续批次数（达到后让出，下轮再扫）。</summary>
    public int MaxBatchesPerBurst { get; init; } = 8;

    /// <summary>单次 burst 最大时长（达到后让出，防长时独占 DB）。</summary>
    public TimeSpan MaxBurstDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>burst 边界让出延迟（让其他 Worker / 在线请求获得 DB 机会）。</summary>
    public TimeSpan YieldDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>优先级类别（0 = 最高；用于日志 / 指标 / 未来动态降速）。</summary>
    public int PriorityClass { get; init; }

    /// <summary>默认预算（工具级，最高优先级）。</summary>
    public static BackgroundDrainBudget ToolSafety { get; } = new() { PriorityClass = 0 };

    /// <summary>配额结算预算。</summary>
    public static BackgroundDrainBudget QuotaSettlement { get; } = new() { PriorityClass = 1 };

    /// <summary>Agent 恢复预算。</summary>
    public static BackgroundDrainBudget AgentRecovery { get; } = new() { PriorityClass = 2 };

    /// <summary>Relation 投影预算。</summary>
    public static BackgroundDrainBudget RelationProjection { get; } = new() { PriorityClass = 3 };

    /// <summary>Learning 物化预算。</summary>
    public static BackgroundDrainBudget LearningMaterialization { get; } = new() { PriorityClass = 4 };

    /// <summary>压缩 / 回填预算（最低优先级）。</summary>
    public static BackgroundDrainBudget Compaction { get; } = new() { PriorityClass = 5 };

    /// <summary>
    /// burst 内是否允许继续续扫：批次数未达上限且时长未超上限。
    /// 返回 false 时调用方应让出（YieldAsync）再进入下一轮轮询。
    /// </summary>
    /// <param name="batchesThisBurst">本次 burst 已连续处理的批次数。</param>
    /// <param name="burstElapsed">本次 burst 已耗时。</param>
    /// <param name="loadFactor">
    /// 动态负载缩放因子（0.2-1.0；null/越界 = 1.0 静态预算）。
    /// 由 <see cref="IBackgroundLoadProbe"/> 观测（DB 池利用率等）——
    /// 高负载时按比例收紧批次数上限（动态降速，WP-D）。
    /// </param>
    public bool ShouldContinueBurst(int batchesThisBurst, TimeSpan burstElapsed, double? loadFactor = null)
    {
        var factor = loadFactor is > 0 and <= 1 ? loadFactor.Value : 1.0;
        var effectiveBatches = (int)Math.Max(1, Math.Ceiling(MaxBatchesPerBurst * factor));
        return batchesThisBurst < effectiveBatches
               && burstElapsed < MaxBurstDuration;
    }

    /// <summary>
    /// 由 DB 连接池利用率派生负载缩放因子（0.2-1.0）：
    /// 池利用率 0% → 1.0（静态预算）；80% → 0.36（明显收紧）；100% → 0.2（保底）。
    /// </summary>
    public static double ComputeScaleFactor(double dbPoolUtilization)
    {
        var clamped = Math.Clamp(dbPoolUtilization, 0.0, 1.0);
        return Math.Max(0.2, 1.0 - clamped * 0.8);
    }

    /// <summary>burst 边界让出（yield）：短暂让出调度，避免持续独占 DB 连接。</summary>
    public async Task YieldAsync(CancellationToken cancellationToken = default)
    {
        if (YieldDelay > TimeSpan.Zero)
        {
            await Task.Delay(YieldDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}

