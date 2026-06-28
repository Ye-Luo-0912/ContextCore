# Learning Evidence Accumulation Pack (Gate)

- EvidenceAccumulationPackPassed: `True`
- GatePassed: `True`
- TotalCases: `27` PassedCases: `27` FailedCases: `0`

## Signal Leakage Ablation (REAL retrain)
- BaselineAccuracy: `1.000`
- AccuracyWithoutPositiveScore: `1.000` (drop=`0.000`)
- AccuracyWithoutScoreLikeFeatures: `1.000` (drop=`0.000`)
- **PositiveScoreDominanceDetected**: `False`
- **LeakageRiskReduced**: `True`

  - `all-features` train=195 eval=58 pairwiseAcc=1.000 included=[Recall3,Recall5,Recall10,MRR,PositiveRankInverseDelta,PositiveScoreMinusNegativeScore,PositiveSelectedMinusNegativeSelected,PackageHasAllConstraints]
  - `without-positiveScore` train=195 eval=58 pairwiseAcc=1.000 included=[Recall3,Recall5,Recall10,MRR,PositiveRankInverseDelta,PositiveSelectedMinusNegativeSelected,PackageHasAllConstraints]
  - `without-score-like` train=195 eval=58 pairwiseAcc=1.000 included=[Recall3,Recall5,Recall10,MRR,PositiveRankInverseDelta,PackageHasAllConstraints]
  - `only-structural` train=195 eval=58 pairwiseAcc=1.000 included=[PackageHasAllConstraints]
  - `only-recall-family` train=195 eval=58 pairwiseAcc=1.000 included=[Recall3,Recall5,Recall10]

## Hard-Negative Labeled Evidence Simulation
- SimulationMode: `ShadowSyntheticLabelDryRun`
- CandidateSpecCount: `60` SimulatedLabeledHardNegativeCount: `60`
- SyntheticLabelConfidence: `0.000`
- **HardNegativeEvidenceStillInsufficient**: `True` (synthetic labels do NOT count as real)
- EvidenceImprovedIfLabelsWereReal: `True`
- SyntheticLabelAuthority: `False` AutoIngest: `False` TrainingSetChanged: `False`

## Counterexample Replay
- CounterexampleCount: `127` ReplayReady: `True`
- CandidateFailureRate: `0.276` ReferenceFailureRate: `0.000`

## Evidence Sufficiency Recomputed
- PreviousScore: `0.514` → NewScore: `0.814` (threshold=`0.700`)
- **EvidenceSufficient**: `False` (Real labels: `False` / Synthetic-only what-if: `True`)
- **SignalLeakageStillSuspected**: `False` HardNegativeEvidenceStillInsufficient: `True`

## Self-Optimization Plan Update
- PlanVersion: `v10.7-self-optimization/v1`
- Resolved:
  - Signal leakage risk reduced (structural features alone score ≥0.85)
- Open:
  - Hard-negative real labels still missing (simulated=60, real=0)
  - EvidenceSufficiencyScore=0.814 below threshold 0.700
- RecommendedActions:
  - Drive 60 hard-negative candidate specs through V9.5 feedback ingestion: reviewers label ≥20 as confirmed negatives. After ingestion, re-run V10.7.
  - Augment hard-negative dataset with adversarial generation from failure clusters (within shadow scope only).
  - Do NOT promote LogisticBaseline to pilot execution. Repeat V10.3-V10.7 cycle after evidence accumulation.

## Authority Invariants
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- SyntheticLabelAuthority: `False` HumanReviewAsGateAuthority: `False` AutoIngest: `False` TrainingSetChanged: `False`
- RuntimePromotionApplied: `False` RuntimePilotExecutionApplied: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

- RuntimePilotExecutionReadyForSeparateGate: `False`
- BlockedForRuntimePilotExecutionBy: `EvidenceInsufficient, HardNegativeEvidenceStillInsufficient`
- Recommendation: `BlockedForRuntimePilotExecution-EvidenceCalibrated-AccumulateMoreEvidence` NextAllowedPhase: `V10.10PilotExecution-pending-evidence`
