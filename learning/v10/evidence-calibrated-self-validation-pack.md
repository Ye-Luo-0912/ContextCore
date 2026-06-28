# Learning Evidence-Calibrated Self-Validation Pack

- EvidenceCalibratedSelfValidationPackPassed: `True`
- GatePassed: `False`
- TotalCases: `28` PassedCases: `28` FailedCases: `0`

## Evidence Sufficiency
- EvidenceSufficiencyScore: `0.514` Threshold: `0.700`
- EvidenceSufficient: `False` SignalLeakageRisk: `True` AtLeastSignalLeakageSuspected: `True`
- HardNegativeEvidenceInsufficient: `True` RouterRiskHigh: `False`
- OfflineReplayStrength: `0.638`  ShadowCanaryAgreement: `0.931`
- FailureClusterCoverage: `1.000`  HardNegativeCandidateCoverage: `1.000`
- RegressionSafetyScore: `1.000`  RollbackSafetyScore: `1.000`
- RouterRiskPenalty: `0.000`  SignalLeakageRiskPenalty: `0.400`

## Disagreement Analysis
- TotalSimulatedQueries: `60` Agree: `56` Disagree: `4`
- AIArbitration: `False` Mode: `DeterministicFeatureBasedAnalysis`
  - candidate-better-evidence: count=1 rate=0.017
  - reference-better-evidence: count=1 rate=0.017
  - uncertain: count=1 rate=0.017
  - missing-evidence: count=1 rate=0.017
  - high-risk: count=0 rate=0.000

## Hard Negative Replay Readiness
- HardNegativeReplayReady: `True`  HardNegativeCoverageSufficient: `False`
- CandidateCount: `60` LabeledCount: `0` CoverageRate: `1.000`
- CandidatesAreLabeled: `False` AutoIngest: `False`

## Human Feedback Signal Policy
- HumanReviewAsGateAuthority: `False` HumanFeedbackAccepted: `True`
- HumanFeedbackAutoIngest: `False` HumanFeedbackRequiresEvidenceBinding: `True`
- HumanFeedbackUsedAsTrainingSignalOnly: `True` HumanReviewBacklogObserved: `True` (12 entries)

## Self-Validation Decision
- SelfValidationPassed: `True`  AIArbitration: `False`
- RuntimePilotExecutionReadyForSeparateGate: `False`
- BlockedForRuntimePilotExecutionBy: `EvidenceInsufficient, SignalLeakageRisk, HardNegativeEvidenceInsufficient`

## Authority Invariants
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionApplied: `False` RuntimePilotExecutionApplied: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- HumanReviewRequired: `False` HumanReviewAsGateAuthority: `False` AutoIngest: `False`
- V8ScopedActivationPreserved: `True`

- Recommendation: `BlockedForRuntimePilotExecution-EvidenceCalibrated-AccumulateMoreEvidence` NextAllowedPhase: `V10.7PilotExecution-pending-evidence`
