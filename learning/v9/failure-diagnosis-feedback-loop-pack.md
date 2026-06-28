# Learning Failure Diagnosis + Feedback Loop Pack

- FailureDiagnosisFeedbackLoopPackPassed: `True`
- GatePassed: `False`
- TotalCases: `21` PassedCases: `21` FailedCases: `0`
- ReadyCases: `1` BlockedCases: `20`

## Authority Invariants
- LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- HumanReviewRequired: `True` AutoIngest: `False`
- V8ScopedActivationPreserved: `True`

## Outputs
- FailureDiagnosisInputPackReady: `True` (clusters=3)
- HardNegativeExpansionReady: `True` (count=60)
- RouterIntentRepairPlanReady: `True` (underrep=3 confusing=8)
- FeedbackIngestionContractReady: `True` (fields=9)
- PackageQualityReadinessPlanReady: `True`
- MemoryPromotionReadinessPlanReady: `True`
- ConstraintGapReadinessPlanReady: `True`

## Failure Clusters
- `ranker-WeightedBaseline` (CandidateReranker/WeightedBaseline) — 4 failures: Hand-tuned weights miss feature interactions; consider feature engineering or move to logistic/tree.
- `router-RouterIntentLogistic` (RouterIntentClassifier/RouterIntentLogistic) — 10 failures: Router dataset has poor intent discriminability; most examples are selected/accepted. Need negative + diverse examples per intent.
- `router-RouterIntentTree` (RouterIntentClassifier/RouterIntentTree) — 10 failures: Router dataset has poor intent discriminability; most examples are selected/accepted. Need negative + diverse examples per intent.

## Future Task Family Readiness
- `PackageQuality` — NotReadyWithPlan: policy-feedback-features dataset is empty; no signal to train PackageQuality shadow scorer.
- `MemoryPromotion` — NotReadyWithPlan: No dedicated memory-shadow lifecycle dataset yet; memory promotion features (recency, semantic anchor, lifecycle stage) need to be exported.
- `ConstraintGap` — NotReadyWithPlan: policy-feedback-features dataset lacks structured missing-constraint / missing-entity / missing-uncertainty annotations.

- Recommendation: `ProceedToV9.7ShadowPromotionReadiness` NextAllowedPhase: `V9.7ShadowPromotionReadiness`
