# Vector Scoped Runtime Experiment Dry-run

Generated: `2026-06-16T17:32:50.2125491+00:00`
OperationId: `vector-scoped-runtime-experiment-dry-run-4142db01c659470bb158dcfa9703d4c9`

## Summary

- PlanPassed: `True`
- Recommendation: `ReadyForExplicitScopedRuntimeExperimentDryRun`
- Mode: `DryRun`
- ProfileName: `post-scoring-risk-gated-v1`
- ScopeCount: `2`
- AllowlistedScopeCount: `1`
- NonAllowlistedScopeChecked: `True`
- DryRunSupported: `True`
- RuntimeSwitchAllowed: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- FormalPackageWritten: `False`
- RuntimeMutated: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- NonAllowlistedScopeLeakCount: `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- RollbackPlan: `Remove scopes from allowlists, keep UseForRuntime=false, discard shadow artifacts, rerun V4.F and runtime-change gate.`

## WorkspaceAllowlist
- `contextcore_eval`

## CollectionAllowlist
- `dataset-v2-stress`

## EvalScopeAllowlist
- `dataset-v2-stress`

## Allowed Actions
- `ScopeAllowlistPlanning`
- `PreviewProfileSelection`
- `RollbackPlanDefinition`
- `ObservationMetricsDefinition`
- `DryRunPackageComparisonPlanning`
- `ShadowArtifactOnlyDryRun`

## Forbidden Actions
- `RuntimeSwitch`
- `FormalIVectorIndexStoreBinding`
- `FormalPackageWrite`
- `PackingPolicyIntegration`
- `PackageOutputMutation`
- `GlobalDefaultOn`
- `NonAllowlistedScopeUse`

## Blocked Reasons
- (empty)

## Required Gate Summary
- foundation-release-candidate-gate: `True:ReadyForReleaseCandidate`
- foundation-reproducibility-check: `True:ReadyForReleaseCandidateReproduction`
- learning-runtime-change-readiness-gate: `True:RuntimeChangeRulesSatisfied`
- service-foundation-freeze-gate: `True:ReadyForV45ExplicitScopedRuntimeExperimentPlanning`
- vector-formal-preview-freeze-gate: `True:ReadyForScopedOptInPreview`
- vector-guarded-formal-retrieval-preview-gate: `True:ReadyForShadowPackageComparison`
- vector-limited-formal-preview-observation-gate: `True:ReadyForFormalPreviewFreeze`
- vector-scoped-formal-preview-optin-gate: `True:ReadyForLimitedFormalPreviewObservation`
- vector-shadow-package-comparison-gate: `True:ReadyForScopedFormalPreviewOptIn`

## Observation Metrics
- FormalOutputChanged: `0`
- FormalPackageWritten: `False`
- LifecycleRiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- NonAllowlistedScopeLeakCount: `0`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RiskAfterPolicy: `0`
- RuntimeMutated: `False`

## Source Reports
- foundationReleaseCandidateGate: `foundation\foundation-release-candidate-gate.json`
- foundationReproducibilityCheck: `foundation\foundation-reproducibility-check.json`
- guardedFormalRetrievalPreviewGate: `vector\v4\vector-guarded-formal-retrieval-preview-gate.json`
- learningRuntimeChangeReadinessGate: `learning\readiness\learning-runtime-change-readiness-gate.json`
- limitedFormalPreviewObservationGate: `vector\v4\vector-limited-formal-preview-observation-gate.json`
- scopedFormalPreviewOptInGate: `vector\v4\vector-scoped-formal-preview-optin-gate.json`
- serviceFoundationFreezeGate: `service\service-foundation-freeze-gate.json`
- shadowPackageComparisonGate: `vector\v4\vector-shadow-package-comparison-gate.json`
- vectorFormalPreviewFreezeGate: `vector\v4\vector-formal-preview-freeze-gate.json`

## Runtime Boundary

- V4.5 只允许显式 scoped runtime experiment planning 和 dry-run。
- 不允许 runtime switch、正式 `IVectorIndexStore` 绑定、正式 package 写入、PackingPolicy 集成或 package output mutation。
- 非 allowlisted scope 必须保持 baseline；本报告只写 shadow planning artifact。
