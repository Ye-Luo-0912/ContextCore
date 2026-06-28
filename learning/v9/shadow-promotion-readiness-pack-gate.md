# Learning Shadow Promotion Readiness Pack (Gate)

- ShadowPromotionReadinessPackPassed: `True`
- GatePassed: `True`
- TotalCases: `25` PassedCases: `25` FailedCases: `0`
- ReadyCases: `1` BlockedCases: `24`

## Authority Invariants
- MLAuthority: `False` LLMAuthority: `False` RuntimeAuthority: `False` GateAuthority: `False`
- RuntimePromotionAllowed: `False` RequiresSeparatePromotionGate: `True` RequiresHumanApproval: `True`
- HumanReviewRequired: `True` AutoIngest: `False`
- RuntimeRerankerChanged: `False` RuntimeRouterChanged: `False`
- PackageOutputChanged: `False` FormalPackageWritten: `False` GlobalDefaultOn: `False`
- V8ScopedActivationPreserved: `True`

## Shadow Promotion Candidate Proposal
- BestShadowCandidate: `LogisticBaseline` (pairwiseAccuracy=1.000)
- CandidatePromotionProposalReady: `True`
- Eligibility:
  - pairwiseAccuracy=1.000 ≥ 0.95 threshold
  - Failure clusters analyzed and documented in V9.4 failure-diagnosis-input-pack
  - Hard-negative expansion candidates available (60 specs)
  - Feedback ingestion contract published (V9.5 schema)
- Risks:
  - LogisticBaseline 100% accuracy likely driven by positiveScore feature dominance; promote only after V9.4 hard-negative expansion + V10 canary observation
  - Test set is small (n=58); confidence intervals wide
  - No production traffic exposure yet — pilot must be scoped

## Router Promotion Readiness
- RouterPromotionReady: `False` RouterRepairRequired: `True`
- BestRouterBaseline: `RouterIntentLogistic` accuracy=0.121
- Blocking Reasons:
  - BestRouterAccuracy=0.121 below 0.85 promotion threshold
  - Router dataset has poor intent discriminability (most examples selected/accepted)
  - Router intent repair plan generated; see learning/v9/router-intent-repair-plan.json

## Human Review Queue
- Entries: `12` (path: `learning/v9/human-review-queue-plan.jsonl`)
- All entries HumanReviewRequired=true / AutoIngest=false

## Controlled Pilot Design
- PilotMode: `ShadowOnlyCanaryDesign` Scope: `demo-workspace/demo-collection`
- KillSwitchRequired: `True` RollbackRequired: `True` ManualPromotionRequired: `True`
- CanaryStages: `5` EntryCriteria: `5` AbortConditions: `5`
- ExpectedNextGate: `V10ControlledRuntimePilotGate`

- Recommendation: `ProceedToV10ControlledRuntimePilotGate` NextAllowedPhase: `V10ControlledRuntimePilotGate`
