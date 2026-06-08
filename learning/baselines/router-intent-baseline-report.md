# Router Intent Offline Baseline Report

Generated: 2026-06-06 08:50:48 +00:00
Input: `D:\Users\Ye_Luo\AppData\Local\Context\learning\features\router-intent-examples.jsonl`
Policy: `learning-offline-baseline/v1`
Status: `Ready`
Samples: `163`
Best baseline: `RuleBasedBaseline`

## Train/Test Split

- Strategy: `DeterministicGroupHash80_20`
- Group key: `SourceId or metadata sampleId`
- Train groups/examples: `81` / `117`
- Test groups/examples: `32` / `46`

## Baselines

| Baseline | Accuracy | MacroF1 |
|---|---:|---:|
| MajorityClassBaseline | 34.78 % | 0.086 |
| RuleBasedBaseline | 56.52 % | 0.5088 |

## MajorityClassBaseline

### Per Intent Precision
| Intent | Value |
|---|---:|
| AuditDeprecated | 0 |
| AutomationRecovery | 0 |
| CodingTask | 0 |
| CurrentTask | 0 |
| FuzzyQuestion | 0.3478 |
| NovelGeneration | 0 |

### Per Intent Recall
| Intent | Value |
|---|---:|
| AuditDeprecated | 0 |
| AutomationRecovery | 0 |
| CodingTask | 0 |
| CurrentTask | 0 |
| FuzzyQuestion | 1 |
| NovelGeneration | 0 |

### Confusion Matrix
| Actual \ Predicted | AuditDeprecated | AutomationRecovery | CodingTask | CurrentTask | FuzzyQuestion | NovelGeneration |
|---|---:|---:|---:|---:|---:|---:|
| AuditDeprecated | 0 | 0 | 0 | 0 | 8 | 0 |
| AutomationRecovery | 0 | 0 | 0 | 0 | 8 | 0 |
| CodingTask | 0 | 0 | 0 | 0 | 7 | 0 |
| CurrentTask | 0 | 0 | 0 | 0 | 1 | 0 |
| FuzzyQuestion | 0 | 0 | 0 | 0 | 16 | 0 |
| NovelGeneration | 0 | 0 | 0 | 0 | 6 | 0 |

## RuleBasedBaseline

### Per Intent Precision
| Intent | Value |
|---|---:|
| AuditDeprecated | 1 |
| AutomationRecovery | 0.8 |
| CodingTask | 1 |
| ConflictCheck | 0 |
| CurrentTask | 0 |
| FuzzyQuestion | 1 |
| NovelGeneration | 0.75 |

### Per Intent Recall
| Intent | Value |
|---|---:|
| AuditDeprecated | 0.5 |
| AutomationRecovery | 1 |
| CodingTask | 0.7143 |
| ConflictCheck | 0 |
| CurrentTask | 0 |
| FuzzyQuestion | 0.1875 |
| NovelGeneration | 1 |

### Confusion Matrix
| Actual \ Predicted | AuditDeprecated | AutomationRecovery | CodingTask | ConflictCheck | CurrentTask | FuzzyQuestion | NovelGeneration |
|---|---:|---:|---:|---:|---:|---:|---:|
| AuditDeprecated | 4 | 2 | 0 | 0 | 0 | 0 | 2 |
| AutomationRecovery | 0 | 8 | 0 | 0 | 0 | 0 | 0 |
| CodingTask | 0 | 0 | 5 | 2 | 0 | 0 | 0 |
| ConflictCheck | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| CurrentTask | 0 | 0 | 0 | 1 | 0 | 0 | 0 |
| FuzzyQuestion | 0 | 0 | 0 | 13 | 0 | 3 | 0 |
| NovelGeneration | 0 | 0 | 0 | 0 | 0 | 0 | 6 |
