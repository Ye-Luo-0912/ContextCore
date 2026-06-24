# Vector Postgres Provider Freeze Gate

Generated: 2026-06-14T15:58:20.3375403+00:00

- Passed: `True`
- VectorPostgresProvider: `ReadyForPreviewShadowStorage`
- DefaultVectorStore: `unchanged`
- UseForRuntime: `False`
- FormalRetrievalAllowed: `False`
- DiagnosticsReady: `True`
- CompatibilityReady: `True`
- ParityPassed: `True`
- ReindexQualityPassed: `True`
- QueryPreviewPassed: `True`
- ShadowEvalPassed: `True`
- A3RecallDelta: `0`
- ExtendedRecallDelta: `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0`
- LifecycleRiskAfterPolicy: `0`
- FormalOutputChanged: `0`
- ProjectionMismatchCount: `0`
- P15GatePassed: `True`
- Recommendation: `ReadyForPreviewShadowStorage`

## Allowed
- `preview`
- `shadow`
- `eval`

## Required
- `V4 readiness gate before formal retrieval`

## Forbidden
- `FormalRetrievalSwitchWithoutV4Gate`
- `PackingPolicyIntegrationWithoutV4Gate`
- `PackageOutputIntegrationWithoutV4Gate`

## BlockedReasons
- none

## Diagnostics
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- `PreviewShadowEvalOnly`
- `VectorRetrievalStillBlockedByA3Recall`
