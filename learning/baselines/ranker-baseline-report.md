# Ranker Offline Baseline Report

Generated: 2026-06-06 08:50:48 +00:00
Input: `D:\Users\Ye_Luo\AppData\Local\Context\learning\features\ranking-pairs.jsonl`
Policy: `learning-offline-baseline/v1`
Status: `Ready`
Pairs: `253`
Best baseline: `SimpleFeatureWeightedBaseline`

## Train/Test Split

- Strategy: `DeterministicGroupHash80_20`
- Group key: `EvalSampleId`
- Train groups/examples: `81` / `190`
- Test groups/examples: `32` / `63`

## Baselines

| Baseline | PairwiseAccuracy | AUC | WinRateOverRule | FPR | FNR |
|---|---:|---:|---:|---:|---:|
| RuleScoreBaseline | 84.13 % | 0.8413 | 0.00 % | 15.87 % | 15.87 % |
| SimpleFeatureWeightedBaseline | 90.48 % | 0.9048 | 6.35 % | 9.52 % | 9.52 % |

## RuleScoreBaseline Top Failure Examples
| Sample | Mode | Intent | Positive | Negative | PositiveScore | NegativeScore | Reason |
|---|---|---|---|---|---:|---:|---|
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | 88.3 | 109.25 | negative candidate outranked positive candidate |
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | 88.3 | 109.25 | negative candidate outranked positive candidate |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | 42.65 | 63.4 | negative candidate outranked positive candidate |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | 42.65 | 63.4 | negative candidate outranked positive candidate |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | 39.45 | 58.05 | negative candidate outranked positive candidate |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | 39.45 | 58.05 | negative candidate outranked positive candidate |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-old-topic | 42.65 | 47.35 | negative candidate outranked positive candidate |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-old-topic | 42.65 | 47.35 | negative candidate outranked positive candidate |
| project-sample-008 | ProjectMode | FuzzyQuestion | memory:project-pool-v2 | memory:project-pool-v1 | 99.775 | 100.1625 | negative candidate outranked positive candidate |
| project-sample-008 | ProjectMode | FuzzyQuestion | memory:project-pool-v2 | memory:project-pool-v1 | 99.775 | 100.1625 | negative candidate outranked positive candidate |

## SimpleFeatureWeightedBaseline Top Failure Examples
| Sample | Mode | Intent | Positive | Negative | PositiveScore | NegativeScore | Reason |
|---|---|---|---|---|---:|---:|---|
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | 94.3 | 105.25 | negative candidate outranked positive candidate |
| novel-sample-008 | NovelMode | NovelGeneration | memory:novel-weapon-v2 | memory:novel-weapon-v1 | 94.3 | 105.25 | negative candidate outranked positive candidate |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | 48.65 | 59.4 | negative candidate outranked positive candidate |
| chat-sample-002 | ChatMode | FuzzyQuestion | memory:chat-active-plan | memory:chat-noise-keyword-1 | 48.65 | 59.4 | negative candidate outranked positive candidate |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | 45.45 | 54.05 | negative candidate outranked positive candidate |
| chat-sample-004 | ChatMode | FuzzyQuestion | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | 45.45 | 54.05 | negative candidate outranked positive candidate |
