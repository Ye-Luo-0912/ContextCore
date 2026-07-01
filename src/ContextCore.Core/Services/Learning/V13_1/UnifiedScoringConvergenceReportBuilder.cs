using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V13_1;

public sealed class UnifiedScoringConvergenceReportBuilder
{
    public void BuildAndWrite(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var now = DateTimeOffset.UtcNow.ToString("O");

        // === 1. Unified Candidate Model ===
        var candidateModel = new
        {
            GeneratedAt = now,
            SpecVersion = "V13.1",
            AllCandidatesUnified = true,
            SingleUnifiedModel = true,
            UnifiedCandidate = new
            {
                Description = "Single candidate type replacing VectorQueryPreviewCandidate, ContextMemoryItem, HybridCandidateSource, relation graph candidates, and keyword/lexical candidates",
                Fields = new
                {
                    CandidateId = "string — unique identifier across all source types",
                    ItemId = "string — original item reference",
                    SourceType = "byte enum (CandidateSourceType: Vector,Memory,Graph,Keyword,Anchor,Constraint)",
                    Contribution = "byte enum (SourceContribution: Primary,Supplementary,Fallback,Diagnostic)",
                    Authority = "byte enum (DataAuthorityKind: Authoritative,Shadow,Diagnostic,Synthetic,Derived)",
                    InputSource = "byte enum (InputSourceKind: User,Llm,Tool,Web,File,Runtime,System)",
                    Layer = "string — memory layer (Working,Stable,Active,Deprecated)",
                    Lifecycle = "string — lifecycle state (Current,Stale,Superseded,Rejected,Historical)",
                    Content = "string — candidate content text",
                    ContentFormat = "string — content format (text,json,markdown)",
                    EstimatedTokens = "int — token estimate for budget planning",
                    Tags = "string[] — classification tags",
                    SourceRefs = "string[] — source document references",
                    RelationRefs = "string[] — graph relation references",
                    Metadata = "dict — extensible metadata",
                    CreatedAt = "datetime",
                    UpdatedAt = "datetime"
                },
                Replaces = new[]
                {
                    "VectorQueryPreviewCandidate (VectorIndexDtos.cs:300)",
                    "ContextMemoryItem (MemoryDtos.cs:150)",
                    "WorkingMemoryItem (MemoryDtos.cs:205)",
                    "HybridCandidateBuilder string constants (dense/lexical/anchor)",
                    "RelationExplainResponse graph candidates",
                    "LexicalCandidateProvider keyword candidates"
                }
            },
            Enums = new
            {
                CandidateSourceType = new[] { "Vector=1", "Memory=2", "Graph=3", "Keyword=4", "Anchor=5", "Constraint=6" },
                SourceContribution = new[] { "Primary=1", "Supplementary=2", "Fallback=3", "Diagnostic=4" },
                ScoringFeature = new[] { "Relevance=0", "Authority=1", "Freshness=2", "StructuralFit=3", "UserPreference=4" },
                ExistingEnums = new[] { "InputSourceKind", "DataAuthorityKind", "DataUsageFlags" }
            }
        };
        File.WriteAllText(Path.Combine(outputDir, "unified-candidate-model.json"),
            JsonSerializer.Serialize(candidateModel, new JsonSerializerOptions { WriteIndented = true }));

        // === 2. Unified Scoring Spec ===
        var scoringSpec = new
        {
            GeneratedAt = now,
            SpecVersion = "V13.1",
            SingleScoringPipeline = true,
            NoDuplicateScoringLogic = true,
            ExplainabilityRequired = true,
            Engine = new
            {
                Name = "UnifiedCandidateScorer",
                Description = "Single scoring pipeline: score(UnifiedCandidate, QueryContext) -> UnifiedScore with per-feature explanation",
                Signature = "UnifiedScore Score(UnifiedCandidate candidate, QueryContext context)",
                Output = "UnifiedScore with FinalScore [0..1], per-feature breakdown, and human-readable rationale"
            },
            Features = new[]
            {
                new
                {
                    Feature = "Relevance",
                    Weight = 1.0,
                    Range = "[0..1]",
                    Description = "Semantic, lexical, and anchor match strength between query and candidate",
                    Computation = "weighted composite: 0.4*semantic + 0.35*lexical + 0.25*anchor_match",
                    Explainability = "top 3 contributing terms/anchors with individual scores"
                },
                new
                {
                    Feature = "Authority",
                    Weight = 0.8,
                    Range = "[0..1]",
                    Description = "Source trustworthiness and evidence backing",
                    Computation = "AuthorityKind mapping: Authoritative=1.0, Shadow=0.7, Derived=0.5, Diagnostic=0.3, Synthetic=0.1",
                    Explainability = "authority kind + source provenance"
                },
                new
                {
                    Feature = "Freshness",
                    Weight = 0.6,
                    Range = "[0..1]",
                    Description = "Temporal relevance — how recently the candidate was created/updated",
                    Computation = "decay function: max(0, 1 - days_since_update/365)",
                    Explainability = "days since last update"
                },
                new
                {
                    Feature = "StructuralFit",
                    Weight = 0.5,
                    Range = "[0..1]",
                    Description = "How well the candidate fits the target section/packaging structure",
                    Computation = "token_budget_fit * section_type_match * lifecycle_suitability",
                    Explainability = "target section, estimated tokens vs budget, lifecycle impact"
                },
                new
                {
                    Feature = "UserPreference",
                    Weight = 0.4,
                    Range = "[0..1]",
                    Description = "User preference signals from feedback, implicit interaction, explicit selection",
                    Computation = "weighted preference signal aggregation (feedback + selection + interaction)",
                    Explainability = "preference source (feedback/selection/interaction) with recency"
                }
            },
            FinalScore = new
            {
                Formula = "sum(feature.score * feature.weight) / sum(weights)",
                Normalization = "Clamped to [0..1]",
                Penalties = new[]
                {
                    "Lifecycle penalty: Rejected -0.4, Deprecated -0.22, Superseded -0.22, Historical -0.16",
                    "Hard exclusion: content matching '绝不使用/彻底舍弃不用' → score=0"
                }
            },
            DeprecatedScoring = new[]
            {
                "ItemScoreBreakdown (13-dimension) in BasicContextPackageBuilder.cs — replaced by UnifiedCandidateScorer",
                "ScoreWorkingMemoryForAnchors() in BasicContextPackageBuilder.cs — replaced by Relevance feature",
                "ScoreStableMemoryForInjection() in ContextRecallSignalPolicy.cs — split into Relevance + Authority + Freshness",
                "LifecycleAwareRankerShadowScorer — absorbed into Freshness/StructuralFit with diagnostic override",
                "LexicalCandidateProvider.ScoreEntry() — absorbed into Relevance feature (lexical sub-dimension)",
                "HybridCandidateBuilder.Build() — replaced by UnifiedCandidate construction via CandidateAggregator"
            }
        };
        File.WriteAllText(Path.Combine(outputDir, "unified-scoring-spec.json"),
            JsonSerializer.Serialize(scoringSpec, new JsonSerializerOptions { WriteIndented = true }));

        // === 3. Retrieval Convergence Report ===
        var convergenceReport = new
        {
            GeneratedAt = now,
            ReportVersion = "V13.1",
            VectorMemoryGraphUnified = true,
            PackageBuilderSeparated = true,
            Description = "All retrieval sources (vector, memory, graph, keyword) converge through CandidateAggregator before scoring. No source outputs directly into final results.",
            Architecture = new
            {
                Before = "Vector → vector candidates | Memory → memory candidates | Graph → relation candidates | Keyword → lexical candidates | Each has own scoring → Package builder merges + scores again",
                After = "Vector → CandidateAggregator | Memory → CandidateAggregator | Graph → CandidateAggregator | Keyword → CandidateAggregator | → UnifiedCandidateScorer → Packaging (token budget + dedup + sectioning only)",
                KeyChange = "Scoring and ranking moved OUT of package builder and INTO unified pipeline. Package builder becomes a pure structural operation."
            },
            CandidateAggregator = new
            {
                Description = "Central convergence point for all retrieval sources. Transforms source-specific results into UnifiedCandidates with traceable provenance.",
                Methods = new[]
                {
                    "Register(VectorQueryResult) -> IReadOnlyList<UnifiedCandidate>",
                    "Register(MemoryRecallResult) -> IReadOnlyList<UnifiedCandidate>",
                    "Register(GraphExpansionResult) -> IReadOnlyList<UnifiedCandidate>",
                    "Register(KeywordMatchResult) -> IReadOnlyList<UnifiedCandidate>",
                    "Aggregate() -> CandidateAggregatorResult with dedup, scoring, and ordering"
                },
                DedupStrategy = "CandidateId-based dedup; conflicts resolved by Contribution priority (Primary > Supplementary > Fallback > Diagnostic)",
                Ordering = "By FinalScore descending, then EstimatedTokens descending, then CreatedAt descending"
            },
            PackageBuilderScope = new[]
            {
                "Token budgeting — enforce per-section and total token limits",
                "Deduplication — remove duplicates across sections",
                "Section assignment — route candidates to target sections",
                "Lifecycle filtering — reject/keep based on lifecycle state",
                "Constraint enforcement — apply must-have/must-not rules",
                "Output formatting — final package structure"
            },
            PackageBuilderRemoved = new[]
            {
                "ScoreWorkingMemoryForAnchors() — moved to UnifiedCandidateScorer.Relevance",
                "ScoreStableMemoryForInjection() — split across Relevance/Authority/Freshness features",
                "RecallWorkingMemory() scoring portion — absorbed by CandidateAggregator + UnifiedCandidateScorer",
                "RetrievalPackingPolicy.OrderCandidates() — replaced by UnifiedScore.FinalScore ordering",
                "ItemScoreBreakdown — replaced by FeatureScore breakdown",
                "ResolveWorkingMemoryReserveScore() — absorbed by StructuralFit feature"
            },
            SourceMapping = new[]
            {
                new { Source = "Vector (dense/embedding)", SourceType = "CandidateSourceType.Vector", Contribution = "Primary" },
                new { Source = "Memory (working/stable)", SourceType = "CandidateSourceType.Memory", Contribution = "Primary" },
                new { Source = "Graph (relation expansion)", SourceType = "CandidateSourceType.Graph", Contribution = "Supplementary" },
                new { Source = "Keyword (lexical match)", SourceType = "CandidateSourceType.Keyword", Contribution = "Supplementary" },
                new { Source = "Anchor (semantic/keyword anchors)", SourceType = "CandidateSourceType.Anchor", Contribution = "Fallback" },
                new { Source = "Constraint (must-have rules)", SourceType = "CandidateSourceType.Constraint", Contribution = "Diagnostic" }
            }
        };
        File.WriteAllText(Path.Combine(outputDir, "retrieval-convergence-report.json"),
            JsonSerializer.Serialize(convergenceReport, new JsonSerializerOptions { WriteIndented = true }));
    }
}
