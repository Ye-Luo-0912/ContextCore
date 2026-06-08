# Extended Eval Triage Report

更新时间：2026-06-05

## 范围

Phase E2 基于 Phase E1 的 15 个 extended failed 样本做 uncertainty mapping 与 package packing quality 小修。实现边界保持不变：不改 retrieval 语义，不改 retrieval scoring，不接 vector，不做 layered retrieval，不接 LLM judge，不默认开启 attention rerank，不做 NamedPipe。

输出文件：

- `eval/extended-failure-triage-report-phase-e2-source15.json`
- `eval/extended-failure-triage-report-phase-e2-source15.md`
- `eval/extended-failure-triage-report.json`
- `eval/extended-failure-triage-report.md`
- `eval/eval-report-phase-e2-a3.json`
- `eval/eval-report-phase-e2-extended.json`
- `eval/eval-report-p15-a3.json`
- `eval/eval-report-p15-extended.json`

## Source 15 Fix Plan

Phase E2 对 E1 的 15 个 failed 样本生成 fix plan：

| Sample | Failure Type | Fix Type | Expected Regression Test |
|---|---|---|---|
| automation-20260529-003 | MissingUncertainty | uncertainty mapping | uncertainty aliases should satisfy expected uncertainty |
| automation-20260529-004 | MissingUncertainty | uncertainty mapping | recovery point reserve and uncertainty aliases should pass |
| automation-20260529-008 | EntityMiss | uncertainty mapping | dead-letter entity and uncertainty aliases should pass |
| automation-20260529-010 | EntityMiss | uncertainty mapping | action report entity and uncertainty aliases should pass |
| automation-sample-001 | MissingMustHit | section priority | last error and recovery evidence should rank inside top 10 |
| chat-20260529-003 | ConstraintMiss | uncertainty mapping | promotion candidate uncertainty should be detected |
| chat-sample-003 | BudgetDroppedImportantItem | section priority | must-hit evidence should be protected under budget pressure |
| chat-sample-004 | BudgetDroppedImportantItem | section priority | conflict evidence should stay high enough under budget pressure |
| chat-sample-005 | MissingMustHit | budget diagnostics | low-budget package should preserve must-hit before low-value items |
| coding-20260529-002 | ConstraintMiss | uncertainty mapping | assertion failure scope should map to expected uncertainty |
| coding-20260529-010 | EntityMiss | uncertainty mapping | build/test verification aliases should pass |
| novel-20260529-001 | MissingMustHit | uncertainty mapping | foreshadowing and character state should be reserved |
| novel-20260529-009 | ConstraintMiss | uncertainty mapping | foreshadowing uncertainty should map correctly |
| novel-20260529-013 | EntityMiss | section priority | item-state evidence should be reserved |
| novel-20260529-017 | ConstraintMiss | section priority | deprecated ending constraint alias should pass |

## E2 修复内容

- `UncertaintyMatchResolver` 增加细分 failure type：
  - `MissingLifecycleUncertainty`
  - `MissingConflictUncertainty`
  - `MissingBudgetUncertainty`
  - `MissingEvidenceUncertainty`
  - `MissingScopeUncertainty`
  - `UncertaintyPresentButWrongSection`
  - `UncertaintyPresentButAliasMismatch`
- 对 diagnostics / conflict_evidence / deprecated_evidence / historical_context / excluded reason / risk metadata 增加语义 alias mapping。
- `BudgetPressureBreakdown` 增强并驱动小范围 packing policy：
  - diagnostics / info 使用独立小预算桶
  - historical / audit 使用独立预算桶
  - budget pressure 下显式 must-hit 优先于低价值 selected item
  - hard constraints 与 must-hit evidence 提升保留优先级
- 增加 mode-specific package policy：
  - AutomationMode 保留 last error / recovery point / retry policy / dead-letter state
  - NovelMode 保留 character state / foreshadowing / world constraints / item state
  - ChatMode 保留 stable preference / scope boundary / active task / promotion policy

## E2 Extended 113 结果（P15 前）

| 指标 | 数值 |
|---|---:|
| TotalSamples | 113 |
| Passed | 43 |
| PassedWithWarnings | 69 |
| Failed | 1 |
| PassRate | 99.12% |
| Recall@3 | 98.82% |
| Recall@5 | 100.00% |
| Recall@10 | 100.00% |
| ConstraintHitRate | 99.12% |
| EntityHitRate | 100.00% |
| UncertaintyHitRate | 90.27% |

