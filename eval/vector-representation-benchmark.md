# Vector Representation Benchmark

Generated: 2026-06-10T14:34:11.0024732+00:00

## A3 Summary

- Samples: `50`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Recommendation: `NeedsBetterEmbedding`
- BestDocumentProfile: `metadata-enriched-v1`
- BestQueryProfile: `mode-intent-query-v1`
- BestRecall: `69.70%`
- BestMRR: `0.7067`
- BestRiskAfterPolicy: `0`
- BestRecoveredMissCount: `3`

| Document Profile | Query Profile | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Recovered | NewRisk | Separation | Recommendation |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| metadata-enriched-v1 | mode-intent-query-v1 | 69.70% | 0.7067 | 0 | 0.00% | 0.00% | 3 | 0 | 0.0106 | NeedsBetterEmbedding |
| metadata-enriched-v1 | intent-query-v1 | 69.70% | 0.6967 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0135 | NeedsBetterEmbedding |
| metadata-enriched-v1 | raw-query-v1 | 69.70% | 0.6807 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0137 | NeedsBetterEmbedding |
| raw-content-v1 | raw-query-v1 | 69.70% | 0.6732 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0045 | NeedsBetterEmbedding |
| title-content-v1 | raw-query-v1 | 69.70% | 0.6732 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0045 | NeedsBetterEmbedding |
| title-summary-content-v1 | raw-query-v1 | 69.70% | 0.6732 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0045 | NeedsBetterEmbedding |
| metadata-enriched-v1 | anchor-query-v1 | 69.70% | 0.6479 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0088 | NeedsBetterEmbedding |
| raw-content-v1 | intent-query-v1 | 68.18% | 0.6683 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0136 | NeedsBetterEmbedding |
| title-content-v1 | intent-query-v1 | 68.18% | 0.6683 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0136 | NeedsBetterEmbedding |
| title-summary-content-v1 | intent-query-v1 | 68.18% | 0.6683 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0136 | NeedsBetterEmbedding |
| raw-content-v1 | mode-intent-query-v1 | 68.18% | 0.6533 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0136 | NeedsBetterEmbedding |
| title-content-v1 | mode-intent-query-v1 | 68.18% | 0.6533 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0136 | NeedsBetterEmbedding |
| title-summary-content-v1 | mode-intent-query-v1 | 68.18% | 0.6533 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0136 | NeedsBetterEmbedding |
| raw-content-v1 | anchor-query-v1 | 68.18% | 0.6417 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0068 | NeedsBetterEmbedding |
| title-content-v1 | anchor-query-v1 | 68.18% | 0.6417 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0068 | NeedsBetterEmbedding |
| title-summary-content-v1 | anchor-query-v1 | 68.18% | 0.6417 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0068 | NeedsBetterEmbedding |
| raw-content-v1 | expanded-anchor-query-v1 | 68.18% | 0.6098 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0114 | NeedsBetterEmbedding |
| title-content-v1 | expanded-anchor-query-v1 | 68.18% | 0.6098 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0114 | NeedsBetterEmbedding |
| title-summary-content-v1 | expanded-anchor-query-v1 | 68.18% | 0.6098 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0114 | NeedsBetterEmbedding |
| metadata-enriched-v1 | expanded-anchor-query-v1 | 66.67% | 0.6235 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0128 | NeedsBetterEmbedding |
| compact-retrieval-text-v1 | anchor-query-v1 | 65.15% | 0.6313 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0091 | NeedsBetterEmbedding |
| anchor-enriched-v1 | anchor-query-v1 | 65.15% | 0.6233 | 0 | 0.00% | 0.00% | 1 | 0 | -0.0008 | NeedsBetterEmbedding |
| compact-retrieval-text-v1 | expanded-anchor-query-v1 | 65.15% | 0.5955 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0097 | NeedsBetterEmbedding |
| anchor-enriched-v1 | expanded-anchor-query-v1 | 63.64% | 0.6219 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0009 | NeedsBetterEmbedding |
| compact-retrieval-text-v1 | raw-query-v1 | 65.15% | 0.6190 | 1 | 1.75% | 0.00% | 0 | 1 | -0.0009 | BlockedByRisk |
| compact-retrieval-text-v1 | intent-query-v1 | 65.15% | 0.6063 | 1 | 1.75% | 0.00% | 0 | 1 | 0.0026 | BlockedByRisk |
| anchor-enriched-v1 | raw-query-v1 | 63.64% | 0.6283 | 1 | 1.75% | 0.00% | 0 | 1 | -0.0026 | BlockedByRisk |
| compact-retrieval-text-v1 | mode-intent-query-v1 | 62.12% | 0.6400 | 1 | 1.75% | 0.00% | 0 | 1 | 0.0126 | BlockedByRisk |
| anchor-enriched-v1 | mode-intent-query-v1 | 60.61% | 0.5883 | 1 | 1.75% | 0.00% | 0 | 1 | 0.0129 | BlockedByRisk |
| anchor-enriched-v1 | intent-query-v1 | 59.09% | 0.5833 | 1 | 1.75% | 0.00% | 0 | 1 | 0.0189 | BlockedByRisk |

## Extended Summary

- Samples: `113`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Recommendation: `ReadyForRetrievalShadow`
- BestDocumentProfile: `metadata-enriched-v1`
- BestQueryProfile: `mode-intent-query-v1`
- BestRecall: `84.38%`
- BestMRR: `0.8400`
- BestRiskAfterPolicy: `0`
- BestRecoveredMissCount: `4`

