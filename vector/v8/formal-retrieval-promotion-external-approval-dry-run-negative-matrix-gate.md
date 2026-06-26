# Dry-Run Negative Matrix Gate

生成: `2026-06-26T14:08:28.8560116+00:00`
操作: `frp-neg-matrix-7088fa47a76d4fa787bfa420f027b999`

## Decision
- MatrixPassed: `True` GatePassed: `True`
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
