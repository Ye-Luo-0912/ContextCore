# Strategy Scoring Architecture (V13.3)

## Overview

ContextCore V13.3 upgrades from a single unified scoring function to a **strategy-based scoring registry**. Different query domains (chat, coding, audit, eval) and runtime conditions (memory pressure, token budget) select different scoring strategies with tuned feature weights.

## Architecture

```
Query → CandidateAggregator → StrategySelector → UnifiedCandidateScorer(strategy) → LifecycleFilter → PackageBuilder
                                    ↑
                           StrategyRegistry
                           ┌──────────────────┐
                           │ precision-core   │
                           │ recall-broad     │
                           │ memory-dense     │
                           │ graph-expand     │
                           │ compression-tight│
                           │ balanced-default │
                           │ speed-fast       │
                           │ audit-strict     │
                           └──────────────────┘
                           LLM (advisory only)
                           ┌──────────────────┐
                           │ suggest strategy  │
                           │ explain ranking   │
                           │ propose hints     │
                           └──────────────────┘
```

## Strategy Profiles

| Strategy | Intent | Domain | Lifecycle | MaxCandidates | Key Features |
|---|---|---|---|---|---|
| precision-core | Precision | Chat | Active | 40 | High authority (1.0), lifecycle penalty 1.2 |
| recall-broad | Recall | Coding | Active | 80 | Relaxed authority (0.5), freshness boost (0.8) |
| memory-dense | MemoryAware | Chat | Active | 60 | User preference (0.7), prefers memory |
| graph-expand | GraphAware | Production | Candidate | 50 | Prefers graph candidates |
| compression-tight | Compression | Production | Candidate | 20 | High relevance (1.2), aggressive lifecycle (1.5) |
| balanced-default v2 | Balanced | Chat | Active | 60 | Equal distribution, no source preference |
| speed-fast | Speed | Eval | Experimental | 15 | Minimal overhead, relaxed filters |
| audit-strict | Precision | Audit | Active | 100 | Max authority (2.0), no freshness/preference |

## Feature Weights by Strategy

| Strategy | Relevance | Authority | Freshness | StructuralFit | UserPreference | LifecyclePenalty |
|---|---|---|---|---|---|---|
| precision-core v1 | 1 | 1 | 0.4 | 0.7 | 0.3 | 1.2 |
| recall-broad v1 | 1 | 0.5 | 0.8 | 0.3 | 0.5 | 0.7 |
| memory-dense v1 | 1 | 0.6 | 0.5 | 0.5 | 0.7 | 0.8 |
| graph-expand v1 | 0.9 | 0.7 | 0.5 | 0.6 | 0.3 | 1 |
| compression-tight v1 | 1.2 | 0.8 | 0.3 | 0.9 | 0.2 | 1.5 |
| balanced-default v2 | 1 | 0.8 | 0.6 | 0.5 | 0.4 | 1 |
| balanced-default v1 | 1 | 1 | 0.5 | 0.5 | 0.5 | 1 |
| speed-fast v1 | 0.8 | 0.3 | 0.2 | 0.2 | 0.1 | 0.5 |
| audit-strict v1 | 1 | 2 | 0 | 0.3 | 0 | 2 |

## LLM Adapter

- **Allowed**: suggest strategy, explain ranking, propose candidate hints
- **Forbidden**: score candidates, execute selection, mutate state, override weights
- LLM suggestions are advisory only; StrategySelector makes the final deterministic decision

## Strategy Versioning

- Strategies are versioned (v1, v2, ...) and lifecycle-tracked (Experimental → Candidate → Active → Deprecated → Retired)
- Multiple versions of the same strategy can coexist (e.g., balanced-default v1 and v2)
- StrategySelector always picks the highest Active version unless explicitly overridden

## Safety Constraints

- No global scoring function — every query routes through a specific strategy
- LLM never participates in scoring or execution path
- Candidate pipeline unchanged — strategy only affects scoring weights
- PackageBuilder remains separated — token budget, dedup, sectioning only
- No runtime promotion, no storage architecture change
