using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;


/// <summary>Guarded formal retrieval preview / gate；只输出 would-change 结果，不写正式 package。</summary>
public sealed class GuardedFormalRetrievalPreviewReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PreviewPassed { get; init; }

    public bool GatePassed { get; init; }

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public bool V4RecheckPassed { get; init; }

    public int SampleCount { get; init; }

    public int QueryCount { get; init; }

    public int BaselineCandidateCount { get; init; }

    public int PreviewVectorCandidateCount { get; init; }

    public int WouldAddCount { get; init; }

    public int WouldRemoveCount { get; init; }

    public int WouldRerankCount { get; init; }

    public int WouldChangeTargetSectionCount { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = GuardedFormalRetrievalPreviewRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Scoped formal preview opt-in recommendation。</summary>
public static class ScopedFormalPreviewOptInRecommendations
{
    public const string ReadyForLimitedFormalPreviewObservation = nameof(ReadyForLimitedFormalPreviewObservation);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByMissingGate = nameof(BlockedByMissingGate);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
}


/// <summary>Scoped formal preview opt-in report；只写 shadow artifact，不写正式 package。</summary>
public sealed class ScopedFormalPreviewOptInReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool PlanPassed { get; init; }

    public bool SmokePassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.Off;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public string SelectedWorkspaceId { get; init; } = string.Empty;

    public string SelectedCollectionId { get; init; } = string.Empty;

    public string SelectedEvalScope { get; init; } = string.Empty;

    public string NonAllowlistedWorkspaceId { get; init; } = string.Empty;

    public string NonAllowlistedCollectionId { get; init; } = string.Empty;

    public string NonAllowlistedEvalScope { get; init; } = string.Empty;

    public int ScopeCount { get; init; }

    public int AllowlistedScopeCount { get; init; }

    public bool NonAllowlistedScopeChecked { get; init; }

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string RollbackInstruction { get; init; } = string.Empty;

    public string Recommendation { get; init; } = ScopedFormalPreviewOptInRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> GateDependencySummary { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Limited formal preview observation recommendation。</summary>
public static class LimitedFormalPreviewObservationRecommendations
{
    public const string ReadyForFormalPreviewFreeze = nameof(ReadyForFormalPreviewFreeze);
    public const string NeedsMoreObservation = nameof(NeedsMoreObservation);
    public const string BlockedByRisk = nameof(BlockedByRisk);
    public const string BlockedByFormalOutputChange = nameof(BlockedByFormalOutputChange);
    public const string BlockedByPackageOutputChange = nameof(BlockedByPackageOutputChange);
    public const string BlockedByPackingPolicyChange = nameof(BlockedByPackingPolicyChange);
    public const string BlockedByRuntimeMutation = nameof(BlockedByRuntimeMutation);
    public const string BlockedByScopeLeak = nameof(BlockedByScopeLeak);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
}


/// <summary>Limited formal preview observation report；聚合多轮 preview-only package comparison。</summary>
public sealed class LimitedFormalPreviewObservationReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ObservationPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Mode { get; init; } = ScopedFormalPreviewOptInModes.PreviewOnly;

    public string ProfileName { get; init; } = HybridUnionScoringRepairProfiles.PostScoringRiskGatedV1;

    public int MinimumObservationRunCount { get; init; }

    public int ObservationRunCount { get; init; }

    public IReadOnlyList<string> WorkspaceAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CollectionAllowlist { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalScopeAllowlist { get; init; } = Array.Empty<string>();

    public int PreviewPackageCount { get; init; }

    public int BaselinePackageCount { get; init; }

    public int CandidateAddCount { get; init; }

    public int CandidateRemoveCount { get; init; }

    public int CandidateUnchangedCount { get; init; }

    public int SectionChangedCount { get; init; }

    public int TokenDeltaTotal { get; init; }

    public int TokenDeltaMax { get; init; }

    public int TokenDeltaP95 { get; init; }

    public double ConstraintCoverageDelta { get; init; }

    public double RelationCoverageDelta { get; init; }

    public int RiskAfterPolicy { get; init; }

    public int MustNotHitRiskAfterPolicy { get; init; }

    public int LifecycleRiskAfterPolicy { get; init; }

    public int FormalOutputChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool RuntimeMutated { get; init; }

    public int NonAllowlistedScopeLeakCount { get; init; }

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public string Recommendation { get; init; } = LimitedFormalPreviewObservationRecommendations.KeepPreviewOnly;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}


/// <summary>Planning shadow 与 legacy retrieval 的质量对比报告；不参与正式 retrieval。</summary>
public sealed class PlanningShadowQualityReport
{
    public string ReportId { get; init; } = string.Empty;

    public string SampleSet { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TotalSamples { get; init; }

    public PlanningShadowQualityGroup Global { get; init; } = new();

    public IReadOnlyDictionary<string, PlanningShadowQualityGroup> ModeBreakdown { get; init; } =
        new Dictionary<string, PlanningShadowQualityGroup>();

    public IReadOnlyDictionary<string, PlanningShadowQualityGroup> IntentBreakdown { get; init; } =
        new Dictionary<string, PlanningShadowQualityGroup>();

    public PlanningShadowQualityRecommendation Recommendation { get; init; } = new();

    public IReadOnlyList<PlanningShadowQualitySample> Samples { get; init; } = Array.Empty<PlanningShadowQualitySample>();
}

public sealed class PlanningShadowQualityGroup
{
    public string Key { get; init; } = string.Empty;

    public int TotalSamples { get; init; }

    public double LegacyPassRate { get; init; }

    public double ShadowPassRate { get; init; }

    public double PassRateDelta { get; init; }

    public double LegacyRecall3 { get; init; }

    public double ShadowRecall3 { get; init; }

    public double Recall3Delta { get; init; }

    public double LegacyRecall5 { get; init; }

    public double ShadowRecall5 { get; init; }

    public double Recall5Delta { get; init; }

    public double LegacyRecall10 { get; init; }

    public double ShadowRecall10 { get; init; }

    public double Recall10Delta { get; init; }

    public double LegacyMrr { get; init; }

    public double ShadowMrr { get; init; }

    public double MrrDelta { get; init; }

    public double LegacyConstraintHitRate { get; init; }

    public double ShadowConstraintHitRate { get; init; }

    public double ConstraintHitDelta { get; init; }

    public double LegacyEntityHitRate { get; init; }

    public double ShadowEntityHitRate { get; init; }

    public double EntityHitDelta { get; init; }

    public double LegacyUncertaintyHitRate { get; init; }

    public double ShadowUncertaintyHitRate { get; init; }

    public double UncertaintyHitDelta { get; init; }

    public int LegacyMustNotHitViolationCount { get; init; }

    public int ShadowMustNotHitViolationCount { get; init; }

    public int MustNotHitViolationDelta { get; init; }

    public int LifecycleViolationCount { get; init; }

    public double BudgetPressureDelta { get; init; }

    public double SelectedCountDelta { get; init; }

    public double MustHitTokenShareDelta { get; init; }

    public int ImprovedSampleCount { get; init; }

    public int RegressedSampleCount { get; init; }

    public int MustHitGainedCount { get; init; }

    public int MustHitLostCount { get; init; }

    public int ConstraintGainedCount { get; init; }

    public int ConstraintLostCount { get; init; }

    public int EntityGainedCount { get; init; }

    public int EntityLostCount { get; init; }

    public int UncertaintyGainedCount { get; init; }

    public int UncertaintyLostCount { get; init; }
}

public sealed class PlanningShadowQualitySample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public bool Improved { get; init; }

    public bool Regressed { get; init; }

    public bool LegacyPassed { get; init; }

    public bool ShadowPassed { get; init; }

    public double LegacyRecall3 { get; init; }

    public double ShadowRecall3 { get; init; }

    public double Recall3Delta { get; init; }

    public double LegacyRecall5 { get; init; }

    public double ShadowRecall5 { get; init; }

    public double Recall5Delta { get; init; }

    public double LegacyRecall10 { get; init; }

    public double ShadowRecall10 { get; init; }

    public double Recall10Delta { get; init; }

    public double LegacyMrr { get; init; }

    public double ShadowMrr { get; init; }

    public double MrrDelta { get; init; }

    public double LegacyConstraintHitRate { get; init; }

    public double ShadowConstraintHitRate { get; init; }

    public double ConstraintHitDelta { get; init; }

    public double LegacyEntityHitRate { get; init; }

    public double ShadowEntityHitRate { get; init; }

    public double EntityHitDelta { get; init; }

    public double LegacyUncertaintyHitRate { get; init; }

    public double ShadowUncertaintyHitRate { get; init; }

    public double UncertaintyHitDelta { get; init; }

    public int LegacyMustNotHitViolationCount { get; init; }

    public int ShadowMustNotHitViolationCount { get; init; }

    public int MustNotHitViolationDelta { get; init; }

    public int LifecycleViolationCount { get; init; }

    public int BudgetPressureDelta { get; init; }

    public int SelectedCountDelta { get; init; }

    public double MustHitTokenShareDelta { get; init; }

    public IReadOnlyList<string> MustHitGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitLost { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstraintGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstraintLost { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityLost { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> UncertaintyGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> UncertaintyLost { get; init; } = Array.Empty<string>();

    public string SuspectedReason { get; init; } = string.Empty;
}

public sealed class PlanningShadowQualityRecommendation
{
    public IReadOnlyList<string> OptInCandidateIntents { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BlockedIntents { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NeedsTuningIntents { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SafeOnlyInShadowIntents { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> IntentReasons { get; init; } = new Dictionary<string, string>();
}
