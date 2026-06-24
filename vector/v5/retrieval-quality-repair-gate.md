# Vector Retrieval Quality Repair Preview Gate

Generated: `2026-06-17T17:55:06.9880162+00:00`
OperationId: `retrieval-quality-repair-gate-5db0a27a1db44a70b4b4a0282e6470dd`

## Summary

- PreviewPassed: `True`
- GatePassed: `True`
- Recommendation: `ReadyForRetrievalQualityRepairFreeze`
- AllowedMode: `PreviewOnly`
- RequiredNextPhase: `RetrievalQualityRepairFreeze`
- VectorProviderSource: `post-scoring-risk-gated-v1`
- GraphCandidateSource: `read-only relation evidence / expansion preview`
- TopK: `5`
- MaxTokenDeltaTotal: `4000`
- MaxTokenDeltaPerSample: `200`
- BestProfileId: `combined-repair`
- FormalOutputChanged: `0`
- FormalSelectedSetChanged: `False`
- FormalPackageWritten: `False`
- ShadowPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- NoRuntimeMutationInvariant: `True`

## Baseline
- profileId: `baseline`
- recall/precision/mrr: `0.5083/0.1017/0.2275`
- mustHitTotal/recalled/belowTopK: `120/61/59`
- contribution (vector/graph/overlap/vOnly/gOnly): `600/235/81/519/154`
- noise (graph/vector/rankingRegression): `0/0/0`
- risk/mustNot/lifecycle/section: `0/0/0/0`
- token total: `16670`

## Profile Comparison
- profileId: `baseline` (Baseline)
  - recall/precision/mrr: `0.5083/0.1017/0.2275` (delta: `0.0000/0.0000/0.0000`)
  - mustHitBelowTopK: `59` (delta: `0`)
  - tokens (repair/baseline/delta): `16670/16670/0`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
- profileId: `candidate-pool-expansion` (Candidate pool expansion)
  - recall/precision/mrr: `0.5083/0.1017/0.2275` (delta: `0.0000/0.0000/0.0000`)
  - mustHitBelowTopK: `59` (delta: `0`)
  - tokens (repair/baseline/delta): `16670/16670/0`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `VectorTopK=10, GraphTopK=10, MergedTopK=12`
- profileId: `topk-adjustment` (TopK adjustment (merged-only))
  - recall/precision/mrr: `0.5083/0.1017/0.2275` (delta: `0.0000/0.0000/0.0000`)
  - mustHitBelowTopK: `59` (delta: `0`)
  - tokens (repair/baseline/delta): `16670/16670/0`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `MergedTopK=8`
- profileId: `section-aware-boost` (Section-aware boost)
  - recall/precision/mrr: `0.5083/0.1017/0.2275` (delta: `0.0000/0.0000/0.0000`)
  - mustHitBelowTopK: `59` (delta: `0`)
  - tokens (repair/baseline/delta): `16670/16670/0`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `SectionBoost=1.50`
- profileId: `must-hit-evidence-boost` (Must-hit evidence boost)
  - recall/precision/mrr: `0.5083/0.1017/0.2275` (delta: `0.0000/0.0000/0.0000`)
  - mustHitBelowTopK: `59` (delta: `0`)
  - tokens (repair/baseline/delta): `16670/16670/0`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `EvidenceBoost=1.75`
- profileId: `graph-relation-anchor-boost` (Graph relation anchor boost)
  - recall/precision/mrr: `1.0000/0.2000/0.9083` (delta: `+0.4917/+0.0983/+0.6808`)
  - mustHitBelowTopK: `0` (delta: `-59`)
  - tokens (repair/baseline/delta): `16674/16670/4`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `RelationBoost=1.60`
- profileId: `lexical-fallback-boost` (Lexical fallback boost)
  - recall/precision/mrr: `0.5083/0.1017/0.2275` (delta: `0.0000/0.0000/0.0000`)
  - mustHitBelowTopK: `59` (delta: `0`)
  - tokens (repair/baseline/delta): `16670/16670/0`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `LexicalBoost=1.40`
- profileId: `combined-repair` (Combined repair)
  - recall/precision/mrr: `1.0000/0.2000/0.9083` (delta: `+0.4917/+0.0983/+0.6808`)
  - mustHitBelowTopK: `0` (delta: `-59`)
  - tokens (repair/baseline/delta): `16674/16670/4`
  - regressions (recall/mrr/graphNoise/ranking/risk/tokenBudget): `False/False/False/False/False/False`
  - adjustments: `VectorTopK=10, GraphTopK=10, MergedTopK=12, SectionBoost=1.50, EvidenceBoost=1.75, RelationBoost=1.60, LexicalBoost=1.40`

## Blocked Reasons
- (empty)

## Source Reports
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v53PackageShadowGate: `vector\v5\formal-adapter-package-shadow-comparison-gate.json`
- v54QualityGate: `vector\v5\graph-vector-retrieval-quality-gate.json`

V5.5 preview only. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.
