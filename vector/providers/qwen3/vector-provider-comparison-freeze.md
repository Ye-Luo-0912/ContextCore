# Embedding Provider Comparison Freeze (V3.10.F)

Generated: 2026-06-15T07:00:41.6357367+00:00

- Passed: `False`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ModelId: `qwen3-embedding-0.6b`
- ProviderComparison: `Conclusive`
- ProviderConfigurationSanityPassed: `True`
- ProviderConfigurationSanityAuditPath: `vector\providers\qwen3\vector-provider-configuration-sanity-audit.json`
- ReadinessGatePassed: `False`
- A3RecallAfterPolicy: `54.55%`
- ExtendedRecallAfterPolicy: `76.88%`
- RiskAfterPolicy: `2`
- FormalOutputChanged: `0`
- PromotionStatus: `DoNotPromote`
- VectorV4RecheckAllowed: `False`
- FormalRetrievalAllowed: `False`
- VectorRetrievalStatus: `PreviewOnly`
- P15GatePassed: `True`
- Recommendation: `BlockedByRisk`

## Allowed
- `preview`
- `shadow`
- `eval`

## Forbidden
- `FormalRetrievalSwitch`
- `PgVectorFormalRetrievalSwitch`
- `FormalIVectorIndexStoreBinding`
- `PackingPolicyIntegration`
- `PackageOutputIntegration`

## BlockedReasons
- `ReadinessGateNotPassed`
- `RiskAfterPolicyNonZero`
- `A3RecallBelow80Percent`
- `ExtendedRecallBelow80Percent`

## Diagnostics
- `EmbeddingProviderComparisonFreezeBlocked`
