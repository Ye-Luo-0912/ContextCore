namespace ContextCore.Core.Services.Learning.V13_1;

// Existing/consolidated from V13
public enum InputSourceKind : byte { Unknown=0,User=1,Llm=2,Tool=3,Web=4,File=5,Runtime=6,System=7 }
public enum DataAuthorityKind : byte { Unknown=0,Authoritative=1,Shadow=2,Diagnostic=3,Synthetic=4,Derived=5 }
[Flags] public enum DataUsageFlags : ushort { None=0,Gate=1,Training=2,Eval=4,Audit=8,Runtime=16,Retrieval=32,Packaging=64 }

/// Unified candidate source type — replaces string constants "dense","lexical","anchor","read-only relation evidence"
public enum CandidateSourceType : byte
{
    Unknown = 0,
    Vector = 1,
    Memory = 2,
    Graph = 3,
    Keyword = 4,
    Anchor = 5,
    Constraint = 6
}

/// Contribution level of a candidate source in aggregated results
public enum SourceContribution : byte
{
    Unknown = 0,
    Primary = 1,
    Supplementary = 2,
    Fallback = 3,
    Diagnostic = 4
}

/// Scoring feature dimensions — all [0..1] normalized
public enum ScoringFeature : byte
{
    Relevance = 0,
    Authority = 1,
    Freshness = 2,
    StructuralFit = 3,
    UserPreference = 4
}

/// Unified Candidate replaces: VectorQueryPreviewCandidate, ContextMemoryItem,
/// relation graph candidates, keyword/lexical candidates
public sealed class UnifiedCandidate
{
    public string CandidateId { get; init; } = "";
    public string ItemId { get; init; } = "";
    public CandidateSourceType SourceType { get; init; }
    public SourceContribution Contribution { get; init; } = SourceContribution.Primary;
    public DataAuthorityKind Authority { get; init; }
    public InputSourceKind InputSource { get; init; }
    public string Layer { get; init; } = "";  // Working, Stable, Active, Deprecated
    public string Lifecycle { get; init; } = ""; // Current, Stale, Superseded, Rejected, Historical
    public string Content { get; init; } = "";
    public string ContentFormat { get; init; } = ""; // text, json, markdown
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SourceRefs { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RelationRefs { get; init; } = Array.Empty<string>();
    public Dictionary<string,string> Metadata { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// Feature-level score with explanation — always included in scoring output
public sealed class FeatureScore
{
    public ScoringFeature Feature { get; init; }
    public double Value { get; init; }  // [0..1]
    public double Weight { get; init; } = 1.0;
    public string Explanation { get; init; } = "";
}

/// Unified scoring result — one per candidate per query
public sealed class UnifiedScore
{
    public string CandidateId { get; init; } = "";
    public double FinalScore { get; init; }
    public IReadOnlyList<FeatureScore> Features { get; init; } = Array.Empty<FeatureScore>();
    public string Rationale { get; init; } = ""; // human-readable summary
    public DataUsageFlags EligibleFor { get; init; }
}

/// Aggregator result — ordered list of scored candidates from all sources
public sealed class CandidateAggregatorResult
{
    public string QueryId { get; init; } = "";
    public string QueryText { get; init; } = "";
    public int SourceCount { get; init; }
    public int TotalCandidates { get; init; }
    public int AfterDedup { get; init; }
    public int AfterScoring { get; init; }
    public IReadOnlyList<UnifiedScore> Scores { get; init; } = Array.Empty<UnifiedScore>();
    public IReadOnlyList<UnifiedCandidate> DedupedCandidates { get; init; } = Array.Empty<UnifiedCandidate>();
}
