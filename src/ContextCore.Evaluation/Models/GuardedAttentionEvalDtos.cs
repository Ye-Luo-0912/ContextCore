namespace ContextCore.Abstractions.Models;

// P3-05: 这些 eval-only DTO 从 ContextCore.Abstractions\Models\EvalDtos.cs 物理迁移到 Evaluation 项目。
// 命名空间保持 ContextCore.Abstractions.Models 以避免调用方 using 变更。
// 仅被 Evaluation / ControlRoom eval 命令 / tests 引用，不被 Core 运行时或 Service 使用。

/// <summary>Guarded attention rerank 实验的评测汇总报告。</summary>
public sealed class GuardedAttentionRerankEvalReport
{
    public string OperationId { get; init; } = string.Empty;

    public string Mode { get; init; } = "SelectedSetPreserving";

    public string ProfileId { get; init; } = "old-score-anchored-v1";

    public int TotalSamples { get; init; }

    public int AppliedSamples { get; init; }

    public int SkippedSamples { get; init; }

    public int BlockedSamples { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int OrderChanges { get; init; }

    public int SectionChanges { get; init; }

    public int MustHitRankDeltaCount { get; init; }

    public int MustNotHitRankDeltaCount { get; init; }

    public int SelectedSetChangeCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public Dictionary<string, int> BlockedReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> SkippedReasons { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GuardedAttentionRerankEvalSample> Samples { get; init; } = Array.Empty<GuardedAttentionRerankEvalSample>();
}

/// <summary>Guarded attention rerank 实验的单样本汇总。</summary>
public sealed class GuardedAttentionRerankEvalSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public bool Applied { get; init; }

    public bool Skipped { get; init; }

    public bool Blocked { get; init; }

    public string SkippedReason { get; init; } = string.Empty;

    public string BlockedReason { get; init; } = string.Empty;

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int OrderChanges { get; init; }

    public int SectionChanges { get; init; }

    public int MustHitRankDeltaCount { get; init; }

    public int MustNotHitRankDeltaCount { get; init; }

    public int SelectedSetChangeCount { get; init; }

    public double SelectedSetChangeRatio { get; init; }

    public IReadOnlyList<AttentionRerankItemChange> TopOrderChanges { get; init; } = Array.Empty<AttentionRerankItemChange>();
}

/// <summary>Selected order 质量指标。rank 越小表示越靠前。</summary>
public sealed class SelectedOrderQualityMetrics
{
    public double SelectedOrderMRR { get; init; }

    public double FirstMustHitSelectedRank { get; init; }

    public double MustHitAverageSelectedRank { get; init; }

    public double ConstraintAverageRank { get; init; }

    public double LifecycleRiskAverageRank { get; init; }

    public double AttentionOrderDelta { get; init; }

    public int MovedUpMustHitCount { get; init; }

    public int MovedDownMustHitCount { get; init; }
}

/// <summary>Selected order 质量闸门结果。</summary>
public sealed class SelectedOrderQualityGateResult
{
    public string Name { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public double Actual { get; init; }

    public double Threshold { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>Guarded attention rerank selected order 质量对比报告。</summary>
public sealed class GuardedAttentionOrderQualityReport
{
    public string OperationId { get; init; } = string.Empty;

    public string Mode { get; init; } = "SelectedSetPreserving";

    public string ProfileId { get; init; } = "old-score-anchored-v1";

    public int TotalSamples { get; init; }

    public int AppliedSamples { get; init; }

    public int SkippedSamples { get; init; }

    public int BlockedSamples { get; init; }

    public int SelectedSetDiffCount { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int HardConstraintMissingCount { get; init; }

    public SelectedOrderQualityMetrics Baseline { get; init; } = new();

    public SelectedOrderQualityMetrics Reranked { get; init; } = new();

    public SelectedOrderQualityMetrics Delta { get; init; } = new();

    public IReadOnlyList<SelectedOrderQualityGateResult> SafetyGates { get; init; } = Array.Empty<SelectedOrderQualityGateResult>();

    public IReadOnlyList<SelectedOrderQualityGateResult> SortingGates { get; init; } = Array.Empty<SelectedOrderQualityGateResult>();

    public IReadOnlyList<GuardedAttentionOrderQualitySample> Samples { get; init; } = Array.Empty<GuardedAttentionOrderQualitySample>();
}

/// <summary>单样本 selected order 质量对比。</summary>
public sealed class GuardedAttentionOrderQualitySample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public bool Succeeded { get; init; }

    public bool Applied { get; init; }

    public bool Skipped { get; init; }

    public bool Blocked { get; init; }

    public string SkippedReason { get; init; } = string.Empty;

    public string BlockedReason { get; init; } = string.Empty;

    public SelectedOrderQualityMetrics Baseline { get; init; } = new();

    public SelectedOrderQualityMetrics Reranked { get; init; } = new();

    public SelectedOrderQualityMetrics Delta { get; init; } = new();

    public int SelectedSetDiffCount { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int HardConstraintMissingCount { get; init; }

    public IReadOnlyList<AttentionRerankOrderItem> OldSelectedOrder { get; init; } = Array.Empty<AttentionRerankOrderItem>();

    public IReadOnlyList<AttentionRerankOrderItem> NewSelectedOrder { get; init; } = Array.Empty<AttentionRerankOrderItem>();

    public IReadOnlyList<AttentionRerankItemChange> MovedUpItems { get; init; } = Array.Empty<AttentionRerankItemChange>();

    public IReadOnlyList<AttentionRerankItemChange> MovedDownItems { get; init; } = Array.Empty<AttentionRerankItemChange>();
}

/// <summary>Guarded attention profile sweep 的 selected-order 汇总报告。</summary>
public sealed class GuardedAttentionProfileSweepReport
{
    public string OperationId { get; init; } = string.Empty;

    public string Mode { get; init; } = "SelectedSetPreserving";

    public int TotalSamples { get; init; }

    public bool IncludeSeedBatches { get; init; }

    public IReadOnlyList<GuardedAttentionProfileSweepProfile> Profiles { get; init; } = Array.Empty<GuardedAttentionProfileSweepProfile>();
}

/// <summary>单个 guarded attention profile 的 sweep 指标。</summary>
public sealed class GuardedAttentionProfileSweepProfile
{
    public string ProfileId { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public Dictionary<string, double> Weights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public int TotalSamples { get; init; }

    public int AppliedSamples { get; init; }

    public int SkippedSamples { get; init; }

    public int BlockedSamples { get; init; }

    public int SelectedSetDiffCount { get; init; }

    public int AddedItems { get; init; }

    public int DroppedItems { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int HardConstraintMissingCount { get; init; }

    public double SelectedOrderMRR { get; init; }

    public double FirstMustHitSelectedRank { get; init; }

    public double MustHitAverageSelectedRank { get; init; }

    public double ConstraintAverageRank { get; init; }

    public double LifecycleRiskAverageRank { get; init; }

    public double AttentionOrderDelta { get; init; }

    public int MovedUpMustHitCount { get; init; }

    public int MovedDownMustHitCount { get; init; }

    public bool SafetyGatePassed { get; init; }

    public bool SortingGatePassed { get; init; }
}
