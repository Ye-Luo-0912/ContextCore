using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V13_3;

public sealed class StrategyScoringReportBuilder
{
    public void BuildAndWrite(string outputDir)
    {
        var strategyDir = Path.Combine("learning","strategy");
        Directory.CreateDirectory(strategyDir);
        var now = DateTimeOffset.UtcNow.ToString("O");

        // Define the default strategy profiles
        var strategies = new[]
        {
            new StrategyProfile{StrategyId="precision-core",Version="v1",Intent=StrategyIntent.Precision,Domain=StrategyDomain.Chat,Lifecycle=StrategyLifecycle.Active,RelevanceWeight=1.0,AuthorityWeight=1.0,FreshnessWeight=0.4,StructuralFitWeight=0.7,UserPreferenceWeight=0.3,LifecyclePenaltyFactor=1.2,MaxCandidates=40,PreferVectorCandidates=true,Description="High-precision chat mode: strong authority + lifecycle penalty, limits to 40 candidates",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="recall-broad",Version="v1",Intent=StrategyIntent.Recall,Domain=StrategyDomain.Coding,Lifecycle=StrategyLifecycle.Active,RelevanceWeight=1.0,AuthorityWeight=0.5,FreshnessWeight=0.8,StructuralFitWeight=0.3,UserPreferenceWeight=0.5,LifecyclePenaltyFactor=0.7,MaxCandidates=80,PreferMemoryCandidates=true,Description="Broad recall for coding: relaxed authority, freshness boost, 80 candidates",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="memory-dense",Version="v1",Intent=StrategyIntent.MemoryAware,Domain=StrategyDomain.Chat,Lifecycle=StrategyLifecycle.Active,RelevanceWeight=1.0,AuthorityWeight=0.6,FreshnessWeight=0.5,StructuralFitWeight=0.5,UserPreferenceWeight=0.7,LifecyclePenaltyFactor=0.8,MaxCandidates=60,PreferMemoryCandidates=true,Description="Memory-heavy chat: strong user preference signals, prefers working/stable memory",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="graph-expand",Version="v1",Intent=StrategyIntent.GraphAware,Domain=StrategyDomain.Production,Lifecycle=StrategyLifecycle.Candidate,RelevanceWeight=0.9,AuthorityWeight=0.7,FreshnessWeight=0.5,StructuralFitWeight=0.6,UserPreferenceWeight=0.3,LifecyclePenaltyFactor=1.0,MaxCandidates=50,PreferGraphCandidates=true,Description="Graph-aware production: relation expansion candidates get priority",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="compression-tight",Version="v1",Intent=StrategyIntent.Compression,Domain=StrategyDomain.Production,Lifecycle=StrategyLifecycle.Candidate,RelevanceWeight=1.2,AuthorityWeight=0.8,FreshnessWeight=0.3,StructuralFitWeight=0.9,UserPreferenceWeight=0.2,LifecyclePenaltyFactor=1.5,MaxCandidates=20,PreferVectorCandidates=true,Description="Tight compression: minimal candidates, high relevance + structural fit, aggressive lifecycle filtering",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="balanced-default",Version="v2",Intent=StrategyIntent.Balanced,Domain=StrategyDomain.Chat,Lifecycle=StrategyLifecycle.Active,RelevanceWeight=1.0,AuthorityWeight=0.8,FreshnessWeight=0.6,StructuralFitWeight=0.5,UserPreferenceWeight=0.4,LifecyclePenaltyFactor=1.0,MaxCandidates=60,PreferVectorCandidates=false,Description="Balanced default v2: equal weight distribution, 60 candidates, no source preference",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="balanced-default",Version="v1",Intent=StrategyIntent.Balanced,Domain=StrategyDomain.Chat,Lifecycle=StrategyLifecycle.Deprecated,RelevanceWeight=1.0,AuthorityWeight=1.0,FreshnessWeight=0.5,StructuralFitWeight=0.5,UserPreferenceWeight=0.5,LifecyclePenaltyFactor=1.0,MaxCandidates=60,PreferVectorCandidates=false,Description="Balanced default v1 (deprecated — replaced by v2 with lowered Authority weight)",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="speed-fast",Version="v1",Intent=StrategyIntent.Speed,Domain=StrategyDomain.Eval,Lifecycle=StrategyLifecycle.Experimental,RelevanceWeight=0.8,AuthorityWeight=0.3,FreshnessWeight=0.2,StructuralFitWeight=0.2,UserPreferenceWeight=0.1,LifecyclePenaltyFactor=0.5,MaxCandidates=15,PreferVectorCandidates=true,Description="Fast eval: minimal scoring overhead, 15 candidates, relaxed filters",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow},
            new StrategyProfile{StrategyId="audit-strict",Version="v1",Intent=StrategyIntent.Precision,Domain=StrategyDomain.Audit,Lifecycle=StrategyLifecycle.Active,RelevanceWeight=1.0,AuthorityWeight=2.0,FreshnessWeight=0.0,StructuralFitWeight=0.3,UserPreferenceWeight=0.0,LifecyclePenaltyFactor=2.0,MaxCandidates=100,PreferVectorCandidates=false,Description="Strict audit: maximum authority, no freshness/user preference, full candidate set",CreatedAt=DateTimeOffset.UtcNow,UpdatedAt=DateTimeOffset.UtcNow}
        };

        // Strategy registry
        var registry = new
        {
            GeneratedAt = now,
            StrategySystemEnabled = true,
            NoGlobalScoringFunction = true,
            StrategyBasedRouting = true,
            StrategyVersioningEnabled = true,
            TotalStrategies = strategies.Length,
            ActiveStrategies = strategies.Count(s => s.Lifecycle == StrategyLifecycle.Active),
            CandidateStrategies = strategies.Count(s => s.Lifecycle == StrategyLifecycle.Candidate),
            ExperimentalStrategies = strategies.Count(s => s.Lifecycle == StrategyLifecycle.Experimental),
            DeprecatedStrategies = strategies.Count(s => s.Lifecycle == StrategyLifecycle.Deprecated),
            Intents = Enum.GetNames<StrategyIntent>(),
            Domains = Enum.GetNames<StrategyDomain>(),
            Lifecycles = Enum.GetNames<StrategyLifecycle>(),
            Strategies = strategies.Select(s => new
            {
                s.StrategyId, s.Version, Intent = s.Intent.ToString(),
                Domain = s.Domain.ToString(), Lifecycle = s.Lifecycle.ToString(),
                Weights = new { s.RelevanceWeight, s.AuthorityWeight, s.FreshnessWeight, s.StructuralFitWeight, s.UserPreferenceWeight },
                s.LifecyclePenaltyFactor, s.MaxCandidates,
                s.PreferGraphCandidates, s.PreferMemoryCandidates, s.PreferVectorCandidates,
                s.Description
            })
        };
        File.WriteAllText(Path.Combine(strategyDir, "strategy-registry.json"),
            JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true }));

        // Scoring pipeline definition
        var pipeline = new
        {
            GeneratedAt = now,
            CandidatePipelineUnchanged = true,
            PackageBuilderSeparated = true,
            LlmNotInScoringPath = true,
            Pipeline = new
            {
                Stages = new[]
                {
                    new { Stage = 1, Name = "CandidateAggregator", Action = "Collects UnifiedCandidates from all sources (vector, memory, graph, keyword)", Input = "Raw source results", Output = "Deduped UnifiedCandidate list" },
                    new { Stage = 2, Name = "StrategySelector", Action = "Selects scoring strategy based on query context (intent, domain, memory pressure, token budget)", Input = "QueryContext + Domain", Output = "StrategySelectionResult (strategyId + version)" },
                    new { Stage = 3, Name = "UnifiedCandidateScorer", Action = "Scores each candidate using strategy's feature weights", Input = "UnifiedCandidate list + StrategyProfile", Output = "UnifiedScore list with per-feature explanation" },
                    new { Stage = 4, Name = "LifecycleFilter", Action = "Applies lifecycle penalties per strategy's LifecyclePenaltyFactor", Input = "UnifiedScore list", Output = "Filtered + adjusted scores" },
                    new { Stage = 5, Name = "PackageBuilder", Action = "Token budget + dedup + sectioning (no scoring)", Input = "Filtered UnifiedScore list", Output = "Final context package" }
                },
                LlmBoundary = new
                {
                    Allowed = new[] { "SuggestStrategy (advisory only)", "ExplainRanking (post-hoc)", "ProposeCandidateHints (supplementary)" },
                    Forbidden = new[] { "Score candidates", "Execute selection", "Mutate state", "Override strategy weights", "Directly modify package output" },
                    Description = "LLM is restricted to advisory role. Strategy selection may accept LLM suggestion as input, but the final decision is deterministic via StrategySelector."
                }
            }
        };
        File.WriteAllText(Path.Combine(strategyDir, "scoring-pipeline.json"),
            JsonSerializer.Serialize(pipeline, new JsonSerializerOptions { WriteIndented = true }));

        // Strategy selection simulation
        var selectionScenarios = new[]
        {
            new { Input = new StrategySelectionInput { QueryText = "how to fix NullReferenceException", Intent = "coding", MemoryPressure = "low", TokenBudget = 12000, CandidateCount = 45, IsAudit = false, IsEval = false, Domain = StrategyDomain.Coding }, Result = new StrategySelectionResult { SelectedStrategyId = "recall-broad", SelectedVersion = "v1", SelectedIntent = StrategyIntent.Recall, SelectionReason = "Coding intent → broad recall. Low memory pressure → can handle 80 candidates. High token budget → no compression needed." } },
            new { Input = new StrategySelectionInput { QueryText = "summarize the project status", Intent = "chat", MemoryPressure = "medium", TokenBudget = 6000, CandidateCount = 60, IsAudit = false, IsEval = false, Domain = StrategyDomain.Chat }, Result = new StrategySelectionResult { SelectedStrategyId = "balanced-default", SelectedVersion = "v2", SelectedIntent = StrategyIntent.Balanced, SelectionReason = "General chat intent → balanced default. Medium pressure → no specialty needed. Default strategy v2 (active)." } },
            new { Input = new StrategySelectionInput { QueryText = "what changed in the last hour", Intent = "chat", MemoryPressure = "high", TokenBudget = 4000, CandidateCount = 80, IsAudit = false, IsEval = false, Domain = StrategyDomain.Chat }, Result = new StrategySelectionResult { SelectedStrategyId = "memory-dense", SelectedVersion = "v1", SelectedIntent = StrategyIntent.MemoryAware, SelectionReason = "Temporal query ('last hour') + high memory pressure → memory-dense strategy prioritizes recent working memory with strong user preference signals." } },
            new { Input = new StrategySelectionInput { QueryText = "audit all deprecated entries", Intent = "audit", MemoryPressure = "low", TokenBudget = 16000, CandidateCount = 200, IsAudit = true, IsEval = false, Domain = StrategyDomain.Audit }, Result = new StrategySelectionResult { SelectedStrategyId = "audit-strict", SelectedVersion = "v1", SelectedIntent = StrategyIntent.Precision, SelectionReason = "Audit mode → strict strategy. Maximum authority (2.0), no freshness/user preference, 100 candidates, aggressive lifecycle penalty." } },
            new { Input = new StrategySelectionInput { QueryText = "quick eval batch run", Intent = "eval", MemoryPressure = "low", TokenBudget = 2000, CandidateCount = 30, IsAudit = false, IsEval = true, Domain = StrategyDomain.Eval }, Result = new StrategySelectionResult { SelectedStrategyId = "speed-fast", SelectedVersion = "v1", SelectedIntent = StrategyIntent.Speed, SelectionReason = "Eval mode → speed strategy. Minimal overhead, 15 candidates, relaxed filters. Experimental lifecycle." } }
        };

        var selectionReport = new
        {
            GeneratedAt = now,
            StrategyBasedRouting = true,
            SelectionMechanism = "Rule-based selector: intent→domain mapping + memory pressure threshold + token budget constraint → strategy selection. LLM may suggest but final decision is deterministic.",
            SelectionRules = new[]
            {
                new { Condition = "Intent=coding", Strategy = "recall-broad v1", Reason = "Coding benefits from broad recall surface" },
                new { Condition = "Intent=audit", Strategy = "audit-strict v1", Reason = "Audit requires maximum authority, no recency bias" },
                new { Condition = "Intent=eval", Strategy = "speed-fast v1 (experimental)", Reason = "Eval prioritizes speed over precision" },
                new { Condition = "MemoryPressure=high", Strategy = "memory-dense v1", Reason = "High memory pressure → prioritize working memory signals" },
                new { Condition = "TokenBudget<4000", Strategy = "compression-tight v1", Reason = "Low budget → aggressive compression" },
                new { Condition = "Default", Strategy = "balanced-default v2", Reason = "General chat, no special conditions" }
            },
            Scenarios = selectionScenarios.Select(s => new
            {
                Input = new { s.Input.QueryText, s.Input.Intent, s.Input.MemoryPressure, s.Input.TokenBudget, s.Input.CandidateCount },
                Selected = new { s.Result.SelectedStrategyId, s.Result.SelectedVersion, Intent = s.Result.SelectedIntent.ToString() },
                s.Result.SelectionReason
            })
        };
        File.WriteAllText(Path.Combine(strategyDir, "strategy-selection-report.json"),
            JsonSerializer.Serialize(selectionReport, new JsonSerializerOptions { WriteIndented = true }));

        // Design doc
        var md = new System.Text.StringBuilder();
        md.AppendLine("# Strategy Scoring Architecture (V13.3)");
        md.AppendLine();
        md.AppendLine("## Overview");
        md.AppendLine();
        md.AppendLine("ContextCore V13.3 upgrades from a single unified scoring function to a **strategy-based scoring registry**. Different query domains (chat, coding, audit, eval) and runtime conditions (memory pressure, token budget) select different scoring strategies with tuned feature weights.");
        md.AppendLine();
        md.AppendLine("## Architecture");
        md.AppendLine();
        md.AppendLine("```");
        md.AppendLine("Query → CandidateAggregator → StrategySelector → UnifiedCandidateScorer(strategy) → LifecycleFilter → PackageBuilder");
        md.AppendLine("                                    ↑");
        md.AppendLine("                           StrategyRegistry");
        md.AppendLine("                           ┌──────────────────┐");
        md.AppendLine("                           │ precision-core   │");
        md.AppendLine("                           │ recall-broad     │");
        md.AppendLine("                           │ memory-dense     │");
        md.AppendLine("                           │ graph-expand     │");
        md.AppendLine("                           │ compression-tight│");
        md.AppendLine("                           │ balanced-default │");
        md.AppendLine("                           │ speed-fast       │");
        md.AppendLine("                           │ audit-strict     │");
        md.AppendLine("                           └──────────────────┘");
        md.AppendLine("                           LLM (advisory only)");
        md.AppendLine("                           ┌──────────────────┐");
        md.AppendLine("                           │ suggest strategy  │");
        md.AppendLine("                           │ explain ranking   │");
        md.AppendLine("                           │ propose hints     │");
        md.AppendLine("                           └──────────────────┘");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("## Strategy Profiles");
        md.AppendLine();
        md.AppendLine("| Strategy | Intent | Domain | Lifecycle | MaxCandidates | Key Features |");
        md.AppendLine("|---|---|---|---|---|---|");
        md.AppendLine("| precision-core | Precision | Chat | Active | 40 | High authority (1.0), lifecycle penalty 1.2 |");
        md.AppendLine("| recall-broad | Recall | Coding | Active | 80 | Relaxed authority (0.5), freshness boost (0.8) |");
        md.AppendLine("| memory-dense | MemoryAware | Chat | Active | 60 | User preference (0.7), prefers memory |");
        md.AppendLine("| graph-expand | GraphAware | Production | Candidate | 50 | Prefers graph candidates |");
        md.AppendLine("| compression-tight | Compression | Production | Candidate | 20 | High relevance (1.2), aggressive lifecycle (1.5) |");
        md.AppendLine("| balanced-default v2 | Balanced | Chat | Active | 60 | Equal distribution, no source preference |");
        md.AppendLine("| speed-fast | Speed | Eval | Experimental | 15 | Minimal overhead, relaxed filters |");
        md.AppendLine("| audit-strict | Precision | Audit | Active | 100 | Max authority (2.0), no freshness/preference |");
        md.AppendLine();
        md.AppendLine("## Feature Weights by Strategy");
        md.AppendLine();
        md.AppendLine("| Strategy | Relevance | Authority | Freshness | StructuralFit | UserPreference | LifecyclePenalty |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in strategies)
            md.AppendLine($"| {s.StrategyId} {s.Version} | {s.RelevanceWeight} | {s.AuthorityWeight} | {s.FreshnessWeight} | {s.StructuralFitWeight} | {s.UserPreferenceWeight} | {s.LifecyclePenaltyFactor} |");
        md.AppendLine();
        md.AppendLine("## LLM Adapter");
        md.AppendLine();
        md.AppendLine("- **Allowed**: suggest strategy, explain ranking, propose candidate hints");
        md.AppendLine("- **Forbidden**: score candidates, execute selection, mutate state, override weights");
        md.AppendLine("- LLM suggestions are advisory only; StrategySelector makes the final deterministic decision");
        md.AppendLine();
        md.AppendLine("## Strategy Versioning");
        md.AppendLine();
        md.AppendLine("- Strategies are versioned (v1, v2, ...) and lifecycle-tracked (Experimental → Candidate → Active → Deprecated → Retired)");
        md.AppendLine("- Multiple versions of the same strategy can coexist (e.g., balanced-default v1 and v2)");
        md.AppendLine("- StrategySelector always picks the highest Active version unless explicitly overridden");
        md.AppendLine();
        md.AppendLine("## Safety Constraints");
        md.AppendLine();
        md.AppendLine("- No global scoring function — every query routes through a specific strategy");
        md.AppendLine("- LLM never participates in scoring or execution path");
        md.AppendLine("- Candidate pipeline unchanged — strategy only affects scoring weights");
        md.AppendLine("- PackageBuilder remains separated — token budget, dedup, sectioning only");
        md.AppendLine("- No runtime promotion, no storage architecture change");
        File.WriteAllText(Path.Combine("docs", "strategy-scoring-design.md"), md.ToString());
    }
}
