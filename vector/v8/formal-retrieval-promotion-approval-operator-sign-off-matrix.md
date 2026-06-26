# Operator Sign-Off Matrix

生成: `2026-06-26T18:59:09.9760877+00:00`
操作: `frp-operator-sign-off-matrix-d25d5eca798a4707ba64ddadd9c470fb`

## Decision
- OperatorSignOffMatrixPassed: `True` GatePassed: `False`
- Total: `9` Passed: `9` Failed: `0`
- Status — NotApplicable: `2` Insufficient: `6` Recorded: `1`

## No-Crossover Contract
- ManualReviewRequired: `False`
- ApprovalSealed: `False`
- CapabilityGrantWritten: `False`
- GrantApplied: `False`
- ApplicationApplied: `False`
- RollbackActivated: `False`
- Crossed: `False`  (Recorded != Crossed — sign-off being recorded does not cross the application boundary)
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

## Operator Sign-Off Cases
- `NotApplicableFromApplicationNotReady`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationBlocked` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffNotApplicable` actual=`OperatorSignOffNotApplicable` matched=`True`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: application status 'GrantApplicationBlocked' + rollback status 'RollbackReady' do not jointly satisfy ApplicationReady && RollbackReady; sign-off not evaluated.
- `NotApplicableFromRollbackNotReady`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReadinessIncomplete` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffNotApplicable` actual=`OperatorSignOffNotApplicable` matched=`True`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: application status 'GrantApplicationReady' + rollback status 'RollbackReadinessIncomplete' do not jointly satisfy ApplicationReady && RollbackReady; sign-off not evaluated.
- `InsufficientMissingIdentity`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffInsufficient` actual=`OperatorSignOffInsufficient` matched=`True`
  - expectedMissing=`OperatorIdentityPresent` matched=`True`
  - elementsMet=`OperatorAuthorityProofPresent, SignOffIntentAffirmative, SignOffTimestampWithinValidityWindow, SignOffCryptographicSealValid`
  - elementsMissing=`OperatorIdentityPresent`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: 1 sign-off credential element(s) missing; sign-off insufficient.
- `InsufficientMissingAuthority`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffInsufficient` actual=`OperatorSignOffInsufficient` matched=`True`
  - expectedMissing=`OperatorAuthorityProofPresent` matched=`True`
  - elementsMet=`OperatorIdentityPresent, SignOffIntentAffirmative, SignOffTimestampWithinValidityWindow, SignOffCryptographicSealValid`
  - elementsMissing=`OperatorAuthorityProofPresent`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: 1 sign-off credential element(s) missing; sign-off insufficient.
- `InsufficientMissingIntent`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffInsufficient` actual=`OperatorSignOffInsufficient` matched=`True`
  - expectedMissing=`SignOffIntentAffirmative` matched=`True`
  - elementsMet=`OperatorIdentityPresent, OperatorAuthorityProofPresent, SignOffTimestampWithinValidityWindow, SignOffCryptographicSealValid`
  - elementsMissing=`SignOffIntentAffirmative`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: 1 sign-off credential element(s) missing; sign-off insufficient.
- `InsufficientMissingTimestamp`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffInsufficient` actual=`OperatorSignOffInsufficient` matched=`True`
  - expectedMissing=`SignOffTimestampWithinValidityWindow` matched=`True`
  - elementsMet=`OperatorIdentityPresent, OperatorAuthorityProofPresent, SignOffIntentAffirmative, SignOffCryptographicSealValid`
  - elementsMissing=`SignOffTimestampWithinValidityWindow`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: 1 sign-off credential element(s) missing; sign-off insufficient.
- `InsufficientMissingSeal`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffInsufficient` actual=`OperatorSignOffInsufficient` matched=`True`
  - expectedMissing=`SignOffCryptographicSealValid` matched=`True`
  - elementsMet=`OperatorIdentityPresent, OperatorAuthorityProofPresent, SignOffIntentAffirmative, SignOffTimestampWithinValidityWindow`
  - elementsMissing=`SignOffCryptographicSealValid`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: 1 sign-off credential element(s) missing; sign-off insufficient.
- `RecordedButNoCrossover`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffRecorded` actual=`OperatorSignOffRecorded` matched=`True`
  - elementsMet=`OperatorIdentityPresent, OperatorAuthorityProofPresent, SignOffIntentAffirmative, SignOffTimestampWithinValidityWindow, SignOffCryptographicSealValid`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: all sign-off credential elements present; sign-off Recorded. Note: Recorded != Crossed. The application boundary remains uncrossed; this matrix does not execute application, does not activate rollback path.
- `InsufficientMultipleMissing`: passedAsExpected=`True`
  - inputs: application=`GrantApplicationReady` rollback=`RollbackReady` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`OperatorSignOffInsufficient` actual=`OperatorSignOffInsufficient` matched=`True`
  - expectedMissing=`OperatorIdentityPresent` matched=`True`
  - elementsMet=`SignOffIntentAffirmative, SignOffTimestampWithinValidityWindow, SignOffCryptographicSealValid`
  - elementsMissing=`OperatorIdentityPresent, OperatorAuthorityProofPresent`
  - notCrossed=`True` applicationNotApplied=`True` rollbackNotActivated=`True` countShapeOk=`True`
  - reasoning: 2 sign-off credential element(s) missing; sign-off insufficient.

V8.15 explicit operator sign-off matrix。上游必须 ApplicationReady && RollbackReady；5 个凭据结构要素（identity / authority / intent / timestamp / seal）任一缺失即 Insufficient；全满足即 Recorded。Recorded ≠ Crossed — sign-off 被记录不代表应用边界被跨过；矩阵不触发应用、不激活回滚、不跨过任何边界。
