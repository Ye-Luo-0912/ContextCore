# Vector Provider Configuration Sanity Audit

Generated: 2026-06-15T07:00:41.4206314+00:00

- Passed: `True`
- ProviderComparison: `Conclusive`
- ExpectedProviderType: `OnnxLocal`
- ExpectedProviderId: `qwen3-embedding-0.6b-onnx`
- ExpectedModelId: `qwen3-embedding-0.6b`
- ExpectedPathSegment: `qwen3-embedding-0.6b-onnx`
- ExpectedDimension: `1024`
- ExpectedUseForRuntime: `False`
- Recommendation: `ReadyForProviderComparisonFreeze`

| Report | Passed | ProviderType | ProviderId | ModelId | Dimension | Runtime | Mismatches |
|---|---|---|---|---|---:|---|---|
| embedding-provider-smoke | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| vector-provider-comparison | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| vector-qwen3-shadow-eval-a3 | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| vector-qwen3-shadow-eval-extended | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| vector-query-profile-sweep-a3 | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| vector-query-profile-sweep-extended | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| vector-qwen3-readiness-gate | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| postgres-vector-query-preview | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |
| postgres-vector-shadow-eval-summary | True | OnnxLocal | qwen3-embedding-0.6b-onnx | qwen3-embedding-0.6b | 1024 | False |  |

## BlockedReasons
- none

## Diagnostics
- `ProviderType=OnnxLocal`
- `UseForRuntime=false`
- `Qwen3ProviderScopeMatched`
