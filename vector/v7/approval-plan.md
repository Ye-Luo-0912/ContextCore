# Scoped Runtime Preview Approval Plan

生成: `2026-06-24T16:06:35.4264351+00:00`
操作: `scoped-runtime-preview-approval-plan-plan-b93ff5bd7ca14104a990880bfd7bc25c`

## Decision
- PlanPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForScopedRuntimePreviewAuthorization`
- NextAllowedPhase: `ScopedRuntimePreviewAuthorizationGathering`

## Approval Plan
- ApprovalPlanId: `arsp-ap-20260624-3e6776f681304dc694b143b0e28e3323`
- ApprovalAuthority: `ArchitectureReviewBoard`
- AuthorizedApprovers: `ReleaseManager, ArchitectureLead`
- ApprovedScopes: `demo-workspace/demo-collection`
- ValidityNotBefore: `2026-06-24T16:06:35.4264351+00:00`
- ValidityNotAfter: `2026-07-24T16:06:35.4264351+00:00`
- ValidityDurationDays: `30`
- ValidityWithinBounds: `True`

## Revocation Mechanism
- RevocationMechanism: `MajorityVoteByAuthorizedApprovers`
- RevocationRequiresMajority: `True`
- EmergencyRevocationContact: `release-manager@contextcore.local`

## Revocation Triggers
- `SafetyBoundaryViolation`
- `RuntimeSwitchDetected`
- `FormalPackageWritten`
- `FormalRetrievalEnabled`
- `ObservationWindowThresholdBreach`
- `P15Regression`
- `RuntimeChangeGateFailure`
- `EmergencyEscalation`

## Kill Switch
- KillSwitchPlan: `RevertToPreviewOnlyWithin60s`
- KillSwitchAction: `RollbackAllScopedPreviewToPreviewOnly`
- KillSwitchResponseTimeSeconds: `60`
- KillSwitchTested: `True`
- KillSwitchConfigured: `True`

## Rollback
- RollbackPlan: `RollbackAllScopedPreviewWithin15Minutes`
- RollbackMaxDurationMinutes: `15`
- RollbackVerified: `True`
- RollbackCheckpointStrategy: `FullPreviewStateSnapshotBeforeAnyApply`
- RollbackConfigured: `True`

## Trace Retention
- TraceRetentionPolicy: `RetainAllPreviewTracesFor90Days`
- TraceRetentionDays: `90`
- TraceStoragePath: `vector/v7/runtime-preview-trace.jsonl`
- TraceRetentionConfigured: `True`

## Prerequisites
- V7FreezePassed: `True`
- V7HardeningPassed: `True`
- V6FreezePassed: `True`
- OptFreezePassed: `True`
- RuntimeChangeGatePassed: `True`
- P15GatePassed: `True`

## Safety Boundaries
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- FormalPackageWritten: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- VectorStoreBindingChanged: `False`
- GlobalDefaultOn: `False`
- NoRuntimeMutationInvariant: `True`


## Allowed Actions
- `ReadV7Freeze`
- `ReadV7Hardening`
- `ReadV6Freeze`
- `ReadOptFFreeze`
- `ReadRuntimeChangeGate`
- `ReadP15Report`
- `DefineApprovalAuthority`
- `DefineApprovedScopes`
- `DefineValidityWindow`
- `DefineRevocationMechanism`
- `DefineKillSwitchPlan`
- `DefineRollbackPlan`
- `DefineTraceRetentionPolicy`
- `WriteApprovalPlanArtifactsOnly`
- `ValidateSafetyBoundaries`

## Forbidden Actions
- `GlobalDefaultOn`
- `FormalRetrievalEnable`
- `FormalPackageWrite`
- `PackingPolicyMutation`
- `PackageOutputMutation`
- `VectorStoreBindingMutation`
- `RuntimeSwitch`
- `RuntimeActivation`
- `WriteConfigPatch`
- `ApplyPreviewResult`
- `ChangeFormalSelectedSet`
- `WriteFormalPackageToOutput`
- `MutationOfApprovedScopes`
- `MutationOfApprovalPlan`
- `ChangeFrozenSafetyBoundaries`

## Blocked Reasons
- (empty)

## Diagnostics
- `stage=plan`
- `v7FreezePassed=True`
- `v7HardeningPassed=True`
- `runtimeChangeGatePassed=True`
- `p15GatePassed=True`
- `authority=ArchitectureReviewBoard`
- `approvers=2`
- `scopes=1`
- `validityDays=30 withinBounds=True`
- `revocationMechanism=MajorityVoteByAuthorizedApprovers triggers=8`
- `killSwitchConfigured=True tested=True`
- `rollbackConfigured=True verified=True`
- `traceRetentionDays=90 configured=True`
- `noRuntimeMutationInvariant=True`
- `planPassed=True gatePassed=False`
- `GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative`

V7.5 scoped runtime preview approval plan. 明确审批主体、scope、有效期、撤销机制、kill switch、rollback、trace retention。不启用 runtime activation。不切 runtime switch。不写 formal package。GatePassed=false is expected for non-gate artifact; *-gate artifact is authoritative。
