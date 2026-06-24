# Vector Query Expansion Shadow Report

Generated: 2026-06-10T15:07:00.5856312+00:00

## A3 Summary

- Samples: `50`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- VectorProfile: `normal-v1`
- TopK: `10`
- MinSimilarity: `-`
- Recommendation: `NeedsBetterEmbedding`
- BestExpansionProfile: `raw-query-v1`
- RecallBefore: `71.21%`
- RecallAfter: `71.21%`
- MRRBefore: `0.5562`
- MRRAfter: `0.5562`
- RiskAfterPolicy: `0`

| Profile | Recall Before | Recall After | MRR Before | MRR After | Risk | MustNot Risk | Lifecycle Risk | Recovered | Intent Recovered | New Risk | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| raw-query-v1 | 71.21% | 71.21% | 0.5562 | 0.5562 | 0 | 0.00% | 0.00% | 0 | 0 | 0 | NeedsBetterEmbedding |
| constraint-aware-query-v1 | 71.21% | 71.21% | 0.5562 | 0.5444 | 0 | 0.00% | 0.00% | 1 | 0 | 0 | NeedsProfileTuning |
| mode-intent-query-v1 | 71.21% | 69.70% | 0.5562 | 0.5553 | 0 | 0.00% | 0.00% | 0 | 0 | 0 | NeedsBetterEmbedding |
| planning-context-query-v1 | 71.21% | 69.70% | 0.5562 | 0.5553 | 0 | 0.00% | 0.00% | 0 | 0 | 0 | NeedsBetterEmbedding |
| anchor-query-v1 | 71.21% | 69.70% | 0.5562 | 0.5373 | 0 | 0.00% | 0.00% | 1 | 0 | 0 | NeedsProfileTuning |
| intent-anchor-query-v1 | 71.21% | 69.70% | 0.5562 | 0.5373 | 0 | 0.00% | 0.00% | 1 | 0 | 0 | NeedsProfileTuning |

## Extended Summary

- Samples: `113`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- VectorProfile: `normal-v1`
- TopK: `10`
- MinSimilarity: `-`
- Recommendation: `ReadyForRetrievalShadow`
- BestExpansionProfile: `raw-query-v1`
- RecallBefore: `84.38%`
- RecallAfter: `84.38%`
- MRRBefore: `0.7697`
- MRRAfter: `0.7697`
- RiskAfterPolicy: `0`

| Profile | Recall Before | Recall After | MRR Before | MRR After | Risk | MustNot Risk | Lifecycle Risk | Recovered | Intent Recovered | New Risk | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| raw-query-v1 | 84.38% | 84.38% | 0.7697 | 0.7697 | 0 | 0.00% | 0.00% | 0 | 0 | 0 | ReadyForRetrievalShadow |
| mode-intent-query-v1 | 84.38% | 83.75% | 0.7697 | 0.7752 | 0 | 0.00% | 0.00% | 0 | 0 | 0 | ReadyForRetrievalShadow |
| planning-context-query-v1 | 84.38% | 83.75% | 0.7697 | 0.7752 | 0 | 0.00% | 0.00% | 0 | 0 | 0 | ReadyForRetrievalShadow |
| constraint-aware-query-v1 | 84.38% | 83.75% | 0.7697 | 0.7630 | 0 | 0.00% | 0.00% | 1 | 0 | 0 | ReadyForRetrievalShadow |
| anchor-query-v1 | 84.38% | 83.13% | 0.7697 | 0.7643 | 0 | 0.00% | 0.00% | 1 | 0 | 0 | ReadyForRetrievalShadow |
| intent-anchor-query-v1 | 84.38% | 83.13% | 0.7697 | 0.7643 | 0 | 0.00% | 0.00% | 1 | 0 | 0 | ReadyForRetrievalShadow |

