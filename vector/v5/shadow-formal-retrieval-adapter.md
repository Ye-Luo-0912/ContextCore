# Vector Shadow Formal Retrieval Adapter

Generated: `2026-06-17T15:30:37.4203540+00:00`
OperationId: `shadow-formal-retrieval-adapter-41fdd96ef81d403497acab2c0ddcfa66`

## Summary

- AdapterPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForShadowAdapterFreeze`
- AllowedMode: `ShadowOnly`
- RequiredNextPhase: `ShadowFormalRetrievalAdapterFreeze`
- VectorProviderSource: `post-scoring-risk-gated-v1`
- GraphCandidateSource: `read-only relation evidence / expansion preview`
- SampleCount: `120`
- TotalBaselineCandidateCount: `600`
- TotalShadowVectorCandidateCount: `600`
- TotalShadowGraphCandidateCount: `235`
- TotalMergedShadowCandidateCount: `751`
- TotalFilteredCandidateCount: `751`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- TargetSectionViolationCount: `0`
- FormalOutputChanged: `0`
- FormalSelectedSetChanged: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalPackageWritten: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- NoRuntimeMutationInvariant: `True`

## Adapter Inputs
- `query`
- `workspaceId`
- `collectionId`
- `package context`
- `baseline candidates`

## Adapter Outputs
- `shadow vector candidates`
- `shadow graph candidates`
- `merged shadow candidates`
- `filtered candidates`
- `trace/explain`

## Gate Order
- `provider scope isolation`
- `candidate eligibility`
- `lifecycle projection`
- `risk projection`
- `must-not risk gate`
- `post-scoring risk gate`
- `formal output/package invariant gate`

## Per-Sample Trace
- sampleId: `rdsv2-stress-train-sample-0001`
  - target: `normal_context`
  - counts (baseline/vector/graph/merged/filtered): `5/5/5/8/8`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0101, rdsv2-stress-holdout-item-0111, rdsv2-stress-train-item-0001, rdsv2-stress-train-item-0011, rdsv2-stress-train-item-0021`
  - filtered ids: `rdsv2-stress-holdout-item-0101, rdsv2-stress-holdout-item-0111, rdsv2-stress-train-item-0001, rdsv2-stress-train-item-0011, rdsv2-stress-train-item-0021`
- sampleId: `rdsv2-stress-train-sample-0002`
  - target: `normal_context`
  - counts (baseline/vector/graph/merged/filtered): `5/5/2/6/6`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0102, rdsv2-stress-holdout-item-0112, rdsv2-stress-train-item-0002, rdsv2-stress-train-item-0012, rdsv2-stress-train-item-0022`
  - filtered ids: `rdsv2-stress-holdout-item-0102, rdsv2-stress-holdout-item-0112, rdsv2-stress-train-item-0002, rdsv2-stress-train-item-0012, rdsv2-stress-train-item-0022`
- sampleId: `rdsv2-stress-train-sample-0003`
  - target: `normal_context`
  - counts (baseline/vector/graph/merged/filtered): `5/5/2/6/6`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0103, rdsv2-stress-holdout-item-0113, rdsv2-stress-train-item-0003, rdsv2-stress-train-item-0013, rdsv2-stress-train-item-0023`
  - filtered ids: `rdsv2-stress-holdout-item-0103, rdsv2-stress-holdout-item-0113, rdsv2-stress-train-item-0003, rdsv2-stress-train-item-0013, rdsv2-stress-train-item-0023`
- sampleId: `rdsv2-stress-train-sample-0004`
  - target: `normal_context`
  - counts (baseline/vector/graph/merged/filtered): `5/5/2/6/6`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0104, rdsv2-stress-holdout-item-0114, rdsv2-stress-train-item-0004, rdsv2-stress-train-item-0014, rdsv2-stress-train-item-0024`
  - filtered ids: `rdsv2-stress-holdout-item-0104, rdsv2-stress-holdout-item-0114, rdsv2-stress-train-item-0004, rdsv2-stress-train-item-0014, rdsv2-stress-train-item-0024`
- sampleId: `rdsv2-stress-train-sample-0005`
  - target: `historical_context`
  - counts (baseline/vector/graph/merged/filtered): `5/5/1/5/5`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0105, rdsv2-stress-holdout-item-0115, rdsv2-stress-train-item-0005, rdsv2-stress-train-item-0015, rdsv2-stress-train-item-0025`
  - filtered ids: `rdsv2-stress-holdout-item-0105, rdsv2-stress-holdout-item-0115, rdsv2-stress-train-item-0005, rdsv2-stress-train-item-0015, rdsv2-stress-train-item-0025`

## Blocked Reasons
- (empty)

## Source Reports
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v51PlanGate: `vector\v5\shadow-formal-retrieval-adapter-plan-gate.json`

V5.2 shadow only. No formal IVectorIndexStore binding, runtime switch, formal package write, PackingPolicy mutation, or package output mutation.
