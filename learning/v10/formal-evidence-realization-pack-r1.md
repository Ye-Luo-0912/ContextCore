# Learning Formal Evidence Realization R1 Pack

- FormalEvidenceRealizationR1PackPassed: `True`
- GatePassed: `False`
- TotalCases: `33` PassedCases: `33` FailedCases: `0`

## Canonical Hash Contract
- **HashInputVersion**: `v10.16R/canonical-v1`
- HashAlgorithm: `SHA-256`
- HashInputContract: `SHA256(sourceShadowLabelId | sourceCandidateSpecId | evidencePath | expectedPreference | rankingPairRowHash | shadowLabelHash)`
- **ContractHashAlgorithmCompliance**: `True`
- RankingPairRowHashCoverage: `1.000`
- ShadowLabelHashCoverage: `1.000`

## Integrity Manifest
- TotalEntries: `60` VerifiedEntries: `60` MismatchedEntries: `0`
- AnyHashMismatch: `False` EvidencePathUnresolvedCount: `0` ExpectedPreferenceUndeterivableCount: `0`

## Integrity Mutation Tests (REAL)
- TotalMutationTests: `6` DetectedMutations: `6`
- **IntegrityMutationTestsPassed**: `True`
- CorruptedHashDetected: `True`
- MissingEvidencePathDetected: `True`
- ExpectedPreferenceMismatchDetected: `True`
- CandidateMarkedFormalDetected: `True`
- RankingPairRowHashMismatchDetected: `True`
- StaleContractHashVersionDetected: `True`

## Terminology Compatibility Map
- MapVersion: `v10.16R/terminology-compatibility-v1`
  - `HumanReviewAsGateAuthority` -> `FeedbackSignalAsGateAuthority` (DeprecatedCompatibilityOnly)
  - `HumanFeedbackAutoIngest` -> `FeedbackSignalAutoIngest` (DeprecatedCompatibilityOnly)
  - `HumanFeedbackAsSignal` -> `ExternalFeedbackAcceptedAsSignal` (DeprecatedCompatibilityOnly)
  - `HumanReviewBacklog*` -> `ExternalFeedbackBacklog*` (DeprecatedCompatibilityOnly)

## Authority + Boundary Invariants
- ExternalFeedbackAcceptedAsSignal: `True` FeedbackSignalAsGateAuthority: `False` FeedbackSignalAutoIngest: `False`
- **MainRecommendationUsesHumanReview**: `False` (must be false)
- FormalLabelsRealized: `False` FormalEvidenceSufficient: `False`
- RuntimePilotExecutionReadyForSeparateGate: `False`
- FormalTrainingSetChanged: `False` AutoIngest: `False`
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionApplied: `False` RuntimePilotExecutionApplied: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

- Recommendation: `ProceedToControlledFormalLabelIngestionWithCanonicalHashV2` NextAllowedPhase: `ControlledFormalLabelIngestion-pending-canonical-hash-v2`
