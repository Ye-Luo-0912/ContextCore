# ContextCore 新阶段执行报告

更新时间：2026-06-07

## 当前结论

本轮完成 Graph Foundation - Phase G1。

P15 baseline 保持冻结：A3 50 和 Extended 113 均为 `0 failed / 100.00% pass`。本轮新增 relation type taxonomy、relation graph validation service、relations diagnostics endpoints、ContextCoreClient 方法、ControlRoom Relations 页面增强、测试和文档；只做关系类型定义、关系验证和只读诊断，不改变 retrieval、relation expansion、planning、`PackingPolicy`、attention、vector、LLM judge 或 package 输出。

当前 `RetrievalPlanProposal` 仍只在显式 intent-scoped opt-in 下可影响 final selected。本轮没有扩大 opt-in intent，没有全局启用 planning proposal，没有改变 legacy scoring，没有改变 `PackingPolicy`，没有接 vector，没有接 LLM judge，没有做正式 layered retrieval，没有做 NamedPipe。

## 本轮完成

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
