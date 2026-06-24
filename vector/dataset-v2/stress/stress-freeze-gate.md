# Retrieval Dataset V2 Stress Freeze Gate

Generated: `2026-06-15T18:15:42.0816463+00:00`
OperationId: `retrieval-dataset-v2-stress-freeze-50e2ce1044ad49668d2318e6c5fc3f9a`

## Summary

- FreezePassed: `True`
- DatasetV2Stress: `ReadyForV4RecheckInput`
- Recommendation: `ReadyForV4RecheckInput`
- DatasetId: `rdsv2-stress-a9f2c86e8d1df488`
- BestPreviewProfile: `post-scoring-risk-gated-v1`
- StressRecall: `50.83%`
- HoldoutRecall: `75.00%`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- LeakageIssueCount: `0`
- AnchorDominanceScore: `0.0000`
- V4RecheckAllowed: `True`
- ReadyForFormalRetrieval: `False`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`

## Preconditions

- MaterializationGatePassed: `True`
- SmallSetReadinessGatePassed: `True`
- StressReadinessRecommendation: `BlockedByHoldoutRecall`
- StressFailureTriageCompleted: `True`
- HybridScoringRepairGatePassed: `True`
- HybridScoringRiskCandidateCount: `0`

## Runtime Boundary

- `ReadyForV4RecheckInput` 只表示可作为 V4 复核输入。
- `ReadyForFormalRetrieval` 保持 `false`。
- `post-scoring-risk-gated-v1` 不得直接接入 runtime。
- 未通过 V4 formal readiness gate 前不得改变 PackingPolicy / package output。

## Blocked Reasons
- (empty)

## Source Reports
- anchorDominanceAudit: `vector\dataset-v2\stress\anchor-dominance-audit.json`
- hybridScoringRepairGate: `vector\dataset-v2\stress\hybrid-scoring-repair-gate.json`
- hybridScoringRiskTriage: `vector\dataset-v2\stress\hybrid-scoring-risk-triage.json`
- leakageAudit: `vector\dataset-v2\stress\leakage-audit.json`
- materializationGate: `vector\dataset-v2\generated\materialization-gate.json`
- smallSetReadinessGate: `vector\dataset-v2\eval\dataset-v2-readiness-gate.json`
- stressFailureTriage: `vector\dataset-v2\stress\stress-failure-triage.json`
- stressReadinessGate: `vector\dataset-v2\stress\stress-readiness-gate.json`
