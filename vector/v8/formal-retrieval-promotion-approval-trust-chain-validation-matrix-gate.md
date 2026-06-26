# Trust Chain Validation Matrix Gate

生成: `2026-06-26T18:23:54.8666207+00:00`
操作: `frp-trust-chain-matrix-4e10c6ef00cf4a928031c10b272f8cc7`

## Decision
- ChainValidationPassed: `True` GatePassed: `True`
- Total: `11` Positive: `1` Negative: `10`
- Passed: `11` Failed: `0`

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

## Trust Chain Cases
- `ChainComplete`: passedAsExpected=`True`
  - status expected=`TrustChainValidated` actual=`TrustChainValidated` matched=`True`
  - chainComplete expected=`True` actual=`True` matched=`True`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `ProvenanceIdNotInRegistry`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceProvenanceNotFoundInRegistry` reasonMatched=`True`
  - expectedField=`ApprovalEvidenceProvenanceId` fieldMatched=`True`
  - actualReasons=`EvidenceProvenanceNotFoundInRegistry`
  - actualFields=`ApprovalEvidenceProvenanceId`
- `SourceKindMismatchWithRecord`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceSourceKindMismatch` reasonMatched=`True`
  - expectedField=`ApprovalEvidenceSourceKind` fieldMatched=`True`
  - actualReasons=`EvidenceSourceKindMismatch`
  - actualFields=`ApprovalEvidenceSourceKind`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `SourceKindNotInAllowedKinds`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceSourceKindNotAllowed` reasonMatched=`True`
  - expectedField=`ApprovalEvidenceSourceKind` fieldMatched=`True`
  - actualReasons=`EvidenceSourceKindNotAllowed`
  - actualFields=`ApprovalEvidenceSourceKind`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `ChecksumMismatch`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceChecksumMismatch` reasonMatched=`True`
  - expectedField=`ApprovalEvidenceChecksum` fieldMatched=`True`
  - actualReasons=`EvidenceChecksumMismatch`
  - actualFields=`ApprovalEvidenceChecksum`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `ProvidedByMismatch`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceProvidedByMismatch` reasonMatched=`True`
  - expectedField=`ApprovalEvidenceProvidedBy` fieldMatched=`True`
  - actualReasons=`EvidenceProvidedByMismatch`
  - actualFields=`ApprovalEvidenceProvidedBy`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `SourceApprovalRequestIdMismatch`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceSourceApprovalRequestIdMismatch` reasonMatched=`True`
  - expectedField=`SourceApprovalRequestId` fieldMatched=`True`
  - actualReasons=`EvidenceSourceApprovalRequestIdMismatch`
  - actualFields=`SourceApprovalRequestId`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `BoundPendingApprovalGateOperationIdMismatch`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceBoundPendingApprovalGateOperationIdMismatch` reasonMatched=`True`
  - expectedField=`BoundPendingApprovalGateOperationId` fieldMatched=`True`
  - actualReasons=`EvidenceBoundPendingApprovalGateOperationIdMismatch`
  - actualFields=`BoundPendingApprovalGateOperationId`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `TrustModeMismatch`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceTrustModeMismatch` reasonMatched=`True`
  - expectedField=`ApprovalEvidenceTrustMode` fieldMatched=`True`
  - actualReasons=`EvidenceTrustModeMismatch`
  - actualFields=`ApprovalEvidenceTrustMode`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `ApprovalScopesNotSubsetOfRecord`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceApprovalScopesNotSubsetOfRecord` reasonMatched=`True`
  - expectedField=`ApprovalScopes` fieldMatched=`True`
  - actualReasons=`EvidenceApprovalScopesNotSubsetOfRecord`
  - actualFields=`ApprovalScopes`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`
- `ApprovalTimestampAfterValidUntil`: passedAsExpected=`True`
  - status expected=`TrustChainBroken` actual=`TrustChainBroken` matched=`True`
  - chainComplete expected=`False` actual=`False` matched=`True`
  - expectedReason=`EvidenceApprovalTimestampAfterRecordValidUntil` reasonMatched=`True`
  - expectedField=`ApprovalTimestamp` fieldMatched=`True`
  - actualReasons=`EvidenceApprovalTimestampAfterRecordValidUntil`
  - actualFields=`ApprovalTimestamp`
  - matchedProvenanceId=`fixture-provenance-trustchain-001` matchedRecordIndex=`0`

V8.11 trust chain validation matrix。evidence 与 trust registry 跨字段链路校验；机器判定不是 approved，不要求人工审查；下一阶段走 PolicyAuthorityModel；不写 mainline、不 seal、不启用 formal retrieval。
