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

    /// <summary>撤销：声明 <see cref="LearningFeedbackEvent.RevokesFeedbackId"/> 指向的反馈事件作废。</summary>
    public const string Revoke = nameof(Revoke);

    /// <summary>失败归因：证据存在但未进入候选集（召回漏失）。</summary>
    public const string EvidenceNotRecalled = nameof(EvidenceNotRecalled);

    /// <summary>失败归因：已召回但被分配器裁掉（排序/分配漏失）。</summary>
    public const string RecalledNotSelected = nameof(RecalledNotSelected);

    /// <summary>失败归因：已选中进入上下文但模型未使用（利用漏失）。</summary>
    public const string SelectedNotUsed = nameof(SelectedNotUsed);

    /// <summary>失败归因：工具调用自身失败（权限/超时/格式等，与证据无关）。</summary>
    public const string ToolFailed = nameof(ToolFailed);
}

/// <summary>反馈事件携带的结构化工具/任务结果摘要；只记录身份与成败，不记录正文。</summary>
public sealed class FeedbackToolResult
{
    public string ToolName { get; set; } = string.Empty;

    public bool Succeeded { get; set; }

    /// <summary>工具结果涉及的实体/候选 ID；不含结果正文。</summary>
    public IReadOnlyList<string> EntityIds { get; set; } = Array.Empty<string>();
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

    /// <summary>事件模型版本；后续字段演进时递增，供离线消费方按版本解析。</summary>
    public int EventSchemaVersion { get; set; } = 1;

    /// <summary>被反馈的决策请求 ID（对应 ContextDecisionResult.RequestId）。</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>被反馈决策使用的策略版本。</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>决策涉及的查询 ID 列表。</summary>
    public IReadOnlyList<string> QueryIds { get; set; } = Array.Empty<string>();

    /// <summary>本次决策召回（进入候选集）的候选 ID 列表。</summary>
    public IReadOnlyList<string> CandidateIds { get; set; } = Array.Empty<string>();

    /// <summary>本次决策最终选中的候选 ID 列表。</summary>
    public IReadOnlyList<string> SelectedIds { get; set; } = Array.Empty<string>();

    /// <summary>决策伴随的工具/任务结果摘要；不含正文。</summary>
    public IReadOnlyList<FeedbackToolResult> ToolResults { get; set; } = Array.Empty<FeedbackToolResult>();

    /// <summary>撤销目标：被本事件作废的反馈事件 ID。</summary>
    public string RevokesFeedbackId { get; set; } = string.Empty;

    /// <summary>撤销发生时间；为空时服务端按创建时间补齐。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

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

    /// <summary>事件模型版本；后续字段演进时递增，供离线消费方按版本解析。</summary>
    public int EventSchemaVersion { get; set; } = 1;

    /// <summary>被反馈的决策请求 ID（对应 ContextDecisionResult.RequestId）。</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>被反馈决策使用的策略版本。</summary>
    public string PolicyVersion { get; set; } = string.Empty;

    /// <summary>决策涉及的查询 ID 列表。</summary>
    public IReadOnlyList<string> QueryIds { get; set; } = Array.Empty<string>();

    /// <summary>本次决策召回（进入候选集）的候选 ID 列表。</summary>
    public IReadOnlyList<string> CandidateIds { get; set; } = Array.Empty<string>();

    /// <summary>本次决策最终选中的候选 ID 列表。</summary>
    public IReadOnlyList<string> SelectedIds { get; set; } = Array.Empty<string>();

    /// <summary>决策伴随的工具/任务结果摘要；不含正文。</summary>
    public IReadOnlyList<FeedbackToolResult> ToolResults { get; set; } = Array.Empty<FeedbackToolResult>();

    /// <summary>撤销目标：被本事件作废的反馈事件 ID。</summary>
    public string RevokesFeedbackId { get; set; } = string.Empty;

    /// <summary>撤销发生时间；为空时服务端按创建时间补齐。</summary>
    public DateTimeOffset? RevokedAt { get; set; }

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

    public string TrainingUse { get; init; } = "disabled_until_evidence_ready"; // was "disabled_until_review"

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

/// <summary>数据删除请求：指定反馈事件不再进入任何后续快照/数据集。</summary>
public sealed class FeedbackDeletionRequest
{
    public string FeedbackId { get; init; } = string.Empty;

    public DateTimeOffset RequestedAt { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>快照内每个事件的训练/调参/评测归属。</summary>
public enum LearningSnapshotSplit
{
    Train = 0,
    Dev = 1,
    Eval = 2
}

/// <summary>
/// 从反馈事件生成的不可变快照；内容寻址（相同输入产出相同 SnapshotId），
/// 记录数据 lineage（源事件 ID、删除/撤销排除、特征版本）与训练/评测隔离归属。
/// 任何基于快照的训练结果都必须能追到 SnapshotId。
/// </summary>
public sealed class LearningFeedbackSnapshot
{
    public string SnapshotId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>特征版本；特征 schema 演进时递增，快照按版本解析。</summary>
    public string FeatureVersion { get; init; } = string.Empty;

    /// <summary>快照针对的策略版本；为空表示覆盖全部策略版本。</summary>
    public string PolicyVersion { get; init; } = string.Empty;

    /// <summary>纳入快照的反馈事件（不可变视图）。</summary>
    public IReadOnlyList<LearningFeedbackEvent> Events { get; init; } = Array.Empty<LearningFeedbackEvent>();

    /// <summary>lineage：全部输入事件 ID（含被排除的）。</summary>
    public IReadOnlyList<string> SourceEventIds { get; init; } = Array.Empty<string>();

    /// <summary>lineage：因删除请求被排除的事件 ID。</summary>
    public IReadOnlyList<string> DeletedEventIds { get; init; } = Array.Empty<string>();

    /// <summary>lineage：因被撤销而排除的事件 ID。</summary>
    public IReadOnlyList<string> RevokedEventIds { get; init; } = Array.Empty<string>();

    /// <summary>训练/评测隔离归属：事件 ID → 分桶。</summary>
    public IReadOnlyDictionary<string, LearningSnapshotSplit> SplitAssignment { get; init; }
        = new Dictionary<string, LearningSnapshotSplit>();

    /// <summary>调参分桶百分比（0-100）；训练/调参/评测互不重叠。</summary>
    public int DevPercent { get; init; }

    /// <summary>评测分桶百分比（0-100）；供校验重算指纹使用。</summary>
    public int EvalPercent { get; init; }

    public int TrainCount { get; init; }

    public int DevCount { get; init; }

    public int EvalCount { get; init; }

    /// <summary>输入指纹（特征版本 + 策略版本 + 全部源事件 ID + 排除 + 切分比例）。</summary>
    public string LineageSignature { get; init; } = string.Empty;
}

