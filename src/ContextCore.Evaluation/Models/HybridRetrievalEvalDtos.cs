using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

public sealed class VectorEmbeddingQualityBaselineReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public int Samples { get; init; }

    public int PairCount { get; init; }

    public int PositiveHitCount { get; init; }

    public int NegativeHitCount { get; init; }

    public double PositiveAverageSimilarity { get; init; }

    public double NegativeAverageSimilarity { get; init; }

    public double SimilaritySeparation { get; init; }

    public double MustHitRecallAt20 { get; init; }

    public double MustNotHitRiskAt20 { get; init; }

    public string EmbeddingProvider { get; init; } = string.Empty;

    public string EmbeddingModel { get; init; } = string.Empty;

    public string Recommendation { get; init; } = VectorQueryShadowRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}


/// <summary>Hybrid union scoring repair recommendation。</summary>
public static class HybridUnionScoringRepairRecommendations
{
    public const string ReadyForDatasetV2StressFreeze = nameof(ReadyForDatasetV2StressFreeze);
    public const string NeedsMoreRankingRepair = nameof(NeedsMoreRankingRepair);
    public const string BlockedByDenseRegression = nameof(BlockedByDenseRegression);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByNegativeDistractor = nameof(BlockedByNegativeDistractor);
    public const string BlockedByAnchorRegression = nameof(BlockedByAnchorRegression);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Hybrid union scoring repair 单个 profile 评估。</summary>
public sealed class HybridUnionScoringRepairProfileReport
{
    public string ProfileName { get; init; } = string.Empty;

    public int SampleCount { get; init; }

    public double RecallAfterPolicy { get; init; }

    public double HoldoutRecallAfterPolicy { get; init; }

    public double MrrAfterPolicy { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public double DenseOnlyRecall { get; init; }

    public double DenseOnlyHoldoutRecall { get; init; }

    public double RecallDeltaVsDense { get; init; }

    public double HoldoutRecallDeltaVsDense { get; init; }

    public int HybridRegressionCount { get; init; }

    public int DenseWinnerPreservedCount { get; init; }

    public int DenseWinnerLostCount { get; init; }

    public int MustHitBelowTopKCount { get; init; }

    public int NegativeDistractorOutranksMustHitCount { get; init; }

    public int AnchorRankingRegressionCount { get; init; }

    public string Recommendation { get; init; } = HybridUnionScoringRepairRecommendations.KeepPreviewOnly;
}


/// <summary>Hybrid union scoring repair preview / gate 报告。</summary>
public sealed class HybridUnionScoringRepairReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DatasetId { get; init; } = string.Empty;

    public string BestProfileName { get; init; } = string.Empty;

    public bool GatePassed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public string Recommendation { get; init; } = HybridUnionScoringRepairRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<HybridUnionScoringRepairProfileReport> Profiles { get; init; } =
        Array.Empty<HybridUnionScoringRepairProfileReport>();
}


/// <summary>Vector V4.R readiness recheck 的推荐结论；通过也只允许进入 guarded formal preview。</summary>
public static class VectorV4ReadinessRecheckRecommendations
{
    public const string ReadyForGuardedFormalPreview = nameof(ReadyForGuardedFormalPreview);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByLegacyRisk = nameof(BlockedByLegacyRisk);
    public const string BlockedByDatasetV2Stress = nameof(BlockedByDatasetV2Stress);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByRuntimeChangeGate = nameof(BlockedByRuntimeChangeGate);
    public const string BlockedByProviderParity = nameof(BlockedByProviderParity);
}


/// <summary>Vector V4.R readiness recheck；只允许产生 guarded formal preview 输入，不启用正式 retrieval。</summary>
public sealed class VectorV4ReadinessRecheckReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool RecheckPassed { get; init; }

    public string Recommendation { get; init; } = VectorV4ReadinessRecheckRecommendations.KeepPreviewOnly;

    public string LegacyVectorStatus { get; init; } = "Unknown";

    public string DatasetV2SmallStatus { get; init; } = "Unknown";

    public string DatasetV2StressStatus { get; init; } = "Unknown";

    public string PgVectorProviderStatus { get; init; } = "Unknown";

    public string Qwen3ProviderComparisonStatus { get; init; } = "Unknown";

    public string HybridRetrievalStatus { get; init; } = "Unknown";

    public string HybridScoringRepairStatus { get; init; } = "Unknown";

    public string RuntimeChangeGateStatus { get; init; } = "Unknown";

    public string BestPreviewProfile { get; init; } = string.Empty;

    public double DatasetV2StressRecall { get; init; }

    public double DatasetV2HoldoutRecall { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public int LeakageIssueCount { get; init; }

    public double AnchorDominanceScore { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool UseForRuntime { get; init; }

    public bool ReadyForGuardedFormalPreview { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Shadow package comparison recommendation。</summary>
public static class VectorShadowPackageComparisonRecommendations
{
    public const string ReadyForScopedFormalPreviewOptIn = nameof(ReadyForScopedFormalPreviewOptIn);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByTokenBudgetRegression = nameof(BlockedByTokenBudgetRegression);
    public const string BlockedByConstraintCoverageRegression = nameof(BlockedByConstraintCoverageRegression);
}


/// <summary>Shadow package comparison 报告；不写正式 package，不改变 runtime。</summary>
public sealed class VectorShadowPackageComparisonReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ComparisonPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int ShadowPackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool ShadowPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = VectorShadowPackageComparisonRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
