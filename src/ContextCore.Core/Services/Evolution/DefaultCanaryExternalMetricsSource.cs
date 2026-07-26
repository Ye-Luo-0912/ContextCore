using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// 任务 C：默认的外部指标采集源（ICanaryExternalMetricsSource 实现）。
//
// 目标：
//   1. 提供 RegisterTaskResult / RegisterToolResult / RegisterRepairResult /
//      RegisterSafetyEvent / RegisterUserFeedback / RegisterContextEvaluation /
//      RegisterCost 等方法，供外部信号源（Tool 执行结果、用户反馈、安全审计、
//      评估标注集、计费/用量仪表）调用注册结果。
//   2. CollectAsync 按 runId 聚合窗口内的累计计数，计算比率与均值。
//   3. 未采集的指标字段返回 null（CanaryProgressionService 优雅降级跳过回滚检查）。
//
// 设计边界：
//   - 本实现为进程内 in-memory 计数器（Singleton），不跨实例持久化。
//     HA 场景下应替换为 PostgresCanaryMetricsAggregator 的全局聚合视图。
//   - ToolSuccessRate 优先从 IToolDispatchJournal 验证（若注入），但累加器仍由
//     RegisterToolResult 维护（Journal 无 ListAsync 接口，无法跨 runId 批量查询）。
//   - 窗口语义：本实现维护的是"自 Reset 以来的累计计数"，CollectAsync 返回的
//     WindowStart/WindowEnd 反映首条/末条样本的时间戳。
// ===========================================================================

/// <summary>
/// 任务 C：默认的 <see cref="ICanaryExternalMetricsSource"/> 实现。
/// 从内部计数器采集外部结果指标（Tool 成功率、Task 成功率、用户反馈等）。
/// </summary>
/// <remarks>
/// 调用方应在 Tool 执行完成、任务完成、收到用户反馈等时机调用对应的 Register 方法。
/// 未采集的指标字段在 <see cref="CollectAsync"/> 返回 null（CanaryProgressionService 优雅降级）。
/// </remarks>
public sealed class DefaultCanaryExternalMetricsSource : ICanaryExternalMetricsSource
{
    private readonly IToolDispatchJournal? _toolDispatchJournal;
    private readonly ConcurrentDictionary<string, ExternalCounters> _counters
        = new(StringComparer.Ordinal);

    /// <summary>构造默认外部指标采集源。</summary>
    /// <param name="toolDispatchJournal">
    /// 可选的 Tool Dispatch Journal。注入后 RegisterToolResult 会通过 GetEntryAsync
    /// 验证 requestId 已存在（诊断用）；不注入时直接累加内部计数器。
    /// </param>
    public DefaultCanaryExternalMetricsSource(IToolDispatchJournal? toolDispatchJournal = null)
    {
        _toolDispatchJournal = toolDispatchJournal;
    }

