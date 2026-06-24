# Vector Embedding Provider Comparison

Generated: 2026-06-14T17:38:04.9153704+00:00

- ProviderId: `onnx-local`
- ProviderType: `OnnxLocal`
- EmbeddingModel: `bge-small-zh-v1.5`
- Dimension: `512`
- IndexedItems: `158`
- QueryCount: `113`
- AverageTopSimilarity: `0.0673`
- PositiveAverageSimilarity: `0.0460`
- NegativeAverageSimilarity: `0.0000`
- SimilaritySeparation: `0.0460`
- MustHitRecallAtK: `0.00%`
- MustHitMrrAfterPolicy: `0.0000`
- MustNotHitRiskAfterPolicy: `0.00%`
- LifecycleRiskAfterPolicy: `0.00%`
- EligibleCandidateCount: `0`
- NoCandidateCount: `0`
- Recommendation: `KeepPreviewOnly`

## Provider Results

| Provider | Type | Model | Dim | Indexed | Recall | MRR | Sep | Eligible | NoCandidate | Risk | Recommendation |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| deterministic-hash | DeterministicHash | deterministic-hash-v1 | 16 | 158 | 3.12% | 0.0107 | 0.0066 | 863 | 0 | 0.00% | NeedsRealEmbeddingProvider |
| onnx-local | OnnxLocal | bge-small-zh-v1.5 | 512 | 158 | 0.00% | 0.0000 | 0.0460 | 0 | 0 | 0.00% | KeepPreviewOnly |

## Diagnostics

- none
