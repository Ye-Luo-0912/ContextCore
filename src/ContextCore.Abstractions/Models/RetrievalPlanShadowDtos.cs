namespace ContextCore.Abstractions.Models;

/// <summary>RetrievalPlan proposal 的 shadow 执行结果；不代表正式 retrieval 输出。</summary>
public sealed class ShadowRetrievalResult
{
    public string OperationId { get; init; } = string.Empty;

    public string ProposalId { get; init; } = string.Empty;

    public string ProposalSummary { get; init; } = string.Empty;

    public IReadOnlyList<ContextRetrievalCandidate> ShadowCandidates { get; init; } = Array.Empty<ContextRetrievalCandidate>();

    public IReadOnlyList<ContextRetrievalCandidate> ShadowSelectedItems { get; init; } = Array.Empty<ContextRetrievalCandidate>();

    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>Legacy hybrid retrieval 与 planning shadow retrieval 的对比报告。</summary>
public sealed class ShadowRetrievalComparisonReport
{
    public string ReportId { get; init; } = string.Empty;

    public string SampleSet { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TotalSamples { get; init; }

    public int SelectedSetDiffCount { get; init; }

    public int AddedItemCount { get; init; }

    public int DroppedItemCount { get; init; }

    public int MustNotHitViolationCount { get; init; }

    public int LifecycleViolationCount { get; init; }

    public double AvgBudgetPressureDelta { get; init; }

    public int ValidPlanCount { get; init; }

    public int NativeValidPlanCount { get; init; }

    public int RepairedPlanCount { get; init; }

    public int FallbackToLegacySafePlanCount { get; init; }

    public int FallbackPlanCount { get; init; }

    public double NativeValidRate { get; init; }

    public int FinalTopKClampCount { get; init; }

    public int VectorDisabledCount { get; init; }

    public int DeprecatedBlockedCount { get; init; }

    public IReadOnlyDictionary<string, int> ValidatorRepairReasons { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, int> RepairReasonCounts { get; init; } = new Dictionary<string, int>();

    public IReadOnlyDictionary<string, RetrievalPlanRepairBreakdown> IntentRepairBreakdown { get; init; } =
        new Dictionary<string, RetrievalPlanRepairBreakdown>();

    public IReadOnlyDictionary<string, RetrievalPlanRepairBreakdown> ModeRepairBreakdown { get; init; } =
        new Dictionary<string, RetrievalPlanRepairBreakdown>();

    public IReadOnlyDictionary<string, int> WarningCounts { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<ShadowRetrievalComparisonItem> Samples { get; init; } = Array.Empty<ShadowRetrievalComparisonItem>();
}

public sealed class RetrievalPlanRepairBreakdown
{
    public string Key { get; init; } = string.Empty;

    public int TotalSamples { get; init; }

    public int NativeValidPlanCount { get; init; }

    public int RepairedPlanCount { get; init; }

    public int FallbackPlanCount { get; init; }

    public double NativeValidRate { get; init; }

    public IReadOnlyDictionary<string, int> RepairReasonCounts { get; init; } = new Dictionary<string, int>();
}

public sealed class ShadowRetrievalComparisonItem
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string LegacyOperationId { get; init; } = string.Empty;

    public string ShadowOperationId { get; init; } = string.Empty;

    public string ProposalId { get; init; } = string.Empty;

    public string ProposalSummary { get; init; } = string.Empty;

    public IReadOnlyList<string> LegacySelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowSelected { get; init; } = Array.Empty<string>();

    public int SelectedSetDiff { get; init; }

    public int AddedCount { get; init; }

    public int DroppedCount { get; init; }

    public IReadOnlyList<string> AddedItems { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DroppedItems { get; init; } = Array.Empty<string>();

    public int MustHitDelta { get; init; }

    public int MustHitCount { get; init; }

    public int LegacyMustHitCount { get; init; }

    public int ShadowMustHitCount { get; init; }

    public IReadOnlyList<string> LegacySelectedMustHit { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowSelectedMustHit { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitDropped { get; init; } = Array.Empty<string>();

    public double LegacyRecall3 { get; init; }

    public double ShadowRecall3 { get; init; }

    public double LegacyRecall5 { get; init; }

    public double ShadowRecall5 { get; init; }

    public double LegacyRecall10 { get; init; }

    public double ShadowRecall10 { get; init; }

    public double LegacyMrr { get; init; }

    public double ShadowMrr { get; init; }

    public double LegacyMustHitTokenShare { get; init; }

    public double ShadowMustHitTokenShare { get; init; }

    public int LegacyMustNotHitViolationCount { get; init; }

    public bool MustNotHitViolation { get; init; }

    public int MustNotHitViolationCount { get; init; }

    public IReadOnlyList<string> MustNotHitViolations { get; init; } = Array.Empty<string>();

    public bool LifecycleViolation { get; init; }

    public int LifecycleViolationCount { get; init; }

    public IReadOnlyList<string> LifecycleRiskAdded { get; init; } = Array.Empty<string>();

    public int ConstraintDelta { get; init; }

    public IReadOnlyList<string> ExpectedHardConstraints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LegacyConstraints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ProposalConstraints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingConstraints { get; init; } = Array.Empty<string>();

    public double LegacyConstraintHitRate { get; init; }

    public double ShadowConstraintHitRate { get; init; }

    public IReadOnlyList<string> ConstraintGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConstraintLost { get; init; } = Array.Empty<string>();

    public int EntityDelta { get; init; }

    public double LegacyEntityHitRate { get; init; }

    public double ShadowEntityHitRate { get; init; }

    public IReadOnlyList<string> EntityGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EntityLost { get; init; } = Array.Empty<string>();

    public int UncertaintyDelta { get; init; }

    public double LegacyUncertaintyHitRate { get; init; }

    public double ShadowUncertaintyHitRate { get; init; }

    public IReadOnlyList<string> UncertaintyGained { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> UncertaintyLost { get; init; } = Array.Empty<string>();

    public int BudgetPressureDelta { get; init; }

    public IReadOnlyList<ShadowRetrievalRankDelta> RankDeltas { get; init; } = Array.Empty<ShadowRetrievalRankDelta>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LegacyChannelSources { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ShadowChannelSources { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyDictionary<string, string> LostByChannel { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> LostByTopKCap { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LostByDisabledChannel { get; init; } = Array.Empty<string>();

    public bool ValidatorApplied { get; init; }

    public bool ValidPlan { get; init; }

    public bool NativeValidPlan { get; init; }

    public bool RepairedPlan { get; init; }

    public bool FallbackToLegacySafePlan { get; init; }

    public IReadOnlyList<string> RejectedPlanReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ValidatorRepairReasons { get; init; } = Array.Empty<string>();

    public string FallbackRootCause { get; init; } = string.Empty;

    public string AfterRepairPlanSummary { get; init; } = string.Empty;

    public bool FinalTopKClamped { get; init; }

    public bool VectorDisabled { get; init; }

    public int DeprecatedBlockedCount { get; init; }

    public int MustNotHitAddedAfterValidation { get; init; }

    public int LifecycleViolationAfterValidation { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Diagnostics { get; init; } = new Dictionary<string, string>();
}

public sealed class ShadowRetrievalRankDelta
{
    public string SourceId { get; init; } = string.Empty;

    public int? LegacyRank { get; init; }

    public int? ShadowRank { get; init; }

    public int? Delta { get; init; }
}

/// <summary>RetrievalPlan proposal 的 shadow validator 结果；只作用于 shadow path。</summary>
public sealed class RetrievalPlanProposalValidationResult
{
    public bool ValidatorApplied { get; init; }

    public bool ValidPlan { get; init; }

    public bool RepairedPlan { get; init; }

    public bool FallbackToLegacySafePlan { get; init; }

    public bool LegacySafeMode { get; init; }

    public RetrievalPlanProposal EffectiveProposal { get; init; } = new();

    public IReadOnlyList<string> RejectedPlanReasons { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ValidatorRepairReasons { get; init; } = Array.Empty<string>();

    public string FallbackRootCause { get; init; } = string.Empty;

    public string AfterRepairPlanSummary { get; init; } = string.Empty;

    public bool FinalTopKClamped { get; init; }

    public bool VectorDisabled { get; init; }
}

/// <summary>Shadow selected item 经过 validator 后的结果。</summary>
public sealed class ShadowSelectedItemValidationResult
{
    public IReadOnlyList<ContextRetrievalCandidate> SelectedItems { get; init; } = Array.Empty<ContextRetrievalCandidate>();

    public IReadOnlyList<string> RejectedReasons { get; init; } = Array.Empty<string>();

    public int DeprecatedBlockedCount { get; init; }

    public int MustNotHitAddedAfterValidation { get; init; }

    public int LifecycleViolationAfterValidation { get; init; }
}

/// <summary>Planning shadow diff triage 汇总报告。</summary>
public sealed class PlanningShadowDiffTriageReport
{
    public string ReportId { get; init; } = string.Empty;

    public string SampleSet { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TotalSamples { get; init; }

    public int DiffSampleCount { get; init; }

    public int MustNotHitAddedCount { get; init; }

    public int MustHitDroppedCount { get; init; }

    public int LifecycleRiskAddedCount { get; init; }

    public int FallbackToLegacySafePlanCount { get; init; }

    public int RepairedPlanCount { get; init; }

    public IReadOnlyDictionary<string, int> SuspectedCauseCounts { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<PlanningShadowDiffTriageSample> Samples { get; init; } = Array.Empty<PlanningShadowDiffTriageSample>();
}

public sealed class PlanningShadowDiffTriageSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public IReadOnlyList<string> LegacySelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowSelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedByShadow { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DroppedByShadow { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustNotHitAdded { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitDropped { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LifecycleRiskAdded { get; init; } = Array.Empty<string>();

    public PlanningShadowChannelPlan ChannelPlan { get; init; } = new();

    public IReadOnlyDictionary<string, int> ChannelTopK { get; init; } = new Dictionary<string, int>();

    public string FallbackRootCause { get; init; } = string.Empty;

    public IReadOnlyList<string> RepairReasons { get; init; } = Array.Empty<string>();

    public string AfterRepairPlanSummary { get; init; } = string.Empty;

    public string SuspectedCause { get; init; } = string.Empty;

    public string SuggestedFix { get; init; } = string.Empty;
}

public sealed class PlanningShadowChannelPlan
{
    public bool UseKeyword { get; init; }

    public bool UseWorkingMemory { get; init; }

    public bool UseStableMemory { get; init; }

    public bool UseRelations { get; init; }

    public bool UseVector { get; init; }

    public bool AuditMode { get; init; }

    public bool ConflictMode { get; init; }

    public bool ValidatorApplied { get; init; }

    public bool ValidPlan { get; init; }

    public bool RepairedPlan { get; init; }

    public bool FallbackToLegacySafePlan { get; init; }
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

/// <summary>Planning opt-in fallback 分析报告；只用于评估 intent-scoped expansion，不启用正式 retrieval。</summary>
public sealed class PlanningOptInFallbackAnalysisReport
{
    public string ReportId { get; init; } = string.Empty;

    public string SampleSet { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TotalSamples { get; init; }

    public IReadOnlyList<string> CurrentOptInIntents { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> CandidateIntents { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, PlanningOptInIntentSummary> IntentSummaries { get; init; } =
        new Dictionary<string, PlanningOptInIntentSummary>();

    public PlanningOptInRecommendation Recommendation { get; init; } = new();

    public IReadOnlyDictionary<string, int> FallbackReasonCounts { get; init; } =
        new Dictionary<string, int>();

    public IReadOnlyList<PlanningOptInFallbackAnalysisSample> Samples { get; init; } =
        Array.Empty<PlanningOptInFallbackAnalysisSample>();
}

public sealed class PlanningOptInFallbackAnalysisSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public bool OptInMatched { get; init; }

    public bool Applied { get; init; }

    public bool FallbackUsed { get; init; }

    public string FallbackReason { get; init; } = string.Empty;

    public string FallbackReasonCategory { get; init; } = string.Empty;

    public bool SafetyCheckFailed { get; init; }

    public IReadOnlyList<string> LegacySelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ProposalSelected { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FinalSelected { get; init; } = Array.Empty<string>();

    public int MustHitDelta { get; init; }

    public int ConstraintDelta { get; init; }

    public int EntityDelta { get; init; }

    public int UncertaintyDelta { get; init; }
}

public sealed class PlanningOptInIntentSummary
{
    public string Intent { get; init; } = string.Empty;

    public int Samples { get; init; }

    public int OptInMatched { get; init; }

    public int Applied { get; init; }

    public int Fallback { get; init; }

    public double FallbackRate { get; init; }

    public double PassDelta { get; init; }

    public double RecallDelta { get; init; }

    public double MrrDelta { get; init; }

    public int MustNotHitViolation { get; init; }

    public int LifecycleViolation { get; init; }

    public string Recommendation { get; init; } = string.Empty;
}

public sealed class PlanningOptInRecommendation
{
    public IReadOnlyList<string> KeepOptIn { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExpandCandidate { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowOnly { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Blocked { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> NeedsPolicyTuning { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> IntentReasons { get; init; } = new Dictionary<string, string>();
}

public sealed class PlanningOptInConstraintSafetyReport
{
    public string ReportId { get; init; } = string.Empty;

    public string SampleSet { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TotalSamples { get; init; }

    public int AffectedSampleCount { get; init; }

    public int FallbackSampleCount { get; init; }

    public int ConstraintRepairedCount { get; init; }

    public int ConstraintRepairFailedCount { get; init; }

    public int ConstraintDroppedByBudgetCount { get; init; }

    public int ConstraintWrongSectionCount { get; init; }

    public IReadOnlyList<PlanningOptInConstraintSafetySample> Samples { get; init; } =
        Array.Empty<PlanningOptInConstraintSafetySample>();
}

public sealed class PlanningOptInConstraintSafetySample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public bool OptInMatched { get; init; }

    public bool Applied { get; init; }

    public bool FallbackUsed { get; init; }

    public IReadOnlyList<string> ExpectedHardConstraints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> LegacyConstraints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ProposalConstraints { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MissingConstraints { get; init; } = Array.Empty<string>();

    public string ConstraintSource { get; init; } = string.Empty;

    public string LostAtStage { get; init; } = string.Empty;

    public string SuggestedFix { get; init; } = string.Empty;

    public string ConstraintRepairStatus { get; init; } = string.Empty;
}

/// <summary>Planning shadow recall loss 诊断报告；只用于 shadow 质量分析。</summary>
public sealed class PlanningShadowRecallLossReport
{
    public string ReportId { get; init; } = string.Empty;

    public string SampleSet { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public int TotalSamples { get; init; }

    public int DegradedSampleCount { get; init; }

    public int MustHitLostCount { get; init; }

    public IReadOnlyDictionary<string, int> SuspectedLossReasonCounts { get; init; } =
        new Dictionary<string, int>();

    public IReadOnlyList<PlanningShadowRecallLossSample> Samples { get; init; } =
        Array.Empty<PlanningShadowRecallLossSample>();
}

public sealed class PlanningShadowRecallLossSample
{
    public string SampleId { get; init; } = string.Empty;

    public string Mode { get; init; } = string.Empty;

    public string Intent { get; init; } = string.Empty;

    public IReadOnlyList<string> LegacySelectedMustHit { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ShadowSelectedMustHit { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> MustHitLost { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, int?> MustHitRankLegacy { get; init; } =
        new Dictionary<string, int?>();

    public IReadOnlyDictionary<string, int?> MustHitRankShadow { get; init; } =
        new Dictionary<string, int?>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LegacyChannelSources { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ShadowChannelSources { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyList<string> DisabledChannels { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, int> TopKCaps { get; init; } = new Dictionary<string, int>();

    public string SuspectedLossReason { get; init; } = string.Empty;

    public string SuggestedFix { get; init; } = string.Empty;
}
