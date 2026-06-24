# Vector Guarded Scoped Runtime Experiment

Generated: `2026-06-17T07:01:35.3427964+00:00`
OperationId: `vector-guarded-scoped-runtime-experiment-experiment-7eceec22f81d489792c0dff5455f739b`

- ExperimentPassed: `True`
- Recommendation: `ReadyForScopedRuntimeExperimentObservation`
- ProposalId: `vsrep-bb5402e39c0f1333`
- ApprovalId: `vsrea-49cee0621cf6bc41`
- ApprovalMode: `ScopedRuntimeExperiment`
- Mode/Profile: `ShadowRuntimeExperiment` / `post-scoring-risk-gated-v1`
- RequestCount: `3`
- ExperimentRouteHitCount: `3`
- NonAllowlistedRequestCount/Leak: `1` / `0`
- BaselinePackageCount: `4`
- ExperimentPreviewPackageCount: `3`
- CandidateAdd/Remove: `3` / `3`
- TokenDeltaTotal/Max: `30` / `10`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalPackageWritten: `False`
- KillSwitchAvailable/Triggered: `True` / `False`
- RollbackVerified: `True`
- ErrorCount: `0`
- LatencyP50/P95: `8` / `12`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## Selected Scopes
- `contextcore_eval/dataset-v2-stress/dataset-v2-stress`

## Blocked Reasons
- (empty)

This report is shadow-only. It does not bind IVectorIndexStore, write formal packages, mutate PackingPolicy, or change package output.
