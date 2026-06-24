# Vector Guarded Scoped Runtime Experiment Rollback Smoke

Generated: `2026-06-17T07:03:12.6873872+00:00`
OperationId: `vector-guarded-scoped-runtime-experiment-rollback-smoke-cb7d13db656948689586741184d8f559`

- ExperimentPassed: `True`
- Recommendation: `ReadyForScopedRuntimeExperimentObservation`
- ProposalId: `vsrep-bb5402e39c0f1333`
- ApprovalId: `vsrea-49cee0621cf6bc41`
- ApprovalMode: `ScopedRuntimeExperiment`
- Mode/Profile: `ShadowRuntimeExperiment` / `post-scoring-risk-gated-v1`
- RequestCount: `1`
- ExperimentRouteHitCount: `0`
- NonAllowlistedRequestCount/Leak: `1` / `0`
- BaselinePackageCount: `2`
- ExperimentPreviewPackageCount: `0`
- CandidateAdd/Remove: `0` / `0`
- TokenDeltaTotal/Max: `0` / `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalPackageWritten: `False`
- KillSwitchAvailable/Triggered: `True` / `True`
- RollbackVerified: `True`
- ErrorCount: `0`
- LatencyP50/P95: `0` / `0`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## Selected Scopes
- `contextcore_eval/dataset-v2-stress/dataset-v2-stress`

## Blocked Reasons
- (empty)

This report is shadow-only. It does not bind IVectorIndexStore, write formal packages, mutate PackingPolicy, or change package output.
