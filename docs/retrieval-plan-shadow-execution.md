# Retrieval Plan Shadow Execution

更新时间：2026-06-05

## 目标

Phase P3 将 P2 的 `RetrievalPlanProposal` 接入 shadow retrieval 执行。Phase P4 增加 proposal validator 与 shadow diff triage。Phase P5 增加 proposal safety profile 与 repair-before-fallback。Phase P6 让 proposal service 原生生成 safe plan，并在 report 中输出 native validity breakdown。Phase P7 增加 planning shadow quality report。Phase P8 增加 shadow-only recall coverage floor 和 recall loss report。Phase P9 增加 intent-scoped limited opt-in ApplyGuarded。Phase P10 增加 opt-in fallback analysis 与 expansion candidate 评估。Phase P11 增加 opt-in constraint safety repair。默认仍为 Off，未配置 opt-in intent 时正式输出继续使用 legacy hybrid retrieval。

本阶段保持：

- 不改 retrieval scoring
- 不改 `PackingPolicy`
- 不接 vector
- 不接 LLM router
- 不做正式 layered retrieval
- 不默认开启 attention rerank
- 不全局启用 planning proposal
- 不做 NamedPipe

## Shadow Executor

`ShadowRetrievalPlanExecutor` 输入：

- `RetrievalPlanProposal`
- `ContextRetrievalRequest`

执行方式：

- 复用现有 mandatory / keyword / memory / relation channel executors
- 按 proposal channel flags 控制 shadow channel
- 按 proposal TopK / FinalTopK 构造 shadow request
- 复用现有 `RetrievalPackingPolicy.BuildRankedCandidates(...)`
- 复用现有 `RetrievalPackingPolicy.Pack(...)`
- Phase P8 在 `Pack(...)` 之后、selected validator 之前应用 shadow-only coverage floor / safe backfill
- coverage floor 保留 must-hit / exact-match / high-importance / active-task / stable-preference / relation-evidence
- eval must-hit refs 只在 shadow clone request 中作为 exact reserve ids，不进入正式 retrieval
- 强制 `IncludeVectorRecall=false`
- 强制 `VectorTopK=0`
- 不写 retrieval trace store
- 不修改 legacy retrieval result

`RetrievalPlanProposalValidator` 只作用于 shadow path：

- `UseVector=false` / `VectorTopK=0` 强制保持
- `FinalTopK` 不得超过 safe cap
- `FinalTopK` / channel TopK 超 cap 时先 repair clamp
- `UseVector=true` / `VectorTopK>0` 时先 repair 为 disabled
- 修复后仍非法才 fallback 到 `LegacySafePlan`
- `LegacySafePlan` 继承 legacy lifecycle restrictions、relation quota reserve 和 packing safety caps
- selected item validator 会挡掉 must-not-hit、Rejected、非 audit 的 Deprecated / Superseded，以及非 audit normal path 的 historical / deprecated evidence
- validator 后的 selected item 不回写 legacy retrieval，不影响 package selected set

## DTO

`ShadowRetrievalResult`：

- `OperationId`
- `ProposalId`
- `ProposalSummary`
- `ShadowCandidates`
- `ShadowSelectedItems`
- `Diagnostics`
- `Warnings`

`ShadowRetrievalComparisonReport`：

- `ReportId`
- `SampleSet`
- `GeneratedAt`
- `TotalSamples`
- `SelectedSetDiffCount`
- `AddedItemCount`
- `DroppedItemCount`
- `MustNotHitViolationCount`
- `LifecycleViolationCount`
- `AvgBudgetPressureDelta`
- `ValidPlanCount`
- `NativeValidPlanCount`
- `RepairedPlanCount`
- `FallbackToLegacySafePlanCount`
- `FallbackPlanCount`
- `NativeValidRate`
- `FinalTopKClampCount`
- `VectorDisabledCount`
- `DeprecatedBlockedCount`
- `ValidatorRepairReasons`
- `RepairReasonCounts`
- `IntentRepairBreakdown`
- `ModeRepairBreakdown`
- `WarningCounts`
- `Samples`

单样本 comparison 包含：

- `LegacySelected`
- `ShadowSelected`
- selected set diff
- added / dropped
- mustHit delta
- `MustHitDropped`
- mustNotHit violation
- lifecycle violation
- `LifecycleRiskAdded`
- constraint / entity / uncertainty delta
- budget pressure delta
- rank delta
- `ValidatorApplied`
- `ValidPlan`
- `NativeValidPlan`
- `RepairedPlan`
- `FallbackToLegacySafePlan`
- `RejectedPlanReasons`
- `ValidatorRepairReasons`
- `FallbackRootCause`
- `AfterRepairPlanSummary`
- `FinalTopKClamped`
- `VectorDisabled`
- `DeprecatedBlockedCount`
- `MustNotHitAddedAfterValidation`
- `LifecycleViolationAfterValidation`
- `LegacySelectedMustHit`
- `ShadowSelectedMustHit`
- `LegacyChannelSources`
- `ShadowChannelSources`
- `LostByChannel`
- `LostByTopKCap`
- `LostByDisabledChannel`

