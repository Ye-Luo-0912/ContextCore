# Vector Scoped Runtime Experiment Approval Request Preview

Generated: `2026-06-17T05:15:23.2945721+00:00`
OperationId: `vector-scoped-runtime-experiment-runtime-approval-preview-8ca6578aa2d046f5bf6e9bdfcaa11f70`

- ProposalId: `vsrep-bb5402e39c0f1333`
- RequiredApprovalMode: `ScopedRuntimeExperiment`
- ProfileName: `post-scoring-risk-gated-v1`
- RecordWritten: `False`
- Recommendation: `NeedsManualApproval`

## Selected Scopes
- `contextcore_eval/dataset-v2-stress/dataset-v2-stress`

## Rollback Plan
`Remove the selected scope from the proposal allowlist, keep UseForRuntime=false, discard shadow artifacts, rerun V4.7 and runtime-change gates.`

## Kill Switch Plan
`Set proposal mode to ProposalOnly, clear workspace/collection/eval scope allowlists, and rerun runtime-change gate before any new proposal.`

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
