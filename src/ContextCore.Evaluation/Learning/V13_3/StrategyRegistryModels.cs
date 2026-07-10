namespace ContextCore.Core.Services.Learning.V13_3;

using V13_1;

/// Scoring strategy intent — what the strategy optimizes for
public enum StrategyIntent : byte
{
    Unknown = 0,
    Precision = 1,
    Recall = 2,
    Compression = 3,
    MemoryAware = 4,
    GraphAware = 5,
    Speed = 6,
    Balanced = 7
}

/// Strategy activation domain — when this strategy applies
public enum StrategyDomain : byte
{
    Unknown = 0,
    Chat = 1,
    Coding = 2,
    Audit = 3,
    Eval = 4,
    Production = 5,
    Diagnostic = 6
}

/// Lifecycle of a strategy version
public enum StrategyLifecycle : byte
{
    Experimental = 0,
    Candidate = 1,
    Active = 2,
    Deprecated = 3,
    Retired = 4
}

/// Individual scoring strategy profile — defines feature weight + domain + lifecycle
public sealed class StrategyProfile
{
    public string StrategyId { get; init; } = "";
    public string Version { get; init; } = "v1";
    public StrategyIntent Intent { get; init; }
    public StrategyDomain Domain { get; init; }
    public StrategyLifecycle Lifecycle { get; init; } = StrategyLifecycle.Candidate;
    public double RelevanceWeight { get; init; } = 1.0;
    public double AuthorityWeight { get; init; } = 0.8;
    public double FreshnessWeight { get; init; } = 0.6;
    public double StructuralFitWeight { get; init; } = 0.5;
    public double UserPreferenceWeight { get; init; } = 0.4;
    public double LifecyclePenaltyFactor { get; init; } = 1.0;  // multiplier on lifecycle penalties
    public int MaxCandidates { get; init; } = 60;
    public bool PreferGraphCandidates { get; init; }
    public bool PreferMemoryCandidates { get; init; }
    public bool PreferVectorCandidates { get; init; }
    public string Description { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// Query context for strategy selection — maps to V13.1 QueryContext
public sealed class StrategySelectionInput
{
    public string QueryText { get; init; } = "";
    public string Intent { get; init; } = "general";     // chat, coding, audit, eval
    public string MemoryPressure { get; init; } = "low"; // low, medium, high, critical
    public int TokenBudget { get; init; } = 8000;
    public int CandidateCount { get; init; }
    public bool IsAudit { get; init; }
    public bool IsEval { get; init; }
    public StrategyDomain Domain { get; init; } = StrategyDomain.Chat;
}

/// Strategy selection result — resolved from input context
public sealed class StrategySelectionResult
{
    public string SelectedStrategyId { get; init; } = "";
    public string SelectedVersion { get; init; } = "";
    public StrategyIntent SelectedIntent { get; init; }
    public string SelectionReason { get; init; } = "";
    public IReadOnlyList<string> CandidatesConsidered { get; init; } = Array.Empty<string>();
    public bool LlmSuggested { get; init; }  // whether LLM influenced the selection
    public string LlmRationale { get; init; } = "";
}

/// Minimal LLM adapter — suggest only, no scoring, no execution
public sealed class LlmStrategySuggestion
{
    public string SuggestedStrategyId { get; init; } = "";
    public string Rationale { get; init; } = "";
    public double Confidence { get; init; }  // [0..1]
    public bool Accepted { get; init; }       // whether the system accepted the suggestion
    public string OverrideReason { get; init; } = "";
}
