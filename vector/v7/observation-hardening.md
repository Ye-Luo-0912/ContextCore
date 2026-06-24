# Controlled Applied Merge Runtime Preview Observation Hardening

生成: `2026-06-24T15:21:03.6932538+00:00`
操作: `controlled-applied-merge-runtime-preview-hardening-hardening-c90317c20b6a48ec96afc36340697d7c`

## Summary
- HardeningPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForRuntimePreviewObservationFreeze`
- NextAllowedPhase: `ControlledAppliedMergeRuntimePreviewObservationFreeze`

## Prerequisites
- ObservationWindowPassed: `True`
- PreflightPassed: `True`
- DryRunPassed: `True`
- V6FreezePassed: `True`
- RuntimeChangeGatePassed: `True`
- P15GatePassed: `True`

## Hardened Observation Thresholds
- ObservationRunCount/Min: `10` / `10`
- FailedRunCount: `0`
- RequestCountTotal/Min: `120` / `120`
- DurationMinutes/Max: `20` / `30`
- ErrorCountTotal/Max: `0` / `0`

## Route Metrics
- AllowlistedPreviewRouteHitCount: `120`
- NonAllowlistedPreviewRouteHitCount: `0`
- KillSwitchPreviewRouteHitCount: `0`
- NonAllowlistedNoOpCount: `40`
- KillSwitchNoOpCount: `30`

## Trace Integrity
- TraceCompletenessPercent: `100.0%`
- TracePayloadStable: `True`
- TraceReplayable: `True`

## Stability Signatures
- DistinctStableSignatureCount: `1`
- DeterministicStable: `True`
- SelectedCandidateIdsStable: `True`
- TracePayloadHashStable: `True`
- WouldApplyAdd min/max: `4` / `4`
- WouldApplyRemove min/max: `4` / `4`
- AppliedAdd/Remove max: `0` / `0`
- AppliedDeltaZero: `True`
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
- Run 1: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 2: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 3: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 4: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 5: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 6: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 7: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 8: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 9: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...
- Run 10: kind=mixed add=4 remove=4 routeHits=12 traceCompleteness=100% sig=b8b2ddaa789b...

## Allowlisted Scopes
- `demo-workspace/demo-collection`

## Allowed Actions
- `RunHardenedObservationWindow`
- `WriteTraceAndReportOnly`
- `DiscardPreviewResult`
- `ValidateAllowlistedRouteHits`
- `VerifyTraceCompleteness`
- `VerifyStabilitySignatures`

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
- `WriteConfigPatch`

## Blocked Reasons
- (empty)

## Diagnostics
- `stage=hardening`
- `observationWindowPassed=True`
- `preflightPassed=True`
- `dryRunPassed=True`
- `v6FreezePassed=True`
- `runtimeChangeGatePassed=True`
- `p15GatePassed=True`
- `observationRunCount=10`
- `failedRunCount=0`
- `requestCountTotal=120`
- `durationMinutes=20`
- `errorCountTotal=0`
- `allowlistedRouteHitsTotal=120`
- `nonAllowlistedRouteHitsTotal=0`
- `killSwitchRouteHitsTotal=0`
- `nonAllowlistedNoOpTotal=40`
- `killSwitchNoOpTotal=30`
- `traceCompletenessAvg=100.0%`
- `tracePayloadStable=True`
- `traceReplayable=True`
- `distinctStableSignatures=1`
- `deterministicStable=True`
- `distinctCandidateIdsHashes=1`
- `candidateIdsStable=True`
- `distinctTracePayloadHashes=1`
- `tracePayloadHashStable=True`
- `addMin=4 addMax=4 removeMin=4 removeMax=4`
- `appliedAddMax=0 appliedRemoveMax=0`
- `appliedDeltaZero=True`
- `rollbackVerified=True killSwitchTested=True`
- `resultDiscarded=true`
- `formalSelectedSetChanged=false`
- `formalPackageWritten=false`
- `packageOutputChanged=false`
- `packingPolicyChanged=false`
- `runtimeMutated=false`
- `vectorStoreBindingChanged=false`
- `formalRetrievalAllowed=false`
- `runtimeSwitchAllowed=false`
- `globalDefaultOn=false`
- `GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative`

V7.3R runtime preview observation hardening. 补强 V7.3 observation 使其达到 freeze-ready 证据强度。不改变 formal output，不启用 runtime switch。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。
