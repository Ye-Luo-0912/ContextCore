# Vector Runtime Retrieval Feature Derivation Preview

Generated: `2026-06-18T00:23:25.4428325+00:00`
OperationId: `runtime-feature-derivation-preview-342ca892ab1a43ae8151bc553b861389`

## Summary

- PreviewPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForRuntimeFeatureFreeze`
- AllowedMode: `PreviewOnly`
- RequiredNextPhase: `RuntimeFeatureDerivationFreeze`
- VectorProviderSource: `post-scoring-risk-gated-v1`
- GraphCandidateSource: `read-only relation evidence / expansion preview`
- TopK: `5`
- SeedTopK: `5`
- SampleCount: `120`
- MaxAllowedRecallRegression: `0.0000`
- MaxAllowedMrrRegression: `0.0000`
- ForbiddenSampleAnnotationReadCount: `0`
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

## Coverage (vs eval ground truth)
- TargetSectionMatchRate: `1.0000`
- RequiredRelationCoverageRate: `0.4633`
- EvidenceAnchorCoverageRate: `0.0000`
- SourceAnchorCoverageRate: `0.0000`
- DerivationCompletenessRate: `0.2574`

## Scoring Comparison
- baseline (dense-only)        recall=`0.5083` precision=`0.1017` mrr=`0.2275` belowTopK=`42`
- derived combined-repair       recall=`0.5083` precision=`0.1017` mrr=`0.2275` belowTopK=`41`
- eval-driven combined-repair   recall=`1.0000` precision=`0.2000` mrr=`0.9083` (V5.5 reference)
- delta (derived − baseline)    recall=`0.0000` mrr=`0.0000`
- baseline risk/mustNot/lifecycle/section: `0/0/0/0`
- derived  risk/mustNot/lifecycle/section: `0/0/0/0`

## Per-Sample Trace
- sampleId: `rdsv2-stress-train-sample-0001` confidence=`1.00`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `20/5/5/0`
  - coverage (target/relation/evidence/source overlap): `True/18/0/0`
  - missingDerivation: `1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0002` confidence=`1.00`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `6/5/5/0`
  - coverage (target/relation/evidence/source overlap): `True/2/0/0`
  - missingDerivation: `1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0003` confidence=`1.00`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `5/5/5/0`
  - coverage (target/relation/evidence/source overlap): `True/1/0/0`
  - missingDerivation: `1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0004` confidence=`1.00`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `5/5/5/0`
  - coverage (target/relation/evidence/source overlap): `True/1/0/0`
  - missingDerivation: `1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0005` confidence=`1.00`
  - envelope.targetSection: `historical_context`
  - envelope counts (relations/evidence/source/mustNot): `5/5/5/0`
  - coverage (target/relation/evidence/source overlap): `True/1/0/0`
  - missingDerivation: `1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`

## Source Scan
- scanPerformed: `True`
- scannedFileCount: `7`
- fixtureTokenHitCount: `0`

## Blocked Reasons
- (empty)

## Source Reports
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v55RepairGate: `vector\v5\retrieval-quality-repair-gate.json`
- v56ContractGate: `vector\v5\runtime-observable-feature-contract-gate.json`

V5.7 preview only. Runtime-derived features computed without reading eval ground-truth labels. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.
