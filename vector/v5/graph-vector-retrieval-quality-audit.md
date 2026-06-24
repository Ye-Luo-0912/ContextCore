# Vector Graph Retrieval Quality Audit

Generated: `2026-06-17T16:43:16.1370736+00:00`
OperationId: `graph-vector-retrieval-quality-audit-97e2b68842a1442c97c1a1fb4c689476`

## Summary

- AuditPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForRetrievalQualityFreeze`
- AllowedMode: `AuditOnly`
- RequiredNextPhase: `RetrievalQualityFreeze`
- VectorProviderSource: `post-scoring-risk-gated-v1`
- GraphCandidateSource: `read-only relation evidence / expansion preview`
- SampleCount: `120`
- MustHitTotal: `120`
- MustHitRecalledTotal: `61`
- Recall: `0.5083`
- Precision: `0.1017`
- MeanReciprocalRank: `0.2275`
- TopK: `5`
- VectorContributionCount: `600`
- GraphContributionCount: `235`
- OverlapCount: `81`
- VectorOnlyCount: `519`
- GraphOnlyCount: `154`
- GraphNoiseCount: `0`
- VectorNoiseCount: `0`
- RankingRegressionCount: `0`
- MustHitBelowTopKCount: `59`
- GraphNoiseThreshold: `0`
- RankingRegressionThreshold: `0`
- MustHitBelowTopKThreshold: `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- SectionMismatchCount: `0`
- MetadataEvidenceGapCount: `0`
- FormalOutputChanged: `0`
- FormalSelectedSetChanged: `False`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- NoRuntimeMutationInvariant: `True`

## Failure Clusters
- clusterId: `MissingCandidate` count: `59`
  - description: MustHit item missing from merged top-K.
  - samples: `rdsv2-stress-test-sample-0009, rdsv2-stress-test-sample-0010, rdsv2-stress-dev-sample-0017, rdsv2-stress-dev-sample-0018, rdsv2-stress-train-sample-0021`
  - items: `rdsv2-stress-test-item-0049, rdsv2-stress-test-item-0050, rdsv2-stress-dev-item-0087, rdsv2-stress-dev-item-0088, rdsv2-stress-train-item-0033`
- clusterId: `RankingTooLow` count: `59`
  - description: MustHit recalled but ranked below TopK or dense baseline rank.
  - samples: `rdsv2-stress-test-sample-0009, rdsv2-stress-test-sample-0010, rdsv2-stress-dev-sample-0017, rdsv2-stress-dev-sample-0018, rdsv2-stress-train-sample-0021`
  - items: `rdsv2-stress-test-item-0049, rdsv2-stress-test-item-0050, rdsv2-stress-dev-item-0087, rdsv2-stress-dev-item-0088, rdsv2-stress-train-item-0033`

## Per-Sample Trace
- sampleId: `rdsv2-stress-train-sample-0001`
  - target: `normal_context`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - source counts (vector/graph/merged/overlap/vectorOnly/graphOnly): `5/5/8/1/4/4`
  - graphNoise/vectorNoise/rankingRegression: `0/0/0`
  - risk/mustNot/lifecycle/sectionMismatch/metadataGap: `0/0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0101, rdsv2-stress-holdout-item-0111, rdsv2-stress-train-item-0001, rdsv2-stress-train-item-0011, rdsv2-stress-train-item-0021`
- sampleId: `rdsv2-stress-train-sample-0002`
  - target: `normal_context`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - source counts (vector/graph/merged/overlap/vectorOnly/graphOnly): `5/2/6/1/4/1`
  - graphNoise/vectorNoise/rankingRegression: `0/0/0`
  - risk/mustNot/lifecycle/sectionMismatch/metadataGap: `0/0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0102, rdsv2-stress-holdout-item-0112, rdsv2-stress-train-item-0002, rdsv2-stress-train-item-0012, rdsv2-stress-train-item-0022`
- sampleId: `rdsv2-stress-train-sample-0003`
  - target: `normal_context`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - source counts (vector/graph/merged/overlap/vectorOnly/graphOnly): `5/2/6/1/4/1`
  - graphNoise/vectorNoise/rankingRegression: `0/0/0`
  - risk/mustNot/lifecycle/sectionMismatch/metadataGap: `0/0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0103, rdsv2-stress-holdout-item-0113, rdsv2-stress-train-item-0003, rdsv2-stress-train-item-0013, rdsv2-stress-train-item-0023`
- sampleId: `rdsv2-stress-train-sample-0004`
  - target: `normal_context`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - source counts (vector/graph/merged/overlap/vectorOnly/graphOnly): `5/2/6/1/4/1`
  - graphNoise/vectorNoise/rankingRegression: `0/0/0`
  - risk/mustNot/lifecycle/sectionMismatch/metadataGap: `0/0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0104, rdsv2-stress-holdout-item-0114, rdsv2-stress-train-item-0004, rdsv2-stress-train-item-0014, rdsv2-stress-train-item-0024`
- sampleId: `rdsv2-stress-train-sample-0005`
  - target: `historical_context`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - source counts (vector/graph/merged/overlap/vectorOnly/graphOnly): `5/1/5/1/4/0`
  - graphNoise/vectorNoise/rankingRegression: `0/0/0`
  - risk/mustNot/lifecycle/sectionMismatch/metadataGap: `0/0/0/0/0`
  - merged ids: `rdsv2-stress-holdout-item-0105, rdsv2-stress-holdout-item-0115, rdsv2-stress-train-item-0005, rdsv2-stress-train-item-0015, rdsv2-stress-train-item-0025`

## Blocked Reasons
- (empty)

## Source Reports
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v52AdapterGate: `vector\v5\shadow-formal-retrieval-adapter-gate.json`
- v53PackageShadowGate: `vector\v5\formal-adapter-package-shadow-comparison-gate.json`

V5.4 audit only. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.
