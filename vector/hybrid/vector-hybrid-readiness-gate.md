# Hybrid Retrieval Readiness Gate

Generated: 2026-06-15T07:00:42.2808510+00:00
- Passed: `False`
- A3RecallAfterPolicy: `4.55%`
- ExtendedRecallAfterPolicy: `3.12%`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PolicyViolationFound: `False`
- P15GatePassed: `True`
- FormalRetrievalAllowed: `False`
- Recommendation: `BlockedByA3Recall`

## Allowed
- `preview`
- `shadow`
- `eval`

## Forbidden
- `FormalRetrievalSwitch`
- `FormalIVectorIndexStoreBinding`
- `PackingPolicyIntegration`
- `PackageOutputIntegration`

## BlockedReasons
- `A3RecallBelow80Percent`
- `ExtendedRecallBelow80Percent`

## Diagnostics
- `HybridRetrievalReadinessGateBlocked`