| Document Profile | Query Profile | Recall | MRR | Risk | MustNotRisk | LifecycleRisk | Recovered | NewRisk | Separation | Recommendation |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| metadata-enriched-v1 | mode-intent-query-v1 | 84.38% | 0.8400 | 0 | 0.00% | 0.00% | 4 | 0 | 0.0364 | ReadyForRetrievalShadow |
| metadata-enriched-v1 | intent-query-v1 | 84.38% | 0.8366 | 0 | 0.00% | 0.00% | 3 | 0 | 0.0432 | ReadyForRetrievalShadow |
| metadata-enriched-v1 | raw-query-v1 | 84.38% | 0.8314 | 0 | 0.00% | 0.00% | 3 | 0 | 0.0478 | ReadyForRetrievalShadow |
| raw-content-v1 | mode-intent-query-v1 | 84.38% | 0.8201 | 0 | 0.00% | 0.00% | 3 | 0 | 0.0609 | ReadyForRetrievalShadow |
| title-content-v1 | mode-intent-query-v1 | 84.38% | 0.8201 | 0 | 0.00% | 0.00% | 3 | 0 | 0.0609 | ReadyForRetrievalShadow |
| title-summary-content-v1 | mode-intent-query-v1 | 84.38% | 0.8201 | 0 | 0.00% | 0.00% | 3 | 0 | 0.0609 | ReadyForRetrievalShadow |
| raw-content-v1 | raw-query-v1 | 83.75% | 0.8303 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0679 | ReadyForRetrievalShadow |
| title-content-v1 | raw-query-v1 | 83.75% | 0.8303 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0679 | ReadyForRetrievalShadow |
| title-summary-content-v1 | raw-query-v1 | 83.75% | 0.8303 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0679 | ReadyForRetrievalShadow |
| metadata-enriched-v1 | anchor-query-v1 | 83.75% | 0.8154 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0415 | ReadyForRetrievalShadow |
| raw-content-v1 | intent-query-v1 | 83.13% | 0.8267 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0673 | ReadyForRetrievalShadow |
| title-content-v1 | intent-query-v1 | 83.13% | 0.8267 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0673 | ReadyForRetrievalShadow |
| title-summary-content-v1 | intent-query-v1 | 83.13% | 0.8267 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0673 | ReadyForRetrievalShadow |
| raw-content-v1 | anchor-query-v1 | 82.50% | 0.8105 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0585 | ReadyForRetrievalShadow |
| title-content-v1 | anchor-query-v1 | 82.50% | 0.8105 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0585 | ReadyForRetrievalShadow |
| title-summary-content-v1 | anchor-query-v1 | 82.50% | 0.8105 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0585 | ReadyForRetrievalShadow |
| metadata-enriched-v1 | expanded-anchor-query-v1 | 82.50% | 0.7977 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0408 | ReadyForRetrievalShadow |
| raw-content-v1 | expanded-anchor-query-v1 | 81.87% | 0.7942 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0556 | ReadyForRetrievalShadow |
| title-content-v1 | expanded-anchor-query-v1 | 81.87% | 0.7942 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0556 | ReadyForRetrievalShadow |
| title-summary-content-v1 | expanded-anchor-query-v1 | 81.87% | 0.7942 | 0 | 0.00% | 0.00% | 2 | 0 | 0.0556 | ReadyForRetrievalShadow |
| anchor-enriched-v1 | anchor-query-v1 | 80.00% | 0.8024 | 0 | 0.00% | 0.00% | 1 | 0 | 0.0434 | ReadyForRetrievalShadow |
| compact-retrieval-text-v1 | anchor-query-v1 | 80.00% | 0.8013 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0517 | ReadyForRetrievalShadow |
| anchor-enriched-v1 | expanded-anchor-query-v1 | 79.37% | 0.7943 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0453 | KeepPreviewOnly |
| compact-retrieval-text-v1 | expanded-anchor-query-v1 | 78.75% | 0.7812 | 0 | 0.00% | 0.00% | 0 | 0 | 0.0560 | KeepPreviewOnly |
| compact-retrieval-text-v1 | intent-query-v1 | 80.00% | 0.7823 | 1 | 0.82% | 0.00% | 0 | 1 | 0.0337 | BlockedByRisk |
| compact-retrieval-text-v1 | mode-intent-query-v1 | 79.37% | 0.8009 | 1 | 0.82% | 0.00% | 1 | 1 | 0.0361 | BlockedByRisk |
| anchor-enriched-v1 | mode-intent-query-v1 | 79.37% | 0.7842 | 1 | 0.82% | 0.00% | 1 | 1 | 0.0338 | BlockedByRisk |
| anchor-enriched-v1 | raw-query-v1 | 78.75% | 0.8013 | 1 | 0.82% | 0.00% | 0 | 1 | 0.0354 | BlockedByRisk |
| compact-retrieval-text-v1 | raw-query-v1 | 78.75% | 0.7919 | 1 | 0.82% | 0.00% | 0 | 1 | 0.0384 | BlockedByRisk |
| anchor-enriched-v1 | intent-query-v1 | 76.88% | 0.7820 | 1 | 0.82% | 0.00% | 0 | 1 | 0.0444 | BlockedByRisk |

