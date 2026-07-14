namespace ContextCore.Abstractions.Models;

/// <summary>上下文学习反馈信号。</summary>
public enum ContextFeedbackSignal
{
    /// <summary>正向反馈，可作为后续规则或评测的正样本。</summary>
    Positive,
    /// <summary>负向反馈，表示候选或行为不应被采纳。</summary>
    Negative,
    /// <summary>过期反馈，表示候选因时效失效。</summary>
    Stale
}

/// <summary>上下文学习失败类型，当前只做记录，不驱动自动调参。</summary>
public enum ContextFailureType
{
    None,
    PromotionFalsePositive,
    PromotionFalseNegative,
    PromotionExpired,
    StaleCandidate,
    Unknown
}

/// <summary>上下文学习案例生命周期状态。</summary>
public enum ContextLearningCaseStatus
{
    Draft,
    Candidate,
    ActiveRegression,
    Archived,
    Rejected
}

/// <summary>由短期晋升候选项 review 生成的反馈信号。</summary>
public sealed class PromotionFeedbackSignal
{
    public string FeedbackId { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string SourceWorkingItemId { get; init; } = string.Empty;

    public string? CreatedTargetItemId { get; init; }

    public string SuggestedTargetLayer { get; init; } = string.Empty;

    public string? ActualTargetLayer { get; init; }

    public double Confidence { get; init; }

    public double Importance { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>运行时反馈类型；只用于反馈收集和离线分析，不直接驱动策略变更。</summary>
public static class LearningFeedbackKinds
{
    public const string Useful = nameof(Useful);

    public const string NotUseful = nameof(NotUseful);

    public const string WrongIntent = nameof(WrongIntent);

    public const string WrongCandidate = nameof(WrongCandidate);

    public const string MissingContext = nameof(MissingContext);

    public const string DeprecatedContext = nameof(DeprecatedContext);

    public const string ConstraintMissing = nameof(ConstraintMissing);

    public const string ConstraintIncorrect = nameof(ConstraintIncorrect);

    public const string RankingWrong = nameof(RankingWrong);

    public const string PromotionWrong = nameof(PromotionWrong);

    public const string ShouldPromote = nameof(ShouldPromote);

    public const string ShouldReject = nameof(ShouldReject);

    public const string NeedsMoreEvidence = nameof(NeedsMoreEvidence);
}

/// <summary>运行时反馈目标类型，限定反馈可以绑定的对象类别。</summary>
public enum LearningFeedbackTargetType
{
    PackageItem,
    RetrievalCandidate,
    RouterPrediction,
    VectorCandidate,
    GraphExpansionCandidate,
    RankerCandidate,
    PromotionCandidate,
    ConstraintGapCandidate,
    StableReviewCandidate
}

/// <summary>运行时反馈提交请求；与存储事件分离，便于入口层做目标类型校验。</summary>
public sealed class LearningFeedbackSubmitRequest
{
    public string FeedbackId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string SourceOperationId { get; init; } = string.Empty;

    public string CapabilityId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public LearningFeedbackTargetType TargetType { get; init; }

    public string FeedbackKind { get; init; } = string.Empty;

    public double FeedbackValue { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string UserCorrection { get; init; } = string.Empty;

    public string RedactionMode { get; init; } = string.Empty;

    public bool MetadataOnly { get; init; }

    public string TrainingUse { get; init; } = "disabled_until_review";

    public double Confidence { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>运行时反馈事件；用于收集人工反馈，不自动训练、不自动调权。</summary>
public sealed class LearningFeedbackEvent
{
    public string FeedbackId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string SourceOperationId { get; init; } = string.Empty;

    public string CapabilityId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;

    public string TargetType { get; init; } = string.Empty;

    public string FeedbackKind { get; init; } = string.Empty;

    public double FeedbackValue { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string UserCorrection { get; init; } = string.Empty;

    public string RedactionMode { get; init; } = string.Empty;

    public bool MetadataOnly { get; init; }

    public string TrainingUse { get; init; } = "disabled_until_review";

    public double Confidence { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>运行时反馈查询条件。</summary>
public sealed class LearningFeedbackEventQuery
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? Source { get; init; }

    public string? SourceOperationId { get; init; }

    public string? CapabilityId { get; init; }

    public string? TargetId { get; init; }

    public string? TargetType { get; init; }

    public string? FeedbackKind { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>运行时反馈提交结果。</summary>
public sealed class LearningFeedbackSubmitResult
{
    public string FeedbackId { get; init; } = string.Empty;

    public bool Created { get; init; }

    public bool DuplicateReplaced { get; init; }

    public LearningFeedbackEvent Event { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>运行时反馈摘要报告。</summary>
public sealed class LearningFeedbackSummaryReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public int FeedbackCount { get; init; }

    public IReadOnlyDictionary<string, int> FeedbackByCapability { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> FeedbackByKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> FeedbackByTargetType { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public int MetadataOnlyCount { get; init; }

    public int TrainingUseDisabledCount { get; init; }

    public IReadOnlyList<LearningFeedbackEvent> RecentFeedback { get; init; } =
        Array.Empty<LearningFeedbackEvent>();

    public string ExportPath { get; init; } = "learning/feedback/learning-feedback-events.jsonl";

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>运行时反馈进入数据集前的人工审核状态。</summary>
public enum FeedbackReviewStatus
{
    PendingReview,
    ApprovedForDataset,
    Rejected,
    NeedsRedaction,
    NeedsMoreEvidence
}

/// <summary>运行时反馈审核请求。</summary>
public sealed class LearningFeedbackReviewRequest
{
    public string Reviewer { get; init; } = "manual";

    public string ReviewReason { get; init; } = string.Empty;

    public string ApprovedCapability { get; init; } = string.Empty;

    public string ApprovedLabelKind { get; init; } = string.Empty;

    public bool RedactionChecked { get; init; }

    public string TrainingUse { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>运行时反馈审核记录；只控制离线数据集候选，不改变正式运行时。</summary>
public sealed class LearningFeedbackReviewRecord
{
    public string FeedbackId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = string.Empty;

    public FeedbackReviewStatus ReviewStatus { get; init; } = FeedbackReviewStatus.PendingReview;

    public string ReviewReason { get; init; } = string.Empty;

    public string ApprovedCapability { get; init; } = string.Empty;

    public string ApprovedLabelKind { get; init; } = string.Empty;

    public bool RedactionChecked { get; init; }

    public string TrainingUse { get; init; } = "disabled_until_evidence_ready"; // V13: was "disabled_until_review"

    public DateTimeOffset ReviewedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>运行时反馈审核查询条件。</summary>
public sealed class LearningFeedbackReviewQuery
{
    public string? FeedbackId { get; init; }

    public FeedbackReviewStatus? ReviewStatus { get; init; }

    public string? Reviewer { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>运行时反馈审核操作结果。</summary>
public sealed class LearningFeedbackReviewResult
{
    public string FeedbackId { get; init; } = string.Empty;

    public FeedbackReviewStatus ReviewStatus { get; init; } = FeedbackReviewStatus.PendingReview;

    public LearningFeedbackReviewRecord Review { get; init; } = new();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>运行时反馈审核摘要。</summary>
public sealed class LearningFeedbackReviewSummaryReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public int FeedbackCount { get; init; }

    public int PendingReviewCount { get; init; }

    public int ApprovedCount { get; init; }

    public int RejectedCount { get; init; }

    public int NeedsRedactionCount { get; init; }

    public int NeedsMoreEvidenceCount { get; init; }

    public IReadOnlyDictionary<string, int> ReviewsByStatus { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LearningFeedbackReviewRecord> RecentReviews { get; init; } =
        Array.Empty<LearningFeedbackReviewRecord>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>已审核反馈生成的离线特征候选；不直接进入训练或正式策略。</summary>
public sealed class FeedbackFeatureCandidate
{
    public string CandidateId { get; init; } = string.Empty;

    public string SourceFeedbackId { get; init; } = string.Empty;

    public string CapabilityId { get; init; } = string.Empty;

    public string TargetType { get; init; } = string.Empty;

    public string LabelKind { get; init; } = string.Empty;

    public bool PositiveLabel { get; init; }

    public bool NegativeLabel { get; init; }

    public string QueryText { get; init; } = string.Empty;

    public string ContextRef { get; init; } = string.Empty;

    public string TargetRef { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string TrainingUse { get; init; } = string.Empty;

    public string RedactionStatus { get; init; } = string.Empty;

    public FeedbackReviewStatus ReviewStatus { get; init; } = FeedbackReviewStatus.PendingReview;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>反馈特征候选查询条件。</summary>
public sealed class LearningFeatureCandidateQuery
{
    public string? CandidateId { get; init; }

    public string? SourceFeedbackId { get; init; }

    public string? CapabilityId { get; init; }

    public string? TargetType { get; init; }

    public string? LabelKind { get; init; }

    public string? TrainingUse { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>反馈特征候选导出报告。</summary>
public sealed class LearningFeedbackFeatureCandidateReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public int FeedbackScanned { get; init; }

    public int ReviewScanned { get; init; }

    public int GeneratedCandidateCount { get; init; }

    public int PendingReviewCount { get; init; }

    public int NeedsMoreEvidenceCount { get; init; }

    public int NeedsRedactionCount { get; init; }

    public int RejectedCount { get; init; }

    public IReadOnlyDictionary<string, int> CandidatesByCapability { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<FeedbackFeatureCandidate> Candidates { get; init; } =
        Array.Empty<FeedbackFeatureCandidate>();

    public string JsonlPath { get; init; } = "learning/feedback/learning-feedback-feature-candidates.jsonl";

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>运行时反馈到离线数据集的质量与 readiness 报告。</summary>
public sealed class LearningFeedbackQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public int FeedbackCount { get; init; }

    public int PendingReviewCount { get; init; }

    public int ApprovedCount { get; init; }

    public int RejectedCount { get; init; }

    public int NeedsRedactionCount { get; init; }

    public int NeedsEvidenceCount { get; init; }

    public int MetadataOnlyCount { get; init; }

    public int TrainingDisabledCount { get; init; }

    public int FeatureCandidateCount { get; init; }

    public IReadOnlyDictionary<string, int> CandidateCountByCapability { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> CandidateCountByLabelKind { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public double RedactionCoverageRate { get; init; }

    public double ReviewCoverageRate { get; init; }

    public IReadOnlyList<LearningFeedbackDatasetReadiness> ApprovedDatasetReadiness { get; init; } =
        Array.Empty<LearningFeedbackDatasetReadiness>();

    public int ExistingPolicyFeatureCount { get; init; }

    public int ExistingRankingPairCount { get; init; }

    public int ExistingRouterIntentExampleCount { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>已审核反馈数据集准入门禁报告；只判断离线候选是否可进入训练集。</summary>
public sealed class LearningApprovedFeedbackDatasetGateReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public bool Passed { get; init; }

    public int ApprovedCount { get; init; }

    public double RedactionCoverageRate { get; init; }

    public int FeatureCandidateCount { get; init; }

    public int TrainableCandidateCount { get; init; }

    public int SmokeExcludedCount { get; init; }

    public int DisabledTrainingUseCount { get; init; }

    public IReadOnlyDictionary<string, int> CandidateCountByCapability { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> PositiveLabelCountByCapability { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> NegativeLabelCountByCapability { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;
}

/// <summary>单个 capability 的 feedback dataset readiness。</summary>
public sealed class LearningFeedbackDatasetReadiness
{
    public string CapabilityId { get; init; } = string.Empty;

    public int ApprovedCandidateCount { get; init; }

    public int PositiveLabelCount { get; init; }

    public int NegativeLabelCount { get; init; }

    public int MetadataOnlyCount { get; init; }

    public int NeedsMoreEvidenceCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

/// <summary>由人工审核或运行反馈生成的学习记录。</summary>
public sealed class ContextLearningRecord
{
    public string RecordId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string SourceKind { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string? CandidateId { get; init; }

    public string? ReviewId { get; init; }

    public string EventKind { get; init; } = string.Empty;

    public ContextFeedbackSignal Signal { get; init; } = ContextFeedbackSignal.Positive;

    public ContextFailureType FailureType { get; init; } = ContextFailureType.None;

    public string Reason { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public double Importance { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>可用于回放、人工分析或后续评测的学习案例。</summary>
public sealed class ContextLearningCase
{
    public string CaseId { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string SourceRecordId { get; init; } = string.Empty;

    public string SourceKind { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string CaseKind { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string InputSummary { get; init; } = string.Empty;

    public string ExpectedBehavior { get; init; } = string.Empty;

    public ContextFeedbackSignal Signal { get; init; } = ContextFeedbackSignal.Positive;

    public ContextFailureType FailureType { get; init; } = ContextFailureType.None;

    public string CorrectionReason { get; init; } = string.Empty;

    public ContextLearningCaseStatus Status { get; init; } = ContextLearningCaseStatus.Draft;

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> PositiveRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NegativeRefs { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>晋升反馈信号查询条件。</summary>
public sealed class PromotionFeedbackSignalQuery
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public string? CandidateId { get; init; }

    public string? Action { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>学习记录查询条件。</summary>
public sealed class ContextLearningRecordQuery
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public ContextFeedbackSignal? Signal { get; init; }

    public ContextFailureType? FailureType { get; init; }

    public string? SourceKind { get; init; }

    public string? SourceId { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>学习案例查询条件。</summary>
public sealed class ContextLearningCaseQuery
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public ContextFeedbackSignal? Signal { get; init; }

    public ContextFailureType? FailureType { get; init; }

    public ContextLearningCaseStatus? Status { get; init; }

    public string? CaseKind { get; init; }

    public string? SourceRecordId { get; init; }

    public int Limit { get; init; } = 20;

    public int Offset { get; init; }
}

/// <summary>从学习记录生成学习案例的请求。</summary>
public sealed class ContextLearningCaseGenerationRequest
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public string? SessionId { get; init; }

    public ContextFeedbackSignal? Signal { get; init; }

    public ContextFailureType? FailureType { get; init; }

    public int Limit { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>学习案例生成结果。</summary>
public sealed class ContextLearningCaseGenerationResult
{
    public int RecordsScanned { get; init; }

    public int Created { get; init; }

    public int Existing { get; init; }

    public IReadOnlyList<ContextLearningCase> Cases { get; init; } = Array.Empty<ContextLearningCase>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>学习案例状态流转请求。</summary>
public sealed class ContextLearningCaseStatusUpdateRequest
{
    public string OperationId { get; init; } = string.Empty;

    public string Reviewer { get; init; } = "manual";

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>学习案例状态流转响应。</summary>
public sealed class ContextLearningCaseStatusUpdateResponse
{
    public string OperationId { get; init; } = string.Empty;

    public string CaseId { get; init; } = string.Empty;

    public ContextLearningCaseStatus Status { get; init; } = ContextLearningCaseStatus.Draft;

    public ContextLearningCase Case { get; init; } = new();
}

/// <summary>上下文学习摘要。</summary>
public sealed class ContextLearningSummary
{
    public string? WorkspaceId { get; init; }

    public string? CollectionId { get; init; }

    public int RecordCount { get; init; }

    public int CaseCount { get; init; }

    public int PositiveCount { get; init; }

    public int NegativeCount { get; init; }

    public int StaleCount { get; init; }

    public int DraftCaseCount { get; init; }

    public int CandidateCaseCount { get; init; }

    public int ActiveRegressionCaseCount { get; init; }

    public int ArchivedCaseCount { get; init; }

    public int RejectedCaseCount { get; init; }

    public IReadOnlyDictionary<ContextFailureType, int> FailureTypeCounts { get; init; } =
        new Dictionary<ContextFailureType, int>();

    public IReadOnlyDictionary<string, int> CaseKindCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>由人工 review history 聚合出的策略反馈样本；只读用于导出和人工分析。</summary>
public sealed class PolicyFeedbackRecord
{
    public string FeedbackRecordId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string SourceType { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Label { get; init; } = PolicyFeedbackLabels.Neutral;

    public string Reason { get; init; } = string.Empty;

    public IReadOnlyList<string> PositiveRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NegativeRefs { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string TargetLayer { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public string Reviewer { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>策略反馈数据集，聚合 promotion / stable / constraint review history。</summary>
public sealed class PolicyFeedbackDataset
{
    public string DatasetId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Scope { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<PolicyFeedbackRecord> Records { get; init; } = Array.Empty<PolicyFeedbackRecord>();

    public int PositiveCount { get; init; }

    public int NegativeCount { get; init; }

    public int NeutralCount { get; init; }

    public IReadOnlyDictionary<string, int> SourceTypes { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string PolicyVersion { get; init; } = string.Empty;

    public string EvalBaselineRef { get; init; } = string.Empty;
}

public static class PolicyFeedbackLabels
{
    public const string Positive = "Positive";
    public const string Negative = "Negative";
    public const string Neutral = "Neutral";
}

/// <summary>供策略学习分析使用的只读特征样本；不参与在线 retrieval / package 决策。</summary>
public sealed class ContextPolicyFeatureExample
{
    public string ExampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string SourceType { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string InputSummary { get; init; } = string.Empty;

    public string CandidateId { get; init; } = string.Empty;

    public string CandidateKind { get; init; } = string.Empty;

    public string CandidateLayer { get; init; } = string.Empty;

    public string CandidateStatus { get; init; } = string.Empty;

    public double CandidateImportance { get; init; }

    public double CandidateRecency { get; init; }

    public IReadOnlyList<string> ChannelSources { get; init; } = Array.Empty<string>();

    public int RelationPathCount { get; init; }

    public double KeywordMatchScore { get; init; }

    public double SemanticAnchorMatchScore { get; init; }

    public double ShortTermMatchScore { get; init; }

    public double StableMatchScore { get; init; }

    public double ConstraintMatchScore { get; init; }

    public double LifecycleRisk { get; init; }

    public bool Selected { get; init; }

    public bool Accepted { get; init; }

    public bool Rejected { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = Array.Empty<string>();

    public string PolicyVersion { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>用于离线 ranking 分析的正负候选 pair，不参与在线排序。</summary>
public sealed class RankingPairExample
{
    public string Query { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string PositiveCandidateId { get; init; } = string.Empty;

    public string NegativeCandidateId { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string EvalSampleId { get; init; } = string.Empty;

    public Dictionary<string, string> FeatureSnapshot { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Learning Feature Dataset 的只读汇总视图。</summary>
public sealed class LearningFeatureDataset
{
    public string DatasetId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public IReadOnlyList<ContextPolicyFeatureExample> FeatureExamples { get; init; } =
        Array.Empty<ContextPolicyFeatureExample>();

    public IReadOnlyList<RankingPairExample> RankingPairs { get; init; } =
        Array.Empty<RankingPairExample>();

    public IReadOnlyList<ContextPolicyFeatureExample> RouterIntentExamples { get; init; } =
        Array.Empty<ContextPolicyFeatureExample>();

    public int FeatureCount { get; init; }

    public int RankingPairCount { get; init; }

    public int RouterIntentExampleCount { get; init; }

    public IReadOnlyDictionary<string, int> LabelDistribution { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> SourceTypeDistribution { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public string LatestExportPath { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;
}

/// <summary>Learning Feature Dataset 导出结果。</summary>
public sealed class LearningFeatureExportResult
{
    public DateTimeOffset ExportedAt { get; init; }

    public string OutputDirectory { get; init; } = string.Empty;

    public string PolicyFeedbackFeaturesPath { get; init; } = string.Empty;

    public string RankingPairsPath { get; init; } = string.Empty;

    public string RouterIntentExamplesPath { get; init; } = string.Empty;

    public int FeatureCount { get; init; }

    public int RankingPairCount { get; init; }

    public int RouterIntentExampleCount { get; init; }

    public string PolicyVersion { get; init; } = string.Empty;
}

/// <summary>Learning feature dataset quality report；只读诊断，不参与在线策略。</summary>
public sealed class LearningDatasetQualityReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string FeatureDirectory { get; init; } = string.Empty;

    public int PolicyFeedbackFeatureCount { get; init; }

    public int RankingPairCount { get; init; }

    public int RouterIntentExampleCount { get; init; }

    public int PositiveCount { get; init; }

    public int NegativeCount { get; init; }

    public int NeutralCount { get; init; }

    public IReadOnlyDictionary<string, int> SourceTypeCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> ModeCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> IntentCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> LabelCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> DataRisks { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, LearningDatasetTaskReadiness> TaskReadiness { get; init; } =
        new Dictionary<string, LearningDatasetTaskReadiness>(StringComparer.OrdinalIgnoreCase);

    public string RecommendedNextAction { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class LearningDatasetTaskReadiness
{
    public string TaskName { get; init; } = string.Empty;

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public string RecommendedNextAction { get; init; } = string.Empty;
}

public static class LearningDatasetTaskNames
{
    public const string RouterIntentClassifier = "RouterIntentClassifier";
    public const string CandidateReranker = "CandidateReranker";
    public const string PromotionJudge = "PromotionJudge";
    public const string ConstraintGapJudge = "ConstraintGapJudge";
    public const string AttentionScorer = "AttentionScorer";
}

public static class LearningDatasetReadinessStatus
{
    public const string Ready = "Ready";
    public const string Limited = "Limited";
    public const string NotReady = "NotReady";
}

public static class LearningDatasetDataRisks
{
    public const string NoPolicyFeedback = "NoPolicyFeedback";
    public const string EvalOnlyDataset = "EvalOnlyDataset";
    public const string ClassImbalance = "ClassImbalance";
    public const string MissingNegativeSamples = "MissingNegativeSamples";
    public const string LowIntentCoverage = "LowIntentCoverage";
    public const string LowModeCoverage = "LowModeCoverage";
}

public sealed class LearningBaselineSplitSummary
{
    public string Strategy { get; init; } = string.Empty;

    public string GroupKey { get; init; } = string.Empty;

    public int TrainGroupCount { get; init; }

    public int TestGroupCount { get; init; }

    public int TrainExampleCount { get; init; }

    public int TestExampleCount { get; init; }
}

public static class RouterIntentClassifierBaselineNames
{
    public const string ExistingRuleBasedRouterBaseline = "ExistingRuleBasedRouterBaseline";
    public const string TokenCentroidRouterBaseline = "TokenCentroidRouterBaseline";
}

/// <summary>Router intent classifier 的离线预测结果；只用于报告，不替换 runtime router。</summary>
public sealed class RouterIntentClassifierPrediction
{
    public string Intent { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public bool Abstained { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<RouterIntentShadowTopPrediction> TopPredictions { get; init; } =
        Array.Empty<RouterIntentShadowTopPrediction>();
}

public sealed class RouterIntentShadowTopPrediction
{
    public string Intent { get; init; } = string.Empty;

    public double Confidence { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class LifecycleAwareFeatureSet
{
    public bool IsDeprecated { get; init; }

    public bool IsSuperseded { get; init; }

    public bool IsHistorical { get; init; }

    public bool IsRejected { get; init; }

    public bool HasReplacement { get; init; }

    public bool HasSupersedesRelation { get; init; }

    public double VersionDistance { get; init; }

    public bool IsCurrentVersion { get; init; }

    public double LifecycleConfidence { get; init; }

    public bool HistoricalSectionOnly { get; init; }
}

/// <summary>统一学习 readiness registry 中的 capability id。</summary>
public static class ShadowCapabilityIds
{
    public const string RelationGovernance = nameof(RelationGovernance);

    public const string JobQueuePostgres = nameof(JobQueuePostgres);

    public const string VectorPostgresProvider = nameof(VectorPostgresProvider);

    public const string GraphExpansion = nameof(GraphExpansion);

    public const string VectorRetrieval = nameof(VectorRetrieval);

    public const string HybridRetrievalPreview = nameof(HybridRetrievalPreview);

    public const string DatasetV2Stress = nameof(DatasetV2Stress);

    public const string VectorV4ReadinessRecheck = nameof(VectorV4ReadinessRecheck);

    public const string GuardedFormalRetrievalPreview = nameof(GuardedFormalRetrievalPreview);

    public const string VectorShadowPackageComparison = nameof(VectorShadowPackageComparison);

    public const string ScopedFormalPreviewOptIn = nameof(ScopedFormalPreviewOptIn);

    public const string LimitedFormalPreviewObservation = nameof(LimitedFormalPreviewObservation);

    public const string VectorFormalPreviewFreeze = nameof(VectorFormalPreviewFreeze);

    public const string ScopedRuntimeExperimentHarnessFreeze = nameof(ScopedRuntimeExperimentHarnessFreeze);

    public const string RouterIntentClassifier = nameof(RouterIntentClassifier);

    public const string CandidateReranker = nameof(CandidateReranker);

    public const string AttentionRerank = nameof(AttentionRerank);

    public const string PlanningProposal = nameof(PlanningProposal);

    public const string PromotionJudge = nameof(PromotionJudge);

    public const string ConstraintGapJudge = nameof(ConstraintGapJudge);

    public const string Qwen3EmbeddingProvider = nameof(Qwen3EmbeddingProvider);

    public const string CurrentEmbeddingProvider = nameof(CurrentEmbeddingProvider);
}

/// <summary>runtime 变更 readiness gate 的单项检查结果。</summary>
public sealed class LearningRuntimeChangeReadinessGateCheck
{
    public string CapabilityId { get; init; } = string.Empty;

    public string Condition { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>统一学习 runtime 变更闸门；只验证冻结状态，不启用任何 capability。</summary>
public sealed class LearningRuntimeChangeReadinessGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public bool Passed { get; init; }

    public string RegistryReportPath { get; init; } = string.Empty;

    public IReadOnlyList<LearningRuntimeChangeReadinessGateCheck> Checks { get; init; } =
        Array.Empty<LearningRuntimeChangeReadinessGateCheck>();

    public IReadOnlyList<string> FailedConditions { get; init; } = Array.Empty<string>();

    public string Recommendation { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;
}

public static class ContextCoreFoundationFreezeRecommendations
{
    public const string ReadyForReleaseCandidate = nameof(ReadyForReleaseCandidate);
    public const string BlockedByMissingReport = nameof(BlockedByMissingReport);
    public const string BlockedByMissingDoc = nameof(BlockedByMissingDoc);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByP15Gate = nameof(BlockedByP15Gate);
    public const string BlockedByRuntimeSwitch = nameof(BlockedByRuntimeSwitch);
    public const string BlockedByFormalRetrieval = nameof(BlockedByFormalRetrieval);
    public const string BlockedByPackageMutation = nameof(BlockedByPackageMutation);
    public const string KeepFrozenPreviewOnly = nameof(KeepFrozenPreviewOnly);
}

public static class FoundationReproducibilityRecommendations
{
    public const string ReadyForReleaseCandidateReproduction = nameof(ReadyForReleaseCandidateReproduction);
    public const string BlockedByMissingFoundationGate = nameof(BlockedByMissingFoundationGate);
    public const string BlockedByMissingRuntimeChangeGate = nameof(BlockedByMissingRuntimeChangeGate);
    public const string BlockedByP15Gate = nameof(BlockedByP15Gate);
    public const string BlockedByLocalSecret = nameof(BlockedByLocalSecret);
    public const string BlockedByRuntimeSwitch = nameof(BlockedByRuntimeSwitch);
    public const string BlockedByFormalRetrieval = nameof(BlockedByFormalRetrieval);
    public const string BlockedByPackageMutation = nameof(BlockedByPackageMutation);
    public const string BlockedByMissingCriticalReport = nameof(BlockedByMissingCriticalReport);
}

public sealed class ContextCoreFoundationFreezeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; } = ContextCoreFoundationFreezeRecommendations.KeepFrozenPreviewOnly;

    public string ContextCoreFoundation { get; init; } = "NotFrozen";

    public string StorageFoundation { get; init; } = "NotFrozen";

    public string VectorFoundation { get; init; } = "KeepPreviewOnly";

    public string NextAllowedPhase { get; init; } = string.Empty;

    public string RelationGovernanceStatus { get; init; } = string.Empty;

    public string LearningFeedbackStatus { get; init; } = string.Empty;

    public string JobQueueStatus { get; init; } = string.Empty;

    public string VectorPostgresProviderStatus { get; init; } = string.Empty;

    public string VectorFormalPreviewStatus { get; init; } = string.Empty;

    public string RuntimeChangeGateStatus { get; init; } = string.Empty;

    public string P15GateStatus { get; init; } = string.Empty;

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public int MissingReportCount { get; init; }

    public int MissingDocCount { get; init; }

    public IReadOnlyDictionary<string, string> StorageProviderReadiness { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> VectorProviderReadiness { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> VectorFormalPreviewReadiness { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, bool> ControlRoomCoverage { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, bool> DocsCoverage { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, bool> GeneratedReportCoverage { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FoundationReproducibilityReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ReproducibilityPassed { get; init; }

    public string Recommendation { get; init; } =
        FoundationReproducibilityRecommendations.BlockedByMissingCriticalReport;

    public string BuildCommand { get; init; } = "dotnet build";

    public string TestCommand { get; init; } = "dotnet test";

    public string P15Command { get; init; } = "scripts/eval-gate-p15.ps1";

    public string RuntimeChangeGateCommand { get; init; } =
        "dotnet run --project src/ContextCore.ControlRoom -- eval learning-runtime-change-readiness-gate";

    public string FoundationGateCommand { get; init; } =
        "dotnet run --project src/ContextCore.ControlRoom -- eval foundation-release-candidate-gate";

    public string ReproducibilityCheckCommand { get; init; } =
        "dotnet run --project src/ContextCore.ControlRoom -- eval foundation-reproducibility-check";

    public string ExpectedOutputSummary { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GitStatusCategories { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, bool> CriticalReportCoverage { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, bool> BoundaryChecks { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public string FoundationGateStatus { get; init; } = string.Empty;

    public string RuntimeChangeGateStatus { get; init; } = string.Empty;

    public string P15GateStatus { get; init; } = string.Empty;

    public bool LocalSecretsDetected { get; init; }

    public int LocalSecretPathCount { get; init; }

    public IReadOnlyList<string> LocalSecretPaths { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class CapabilityStatus
{
    public string CapabilityId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string Recommendation { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public string SourceReportPath { get; init; } = string.Empty;

    public IReadOnlyList<string> AllowedModes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenModes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class FoundationServiceStatusResponse
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string StatusKind { get; init; } = "foundation/status";

    public bool ReadOnly { get; init; } = true;

    public string FoundationGateStatus { get; init; } = string.Empty;

    public string RuntimeChangeGateStatus { get; init; } = string.Empty;

    public string ReproducibilityStatus { get; init; } = string.Empty;

    public string VectorFormalPreviewStatus { get; init; } = string.Empty;

    public string PostgresFreezeStatus { get; init; } = string.Empty;

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool FormalPackageWritten { get; init; }

    public IReadOnlyList<CapabilityStatus> Capabilities { get; init; } = Array.Empty<CapabilityStatus>();

    public IReadOnlyDictionary<string, bool> ReportCoverage { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FoundationApiResponseEnvelope<T>
{
    public bool Success { get; init; } = true;

    public string CapabilityId { get; init; } = string.Empty;

    public string Status { get; init; } = "Ready";

    public string Recommendation { get; init; } = string.Empty;

    public T? Data { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Diagnostics { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SchemaVersion { get; init; } = "foundation-api-envelope-v1";
}

public sealed class FoundationApiSecurityDiagnosticsReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool AuthConfigured { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public bool DevelopmentMode { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public enum ServiceDeploymentProfile
{
    Development = 0,
    Service = 1,
    Production = 2
}

public sealed class FoundationServiceAuthDiagnosticsReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public ServiceDeploymentProfile DeploymentProfile { get; init; } = ServiceDeploymentProfile.Development;

    public bool AuthConfigured { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public bool RequireApiKey { get; init; }

    public string ApiKeyHeaderName { get; init; } = "X-ContextCore-Key";

    public bool DevelopmentNoAuthAllowed { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FoundationServiceDeploymentProfileGateReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool GatePassed { get; init; }

    public ServiceDeploymentProfile DeploymentProfile { get; init; } = ServiceDeploymentProfile.Development;

    public bool AuthConfigured { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public bool RequireApiKey { get; init; }

    public bool DevelopmentNoAuthAllowed { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FoundationReportNavigationEntry
{
    public string ReportId { get; init; } = string.Empty;

    public string CapabilityId { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public bool Exists { get; init; }

    public DateTimeOffset? GeneratedAt { get; init; }

    public string ContentType { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public bool SafeToExpose { get; init; }
}

public sealed class FoundationReportNavigationResponse
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int ReportCount { get; init; }

    public int ExistingReportCount { get; init; }

    public int DegradedReportCount { get; init; }

    public IReadOnlyList<string> MissingReportIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FoundationReportNavigationEntry> Reports { get; init; } =
        Array.Empty<FoundationReportNavigationEntry>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class FoundationApiEndpointContract
{
    public string Method { get; init; } = string.Empty;

    public string Route { get; init; } = string.Empty;

    public string CapabilityId { get; init; } = string.Empty;

    public string ResponseType { get; init; } = string.Empty;

    public bool UsesEnvelope { get; init; } = true;

    public bool ReadOnly { get; init; } = true;
}

public sealed class FoundationApiClientMethodContract
{
    public string MethodName { get; init; } = string.Empty;

    public string Route { get; init; } = string.Empty;

    public string ResponseType { get; init; } = string.Empty;

    public bool DeserializesEnvelope { get; init; } = true;
}

public sealed class FoundationApiContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ContractPassed { get; init; }

    public bool FreezePassed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public int EndpointCount { get; init; }

    public int ClientMethodCount { get; init; }

    public string EnvelopeSchemaVersion { get; init; } = string.Empty;

    public IReadOnlyList<string> EnvelopeSchemaFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FoundationApiEndpointContract> Endpoints { get; init; } =
        Array.Empty<FoundationApiEndpointContract>();

    public IReadOnlyList<FoundationApiClientMethodContract> ClientMethods { get; init; } =
        Array.Empty<FoundationApiClientMethodContract>();

    public string AuthMode { get; init; } = string.Empty;

    public bool AuthConfigured { get; init; }

    public bool ApiKeyConfigured { get; init; }

    public bool DevelopmentMode { get; init; }

    public bool ProductionMode { get; init; }

    public bool ProductionAuthRequired { get; init; }

    public bool ProductionAuthConfigured { get; init; }

    public bool DegradedBehaviorStable { get; init; }

    public bool MissingReportReturnsDegraded { get; init; }

    public bool ReportNavigationSchemaStable { get; init; }

    public IReadOnlyList<string> ReportNavigationSchemaFields { get; init; } = Array.Empty<string>();

    public bool ForbiddenActionsExposed { get; init; }

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool ReadOnly { get; init; } = true;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FoundationOpenApiContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public int EndpointCount { get; init; }

    public IReadOnlyList<string> EndpointIds { get; init; } = Array.Empty<string>();

    public string EnvelopeSchemaVersion { get; init; } = string.Empty;

    public string AuthScheme { get; init; } = string.Empty;

    public string ApiKeyHeaderName { get; init; } = string.Empty;

    public int ClientMethodCount { get; init; }

    public int RequestSchemaCount { get; init; }

    public int ResponseSchemaCount { get; init; }

    public int ForbiddenActionCount { get; init; }

    public bool BreakingChangeDetected { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public bool ReadOnly { get; init; } = true;

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class HostedServiceEndpointProbeResult
{
    public string Method { get; init; } = string.Empty;

    public string Route { get; init; } = string.Empty;

    public int StatusCode { get; init; }

    public bool Success { get; init; }

    public bool EnvelopeSchemaMatched { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public string Error { get; init; } = string.Empty;
}

public sealed class HostedServiceSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool SmokePassed { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ServiceDeploymentProfile DeploymentProfile { get; init; } = ServiceDeploymentProfile.Development;

    public int EndpointCount { get; init; }

    public int SuccessfulEndpointCount { get; init; }

    public int FailedEndpointCount { get; init; }

    public bool AuthPassed { get; init; }

    public bool UnauthorizedCheckPassed { get; init; }

    public bool EnvelopeSchemaMatched { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<HostedServiceEndpointProbeResult> Endpoints { get; init; } =
        Array.Empty<HostedServiceEndpointProbeResult>();
}
