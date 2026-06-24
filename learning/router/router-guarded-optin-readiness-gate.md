# Router Guarded Opt-in Readiness Gate

Generated: 2026-06-11T09:18:00.4971204+00:00
PolicyVersion: `router-guarded-optin-gate-r2.f/v1`

## Summary

- Passed: `False`
- Recommendation: `KeepRuleBased`
- Fixes: `1`
- Breaks: `3`
- NetGain: `-2`
- PerIntentRegressionCount: `3`
- AgreementRate: `88.57 %`
- AgreementRateThreshold: `85.00 %`
- LowConfidenceCount: `0`
- LowConfidenceMaxCount: `0`
- P15GatePassed: `True`

## Failure Reasons

- `ShadowBreaksRuntimeGreaterThanFixes`
- `ShadowBreaksRuntimeNonZero`
- `NetGainNotPositive`
- `PerIntentRegressionNonZero`

## Runtime Safety

- This gate is offline/read-only.
- It does not replace the runtime router.
- It does not change retrieval, planning, PackingPolicy, scoring, or package output.
- Reports with the same input dataset path are counted once to avoid duplicated freeze metrics.
