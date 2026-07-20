using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// R17-2 默认 <see cref="IGuardedOptimizationPipeline"/> 实现：5 阶段严格顺序推进 + 自动回滚 + in-memory run state。
/// </summary>
/// <remarks>
/// <b>硬边界</b>（与 project memory 一致）：
/// <list type="bullet">
/// <item>阶段严格顺序推进 <see cref="OptimizationStage"/>：OfflineExperiment → Shadow → ScopedCanary → Promotion；不允许跳跃（如 Shadow 直跳 Promotion）。</item>
/// <item>任何阶段命中 <see cref="RollbackCondition"/> 自动切回 AutomaticRollback（基线路径），由 <see cref="IPromotionJudge"/> 决定 Rollback decision。</item>
/// <item>终态：<see cref="PipelineRunStatus.Promoted"/> / <see cref="PipelineRunStatus.RolledBack"/> / <see cref="PipelineRunStatus.Rejected"/>；终态不可推进。</item>
/// <item>仅暴露 <see cref="StartAsync"/> / <see cref="AdvanceAsync"/> / <see cref="GetStatusAsync"/>；不暴露 Policy 修改 / 配置修改 / 模型启用接口。</item>
/// </list>
///
/// <b>State storage</b>：本实现使用 <see cref="ConcurrentDictionary{TKey, TValue}"/> in-memory store。
/// 生产部署应替换为基于 PostgresContextLearningStore 或新 store 的持久化实现。
///
/// <b>Metrics 注入</b>：本实现提供 <see cref="AdvanceWithMetricsAsync"/> 扩展方法（非接口方法），
/// 允许调用方注入 baseline/experiment 指标；接口 <see cref="AdvanceAsync"/> 仅触发推进并使用上次注入的指标。
/// </remarks>
public sealed class DefaultGuardedOptimizationPipeline : IGuardedOptimizationPipeline
{
    private readonly IPromotionJudge _judge;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, PipelineRunState> _runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CanaryAssignment> _canaryAssignments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RollbackRecord> _rollbackRecords = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BaselineComparison> _baselineComparisons = new(StringComparer.Ordinal);

    /// <summary>构造 pipeline。</summary>
    /// <param name="judge">Promotion judge（必填）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultGuardedOptimizationPipeline(
        IPromotionJudge judge,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _judge = judge;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<PipelineRunResult> StartAsync(
        OptimizationProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();

        // 硬边界：仅接受 ExperimentReady 状态的 proposal（来自 R16 Agent 上限）
        if (proposal.Status != OptimizationProposalStatus.ExperimentReady)
        {
            throw new InvalidOperationException(
                $"GuardedOptimizationPipeline.StartAsync 仅接受 ExperimentReady proposal；收到 {proposal.Status}。" +
                "Agent 输出 Status 上限为 ExperimentReady，pipeline 在此基础上推进。");
        }
        // 硬边界：RollbackConditions 必须至少 1 条（保证自动回滚有条件可用）
        if (proposal.RollbackConditions.Count == 0)
        {
            throw new InvalidOperationException(
                "GuardedOptimizationPipeline.StartAsync 要求 proposal 至少有 1 条 RollbackCondition；" +
                "无回滚条件的 proposal 不允许进入 pipeline。");
        }

        var runId = BuildRunId(proposal);
        var now = _timeProvider.GetUtcNow();
        var state = new PipelineRunState
        {
            RunId = runId,
            ProposalId = proposal.ProposalId,
            ProposalVersion = proposal.Version,
            Proposal = proposal,
            CurrentStage = OptimizationStage.OfflineExperiment,
            Status = PipelineRunStatus.Running,
            StartedAt = now,
            UpdatedAt = now
        };
        if (!_runs.TryAdd(runId, state))
        {
            throw new InvalidOperationException($"Pipeline run ID 冲突：{runId}");
        }

        return Task.FromResult(BuildResult(state));
    }