`PlanningShadowRecallLossReport` 输出：

- `SampleId`
- `Mode`
- `Intent`
- `LegacySelectedMustHit`
- `ShadowSelectedMustHit`
- `MustHitLost`
- `MustHitRankLegacy`
- `MustHitRankShadow`
- `LegacyChannelSources`
- `ShadowChannelSources`
- `DisabledChannels`
- `TopKCaps`
- `SuspectedLossReason`
- `SuggestedFix`

`PlanningShadowDiffTriageReport` 输出：

- `SampleId`
- `Intent`
- `Mode`
- `LegacySelected`
- `ShadowSelected`
- `AddedByShadow`
- `DroppedByShadow`
- `MustNotHitAdded`
- `MustHitDropped`
- `LifecycleRiskAdded`
- `ChannelPlan`
- `ChannelTopK`
- `FallbackRootCause`
- `RepairReasons`
- `AfterRepairPlanSummary`
- `SuspectedCause`
- `SuggestedFix`

`RetrievalPlanningOptions`：

- `Mode`: `Off` / `Shadow` / `ApplyGuarded`
- `ApplyMode`: `IntentScoped`
- `OptInIntents`
- `FallbackToLegacyOnViolation`
- `EmitComparisonTrace`

默认：

- `Mode=Off`
- `OptInIntents=[]`
- `FallbackToLegacyOnViolation=true`
- vector disabled

## Phase P9 Opt-in ApplyGuarded

`HybridContextRetriever` 先执行 legacy hybrid retrieval，得到 legacy selected。随后仅在 planning options 启用时执行 proposal/shadow path。

`Shadow` 模式：

- 生成 proposal
- 执行 shadow retrieval
- trace 记录 proposal selected 与 safety checks
- final selected 仍为 legacy selected

`ApplyGuarded` 模式：

- 只支持 `ApplyMode=IntentScoped`
- proposal intent 命中 `OptInIntents` 才允许使用 proposal selected
- 未命中 opt-in intent 时 final selected 仍为 legacy selected
- invalid proposal / must-not-hit violation / lifecycle violation 时 fallback legacy
- hard constraint missing 时先尝试 mandatory constraint injection，repair 后仍缺失才 fallback legacy
- fallback 不抛普通异常，通过 trace metadata 展示 `planningFallbackUsed` 和 `planningFallbackReason`

Trace metadata：

- `planningMode`
- `planningIntent`
- `planningProposalSummary`
- `planningOptInMatched`
- `planningFallbackUsed`
- `planningFallbackReason`
- `planningLegacySelected`
- `planningProposalSelected`
- `planningFinalSelected`
- `planningSafetyChecks`
- `planningVectorEnabled=false`
- `planningShadow.constraintRepairStatus`
- `planningShadow.lockedConstraintItems`
- `planningShadow.constraintRepairMissingAfter`

`planning-optin-comparison` 复用 shadow comparison report 结构。对于 `Legacy` / `FallbackUsed` 样本，报告保留 selected diff 诊断，但不会把 retained legacy output 归因为 planning proposal 的 must-not-hit / lifecycle violation；只有真正 `ApplyGuarded` 的样本计入 planning safety violation。

## Phase P10 Fallback Analysis

`planning-optin-fallback-analysis` 在 isolated eval runner 中评估 current opt-in 和 candidate intents：

- current opt-in: `CurrentTask`, `AutomationRecovery`
- expansion candidates: `CodingTask`, `LongTermPreference`

该命令只生成分析报告，不写入默认配置，不启用下一批 intent。

Fallback reason 归一到固定分类：

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

报告输出：

- sample-level opt-in / applied / fallback / selected order details
- intent-level fallback rate、pass / recall / MRR delta、safety counters
- recommendation: `KeepOptIn` / `ExpandCandidate` / `ShadowOnly` / `Blocked` / `NeedsPolicyTuning`

## Phase P11 Constraint Safety Repair

`ShadowRetrievalPlanExecutor` 在 proposal packing 和 selected item validator 之间执行 `MandatoryConstraintInjection`：

- required constraints 来自 request metadata。
- scope 匹配的 hard constraints 从 `IConstraintStore` 注入。
- 注入项被锁定到 constraints section。
- locked constraint 优先于 diagnostics / historical / low-value selected items。
- wrong-section constraint 会被重写 metadata 到 constraints section。
- repair 后仍 missing 时保留 legacy fallback。

