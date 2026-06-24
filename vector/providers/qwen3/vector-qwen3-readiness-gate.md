# Vector Qwen3 Readiness Gate

Generated: 2026-06-14T18:12:16.8906366+00:00

- Passed: `False`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ProviderType: `OnnxLocal`
- ModelId: `qwen3-embedding-0.6b`
- ModelPath: `$(repo-root)\src\ContextCore.Embedding\Models\qwen3-embedding-0.6b-onnx\model_int8.onnx`
- TokenizerPath: `$(repo-root)\src\ContextCore.Embedding\Models\qwen3-embedding-0.6b-onnx\tokenizer.json`
- Dimension: `1024`
- UseForRuntime: `False`
- ProviderCompatibilityPassed: `True`
- A3RecallAfterPolicy: `54.55%`
- ExtendedRecallAfterPolicy: `76.88%`
- RiskAfterPolicy: `2`
- MustNotHitRiskAfterPolicy: `1.75%`
- LifecycleRiskAfterPolicy: `0.00%`
- FormalOutputChanged: `0`
- ProjectionMismatchCount: `999`
- PgVectorFileSystemParityPassed: `False`
- P15GatePassed: `True`
- FormalRetrievalAllowed: `False`
- Recommendation: `BlockedByRisk`

## Blocked Reasons

- A3RecallBelow80Percent
- ExtendedRecallBelow80Percent
- RiskAfterPolicyNonZero
- MustNotHitRiskAfterPolicyNonZero
- ProjectionMismatchNonZero
- PgVectorFileSystemParityNotPassed

## Diagnostics

- none