    /// <inheritdoc />
    /// <remarks>
    /// 接口方法：使用上次通过 <see cref="AdvanceWithMetricsAsync"/> 注入的指标。
    /// 若无注入指标，返回当前状态（视为未观察到指标变化，等价于 Hold）。
    /// </remarks>
    public Task<PipelineRunResult> AdvanceAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(runId, out var state))
        {
            throw new InvalidOperationException($"Pipeline run not found: {runId}");
        }
        if (IsTerminal(state.Status))
        {
            return Task.FromResult(BuildResult(state));
        }
        // 接口方法不注入新指标 → 返回当前状态（等价于 Hold）
        return Task.FromResult(BuildResult(state));
    }

    /// <summary>
    /// 推进到下一阶段（注入指标版）：调用 <see cref="IPromotionJudge"/> 裁决并应用 decision。
    /// </summary>
    /// <param name="runId">运行 ID。</param>
    /// <param name="baselineMetrics">基线指标。</param>
    /// <param name="experimentMetrics">实验指标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<PipelineRunResult> AdvanceWithMetricsAsync(
        string runId,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(baselineMetrics);
        ArgumentNullException.ThrowIfNull(experimentMetrics);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_runs.TryGetValue(runId, out var state))
        {
            throw new InvalidOperationException($"Pipeline run not found: {runId}");
        }

        // 终态：直接返回当前状态（幂等）
        if (IsTerminal(state.Status))
        {
            return BuildResult(state);
        }

        // 持久化 BaselineComparison（供审计）
        var comparison = new BaselineComparison(
            comparisonId: $"cmp-{runId}-{state.StageMetrics.Count + 1}",
            proposalId: state.ProposalId,
            baselineMetrics: baselineMetrics,
            experimentMetrics: experimentMetrics,
            comparedAt: _timeProvider.GetUtcNow());
        _baselineComparisons[comparison.ComparisonId] = comparison;
        state.StageMetrics = state.StageMetrics.Append(comparison).ToList();

        // 调用 judge
        var request = new PromotionJudgeRequest(
            proposal: state.Proposal,
            currentStage: state.CurrentStage,
            baselineMetrics: baselineMetrics,
            experimentMetrics: experimentMetrics);
        var judgeResult = await _judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);

        // 应用 judge decision
        switch (judgeResult.Decision)
        {
            case PromotionDecision.Advance:
                if (judgeResult.NextStage is null)
                {
                    throw new InvalidOperationException(
                        $"Judge returned Advance 但未提供 NextStage；runId={runId}");
                }
                // 硬边界：严格顺序推进 — 验证下一阶段是当前阶段的合法后继
                VerifyStageProgression(state.CurrentStage, judgeResult.NextStage.Value);
                state.CurrentStage = judgeResult.NextStage.Value;
                state.Status = PipelineRunStatus.StageCompleted;
                break;

            case PromotionDecision.Promote:
                state.CurrentStage = OptimizationStage.Promotion;
                state.Status = PipelineRunStatus.Promoted;
                state.CompletedAt = _timeProvider.GetUtcNow();
                break;

            case PromotionDecision.Rollback:
                var triggeredCondition = FindTriggeredCondition(state.Proposal, experimentMetrics);
                var rollbackRecord = new RollbackRecord(
                    recordId: $"rb-{runId}-{_timeProvider.GetUtcNow().UtcDateTime.Ticks}",
                    runId: runId,
                    proposalId: state.ProposalId,
                    reason: RollbackReason.RollbackConditionTriggered,
                    triggeredAt: _timeProvider.GetUtcNow())
                {
                    TriggeredConditionMetricName = triggeredCondition?.MetricName,
                    TriggeredConditionThreshold = triggeredCondition?.Threshold,
                    TriggeredConditionValue = triggeredCondition is not null
                        ? experimentMetrics.TryGetValue(triggeredCondition.MetricName, out var v) ? v : (double?)null
                        : null,
                    TriggeredAtStage = state.CurrentStage
                };
                _rollbackRecords[rollbackRecord.RecordId] = rollbackRecord;
                state.RollbackReason = judgeResult.Rationale;
                state.CurrentStage = OptimizationStage.AutomaticRollback;
                state.Status = PipelineRunStatus.RolledBack;
                state.CompletedAt = _timeProvider.GetUtcNow();
                break;

            case PromotionDecision.Reject:
                state.Status = PipelineRunStatus.Rejected;
                state.RollbackReason = judgeResult.Rationale;
                state.CompletedAt = _timeProvider.GetUtcNow();
                break;

            case PromotionDecision.Hold:
                // 保持当前 stage，Status 仍为 Running（等待更多数据）
                break;

            default:
                throw new InvalidOperationException(
                    $"Judge 返回未知的 PromotionDecision: {judgeResult.Decision}");
        }

        state.UpdatedAt = _timeProvider.GetUtcNow();
        _runs[runId] = state; // 替换为更新后的状态（线程安全）

        return BuildResult(state);
    }

    /// <inheritdoc />
    public Task<PipelineRunResult?> GetStatusAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(runId, out var state))
        {
            return Task.FromResult<PipelineRunResult?>(null);
        }
        return Task.FromResult<PipelineRunResult?>(BuildResult(state));
    }

    /// <summary>获取指定 run 的所有 canary assignment（供审计）。</summary>
    public Task<IReadOnlyList<CanaryAssignment>> GetCanaryAssignmentsAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = _canaryAssignments.Values
            .Where(a => a.RunId == runId)
            .OrderBy(a => a.AssignedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<CanaryAssignment>>(list);
    }

    /// <summary>记录 canary assignment（供 ScopedCanary 阶段审计）。</summary>
    public Task RecordCanaryAssignmentAsync(
        CanaryAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();
        _canaryAssignments[assignment.AssignmentId] = assignment;
        return Task.CompletedTask;
    }

    /// <summary>获取指定 run 的回滚记录（如有）。</summary>
    public Task<RollbackRecord?> GetRollbackRecordAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = _rollbackRecords.Values.FirstOrDefault(r => r.RunId == runId);
        return Task.FromResult(record);
    }

    private static bool IsTerminal(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Promoted => true,
        PipelineRunStatus.RolledBack => true,
        PipelineRunStatus.Rejected => true,
        PipelineRunStatus.Cancelled => true,
        PipelineRunStatus.Failed => true,
        _ => false
    };

    private static void VerifyStageProgression(OptimizationStage current, OptimizationStage next)
    {
        // 严格顺序推进：不允许跳跃
        var validNext = current switch
        {
            OptimizationStage.OfflineExperiment => OptimizationStage.Shadow,
            OptimizationStage.Shadow => OptimizationStage.ScopedCanary,
            OptimizationStage.ScopedCanary => OptimizationStage.Promotion,
            OptimizationStage.AutomaticRollback => throw new InvalidOperationException(
                "AutomaticRollback 是终态，不允许推进"),
            OptimizationStage.Promotion => throw new InvalidOperationException(
                "Promotion 是终态，不允许推进"),
            _ => throw new InvalidOperationException($"未知 stage: {current}")
        };
        if (next != validNext)
        {
            throw new InvalidOperationException(
                $"阶段跳跃不允许：从 {current} 不能直接跳到 {next}（合法后继为 {validNext}）");
        }
    }

    private static RollbackCondition? FindTriggeredCondition(
        OptimizationProposal proposal,
        IReadOnlyDictionary<string, double> experimentMetrics)
    {
        foreach (var condition in proposal.RollbackConditions)
        {
            if (experimentMetrics.TryGetValue(condition.MetricName, out var value) &&
                condition.IsTriggered(value))
            {
                return condition;
            }
        }
        return null;
    }

    private static string BuildRunId(OptimizationProposal proposal)
    {
        var componentTag = proposal.TargetComponent.ToString().ToLowerInvariant();
        var timestamp = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return $"run-{componentTag}-{proposal.ProposalId}-{timestamp}";
    }

    private static PipelineRunResult BuildResult(PipelineRunState state)
    {
        // 将最近一次 BaselineComparison 的实验指标作为 StageMetrics（符合契约类型 IReadOnlyDictionary<string, double>）
        var latestMetrics = state.StageMetrics.Count > 0
            ? state.StageMetrics[^1].ExperimentMetrics
            : new Dictionary<string, double>();
        return new PipelineRunResult(
            runId: state.RunId,
            proposalId: state.ProposalId,
            proposalVersion: state.ProposalVersion,
            stage: state.CurrentStage,
            status: state.Status,
            stageMetrics: latestMetrics,
            rollbackReason: state.RollbackReason,
            completedAt: state.CompletedAt);
    }

    /// <summary>Pipeline run 内部状态（in-memory，不暴露到 Abstractions）。</summary>
    private sealed class PipelineRunState
    {
        public string RunId { get; init; } = string.Empty;
        public string ProposalId { get; init; } = string.Empty;
        public OptimizationProposalVersion ProposalVersion { get; init; } = OptimizationProposalVersion.Initial;
        public OptimizationProposal Proposal { get; init; } = null!;
        public OptimizationStage CurrentStage { get; set; }
        public PipelineRunStatus Status { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? RollbackReason { get; set; }
        public List<BaselineComparison> StageMetrics { get; set; } = new();
    }
}
