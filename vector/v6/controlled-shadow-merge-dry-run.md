# Controlled Shadow Merge Dry-run

Generated: `2026-06-22T12:46:39.9301919+00:00`
OperationId: `controlled-shadow-merge-dry-run-gate-1e2411951b284be4926bb359b724e153`

## Summary
- DryRunPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForControlledShadowMergeObservation`
- ProposalId: `csm-aa75535bb5694dca`
- ProposalGatePassed: `True`
- ProposalConstraintsApplied: `True`
- ScopeCount: `1`
- RequestCount/MaxRequestCount: `12` / `120`
- DurationMinutes/MaxDurationMinutes: `0` / `30`
- ErrorCount/MaxErrorCount: `0` / `0`
- RequestDurationErrorLimitEnforced: `True`
- ObservationRunCount/MinObservationRunCount: `10` / `10`
- SampleObservationCount/MinSampleObservationCount: `120` / `120`
- ObservationWindowLimitEnforced: `True`
- Observation/Stop condition counts: `25` / `20`
- ObservationPlanConstraintPresent: `True`
- DryRunPreviewGenerated: `True`
- PreviewAdd/Remove: `7` / `7`
- Proposal max add/remove: `10` / `10`
- AddRemoveLimitEnforced: `True`
- TokenDeltaTotal/Max: `0` / `0`
- Proposal max token total/sample: `128` / `32`
- TokenSectionPriorityGatePassed: `True`
- PriorityInversionCount: `0`
- SectionMismatchCount: `0`
- DroppedRequiredCandidateCount: `0`
- RollbackPlanPresent/Verified: `True` / `True`
- KillSwitchAvailable/Verified: `True` / `True`
- AppliedAdd/Remove: `0` / `0`
- Risk/must-not/lifecycle: `0` / `0` / `0`
- FormalOutputChanged: `0`
- FormalSelectedSetChanged/FormalPackageWritten: `False` / `False`
- Package/PackingPolicy/runtime/vector: `False` / `False` / `False` / `False`
- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `False` / `False` / `False` / `False`

## SelectedScopes
- `contextcore-foundation/source-diverse-shadow-validation/v6-source-diverse-shadow-validation`

## ConstraintChecks
- `add/remove limit`: `Passed`
- `explicit scope`: `Passed`
- `kill switch plan`: `Passed`
- `observation window`: `Passed`
- `observation/stop condition plan`: `Passed`
- `proposal gate`: `Passed`
- `request/duration/error limit`: `Passed`
- `rollback plan`: `Passed`
- `token/section/priority gate`: `Passed`

## BlockedReasons
- (empty)

Controlled shadow merge dry-run gate only. It verifies proposal constraints against preview artifacts and does not apply add/remove, mutate formal output, write formal package, alter PackingPolicy/package output, switch runtime, or bind vector storage.
