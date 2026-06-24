# Vector Guarded Scoped Runtime Experiment Plan Gate

Generated: `2026-06-17T06:25:28.7637069+00:00`
OperationId: `vector-guarded-scoped-runtime-experiment-gate-c05a2154fa93495f8f6e6fd20cbf4260`

## Summary
- PlanPassed: `True`
- Recommendation: `ReadyForScopedRuntimeExperimentActivationContract`
- ProposalId: `vsrep-bb5402e39c0f1333`
- RequiredApprovalMode: `ScopedRuntimeExperiment`
- MaxRequestCount: `120`
- MaxDurationMinutes: `30`
- RuntimeSwitchAllowed: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- KillSwitchPlan: `Set proposal mode to ProposalOnly, clear workspace/collection/eval scope allowlists, and rerun runtime-change gate before any new proposal.`
- RollbackPlan: `Remove the selected scope from the proposal allowlist, keep UseForRuntime=false, discard shadow artifacts, rerun V4.7 and runtime-change gates.`

## Selected Scopes
- `contextcore_eval/dataset-v2-stress/dataset-v2-stress`

## Observation Plan
- `RequestCount`
- `BaselinePackageCount`
- `ExperimentPackageCount`
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

## Allowed Actions
- `PrepareScopedRuntimeExperimentApproval`
- `ValidateSelectedScopeActivationContract`
- `CollectExperimentMetricsPlan`
- `ValidateRollbackPlan`
- `ValidateKillSwitchPlan`

## Forbidden Actions
- `GlobalRuntimeSwitch`
- `NonAllowlistedScopeUse`
- `FormalPackageWrite`
- `PackingPolicyMutation`
- `PackageOutputMutationOutsideExperimentArtifact`
- `DIBindingMutation`
- `IVectorIndexStoreGlobalBindingMutation`
- `DisablingRuntimeChangeGate`
- `TreatingNoOpHarnessOnlyApprovalAsRuntimeApproval`

## Blocked Reasons
- (empty)

V4.11 只冻结 guarded scoped runtime experiment plan / activation contract；不授权 runtime switch。
