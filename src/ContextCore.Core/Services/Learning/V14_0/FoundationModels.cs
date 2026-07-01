namespace ContextCore.Core.Services.Learning.V14_0;

/// Single row in the feature store — extracted from runtime context selection
public sealed class CandidateFeatureRecord
{
    public string RecordId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public byte SourceType { get; init; }          // CandidateSourceType enum
    public byte Authority { get; init; }           // DataAuthorityKind enum
    public byte StrategyType { get; init; }        // StrategyIntent enum
    public float VectorScore { get; init; }
    public float GraphScore { get; init; }
    public float MemoryScore { get; init; }
    public float RecencyScore { get; init; }
    public float TokenCost { get; init; }
    public float LatencyCost { get; init; }
    public float UserPreferenceSignal { get; init; }
    public bool SelectionOutcome { get; init; }     // selected or dropped
    public bool IncludedInPackage { get; init; }
    public float PackageContributionScore { get; init; }
    public string DropReason { get; init; } = "";   // empty if selected
    public DateTimeOffset RecordedAt { get; init; }
}

/// Single feedback event — fully automated, no human labeling
public sealed class ContextSelectionFeedbackEvent
{
    public string EventId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public bool Selected { get; init; }
    public bool IncludedInPackage { get; init; }
    public float DownstreamSuccessProxy { get; init; } // [0..1] proxy signal
    public sbyte UserImplicitSignal { get; init; }     // -1=negative, 0=neutral, 1=positive
    public float CostEfficiencyScore { get; init; }    // success/cost ratio
    public DateTimeOffset Timestamp { get; init; }
}

/// Selection effectiveness per candidate
public sealed class CandidateEffectivenessEntry
{
    public string CandidateId { get; init; } = "";
    public int SelectionCount { get; init; }
    public int PackageInclusionCount { get; init; }
    public int PositiveSignals { get; init; }
    public int NegativeSignals { get; init; }
    public float EffectivenessScore { get; init; }
    public float AverageContributionScore { get; init; }
    public float DriftFromHistorical { get; init; }
}

/// Ranking stability measurement
public sealed class RankingDriftEntry
{
    public string CandidateId { get; init; } = "";
    public int PreviousRank { get; init; }
    public int CurrentRank { get; init; }
    public int RankDelta { get; init; }
    public float ScoreDelta { get; init; }
    public bool SignificantDrift { get; init; }
}

/// Hybrid scoring bridge — neural bias slot, initially zero
public sealed class HybridScoringBridgeRecord
{
    public string CandidateId { get; init; } = "";
    public float DeterministicScore { get; init; }
    public float NeuralBias { get; init; }         // initially 0, filled by V14 neural system
    public float FinalScore { get; init; }          // = deterministic + neural_bias
    public bool NeuralBiasActive { get; init; }     // false until V14 neural is trained
    public DateTimeOffset EvaluatedAt { get; init; }
}
