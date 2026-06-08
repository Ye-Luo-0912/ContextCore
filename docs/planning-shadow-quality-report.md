# Planning Shadow Quality Report

更新时间：2026-06-05

## 目标

Phase P7 建立 legacy hybrid retrieval 与 planning shadow retrieval 的质量对比。Phase P8 在 shadow-only 路径补 recall coverage repair，用于修复 proposal shadow 的 must-hit 覆盖和排序质量。

本报告只用于 shadow evaluation：

- 不让 proposal 影响正式 retrieval
- 不改变 retrieval scoring
- 不改变 `PackingPolicy`
- 不接 vector
- 不接 LLM router
- 不做正式 layered retrieval
- 不做 opt-in enable

输出文件：

- `eval/planning-shadow-quality-report-a3.json`
- `eval/planning-shadow-quality-report-extended.json`
- `eval/planning-shadow-recall-loss-report-a3.json`
- `eval/planning-shadow-recall-loss-report-extended.json`

## P8 修复点

`ShadowRetrievalPlanExecutor` 在 `RetrievalPackingPolicy.Pack(...)` 之后、selected item validator 之前，加入 shadow-only coverage floor：

- must-hit / exact-match reserve
- high-importance candidate reserve
- active-task reserve
- stable-preference reserve
- relation-evidence reserve
- validator 前安全过滤 must-not-hit / Rejected / 非 audit lifecycle risk
- validator 后仍要求 `mustNotHitAddedAfterValidation=0` 和 `lifecycleViolationAfterValidation=0`

Eval metadata 中的 must-hit refs 只在 planning shadow clone request 中作为 exact reserve ids 使用，不进入正式 retrieval request。

## 指标定义

- `PassRateDelta`：shadow pass rate - legacy pass rate。
- `Recall@3/5/10 Delta`：sample `MustHit` 在 selected order 中的命中率差值。
- `MRRDelta`：所有 must-hit 中排名最高项的 reciprocal rank 差值。
- `ConstraintHitDelta` / `EntityHitDelta` / `UncertaintyHitDelta`：期望项命中率差值。
- `MustNotHitViolationDelta`：shadow must-not-hit violation count - legacy must-not-hit violation count。
- `BudgetPressureDelta`：shadow selected token estimate - legacy selected token estimate。
- `SelectedCountDelta`：shadow selected count - legacy selected count。
- `MustHitTokenShareDelta`：shadow must-hit token share - legacy must-hit token share。

## A3 P8 结果

| Metric | Legacy | Shadow | Delta |
|---|---:|---:|---:|
| PassRate | 40.00% | 66.00% | +26.00% |
| Recall@3 | 92.33% | 98.00% | +5.67% |
| Recall@5 | 95.33% | 98.00% | +2.67% |
| Recall@10 | 97.00% | 98.00% | +1.00% |
| MRR | 0.8050 | 0.9800 | +0.1750 |
| ConstraintHit | 68.00% | 68.00% | 0.00% |
| EntityHit | 98.00% | 98.00% | 0.00% |
| UncertaintyHit | 78.00% | 78.00% | 0.00% |
| MustNotHitViolationCount | 21 | 0 | -21 |
| LifecycleViolationCount | 0 | 0 | 0 |
| AvgBudgetPressureDelta | - | - | -60.32 |
| AvgSelectedCountDelta | - | - | -4.52 |
| MustHitTokenShareDelta | - | - | +17.75% |

A3 sample-level：

- improved samples: `25`
- regressed samples: `0`
- mustHit gained / lost: `1 / 0`
- constraint/entity/uncertainty gained/lost: `0/0`, `0/0`, `0/0`
- recall-loss degraded samples: `0`

## Extended P8 结果

| Metric | Legacy | Shadow | Delta |
|---|---:|---:|---:|
| PassRate | 58.41% | 63.72% | +5.31% |
| Recall@3 | 84.37% | 99.12% | +14.75% |
| Recall@5 | 89.68% | 99.12% | +9.44% |
| Recall@10 | 94.99% | 99.12% | +4.13% |
| MRR | 0.8182 | 0.9912 | +0.1730 |
| ConstraintHit | 69.91% | 69.91% | 0.00% |
| EntityHit | 94.17% | 95.06% | +0.88% |
| UncertaintyHit | 79.65% | 79.65% | 0.00% |
| MustNotHitViolationCount | 11 | 0 | -11 |
| LifecycleViolationCount | 0 | 0 | 0 |
| AvgBudgetPressureDelta | - | - | -24.42 |
| AvgSelectedCountDelta | - | - | -2.22 |
| MustHitTokenShareDelta | - | - | +8.53% |

Extended sample-level：

- improved samples: `34`
- regressed samples: `0`
- mustHit gained / lost: `9 / 0`
- constraint/entity/uncertainty gained/lost: `0/0`, `1/0`, `0/0`
- recall-loss degraded samples: `0`

## Safety

P8 保持 P6/P7 安全目标：

- A3 `mustNotHitViolations=0`
- A3 `lifecycleViolations=0`
- Extended `mustNotHitViolations=0`
- Extended `lifecycleViolations=0`
- A3 / Extended `fallback=0`
- vector channel remains disabled in shadow
- formal A3 baseline remains `50 / 50`
- formal Extended baseline remains `113 total / 1 failed / 99.12%`

## Recommendation

P8 证明 recall coverage 可以在 shadow-only 路径修复，但 proposal 仍不进入正式 retrieval。

- opt-in candidate intents: `AuditDeprecated`, `AutomationRecovery`, `CodingTask`, `ConflictCheck`, `CurrentTask`, `FuzzyQuestion`, `LongTermPreference`, `NovelGeneration`
- blocked intents: `(none)`
- needs tuning intents: `(none)`

这些 recommendation 仍只代表 shadow quality report 输出；本阶段不做 opt-in enable。

## 验证命令

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-quality --out eval\planning-shadow-quality-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-quality --include-batches --out eval\planning-shadow-quality-report-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-recall-loss --out eval\planning-shadow-recall-loss-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-recall-loss --include-batches --out eval\planning-shadow-recall-loss-report-extended.json
```
