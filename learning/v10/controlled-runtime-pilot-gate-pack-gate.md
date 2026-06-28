# Learning Controlled Runtime Pilot Gate Pack (Gate)

- ControlledRuntimePilotGatePackPassed: `True`
- GatePassed: `True`
- TotalCases: `28` PassedCases: `28` FailedCases: `0`
- ReadyCases: `1` BlockedCases: `27`

## Authority Invariants
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionApplied: `False` RequiresSeparatePromotionGate: `True` RequiresHumanApproval: `True`
- HumanReviewRequired: `True` AutoIngest: `False` HumanReviewCompleted: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False` ProductionDecisionChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

## Validation Summary
- V9PromotionReadinessValidated: `True`
- CandidatePromotionProposalValidated: `True`
- HumanReviewQueueValidated: `True`
- ControlledPilotDesignValidated: `True`
- OfflineReplayReady: `True`
- ShadowCanarySimulationReady: `True`
- RuntimePilotExecutionReady: `False`
- BlockedForRuntimePilotExecutionBy: `HumanReviewNotCompleted`

## Offline Replay
- Candidate `LogisticBaseline` pairwiseAccuracy=1.000
- Reference `WeightedBaseline` pairwiseAccuracy=0.862
- AccuracyDelta: `0.138` (positive=candidate stronger)

## Shadow Canary Simulation
- CanaryMode: `ScopedShadowSimulation` Scope: `demo-workspace/demo-collection`
- SimulatedQueryCount: `58` ShadowAgreementCount: `54` Rate: `0.931`
- KillSwitchArmed: `True` RollbackReady: `True`

- Recommendation: `ProceedToHumanReviewCompletionOrV10.3PilotExecutionGate` NextAllowedPhase: `V10.3PilotExecutionGate-pending-human-review`
