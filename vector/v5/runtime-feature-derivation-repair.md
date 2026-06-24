# Vector Runtime Retrieval Feature Derivation Repair

Generated: `2026-06-18T06:50:14.4297197+00:00`
OperationId: `runtime-feature-derivation-repair-2ae337d629bd47d4bfc80a60dda92a7a`

## Summary

- PreviewPassed: `False`
- GatePassed: `False`
- Recommendation: `BlockedByDerivedRecallNotImproved`
- AllowedMode: `PreviewOnly`
- RequiredNextPhase: `RuntimeFeatureDerivationRepairFreeze`
- VectorProviderSource: `post-scoring-risk-gated-v1`
- GraphCandidateSource: `read-only relation evidence / expansion preview`
- TopK: `5`  DenseSeedTopK: `8`  AnchorSeedTopK: `12`  RelationTopK: `20`
- SampleCount: `120` (train=`96` / holdout=`24`)
- ForbiddenSampleAnnotationReadCount: `0`
- MinRelationCoverageRate: `0.5500`
- MaxAllowedHoldoutRecallRegression: `0.0000`
- MaxAllowedHoldoutMrrRegression: `0.0000`
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

## Coverage (canonical anchors)
- TargetSectionMatchRate: `0.9833`
- CanonicalRequiredRelationCoverageRate: `0.1333` (applicable=120, covered=16)
- CanonicalEvidenceAnchorCoverageRate: `0.4750` (applicable=120, covered=57)
- CanonicalSourceAnchorCoverageRate: `0.4750` (applicable=120, covered=57)

## Train Scoring
- baseline recall/mrr: `0.5104/0.2299`
- derived  recall/mrr: `0.4792/0.1998`
- delta R/MRR: `-0.0312/-0.0300`

## Holdout Scoring
- baseline recall/mrr: `0.5000/0.2181`
- derived  recall/mrr: `0.4583/0.2076`
- delta R/MRR: `-0.0417/-0.0104`

## Risk
- derived risk/mustNot/lifecycle/section: `0/0/0/0`

## Derivation Diagnostics
- `split: train=96 holdout=24 (modulus=5, holdout-remainder=0)`
- `train recall delta: -0.0312`
- `train mrr delta: -0.0300`
- `holdout recall delta: -0.0417`
- `holdout mrr delta: -0.0104`
- `canonical relation coverage: 0.1333 (min required: 0.5500)`
- `canonical evidence coverage: 0.4750`
- `canonical source coverage: 0.4750`

## Per-Sample Trace
- sampleId: `rdsv2-stress-train-sample-0001` split=`holdout` confidence=`0.40`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `2/8/8/0`
  - canonical overlap (relation/evidence/source): `0/1/1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0002` split=`train` confidence=`0.40`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `2/8/8/0`
  - canonical overlap (relation/evidence/source): `0/1/1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0003` split=`train` confidence=`0.60`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `2/12/12/0`
  - canonical overlap (relation/evidence/source): `0/1/1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0004` split=`train` confidence=`0.60`
  - envelope.targetSection: `normal_context`
  - envelope counts (relations/evidence/source/mustNot): `2/12/12/0`
  - canonical overlap (relation/evidence/source): `0/1/1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`
- sampleId: `rdsv2-stress-train-sample-0005` split=`train` confidence=`0.70`
  - envelope.targetSection: `historical_context`
  - envelope counts (relations/evidence/source/mustNot): `2/14/14/0`
  - canonical overlap (relation/evidence/source): `0/1/1`
  - mustHit (count/recalled/below): `1/1/0`
  - recall/precision/mrr: `1.0000/0.2000/0.3333`
  - risk/mustNot/lifecycle/section: `0/0/0/0`

## Source Scan
- scanPerformed: `True`
- scannedFileCount: `10`
- fixtureTokenHitCount: `0`

## Blocked Reasons
- `DerivedMrrNotImproved`
- `DerivedRecallNotImproved`
- `HoldoutMrrRegression`
- `HoldoutRecallRegression`
- `LowRelationCoverage`

## Source Reports
- stressCorpus: `vector\dataset-v2\stress\corpus.jsonl`
- stressSamples: `vector\dataset-v2\stress\samples.jsonl`
- v57DerivationGate: `vector\v5\runtime-feature-derivation-gate.json`

V5.8 preview only. Repair derivation uses canonical anchor resolution and runtime relation intent. No formal retrieval, formal package write, formal selected set change, package output mutation, packing policy mutation, runtime switch, or vector store binding change.
