# Vector Limited Formal Preview Observation Gate

Generated: `2026-06-16T01:57:33.2142307+00:00`
OperationId: `vector-limited-formal-preview-observation-gate-787aee0591d3449b8c7e380798933e47`

## Summary

- ObservationPassed: `False`
- GatePassed: `True`
- Recommendation: `ReadyForFormalPreviewFreeze`
- Mode: `PreviewOnly`
- ProfileName: `post-scoring-risk-gated-v1`
- ObservationRunCount: `3`
- MinimumObservationRunCount: `3`
- PreviewPackageCount: `360`
- BaselinePackageCount: `360`
- CandidateAddCount: `171`
- CandidateRemoveCount: `171`
- CandidateUnchangedCount: `1629`
- SectionChangedCount: `0`
- TokenDeltaTotal: `165`
- TokenDeltaMax: `10`
- TokenDeltaP95: `10`
- ConstraintCoverageDelta: `0.016666666666666666`
- RelationCoverageDelta: `0.05694444444444445`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- FormalPackageWritten: `False`
- RuntimeMutated: `False`
- NonAllowlistedScopeLeakCount: `0`
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
- scopedFormalPreviewOptInGate: `vector\v4\vector-scoped-formal-preview-optin-gate.json`
- shadowPackageComparisonGate: `vector\v4\vector-shadow-package-comparison-gate.json`

## Runtime Boundary

- 本报告只聚合 preview-only observation，不写正式 package。
- `FormalPackageWritten=false`、`RuntimeMutated=false`、`PackageOutputChanged=false`、`PackingPolicyChanged=false` 是 gate 条件。
- `UseForRuntime=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`。
