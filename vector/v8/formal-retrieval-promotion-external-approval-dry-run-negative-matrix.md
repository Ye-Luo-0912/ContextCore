# Dry-Run Negative Matrix

生成: `2026-06-26T13:46:13.3654058+00:00`
操作: `frp-neg-matrix-8dc68c18da8b4b2e83cffd9d668dbbf2`

## Decision
- MatrixPassed: `True` GatePassed: `False`
- Total: `7` Passed: `7` Failed: `0`

## Negative Cases
- `SourceKindMismatch`: expected=`FixtureSourceKindMismatch` failedAsExpected=`True` actual=`FixtureSourceKindMismatch`
- `ProvidedByMismatch`: expected=`FixtureProvidedByMismatch` failedAsExpected=`True` actual=`FixtureProvidedByMismatch`
- `TrustRecordExpired`: expected=`FixtureTrustRecordExpired` failedAsExpected=`True` actual=`FixtureTrustRecordExpired`
- `ChecksumMismatch`: expected=`FixtureChecksumMismatch` failedAsExpected=`True` actual=`FixtureChecksumMismatch`
- `RecordApprovalRequestMismatch`: expected=`FixtureTrustRecordApprovalRequestMismatch` failedAsExpected=`True` actual=`FixtureApprovalRequestBindingFailed, FixtureTrustRecordApprovalRequestMismatch`
- `RecordBoundGateMismatch`: expected=`FixtureTrustRecordBoundGateMismatch` failedAsExpected=`True` actual=`FixtureApprovalRequestBindingFailed, FixtureTrustRecordBoundGateMismatch`
- `SourceGateIdsMismatch`: expected=`FixtureSourceGateIdsMismatch` failedAsExpected=`True` actual=`FixtureSourceGateIdsMismatch`

V8.7 dry-run negative matrix。
