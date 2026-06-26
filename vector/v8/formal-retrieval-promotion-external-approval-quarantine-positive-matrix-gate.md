# Quarantine Positive Matrix Gate

生成: `2026-06-26T18:08:37.7594389+00:00`
操作: `frp-q-pos-matrix-a1e76b0a270940deac692d301ea68b8a`

## Decision
- PositiveMatrixPassed: `True` GatePassed: `True`
- Total: `6` Passed: `6` Failed: `0`

## No-Manual-Review Contract
- ManualReviewRequired: `False`
- ApprovalSealed: `False`
- CapabilityGrantWritten: `False`
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

## Positive Cases
- `ValidEvidenceCandidate`: passedAsExpected=`True`
  - evidence: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - registry: status expected=`Missing` actual=`Missing` matched=`True` candidateValid=`False` schemaValid=`False`
  - noMissingFields=`True` noInvalidFields=`True`
  - notApproved=`True` notSealed=`True` notPromoted=`True`
- `ValidTrustRegistryCandidate`: passedAsExpected=`True`
  - evidence: status expected=`Missing` actual=`Missing` matched=`True` candidateValid=`False` schemaValid=`False`
  - registry: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - noMissingFields=`True` noInvalidFields=`True`
  - notApproved=`True` notSealed=`True` notPromoted=`True`
- `ValidEvidenceAndRegistryPair`: passedAsExpected=`True`
  - evidence: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - registry: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - noMissingFields=`True` noInvalidFields=`True`
  - notApproved=`True` notSealed=`True` notPromoted=`True`
- `ValidMultiRecordTrustRegistry`: passedAsExpected=`True`
  - evidence: status expected=`Missing` actual=`Missing` matched=`True` candidateValid=`False` schemaValid=`False`
  - registry: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - noMissingFields=`True` noInvalidFields=`True`
  - notApproved=`True` notSealed=`True` notPromoted=`True`
- `ValidEvidenceWithMultiRecordRegistry`: passedAsExpected=`True`
  - evidence: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - registry: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - noMissingFields=`True` noInvalidFields=`True`
  - notApproved=`True` notSealed=`True` notPromoted=`True`
- `ValidRegistryWithFarFutureValidUntil`: passedAsExpected=`True`
  - evidence: status expected=`Missing` actual=`Missing` matched=`True` candidateValid=`False` schemaValid=`False`
  - registry: status expected=`MachineValidatedCandidate` actual=`MachineValidatedCandidate` matched=`True` candidateValid=`True` schemaValid=`True`
  - noMissingFields=`True` noInvalidFields=`True`
  - notApproved=`True` notSealed=`True` notPromoted=`True`

V8.10 quarantine positive matrix。结构合法 candidate 走机器校验得 MachineValidatedCandidate；机器校验不是 approved、不要求人工审查；下一步走 TrustChainValidation / PolicyAuthorityModel，不进入 manual review；不写 mainline、不 seal、不启用 formal retrieval。
