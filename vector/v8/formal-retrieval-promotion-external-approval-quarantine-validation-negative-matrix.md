# Quarantine Negative Matrix

生成: `2026-06-26T16:50:10.8007558+00:00`

## Decision
- MatrixPassed: `True` GatePassed: `False`
- Total: `8` Passed: `8` Failed: `0`

## Negative Cases
- `EvidenceMissingField`: expected=`EvidenceCandidateSchemaInvalid` failedAsExpected=`True` actual=`EvidenceCandidateSchemaInvalid`
- `EvidenceEmptyScopes`: expected=`EvidenceCandidateSchemaInvalid` failedAsExpected=`True` actual=`EvidenceCandidateSchemaInvalid`
- `EvidenceDefaultTime`: expected=`EvidenceCandidateSchemaInvalid` failedAsExpected=`True` actual=`EvidenceCandidateSchemaInvalid`
- `RegistryMissingRecords`: expected=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True` actual=`TrustRegistryCandidateSchemaInvalid`
- `RegistryEmptySourceKinds`: expected=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True` actual=`TrustRegistryCandidateSchemaInvalid`
- `RecordMissingChecksum`: expected=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True` actual=`TrustRegistryCandidateSchemaInvalid`
- `RecordMissingProvidedBy`: expected=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True` actual=`TrustRegistryCandidateSchemaInvalid`
- `RecordDefaultValidUntil`: expected=`TrustRegistryCandidateSchemaInvalid` failedAsExpected=`True` actual=`TrustRegistryCandidateSchemaInvalid`

V8.9 quarantine negative matrix。