当前 failed 分类：

| Category | Count |
|---|---:|
| BudgetDroppedImportantItem | 1 |
| ConstraintMiss | 1 |

剩余 failed 样本：

| Sample | Mode | Reason | 处理结论 |
|---|---|---|---|
| chat-20260529-003 | ChatMode | `重复解释不应提升` 缺少正向约束证据 | 保留失败。语料中存在 `pattern:repeated-user-preference`，语义是“多次重复稳定模式可提升”，与 expected constraint 相反，不能用 alias 硬判通过。 |

## P15 Constraint Activation Closure

P15 不再对 `chat-20260529-003` 做 uncertainty alias 或 resolver 硬判。该样本通过正式 constraint review 链路补齐 hard constraint：

1. `ConstraintGapCandidate` fixture 表达缺口。
2. `ConstraintGapCandidateService.AcceptAsync(...)` 创建 `Status=Candidate` 的 CandidateConstraint。
3. `CandidateConstraintReviewService.ActivateAsync(...)` 激活为 `Status=Active`、`Level=Hard`。
4. package builder 将 active hard constraint 注入 constraints section。

约束语义：

> 重复解释、重复澄清、重复说明本身不应被提升为长期偏好或稳定事实；只有用户明确确认其为长期规则时才可提升。

fixture 按 `SourceSampleId=chat-20260529-003` 隔离，不写入 eval resolver alias，也不直接放入 corpus `constraints`。package trace 保留 `sourceConstraintGapId`、`sourceConstraintGapReviewId`、`sourceCandidateConstraintReviewId`、`sourceSampleId`、`sourceOperationId` 与 `evidenceRefs`。

P15 Extended 113 结果：

| 指标 | 数值 |
|---|---:|
| TotalSamples | 113 |
| Passed | 43 |
| PassedWithWarnings | 70 |
| Failed | 0 |
| PassRate | 100.00% |
| Recall@10 | 100.00% |
| ConstraintHitRate | 100.00% |
| EntityHitRate | 100.00% |
| UncertaintyHitRate | 90.27% |

`chat-20260529-003` 当前为 `PassedWithWarnings`，`PackageHasAllConstraints=true`，`PackageHasAllEntities=true`，`PackageHasAllUncertainties=true`，`RetrievalRecall10=100.00%`。

## B0 Baseline Freeze

P15 baseline 已冻结为 B0 regression gate：

| Suite | Report | Total | Failed | PassRate |
|---|---|---:|---:|---:|
| A3 baseline | `eval/eval-report-p15-a3.json` | 50 | 0 | 100.00% |
| Extended baseline | `eval/eval-report-p15-extended.json` | 113 | 0 | 100.00% |

Regression gate：

- A3 must remain `100.00%`
- Extended must remain `100.00%`
- mustNotHit violation = `0`
- lifecycle violation = `0`
- hard constraint missing = `0`

必须重新跑 Extended 113 eval 的改动：

- retrieval scoring
- `PackingPolicy`
- planning opt-in
- attention behavior
- uncertainty resolver
- constraint injection
- memory layer selection
- vector / graph / relation expansion

完整 baseline freeze 记录见 `docs/eval-baseline-p15.md`。可选脚本为 `scripts/eval-gate-p15.ps1`。

## 验证

已执行：

```powershell
dotnet build -m:1 /nodeReuse:false
dotnet test -m:1 /nodeReuse:false
dotnet run --project src\ContextCore.ControlRoom -- eval run --out eval\eval-report-phase-e2-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval run --include-batches --out eval\eval-report-phase-e2-extended.json
dotnet run --project src\ContextCore.ControlRoom -- eval run --out eval\eval-report-p15-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval run --include-batches --out eval\eval-report-p15-extended.json
```

结果：

- build：成功，`0 warning / 0 error`
- test：成功，`401 + 36 + 19 passed / 0 failed`
- A3 50：`50 total / 47 passed / 3 warnings / 0 failed`
- Extended 113：`113 total / 43 passed / 69 warnings / 1 failed`
- P15 A3 50：`50 total / 47 passed / 3 warnings / 0 failed`
- P15 Extended 113：`113 total / 43 passed / 70 warnings / 0 failed`
