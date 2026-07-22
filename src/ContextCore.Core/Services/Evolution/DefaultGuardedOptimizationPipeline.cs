using ContextCore.Abstractions;

namespace ContextCore.Core.Services.Evolution;

/// <summary>
/// R17-2 默认 <see cref="IGuardedOptimizationPipeline"/> 实现：5 阶段严格顺序推进 + 自动回滚 + 持久化 run state。
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
/// <b>State storage</b>（R27-3 起）：本实现通过注入 <see cref="IPipelineRunStore"/> 持久化 run state + 3 类审计记录。
/// 默认使用 <see cref="InMemoryPipelineRunStore"/>（in-memory）；生产部署应注入 PostgresPipelineRunStore 以支持 HA 场景跨进程恢复。
///
/// <b>Metrics 注入</b>：本实现提供 <see cref="AdvanceWithMetricsAsync"/> 扩展方法（非接口方法），
/// 允许调用方注入 baseline/experiment 指标；接口 <see cref="AdvanceAsync"/> 仅触发推进并使用上次注入的指标。
/// </remarks>
public sealed class DefaultGuardedOptimizationPipeline : IGuardedOptimizationPipeline
{
    private readonly IPromotionJudge _judge;
    private readonly IPipelineRunStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造 pipeline。</summary>
    /// <param name="judge">Promotion judge（必填）。</param>
    /// <param name="store">Pipeline run store（可选，默认 <see cref="InMemoryPipelineRunStore"/>）。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 <see cref="TimeProvider.System"/>）。</param>
    public DefaultGuardedOptimizationPipeline(
        IPromotionJudge judge,
        IPipelineRunStore? store = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _judge = judge;
        _store = store ?? new InMemoryPipelineRunStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<PipelineRunResult> StartAsync(
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
        var snapshot = new PipelineRunSnapshot
        {
            RunId = runId,
            ProposalId = proposal.ProposalId,
            ProposalVersion = proposal.Version,
            Proposal = proposal,
            CurrentStage = OptimizationStage.OfflineExperiment,
            Status = PipelineRunStatus.Running,
            StartedAt = now,
            UpdatedAt = now,
            CompletedAt = null,
            RollbackReason = null,
            StageMetrics = Array.Empty<BaselineComparison>(),
            // P0-7：初始 revision = 1
            // P2-1：StartAsync 使用 TryCreateRunAsync（insert-if-absent）替代 SaveRunAsync（覆盖）
            Revision = 1,
            LeaseOwner = null,
            LeaseExpiresAt = null,
            LastTransitionId = null
        };
        // P2-1：TryCreateRunAsync 保证 RunId 碰撞时返回 false 而非覆盖。
        // GUID RunId 碰撞概率极低；若发生，抛异常通知调用方。
        var created = await _store.TryCreateRunAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException(
                $"Pipeline run 创建失败：RunId={runId} 已存在。" +
                "GUID RunId 碰撞概率极低；若反复出现请检查 RunId 生成逻辑。");
        }

        return BuildResult(snapshot);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 接口方法：使用上次通过 <see cref="AdvanceWithMetricsAsync"/> 注入的指标。
    /// 若无注入指标，返回当前状态（视为未观察到指标变化，等价于 Hold）。
    /// </remarks>
    public async Task<PipelineRunResult> AdvanceAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new InvalidOperationException($"Pipeline run not found: {runId}");
        }
        if (IsTerminal(snapshot.Status))
        {
            return BuildResult(snapshot);
        }
        // 接口方法不注入新指标 → 返回当前状态（等价于 Hold）
        return BuildResult(snapshot);
    }

    /// <summary>
    /// 推进到下一阶段（注入指标版）：调用 <see cref="IPromotionJudge"/> 裁决并应用 decision。
    /// 此重载自动生成 transitionId（非幂等；重试时生成新 ID）。
    /// </summary>
    /// <param name="runId">运行 ID。</param>
    /// <param name="baselineMetrics">基线指标。</param>
    /// <param name="experimentMetrics">实验指标。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// P2-2：需要端到端幂等的调用方应使用接受 <see cref="PipelineTransitionRequest"/> 的重载，
    /// 在重试时复用同一 TransitionId，让 store 的幂等检查识别重放。
    /// </remarks>
    public Task<PipelineRunResult> AdvanceWithMetricsAsync(
        string runId,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        CancellationToken cancellationToken = default)
    {
        // P2-2：自动生成 transitionId（非幂等场景；向后兼容）
        var transitionId = $"t-{runId}-{Guid.NewGuid():N}";
        return AdvanceWithMetricsAsync(
            runId, baselineMetrics, experimentMetrics,
            new PipelineTransitionRequest { TransitionId = transitionId },
            cancellationToken);
    }

    /// <summary>
    /// 推进到下一阶段（注入指标版 + 调用方提供 transition 标识）：调用 <see cref="IPromotionJudge"/> 裁决并应用 decision。
    /// </summary>
    /// <param name="runId">运行 ID。</param>
    /// <param name="baselineMetrics">基线指标。</param>
    /// <param name="experimentMetrics">实验指标。</param>
    /// <param name="transition">P2-2：调用方提供的 transition 标识（含 TransitionId / ObservationBatchId / IdempotencyKey）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// P0-7：使用 <see cref="IPipelineRunStore.TryTransitionAsync"/> 原子 CAS 推进。
    /// snapshot 更新 + audit 批量（BaselineComparison / CanaryAssignment / RollbackRecord）在同一事务内提交。
    /// 若 CAS 失败（并发推进冲突），抛 <see cref="InvalidOperationException"/> 通知调用方。
    /// <para>
    /// P2-2：调用方提供 <paramref name="transition"/>.TransitionId 实现端到端幂等。
    /// 若响应丢失后重试，使用相同 TransitionId，store 通过 LastTransitionId 幂等检查返回当前快照。
    /// </para>
    /// <para>
    /// P2-3：当 judge 决定推进到 <see cref="OptimizationStage.ScopedCanary"/> 时，
    /// 自动生成 <see cref="CanaryAssignment"/> 并作为 transition audit 一部分原子提交，
    /// 不再需要调用方通过 <see cref="RecordCanaryAssignmentAsync"/> 独立写入。
    /// </para>
    /// </remarks>
    public async Task<PipelineRunResult> AdvanceWithMetricsAsync(
        string runId,
        IReadOnlyDictionary<string, double> baselineMetrics,
        IReadOnlyDictionary<string, double> experimentMetrics,
        PipelineTransitionRequest transition,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(baselineMetrics);
        ArgumentNullException.ThrowIfNull(experimentMetrics);
        ArgumentNullException.ThrowIfNull(transition);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            throw new InvalidOperationException($"Pipeline run not found: {runId}");
        }

        // 终态：直接返回当前状态（幂等）
        if (IsTerminal(snapshot.Status))
        {
            return BuildResult(snapshot);
        }

        // 构造 BaselineComparison（供审计，在 CAS 事务内写入）
        var comparison = new BaselineComparison(
            comparisonId: $"cmp-{runId}-{snapshot.StageMetrics.Count + 1}",
            proposalId: snapshot.ProposalId,
            baselineMetrics: baselineMetrics,
            experimentMetrics: experimentMetrics,
            comparedAt: _timeProvider.GetUtcNow());
        var updatedMetrics = snapshot.StageMetrics.Append(comparison).ToList();

        // 调用 judge
        var request = new PromotionJudgeRequest(
            proposal: snapshot.Proposal,
            currentStage: snapshot.CurrentStage,
            baselineMetrics: baselineMetrics,
            experimentMetrics: experimentMetrics);
        var judgeResult = await _judge.JudgeAsync(request, cancellationToken).ConfigureAwait(false);

        // 应用 judge decision → 生成 next snapshot（P0-7：Revision +1 + LastTransitionId）
        // P2-2：使用调用方提供的 transitionId
        var now = _timeProvider.GetUtcNow();
        var transitionId = transition.TransitionId;
        PipelineRunSnapshot nextSnapshot;
        RollbackRecord? rollbackRecord = null;
        CanaryAssignment? canaryAssignment = null;
        switch (judgeResult.Decision)
        {
            case PromotionDecision.Advance:
                if (judgeResult.NextStage is null)
                {
                    throw new InvalidOperationException(
                        $"Judge returned Advance 但未提供 NextStage；runId={runId}");
                }
                // 硬边界：严格顺序推进 — 验证下一阶段是当前阶段的合法后继
                VerifyStageProgression(snapshot.CurrentStage, judgeResult.NextStage.Value);
                // P2-3：进入 ScopedCanary 时自动生成 CanaryAssignment 作为 transition audit 一部分原子提交。
                if (judgeResult.NextStage == OptimizationStage.ScopedCanary)
                {
                    canaryAssignment = new CanaryAssignment(
                        assignmentId: $"ca-{runId}-{now.UtcDateTime.Ticks}",
                        proposalId: snapshot.ProposalId,
                        runId: runId,
                        strategy: CanaryAssignmentStrategy.HashBased,
                        assignedAt: now);
                }
                nextSnapshot = snapshot with
                {
                    CurrentStage = judgeResult.NextStage.Value,
                    Status = PipelineRunStatus.StageCompleted,
                    UpdatedAt = now,
                    StageMetrics = updatedMetrics,
                    Revision = snapshot.Revision + 1,
                    LastTransitionId = transitionId
                };
                break;

            case PromotionDecision.Promote:
                nextSnapshot = snapshot with
                {
                    CurrentStage = OptimizationStage.Promotion,
                    Status = PipelineRunStatus.Promoted,
                    CompletedAt = now,
                    UpdatedAt = now,
                    StageMetrics = updatedMetrics,
                    Revision = snapshot.Revision + 1,
                    LastTransitionId = transitionId
                };
                break;

            case PromotionDecision.Rollback:
                var triggeredCondition = FindTriggeredCondition(snapshot.Proposal, experimentMetrics);
                rollbackRecord = new RollbackRecord(
                    recordId: $"rb-{runId}-{now.UtcDateTime.Ticks}",
                    runId: runId,
                    proposalId: snapshot.ProposalId,
                    reason: RollbackReason.RollbackConditionTriggered,
                    triggeredAt: now)
                {
                    TriggeredConditionMetricName = triggeredCondition?.MetricName,
                    TriggeredConditionThreshold = triggeredCondition?.Threshold,
                    TriggeredConditionValue = triggeredCondition is not null
                        ? experimentMetrics.TryGetValue(triggeredCondition.MetricName, out var v) ? v : (double?)null
                        : null,
                    TriggeredAtStage = snapshot.CurrentStage
                };
                nextSnapshot = snapshot with
                {
                    RollbackReason = judgeResult.Rationale,
                    CurrentStage = OptimizationStage.AutomaticRollback,
                    Status = PipelineRunStatus.RolledBack,
                    CompletedAt = now,
                    UpdatedAt = now,
                    StageMetrics = updatedMetrics,
                    Revision = snapshot.Revision + 1,
                    LastTransitionId = transitionId
                };
                break;

            case PromotionDecision.Reject:
                nextSnapshot = snapshot with
                {
                    Status = PipelineRunStatus.Rejected,
                    RollbackReason = judgeResult.Rationale,
                    CompletedAt = now,
                    UpdatedAt = now,
                    StageMetrics = updatedMetrics,
                    Revision = snapshot.Revision + 1,
                    LastTransitionId = transitionId
                };
                break;

            case PromotionDecision.Hold:
                // 保持当前 stage，Status 仍为 Running（等待更多数据）；仅更新 StageMetrics + UpdatedAt
                nextSnapshot = snapshot with
                {
                    UpdatedAt = now,
                    StageMetrics = updatedMetrics,
                    Revision = snapshot.Revision + 1,
                    LastTransitionId = transitionId
                };
                break;

            default:
                throw new InvalidOperationException(
                    $"Judge 返回未知的 PromotionDecision: {judgeResult.Decision}");
        }

        // P0-7：原子 CAS 推进 — snapshot + audit 批量在同一事务内写入
        // P2-3：CanaryAssignment 作为 transition audit 一部分原子提交（不再独立写入）
        var auditBatch = new PipelineAuditBatch
        {
            BaselineComparison = comparison,
            CanaryAssignment = canaryAssignment,
            RollbackRecord = rollbackRecord
        };
        var transitionedSnapshot = await _store.TryTransitionAsync(
            runId,
            expectedRevision: snapshot.Revision,
            expectedStage: snapshot.CurrentStage,
            next: nextSnapshot,
            audit: auditBatch,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (transitionedSnapshot is null)
        {
            throw new InvalidOperationException(
                $"Pipeline CAS 推进失败：runId={runId}，expectedRevision={snapshot.Revision}，" +
                $"expectedStage={snapshot.CurrentStage}。可能原因：另一实例已并发推进此 run。" +
                "调用方应重新读取当前状态并决定是否重试。");
        }

        return BuildResult(transitionedSnapshot);
    }

    /// <inheritdoc />
    public async Task<PipelineRunResult?> GetStatusAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : BuildResult(snapshot);
    }

    /// <summary>获取指定 run 的所有 canary assignment（供审计）。</summary>
    public Task<IReadOnlyList<CanaryAssignment>> GetCanaryAssignmentsAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _store.ListCanaryAssignmentsByRunAsync(runId, cancellationToken);
    }

    /// <summary>记录 canary assignment（供 ScopedCanary 阶段审计）。</summary>
    /// <remarks>
    /// P2-3：已过时。CanaryAssignment 现在通过 <see cref="AdvanceWithMetricsAsync"/> 的
    /// transition audit（<see cref="PipelineAuditBatch.CanaryAssignment"/>）原子提交，
    /// 不再需要独立写入。此方法保留用于管理员手动恢复 / 测试场景。
    /// </remarks>
    [Obsolete("CanaryAssignment is now created atomically via AdvanceWithMetricsAsync transition audit. Use PipelineAuditBatch.CanaryAssignment.", error: false)]
    public Task RecordCanaryAssignmentAsync(
        CanaryAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();
        return _store.SaveCanaryAssignmentAsync(assignment, cancellationToken);
    }

    /// <summary>获取指定 run 的回滚记录（如有）。</summary>
    public Task<RollbackRecord?> GetRollbackRecordAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _store.GetRollbackRecordByRunAsync(runId, cancellationToken);
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
        // P2-1：使用 GUID 保证 RunId 唯一性，不依赖秒精度时间戳（避免同秒碰撞导致覆盖）。
        var uniqueId = Guid.NewGuid().ToString("N");
        return $"run-{componentTag}-{proposal.ProposalId}-{uniqueId}";
    }

    private static PipelineRunResult BuildResult(PipelineRunSnapshot snapshot)
    {
        // 将最近一次 BaselineComparison 的实验指标作为 StageMetrics（符合契约类型 IReadOnlyDictionary<string, double>）
        var latestMetrics = snapshot.StageMetrics.Count > 0
            ? snapshot.StageMetrics[^1].ExperimentMetrics
            : new Dictionary<string, double>();
        return new PipelineRunResult(
            runId: snapshot.RunId,
            proposalId: snapshot.ProposalId,
            proposalVersion: snapshot.ProposalVersion,
            stage: snapshot.CurrentStage,
            status: snapshot.Status,
            stageMetrics: latestMetrics,
            rollbackReason: snapshot.RollbackReason,
            completedAt: snapshot.CompletedAt);
    }
}
