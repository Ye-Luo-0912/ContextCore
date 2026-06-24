# Vector Provider Comparison V3.10

Generated: 2026-06-14T18:12:15.9665057+00:00

- Recommendation: `BlockedByRisk`

| Provider | Type | Model | Dim | Runtime | Indexed | A3 Recall | A3 MRR | A3 Risk | Extended Recall | Extended MRR | Extended Risk | PgVector Parity | Recommendation |
|---|---|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---|---|
| current |  | current | 0 | False | 158 | 71.21% | 0.6765 | 0 | 84.38% | 0.8229 | 0 | True | BlockedByA3Recall |
| qwen3-embedding-0.6b-onnx | OnnxLocal | qwen3-embedding-0.6b | 1024 | False | 158 | 54.55% | 0.6262 | 1 | 76.88% | 0.8058 | 1 | False | BlockedByRisk |

## Diagnostics

- none
