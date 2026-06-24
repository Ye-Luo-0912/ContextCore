# Vector Scoped Runtime Experiment Proposal

Generated: `2026-06-17T03:13:19.7715232+00:00`
OperationId: `vector-scoped-runtime-experiment-proposal-a7d63db9784c46d5919815d7e745ecca`

## Summary

- ProposalId: `vsrep-bb5402e39c0f1333`
- ProposalPassed: `True`
- Recommendation: `ReadyForManualExperimentApproval`
- WorkspaceId: `contextcore_eval`
- CollectionId: `dataset-v2-stress`
- EvalScopeId: `dataset-v2-stress`
- ProfileName: `post-scoring-risk-gated-v1`
- ApprovalRequired: `True`
- Approved: `False`
- RuntimeSwitchAllowed: `False`
- FormalRetrievalAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`
- WriteFormalPackage: `False`
- ConfigPatchWritten: `False`
- DiBindingChanged: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
- NonAllowlistedScopeLeakCount: `0`
- RollbackPlan: `Remove the selected scope from the proposal allowlist, keep UseForRuntime=false, discard shadow artifacts, rerun V4.7 and runtime-change gates.`
- KillSwitchPlan: `Set proposal mode to ProposalOnly, clear workspace/collection/eval scope allowlists, and rerun runtime-change gate before any new proposal.`

## Required Gate Summary
- foundation-release-candidate-gate: `True:ReadyForReleaseCandidate`
- foundation-reproducibility-check: `True:ReadyForReleaseCandidateReproduction`
- learning-runtime-change-readiness-gate: `True:RuntimeChangeRulesSatisfied`
- service-foundation-freeze-gate: `True:ReadyForV45ExplicitScopedRuntimeExperimentPlanning`
- vector-formal-preview-freeze-gate: `True:ReadyForScopedOptInPreview`
- vector-scoped-runtime-experiment-design-freeze-gate: `True:ReadyForRuntimeExperimentProposal`

## Proposed Config Patch Preview
- collectionAllowlist: `dataset-v2-stress`
- evalScopeAllowlist: `dataset-v2-stress`
- formalRetrievalAllowed: `false`
- killSwitchInstruction: `Set proposal mode to ProposalOnly, clear workspace/collection/eval scope allowlists, and rerun runtime-change gate before any new proposal.`
- observationWindow: `manual-approval-required-before-any-runtime-change`
- previewOnly: `true`
- profileName: `post-scoring-risk-gated-v1`
- readyForRuntimeSwitch: `false`
- rollbackInstruction: `Remove the selected scope from the proposal allowlist, keep UseForRuntime=false, discard shadow artifacts, rerun V4.7 and runtime-change gates.`
- traceOutputPath: `vector/v4/scoped-runtime-experiment-traces.jsonl`
- useForRuntime: `false`
- workspaceAllowlist: `contextcore_eval`
- writeFormalPackage: `false`
- writeTarget: `none`

## Observation Plan
- BaselinePackageCount: `collect`
- CandidateAddCount: `collect`
- CandidateRemoveCount: `collect`
- ErrorCount: `collect`
- FormalOutputChanged: `mustRemainZero`
- LatencyP50: `collect`
- LatencyP95: `collect`
- LifecycleRiskAfterPolicy: `mustRemainZero`
- MustNotHitRiskAfterPolicy: `mustRemainZero`
- PackageOutputChanged: `mustRemainFalse`
- PackingPolicyChanged: `mustRemainFalse`
- PreviewPackageCount: `collect`
- RequestCount: `collect`
- RiskAfterPolicy: `mustRemainZero`
- RollbackVerified: `required`
- RuntimeMutated: `mustRemainFalse`
- ScopeLeakCount: `mustRemainZero`
- TokenDeltaMax: `collect`
- TokenDeltaTotal: `collect`

## Forbidden Actions
- `ModifyAppsettingsRuntimeConfig`
- `ModifyDIBinding`
- `FormalIVectorIndexStoreBinding`
- `FormalPackageWrite`
- `PackingPolicyMutation`
- `PackageOutputMutation`
- `RuntimeSwitch`
- `GlobalDefaultOn`
- `NonAllowlistedScopeUse`

## Blocked Reasons
- (empty)

## Source Reports
- foundationReleaseCandidateGate: `foundation\foundation-release-candidate-gate.json`
- foundationReproducibilityCheck: `foundation\foundation-reproducibility-check.json`
- learningRuntimeChangeReadinessGate: `learning\readiness\learning-runtime-change-readiness-gate.json`
- scopedRuntimeExperimentDesignFreezeGate: `vector\v4\vector-scoped-runtime-experiment-design-freeze-gate.json`
- scopedRuntimeExperimentGate: `vector\v4\vector-scoped-runtime-experiment-gate.json`
- serviceFoundationFreezeGate: `service\service-foundation-freeze-gate.json`
- vectorFormalPreviewFreezeGate: `vector\v4\vector-formal-preview-freeze-gate.json`

## Runtime Boundary

- V4.8 只生成 explicit scoped runtime experiment proposal 和 config patch preview。
- 不写 appsettings，不改 DI binding，不绑定正式 `IVectorIndexStore`，不写正式 package。
- `Approved=false`；任何 approval 必须来自后续人工 gate，不能由本 eval 自动产生。
