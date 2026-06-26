# Policy Authority Matrix

生成: `2026-06-26T18:31:06.5239783+00:00`
操作: `frp-policy-authority-matrix-a1701f8730124142834ee87113178150`

## Decision
- PolicyAuthorityMatrixPassed: `True` GatePassed: `False`
- Total: `6` Passed: `6` Failed: `0`
- Effect breakdown — Grant: `2` Deny: `3` Indeterminate: `1`

## No-Manual-Review / No-Application Contract
- ManualReviewRequired: `False`
- ApprovalSealed: `False`
- CapabilityGrantWritten: `False`
- GrantApplied: `False`  (Grant decision is not Grant application)
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

## Policy Authority Cases
- `TrustChainBrokenInput`: passedAsExpected=`True`
  - request: capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - effect expected=`Deny` actual=`Deny` matched=`True`
  - status expected=`PolicyAuthorityUnreachable` actual=`PolicyAuthorityUnreachable` matched=`True`
  - rule expected=`NoTrustChain` actual=`NoTrustChain` matched=`True`
  - resolved expected=`False` actual=`False` matched=`True`
  - appliedTrustMode=`` grantNotApplied=`True`
  - reasoning: trust chain not validated; policy authority unreachable.
- `FixtureTrustModeBlocksGrant`: passedAsExpected=`True`
  - request: capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - effect expected=`Deny` actual=`Deny` matched=`True`
  - status expected=`PolicyAuthorityResolved` actual=`PolicyAuthorityResolved` matched=`True`
  - rule expected=`FixtureTrustModeCannotAuthorizeProduction` actual=`FixtureTrustModeCannotAuthorizeProduction` matched=`True`
  - resolved expected=`True` actual=`True` matched=`True`
  - appliedTrustMode=`fixture-dry-run` grantNotApplied=`True`
  - reasoning: trust mode 'fixture-dry-run' is fixture/preview; cannot authorize production capability.
- `ScopeOutOfAuthority`: passedAsExpected=`True`
  - request: capability=`FormalRetrievalActivation` scope=`out-of-bounds/illegal-collection`
  - effect expected=`Deny` actual=`Deny` matched=`True`
  - status expected=`PolicyAuthorityResolved` actual=`PolicyAuthorityResolved` matched=`True`
  - rule expected=`ScopeOutOfAuthority` actual=`ScopeOutOfAuthority` matched=`True`
  - resolved expected=`True` actual=`True` matched=`True`
  - appliedTrustMode=`production-signed` grantNotApplied=`True`
  - reasoning: requested scope 'out-of-bounds/illegal-collection' not in record allowed scopes.
- `CapabilityNotInPolicyAuthority`: passedAsExpected=`True`
  - request: capability=`UnknownExperimentalCapability` scope=`demo-workspace/demo-collection`
  - effect expected=`Indeterminate` actual=`Indeterminate` matched=`True`
  - status expected=`PolicyAuthorityResolved` actual=`PolicyAuthorityResolved` matched=`True`
  - rule expected=`CapabilityNotInPolicyAuthority` actual=`CapabilityNotInPolicyAuthority` matched=`True`
  - resolved expected=`True` actual=`True` matched=`True`
  - appliedTrustMode=`production-signed` grantNotApplied=`True`
  - reasoning: requested capability 'UnknownExperimentalCapability' not enumerated by policy; outcome indeterminate.
- `CleanProductionGrant`: passedAsExpected=`True`
  - request: capability=`FormalRetrievalActivation` scope=`demo-workspace/demo-collection`
  - effect expected=`Grant` actual=`Grant` matched=`True`
  - status expected=`PolicyAuthorityResolved` actual=`PolicyAuthorityResolved` matched=`True`
  - rule expected=`AuthorizedByPolicy` actual=`AuthorizedByPolicy` matched=`True`
  - resolved expected=`True` actual=`True` matched=`True`
  - appliedTrustMode=`production-signed` grantNotApplied=`True`
  - reasoning: trust mode 'production-signed' authorizes 'FormalRetrievalActivation' in scope 'demo-workspace/demo-collection'. Decision only; not applied.
- `CleanProductionGrant_MainlineEvidenceWrite`: passedAsExpected=`True`
  - request: capability=`MainlineEvidenceWrite` scope=`demo-workspace/demo-collection`
  - effect expected=`Grant` actual=`Grant` matched=`True`
  - status expected=`PolicyAuthorityResolved` actual=`PolicyAuthorityResolved` matched=`True`
  - rule expected=`AuthorizedByPolicy` actual=`AuthorizedByPolicy` matched=`True`
  - resolved expected=`True` actual=`True` matched=`True`
  - appliedTrustMode=`production-signed` grantNotApplied=`True`
  - reasoning: trust mode 'production-signed' authorizes 'MainlineEvidenceWrite' in scope 'demo-workspace/demo-collection'. Decision only; not applied.

V8.12 policy authority matrix。trust-chain-validated candidate 经规则栈（NoTrustChain / FixtureTrustModeCannotAuthorizeProduction / ScopeOutOfAuthority / CapabilityNotInPolicyAuthority / AuthorizedByPolicy）得到机器决策；Grant 决策 ≠ Grant 应用；GrantApplied 永远 false；不写 mainline、不 seal、不进 manual review。
