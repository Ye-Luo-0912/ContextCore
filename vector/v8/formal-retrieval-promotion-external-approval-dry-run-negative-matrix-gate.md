# Dry-Run Negative Matrix Gate

生成: `2026-06-26T13:46:42.8675535+00:00`
操作: `frp-neg-matrix-50737a11932242559649b982765357f5`

## Decision
- MatrixPassed: `True` GatePassed: `True`
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
