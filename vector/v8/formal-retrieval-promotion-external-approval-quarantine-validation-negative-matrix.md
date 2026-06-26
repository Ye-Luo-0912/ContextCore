# Quarantine Negative Matrix

生成: `2026-06-26T17:07:36.8257479+00:00`
操作: `frp-q-neg-matrix-be12857d214646be96350b957495e535`

## Decision
- MatrixPassed: `True` GatePassed: `False`
- Total: `8` Passed: `8` Failed: `0`
- PromotionToMainlinePerformed: `False`
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

## Negative Cases
- `EvidenceMissingField`: expectedReason=`EvidenceCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`ApprovalId` matched=`True`
  - expectedInvalid=`` matched=`True`
  - actualBlocked=`EvidenceCandidateSchemaInvalid`
  - actualMissing=`ApprovalId`
  - actualInvalid=``
- `EvidenceEmptyScopes`: expectedReason=`EvidenceCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`` matched=`True`
  - expectedInvalid=`ApprovalScopes` matched=`True`
  - actualBlocked=`EvidenceCandidateSchemaInvalid`
  - actualMissing=``
  - actualInvalid=`ApprovalScopes`
- `EvidenceDefaultTime`: expectedReason=`EvidenceCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`` matched=`True`
  - expectedInvalid=`ApprovalTimestamp` matched=`True`
  - actualBlocked=`EvidenceCandidateSchemaInvalid`
  - actualMissing=``
  - actualInvalid=`ApprovalTimestamp`
- `RegistryMissingRecords`: expectedReason=`TrustRegistryCandidateInvalid` failedAsExpected=`True`
  - expectedMissing=`TrustedProvenanceRecords` matched=`True`
  - expectedInvalid=`` matched=`True`
  - actualBlocked=`TrustRegistryCandidateInvalid`
  - actualMissing=`TrustedProvenanceRecords`
  - actualInvalid=``
- `RegistryEmptySourceKinds`: expectedReason=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`` matched=`True`
  - expectedInvalid=`AllowedSourceKinds` matched=`True`
  - actualBlocked=`TrustRegistryCandidateSchemaInvalid`
  - actualMissing=``
  - actualInvalid=`AllowedSourceKinds`
- `RecordMissingChecksum`: expectedReason=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`TrustedProvenanceRecords[0].ApprovalEvidenceChecksum` matched=`True`
  - expectedInvalid=`` matched=`True`
  - actualBlocked=`TrustRegistryCandidateSchemaInvalid`
  - actualMissing=`TrustedProvenanceRecords[0].ApprovalEvidenceChecksum`
  - actualInvalid=``
- `RecordMissingProvidedBy`: expectedReason=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`TrustedProvenanceRecords[1].ApprovalEvidenceProvidedBy` matched=`True`
  - expectedInvalid=`` matched=`True`
  - actualBlocked=`TrustRegistryCandidateSchemaInvalid`
  - actualMissing=`TrustedProvenanceRecords[1].ApprovalEvidenceProvidedBy`
  - actualInvalid=``
- `RecordDefaultValidUntil`: expectedReason=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True`
  - expectedMissing=`` matched=`True`
  - expectedInvalid=`TrustedProvenanceRecords[1].ValidUntil` matched=`True`
  - actualBlocked=`TrustRegistryCandidateSchemaInvalid`
  - actualMissing=``
  - actualInvalid=`TrustedProvenanceRecords[1].ValidUntil`

V8.9R quarantine negative matrix。真实 candidate JSON + 字段定位校验。
