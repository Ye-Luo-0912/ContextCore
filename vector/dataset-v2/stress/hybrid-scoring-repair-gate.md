# Retrieval Dataset V2 hybrid-scoring-repair-gate

- DatasetId: `rdsv2-stress-a9f2c86e8d1df488`
- BestProfileName: `post-scoring-risk-gated-v1`
- GatePassed: `True`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- Recommendation: `ReadyForDatasetV2StressFreeze`

## Profiles
| Profile | Recall | HoldoutRecall | MRR | DenseDelta | HoldoutDelta | DenseLost | BelowTopK | Negative | AnchorRegression | Risk | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| baseline-hybrid-full | 43.33% | 62.50% | 0.1911 | -4.17% | 0.00% | 5 | 68 | 5 | 1 | 6 | BlockedByRisk |
| dense-preserving-union-v1 | 47.50% | 62.50% | 0.2101 | 0.00% | 0.00% | 0 | 63 | 7 | 1 | 9 | BlockedByRisk |
| dense-winner-floor-v1 | 47.50% | 62.50% | 0.2101 | 0.00% | 0.00% | 0 | 63 | 7 | 1 | 9 | BlockedByRisk |
| negative-distractor-penalty-v1 | 50.83% | 75.00% | 0.2275 | 3.33% | 12.50% | 0 | 59 | 5 | 0 | 7 | BlockedByRisk |
| post-scoring-risk-gated-v1 | 50.83% | 75.00% | 0.2275 | 3.33% | 12.50% | 0 | 59 | 0 | 0 | 0 | ReadyForDatasetV2StressFreeze |
| anchor-score-capped-v1 | 43.33% | 62.50% | 0.1911 | -4.17% | 0.00% | 5 | 68 | 5 | 1 | 6 | BlockedByRisk |
| contribution-aware-rerank-v1 | 47.50% | 62.50% | 0.2101 | 0.00% | 0.00% | 0 | 63 | 7 | 1 | 9 | BlockedByRisk |
| combined-safe-v1 | 47.50% | 62.50% | 0.2101 | 0.00% | 0.00% | 0 | 63 | 7 | 1 | 9 | BlockedByRisk |
