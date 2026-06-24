# Controlled Shadow Merge Freeze

Generated: `2026-06-22T15:35:50.9915327+00:00`
OperationId: `controlled-shadow-merge-freeze-ddb2eeedb44e4b2581252dd61fc97031`

## Summary
- FreezePassed: `True`
- PromotionDecisionPassed: `False`
- Recommendation: `ReadyForControlledShadowMergePromotionDecision`
- PromotionDecision: `ReadyForControlledAppliedMergeProposal`
- NextAllowedPhase: `ControlledAppliedMergeProposal`
- AllowedMode: `ControlledShadowMergeFreezeOnly`
- ObservationWindowGatePassed: `True`
- RuntimeChangeGatePassed: `True`
- ProposalId: `csm-aa75535bb5694dca`
- ObservationRunCount/MinObservationRunCount: `10` / `10`
- RequestCountTotal/MaxRequestCount: `120` / `120`
- SampleObservationCount/MinSampleObservationCount: `120` / `120`
- ProposalConstraintsApplied: `True`
- RequestDurationErrorWindowEnforced: `True`
- ObservationWindowLimitEnforced: `True`
- DeterministicDryRunStable: `True`
- PreviewAddRemoveStable: `True`
- PreviewAdd min/max: `7` / `7`
- PreviewRemove min/max: `7` / `7`
- AppliedAdd/Remove max: `0` / `0`
- Risk/MustNot/Lifecycle max: `0` / `0` / `0`
- TokenDeltaTotalMax/TokenDeltaMaxMax: `0` / `0`
- PriorityInversionCountTotal: `0`
- SectionMismatchCountTotal: `0`
- DroppedRequiredCandidateCountTotal: `0`
- RollbackVerified: `True`
- KillSwitchVerified: `True`
- FormalSelectedSetChanged/FormalOutputChangedMax/FormalPackageWritten: `False` / `0` / `False`
- Package/PackingPolicy/runtime/vector: `False` / `False` / `False` / `False`
- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `False` / `False` / `False` / `False`

## AllowedActions
- `controlled applied merge proposal planning`
- `scope/limit review`
- `rollback and kill switch validation`

## ForbiddenActions
- `applied merge`
- `formal selected set mutation`
- `formal package write`
- `PackingPolicy mutation`
- `package output mutation`
- `runtime switch`
- `formal retrieval enable`
- `formal IVectorIndexStore binding mutation`
- `global default-on`

## BlockedReasons
- (empty)

Controlled shadow merge freeze only. It does not apply preview add/remove, mutate formal output, write a formal package, alter PackingPolicy/package output, switch runtime, or bind vector storage.
