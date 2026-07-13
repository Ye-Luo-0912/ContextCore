using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

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

public static class RouterGuardedOptInGateFailureReasons
{
    public const string MissingShadowEvalReport = "MissingShadowEvalReport";
    public const string MissingTriageReport = "MissingTriageReport";
    public const string ShadowBreaksRuntimeGreaterThanFixes = "ShadowBreaksRuntimeGreaterThanFixes";
    public const string ShadowBreaksRuntimeNonZero = "ShadowBreaksRuntimeNonZero";
    public const string ShadowFixesRuntimeNotPositive = "ShadowFixesRuntimeNotPositive";
    public const string NetGainNotPositive = "NetGainNotPositive";
    public const string PerIntentRegressionNonZero = "PerIntentRegressionNonZero";
    public const string AgreementRateBelowThreshold = "AgreementRateBelowThreshold";
    public const string LowConfidenceCountAboveThreshold = "LowConfidenceCountAboveThreshold";
    public const string P15GateNotPassing = "P15GateNotPassing";
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
        [];

    public IReadOnlyList<RankerComparisonExample> TopFixedExamples { get; init; } =
        [];

    public IReadOnlyList<RankerComparisonExample> TopNewlyFailedExamples { get; init; } =
        [];

    public IReadOnlyList<RankerBaselineFailureExample> TopFailureExamples { get; init; } =
        [];
}
