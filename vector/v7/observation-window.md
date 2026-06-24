# Controlled Applied Merge Runtime Preview Observation Window

生成: `2026-06-24T15:07:34.2122011+00:00`
操作: `controlled-applied-merge-runtime-preview-observation-observation-d53731c49c714567aefe7f7b22a43ae1`

## Summary
- ObservationPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForRuntimePreviewObservationFreeze`
- NextAllowedPhase: `ControlledAppliedMergeRuntimePreviewObservationFreeze`

## Prerequisites
- PreflightPassed: `True`
- DryRunPassed: `True`
- V6FreezePassed: `True`
- RuntimeChangeGatePassed: `True`
- P15GatePassed: `True`

## Observation Window
- ObservationRunCount/Min: `5` / `3`
- FailedRunCount: `0`
- RequestCountTotal/Max: `5` / `100`
- DurationMinutes/Max: `5` / `30`
- ErrorCount/Max: `0` / `0`
- RequestDurationErrorWindowEnforced: `True`
- ObservationWindowLimitEnforced: `True`

## Stability
- DistinctStableSignatureCount: `1`
- DeterministicDryRunStable: `True`
- PreviewAddRemoveStable: `True`
- WouldApplyAdd min/max/total: `4` / `4` / `20`
- WouldApplyRemove min/max/total: `4` / `4` / `20`
- AppliedAdd/Remove max: `0` / `0`
- AppliedDeltaZero: `True`

## Metrics
- TokenDeltaTotalMax/PerSampleMax: `400` / `200`
- TokenDeltaWithinBudget: `True`
- ScopeLeakCountTotal: `0`
- ErrorCountTotal: `0`
- LatencyP50Avg: `5.9ms`  LatencyP95Max: `15.0ms`
- Risk/MustNot/Lifecycle max: `0` / `0` / `0`
- RollbackVerified: `True`
- KillSwitchTested: `True`
- TraceWritten: `True`
- ResultDiscarded: `True`

## Runtime Invariants (all must be false)
- FormalSelectedSetChanged: `False`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- GlobalDefaultOn: `False`
- NoRuntimeMutationInvariant: `True`

## Runs
- Run 1: passed=True add=4 remove=4 token=400 sig=51e22a77e139...
- Run 2: passed=True add=4 remove=4 token=400 sig=51e22a77e139...
- Run 3: passed=True add=4 remove=4 token=400 sig=51e22a77e139...
- Run 4: passed=True add=4 remove=4 token=400 sig=51e22a77e139...
- Run 5: passed=True add=4 remove=4 token=400 sig=51e22a77e139...

## Allowlisted Scopes
- `demo-workspace/demo-collection`

## Allowed Actions
- `RunScopedObservationWindow`
- `WriteTraceAndReportOnly`
- `DiscardPreviewResult`
- `ValidateAllowlistedScope`
- `VerifyRollbackPerRun`
- `VerifyKillSwitchPerRun`

## Forbidden Actions
- `GlobalDefaultOn`
- `FormalRetrievalEnable`
- `FormalPackageWrite`
- `PackingPolicyMutation`
- `PackageOutputMutationOutsidePreview`
- `VectorStoreBindingMutation`
- `RuntimeSwitch`
- `NonAllowlistedScopeUse`
- `ChangeFormalSelectedSet`
- `ApplyPreviewResult`

## Blocked Reasons
- (empty)

## Diagnostics
- `stage=observation`
- `preflightPassed=True`
- `dryRunPassed=True`
- `v6FreezePassed=True`
- `runtimeChangeGatePassed=True`
- `p15GatePassed=True`
- `observationRunCount=5`
- `failedRunCount=0`
- `requestCountTotal=5`
- `durationMinutes=5`
- `errorCountTotal=0`
- `distinctStableSignatures=1`
- `deterministicStable=True`
- `addMin=4 addMax=4 removeMin=4 removeMax=4`
- `appliedAddMax=0 appliedRemoveMax=0`
- `tokenTotalMax=400 tokenMaxMax=200`
- `scopeLeakTotal=0`
- `latencyP50Avg=5.9ms latencyP95Max=15.0ms`
- `riskMax=0 mustNotMax=0 lifecycleMax=0`
- `rollbackVerified=True killSwitchTested=True`
- `resultDiscarded=true`
- `traceWritten=true`
- `formalSelectedSetChanged=false`
- `formalPackageWritten=false`
- `packageOutputChanged=false`
- `packingPolicyChanged=false`
- `runtimeMutated=false`
- `vectorStoreBindingChanged=false`
- `formalRetrievalAllowed=false`
- `runtimeSwitchAllowed=false`
- `globalDefaultOn=false`

V7.3 scoped runtime preview observation window. scoped、preview-only、allowlisted only、discard result、trace/report only、no formal output mutation。不接 formal retrieval，不做 global runtime switch，不写 formal package，不改 PackingPolicy/package output，不绑定正式 IVectorIndexStore。
