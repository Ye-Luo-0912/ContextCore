# Vector Formal Adapter Package Shadow Comparison Gate

Generated: `2026-06-17T16:42:27.1312528+00:00`
OperationId: `formal-adapter-package-shadow-comparison-gate-5e571fadd66141fe89564c3668b8edc2`

## Summary

- ComparisonPassed: `True`
- GatePassed: `True`
- Recommendation: `ReadyForFormalAdapterPackageShadowFreeze`
- AllowedMode: `ShadowOnly`
- RequiredNextPhase: `FormalAdapterPackageShadowFreeze`
- VectorProviderSource: `post-scoring-risk-gated-v1`
- GraphCandidateSource: `read-only relation evidence / expansion preview`
- SampleCount: `120`
- TotalBaselinePackageItemCount: `600`
- TotalShadowPackageItemCount: `600`
- SelectedCount: `543`
- DroppedCount: `57`
- AddedCount: `57`
- SectionChangedCount: `0`
- OrderChangedCount: `23`
- PriorityChangedCount: `0`
- BaselineTokenTotal: `16615`
- ShadowTokenTotal: `16670`
- TokenDeltaTotal: `55`
- TokenDeltaMax: `10`
- TokenDeltaAbsoluteTotal: `55`
- TokenDeltaBudgetTotal: `4000`
- TokenDeltaBudgetPerSample: `200`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- TargetSectionViolationCount: `0`
- FormalOutputChanged: `0`
- FormalSelectedSetChanged: `False`
- FormalPackageWritten: `False`
- ShadowPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- NoRuntimeMutationInvariant: `True`

## Baseline Section Histogram
- normal_context: `510`
- historical_context: `90`

## Shadow Section Histogram
- normal_context: `510`
- historical_context: `90`

## Per-Sample Trace
- sampleId: `rdsv2-stress-train-sample-0001`
  - target: `normal_context`
  - counts (baseline/shadow/selected/dropped/added): `5/5/5/0/0`
  - section/order/priority changed: `0/0/0`
  - tokens (baseline/shadow/delta): `145/145/0`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - shadow ids: `rdsv2-stress-holdout-item-0101, rdsv2-stress-holdout-item-0111, rdsv2-stress-train-item-0001, rdsv2-stress-train-item-0011, rdsv2-stress-train-item-0021`
  - baseline ids: `rdsv2-stress-holdout-item-0101, rdsv2-stress-holdout-item-0111, rdsv2-stress-train-item-0001, rdsv2-stress-train-item-0011, rdsv2-stress-train-item-0021`
- sampleId: `rdsv2-stress-train-sample-0002`
  - target: `normal_context`
  - counts (baseline/shadow/selected/dropped/added): `5/5/5/0/0`
  - section/order/priority changed: `0/0/0`
  - tokens (baseline/shadow/delta): `140/140/0`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - shadow ids: `rdsv2-stress-holdout-item-0102, rdsv2-stress-holdout-item-0112, rdsv2-stress-train-item-0002, rdsv2-stress-train-item-0012, rdsv2-stress-train-item-0022`
  - baseline ids: `rdsv2-stress-holdout-item-0102, rdsv2-stress-holdout-item-0112, rdsv2-stress-train-item-0002, rdsv2-stress-train-item-0012, rdsv2-stress-train-item-0022`
- sampleId: `rdsv2-stress-train-sample-0003`
  - target: `normal_context`
  - counts (baseline/shadow/selected/dropped/added): `5/5/5/0/0`
  - section/order/priority changed: `0/0/0`
  - tokens (baseline/shadow/delta): `140/140/0`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - shadow ids: `rdsv2-stress-holdout-item-0103, rdsv2-stress-holdout-item-0113, rdsv2-stress-train-item-0003, rdsv2-stress-train-item-0013, rdsv2-stress-train-item-0023`
  - baseline ids: `rdsv2-stress-holdout-item-0103, rdsv2-stress-holdout-item-0113, rdsv2-stress-train-item-0003, rdsv2-stress-train-item-0013, rdsv2-stress-train-item-0023`
- sampleId: `rdsv2-stress-train-sample-0004`
  - target: `normal_context`
  - counts (baseline/shadow/selected/dropped/added): `5/5/5/0/0`
  - section/order/priority changed: `0/0/0`
  - tokens (baseline/shadow/delta): `140/140/0`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - shadow ids: `rdsv2-stress-holdout-item-0104, rdsv2-stress-holdout-item-0114, rdsv2-stress-train-item-0004, rdsv2-stress-train-item-0014, rdsv2-stress-train-item-0024`
  - baseline ids: `rdsv2-stress-holdout-item-0104, rdsv2-stress-holdout-item-0114, rdsv2-stress-train-item-0004, rdsv2-stress-train-item-0014, rdsv2-stress-train-item-0024`
- sampleId: `rdsv2-stress-train-sample-0005`
  - target: `historical_context`
  - counts (baseline/shadow/selected/dropped/added): `5/5/5/0/0`
  - section/order/priority changed: `0/0/0`
  - tokens (baseline/shadow/delta): `135/135/0`
  - risk/mustNot/lifecycle/targetSection: `0/0/0/0`
  - shadow ids: `rdsv2-stress-holdout-item-0105, rdsv2-stress-holdout-item-0115, rdsv2-stress-train-item-0005, rdsv2-stress-train-item-0015, rdsv2-stress-train-item-0025`
  - baseline ids: `rdsv2-stress-holdout-item-0105, rdsv2-stress-holdout-item-0115, rdsv2-stress-train-item-0005, rdsv2-stress-train-item-0015, rdsv2-stress-train-item-0025`

## Blocked Reasons
- (empty)

## Source Reports
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v52AdapterGate: `vector\v5\shadow-formal-retrieval-adapter-gate.json`

V5.3 shadow only. No formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.
