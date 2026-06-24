# Vector Scoped Runtime Experiment Observation Window Summary

Generated: `2026-06-17T07:34:14.8178451+00:00`
OperationId: `vector-scoped-runtime-experiment-observation-window-summary-b0082f2b09c34885bb18fd3f14c7f0ea`

- ObservationWindowId: `vsreow-df16886e0acecbb2`
- ObservationPassed: `True`
- Recommendation: `ReadyForScopedRuntimeExperimentObservationFreeze`
- ProposalId/ApprovalId: `vsrep-bb5402e39c0f1333` / `vsrea-49cee0621cf6bc41`
- Mode/Profile: `ScopedShadowObservation` / `post-scoring-risk-gated-v1`
- ObservationRunCount: `3`
- RequestCount: `360`
- ExperimentRouteHitCount: `360`
- NonAllowlistedRequestCount/Leak: `3` / `0`
- BaselinePackageCount: `363`
- ExperimentPreviewPackageCount: `360`
- CandidateAdd/Remove: `171` / `171`
- TokenDeltaTotal/Max: `165` / `10`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalPackageWritten: `False`
- KillSwitchAvailable/SmokePassed: `True` / `True`
- RollbackVerified: `True`
- TraceCompleteness: `100`
- ErrorCount: `0`
- LatencyP50/P95: `8` / `12`
- StopConditionTriggered: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## Selected Scopes
- `contextcore_eval/dataset-v2-stress/dataset-v2-stress`

## Blocked Reasons
- (empty)

This V4.15 observation window is shadow-only: no formal package write, no IVectorIndexStore binding mutation, no PackingPolicy mutation, and no package output mutation.
