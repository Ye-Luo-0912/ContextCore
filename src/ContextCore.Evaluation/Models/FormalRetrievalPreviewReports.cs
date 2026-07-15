using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;


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

    public double BudgetPressureDelta { get; init; }

    public double SelectedCountDelta { get; init; }

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
