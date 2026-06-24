# Controlled Shadow Merge Proposal

Generated: `2026-06-22T11:11:48.2818498+00:00`
OperationId: `controlled-shadow-merge-proposal-ba04c76cef6740f79fce3853fb546329`

## Summary
- ProposalPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForControlledMergePreviewPlan`
- ProposalId: `csm-aa75535bb5694dca`
- Mode: `ProposalOnly`
- AllowedMode: `ControlledShadowMergePreviewProposalOnly`
- NextAllowedPhase: `ControlledMergePreviewPlan`
- Gate V6.6/V6.7/observation/promotion/runtime: `True` / `True` / `True` / `True` / `True`
- ScopeCount: `1`
- Max request/duration/errors: `120` / `30` / `0`
- Max preview add/remove/token total/token max: `10` / `10` / `128` / `32`
- Minimum observation runs/samples: `10` / `120`
- Observed preview add/remove: `7` / `7`
- Applied add/remove: `0` / `0`
- Risk/must-not/lifecycle: `0` / `0` / `0`
- Formal/package/policy/runtime/vector: `0` / `False` / `False` / `False` / `False`
- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `False` / `False` / `False` / `False`
- RollbackPlanPresent: `True`
- KillSwitchPlanPresent: `True`

## SelectedScopes
- `contextcore-foundation/source-diverse-shadow-validation/v6-source-diverse-shadow-validation`

## RequiredGateSummary
- `Runtime change gate`: `Passed`
- `Shadow merge promotion decision`: `Passed`
- `V6.6 source-diverse shadow adapter validation`: `Passed`
- `V6.7 shadow candidate merge preview`: `Passed`
- `V6.7 shadow merge observation`: `Passed`

## ScopeConditions
- `workspace allowlist must be explicit`
- `collection allowlist must be explicit`
- `eval scope allowlist must be explicit`
- `non-allowlisted scope must remain baseline`
- `non-allowlisted scope leak count must remain zero`
- `global default-on is forbidden`

## LimitConditions
- `MaxRequestCount <= 120`
- `MaxDurationMinutes <= 30`
- `MaxErrorCount <= 0`
- `MaxPreviewAddCount <= 10`
- `MaxPreviewRemoveCount <= 10`
- `MaxTokenDeltaTotal <= 128`
- `MaxTokenDeltaPerSample <= 32`
- `MinObservationRunCount >= 10`
- `MinSampleObservationCount >= 120`

## GateConditions
- `V6.6 gate passed`
- `V6.7 preview gate passed`
- `V6.7 observation gate passed`
- `shadow merge promotion decision passed`
- `runtime-change gate passed`
- `risk/mustNot/lifecycle remain zero`
- `formal/package/PackingPolicy/runtime/vector binding invariants unchanged`

## RollbackConditions
- `rollback plan must be present before any controlled preview route`
- `rollback must return selected scope to baseline`
- `rollback must not delete historical trace artifacts`
- `rollback must not write formal package or mutate package output`

## KillSwitchConditions
- `kill switch plan must be present before any controlled preview route`
- `kill switch must fail closed to baseline`
- `kill switch must not affect non-allowlisted baseline requests`
- `kill switch state must be traceable in observation artifacts`

## ObservationConditions
- `AppliedAddCountMustRemainZero`
- `AppliedRemoveCountMustRemainZero`
- `BaselineSelectedSetCount`
- `FormalOutputChanged`
- `FormalPackageWritten`
- `FormalSelectedSetChanged`
- `KillSwitchTriggered`
- `LifecycleRiskAfterPolicy`
- `MustNotHitRiskAfterPolicy`
- `NonAllowlistedScopeLeakCount`
- `PackageOutputChanged`
- `PackingPolicyChanged`
- `PreviewAddCount`
- `PreviewMergedSetCount`
- `PreviewRemoveCount`
- `PriorityInversionCount`
- `RequestCount`
- `RiskAfterPolicy`
- `RollbackVerified`
- `RuntimeMutated`
- `SectionMismatchCount`
- `TokenDeltaMax`
- `TokenDeltaTotal`
- `TraceCompleteness`
- `VectorStoreBindingChanged`

## StopConditions
- `AppliedAddCount > 0`
- `AppliedRemoveCount > 0`
- `FormalOutputChanged > 0`
- `FormalPackageWritten=true`
- `FormalSelectedSetChanged=true`
- `KillSwitchUnavailable`
- `LifecycleRiskAfterPolicy > 0`
- `MustNotHitRiskAfterPolicy > 0`
- `NonAllowlistedScopeLeakCount > 0`
- `PackageOutputChanged=true`
- `PackingPolicyChanged=true`
- `PriorityInversionCount > 0`
- `RiskAfterPolicy > 0`
- `RollbackUnavailable`
- `RuntimeMutated=true`
- `SectionMismatchCount > 0`
- `TokenDeltaMax > configured budget`
- `TokenDeltaTotal > configured budget`
- `TraceCompleteness < 100%`
- `VectorStoreBindingChanged=true`

## AllowedActions
- `controlled shadow merge preview planning`
- `scope/limit review`
- `rollback and kill switch validation`
- `observation plan review`

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
- `non-allowlisted scope use`

## BlockedReasons
- (empty)

This proposal only defines the future controlled shadow merge preview contract. It does not apply add/remove, mutate the formal selected set, write a formal package, alter PackingPolicy/package output, switch runtime, or bind a vector store.
