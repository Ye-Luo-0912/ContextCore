# Lifecycle-aware Ranker Offline Report

Generated: 2026-06-06 09:57:47 +00:00
Input: `$(repo-root)\learning\features\ranking-pairs.jsonl`
Policy: `learning-offline-baseline/v1`
Status: `Ready`
Pairs: `253`
Best baseline: `LifecycleAwareFeatureBaseline`
Target passed: `True`
Simple baseline: accuracy `90.48 %`, residual `3`, DeprecatedNoise `3`

## Train/Test Split

- Strategy: `DeterministicGroupHash80_20`
- Group key: `EvalSampleId`
- Train groups/examples: `81` / `190`
- Test groups/examples: `32` / `63`

## Baselines

| Baseline | PairwiseAccuracy | AUC | WinOverSimple | FPR | FNR | ResidualFailures | DeprecatedNoise |
|---|---:|---:|---:|---:|---:|---:|---:|
| RuleScoreBaseline | 84.13 % | 0.8413 | 0.00 % | 15.87 % | 15.87 % | 5 | 5 |
| SimpleFeatureWeightedBaseline | 90.48 % | 0.9048 | 0.00 % | 9.52 % | 9.52 % | 3 | 3 |
| LifecycleAwareFeatureBaseline | 100.00 % | 1 | 6.52 % | 0.00 % | 0.00 % | 0 | 0 |

## RuleScoreBaseline

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 5 | chat-sample-002, chat-sample-004, novel-sample-008, project-sample-008 |

### Top Fixed Examples vs Simple
- (none)

### Top Newly Failed Examples vs Simple
| Sample | Mode | Intent | Positive | Negative | BaselineMargin | CandidateMargin | Cluster | Reason |
|---|---|---|---|---|---:|---:|---|---|
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-old-topic | 5.3 | -4.7 | DeprecatedNoise | baseline passed; candidate newly failed |
| project-sample-008 | ProjectMode | FuzzyQuestion | memory:project-pool-v2 | memory:project-pool-v1 | 9.6125 | -0.3875 | DeprecatedNoise | baseline passed; candidate newly failed |

## SimpleFeatureWeightedBaseline

### Failure Clusters
| Cluster | Count | Examples |
|---|---:|---|
| DeprecatedNoise | 3 | chat-sample-002, chat-sample-004, novel-sample-008 |

### Top Fixed Examples vs Simple
- (none)

### Top Newly Failed Examples vs Simple
- (none)

## LifecycleAwareFeatureBaseline

### Failure Clusters
- (none)

### Top Fixed Examples vs Simple
| Sample | Mode | Intent | Positive | Negative | BaselineMargin | CandidateMargin | Cluster | Reason |
|---|---|---|---|---|---:|---:|---|---|
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | -10.75 | 17.25 | DeprecatedNoise | baseline failed; candidate fixed |
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | -10.95 | 17.05 | DeprecatedNoise | baseline failed; candidate fixed |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | -8.6 | 7.4 | DeprecatedNoise | baseline failed; candidate fixed |

### Top Newly Failed Examples vs Simple
- (none)
