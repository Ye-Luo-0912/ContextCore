# Learning Counterexample Repair Pack (Gate)

- CounterexampleRepairPackPassed: `True`
- GatePassed: `True`
- TotalCases: `27` PassedCases: `27` FailedCases: `0`

## Evidence-Bound Hard-Negative Realization
- EvidenceBoundShadowLabelCount: `60`
- UnboundCandidateSpecCount: `0`
- BindingCoverageRate: `1.000`
- EvidenceBoundShadowLabelsAreFormal: `False`

## Counterexample Repair
- TotalCounterexamples: `127`
- CandidateFailureCount: `35`
- ReferenceFailureCount: `0`
- TopCases: `10`

## Repaired Shadow Scoring Proposal
- ProposalMode: `ShadowProposalOnly`
- AddedFeatures: Recall3 as tiebreaker, MRR as secondary tiebreaker
- RuntimeRerankerChanged: `False` RuntimePilotReady: `False`

## Counterexample Replay After Repair
- CounterexampleCount: `127`
- OriginalCandidateFailureRate: `0.276`
- RepairedCandidateFailureRate: `0.000`
- ReferenceFailureRate: `0.000`
- RepairImprovement: `0.276`
- RepairedCandidateMatchesOrBeatsReference: `True`

## Evidence Sufficiency Recomputed V2
- PreviousScore: `0.814` → NewScoreV2: `1.000` (threshold=`0.700`)
- EvidenceSufficient: `True` EvidenceBoundShadowLabelsAreFormal: `False`

## Authority Invariants
- ShadowOnly: `True` SyntheticLabelAuthority: `False` AutoIngest: `False` TrainingSetChanged: `False`
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionApplied: `False` RuntimePilotExecutionApplied: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

- RuntimePilotExecutionReadyForSeparateGate: `True`
- Recommendation: `ProceedToV10.13PilotExecutionGate-pending-formal-labels` NextAllowedPhase: `V10.13PilotExecutionGate-pending-formal-labels`
