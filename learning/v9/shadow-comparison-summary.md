# V9.1-V9.3 Shadow Comparison Summary

- BestRankerCandidate: `LogisticBaseline` (pairwiseAccuracy=1.000)
- BestRouterCandidate: `RouterIntentLogistic` (accuracy=0.121)
- ShadowOnly: `True` RuntimeAuthority: `False` GateAuthority: `False`

## Residual Failure Clusters
- WeightedBaseline: 8 top failures (acc=0.862)
- RouterIntentLogistic: 10 top failures (acc=0.121)
- RouterIntentTree: 10 top failures (acc=0.061)

## Next Training Priorities
- Expand hard-negative dataset to 50+ samples (V9.4 LLM-assisted generation)
- Train full MLP on exported dataset (V9.3 dedicated)
- Improve top router baseline accuracy beyond 0.85
- Run V9.4 LLM-assisted failure diagnosis on exported failure samples
