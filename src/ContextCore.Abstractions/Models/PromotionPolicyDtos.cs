namespace ContextCore.Abstractions.Models;

/// <summary>Promotion 条件评估的建议结果。</summary>
public enum PromotionEvaluationDecision
{
    /// <summary>不建议提升，避免污染中期或长期记忆层。</summary>
    DoNotPromote,
    /// <summary>建议提升到中期工作记忆层。</summary>
    PromoteToWorkingMemory,
    /// <summary>建议提升到长期稳定记忆层。</summary>
    PromoteToStableMemory,
    /// <summary>规则信号不足或存在冲突，需要进入审核。</summary>
    NeedsReview
}

/// <summary>Promotion 候选项的审核状态。</summary>
public enum PromotionCandidateStatus
{
    /// <summary>候选状态，等待审核或自动处理。</summary>
    Candidate,
    /// <summary>待审核状态，等同于 Candidate，用于短期候选项人工 review 语义。</summary>
    Pending = Candidate,
    /// <summary>已接受，可执行实际提升。</summary>
    Accepted,
    /// <summary>已拒绝，不应提升。</summary>
    Rejected,
    /// <summary>需要人工或更强模型进一步审核。</summary>
    NeedsReview,
    /// <summary>已被后续候选或新事实覆盖。</summary>
    Superseded,
    /// <summary>候选项已过期，不再参与审核动作。</summary>
    Expired
}

/// <summary>Promotion 条件评估请求，只描述候选内容，不执行任何写入。</summary>
public sealed class PromotionEvaluationRequest
{
    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>候选来源 ID，可对应原始上下文、工作记忆或外部来源。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>候选内容类型，例如 task、constraint、dialogue、note。</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>候选内容正文。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>候选标签。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>候选内容置信度，范围 0～1；未提供时按 0.5 处理。</summary>
    public double Confidence { get; init; } = 0.5;

    /// <summary>附加元数据，可用于显式覆盖或补充规则信号。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>Promotion 条件评估结果；该结果只是建议，不代表已经写入记忆层。</summary>
public sealed class PromotionEvaluationResult
{
    /// <summary>建议动作。</summary>
    public PromotionEvaluationDecision Decision { get; init; } = PromotionEvaluationDecision.DoNotPromote;

    /// <summary>建议目标层；不建议提升时为空。</summary>
    public ContextMemoryLayer? TargetLayer { get; init; }

    /// <summary>命中的最高优先级类别。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>可读原因说明。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>规则评分，范围 0～1。</summary>
    public double Score { get; init; }

    /// <summary>命中的规则名称列表。</summary>
    public IReadOnlyList<string> MatchedRules { get; init; } = Array.Empty<string>();

    /// <summary>是否建议进入人工或自动审核队列。</summary>
    public bool RequiresReview { get; init; }

    /// <summary>是否建议提升到某个记忆层。</summary>
    public bool ShouldPromote =>
        Decision is PromotionEvaluationDecision.PromoteToWorkingMemory
            or PromotionEvaluationDecision.PromoteToStableMemory;
}

/// <summary>进入 Promotion Review 流程的候选项。</summary>
public sealed class PromotionCandidate
{
    /// <summary>候选项唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>所属工作空间 ID。</summary>
    public string WorkspaceId { get; init; } = string.Empty;

    /// <summary>所属集合 ID。</summary>
    public string CollectionId { get; init; } = string.Empty;

    /// <summary>候选来源 ID，可对应原始上下文或记忆条目。</summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>候选来源类型，例如 context、memory、external。</summary>
    public string SourceKind { get; init; } = string.Empty;

    /// <summary>候选内容。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>建议目标层；拒绝或待审核时可为空。</summary>
    public ContextMemoryLayer? TargetLayer { get; init; }

    /// <summary>当前审核状态。</summary>
    public PromotionCandidateStatus Status { get; init; } = PromotionCandidateStatus.Candidate;

    /// <summary>条件评估建议。</summary>
    public PromotionEvaluationDecision Decision { get; init; } = PromotionEvaluationDecision.NeedsReview;

    /// <summary>候选分类。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>候选原因或审核说明。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>置信度或规则评分，范围 0～1。</summary>
    public double Confidence { get; init; }

    /// <summary>命中的规则名称列表。</summary>
    public IReadOnlyList<string> MatchedRules { get; init; } = Array.Empty<string>();

    /// <summary>来源引用 ID 列表。</summary>
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();

    /// <summary>最近一次审核人；未审核时为空。</summary>
    public string? Reviewer { get; init; }

    /// <summary>附加元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>最后更新时间。</summary>
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Promotion Eval 的单条样本。</summary>
public sealed class PromotionEvalSample
{
    /// <summary>样本唯一标识符。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>样本说明。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>待评估的短期内容。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>期望的评估结果。</summary>
    public PromotionEvaluationDecision ExpectedDecision { get; init; } = PromotionEvaluationDecision.DoNotPromote;

    /// <summary>期望目标层；不应提升时为空。</summary>
    public ContextMemoryLayer? ExpectedTargetLayer { get; init; }

    /// <summary>样本标签。</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>样本元数据。</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>Promotion Eval 单条样本结果。</summary>
public sealed class PromotionEvalSampleResult
{
    /// <summary>样本 ID。</summary>
    public string SampleId { get; init; } = string.Empty;

    /// <summary>期望结果。</summary>
    public PromotionEvaluationDecision ExpectedDecision { get; init; }

    /// <summary>实际结果。</summary>
    public PromotionEvaluationDecision ActualDecision { get; init; }

    /// <summary>期望目标层。</summary>
    public ContextMemoryLayer? ExpectedTargetLayer { get; init; }

    /// <summary>实际目标层。</summary>
    public ContextMemoryLayer? ActualTargetLayer { get; init; }

    /// <summary>是否正确提升到期望目标层。</summary>
    public bool IsCorrectPromotion { get; init; }

    /// <summary>是否错误提升了不应提升的内容。</summary>
    public bool IsErroneousPromotion { get; init; }

    /// <summary>是否遗漏了应提升内容。</summary>
    public bool IsMissedPromotion { get; init; }

    /// <summary>是否污染长期稳定层。</summary>
    public bool IsStableLayerPollution { get; init; }

    /// <summary>是否进入 needs_review。</summary>
    public bool IsNeedsReview { get; init; }

    /// <summary>评估原因。</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Promotion Eval 汇总报告。</summary>
public sealed class PromotionEvalReport
{
    public int TotalSamples { get; init; }

    public int ExpectedPromotionSamples { get; init; }

    public int ExpectedNoPromotionSamples { get; init; }

    public int CorrectPromotionCount { get; init; }

    public int ErroneousPromotionCount { get; init; }

    public int MissedPromotionCount { get; init; }

    public int StableLayerPollutionCount { get; init; }

    public int NeedsReviewCount { get; init; }

    public double CorrectPromotionRate { get; init; }

    public double ErroneousPromotionRate { get; init; }

    public double MissedPromotionRate { get; init; }

    public double StableLayerPollutionRate { get; init; }

    public double NeedsReviewRate { get; init; }

    public IReadOnlyList<PromotionEvalSampleResult> Results { get; init; } = Array.Empty<PromotionEvalSampleResult>();
}
