# Ranker Weight Sweep Report

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
- Baseline weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

## Best Result

- Configuration: `activeStatusBoost=0`
- PairwiseAccuracy: `90.48 %`
- Delta: `0.00 %`
- Recommendation: `Neutral`

## Sweep Results

| Config | Parameter | Value | PairwiseAccuracy | Delta | WinOverBaseline | FPR | FNR | Fixed | Newly Failed | Recommendation |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| activeStatusBoost=0 | activeStatusBoost | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| activeStatusBoost=1 | activeStatusBoost | 1 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| activeStatusBoost=2 | activeStatusBoost | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| activeStatusBoost=3 | activeStatusBoost | 3 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| currentVersionBoost=0 | currentVersionBoost | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| currentVersionBoost=1 | currentVersionBoost | 1 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| currentVersionBoost=2 | currentVersionBoost | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| currentVersionBoost=3 | currentVersionBoost | 3 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| default | default | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| lifecyclePenaltyWeight=0 | lifecyclePenaltyWeight | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| lifecyclePenaltyWeight=2 | lifecyclePenaltyWeight | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| lifecyclePenaltyWeight=4 | lifecyclePenaltyWeight | 4 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| lifecyclePenaltyWeight=6 | lifecyclePenaltyWeight | 6 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| lifecyclePenaltyWeight=8 | lifecyclePenaltyWeight | 8 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| noiseKeywordPenalty=0 | noiseKeywordPenalty | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| noiseKeywordPenalty=2 | noiseKeywordPenalty | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| noiseKeywordPenalty=4 | noiseKeywordPenalty | 4 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| noiseKeywordPenalty=6 | noiseKeywordPenalty | 6 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| recencyWeight=0 | recencyWeight | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| recencyWeight=0.5 | recencyWeight | 0.5 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| recencyWeight=1 | recencyWeight | 1 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| recencyWeight=2 | recencyWeight | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| relationEvidenceBoost=0 | relationEvidenceBoost | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| relationEvidenceBoost=1 | relationEvidenceBoost | 1 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| relationEvidenceBoost=2 | relationEvidenceBoost | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| relationEvidenceBoost=3 | relationEvidenceBoost | 3 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| stablePreferenceBoost=0 | stablePreferenceBoost | 0 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| stablePreferenceBoost=1 | stablePreferenceBoost | 1 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| stablePreferenceBoost=2 | stablePreferenceBoost | 2 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |
| stablePreferenceBoost=3 | stablePreferenceBoost | 3 | 90.48 % | 0.00 % | 0.00 % | 9.52 % | 9.52 % | 0 | 0 | Neutral |

## activeStatusBoost=0

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## activeStatusBoost=1

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=1, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## activeStatusBoost=2

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=2, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## activeStatusBoost=3

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=3, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## currentVersionBoost=0

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## currentVersionBoost=1

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=1, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## currentVersionBoost=2

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=2, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## currentVersionBoost=3

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=3, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## default

- Weights: `lifecyclePenaltyWeight=4, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)

## lifecyclePenaltyWeight=0

- Weights: `lifecyclePenaltyWeight=0, recencyWeight=0, currentVersionBoost=0, activeStatusBoost=0, noiseKeywordPenalty=0, relationEvidenceBoost=0, stablePreferenceBoost=0`

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples
- (none)

### Top Newly Failed Examples
- (none)
