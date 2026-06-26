# Rollback Readiness Matrix Gate

生成: `2026-06-26T18:50:21.5866705+00:00`
操作: `frp-rollback-readiness-matrix-5e75ac3dd0df4d7f852c6610cdaa035d`

## Decision
- RollbackReadinessMatrixPassed: `True` GatePassed: `True`
- Total: `9` Passed: `9` Failed: `0`
- Status — NotApplicable: `2` Incomplete: `6` Ready: `1`

## No-Activation Contract
- ManualReviewRequired: `False`
- ApprovalSealed: `False`
- CapabilityGrantWritten: `False`
- GrantApplied: `False`
- ApplicationApplied: `False`
- RollbackActivated: `False`  (RollbackReady != Activated)
- PromotionToMainlinePerformed: `False`
- EvidenceCopiedToMainline: `False`
- TrustRegistryCopiedToMainline: `False`
- MainlineEvidencePresent: `False`
- MainlineTrustRegistryPresent: `False`

## Safety
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- VectorStoreBindingChanged: `False`
- GlobalDefaultOn: `False`
- ConfigPatchWritten: `False`
- RuntimeActivation: `False`
- NoRuntimeMutationInvariant: `True`

## Rollback Readiness Cases
- `NotApplicableFromBlockedApplication`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationBlocked` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessNotApplicable` actual=`RollbackReadinessNotApplicable` matched=`True`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: application status 'GrantApplicationBlocked' is not Ready; rollback readiness not evaluated.
- `NotApplicableFromNotApplicableApplication`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationNotApplicable` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessNotApplicable` actual=`RollbackReadinessNotApplicable` matched=`True`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: application status 'GrantApplicationNotApplicable' is not Ready; rollback readiness not evaluated.
- `IncompleteMissingSnapshot`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessIncomplete` actual=`RollbackReadinessIncomplete` matched=`True`
  - expectedMissing=`PreApplicationSnapshotPresent` matched=`True`
  - rollbackElementsMet=`RollbackPlaybookPresent, RollbackDryRunPassed, StateRestorationProvenInTest, RollbackOperatorAccessPathPresent`
  - rollbackElementsMissing=`PreApplicationSnapshotPresent`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 rollback element(s) missing; rollback readiness incomplete.
- `IncompleteMissingPlaybook`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessIncomplete` actual=`RollbackReadinessIncomplete` matched=`True`
  - expectedMissing=`RollbackPlaybookPresent` matched=`True`
  - rollbackElementsMet=`PreApplicationSnapshotPresent, RollbackDryRunPassed, StateRestorationProvenInTest, RollbackOperatorAccessPathPresent`
  - rollbackElementsMissing=`RollbackPlaybookPresent`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 rollback element(s) missing; rollback readiness incomplete.
- `IncompleteMissingDryRun`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessIncomplete` actual=`RollbackReadinessIncomplete` matched=`True`
  - expectedMissing=`RollbackDryRunPassed` matched=`True`
  - rollbackElementsMet=`PreApplicationSnapshotPresent, RollbackPlaybookPresent, StateRestorationProvenInTest, RollbackOperatorAccessPathPresent`
  - rollbackElementsMissing=`RollbackDryRunPassed`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 rollback element(s) missing; rollback readiness incomplete.
- `IncompleteMissingRestorationProof`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessIncomplete` actual=`RollbackReadinessIncomplete` matched=`True`
  - expectedMissing=`StateRestorationProvenInTest` matched=`True`
  - rollbackElementsMet=`PreApplicationSnapshotPresent, RollbackPlaybookPresent, RollbackDryRunPassed, RollbackOperatorAccessPathPresent`
  - rollbackElementsMissing=`StateRestorationProvenInTest`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 rollback element(s) missing; rollback readiness incomplete.
- `IncompleteMissingOperatorAccess`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessIncomplete` actual=`RollbackReadinessIncomplete` matched=`True`
  - expectedMissing=`RollbackOperatorAccessPathPresent` matched=`True`
  - rollbackElementsMet=`PreApplicationSnapshotPresent, RollbackPlaybookPresent, RollbackDryRunPassed, StateRestorationProvenInTest`
  - rollbackElementsMissing=`RollbackOperatorAccessPathPresent`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 rollback element(s) missing; rollback readiness incomplete.
- `RollbackReadyButNothingActivated`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReady` actual=`RollbackReady` matched=`True`
  - rollbackElementsMet=`PreApplicationSnapshotPresent, RollbackPlaybookPresent, RollbackDryRunPassed, StateRestorationProvenInTest, RollbackOperatorAccessPathPresent`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: all rollback elements present; if applied we can undo. Note: application still not executed, rollback path still not activated.
- `IncompleteMultipleMissing`: passedAsExpected=`True`
  - inputApplicationStatus=`GrantApplicationReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`RollbackReadinessIncomplete` actual=`RollbackReadinessIncomplete` matched=`True`
  - expectedMissing=`PreApplicationSnapshotPresent` matched=`True`
  - rollbackElementsMet=`RollbackDryRunPassed, StateRestorationProvenInTest, RollbackOperatorAccessPathPresent`
  - rollbackElementsMissing=`PreApplicationSnapshotPresent, RollbackPlaybookPresent`
  - rollbackNotActivated=`True` applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 2 rollback element(s) missing; rollback readiness incomplete.

V8.14 rollback readiness matrix。上游 application Ready 才评估；5 个回滚要素（snapshot / playbook / dry-run / restoration / operator-access）任一缺失即 Incomplete；全满足即 RollbackReady。RollbackReady 仍意味着回滚 path 没被执行，应用也没被执行。
