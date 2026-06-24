# Vector Query Profile Sweep Report

Generated: 2026-06-14T18:12:03.2639331+00:00

## A3 Sweep

- Samples: `50`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ProviderType: `OnnxLocal`
- EmbeddingModel: `qwen3-embedding-0.6b`
- Dimension: `1024`
- UseForRuntime: `False`
- ResultCount: `512`
- Recommendation: `KeepPreviewOnly`
- Best: `normal-v1:top3:min0.10:all`
- BestRecallAfter: `50.00%`
- BestMRRAfter: `0.6100`
- BestRiskAfter: `0`
- BestSeparation: `-0.0131`

| Profile | TopK | MinSim | Layer | RecallAfter | MRR | RiskAfter | Separation | Recommendation |
|---|---:|---:|---|---:|---:|---:|---:|---|
| audit-v1 | 20 | 0.60 | all | 89.39% | 0.7704 | 160 | 0.0255 | RequiresReranker |
| audit-v1 | 20 | 0.10 | all | 89.39% | 0.7704 | 172 | 0.0255 | RequiresReranker |
| audit-v1 | 20 | 0.20 | all | 89.39% | 0.7704 | 172 | 0.0255 | RequiresReranker |
| audit-v1 | 20 | 0.30 | all | 89.39% | 0.7704 | 172 | 0.0255 | RequiresReranker |
| audit-v1 | 20 | 0.40 | all | 89.39% | 0.7704 | 172 | 0.0255 | RequiresReranker |
| audit-v1 | 20 | 0.50 | all | 89.39% | 0.7704 | 172 | 0.0255 | RequiresReranker |
| diagnostics-v1 | 20 | 0.60 | all | 89.39% | 0.7562 | 164 | 0.0255 | RequiresReranker |
| diagnostics-v1 | 20 | 0.10 | all | 89.39% | 0.7562 | 176 | 0.0255 | RequiresReranker |
| diagnostics-v1 | 20 | 0.20 | all | 89.39% | 0.7562 | 176 | 0.0255 | RequiresReranker |
| diagnostics-v1 | 20 | 0.30 | all | 89.39% | 0.7562 | 176 | 0.0255 | RequiresReranker |
| diagnostics-v1 | 20 | 0.40 | all | 89.39% | 0.7562 | 176 | 0.0255 | RequiresReranker |
| diagnostics-v1 | 20 | 0.50 | all | 89.39% | 0.7562 | 176 | 0.0255 | RequiresReranker |
| audit-v1 | 10 | 0.60 | all | 87.88% | 0.7704 | 95 | -0.0101 | RequiresReranker |
| audit-v1 | 10 | 0.10 | all | 87.88% | 0.7704 | 100 | -0.0101 | RequiresReranker |
| audit-v1 | 10 | 0.20 | all | 87.88% | 0.7704 | 100 | -0.0101 | RequiresReranker |
| audit-v1 | 10 | 0.30 | all | 87.88% | 0.7704 | 100 | -0.0101 | RequiresReranker |
| audit-v1 | 10 | 0.40 | all | 87.88% | 0.7704 | 100 | -0.0101 | RequiresReranker |
| audit-v1 | 10 | 0.50 | all | 87.88% | 0.7704 | 100 | -0.0101 | RequiresReranker |
| diagnostics-v1 | 10 | 0.60 | all | 87.88% | 0.7562 | 97 | -0.0101 | RequiresReranker |
| diagnostics-v1 | 10 | 0.10 | all | 87.88% | 0.7562 | 102 | -0.0101 | RequiresReranker |

## Extended Sweep

- Samples: `113`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ProviderType: `OnnxLocal`
- EmbeddingModel: `qwen3-embedding-0.6b`
- Dimension: `1024`
- UseForRuntime: `False`
- ResultCount: `512`
- Recommendation: `KeepPreviewOnly`
- Best: `normal-v1:top3:min0.10:all`
- BestRecallAfter: `70.63%`
- BestMRRAfter: `0.7965`
- BestRiskAfter: `0`
- BestSeparation: `0.0121`

| Profile | TopK | MinSim | Layer | RecallAfter | MRR | RiskAfter | Separation | Recommendation |
|---|---:|---:|---|---:|---:|---:|---:|---|
| audit-v1 | 20 | 0.60 | all | 93.13% | 0.8697 | 273 | 0.0521 | RequiresReranker |
| audit-v1 | 20 | 0.10 | all | 93.13% | 0.8697 | 285 | 0.0521 | RequiresReranker |
| audit-v1 | 20 | 0.20 | all | 93.13% | 0.8697 | 285 | 0.0521 | RequiresReranker |
| audit-v1 | 20 | 0.30 | all | 93.13% | 0.8697 | 285 | 0.0521 | RequiresReranker |
| audit-v1 | 20 | 0.40 | all | 93.13% | 0.8697 | 285 | 0.0521 | RequiresReranker |
| audit-v1 | 20 | 0.50 | all | 93.13% | 0.8697 | 285 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.60 | all | 93.13% | 0.8634 | 277 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.10 | all | 93.13% | 0.8634 | 289 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.20 | all | 93.13% | 0.8634 | 289 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.30 | all | 93.13% | 0.8634 | 289 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.40 | all | 93.13% | 0.8634 | 289 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.50 | all | 93.13% | 0.8634 | 289 | 0.0521 | RequiresReranker |
| audit-v1 | 20 | 0.70 | all | 91.25% | 0.8679 | 158 | 0.0521 | RequiresReranker |
| diagnostics-v1 | 20 | 0.70 | all | 91.25% | 0.8620 | 160 | 0.0521 | RequiresReranker |
| audit-v1 | 10 | 0.60 | all | 90.62% | 0.8692 | 141 | 0.0174 | RequiresReranker |
| audit-v1 | 10 | 0.10 | all | 90.62% | 0.8692 | 146 | 0.0174 | RequiresReranker |
| audit-v1 | 10 | 0.20 | all | 90.62% | 0.8692 | 146 | 0.0174 | RequiresReranker |
| audit-v1 | 10 | 0.30 | all | 90.62% | 0.8692 | 146 | 0.0174 | RequiresReranker |
| audit-v1 | 10 | 0.40 | all | 90.62% | 0.8692 | 146 | 0.0174 | RequiresReranker |
| audit-v1 | 10 | 0.50 | all | 90.62% | 0.8692 | 146 | 0.0174 | RequiresReranker |

## Embedding Quality Baseline

- Samples: `113`
- Provider: `qwen3-embedding-0.6b-onnx`
- Model: `qwen3-embedding-0.6b`
- PositiveAverageSimilarity: `0.8321`
- NegativeAverageSimilarity: `0.7801`
- SimilaritySeparation: `0.0521`
- MustHitRecallAt20: `93.13%`
- MustNotHitRiskAt20: `31.15%`
- Recommendation: `KeepPreviewOnly`

