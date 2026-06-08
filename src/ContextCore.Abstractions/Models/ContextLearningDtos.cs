namespace ContextCore.Abstractions;

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

public sealed class RouterIntentBaselineReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public LearningBaselineSplitSummary Split { get; init; } = new();

    public IReadOnlyList<RouterIntentBaselineResult> Baselines { get; init; } =
        Array.Empty<RouterIntentBaselineResult>();

    public string BestBaseline { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class RouterIntentBaselineResult
{
    public string BaselineName { get; init; } = string.Empty;

    public double Accuracy { get; init; }

    public double MacroF1 { get; init; }

    public IReadOnlyDictionary<string, double> PerIntentPrecision { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, double> PerIntentRecall { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ConfusionMatrix { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
}

public sealed class RankerBaselineReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public int PairCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public LearningBaselineSplitSummary Split { get; init; } = new();

    public IReadOnlyList<RankerBaselineResult> Baselines { get; init; } =
        Array.Empty<RankerBaselineResult>();

    public string BestBaseline { get; init; } = string.Empty;

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class RankerBaselineResult
{
    public string BaselineName { get; init; } = string.Empty;

    public double PairwiseAccuracy { get; init; }

    public double? Auc { get; init; }

    public double WinRateOverRule { get; init; }

    public double FalsePositiveRate { get; init; }

    public double FalseNegativeRate { get; init; }

    public IReadOnlyList<RankerBaselineFailureExample> TopFailureExamples { get; init; } =
        Array.Empty<RankerBaselineFailureExample>();
}

public sealed class RankerBaselineFailureExample
{
    public string EvalSampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string PositiveCandidateId { get; init; } = string.Empty;

    public string NegativeCandidateId { get; init; } = string.Empty;

    public double PositiveScore { get; init; }

    public double NegativeScore { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed class RankerFeatureAblationReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public int PairCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public LearningBaselineSplitSummary Split { get; init; } = new();

    public RankerBaselineResult Baseline { get; init; } = new();

    public IReadOnlyList<RankerFeatureAblationResult> Ablations { get; init; } =
        Array.Empty<RankerFeatureAblationResult>();

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class RankerFeatureAblationResult
{
    public string FeatureName { get; init; } = string.Empty;

    public string DisabledFeature { get; init; } = string.Empty;

    public double PairwiseAccuracy { get; init; }

    public double AccuracyDelta { get; init; }

    public double? Auc { get; init; }

    public double FalsePositiveRate { get; init; }

    public double FalseNegativeRate { get; init; }

    public IReadOnlyList<RankerFailureClusterSummary> FailureClusters { get; init; } =
        Array.Empty<RankerFailureClusterSummary>();

    public IReadOnlyList<RankerComparisonExample> TopFixedExamples { get; init; } =
        Array.Empty<RankerComparisonExample>();

    public IReadOnlyList<RankerComparisonExample> TopNewlyFailedExamples { get; init; } =
        Array.Empty<RankerComparisonExample>();
}

public sealed class RankerWeightSweepReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public int PairCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public LearningBaselineSplitSummary Split { get; init; } = new();

    public RankerFeatureWeights BaselineWeights { get; init; } = new();

    public RankerBaselineResult Baseline { get; init; } = new();

    public RankerWeightSweepResult BestResult { get; init; } = new();

    public IReadOnlyList<RankerWeightSweepResult> SweepResults { get; init; } =
        Array.Empty<RankerWeightSweepResult>();

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class RankerWeightSweepResult
{
    public string ConfigurationId { get; init; } = string.Empty;

    public string ParameterName { get; init; } = string.Empty;

    public double ParameterValue { get; init; }

    public RankerFeatureWeights Weights { get; init; } = new();

    public double PairwiseAccuracy { get; init; }

    public double AccuracyDelta { get; init; }

    public double? Auc { get; init; }

    public double WinRateOverBaseline { get; init; }

    public double FalsePositiveRate { get; init; }

    public double FalseNegativeRate { get; init; }

    public IReadOnlyList<RankerFailureClusterSummary> FailureClusters { get; init; } =
        Array.Empty<RankerFailureClusterSummary>();

    public IReadOnlyList<RankerComparisonExample> TopFixedExamples { get; init; } =
        Array.Empty<RankerComparisonExample>();

    public IReadOnlyList<RankerComparisonExample> TopNewlyFailedExamples { get; init; } =
        Array.Empty<RankerComparisonExample>();

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class RankerFeatureWeights
{
    public double LifecyclePenaltyWeight { get; init; }

    public double RecencyWeight { get; init; }

    public double CurrentVersionBoost { get; init; }

    public double ActiveStatusBoost { get; init; }

    public double NoiseKeywordPenalty { get; init; }

    public double RelationEvidenceBoost { get; init; }

    public double StablePreferenceBoost { get; init; }
}

public sealed class RankerFailureClusterSummary
{
    public string Cluster { get; init; } = string.Empty;

    public int Count { get; init; }

    public IReadOnlyList<string> ExampleIds { get; init; } = Array.Empty<string>();
}

public sealed class RankerComparisonExample
{
    public string EvalSampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string PositiveCandidateId { get; init; } = string.Empty;

    public string NegativeCandidateId { get; init; } = string.Empty;

    public double BaselinePositiveScore { get; init; }

    public double BaselineNegativeScore { get; init; }

    public double CandidatePositiveScore { get; init; }

    public double CandidateNegativeScore { get; init; }

    public string FailureCluster { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed class RankerResidualErrorAuditReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public int PairCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public LearningBaselineSplitSummary Split { get; init; } = new();

    public RankerBaselineResult Baseline { get; init; } = new();

    public IReadOnlyList<RankerResidualFailureDetail> Failures { get; init; } =
        Array.Empty<RankerResidualFailureDetail>();

    public IReadOnlyList<RankerResidualFailureCluster> FailureClusters { get; init; } =
        Array.Empty<RankerResidualFailureCluster>();

    public IReadOnlyList<RankerFeatureConflictSummary> FeatureConflicts { get; init; } =
        Array.Empty<RankerFeatureConflictSummary>();

    public IReadOnlyList<RankerHardNegativeRecommendation> HardNegativeRecommendations { get; init; } =
        Array.Empty<RankerHardNegativeRecommendation>();

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class RankerResidualFailureDetail
{
    public string EvalSampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string PositiveCandidateId { get; init; } = string.Empty;

    public string NegativeCandidateId { get; init; } = string.Empty;

    public double PositiveScore { get; init; }

    public double NegativeScore { get; init; }

    public double Margin { get; init; }

    public double PositiveKeywordMatchScore { get; init; }

    public double NegativeKeywordMatchScore { get; init; }

    public double PositiveSemanticAnchorMatchScore { get; init; }

    public double NegativeSemanticAnchorMatchScore { get; init; }

    public bool PositiveSelected { get; init; }

    public bool NegativeSelected { get; init; }

    public int PositiveRank { get; init; }

    public int NegativeRank { get; init; }

    public string PositiveKind { get; init; } = string.Empty;

    public string NegativeKind { get; init; } = string.Empty;

    public string PositiveSection { get; init; } = string.Empty;

    public string NegativeSection { get; init; } = string.Empty;

    public string FailureCluster { get; init; } = string.Empty;

    public string ProbableCause { get; init; } = string.Empty;
}

public sealed class RankerResidualFailureCluster
{
    public string Cluster { get; init; } = string.Empty;

    public int Count { get; init; }

    public double AverageMargin { get; init; }

    public IReadOnlyList<string> ExampleIds { get; init; } = Array.Empty<string>();

    public string ProbableCause { get; init; } = string.Empty;
}

public sealed class RankerFeatureConflictSummary
{
    public string FeatureName { get; init; } = string.Empty;

    public int FailureCount { get; init; }

    public double AveragePositiveValue { get; init; }

    public double AverageNegativeValue { get; init; }

    public double AverageDelta { get; init; }

    public string Interpretation { get; init; } = string.Empty;
}

public sealed class RankerHardNegativeRecommendation
{
    public string RecommendationType { get; init; } = string.Empty;

    public string Cluster { get; init; } = string.Empty;

    public int Count { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string SuggestedAction { get; init; } = string.Empty;

    public IReadOnlyList<string> ExampleIds { get; init; } = Array.Empty<string>();
}

/// <summary>由离线 residual audit 派生的 hard negative 样本；仅用于数据分析和后续离线实验。</summary>
public sealed class HardNegativeExample
{
    public string ExampleId { get; init; } = string.Empty;

    public string WorkspaceId { get; init; } = string.Empty;

    public string CollectionId { get; init; } = string.Empty;

    public string SourceSampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string PositiveCandidateId { get; init; } = string.Empty;

    public string NegativeCandidateId { get; init; } = string.Empty;

    public string HardNegativeType { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public Dictionary<string, string> PositiveFeatures { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> NegativeFeatures { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string ExpectedPreference { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class HardNegativeDatasetReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string SourceAuditPath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public int SourceFailureCount { get; init; }

    public int ExampleCount { get; init; }

    public IReadOnlyDictionary<string, int> TypeCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> ClusterCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<HardNegativeExample> Examples { get; init; } = Array.Empty<HardNegativeExample>();

    public string PolicyVersion { get; init; } = string.Empty;
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

public sealed class LifecycleAwareRankerReport
{
    public DateTimeOffset GeneratedAt { get; init; }

    public string InputPath { get; init; } = string.Empty;

    public int PairCount { get; init; }

    public bool Ready { get; init; }

    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<string> NotReadyReasons { get; init; } = Array.Empty<string>();

    public LearningBaselineSplitSummary Split { get; init; } = new();

    public IReadOnlyList<LifecycleAwareRankerResult> Baselines { get; init; } =
        Array.Empty<LifecycleAwareRankerResult>();

    public string BestBaseline { get; init; } = string.Empty;

    public double BaselineAccuracy { get; init; }

    public int BaselineResidualFailures { get; init; }

    public int BaselineDeprecatedNoiseFailures { get; init; }

    public bool TargetPassed { get; init; }

    public IReadOnlyList<string> TargetFailures { get; init; } = Array.Empty<string>();

    public string PolicyVersion { get; init; } = string.Empty;
}

public sealed class LifecycleAwareRankerResult
{
    public string BaselineName { get; init; } = string.Empty;

    public double PairwiseAccuracy { get; init; }

    public double? Auc { get; init; }

    public double WinRateOverSimple { get; init; }

    public double FalsePositiveRate { get; init; }

    public double FalseNegativeRate { get; init; }

    public int ResidualFailures { get; init; }

    public int DeprecatedNoiseFailures { get; init; }

    public IReadOnlyList<RankerFailureClusterSummary> FailureClusters { get; init; } =
        Array.Empty<RankerFailureClusterSummary>();

    public IReadOnlyList<RankerComparisonExample> TopFixedExamples { get; init; } =
        Array.Empty<RankerComparisonExample>();

    public IReadOnlyList<RankerComparisonExample> TopNewlyFailedExamples { get; init; } =
        Array.Empty<RankerComparisonExample>();

    public IReadOnlyList<RankerBaselineFailureExample> TopFailureExamples { get; init; } =
        Array.Empty<RankerBaselineFailureExample>();
}
