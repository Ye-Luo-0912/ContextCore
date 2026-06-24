# Controlled Shadow Merge Observation Window

Generated: `2026-06-22T14:09:15.2672707+00:00`
OperationId: `controlled-shadow-merge-observation-window-7e6e3a1eb3fe44998f9e33605fb81d00`

## Summary
- ObservationPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForControlledShadowMergeObservationFreeze`
- ProposalId: `csm-aa75535bb5694dca`
- V6.10 DryRunGatePassed: `True`
- ProposalConstraintsApplied: `True`
- ObservationRunCount/MinObservationRunCount: `10` / `10`
- RequestCountTotal/MaxRequestCount: `120` / `120`
- DurationMinutes/MaxDurationMinutes: `0` / `30`
- ErrorCount/MaxErrorCount: `0` / `0`
- RequestDurationErrorWindowEnforced: `True`
- SampleObservationCount/MinSampleObservationCount: `120` / `120`
- ObservationWindowLimitEnforced: `True`
- DeterministicDryRunStable: `True`
- DistinctStableSignatureCount: `1`
- PreviewAddCount min/max/total: `7` / `7` / `70`
- PreviewRemoveCount min/max/total: `7` / `7` / `70`
- AppliedAdd/Remove max: `0` / `0`
- Risk/MustNot/Lifecycle max: `0` / `0` / `0`
- TokenDeltaTotalMax/TokenDeltaMaxMax: `0` / `0`
- PriorityInversionCountTotal: `0`
- SectionMismatchCountTotal: `0`
- DroppedRequiredCandidateCountTotal: `0`
- RollbackVerified: `True`
- KillSwitchVerified: `True`
- AppliedDeltaZero: `True`
- FormalOutputChangedMax: `0`
- FormalSelectedSetChanged/FormalPackageWritten: `False` / `False`
- Package/PackingPolicy/runtime/vector: `False` / `False` / `False` / `False`
- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `False` / `False` / `False` / `False`

## SelectedScopes
- `contextcore-foundation/source-diverse-shadow-validation/v6-source-diverse-shadow-validation`

## BlockedReasons
- (empty)

Controlled shadow merge observation window only. It repeats V6.10 dry-run gate under proposal constraints and does not apply add/remove, mutate formal output, write formal package, alter PackingPolicy/package output, switch runtime, or bind vector storage.
