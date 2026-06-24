# Vector Scoped Formal Preview Opt-in Plan

Generated: `2026-06-16T00:54:24.9782841+00:00`
OperationId: `vector-scoped-formal-preview-optin-plan-430bf91e741446389dd319b28578fa20`

## Summary

- PlanPassed: `True`
- SmokePassed: `False`
- GatePassed: `False`
- Recommendation: `ReadyForLimitedFormalPreviewObservation`
- Mode: `PreviewOnly`
- ProfileName: `post-scoring-risk-gated-v1`
- SelectedWorkspaceId: `contextcore_eval`
- SelectedCollectionId: `dataset-v2-stress`
- SelectedEvalScope: `dataset-v2-stress`
- ScopeCount: `2`
- AllowlistedScopeCount: `1`
- NonAllowlistedScopeChecked: `True`
- PreviewPackageCount: `120`
- BaselinePackageCount: `120`
- CandidateAddCount: `57`
- CandidateRemoveCount: `57`
- TokenDeltaTotal: `55`
- TokenDeltaMax: `10`
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
- RollbackInstruction: `Remove the selected workspace, collection, and eval scope from allowlists; keep Mode=Off.`

## WorkspaceAllowlist
- `contextcore_eval`

## CollectionAllowlist
- `dataset-v2-stress`

## EvalScopeAllowlist
- `dataset-v2-stress`

## BlockedReasons
- (empty)

## Gate Dependency Summary
- GuardedFormalRetrievalPreview: `True:ReadyForShadowPackageComparison`
- ShadowPackageComparison: `True:ReadyForScopedFormalPreviewOptIn`
- V4ReadinessRecheck: `True:ReadyForGuardedFormalPreview`

## Source Reports
- guardedFormalRetrievalPreviewGate: `vector\v4\vector-guarded-formal-retrieval-preview-gate.json`
- shadowPackageComparisonGate: `vector\v4\vector-shadow-package-comparison-gate.json`
- v4ReadinessRecheck: `vector\v4\vector-v4-readiness-recheck.json`

## Runtime Boundary

- Scoped formal preview opt-in 只允许显式 allowlist scope 的 preview-only。
- 非 allowlisted scope 必须保持 current formal baseline path。
- `FormalPackageWritten=false`、`RuntimeMutated=false`、`PackageOutputChanged=false`、`PackingPolicyChanged=false` 是 gate 条件。
- `UseForRuntime`、`FormalRetrievalAllowed`、`ReadyForRuntimeSwitch` 均保持 `false`。
