# ContextCore 新阶段执行报告

更新时间：2026-06-10

## 当前结论

本轮推进 Vector Index Foundation - Phase V3.9：Query Intent Expansion Shadow Baseline。

P15 baseline 继续作为冻结回归闸门。本轮只新增 query intent expansion profile/service、A3 / Extended expansion shadow eval、ControlRoom 只读摘要与报告输出；不接正式 retrieval，不改 scoring，不改 `PackingPolicy`，不让 vector 进入 package 输出。

### Phase V3.9

新增模型 / 实现：

- `VectorQueryExpansionProfile`
- `VectorQueryExpansionRequest`
- `VectorQueryExpansionResult`
- `VectorQueryExpansionShadowReport`
- `VectorQueryExpansionService`
- `VectorQueryExpansionShadowRunner`
- CLI：`eval vector-query-expansion-shadow`

新增输出：

- `eval/vector-query-expansion-shadow-a3.json`
- `eval/vector-query-expansion-shadow-extended.json`
- `eval/vector-query-expansion-shadow.md`

V3.9 行为：

- 支持 `raw-query-v1`、`mode-intent-query-v1`、`anchor-query-v1`、`intent-anchor-query-v1`、`planning-context-query-v1`、`constraint-aware-query-v1`。
- Expansion service 只组合运行时信号：mode、intent / routerIntent、task kind、planning snapshot、query anchors、working memory anchors、constraint hints 和安全过滤后的 request metadata。
- Expansion service 不读取 `mustHit` / `mustNotHit` label，不使用 sampleId / itemId / fixture 文件名特判，不内置领域词表、synonym 或 alias。
- Eval runner 只在报告层使用 label 计算 recall / risk；不会把 label 输入 expansion service 或 eligibility policy。
- V4 readiness gate 新增 expansion 条件：A3 / Extended expanded recall 均需 `>=80%`，且 expanded risk、mustNotHit risk、lifecycle risk 均为 `0`。

验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`720 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：通过，A3 50 / Extended 113 均 `100%`
- `eval vector-query-expansion-shadow --provider onnx-local`
  - A3 best expansion=`raw-query-v1`，`RecallBefore=71.21%`，`RecallAfter=71.21%`，`RiskAfterPolicy=0`，`Recommendation=NeedsBetterEmbedding`
  - Extended best expansion=`raw-query-v1`，`RecallBefore=84.38%`，`RecallAfter=84.38%`，`RiskAfterPolicy=0`，`Recommendation=ReadyForRetrievalShadow`
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`
  - `Passed=false`
  - `FailReasons=A3RecallAtLeast80Percent,A3FusionRecallAtLeast80Percent,A3ExpandedRecallAtLeast80Percent`

当前结论：query expansion baseline 没有引入 mustNotHit / lifecycle risk，但没有提升 A3 recall；vector 继续保持 preview / shadow-only，不得进入 retrieval shadow 或正式检索路径。

### Phase V3.8

新增模型 / 实现：

- `DocumentRepresentationProfiles`
- `QueryRepresentationProfiles`
- `VectorMissSetRepresentationAuditReport`
- `VectorRepresentationBenchmarkReport`
- `VectorMissSetRepresentationAuditRunner`
- CLI：`eval vector-representation-benchmark`

新增输出：

- `eval/vector-missset-representation-audit-a3.json`
- `eval/vector-missset-representation-audit-extended.json`
- `eval/vector-missset-representation-audit.md`
- `eval/vector-representation-benchmark-a3.json`
- `eval/vector-representation-benchmark-extended.json`
- `eval/vector-representation-benchmark.md`

V3.8 行为：

- 对 missed mustHit 输出 query anchors、document anchors、raw rank、eligible rank、miss reason、representation diagnosis 与 recommended repair。
- 对 `raw-content-v1`、`title-content-v1`、`title-summary-content-v1`、`anchor-enriched-v1`、`metadata-enriched-v1`、`compact-retrieval-text-v1` 进行 document representation benchmark。
- 对 `raw-query-v1`、`intent-query-v1`、`anchor-query-v1`、`mode-intent-query-v1`、`expanded-anchor-query-v1` 进行 query representation benchmark。
- Benchmark 使用临时 vector index，并只读继承当前 vector index entry 的 lifecycle/backfill runtime metadata；不写正式 index，不改变 retrieval/package 输出。
- 表示 profile 与 eligibility policy 不读取 eval label、sampleId、itemId、fixture 名称或领域词表。

V3.8 当前结果：

- `eval vector-representation-benchmark --provider onnx-local`
  - A3 best document=`metadata-enriched-v1`，best query=`mode-intent-query-v1`，`Recall=69.70%`，`MRR=0.7067`，`RiskAfterPolicy=0`，`Recommendation=NeedsBetterEmbedding`
  - Extended best document=`metadata-enriched-v1`，best query=`mode-intent-query-v1`，`Recall=84.38%`，`MRR=0.8400`，`RiskAfterPolicy=0`，`Recommendation=ReadyForRetrievalShadow`
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`
  - `Passed=false`
  - `FailReasons=A3RecallAtLeast80Percent,A3FusionRecallAtLeast80Percent`

当前结论：representation benchmark 没有引入 mustNotHit / lifecycle risk，但 A3 recall 仍低于 80%，因此 vector 继续保持 preview / shadow-only，不得进入 retrieval shadow 或正式检索路径。

验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`715 passed / 0 failed`
- `eval vector-representation-benchmark --provider onnx-local`：A3 / Extended representation benchmark 与 miss-set audit 已生成
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`：gate 已刷新，当前因 A3 recall 与 A3 fusion recall 未达 80% 未通过

### Phase V3.7

新增模型 / 实现：

- `VectorRankerFusionShadowReport`
- `VectorRankerFusionStrategyResult`
- `VectorRankerFusionShadowSample`
- `VectorRankerFusionCandidate`
- `VectorRankerFusionShadowRunner`
- CLI：`eval vector-ranker-fusion-shadow`

新增输出：

- `eval/vector-ranker-fusion-shadow-a3.json`
- `eval/vector-ranker-fusion-shadow-extended.json`
- `eval/vector-ranker-fusion-shadow.md`

Fusion shadow 行为：

- 支持 `VectorOnly`、`RankerOnly`、`UnionThenRank`、`VectorBoostedRanker`、`RankerFilteredVector`、`LifecycleAwareFusion`。
- `VectorOnly` 保持现有 vector query shadow 语义：先取原始 topK，再应用 eligibility policy。
- 其他 fusion 策略只在更宽只读候选池上做 shadow 排序分析；不写 store，不改变正式 retrieval/package 输出。
- 风险候选不会被静默视为安全；mustNotHit / lifecycle risk 由 eval report 计数并驱动 recommendation。

V3.7 当前结果：

- `eval vector-ranker-fusion-shadow --provider onnx-local`
  - A3 best=`VectorOnly`，`FusionRecall=71.21%`，`FusionMRR=0.6765`，`FusionRisk=0`，`Recommendation=NeedsFusionTuning`
  - Extended best=`VectorOnly`，`FusionRecall=84.38%`，`FusionMRR=0.8229`，`FusionRisk=0`，`Recommendation=ReadyForRetrievalShadow`
  - `RankerFilteredVector` 可追回个别 mustHit，但引入 mustNotHit 风险，因此 `Recommendation=BlockedByRisk`
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`
  - `Passed=false`
  - `FailReasons=A3RecallAtLeast80Percent,A3FusionRecallAtLeast80Percent`
  - fusion risk / lifecycle risk / newly risky / formal output 条件均未触发失败

当前结论：Extended 已满足 vector retrieval shadow 质量门槛；A3 在 risk=0 条件下仍未达到 80% recall，fusion baseline 暂未修复该缺口，因此 vector 继续保持 preview / shadow-only。

验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`710 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`，`MustNotHitViolation=0`，`LifecycleViolation=0`，`HardConstraintMissing=0`
- `eval vector-ranker-fusion-shadow --provider onnx-local`：A3 / Extended fusion shadow 已生成
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`：gate 已刷新，当前因 A3 recall 与 A3 fusion recall 未达 80% 未通过

### Phase V3.6.4

新增模型 / 实现：

- `VectorSafeRecallRecoveryReport`
- `VectorSafeRecallRecoverySweepResult`
- `VectorBlockedMustHitAuditRecord`
- `VectorRetrievalShadowReadinessGateReport`
- `VectorSafeRecallRecoveryRunner`
- CLI：`eval vector-safe-recall-recovery`
- CLI：`eval vector-retrieval-shadow-readiness-gate`

新增输出：

- `eval/vector-safe-recall-recovery-a3.json`
- `eval/vector-safe-recall-recovery-extended.json`
- `eval/vector-safe-recall-recovery.md`
- `eval/vector-retrieval-shadow-readiness-gate.json`
- `eval/vector-retrieval-shadow-readiness-gate.md`

Safe recall recovery 行为：

- 对 `BelowTopK` miss 扫描 `topK=10/20/30/50`、`minSimilarity=0.05/0.10/0.15/0.20/0.30`、`stable-only / candidate+stable / exclude-historical` 与 `normal-v1 / current-task-v1 / audit-v1 / diagnostics-v1`。
- 对 `BlockedByEligibilityPolicy` miss 输出阻断原因、resolved lifecycle、metadata completeness、replacement state、target section、classification 与 recommended repair。
- 高召回但带风险的配置只写入 tuning report，不作为 V4 gate 通过依据。
- 修正 `onnx-local` CLI 默认维度为 `512`，匹配当前 `bge-small-zh-v1.5` provider-scoped index；其他 ONNX 模型仍可显式 `--dimension` 覆盖。

硬边界：

- 不放松 lifecycle / deprecated / historical / provider / duplicate / orphan / stale safety gate。
- 不使用 itemId / sampleId / fixture 文件名 / mustHit / mustNotHit label / 领域词表做 policy 特判。
- eval label 只用于 audit/report，不进入 `VectorCandidateEligibilityPolicy`。
- 不接正式 retrieval，不改 scoring，不改 `PackingPolicy`，不让 vector 进入 package 输出。

V3.6.4 当前结果：

- `eval vector-safe-recall-recovery --provider onnx-local`
  - A3 baseline：`RecallAfterPolicy=71.21%`，`MRR=0.6765`，`RiskAfterPolicy=0`
  - A3 miss：`BelowTopK=10`，`BlockedMustHit=9`
  - A3 高召回 sweep 可达 `88%`，但 `RiskAfterPolicy > 0`，不能作为 safe recovery 配置
  - A3 最佳 risk-free sweep：`normal-v1:top10:min0.05:stable-only`，`RecallAfterPolicy=3.03%`
  - Extended baseline：`RecallAfterPolicy=84.38%`，`MRR=0.8229`，`RiskAfterPolicy=0`
  - Extended miss：`BelowTopK=16`，`BlockedMustHit=9`
  - blocked mustHit 当前均为 `DeprecatedMustHitBlockedCorrectly`
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`
  - `Passed=false`
  - `FailReasons=A3RecallAtLeast80Percent`
  - risk / mustNotHit / lifecycle / formal output 条件均未触发失败

当前结论：Extended 继续满足 retrieval shadow 候选质量；A3 在保持 risk=0 时 recall 未达 80%，因此 V4 gate 不通过，vector 继续保持 preview / shadow-only。

验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`707 passed / 0 failed`
- `eval vector-safe-recall-recovery --provider onnx-local`：A3 / Extended safe recall recovery 已生成
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`：gate 已生成，当前因 A3 recall 未达 80% 未通过
- `eval vector-query-shadow-eval --provider onnx-local`：A3 / Extended shadow eval 已刷新
- `eval vector-query-profile-sweep --provider onnx-local`：A3 / Extended profile sweep 已刷新

### Phase V3.6.3

新增模型 / 实现：

- `VectorRecallLossAuditReport`
- `VectorRecallLossMiss`
- `VectorIntentReadinessReport`
- `VectorIntentReadinessBucket`
- `VectorRecallLossAuditRunner`
- CLI：`eval vector-recall-loss-audit`

新增输出：

- `eval/vector-recall-loss-audit-a3.json`
- `eval/vector-recall-loss-audit-extended.json`
- `eval/vector-recall-loss-audit.md`

Recall loss audit 行为：

- 对每个 eval sample 执行当前配置预览与宽口径诊断预览。
- 当前配置预览用于判断 policy 后是否召回 mustHit。
- 宽口径诊断预览只用于解释 miss reason，不进入 eligibility policy。
- 输出 `BelowTopK`、`BelowSimilarityThreshold`、`BlockedByEligibilityPolicy`、`LayerFilterExcluded`、`ItemKindFilterExcluded`、`NoCandidateGenerated`、`LowSimilaritySeparation`、`RequiresRankerFusion` 等原因。
- 按 Mode / Intent 聚合 readiness，辅助判断哪些 intent 只适合 preview-only，哪些可进入后续 retrieval shadow 评估。

硬边界：

- 不放松 lifecycle / deprecated / historical / provider / duplicate / orphan / stale safety gate。
- 不使用 itemId / sampleId / fixture 文件名 / mustHit / mustNotHit label / 领域词表做 policy 特判。
- eval label 只用于 audit/report，不进入 `VectorCandidateEligibilityPolicy`。
- 不接正式 retrieval，不改 scoring，不改 `PackingPolicy`，不让 vector 进入 package 输出。

V3.6.3 当前结果：

- `eval vector-recall-loss-audit --provider onnx-local`
  - A3：`MustHitRecallAfterPolicy=71.21%`，`MustHitMrrAfterPolicy=0.6765`，`RiskAfterPolicy=0`，`MissedMustHitCount=19`，`Recommendation=NeedsProfileTuning`
  - A3 miss reason：`BelowTopK=10`，`BlockedByEligibilityPolicy=9`
  - Extended：`MustHitRecallAfterPolicy=84.38%`，`MustHitMrrAfterPolicy=0.8229`，`RiskAfterPolicy=0`，`MissedMustHitCount=25`，`Recommendation=ReadyForRetrievalShadow`
  - Extended miss reason：`BelowTopK=16`，`BlockedByEligibilityPolicy=9`
- A3 readiness：
  - ready：`FuzzyQuestion`、`NovelGeneration`
  - needs tuning：`AuditDeprecated`、`AutomationRecovery`、`CodingTask`
- Extended readiness：
  - ready：`AutomationRecovery`、`CodingTask`、`ConflictCheck`、`CurrentTask`、`FuzzyQuestion`、`LongTermPreference`、`NovelGeneration`
  - needs tuning：`AuditDeprecated`

当前结论：V3.6.3 已把 A3 recall loss 拆分为 TopK 覆盖不足与资格策略阻断两类主要问题；Extended 已具备后续 retrieval shadow 评估候选质量，但 A3 仍需 profile/topK 或 audit-profile 分流调优。本轮保持 vector preview / shadow-only。

验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`703 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`，`MustNotHitViolation=0`，`LifecycleViolation=0`，`HardConstraintMissing=0`
- `eval vector-query-shadow-eval --provider onnx-local`：A3 / Extended shadow eval 已刷新
- `eval vector-query-profile-sweep --provider onnx-local`：A3 / Extended profile sweep 已刷新

### Phase V3.6.2

新增模型 / 实现：

- `VectorLifecycleMetadataBackfillPlan`
- `VectorLifecycleMetadataBackfillCandidate`
- `VectorLifecycleMetadataBackfillResult`
- `VectorLifecycleMetadataBackfillPlanner`
- `VectorSourceLifecycleMetadataResolver` 支持读取 `vectorLifecycleBackfill.*` sidecar metadata
- CLI：`eval vector-lifecycle-metadata-backfill-plan`
- CLI：`eval vector-lifecycle-metadata-backfill-apply --confirm`

新增输出：

- `eval/vector-lifecycle-metadata-backfill-plan.json`
- `eval/vector-lifecycle-metadata-backfill-plan.md`
- `eval/vector-lifecycle-metadata-backfill-result.json`
- `eval/vector-lifecycle-metadata-backfill-result.md`

Backfill 行为：

- plan 默认只读，统计 unknown lifecycle、auto resolvable、manual review required、cannot resolve、expected coverage after。
- apply 必须显式 `--confirm`。
- apply 只更新 provider-scoped vector entry 的 sidecar metadata，不修改业务 source item。
- sidecar metadata 包含 lifecycle、reviewStatus、metadataSource、reason、evidenceMetadataKeys、policyVersion、appliedAt。

硬边界：

- planner 只使用运行时 metadata、source type、layer、itemKind、source tags、replacement metadata、createdFrom/sourceOperationId。
- 禁止 itemId / sampleId / fixture 文件名 / mustHit / mustNotHit label / 领域词表特判。
- 不接正式 retrieval，不改 scoring，不改 `PackingPolicy`，不让 vector 进入 package 输出。

V3.6.2 当前结果：

- backfill plan：`UnknownLifecycleBefore=9`，`AutoResolvableCount=9`，`ManualReviewRequiredCount=0`，`ExpectedCoverageAfter=100.00%`
- backfill apply：`updated=9`，`skipped=149`，`failed=0`
- lifecycle coverage：`KnownLifecycleCount=158/158`，`UnknownLifecycleCount=0`，`Recommendation=ReadyForVectorShadowEval`
- residual risk audit：
  - A3：`ResidualRiskCount=0`，`BeforeRepairRiskCount=174`，`AfterRepairRiskCount=0`，`BlockedByLifecycleMetadataGate=145`
  - Extended：`ResidualRiskCount=0`，`BeforeRepairRiskCount=215`，`AfterRepairRiskCount=0`，`BlockedByLifecycleMetadataGate=185`
- vector query shadow eval：
  - A3：`RiskAfterPolicy=0`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`，`MustHitRecallAfterPolicy=71.21%`，`Recommendation=KeepPreviewOnly`
  - Extended：`RiskAfterPolicy=0`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`，`MustHitRecallAfterPolicy=84.38%`，`Recommendation=ReadyForRetrievalShadow`
- vector query profile sweep：
  - A3 best：`normal-v1:top10:min0.10:all`，`RecallAfter=71.21%`，`MRR=0.6765`，`RiskAfter=0`
  - Extended best：`normal-v1:top10:min0.10:all`，`RecallAfter=84.38%`，`MRR=0.8229`，`RiskAfter=0`

当前结论：V3.6.2 已把 lifecycle coverage 恢复到 `100%`，且 policy 后 residual risk / must-not-hit risk / lifecycle risk 均为 `0`。Extended 已达到 `ReadyForRetrievalShadow`，但 A3 recall 仍不足，因此整体仍保持 preview / shadow-only，不进入正式 retrieval。

### Phase V3.6.1

新增模型 / 实现：

- `VectorLifecycleMetadataCoverageReport`
- `VectorLifecycleMetadataCoverageBucket`
- `VectorLifecycleMetadataCoverageReportBuilder`
- `VectorSourceLifecycleMetadataResolver`
- CLI：`eval vector-lifecycle-metadata-coverage`

新增输出：

- `eval/vector-lifecycle-metadata-coverage.json`
- `eval/vector-lifecycle-metadata-coverage.md`

Policy gate 更新：

- `normal-v1` / `current-task-v1` 要求 explicit lifecycle。
- unknown lifecycle -> `UnknownLifecycleBlocked`
- lifecycle metadata incomplete -> `LifecycleMetadataIncompleteBlocked`
- missing replacement metadata -> `ReplacementMetadataMissingBlocked`
- legacy/deprecated/historical source 缺 lifecycle -> `LegacySourceRequiresLifecycleMetadata`
- historical source 需要 audit profile -> `HistoricalSourceRequiresAuditProfile`
- `audit-v1` / `diagnostics-v1` 仍可只读观察 unknown / historical candidate，但 target section 必须是 `audit_context` / `diagnostics_only`，不允许进入 `normal_context`。

硬边界：

- resolver / policy 只读取运行时 metadata、lifecycle、reviewStatus、replacement metadata、source type、layer、itemKind 与 vector diagnostics。
- 不读取 eval label、sampleId、fixture name、itemId 特判或领域词表。
- coverage report 只读统计，不补写 metadata，不写 index。

V3.6.1 当前结果：

- `eval vector-lifecycle-metadata-coverage --provider onnx-local`
  - `TotalVectorSourceItems=158`
  - `KnownLifecycleCount=149`
  - `UnknownLifecycleCount=9`
  - `LifecycleCoverageRate=94.30%`
  - `Recommendation=NeedsLifecycleMetadataBackfill`
- `eval vector-residual-risk-audit --provider onnx-local`
  - A3：`ResidualRiskCount=0`，`BeforeRepairRiskCount=222`，`AfterRepairRiskCount=0`，`BlockedByLifecycleMetadataGate=193`
  - Extended：`ResidualRiskCount=0`，`BeforeRepairRiskCount=270`，`AfterRepairRiskCount=0`，`BlockedByLifecycleMetadataGate=240`
- `eval vector-query-shadow-eval --provider onnx-local`
  - A3：`RiskAfterPolicy=0`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`
  - Extended：`RiskAfterPolicy=0`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`
- `eval vector-query-profile-sweep --provider onnx-local`
  - A3 best：`normal-v1:top10:min0.10:all`，`RecallAfter=50.00%`，`MRR=0.5664`，`RiskAfter=0`
  - Extended best：`normal-v1:top10:min0.10:all`，`RecallAfter=75.62%`，`MRR=0.7742`，`RiskAfter=0`

当前结论：V3.6.1 已把 ONNX policy 后 residual risk 压到 `0`，但 unknown-lifecycle gate 带来明显 recall loss，因此 recommendation 仍为 `KeepPreviewOnly`。下一步应做 source lifecycle metadata backfill / governance，而不是把 vector 接入正式 retrieval。

### Phase V3.5

新增 CLI / 输出：

- `eval embedding-provider-smoke`
- `eval/embedding-provider-smoke-report.json`
- `eval/embedding-provider-smoke-report.md`
- `appsettings.VectorEmbedding.sample.json`
- `docs/vector-embedding-provider-local-runbook.md`

Smoke test 检查：

- provider 是否 enabled
- `ModelPath` 是否存在
- `TokenizerPath` 是否存在
- tokenization 是否可执行
- ONNX inference 是否可执行
- 输出 dimension 是否匹配配置
- normalization 是否生效
- batch embedding 是否可执行

新增 diagnostics：

- `OnnxSessionFailed`

Provider-scoped 行为：

- `vector-reindex-plan`、`vector-reindex-apply`、`vector-query-preview`、`vector-query-shadow-eval`、`vector-query-profile-sweep` 支持 `--provider deterministic-hash|onnx-local`。
- `--provider onnx-local` 必须显式传入 `--model-path` / `--tokenizer-path` 或通过本地私有配置提供。
- provider / model 不一致时，query search 不会混用旧 index。
- dimension / normalization 不一致继续在 preview candidate 上暴露 diagnostics，并由 eligibility policy 阻断。
- ONNX provider 缺模型或 tokenizer 时，smoke / sweep / shadow 输出 diagnostics，不伪造质量指标。
- `vector-reindex-apply --provider onnx-local --confirm` 在 provider diagnostics 为 error 时生成 blocked report，不写 vector index。

Provider comparison 更新：

- `eval/vector-embedding-provider-comparison.json`
- `eval/vector-embedding-provider-comparison.md`
- 报告至少包含 `deterministic-hash` 和 `onnx-local` 两个 provider result。
- 新增字段：`MustHitMrrAfterPolicy`、`EligibleCandidateCount`、`NoCandidateCount`、`ProviderResults`。

模型文件边界：

- 不提交模型文件。
- 不提交 tokenizer / vocab 文件。
- `OnnxLocal` 默认关闭。
- 本地模型路径只通过本地配置或 CLI 参数传入。

V3.5 测试覆盖：

- missing model file -> smoke fail with `ModelFileMissing`
- missing tokenizer -> smoke fail with `TokenizerUnavailable`
- deterministic provider smoke 不要求模型文件
- provider/model scoped search 不混用旧 index
- dimension mismatch 仍作为 candidate diagnostics 被 policy 阻断
- provider comparison 支持 multi-provider report

V3.5 本地实测：

- `eval embedding-provider-smoke --provider onnx-local`：`Succeeded=True`，`EmbeddingModel=bge-small-zh-v1.5`，`Dimension=512`，diagnostics=`0`
- `eval vector-reindex-apply --provider onnx-local --confirm`：`created=0`，`updated=158`，`failed=0`
- `eval vector-index-coverage --provider onnx-local`：`IndexedItems=158/158`，`CoverageRate=100.00%`，`DuplicateCount=0`，`OrphanCount=0`，`DimensionMismatchCount=0`，`ProviderUnavailableCount=0`，`Recommendation=ReadyForVectorShadowEval`
- `eval vector-query-profile-sweep --provider onnx-local`：A3 best 为 `normal-v1:top20:min0.70:exclude-historical`，`RecallAfter=34.85%`，`MRR=0.4500`，`RiskAfter=0`；Extended best 为 `normal-v1:top20:min0.70:exclude-historical`，`RecallAfter=60.62%`，`MRR=0.6991`，`RiskAfter=0`
- `eval vector-query-shadow-eval --provider onnx-local`：A3 `RiskAfterPolicy=6`、Extended `RiskAfterPolicy=6`，`FormalOutputChanged=0`
- provider comparison：顶层 `Recommendation=BlockedByRisk`；deterministic-hash `SimilaritySeparation=0.0066` / `MustHitRecallAfterPolicy=3.75%`；onnx-local `SimilaritySeparation=0.0857` / `MustHitRecallAfterPolicy=84.38%` / `MustHitMrrAfterPolicy=0.8217`

当前结论：V3.5 已具备真实 provider 本地 smoke、provider-scoped reindex 和离线质量重跑能力。ONNX 本地模型显著优于 deterministic hash，但 policy 后仍有 mustNotHit 风险，因此继续保持 preview / shadow-only，不进入正式 retrieval。模型文件与 tokenizer 文件仍必须通过本地配置或 CLI 参数显式提供；本轮不新增、不提交模型或 tokenizer 文件，也不得通过假模型、样本名或领域词表伪造质量提升。

### Phase V3.4

新增 DTO / 契约：

- `EmbeddingProviderOptions`
- `EmbeddingProviderTypes`
- `IEmbeddingTokenizer`
- `EmbeddingTokenizationResult`
- `VectorEmbeddingProviderComparisonReport`

新增实现：

- `OnnxEmbeddingGenerator`
- `EmbeddingProviderDiagnosticsBuilder`
- `VectorEmbeddingProviderComparisonReportBuilder`

ProviderType 第一版支持：

- `DeterministicHash`
- `OnnxLocal`
- `Disabled`

V3.4 行为：

- `OnnxEmbeddingGenerator` 位于 `ContextCore.Embedding`，作为 V1 `IEmbeddingGenerator` adapter。
- ONNX tokenizer 通过 `IEmbeddingTokenizer` 注入，generator 不写死 tokenizer 实现。
- 默认 tokenizer 实现仍是 `BertWordPieceTokenizer`。
- 默认 provider 仍为 deterministic hash，不要求模型文件。
- `OnnxLocal` 只有在显式配置且模型/词表文件存在时才执行。
- 缺失模型 / tokenizer / unsupported pooling 通过 diagnostics 暴露，不伪造结果。

Provider diagnostics 新增：

- `ProviderUnavailable`
- `ModelFileMissing`
- `TokenizerUnavailable`
- `DimensionMismatch`
- `EmbeddingModelMismatch`
- `ProviderMismatch`
- `NormalizationMismatch`
- `UnsupportedPoolingStrategy`

Reindex / query preview 兼容性：

- provider / model / dimension / normalization 变化时，reindex plan 标记 `Update`，metadata 写入 `requiresReindex=true` 与 `changeReasons`。
- diagnostics 增加 `RequiresReindex`、`EmbeddingProviderChanged`、`EmbeddingModelChanged`、`DimensionChanged`。
- query preview 不再静默混用不同 provider/model/dimension/normalization 的 index entry；不兼容候选会被标记 diagnostics 并由 eligibility policy 阻断。

新增报告：

- `eval/vector-embedding-provider-comparison.json`
- `eval/vector-embedding-provider-comparison.md`

报告字段：

- `ProviderId`
- `ProviderType`
- `EmbeddingModel`
- `Dimension`
- `IndexedItems`
- `QueryCount`
- `AverageTopSimilarity`
- `PositiveAverageSimilarity`
- `NegativeAverageSimilarity`
- `SimilaritySeparation`
- `MustHitRecallAtK`
- `MustNotHitRiskAfterPolicy`
- `LifecycleRiskAfterPolicy`
- `Recommendation`

模型文件边界：

- 不提交 ONNX 模型文件、词表或 tokenizer 大文件。
- `ModelPath` / `TokenizerPath` 通过本地私有配置或 CLI 参数传入。
- 默认测试不需要模型文件。

V3.4 测试覆盖：

- missing model file -> `ProviderUnavailable` / `ModelFileMissing`
- disabled ONNX provider 不要求模型文件
- deterministic hash provider remains stable
- dimension mismatch -> query preview blocked with diagnostics
- provider/model changed -> `RequiresReindex`
- provider comparison report generated

V3.4 当前验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`680 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：已通过，A3 `50 total / 0 failed / 100% pass`，Extended `113 total / 0 failed / 100% pass`
- `eval vector-query-profile-sweep`：已生成 `eval/vector-query-profile-sweep-a3.json`、`eval/vector-query-profile-sweep-extended.json`、`eval/vector-query-profile-sweep.md`
- `eval vector-query-shadow-eval`：已生成 A3 / Extended shadow eval report；A3 `RawCandidateCount=500`、`EligibleCandidateCount=430`、`RiskAfterPolicy=0`，Extended `RawCandidateCount=1130`、`EligibleCandidateCount=965`、`RiskAfterPolicy=0`
- `eval/vector-embedding-provider-comparison.json`：默认 provider 为 `deterministic-hash` / `deterministic-hash-v1`，`IndexedItems=158`，`QueryCount=113`，`SimilaritySeparation=0.0066`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`Recommendation=NeedsRealEmbeddingProvider`

当前结论：V3.4 已具备真实 provider adapter 与诊断基础，但仍保持离线 comparison / preview-only。当前 deterministic hash baseline 仍显示 `NeedsRealEmbeddingProvider`，下一步如果引入本地 ONNX 模型，应先用 provider comparison 和 V3.3 sweep 验证语义分离与风险，不得直接接正式 retrieval。

### Phase V3.3

新增 DTO / 能力：

- `VectorQueryProfileSweepResult`
- `VectorQueryProfileSweepReport`
- `VectorEmbeddingQualityBaselineReport`
- `VectorQueryProfileSweepRunner`

V3.3 sweep 范围：

- profile：`normal-v1` / `current-task-v1` / `audit-v1` / `diagnostics-v1`
- topK：`3` / `5` / `10` / `20`
- minSimilarity：`0.10` 到 `0.80`
- layer filter：`all` / `stable-only` / `candidate-stable` / `exclude-historical`

V3.3 约束：

- policy 不读取 eval `mustHit` / `mustNotHit` label。
- policy 不读取 `sampleId`、fixture 名称或领域词表。
- eval runner 只在离线评估层使用 label 计算 recall / MRR / risk。
- 不通过领域词表、样本名或特殊 case 调整 sweep 结果。

新增 CLI / 报告：

- `eval vector-query-profile-sweep`
- `eval/vector-query-profile-sweep-a3.json`
- `eval/vector-query-profile-sweep-extended.json`
- `eval/vector-query-profile-sweep.md`
- `eval/vector-embedding-quality-baseline.json`
- `eval/vector-embedding-quality-baseline.md`

本轮 sweep 结果：

- A3 best：`normal-v1:top10:min0.10:exclude-historical`
  - `MustHitRecallAfterPolicy=9.09%`
  - `MustHitMrrAfterPolicy=0.0202`
  - `RiskAfterPolicy=0`
  - `SimilaritySeparation=0.4357`
  - `Recommendation=NeedsPolicyTuning`
- Extended best：`normal-v1:top20:min0.10:stable-only`
  - `MustHitRecallAfterPolicy=6.88%`
  - `MustHitMrrAfterPolicy=0.0243`
  - `RiskAfterPolicy=0`
  - `SimilaritySeparation=0.0335`
  - `Recommendation=NeedsPolicyTuning`
- Embedding quality baseline：
  - `PositiveAverageSimilarity=0.3661`
  - `NegativeAverageSimilarity=0.3595`
  - `SimilaritySeparation=0.0066`
  - `MustHitRecallAt20=12.50%`
  - `MustNotHitRiskAt20=4.10%`
  - `Recommendation=NeedsRealEmbeddingProvider`

ControlRoom Vector Index 页面新增 Shadow Quality Summary，只读展示最新 sweep 报告中的 current recommendation、best profile、best topK、best minSimilarity、riskAfterPolicy 与 similarity separation。该区块不自动触发 sweep，不写 vector index，不影响 retrieval/package。

本轮验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`674 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：已通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`
- `eval vector-query-profile-sweep`：已生成 V3.3 sweep 与 embedding quality baseline 报告
- `eval vector-query-shadow-eval`：已刷新 V3.2 / V3.3 policy 后风险报告

结论：candidate eligibility policy 能继续把 after-policy 风险压到 0，但 deterministic hash embedding 的语义分离和 must-hit recall 明显不足。V3.3 仍保持 preview / shadow-only，下一步若继续 vector 线，应优先引入真实 embedding provider 的离线质量基线，而不是把当前 hash embedding 接入正式 retrieval。

### Phase V3.2

新增 DTO / 能力：

- `VectorQueryProfile`
- `VectorCandidateEligibilityResult`
- `VectorCandidateBlockedReason`
- `VectorQueryProfileRegistry`
- `VectorCandidateEligibilityPolicy`

默认 profiles：

- `normal-v1`
- `current-task-v1`
- `audit-v1`
- `diagnostics-v1`

V3.2 行为：

- `query-preview` 新增 `ProfileId`。
- 候选新增 `RawRank`、`EligibilityStatus`、`BlockedReasons`、`TargetSection`、`RiskIfNormalSelected`、`RiskAfterPolicy`。
- `MinSimilarity` 改为 policy 阈值，低相似度候选以 `SimilarityBelowThreshold` 标记，不在 store query 阶段静默消失。
- `normal-v1` 阻断 deprecated / historical / rejected / candidate lifecycle、duplicate、orphan、dimension mismatch、stale embedding 与低相似度候选。
- `audit-v1` 可将 historical / deprecated candidate 路由到 `audit_context`，正确分区后不计为 normal selected 风险。
- policy 只读取运行时 metadata 与 vector diagnostics；不读取 eval `mustHit` / `mustNotHit` label、`sampleId`、fixture 名称或领域词表。

Shadow eval 新增指标：

- `RawCandidateCount`
- `EligibleCandidateCount`
- `BlockedCandidateCount`
- `RiskBeforePolicy`
- `RiskAfterPolicy`
- `MustHitRecallBeforePolicy`
- `MustHitRecallAfterPolicy`
- `MustNotHitRiskBeforePolicy`
- `MustNotHitRiskAfterPolicy`
- `LifecycleRiskBeforePolicy`
- `LifecycleRiskAfterPolicy`
- `BlockedByReason`

Recommendation 规则：

- `RiskAfterPolicy > 0` -> `BlockedByRisk`
- `EligibleCandidateCount = 0` -> `NeedsPolicyTuning`
- index 覆盖不足 -> `NeedsMoreIndexedData`
- 风险为 0 且存在 eligible candidate -> `ReadyForRetrievalShadow`

V3.2 测试覆盖：

- deprecated candidate 被 `normal-v1` 阻断
- historical candidate 被 `normal-v1` 阻断
- `audit-v1` 将 historical candidate 路由到 `audit_context`
- 低于阈值相似度候选被 `SimilarityBelowThreshold` 阻断
- duplicate / orphan diagnostics 会阻断候选
- policy 不读取 eval label
- shadow eval 不改变 formal retrieval output

本轮验证进度：

- `dotnet build`：已通过，`0 warning / 0 error`
- vector foundation 相关测试：已通过，`39 passed / 0 failed`
- `dotnet test`：已通过，`671 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：已通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`
- `eval vector-query-shadow-eval`：已生成 V3.2 before/after-policy 报告

V3.2 shadow eval 刷新：

- A3：`RawCandidateCount=500`，`EligibleCandidateCount=430`，`BlockedCandidateCount=70`，`RiskBeforePolicy=70`，`RiskAfterPolicy=0`，`Recommendation=NeedsPolicyTuning`
- Extended：`RawCandidateCount=1130`，`EligibleCandidateCount=965`，`BlockedCandidateCount=165`，`RiskBeforePolicy=165`，`RiskAfterPolicy=0`，`Recommendation=NeedsPolicyTuning`
- 主要阻断原因：`DeprecatedCandidateBlocked`
- 结论：V3.2 policy 已将 deprecated/lifecycle raw 风险降为 after-policy 0，但 deterministic hash preview recall 仍低，下一阶段应继续做 vector recall / quality tuning，不应接入正式 retrieval。

### Phase V3.1

新增 DTO / 能力：

- `VectorReindexSourceItem`
- `VectorIndexCoverageReport`
- `VectorIndexCoverageBucket`
- `VectorIndexCoverageReportBuilder`

V3.1 行为：

- Eval CLI 默认使用 `--source eval-corpus`，从 `eval/contexts/*/corpus*.json` 构造真实语料 source items。
- 默认索引空间为 `eval-vector` / `corpus`。
- 显式 `--source store` 时保留 V2 扫描 context / memory store 的行为。
- source item 按 `ItemId` 去重；重复执行 apply 使用稳定 `EntryId` upsert，不通过重复数据提高覆盖率。
- diagnostics / coverage 使用同一 external source items，避免把 eval-corpus vector entries 误判为 orphan。
- ControlRoom Vector Index 页面展示 Coverage Summary。

当前 V3.1 报告：

- `vector/reindex/vector-index-coverage-report.json`
- `vector/reindex/vector-index-coverage-report.md`
- `eval/vector-query-shadow-eval-a3.json`
- `eval/vector-query-shadow-eval-extended.json`
- `eval/vector-query-shadow-eval.md`

Coverage baseline：

- `TotalSourceItems=158`
- `IndexedItems=158`
- `CoverageRate=100.00%`
- `DuplicateCount=0`
- `OrphanCount=0`
- `DimensionMismatchCount=0`
- `ProviderUnavailableCount=0`
- `Recommendation=ReadyForVectorShadowEval`

Vector shadow eval 刷新：

- A3：`Samples=50`，`CandidateCount=500`，`IndexedCoverage=100.00%`，`Recommendation=BlockedByRisk`
- Extended：`Samples=113`，`CandidateCount=1130`，`IndexedCoverage=100.00%`，`Recommendation=BlockedByRisk`
- `BlockedByRisk` 来自 deterministic hash baseline 的 deprecated / lifecycle 噪声暴露。本轮只记录 baseline，不做 scorer 或 policy 修复。

V3.1 测试覆盖：

- empty index => `NeedsInitialIndexing`
- indexed entries > 0 => coverage rate > 0
- duplicate entries => `BlockedByDiagnostics`
- stale entries => `NeedsReindex`
- coverage report does not write index
- external eval source item reindex
- repeated external source apply does not create duplicate vector entries

V3.1 当前验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`666 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：已通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`
- `eval vector-reindex-plan`：`TotalCandidates=158`
- `eval vector-reindex-apply --confirm`：`created=158, updated=0, failed=0`
- `eval vector-index-diagnostics`：`DuplicateVectorEntry=0`，`DimensionMismatch=0`，`ProviderUnavailable=0`，`OrphanVectorEntry=0`
- `eval vector-index-coverage`：`Recommendation=ReadyForVectorShadowEval`
- `eval vector-query-shadow-eval`：不再返回空索引 `NeedsMoreIndexedData`

### Phase V3

新增 DTO：

- `VectorQueryPreviewRequest`
- `VectorQueryPreviewResult`
- `VectorQueryPreviewCandidate`
- `VectorQueryPreviewDiagnostics`
- `VectorQueryShadowEvalReport`
- `VectorQueryShadowEvalSample`

新增实现：

- `VectorQueryPreviewService`
- `VectorQueryShadowEvalRunner`

Query Preview 行为：

- 使用 `IEmbeddingGenerator` 生成 query embedding。
- 使用独立 `IVectorIndexStore` 执行 brute-force cosine topK 查询。
- 支持 `Layer`、`ItemKind`、`MinSimilarity` filter。
- 只读执行，不写入 index。
- 返回 candidate similarity，同时透传 duplicate / stale / orphan / lifecycle-risk 诊断。
- TopK 限制在安全上限内，避免 preview 造成无意义扫描与输出噪声。

新增 Service API：

- `POST /api/vector/query-preview`

新增 Client 方法：

- `PreviewVectorQueryAsync`

ControlRoom 更新：

- Vector Index 页面新增 `Q` Query Preview。
- 需要手动输入 query text / topK / layer filter / minSimilarity。
- 不随页面刷新自动调用 query preview，避免重复查询制造噪声。

CLI 更新：

- `eval vector-query-preview`
- `eval vector-query-shadow-eval`
- 默认输出：
  - `vector/query/vector-query-preview.json`
  - `vector/query/vector-query-preview.md`
  - `eval/vector-query-shadow-eval-a3.json`
  - `eval/vector-query-shadow-eval-extended.json`
  - `eval/vector-query-shadow-eval.md`

Shadow eval 指标：

- `IndexedCoverage`
- `QueryCount`
- `CandidateCount`
- `MustHitRecallAtK`
- `MustNotHitRiskAtK`
- `LifecycleRiskAtK`
- `DeprecatedHitCount`
- `DuplicateHitCount`
- `AverageTopSimilarity`
- `NoCandidateCount`
- `LowConfidenceCount`
- `TopNoiseClusters`
- `Recommendation`

V3 测试覆盖：

- query preview returns nearest vector candidate
- layer filter works
- minSimilarity filter works
- empty index returns diagnostics
- duplicate / stale / orphan diagnostics surfaced
- shadow eval does not affect formal retrieval output
- mustNotHit risk is counted
- ContextCoreClient query preview route
- ControlRoom renders query preview

V3 初始验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`658 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：已通过
  - A3 baseline：`50 total / 0 failed / 100.00% pass`
  - Extended baseline：`113 total / 0 failed / 100.00% pass`
  - `MustNotHitViolationCount=0`
  - `LifecycleViolationCount=0`
  - `HardConstraintMissingCount=0`
- `eval vector-query-shadow-eval`：已生成空索引诊断报告
  - A3：`Samples=50`，`CandidateCount=0`，`Recommendation=NeedsMoreIndexedData`
  - Extended：`Samples=113`，`CandidateCount=0`，`Recommendation=NeedsMoreIndexedData`
  - 当前本地独立 vector index 未 reindex，报告按空索引诊断，不伪造候选或通过率。

### Phase V2

新增 DTO / interface：

- `VectorReindexRequest`
- `VectorReindexPlan`
- `VectorReindexPlanItem`
- `VectorReindexResult`
- `VectorReindexSummary`
- `VectorReindexSubmitResponse`
- `VectorReindexReportQueryResponse`
- `IVectorReindexReportStore`
- `ContextJobKind.VectorReindex`

新增实现：

- `VectorReindexPlanner`
- `VectorReindexExecutor`
- `VectorIndexingJobProcessor`
- `VectorReindexReportRenderer`
- `InMemoryVectorReindexReportStore`
- `FileVectorReindexReportStore`

Reindex plan 行为：

- 扫描 context / memory source items。
- 计算 source content hash。
- 对比独立 V1 vector index entries。
- 识别 `Create` / `Update` / `Skip` / `Duplicate` / `DeleteOrphan`。
- 计划阶段不写入 vector store。
- duplicate 只按同一 `workspace + collection + itemId + provider + model` 识别，避免把不同模型历史 entry 误报为重复噪声。

Apply 行为：

- 只有 `Apply=true`、`DryRun=false` 且 `ConfirmApply=true` 才写入。
- 使用 `DeterministicHashEmbeddingGenerator` 生成可重复 embedding。
- 仅对 `Create` / `Update` 执行 upsert。
- `Duplicate` / `DeleteOrphan` 第一版只报告，不自动清理，避免误删。
- 写入独立 V1 index，不影响旧 `IVectorStore`、正式 retrieval、package 或 `PackingPolicy`。

新增 Service API：

- `POST /api/vector/reindex-plan`
- `POST /api/vector/reindex-submit`
- `GET /api/vector/reindex-reports`
- `GET /api/vector/reindex-reports/{id}`

新增 Client 方法：

- `CreateVectorReindexPlanAsync`
- `SubmitVectorReindexAsync`
- `GetVectorReindexReportsAsync`
- `GetVectorReindexReportAsync`

ControlRoom 更新：

- Vector Index 页面新增：
  - `P` Reindex Plan
  - `A` Apply Reindex，必须输入 `YES`
  - `R` Reindex Reports
  - `D` Diagnostics / refresh
- Apply 只提交 `vector_reindex` job，不改变正式 retrieval/package 输出。

CLI 更新：

- `eval vector-reindex-plan`
- `eval vector-reindex-apply --confirm`
- `eval vector-index-diagnostics`
- 默认输出：
  - `vector/reindex/vector-reindex-report.json`
  - `vector/reindex/vector-reindex-report.md`
  - `vector/reindex/vector-index-diagnostics.json`
  - `vector/reindex/vector-index-diagnostics.md`

V2 测试覆盖：

- reindex plan detects missing entries
- stale entry detected by content hash
- apply creates vector entries
- apply updates stale entries
- duplicate entries are reported
- orphan entries are reported
- dry-run does not write vector entries
- apply requires explicit confirm
- FileSystem / InMemory stores pass
- ContextCoreClient vector reindex methods
- ControlRoom Apply requires `YES`

V2 当前验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`649 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：已通过
  - A3 baseline：`50 total / 0 failed / 100.00% pass`
  - Extended baseline：`113 total / 0 failed / 100.00% pass`
  - `MustNotHitViolationCount=0`
  - `LifecycleViolationCount=0`
  - `HardConstraintMissingCount=0`

### Phase V1

新增 DTO / interface：

- `VectorIndexEntry`
- `VectorIndexQuery`
- `VectorIndexSearchQuery`
- `VectorIndexSearchResult`
- `VectorIndexDiagnosticsReport`
- `VectorIndexStatusResponse`
- `VectorReindexPreviewRequest`
- `VectorReindexPreviewResponse`
- `EmbeddingGeneratorRequest`
- `EmbeddingGeneratorInput`
- `EmbeddingGeneratorResult`
- `IEmbeddingGenerator`
- `IVectorIndexStore`

新增实现：

- `MockEmbeddingGenerator`
- `DeterministicHashEmbeddingGenerator`
- `InMemoryVectorIndexStore`
- `FileVectorIndexStore`
- `VectorIndexService`

新增 Service API：

- `GET /api/vector/status`
- `GET /api/vector/diagnostics`
- `POST /api/vector/reindex-preview`

新增 Client 方法：

- `GetVectorStatusAsync`
- `GetVectorDiagnosticsAsync`
- `PreviewVectorReindexAsync`

ControlRoom 更新：

- Service Dashboard / 主菜单新增 `37` VectorIndex。
- 新增 Service Vector Index 只读页面。
- 页面展示 provider / model / dimension、indexed / stale / missing / duplicate / orphan counts、diagnostics 与 reindex preview。

存储与噪声控制：

- V1 index 写入独立 `vectors/vector-index.jsonl`，与旧 `vectors.jsonl` 分离。
- store 使用 `EntryId` upsert。
- diagnostics 会检测同一 `workspace + collection + itemId + provider + model` 下的 `DuplicateVectorEntry`，避免重复 entry 浪费存储空间或影响后续样本质量判断。
- `reindex-preview` 只返回 `Create` / `Update` / `Current` / `DeleteOrphan`，不执行写入或删除。

V1 验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过
- `scripts/eval-gate-p15.ps1`：已通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`

文档：

- `docs/vector-index-foundation.md`
- `docs/controlroom-service-mode.md`

---

上一轮完成 Graph Foundation - Phase G7.1：Guarded Opt-in Warning Classification & Freeze Gate。

P15 baseline 保持冻结：A3 50 和 Extended 113 均为 `0 failed / 100.00% pass`。本轮在 G7 guarded opt-in 基础上新增 warning 分类、expected/unexpected warning delta、guard status 和 `eval graph-expansion-guarded-optin-gate`；默认仍为 `Off`，只允许显式 opt-in 的 `audit-v1` / `conflict-v1`，不启用 `normal-v1` / `current-task-v1`，不改变 normal selected set，不改变 `RetrievalPackingPolicy`。

当前 `RetrievalPlanProposal` 仍只在显式 intent-scoped opt-in 下可影响 final selected。本轮没有扩大 opt-in intent，没有全局启用 planning proposal，没有改变 legacy scoring，没有改变 `PackingPolicy`，没有接 vector，没有接 LLM judge，没有做正式 layered retrieval，没有做 NamedPipe。

## 本轮完成

### Phase G7.1

- 新增 `GraphExpansionComparisonWarningKind`：
  - `AuxiliaryGraphSectionAdded`
  - `ExpectedAuditContextAdded`
  - `ExpectedConflictEvidenceAdded`
  - `GraphContributionDeduplicated`
  - `UnexpectedPackageWarningDelta`
  - `NormalSelectedSetChanged`
  - `DisallowedNormalContextInjection`
  - `RiskFallbackTriggered`
  - `MissingEvidenceDetected`
  - `LifecycleRiskDetected`
  - `WrongSectionRiskDetected`
- `graph-expansion-optin-comparison` 报告新增：
  - `ExpectedWarningDelta`
  - `UnexpectedWarningDelta`
  - `WarningDeltaByKind`
  - `DisallowedNormalContextInjection`
  - `GuardStatus`
- 合法辅助 section 增量归类为 expected：
  - `audit_context`
  - `conflict_evidence`
  - `historical_context`
  - `diagnostics_only`
- `normal_context` 增量永远不归类为 expected。
- 新增 freeze gate：
  - `eval graph-expansion-guarded-optin-gate`
  - `NormalSelectedSetChanged = 0`
  - `RiskAfterRoutingCount = 0`
  - `WrongSectionRiskCount = 0`
  - `MustNotHitRiskCount = 0`
  - `LifecycleRiskCount = 0`
  - `MissingEvidenceCount = 0`
  - `UnexpectedWarningDelta = 0`
  - `DisallowedNormalContextInjection = 0`
- ControlRoom Package Preview / Retrieval Debug 新增：
  - `ExpectedDelta`
  - `UnexpectedWarn`

### Phase G7

- 新增 `GraphExpansionApplyOptions`：
  - `Mode`: `Off` / `Shadow` / `ApplyGuarded`
  - `ApplyMode`: `ProfileScoped`
  - `OptInProfiles`
  - `AllowedTargetSections`
  - `DisallowNormalContextInjection`
  - `FallbackOnRisk`
  - `MaxAddedItemsPerPackage`
  - `EmitComparisonTrace`
- 默认配置保持安全：
  - `Mode=Off`
  - `OptInProfiles=[]`
  - `DisallowNormalContextInjection=true`
  - `FallbackOnRisk=true`
- 新增 `GraphExpansionApplyPolicy`：
  - 只允许 `audit-v1` / `conflict-v1`。
  - 只允许写入 `audit_context` / `conflict_evidence` / `historical_context` / `diagnostics_only`。
  - `riskAfterRouting` / `wrongSection` / `mustNotHit` / `lifecycle` / `missingEvidence` 任一风险非 0 时 fallback。
  - 同一 `targetSection + itemId` 只追加一次，避免重复 graph item 浪费存储和制造噪声。
- `BasicContextPackageBuilder` 在正常组包完成后追加 `GraphExpansionSectionContribution`：
  - 不改 `SelectedItems`。
  - 不改 `RetrievalPackingPolicy`。
  - 不向 `normal_context` 注入 graph item。
  - 辅助 section 内容标记 `source=graph_expansion_guarded`。
- package trace metadata 新增：
  - `graphExpansionMode`
  - `graphExpansionApplied`
  - `graphExpansionProfiles`
  - `graphExpansionAddedItems`
  - `graphExpansionTargetSections`
  - `graphExpansionFallbackUsed`
  - `graphExpansionFallbackReason`
  - `graphExpansionRiskChecks`
- ControlRoom Package Preview / Retrieval Debug 展示 graph expansion 状态。
- 新增 eval comparison：
  - `eval graph-expansion-optin-comparison`
  - `eval/graph-expansion-optin-comparison-a3.json`
  - `eval/graph-expansion-optin-comparison-extended.json`
  - `eval/graph-expansion-optin-comparison.md`

### Phase G5.3

- 新增 `GraphExpansionTargetSection` 标准分区：
  - `normal_context`
  - `working_context`
  - `stable_context`
  - `historical_context`
  - `audit_context`
  - `conflict_evidence`
  - `diagnostics_only`
  - `excluded`
- `RelationExpansionPreviewRelation` / validator 输出新增：
  - `targetSection`
  - `sectionReason`
  - `riskIfNormalSelected`
  - `riskAfterSectionRouting`
- `audit-v1` section-aware routing：
  - deprecated / historical target 进入 `audit_context`。
  - 正确分区后不再计为 normal must-not-hit / lifecycle risk。
  - 错误进入 `normal_context` 时报告 `BlockedByWrongSectionRisk`。
- `conflict-v1` section-aware routing：
  - `conflicts_with` / `superseded_by` / `replaces` / `replaced_by` target 进入 `conflict_evidence`。
  - evidence / confidence 不达标仍 blocked。
  - 错误进入 `normal_context` 时报告 `BlockedByWrongSectionRisk`。
- `normal-v1` / `current-task-v1` 保持安全策略：
  - 禁止 toward-historical。
  - 禁止 deprecated / historical target。
  - backward replacement traversal 继续 blocked。
- shadow report 新增指标：
  - `AcceptedToNormalContext`
  - `AcceptedToHistoricalContext`
  - `AcceptedToAuditContext`
  - `AcceptedToConflictEvidence`
  - `AcceptedToDiagnosticsOnly`
  - `RiskIfNormalSelected`
  - `RiskAfterSectionRouting`
  - `HistoricalAuditExpansion`
  - `ConflictEvidenceExpansion`
  - `WrongSectionRisk`
- recommendation 新增：
  - `ReadyForAuditShadow`
  - `ReadyForConflictShadow`
  - `ReadyForSectionAwareShadow`
  - `BlockedByWrongSectionRisk`
- ControlRoom Relation Expansion Preview 展示：
  - `section`
  - `sectionReason`
  - `riskNormal`
  - `riskAfterRouting`
- 重新生成报告：
  - `eval/relation-expansion-profile-shadow-report.json`
  - `eval/relation-expansion-profile-shadow-report.md`
  - `eval/relation-expansion-shadow-eval-a3.json`
  - `eval/relation-expansion-shadow-eval-extended.json`
  - `eval/relation-expansion-shadow-eval.md`
- 当前 G5.3 shadow 结果：
  - A3：`FormalOutputChanged=0`，`SelectedSetChanged=0`。
  - Extended：`FormalOutputChanged=0`，`SelectedSetChanged=0`。
  - `audit-v1`：`ReadyForAuditShadow`，`RiskIfNormalSelected>0`，`RiskAfterSectionRouting=0`。
  - `conflict-v1`：`ReadyForConflictShadow`，`RiskIfNormalSelected>0`，`RiskAfterSectionRouting=0`。
  - `normal-v1` / `current-task-v1`：`KeepPreviewOnly`，`MustNotHitRisk=0`，`LifecycleRisk=0`。
- 新增/更新测试覆盖：
  - audit profile routes deprecated target to `audit_context`。
  - audit profile does not count correctly routed historical target as normal must-not-hit risk。
  - conflict profile routes old conflicting target to `conflict_evidence`。
  - normal profile still blocks historical target。
  - wrong section routing is reported as risk。
  - ControlRoom renders section-aware preview fields。
- 安全边界：
  - 不改正式 retrieval。
  - 不改正式 relation expansion。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不改变 package 输出或 selected set。

### Phase G5.2

- 新增 `RelationTraversalPolicy` DTO，并接入 `RelationExpansionProfile.TraversalPolicies`。
- `RelationExpansionPolicyValidator` 增加 replacement 方向与 target lifecycle 校验：
  - `superseded_by` / `replaced_by` 可表示 old -> latest。
  - `replaces` 在 normal / current-task profile 下禁止 new -> old。
  - deprecated / superseded / historical / rejected target 在 normal / current-task profile 下阻断。
  - audit profile 允许 historical target，但标记到 `audit/historical` section。
  - conflict profile 允许双向 replacement traversal，但仍要求 evidence / confidence 达标。
- 新增 blocked reason / report counter：
  - `BackwardReplacementTraversalBlocked`
  - `DeprecatedTargetBlocked`
  - `HistoricalTargetBlocked`
  - `AuditOnlyHistoricalTraversal`
  - `ReplacementTargetInactive`
  - `ReplacementTargetRejected`
  - `ReplacementTargetMissing`
  - `AllowedTowardLatest`
  - `BlockedTowardHistorical`
  - `HistoricalAllowedOnlyInAudit`
- 更新 relation expansion preview / shadow eval：
  - preview relation 输出 `traversalDirection` / `targetLifecycle` / `targetSection`。
  - sample/profile report 输出 backward / deprecated / historical target 阻断统计。
  - `current-task-v1` preview allowlist 覆盖 `conflicts_with`，由 lifecycle policy 而不是 type 缺口解释 deprecated target 阻断。
- 重新生成报告：
  - `eval/relation-expansion-profile-shadow-report.json`
  - `eval/relation-expansion-profile-shadow-report.md`
  - `eval/relation-expansion-shadow-eval-a3.json`
  - `eval/relation-expansion-shadow-eval-extended.json`
  - `eval/relation-expansion-shadow-eval.md`
- 当前 G5.2 shadow 结果：
  - A3：`FormalOutputChanged=0`，`SelectedSetChanged=0`，`BlockedByMissingEvidence=0`。
  - Extended：`FormalOutputChanged=0`，`SelectedSetChanged=0`，`BlockedByMissingEvidence=0`。
  - `normal-v1`：`KeepPreviewOnly`，`MustNotHitRisk=0`，`LifecycleRisk=0`。
  - `current-task-v1`：`KeepPreviewOnly`，`MustNotHitRisk=0`，`LifecycleRisk=0`。
  - `audit-v1` / `conflict-v1` 仍为 `BlockedByRisk`，用于保留 historical / conflict-direction shadow 风险信号。
- 新增/更新测试覆盖：
  - normal profile blocks `replaces` from new item to old item。
  - normal profile allows `superseded_by` from old item to active replacement。
  - current-task profile blocks historical target。
  - audit profile allows historical target and marks audit/historical section。
  - conflict profile allows replacement both directions with evidence。
  - blocked reasons are surfaced in preview and report。
- 安全边界：
  - 不改正式 retrieval。
  - 不改正式 relation expansion。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不改变 package 输出或 selected set。

### Phase G1

- 新增 `RelationTypeDefinition` DTO：
  - `Type`
  - `IsDirectional`
  - `InverseType`
  - `DefaultWeight`
  - `RequiresEvidence`
  - `AuditOnly`
  - `AllowsNormalExpansion`
  - `AllowedSourceKinds`
  - `AllowedTargetKinds`
  - `Warnings`
- 新增 `RelationTypeRegistry`，第一版定义：
  - `contains`
  - `references`
  - `derived_from`
  - `evidence_for`
  - `supports`
  - `depends_on`
  - `requires`
  - `blocks`
  - `conflicts_with`
  - `applies_to`
  - `superseded_by`
  - `replaces`
  - `replaced_by`
  - `same_as`
  - `related_to`
- 新增 `RelationGraphValidationService`。
- 支持 diagnostics：
  - `UnknownRelationType`
  - `MissingInverseRelation`
  - `BrokenSource`
  - `BrokenTarget`
  - `MissingEvidence`
  - `InvalidDirection`
  - `InvalidSourceKind`
  - `InvalidTargetKind`
  - `DuplicateRelation`
  - `ConflictingRelation`
  - `SupersedeCycle`
  - `WeakRelatedToOveruse`
  - `AuditOnlyRelationInNormalPath`
- 对 S3 supersede / replace relation 增加专项验证：
  - `superseded_by` 必须有 `replaces` inverse。
  - replacement target 必须存在。
  - replacement target 不得 rejected / deprecated / superseded。
  - replacement graph 不得形成 cycle。
- 新增 endpoint：
  - `GET /api/relations/types`
  - `GET /api/relations/diagnostics`
  - `GET /api/relations/diagnostics/{itemId}`
- `ContextCoreClient` 新增：
  - `GetRelationTypesAsync(...)`
  - `GetRelationDiagnosticsAsync(...)`
  - `GetItemRelationDiagnosticsAsync(...)`
- ControlRoom Relations 页面增强：
  - 展示 Relation Types。
  - 展示全局 Relation Diagnostics。
  - 输入 itemId 后展示 incoming / outgoing relations 和 item diagnostics。
  - 只读，不做自动修复。
- 新增文档：
  - `docs/graph-foundation.md`
- 安全边界：
  - 不改 retrieval。
  - 不改 relation expansion。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不改变 package 输出。

### Phase S3

- `Supersede` 操作现在写入 `IRelationStore`：
  - `oldItem --superseded_by--> replacementItem`
  - `replacementItem --replaces--> oldItem`
- relation metadata 固定记录：
  - `source=stable_lifecycle_review`
  - `reviewId`
  - `reviewer`
  - `reason`
  - `createdAt`
  - `confidence=1.0`
  - `lifecycle=Active`
- 新增 `StableReplacementChainResponse`。
- 新增 endpoint：
  - `GET /api/memory/stable/{id}/replacement-chain`
- `ContextCoreClient` 新增：
  - `GetStableReplacementChainAsync(...)`
- ControlRoom Stable Memory 页面新增：
  - `C <id>` replacement chain。
  - 展示 current / root / latest item、previous / next items、replacement relations、warnings。
- Stable diagnostics 新增：
  - `SupersededWithoutRelation`
  - `MetadataRelationMismatch`
  - `BrokenReplacementLink`
  - `ReplacementTargetMissing`
  - `ReplacementTargetInactive`
  - `ReplacementCycle`
  - `MultipleActiveReplacements`
  - `ScopeMismatchInReplacement`
- 新增测试覆盖：
  - supersede writes `superseded_by` relation。
  - replacement writes `replaces` relation。
  - replacement chain returns latest item。
  - diagnostics detects metadata/relation mismatch。
  - diagnostics detects broken replacement link。
  - diagnostics detects replacement cycle。
  - ControlRoom renders replacement chain。
- 安全边界：
  - 不编辑 stable 内容。
  - 不自动 merge。
  - 不改 retrieval scoring。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不做 NamedPipe。
  - 不改变 package selected set。

### Phase S2

- 新增 Stable lifecycle review DTO：
  - `StableLifecycleReviewRequest`
  - `StableLifecycleReviewResult`
  - `StableLifecycleReviewRecord`
- 支持人工 lifecycle 操作：
  - `Deprecate`
  - `Supersede`
  - `Reject`
- 新增 `IStableLifecycleReviewStore`。
- 新增 store 实现：
  - `InMemoryStableLifecycleReviewStore`
  - `FileStableLifecycleReviewStore`
- 新增 `StableLifecycleReviewService`：
  - 校验 stable item exists。
  - 校验 transition legal。
  - `Supersede` 必须提供 replacement item。
  - replacement item 必须存在，且不能是 rejected / deprecated / superseded。
  - old item metadata 写入 `supersededBy`。
  - replacement item metadata 写入 `replaces`。
  - provenance 缺失只写 warning，不阻塞人工 review。
  - 所有操作写入 stable lifecycle review history。
- 新增 endpoint：
  - `POST /api/memory/stable/{id}/deprecate`
  - `POST /api/memory/stable/{id}/supersede`
  - `POST /api/memory/stable/{id}/reject`
  - `GET /api/memory/stable/{id}/reviews`
- `ContextCoreClient` 新增：
  - `DeprecateStableMemoryAsync(...)`
  - `SupersedeStableMemoryAsync(...)`
  - `RejectStableMemoryAsync(...)`
  - `GetStableMemoryReviewsAsync(...)`
- ControlRoom Stable Memory 页面增强：
  - `X <id>` deprecate。
  - `S <id>` supersede。
  - `R <id>` reject。
  - `H <id>` review history。
  - 操作前展示 detail / explain / diagnostics，并要求输入 `YES`。
- 新增测试覆盖：
  - deprecate stable item records review。
  - reject stable item records review。
  - supersede stable item requires replacement。
  - supersede writes `supersededBy` / `replaces` metadata。
  - invalid transition returns structured error source。
  - HTTP endpoint invalid transition returns `ContextCoreErrorResponse`。
  - ControlRoom renders stable lifecycle review result。
- 安全边界：
  - 不编辑 stable 内容。
  - 不自动 merge。
  - 不自动 stable promotion。
  - 不改 retrieval scoring。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不做 NamedPipe。
  - 不改变 package selected set。

### Phase S1

- 新增 Stable Memory governance DTO：
  - `StableMemorySnapshot`
  - `StableMemoryRecord`
  - `StableMemoryExplanation`
  - `StableMemoryDiagnosticsReport`
  - `StableMemoryDiagnostic`
- 新增诊断类型：
  - `DuplicateStableMemory`
  - `PossibleConflict`
  - `MissingProvenance`
  - `MissingEvidenceRefs`
  - `StableWithoutReviewSource`
  - `StableConstraintWithoutScope`
  - `DecisionRecordWithoutSource`
  - `DeprecatedStillActive`
  - `SupersededWithoutReplacement`
  - `GlobalMemoryScopeRisk`
- 新增 `StableMemoryGovernanceService`：
  - 聚合 `Layer=Stable` 的 memory。
  - 聚合非 Candidate 的 constraints 作为长期 constraint 治理对象。
  - 从 type / metadata 识别 decision records。
  - 聚合 global memory。
  - 复用 `ContextProvenanceService` 返回 explain 来源链。
  - 诊断只读输出，不自动修复、不改变状态。
- 新增 endpoint：
  - `GET /api/memory/stable/snapshot`
  - `GET /api/memory/stable/diagnostics`
  - `GET /api/memory/stable/{id}/explain`
- `ContextCoreClient` 新增：
  - `GetStableMemorySnapshotAsync(...)`
  - `GetStableMemoryDiagnosticsAsync(...)`
  - `ExplainStableMemoryAsync(...)`
- ControlRoom Service Mode 新增 Stable Memory 页面：
  - `36` 从主菜单或 Service Dashboard 进入。
  - 展示 snapshot counts、recent stable items、diagnostics。
  - 支持 detail、`E <id>` explain、`P <id>` provenance。
  - 页面只读，不提供 accept / reject / expire / supersede / activate。
- 新增文档：
  - `docs/stable-memory-governance.md`
- 更新文档：
  - `docs/controlroom-service-mode.md`
  - `docs/ContextCore_新阶段执行报告.md`
- 新增测试覆盖：
  - stable snapshot counts stable memories。
  - diagnostics detects missing provenance。
  - diagnostics detects duplicate stable memory。
  - diagnostics detects superseded without replacement。
  - explain returns provenance chain。
  - ControlRoom renders stable memory page。
  - `ContextCoreClient` stable governance routes。
- 安全边界：
  - 不做 stable 写操作。
  - 不改 retrieval scoring。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不做 NamedPipe。
  - 不改变 package selected set。

### Phase M2

- 新增 CandidateMemory review DTO：
  - `CandidateMemoryReviewRequest`
  - `CandidateMemoryReviewResult`
  - `CandidateMemoryReviewRecord`
- 新增 review action：
  - `MarkReadyForStableReview`
  - `NeedsMoreEvidence`
  - `Reject`
  - `Expire`
  - `Supersede`
- 新增 `ICandidateMemoryReviewStore`。
- 新增 store 实现：
  - `InMemoryCandidateMemoryReviewStore`
  - `FileCandidateMemoryReviewStore`
- 新增 `CandidateMemoryReviewService`：
  - 校验 candidate exists。
  - 校验 legal status transition。
  - 校验 supersede target exists。
  - 阻止 stable / active item 通过 candidate endpoint 修改。
  - provenance 缺失只写 warning，不自动阻断。
  - 所有操作都写入 review history。
- 新增 endpoint：
  - `POST /api/memory/candidates/{id}/ready-for-stable-review`
  - `POST /api/memory/candidates/{id}/needs-more-evidence`
  - `POST /api/memory/candidates/{id}/reject`
  - `POST /api/memory/candidates/{id}/expire`
  - `POST /api/memory/candidates/{id}/supersede`
  - `GET /api/memory/candidates/{id}/reviews`
- `ContextCoreClient` 新增：
  - `MarkCandidateMemoryReadyForStableReviewAsync(...)`
  - `MarkCandidateMemoryNeedsMoreEvidenceAsync(...)`
  - `RejectCandidateMemoryAsync(...)`
  - `ExpireCandidateMemoryAsync(...)`
  - `SupersedeCandidateMemoryAsync(...)`
  - `GetCandidateMemoryReviewsAsync(...)`
- ControlRoom Candidate Memory 页面增强：
  - `Ready <id>`
  - `N <id>` / `Needs <id>`
  - `Reject <id>`
  - `Expire <id>` / `X <id>`
  - `Supersede <id>` / `U <id>`
  - `H <id>`
  - mutation 前展示 detail + explain，并要求输入 `YES`。
- diagnostics 增强：
  - `CandidateMemoryDiagnostic.SuggestedAction`
  - 只输出建议，不自动执行。
- `explain` 增强：
  - 返回 `CandidateMemoryReviewHistory`。
- 新增测试覆盖：
  - reject candidate records review。
  - expire stale candidate records review。
  - needs-more-evidence records review。
  - supersede candidate validates target。
  - invalid transition returns `ContextCoreErrorResponse`。
  - ControlRoom requires confirmation。
- 安全边界：
  - 不自动 stable promotion。
  - 不改 retrieval scoring。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不做 NamedPipe。
  - 不改变 package selected set。

### Phase M1

- 新增中期候选记忆治理 DTO：
  - `CandidateMemorySnapshot`
  - `CandidateMemoryRecord`
  - `CandidateMemoryExplanation`
  - `CandidateMemoryProvenanceLink`
  - `CandidateMemoryDiagnosticsReport`
  - `CandidateMemoryDiagnostic`
- 新增 `CandidateMemorySnapshotService`：
  - 聚合 `Status=Candidate` 的 candidate memory。
  - 聚合 `Status=Candidate` 的 candidate constraints。
  - 从 type / metadata 识别 candidate decisions。
  - 关联 promotion candidate、stable review candidate、constraint gap、feedback signal、learning case 与 review history。
  - 输出统一 `CandidateMemoryRecord`，供 Service API、client、ControlRoom 与测试复用。
- 新增只读 endpoint：
  - `GET /api/memory/candidates/snapshot`
  - `GET /api/memory/candidates/diagnostics`
  - `GET /api/memory/candidates/{id}`
  - `GET /api/memory/candidates/{id}/explain`
- `explain` 返回：
  - source promotion candidate
  - source stable review candidate
  - source constraint gap
  - source feedback signal
  - source learning case
  - evidence refs
  - promotion / stable / constraint gap / candidate constraint review history
  - provenance chain
  - risk flags
- diagnostics 覆盖：
  - duplicate candidate
  - stale candidate
  - candidate without evidence
  - candidate with rejected source
  - candidate conflicts with active stable memory
  - candidate superseded by newer candidate
- `ContextCoreClient` 新增：
  - `GetCandidateMemorySnapshotAsync(...)`
  - `GetCandidateMemoryAsync(...)`
  - `ExplainCandidateMemoryAsync(...)`
  - `GetCandidateMemoryDiagnosticsAsync(...)`
- ControlRoom Service Mode 新增 Candidate Memory 页面：
  - `35` 从主菜单或 Service Dashboard 进入。
  - 展示 snapshot counts、warnings、recent candidates。
  - 展示 diagnostics。
  - 支持 detail 与 `E <id>` explain。
  - 页面只读，不 accept / reject / promote / activate，不编辑配置。
- 更新文档：
  - `docs/mid-term-memory-governance.md`
  - `docs/controlroom-service-mode.md`
  - `docs/ContextCore_新阶段执行报告.md`
- 安全边界：
  - 不改 retrieval scoring。
  - 不改 `PackingPolicy`。
  - 不改 planning / attention。
  - 不接 vector。
  - 不接 LLM judge。
  - 不做 NamedPipe。
  - 不改变 package selected set。

### Phase 6H

- 新增 runbook：
  - `docs/ranker-shadow-trace-collection-runbook.md`
- Runbook 覆盖：
  - 如何开启 `Learning:RankerShadow:TraceCollectionEnabled`。
  - 如何设置 `MaxCandidatesPerTrace`。
  - 推荐采样场景。
  - 如何导出 traces。
  - 如何运行 `eval ranker-shadow-trace-quality`。
  - 如何关闭采集。
  - 如何解释 quality report recommendation。
- 新增采集脚本：
  - `scripts/collect-ranker-shadow-traces.ps1`
  - 默认 dry-run，不调用服务。
  - 显式 `-Execute` 后才调用已运行的 `ContextCore.Service`。
  - 检查 `/api/status`（可选 warning）。
  - 检查 `/api/health/ready`（失败停止）。
  - 对固定场景调用 `/api/context/retrieve`、`/api/package/build-detailed`、`/api/retrieval/ranker-shadow/debug`。
  - 导出 `/api/learning/ranker-shadow/traces?format=jsonl`。
  - 运行 `eval ranker-shadow-trace-quality`。
- 固定采样场景覆盖：
  - Chat fuzzy preference / version conflict / deprecated noise
  - Novel character state / item state / old-vs-current setting
  - Project current task / deprecated design
  - Automation recovery / retry / dead-letter
  - Coding verification / deprecated interface
- 更新质量报告门槛：
  - `TraceCount >= 30`
  - `CandidateScoreCount > 0`
  - `MustHitDemotedCount = 0`
  - `MustNotHitPromotedCount = 0`
  - `LifecycleViolationCount = 0`
  - `DeprecatedDemotionCount > 0` 或 `VersionConflictFixCount > 0`
- 扩展 `RankerShadowTraceQualityReport`：
  - 新增 `LifecycleViolationCount`。
  - `ReadyForGuardedOptIn` recommendation 现在要求达到上述门槛。
- 安全保证：
  - 脚本仅在 `-Execute` 时做采样调用。
  - 采样调用不启用正式 scorer。
  - 不改变 retrieval scoring。
  - 不改变 selected set。
  - 不训练模型。

### Phase 6G

- 新增 DTO：
  - `RankerShadowTraceQualityReport`
  - `RankerShadowTraceQualityBreakdown`
  - `RankerShadowTraceRiskSample`
  - `RankerShadowTraceRecommendedNextSteps`
- 新增 `RankerShadowTraceQualityReportBuilder`：
  - 只读读取 runtime `RankerShadowTrace` records。
  - 统计 deprecated / historical demotion、version conflict fix、current version promotion、must-hit demotion、must-not-hit promotion。
  - 输出 mode / intent breakdown。
  - 输出 risk samples。
  - 输出 recommendation：
    - `KeepShadowOnly`
    - `ReadyForGuardedOptIn`
    - `NeedsMoreRealTraces`
    - `BlockedByRisk`
- 新增 eval CLI：
  - `eval ranker-shadow-trace-quality`
  - 支持 `--workspace`、`--collection`、`--take`、`--out`、`--md-out`。
- 新增报告输出：
  - `learning/baselines/ranker-shadow-trace-quality-report.json`
  - `learning/baselines/ranker-shadow-trace-quality-report.md`
- 当前本地报告：
  - `TraceCount=0`
  - `CandidateScoreCount=0`
  - `RecommendedNextStep=NeedsMoreRealTraces`
  - 原因：runtime trace collection 默认关闭，当前没有真实 runtime shadow traces。
- ControlRoom Ranker Shadow Debug 页面新增 Trace Quality Summary 只读区块：
  - 展示 trace count、candidate score count、demotion / promotion 统计、risk count、recommended next step。
  - 只读取 recent traces，不触发 retrieval。
- 安全保证：
  - 不接正式 scorer。
  - 不改变 retrieval scoring。
  - 不改变 selected set / order / package sections。
  - 不改 `PackingPolicy`。

### Phase 6F

- 新增配置：
  - `Learning:RankerShadow:TraceCollectionEnabled=false`
  - `Learning:RankerShadow:Profile=lifecycle-aware-v1`
  - `Learning:RankerShadow:MaxCandidatesPerTrace=50`
- 扩展 retrieval/package trace：
  - 新增 `RankerShadowTrace`。
  - 记录 candidate-level `candidateId`、`legacyScore`、`lifecycleAwareScore`、`scoreDelta`、`lifecycleFeatures`、`demotionReasons`、`promotionReasons`。
  - trace collection 在正式 selected set / order / package sections 确定后执行，不回写 scorer，不参与 packing。
- 新增 `LifecycleAwareRankerTraceBuilder`：
  - 从现有 selected / dropped candidate diagnostics 构造 lifecycle-aware shadow score trace。
  - 按 `MaxCandidatesPerTrace` 限制 trace 大小。
  - 标记 deprecated / historical demotion、current / active promotion、version conflict fix、must-hit demotion、must-not-hit promotion 等诊断信号。
- 新增 `RankerShadowTraceExportService`：
  - 从 `IRetrievalTraceStore` 读取 recent retrieval traces。
  - 输出 JSON list 或 JSONL/NDJSON-compatible records。
- 新增 endpoint：
  - `GET /api/learning/ranker-shadow/traces`
  - 支持 `workspaceId`、`collectionId`、`take`、`format=jsonl`。
- `ContextCoreClient` 新增：
  - `GetRankerShadowTracesAsync(...)`
  - `ExportRankerShadowTracesAsync(...)`
- ControlRoom Ranker Shadow Debug 页面新增 Recent Shadow Traces 只读区块：
  - 展示 retrieval id、createdAt、profile、candidate score count、deprecated demotion count、query。
  - 不触发新的 retrieval，不编辑配置。
- 安全保证：
  - `rankerShadowFormalOutputChanged=false`
  - `rankerShadowSelectedSetChanged=false`
  - `rankerShadowPackageSectionsChanged=false`
  - selected set、order、package sections 均不改变。
- 更新文档：
  - `docs/learning-offline-baseline.md`
  - `docs/learning-feature-dataset.md`
  - `docs/controlroom-service-mode.md`
  - `docs/ContextCore_新阶段执行报告.md`

### Phase 6E

- 新增 debug DTO：
  - `LifecycleAwareRankerShadowDebugRequest`
  - `LifecycleAwareRankerShadowDebugResponse`
- 新增 `LifecycleAwareRankerDebugService`：
  - 调用现有 retriever 收集 selected / dropped candidates。
  - 将候选转换为诊断快照后复用 `LifecycleAwareRankerShadowScorer`。
  - 输出 `legacyScore`、`lifecycleAwareScore`、`scoreDelta`、`reason` 和 lifecycle-aware feature breakdown。
  - 强制 debug result `FormalOutputChanged=false`、`SelectedSetChanged=false`，`FinalSelectedIds` 等于 `LegacySelectedIds`。
  - debug path 不改变正式 retrieval output，不改变 selected set，不改 `PackingPolicy`。
- 新增 endpoint：
  - `POST /api/retrieval/ranker-shadow/debug`
- 新增配置：
  - `Learning:RankerShadow:Enabled=false`
  - `Learning:RankerShadow:DebugEndpointEnabled=true`
  - `Learning:RankerShadow:Profile=lifecycle-aware-v1`
- `DebugEndpointEnabled` 只控制只读 debug endpoint 是否可调用，不启用 runtime scorer。
- `ContextCoreClient` 新增：
  - `DebugLifecycleAwareRankerAsync(...)`
- ControlRoom Service Mode 新增 Ranker Shadow Debug 页面：
  - `34` 从主菜单或 Service Dashboard 进入。
  - 输入 query，可选 mode。
  - 展示 candidate score comparison。
  - 展示 deprecated / historical demotion reason。
  - 展示 current / active promotion reason。
  - 展示 version conflict fixes、must-hit demotions、must-not-hit promotions。
- 更新文档：
  - `docs/learning-offline-baseline.md`
  - `docs/learning-feature-dataset.md`
  - `docs/controlroom-service-mode.md`
  - `docs/ContextCore_新阶段执行报告.md`

### Phase 6D

- 新增 `LifecycleAwareRankerShadowOptions`。
- 新增 shadow trace / report DTO：
  - `LifecycleAwareRankerShadowCandidateScore`
  - `LifecycleAwareRankerShadowTrace`
  - `LifecycleAwareRankerShadowSample`
  - `LifecycleAwareRankerShadowReport`
- 新增 `LifecycleAwareRankerShadowScorer`：
  - 输入 eval selected/dropped candidate diagnostics。
  - 输出 `legacyScore`、`lifecycleAwareScore`、`scoreDelta`、`reason` 与 lifecycle-aware feature snapshot。
  - 不改变排序，不改变 selected set，不写 runtime scorer。
- 新增 `LifecycleAwareRankerShadowReportBuilder`：
  - 汇总 `FormalOutputChanged`、`SelectedSetChanged`、`DeprecatedNoiseDemotedCount`、`VersionConflictFixedCount`、`MustHitDemotedCount`、`MustNotHitPromotedCount`、`LifecycleViolationCount`、`PotentialMRRDelta`、`PotentialPairwiseWinRate`。
- 新增 eval CLI：
  - `eval lifecycle-ranker-shadow`
- 新增报告输出：
  - `learning/baselines/lifecycle-aware-ranker-shadow-report-a3.json`
  - `learning/baselines/lifecycle-aware-ranker-shadow-report-extended.json`
- 新增默认关闭配置：
  - `Learning:RankerShadow:Enabled=false`
  - `Learning:RankerShadow:Profile=lifecycle-aware-v1`
- 更新文档：
  - `docs/learning-offline-baseline.md`
  - `docs/learning-feature-dataset.md`
  - `docs/ContextCore_新阶段执行报告.md`

### Phase 6C 基础

- 新增 hard negative / lifecycle-aware DTO：
  - `HardNegativeExample`
  - `HardNegativeDatasetReport`
  - `LifecycleAwareFeatureSet`
  - `LifecycleAwareRankerReport`
  - `LifecycleAwareRankerResult`
- 扩展 `LearningOfflineBaselineRunner`：
  - `RunHardNegativeGenerationAsync`
  - `BuildHardNegativeReport`
  - `BuildHardNegativeMarkdownReport`
  - `RunLifecycleAwareRankerAsync`
  - `BuildLifecycleAwareRankerReport`
  - `BuildLifecycleAwareRankerMarkdownReport`
  - `ExtractLifecycleAwareFeatures`
- 从 `ranker-residual-audit-report.json` 自动生成 hard negatives：
  - `DeprecatedSameKeyword`
  - `VersionConflict`
  - `HistoricalSelectedNoise`
  - `WeakLifecycleMarker`
  - `SemanticAnchorOvermatch`
  - `KeywordNoise`
- 新增 lifecycle-aware offline features：
  - `IsDeprecated`
  - `IsSuperseded`
  - `IsHistorical`
  - `IsRejected`
  - `HasReplacement`
  - `HasSupersedesRelation`
  - `VersionDistance`
  - `IsCurrentVersion`
  - `LifecycleConfidence`
  - `HistoricalSectionOnly`
- 新增 CLI：
  - `eval learning-hard-negatives`
  - `eval learning-lifecycle-aware-ranker`
  - `eval learning-ranker-analysis` 同步生成 6A/6B/6C 报告
- 新增报告输出：
  - `learning/features/hard-negatives.jsonl`
  - `learning/baselines/hard-negative-report.json`
  - `learning/baselines/hard-negative-report.md`
  - `learning/baselines/lifecycle-aware-ranker-report.json`
  - `learning/baselines/lifecycle-aware-ranker-report.md`
- 新增 ranker residual audit DTO：
  - `RankerResidualErrorAuditReport`
  - `RankerResidualFailureDetail`
  - `RankerResidualFailureCluster`
  - `RankerFeatureConflictSummary`
  - `RankerHardNegativeRecommendation`
- 扩展 `LearningOfflineBaselineRunner`：
  - `RunRankerResidualAuditAsync`
  - `BuildRankerResidualAuditReport`
  - `BuildRankerResidualAuditMarkdownReport`
- Residual failure detail 输出：
  - sample / mode / intent
  - positive / negative candidate id
  - positive / negative offline score 与 margin
  - keyword match score
  - semantic anchor match score
  - selected / rank
  - kind / section
  - failure cluster
  - probable cause
- Feature conflict summary：
  - `KeywordMatch`
  - `SemanticAnchorMatch`
  - `Rank`
  - `Selection`
- DeprecatedNoise hard negative recommendations：
  - `DeprecatedSameKeyword`
  - `VersionConflict`
  - `HistoricalSelectedNoise`
  - `WeakLifecycleMarker`
  - `SemanticAnchorOvermatch`
- 新增 CLI：
  - `eval learning-ranker-residual-audit`
  - `eval learning-ranker-analysis`
- 新增报告输出：
  - `learning/baselines/ranker-residual-audit-report.json`
  - `learning/baselines/ranker-residual-audit-report.md`
- 更新文档：
  - `docs/learning-offline-baseline.md`
  - `docs/learning-loop-foundation.md`
  - `docs/learning-feature-dataset.md`
  - `docs/ContextCore_新阶段执行报告.md`

## 新增测试覆盖

- shadow scorer computes lifecycle-aware score
- shadow scorer does not change selected set
- deprecated candidate receives lower lifecycle-aware score
- lifecycle-aware ranker shadow report generated
- formal output remains unchanged in shadow report
- ranker shadow debug service returns lifecycle-aware scores without changing output
- ranker shadow debug endpoint returns lifecycle-aware scores
- ranker shadow debug endpoint reports deprecated demotion reason
- `ContextCoreClient.DebugLifecycleAwareRankerAsync(...)` calls `/api/retrieval/ranker-shadow/debug`
- ControlRoom renders Ranker Shadow Debug score comparison
- ControlRoom dashboard exposes Ranker Shadow Debug entry
- empty dataset gives `NotReady`
- router report computes metrics
- ranker report computes pairwise accuracy
- output files are generated
- offline baseline does not mutate input examples or touch business logic paths
- ablation report generated
- weight sweep report generated
- failure clustering works
- baseline metrics unchanged
- empty pairs produces NotReady residual audit report
- deprecated negative outranking positive is clustered as `DeprecatedNoise`
- residual audit report includes feature deltas
- hard negative recommendations are generated for `DeprecatedNoise`
- residual audit markdown contains failure table
- hard negative generation from residual audit
- `DeprecatedSameKeyword` classification
- `VersionConflict` classification
- lifecycle feature extraction
- lifecycle-aware baseline computes metrics
- hard negative / lifecycle-aware output files are generated

## 本轮验证

- `dotnet build`：通过，0 warning / 0 error。
- `dotnet test`：通过。
  - `ContextCore.Tests`: 453 passed。
  - `ContextCore.Service.Tests`: 37 passed。
  - `ContextCore.IntegrationTests`: 19 passed。
- `dotnet run --project src\ContextCore.ControlRoom -- eval lifecycle-ranker-shadow`：通过。
  - 输出：`learning/baselines/lifecycle-aware-ranker-shadow-report-a3.json`
  - Samples：`50`
  - FormalOutputChanged：`0`
  - SelectedSetChanged：`0`
  - LifecycleViolationCount：`0`
  - DeprecatedNoiseDemotedCount：`178`
  - VersionConflictFixedCount：`164`
  - MustHitDemotedCount：`23`
  - MustNotHitPromotedCount：`30`
  - PotentialMRRDelta：`+0.0290`
  - PotentialPairwiseWinRate：`99.00%`
- `dotnet run --project src\ContextCore.ControlRoom -- eval lifecycle-ranker-shadow --include-batches`：通过。
  - 输出：`learning/baselines/lifecycle-aware-ranker-shadow-report-extended.json`
  - Samples：`113`
  - FormalOutputChanged：`0`
  - SelectedSetChanged：`0`
  - LifecycleViolationCount：`0`
  - DeprecatedNoiseDemotedCount：`406`
  - VersionConflictFixedCount：`402`
  - MustHitDemotedCount：`52`
  - MustNotHitPromotedCount：`30`
  - PotentialMRRDelta：`+0.0516`
  - PotentialPairwiseWinRate：`99.56%`
- 输出评估：
  - shadow safety gate 达标：formal output、selected set、lifecycle violation 均为 `0`。
  - lifecycle-aware score 对 deprecated / historical noise 有稳定 demotion 信号。
  - `MustHitDemotedCount` 与 `MustNotHitPromotedCount` 仍作为风险观察项保留；本轮不调正式 scorer，不接 runtime，不改变 selected set。
- Phase 6C 前序 `dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-analysis`：通过。
  - Ranker pairs：`253`
  - Baseline `SimpleFeatureWeightedBaseline`：pairwise accuracy `90.48%`
  - Ablation `keyword match`：pairwise accuracy `100.00%`，delta `+9.52%`
  - Ablation `semantic anchor match`：pairwise accuracy `100.00%`，delta `+9.52%`
  - 其他 ablation：pairwise accuracy `90.48%`，delta `0.00%`
  - Failure cluster：baseline residual failure 主要为 `DeprecatedNoise (3)`
  - Weight sweep best：`90.48%`，neutral，未产生优于 baseline 的上线候选。
  - Residual audit failures：`3`
  - Residual audit clusters：`DeprecatedNoise:3`
  - Feature conflict：`KeywordMatch` average delta `-13.05`，`SemanticAnchorMatch` average delta `-13.05`
  - Hard negative recommendations：`DeprecatedSameKeyword`、`VersionConflict`、`HistoricalSelectedNoise`、`WeakLifecycleMarker`、`SemanticAnchorOvermatch`
  - Hard negative dataset：`18` examples
    - `DeprecatedSameKeyword:3`
    - `VersionConflict:3`
    - `HistoricalSelectedNoise:3`
    - `WeakLifecycleMarker:3`
    - `SemanticAnchorOvermatch:3`
    - `KeywordNoise:3`
  - Lifecycle-aware ranker：
    - `RuleScoreBaseline`：pairwise accuracy `84.13%`，residual `5`，DeprecatedNoise `5`，FPR/FNR `15.87% / 15.87%`
    - `SimpleFeatureWeightedBaseline`：pairwise accuracy `90.48%`，residual `3`，DeprecatedNoise `3`，FPR/FNR `9.52% / 9.52%`
    - `LifecycleAwareFeatureBaseline`：pairwise accuracy `100.00%`，residual `0`，DeprecatedNoise `0`，FPR/FNR `0.00% / 0.00%`
    - targetPassed：`true`
  - 输出：
    - `learning/baselines/ranker-ablation-report.json`
    - `learning/baselines/ranker-ablation-report.md`
    - `learning/baselines/ranker-weight-sweep-report.json`
    - `learning/baselines/ranker-weight-sweep-report.md`
    - `learning/baselines/ranker-residual-audit-report.json`
    - `learning/baselines/ranker-residual-audit-report.md`
    - `learning/features/hard-negatives.jsonl`
    - `learning/baselines/hard-negative-report.json`
    - `learning/baselines/hard-negative-report.md`
    - `learning/baselines/lifecycle-aware-ranker-report.json`
    - `learning/baselines/lifecycle-aware-ranker-report.md`
- `scripts/eval-gate-p15.ps1`：通过。
- A3 baseline：`50 total / 0 failed / 100.00%`。
- Extended baseline：`113 total / 0 failed / 100.00%`。
- eval report：
  - `eval/eval-report-p15-a3.json`
  - `eval/eval-report-p15-extended.json`
- regression gate：
  - A3 mustNotHit violation = `0`
  - A3 lifecycle violation = `0`
  - A3 hard constraint missing = `0`
  - Extended mustNotHit violation = `0`
  - Extended lifecycle violation = `0`
  - Extended hard constraint missing = `0`

P15 关闭的是 corpus 缺失导致的 expected hard constraint gap，不是通过 resolver alias 放行。constraint 必须由 `ConstraintGapCandidate accept -> CandidateConstraint activate -> Active/Hard Constraint` 产生，并真实进入 constraints/package sections。本轮 Feature Extraction Dataset 只引用该 baseline，不改变 baseline 业务路径。

本轮 Hard Negative Dataset 与 Lifecycle-aware Ranker 只读取 Phase 3 导出的 `ranking-pairs.jsonl` 和 Phase 6B 的 `ranker-residual-audit-report.json` 并生成离线数据/诊断报告；没有把 residual audit、hard negatives、lifecycle-aware scoring、ablation 或 sweep 接回 retrieval、planning、`PackingPolicy`、attention 或 constraints 执行路径，也没有训练或保存生产模型。当前离线结果显示 lifecycle-aware features 可以解释并修复 eval-only pair 中的 deprecated noise，但这只是后续离线实验信号，不作为正式 scorer 变更。

## 前序 P11 摘要

- 新增 `PlanningOptInConstraintSafetyReport` / sample DTO。
- 新增 `PlanningOptInConstraintSafetyReportBuilder`。
  - sample-level 输出 expected hard constraints、legacy/proposal constraints、missing constraints、constraint source、lostAtStage、suggestedFix。
  - report-level 输出 affected、fallback、repaired、repairFailed、droppedByBudget、wrongSection。
- 新增 eval CLI：
  - `eval planning-optin-constraint-safety`
- 新增 report 输出：
  - `eval/planning-optin-constraint-safety-report-a3.json`
  - `eval/planning-optin-constraint-safety-report-extended.json`
- `ShadowRetrievalPlanExecutor` 新增 `MandatoryConstraintInjection`。
  - 从 metadata 解析 required hard constraints。
  - 优先从 `IConstraintStore` 读取 scope 匹配 hard constraints。
  - 注入项标记 `mandatory=true`、`lockedConstraint=true`、`section=constraints`。
  - budget pressure 下优先裁剪 diagnostics / historical / low-value items。
  - wrong-section constraint 会被修正到 constraints section。
- `HybridContextRetriever` 的 planning safety check 改为要求 hard constraint 内容命中且位于 constraints section。
- fallback analysis 新增分类：
  - `ConstraintRepaired`
  - `ConstraintRepairFailed`
  - `ConstraintDroppedByBudget`
  - `ConstraintWrongSection`
- P11 评估结果：
  - A3 constraint safety: affected=10, fallback=0, repaired=10。
  - Extended constraint safety: affected=39, fallback=9, repaired=30；剩余 9 个为语料中没有对应 hard constraint 的 expected constraint，继续 fallback。
- 新增 `ShadowRetrievalResult` DTO。
- 新增 `PlanningOptInFallbackAnalysisReport` / sample / intent summary / recommendation DTO。
- 新增 `PlanningOptInFallbackAnalysisReportBuilder`。
  - sample-level 输出 opt-in matched / applied / fallback / selected refs / quality delta。
  - intent-level 输出 samples、applied、fallback rate、pass / recall / MRR delta、must-not-hit / lifecycle counters。
  - fallback reason 分类为 `MustNotHitRisk`、`LifecycleRisk`、`HardConstraintMissing`、`InvalidPlan`、`SelectedSetUnsafe`、`BudgetPressureRegression`、`QualityRegression`、`Unknown`。
  - recommendation 输出 `KeepOptIn`、`ExpandCandidate`、`ShadowOnly`、`Blocked`、`NeedsPolicyTuning`。
- 新增 eval CLI：
  - `eval planning-optin-fallback-analysis`
- 新增 report 输出：
  - `eval/planning-optin-fallback-analysis-a3.json`
  - `eval/planning-optin-fallback-analysis-extended.json`
- P10 评估但不启用下一批候选 intent：
  - `CodingTask`
  - `LongTermPreference`
- 新增 `RetrievalPlanningOptions`。
  - `Mode`: `Off` / `Shadow` / `ApplyGuarded`
  - `ApplyMode`: `IntentScoped`
  - `OptInIntents`
  - `FallbackToLegacyOnViolation`
  - `EmitComparisonTrace`
- `HybridContextRetriever` 新增 planning opt-in 执行层。
  - legacy hybrid retrieval 先完整执行。
  - `Shadow` 模式只记录 comparison trace，不改变 final selected。
  - `ApplyGuarded` 仅在 intent 命中 `OptInIntents` 时可使用 proposal selected。
  - invalid proposal / must-not-hit / lifecycle / hard constraint missing 均 fallback legacy。
  - vector 保持 disabled。
- retrieval trace metadata 新增：
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
- ControlRoom Package Preview / Retrieval Debug 增加只读 planning execution status 展示。
- 新增 eval CLI：
  - `eval planning-optin-comparison`
- 新增 report 输出：
  - `eval/planning-optin-comparison-a3.json`
  - `eval/planning-optin-comparison-extended.json`
- 新增 `ShadowRetrievalComparisonReport` / sample / rank delta DTO。
- 新增 `RetrievalPlanSafetyProfile`。
  - `MaxFinalTopK`
  - `MaxKeywordTopK`
  - `MaxMemoryTopK`
  - `MaxRelationTopK`
  - `MaxVectorTopK`
  - `AllowVector`
  - `AllowDeprecatedInNormalMode`
  - `AllowSupersededInNormalMode`
  - `RequireLifecycleFilter`
- 扩展 comparison sample：
  - `LegacySelected`
  - `ShadowSelected`
  - `MustHitDropped`
  - `LifecycleRiskAdded`
  - `ValidatorApplied`
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
- 扩展 comparison report：
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
- 新增 `PlanningShadowQualityReport` / group / sample / recommendation DTO。
- 新增 `PlanningShadowRecallLossReport` / sample DTO。
- 新增 `PlanningShadowQualityReportBuilder`。
  - 计算 global / mode / intent deltas。
  - 输出 sample-level improved / regressed、mustHit gained/lost、constraint/entity/uncertainty gained/lost、selected count change 和 suspected reason。
  - 输出 recommendation：opt-in candidate intents、blocked intents、needs tuning intents、safe-only-in-shadow intents。
- 新增 `PlanningShadowRecallLossReportBuilder`。
  - 输出退化样本、lost mustHit、legacy/shadow rank、channel sources、disabled channels、TopK caps、suspected loss reason 和 suggested fix。
- 新增 `ShadowRetrievalPlanExecutor`。
  - 复用现有 mandatory / keyword / memory / relation channel executors。
  - 复用现有 `RetrievalPackingPolicy`。
  - 按 proposal channel flags / TopK 执行 shadow retrieval。
  - 强制关闭 vector channel。
  - 非法 proposal fallback 到 `LegacySafePlan`，并记录 warning / diagnostic。
  - Phase P8 在 `Pack(...)` 后、selected validator 前应用 shadow-only coverage floor。
  - coverage floor 保留 must-hit / exact-match / high-importance / active-task / stable-preference / relation-evidence。
  - eval must-hit refs 只作为 shadow clone request 的 exact reserve ids，不进入正式 retrieval。
- 更新 `RetrievalPlanProposalService`。
  - 生成 proposal 时直接按 `RetrievalPlanSafetyProfile` 输出 native safe `FinalTopK` / channel TopK。
  - 增加 intent-specific safe defaults：`CurrentTask`、`AuditDeprecated`、`ConflictCheck`、`CodingTask`、`NovelGeneration`、`AutomationRecovery`、`LongTermPreference`、`FuzzyQuestion`。
  - 增加 intent-specific recall reserve reason，并让 `CurrentTask` 保留 relation reserve。
  - `UseVector=false`、`VectorTopK=0` 强制保持。
  - 非 audit / conflict 模式记录 deprecated / superseded normal path blocked。
  - 正常 service-generated proposal 不再输出 `.clamped` repair reason。
- 更新 `RetrievalPlanProposalValidator`。
  - Rejected 永不进入 shadow selected。
  - Deprecated / Superseded 非 audit / conflict path 不进入 normal selected。
  - `AuditMode=false` 时 historical / deprecated evidence 不进入 normal selected。
  - `UseVector=false` / `VectorTopK=0` 强制保持。
  - `FinalTopK` / channel TopK 超过 safe cap 时先 repair clamp。
  - `UseVector=true` / `VectorTopK>0` 时先 repair 为 disabled。
  - 修复后仍非法才 fallback。
  - relation expansion 结果仍走 eligibility / lifecycle 过滤。
- 新增 `PlanningShadowDiffTriageReport` / sample / channel plan DTO。
- 新增 `PlanningShadowDiffTriageReportBuilder`。
- 新增 `ShadowRetrievalComparisonReportBuilder`。
  - 统计 `nativeValidPlanCount`、`nativeValidRate`。
  - 统计 repair reason 八类：`FinalTopKClamped`、`KeywordTopKClamped`、`MemoryTopKClamped`、`RelationTopKClamped`、`VectorDisabled`、`DeprecatedBlocked`、`SupersededBlocked`、`InvalidNormalLifecycle`。
  - 输出 intent / mode repair breakdown。
- 新增 `PlanningShadowEvalRunner`。
- 新增 eval CLI：
  - `eval planning-shadow`
  - `eval planning-shadow-quality`
  - `eval planning-shadow-recall-loss`
- 输出 report：
  - `eval/planning-shadow-comparison-a3.json`
  - `eval/planning-shadow-comparison-extended.json`
  - `eval/planning-shadow-diff-triage-a3.json`
  - `eval/planning-shadow-diff-triage-extended.json`
  - `eval/planning-shadow-quality-report-a3.json`
  - `eval/planning-shadow-quality-report-extended.json`
  - `eval/planning-shadow-recall-loss-report-a3.json`
  - `eval/planning-shadow-recall-loss-report-extended.json`
- 新增文档：
  - `docs/retrieval-plan-shadow-execution.md`
  - `docs/planning-shadow-quality-report.md`
- 更新文档：
  - `docs/planning-context-snapshot.md`
  - `docs/retrieval-plan-proposal.md`
  - `docs/ContextCore_新阶段执行报告.md`

## Shadow 安全边界

- shadow 不修改 legacy retrieval result。
- shadow 不写 retrieval trace store。
- shadow 仍走现有 eligibility / lifecycle filter。
- shadow vector 固定 disabled：`IncludeVectorRecall=false`、`VectorTopK=0`。
- shadow proposal 可修复问题先 repair；修复后仍非法才 fallback 到 `LegacySafePlan`。
- validator 后记录 `validatorApplied`、`validPlan`、`repairedPlan`、`fallbackToLegacySafePlan`、`rejectedPlanReasons`、`validatorRepairReasons`。
- validator 后记录 `mustNotHitAddedAfterValidation` 和 `lifecycleViolationAfterValidation`。
- shadow comparison 只做诊断，不参与 package build，不影响 selected set。

## Comparison 字段

单样本 comparison 包含：

- selected set diff
- legacySelected / shadowSelected
- added / dropped
- mustHit delta
- mustHitDropped
- mustNotHit violation
- lifecycle violation
- lifecycleRiskAdded
- constraint / entity / uncertainty delta
- budget pressure delta
- rank delta
- validator status / repair / fallback / rejected reasons
- fallback root cause
- repair reasons
- after-repair plan summary
- after-validation must-not-hit / lifecycle counts

汇总 report 包含：

- total samples
- selectedSetDiffCount
- added / dropped count
- mustNotHitViolationCount
- lifecycleViolationCount
- validPlanCount
- nativeValidPlanCount
- repairedPlanCount
- fallbackToLegacySafePlanCount
- fallbackPlanCount
- nativeValidRate
- finalTopKClampCount
- vectorDisabledCount
- deprecatedBlockedCount
- validatorRepairReasons
- repairReasonCounts
- intentRepairBreakdown
- modeRepairBreakdown
- warning counts

Diff triage report 逐样本包含：

- sampleId / intent / mode
- legacySelected / shadowSelected
- addedByShadow / droppedByShadow
- mustNotHitAdded / mustHitDropped / lifecycleRiskAdded
- channelPlan / channelTopK
- fallbackRootCause / repairReasons / afterRepairPlanSummary
- suspectedCause / suggestedFix

Quality report 包含：

- passRateDelta
- recall@3/5/10 delta
- mrrDelta
- constraintHitDelta / entityHitDelta / uncertaintyHitDelta
- mustNotHitViolationDelta
- budgetPressureDelta
- selectedCountDelta
- mustHitTokenShareDelta
- mode breakdown
- intent breakdown
- sample-level improved / regressed / gained / lost
- recommendation

## Eval 结果（P8 历史，P15 前）

A3 baseline：

- command: `eval run --out eval/eval-report-phase-p8-a3.json`
- total: `50`
- failed: `0`
- pass rate: `100.00%`

Extended（P15 前）：

- command: `eval run --include-batches --out eval/eval-report-phase-p8-extended.json`
- total: `113`
- failed: `1`
- retained failure: `chat-20260529-003`（P15 已通过正式 constraint activation closure 关闭）

Planning shadow A3：

- output: `eval/planning-shadow-comparison-a3.json`
- samples: `50`
- selectedSetDiffSamples: `49`
- added / dropped: `2 / 228`
- mustNotHitViolations: `0`
- lifecycleViolations: `0`
- valid / native / repaired / fallback: `50 / 50 / 0 / 0`
- nativeValidRate: `100.0%`
- finalTopKClamp: `0`
- vectorDisabled: `50`
- deprecatedBlocked: `0`
- repairReasonCounts: all `0`

Planning shadow Extended：

- output: `eval/planning-shadow-comparison-extended.json`
- samples: `113`
- selectedSetDiffSamples: `110`
- added / dropped: `162 / 413`
- mustNotHitViolations: `0`
- lifecycleViolations: `0`
- valid / native / repaired / fallback: `113 / 113 / 0 / 0`
- nativeValidRate: `100.0%`
- finalTopKClamp: `0`
- vectorDisabled: `113`
- deprecatedBlocked: `0`
- repairReasonCounts: all `0`

Planning shadow diff triage：

- A3 output: `eval/planning-shadow-diff-triage-a3.json`
  - diff samples: `49`
  - repaired / fallback: `0 / 0`
  - mustNotHitAdded: `0`
  - mustHitDropped: `0`
  - lifecycleRiskAdded: `0`
  - suspectedCause: `ChannelPlanDiff=49; NoDiff=1`
- Extended output: `eval/planning-shadow-diff-triage-extended.json`
  - diff samples: `110`
  - repaired / fallback: `0 / 0`
  - mustNotHitAdded: `0`
  - mustHitDropped: `0`
  - lifecycleRiskAdded: `0`
  - suspectedCause: `ChannelPlanDiff=110; NoDiff=3`

Planning shadow quality A3：

- output: `eval/planning-shadow-quality-report-a3.json`
- samples: `50`
- passRateDelta: `+26.00%`
- recall@3/5/10 delta: `+5.67% / +2.67% / +1.00%`
- mrrDelta: `+0.1750`
- constraint/entity/uncertainty delta: `0.00% / 0.00% / 0.00%`
- mustNotHitViolationDelta: `-21`
- lifecycleViolations: `0`
- selectedCountDelta: `-4.52`
- mustHitTokenShareDelta: `+17.75%`
- improved / regressed: `25 / 0`

Planning shadow quality Extended：

- output: `eval/planning-shadow-quality-report-extended.json`
- samples: `113`
- passRateDelta: `+5.31%`
- recall@3/5/10 delta: `+14.75% / +9.44% / +4.13%`
- mrrDelta: `+0.1730`
- constraint/entity/uncertainty delta: `0.00% / +0.88% / 0.00%`
- mustNotHitViolationDelta: `-11`
- lifecycleViolations: `0`
- selectedCountDelta: `-2.22`
- mustHitTokenShareDelta: `+8.53%`
- improved / regressed: `34 / 0`

Planning shadow recall loss：

- A3 output: `eval/planning-shadow-recall-loss-report-a3.json`
  - degraded samples: `0`
  - mustHitLost: `0`
- Extended output: `eval/planning-shadow-recall-loss-report-extended.json`
  - degraded samples: `0`
  - mustHitLost: `0`

Quality recommendation：

- opt-in candidate intents: `AuditDeprecated`, `AutomationRecovery`, `CodingTask`, `ConflictCheck`, `CurrentTask`, `FuzzyQuestion`, `LongTermPreference`, `NovelGeneration`
- blocked intents: `(none)`
- needs tuning intents: `(none)`
- safe only in shadow intents: `(none)`

该 recommendation 仍只代表 shadow quality report 输出，本阶段没有做 opt-in enable。

## 测试覆盖

新增覆盖：

- empty ranker shadow trace quality report returns `NeedsMoreRealTraces`
- deprecated / historical demotion counted in quality report
- must-hit demotion counted as risk
- must-not-hit promotion counted as risk
- ranker shadow trace quality report files generated by CLI
- ControlRoom renders Trace Quality Summary
- ranker shadow trace collection disabled by default
- enabling ranker shadow trace collection records shadow scores
- ranker shadow trace collection does not change retrieval output
- deprecated candidate gets demotion reason in trace
- ranker shadow trace export returns JSONL-compatible records
- ranker shadow trace endpoint exports JSONL-compatible traces
- `ContextCoreClient.GetRankerShadowTracesAsync(...)` route
- `ContextCoreClient.ExportRankerShadowTracesAsync(...)` route
- ControlRoom renders Recent Shadow Traces
- shadow execution does not affect legacy output
- invalid proposal falls back safely
- lifecycle filter still applies in shadow
- mustNotHit violation is reported
- proposal service generates native safe TopK
- proposal service respects intent-specific safe defaults
- planning-shadow reports native valid / repaired breakdown
- planning-shadow quality report computes global deltas
- planning-shadow quality report computes mode deltas
- planning-shadow quality report computes intent deltas
- planning-shadow quality report marks regressed samples
- planning-shadow quality recommendation logic works
- validator rejects non-audit deprecated plan
- validator rejects rejected lifecycle plan
- validator forces vector disabled
- validator repairs high FinalTopK
- fallback only happens when repair fails
- repaired proposal keeps mustNotHit violation = 0
- invalid proposal falls back to legacy safe plan
- mustNotHit added by shadow is reported
- comparison report contains added/dropped/rank delta
- A3 planning-shadow runs successfully

保留 P2 覆盖：

- proposal intent detection
- proposal includes snapshot context
- proposal does not affect retrieval output
- service endpoint returns proposal
- `ContextCoreClient.ProposeRetrievalPlanAsync(...)` route
- ControlRoom renders planning proposal

## 验证结果

已执行：

```powershell
dotnet build
dotnet test
powershell -ExecutionPolicy Bypass -File scripts\eval-gate-p15.ps1
```

结果：

- build：成功，`0 warning / 0 error`
- test：成功，`568 passed / 0 failed`
  - `ContextCore.Tests`：`509 passed`
  - `ContextCore.IntegrationTests`：`19 passed`
  - `ContextCore.Service.Tests`：`40 passed`
- A3 baseline：`50 total / 0 failed / 100.00% pass`
- Extended baseline：`113 total / 0 failed / 100.00% pass`
- P15 gate：
  - A3 `MustNotHitViolationCount=0`
  - A3 `LifecycleViolationCount=0`
  - A3 `HardConstraintMissingCount=0`
  - Extended `MustNotHitViolationCount=0`
  - Extended `LifecycleViolationCount=0`
  - Extended `HardConstraintMissingCount=0`

## 相关文档

- `docs/planning-context-snapshot.md`
- `docs/retrieval-plan-proposal.md`
- `docs/retrieval-plan-shadow-execution.md`
- `docs/planning-shadow-quality-report.md`
- `docs/controlroom-service-mode.md`
- `docs/mid-term-memory-governance.md`
- `docs/stable-memory-governance.md`
- `docs/learning-offline-baseline.md`
- `docs/learning-feature-dataset.md`
- `docs/ranker-shadow-trace-collection-runbook.md`
- `docs/extended-eval-triage-report.md`
- `docs/attention-order-quality-report.md`

## Graph Foundation G2：Relation Evidence / Confidence / Lifecycle

G2 在 G1 relation taxonomy / graph validation 基础上补齐 relation evidence、confidence、lifecycle、review status 和 explain 诊断。

范围保持只读治理：

- 不改变 retrieval scoring
- 不改变 relation expansion
- 不改变 `PackingPolicy`
- 不改变 planning / attention / package output
- 不接 LLM judge / vector / NamedPipe

新增内容：

- `RelationEvidence`
- `RelationItemReference`
- `RelationExplainResponse`
- `GET /api/relations/{relationId}/explain`
- `ContextCoreClient.ExplainRelationAsync(...)`
- ControlRoom Service Relations 页面 `E <relationId>` explain
- relation metadata 标准字段：`evidenceRefs`、`sourceRefs`、`sourceOperationId`、`sourceItemId`、`createdBy`、`createdFrom`、`confidence`、`confidenceReason`、`lifecycle`、`reviewStatus`、`policyVersion`

G2 诊断新增：

- `LowConfidence`
- `UnreviewedHighImpactRelation`
- `RejectedRelationStillActive`
- `DeprecatedRelationUsedInNormalPath`
- `CandidateRelationUsedInNormalPath`
- `RelationConfidenceMissing`
- `RelationEvidenceBroken`
- `RelationLifecycleMismatch`

S3 supersede / replaces relation metadata 已补齐：

- `confidence=1.0`
- `confidenceReason=stable_lifecycle_review`
- `lifecycle=Active`
- `reviewStatus=Reviewed`
- `sourceOperationId`
- `sourceItemId`
- `sourceRefs`
- `evidenceRefs`
- `reviewId`
- `createdBy`
- `createdFrom=stable_lifecycle_review`
- `policyVersion`

新增测试覆盖：

- S3 supersede relation has confidence / evidence / lifecycle metadata
- missing evidence is diagnosed
- low confidence is diagnosed
- candidate relation in normal path is diagnosed
- relation explain returns source / target / inverse / evidence
- `ContextCoreClient.ExplainRelationAsync(...)` route
- ControlRoom renders relation explain

G2 验证结果：

- `dotnet build`：成功，`0 warning / 0 error`
- `dotnet test`：成功，`568 passed / 0 failed`
  - `ContextCore.Tests`：`509 passed`
  - `ContextCore.Service.Tests`：`40 passed`
  - `ContextCore.IntegrationTests`：`19 passed`
- `scripts/eval-gate-p15.ps1`：passed
- A3 baseline：`50 total / 0 failed / 100.00% pass`
- Extended baseline：`113 total / 0 failed / 100.00% pass`
- A3 / Extended `MustNotHitViolationCount=0`
- A3 / Extended `LifecycleViolationCount=0`
- A3 / Extended `HardConstraintMissingCount=0`

## Graph Foundation G3：Relation Review / Lifecycle Operations

G3 在 G2 relation evidence / confidence / lifecycle / explain 基础上补齐 relation 人工 review 与 lifecycle 操作。

范围保持治理写路径，不接入业务检索链路：

- 不改变 retrieval scoring
- 不改变 relation expansion
- 不改变 `PackingPolicy`
- 不改变 planning / attention / package output
- 不接 LLM judge / vector / NamedPipe

新增内容：

- `RelationReviewRequest`
- `RelationReviewResult`
- `RelationReviewRecord`
- `IRelationReviewStore`
- `InMemoryRelationReviewStore`
- `FileRelationReviewStore`
- `RelationReviewService`
- `POST /api/relations/{relationId}/review`
- `POST /api/relations/{relationId}/reject`
- `POST /api/relations/{relationId}/deprecate`
- `POST /api/relations/{relationId}/needs-evidence`
- `GET /api/relations/{relationId}/reviews`
- `ContextCoreClient.ReviewRelationAsync(...)`
- `ContextCoreClient.RejectRelationAsync(...)`
- `ContextCoreClient.DeprecateRelationAsync(...)`
- `ContextCoreClient.MarkRelationNeedsEvidenceAsync(...)`
- `ContextCoreClient.GetRelationReviewsAsync(...)`
- ControlRoom Service Relations 页面 `V/R/X/N/H` 操作

Lifecycle / reviewStatus 更新规则：

- Review：`reviewStatus=Reviewed`
- Reject：`lifecycle=Rejected`，`reviewStatus=Rejected`
- Deprecate：`lifecycle=Deprecated`
- MarkNeedsEvidence：`reviewStatus=NeedsEvidence`
- RestoreActive 后置

Validation：

- relation 必须存在
- source / target 必须存在
- relation type 必须已注册
- lifecycle transition 必须合法
- high-impact relation 操作必须带 reason
- `superseded_by` / `replaces` inverse mismatch 只诊断，不自动修复

G3 诊断新增：

- `RejectedRelationHasActiveInverse`
- `DeprecatedRelationUsedByActiveChain`
- `NeedsEvidenceHighImpactRelation`
- `ReviewedRelationMissingReviewer`
- `ConfidenceChangedWithoutReview`
- `RelationReviewHistoryMissing`

ControlRoom：

- `E <relationId>` explain
- `V <relationId>` review
- `R <relationId>` reject
- `X <relationId>` deprecate
- `N <relationId>` mark needs evidence
- `H <relationId>` review history
- 写操作前展示 explain 并要求输入 `YES`

G3 验证结果：

- `dotnet build`：成功，`0 warning / 0 error`
- `dotnet test`：成功，`576 passed / 0 failed`
  - `ContextCore.Tests`：`517 passed`
  - `ContextCore.Service.Tests`：`40 passed`
  - `ContextCore.IntegrationTests`：`19 passed`
- `scripts/eval-gate-p15.ps1`：passed
- A3 baseline：`50 total / 0 failed / 100.00% pass`
- Extended baseline：`113 total / 0 failed / 100.00% pass`
- A3 / Extended `MustNotHitViolationCount=0`
- A3 / Extended `LifecycleViolationCount=0`
- A3 / Extended `HardConstraintMissingCount=0`

## Graph Foundation G4：Graph-aware Relation Expansion Governance

G4 在 G3 relation review / lifecycle operations 之后，新增 relation expansion profile、validator、preview 和 shadow report。范围保持 preview / shadow-only：

- 不改变正式 retrieval scoring
- 不改变正式 relation expansion executor
- 不改变 `PackingPolicy`
- 不改变 planning / attention / package output
- 不写 relation store
- 不接 LLM judge / vector / NamedPipe

新增内容：

- `RelationExpansionProfile`
- `RelationExpansionProfileRegistry`
- `RelationExpansionPolicyValidator`
- `RelationExpansionPreviewService`
- `RelationExpansionProfileShadowReportBuilder`
- `GET /api/relations/expansion/profiles`
- `POST /api/relations/expansion/preview`
- `ContextCoreClient.GetRelationExpansionProfilesAsync(...)`
- `ContextCoreClient.PreviewRelationExpansionAsync(...)`
- ControlRoom Service Relations 页面 `P` profiles、单独 `X` expansion preview
- `eval relation-expansion-profile-shadow`
- `eval/relation-expansion-profile-shadow-report.json`
- `eval/relation-expansion-profile-shadow-report.md`

Validator 检查：

- `UnknownRelationType`
- `BlockedRelationType`
- `RelationTypeNotAllowed`
- `ConfidenceTooLow`
- `MissingEvidence`
- `InvalidLifecycle`
- `AuditOnlyRelationInNormalProfile`
- `FanoutExceeded`
- `DepthExceeded`

G4 测试覆盖：

- normal profile blocks audit-only relation
- audit profile allows historical relation
- low confidence relation blocked
- missing evidence relation blocked when required
- fanout cap works
- depth cap works
- preview does not mutate relation store / retrieval output
- client route for profiles / preview
- Service endpoint profiles / preview
- ControlRoom renders expansion profiles / preview

G4 验证结果：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：成功，`584 passed / 0 failed`
  - `ContextCore.Tests`：`525 passed`
  - `ContextCore.Service.Tests`：`40 passed`
  - `ContextCore.IntegrationTests`：`19 passed`
- `scripts/eval-gate-p15.ps1`：passed
- A3 baseline：`50 total / 0 failed / 100.00% pass`
- Extended baseline：`113 total / 0 failed / 100.00% pass`
- A3 / Extended `MustNotHitViolationCount=0`
- A3 / Extended `LifecycleViolationCount=0`
- A3 / Extended `HardConstraintMissingCount=0`
- relation expansion shadow report：`eval/relation-expansion-profile-shadow-report.json`
- relation expansion shadow markdown：`eval/relation-expansion-profile-shadow-report.md`
- shadow summary：`Profiles=4`，`Samples=12`，`Accepted=26`，`Blocked=40`

## Graph Foundation G5：Relation Expansion Shadow Evaluation against Eval Samples

G5 在 G4 relation expansion profile / validator / preview 基础上，把 profiles 放到 A3 / Extended eval 样本上做 shadow evaluation。

范围保持只读 shadow：

- 不改变正式 retrieval 输出
- 不改变正式 relation expansion executor
- 不改变 `PackingPolicy`
- 不改变 package output
- 不写 relation store
- 不接 LLM judge / vector / NamedPipe

新增内容：

- `RelationExpansionShadowEvalReport`
- `RelationExpansionShadowSample`
- `RelationExpansionShadowProfileSummary`
- `RelationExpansionShadowEvalRunner`
- `eval relation-expansion-shadow-eval`
- `eval/relation-expansion-shadow-eval-a3.json`
- `eval/relation-expansion-shadow-eval-extended.json`
- `eval/relation-expansion-shadow-eval.md`

样本级报告包含：

- `sampleId`
- `mode`
- `intent`
- `profileId`
- `seedItems`
- `expandedRelations`
- `acceptedRelations`
- `blockedRelations`
- `wouldAddCandidates`
- `wouldAddMustHit`
- `wouldAddMustNotHit`
- `wouldAddLifecycleRisk`
- `blockedReasons`
- `fanoutTrimmed`
- `depthTrimmed`
- `recommendation`

Profile 汇总包含：

- `Samples`
- `AcceptedRelations`
- `BlockedRelations`
- `WouldAddCandidates`
- `MustHitGain`
- `MustNotHitRisk`
- `LifecycleRisk`
- `BlockedByType`
- `BlockedByLifecycle`
- `BlockedByConfidence`
- `BlockedByMissingEvidence`
- `FanoutTrimmed`
- `DepthTrimmed`
- `Recommendation`

G5 当前报告结果：

- A3：`50` eval samples，`200` profile/sample rows，`FormalOutputChanged=0`，`SelectedSetChanged=0`
- Extended：`113` eval samples，`452` profile/sample rows，`FormalOutputChanged=0`，`SelectedSetChanged=0`
- A3 / Extended profile recommendations 当前均为 `NeedsPolicyTuning`
- 主要阻断原因：eval corpus 中仍有 legacy relation type `supersedes`，不属于 G1 taxonomy；部分 relation fixture 缺 evidence metadata。

G5 测试覆盖：

- shadow eval does not affect formal retrieval output
- profile summary aggregates accepted / blocked relations
- mustNotHit risk is reported
- lifecycle risk is reported
- blocked reasons are preserved

G5 验证结果：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：成功，`589 passed / 0 failed`
  - `ContextCore.Tests`：`530 passed`
  - `ContextCore.Service.Tests`：`40 passed`
  - `ContextCore.IntegrationTests`：`19 passed`
- `scripts/eval-gate-p15.ps1`：passed
- A3 baseline：`50 total / 0 failed / 100.00% pass`
- Extended baseline：`113 total / 0 failed / 100.00% pass`
- A3 / Extended `MustNotHitViolationCount=0`
- A3 / Extended `LifecycleViolationCount=0`
- A3 / Extended `HardConstraintMissingCount=0`

## Graph Foundation G5.1：Relation Corpus Normalization & Evidence Backfill

G5.1 针对 G5 shadow eval 暴露出的 relation corpus hygiene 问题做离线修正：

- legacy relation type 标准化
- deterministic / eval fixture relation metadata backfill
- corpus hygiene JSON / Markdown 报告
- graph validation suggestion 增强
- relation expansion shadow eval 使用标准化与回填后的 in-memory fixture relation

范围保持不变：

- 不改变正式 retrieval
- 不改变正式 relation expansion executor
- 不改变 `PackingPolicy`
- 不改变 package output
- 不写回 eval corpus 文件
- 不写 runtime `IRelationStore`

新增内容：

- `RelationTypeNormalizer`
- `RelationCorpusHygieneReport`
- `RelationCorpusHygieneReportBuilder`
- `eval relation-corpus-hygiene`
- `eval/relation-corpus-hygiene-report.json`
- `eval/relation-corpus-hygiene-report.md`

标准化规则：

- `supersedes -> replaces`
- `is_superseded_by -> superseded_by`
- `replacedBy -> replaced_by`
- `dependsOn -> depends_on`
- `evidenceFor -> evidence_for`

Hygiene 报告结果：

- corpus files：`11`
- relations：`21`
- legacy relation types：`11`
- unknown relation types：`0`
- missing evidence relations：`21`
- missing confidence relations：`0`
- missing lifecycle relations：`21`
- missing reviewStatus relations：`21`
- migration candidates：`11`
- backfill candidates：`21`

G5.1 shadow eval 结果：

- A3：`50` eval samples，`200` profile/sample rows，`FormalOutputChanged=0`，`SelectedSetChanged=0`
- Extended：`113` eval samples，`452` profile/sample rows，`FormalOutputChanged=0`，`SelectedSetChanged=0`
- A3 / Extended `BlockedByMissingEvidence=0`
- legacy `supersedes` 不再作为 `UnknownRelationType` 阻断
- `normal-v1` 当前主要 recommendation 为 `BlockedByRisk`，原因是 normalized `replaces` 会 shadow-add deprecated / old target items
- `current-task-v1` 不再因 legacy type 或 missing metadata 退化；剩余阻断来自 profile relation type allowlist

G5.1 测试覆盖：

- `supersedes` normalizes to `replaces`
- legacy relation type appears in hygiene report
- missing evidence appears in hygiene report
- deterministic relation backfill sets confidence/lifecycle/reviewStatus
- relation expansion shadow eval consumes normalized relation type
- graph validation emits normalizedType / evidence backfill suggestions

G5.1 当前验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`595 passed / 0 failed`
- `eval relation-corpus-hygiene`：已生成 JSON / Markdown
- `eval relation-expansion-profile-shadow`：已重新生成 JSON / Markdown
- `eval relation-expansion-shadow-eval`：已重新生成 A3 / Extended / Markdown
- `scripts/eval-gate-p15.ps1`：passed，A3 / Extended 均 `0 failed / 100.00% pass`

## Graph Foundation G6：Relation Expansion Shadow in Retrieval Trace

G6 将 G5.3 中已验证为 section-aware safe 的 `audit-v1` / `conflict-v1` graph expansion shadow 接入 retrieval/package trace collection。范围仍然保持只读采集：

- 不改变正式 retrieval 输出
- 不改变 selected set
- 不改变 order
- 不改变 `PackingPolicy`
- 不改变 package sections
- `FormalOutputChanged=0`

新增配置：

- `Learning:GraphExpansionShadow:Enabled=false`
- `Learning:GraphExpansionShadow:TraceCollectionEnabled=false`
- `Learning:GraphExpansionShadow:Profiles=["audit-v1","conflict-v1"]`
- `Learning:GraphExpansionShadow:MaxRelationsPerTrace=50`

新增 runtime trace / export：

- `ContextRetrievalTrace.GraphExpansionShadowTrace`
- `GraphExpansionShadowTraceBuilder`
- `GraphExpansionShadowTraceExportService`
- `GET /api/learning/graph-expansion-shadow/traces`
- JSONL export：`format=jsonl`

新增 quality report：

- `GraphExpansionShadowTraceQualityReport`
- `GraphExpansionShadowTraceQualityReportBuilder`
- CLI：`eval graph-expansion-shadow-trace-quality`
- 输出：
  - `learning/graph-shadow/graph-expansion-shadow-trace-quality-report.json`
  - `learning/graph-shadow/graph-expansion-shadow-trace-quality-report.md`

ControlRoom 更新：

- Relations 页面新增 Graph Shadow Trace Quality Summary
- Relations 页面新增 Recent Graph Shadow Traces
- 只读展示 accepted / blocked relation counts、target sections、riskAfterRouting、wrongSectionRisk 与 recommendation

G6 测试覆盖：

- trace collection disabled by default
- enabling collection records audit/conflict graph shadow
- graph shadow does not change formal retrieval output
- wrong section risk counted
- quality report handles empty traces as `NeedsMoreRealTraces`
- endpoint / client export JSONL
- ControlRoom renders graph shadow trace quality summary

G6 当前验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`613 passed / 0 failed`
- `eval graph-expansion-shadow-trace-quality`：已生成 JSON / Markdown
  - 本地 runtime trace collection 未开启，因此 `TraceCount=0`，recommendation=`NeedsMoreRealTraces`
- `scripts/eval-gate-p15.ps1`：passed
  - A3 baseline：`50 total / 0 failed / 100.00% pass`
  - Extended baseline：`113 total / 0 failed / 100.00% pass`
  - `MustNotHitViolationCount=0`
  - `LifecycleViolationCount=0`
  - `HardConstraintMissingCount=0`

## Graph Foundation G6.1：Graph Shadow Trace Collection Runbook & Sample Collection

G6.1 补齐 graph expansion shadow trace 的真实采集 runbook 与脚本。范围保持只读采集，不启用 graph opt-in：

- 不改变正式 retrieval 输出
- 不改变正式 relation expansion
- 不改变 selected set / order
- 不改变 `PackingPolicy`
- 不改变 package output

新增文档：

- `docs/graph-shadow-trace-collection-runbook.md`

Runbook 覆盖：

- 如何开启 `Graph:ExpansionShadow:TraceCollectionEnabled`
- 如何配置 `Profiles=["audit-v1","conflict-v1"]`
- 如何配置 `MaxRelationsPerTrace`
- 推荐采样场景
- 如何导出 graph expansion shadow traces
- 如何运行 `eval graph-expansion-shadow-trace-quality`
- 如何解释 recommendation
- 如何关闭采集

新增脚本：

- `scripts/collect-graph-expansion-shadow-traces.ps1`

脚本行为：

- 默认 dry-run，只打印采样计划和输出路径
- 只有显式传入 `-Execute` 才调用已运行的 `ContextCore.Service`
- 检查 `/api/status`
- 检查 `/api/health/ready`
- 对固定场景调用 `/api/context/retrieve`
- 对固定场景调用 `/api/context/query`
- 对固定场景调用 `/api/package/build-detailed`
- 导出 `/api/learning/graph-expansion-shadow/traces?format=jsonl`
- 运行 `eval graph-expansion-shadow-trace-quality`
- 输出到 `learning/graph-shadow`

固定采样场景扩展为 30 个不同场景，覆盖 Chat / Project / Novel / Automation / Coding：

- Chat version conflict / deprecated preference / audit old topic / overwritten style / old-session scope / long-term preference conflict
- Project deprecated design / superseded pool / old storage choice / migration conflict / retired policy / previous release plan
- Novel old plot / weapon v1-v2 conflict / world rule conflict / character-state retcon / superseded location rule / foreshadowing conflict
- Automation old backup strategy / conflict recovery config / dead-letter policy conflict / superseded retry limit / old credential rotation / failed-step history
- Coding deprecated interface / old timeout config / obsolete API contract / test policy conflict / legacy build path / deprecated schema field

采样完整性约束：

- `TraceCount >= 30` 必须来自不同 `operationId` 与不同采样意图。
- 重复 query / 重复 fixture 只能用于验证采集链路连通性，不作为 readiness 依据。
- 采样必须覆盖 audit/historical routing、conflict evidence routing，以及可解释的 blocked relation。
- 如果真实语料或夹具不足，应保持 `NeedsMoreRealTraces`，不允许用容易合格或重复数据补通过率。
- 后端 graph shadow trace 增加 `traceSignature`。同一 workspace / collection 下重复的 graph shadow payload 会被压缩为 `duplicateSuppressed=true`，trace export 与 quality report 按 signature 去重，避免浪费存储空间或制造 readiness 噪声。

G7 进入门槛已写入 runbook 和 quality report Markdown：

- `TraceCount >= 30`
- `AcceptedRelationCount > 0`
- `AuditContextCount > 0` 或 `ConflictEvidenceCount > 0`
- `RiskAfterRoutingCount = 0`
- `WrongSectionRiskCount = 0`
- `MustNotHitRiskCount = 0`
- `LifecycleRiskCount = 0`
- `MissingEvidenceCount = 0`

G6.1 测试覆盖：

- runbook 文档存在
- collection script 支持 dry-run 和参数校验
- collection script dry-run 可执行通过
- quality report handles non-empty graph trace fixture
- empty trace 仍返回 `NeedsMoreRealTraces`
- P15 gate remains passing

## Vector Index Foundation V3.6：ONNX Residual Risk Audit & Eligibility Policy Repair

V3.6 在 V3.5 ONNX provider 质量提升基础上，补齐 residual risk audit、risk classification 和通用 eligibility policy repair。范围保持纯离线 / preview / shadow：

- 不接正式 retrieval
- 不改 retrieval scoring
- 不改 `PackingPolicy`
- 不让 vector 进入 package 输出
- 不按 sampleId / itemId / fixture 名称 / 领域词表做特判

新增模型与报告：

- `VectorResidualRiskAuditReport`
- `VectorResidualRiskDetail`
- `VectorResidualRiskTypes`
- `VectorResidualRiskAuditRunner`
- CLI：`eval vector-residual-risk-audit`
- 输出：
  - `eval/vector-residual-risk-audit-a3.json`
  - `eval/vector-residual-risk-audit-extended.json`
  - `eval/vector-residual-risk-audit.md`

通用 policy repair：

- `VectorQueryProfile.DiagnosticsOnlyItemKinds`
- `VectorCandidateBlockedReason.DiagnosticsOnlyItemKindBlocked`
- `VectorCandidateBlockedReason.SupersededCandidateBlocked`
- policy 使用运行时 `itemKind` / lifecycle / reviewStatus / replacement metadata / diagnostics，不读取 eval label 或 itemId。
- `normal-v1` / `current-task-v1` / `audit-v1` 默认会阻断诊断型 itemKind，避免 diagnostics / stress-test 类候选进入 normal vector preview 风险。

Risk-oriented sweep 增强：

- `RiskAfterPolicyByType`
- `RecallLossAfterRepair`
- `SimilarityMarginForRiskCandidates`
- `RequiresReranker` recommendation

ControlRoom 更新：

- Service Vector Index 页面新增 Residual Risk Summary。
- 展示 residual risk count、top risk types、whyPolicyAllowed、expectedAction。
- 页面只读取本地 residual audit 报告，不自动执行 eval，不写 index。

V3.6 当前结果：

- `eval vector-residual-risk-audit --provider onnx-local`
  - A3：`ResidualRiskCount=2`，`RiskAfterPolicyByType={LifecycleMetadataGap:2}`，`Recommendation=NeedsPolicyTuning`
  - Extended：`ResidualRiskCount=2`，`RiskAfterPolicyByType={LifecycleMetadataGap:2}`，`Recommendation=NeedsPolicyTuning`
- `eval vector-query-shadow-eval --provider onnx-local`
  - A3：`RiskAfterPolicy=2`，`MustNotHitRiskAfterPolicy=3.51%`，`LifecycleRiskAfterPolicy=0`
  - Extended：`RiskAfterPolicy=2`，`MustNotHitRiskAfterPolicy=1.64%`，`LifecycleRiskAfterPolicy=0`
- `eval vector-query-profile-sweep --provider onnx-local`
  - A3 best：`normal-v1:top5:min0.10:all`，`RiskAfter=0`，`Recommendation=KeepPreviewOnly`
  - Extended best：`normal-v1:top5:min0.10:all`，`RiskAfter=0`，`Recommendation=KeepPreviewOnly`

剩余 2 个 residual risk 为 lifecycle metadata 缺口：

- `doc:legacy-code-structure`
- `doc:legacy-project-info`

这两个风险没有通过 resolver alias、sampleId、itemId 或领域词表硬编码压掉；预期后续通过 corpus metadata 治理或人工 policy 决策处理。

V3.6 测试覆盖：

- residual risk audit records `whyPolicyAllowed`
- metadata gap risk classified as `LifecycleMetadataGap`
- semantic overmatch classified as `RequiresReranker`
- policy repair 不读取 eval label
- itemId / sampleId 特判质量测试
- riskAfterPolicy 非 0 不允许 `ReadyForRetrievalShadow`
- profile sweep includes risk type breakdown
- ControlRoom renders residual risk summary

V3.6 验证：

- `dotnet build`：已通过，`0 warning / 0 error`
- `dotnet test`：已通过，`690 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：passed
  - A3 baseline：`50 total / 0 failed / 100.00% pass`
  - Extended baseline：`113 total / 0 failed / 100.00% pass`
  - `MustNotHitViolationCount=0`
  - `LifecycleViolationCount=0`
  - `HardConstraintMissingCount=0`

## Vector Phase V3.F + Learning Loop R1

本阶段先冻结 Vector Preview / Shadow 结论，然后进入 Router Intent Classifier 的离线 baseline 与 shadow readiness 准备。

新增文档：

- `docs/vector-preview-shadow-freeze.md`

Vector freeze 结论：

- V1 - V3.9 已完成 preview / shadow 评估。
- V4 readiness gate failed。
- 失败项为 `A3RecallAtLeast80Percent`、`A3FusionRecallAtLeast80Percent`、`A3ExpandedRecallAtLeast80Percent`。
- 失败原因是 A3 recall / fusion recall / expanded recall 均低于 80%。
- vector 不进入 formal retrieval / scoring / `PackingPolicy` / package output。

Learning Loop R1 新增：

- `RouterIntentClassifier` 抽象
- `ExistingRuleBasedRouterBaseline`
- `TokenCentroidRouterBaseline`
- `RouterIntentEvaluationRunner`
- CLI：`eval router-intent-baseline`

R1 输出：

- `learning/router/router-intent-baseline-report.json`
- `learning/router/router-intent-baseline-report.md`
- `learning/router/router-intent-confusion-matrix.json`

R1 报告指标：

- `Accuracy`
- `MacroF1`
- `PerIntentPrecision`
- `PerIntentRecall`
- `ConfusionMatrix`
- `LowConfidenceCount`
- `AbstainCount`
- `CurrentTaskRecall`
- `FuzzyQuestionRecall`
- `CodingTaskRecall`
- `NovelGenerationRecall`
- `AutomationRecoveryRecall`

ControlRoom：

- Service Learning Features 页面新增 Router Intent Baseline 只读摘要。
- 若 `learning/router/router-intent-baseline-report.json` 尚未生成，则显示 `not generated`。

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy`。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。

R1 当前运行结果：

- `SampleCount=163`
- `BestBaseline=TokenCentroidRouterBaseline`
- `Recommendation=ReadyForRouterShadow`
- `ExistingRuleBasedRouterBaseline`：`Accuracy=97.14%`，`MacroF1=0.6176`，`LowConfidence=0`，`Abstain=0`
- `TokenCentroidRouterBaseline`：`Accuracy=91.43%`，`MacroF1=0.7027`，`LowConfidence=0`，`Abstain=0`

该结果只表示离线 router shadow readiness，仍不替换 runtime router。

## Learning Loop Phase R2：Router Intent Shadow Trace & Disagreement Analysis

R2 已完成 Router Intent Shadow Trace 与 disagreement analysis 基础链路。

新增内容：

- `RouterShadowOptions`
- `RouterIntentShadowTrace`
- `IRouterIntentShadowTraceStore`
- `InMemoryRouterIntentShadowTraceStore`
- `FileRouterIntentShadowTraceStore`
- `RouterIntentShadowService`
- `RouterIntentShadowReportBuilder`
- API：`GET /api/learning/router-shadow/traces`
- Client：`GetRouterShadowTracesAsync` / `ExportRouterShadowTracesAsync`
- CLI：
  - `eval router-intent-shadow-eval`
  - `eval router-shadow-trace-quality`

Runtime 接入点：

- `POST /api/context/query`
- `POST /api/context/planning/propose`
- `POST /api/package/build`
- `POST /api/package/build-detailed`
- `POST /api/package/preview`

所有接入点均为旁路记录，默认关闭；shadow intent 不回写正式 runtime router，不改变 planning、retrieval、`PackingPolicy` 或 package output。

输出：

- `learning/router/router-intent-shadow-eval-a3.json`
- `learning/router/router-intent-shadow-eval-extended.json`
- `learning/router/router-intent-shadow-eval.md`
- `learning/router/router-shadow-trace-quality-report.json`
- `learning/router/router-shadow-trace-quality-report.md`

当前 R2 报告结果：

- 离线 shadow eval：
  - `Samples=35`
  - `AgreementRate=88.57%`
  - `ShadowFixesRuntime=1`
  - `ShadowBreaksRuntime=3`
  - `Recommendation=KeepRuleBased`
- runtime trace quality：
  - `TraceCount=0`
  - `Recommendation=NeedsMoreRealTraces`

R2 测试覆盖：

- shadow disabled by default
- shadow trace 不改变 runtime router output
- disagreement 正确记录
- low confidence 正确统计
- client router shadow trace route
- service endpoint JSONL export
- itemId / sampleId 不参与 shadow classifier prediction

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy` / package output。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。
- FileSystem / InMemory store 按 `requestId` upsert，避免重复 trace 噪声。

## Learning Loop Phase R2.1：Router Disagreement Triage & Intent Boundary Repair

R2.1 在 R2 的离线 disagreement 基础上补充分诊、intent boundary 文档与 hard negative 数据集。

新增内容：

- `RouterDisagreementTriageRunner`
- `RouterDisagreementTriageReport`
- `RouterDisagreementTriageDetail`
- `RouterHardNegativeExample`
- CLI：`eval router-disagreement-triage`
- 文档：`docs/router-intent-boundaries.md`

输出：

- `learning/router/router-disagreement-triage-a3.json`
- `learning/router/router-disagreement-triage-extended.json`
- `learning/router/router-disagreement-triage.md`
- `learning/router/router-hard-negatives.jsonl`

分诊字段：

- `sampleId`
- `queryText`
- `mode`
- `expectedIntent`
- `runtimeIntent`
- `shadowIntent`
- `runtimeCorrect`
- `shadowCorrect`
- `shadowFixesRuntime`
- `shadowBreaksRuntime`
- `confidence`
- `topPredictions`
- `disagreementType`
- `triageCategory`
- `recommendedAction`

ControlRoom：

- Service Learning Features 页面新增 Router Disagreement Triage Summary。
- 展示 disagreement count、shadow fixes、shadow breaks、top confusion pairs、hard negative count 与 recommendation。

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy` / package output。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。
- hard negative 只做离线数据增强建议，不自动进入 runtime policy。

## Learning Loop Phase R2.F：Router Shadow Freeze & Opt-in Readiness Gate

R2.F 冻结当前 router shadow 结论，并新增 guarded opt-in readiness gate。

新增内容：

- `docs/router-intent-shadow-freeze.md`
- `RouterGuardedOptInReadinessGateRunner`
- `RouterGuardedOptInReadinessGateReport`
- CLI：`eval router-guarded-optin-readiness-gate`

输出：

- `learning/router/router-guarded-optin-readiness-gate.json`
- `learning/router/router-guarded-optin-readiness-gate.md`

冻结指标：

- R1 best classifier：`TokenCentroidRouterBaseline`
- R2 shadow eval agreement rate：`88.57%`
- R2 runtime trace：`TraceCount=0`，`NeedsMoreRealTraces`
- R2.1：`fixes=1`，`breaks=3`，`hardNegatives=3`
- 当前结论：`KeepRuleBased`
- R3 guarded opt-in：blocked

Gate 条件：

- `ShadowBreaksRuntime = 0`
- `ShadowFixesRuntime > 0`
- `NetGain > 0`
- `PerIntentRegression = 0`
- `AgreementRate >= configured threshold`
- `LowConfidenceCount <= configured threshold`
- P15 gate remains passing

当前 gate 预期失败，失败原因包含：

- `ShadowBreaksRuntimeGreaterThanFixes`

ControlRoom：

- Service Learning Features 页面新增 Router Opt-in Readiness Summary。
- 展示 passed、fixes、breaks、net gain、blocked reason 与 recommendation。

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy` / package output。
- 不使用 sampleId / itemId / fixture 特判。
## Learning Loop Phase CR1：Candidate Reranker Shadow Trace Collection & Quality Gate

CR1 新增 candidate reranker / lifecycle-aware ranker shadow eval 和 trace quality gate。

新增 DTO / report：

- `CandidateRerankerShadowOptions`
- `CandidateRerankerShadowTrace`
- `CandidateRerankerShadowEvalReport`
- `CandidateRerankerShadowTraceQualityReport`

新增 CLI：

- `eval candidate-reranker-shadow-eval`
- `eval candidate-reranker-shadow-trace-quality`

输出：

- `learning/ranker/candidate-reranker-shadow-eval-a3.json`
- `learning/ranker/candidate-reranker-shadow-eval-extended.json`
- `learning/ranker/candidate-reranker-shadow-eval.md`
- `learning/ranker/candidate-reranker-shadow-trace-quality-report.json`
- `learning/ranker/candidate-reranker-shadow-trace-quality-report.md`

当前结果：

- A3：`samples=50`，`netGain=-49`，`improve=0`，`regress=49`，`risk=178`，`Recommendation=BlockedByRisk`
- Extended：`samples=113`，`netGain=-18`，`improve=24`，`regress=42`，`risk=50`，`Recommendation=BlockedByRisk`
- Runtime trace quality：`TraceCount=0`，`Recommendation=NeedsMoreRealTraces`

ControlRoom：

- Learning Features 页面新增 `Candidate Reranker Shadow Summary`。
- 显示 trace count、eval net gain、would improve / regress、risk count 和 recommendation。

边界：

- 默认关闭。
- 不替换正式 scorer。
- 不改变 retrieval output / selected set / scoring / `PackingPolicy` / package sections。
- 不使用 sampleId / itemId / fixture 特判来提高指标。

## Learning Loop Phase CR1.1：Candidate Reranker Shadow Failure Triage & Score Contract Audit

CR1.1 新增 candidate reranker shadow failure audit，用于解释 CR1 中 A3 / Extended 的回归和 risk topK，不进入 runtime trace，不替换正式 scorer，不改变 retrieval / selected set / `PackingPolicy` / package output。

新增 DTO / report：

- `CandidateRerankerShadowFailureAuditReport`
- `CandidateRerankerShadowFailureAuditRecord`
- `CandidateRerankerRegressionReasons`
- `CandidateRerankerScoreContractStatuses`

新增 CLI：

- `eval candidate-reranker-shadow-failure-audit`

输出：

- `learning/ranker/candidate-reranker-shadow-failure-audit-a3.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit-extended.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit.md`

更新：

- `candidate-reranker-shadow-eval` 增加 `ScoreContractStatus`、`RankableCandidateCount`、`BlockedCandidateCount`、`RiskCandidateInShadowTopK` 与 `RegressionReasonSummary`。
- ControlRoom Learning Features 页面新增 Candidate Reranker Failure Audit Summary。

边界：

- eval label 只用于 audit/report，不进入 policy。
- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 不为提高通过率修改正式 retrieval scoring、selected set、`PackingPolicy` 或 package sections。

## Learning Loop Phase CR1.2：Candidate Metadata Alignment & Ranker Eligibility Guard

CR1.2 新增 candidate feature envelope、feature completeness report 与 rerank eligibility guard。该阶段只对 shadow runner 的候选元数据输入做对齐和诊断，不替换正式 scorer，不改变 retrieval / selected set / `PackingPolicy` / package output。

新增 DTO / 服务：

- `CandidateFeatureEnvelope`
- `RankerCandidateEligibilityDecision`
- `CandidateRerankerFeatureCompletenessReport`
- `CandidateFeatureEnvelopeBuilder`
- `RankerCandidateEligibilityGuard`
- `CandidateRerankerFeatureCompletenessRunner`

新增 CLI：

- `eval candidate-reranker-feature-completeness`

输出：

- `learning/ranker/candidate-reranker-feature-completeness-a3.json`
- `learning/ranker/candidate-reranker-feature-completeness-extended.json`
- `learning/ranker/candidate-reranker-feature-completeness.md`

更新：

- `candidate-reranker-shadow-eval` 增加 `RawCandidateCount`、`FeatureCompletenessRate`、`RiskCandidateInRawTopK`、`RiskCandidateInShadowTopK`、`RiskCandidateBlockedBeforeRerank`、`EligibilityGuardStatus`。
- `candidate-reranker-shadow-failure-audit` 读取 guard-aware shadow eval，区分 score contract 问题、风险候选进入 shadow topK 与剩余 listwise 质量回归。
- ControlRoom Learning Features 页面新增 Candidate Feature Completeness / Eligibility Guard Summary。

当前结果：

- A3 feature completeness：`88.02%`，`RawCandidateCount=466`，`RankableCandidateCount=243`，`BlockedCandidateCount=71`，`RiskCandidateBlockedBeforeRerank=152`
- Extended feature completeness：`95.02%`，`RawCandidateCount=2627`，`RankableCandidateCount=1947`，`BlockedCandidateCount=307`，`RiskCandidateBlockedBeforeRerank=373`
- A3 shadow eval：`RiskCandidateInShadowTopK=0`，`ScoreContractStatus=Passed`，`NetGain=-17`，`Recommendation=KeepFormalRanking`
- Extended shadow eval：`RiskCandidateInShadowTopK=0`，`ScoreContractStatus=Passed`，`NetGain=-3`，`Recommendation=KeepFormalRanking`
- A3 failure audit regression：`20`，其中 `MissingFeatureMetadata=15`、`ScoreScaleMismatch=5`
- Extended failure audit regression：`30`，其中 `MissingFeatureMetadata=25`、`ScoreScaleMismatch=5`

边界：

- Feature envelope builder 只使用 runtime metadata、lifecycle / reviewStatus、provenance、replacement chain、diagnostics 和 source refs。
- Eligibility guard 只影响 shadow rerank 候选池，不改变正式 retrieval output、selected set、score、`PackingPolicy` 或 package sections。
- eval label 只用于 audit/report，不进入 feature builder 或 guard。
- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 当前结论仍是 `KeepFormalRanking`，不进入 runtime trace 或 guarded opt-in。

## Learning Loop Phase CR1.3：Ranker Score Scale & Listwise Calibration Audit

CR1.3 新增 candidate reranker score distribution 和 listwise calibration audit。该阶段只读取 CR1.2 guard-aware shadow eval，分析 score scale、top1 margin、listwise regression 与 formal ranking implicit priority，不替换正式 scorer，不改变 retrieval / selected set / `PackingPolicy` / package output。

新增 DTO / report：

- `CandidateRerankerScoreDistributionReport`
- `CandidateRerankerScoreDistributionSample`
- `CandidateRerankerListwiseCalibrationReport`
- `CandidateRerankerListwiseCalibrationSample`
- `CandidateRerankerFormalPriorityComparison`
- `CandidateRerankerCalibrationIssues`

新增 CLI：

- `eval candidate-reranker-score-distribution`
- `eval candidate-reranker-listwise-calibration`

输出：

- `learning/ranker/candidate-reranker-score-distribution-a3.json`
- `learning/ranker/candidate-reranker-score-distribution-extended.json`
- `learning/ranker/candidate-reranker-score-distribution.md`
- `learning/ranker/candidate-reranker-listwise-calibration-a3.json`
- `learning/ranker/candidate-reranker-listwise-calibration-extended.json`
- `learning/ranker/candidate-reranker-listwise-calibration.md`

当前结果：

- A3 score distribution：`ScoreMean=55.4941`，`ScoreStdDev=30.1902`，`Top1MarginAverage=31.2068`，`ScoreOverlapMustHitVsNonHit=0.9053`，`LowMarginDecisionCount=0`，`Recommendation=NeedsFeatureTuning`
- Extended score distribution：`ScoreMean=37.2669`，`ScoreStdDev=24.1415`，`Top1MarginAverage=27.5602`，`ScoreOverlapMustHitVsNonHit=0.6023`，`LowMarginDecisionCount=3`，`Recommendation=NeedsFeatureTuning`
- A3 listwise calibration：`RegressionCount=20`，`LowMarginDecisionCount=5`，`FormalPriorityMismatchCount=3`，`AverageTopKOverlap=0.5732`，`Recommendation=NeedsFeatureTuning`
- Extended listwise calibration：`RegressionCount=30`，`LowMarginDecisionCount=1`，`FormalPriorityMismatchCount=27`，`AverageTopKOverlap=0.6666`，`Recommendation=NeedsFeatureTuning`

ControlRoom：

- Learning Features 页面新增 Ranker Calibration Summary。
- 展示 score distribution、low-margin decisions、top calibration issues、formal priority mismatch 和 recommended action。

结论：

- Score contract 仍为 passed，risk candidate 不进入 shadow topK。
- 剩余问题集中在 listwise calibration、intent feature 缺失和 formal implicit priority 表达不足。
- 当前仍保持 `KeepFormalRanking` / `NeedsFeatureTuning`，不进入 runtime trace 或 guarded opt-in。

边界：

- eval label 只用于 audit/report，不进入 policy。
- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 不为提高 shadow 指标修改正式 retrieval scoring、selected set、`PackingPolicy` 或 package sections。

## Learning Loop Phase CR1.4：Formal Priority Feature Alignment & Listwise Shadow Repair

CR1.4 新增 formal priority feature alignment 和 shadow-only listwise repair。该阶段只提取正式排序可观测的 priority features，并在 shadow profile 中评估是否能减少 listwise regression；不替换正式 scorer，不改变 retrieval / selected set / `PackingPolicy` / package output。

新增 DTO / 服务：

- `CandidateRerankerFormalPriorityFeatureSet`
- `CandidateRerankerFormalPriorityAlignmentReport`
- `CandidateRerankerFormalPriorityAlignmentSample`
- `FormalPriorityFeatureExtractor`
- `CandidateRerankerFormalPriorityAlignmentRunner`

新增 shadow-only profiles：

- `baseline-lifecycle-aware`
- `formal-priority-aware-v1`
- `formal-priority-aware-with-abstain-v1`

新增 CLI：

- `eval candidate-reranker-formal-priority-alignment`
- `eval candidate-reranker-shadow-eval --profile <profileId>`

输出：

- `learning/ranker/candidate-reranker-formal-priority-alignment-a3.json`
- `learning/ranker/candidate-reranker-formal-priority-alignment-extended.json`
- `learning/ranker/candidate-reranker-formal-priority-alignment.md`

`candidate-reranker-shadow-eval` 新增字段：

- `ShadowProfile`
- `WouldApplyCount`
- `AbstainCount`
- `NetGainAfterAbstain`
- `FormalPriorityRecoveredCount`
- `UnexplainedRegressionCount`

当前结果：

- A3 alignment：`RegressionCount=20`，`RecoveredCount=0`，`UnexplainedMismatchCount=20`，`AbstainCount=0`，`NetGainAfterAbstain=3`，`Recommendation=NeedsFeatureTuning`
- Extended alignment：`RegressionCount=30`，`RecoveredCount=0`，`UnexplainedMismatchCount=30`，`AbstainCount=2`，`NetGainAfterAbstain=23`，`Recommendation=NeedsFeatureTuning`
- baseline shadow eval：A3 `NetGain=-17`，`NetGainAfterAbstain=3`，`Recommendation=KeepFormalRanking`
- baseline shadow eval：Extended `NetGain=-3`，`NetGainAfterAbstain=27`，`Recommendation=KeepFormalRanking`

ControlRoom：

- Learning Features 页面新增 Formal Priority Alignment Summary。
- 展示 recovered count、unexplained mismatch、abstain count、net gain after abstain 和 recommendation。

结论：

- formal-priority-aware profile 当前没有追回 baseline regression。
- margin-aware abstain 只用于 shadow recommendation，不改变 formal output。
- 当前仍保持 `KeepFormalRanking`，不进入 runtime trace 或 guarded opt-in。

边界：

- feature extractor 只使用 runtime metadata。
- eval label 只用于 audit/report，不进入 policy。
- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 不为提高 shadow 指标修改正式 retrieval scoring、selected set、`PackingPolicy` 或 package sections。

## Learning Loop Phase S0：Shadow Baseline Freeze Registry & Readiness Dashboard

S0 新增统一 shadow readiness registry、freeze report、runtime-change gate 和 ControlRoom Learning Readiness Dashboard。该阶段只读取已有 Graph / Vector / Router / CandidateReranker / Attention / Planning 报告并冻结结论，不改变正式 runtime 行为。

新增 DTO / 服务：

- `ShadowCapabilityReadiness`
- `LearningReadinessRegistry`
- `LearningRuntimeChangeReadinessGateReport`
- `LearningReadinessFreezeRunner`

新增 CLI：

- `eval learning-readiness-freeze-report`
- `eval learning-runtime-change-readiness-gate`

输出：

- `learning/readiness/learning-readiness-freeze-report.json`
- `learning/readiness/learning-readiness-freeze-report.md`
- `learning/readiness/learning-runtime-change-readiness-gate.json`
- `learning/readiness/learning-runtime-change-readiness-gate.md`

当前 freeze 结论：

- `GraphExpansion`：`ReadyForGuardedOptIn`，仅限 `audit-v1` / `conflict-v1`
- `VectorRetrieval`：`PreviewOnly` / `BlockedByRecall`
- `RouterIntentClassifier`：`KeepRuleBased`
- `CandidateReranker`：`KeepFormalRanking`
- `AttentionRerank`：`ApplyGuarded` opt-in only，default off
- `PlanningProposal`：intent-scoped opt-in only，default off

当前 runtime-change gate：

- `Passed=True`
- `Recommendation=RuntimeChangeRulesSatisfied`
- 含义是 registry 已正确禁止不允许的 runtime mode，不代表任何 shadow capability 被默认启用。

ControlRoom：

- Learning Features 页面新增 Learning Readiness Dashboard。
- 展示 capability、status、gate、recommendation、blocked reasons、allowed modes、forbidden modes、last report path。
- Learning Features 页面新增 Learning Runtime Change Gate。

边界：

- 不改变正式 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- Vector V4 gate 未通过时禁止 vector runtime shadow。
- Router breaks > fixes 时禁止 router guarded opt-in。
- CandidateReranker raw `NetGain <= 0` 时禁止 reranker runtime shadow / opt-in。
- Graph `normal-v1` / `current-task-v1` 禁止 default on。

## Learning Loop Phase F1：Runtime Feedback Collection Foundation

F1 新增运行时反馈采集基础设施，用于收集用户/操作员对 shadow 或 runtime 输出的反馈，但不自动训练、不自动调权、不改变任何正式 runtime 行为。

新增内容：

- `LearningFeedbackEvent` / `LearningFeedbackEventQuery`
- `LearningFeedbackSubmitResult` / `LearningFeedbackSummaryReport`
- `ILearningFeedbackStore`
- `InMemoryLearningFeedbackStore`
- `FileLearningFeedbackStore`
- `LearningFeedbackService`
- `ContextCoreClient.SubmitLearningFeedbackAsync`
- `ContextCoreClient.GetLearningFeedbackAsync(LearningFeedbackEventQuery)`
- `ContextCoreClient.GetLearningFeedbackSummaryAsync`
- `ContextCoreClient.ExportLearningFeedbackAsync`

新增 API：

- `POST /api/learning/feedback`
- `GET /api/learning/feedback?runtimeFeedback=true`
- `GET /api/learning/feedback/summary`
- `GET /api/learning/feedback/export`

新增 CLI：

- `eval learning-feedback-summary`
- `eval export-learning-feedback`

输出：

- `learning/feedback/learning-feedback-summary.json`
- `learning/feedback/learning-feedback-summary.md`
- `learning/feedback/learning-feedback-events.jsonl`

ControlRoom：

- Learning Features 页面新增 Runtime Feedback Summary。
- 展示反馈总数、capability 分布、kind 分布、recent feedback、export path。

治理边界：

- 原 `GET /api/learning/feedback` 默认仍保持 promotion feedback 查询兼容。
- runtime feedback 使用独立 `runtime-feedback-events.jsonl`。
- 反馈按稳定 `FeedbackId` upsert，减少重复存储和噪声。
- 支持 `metadataOnly=true` / `redactionMode=metadata-only`，不把用户原始敏感内容直接作为训练样本。
- metadata 写入 `trainingUse=disabled_until_review`。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Learning Loop Phase F1.1：Feedback Capture Surfaces & Submission Smoke Flow

F1.1 在 F1 runtime feedback foundation 上补齐显式提交入口、目标类型绑定、ControlRoom 操作和 smoke 验证。该阶段仍不训练、不调权、不自动启用任何 shadow / opt-in，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 DTO：

- `LearningFeedbackTargetType`
- `LearningFeedbackSubmitRequest`
- `LearningFeedbackSmokeReport`

目标类型：

- `PackageItem`
- `RetrievalCandidate`
- `RouterPrediction`
- `VectorCandidate`
- `GraphExpansionCandidate`
- `RankerCandidate`
- `PromotionCandidate`
- `ConstraintGapCandidate`
- `StableReviewCandidate`

提交字段：

- `CapabilityId`
- `TargetId`
- `TargetType`
- `SourceOperationId`
- `FeedbackKind`
- `Reason`
- `RedactionMode`
- `MetadataOnly`
- `TrainingUse=disabled_until_review`

新增 CLI：

- `eval submit-learning-feedback`
- `eval learning-feedback-smoke`

Smoke 输出：

- `learning/feedback/learning-feedback-smoke-report.json`
- `learning/feedback/learning-feedback-smoke-report.md`

当前 smoke 结果：

- `Recommendation=FeedbackCaptureReady`
- `failed=0`
- `before=1`
- `after=1`

ControlRoom：

- Learning Features 页面新增 `F feedback` 提交动作。
- Runtime Feedback Summary 展示 recent feedback、target type 分布、capability 分布、metadataOnly count 和 trainingUse disabled count。

治理边界：

- target type 必须属于 `LearningFeedbackTargetType`。
- invalid capability / invalid target type 会拒绝。
- duplicate `FeedbackId` 走 upsert，不制造重复反馈噪声。
- metadata-only 默认保留，避免把原始敏感内容直接作为训练样本。

## Learning Loop Phase F2：Feedback Review & Feature Candidate Builder

F2 在 runtime feedback collection 之后新增人工 review、redaction validation、feature candidate builder 和 dataset candidate export。该阶段仍不训练、不调权、不自动启用任何 shadow / opt-in，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 DTO / store / service：

- `FeedbackReviewStatus`
- `LearningFeedbackReviewRequest`
- `LearningFeedbackReviewResult`
- `LearningFeedbackReviewRecord`
- `LearningFeedbackReviewSummaryReport`
- `FeedbackFeatureCandidate`
- `LearningFeedbackFeatureCandidateReport`
- `ILearningFeedbackReviewStore`
- `InMemoryLearningFeedbackReviewStore`
- `FileLearningFeedbackReviewStore`
- `LearningFeedbackReviewService`
- `LearningFeedbackFeatureCandidateBuilder`

新增 API：

- `POST /api/learning/feedback/{feedbackId}/review/approve`
- `POST /api/learning/feedback/{feedbackId}/review/reject`
- `POST /api/learning/feedback/{feedbackId}/review/needs-redaction`
- `POST /api/learning/feedback/{feedbackId}/review/needs-evidence`
- `GET /api/learning/feedback/reviews`
- `GET /api/learning/feedback/reviews/summary`

新增 CLI：

- `eval learning-feedback-review-summary`
- `eval learning-feedback-feature-candidates`

输出：

- `learning/feedback/learning-feedback-review-summary.json`
- `learning/feedback/learning-feedback-review-summary.md`
- `learning/feedback/learning-feedback-feature-candidates.jsonl`
- `learning/feedback/learning-feedback-feature-candidates.md`
- `learning/feedback/learning-feedback-feature-candidates-report.json`

Builder 规则：

- 只处理 `ApprovedForDataset`。
- review `TrainingUse` 必须不是 `disabled_until_review`。
- `RedactionChecked=true` 才允许生成候选。
- `metadataOnly=true` 的反馈只生成 metadata-safe candidate，不泄露原始 reason / correction。
- 缺 `SourceOperationId`、`TargetId` 或 `CapabilityId` 时标记为 `NeedsMoreEvidence`，不生成候选。
- candidate id 稳定生成，重复运行不会制造重复样本噪声。

ControlRoom：

- Learning Features 页面 Runtime Feedback 区块增加 review summary。
- 增加 feature candidate summary，只读本地候选导出报告。

治理边界：

- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 不从 feedback review 自动训练或调权。
- 不改变任何正式 runtime 行为。

## Learning Loop Phase F3：Feedback Quality & Dataset Readiness Report

F3 在 F2 的 feedback review / feature candidate builder 后新增质量报告，用于判断 feedback 是否具备进入后续离线 dataset export / offline baseline 的基础条件。该阶段仍不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 DTO / builder：

- `LearningFeedbackQualityReport`
- `LearningFeedbackDatasetReadiness`
- `LearningFeedbackQualityReportBuilder`

新增 CLI：

- `eval learning-feedback-quality`

输出：

- `learning/feedback/learning-feedback-quality-report.json`
- `learning/feedback/learning-feedback-quality-report.md`

报告指标：

- `FeedbackCount`
- `PendingReviewCount`
- `ApprovedCount`
- `RejectedCount`
- `NeedsRedactionCount`
- `NeedsEvidenceCount`
- `MetadataOnlyCount`
- `TrainingDisabledCount`
- `FeatureCandidateCount`
- `CandidateCountByCapability`
- `CandidateCountByLabelKind`
- `RedactionCoverageRate`
- `ReviewCoverageRate`
- `ApprovedDatasetReadiness`

Readiness capability：

- `RouterIntentClassifier`
- `CandidateReranker`
- `VectorRetrieval`
- `GraphExpansion`
- `PromotionJudge`
- `ConstraintGapJudge`

Blocked reasons：

- `NoFeedback`
- `NoApprovedFeedback`
- `NeedsReview`
- `NeedsRedaction`
- `MissingNegativeSamples`
- `MissingPositiveSamples`
- `MetadataOnlyInsufficient`
- `NeedsMoreEvidence`
- `LabelCoverageTooLow`

ControlRoom：

- Learning Features 页面 Runtime Feedback 区块增加 Feedback Quality / Dataset Readiness Summary。
- 只读展示 review coverage、redaction coverage、feature candidate count、readiness by capability 和 blocked reasons。

治理边界：

- 未审核 feedback 仍不会生成候选。
- metadata-only 候选不会泄露原始文本。
- readiness 只用于离线报告，不会自动启用训练、调权或 runtime opt-in。

## Learning Loop Phase F3.1：Feedback Review Operations & Approved Dataset Smoke

本阶段补齐 runtime feedback 的人工 review 操作、review smoke flow 和 approved dataset gate。范围仍限制为 feedback review / report / dataset gate，不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增能力：

- ControlRoom Learning Features 页面支持 `A Approve`、`R Reject`、`N NeedsRedaction`、`E NeedsEvidence`、`H ReviewHistory`。
- Review 操作前展示 feedback 详情，并要求输入 `YES`。
- `eval learning-feedback-review-smoke` 覆盖 submit、needs-redaction、reject、approve metadata-safe feedback、feature candidate export 和 quality refresh。
- `eval learning-approved-feedback-dataset-gate` 输出 approved dataset gate。

输出文件：

- `learning/feedback/learning-feedback-smoke-report.json`
- `learning/feedback/learning-feedback-smoke-report.md`
- `learning/feedback/learning-approved-feedback-dataset-gate.json`
- `learning/feedback/learning-approved-feedback-dataset-gate.md`

治理结论：

- smoke feedback 固定标记 `trainingUse=smoke_test_only` 与 `excludedFromTraining=true`。
- gate 将 smoke 候选排除在 trainable dataset 外，不能用 smoke 数据绕过真实 approved dataset readiness。
- 当前真实数据仍应因 `NoApprovedFeedback` / `NeedsReviewedFeedback` 失败，这是预期状态。
## Storage Foundation Phase FS1 - FileSystem Layout Registry & Artifact Routing

- 新增 `ArtifactKind`、`ArtifactDescriptor`、`IContextPathResolver`、`IArtifactStore`。
- 新增 `ContextCoreDataLayout`，负责 workspace / collection id sanitize、root escape prevention 和稳定路径解析。
- 新增 `FileArtifactStore`，支持 JSON / Markdown 原子写、JSONL 追加、读取、列表和 manifest upsert。
- 第一批 eval/report 写出保留旧路径，同时镜像到 `context-core-data` 标准 artifact 布局。
- ControlRoom Service Admin / Runtime 页面增加 File Layout Status，只读展示 data root、manifest/report 计数和路径样例。
- 本阶段不改变 retrieval、planning、scoring、PackingPolicy 或 package output。

## Storage Foundation Phase FS2 - Memory Layer Store Routing & Temporal Memory Placeholder

- 扩展 `ArtifactKind`，覆盖 short-term raw / working / archive / compaction、temporal placeholder、candidate item/review/diagnostics/evidence、stable item/lifecycle review/replacement/provenance/diagnostics。
- `ContextCoreDataLayout` 增加标准 memory layer 路由：`memory/short-term/*`、`memory/temporal/*`、`memory/candidate/*`、`memory/stable/*`。
- 第一批 memory store 新写入走 layout resolver：short-term raw / working / archive / compaction、CandidateMemory review、StableLifecycleReview、stable memory。
- 保留 legacy path fallback：旧文件可读，不删除旧文件，不做破坏性迁移。
- 新增 `MemoryLayoutDiagnostics`，ControlRoom Admin / Memory 页面展示 memory path、artifact count、legacy fallback、temporal placeholder 与 diagnostics。
- 本阶段不改变 memory 业务逻辑、retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase FS3 - Trace / Tool-call Artifact Routing

- 扩展 `ArtifactKind`，覆盖 retrieval、planning、tool-call、router/ranker/vector/graph shadow、package build、model call 和 error trace。
- `ContextCoreDataLayout` 增加 trace 日期分片路由：`traces/{category}/{date}`，tool-call 额外按 `{operationId}` 分目录。
- 新增 `TraceArtifactDescriptorFactory` 与 `TraceArtifactWriter`，统一生成 trace descriptor 并支持 trace JSON / JSONL / tool-call request/response/error 写入。
- 第一批 trace writer 新写入走标准 layout：retrieval trace、package build trace、router shadow trace；ranker / graph shadow 继续作为 retrieval trace block 随 retrieval trace routing 迁移。
- 查询保留 legacy fallback，旧 trace 可读，不删除旧文件，不做破坏性迁移。
- 新增 `TraceLayoutDiagnostics`，ControlRoom Admin / Runtime 页面展示 trace root、分类计数、tool-call placeholder、legacy fallback 和 diagnostics。
- 本阶段不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase FS4 - Eval / Report Artifact Migration & Manifest Enrichment

- 扩展 `ArtifactManifestEntry`，记录 artifact kind、workspace / collection、legacy path、content type、extension、report / capability / provider、policy / schema version、created / updated time、size、content hash、latest / snapshot 标记和 source command。
- 新增 `ReportArtifactRegistry`、`ReportArtifactDescriptorFactory` 与 `ReportArtifactMirrorWriter`，统一把第一批 eval / learning / vector / graph 报告从 legacy path 镜像到标准 layout。
- 第一批覆盖 `eval/p15/*`、`eval/vector*`、`eval/graph*`、`eval/relation-expansion*`、`learning/feedback/*`、`learning/readiness/*`、`learning/router/*`、`learning/ranker/*`、`vector/reindex/*`。
- 旧路径继续写出；标准路径同步写出；manifest 同时记录 legacy path 与 standard relative path；不删除旧文件，不做破坏性迁移。
- 增加 latest report alias：mirror 时同步写 `ReportId=latest`，manifest 中标记 `isLatest=true`，snapshot report 标记 `isSnapshot=true`。
- 新增 `ReportLayoutDiagnostics`，统计 report count by kind、latest report count、legacy mirrored count、missing standard / legacy artifact、duplicate content hash、largest reports 和 manifest count。
- ControlRoom Admin / Runtime 页面新增 Report Layout Status，只读展示 report categories、latest reports、legacy mirror、diagnostics 和 sample resolved paths。
- 本阶段不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB0 - Artifact Plane vs Operational Store Boundary

- 新增 `StorageResponsibilityKind`、`StorageResponsibilityEntry`、`StorageBoundaryReport`。
- 新增 `StorageResponsibilityRegistry`，为所有 `ArtifactKind` 和关键 StoreKind 标记 responsibility、current provider、preferred provider、migration priority、migration risk 和 notes。
- 明确 EvalReport / LearningReport / ReadinessGate 属于 `ArtifactOnly` / `FileSystemPreferred`。
- 明确 retrieval / tool-call / shadow trace 属于 `TraceOnly` / `FileSystemPreferred`。
- 明确 FeedbackExport 属于 `ExportOnly` / `FileSystemPreferred`。
- 明确 ContextItem、MemoryItem、RelationItem、ConstraintState、JobRecord 属于 `OperationalState` / `DatabaseRecommended`。
- 明确 VectorIndexEntry 属于 `IndexState` / `DatabaseRecommended`，后续可作为 pgvector candidate。
- 新增 `eval storage-boundary-report`，输出 `storage/storage-boundary-report.json` 与 `storage/storage-boundary-report.md`。
- ControlRoom Admin / Runtime 页面新增 Storage Boundary Status，只读展示分类计数、高优先级迁移候选和 recommended next phases。
- 本阶段只标记迁移候选，不迁移数据库，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB1 - Postgres Operational Store Foundation & Schema Baseline

- 扩展 `PostgresOptions` 并新增兼容型 `PostgresStoreOptions`，覆盖 `Enabled`、`ConnectionString`、`SchemaName`、`AutoMigrate`、`CommandTimeoutSeconds`、`ProviderId`。
- 新增 `IPostgresConnectionFactory` 与 `IStoreMigrationRunner` 契约。
- 新增 `context_schema_migrations` migration table，并保留旧 `schema_versions` 兼容读取。
- 扩展 baseline schema migration，覆盖 workspaces、collections、context/memory/relation/constraint/learning/job/vector operational tables。
- 新增 `PostgresOperationalStoreDiagnostics`、status / diagnostics / migration preview / migration apply response DTO。
- 新增 API：`GET /api/admin/storage/postgres/status`、`GET /api/admin/storage/postgres/diagnostics`、`POST /api/admin/storage/postgres/migrations/dry-run`、`POST /api/admin/storage/postgres/migrations/apply`。
- `ContextCoreClient` 增加 Postgres storage status / diagnostics / migration preview / apply 强类型方法。
- ControlRoom Admin / Runtime 页面新增 Postgres Operational Store Status，只读展示 enabled、connection、schema version、pending migrations、missing required tables 和 capability status。
- 新增 CLI：`eval postgres-storage-diagnostics`、`eval postgres-migration-preview`、`eval postgres-migration-apply --confirm`。
- 安全策略：默认不 auto migrate；migration apply 必须显式 `--confirm`；report 只输出 redacted connection string。
- ORM 策略：DB1 的 DDL / migration / diagnostics 保持 Npgsql 原生 SQL；DB2+ CRUD / query store 可引入 Dapper 等高性能轻量 ORM。
- 本阶段不迁移数据，不切换 provider，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB1.1 - Postgres Local Migration Smoke & Schema Verification

- 新增 `appsettings.Postgres.sample.json`，使用占位 connection string、`SchemaName=contextcore_smoke`、`AutoMigrate=false`，禁止提交真实密码。
- 新增 `docs/postgres-local-smoke-runbook.md`，说明本地 Postgres 配置、独立 schema、dry-run / apply、schema verification、cleanup 和 connection string 安全规则。
- 新增 CLI：`eval postgres-migration-smoke`。
- smoke apply 必须显式 `--confirm`；没有 confirm 时只输出 `ConfirmRequired` 诊断，不写数据库。
- cleanup 必须显式 `--drop-confirm`，且要求配置独立 schema，避免误删默认 search path 对象。
- 新增 `PostgresSchemaVerificationReport`，输出 `storage/postgres/postgres-schema-verification-report.json` 与 `.md`。
- schema verification 检查 connection、schema version、applied migration count、required tables、required indexes、missing tables/indexes，并输出 `NotConfigured` / `BlockedByConnection` / `SchemaIncomplete` / `MigrationFailed` / `ReadyForProviderDevelopment`。
- ControlRoom Postgres Operational Store Status 增加 Schema Verification Summary，只读展示 schema name、version、required/missing tables/indexes 和 recommendation。
- 本阶段不迁移数据，不切换 provider，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB1.2 - Postgres Real Local Smoke Apply & Baseline Verification

- 本阶段目标是执行真实本地 Postgres smoke apply，验证 `contextcore_smoke` 独立 schema 的 baseline tables / indexes。
- 补充用户私有配置读取：CLI 按 `--connection-string`、`CONTEXTCORE_POSTGRES_CONNECTION_STRING`、`%USERPROFILE%\.contextcore\secrets.json` 顺序读取 Postgres smoke 配置。
- 真实连接串放在用户目录，不提交到仓库，不写入报告明文。
- 当前本地使用 `pgvector/pgvector:pg17` smoke 容器和 `contextcore_smoke` 独立 schema 执行验证。
- 已补充 `docs/postgres-local-smoke-runbook.md` 的 DB1.2 preflight、用户私有配置路径与成功验收条件。
- 本阶段未迁移数据，未切换 provider，未改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2 - RelationStore Postgres Provider

- 新增 / 增强 `PostgresRelationStore`，支持 upsert、get by id、delete、list、source query、target query、type query、lifecycle query、reviewStatus query 和 replacement chain 相关边查询。
- RelationStore Postgres provider 只用于 explicit diagnostics / parity eval；默认 runtime provider 仍为 `FileSystemRelationStore`。
- 不 dual-write、不 shadow-read、不迁移业务数据、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 新增 `PostgresRelationStoreOptions`、`PostgresRelationStoreDiagnostics`、`PostgresRelationStoreParityReport`。
- 新增 CLI：
  - `eval postgres-relation-store-diagnostics`
  - `eval postgres-relation-store-parity --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-relation-store-diagnostics.json`
  - `storage/postgres/postgres-relation-store-diagnostics.md`
  - `storage/postgres/postgres-relation-store-parity-report.json`
  - `storage/postgres/postgres-relation-store-parity-report.md`
- 当前本地结果：
  - diagnostics recommendation = `ReadyForParityEval`
  - relation table exists = `true`
  - relation reviews table exists = `true`
  - missing indexes = `0`
  - parity recommendation = `ParityPassed`
  - mismatches = `0`
- ControlRoom Admin / Runtime 增加 RelationStore Provider Status，只读展示 active runtime provider、Postgres provider 状态、relation count、diagnostics 和 not-used-for-runtime warning。

## Storage Foundation Phase DB2.1 - Relation Review & Diagnostics Postgres Provider

- 新增 `PostgresRelationReviewStore`，实现 `IRelationReviewStore` append / query-by-relation 契约，并支持 workspace / collection、reviewStatus、reviewer、operationId filter 和 latest review lookup。
- 新增 `PostgresRelationDiagnosticsStore`，作为 relation diagnostics snapshot/projection store，支持 relationId、itemId、diagnostic kind、severity filter。
- 新增 `RelationDiagnosticsSnapshot`、`PostgresRelationReviewProviderDiagnostics`、`PostgresRelationReviewParityReport`。
- 新增 `relation_diagnostics` schema table 和 relation diagnostics indexes，schema version 提升为 `cc-schema-v4`。
- 新增 CLI：
  - `eval postgres-relation-review-diagnostics`
  - `eval postgres-relation-review-parity --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-relation-review-diagnostics.json`
  - `storage/postgres/postgres-relation-review-diagnostics.md`
  - `storage/postgres/postgres-relation-review-parity-report.json`
  - `storage/postgres/postgres-relation-review-parity-report.md`
- 当前本地结果：
  - review diagnostics recommendation = `ReadyForParityEval`
  - relation reviews table exists = `true`
  - relation diagnostics table exists = `true`
  - missing indexes = `0`
  - parity recommendation = `ParityPassed`
  - mismatches = `0`
  - cleanup performed = `true`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 review provider status、diagnostics provider status、review count、diagnostics count 和 parity status。
- 本阶段未切换 runtime provider，未 dual-write，未 shadow-read，未迁移业务数据，未改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.2 - Relation Governance Provider Readiness Gate

- 新增 `PostgresRelationGovernanceParityReport` 与 `PostgresRelationGovernanceReadinessGateReport`。
- 新增 CLI：
  - `eval postgres-relation-governance-parity --cleanup-confirm`
  - `eval postgres-relation-governance-readiness-gate`
- 输出：
  - `storage/postgres/postgres-relation-governance-parity-report.json`
  - `storage/postgres/postgres-relation-governance-parity-report.md`
  - `storage/postgres/postgres-relation-governance-readiness-gate.json`
  - `storage/postgres/postgres-relation-governance-readiness-gate.md`
- Governance parity 在隔离 workspace / collection 中统一覆盖 relation edge、source / target / type / lifecycle / reviewStatus query、replacement chain query、relation review latest / status filter、diagnostics relation / item / kind / severity filter 和 cleanup。
- Readiness gate 检查 Postgres storage ready、schema version `cc-schema-v4`、relation / relation_reviews / relation_diagnostics 表、缺失索引、relation store parity、relation review parity、diagnostics parity、governance parity、mismatch、cleanup、`UseForRuntime=false` 和最新 P15 report。
- 当前本地结果：
  - governance parity recommendation = `ReadyForDualWrite`
  - relation parity passed = `true`
  - review parity passed = `true`
  - diagnostics parity passed = `true`
  - governance parity passed = `true`
  - mismatches = `0`
  - cleanup performed = `true`
  - readiness gate passed = `true`
  - gate recommendation = `ReadyForDualWrite`
  - can dual-write = `true`
  - can shadow-read = `false`
  - can runtime switch = `false`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Governance Readiness，只读展示 edge/review/diagnostics/governance parity、gate、blocked reasons 和 allowed modes。
- 本阶段仍未切换 runtime provider，未 dual-write，未 shadow-read，未迁移业务数据，未改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.3 - Relation Governance Dual-write Trace Collection

- 新增 `RelationGovernanceDualWriteOptions`，默认 `Enabled=false`、`WritePostgres=false`、`TraceEnabled=true`、`FallbackOnPostgresFailure=true`、`FailOnMismatch=false`。
- 新增 `RelationGovernanceDualWriteCoordinator`，覆盖 relation edge upsert / delete、relation review write 和 relation diagnostics snapshot write。
- FileSystem 仍是 source of truth；Postgres 只作为显式 smoke / eval 的 dual-write target。
- Postgres 写失败时记录 trace 并 fallback，不影响 FileSystem 正式写。
- 新增 `ArtifactKind.TraceRelationDualWrite`，标准 trace 路由为 `traces/relation-dual-write/{date}`。
- 新增 `RelationGovernanceDualWriteTrace`、`PostgresRelationDualWriteSmokeReport` 和 `PostgresRelationDualWriteQualityReport`。
- 新增 CLI：
  - `eval postgres-relation-dual-write-smoke --cleanup-confirm`
  - `eval postgres-relation-dual-write-quality`
- 输出：
  - `storage/postgres/postgres-relation-dual-write-smoke-report.json`
  - `storage/postgres/postgres-relation-dual-write-smoke-report.md`
  - `storage/postgres/postgres-relation-dual-write-traces.jsonl`
  - `storage/postgres/postgres-relation-dual-write-quality-report.json`
  - `storage/postgres/postgres-relation-dual-write-quality-report.md`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Dual-write Status，只读展示 trace count、mismatch count、Postgres failure count、fallback count 和 recommendation。
- 本阶段不启用 shadow-read，不切换 runtime provider，不迁移历史业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.4 - Relation Governance Shadow-read Comparison

- 新增 `RelationGovernanceShadowReadOptions`，默认 `Enabled=false`、`TraceEnabled=true`、`ReadPostgres=false`、`CompareResults=true`、`FailOnMismatch=false`。
- 新增 `RelationGovernanceShadowReadCoordinator`，覆盖 relation edge get/list/source/target/type/lifecycle/reviewStatus/replacement chain、relation review latest/list/filter、diagnostics relation/item/kind/severity 查询。
- FileSystem read result 始终是正式返回值；Postgres read result 只用于 comparison。
- Postgres 读失败时记录 trace 和 fallback，不影响 FileSystem 正式读。
- 新增 `ArtifactKind.TraceRelationShadowRead`，标准 trace 路由为 `traces/relation-shadow-read/{date}`。
- 新增 `RelationGovernanceShadowReadTrace`、`PostgresRelationShadowReadSmokeReport` 和 `PostgresRelationShadowReadQualityReport`。
- 新增 CLI：
  - `eval postgres-relation-shadow-read-smoke --cleanup-confirm`
  - `eval postgres-relation-shadow-read-quality`
- 输出：
  - `storage/postgres/postgres-relation-shadow-read-smoke-report.json`
  - `storage/postgres/postgres-relation-shadow-read-smoke-report.md`
  - `storage/postgres/postgres-relation-shadow-read-traces.jsonl`
  - `storage/postgres/postgres-relation-shadow-read-quality-report.json`
  - `storage/postgres/postgres-relation-shadow-read-quality-report.md`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Shadow-read Status，只读展示 trace count、mismatch count、Postgres read failure count、FileSystem/Postgres 平均 latency 和 recommendation。
- 本阶段不切换 runtime provider，不迁移业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.5 - Relation Governance Guarded Provider Switch

- 新增 `RelationGovernanceProviderMode`，支持 `FileSystemPrimary`、`DualWriteOnly`、`ShadowRead`、`GuardedPostgresPrimary`。
- 新增 `RelationGovernanceProviderSwitchOptions`，默认 `Enabled=false`、`Mode=FileSystemPrimary`、`FallbackToFileSystem=true`、`ContinueComparisonTrace=true`、`FailClosedOnMismatch=true`、`RequireReadinessGate=true`。
- 新增 `RelationGovernanceProviderRouter`，根据 mode 决定 relation governance 的 write path、read path、fallback path 和 comparison trace path。
- `GuardedPostgresPrimary` 必须通过 governance readiness gate 与 shadow-read quality gate，且 workspace / collection 必须位于 allowlist。
- Postgres failure 时 fallback FileSystem；mismatch 写入 trace，并按 `FailClosedOnMismatch` 阻断或 fallback。
- 新增 `ArtifactKind.TraceRelationProviderSwitch`，标准 trace 路由为 `traces/relation-provider-switch/{date}`。
- 新增 `RelationGovernanceProviderSwitchTrace`、`PostgresRelationProviderSwitchSmokeReport` 和 `PostgresRelationProviderSwitchGateReport`。
- 新增 CLI：
  - `eval postgres-relation-provider-switch-smoke --cleanup-confirm`
  - `eval postgres-relation-provider-switch-gate`
- 输出：
  - `storage/postgres/postgres-relation-provider-switch-smoke-report.json`
  - `storage/postgres/postgres-relation-provider-switch-smoke-report.md`
  - `storage/postgres/postgres-relation-provider-switch-gate.json`
  - `storage/postgres/postgres-relation-provider-switch-gate.md`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Provider Switch Status，只读展示 current mode、allowlist、primary provider、fallback、readiness gate、switch gate、recent traces 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。默认 runtime provider 仍为 FileSystem。

## Storage Foundation Phase DB2.6 - Relation Governance Runtime Canary

- 新增 `RelationGovernanceCanaryOptions`，默认 `Enabled=false`、`Mode=GuardedPostgresPrimary`、`FallbackToFileSystem=true`、`ContinueComparisonTrace=true`、`FailClosedOnMismatch=true`、`RequireProviderSwitchGate=true`。
- 新增 `RelationGovernanceCanaryRunner` 与 `PostgresRelationRuntimeCanaryReport`。
- 新增 CLI：
  - `eval postgres-relation-runtime-canary --cleanup-confirm`
- Canary 默认 scope 为 `contextcore_canary/relation-governance-canary`，只在该隔离 workspace / collection 中启用 `GuardedPostgresPrimary`。
- Canary preflight 检查 Postgres storage diagnostics、governance readiness gate、dual-write quality、shadow-read quality 和 provider switch gate。
- Canary 流程覆盖 relation edge / review / diagnostics 写入，以及 get/list/source/target/type/lifecycle/reviewStatus/replacement-chain/latest-review/diagnostics relation-item-kind-severity 查询。
- 输出：
  - `storage/postgres/postgres-relation-runtime-canary-report.json`
  - `storage/postgres/postgres-relation-runtime-canary-report.md`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Runtime Canary Status，只读展示 canary scope、provider mode、gate、primary read/write、fallback、mismatch 和 recommendation。
- 本阶段不做 global default on，不迁移历史业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。`ReadyForScopedServiceMode` 只表示 canary scope 可进入后续 scoped service mode 讨论。

## Storage Foundation Phase DB2.7 - Relation Governance Scoped Service Mode

- 扩展 `RelationGovernanceProviderSwitchOptions` / `RelationGovernanceCanaryOptions`，支持 `WorkspaceAllowlist`、`CollectionAllowlist` 和 `RequireRuntimeCanaryPassed`。
- Service filesystem runtime 下新增 scoped relation governance wrapper：allowlist 命中且 gate/canary 通过时，relation edge / review 可走 `GuardedPostgresPrimary`；未命中 allowlist 时保持 `FileSystemPrimary`。
- Postgres primary 写入时保留 FileSystem fallback 副本；读路径继续 comparison trace，mismatch 可 fail-closed。
- 新增 Admin API：
  - `GET /api/admin/storage/relation-provider/status`
  - `GET /api/admin/storage/relation-provider/scoped-diagnostics`
- `ContextCoreClient` 增加 relation provider status / scoped diagnostics 强类型方法。
- 新增 CLI：
  - `eval postgres-relation-scoped-service-mode-smoke --cleanup-confirm`
  - `eval postgres-relation-scoped-service-mode-gate`
- 输出：
  - `storage/postgres/postgres-relation-scoped-service-mode-smoke-report.json`
  - `storage/postgres/postgres-relation-scoped-service-mode-smoke-report.md`
  - `storage/postgres/postgres-relation-scoped-service-mode-gate.json`
  - `storage/postgres/postgres-relation-scoped-service-mode-gate.md`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Scoped Service Mode 区块，只读展示 mode、active provider、allowlist、fallback、comparison trace、gate/canary status、mismatch、Postgres failure 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.8 - Relation Governance Scoped Service Mode Extended Canary

- 新增 `RelationGovernanceExtendedCanaryOptions`，默认 `Enabled=false`，仅允许显式 isolated scope canary。
- 新增 `RelationGovernanceExtendedCanaryRunner` 与 `PostgresRelationScopedExtendedCanaryReport`。
- `RelationGovernanceProviderRouter` 增加 `DeleteRelationAsync`，delete 也走 guarded provider switch / fallback / comparison trace。
- Extended canary 默认 scope：`contextcore_canary/relation-governance-extended-canary`。
- 覆盖 relation edge create / update / delete / source / target / type / lifecycle / reviewStatus 查询。
- 覆盖 relation review review / reject / deprecate / needs-evidence / latest / history。
- 覆盖 diagnostics snapshot relation / item / kind / severity 查询。
- 覆盖 replacement chain `superseded_by` / `replaces` 查询。
- 覆盖 graph expansion preview `audit-v1`、`conflict-v1`、`normal-v1`、`current-task-v1` 的 FileSystem / Postgres parity。
- 新增 CLI：
  - `eval postgres-relation-scoped-extended-canary --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-relation-scoped-extended-canary-report.json`
  - `storage/postgres/postgres-relation-scoped-extended-canary-report.md`
  - `storage/postgres/postgres-relation-scoped-extended-canary-traces.jsonl`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Extended Canary Summary，只读展示 operation count、mismatch、fallback、graph preview parity、review lifecycle parity、diagnostics parity、replacement chain parity 和 recommendation。
- 本阶段不做 global default on，不迁移历史业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.9 - Relation Governance Selected Workspace Canary

- 新增 `RelationGovernanceSelectedWorkspaceCanaryOptions`，默认 `Enabled=false`，selected workspace / collection 必须明确配置。
- 新增 `RelationGovernanceSelectedWorkspaceCanaryRunner` 与 `PostgresRelationSelectedWorkspaceCanaryReport`。
- Selected canary 前置 gate 包括 governance readiness gate、provider switch gate、runtime canary、scoped service mode gate、extended canary 和 P15 gate。
- Canary 在受控 selected scope 中执行 relation create / update / delete、review / reject / deprecate / needs-evidence、diagnostics write/query、replacement chain lookup、relation explain、graph expansion preview parity、ControlRoom read path 和 client API-style roundtrip。
- 非 selected scope 仍保持 FileSystem，不写 Postgres。
- 新增 CLI：
  - `eval postgres-relation-selected-workspace-canary`
- 输出：
  - `storage/postgres/postgres-relation-selected-workspace-canary-report.json`
  - `storage/postgres/postgres-relation-selected-workspace-canary-report.md`
  - `storage/postgres/postgres-relation-selected-workspace-canary-traces.jsonl`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Selected Workspace Canary Summary，只读展示 selected scope、mode、operation count、mismatch、fallback、latency 和 rollback instruction。
- 本阶段不做 global default on，不迁移所有历史业务数据，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.10 - Relation Governance Scoped Service Mode Expansion

- 扩展 `RelationGovernanceProviderSwitchOptions`，新增 `ScopedRules`、`ScopeName`、`ScopeDescription`、`RolloutStage` 和 `Enabled` 等 scoped rollout 元数据。
- `RelationGovernanceProviderRouter` 支持 rule-first scope routing：全局 `Mode` 可保持 `FileSystemPrimary`，仅命中启用的 `ScopedRules` 时进入 `GuardedPostgresPrimary`。
- Service scoped diagnostics 支持读取 `ScopedRules`，并保持非命中 scope 为 `FileSystemRelationStore`。
- 新增 `RelationGovernanceScopedExpansionPlan`、`RelationGovernanceScopedExpansionScopeStatus`、`PostgresRelationScopedExpansionReport`。
- 新增 `RelationGovernanceScopedExpansionRunner`：
  - 默认覆盖两个明确 allowlisted scope。
  - 每个 scope 复用 extended canary 的 relation edge、review、diagnostics、replacement chain 和 graph expansion preview parity。
  - 额外验证 non-allowlisted scope 仍为 FileSystem。
- 新增 CLI：
  - `eval postgres-relation-scoped-expansion-plan`
  - `eval postgres-relation-scoped-expansion-smoke --cleanup-confirm`
  - `eval postgres-relation-scoped-expansion-gate`
- 输出：
  - `storage/postgres/postgres-relation-scoped-expansion-plan.json`
  - `storage/postgres/postgres-relation-scoped-expansion-plan.md`
  - `storage/postgres/postgres-relation-scoped-expansion-smoke-report.json`
  - `storage/postgres/postgres-relation-scoped-expansion-smoke-report.md`
  - `storage/postgres/postgres-relation-scoped-expansion-gate.json`
  - `storage/postgres/postgres-relation-scoped-expansion-gate.md`
  - `storage/postgres/postgres-relation-scoped-expansion-traces.jsonl`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Scoped Expansion Summary，只读展示 scope list、per-scope mode、mismatch/fallback/failure、non-allowlisted status、rollout stage、latency 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不关闭 fallback，不关闭 comparison trace，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.11 - Relation Governance Scoped Runtime Observation Window

- 新增 `RelationGovernanceScopedObservationOptions`，默认 `Enabled=false`，继续要求 fallback 和 comparison trace 保持开启。
- 新增 `RelationGovernanceScopedObservationRunner` 与 `PostgresRelationScopedObservationReport`。
- Observation window 复用 scoped expansion 的两个 allowlisted scope，并继续验证 non-allowlisted scope 保持 FileSystem。
- 覆盖 relation edge、review、diagnostics、replacement chain、graph expansion preview、relation explain 和 ControlRoom/API read path simulation。
- 新增 CLI：
  - `eval postgres-relation-scoped-observation-window`
  - `eval postgres-relation-scoped-observation-quality`
- 输出：
  - `storage/postgres/postgres-relation-scoped-observation-report.json`
  - `storage/postgres/postgres-relation-scoped-observation-report.md`
  - `storage/postgres/postgres-relation-scoped-observation-traces.jsonl`
  - `storage/postgres/postgres-relation-scoped-observation-quality-report.json`
  - `storage/postgres/postgres-relation-scoped-observation-quality-report.md`
- Quality gate 检查 scoped expansion gate、mismatch、Postgres failure、non-allowlisted scope leak、fallback path、P95 latency 和 P15 gate。
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Scoped Observation Summary，只读展示 window、operation count、mismatch/failure/fallback、latency、per-scope status、recommendation 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不关闭 fallback，不关闭 comparison trace，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.12 - Relation Governance Selected Normal Workspace Canary

- 新增 `RelationGovernanceSelectedNormalWorkspaceOptions`，默认 `Enabled=false`，selected normal workspace / collection 必须显式配置。
- 新增 `RelationGovernanceSelectedNormalWorkspaceRunner` 与 `PostgresRelationSelectedNormalWorkspaceCanaryReport`。
- 前置 gate 覆盖 governance readiness gate、provider switch gate、runtime canary、scoped service mode gate、extended canary、selected workspace canary、scoped expansion gate、scoped observation quality gate、selected normal scope 配置和 P15 gate。
- Canary 在 selected normal scope 中执行 relation create / update / delete、review / reject / deprecate / needs-evidence、diagnostics write/query、replacement chain lookup、relation explain、graph expansion preview parity、ControlRoom read path 和 client API-style roundtrip。
- 非 selected normal scope 仍保持 FileSystem，不写 Postgres。
- Cleanup 默认 `None`；normal scope 不使用按 workspace / collection 的批量删除作为常规清理，避免误删正常数据。
- 新增 CLI：
  - `eval postgres-relation-selected-normal-workspace-canary`
- 输出：
  - `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.json`
  - `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.md`
  - `storage/postgres/postgres-relation-selected-normal-workspace-canary-traces.jsonl`
- 当前本地结果：`Recommendation=ReadyForLimitedNormalScope`，`OperationCount=35`，`MismatchCount=0`，`PostgresFailureCount=0`，`ScopeLeakCount=0`。
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Selected Normal Workspace Canary Summary，只读展示 selected normal scope、mode、operation count、mismatch/failure/fallback、latency、parity 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不关闭 fallback，不关闭 comparison trace，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.13 - Relation Governance Limited Normal Scope Observation

- 新增 `RelationGovernanceLimitedNormalScopeObservationOptions`，默认 `Enabled=false`，依赖 DB2.12 selected normal workspace canary passed。
- 新增 `RelationGovernanceLimitedNormalScopeObservationRunner` 与 `PostgresRelationLimitedNormalScopeObservationReport`。
- Observation 复用 selected normal canary 的 relation edge、review、diagnostics、replacement chain、graph expansion preview、ControlRoom read path 和 client API-style roundtrip 覆盖，并按 `MaxOperations` 做多轮观察。
- Quality gate 检查 selected normal canary、mismatch、Postgres failure、scope leak、fallback rate、P95 latency、graph/review/diagnostics/replacement parity 和 P15 gate。
- `CleanupMode=None` 默认不删除 normal workspace 数据；显式 cleanup 只删除 canary relation id，不做 normal scope 批量删除。
- 新增 CLI：
  - `eval postgres-relation-limited-normal-scope-observation`
  - `eval postgres-relation-limited-normal-scope-quality`
- 输出：
  - `storage/postgres/postgres-relation-limited-normal-scope-observation-report.json`
  - `storage/postgres/postgres-relation-limited-normal-scope-observation-report.md`
  - `storage/postgres/postgres-relation-limited-normal-scope-quality-report.json`
  - `storage/postgres/postgres-relation-limited-normal-scope-quality-report.md`
  - `storage/postgres/postgres-relation-limited-normal-scope-traces.jsonl`
- 当前本地结果：`Recommendation=ReadyForMultiNormalScopeCanary`，`OperationCount=105`，`MismatchCount=0`，`PostgresFailureCount=0`，`ScopeLeakCount=0`，`FallbackRate=0`。
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Limited Normal Scope Observation Summary，只读展示 selected normal scope、operation count、mismatch/failure/fallback/scope leak、latency、parity 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不关闭 fallback，不关闭 comparison trace，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.14 - Relation Governance Multi Normal Scope Canary

- 新增 `RelationGovernanceNormalScopeRule` 和 `RelationGovernanceMultiNormalScopeCanaryOptions`，默认 `Enabled=false`，至少需要 2 个显式启用的 normal scopes。
- 新增 `RelationGovernanceMultiNormalScopeCanaryRunner` 与 `PostgresRelationMultiNormalScopeCanaryReport`。
- Canary 对每个 scope 复用 limited normal observation 的 relation edge、review、diagnostics、replacement chain、graph expansion preview、ControlRoom read path 和 client API-style roundtrip。
- 额外检查 cross-scope 数据隔离、non-allowlisted scope 保持 FileSystem、per-scope trace 独立和 fallback path。
- Quality gate 检查 limited normal observation quality、P15 gate、scope count、mismatch、Postgres failure、scope leak、non-allowlisted scope、P95 latency 和 graph/review/diagnostics/replacement parity。
- 新增 CLI：
  - `eval postgres-relation-multi-normal-scope-canary`
  - `eval postgres-relation-multi-normal-scope-quality`
- 输出：
  - `storage/postgres/postgres-relation-multi-normal-scope-canary-report.json`
  - `storage/postgres/postgres-relation-multi-normal-scope-canary-report.md`
  - `storage/postgres/postgres-relation-multi-normal-scope-quality-report.json`
  - `storage/postgres/postgres-relation-multi-normal-scope-quality-report.md`
  - `storage/postgres/postgres-relation-multi-normal-scope-traces.jsonl`
- ControlRoom Admin / Runtime 的 RelationStore Provider Status 增加 Multi Normal Scope Canary Summary，只读展示 scopes、per-scope operation count、mismatch/failure/fallback/scope leak、latency、non-allowlisted scope status 和 rollback instruction。
- 本阶段不做 global default on，不迁移历史业务数据，不关闭 fallback，不关闭 comparison trace，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB2.F - Relation Governance Postgres Provider Freeze

- 新增 `docs/relation-governance-postgres-freeze.md`，冻结 DB2.2 - DB2.14 的 relation governance Postgres rollout 结论。
- Readiness、dual-write、shadow-read、scoped service mode、selected workspace canary 和 multi normal scope canary 均已通过。
- 冻结指标：`MismatchCount=0`、`PostgresFailureCount=0`、`ScopeLeakCount=0`。
- `LearningReadinessFreezeRunner` 增加 `RelationGovernance` capability：
  - `Status=ReadyForLimitedScopeExpansion`
  - `AllowedMode=GuardedPostgresPrimary` only for allowlisted scopes
  - `Forbidden=GlobalDefaultOn`
  - `Required=fallback + comparison trace`
- `learning-runtime-change-readiness-gate` 增加 relation governance 规则：global default-on、missing fallback、missing comparison trace 均必须 fail。
- ControlRoom Admin / Runtime 展示 Relation Governance Freeze 状态。
- 本阶段不做 global default on，不迁移历史业务数据，不关闭 fallback，不关闭 comparison trace，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB3 - Learning Feedback Postgres Provider

- 新增 `PostgresLearningFeedbackStore`，支持 submit / upsert、list / filter、summary、export projection。
- 新增 `PostgresLearningFeedbackReviewStore`，支持 approve / reject / needs-redaction / needs-evidence、list、latest review、summary。
- 新增 `PostgresLearningFeatureCandidateStore`，支持 upsert、capability / label kind filter、jsonl export projection。
- 新增 `ILearningFeatureCandidateStore`、`LearningFeatureCandidateQuery`，并补齐 InMemory / FileSystem feature candidate store。
- Postgres DI 只注册 concrete provider，不替换 `ILearningFeedbackStore` 或 `ILearningFeedbackReviewStore`，默认 FileSystem 仍为 source of truth。
- 新增 CLI：
  - `eval postgres-learning-feedback-diagnostics`
  - `eval postgres-learning-feedback-parity --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-learning-feedback-diagnostics.json`
  - `storage/postgres/postgres-learning-feedback-diagnostics.md`
  - `storage/postgres/postgres-learning-feedback-parity-report.json`
  - `storage/postgres/postgres-learning-feedback-parity-report.md`
- 本地结果：diagnostics `Status=ReadyForParityEval`；parity `Recommendation=ReadyForParityEval`，`Mismatches=0`，`Cleanup=true`。
- ControlRoom Postgres Status 增加 Learning Feedback Provider Status 和 Parity 状态。
- 本阶段不 dual-write、不 shadow-read、不训练、不自动改变 readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB3.1 - Learning Feedback Provider Readiness + Dual-write / Shadow-read Smoke

- 新增 `LearningFeedbackPostgresReadinessGateReport`。
- 新增 `LearningFeedbackDualWriteOptions` / `LearningFeedbackShadowReadOptions`，默认均关闭。
- 新增 `LearningFeedbackDualWriteTrace` / `LearningFeedbackShadowReadTrace`。
- 新增 artifact kind：
  - `TraceLearningFeedbackDualWrite`
  - `TraceLearningFeedbackShadowRead`
- 新增 `LearningFeedbackDualWriteCoordinator`，覆盖 feedback upsert、review write 和 feature candidate upsert；FileSystem 仍为正式写入源，Postgres 只是显式 smoke target。
- 新增 `LearningFeedbackShadowReadCoordinator`，覆盖 feedback list/filter/summary、review list/latest、feature candidate list/export projection；FileSystem 结果仍为正式返回值。
- 新增 CLI：
  - `eval postgres-learning-feedback-readiness-gate`
  - `eval postgres-learning-feedback-dual-write-smoke --cleanup-confirm`
  - `eval postgres-learning-feedback-shadow-read-smoke --cleanup-confirm`
  - `eval postgres-learning-feedback-provider-quality`
- 输出：
  - `storage/postgres/postgres-learning-feedback-readiness-gate.json`
  - `storage/postgres/postgres-learning-feedback-readiness-gate.md`
  - `storage/postgres/postgres-learning-feedback-dual-write-smoke-report.json`
  - `storage/postgres/postgres-learning-feedback-dual-write-smoke-report.md`
  - `storage/postgres/postgres-learning-feedback-shadow-read-smoke-report.json`
  - `storage/postgres/postgres-learning-feedback-shadow-read-smoke-report.md`
  - `storage/postgres/postgres-learning-feedback-provider-quality-report.json`
  - `storage/postgres/postgres-learning-feedback-provider-quality-report.md`
  - `storage/postgres/postgres-learning-feedback-dual-write-traces.jsonl`
  - `storage/postgres/postgres-learning-feedback-shadow-read-traces.jsonl`
- 当前本地结果：
  - readiness gate `GatePassed=true`
  - dual-write smoke `Mismatches=0`，`PostgresFailures=0`
  - shadow-read smoke `Mismatches=0`，`PostgresFailures=0`
  - provider quality `TraceCount=17`，`Recommendation=ReadyForScopedServiceMode`
- ControlRoom Postgres Status 增加 Learning Feedback readiness / smoke / quality 状态，只读展示 diagnostics、gate、dual-write、shadow-read、mismatch/failure/fallback 和 recommendation。
- 本阶段不切换默认 provider、不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB3.2 - Learning Feedback Scoped Service Mode

- 新增 `LearningFeedbackProviderMode`：
  - `FileSystemPrimary`
  - `DualWriteOnly`
  - `ShadowRead`
  - `GuardedPostgresPrimary`
- 新增 `LearningFeedbackProviderSwitchOptions`、`LearningFeedbackScopedRule` 和 `LearningFeedbackProviderSwitchTrace`。
- 新增 artifact kind：
  - `TraceLearningFeedbackProviderSwitch`
- 新增 `LearningFeedbackProviderRouter`，覆盖：
  - feedback submit / upsert
  - feedback query / summary / export projection
  - review upsert / list / latest / summary
  - feature candidate upsert / list / export projection
- `GuardedPostgresPrimary` 只允许显式 scoped rule / allowlist 命中，非 allowlisted scope 继续 FileSystem；provider quality 未 ready 时拒绝进入 scoped mode。
- smoke runner 使用隔离 workspace / collection，并验证：
  - Postgres primary read/write
  - FileSystem fallback result
  - non-allowlisted scope remains FileSystem
  - metadataOnly / redactionMode / trainingUse roundtrip
  - duplicate stable upsert
  - summary / export projection parity
- 新增 CLI：
  - `eval postgres-learning-feedback-scoped-service-mode-smoke --cleanup-confirm`
  - `eval postgres-learning-feedback-scoped-service-mode-gate`
- 输出：
  - `storage/postgres/postgres-learning-feedback-scoped-service-mode-smoke-report.json`
  - `storage/postgres/postgres-learning-feedback-scoped-service-mode-smoke-report.md`
  - `storage/postgres/postgres-learning-feedback-scoped-service-mode-gate.json`
  - `storage/postgres/postgres-learning-feedback-scoped-service-mode-gate.md`
  - `storage/postgres/postgres-learning-feedback-provider-switch-traces.jsonl`
- 当前本地结果：
  - scoped smoke `Recommendation=ReadyForSelectedFeedbackScope`
  - scoped gate `Passed=true`
  - `MismatchCount=0`
  - `PostgresFailureCount=0`
  - `ExportProjectionParityPassed=true`
  - `SummaryParityPassed=true`
- ControlRoom Postgres Status 增加 Learning Feedback Scoped Service Mode，只读展示 mode、allowlist、primary provider、fallback、comparison trace、gate、mismatch/failure/fallback、export/summary parity 和 rollback instruction。
- 本阶段不训练、不调权、不切换全局默认 provider、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB4.3：Job Queue Limited Worker Scope Observation

- 新增 `JobQueueLimitedWorkerScopeObservationOptions`，默认 `Enabled=false`，要求 scoped worker canary 已通过。
- 新增 `JobQueueLimitedWorkerScopeObservationTrace`、`PostgresJobQueueLimitedWorkerScopeObservationReport`、`PostgresJobQueueLimitedWorkerScopeQualityReport`。
- 新增 `TraceJobQueueLimitedWorkerScopeObservation`。
- 新增 CLI：
  - `eval postgres-job-queue-limited-worker-scope-observation --cleanup-confirm`
  - `eval postgres-job-queue-limited-worker-scope-quality`
- 输出：
  - `storage/postgres/postgres-job-queue-limited-worker-scope-observation-report.json`
  - `storage/postgres/postgres-job-queue-limited-worker-scope-observation-report.md`
  - `storage/postgres/postgres-job-queue-limited-worker-scope-quality-report.json`
  - `storage/postgres/postgres-job-queue-limited-worker-scope-quality-report.md`
  - `storage/postgres/postgres-job-queue-limited-worker-scope-traces.jsonl`
- Observation 覆盖：
  - 多个 noop jobs complete
  - fail-once jobs retry 后 complete
  - always-fail jobs 达到 max retry 后 dead-letter
  - long-running job heartbeat 续租
  - expired lease reacquire
  - lease conflict 不重复执行
  - pending job cancel
  - non-selected scope 继续 FileSystem/InMemory
- 当前本地结果：
  - observation `Recommendation=ReadyForJobQueueFreezeGate`
  - quality `Passed=True`
  - `JobCount=11`
  - `CompletedCount=8`
  - `RetriedCount=4`
  - `DeadLetterCount=2`
  - `CancelledCount=1`
  - `LeaseAcquireCount=15`
  - `LeaseConflictCount=1`
  - `LeaseExpiredReacquireCount=1`
  - `HeartbeatCount=2`
  - `DuplicateExecutionCount=0`
  - `LeaseViolationCount=0`
  - `RetryViolationCount=0`
  - `DeadLetterViolationCount=0`
  - `PostgresFailureCount=0`
  - `ScopeLeakCount=0`
  - `NonSelectedScopeRemainsFileSystem=true`
  - `RuntimeWorkerGlobalProviderUnchanged=true`
- 本阶段不切换全局 job queue provider，不接真实生产 worker loop，不影响非 canary scope，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB4.F：Job Queue Postgres Scoped Worker Freeze Gate

- 新增 `docs/job-queue-postgres-freeze.md`。
- 新增 `JobQueuePostgresFreezeGateReport`。
- 新增 CLI：
  - `eval postgres-job-queue-freeze-gate`
- 输出：
  - `storage/postgres/postgres-job-queue-freeze-gate.json`
  - `storage/postgres/postgres-job-queue-freeze-gate.md`
- Freeze gate 条件：
  - job queue diagnostics `ReadyForParityEval`
  - provider quality `ReadyForScopedWorkerCanary`
  - scoped worker canary `ReadyForLimitedWorkerScope`
  - limited worker scope quality passed
  - duplicate / lease / retry / dead-letter violations 均为 0
  - Postgres failure / scope leak 均为 0
  - global worker provider unchanged
  - P15 gate passed
- Freeze 输出状态：
  - `JobQueuePostgres=ReadyForScopedWorkerMode`
  - `DefaultProvider=ExistingProvider`
  - `AllowedMode=GuardedPostgresPrimary only for explicit allowlisted worker scopes`
  - Required: lease / heartbeat / retry / dead-letter quality gates
  - Forbidden: `GlobalWorkerProviderSwitch`
  - Forbidden: `ProductionWorkerLoopSwitchWithoutGate`
- readiness registry 增加 `JobQueuePostgres`：
  - `GatePassed=true`
  - `AllowedRuntimeModes=GuardedPostgresPrimary:ExplicitAllowlistedWorkerScopes:LeaseHeartbeatQualityGate:RetryDeadLetterQualityGate`
  - `ForbiddenRuntimeModes` 包含 global worker provider switch、缺 scoped allowlist、缺 lease quality gate、缺 retry/dead-letter quality gate。
- runtime-change gate 新增 Job Queue 硬规则，当前 `Passed=True`。
- 当前本地结果：
  - freeze gate `Passed=True`
  - `Recommendation=ReadyForScopedWorkerMode`
  - `DuplicateExecutionCount=0`
  - `LeaseViolationCount=0`
  - `RetryViolationCount=0`
  - `DeadLetterViolationCount=0`
  - `PostgresFailureCount=0`
  - `ScopeLeakCount=0`
  - `NonSelectedScopeRemainsFileSystem=true`
  - `RuntimeWorkerGlobalProviderUnchanged=true`
- 本阶段不切换全局 job queue provider，不接真实生产 worker loop，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB4 - Job Queue Postgres Provider

- 新增 Postgres job queue provider diagnostics、parity 和 lease contract smoke。
- schema 升级为 `cc-schema-v5`，`context_jobs` 增加 lease owner、lease expiry、heartbeat、updated_at 字段，并补齐 kind / lease / attempt 索引。
- 扩展 `PostgresContextJobQueue`，显式覆盖 enqueue、get by id、list/filter、lease acquire、heartbeat renew、complete、fail、retry/reschedule、cancel、dead-letter 和 smoke cleanup。
- `ContextJobQuery` 增加 `Kind` 过滤，InMemory / FileSystem / Postgres query 行为保持一致。
- 新增 CLI：
  - `eval postgres-job-queue-diagnostics`
  - `eval postgres-job-queue-parity --cleanup-confirm`
  - `eval postgres-job-queue-lease-smoke --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-job-queue-diagnostics.json`
  - `storage/postgres/postgres-job-queue-diagnostics.md`
  - `storage/postgres/postgres-job-queue-parity-report.json`
  - `storage/postgres/postgres-job-queue-parity-report.md`
  - `storage/postgres/postgres-job-queue-lease-smoke-report.json`
  - `storage/postgres/postgres-job-queue-lease-smoke-report.md`
- ControlRoom Postgres Status 增加 Job Queue Provider Status，只读展示 diagnostics、queue counts、parity、lease smoke、retry/dead-letter transition 和 recommendation。
- 默认 job queue provider 不变，`UseForRuntime=false`；不接 worker loop，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB4.1 - Job Queue Dual-write / Shadow-read Smoke

- 新增 `JobQueueDualWriteOptions` 和 `JobQueueShadowReadOptions`，默认均为 `Enabled=false`。
- 新增 `JobQueueDualWriteCoordinator` 和 `JobQueueShadowReadCoordinator`，仅由 eval smoke 调用，不注册到 runtime worker loop。
- 新增 trace DTO：
  - `JobQueueDualWriteTrace`
  - `JobQueueShadowReadTrace`
- 新增 artifact kind：
  - `TraceJobQueueDualWrite`
  - `TraceJobQueueShadowRead`
- 新增 CLI：
  - `eval postgres-job-queue-dual-write-smoke --cleanup-confirm`
  - `eval postgres-job-queue-shadow-read-smoke --cleanup-confirm`
  - `eval postgres-job-queue-provider-quality`
- 输出：
  - `storage/postgres/postgres-job-queue-dual-write-smoke-report.json`
  - `storage/postgres/postgres-job-queue-shadow-read-smoke-report.json`
  - `storage/postgres/postgres-job-queue-provider-quality-report.json`
  - `storage/postgres/postgres-job-queue-dual-write-traces.jsonl`
  - `storage/postgres/postgres-job-queue-shadow-read-traces.jsonl`
- ControlRoom Postgres Status 增加 Job Queue Dual-write / Shadow-read Status，只读展示 smoke、quality、mismatch/failure/fallback 和 lease/retry/dead-letter/count parity。
- 当前本地结果：
  - dual-write smoke `TraceCount=14`，`MismatchCount=0`，`PostgresFailureCount=0`，`Recommendation=ReadyForScopedWorkerCanary`
  - shadow-read smoke `TraceCount=8`，`MismatchCount=0`，`PostgresFailureCount=0`，`Recommendation=ReadyForScopedWorkerCanary`
  - provider quality `TraceCount=22`，`MismatchCount=0`，`PostgresFailureCount=0`，`Recommendation=ReadyForScopedWorkerCanary`
- 本阶段不切换默认 job queue provider，不接 runtime worker loop，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB4.2：Job Queue Scoped Worker Canary

- 新增 `JobQueueWorkerProviderMode`：
  - `FileSystemPrimary`
  - `GuardedPostgresPrimary`
- 新增 `JobQueueScopedWorkerCanaryOptions`，默认 `Enabled=false`，只允许 selected canary scope 使用 Postgres queue。
- 新增 scoped worker router，仅在 eval canary 中使用：
  - selected canary scope 使用 Postgres primary。
  - non-selected scope 保持 FileSystem/InMemory。
  - provider quality 非 `ReadyForScopedWorkerCanary` 时直接阻断。
  - Postgres failure / mismatch / scope leak 写入报告并阻断 recommendation。
- Canary job 只使用 `ContextJobKind.Custom` 和安全 payload，不运行真实 compression / model / retrieval / vector reindex job。
- 新增 trace / report：
  - `JobQueueScopedWorkerCanaryTrace`
  - `PostgresJobQueueScopedWorkerCanaryReport`
  - `PostgresJobQueueScopedWorkerQualityReport`
  - `TraceJobQueueScopedWorkerCanary`
- 新增 eval：
  - `postgres-job-queue-scoped-worker-canary --cleanup-confirm`
  - `postgres-job-queue-scoped-worker-quality`
- 输出：
  - `storage/postgres/postgres-job-queue-scoped-worker-canary-report.json`
  - `storage/postgres/postgres-job-queue-scoped-worker-canary-report.md`
  - `storage/postgres/postgres-job-queue-scoped-worker-quality-report.json`
  - `storage/postgres/postgres-job-queue-scoped-worker-quality-report.md`
  - `storage/postgres/postgres-job-queue-scoped-worker-traces.jsonl`
- ControlRoom Postgres Status 增加 Job Queue Scoped Worker Canary Summary，只读展示 selected scope、job count、retry/dead-letter、lease/heartbeat、mismatch/failure/scope leak 和 runtime provider unchanged。
- 当前本地结果：
  - scoped worker canary `Recommendation=ReadyForLimitedWorkerScope`
  - scoped worker quality `Passed=True`
  - `JobCount=6`
  - `CompletedCount=4`
  - `RetriedCount=2`
  - `DeadLetterCount=1`
  - `LeaseAcquireCount=8`
  - `LeaseConflictCount=1`
  - `LeaseExpiredReacquireCount=1`
  - `HeartbeatCount=1`
  - `MismatchCount=0`
  - `PostgresFailureCount=0`
  - `ScopeLeakCount=0`
  - `NonSelectedScopeRemainsFileSystem=true`
- 本阶段不切换全局 job queue provider，不接真实 runtime worker loop，不影响非 canary scope，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB3.4：Learning Feedback Limited Scope Observation + Freeze Gate

- 新增 `LearningFeedbackLimitedScopeObservationOptions`，默认 `Enabled=false`，保留 fallback、comparison trace 和 fail-closed mismatch 语义。
- 新增 `LearningFeedbackLimitedScopeObservationReport`、`LearningFeedbackLimitedScopeQualityReport`、`LearningFeedbackPostgresFreezeGateReport`。
- 新增 eval：
  - `postgres-learning-feedback-limited-scope-observation`
  - `postgres-learning-feedback-limited-scope-quality`
  - `postgres-learning-feedback-freeze-gate`
- 输出：
  - `storage/postgres/postgres-learning-feedback-limited-scope-observation-report.json`
  - `storage/postgres/postgres-learning-feedback-limited-scope-quality-report.json`
  - `storage/postgres/postgres-learning-feedback-freeze-gate.json`
  - `storage/postgres/postgres-learning-feedback-limited-scope-traces.jsonl`
- freeze gate 通过时只表示 `LearningFeedbackPostgres=ReadyForScopedServiceMode`，默认 provider 仍为 FileSystem。
- 允许模式：allowlisted scope 下的 `GuardedPostgresPrimary`。
- 必须保留 fallback 与 comparison trace。
- 禁止 global default-on、auto-training、auto-readiness-change。
- 本阶段不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation Phase DB3.3 - Learning Feedback Selected Normal Scope Canary

- 新增 `LearningFeedbackSelectedNormalScopeOptions`，默认 `Enabled=false`，`Mode=GuardedPostgresPrimary`，保留 fallback 和 comparison trace。
- 新增 `LearningFeedbackSelectedNormalScopeCleanupMode`：
  - `None`
  - `CanaryOnly`
  - `ExplicitConfirm`
- 新增 `LearningFeedbackSelectedNormalScopeCanaryReport`。
- 新增 CLI：
  - `eval postgres-learning-feedback-selected-normal-scope-canary`
- 输出：
  - `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-report.json`
  - `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-report.md`
  - `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-traces.jsonl`
- 前置 gate：
  - Postgres storage diagnostics `Ready`
  - learning feedback readiness gate passed
  - dual-write smoke passed
  - shadow-read smoke passed
  - provider quality `ReadyForScopedServiceMode`
  - scoped service mode gate passed
  - selected workspace / collection 显式配置
  - P15 gate passed
- Canary 覆盖：
  - feedback submit / duplicate stable upsert
  - metadataOnly / redactionMode / trainingUse roundtrip
  - approve / reject / needs-redaction / needs-evidence
  - feature candidate build / upsert / list
  - feedback summary / review summary
  - feature candidate export projection
  - non-selected scope FileSystem check
- Canary 数据安全：
  - approved canary review 使用 `trainingUse=smoke_test_only`
  - generated feature candidate 带 `excludedFromTraining=true`
  - trainable dataset gate 继续排除 smoke / excluded candidate
  - `CleanupMode=None` 不删除真实 normal scope 数据；显式 cleanup 只按 canary id 前缀清理
- ControlRoom Postgres Status 增加 Learning Feedback Selected Normal Scope Canary Summary，只读展示 workspace / collection、operation count、mismatch/failure/fallback/scope leak、export/summary/review/candidate parity、recommendation 和 rollback instruction。
- 当前本地结果：
  - `Recommendation=ReadyForLimitedFeedbackScope`
  - `OperationCount=18`
  - `PostgresPrimaryReadCount=7`
  - `PostgresPrimaryWriteCount=10`
  - `ComparisonTraceCount=18`
  - `MismatchCount=0`
  - `PostgresFailureCount=0`
  - `ScopeLeakCount=0`
  - export / summary / review summary / feature candidate parity 均通过
- 本阶段不训练、不调权、不切换全局默认 provider、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB5.0：VectorIndexStore pgvector Provider Foundation

- 新增 `PostgresVectorIndexStore`，覆盖 upsert、get by entry id、delete、list、nearest neighbor query 和 cleanup test entries。
- schema 升级到 `cc-schema-v6`，`vector_index_entries` 补齐 source/provider/model/normalized/metadata_json 字段。
- 新增 provider/model/dimension 兼容性检查，query 必须显式 provider、model、dimension，维度不匹配时阻断，避免混用 embedding space。
- 新增 report DTO：
  - `PostgresVectorDiagnosticsReport`
  - `PostgresVectorCompatibilityReport`
  - `PostgresVectorProviderSmokeReport`
- 新增 eval：
  - `postgres-vector-diagnostics`
  - `postgres-vector-compatibility`
  - `postgres-vector-provider-smoke --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-vector-diagnostics.json`
  - `storage/postgres/postgres-vector-diagnostics.md`
  - `storage/postgres/postgres-vector-compatibility.json`
  - `storage/postgres/postgres-vector-compatibility.md`
  - `storage/postgres/postgres-vector-provider-smoke-report.json`
  - `storage/postgres/postgres-vector-provider-smoke-report.md`
- ControlRoom Postgres Status 增加 Vector Index Provider Status，只读展示 pgvector、schema/table/index、provider compatibility、smoke result 和 runtime disabled 状态。
- 当前本地结果：
  - diagnostics `Recommendation=ReadyForVectorParityEval`
  - `ProviderEnabled=true`
  - `ConnectionAvailable=true`
  - `PgVectorAvailable=true`
  - `SchemaVersion=cc-schema-v6`
  - `TableExists=true`
  - `MissingIndexCount=0`
  - compatibility `Recommendation=ReadyForVectorParityEval`
  - `ExistingCompatibleEntryCount=0`
  - `StaleProviderModelEntriesCount=0`
  - smoke `Recommendation=ReadyForVectorParityEval`
  - `InsertedCount=3`
  - `UpsertedCount=1`
  - `QueryCount=2`
  - `MismatchCount=0`
  - `DimensionMismatchBlocked=true`
  - `ProviderModelMismatchBlocked=true`
  - `CleanupPerformed=true`
- 本阶段不接正式 retrieval，不改变 vector readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB5.1：VectorIndexStore FileSystem/Postgres Parity Eval

- 新增 `PostgresVectorIndexParityRunner` 和 `PostgresVectorIndexParityReport`。
- 新增 CLI：
  - `postgres-vector-parity --cleanup-confirm`
- 输出：
  - `storage/postgres/postgres-vector-parity-report.json`
  - `storage/postgres/postgres-vector-parity-report.md`
- parity 覆盖：
  - deterministic vector entry upsert
  - duplicate upsert semantics
  - get by entry id
  - list by workspace / collection / provider / model / dimension
  - nearest-neighbor topK query
  - similarity score ordering parity
  - delete by entry id
  - metadata / sourceId / sourceKind roundtrip
  - dimension mismatch blocked
  - provider/model mismatch does not silently mix entries
  - cleanup test entries
- ControlRoom Postgres Status 的 Vector Index Provider Status 增加 Vector Parity Summary。
- 当前本地结果：
  - `Recommendation=ReadyForProviderScopedReindex`
  - `OperationCount=14`
  - `FileSystemEntryCount=3`
  - `PostgresEntryCount=3`
  - `InsertedCount=4`
  - `UpsertedCount=1`
  - `DeletedCount=1`
  - `QueryCount=3`
  - `MismatchCount=0`
  - `OrderingMismatchCount=0`
  - `ScoreDeltaMax=0.00000002`
  - `MetadataMismatchCount=0`
  - `DimensionMismatchBlocked=true`
  - `ProviderModelMismatchBlocked=true`
  - `CleanupPerformed=true`
  - `UseForRuntime=false`
- 本阶段不绑定 `IVectorIndexStore`，不接 formal retrieval，不改变 VectorRetrieval readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Foundation - Phase DB5.2：Provider-scoped Reindex into pgvector

本阶段只做 provider-scoped reindex into pgvector，不绑定 `IVectorIndexStore`，不接 formal retrieval，不改变 VectorRetrieval readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增：

- `PostgresVectorProviderScopedReindexPlan`
- `PostgresVectorProviderScopedReindexResult`
- `PostgresVectorProviderScopedReindexReport`
- `PostgresVectorProviderScopedReindexRunner`
- `postgres-vector-provider-scoped-reindex-plan`
- `postgres-vector-provider-scoped-reindex-apply --confirm`
- `postgres-vector-provider-scoped-reindex-quality`

输出：

- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.md`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.md`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.md`

当前本地结果：

- `CandidateCount=158`
- `PlannedInsertCount=0`
- `PlannedUpdateCount=0`
- `PlannedSkipCount=158`
- `AppliedInsertCount=0`
- `AppliedUpdateCount=0`
- `IndexedEntryCountAfterApply=158`
- `MetadataRoundtripMismatchCount=0`
- `Recommendation=ReadyForPgVectorQueryPreview`
- `UseForRuntime=false`

说明：首次 DB5.2 apply 已写入 158 条 provider-scoped entries；最终验证重跑是幂等 no-op，因此最新报告显示 planned skip 158、applied insert/update 0。

边界：

- apply 必须显式 `--confirm`。
- 只写 `PostgresVectorIndexStore`。
- 不写 FileSystem formal vector store。
- 不改变 `VectorIndexService` runtime path。

## Storage Foundation - Phase DB5.3：PgVector Query Preview

本阶段只做 PgVector Query Preview，不绑定 `IVectorIndexStore`，不接 formal retrieval，不改变 VectorRetrieval readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增：

- `PostgresVectorQueryPreviewRunner`
- `PostgresVectorQueryPreviewReport`
- `PostgresVectorQueryPreviewSample`
- `postgres-vector-query-preview`

输出：

- `storage/postgres/postgres-vector-query-preview-report.json`
- `storage/postgres/postgres-vector-query-preview-report.md`

覆盖：

- provider / model / dimension / normalized scope query
- query vector nearest-neighbor topK
- similarity score ordering
- metadata_json roundtrip projection
- lifecycle / eligibility metadata projection
- target section / risk projection
- dimension mismatch blocked
- provider/model mismatch blocked
- empty index clear status
- FileSystem temporary baseline comparison

当前本地结果：

- `Recommendation=ReadyForPgVectorShadowEval`
- `QueryCount=113`
- `CandidateCount=1130`
- `PgVectorCandidateCount=1130`
- `FileSystemCandidateCount=1130`
- `TopKOverlapCount=1130`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `ScoreDeltaMax=0.00000009`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`
- `UseForRuntime=false`

边界确认：

- FileSystem preview baseline 只使用临时目录，不写正式 vector store。
- pgvector preview 只读查询 provider-scoped index。
- target section / risk projection 只进入报告。
- 正式 retrieval / planning / scoring / `PackingPolicy` / package output 未改变。

## Storage Foundation - Phase DB5.4：PgVector Shadow Eval

本阶段只做 PgVector Shadow Eval，不绑定 `IVectorIndexStore`，不接 formal retrieval，不改变 VectorRetrieval readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增：

- `PostgresVectorShadowEvalRunner`
- `PostgresVectorShadowEvalReport`
- `PostgresVectorShadowEvalSummaryReport`
- `postgres-vector-shadow-eval`
- `postgres-vector-shadow-eval-a3`
- `postgres-vector-shadow-eval-extended`

输出：

- `storage/postgres/postgres-vector-shadow-eval-a3.json`
- `storage/postgres/postgres-vector-shadow-eval-a3.md`
- `storage/postgres/postgres-vector-shadow-eval-extended.json`
- `storage/postgres/postgres-vector-shadow-eval-extended.md`
- `storage/postgres/postgres-vector-shadow-eval-summary.json`
- `storage/postgres/postgres-vector-shadow-eval-summary.md`

覆盖：

- pgvector nearest-neighbor retrieval
- candidate eligibility policy
- lifecycle / risk projection
- must-hit / must-not-hit 检查
- recall / MRR
- formal output changed 检查
- FileSystem vector shadow eval 对照

当前本地结果：

- Summary `Recommendation=ReadyForVectorPostgresFreeze`
- A3 `SampleCount=50`，`RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- A3 `TopKOverlapRate=100.00%`，`OrderingMismatchCount=0`，`MetadataMismatchCount=0`，`EligibilityMetadataMismatchCount=0`，`RiskProjectionMismatchCount=0`
- Extended `SampleCount=113`，`RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- Extended `TopKOverlapRate=100.00%`，`OrderingMismatchCount=0`，`MetadataMismatchCount=0`，`EligibilityMetadataMismatchCount=0`，`RiskProjectionMismatchCount=0`
- `UseForRuntime=false`

边界确认：

- pgvector shadow eval 只证明 Postgres vector store 与 FileSystem vector store 的 parity。
- 不启用正式 retrieval。
- 不改变 `PackingPolicy`。
- 不改变 package output。
- 不提升 VectorRetrieval readiness。

## Storage Foundation - Phase DB5.F：Vector Postgres Provider Freeze

本阶段只做 Vector Postgres provider freeze，不绑定正式 `IVectorIndexStore`，不接 formal retrieval，不改变 VectorRetrieval readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增：

- `VectorPostgresProviderFreezeGateReport`
- `VectorPostgresProviderFreezeGateRunner`
- `postgres-vector-freeze-gate`
- readiness registry capability：`VectorPostgresProvider`
- runtime-change gate pgvector 正式检索阻断规则
- `docs/vector-postgres-provider-freeze.md`

输出：

- `storage/postgres/postgres-vector-freeze-gate.json`
- `storage/postgres/postgres-vector-freeze-gate.md`

冻结条件：

- DB5.0 diagnostics `ReadyForVectorParityEval`
- DB5.1 parity `ReadyForProviderScopedReindex`
- DB5.2 provider-scoped reindex quality `ReadyForPgVectorQueryPreview`
- DB5.3 query preview `ReadyForPgVectorShadowEval`
- DB5.4 shadow eval `ReadyForVectorPostgresFreeze`
- A3 / Extended `RecallDelta=0`
- risk 不增加
- `FormalOutputChanged=0`
- projection mismatch `0`
- `UseForRuntime=false`

冻结输出：

- `VectorPostgresProvider=ReadyForPreviewShadowStorage`
- `DefaultVectorStore=unchanged`
- `FormalRetrievalAllowed=false`
- Allowed：preview / shadow / eval only
- Required：正式 retrieval 前必须通过 V4 readiness gate
- Forbidden：未通过 V4 gate 前禁止 formal retrieval switch、正式 `IVectorIndexStore` 绑定、`PackingPolicy` / package output 集成

边界确认：

- pgvector provider freeze 不提升 `VectorRetrieval` readiness。
- `VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。
- ControlRoom 只读展示 freeze 状态。
- runtime-change gate 会阻断 pgvector formal retrieval switch 和正式 store 绑定。

## Vector Foundation - Phase V3.10：Qwen3-Embedding Provider Comparison

本阶段只比较 Qwen3 embedding provider 的 preview / shadow quality，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增：

- `QwenBpeEmbeddingTokenizer`
- `EmbeddingTokenizerFactory`
- Qwen3 provider alias：`--provider qwen3`
- `VectorProviderComparisonV310Report`
- `VectorQwen3ReadinessGateReport`
- `vector-provider-comparison`
- `vector-qwen3-shadow-eval`
- `vector-qwen3-readiness-gate`
- `vector-provider-configuration-sanity-audit`
- `vector-provider-comparison-freeze`

模型目录：

- `src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/`

输出：

- `vector/providers/qwen3/embedding-provider-smoke.json`
- `vector/providers/qwen3/vector-provider-comparison.json`
- `vector/providers/qwen3/vector-qwen3-shadow-eval-a3.json`
- `vector/providers/qwen3/vector-qwen3-shadow-eval-extended.json`
- `vector/providers/qwen3/vector-qwen3-readiness-gate.json`
- `vector/providers/qwen3/vector-provider-configuration-sanity-audit.json`
- `vector/providers/qwen3/vector-provider-comparison-freeze.json`
- `vector/providers/qwen3/postgres-vector-provider-scoped-reindex-quality-report.json`
- `vector/providers/qwen3/postgres-vector-query-preview-report.json`
- `vector/providers/qwen3/postgres-vector-shadow-eval-summary.json`

当前结果：

- Qwen3 provider smoke：`Succeeded=true`
- Qwen3 FileSystem provider scope：`158` entries
- A3 recall：`54.55%`
- A3 MRR：`0.6262`
- A3 risk after policy：`1`
- Extended recall：`76.88%`
- Extended MRR：`0.8058`
- Extended risk after policy：`1`
- pgvector reindex quality：`ReadyForPgVectorQueryPreview`
- pgvector query preview：`BlockedByRiskProjectionMismatch`
- pgvector shadow eval：`BlockedByProjectionMismatch`
- provider configuration sanity audit：`Passed=true`，`ProviderComparison=Conclusive`
- readiness gate：`Passed=false`，`Recommendation=BlockedByRisk`
- provider comparison freeze：`Passed=false`，`PromotionStatus=DoNotPromote`，`VectorV4RecheckAllowed=false`

结论：

- Qwen3 当前不能进入 Vector V4 recheck。
- 本轮已修正 provider alias / provider type 混用风险：`--provider qwen3` 表示 preset alias，实际 provider type 强制为 `OnnxLocal`；`--provider-type` 才表示实现类型。
- 因 sanity audit 已通过，当前 freeze 是质量失败（A3 / Extended recall 低于阈值且 risk 非 0），不是 provider configuration mismatch。
- 若未来 sanity audit 未通过，freeze 必须输出 `ProviderComparison=Inconclusive`、`Recommendation=BlockedByProviderConfigurationMismatch`，不得输出 `DoNotPromote` 或触发 V4 recheck。
- `VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。
- `UseForRuntime=false`，`FormalRetrievalAllowed=false` 保持不变。


## V3.11 Hybrid Retrieval Preview / Lexical + Dense Candidate Union

执行内容：

- 新增 HybridVectorLexicalPreviewOptions（UseForRuntime=false, MaxRiskAllowed=0）。
- 新增 LexicalCandidateProvider / AnchorCandidateProvider（label-free）。
- 新增 HybridCandidateUnionPolicy（dedup by ItemId，保留来源贡献与 eligibility 元数据）。
- 新增 HybridRetrievalPreviewRunner（复用 VectorQueryShadowEvalRunner.BuildSampleResult）。
- 新增 HybridRetrievalReadinessGateRunner（8 条 gate 规则，FormalRetrievalAllowed=false）。
- 新增 CLI：val vector-hybrid-preview / ector-hybrid-shadow-eval / ector-hybrid-readiness-gate。
- ControlRoom Vector Index 页面新增 "Hybrid Retrieval Preview Summary" section。
- 新增测试：label-free、确定性、union 去重、风险策略、formal retrieval 禁用、P15 保持。

不变量：

- FormalRetrievalAllowed 恒 false。
- UseForRuntime 恒 false。
- 候选提供者 / union / 策略 label-free。
- 不修改任何运行时 retrieval / planning / scoring / PackingPolicy / package output 代码。
- 复用 VectorCandidateEligibilityPolicy 作为唯一安全闸门。
- FormalOutputChanged 恒 0。

## V3.11.1 Hybrid Retrieval Recall Regression Sanity Audit

执行内容：

- 新增 HybridRetrievalRecallRegressionAuditRunner（7 profile 对齐诊断）。
- 新增 HybridRetrievalRecallRegressionAuditReport / HybridRecallRegressionAuditProfileResult DTO。
- 新增 CLI：val vector-hybrid-recall-regression-audit。
- ControlRoom Vector Index 页面新增 "Hybrid Recall Regression Audit Summary" section。

诊断目标：定位 hybrid preview recall（~4.55%）远低于 legacy dense baseline（~71.21%）的根因。

gate 规则：hybrid-dense-only recall 必须等于 legacy dense baseline；RiskAfterPolicy=0；FormalOutputChanged=0；UseForRuntime=false。

不变量：

- audit 不接 formal retrieval。
- 不改变 retrieval / planning / scoring / PackingPolicy / package output。
- label-free（不特判 sampleId / itemId / fixture 词表）。

## Vector Foundation - Phase V3.11.F：Hybrid Retrieval Preview Freeze

本阶段只冻结 Hybrid Retrieval preview / shadow 结论，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `HybridRetrievalPreviewFreezeReport`
- `HybridRetrievalPreviewFreezeRunner`
- CLI：`eval vector-hybrid-freeze-gate`
- ControlRoom Vector Index 页面新增 "Hybrid Retrieval Freeze Status"

输出：

- `vector/hybrid/vector-hybrid-freeze-gate.json`
- `vector/hybrid/vector-hybrid-freeze-gate.md`
- `docs/vector-hybrid-retrieval-freeze.md`

当前结果：

- `FreezePassed=true`
- `HybridRetrievalStatus=KeepPreviewOnly`
- `Recommendation=BlockedByA3Recall`
- Legacy dense A3 recall：`4.55%`
- Hybrid dense-only A3 recall：`4.55%`
- Hybrid best A3 recall：`4.55%`
- Legacy dense Extended recall：`3.13%`
- Hybrid dense-only Extended recall：`3.13%`
- Hybrid best Extended recall：`3.13%`
- `DenseCandidateDroppedCount=0`
- `EligibilityMismatchCount=0`
- `DedupOverwriteCount=0`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`
- `V4RecheckAllowed=false`

结论：

- dense-only 与 legacy dense 对齐，说明 hybrid framework 未造成 recall regression。
- lexical / anchor 没有带来 recall 增益。
- 当前 recall 低于 V4 gate，V4 recheck 继续禁止。
- HybridRetrieval 不允许接 formal retrieval，不允许替代正式 retrieval source，不允许影响 `PackingPolicy` 或 package output。

## Vector Foundation - Phase V3.12：Retrieval Dataset / Query-Corpus Alignment Audit

本阶段只做 retrieval dataset / query-corpus alignment audit，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetAlignmentAuditRunner`
- `RetrievalDatasetAlignmentAuditReport`
- `RetrievalDatasetAlignmentAuditSummaryReport`
- CLI：
  - `eval vector-retrieval-dataset-alignment-audit`
  - `eval vector-retrieval-dataset-alignment-audit-a3`
  - `eval vector-retrieval-dataset-alignment-audit-extended`
- ControlRoom Vector Index 页面新增 "Dataset Alignment Audit Summary"

输出：

- `vector/alignment/vector-retrieval-dataset-alignment-audit-a3.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-extended.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.md`

当前结果：

- Summary recommendation：`KeepPreviewOnly`
- `AlignmentIssueCount=50`
- issue breakdown：`MustHitLifecycleFiltered=50`
- A3：`SampleCount=50`，`MustHitCount=66`，`MustHitPresentInCorpusCount=66`，`MustHitPresentInProviderScopeCount=66`，`MustHitBlockedByEligibilityCount=25`，`QueryTokenCoverageAverage=100.00%`，`QueryCorpusTokenOverlapAverage=72.00%`，`AnchorCoverageRate=100.00%`
- Extended：`SampleCount=113`，`MustHitCount=160`，`MustHitPresentInCorpusCount=160`，`MustHitPresentInProviderScopeCount=160`，`MustHitBlockedByEligibilityCount=25`，`QueryTokenCoverageAverage=100.00%`，`QueryCorpusTokenOverlapAverage=79.96%`，`AnchorCoverageRate=100.00%`

结论：

- 当前 mustHit corpus coverage 与 provider scope coverage 都是 `100.00%`。
- query token 与 corpus token 有 overlap，anchor metadata 覆盖完整。
- 低 recall 的主要可见 alignment issue 是 mustHit 被当前 lifecycle / eligibility policy 阻断。
- V3.12 只提供 recall source repair 输入，不提升 VectorRetrieval readiness。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`，`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## Vector Foundation - Phase V3.13：Eligibility Recall Loss Review / Lifecycle-Filtered MustHit Triage

本阶段只做 lifecycle-filtered mustHit triage 和 section-routing review，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `VectorEligibilityRecallLossTriageRunner`
- `VectorEligibilityRecallLossTriageReport`
- `VectorEligibilityRecallLossTriageSummaryReport`
- CLI：
  - `eval vector-eligibility-recall-loss-triage`
  - `eval vector-eligibility-recall-loss-triage-a3`
  - `eval vector-eligibility-recall-loss-triage-extended`
- ControlRoom Vector Index 页面新增 "Eligibility Recall Loss Triage Summary"

输出：

- `vector/eligibility/vector-eligibility-recall-loss-triage-a3.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-extended.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.md`

当前结果：

- Summary recommendation：`NeedsMetadataRepair`
- `TotalFilteredMustHit=50`
- `CorrectlyBlockedCount=18`
- `RouteToAuditCount=18`
- `RouteToHistoricalCount=0`
- `MetadataRepairNeededCount=50`
- `EvalExpectationReviewNeededCount=0`
- `UnsafeToRecoverCount=0`
- `RecoverableWithoutNormalContextCount=18`
- `RecoverableToNormalContextCount=0`
- category breakdown：`CorrectlyBlockedDeprecated=18`，`MetadataLifecycleRepairNeeded=32`
- A3：`TotalFilteredMustHit=25`，`CorrectlyBlockedCount=9`，`RouteToAuditCount=9`，`MetadataRepairNeededCount=25`
- Extended：`TotalFilteredMustHit=25`，`CorrectlyBlockedCount=9`，`RouteToAuditCount=9`，`MetadataRepairNeededCount=25`

结论：

- 18 条 filtered mustHit 是 deprecated 内容，当前阻断正确，只允许进入 audit / diagnostics，不允许进入 `normal_context`。
- 32 条 filtered mustHit 需要 lifecycle / review / replacement metadata repair。
- deprecated / historical / superseded 不允许通过 normal context recovery 提升 recall。
- eval label 只用于 audit，不进入 policy。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`，`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## Vector Foundation - Phase V3.14：Lifecycle Metadata Repair Plan for Vector Recall

本阶段只做 metadata repair plan / preview，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `VectorLifecycleMetadataRepairPlanRunner`
- `VectorLifecycleMetadataRepairPlanReport`
- `VectorLifecycleMetadataRepairPlanSummaryReport`
- CLI：
  - `eval vector-lifecycle-metadata-repair-plan`
  - `eval vector-lifecycle-metadata-repair-plan-a3`
  - `eval vector-lifecycle-metadata-repair-plan-extended`
- ControlRoom Vector Index 页面新增 "Lifecycle Metadata Repair Plan Summary"

输出：

- `vector/eligibility/vector-lifecycle-metadata-repair-plan-a3.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-extended.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.md`

当前结果：

- Summary recommendation：`NeedsHumanReview`
- `CandidateCount=32`
- `AutoRepairableCount=0`
- `HumanReviewRequiredCount=32`
- `ForbiddenRepairCount=0`
- `CorrectlyBlockedSkippedCount=18`
- `EstimatedRecallRecovery=0`
- `RiskAfterRepairEstimate=0`
- A3：`CandidateCount=16`，`AutoRepairableCount=0`，`HumanReviewRequiredCount=16`，`CorrectlyBlockedSkippedCount=9`
- Extended：`CandidateCount=16`，`AutoRepairableCount=0`，`HumanReviewRequiredCount=16`，`CorrectlyBlockedSkippedCount=9`

结论：

- repair plan 只处理 `MetadataLifecycleRepairNeeded`，跳过 correctly blocked deprecated / historical / superseded。
- 当前没有候选满足 auto-repair 条件。
- 所有 32 个 repair candidates 都需要人工复核 provenance / review / replacement evidence。
- 本阶段不写入 metadata，不改变 vector readiness。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`，`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## Vector Foundation - Phase V3.15：Lifecycle Metadata Review Candidate Foundation

本阶段只做人工 review candidate 生成、查询、detail/explain 和 ControlRoom 展示。不自动 repair，不 approve/reject，不写 sidecar metadata，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `VectorLifecycleMetadataReviewCandidate`
- `VectorLifecycleMetadataReviewCandidateGenerationRequest`
- `VectorLifecycleMetadataReviewCandidateGenerationResult`
- `VectorLifecycleMetadataReviewCandidateExplanation`
- `IVectorLifecycleMetadataReviewCandidateStore`
- `InMemoryVectorLifecycleMetadataReviewCandidateStore`
- `FileVectorLifecycleMetadataReviewCandidateStore`
- `VectorLifecycleMetadataReviewCandidateService`
- API：
  - `POST /api/vector/lifecycle-metadata/review-candidates/generate`
  - `GET /api/vector/lifecycle-metadata/review-candidates`
  - `GET /api/vector/lifecycle-metadata/review-candidates/{id}`
  - `GET /api/vector/lifecycle-metadata/review-candidates/{id}/explain`
- CLI：
  - `eval vector-lifecycle-metadata-review-candidates-generate`
  - `eval vector-lifecycle-metadata-review-candidates`
- ControlRoom Vector Index 页面新增 "Lifecycle Metadata Review Candidates"

输出：

- `vector/eligibility/vector-lifecycle-metadata-review-candidates.json`
- `vector/eligibility/vector-lifecycle-metadata-review-candidates.md`

当前预期结果：

- `CandidateCount=32`
- `PendingCount=32`
- `CorrectlyBlockedSkippedCount=18`
- `Recommendation=NeedsHumanReview`

结论：

- review candidates 只从 V3.14 `HumanReviewRequired` repair candidates 生成。
- correctly blocked deprecated / historical / superseded 不生成 normal repair candidate。
- 重复生成保持 deterministic candidate id，并保留已有 status。
- metadata 标记 review-only / no runtime effect / no sidecar write。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`，`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## Vector Foundation V3.16：Lifecycle Metadata Review / Sidecar Apply

本阶段新增 lifecycle metadata review decision、review history、sidecar metadata store 和 smoke/summary/preview CLI。边界保持：不修改业务 source item，不放松 eligibility policy，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-lifecycle-metadata-review-summary`
- `eval vector-lifecycle-metadata-sidecar-preview`
- `eval vector-lifecycle-metadata-review-smoke`

安全规则：只有 `ApproveForSidecar` 写 sidecar；`Reject`、`NeedsEvidence`、`Supersede` 只写 review history。deprecated / historical / superseded 不允许 approve 到 `normal_context`；`normal_context` 只允许 Active / Current / Stable。

## Vector Foundation V3.17：Sidecar-aware Eligibility Re-evaluation Preview

本阶段新增 sidecar-aware eligibility resolver / preview re-evaluation。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不替换正式 eligibility policy，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `VectorLifecycleSidecarResolver`
- `VectorSidecarEligibilityPreviewRunner`
- `VectorLifecycleSidecarResolution`
- `VectorSidecarEligibilityPreviewReport`
- CLI：
  - `eval vector-sidecar-eligibility-preview`
  - `eval vector-sidecar-eligibility-recheck`
  - `eval vector-sidecar-eligibility-quality`
- ControlRoom Vector Index 页面新增 "Sidecar-aware Eligibility Preview Summary"

安全规则：

- source item 始终 unchanged。
- sidecar 只用于 preview/eval effective metadata。
- deprecated / historical / superseded 不允许被提升到 `normal_context`。
- sidecar 缺 evidence/source refs 或存在冲突时 fail closed。
- rejected / needs-evidence / superseded candidate 的 sidecar 不生效。

当前预期：

- 真实 PendingReview=32。
- 真实 ApprovedForSidecar=0。
- 真实 SidecarEntryCount=0。
- `Recommendation=NoApprovedSidecarEntries`。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`。

## Vector Foundation V3.18：Human Review Batch Workflow

本阶段新增 lifecycle metadata human review batch workflow。边界保持：不自动 approve，不自动写真实 sidecar，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `VectorLifecycleMetadataReviewBatch`
- `VectorLifecycleMetadataReviewSheetRow`
- `VectorLifecycleMetadataReviewBatchImportResult`
- `VectorLifecycleMetadataReviewBatchValidationReport`
- `VectorLifecycleMetadataReviewBatchApplyPreviewReport`
- `VectorLifecycleMetadataReviewBatchService`
- CLI：
  - `eval vector-lifecycle-metadata-review-batch-create`
  - `eval vector-lifecycle-metadata-review-batch-export`
  - `eval vector-lifecycle-metadata-review-batch-import`
  - `eval vector-lifecycle-metadata-review-batch-validate`
  - `eval vector-lifecycle-metadata-review-batch-apply-preview`
- ControlRoom Vector Index 页面新增 "Human Review Batch Summary"

输出目录：

- `vector/eligibility/review-batches/{batchId}/batch.json`
- `vector/eligibility/review-batches/{batchId}/review-sheet.jsonl`
- `vector/eligibility/review-batches/{batchId}/review-sheet.md`
- `vector/eligibility/review-batches/{batchId}/import-result.json`
- `vector/eligibility/review-batches/{batchId}/validation-report.json/.md`
- `vector/eligibility/review-batches/{batchId}/apply-preview.json/.md`

安全规则：

- ApproveForSidecar 必须包含 reviewer、reviewer reason、evidenceRefs 或 sourceRefs。
- deprecated / historical / superseded 不允许 approve 到 `normal_context`。
- duplicate candidate decision 不允许 last-write-wins。
- apply preview 不写真实 sidecar，`RealSidecarWritten=false`。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`。

## Vector Foundation V3.18.3：Ingestion Metadata Contract / Legacy Dataset Limitation

本阶段停止继续人工 review 旧 batch，改为输出 Retrieval Dataset V2 metadata contract、validator 和 legacy dataset limitation report。边界保持：不生成正式数据，不写 sidecar，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2MetadataContractReport`
- `RetrievalDatasetV2ValidationReport`
- `RetrievalDatasetLegacyLimitationReport`
- `RetrievalDatasetV2MetadataContractRunner`
- CLI：
  - `eval vector-retrieval-dataset-v2-contract`
  - `eval vector-retrieval-dataset-v2-validator`
  - `eval vector-legacy-dataset-limitation-report`

输出：

- `vector/eligibility/vector-retrieval-dataset-v2-contract.json/.md`
- `vector/eligibility/vector-retrieval-dataset-v2-validation-report.json/.md`
- `vector/eligibility/vector-legacy-dataset-limitation-report.json/.md`

当前结果：

- legacy review candidates：`32`
- 缺 evidence/source/provenance candidates：`32`
- evidence backfill recommendation：`NeedsIngestionMetadataBackfill`
- legacy dataset suitable for primary recall repair：`false`
- validator corpus items：`158`
- validator query samples：`113`
- validator issue count：`1173`
- validator recommendation：`NeedsRelationEvidenceBackfill`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`

结论：旧 vector eval corpus 可继续用于解释 recall loss，但不适合作为主 recall repair 目标。Retrieval Dataset V2 必须在 ingestion 阶段补齐 sourceRefs、evidenceRefs、provenance、split、lifecycle/reviewStatus/replacementState、targetSection 和 relation evidence 后，才能进入下一轮 recall repair / V4 重新评估。

## Vector Foundation V3.19：LLM-generated Retrieval Dataset V2 Generator

本阶段新增 Retrieval Dataset V2 generator、validator 和 quality report。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 `VectorRetrieval` readiness，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2GenerationOptions`
- `RetrievalDatasetV2CorpusItem`
- `RetrievalDatasetV2Sample`
- `RetrievalDatasetV2Generator`
- `RetrievalDatasetV2GenerationReport`
- `RetrievalDatasetV2QualityReport`
- CLI：
  - `eval retrieval-dataset-v2-generate --dry-run`
  - `eval retrieval-dataset-v2-generate --confirm`
  - `eval retrieval-dataset-v2-validate`
  - `eval retrieval-dataset-v2-quality`
- ControlRoom Vector Index 页面新增 "Dataset V2 Generation Summary"

输出：

- `vector/dataset-v2/generated/generation-report.json/.md`
- `vector/dataset-v2/generated/validation-report.json/.md`
- `vector/dataset-v2/generated/quality-report.json/.md`
- `vector/dataset-v2/generated/corpus.jsonl`（仅 `--confirm`）
- `vector/dataset-v2/generated/samples.jsonl`（仅 `--confirm`）

当前 dry-run / quality 结果：

- generated corpus/sample：`28/21`
- validation issues：`0`
- missing evidence/provenance：`0/0`
- mustHit missing：`0`
- mustNot overlap：`0`
- itemId leakage：`0`
- relation inconsistency：`0`
- judge warnings：`0`
- recommendation：`ReadyForDatasetV2ShadowEval`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`

说明：当前 generator 是确定性模板生成基线，用于约束 LLM 输出落盘前后的 contract/validator 行为；不会自动将生成数据接入正式检索。

## Vector Foundation V3.19.1：Dataset V2 Materialization / Immutability Gate

本阶段将 V3.19 generated Dataset V2 从 dry-run 报告推进为显式确认写出的 artifact，并增加 manifest、fingerprint 和不可变性 gate。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 `VectorRetrieval` readiness，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2Manifest`
- `RetrievalDatasetV2MaterializationReport`
- `RetrievalDatasetV2MaterializationRunner`
- CLI：
  - `eval retrieval-dataset-v2-generate --confirm`
  - `eval retrieval-dataset-v2-materialization-gate`
- ControlRoom Vector Index 页面新增 "Dataset V2 Materialization Summary"

输出：

- `vector/dataset-v2/generated/corpus.jsonl`
- `vector/dataset-v2/generated/samples.jsonl`
- `vector/dataset-v2/generated/dataset-v2-manifest.json`
- `vector/dataset-v2/generated/materialization-report.json/.md`
- `vector/dataset-v2/generated/materialization-gate.json/.md`

Gate 条件：

- corpus / samples artifact 存在。
- validation issue count 为 0。
- quality recommendation 为 `ReadyForDatasetV2ShadowEval`。
- corpus/sample hash 与 manifest 稳定一致。
- missing evidence / provenance / itemId leakage / relation inconsistency 均为 0。
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

说明：通过 materialization gate 只表示 generated Dataset V2 artifact 已稳定落盘，可进入后续 shadow eval 准备；不代表 formal retrieval 或 runtime 使用被允许。

## Vector Foundation V3.20：Dataset V2 Dense / Hybrid / Eligibility Shadow Eval

本阶段对 V3.19.1 已物化的 Dataset V2 执行 dense / hybrid / eligibility shadow eval。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2ShadowEvalProfileReport`
- `RetrievalDatasetV2ShadowEvalSummaryReport`
- `RetrievalDatasetV2ReadinessGateReport`
- `RetrievalDatasetV2ShadowEvalRunner`
- CLI：
  - `eval retrieval-dataset-v2-shadow-eval`
  - `eval retrieval-dataset-v2-dense-shadow-eval`
  - `eval retrieval-dataset-v2-hybrid-shadow-eval`
  - `eval retrieval-dataset-v2-readiness-gate`
- ControlRoom Vector Index 页面新增 "Dataset V2 Shadow Eval Summary"

输出：

- `vector/dataset-v2/eval/dataset-v2-dense-shadow-eval.json/.md`
- `vector/dataset-v2/eval/dataset-v2-hybrid-shadow-eval.json/.md`
- `vector/dataset-v2/eval/dataset-v2-shadow-eval-summary.json/.md`
- `vector/dataset-v2/eval/dataset-v2-readiness-gate.json/.md`

当前结果：

- DatasetId：`rdsv2-9d8678d981f1aac1`
- dense best recall：`80.95%`
- hybrid best profile：`hybrid-dense-plus-anchor`
- hybrid best recall：`100.00%`
- MRR：`1.0000`
- risk：`0`
- must-not risk：`0`
- lifecycle risk：`0`
- formal output changed：`0`
- pgvector parity：`true`
- readiness gate：`passed`
- recommendation：`ReadyForDatasetV2RetrievalCandidate`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

说明：pgvector profile 仍是 preview/eval parity projection，不切换正式 vector store；通过 Dataset V2 readiness gate 不等于 formal retrieval allowed。

## Vector Foundation V3.21：Dataset V2 Stress / Holdout / Leakage Audit

本阶段新增 Dataset V2 stress / holdout / leakage audit。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2StressOptions`
- `RetrievalDatasetV2StressReport`
- `RetrievalDatasetV2StressRunner`
- CLI：
  - `eval retrieval-dataset-v2-stress-generate --dry-run`
  - `eval retrieval-dataset-v2-stress-generate --confirm`
  - `eval retrieval-dataset-v2-leakage-audit`
  - `eval retrieval-dataset-v2-anchor-dominance-audit`
  - `eval retrieval-dataset-v2-stress-shadow-eval`
  - `eval retrieval-dataset-v2-stress-readiness-gate`
- ControlRoom Vector Index 页面新增 "Dataset V2 Stress / Leakage Summary"

输出：

- `vector/dataset-v2/stress/corpus.jsonl`
- `vector/dataset-v2/stress/samples.jsonl`
- `vector/dataset-v2/stress/stress-generation-report.json/.md`
- `vector/dataset-v2/stress/leakage-audit.json/.md`
- `vector/dataset-v2/stress/anchor-dominance-audit.json/.md`
- `vector/dataset-v2/stress/stress-shadow-eval.json/.md`
- `vector/dataset-v2/stress/stress-readiness-gate.json/.md`

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- CorpusItemCount：`120`
- SampleCount：`120`
- ValidationIssueCount：`0`
- LeakageIssueCount：`0`
- AnchorDominanceScore：`0.0000`
- DenseRecall：`47.50%`
- LexicalRecall：`47.50%`
- AnchorRecall：`27.50%`
- HybridRecall：`43.33%`
- HoldoutHybridRecall：`62.50%`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- Recommendation：`BlockedByHoldoutRecall`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：V3.21 证明当前 stress dataset 没有 leakage / anchor dominance / risk 问题，但 holdout recall 尚未达到 gate 阈值，因此保持 preview/eval only，不触发 formal retrieval 或 V4 方向变更。

## Vector Foundation V3.22：Dataset V2 Stress Recall Failure Triage

本阶段对 V3.21 stress recall failure 做归因分析。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2StressRecallFailureDetail`
- `RetrievalDatasetV2StressRecallFailureTriageReport`
- `RetrievalDatasetV2StressRecallFailureTriageRunner`
- CLI：
  - `eval retrieval-dataset-v2-stress-failure-triage`
  - `eval retrieval-dataset-v2-stress-failure-triage-holdout`
  - `eval retrieval-dataset-v2-stress-failure-clusters`
- ControlRoom Vector Index 页面新增 "Dataset V2 Stress Failure Triage Summary"

输出：

- `vector/dataset-v2/stress/stress-failure-triage.json/.md`
- `vector/dataset-v2/stress/stress-failure-triage-holdout.json/.md`
- `vector/dataset-v2/stress/stress-failure-clusters.json/.md`

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- FailureCount：`68`
- HoldoutFailureCount：`9`
- FailureCountByReason：
  - `MustHitBelowTopK=59`
  - `HybridUnionRankingRegression=5`
  - `NegativeDistractorOutranksMustHit=3`
  - `AnchorRankingRegression=1`
- DenseOnlyWinCount：`5`
- HybridWinCount：`0`
- AnchorRegressionCount：`1`
- Profile comparison：`dense-only=47.50%`、`hybrid-full=43.33%`、`hybrid-with-anchor-shuffle=47.50%`、`hybrid-with-metadata-anchor-removed=47.50%`、`anchor-only=27.50%`
- MustHitMissingFromCandidateSetCount：`0`
- EligibilityBlockedCount：`0`
- Recommendation：`NeedsHybridUnionScoringRepair`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：V3.22 将 stress recall failure 归因到 topK promotion / hybrid union scoring。当前没有发现 must-hit 缺 corpus、provider scope 缺失或 eligibility block，因此下一步应做 ranking repair preview，而不是放松 policy 或开启 formal retrieval。

## Vector Foundation V3.23：Hybrid Union Scoring / Ranking Repair Preview

本阶段新增离线 hybrid union scoring repair preview。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `HybridUnionScoringRepairOptions`
- `HybridUnionScoringRepairProfileReport`
- `HybridUnionScoringRepairReport`
- `HybridUnionScoringRepairRunner`
- 新增 profile：`post-scoring-risk-gated-v1`
- CLI：
  - `eval retrieval-dataset-v2-hybrid-scoring-repair-preview`
  - `eval retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval`
  - `eval retrieval-dataset-v2-hybrid-scoring-repair-gate`
- ControlRoom Vector Index 页面新增 "Hybrid Scoring Repair Summary"

输出：

- `vector/dataset-v2/stress/hybrid-scoring-repair-preview.json/.md`
- `vector/dataset-v2/stress/hybrid-scoring-repair-shadow-eval.json/.md`
- `vector/dataset-v2/stress/hybrid-scoring-repair-gate.json/.md`

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- baseline hybrid recall / holdout：`43.33%` / `62.50%`
- dense-preserving-union recall / holdout：`47.50%` / `62.50%`
- dense-winner-floor recall / holdout：`47.50%` / `62.50%`
- negative-distractor-penalty recall / holdout：`50.83%` / `75.00%`
- negative-distractor-penalty DenseWinnerLostCount：`0`
- negative-distractor-penalty MustHitBelowTopKCount：`59`
- all repair profiles RiskAfterPolicy：`>0`
- GatePassed：`false`
- Recommendation：`NeedsMoreRankingRepair`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：V3.23 的通用 scoring repair 已证明可提升 recall 和 holdout recall，但 risk gate 未通过，因此不能 freeze 为 retrieval candidate。后续 ranking repair 必须继续保持 label-free：不得使用 mustHit / mustNot label 调排序，不得加入 sampleId / itemId 特判或 fixture/domain 词表。

## Vector Foundation V3.23.1：Hybrid Scoring Risk Regression Triage

本阶段对 V3.23 best improving profile `negative-distractor-penalty-v1` 的 risk regression 做只读 triage。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `HybridScoringRiskRegressionDetail`
- `HybridScoringRiskRegressionTriageReport`
- `HybridScoringRiskRegressionTriageRunner`
- CLI：
  - `eval retrieval-dataset-v2-hybrid-scoring-risk-triage`
  - `eval retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout`
- ControlRoom Vector Index 页面新增 "Hybrid Scoring Risk Triage Summary"

输出：

- `vector/dataset-v2/stress/hybrid-scoring-risk-triage.json/.md`
- `vector/dataset-v2/stress/hybrid-scoring-risk-triage-holdout.json/.md`

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- ProfileName：`negative-distractor-penalty-v1`
- RiskCandidateCount：`7`
- RiskByType：`MustNotHitRisk=7`
- RiskBySplit：`dev=6`、`test=1`
- RiskByDifficulty：`ambiguous_target_section=3`、`near_duplicate_distractor=3`、`query_with_sparse_tokens=1`
- BlockedCandidateReintroducedCount：`0`
- EligibilityBypassCount：`0`
- LifecycleRiskPromotedCount：`0`
- RiskProjectionMismatchCount：`0`
- RepairableByPostScoringRiskGateCount：`7`
- Holdout RiskCandidateCount：`0`
- Recommendation：`NeedsPostScoringRiskGate`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：V3.23.1 证明当前 risk regression 不是 blocked candidate reintroduced、eligibility bypass、lifecycle risk promoted 或 risk projection mismatch。风险全部来自 final topK 中 must-not candidate，下一步应做 post-scoring risk gate preview，且继续禁止 formal retrieval。

## Vector Foundation V3.23.1 Fix：Post-scoring Risk Gate Preview

本修复只作用于 Dataset V2 stress/eval preview。新增 `post-scoring-risk-gated-v1`，复用 `negative-distractor-penalty-v1` 的通用打分结果，并在最终 topK 前执行 eval-only risk projection gate。该 gate 不进入正式 retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

修复后结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- BestProfileName：`post-scoring-risk-gated-v1`
- RecallAfterPolicy：`50.83%`
- HoldoutRecallAfterPolicy：`75.00%`
- DenseWinnerLostCount：`0`
- MustHitBelowTopKCount：`59`
- NegativeDistractorOutranksMustHitCount：`0`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- GatePassed：`true`
- Recommendation：`ReadyForDatasetV2StressFreeze`
- risk triage：`RiskCandidateCount=0`，`Recommendation=ReadyForSafeScoringRepair`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：V3.23.1 发现的 must-not risk regression 已由 post-scoring risk gate 在 preview/eval 层消除。该结果允许继续进入 Dataset V2 stress freeze 评估，但不允许直接接 formal retrieval；正式检索仍需后续 V4 gate。

## Vector Foundation V3.24：Dataset V2 Stress Freeze

本阶段新增 Dataset V2 stress freeze gate。边界保持：不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` 或 package output。

新增：

- `RetrievalDatasetV2StressFreezeReport`
- `RetrievalDatasetV2StressFreezeRunner`
- CLI：
  - `eval retrieval-dataset-v2-stress-freeze-gate`
- ControlRoom Vector Index 页面新增 "Dataset V2 Stress Freeze Status"
- learning runtime-change gate 新增硬规则：
  - Dataset V2 Stress freeze 通过不等于 formal retrieval allowed
  - `post-scoring-risk-gated-v1` 不允许直接接 runtime
  - 未通过 V4 formal readiness gate 不允许改变 `PackingPolicy` / package output

输出：

- `vector/dataset-v2/stress/stress-freeze-gate.json`
- `vector/dataset-v2/stress/stress-freeze-gate.md`

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- DatasetV2Stress：`ReadyForV4RecheckInput`
- BestPreviewProfile：`post-scoring-risk-gated-v1`
- StressRecall：`50.83%`
- HoldoutRecall：`75.00%`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- LeakageIssueCount：`0`
- AnchorDominanceScore：`0`
- V4RecheckAllowed：`true`，仅作为 evaluation input
- ReadyForFormalRetrieval：`false`
- FormalRetrievalAllowed：`false`
- Recommendation：`ReadyForV4RecheckInput`

结论：Dataset V2 stress artifact 已冻结为可进入 V4 复核的输入，但 formal retrieval 仍然禁用。后续若要接正式检索，必须另行通过 V4 formal readiness gate；不得以本 freeze gate 直接启用 runtime、正式 store 绑定、`PackingPolicy` 或 package output integration。

## Vector Foundation - Phase V4.R：Vector Formal Retrieval Readiness Re-evaluation

本阶段新增：

- `VectorV4ReadinessRecheckReport`
- `VectorV4ReadinessRecheckRunner`
- CLI：`eval vector-v4-readiness-recheck`
- ControlRoom Vector Index：V4 Readiness Recheck Summary

输入汇总：

- legacy vector readiness / legacy dataset limitation
- pgvector provider freeze gate
- qwen3 provider comparison freeze
- hybrid retrieval freeze
- Dataset V2 materialization gate
- Dataset V2 small shadow readiness gate
- Dataset V2 stress freeze gate
- hybrid scoring repair gate
- hybrid scoring risk triage
- learning runtime-change readiness gate

输出：

- `vector/v4/vector-v4-readiness-recheck.json`
- `vector/v4/vector-v4-readiness-recheck.md`

当前结果：

- RecheckPassed：`true`
- Recommendation：`ReadyForGuardedFormalPreview`
- LegacyVectorStatus：`PreviewOnly / legacy limitations recorded`
- DatasetV2SmallStatus：`ReadyForDatasetV2RetrievalCandidate`
- DatasetV2StressStatus：`ReadyForV4RecheckInput`
- PgVectorProviderStatus：`ReadyForPreviewShadowStorage`
- HybridScoringRepairStatus：`ReadyForDatasetV2StressFreeze`
- RuntimeChangeGateStatus：`Passed`
- BestPreviewProfile：`post-scoring-risk-gated-v1`
- StressRecall：`50.83%`
- HoldoutRecall：`75.00%`
- RiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- FormalRetrievalAllowed：`false`
- ReadyForGuardedFormalPreview：`true`
- ReadyForRuntimeSwitch：`false`

边界：

- V4.R 通过不等于 runtime switch。
- 本阶段只允许进入 GuardedFormalPreview。
- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Vector Foundation - Phase V4.1：Guarded Formal Retrieval Preview

本阶段新增：

- `GuardedFormalRetrievalPreviewOptions`
- `GuardedFormalRetrievalPreviewReport`
- `GuardedFormalRetrievalPreviewRunner`
- CLI：`eval vector-guarded-formal-retrieval-preview`
- CLI：`eval vector-guarded-formal-retrieval-preview-gate`
- ControlRoom Vector Index：Guarded Formal Retrieval Preview Summary

输出：

- `vector/v4/vector-guarded-formal-retrieval-preview.json`
- `vector/v4/vector-guarded-formal-retrieval-preview.md`
- `vector/v4/vector-guarded-formal-retrieval-preview-gate.json`
- `vector/v4/vector-guarded-formal-retrieval-preview-gate.md`

当前结果：

- PreviewPassed：`true`
- GatePassed：`true`
- Recommendation：`ReadyForShadowPackageComparison`
- ProfileName：`post-scoring-risk-gated-v1`
- SampleCount：`120`
- QueryCount：`120`
- BaselineCandidateCount：`600`
- PreviewVectorCandidateCount：`600`
- WouldAddCount：`57`
- WouldRemoveCount：`57`
- WouldRerankCount：`0`
- WouldChangeTargetSectionCount：`0`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- PackingPolicyChanged：`false`
- PackageOutputChanged：`false`
- FormalRetrievalAllowed：`false`
- ReadyForRuntimeSwitch：`false`

边界：

- V4.1 通过只允许进入 Shadow Package Comparison。
- 不切换 runtime。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Vector Foundation - Phase V4.3：Scoped Formal Preview Opt-in

本阶段新增：

- `ScopedFormalPreviewOptInOptions`
- `ScopedFormalPreviewOptInReport`
- `ScopedFormalPreviewOptInRunner`
- `ScopedFormalPreviewOptInPolicy`
- CLI：`eval vector-scoped-formal-preview-optin-plan`
- CLI：`eval vector-scoped-formal-preview-optin-smoke`
- CLI：`eval vector-scoped-formal-preview-optin-gate`
- ControlRoom Vector Index：Scoped Formal Preview Opt-in Summary

输出：

- `vector/v4/vector-scoped-formal-preview-optin-plan.json`
- `vector/v4/vector-scoped-formal-preview-optin-plan.md`
- `vector/v4/vector-scoped-formal-preview-optin-smoke.json`
- `vector/v4/vector-scoped-formal-preview-optin-smoke.md`
- `vector/v4/vector-scoped-formal-preview-optin-gate.json`
- `vector/v4/vector-scoped-formal-preview-optin-gate.md`

当前结果：

- GatePassed：`true`
- Recommendation：`ReadyForLimitedFormalPreviewObservation`
- Mode：`PreviewOnly`
- ProfileName：`post-scoring-risk-gated-v1`
- ScopeCount：`2`
- AllowlistedScopeCount：`1`
- NonAllowlistedScopeChecked：`true`
- NonAllowlistedScopeLeakCount：`0`
- PreviewPackageCount：`120`
- BaselinePackageCount：`120`
- CandidateAddCount：`57`
- CandidateRemoveCount：`57`
- TokenDeltaTotal：`55`
- TokenDeltaMax：`10`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- PackageOutputChanged：`false`
- PackingPolicyChanged：`false`
- FormalPackageWritten：`false`
- RuntimeMutated：`false`
- FormalRetrievalAllowed：`false`
- ReadyForRuntimeSwitch：`false`

边界：V4.3 通过只允许进入 limited formal preview observation，不允许 runtime switch、正式 retrieval、正式 package 写入或 `PackingPolicy` / package output integration。

## Vector Foundation - Phase V4.2：Shadow Package Comparison

本阶段新增：

- `VectorShadowPackageComparisonOptions`
- `VectorShadowPackageComparisonReport`
- `VectorShadowPackageComparisonRunner`
- CLI：`eval vector-shadow-package-comparison`
- CLI：`eval vector-shadow-package-comparison-gate`
- ControlRoom Vector Index：Shadow Package Comparison Summary

输出：

- `vector/v4/vector-shadow-package-comparison.json`
- `vector/v4/vector-shadow-package-comparison.md`
- `vector/v4/vector-shadow-package-comparison-gate.json`
- `vector/v4/vector-shadow-package-comparison-gate.md`

当前结果：

- GatePassed：`true`
- Recommendation：`ReadyForScopedFormalPreviewOptIn`
- ProfileName：`post-scoring-risk-gated-v1`
- SampleCount：`120`
- QueryCount：`120`
- CandidateAddCount：`57`
- CandidateRemoveCount：`57`
- CandidateUnchangedCount：`543`
- SectionChangedCount：`0`
- TokenDeltaTotal：`55`
- TokenDeltaMax：`10`
- ConstraintCoverageDelta：`0.0167`
- RelationCoverageDelta：`0.0569`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- PackageOutputChanged：`false`
- PackingPolicyChanged：`false`
- ShadowPackageWritten：`false`
- RuntimeMutated：`false`
- FormalRetrievalAllowed：`false`
- ReadyForRuntimeSwitch：`false`

边界：

- V4.2 通过只允许进入 scoped formal preview opt-in 评估。
- 不切换 runtime。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Vector Foundation - Phase V4.4：Limited Formal Preview Observation

本阶段新增：

- `LimitedFormalPreviewObservationOptions`
- `LimitedFormalPreviewObservationReport`
- `LimitedFormalPreviewObservationRunner`
- CLI：`eval vector-limited-formal-preview-observation`
- CLI：`eval vector-limited-formal-preview-observation-gate`
- ControlRoom Vector Index：Limited Formal Preview Observation Summary

输出：

- `vector/v4/vector-limited-formal-preview-observation.json`
- `vector/v4/vector-limited-formal-preview-observation.md`
- `vector/v4/vector-limited-formal-preview-observation-gate.json`
- `vector/v4/vector-limited-formal-preview-observation-gate.md`

Gate 条件：

- V4.3 scoped formal preview opt-in gate passed。
- ObservationRunCount 达到配置值。
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy 均为 `0`。
- FormalOutputChanged 为 `0`。
- PackageOutputChanged / PackingPolicyChanged 均为 `false`。
- FormalPackageWritten / RuntimeMutated 均为 `false`。
- NonAllowlistedScopeLeakCount 为 `0`。
- UseForRuntime / FormalRetrievalAllowed / ReadyForRuntimeSwitch 均为 `false`。

边界：V4.4 仍为 preview / eval only。通过后只允许进入 Formal Preview Freeze，不允许 runtime switch、正式 retrieval、正式 vector store 绑定、正式 package 写入或 `PackingPolicy` / package output integration。

## Vector Foundation - Phase V4.F：Formal Preview Freeze

本阶段新增：

- `VectorFormalPreviewFreezeReport`
- `VectorFormalPreviewFreezeRunner`
- CLI：`eval vector-formal-preview-freeze-gate`
- ControlRoom Vector Index：Formal Preview Freeze Status

输出：

- `vector/v4/vector-formal-preview-freeze-gate.json`
- `vector/v4/vector-formal-preview-freeze-gate.md`

Freeze gate 汇总：

- `vector-v4-readiness-recheck`
- `vector-guarded-formal-retrieval-preview-gate`
- `vector-shadow-package-comparison-gate`
- `vector-scoped-formal-preview-optin-gate`
- `vector-limited-formal-preview-observation-gate`
- `learning-runtime-change-readiness-gate`

Freeze 输出状态：

- VectorFormalPreview：`ReadyForScopedOptInPreview`
- AllowedMode：`ScopedPreviewOnly`
- FormalRetrievalAllowed：`false`
- ReadyForRuntimeSwitch：`false`
- UseForRuntime：`false`
- RuntimeSwitchAllowed：`false`

Readiness registry / runtime-change gate 更新：

- 增加 `VectorFormalPreviewFreeze` capability。
- V4.F 通过后仍不得 runtime switch。
- runtime switch attempt、formal package write attempt、`PackingPolicy` / package output integration 必须 fail。

边界：V4.F 只冻结 scoped preview opt-in preview 的受控可用状态。不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## ContextCore Foundation Freeze / Release Candidate Report

本阶段新增：

- `ContextCoreFoundationFreezeReport`
- `ContextCoreFoundationFreezeRunner`
- CLI：`eval foundation-freeze-report`
- CLI：`eval foundation-release-candidate-gate`
- ControlRoom：Foundation Freeze Summary
- 文档：`docs/ContextCore_Foundation_Freeze_Report.md`

汇总范围：

- storage provider readiness
- vector provider readiness
- vector formal preview readiness
- runtime-change gate status
- P15 gate status
- ControlRoom coverage
- docs coverage
- generated report coverage

输出：

- `foundation/foundation-freeze-report.json`
- `foundation/foundation-freeze-report.md`
- `foundation/foundation-release-candidate-gate.json`
- `foundation/foundation-release-candidate-gate.md`

Freeze 输出状态：

- ContextCoreFoundation：`Frozen`
- StorageFoundation：`Frozen`
- VectorFoundation：`ReadyForScopedFormalPreview`
- RuntimeSwitchAllowed：`false`
- FormalRetrievalAllowed：`false`
- NextAllowedPhase：`ScopedRuntimeExperimentPlanning or NextSubsystemDevelopment`

边界：全局 foundation freeze 通过不等于 runtime enablement。不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## RC0：Foundation Release Candidate Cleanup / Reproducibility Check

本阶段新增：

- `FoundationReproducibilityReport`
- `FoundationReproducibilityRunner`
- CLI：`eval foundation-reproducibility-check`
- `.gitignore` 本地 secrets / DB config / temp traces / large model binaries 规则

检查范围：

- `git status` 分类：source code、tests、docs、generated reports、local config / secrets、model files、temporary files。
- 关键报告存在性：
  - `foundation/foundation-release-candidate-gate.md`
  - `learning/readiness/learning-runtime-change-readiness-gate.md`
  - `vector/v4/vector-formal-preview-freeze-gate.md`
  - `docs/ContextCore_Foundation_Freeze_Report.md`
- 关键边界：
  - `FormalRetrievalAllowed=false`
  - `RuntimeSwitchAllowed=false`
  - `ReadyForRuntimeSwitch=false`
  - `PackingPolicyChanged=false`
  - `PackageOutputChanged=false`
  - applicable `UseForRuntime=false`

输出：

- `foundation/foundation-reproducibility-check.json`
- `foundation/foundation-reproducibility-check.md`

边界：RC0 只做 release candidate cleanup 与可复现性检查。不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC1：Service/API Hardening for Frozen Foundation

本阶段新增：

- `CapabilityStatus`
- `FoundationServiceStatusResponse`
- `FoundationStatusService`
- Client 强类型方法：
  - `GetFoundationStatusAsync`
  - `GetFoundationReleaseCandidateStatusAsync`
  - `GetFoundationReproducibilityStatusAsync`
  - `GetFoundationRuntimeChangeGateStatusAsync`
  - `GetFoundationVectorFormalPreviewStatusAsync`
  - `GetFoundationPostgresFreezeStatusAsync`
- CLI：
  - `eval service-foundation-status-smoke`
  - `eval service-readiness-api-smoke`

新增只读 API：

- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/vector-formal-preview`
- `GET /api/admin/foundation/postgres-freeze-status`

输出：

- `foundation/service-foundation-status-smoke.json`
- `foundation/service-foundation-status-smoke.md`
- `foundation/service-readiness-api-smoke.json`
- `foundation/service-readiness-api-smoke.md`

ControlRoom：

- Service Operational 页面新增统一 Foundation / Runtime Gate / Vector Formal Preview / Storage Freeze Summary。
- Service Mode 下通过 `ContextCoreClient` 读取 API；本地模式下只读报告文件。

边界：SVC1 只做 frozen foundation 的 read-only service/API hardening。不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC2：Read-only API Auth / Report Navigation / Degraded Status Hardening

本阶段新增：

- `FoundationApiResponseEnvelope<T>`
- `FoundationApiSecurityDiagnosticsReport`
- `FoundationReportNavigationEntry`
- `FoundationReportNavigationResponse`
- `ServiceReportNavigationSmokeReport`
- Client 强类型方法：
  - `GetFoundationReportsAsync`
  - `GetFoundationReportAsync`
- CLI：
  - `eval service-api-security-diagnostics`
  - `eval service-report-navigation-smoke`

Foundation read-only API 统一 envelope：

- `Success`
- `CapabilityId`
- `Status`
- `Recommendation`
- `Data`
- `Diagnostics`
- `GeneratedAt`
- `SchemaVersion`

新增只读 API：

- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`

输出：

- `service/service-api-security-diagnostics.json`
- `service/service-api-security-diagnostics.md`
- `service/service-report-navigation-smoke.json`
- `service/service-report-navigation-smoke.md`

ControlRoom：

- Service Operational 页面新增 Service API Hardening Summary。
- 展示 auth configured、secret leak、absolute path leak、report navigation status、degraded report count、recommendation。

安全规则：

- 不在 response / logs / report 中输出 API key。
- 报告导航只暴露安全相对路径。
- 缺失报告返回 degraded status，不抛异常。
- 不返回本地绝对路径、secret 路径或模型二进制路径。

边界：SVC2 只做 read-only service hardening。不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC3：Service OpenAPI / Client Contract Freeze

本阶段新增：

- `FoundationApiEndpointContract`
- `FoundationApiClientMethodContract`
- `FoundationApiContractReport`
- Client 兼容别名：
  - `GetFoundationReleaseCandidateAsync`
  - `GetFoundationReproducibilityAsync`
  - `GetRuntimeChangeGateAsync`
  - `GetVectorFormalPreviewStatusAsync`
  - `GetPostgresFreezeStatusAsync`
- CLI：
  - `eval service-api-contract-report`
  - `eval service-api-contract-freeze-gate`

冻结 endpoint contract：

- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/vector-formal-preview`
- `GET /api/admin/foundation/postgres-freeze-status`
- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`

冻结 envelope schema：

- `Success`
- `CapabilityId`
- `Status`
- `Recommendation`
- `Data`
- `Diagnostics`
- `GeneratedAt`
- `SchemaVersion=foundation-api-envelope-v1`

输出：

- `service/service-api-contract-report.json`
- `service/service-api-contract-report.md`
- `service/service-api-contract-freeze-gate.json`
- `service/service-api-contract-freeze-gate.md`

ControlRoom：

- Service Operational 页面新增 Service API Contract Freeze Summary。
- 展示 endpoint count、client method count、envelope schema version、auth contract status、degraded behavior status 和 freeze recommendation。

边界：SVC3 只做 OpenAPI / client contract freeze。不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC4：Service Auth Enforcement / Deployment Profile

本阶段新增：

- `ServiceDeploymentProfile`
- `FoundationServiceAuthOptions`
- `FoundationServiceAuthDiagnosticsReport`
- `FoundationServiceAuthEnforcementSmokeReport`
- `FoundationServiceDeploymentProfileGateReport`
- CLI：
  - `eval service-auth-diagnostics`
  - `eval service-auth-enforcement-smoke`
  - `eval service-deployment-profile-gate`

Profile 支持：

- `Development`
- `Service`
- `Production`

规则：

- Development profile 允许 no-auth，但 response / report 必须明确 `DevelopmentOnly` / `NotConfigured`。
- Service profile 下 `RequireApiKey=true` 必须配置 API key。
- Production profile 下缺 auth 必须 gate fail。
- wrong API key 必须 unauthorized。
- correct API key 可访问 foundation read-only API。
- API key value 不进入 response / report / log。
- 本地 absolute secret path 不进入 response / report / log。

输出：

- `service/service-auth-diagnostics.json`
- `service/service-auth-diagnostics.md`
- `service/service-auth-enforcement-smoke.json`
- `service/service-auth-enforcement-smoke.md`

## SVC5 - OpenAPI Export / Client SDK Contract Snapshot

本阶段只冻结 read-only foundation API 的 OpenAPI 与 client SDK contract snapshot，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增输出：

- `service/openapi/foundation-api.openapi.json`
- `service/openapi/foundation-api-contract-snapshot.json`
- `service/openapi/foundation-client-contract-snapshot.json`
- `service/openapi/service-openapi-contract-report.json`
- `service/openapi/service-openapi-contract-report.md`
- `service/openapi/service-api-contract-drift-gate.json`
- `service/openapi/service-api-contract-drift-gate.md`

当前结果：

- `EndpointCount=8`
- `ClientMethodCount=13`
- `EnvelopeSchemaVersion=foundation-api-envelope-v1`
- `AuthScheme=ApiKeyAuth`
- `BreakingChangeDetected=false`
- `SecretLeakDetected=false`
- `AbsolutePathLeakDetected=false`
- `Recommendation=ReadyForOpenApiContractFreeze`

Drift gate 阻断 endpoint 删除、envelope schema 破坏、client method 删除、auth scheme 降级、forbidden action 缺失、本地绝对路径泄漏和 secret 泄漏。ControlRoom 已增加 Service OpenAPI / Client Contract Snapshot Summary。

## SVC6 - Hosted Service Deployment Smoke / Read-only API Runtime Check

本阶段只做 hosted service deployment smoke 和 read-only API runtime check，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval service-hosted-deployment-smoke`
- `eval service-readonly-runtime-smoke`
- `eval service-hosted-api-contract-smoke`

新增输出：

- `service/hosted/service-hosted-deployment-smoke.json`
- `service/hosted/service-hosted-deployment-smoke.md`
- `service/hosted/service-readonly-runtime-smoke.json`
- `service/hosted/service-readonly-runtime-smoke.md`
- `service/hosted/service-hosted-api-contract-smoke.json`
- `service/hosted/service-hosted-api-contract-smoke.md`

行为说明：

- 未配置 `--base-url` / `CONTEXTCORE_SERVICE_BASE_URL` 时，报告 `NeedsHostedServiceConfig`，不伪造 hosted 通过结果。
- 配置 hosted `BaseUrl` 后，smoke 覆盖 8 个 foundation read-only endpoint，并校验 auth、wrong-key unauthorized、统一 envelope、secret/path leak 和 runtime 边界。
- ControlRoom 已增加 Hosted Service Smoke Summary。
- `service/service-deployment-profile-gate.json`
- `service/service-deployment-profile-gate.md`

ControlRoom：

- Service Operational 页面新增 Service Auth / Deployment Profile Summary。
- 展示 profile、auth configured、api key configured、development no-auth allowed、production gate status、secret leak status 和 recommendation。

边界：SVC4 只做 service auth enforcement / deployment profile。不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC.F - Service Foundation Freeze

本阶段只做 Service Foundation Freeze，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增报告：

- `ServiceFoundationFreezeReport`

新增命令：

- `eval service-foundation-freeze-gate`

新增输出：

- `service/service-foundation-freeze-gate.json`
- `service/service-foundation-freeze-gate.md`

Gate 汇总：

- SVC1 read-only foundation API smoke
- SVC2 service hardening / report navigation / security diagnostics
- SVC3 API contract freeze
- SVC4 auth deployment profile gate
- SVC5 OpenAPI/client contract drift gate
- SVC6 hosted read-only smoke / runtime smoke / hosted API contract smoke
- foundation release candidate gate
- foundation reproducibility check
- learning runtime-change readiness gate
- P15 gate

当前输出状态：

- `ServiceFoundation=Frozen`
- `FoundationApi=ReadyForHostedReadOnlyService`
- `OpenApiContract=Frozen`
- `AuthDeploymentProfile=Ready`
- `RuntimeMutationAllowed=false`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `Recommendation=ReadyForV45ExplicitScopedRuntimeExperimentPlanning`

ControlRoom 已增加 Service Foundation Freeze Status，展示 SVC1-SVC6 状态、hosted smoke、auth profile、contract drift、runtime mutation status 和 next allowed phase。SVC.F 通过后不继续扩 SVC 小阶段，下一阶段进入 V4.5 Explicit Scoped Runtime Experiment Planning。

## Vector Foundation - Phase V4.5: Explicit Scoped Runtime Experiment Planning

本阶段只做 scoped runtime experiment planning / dry-run / gate，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ExplicitScopedRuntimeExperimentPlanOptions`
- `ExplicitScopedRuntimeExperimentPlanReport`
- `ExplicitScopedRuntimeExperimentPlanRunner`
- `eval vector-scoped-runtime-experiment-plan`
- `eval vector-scoped-runtime-experiment-dry-run`
- `eval vector-scoped-runtime-experiment-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-plan.json/.md`
- `vector/v4/vector-scoped-runtime-experiment-dry-run.json/.md`
- `vector/v4/vector-scoped-runtime-experiment-gate.json/.md`

Gate 汇总 Foundation RC、foundation reproducibility、Service Foundation freeze、Vector Formal Preview freeze、runtime-change gate 和 V4.1-V4.4 gates。计划只允许输出 scope allowlist、preview profile、rollback plan、observation metrics、dry-run package comparison plan 和 forbidden actions。

边界保持：

- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `RuntimeSwitchAllowed=false`
- `WriteFormalPackage=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

## Vector Foundation - Phase V4.6: Explicit Scoped Runtime Experiment Dry-run Observation

本阶段只做 scoped runtime experiment dry-run observation，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ScopedRuntimeExperimentDryRunObservationOptions`
- `ScopedRuntimeExperimentDryRunObservationReport`
- `ScopedRuntimeExperimentDryRunObservationRunner`
- `eval vector-scoped-runtime-experiment-dry-run-observation`
- `eval vector-scoped-runtime-experiment-dry-run-observation-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation.json/.md`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation-gate.json/.md`

Gate 检查：

- V4.5 scoped runtime experiment gate passed
- observation run count 达到配置值
- risk / must-not / lifecycle risk 为 0
- formal output changed 为 0
- formal package written / runtime mutated / vector store binding changed 为 false
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`
- non-allowlisted scope leak 为 0
- rollback plan available
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`

当前阶段仍然只允许 shadow artifact 和 dry-run observation；runtime switch、正式 package 写入、正式 store 绑定、`PackingPolicy` integration 和 package output mutation 继续禁止。

## Vector Foundation - Phase V4.7: Scoped Runtime Experiment Design Freeze

本阶段只做 scoped runtime experiment design freeze，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ScopedRuntimeExperimentDesignFreezeReport`
- `ScopedRuntimeExperimentDesignFreezeRunner`
- `eval vector-scoped-runtime-experiment-design-freeze-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.md`

Freeze gate 汇总：

- foundation-release-candidate-gate passed
- service-foundation-freeze-gate passed
- vector-formal-preview-freeze-gate passed
- vector-scoped-runtime-experiment-gate passed
- vector-scoped-runtime-experiment-dry-run-observation-gate passed
- learning-runtime-change-readiness-gate passed
- P15 gate passed

冻结状态：

- `ScopedRuntimeExperimentDesign=Frozen`
- `AllowedMode=ExplicitScopedRuntimeExperimentOnly`
- `ReadyForRuntimeExperimentProposal=true`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `FormalPackageWriteAllowed=false`
- `PackingPolicyIntegrationAllowed=false`
- `GlobalDefaultOnAllowed=false`

允许动作仅限 selected scope experiment planning、selected scope dry-run observation、selected scope runtime experiment proposal、rollback plan validation 和 metrics collection plan。global runtime switch、non-allowlisted scope use、formal `IVectorIndexStore` binding、formal package write、`PackingPolicy` mutation、package output mutation、禁用 runtime-change gate、未经过后续显式 gate 启用 formal retrieval 均继续禁止。

## Vector Foundation - Phase V4.8: Explicit Scoped Runtime Experiment Proposal

本阶段只做 explicit scoped runtime experiment proposal / config patch preview / approval gate，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ExplicitScopedRuntimeExperimentProposalOptions`
- `ExplicitScopedRuntimeExperimentProposalReport`
- `ExplicitScopedRuntimeExperimentProposalRunner`
- `eval vector-scoped-runtime-experiment-proposal`
- `eval vector-scoped-runtime-experiment-config-preview`
- `eval vector-scoped-runtime-experiment-proposal-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-proposal.json/.md`
- `vector/v4/vector-scoped-runtime-experiment-config-preview.json/.md`
- `vector/v4/vector-scoped-runtime-experiment-proposal-gate.json/.md`

Proposal gate 检查：

- foundation-release-candidate-gate passed
- foundation-reproducibility-check passed
- service-foundation-freeze-gate passed
- vector-formal-preview-freeze-gate passed
- vector-scoped-runtime-experiment-design-freeze-gate passed
- learning-runtime-change-readiness-gate passed
- selected workspace / collection / eval scope 已配置
- rollback plan、kill switch plan、observation metrics 存在
- `ApprovalRequired=true`
- `Approved=false`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`

本阶段 ProposedConfigPatch 只作为 preview 输出，不写入 appsettings，不修改 DI binding，不写正式 package，不改 `PackingPolicy` 或 package output。通过后只能进入人工 experiment approval，不代表 runtime switch allowed。

## Vector Foundation - Phase V4.9: Scoped Runtime Experiment Approval / No-op Harness

本阶段只做 manual approval record 和 no-op runtime harness，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ScopedRuntimeExperimentApprovalRecord`
- `ScopedRuntimeExperimentApprovalOptions`
- `ScopedRuntimeExperimentApprovalService`
- `IScopedRuntimeExperimentApprovalStore`
- `FileSystemScopedRuntimeExperimentApprovalStore`
- `InMemoryScopedRuntimeExperimentApprovalStore`
- `ScopedRuntimeExperimentNoOpHarnessOptions`
- `ScopedRuntimeExperimentNoOpHarnessReport`
- `ScopedRuntimeExperimentNoOpHarnessRunner`

新增 eval：

- `eval vector-scoped-runtime-experiment-approval-preview`
- `eval vector-scoped-runtime-experiment-approve`
- `eval vector-scoped-runtime-experiment-approval-summary`
- `eval vector-scoped-runtime-experiment-noop-harness`
- `eval vector-scoped-runtime-experiment-noop-harness-gate`

输出：

- `vector/v4/runtime-experiment/approval-preview.json/.md`
- `vector/v4/runtime-experiment/approval-record.json/.md`
- `vector/v4/runtime-experiment/approval-summary.json/.md`
- `vector/v4/runtime-experiment/noop-harness-report.json/.md`
- `vector/v4/runtime-experiment/noop-harness-gate.json/.md`

Gate 边界：

- approval mode 只能是 `NoOpHarnessOnly`
- approval record 必须未过期、未撤销
- no-op harness 必须 passed
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `FormalPackageWritten=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`

V4.9 通过后也不代表 runtime switch approval；它只允许进入后续 no-op/dry-run harness freeze 设计。

## Vector Foundation - Phase V4.10: Scoped Runtime Experiment Dry-run Harness Freeze

本阶段只做 no-op harness freeze gate，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ScopedRuntimeExperimentHarnessFreezeReport`
- `ScopedRuntimeExperimentHarnessFreezeRunner`

新增 eval：

- `eval vector-scoped-runtime-experiment-harness-freeze-gate`

输出：

- `vector/v4/runtime-experiment/harness-freeze-gate.json`
- `vector/v4/runtime-experiment/harness-freeze-gate.md`

Freeze 条件：

- V4.8 proposal gate passed
- V4.9 approval summary passed
- V4.9 no-op harness gate passed
- V4.7 design freeze gate passed
- Service Foundation freeze passed
- Foundation RC passed
- runtime-change gate passed
- P15 gate passed
- approval mode 为 `NoOpHarnessOnly`
- approval 未过期、未撤销
- no-op harness passed
- 所有 runtime/formal/package/DI/vector binding/`PackingPolicy`/package output mutation 字段保持 false 或 0

V4.10 通过后状态为 `ReadyForGuardedRuntimeExperimentPlanning`，下一阶段只允许 `GuardedScopedRuntimeExperimentPlan`。`NoOpHarnessOnly` 不代表 runtime approval，runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation、package output mutation 和 global default-on 仍被禁止。

## Vector Foundation - Phase V4.11: Guarded Scoped Runtime Experiment Plan

本阶段只做 guarded scoped runtime experiment plan / activation contract，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `GuardedScopedRuntimeExperimentPlanOptions`
- `GuardedScopedRuntimeExperimentPlanReport`
- `GuardedScopedRuntimeExperimentPlanRunner`

新增 eval：

- `eval vector-guarded-scoped-runtime-experiment-plan`
- `eval vector-guarded-scoped-runtime-experiment-plan-gate`

输出：

- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.json/.md`

Gate 条件：

- Foundation RC passed
- Service Foundation freeze passed
- Vector Formal Preview freeze passed
- V4.7 design freeze passed
- V4.10 harness freeze passed
- runtime-change gate passed
- selected workspace / collection / eval scope 显式配置
- `RequiredApprovalMode=ScopedRuntimeExperiment`
- `NoOpHarnessOnly` approval 不得算作 runtime approval
- kill switch plan、rollback plan、observation plan、stop conditions 存在
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`

V4.11 通过后 recommendation 为 `ReadyForScopedRuntimeExperimentActivationContract`。它只允许进入后续显式 runtime experiment approval/activation contract 阶段，不授权 runtime switch、formal retrieval、formal package write、DI / vector binding mutation、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 或 global default-on。

## Vector Foundation - Phase V4.12: Scoped Runtime Experiment Approval Gate

本阶段只做 `ScopedRuntimeExperiment` approval record / approval gate，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ScopedRuntimeExperimentApprovalGateReport`
- `ScopedRuntimeExperimentApprovalRequestPreviewReport`
- `ScopedRuntimeExperimentRuntimeApprovalRunner`

新增 eval：

- `eval vector-scoped-runtime-experiment-approval-request-preview`
- `eval vector-scoped-runtime-experiment-approve-runtime --proposal-id <id> --approved-by <name> --confirm`
- `eval vector-scoped-runtime-experiment-approval-gate`
- `eval vector-scoped-runtime-experiment-approval-summary`

输出：

- `vector/v4/runtime-experiment/runtime-approval-request-preview.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-record.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-gate.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-summary.json/.md`

Gate 条件：

- V4.11 guarded plan gate passed
- runtime approval record exists
- `ApprovalMode=ScopedRuntimeExperiment`
- approval not expired / not revoked
- risk、rollback、kill switch、scope、observation plan acknowledgements present
- selected scope 与 V4.11 plan 一致
- rollback / kill switch / observation plan present
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`

V4.12 通过后 recommendation 为 `ReadyForActivationPreflight`。该 approval 只允许进入 V4.13 activation preflight，不等于 runtime switch approval；`NoOpHarnessOnly` approval 不能通过 V4.12 gate，runtime-change gate 仍必须阻断 runtime switch / formal retrieval / formal package write。

## Vector Foundation - Phase V4.13: Activation Preflight + Guarded Runtime Dry-run Route

本阶段只做 activation preflight 与 guarded runtime dry-run route，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增：

- `ScopedRuntimeExperimentActivationPreflightOptions`
- `ScopedRuntimeExperimentActivationPreflightReport`
- `ScopedRuntimeExperimentActivationPreflightRunner`

新增 eval：

- `eval vector-scoped-runtime-experiment-activation-preflight`
- `eval vector-scoped-runtime-experiment-dry-run-route`
- `eval vector-scoped-runtime-experiment-activation-gate`

输出：

- `vector/v4/runtime-experiment/activation-preflight.json/.md`
- `vector/v4/runtime-experiment/dry-run-route-report.json/.md`
- `vector/v4/runtime-experiment/activation-gate.json/.md`
- `vector/v4/runtime-experiment/dry-run-route-traces.jsonl`

Gate 条件：

- Foundation RC passed
- Service Foundation freeze passed
- Vector Formal Preview freeze passed
- V4.11 guarded plan gate passed
- V4.12 approval gate passed
- runtime-change gate passed
- `ProposalId` / `ApprovalId` 与前置 artifact 匹配
- `ApprovalMode=ScopedRuntimeExperiment`
- kill switch / rollback plan / trace sink available
- selected scope exists
- non-allowlisted scope leak count = 0
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `FormalPackageWritten=false`
- `PackingPolicyChanged=false`
- `PackageOutputChanged=false`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`

V4.13 预期 recommendation 为 `ReadyForGuardedScopedRuntimeExperiment`。Dry-run route trace 明确标记 `DryRunOnly`；该阶段不授权 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation 或 package output mutation。

## Vector Foundation - Phase V4.14: Guarded Scoped Runtime Experiment

本阶段执行第一个真实 scoped runtime experiment，但仅限 shadow/observation experiment。只有 explicit allowlisted workspace / collection / eval scope 可以命中 experiment route；non-allowlisted scope 必须保持 baseline。正式 retrieval result 不被替换，不写正式 package，不改变正式 package output，不改变 `PackingPolicy`，不修改 DI / 全局 `IVectorIndexStore` binding，不做 global default-on。

新增：

- `GuardedScopedRuntimeExperimentOptions`
- `GuardedScopedRuntimeExperimentReport`
- `ScopedRuntimeExperimentTrace`
- `GuardedScopedRuntimeExperimentRunner`

新增 eval：

- `eval vector-guarded-scoped-runtime-experiment`
- `eval vector-guarded-scoped-runtime-experiment-observation`
- `eval vector-guarded-scoped-runtime-experiment-rollback-smoke`
- `eval vector-guarded-scoped-runtime-experiment-gate`

输出：

- `vector/v4/runtime-experiment/guarded-runtime-experiment-report.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-observation.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-rollback-smoke.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-gate.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-traces.jsonl`

Gate 条件：

- V4.13 activation gate passed
- V4.12 scoped runtime experiment approval gate passed
- `ApprovalMode=ScopedRuntimeExperiment`
- selected scope 显式配置
- `ExperimentRouteHitCount > 0`
- `NonAllowlistedScopeLeakCount=0`
- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `PackageOutputChanged=false`
- `PackingPolicyChanged=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `FormalPackageWritten=false`
- kill switch available
- rollback smoke verified
- learning runtime-change gate remains passing

V4.14 预期 recommendation 为 `ReadyForScopedRuntimeExperimentObservation`。该阶段仍不授权 runtime switch、formal retrieval、formal package write、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 或 global default-on。

## Vector Foundation - Phase V4.15: Scoped Runtime Experiment Observation Window

本阶段扩展 V4.14 的 scoped shadow runtime experiment observation window。仅 explicit allowlisted workspace / collection / eval scope 可命中 experiment route；non-allowlisted scope 必须保持 baseline。正式 retrieval result 不被替换，不写正式 package，不改变正式 package output，不改变 `PackingPolicy`，不修改 DI / 全局 `IVectorIndexStore` binding，不做 global default-on。

新增：

- `ScopedRuntimeExperimentObservationWindowOptions`
- `ScopedRuntimeExperimentObservationWindowReport`
- `ScopedRuntimeExperimentObservationWindowRunner`

新增 eval：

- `eval vector-scoped-runtime-experiment-observation-window`
- `eval vector-scoped-runtime-experiment-observation-window-summary`
- `eval vector-scoped-runtime-experiment-observation-window-gate`

输出：

- `vector/v4/runtime-experiment/observation-window.json/.md`
- `vector/v4/runtime-experiment/observation-window-summary.json/.md`
- `vector/v4/runtime-experiment/observation-window-gate.json/.md`
- `vector/v4/runtime-experiment/observation-window-traces.jsonl`

Gate 条件：

- V4.14 guarded scoped runtime experiment gate passed
- observation run count / request count 达到配置下限
- `ExperimentRouteHitCount > 0`
- `NonAllowlistedScopeLeakCount=0`
- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `PackageOutputChanged=false`
- `PackingPolicyChanged=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`
- `FormalPackageWritten=false`
- kill switch available 且 smoke passed
- rollback verified
- `TraceCompleteness=100`
- error count / latency P95 不超过阈值
- P15 与 learning runtime-change gate remains passing

V4.15 预期 recommendation 为 `ReadyForScopedRuntimeExperimentObservationFreeze`。该阶段仍不授权 runtime switch、formal retrieval、formal package write、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 或 global default-on。

## Vector Foundation - Phase V4.16: Scoped Runtime Experiment Observation Freeze / Promotion Decision

本阶段只冻结 V4.14/V4.15 scoped runtime experiment observation 主线结论，并输出 promotion decision。它不新增旁支能力，不接 formal retrieval，不切 global runtime，不绑定正式 `IVectorIndexStore`，不写 formal package，不改变 `PackingPolicy` 或 package output。

新增：

- `ScopedRuntimeExperimentObservationFreezeReport`
- `ScopedRuntimeExperimentObservationFreezeRunner`

新增 eval：

- `eval vector-scoped-runtime-experiment-observation-freeze`
- `eval vector-scoped-runtime-experiment-promotion-decision`

输出：

- `vector/v4/runtime-experiment/observation-freeze.json/.md`
- `vector/v4/runtime-experiment/promotion-decision.json/.md`

Gate 汇总：

- V4.14 guarded scoped runtime experiment gate passed
- V4.15 observation window gate passed
- P15 与 learning runtime-change gate passed
- risk / formal output / scope leak / trace gap / runtime mutation 均为 0 或 false
- kill switch 与 rollback 均通过

通过时 recommendation 为 `ReadyForFormalRetrievalIntegrationPlan`，但 `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false` 继续保持不变。

## Vector Foundation - Phase V5.0: Formal Retrieval Integration Plan

本阶段只做 formal retrieval integration plan，不实际接入。它不切 runtime，不绑定正式 `IVectorIndexStore`，不写 formal package，不改变 `PackingPolicy` 或 package output。

新增：

- `FormalRetrievalIntegrationPlanReport`
- `FormalRetrievalIntegrationPlanRunner`

新增 eval：

- `eval vector-formal-retrieval-integration-plan`
- `eval vector-formal-retrieval-integration-plan-gate`

输出：

- `vector/v5/formal-retrieval-integration-plan.json/.md`
- `vector/v5/formal-retrieval-integration-plan-gate.json/.md`

Integration plan 覆盖：

- vector retrieval provider
- `IVectorIndexStore` binding
- package builder / context package assembly
- fallback path
- config switch
- trace / comparison output
- rollback / kill switch

Gate 条件：

- V4.16 promotion decision passed
- P15 passed
- learning runtime-change gate passed
- 无 formal output mutation / package output mutation / `PackingPolicy` mutation / DI 或 vector binding mutation

通过时 `AllowedMode=PlanOnly`，`RequiredNextPhase=ShadowFormalRetrievalAdapter`，`FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false` 继续保持不变。

## Vector Foundation - Phase V5.11: Retrieval Evaluation Protocol Freeze / Candidate Source Discriminability Audit

本阶段只冻结 retrieval eval 口径并审计候选来源区分度。它不接 formal retrieval，不改变 selected set、`PackingPolicy`、package output 或 runtime binding。

新增：

- `RetrievalEvalProtocol`
- `RetrievalEvalProtocolAuditRunner`
- `RetrievalEvalProtocolAuditReport`
- `CandidateSourceDiscriminabilityAuditReport`
- `RetrievalEvalProtocolGateReport`

新增 eval：

- `eval vector-retrieval-eval-protocol-audit`
- `eval vector-candidate-source-discriminability-audit`
- `eval vector-retrieval-eval-protocol-gate`

输出：

- `vector/v5/retrieval-eval-protocol-audit.json/.md`
- `vector/v5/candidate-source-discriminability-audit.json/.md`
- `vector/v5/retrieval-eval-protocol-gate.json/.md`

协议冻结：

- `VectorTopK`
- `MergedTopK`
- `FinalTopK`
- score threshold
- deterministic tie-break：score desc、source precedence、candidate id ordinal
- train / holdout split

审计内容：

- V5.7 / V5.10 baseline recall / MRR 同协议复跑。
- hash/order sensitivity 与 tie-break deterministic 检查。
- dense / lexical / anchor / evidence-source / relation / metadata 的 overlap、unique candidate、unique must-hit recovery、marginal recall / MRR。
- split / difficulty 维度的来源边际贡献。
- template homogeneity 与 source non-discriminative 检测。

Gate 条件：

- baseline protocol reproducible。
- tie-break deterministic。
- `HashOrderSensitivityCount=0`。
- 不存在 eval-label scoring / candidate generation。
- risk / must-not risk / lifecycle risk 全部为 0。
- `FormalOutputChanged=0`。
- `FormalPackageWritten=false`、`PackageOutputChanged=false`、`PackingPolicyChanged=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`。
- `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。

当前结果：

- `vector-retrieval-eval-protocol-audit`：`ProtocolPassed=true`，V5.7/V5.10 baseline recall 均为 `47.50%`，`HashOrderSensitivityCount=0`。
- `vector-candidate-source-discriminability-audit`：`ProtocolPassed=true`，`NonDiscriminativeSourceCount=1`，`TemplateHomogeneityScore=0.2083`，risk/must-not/lifecycle risk 均为 `0`。
- `vector-retrieval-eval-protocol-gate`：`GatePassed=true`，`Recommendation=NeedsInputMetadataEnrichment`，runtime/formal/package/`PackingPolicy` invariants 均未改变。

## Vector Foundation - Phase V5.12: Input Metadata Enrichment / Source Discriminability Preview

本阶段只做 metadata enrichment preview。它不接 formal retrieval，不改正式 ingestion/source item，不改 selected set、`PackingPolicy`、package output 或 runtime binding。

新增：

- `InputMetadataEnrichmentPreviewRunner`
- `InputMetadataEnrichmentPreviewReport`
- `InputMetadataCoverageSnapshot`

新增 eval：

- `eval vector-input-metadata-enrichment-preview`
- `eval vector-input-metadata-enrichment-gate`

输出：

- `vector/v5/input-metadata-enrichment-preview.json/.md`
- `vector/v5/input-metadata-enrichment-gate.json/.md`

Enriched projection 只使用 runtime-observable metadata：

- canonical evidence/source refs
- provenance / source fingerprint
- relation type / direction / confidence
- itemKind / sourceKind
- lifecycle / reviewStatus / targetSection
- query-derived anchors

安全约束：

- enrichment projection 不读取 mustHit / mustNot / expectedTargetSection 来生成 metadata。
- 不使用 sampleId / itemId 特判。
- 不使用 fixture/domain 词表。
- 使用 V5.11 固定协议 before/after 复跑 source discriminability。
- 不写正式 corpus / source item。

当前结果：

- `GatePassed=true`
- `Recommendation=ReadyForSourceRepairRecheck`
- `MetadataCoverageDelta=1680`
- `BeforeRecall=47.50%`
- `AfterRecall=47.50%`
- `IndependentNonDenseSourceCount=3`
- risk / mustNot / lifecycle risk 均为 `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants 均未改变

## Vector Foundation - Phase V5.13: Enriched Candidate Source Repair Recheck

本阶段用 V5.12 enriched projection 重跑 query-driven candidate source repair，判断 enriched metadata 是否真正转化为 retrieval quality 提升。本阶段仍只读，不接 formal retrieval，不改 selected set、`PackingPolicy`、package output 或 runtime binding。

新增：

- `EnrichedCandidateSourceRepairRecheckRunner`
- `EnrichedCandidateSourceRepairRecheckReport`

新增 eval：

- `eval vector-enriched-candidate-source-repair-recheck`
- `eval vector-enriched-candidate-source-repair-recheck-gate`

输出：

- `vector/v5/enriched-candidate-source-repair-recheck.json/.md`
- `vector/v5/enriched-candidate-source-repair-recheck-gate.json/.md`

当前结果：

- `RecheckPassed=false`
- `GatePassed=false`
- `Recommendation=BlockedByQualityRegression`
- `QualityImproved=true`
- `MetadataCoverageDelta=1680`
- train derived recall `41.67% -> 45.83%`
- holdout derived recall `50.00% -> 41.67%`
- must-hit below topK `56 -> 52`
- risk / mustNot / lifecycle risk 均为 `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants 均未改变

结论：enriched metadata 已经能影响排序并减少训练集 belowTopK，但 holdout 出现回退，不能作为 source repair freeze 输入。下一步应做 holdout-safe 的 source-aware ranking/scoring repair。

## Vector Foundation - Phase V5.14: Holdout-safe Source-aware Ranking Repair

本阶段实现 holdout-safe source-aware ranking repair。它只使用 V5.12 enriched projection 与 Dataset V2 contract 中 runtime-observable 的 query/source/evidence/relation/provenance metadata；profile 选择仅使用 train/dev，test、holdout、blind-holdout 只用于不退化验证。本阶段不接 formal retrieval，不改 selected set、`PackingPolicy`、package output、runtime binding、vector binding 或 formal package。

新增：

- `SourceAwareRankingRepairRunner`
- `SourceAwareRankingRepairReport`
- locked blind holdout artifacts

新增 eval：

- `eval vector-source-aware-ranking-repair`
- `eval vector-source-aware-ranking-repair-gate`

输出：

- `vector/v5/source-aware-ranking-repair.json/.md`
- `vector/v5/source-aware-ranking-repair-gate.json/.md`
- `vector/v5/source-aware-ranking-blind-holdout-corpus.jsonl`
- `vector/v5/source-aware-ranking-blind-holdout-samples.jsonl`
- `vector/v5/source-aware-ranking-blind-holdout-manifest.json`

当前结果：

- `GatePassed=true`
- `Recommendation=ReadyForSourceAwareRankingFreeze`
- `SelectedProfile=combined-safe`
- train/dev recall delta `+43.59%`
- test recall delta `+88.89%`
- holdout recall delta `+33.33%`
- blind-holdout recall delta `+4.17%`
- `DenseWinnerLostCount=0`
- `UniqueSourceRecoveryCount=58`
- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- formal/package/`PackingPolicy`/runtime/vector binding invariants 均未改变

结论：enriched metadata 已能通过 source-aware ranking 转化为 train/dev 提升，并且 test、holdout、blind-holdout 均未退化。该结果仍是 preview/eval-only，不授权 formal retrieval 或 runtime switch。

## Vector Foundation - Phase V5.15: Output Token Budget / Priority Policy Shadow Gate

本阶段锁定 V5.14 `combined-safe` profile 和 V5.11 `RetrievalEvalProtocol`，只做 package output policy shadow validation。不接 formal retrieval，不改 formal selected set，不写 formal package，不改 `PackingPolicy` / package output / runtime binding / vector binding。

新增：

- `OutputTokenPriorityShadowGateRunner`
- `OutputTokenPriorityShadowGateReport`

新增 eval：

- `eval vector-output-token-priority-shadow`
- `eval vector-output-token-priority-shadow-gate`

输出：

- `vector/v5/output-token-priority-shadow.json/.md`
- `vector/v5/output-token-priority-shadow-gate.json/.md`

当前结果：

- `GatePassed=true`
- `Recommendation=ReadyForOutputPolicyShadowFreeze`
- `ProfileName=combined-safe`
- `SampleCount=144`
- `TokenDeltaTotal=126`
- `TokenDeltaMax=8`
- `TokenDeltaP95=8`
- `PriorityInversionCount=0`
- `DroppedRequiredCandidateCount=0`
- `SectionMismatchCount=0`
- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `FormalSelectedSetChanged=false`
- `FormalPackageWritten=false`
- `PackageOutputChanged=false`
- `PackingPolicyChanged=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`

结论：`combined-safe` 在 shadow package output policy 层通过 token budget、priority ordering、required candidate、section routing 和 risk gate；该结论仍为 shadow/eval-only，不授权 formal retrieval、runtime switch、formal package write 或 package output mutation。

## Formal Adapter Input Contract Enforcement

本阶段固定 future formal adapter 允许读取的 runtime input contract，防止 formal integration 时混入 Dataset/Eval 字段、gold labels、sample metadata 或 shadow artifact 字段。

新增：

- `FormalAdapterRuntimeInputEnvelope`
- `FormalAdapterRuntimePackageContext`
- `FormalAdapterRuntimeCandidateInput`
- `FormalAdapterRuntimeProvenanceInput`
- `FormalAdapterRuntimeRelationEvidenceInput`
- `FormalAdapterInputContractRunner`
- `FormalAdapterInputContractReport`

新增 eval：

- `eval vector-formal-adapter-input-contract`
- `eval vector-formal-adapter-input-contract-gate`

输出：

- `vector/v5/formal-adapter-input-contract.json/.md`
- `vector/v5/formal-adapter-input-contract-gate.json/.md`

合同要求：

- allowed input 仅限 runtime-observable request / scope / query / package context / candidate metadata / lifecycle / routing / provenance / relation evidence / source contribution 字段
- denied input 包括 Dataset V2 sample、must-hit/must-not、expected target section、split/difficulty/rationale、sample metadata、shadow/gate/report artifact
- formal adapter source 若读取 denied 字段，gate 必须阻断

边界保持：不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写 formal package，不改 `PackingPolicy` 或 package output。

## Formal Retrieval Integration Decision Gate

本阶段新增 V5 主线决策 gate，综合 V5.0-V5.16 结果，判断是否允许进入 `FormalRetrievalIntegrationFreeze / AdapterNoOpBindingPlan`。

新增：

- `FormalRetrievalIntegrationDecisionRunner`
- `FormalRetrievalIntegrationDecisionReport`

新增 eval：

- `eval vector-formal-retrieval-integration-decision`
- `eval vector-formal-retrieval-integration-decision-gate`

输出：

- `vector/v5/formal-retrieval-integration-decision.json/.md`
- `vector/v5/formal-retrieval-integration-decision-gate.json/.md`

Gate 汇总：

- V5.0 project state audit
- V5.0 formal retrieval integration plan gate
- V5.1 shadow adapter plan gate
- V5.11 retrieval eval protocol gate
- V5.12 metadata enrichment gate
- V5.13 enriched source repair gate
- V5.14 source-aware ranking gate
- V5.15 output token priority shadow gate
- V5.16 formal adapter input contract gate
- runtime-change gate
- P15 gate

V5.13 `BlockedByQualityRegression` 可由 V5.14 `ReadyForSourceAwareRankingFreeze` 显式 supersede，前提是 V5.13 报告自身 risk / must-not / lifecycle / formal output / package / `PackingPolicy` / runtime / vector binding 不变量全部安全。该 supersede 只说明中间 source repair 回退已由后续主线 ranking repair 修复，不授权 formal retrieval。

通过语义：

- 允许进入 formal retrieval integration freeze 设计
- 允许进入 adapter no-op binding plan
- 不允许 formal retrieval
- 不允许 runtime switch
- 不允许 formal `IVectorIndexStore` runtime binding
- 不允许 formal package write
- 不允许 `PackingPolicy` / package output mutation

## V6.6 Source-diverse Shadow Adapter Delta Validation

本阶段新增 source-diverse shadow adapter validation runner / report，并接入 ControlRoom summary。验证集包含明确 workspace / collection / eval scope metadata，并通过 query/source/evidence/relation anchor 形成区分度；排序与候选生成不读取 must-hit/must-not、sampleId 或 itemId 特判。

新增 eval：

- `eval vector-source-diverse-shadow-adapter-validation`
- `eval vector-source-diverse-shadow-adapter-validation-gate`

输出：

- `vector/v6/source-diverse-shadow-adapter-validation.json/.md`
- `vector/v6/source-diverse-shadow-adapter-validation-gate.json/.md`

Gate 继续要求 `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`UseForRuntime=false`，并要求 applied add/remove 为 0、risk/must-not/lifecycle 为 0、formal package / package output / `PackingPolicy` / runtime / vector binding 全部不变。

## V6.7 Shadow Candidate Merge Preview

V6.7 adds a shadow-only merge preview on top of V6.6 source-diverse validation. It reads V6.6 hypothetical add/remove results and builds a preview merged candidate set from baseline candidates plus shadow adapter candidates. The formal selected set remains unchanged; applied add/remove must stay zero.

Commands:

- `eval vector-shadow-candidate-merge-preview`
- `eval vector-shadow-candidate-merge-preview-gate`

Artifacts:

- `vector/v6/shadow-candidate-merge-preview.json/.md`
- `vector/v6/shadow-candidate-merge-preview-gate.json/.md`

Gate invariants: V6.6 must pass, preview add/remove must be positive, applied add/remove must remain zero, formal selected set and formal output must remain unchanged, formal package must not be written, package output, `PackingPolicy`, runtime, and vector store binding must remain unchanged, risk/must-not/lifecycle counts must be zero, token delta must remain within budget, and there must be no priority inversion or section mismatch.

## V6.7 Shadow Candidate Merge Preview Observation

This follow-up repeats the V6.7 preview merge across multiple observation runs and verifies deterministic preview signatures plus safety invariants. It confirms preview add/remove stability without applying deltas or changing formal output.

Commands:

- `eval vector-shadow-candidate-merge-preview-observation --runs 10`
- `eval vector-shadow-candidate-merge-preview-observation-gate --runs 10`

Artifacts:

- `vector/v6/shadow-candidate-merge-preview-observation.json/.md`
- `vector/v6/shadow-candidate-merge-preview-observation-gate.json/.md`

Gate invariants: V6.7 gate must pass, run count must meet the configured minimum, stable signatures and preview add/remove counts must be deterministic across runs, applied add/remove must stay zero, risk/must-not/lifecycle counts must stay zero, priority inversion and section mismatch must stay zero, token delta must remain within budget, and formal selected set/output/package, `PackingPolicy`, runtime, and vector binding must remain unchanged.

## V6.7 Shadow Merge Stability Freeze / Promotion Decision

本阶段新增 shadow merge stability freeze 与 promotion decision。Gate 汇总 V6.6、V6.7 preview、V6.7 observation 与 runtime-change gate，确认 preview add/remove 在多轮观察下稳定，risk/mustNot/lifecycle 为 0，priority/section 不破坏，token delta 可控，formal output/package/PackingPolicy/runtime/vector binding 均未改变。通过后的推荐状态为 `ReadyForControlledMergeProposal`，但仍保持 `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`。

## V6.8 Controlled Shadow Merge Proposal

本阶段只定义未来如果允许 controlled shadow merge preview 时必须满足的 scope、limit、gate、rollback、kill switch 与 observation 条件。它不执行 merge，不改变 formal selected set，不写 formal package，不改变 `PackingPolicy`、package output、runtime 或 vector binding。

新增 eval：

- `eval vector-controlled-shadow-merge-proposal`
- `eval vector-controlled-shadow-merge-proposal-gate`

输出：

- `vector/v6/controlled-shadow-merge-proposal.json/.md`
- `vector/v6/controlled-shadow-merge-proposal-gate.json/.md`

Gate 汇总 V6.6 source-diverse validation、V6.7 merge preview、V6.7 observation、shadow merge promotion decision 与 runtime-change gate。必须显式配置 workspace / collection / eval scope allowlist，并定义 request/duration/error/add/remove/token 限制、rollback plan、kill switch plan、observation 条件和 stop conditions。

通过后仅允许进入 `ControlledMergePreviewPlan`；`FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、applied add/remove=0、formal/package/`PackingPolicy`/runtime/vector binding 不变量继续保持不变。

## V6.10 Controlled Shadow Merge Dry-run Gate

当前阶段将 V6 controlled shadow merge proposal 中冻结的 scope、limit、rollback、kill switch、observation 条件实际套到 dry-run preview 上验证。该 gate 只读取 proposal、V6.6 source-diverse validation、V6.7 merge preview/observation 和 runtime-change gate artifact。

新增 eval：

- `eval vector-controlled-shadow-merge-dry-run`
- `eval vector-controlled-shadow-merge-dry-run-gate`

验证内容：

- proposal constraints 必须生效。
- preview add/remove 必须在 proposal max add/remove 内。
- token delta、section mismatch、priority inversion、dropped required candidate 必须过 gate。
- rollback plan 与 kill switch plan 必须可用并可验证。
- applied add/remove 必须保持 `0/0`。
- formal selected set、formal output、formal package、PackingPolicy、package output、runtime 和 vector binding 必须不变。

输出：

- `vector/v6/controlled-shadow-merge-dry-run.json/.md`
- `vector/v6/controlled-shadow-merge-dry-run-gate.json/.md`

边界：不接 formal retrieval，不切 runtime，不写 formal package，不应用 preview add/remove，不改变 PackingPolicy 或 package output。
## V6.11 Controlled Shadow Merge Observation Window

当前阶段在 V6.10 dry-run gate 的约束下做多轮 controlled shadow merge observation。它重复执行 dry-run gate，验证 proposal constraints 在观察窗口中持续生效，并确认 preview add/remove 稳定但仍不 applied merge。

新增 eval：

- `eval vector-controlled-shadow-merge-observation-window`
- `eval vector-controlled-shadow-merge-observation-window-gate`

验证内容：

- V6.10 dry-run gate 必须通过。
- proposal constraints 必须在每轮 observation 中生效。
- request count、duration、error count 必须保持在 proposal window 内。
- observation run count 与 sample observation count 必须满足 proposal 下限。
- preview add/remove 必须稳定且大于 0。
- applied add/remove 必须保持 `0/0`。
- risk、must-not risk、lifecycle risk 必须为 0。
- token delta、priority inversion、section mismatch、dropped required candidate 必须过 gate。
- rollback 与 kill switch 必须可用。
- formal selected set、formal output、formal package、PackingPolicy、package output、runtime 和 vector binding 必须不变。

输出：

- `vector/v6/controlled-shadow-merge-observation-window.json/.md`
- `vector/v6/controlled-shadow-merge-observation-window-gate.json/.md`

边界：只做多轮 observation，不接 formal retrieval，不切 runtime，不写 formal package，不应用 preview add/remove，不改变 PackingPolicy 或 package output。

## V6.13 Controlled Shadow Merge Freeze / Promotion Decision

V6.13 freezes the V6.11 controlled shadow merge observation window and records the promotion decision for the next planning phase only. It does not apply preview add/remove, does not change the formal selected set, and does not write or mutate formal package output.

New eval commands:
- `eval vector-controlled-shadow-merge-freeze`
- `eval vector-controlled-shadow-merge-promotion-decision`

Gate checks:
- V6.11 observation window gate passed.
- Proposal constraints, request/duration/error window, and observation window limits remain enforced.
- Deterministic dry-run signature and preview add/remove counts remain stable.
- Applied add/remove remain `0`.
- risk / must-not / lifecycle risk remain `0`.
- formal output changed remains `0`.
- formal package written, package output changed, PackingPolicy changed, runtime mutated, and vector store binding changed remain false.
- runtime-change gate remains passing.

Outputs:
- `vector/v6/controlled-shadow-merge-freeze.json` / `.md`
- `vector/v6/controlled-shadow-merge-promotion-decision.json` / `.md`

If the freeze and promotion decision pass, the next allowed phase is `ControlledAppliedMergeProposal`. This is still planning-only: `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, `ReadyForRuntimeSwitch=false`, and applied merge remains forbidden until an explicit later gate.

## V6.14 Controlled Applied Merge Proposal

V6.14 defines the contract for a future controlled applied merge preview. It does not apply merge deltas and does not change the formal selected set, formal package, PackingPolicy, package output, runtime binding, or vector store binding.

New eval commands:

- `eval vector-controlled-applied-merge-proposal`
- `eval vector-controlled-applied-merge-proposal-gate`

The gate requires the V6.13 controlled shadow merge promotion decision, the runtime-change gate, explicit workspace/collection/eval-scope allowlists, bounded request/duration/error limits, bounded applied add/remove limits, a manual approval plan, rollback plan, kill switch plan, observation metrics, and stop conditions. It also requires applied add/remove to remain `0`, risk/must-not/lifecycle risk to remain `0`, and all formal/package/PackingPolicy/runtime/vector-binding invariants to remain unchanged.

Passing recommendation is `ReadyForControlledAppliedMergeDryRunGate`. This is not permission to apply a merge; it only permits the next dry-run gate to validate the proposed constraints.
