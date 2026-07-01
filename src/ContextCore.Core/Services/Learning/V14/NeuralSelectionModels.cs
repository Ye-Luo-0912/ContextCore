namespace ContextCore.Core.Services.Learning.V14;

/// Feature index in CandidateFeatureVector — byte-backed for stability
public enum FeatureIndex : byte
{
    SourceType = 0,
    Authority = 1,
    StrategyType = 2,
    VectorScore = 3,
    GraphScore = 4,
    MemoryScore = 5,
    RecencyScore = 6,
    TokenCost = 7,
    UserPreferenceSignal = 8,
    HistoricalSuccessRate = 9
}

/// Lightweight feature vector — fixed 10 dimensions, all [0..1] normalized
public readonly struct CandidateFeatureVector
{
    public float SourceType { get; init; }       // byte enum → normalized
    public float Authority { get; init; }        // DataAuthorityKind → normalized
    public float StrategyType { get; init; }     // StrategyIntent → normalized
    public float VectorScore { get; init; }      // vector similarity
    public float GraphScore { get; init; }       // graph expansion score
    public float MemoryScore { get; init; }      // memory recall score
    public float RecencyScore { get; init; }     // time-decayed freshness
    public float TokenCost { get; init; }        // estimated token cost normalized
    public float UserPreferenceSignal { get; init; } // aggregated from FeedbackEvent
    public float HistoricalSuccessRate { get; init; } // from CandidateFeedbackSummary

    public float[] ToArray() => [SourceType, Authority, StrategyType, VectorScore, GraphScore, MemoryScore, RecencyScore, TokenCost, UserPreferenceSignal, HistoricalSuccessRate];
    public int Dimension => 10;
}

/// Lightweight MLP model spec — small, deterministic, replaceable
public sealed class NeuralModelSpec
{
    public string ModelId { get; init; } = "context-selector-mlp-v1";
    public string Architecture { get; init; } = "3-layer MLP + linear combiner";
    public int InputDim { get; init; } = 10;
    public int[] HiddenDims { get; init; } = [16, 8];
    public int OutputDim { get; init; } = 3; // [selection_score, ranking_score, drop_prob]
    public string Activation { get; init; } = "ReLU → ReLU → Sigmoid";
    public int TotalParameters { get; init; }     // computed: (10*16+16) + (16*8+8) + (8*3+3) = 176+136+27 = 339
    public string Optimizer { get; init; } = "SGD with momentum 0.9, learning_rate=0.001";
    public string LossFunction { get; init; } = "weighted MSE: 0.4*selection + 0.35*ranking + 0.25*drop";
    public string Normalization { get; init; } = "BatchNorm after each hidden layer";
    public string DropoutRate { get; init; } = "0.1 during training, 0.0 at inference";
    public string DeterministicSeed { get; init; } = "42 (same seed → same weights → same output)";
    public bool Replaceable { get; init; } = true;
    public string ReplaceabilityNote { get; init; } = "Model weights are serializable as JSON array. Can be replaced with LightGBM or linear+nonlinear hybrid without changing API.";
}

/// Neural inference output — 3 scores from the model
public sealed class NeuralSelectionOutput
{
    public double SelectionScore { get; init; }   // [0..1] how likely candidate should be selected
    public double RankingScore { get; init; }      // [0..1] relative rank among candidates
    public double DropProbability { get; init; }   // [0..1] probability candidate should be dropped
}

/// Hybrid score — strategy + neural with configurable blend
public sealed class HybridScore
{
    public double StrategyScore { get; init; }     // from V13.3 strategy pipeline
    public double NeuralScore { get; init; }       // NeuralSelectionOutput.SelectionScore
    public double FinalScore { get; init; }         // alpha * strategy + (1-alpha) * neural
    public double BlendAlpha { get; init; } = 0.3; // neural weight, [0..1]
    public string BlendMode { get; init; } = "soft"; // "soft" | "neural-only" | "strategy-only"
}

/// Learning signal — auto-collected, no manual labeling
public sealed class LearningSignal
{
    public string SignalId { get; init; } = "";
    public string CandidateId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public LearningSignalKind Kind { get; init; }
    public double Value { get; init; }              // signal value [0..1]
    public double Weight { get; init; } = 1.0;     // signal weight for training
    public CandidateFeatureVector FeatureVector { get; init; }
    public bool CandidateSelected { get; init; }
    public bool PackageSuccessful { get; init; }
    public double LatencyMs { get; init; }
    public int TokenCost { get; init; }
    public DateTimeOffset CollectedAt { get; init; }
}

public enum LearningSignalKind : byte
{
    Unknown = 0,
    CandidateSelected = 1,
    CandidateDropped = 2,
    PackageSuccess = 3,
    PackageFailure = 4,
    DownstreamFeedback = 5,
    LatencyRecorded = 6,
    TokenEfficiency = 7
}
