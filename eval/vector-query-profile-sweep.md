# Vector Query Profile Sweep Report

Generated: 2026-06-14T17:38:05.0524934+00:00

## A3 Sweep

- Samples: `50`
- ResultCount: `512`
- Recommendation: `NeedsRealEmbeddingProvider`
- Best: `normal-v1:top20:min0.10:all`
- BestRecallAfter: `12.12%`
- BestMRRAfter: `0.0253`
- BestRiskAfter: `0`
- BestSeparation: `0.0099`

| Profile | TopK | MinSim | Layer | RecallAfter | MRR | RiskAfter | Separation | Recommendation |
|---|---:|---:|---|---:|---:|---:|---:|---|
| audit-v1 | 20 | 0.10 | exclude-historical | 15.15% | 0.0249 | 16 | -0.0117 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.20 | exclude-historical | 15.15% | 0.0249 | 16 | -0.0117 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.10 | exclude-historical | 15.15% | 0.0240 | 16 | -0.0117 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.20 | exclude-historical | 15.15% | 0.0240 | 16 | -0.0117 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.10 | all | 15.15% | 0.0229 | 145 | 0.0099 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.20 | all | 15.15% | 0.0229 | 145 | 0.0099 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.10 | all | 15.15% | 0.0220 | 145 | 0.0099 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.20 | all | 15.15% | 0.0220 | 145 | 0.0099 | NeedsPolicyTuning |
| normal-v1 | 20 | 0.10 | all | 12.12% | 0.0253 | 0 | 0.0099 | NeedsRealEmbeddingProvider |
| normal-v1 | 20 | 0.10 | exclude-historical | 12.12% | 0.0253 | 0 | -0.0117 | NeedsRealEmbeddingProvider |
| normal-v1 | 20 | 0.20 | all | 12.12% | 0.0253 | 0 | 0.0099 | NeedsRealEmbeddingProvider |
| normal-v1 | 20 | 0.20 | exclude-historical | 12.12% | 0.0253 | 0 | -0.0117 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.10 | all | 12.12% | 0.0253 | 0 | 0.0099 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.10 | exclude-historical | 12.12% | 0.0253 | 0 | -0.0117 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.20 | all | 12.12% | 0.0253 | 0 | 0.0099 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.20 | exclude-historical | 12.12% | 0.0253 | 0 | -0.0117 | NeedsRealEmbeddingProvider |
| audit-v1 | 10 | 0.10 | exclude-historical | 9.09% | 0.0211 | 7 | 0.4357 | NeedsPolicyTuning |
| audit-v1 | 10 | 0.20 | exclude-historical | 9.09% | 0.0211 | 7 | 0.4357 | NeedsPolicyTuning |
| audit-v1 | 10 | 0.30 | exclude-historical | 9.09% | 0.0211 | 7 | 0.4357 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.30 | exclude-historical | 9.09% | 0.0211 | 13 | -0.0117 | NeedsPolicyTuning |

## Extended Sweep

- Samples: `113`
- ResultCount: `512`
- Recommendation: `NeedsRealEmbeddingProvider`
- Best: `normal-v1:top20:min0.10:exclude-historical`
- BestRecallAfter: `11.87%`
- BestMRRAfter: `0.0212`
- BestRiskAfter: `0`
- BestSeparation: `-0.0196`

| Profile | TopK | MinSim | Layer | RecallAfter | MRR | RiskAfter | Separation | Recommendation |
|---|---:|---:|---|---:|---:|---:|---:|---|
| audit-v1 | 20 | 0.10 | exclude-historical | 13.13% | 0.0207 | 33 | -0.0196 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.20 | exclude-historical | 13.13% | 0.0207 | 33 | -0.0196 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.10 | exclude-historical | 13.13% | 0.0201 | 33 | -0.0196 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.20 | exclude-historical | 13.13% | 0.0201 | 33 | -0.0196 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.10 | all | 12.50% | 0.0179 | 320 | 0.0066 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.20 | all | 12.50% | 0.0179 | 320 | 0.0066 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.10 | all | 12.50% | 0.0174 | 320 | 0.0066 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.20 | all | 12.50% | 0.0174 | 320 | 0.0066 | NeedsPolicyTuning |
| normal-v1 | 20 | 0.10 | exclude-historical | 11.87% | 0.0212 | 0 | -0.0196 | NeedsRealEmbeddingProvider |
| normal-v1 | 20 | 0.20 | exclude-historical | 11.87% | 0.0212 | 0 | -0.0196 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.10 | exclude-historical | 11.87% | 0.0212 | 0 | -0.0196 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.20 | exclude-historical | 11.87% | 0.0212 | 0 | -0.0196 | NeedsRealEmbeddingProvider |
| normal-v1 | 20 | 0.10 | all | 11.25% | 0.0207 | 0 | 0.0066 | NeedsRealEmbeddingProvider |
| normal-v1 | 20 | 0.20 | all | 11.25% | 0.0207 | 0 | 0.0066 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.10 | all | 11.25% | 0.0207 | 0 | 0.0066 | NeedsRealEmbeddingProvider |
| current-task-v1 | 20 | 0.20 | all | 11.25% | 0.0207 | 0 | 0.0066 | NeedsRealEmbeddingProvider |
| audit-v1 | 20 | 0.30 | exclude-historical | 8.75% | 0.0174 | 29 | -0.0196 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.30 | exclude-historical | 8.75% | 0.0169 | 29 | -0.0196 | NeedsPolicyTuning |
| audit-v1 | 20 | 0.30 | all | 8.75% | 0.0154 | 272 | 0.0066 | NeedsPolicyTuning |
| diagnostics-v1 | 20 | 0.30 | all | 8.75% | 0.0150 | 272 | 0.0066 | NeedsPolicyTuning |

## Embedding Quality Baseline

- Samples: `113`
- Provider: `deterministic-hash`
- Model: `deterministic-hash-v1`
- PositiveAverageSimilarity: `0.3661`
- NegativeAverageSimilarity: `0.3595`
- SimilaritySeparation: `0.0066`
- MustHitRecallAt20: `12.50%`
- MustNotHitRiskAt20: `4.10%`
- Recommendation: `NeedsRealEmbeddingProvider`

