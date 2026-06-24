# Vector V4 Readiness Recheck

Generated: `2026-06-16T01:56:45.2276027+00:00`
OperationId: `vector-v4-readiness-recheck-4ce26926c4904977b51fee8f205200ce`

## Summary

- RecheckPassed: `True`
- Recommendation: `ReadyForGuardedFormalPreview`
- LegacyVectorStatus: `PreviewOnly / legacy limitations recorded`
- DatasetV2SmallStatus: `ReadyForDatasetV2RetrievalCandidate`
- DatasetV2StressStatus: `ReadyForV4RecheckInput`
- PgVectorProviderStatus: `ReadyForPreviewShadowStorage`
- Qwen3ProviderComparisonStatus: `Conclusive`
- HybridRetrievalStatus: `KeepPreviewOnly`
- HybridScoringRepairStatus: `ReadyForDatasetV2StressFreeze`
- RuntimeChangeGateStatus: `Passed`
- BestPreviewProfile: `post-scoring-risk-gated-v1`
- StressRecall: `50.83%`
- HoldoutRecall: `75.00%`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- LeakageIssueCount: `0`
- AnchorDominanceScore: `0.0000`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- ReadyForGuardedFormalPreview: `True`
- ReadyForRuntimeSwitch: `False`

## Runtime Boundary

- V4.R 通过不等于 runtime switch。
- 本阶段只允许进入 GuardedFormalPreview。
- `ReadyForRuntimeSwitch` 恒为 `false`。
- `FormalRetrievalAllowed` 在 V4.R 仍为 `false`。
- 未通过后续 formal readiness gate 前，不允许绑定正式 `IVectorIndexStore`，也不允许改变 PackingPolicy / package output。

## BlockedReasons
- (empty)

## Source Reports
- datasetV2MaterializationGate: `vector\dataset-v2\generated\materialization-gate.json`
- datasetV2SmallReadinessGate: `vector\dataset-v2\eval\dataset-v2-readiness-gate.json`
- datasetV2StressFreezeGate: `vector\dataset-v2\stress\stress-freeze-gate.json`
- hybridRetrievalFreeze: `vector\hybrid\vector-hybrid-freeze-gate.json`
- hybridScoringRepairGate: `vector\dataset-v2\stress\hybrid-scoring-repair-gate.json`
- hybridScoringRiskTriage: `vector\dataset-v2\stress\hybrid-scoring-risk-triage.json`
- legacyDatasetLimitationReport: `vector\eligibility\vector-legacy-dataset-limitation-report.json`
- legacyVectorReadinessGate: `eval\vector-retrieval-shadow-readiness-gate.json`
- pgVectorProviderFreezeGate: `storage\postgres\postgres-vector-freeze-gate.json`
- qwen3ProviderComparisonFreeze: `vector\providers\qwen3\vector-provider-comparison-freeze.json`
- runtimeChangeGate: `learning\readiness\learning-runtime-change-readiness-gate.json`