    /// <summary>
    /// 注册一次任务结果（用于 TaskSuccessRate 计算）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="succeeded">任务是否成功。</param>
    public void RegisterTaskResult(string runId, bool succeeded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            counters.TaskTotal++;
            if (succeeded) counters.TaskSuccess++;
            counters.Touch();
        }
    }

    /// <summary>
    /// 注册一次 Tool 调用结果（用于 ToolSuccessRate 计算）。
    /// 若注入了 <see cref="IToolDispatchJournal"/>，会通过 GetEntryAsync 验证 requestId
    /// 存在（仅诊断；不影响计数）。未注入或 requestId 不存在时直接累加计数器。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="requestId">Tool 调用 RequestId。</param>
    /// <param name="succeeded">Tool 是否成功。</param>
    public async ValueTask RegisterToolResultAsync(
        string runId,
        string requestId,
        bool succeeded,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        // 若注入 Journal，验证 requestId 已 Prepare（诊断用；不阻塞计数）
        if (_toolDispatchJournal is not null)
        {
            try
            {
                var entry = await _toolDispatchJournal.GetEntryAsync(requestId, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    // requestId 不在 journal 中——仅记录诊断，不抛异常（外部信号源可能晚于 journal 写入）
                    // 生产场景若需严格校验，可在此抛 InvalidOperationException。
                }
            }
            catch
            {
                // Journal 校验失败不阻塞计数（容错）
            }
        }

        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            counters.ToolTotal++;
            if (succeeded) counters.ToolSuccess++;
            counters.Touch();
        }
    }

    /// <summary>
    /// 注册一次修复尝试（用于 RepairRate 计算）。
    /// 仅当 needsRepair=true 时计入分母；repaired=true 计入分子。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="needsRepair">是否需要修复（true 计入分母）。</param>
    /// <param name="repaired">是否修复成功（true 计入分子；needsRepair=false 时忽略）。</param>
    public void RegisterRepairResult(string runId, bool needsRepair, bool repaired)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!needsRepair) return;

        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            counters.RepairTotal++;
            if (repaired) counters.RepairSuccess++;
            counters.Touch();
        }
    }

    /// <summary>
    /// 注册一次安全事件（用于 SafetyViolationRate 计算）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="violation">是否发生安全违规（true 计入分子，false 仅累加分母）。</param>
    public void RegisterSafetyEvent(string runId, bool violation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            counters.SafetyTotal++;
            if (violation) counters.SafetyViolation++;
            counters.Touch();
        }
    }

    /// <summary>
    /// 注册一次用户反馈（用于 UserAcceptance / AnswerQuality 计算）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="accepted">用户是否接受（true 计入 UserAcceptance 分子）。</param>
    /// <param name="qualityScore">用户给出的质量分（0.0-1.0；null 时不计入 AnswerQuality 均值）。</param>
    public void RegisterUserFeedback(string runId, bool accepted, double? qualityScore = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            counters.UserFeedbackTotal++;
            if (accepted) counters.UserFeedbackAccepted++;
            if (qualityScore.HasValue)
            {
                counters.AnswerQualitySum += qualityScore.Value;
                counters.AnswerQualityCount++;
            }
            counters.Touch();
        }
    }

    /// <summary>
    /// 注册一次上下文评估（用于 ContextPrecision / ContextRecallProxy 计算）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="precision">精确率（相关候选 / 总候选；null 时跳过）。</param>
    /// <param name="recallProxy">召回率 proxy（命中 / 应命中；null 时跳过）。</param>
    public void RegisterContextEvaluation(string runId, double? precision = null, double? recallProxy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            if (precision.HasValue)
            {
                counters.ContextPrecisionSum += precision.Value;
                counters.ContextPrecisionCount++;
            }
            if (recallProxy.HasValue)
            {
                counters.ContextRecallSum += recallProxy.Value;
                counters.ContextRecallCount++;
            }
            counters.Touch();
        }
    }

    /// <summary>
    /// 注册一次成本样本（用于 TokenCost / InferenceCost 计算）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="tokenCost">Token 消耗（null 时跳过）。</param>
    /// <param name="inferenceCost">推理费用（美元；null 时跳过）。</param>
    public void RegisterCost(string runId, double? tokenCost = null, double? inferenceCost = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var counters = _counters.GetOrAdd(runId, _ => new ExternalCounters());
        lock (counters)
        {
            if (tokenCost.HasValue)
            {
                counters.TokenCostSum += tokenCost.Value;
                counters.TokenCostCount++;
            }
            if (inferenceCost.HasValue)
            {
                counters.InferenceCostSum += inferenceCost.Value;
                counters.InferenceCostCount++;
            }
            counters.Touch();
        }
    }

    /// <summary>
    /// 重置指定 run 的外部指标计数器（推进到下一档百分比后调用）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    public void Reset(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (_counters.TryGetValue(runId, out var counters))
        {
            lock (counters)
            {
                counters.Reset();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<ExternalResultMetrics> CollectAsync(
        string runId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_counters.TryGetValue(runId, out var counters))
        {
            return new ValueTask<ExternalResultMetrics>(BuildEmpty(windowStart, windowEnd));
        }

        long taskTotal, taskSuccess, toolTotal, toolSuccess, repairTotal, repairSuccess;
        long safetyTotal, safetyViolation, userTotal, userAccepted;
        long answerQualityCount, ctxPrecisionCount, ctxRecallCount, tokenCostCount, inferenceCostCount;
        double answerQualitySum, ctxPrecisionSum, ctxRecallSum, tokenCostSum, inferenceCostSum;
        DateTimeOffset actualWindowStart, actualWindowEnd;

        lock (counters)
        {
            taskTotal = counters.TaskTotal;
            taskSuccess = counters.TaskSuccess;
            toolTotal = counters.ToolTotal;
            toolSuccess = counters.ToolSuccess;
            repairTotal = counters.RepairTotal;
            repairSuccess = counters.RepairSuccess;
            safetyTotal = counters.SafetyTotal;
            safetyViolation = counters.SafetyViolation;
            userTotal = counters.UserFeedbackTotal;
            userAccepted = counters.UserFeedbackAccepted;
            answerQualityCount = counters.AnswerQualityCount;
            answerQualitySum = counters.AnswerQualitySum;
            ctxPrecisionCount = counters.ContextPrecisionCount;
            ctxPrecisionSum = counters.ContextPrecisionSum;
            ctxRecallCount = counters.ContextRecallCount;
            ctxRecallSum = counters.ContextRecallSum;
            tokenCostCount = counters.TokenCostCount;
            tokenCostSum = counters.TokenCostSum;
            inferenceCostCount = counters.InferenceCostCount;
            inferenceCostSum = counters.InferenceCostSum;
            actualWindowStart = counters.WindowStart;
            actualWindowEnd = counters.WindowEnd;
        }

        // 窗口语义：使用计数器内首条/末条样本时间戳（若已采集），否则回退到调用方传入的窗口。
        var effectiveStart = actualWindowStart == DateTimeOffset.MinValue ? windowStart : actualWindowStart;
        var effectiveEnd = actualWindowEnd == DateTimeOffset.MinValue ? windowEnd : actualWindowEnd;

        var sampleCount = (int)Math.Max(Math.Max(taskTotal, toolTotal), Math.Max(safetyTotal, userTotal));

        return new ValueTask<ExternalResultMetrics>(new ExternalResultMetrics
        {
            TaskSuccessRate = taskTotal > 0 ? (double)taskSuccess / taskTotal : null,
            ToolSuccessRate = toolTotal > 0 ? (double)toolSuccess / toolTotal : null,
            RepairRate = repairTotal > 0 ? (double)repairSuccess / repairTotal : null,
            SafetyViolationRate = safetyTotal > 0 ? (double)safetyViolation / safetyTotal : null,
            ContextPrecision = ctxPrecisionCount > 0 ? ctxPrecisionSum / ctxPrecisionCount : null,
            ContextRecallProxy = ctxRecallCount > 0 ? ctxRecallSum / ctxRecallCount : null,
            UserAcceptance = userTotal > 0 ? (double)userAccepted / userTotal : null,
            AnswerQuality = answerQualityCount > 0 ? answerQualitySum / answerQualityCount : null,
            TokenCost = tokenCostCount > 0 ? tokenCostSum / tokenCostCount : null,
            InferenceCost = inferenceCostCount > 0 ? inferenceCostSum / inferenceCostCount : null,
            SampleCount = sampleCount,
            WindowStart = effectiveStart,
            WindowEnd = effectiveEnd
        });
    }

    private static ExternalResultMetrics BuildEmpty(DateTimeOffset windowStart, DateTimeOffset windowEnd)
        => new()
        {
            TaskSuccessRate = null,
            ToolSuccessRate = null,
            RepairRate = null,
            SafetyViolationRate = null,
            ContextPrecision = null,
            ContextRecallProxy = null,
            UserAcceptance = null,
            AnswerQuality = null,
            TokenCost = null,
            InferenceCost = null,
            SampleCount = 0,
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };

    /// <summary>
    /// per-runId 外部指标计数器（含锁保护）。
    /// 所有字段为 long/double 以支持原子读取；lock 保护并发写。
    /// </summary>
    private sealed class ExternalCounters
    {
        // TaskSuccessRate 分子/分母
        public long TaskTotal;
        public long TaskSuccess;

        // ToolSuccessRate 分子/分母
        public long ToolTotal;
        public long ToolSuccess;

        // RepairRate 分子/分母
        public long RepairTotal;
        public long RepairSuccess;

        // SafetyViolationRate 分子/分母
        public long SafetyTotal;
        public long SafetyViolation;

        // UserAcceptance 分子/分母
        public long UserFeedbackTotal;
        public long UserFeedbackAccepted;

        // AnswerQuality 均值（sum + count）
        public double AnswerQualitySum;
        public long AnswerQualityCount;

        // ContextPrecision / ContextRecallProxy 均值
        public double ContextPrecisionSum;
        public long ContextPrecisionCount;
        public double ContextRecallSum;
        public long ContextRecallCount;

        // TokenCost / InferenceCost 均值
        public double TokenCostSum;
        public long TokenCostCount;
        public double InferenceCostSum;
        public long InferenceCostCount;

        public DateTimeOffset WindowStart;
        public DateTimeOffset WindowEnd;

        public void Touch()
        {
            var now = DateTimeOffset.UtcNow;
            if (WindowStart == DateTimeOffset.MinValue)
            {
                WindowStart = now;
            }
            WindowEnd = now;
        }

        public void Reset()
        {
            TaskTotal = 0; TaskSuccess = 0;
            ToolTotal = 0; ToolSuccess = 0;
            RepairTotal = 0; RepairSuccess = 0;
            SafetyTotal = 0; SafetyViolation = 0;
            UserFeedbackTotal = 0; UserFeedbackAccepted = 0;
            AnswerQualitySum = 0.0; AnswerQualityCount = 0;
            ContextPrecisionSum = 0.0; ContextPrecisionCount = 0;
            ContextRecallSum = 0.0; ContextRecallCount = 0;
            TokenCostSum = 0.0; TokenCostCount = 0;
            InferenceCostSum = 0.0; InferenceCostCount = 0;
            WindowStart = DateTimeOffset.MinValue;
            WindowEnd = DateTimeOffset.MinValue;
        }
    }
}
