# Ranker Feature Ablation Report

Generated: 2026-06-06 09:57:47 +00:00
Input: `D:\Users\Ye_Luo\AppData\Local\Context\learning\features\ranking-pairs.jsonl`
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
- FPR/FNR: `9.52 %` / `9.52 %`

## Ablations

| Disabled Feature | PairwiseAccuracy | Delta | FPR | FNR | Fixed | Newly Failed | Top Cluster |
|---|---:|---:|---:|---:|---:|---:|---|
| lifecycle | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| recency | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| channel source | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| relation path | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| short-term match | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| stable match | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| constraint match | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |
| keyword match | 100.00 % | +9.52 % | 0.00 % | 0.00 % | 3 | 0 | - |
| semantic anchor match | 100.00 % | +9.52 % | 0.00 % | 0.00 % | 3 | 0 | - |
| importance | 90.48 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | DeprecatedNoise (3) |

## lifecycle

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## recency

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## channel source

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## relation path

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## short-term match

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## stable match

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## constraint match

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## keyword match

### Failure Clusters
- (none)

### Top Fixed Examples
| Sample | Mode | Intent | Positive | Negative | BaselineMargin | CandidateMargin | Cluster | Reason |
|---|---|---|---|---|---:|---:|---|---|
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | -10.95 | 3.025 | DeprecatedNoise | baseline failed; candidate fixed |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | -10.75 | 3.125 | DeprecatedNoise | baseline failed; candidate fixed |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | -8.6 | 4.2 | DeprecatedNoise | baseline failed; candidate fixed |

### Top Newly Failed Examples
- (none)

## semantic anchor match

### Failure Clusters
- (none)

### Top Fixed Examples
| Sample | Mode | Intent | Positive | Negative | BaselineMargin | CandidateMargin | Cluster | Reason |
|---|---|---|---|---|---:|---:|---|---|
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | -10.95 | 3.025 | DeprecatedNoise | baseline failed; candidate fixed |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | -10.75 | 3.125 | DeprecatedNoise | baseline failed; candidate fixed |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | -8.6 | 4.2 | DeprecatedNoise | baseline failed; candidate fixed |

### Top Newly Failed Examples
- (none)

## importance

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)