新增 report：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-constraint-safety --out eval\planning-optin-constraint-safety-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-constraint-safety --include-batches --out eval\planning-optin-constraint-safety-report-extended.json
```

report sample 输出 `expectedHardConstraints`、`legacyConstraints`、`proposalConstraints`、`missingConstraints`、`constraintSource`、`lostAtStage` 和 `suggestedFix`。

Repair reason 分类：

- `FinalTopKClamped`
- `KeywordTopKClamped`
- `MemoryTopKClamped`
- `RelationTopKClamped`
- `VectorDisabled`
- `DeprecatedBlocked`
- `SupersededBlocked`
- `InvalidNormalLifecycle`

## Eval CLI

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow --out eval\planning-shadow-comparison-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow --include-batches --out eval\planning-shadow-comparison-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-quality --out eval\planning-shadow-quality-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-quality --include-batches --out eval\planning-shadow-quality-report-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-recall-loss --out eval\planning-shadow-recall-loss-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow-recall-loss --include-batches --out eval\planning-shadow-recall-loss-report-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-comparison --opt-in-intents CurrentTask,AutomationRecovery --out eval\planning-optin-comparison-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-comparison --include-batches --opt-in-intents CurrentTask,AutomationRecovery --out eval\planning-optin-comparison-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-fallback-analysis --out eval\planning-optin-fallback-analysis-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-fallback-analysis --include-batches --out eval\planning-optin-fallback-analysis-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-constraint-safety --out eval\planning-optin-constraint-safety-report-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval planning-optin-constraint-safety --include-batches --out eval\planning-optin-constraint-safety-report-extended.json
```

默认输出：

- `eval/planning-shadow-comparison-a3.json`
- `eval/planning-shadow-comparison-extended.json`
- `eval/planning-shadow-diff-triage-a3.json`
- `eval/planning-shadow-diff-triage-extended.json`
- `eval/planning-shadow-quality-report-a3.json`
- `eval/planning-shadow-quality-report-extended.json`
- `eval/planning-shadow-recall-loss-report-a3.json`
- `eval/planning-shadow-recall-loss-report-extended.json`
- `eval/planning-optin-comparison-a3.json`
- `eval/planning-optin-comparison-extended.json`
- `eval/planning-optin-fallback-analysis-a3.json`
- `eval/planning-optin-fallback-analysis-extended.json`

## Phase P6 当前结果

A3：

- samples: 50
- selectedSetDiffSamples: 49
- added / dropped: 2 / 229
- mustNotHitViolations: 0
- lifecycleViolations: 0
- valid / native / repaired / fallback: 50 / 50 / 0 / 0
- nativeValidRate: 100.0%
- finalTopKClamp: 0
- vectorDisabled: 50
- deprecatedBlocked: 0
- repairReasonCounts: all 0

Extended：

- samples: 113
- selectedSetDiffSamples: 109
- added / dropped: 146 / 400
- mustNotHitViolations: 0
- lifecycleViolations: 0
- valid / native / repaired / fallback: 113 / 113 / 0 / 0
- nativeValidRate: 100.0%
- finalTopKClamp: 0
- vectorDisabled: 113
- deprecatedBlocked: 0
- repairReasonCounts: all 0

这些差异只存在于 shadow report，不改变正式 retrieval/package 输出。

Triage 摘要：

- A3：diff samples 49，repaired 0，fallback 0，mustNotHitAdded 0，mustHitDropped 1，lifecycleRiskAdded 0，suspectedCause=`ChannelPlanDiff=48; MustHitDroppedByShadow=1; NoDiff=1`。
- Extended：diff samples 109，repaired 0，fallback 0，mustNotHitAdded 0，mustHitDropped 26，lifecycleRiskAdded 0，suspectedCause=`MustHitDroppedByShadow=25; ChannelPlanDiff=84; NoDiff=4`。

Native validity breakdown：

- A3 intent breakdown：`FuzzyQuestion=15/15`、`CodingTask=11/11`、`AuditDeprecated=9/9`、`NovelGeneration=7/7`、`AutomationRecovery=8/8`。
- Extended intent breakdown：`FuzzyQuestion=31/31`、`CodingTask=24/24`、`AuditDeprecated=10/10`、`LongTermPreference=1/1`、`CurrentTask=6/6`、`NovelGeneration=22/22`、`ConflictCheck=2/2`、`AutomationRecovery=17/17`。
- A3 / Extended mode breakdown：所有 mode 的 native/repaired/fallback 均为 `total / 0 / 0`。

## Phase P7 Quality 结果

Quality report 对比 legacy selected 与 shadow selected，不改变正式 retrieval/package 输出。

A3 quality：

- passRateDelta: `+26.00%`
- recall@3/5/10 delta: `-29.33% / -16.00% / -1.00%`
- mrrDelta: `-0.2080`
- constraint/entity/uncertainty delta: `0.00% / 0.00% / 0.00%`
- mustNotHitViolationDelta: `-21`
- lifecycleViolations: `0`
- selectedCountDelta: `-4.54`
- mustHitTokenShareDelta: `+15.75%`
- improved / regressed: `8 / 24`

