# Vector Guarded Formal Retrieval Preview

Generated: `2026-06-15T18:37:03.7943835+00:00`
OperationId: `vector-guarded-formal-retrieval-preview-fc4135a93bb8413c8a42e0bd0074e8b2`

## Summary

- PreviewPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForShadowPackageComparison`
- ProfileName: `post-scoring-risk-gated-v1`
- V4RecheckPassed: `True`
- SampleCount: `120`
- QueryCount: `120`
- BaselineCandidateCount: `600`
- PreviewVectorCandidateCount: `600`
- WouldAddCount: `57`
- WouldRemoveCount: `57`
- WouldRerankCount: `0`
- WouldChangeTargetSectionCount: `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`

## BlockedReasons
- (empty)

## Runtime Boundary

- 本报告只输出 guarded formal retrieval preview 的 would-change 统计。
- 不写正式 package，不改变 PackingPolicy 输入，不改变最终 package output。
- `UseForRuntime`、`FormalRetrievalAllowed`、`ReadyForRuntimeSwitch` 均保持 `false`。

## Source Reports
- datasetV2StressFreezeGate: `vector\dataset-v2\stress\stress-freeze-gate.json`
- hybridScoringRepairGate: `vector\dataset-v2\stress\hybrid-scoring-repair-gate.json`
- hybridScoringRiskTriage: `vector\dataset-v2\stress\hybrid-scoring-risk-triage.json`
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v4ReadinessRecheck: `vector\v4\vector-v4-readiness-recheck.json`
