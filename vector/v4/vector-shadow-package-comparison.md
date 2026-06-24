# Vector Shadow Package Comparison

Generated: `2026-06-16T00:25:44.7946536+00:00`
OperationId: `vector-shadow-package-comparison-9af6ad6796db4ddbb33ff3a0299d4b11`

## Summary

- ComparisonPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForScopedFormalPreviewOptIn`
- ProfileName: `post-scoring-risk-gated-v1`
- SampleCount: `120`
- QueryCount: `120`
- BaselinePackageCount: `120`
- ShadowPackageCount: `120`
- CandidateAddCount: `57`
- CandidateRemoveCount: `57`
- CandidateUnchangedCount: `543`
- SectionChangedCount: `0`
- TokenDeltaTotal: `55`
- TokenDeltaMax: `10`
- ConstraintCoverageDelta: `0.0167`
- RelationCoverageDelta: `0.0569`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- ShadowPackageWritten: `False`
- RuntimeMutated: `False`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## BlockedReasons
- (empty)

## Runtime Boundary

- Shadow package 只写离线报告，不写正式 package artifact。
- `PackingPolicyChanged=false` 与 `PackageOutputChanged=false` 是本阶段 gate 条件。
- `UseForRuntime`、`FormalRetrievalAllowed`、`ReadyForRuntimeSwitch` 均保持 `false`。

## Source Reports
- guardedFormalRetrievalPreviewGate: `vector\v4\vector-guarded-formal-retrieval-preview-gate.json`
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
