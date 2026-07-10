namespace ContextCore.Core.Services.Learning.V13_2;

using V13_1;

/// Non-human-label feedback signals. All signals are derived from runtime behavior,
/// implicit user interaction, or system metrics — NOT manual annotation.
public enum FeedbackSignalKind : byte
{
    Unknown = 0,
    UserClicked = 1,
    UserIgnored = 2,
    ContextUsed = 3,
    ContextNotUsed = 4,
    DownstreamSuccess = 5,
    DownstreamFailure = 6,
    RuntimeRelevanceProxy = 7,
    PackageUtilityScore = 8,
    LatencySignal = 9,
    TokenCostSignal = 10,
    DwellTime = 11,
    RequeryRate = 12
}

/// Single feedback event — lightweight, no human labeling dependency
public sealed class FeedbackEvent
{
    public string EventId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public FeedbackSignalKind SignalKind { get; init; }
    public double ScoreAtTime { get; init; }
    public bool UsedInFinalPackage { get; init; }
    public bool DownstreamSuccess { get; init; }
    public double LatencyImpactMs { get; init; }
    public int TokenCostImpact { get; init; }
    public double SignalConfidence { get; init; } = 1.0; // [0..1] — derived from signal source reliability
    public Dictionary<string,string> Metadata { get; init; } = new();
    public DateTimeOffset RecordedAt { get; init; }
}

/// Aggregated feedback for a single candidate across events
public sealed class CandidateFeedbackSummary
{
    public string CandidateId { get; init; } = "";
    public int TotalEvents { get; init; }
    public int ClickCount { get; init; }
    public int UseCount { get; init; }
    public int SuccessCount { get; init; }
    public double AverageScoreAtTime { get; init; }
    public double AverageUtilityScore { get; init; }
    public double EffectivenessRate { get; init; } // SuccessCount / UseCount
    public double ClickThroughRate { get; init; }   // ClickCount / TotalEvents
    public double ContributionScore { get; init; }  // derived from downstream success
}

/// Scoring drift measurement — predicted vs actual usefulness
public sealed class ScoringDriftReport
{
    public string GeneratedAt { get; init; } = "";
    public bool ScoringIsEvaluable { get; init; }
    public int CandidateCount { get; init; }
    public int CandidatesWithFeedback { get; init; }
    public double MeanPredictedScore { get; init; }
    public double MeanActualUtility { get; init; }
    public double DriftMagnitude { get; init; }  // |mean_predicted - mean_actual|
    public double RankCorrelation { get; init; }  // Spearman-like ranking correlation
    public string DriftStatus { get; init; } = ""; // "stable", "drifting", "significant"
    public IReadOnlyList<string> HighDriftCandidates { get; init; } = Array.Empty<string>();
}

/// Per-candidate effectiveness measurement
public sealed class CandidateEffectivenessReport
{
    public string GeneratedAt { get; init; } = "";
    public bool CandidateTraceabilityComplete { get; init; }
    public int TotalCandidates { get; init; }
    public int EffectiveCandidates { get; init; }  // contribution > threshold
    public int IneffectiveCandidates { get; init; }
    public double MeanEffectiveness { get; init; }
    public IReadOnlyList<CandidateFeedbackSummary> TopContributors { get; init; } = Array.Empty<CandidateFeedbackSummary>();
    public IReadOnlyList<CandidateFeedbackSummary> BottomContributors { get; init; } = Array.Empty<CandidateFeedbackSummary>();
}

/// Feature weight calibration — rule-based, NOT gradient descent
public sealed class FeatureWeightCalibration
{
    public string FeatureName { get; init; } = "";
    public double CurrentWeight { get; init; }
    public double CalibratedWeight { get; init; }
    public double WeightDelta { get; init; }
    public string CalibrationSignal { get; init; } = ""; // which feedback signal drove the change
    public bool Calibrated { get; init; }
    public bool DeterministicCorePreserved { get; init; } = true;
}

/// Package-level outcome scoring
public sealed class PackageOutcomeScore
{
    public string PackageId { get; init; } = "";
    public double UtilityScore { get; init; }
    public IReadOnlyList<CandidateContribution> CandidateContributions { get; init; } = Array.Empty<CandidateContribution>();
    public bool AllContributionsTraceable { get; init; }
}

public sealed class CandidateContribution
{
    public string CandidateId { get; init; } = "";
    public double Contribution { get; init; }
    public bool UsedInPackage { get; init; }
    public bool ContributedToSuccess { get; init; }
}
