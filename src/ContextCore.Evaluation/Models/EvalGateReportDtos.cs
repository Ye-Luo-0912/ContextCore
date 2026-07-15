using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

/// <summary>A3 / Extended retrieval dataset alignment audit 汇总。</summary>
public sealed class RetrievalDatasetAlignmentAuditSummaryReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<RetrievalDatasetAlignmentAuditReport> Reports { get; init; } =
        Array.Empty<RetrievalDatasetAlignmentAuditReport>();

    public string Recommendation { get; init; } = RetrievalDatasetAlignmentRecommendations.KeepPreviewOnly;

    public int AlignmentIssueCount { get; init; }

    public IReadOnlyDictionary<string, int> IssueBreakdown { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }
}


/// <summary>上下文评测静态语料库数据，供 InMemory 模式一键加载。</summary>
public sealed class ContextEvalCorpus
{
    public IReadOnlyList<ContextItem> Contexts { get; init; } = Array.Empty<ContextItem>();
    public IReadOnlyList<ContextMemoryItem> Memories { get; init; } = Array.Empty<ContextMemoryItem>();
    public IReadOnlyList<ContextRelation> Relations { get; init; } = Array.Empty<ContextRelation>();
    public IReadOnlyList<ContextConstraint> Constraints { get; init; } = Array.Empty<ContextConstraint>();

    /// <summary>
    /// 评测专用的约束缺口激活 fixture。加载时必须经过 ConstraintGap accept 与 CandidateConstraint activate 正式链路。
    /// </summary>
    public IReadOnlyList<ConstraintGapCandidate> ActivatedConstraintGaps { get; init; } = Array.Empty<ConstraintGapCandidate>();
}
