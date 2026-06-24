# Controlled Applied Merge Proposal

Generated: `2026-06-22T16:18:09.6720486+00:00`
OperationId: `controlled-applied-merge-proposal-6ed051ac7ad0443d94c7d119907a6700`

## Summary
- ProposalPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForControlledAppliedMergeDryRunGate`
- ProposalId: `camp-05b30e065beb8f10`
- RequiredApprovalMode: `ControlledAppliedMergePreview`
- Mode: `ProposalOnly`
- AllowedMode: `ControlledAppliedMergeProposalOnly`
- NextAllowedPhase: `ControlledAppliedMergeDryRunGate`
- PromotionDecisionGatePassed: `True`
- RuntimeChangeGatePassed: `True`
- ScopeCount: `1`
- Max request/duration/errors: `120` / `30` / `0`
- Max applied add/remove/token total/token max: `7` / `7` / `128` / `32`
- Stable preview add/remove: `7` / `7`
- Applied add/remove: `0` / `0`
- ManualApprovalRequired/ApprovalPlanPresent: `True` / `True`
- Rollback/KillSwitch present: `True` / `True`
- Risk/must-not/lifecycle: `0` / `0` / `0`
- Formal selected/formal output/formal package: `False` / `0` / `False`
- Package/PackingPolicy/runtime/vector: `False` / `False` / `False` / `False`
- AppliedMergeAllowed/FormalPackageWriteAllowed: `False` / `False`
- UseForRuntime/FormalRetrieval/RuntimeSwitch/ReadyForRuntimeSwitch: `False` / `False` / `False` / `False`

## SelectedScopes
- `contextcore-foundation/source-diverse-shadow-validation/v6-source-diverse-shadow-validation`

## RequiredGateSummary
- `Runtime change gate`: `Passed`
- `V6.13 controlled shadow merge promotion decision`: `Passed`

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
- `MaxAppliedAddCount <= 7`
- `MaxAppliedRemoveCount <= 7`
- `MaxTokenDeltaTotal <= 128`
- `MaxTokenDeltaPerSample <= 32`
- `MinObservationRunCount >= 10`
- `MinSampleObservationCount >= 120`
- `AppliedAddCount must remain zero until a later explicit applied-preview gate`
- `AppliedRemoveCount must remain zero until a later explicit applied-preview gate`

## ApprovalConditions
- `approval expiry must be enforced`
- `ApprovalMode must be ControlledAppliedMergePreview`
- `ApprovedBy is required in a later approval phase`
- `explicit confirmation is required before any applied preview dry-run`
- `kill switch acknowledgement is required`
- `observation acknowledgement is required`
- `Reason is required in a later approval phase`
- `revoked approval must fail closed`
- `risk acknowledgement is required`
- `rollback acknowledgement is required`
- `scope acknowledgement is required`

## RollbackConditions
- `rollback plan must be present before any controlled applied merge preview`
- `rollback must return selected scope to baseline`
- `rollback must preserve historical trace artifacts`
- `rollback must not write formal package or mutate package output`

## KillSwitchConditions
- `kill switch plan must be present before any controlled applied merge preview`
- `kill switch must fail closed to baseline`
- `kill switch must not affect non-allowlisted baseline requests`
- `kill switch state must be traceable in observation artifacts`

## ObservationConditions
- `AppliedAddCountMustRemainZeroUntilLaterGate`
- `AppliedRemoveCountMustRemainZeroUntilLaterGate`
- `BaselineSelectedSetCount`
- `ControlledAppliedPreviewSetCount`
- `DroppedRequiredCandidateCount`
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
- `AppliedAddCount > 0 before applied preview gate`
- `AppliedRemoveCount > 0 before applied preview gate`
- `DroppedRequiredCandidateCount > 0`
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
- `controlled applied merge preview proposal review`
- `manual approval planning`
- `scope/limit review`
- `rollback and kill switch validation`
- `observation plan review`

## ForbiddenActions
- `applied merge before explicit later gate`
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

This proposal only defines the future controlled applied merge preview contract. It does not apply add/remove, mutate the formal selected set, write a formal package, alter PackingPolicy/package output, switch runtime, or bind a vector store.
