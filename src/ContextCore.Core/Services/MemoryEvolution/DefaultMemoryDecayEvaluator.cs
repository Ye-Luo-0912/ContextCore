using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// IMemoryDecayEvaluator 的默认实现。根据 MemoryUtilityStats 评估 item 是否需要降权。
/// </summary>
/// <remarks>
/// 降权因素优先级（高 → 低）：
/// 1. EvidenceInvalid → Rejected（evidence 失效）
/// 2. ConflictWithCurrent → Rejected（与当前状态冲突）
/// 3. NewVersionAvailable → Superseded（已有新版本）
/// 4. TaskCompleted → Archived（任务已完成）
/// 5. NoEffectiveContribution → Cooling（多次选择但无有效贡献）
/// 6. LongTermNoHit → Cooling/Dormant/Archived（长期未命中，按未命中时长决定）
///
/// 阈值参数（可通过构造函数配置）：
/// - CoolingThreshold：长期未命中 → Cooling 的阈值（默认 7 天）
/// - DormantThreshold：Cooling → Dormant 的阈值（默认 30 天）
/// - ArchiveThreshold：Dormant → Archived 的阈值（默认 90 天）
/// - NoContributionSelectionThreshold：NoEffectiveContribution 触发的最小选择次数（默认 5 次）
/// - NoContributionUsefulThreshold：NoEffectiveContribution 触发的最大有效贡献次数（默认 0 次）
///
/// 状态机合法性：
/// - 若当前状态不允许转换到目标状态（CanTransitionTo 返回 false），返回 TargetState = CurrentState。
/// - 已是终态（Archived）的 item 不评估降权。
/// </remarks>
public sealed class DefaultMemoryDecayEvaluator : IMemoryDecayEvaluator
{
    private readonly TimeProvider? _timeProvider;

