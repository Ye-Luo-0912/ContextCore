# Grant Application Matrix Gate

生成: `2026-06-26T19:22:58.1097016+00:00`
操作: `frp-grant-application-matrix-e0098e566ebb472eb689be6cb7ec13e1`

## Decision
- GrantApplicationMatrixPassed: `True` GatePassed: `True`
- Total: `9` Passed: `9` Failed: `0`
- Status — NotApplicable: `2` Blocked: `6` Ready: `1`

## No-Application Contract
- ManualReviewRequired: `False`
- ApprovalSealed: `False`
- CapabilityGrantWritten: `False`
- GrantApplied: `False`
- ApplicationApplied: `False`  (Ready != Applied)
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

## Grant Application Cases
- `DenyInputNotApplicable`: passedAsExpected=`True`
  - input: effect=`Deny` rule=`FixtureTrustModeCannotAuthorizeProduction` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationNotApplicable` actual=`GrantApplicationNotApplicable` matched=`True`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: policy effect 'Deny' is not Grant; application path not entered.
- `IndeterminateInputNotApplicable`: passedAsExpected=`True`
  - input: effect=`Indeterminate` rule=`CapabilityNotInPolicyAuthority` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationNotApplicable` actual=`GrantApplicationNotApplicable` matched=`True`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: policy effect 'Indeterminate' is not Grant; application path not entered.
- `GrantMissingApprovalSeal`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationBlocked` actual=`GrantApplicationBlocked` matched=`True`
  - expectedMissing=`ApprovalSealArtifactPresent` matched=`True`
  - met=`DryRunCleanArtifactPresent, AuditLogArtifactPresent, RuntimeChangeReadinessGatePassed, TrustChainReverificationGatePassed`
  - missing=`ApprovalSealArtifactPresent`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 precondition(s) missing; application blocked.
- `GrantMissingDryRunArtifact`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationBlocked` actual=`GrantApplicationBlocked` matched=`True`
  - expectedMissing=`DryRunCleanArtifactPresent` matched=`True`
  - met=`ApprovalSealArtifactPresent, AuditLogArtifactPresent, RuntimeChangeReadinessGatePassed, TrustChainReverificationGatePassed`
  - missing=`DryRunCleanArtifactPresent`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 precondition(s) missing; application blocked.
- `GrantMissingAuditLog`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationBlocked` actual=`GrantApplicationBlocked` matched=`True`
  - expectedMissing=`AuditLogArtifactPresent` matched=`True`
  - met=`ApprovalSealArtifactPresent, DryRunCleanArtifactPresent, RuntimeChangeReadinessGatePassed, TrustChainReverificationGatePassed`
  - missing=`AuditLogArtifactPresent`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 precondition(s) missing; application blocked.
- `GrantMissingRuntimeGate`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationBlocked` actual=`GrantApplicationBlocked` matched=`True`
  - expectedMissing=`RuntimeChangeReadinessGatePassed` matched=`True`
  - met=`ApprovalSealArtifactPresent, DryRunCleanArtifactPresent, AuditLogArtifactPresent, TrustChainReverificationGatePassed`
  - missing=`RuntimeChangeReadinessGatePassed`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 precondition(s) missing; application blocked.
- `GrantMissingTrustChainReverification`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationBlocked` actual=`GrantApplicationBlocked` matched=`True`
  - expectedMissing=`TrustChainReverificationGatePassed` matched=`True`
  - met=`ApprovalSealArtifactPresent, DryRunCleanArtifactPresent, AuditLogArtifactPresent, RuntimeChangeReadinessGatePassed`
  - missing=`TrustChainReverificationGatePassed`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 1 precondition(s) missing; application blocked.
- `GrantAllPreconditionsMet_Ready_ButNotApplied`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationReady` actual=`GrantApplicationReady` matched=`True`
  - met=`ApprovalSealArtifactPresent, DryRunCleanArtifactPresent, AuditLogArtifactPresent, RuntimeChangeReadinessGatePassed, TrustChainReverificationGatePassed`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: all preconditions met; application Ready. Ready != Applied — application is a separate write-out path not executed here.
- `GrantMultiplePreconditionsMissing`: passedAsExpected=`True`
  - input: effect=`Grant` rule=`AuthorizedByPolicy` capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - status expected=`GrantApplicationBlocked` actual=`GrantApplicationBlocked` matched=`True`
  - expectedMissing=`ApprovalSealArtifactPresent` matched=`True`
  - met=`AuditLogArtifactPresent, RuntimeChangeReadinessGatePassed, TrustChainReverificationGatePassed`
  - missing=`ApprovalSealArtifactPresent, DryRunCleanArtifactPresent`
  - applicationNotApplied=`True` countShapeOk=`True`
  - reasoning: 2 precondition(s) missing; application blocked.

V8.13 grant application matrix。policy decision + 5 个 artifact-level precondition → NotApplicable / Blocked / Ready。Ready != Applied — ApplicationApplied 永远 false；不写 grant、不激活、不进 manual review。
