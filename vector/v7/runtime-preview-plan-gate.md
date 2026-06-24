# Controlled Applied Merge Runtime Preview Plan Gate

生成: `2026-06-24T12:40:43.0123861+00:00`
OperationId: `controlled-applied-merge-runtime-preview-gate-1cc6c272abbc435ab1d84d61e1c55188`

## Summary
- PlanPassed: `True`
- Recommendation: `ReadyForRuntimePreviewActivation`
- Mode: `PlanOnly`
- NextAllowedPhase: `ControlledAppliedMergeRuntimePreviewActivationContract`

## Prerequisites
- V6FreezePassed: `True`
- OPTFreezePassed: `True`
- RuntimeChangeGatePassed: `True`
- P15GatePassed: `True`

## Plan Definition
- ConfigSwitch: `ControlledAppliedMergeRuntimePreview:Enabled`
- ApprovalMode: `ControlledAppliedMergePreview`
- TracePath: `vector/v7/runtime-preview-trace.jsonl`
- MaxRequestCount: `100`
- MaxDurationMinutes: `30`

## Allowlisted Scopes
- `demo-workspace/demo-collection`

## Kill Switch Plan
Set ControlledAppliedMergeRuntimePreview:Enabled=false; flush preview trace; discard preview packages; revert to baseline retrieval path

## Rollback Plan
Disable config switch → flush trace → discard preview artifacts → verify baseline package output unchanged → verify no formal package written → verify no PackingPolicy mutation


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
- `PrepareRuntimePreviewActivationContract`
- `ValidateAllowlistedScopeActivation`
- `CollectPreviewMetricsPlan`
- `ValidateRollbackPlan`
- `ValidateKillSwitchPlan`
- `WritePreviewTraceOnly`

## Forbidden Actions
- `GlobalDefaultOn`
- `FormalRetrievalEnable`
- `FormalPackageWrite`
- `PackingPolicyMutation`
- `PackageOutputMutationOutsidePreview`
- `VectorStoreBindingMutation`
- `RuntimeSwitch`
- `NonAllowlistedScopeUse`
- `DisablingRuntimeChangeGate`
- `DisablingP15Gate`

## Runtime Invariants (all must be false)
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- FormalPackageWritten: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- GlobalDefaultOn: `False`


## Blocked Reasons
- (empty)

## Diagnostics
- `Stage: gate`
- `V6FreezePassed: True`
- `OPTFreezePassed: True`
- `RuntimeChangeGatePassed: True`
- `P15GatePassed: True`
- `AllowlistedScopes: 1`
- `ConfigSwitch: ControlledAppliedMergeRuntimePreview:Enabled`
- `ApprovalMode: ControlledAppliedMergePreview`
- `TracePath: vector/v7/runtime-preview-trace.jsonl`
- `MaxRequestCount: 100`
- `MaxDurationMinutes: 30`
- `FormalRetrievalAllowed: false`
- `RuntimeSwitchAllowed: false`
- `FormalPackageWritten: false`
- `PackingPolicyChanged: false`
- `PackageOutputChanged: false`
- `RuntimeMutated: false`
- `VectorStoreBindingChanged: false`
- `GlobalDefaultOn: false`

V7.0 controlled applied merge runtime preview plan. 只产出 plan，不实现 runtime preview。不接 formal retrieval，不做 global runtime switch，不写 formal package，不改 PackingPolicy/package output，不绑定正式 IVectorIndexStore。
