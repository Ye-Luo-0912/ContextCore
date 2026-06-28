# Learning Layer Bootstrap

- LearningLayerBootstrapPassed: `True`
- GatePassed: `False`
- TotalCases: `19` PassedCases: `19` FailedCases: `0`
- ReadyCases: `1` BlockedCases: `18`
- ShadowOnly: `True` RuntimeAuthority: `False` GateAuthority: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`
- MainlineEvidencePresent: `False` MainlineTrustRegistryPresent: `False`
- Recommendation: `ProceedToV9BaselineImplementation` NextAllowedPhase: `V9.1BaselineImplementation`

## Dataset Inventory
- RankingPairCount: `253`
- RouterIntentExampleCount: `163`
- PolicyFeedbackFeatureCount: `0`
- HardNegativeCount: `18`
- UsableTaskFamilies: CandidateReranker, RouterIntentClassifier
- NotReadyTaskFamilies: ConstraintGap, MemoryPromotion, PackageQuality
- KnownRisks: EvalOnlyDataset, MissingNegativeSamples, NoPolicyFeedback

## Feature Contract
- Entry count: `31` Task families: CandidateReranker, ConstraintGap, MemoryPromotion, PackageQuality, RouterIntentClassifier
- AllEntriesShadowOnly: `True` AnyEntryRuntimeAuthority: `False` AnyEntryGateAuthority: `False`

## Baseline Plan
- Entry count: `12`
- AllEntriesShadowOnly: `True` AnyEntryRuntimeAuthority: `False` AnyEntryGateAuthority: `False`
  - `WeightedBaseline` (CandidateReranker / V9.1) — ShadowOnly=True
  - `LogisticBaseline` (CandidateReranker / V9.1) — ShadowOnly=True
  - `GBDTBaseline` (CandidateReranker / V9.2) — ShadowOnly=True
  - `LightweightMLPShadowCandidate` (CandidateReranker / V9.3) — ShadowOnly=True
  - `RouterIntentLogistic` (RouterIntentClassifier / V9.1) — ShadowOnly=True
  - `RouterIntentGBDT` (RouterIntentClassifier / V9.2) — ShadowOnly=True
  - `LLMAssistedFailureDiagnosis` (CandidateReranker / V9.4) — ShadowOnly=True
  - `HardNegativeGeneration` (CandidateReranker / V9.4) — ShadowOnly=True
  - `HumanReviewFeedbackIngestion` (PackageQuality / V9.5) — ShadowOnly=True
  - `PackageQualityShadowScorer` (PackageQuality / V9.5) — ShadowOnly=True
  - `MemoryPromotionShadowScorer` (MemoryPromotion / V9.6) — ShadowOnly=True
  - `ConstraintGapShadowDetector` (ConstraintGap / V9.6) — ShadowOnly=True
