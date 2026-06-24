# Router Intent Classifier Baseline Report

Generated: 2026-06-11 06:08:59 +00:00

## Summary

- Input: `D:\Users\Ye_Luo\AppData\Local\Context\learning\features\router-intent-examples.jsonl`
- Samples: `163`
- Status: `Ready`
- Best baseline: `TokenCentroidRouterBaseline`
- Recommendation: `ReadyForRouterShadow`
- Policy version: `router-intent-classifier-r1/v1`

## Split

- Strategy: `DeterministicGroupHash80_20`
- Group key: `SourceType+SourceId`
- Train: `128` examples / `89` groups
- Test: `35` examples / `24` groups

## Baselines

| Baseline | Accuracy | MacroF1 | LowConfidence | Abstain | CurrentTask | FuzzyQuestion | CodingTask | NovelGeneration | AutomationRecovery | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| ExistingRuleBasedRouterBaseline | 97.14 % | 0.6176 | 0 | 0 | 0.00 % | 100.00 % | 100.00 % | 100.00 % | 100.00 % | KeepRuleBased |
| TokenCentroidRouterBaseline | 91.43 % | 0.7027 | 0 | 0 | 100.00 % | 100.00 % | 77.78 % | 100.00 % | 100.00 % | ReadyForRouterShadow |

## Best Baseline Confusion Matrix

- AuditDeprecated: AuditDeprecated=5, AutomationRecovery=0, CodingTask=1, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=0, LongTermPreference=0, NovelGeneration=0
- AutomationRecovery: AuditDeprecated=0, AutomationRecovery=3, CodingTask=0, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=0, LongTermPreference=0, NovelGeneration=0
- CodingTask: AuditDeprecated=0, AutomationRecovery=0, CodingTask=7, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=2, LongTermPreference=0, NovelGeneration=0
- ConflictCheck: AuditDeprecated=0, AutomationRecovery=0, CodingTask=0, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=0, LongTermPreference=0, NovelGeneration=0
- CurrentTask: AuditDeprecated=0, AutomationRecovery=0, CodingTask=0, ConflictCheck=0, CurrentTask=1, FuzzyQuestion=0, LongTermPreference=0, NovelGeneration=0
- FuzzyQuestion: AuditDeprecated=0, AutomationRecovery=0, CodingTask=0, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=8, LongTermPreference=0, NovelGeneration=0
- LongTermPreference: AuditDeprecated=0, AutomationRecovery=0, CodingTask=0, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=0, LongTermPreference=0, NovelGeneration=0
- NovelGeneration: AuditDeprecated=0, AutomationRecovery=0, CodingTask=0, ConflictCheck=0, CurrentTask=0, FuzzyQuestion=0, LongTermPreference=0, NovelGeneration=8

## Runtime Safety

- This report is offline-only.
- It does not replace the runtime planning router.
- It does not change retrieval, planning, PackingPolicy, scoring, or package output.
