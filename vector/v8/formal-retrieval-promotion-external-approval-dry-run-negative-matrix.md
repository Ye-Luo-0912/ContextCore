# Dry-Run Negative Matrix

生成: `2026-06-26T14:08:09.4611658+00:00`
操作: `frp-neg-matrix-db0113bac89842b0aeefa165f94a5c3e`

## Decision
- MatrixPassed: `True` GatePassed: `False`
- Total: `9` Passed: `9` Failed: `0`

## Negative Cases
- `SourceKindMismatch`: expected=`FixtureSourceKindMismatch` failedAsExpected=`True` actual=`FixtureSourceKindMismatch`
- `ProvidedByMismatch`: expected=`FixtureProvidedByMismatch` failedAsExpected=`True` actual=`FixtureProvidedByMismatch`
- `TrustRecordExpired`: expected=`FixtureTrustRecordExpired` failedAsExpected=`True` actual=`FixtureTrustRecordExpired`
- `ChecksumMismatch`: expected=`FixtureChecksumMismatch` failedAsExpected=`True` actual=`FixtureChecksumMismatch`
- `RecordApprovalRequestMismatch`: expected=`FixtureTrustRecordApprovalRequestMismatch` failedAsExpected=`True` actual=`FixtureApprovalRequestBindingFailed, FixtureTrustRecordApprovalRequestMismatch`
- `RecordBoundGateMismatch`: expected=`FixtureTrustRecordBoundGateMismatch` failedAsExpected=`True` actual=`FixtureApprovalRequestBindingFailed, FixtureTrustRecordBoundGateMismatch`
- `SourceGateIdsMismatch`: expected=`FixtureSourceGateIdsMismatch` failedAsExpected=`True` actual=`FixtureSourceGateIdsMismatch`
- `MainlineEvidencePresent`: expected=`MainlineEvidencePresent` failedAsExpected=`True` actual=`MainlineEvidencePresent`
- `MainlineTrustRegistryPresent`: expected=`MainlineTrustRegistryPresent` failedAsExpected=`True` actual=`MainlineTrustRegistryPresent`

V8.7 dry-run negative matrix。