    /// <summary>长期未命中 → Cooling 的阈值（默认 7 天）。</summary>
    public TimeSpan CoolingThreshold { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Cooling → Dormant 的阈值（默认 30 天）。</summary>
    public TimeSpan DormantThreshold { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Dormant → Archived 的阈值（默认 90 天）。</summary>
    public TimeSpan ArchiveThreshold { get; init; } = TimeSpan.FromDays(90);

    /// <summary>NoEffectiveContribution 触发的最小选择次数（默认 5 次）。</summary>
    public int NoContributionSelectionThreshold { get; init; } = 5;

    /// <summary>NoEffectiveContribution 触发的最大有效贡献次数（默认 0 次）。</summary>
    public int NoContributionUsefulThreshold { get; init; } = 0;

    public DefaultMemoryDecayEvaluator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<MemoryDecayAssessment> EvaluateAsync(
        string sourceItemId,
        string workspaceId,
        string collectionId,
        MemoryState currentState,
        MemoryUtilityStats? stats,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        cancellationToken.ThrowIfCancellationRequested();

        var evaluatedAt = now ?? Now();

        // 已是终态的 item 不评估降权
        if (currentState.IsTerminal())
        {
            return Task.FromResult(new MemoryDecayAssessment
            {
                SourceItemId = sourceItemId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                CurrentState = currentState,
                TargetState = currentState,
                DecayFactor = MemoryDecayFactor.Unknown,
                ReasonDetail = "Item is in terminal state (Archived). No decay assessment needed.",
                AssessedAt = evaluatedAt
            });
        }

        // 已是 Rejected/Superseded/Replaced 的 item 不评估衰减（由独立流程推进）
        if (currentState == MemoryState.Rejected
            || currentState == MemoryState.Superseded
            || currentState == MemoryState.Replaced)
        {
            return Task.FromResult(new MemoryDecayAssessment
            {
                SourceItemId = sourceItemId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                CurrentState = currentState,
                TargetState = currentState,
                DecayFactor = MemoryDecayFactor.Unknown,
                ReasonDetail = $"Item is in {currentState} state. Decay not applicable; handled by consolidation ETL or manual review.",
                AssessedAt = evaluatedAt
            });
        }

        // 仅评估 Fresh / Active / Cooling / Dormant 状态的衰减
        var assessment = EvaluateDecay(
            sourceItemId, workspaceId, collectionId, currentState, stats, evaluatedAt);
        return Task.FromResult(assessment);
    }

    private MemoryDecayAssessment EvaluateDecay(
        string sourceItemId,
        string workspaceId,
        string collectionId,
        MemoryState currentState,
        MemoryUtilityStats? stats,
        DateTimeOffset now)
    {
        // 因素 6：多次被选择但未产生有效贡献 → Cooling
        if (stats is not null
            && currentState == MemoryState.Active
            && stats.SelectedCount >= NoContributionSelectionThreshold
            && stats.UsefulFeedbackCount <= NoContributionUsefulThreshold)
        {
            return TryBuildAssessment(
                sourceItemId, workspaceId, collectionId,
                currentState, MemoryState.Cooling,
                MemoryDecayFactor.NoEffectiveContribution,
                $"Selected {stats.SelectedCount} times but only {stats.UsefulFeedbackCount} useful feedback.",
                stats, now);
        }

        // 因素 1：长期未命中 → Cooling/Dormant/Archived
        if (stats is not null && stats.LastRecallTime is not null)
        {
            var timeSinceLastRecall = now - stats.LastRecallTime.Value;

            // Dormant → Archived（超过 ArchiveThreshold）
            if (currentState == MemoryState.Dormant
                && timeSinceLastRecall >= ArchiveThreshold)
            {
                return TryBuildAssessment(
                    sourceItemId, workspaceId, collectionId,
                    currentState, MemoryState.Archived,
                    MemoryDecayFactor.LongTermNoHit,
                    $"No recall for {timeSinceLastRecall.TotalDays:F1} days (>= ArchiveThreshold {ArchiveThreshold.TotalDays} days).",
                    stats, now);
            }

            // Cooling → Dormant（超过 DormantThreshold）
            if (currentState == MemoryState.Cooling
                && timeSinceLastRecall >= DormantThreshold)
            {
                return TryBuildAssessment(
                    sourceItemId, workspaceId, collectionId,
                    currentState, MemoryState.Dormant,
                    MemoryDecayFactor.LongTermNoHit,
                    $"No recall for {timeSinceLastRecall.TotalDays:F1} days (>= DormantThreshold {DormantThreshold.TotalDays} days).",
                    stats, now);
            }

            // Active → Cooling（超过 CoolingThreshold）
            if (currentState == MemoryState.Active
                && timeSinceLastRecall >= CoolingThreshold)
            {
                return TryBuildAssessment(
                    sourceItemId, workspaceId, collectionId,
                    currentState, MemoryState.Cooling,
                    MemoryDecayFactor.LongTermNoHit,
                    $"No recall for {timeSinceLastRecall.TotalDays:F1} days (>= CoolingThreshold {CoolingThreshold.TotalDays} days).",
                    stats, now);
            }
        }

        // 无降权触发
        return new MemoryDecayAssessment
        {
            SourceItemId = sourceItemId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CurrentState = currentState,
            TargetState = currentState,
            DecayFactor = MemoryDecayFactor.Unknown,
            ReasonDetail = stats is null
                ? "No stats available; no decay assessment."
                : "No decay factors triggered.",
            AssessedAt = now
        };
    }

    private MemoryDecayAssessment TryBuildAssessment(
        string sourceItemId,
        string workspaceId,
        string collectionId,
        MemoryState currentState,
        MemoryState targetState,
        MemoryDecayFactor factor,
        string reasonDetail,
        MemoryUtilityStats stats,
        DateTimeOffset now)
    {
        // 状态机合法性检查
        if (!currentState.CanTransitionTo(targetState))
        {
            return new MemoryDecayAssessment
            {
                SourceItemId = sourceItemId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                CurrentState = currentState,
                TargetState = currentState, // 不允许转换，保持原状态
                DecayFactor = MemoryDecayFactor.Unknown,
                ReasonDetail = $"Decay factor {factor} triggered but state transition {currentState} -> {targetState} not allowed.",
                AssessedAt = now
            };
        }

        return new MemoryDecayAssessment
        {
            SourceItemId = sourceItemId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            CurrentState = currentState,
            TargetState = targetState,
            DecayFactor = factor,
            ReasonDetail = reasonDetail,
            AssessedAt = now
        };
    }

    private DateTimeOffset Now() => _timeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
}