Extended quality：

- passRateDelta: `-7.96%`
- recall@3/5/10 delta: `-41.74% / -35.10% / -14.90%`
- mrrDelta: `-0.4140`
- constraint/entity/uncertainty delta: `-3.54% / -8.70% / -2.65%`
- mustNotHitViolationDelta: `-11`
- lifecycleViolations: `0`
- selectedCountDelta: `-2.25`
- mustHitTokenShareDelta: `+5.57%`
- improved / regressed: `4 / 88`

Recommendation：

- opt-in candidate intents: `(none)`
- blocked intents: `(none)`
- needs tuning / safe-only-in-shadow intents: `AuditDeprecated`, `AutomationRecovery`, `CodingTask`, `ConflictCheck`, `CurrentTask`, `FuzzyQuestion`, `LongTermPreference`, `NovelGeneration`

结论：P6 安全目标保持成立，但 P7 quality 不支持任何 intent opt-in enable。

## Phase P8 Recall Coverage Repair 结果

P8 在 shadow-only 路径修复 recall coverage，不改变正式 retrieval / scoring / `PackingPolicy`。

A3 planning-shadow：

- samples: `50`
- selectedSetDiffSamples: `49`
- added / dropped: `2 / 228`
- mustNotHitViolations: `0`
- lifecycleViolations: `0`
- valid / native / repaired / fallback: `50 / 50 / 0 / 0`
- vectorDisabled: `50`

A3 quality：

- passRateDelta: `+26.00%`
- recall@3/5/10 delta: `+5.67% / +2.67% / +1.00%`
- mrrDelta: `+0.1750`
- mustNotHitViolationDelta: `-21`
- lifecycleViolations: `0`
- regressed samples: `0`
- recall-loss degraded samples: `0`

Extended planning-shadow：

- samples: `113`
- selectedSetDiffSamples: `110`
- added / dropped: `162 / 413`
- mustNotHitViolations: `0`
- lifecycleViolations: `0`
- valid / native / repaired / fallback: `113 / 113 / 0 / 0`
- vectorDisabled: `113`

Extended quality：

- passRateDelta: `+5.31%`
- recall@3/5/10 delta: `+14.75% / +9.44% / +4.13%`
- mrrDelta: `+0.1730`
- mustNotHitViolationDelta: `-11`
- lifecycleViolations: `0`
- regressed samples: `0`
- recall-loss degraded samples: `0`

正式 baseline 保持：

- A3：`50 total / 0 failed / 100.00%`
- Extended：`113 total / 1 failed / 99.12%`

## Phase P12 Constraint Corpus Gap Review

P12 不扩大 opt-in intent，不全局启用 planning，不改变 scoring / PackingPolicy，也不自动写 `ConstraintStore`。

在 P11 后，A3 hard constraint fallback 已降为 `0`；Extended 仍有部分 fallback 来自 constraint corpus 缺少对应 expected hard constraint。P12 将这些缺口整理为 `ConstraintGapCandidate`：

- 来源：`planning-optin-constraint-safety-report` / `extended-failure-triage-report`
- 生成前查询 scope 匹配 hard constraints
- 已有匹配 constraint 时不创建 gap
- 没有匹配时创建 `Pending` gap
- 去重键：workspace + collection + expectedConstraintText + sourceSampleId
- 只读 review，不触发 ConstraintStore 写入

新增 API：

- `POST /api/constraints/gaps/generate`
- `GET /api/constraints/gaps`
- `GET /api/constraints/gaps/{id}`

## 测试

新增覆盖：

- shadow execution does not affect legacy output
- invalid proposal falls back safely
- lifecycle filter still applies in shadow
- mustNotHit violation is reported
- validator rejects non-audit deprecated plan
- validator rejects rejected lifecycle plan
- validator forces vector disabled
- validator repairs high FinalTopK
- fallback only happens when repair fails
- invalid proposal falls back to legacy safe plan
- repaired proposal keeps mustNotHit violation = 0
- proposal service generates native safe TopK
- intent-specific defaults respected
- planning-shadow reports native valid / repaired breakdown
- planning shadow quality report computes global / mode / intent deltas
- planning shadow quality report marks regressed samples
- planning shadow quality recommendation logic works
- recall loss report identifies lost mustHit
- intent-specific reserve applies
- coverage floor prevents high importance loss
- safety validator still blocks mustNotHit
- formal retrieval output unchanged
- shadow selected item validator reports after-validation safety counts
- planning shadow diff triage reports must-not-hit additions
- comparison report contains added/dropped/rank delta
- A3 planning-shadow runs successfully
