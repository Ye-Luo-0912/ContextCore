# Vector Embedding Provider Comparison

Generated: 2026-06-14T18:12:03.2367470+00:00

- ProviderId: `qwen3-embedding-0.6b-onnx`
- ProviderType: `OnnxLocal`
- EmbeddingModel: `qwen3-embedding-0.6b`
- Dimension: `1024`
- IndexedItems: `158`
- QueryCount: `113`
- AverageTopSimilarity: `0.8521`
- PositiveAverageSimilarity: `0.8321`
- NegativeAverageSimilarity: `0.7801`
- SimilaritySeparation: `0.0521`
- MustHitRecallAtK: `76.88%`
- MustHitMrrAfterPolicy: `0.8058`
- MustNotHitRiskAfterPolicy: `0.82%`
- LifecycleRiskAfterPolicy: `0.00%`
- EligibleCandidateCount: `880`
- NoCandidateCount: `0`
- Recommendation: `BlockedByRisk`

## Provider Results

| Provider | Type | Model | Dim | Indexed | Recall | MRR | Sep | Eligible | NoCandidate | Risk | Recommendation |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| deterministic-hash | DeterministicHash | deterministic-hash-v1 | 16 | 158 | 3.12% | 0.0107 | 0.0066 | 863 | 0 | 0.00% | NeedsRealEmbeddingProvider |
| qwen3-embedding-0.6b-onnx | OnnxLocal | qwen3-embedding-0.6b | 1024 | 158 | 76.88% | 0.8058 | 0.0521 | 880 | 0 | 0.82% | BlockedByRisk |

## Diagnostics

- none
