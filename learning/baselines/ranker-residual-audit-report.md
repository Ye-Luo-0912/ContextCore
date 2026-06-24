# Ranker Residual Error Audit Report

Generated: 2026-06-06 09:57:47 +00:00
Input: `$(repo-root)\learning\features\ranking-pairs.jsonl`
Policy: `learning-offline-baseline/v1`
Status: `Ready`
Pairs: `253`

## Train/Test Split

- Strategy: `DeterministicGroupHash80_20`
- Group key: `EvalSampleId`
- Train groups/examples: `81` / `190`
- Test groups/examples: `32` / `63`

## Baseline

- Baseline: `SimpleFeatureWeightedBaseline`
- PairwiseAccuracy: `90.48 %`
- Residual failures: `3`

## Failure Clusters

| Cluster | Count | Avg Margin | Examples | Probable Cause |
|---|---:|---:|---|---|
| DeprecatedNoise | 3 | -10.1 | chat-sample-002, chat-sample-004, novel-sample-008 | Deprecated or historical negatives retain high lexical/semantic similarity. |

## Feature Conflicts
| Feature | Failures | Positive Avg | Negative Avg | Delta | Interpretation |
|---|---:|---:|---:|---:|---|
| KeywordMatch | 3 | 25.4 | 38.45 | -13.05 | Keyword feature favors the negative candidate when average delta is below zero. |
| SemanticAnchorMatch | 3 | 25.4 | 38.45 | -13.05 | Semantic anchor feature overmatches the negative candidate when average delta is below zero. |
| Rank | 3 | 2 | 0 | 2 | Lower rank is better; positive rank should be lower than negative rank when both are selected. |
| Selection | 3 | 1 | 0 | 1 | Selected-state conflict indicates historical or deprecated negatives were selected offline. |

## Hard Negative Recommendations
| Type | Cluster | Count | Examples | Reason | Suggested Action |
|---|---|---:|---|---|---|
| DeprecatedSameKeyword | DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 | Deprecated negatives share query keywords with current positives. | Generate pairs where deprecated/historical items share the same keyword surface as active positives. |
| VersionConflict | DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 | Residual failures include older version candidates outranking active versions. | Add hard negatives that pair v1/old/deprecated items against v2/latest/current positives. |
| HistoricalSelectedNoise | DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 | Historical candidates can still be attractive when selected/rank features are strong. | Add selected historical/deprecated negatives with explicit lifecycle markers to teach demotion offline. |
| WeakLifecycleMarker | DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 | Lifecycle markers are not strong enough in eval-only ranking pairs. | Add negatives with weak or missing deprecated markers and require positive active/current evidence. |
| SemanticAnchorOvermatch | DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 | Semantic anchor surface can overmatch deprecated negatives. | Add semantically similar deprecated negatives that differ only by lifecycle/version state. |

## Residual Failure Details
| Sample | Mode | Intent | Positive | Negative | PosScore | NegScore | Margin | PosKeyword | NegKeyword | PosSemantic | NegSemantic | PosSelected | NegSelected | PosRank | NegRank | PosKind | NegKind | PosSection | NegSection | Cluster | Probable Cause |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|---:|---:|---|---|---|---|---|---|
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | 94.3 | 105.25 | -10.95 | 41.15 | 54.625 | 41.15 | 54.625 | True | False | 2 | 0 | working_memory | historical_context | working_memory |  | DeprecatedNoise | Deprecated negative has comparable or stronger keyword/semantic match than the active positive. |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | 48.65 | 59.4 | -10.75 | 18.325 | 31.7 | 18.325 | 31.7 | True | False | 2 | 0 | working_memory | historical_context | working_memory |  | DeprecatedNoise | Deprecated negative has comparable or stronger keyword/semantic match than the active positive. |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | 45.45 | 54.05 | -8.6 | 16.725 | 29.025 | 16.725 | 29.025 | True | False | 2 | 0 | working_memory | historical_context | working_memory |  | DeprecatedNoise | Deprecated negative has comparable or stronger keyword/semantic match than the active positive. |
