# Planning Opt-in Fallback Analysis

更新时间：2026-06-05

## 目标

Phase P10 对 intent-scoped `ApplyGuarded` 做 fallback 与扩展候选分析。该报告只用于评估 opt-in 扩容，不会修改默认配置，也不会让 proposal 全局影响正式 retrieval。

本阶段保持：

- 不全局启用 planning proposal
- 不接 vector
- 不接 LLM router
- 不做正式 layered retrieval
- 不改 legacy scoring
- 不改 `PackingPolicy`

## 报告输出

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-fallback-analysis --out eval\planning-optin-fallback-analysis-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-fallback-analysis --include-batches --out eval\planning-optin-fallback-analysis-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-constraint-safety --out eval\planning-optin-constraint-safety-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-constraint-safety --include-batches --out eval\planning-optin-constraint-safety-report-extended.json
```

默认分析 intent：

- current opt-in: `CurrentTask`, `AutomationRecovery`
- expansion candidates: `CodingTask`, `LongTermPreference`

这些默认只作用于 eval command，不写入 runtime 默认配置。

## Sample 字段

每个样本输出：

- `SampleId`
- `Mode`
- `Intent`
- `OptInMatched`
- `Applied`
- `FallbackUsed`
- `FallbackReason`
- `FallbackReasonCategory`
- `SafetyCheckFailed`
- `LegacySelected`
- `ProposalSelected`
- `FinalSelected`
- `MustHitDelta`
- `ConstraintDelta`
- `EntityDelta`
- `UncertaintyDelta`

## Intent 汇总

每个 intent 输出：

- `Samples`
- `OptInMatched`
- `Applied`
- `Fallback`
- `FallbackRate`
- `PassDelta`
- `RecallDelta`
- `MrrDelta`
- `MustNotHitViolation`
- `LifecycleViolation`
- `Recommendation`

`RecallDelta` 使用 `ShadowRecall10 - LegacyRecall10`。`MrrDelta` 使用 `ShadowMrr - LegacyMrr`。

## Fallback 分类

固定分类：

- `MustNotHitRisk`
- `LifecycleRisk`
- `HardConstraintMissing`
- `ConstraintRepaired`
- `ConstraintRepairFailed`
- `ConstraintDroppedByBudget`
- `ConstraintWrongSection`
- `InvalidPlan`
- `SelectedSetUnsafe`
- `BudgetPressureRegression`
- `QualityRegression`
- `Unknown`

## 推荐输出

顶层 recommendation 输出：

- `KeepOptIn`
- `ExpandCandidate`
- `ShadowOnly`
- `Blocked`
- `NeedsPolicyTuning`

推荐规则第一版保持保守：

- must-not-hit / lifecycle risk -> `Blocked`
- fallback rate 过高或 quality delta 退化 -> `NeedsPolicyTuning`
- 当前 opt-in 且安全、质量不退化 -> `KeepOptIn`
- candidate intent 且安全、质量不退化、能实际 applied -> `ExpandCandidate`
- 其他安全但证据不足 -> `ShadowOnly`

## 边界

`planning-optin-fallback-analysis` 复用 P9 opt-in comparison path。它会显式把 current opt-in 和 candidate intents 传给 isolated eval runner，但不会修改 `RetrievalPlanningOptions` 默认值，也不会更改正式 service 配置。

## Phase P11 Constraint Safety

P11 将 hard constraint fallback 前移为 repair-before-fallback：

- proposal selected 后先检查 required hard constraints。
- scope 匹配的 hard constraints 从 `IConstraintStore` 注入。
- 注入项标记为 `lockedConstraint=true`，并放入 `constraints` section。
- budget pressure 下优先裁剪 diagnostics / historical / low-value items。
- repair 后仍缺失才 fallback legacy。

Constraint safety report sample 输出：

- `sampleId`
- `mode`
- `intent`
- `expectedHardConstraints`
- `legacyConstraints`
- `proposalConstraints`
- `missingConstraints`
- `constraintSource`
- `lostAtStage`
- `suggestedFix`

P11 A3 结果：原 hard-constraint fallback 全部被 repair，`fallback=0`。Extended 中可从 constraint store 或 selected wrong-section 修复的样本被 repair；没有对应语料 hard constraint 的样本继续 fallback，保持安全优先。
