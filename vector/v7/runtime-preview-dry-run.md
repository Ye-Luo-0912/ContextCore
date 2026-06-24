# Controlled Applied Merge Runtime Preview Dry-run

生成: `2026-06-24T13:04:03.8323261+00:00`
操作: `controlled-applied-merge-runtime-preview-dryrun-dry-run-4508695634fb48508d395c9af6437ab8`

## Summary
- DryRunPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForRuntimePreviewGate`
- NextAllowedPhase: `ControlledAppliedMergeRuntimePreviewGate`

## Prerequisites
- PlanPassed: `True`
- V6FreezePassed: `True`

## Dry-run Metrics
- AllowlistedScopes: `1`
- TracePath: `vector/v7/runtime-preview-trace.jsonl`
- ObservationRuns: `3`
- RequestCount: `3`
- WouldApplyAdd: `4`  WouldApplyRemove: `4`
- AppliedAdd: `0`  AppliedRemove: `0`
- BaselinePackages: `3`  PreviewPackages: `3`
- TotalTokenDelta: `400`  MaxTokenPerSample: `200`
- ScopeLeakCount: `0`  ErrorCount: `0`
- LatencyP50: `5.8ms`  LatencyP95: `14.5ms`
- RollbackVerified: `True`
- KillSwitchTested: `True`
- StopConditionsChecked: `True`
- TraceWritten: `True`
- Risk: `0`

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


## Allowlisted Scopes
- `demo-workspace/demo-collection`

## Observation Metrics
- `RequestCount`
- `PreviewPackageCount`
- `BaselinePackageCount`
- `CandidateAddCount`
- `CandidateRemoveCount`
- `TokenDeltaTotal`
- `TokenDeltaMax`
- `RiskAfterPolicy`
- `MustNotHitRiskAfterPolicy`
- `LifecycleRiskAfterPolicy`
- `FormalOutputChanged`
- `PackageOutputChanged`
- `PackingPolicyChanged`
- `ScopeLeakCount`
- `ErrorCount`
- `LatencyP50`
- `LatencyP95`
- `KillSwitchTriggered`
- `RollbackVerified`
- `TraceCompleteness`

## Stop Conditions
- `RiskAfterPolicy > 0`
- `MustNotHitRiskAfterPolicy > 0`
- `LifecycleRiskAfterPolicy > 0`
- `FormalOutputChanged > 0`
- `PackageOutputChanged=true`
- `PackingPolicyChanged=true`
- `RuntimeMutated unexpected`
- `NonAllowlistedScopeLeakCount > 0`
- `ErrorCount > threshold`
- `LatencyP95 > threshold`
- `MissingTraceCount > 0`
- `KillSwitchTriggered=true`

## Allowed Actions
- `ComputePreviewResultOnly`
- `WritePreviewTraceOnly`
- `ValidateAllowlistedScope`
- `SimulateRollback`
- `SimulateKillSwitch`
- `CollectDryRunMetrics`

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
- `DisablingRuntimeChangeGate`

## Blocked Reasons
- (empty)

## Diagnostics
- `stage=dry-run`
- `planPassed=True`
- `v6FreezePassed=True`
- `observationRuns=3`
- `stablePreviewAdd=7 stablePreviewRemove=7`
- `wouldApplyAdd=4 wouldApplyRemove=4`
- `totalTokenDelta=400 maxTokenPerSample=200`
- `requestCount=3`
- `baselinePackageCount=3 previewPackageCount=3`
- `scopeLeakCount=0 errorCount=0`
- `latencyP50=5.8ms latencyP95=14.5ms`
- `rollback=simulated-verified`
- `killSwitch=simulated-tested`
- `stopConditions=checked`
- `trace=written-to-plan-trace-path`
- `formalSelectedSetChanged=false`
- `formalPackageWritten=false`
- `packageOutputChanged=false`
- `packingPolicyChanged=false`
- `runtimeMutated=false`
- `vectorStoreBindingChanged=false`

V7.1 controlled applied merge runtime preview dry-run harness. 只计算 preview result，只写 trace/report，不改变 formal selected set，不写 formal package，不改变 package output，不改变 PackingPolicy。
