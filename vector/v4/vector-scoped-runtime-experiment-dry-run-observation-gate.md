# Vector Scoped Runtime Experiment Dry-run Observation Gate

Generated: `2026-06-17T02:36:39.4641830+00:00`
OperationId: `vector-scoped-runtime-experiment-dry-run-observation-gate-4a92cc99bfb142aea4ddd4302c209ab2`

## Summary

- ObservationPassed: `False`
- GatePassed: `True`
- Recommendation: `ReadyForScopedRuntimeExperimentDesignFreeze`
- Mode: `DryRun`
- ProfileName: `post-scoring-risk-gated-v1`
- ObservationRunCount: `3`
- MinimumObservationRunCount: `3`
- AllowlistedScopeCount: `1`
- NonAllowlistedScopeChecked: `True`
- DryRunPackageCount: `360`
- BaselinePackageCount: `360`
- CandidateAddCount: `171`
- CandidateRemoveCount: `171`
- TokenDeltaTotal: `165`
- TokenDeltaMax: `10`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- FormalPackageWritten: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- NonAllowlistedScopeLeakCount: `0`
- RollbackPlanAvailable: `True`
- RuntimeChangeGateConsistent: `True`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## WorkspaceAllowlist
- `contextcore_eval`

## CollectionAllowlist
- `dataset-v2-stress`

## EvalScopeAllowlist
- `dataset-v2-stress`

## BlockedReasons
- (empty)

## Source Reports
- learningRuntimeChangeReadinessGate: `learning\readiness\learning-runtime-change-readiness-gate.json`
- shadowPackageComparisonGate: `vector\v4\vector-shadow-package-comparison-gate.json`
- v45ScopedRuntimeExperimentGate: `vector\v4\vector-scoped-runtime-experiment-gate.json`

## Runtime Boundary

- V4.6 只允许 scoped runtime experiment dry-run observation。
- 不允许 runtime switch、正式 `IVectorIndexStore` 绑定、正式 package 写入、PackingPolicy 集成或 package output mutation。
- 本报告只写 shadow observation artifact；非 allowlisted scope 必须保持 baseline。
