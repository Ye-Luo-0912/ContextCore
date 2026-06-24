# Vector Index Foundation

更新时间：2026-06-10

## 范围

Phase V1 / V2 / V3 / V3.1 / V3.2 / V3.3 / V3.4 / V3.5 / V3.6 / V3.6.1 / V3.6.2 / V3.6.3 / V3.6.4 / V3.7 / V3.8 / V3.9 只补 vector index 基础设施、可控 reindex pipeline、query preview、eval shadow、覆盖率基线、candidate eligibility policy、profile sweep、embedding quality baseline、真实 provider adapter、本地 provider smoke test、residual risk audit、lifecycle metadata coverage gate、lifecycle sidecar backfill、recall loss audit、intent/profile readiness split、safe recall recovery、vector + ranker fusion shadow baseline、representation benchmark 与 query intent expansion shadow baseline：

- 不接正式 retrieval。
- 不改 retrieval scoring。
- 不改 `PackingPolicy`。
- 不让 vector 进入 package 输出。
- 不让真实 provider 自动进入正式检索。

当前实现与旧 `IVectorStore` 分离，使用独立 `IVectorIndexEntry` / `IVectorIndexStore`，避免影响现有 retrieval 或历史向量存储路径。

## Vector Preview / Shadow Freeze

V3.F 已将 V1 - V3.9 的 vector preview / shadow 结论冻结到：

- `docs/vector-preview-shadow-freeze.md`

冻结结论：

- V4 readiness gate failed。
- 失败原因是 A3 recall、fusion recall、expanded recall 均低于 80%。
- vector 不进入 formal retrieval / scoring / `PackingPolicy` / package output。
- 后续只能作为离线评估、shadow trace 或实验输入继续推进。

## Phase V3.9：Query Intent Expansion Shadow Baseline

V3.9 新增 `VectorQueryExpansionService`、`VectorQueryExpansionShadowRunner` 和 CLI：

- `eval vector-query-expansion-shadow`

输出：

- `eval/vector-query-expansion-shadow-a3.json`
- `eval/vector-query-expansion-shadow-extended.json`
- `eval/vector-query-expansion-shadow.md`

Query expansion profiles：

- `raw-query-v1`
- `mode-intent-query-v1`
- `anchor-query-v1`
- `intent-anchor-query-v1`
- `planning-context-query-v1`
- `constraint-aware-query-v1`

`VectorQueryExpansionService` 只组合运行时信号：mode、intent / routerIntent、task kind、planning snapshot、query anchors、working memory anchors、constraint hints 和经过过滤的 request metadata。它不读取 `mustHit` / `mustNotHit` label，不使用 sampleId / itemId / fixture 文件名特判，不内置领域词表、synonym 或 alias。eval runner 可以使用 label 计算 recall / risk，但该信息不会进入 expansion service 或 eligibility policy。

V3.9 继续使用现有 `VectorQueryPreviewService` 和 `VectorCandidateEligibilityPolicy` 做 shadow 评估，不写 vector index，不改变正式 retrieval、scoring、`PackingPolicy` 或 package 输出。V4 readiness gate 新增 expansion 条件：A3 / Extended expanded recall 均需 `>=80%`，且 expanded risk、mustNotHit risk、lifecycle risk 均为 `0`。

当前本地 ONNX 结果：

- A3：best expansion=`raw-query-v1`，`RecallBefore=71.21%`，`RecallAfter=71.21%`，`RiskAfterPolicy=0`，`Recommendation=NeedsBetterEmbedding`
- Extended：best expansion=`raw-query-v1`，`RecallBefore=84.38%`，`RecallAfter=84.38%`，`RiskAfterPolicy=0`，`Recommendation=ReadyForRetrievalShadow`
- V4 readiness gate 仍失败：`A3RecallAtLeast80Percent`、`A3FusionRecallAtLeast80Percent`、`A3ExpandedRecallAtLeast80Percent`

结论：query expansion baseline 没有引入 mustNotHit / lifecycle risk，但也未解决 A3 recall 缺口；vector 继续保持 preview / shadow-only。

## Phase V3.8：Vector Representation Benchmark & A3 Miss-set Audit

V3.8 新增 `VectorMissSetRepresentationAuditRunner` 和 CLI：

- `eval vector-representation-benchmark`

输出：

- `eval/vector-missset-representation-audit-a3.json`
- `eval/vector-missset-representation-audit-extended.json`
- `eval/vector-missset-representation-audit.md`
- `eval/vector-representation-benchmark-a3.json`
- `eval/vector-representation-benchmark-extended.json`
- `eval/vector-representation-benchmark.md`

Document representation profiles：

- `raw-content-v1`
- `title-content-v1`
- `title-summary-content-v1`
- `anchor-enriched-v1`
- `metadata-enriched-v1`
- `compact-retrieval-text-v1`

Query representation profiles：

- `raw-query-v1`
- `intent-query-v1`
- `anchor-query-v1`
- `mode-intent-query-v1`
- `expanded-anchor-query-v1`

Representation benchmark 使用临时 vector index，对不同 document/query 表示组合重建离线索引并评估 recall / MRR / risk；它不会写入正式 vector index，不接正式 retrieval，不改变 scoring、`PackingPolicy` 或 package 输出。临时 index 会只读继承当前 vector index entry 的运行时 metadata / lifecycle sidecar，避免因 benchmark source 缺少已回填 metadata 而低估安全策略效果；不读取 eval label、sampleId、itemId、fixture 名称或领域词表作为 policy 条件。

当前本地 ONNX 结果：

- A3：best document=`metadata-enriched-v1`，best query=`mode-intent-query-v1`，`Recall=69.70%`，`RiskAfterPolicy=0`，`Recommendation=NeedsBetterEmbedding`
- Extended：best document=`metadata-enriched-v1`，best query=`mode-intent-query-v1`，`Recall=84.38%`，`RiskAfterPolicy=0`，`Recommendation=ReadyForRetrievalShadow`
- V4 readiness gate 仍失败：`A3RecallAtLeast80Percent` 与 `A3FusionRecallAtLeast80Percent`

结论：representation 改写没有引入安全风险，但没有解决 A3 召回缺口；vector 继续保持 preview / shadow-only。

## Phase V3.7：Vector + Ranker Fusion Shadow Baseline

V3.7 新增 `VectorRankerFusionShadowRunner` 和 CLI：

- `eval vector-ranker-fusion-shadow`

输出：

- `eval/vector-ranker-fusion-shadow-a3.json`
- `eval/vector-ranker-fusion-shadow-extended.json`
- `eval/vector-ranker-fusion-shadow.md`

支持的 fusion strategies：

- `VectorOnly`
- `RankerOnly`
- `UnionThenRank`
- `VectorBoostedRanker`
- `RankerFilteredVector`
- `LifecycleAwareFusion`

fusion runner 使用更宽的只读 vector 候选池做 shadow 排序，但最终指标按配置 `topK` 评估。`VectorOnly` 基线保持现有 vector query shadow 语义：先取原始 topK，再应用 eligibility policy；其他 fusion 策略只在报告内比较，不写入 store，不改变正式 retrieval、不改 scoring、不改 `PackingPolicy`，也不让 vector 进入 package 输出。

V3.7 当前 ONNX 本地结果：

- A3：best=`VectorOnly`，`FusionRecall=71.21%`，`FusionMRR=0.6765`，`FusionRisk=0`，`Recommendation=NeedsFusionTuning`
- Extended：best=`VectorOnly`，`FusionRecall=84.38%`，`FusionMRR=0.8229`，`FusionRisk=0`，`Recommendation=ReadyForRetrievalShadow`
- 其他 fusion strategy 可在个别样本追回 mustHit，但会引入 mustNotHit 风险，因此 recommendation=`BlockedByRisk`

V4 readiness gate 已纳入 fusion 条件：

- `A3FusionRecallAtLeast80Percent`
- `ExtendedFusionRecallAtLeast80Percent`
- `FusionRiskAfterPolicyZero`
- `FusionLifecycleRiskZero`
- `FusionNewlyRiskySamplesZero`
- `FusionFormalOutputChangedZero`

当前 gate 失败项为 `A3RecallAtLeast80Percent` 与 `A3FusionRecallAtLeast80Percent`。这意味着 vector 仍保持 preview / shadow-only，不得进入 retrieval shadow 或正式检索路径。

## 数据模型

`VectorIndexEntry` 包含：

- `EntryId`
- `ItemId`
- `ItemKind`
- `Layer`
- `WorkspaceId`
- `CollectionId`
- `ContentHash`
- `EmbeddingModel`
- `EmbeddingProvider`
- `Dimension`
- `Vector`
- `CreatedAt`
- `UpdatedAt`
- `Metadata`

`EntryId` 由 generator 生成并用于 upsert。后端 diagnostics 会检测同一 `workspace + collection + itemId + provider + model` 下的重复 entry，避免重复数据浪费存储空间并制造噪声。

## Embedding Generator

V1 提供两个可重复实现：

- `MockEmbeddingGenerator`
- `DeterministicHashEmbeddingGenerator`

默认 DI 使用 `DeterministicHashEmbeddingGenerator`。该实现基于 SHA-256 生成稳定单位向量，适合本地测试、diagnostics 和 reindex preview。

Phase V3.4 新增 provider adapter 配置：

- `EmbeddingProviderOptions`
- `ProviderType=DeterministicHash`
- `ProviderType=OnnxLocal`
- `ProviderType=Disabled`

`OnnxEmbeddingGenerator` 位于 `ContextCore.Embedding`，用于把本地 ONNX embedding provider 适配到 V1 `IEmbeddingGenerator`。它通过 `IEmbeddingTokenizer` 注入 tokenizer，不在 generator 内写死 tokenizer 实现；默认本地 tokenizer 实现为 `BertWordPieceTokenizer`。

ONNX provider 只在显式配置 `ProviderType=OnnxLocal` 且模型/词表文件存在时执行。默认测试和默认 Service 配置不要求模型文件存在。

Phase V3.5 新增本地 smoke test：

- CLI：`eval embedding-provider-smoke`
- 检查 `ModelPath` / `TokenizerPath` 是否存在。
- 检查 provider 是否 enabled。
- 检查 tokenization、ONNX inference、输出维度、normalization 与 batch embedding。
- 缺失模型或 tokenizer 时输出 diagnostics，不写 index，不接正式 retrieval。

不要提交模型文件：

- ONNX 模型文件、词表、tokenizer 配置都应放在本地专用目录或用户私有配置指向的位置。
- 仓库只保留 adapter、diagnostics、文档和测试，不提交大模型二进制。
- `ModelPath` / `TokenizerPath` 应通过本地配置或 CLI 参数传入。

## Store

新增 `IVectorIndexStore`：

- `UpsertAsync`
- `DeleteAsync`
- `GetByItemIdAsync`
- `ListAsync`
- `SearchAsync`
- `GetDiagnosticsAsync`

实现：

- `InMemoryVectorIndexStore`
- `FileVectorIndexStore`

文件系统实现写入：

- `workspaces/{workspaceId}/collections/{collectionId}/vectors/vector-index.jsonl`

## Diagnostics

`VectorIndexDiagnostics` 支持：

- `MissingEmbedding`
- `StaleEmbedding`
- `ContentHashMismatch`
- `DimensionMismatch`
- `UnsupportedEmbeddingModel`
- `ProviderUnavailable`
- `DuplicateVectorEntry`
- `OrphanVectorEntry`
- `ModelFileMissing`
- `TokenizerUnavailable`
- `EmbeddingModelMismatch`
- `ProviderMismatch`
- `NormalizationMismatch`
- `UnsupportedPoolingStrategy`
- `OnnxSessionFailed`
- `RequiresReindex`
- `EmbeddingProviderChanged`
- `EmbeddingModelChanged`
- `DimensionChanged`

`VectorIndexService` 会只读聚合 source context / memory items 与 vector entries：

- source item 没有 vector entry -> `MissingEmbedding`
- source 内容 hash 与 entry 不一致 -> `StaleEmbedding` / `ContentHashMismatch`
- entry 维度声明与实际 vector 长度不一致 -> `DimensionMismatch`
- entry 模型与当前 generator 不一致 -> `UnsupportedEmbeddingModel`
- entry provider / model / dimension / normalization 与当前 generator 不一致 -> `RequiresReindex` 和对应 changed/mismatch diagnostics
- entry 没有 source item -> `OrphanVectorEntry`
- 同 item/model/provider 多 entry -> `DuplicateVectorEntry`

Phase V3.1 增加 eval-corpus source 对齐：Eval CLI 可以从 `eval/contexts/*/corpus*.json` 构造 `VectorReindexSourceItem`，用于 reindex plan / apply / diagnostics / coverage。该路径不要求先把 eval corpus 写入正式 context / memory store，也不会让 vector 进入正式 retrieval 或 package。source item 按 `ItemId` 去重，同一 item 多次 apply 走稳定 `EntryId` upsert，避免重复数据浪费存储空间或制造诊断噪声。

## API

V1 Service endpoints：

- `GET /api/vector/status`
- `GET /api/vector/diagnostics`
- `POST /api/vector/reindex-preview`
- `POST /api/vector/query-preview`

`reindex-preview` 只返回预期动作：

- `Create`
- `Update`
- `Current`
- `DeleteOrphan`

它不执行 upsert/delete，不写入 index。

V3 `query-preview` 使用当前 `IEmbeddingGenerator` 生成 query embedding，再对独立 `IVectorIndexStore` 执行 brute-force cosine topK 查询。它支持：

- `TopK`
- `ProfileId`
- `Layer`
- `ItemKind`
- `MinSimilarity`

`query-preview` 只读执行，不写入 index，不调用正式 retrieval，不改变 package 输出。返回候选会透传 duplicate / stale / orphan / lifecycle-risk 诊断，避免隐藏重复数据或噪声。

Phase V3.2 增加 `VectorQueryProfile` 与 `VectorCandidateEligibilityPolicy`。默认 profile：

- `normal-v1`
- `current-task-v1`
- `audit-v1`
- `diagnostics-v1`

Eligibility policy 只使用运行时元数据与 vector diagnostics：

- `lifecycle`
- `layer`
- `itemKind`
- source type / source kind
- similarity
- replacement metadata / stable review status
- duplicate / orphan / stale / dimension mismatch diagnostics

它不读取 eval `mustHit` / `mustNotHit` label、不读取 `sampleId`、不读取 fixture 文件名、不读取 `itemId` 特判，也不使用领域词表。`MinSimilarity` 在 V3.2 中作为 policy 阈值生效，低于阈值的 raw candidate 会被标记为 `SimilarityBelowThreshold`，不会在 store query 阶段被静默过滤。

候选新增输出：

- `RawRank`
- `EligibilityStatus`
- `BlockedReasons`
- `TargetSection`
- `RiskIfNormalSelected`
- `RiskAfterPolicy`

`normal-v1` 会阻断 deprecated / historical / rejected / candidate lifecycle、低相似度、重复 entry、orphan entry、dimension mismatch 与 stale embedding。`audit-v1` 可以把 historical / deprecated candidate 路由到 `audit_context`，因此不会把正确分区的历史候选计入 normal selected 风险。

V3.6 增加通用 policy repair：profile 可以声明 `DiagnosticsOnlyItemKinds`，policy 只根据运行时 `itemKind` 把诊断型候选阻断或路由到 diagnostics-only，不读取 eval label、sampleId、fixture 名称、itemId 或领域词表。该规则用于减少诊断/压力测试候选进入 normal vector preview 风险，同时避免按单个样本或单个候选 ID 做特判。

Phase V3.3 增加离线 `VectorQueryProfileSweepRunner` 与 `VectorEmbeddingQualityBaselineReport`。该 runner 只复用 vector query preview 结果做离线评估，不写入 index，不接正式 retrieval，不改 scoring，不改 `PackingPolicy`，不让 vector 进入 package 输出。

V3.3 sweep 维度：

- profile：`normal-v1` / `current-task-v1` / `audit-v1` / `diagnostics-v1`
- topK：`3` / `5` / `10` / `20`
- minSimilarity：`0.10` 到 `0.80`
- layer filter：`all` / `stable-only` / `candidate-stable` / `exclude-historical`

V3.3 评估约束：

- policy 不读取 eval `mustHit` / `mustNotHit` label。
- policy 不读取 `sampleId`、fixture 名称或领域词表。
- eval runner 可以使用 label 计算 recall / MRR / risk 指标。
- 不允许通过领域词表、样本名或特殊 case 调整 sweep 结果。

Phase V3.4 增加 provider compatibility 检查：

- reindex planner 在 provider / model / dimension / normalization 变化时将 item 标记为 `Update`，metadata 写入 `requiresReindex=true` 与 `changeReasons`。
- query preview 会把 query generator 与 index entry 的 provider / model / dimension / normalization 做兼容性检查。
- 不兼容候选会在 candidate diagnostics 中标记 `ProviderMismatch` / `EmbeddingModelMismatch` / `DimensionMismatch` / `NormalizationMismatch`，并通过 eligibility policy 阻断，不会静默混用不同 embedding 空间。

## Residual Risk Audit

V3.6 新增离线 residual risk audit：

- runner：`VectorResidualRiskAuditRunner`
- CLI：`eval vector-residual-risk-audit`
- JSON：
  - `eval/vector-residual-risk-audit-a3.json`
  - `eval/vector-residual-risk-audit-extended.json`
- Markdown：
  - `eval/vector-residual-risk-audit.md`

每条 residual risk 记录：

- `sampleId`
- `queryText`
- `profileId`
- `providerId`
- `embeddingModel`
- `candidateItemId`
- `similarity`
- `similarityMargin`
- `rawRank`
- `eligibleRank`
- `targetSection`
- `riskType`
- `riskReason`
- `itemLifecycle`
- `itemLayer`
- `itemKind`
- `sourceRef`
- `contentHash`
- `whyPolicyAllowed`
- `expectedAction`

风险分类包括：

- `DeprecatedMetadataGap`
- `LifecycleMetadataGap`
- `SupersededItemAllowed`
- `HistoricalItemAllowed`
- `WrongVersionActiveItem`
- `SemanticOvermatch`
- `SameTopicWrongIntent`
- `LowMarginAmbiguity`
- `SimilarityThresholdTooLoose`
- `ProfileTooBroad`
- `RequiresReranker`
- `RequiresHumanPolicyDecision`

Residual audit 可以使用 eval label 解释风险来源，但 `VectorCandidateEligibilityPolicy` 不允许读取 eval label。V3.6 已补测试保证 policy 不读取 `mustHit` / `mustNotHit`、`sampleId`、fixture 名称、`itemId` 或领域词表。

V3.6 risk-oriented sweep 新增：

- `RiskAfterPolicyByType`
- `RecallLossAfterRepair`
- `SimilarityMarginForRiskCandidates`

Recommendation 规则：

- `RiskAfterPolicy > 0` 且可由运行时 metadata 修复 -> `NeedsPolicyTuning`
- `RiskAfterPolicy > 0` 且 metadata 不足以判断 -> `RequiresReranker` / `KeepPreviewOnly`
- `RiskAfterPolicy = 0` 且 recall / MRR 稳定 -> `ReadyForRetrievalShadow`
- `RiskAfterPolicy = 0` 但 recall 损失过大 -> `KeepPreviewOnly`

## Lifecycle Metadata Coverage Gate

V3.6.1 新增 lifecycle metadata coverage 与 unknown-lifecycle blocking gate：

- resolver：`VectorSourceLifecycleMetadataResolver`
- builder：`VectorLifecycleMetadataCoverageReportBuilder`
- CLI：`eval vector-lifecycle-metadata-coverage`
- JSON：`eval/vector-lifecycle-metadata-coverage.json`
- Markdown：`eval/vector-lifecycle-metadata-coverage.md`

Coverage report 统计：

- `TotalVectorSourceItems`
- `KnownLifecycleCount`
- `UnknownLifecycleCount`
- `MissingReviewStatusCount`
- `MissingReplacementInfoCount`
- `LegacySourceWithoutLifecycleCount`
- `DeprecatedSourceWithoutLifecycleCount`
- `LifecycleCoverageRate`
- `CoverageByLayer`
- `CoverageByItemKind`
- `CoverageBySourceType`
- `Recommendation`

V3.6.1 policy gate：

- `normal-v1` / `current-task-v1` 要求明确 lifecycle，并阻断 lifecycle metadata incomplete 的候选。
- unknown lifecycle 被标记为 `UnknownLifecycleBlocked`。
- lifecycle metadata incomplete 被标记为 `LifecycleMetadataIncompleteBlocked`。
- 缺失 replacement metadata 的历史/替代链候选被标记为 `ReplacementMetadataMissingBlocked`。
- legacy/deprecated/historical source 缺少显式 lifecycle metadata 时被标记为 `LegacySourceRequiresLifecycleMetadata` 或 `HistoricalSourceRequiresAuditProfile`。
- `audit-v1` / `diagnostics-v1` 可观察 unknown / historical candidate，但 target section 必须是 `audit_context` 或 `diagnostics_only`，不得进入 `normal_context`。

约束边界：

- resolver 与 policy 只读取运行时 metadata、layer、itemKind、source type、review/replacement metadata 与 vector diagnostics。
- policy 不读取 eval label、sampleId、fixture name、itemId 特判或领域词表。
- coverage report 只读统计，不补写 metadata，不写 vector index。

V3.6.1 本地 ONNX 结果：

- lifecycle coverage：`149/158` known，`UnknownLifecycleCount=9`，`LifecycleCoverageRate=94.30%`，`Recommendation=NeedsLifecycleMetadataBackfill`。
- residual audit：A3 / Extended `ResidualRiskCount=0`，`AfterRepairRiskCount=0`。
- vector shadow eval：A3 / Extended `RiskAfterPolicy=0`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`。
- 因 lifecycle gate 带来 recall loss，当前 recommendation 仍为 `KeepPreviewOnly`，不得接正式 retrieval。

## Lifecycle Metadata Backfill Plan / Apply

V3.6.2 新增 lifecycle metadata backfill plan / apply：

- DTO：`VectorLifecycleMetadataBackfillPlan`
- DTO：`VectorLifecycleMetadataBackfillCandidate`
- DTO：`VectorLifecycleMetadataBackfillResult`
- planner：`VectorLifecycleMetadataBackfillPlanner`
- CLI：`eval vector-lifecycle-metadata-backfill-plan`
- CLI：`eval vector-lifecycle-metadata-backfill-apply --confirm`
- plan JSON：`eval/vector-lifecycle-metadata-backfill-plan.json`
- plan Markdown：`eval/vector-lifecycle-metadata-backfill-plan.md`
- result JSON：`eval/vector-lifecycle-metadata-backfill-result.json`
- result Markdown：`eval/vector-lifecycle-metadata-backfill-result.md`

Backfill 只写入 provider-scoped vector entry 的 sidecar metadata：

- `vectorLifecycleBackfill.lifecycle`
- `vectorLifecycleBackfill.reviewStatus`
- `vectorLifecycleBackfill.metadataSource`
- `vectorLifecycleBackfill.reason`
- `vectorLifecycleBackfill.evidenceMetadataKeys`
- `vectorLifecycleBackfill.policyVersion`
- `vectorLifecycleBackfill.appliedAt`

它不修改 `ContextItem` / `ContextMemoryItem` / stable memory / constraint store，也不改变 retrieval、planning、`PackingPolicy` 或 package 输出。`apply` 必须显式 `--confirm`；无证据项只进入 `ManualReviewRequired`，不会自动回填。

Planner 允许使用的证据只限运行时 metadata：

- 已有 `lifecycle` / `status` / `reviewStatus`
- stable / candidate provenance
- replacement metadata 与 relation chain marker：`supersededBy` / `replacedBy` / `replacementItemId` / `superseded_by` / `replaced_by`
- source type metadata：`sourceType` / `sourceKind` / `source` / `sourceMode`
- source tags 中的通用 lifecycle marker
- `createdFrom` / `sourceOperationId`
- layer / itemKind

禁止使用：

- itemId 特判
- sampleId 特判
- fixture 文件名特判
- `mustHit` / `mustNotHit` label
- 领域词表硬编码

V3.6.2 本地 ONNX 结果：

- backfill plan：`UnknownLifecycleBefore=9`，`AutoResolvableCount=9`，`ManualReviewRequiredCount=0`，`ExpectedCoverageAfter=100.00%`
- backfill apply：`updated=9`，`skipped=149`，`failed=0`
- lifecycle coverage：`158/158` known，`UnknownLifecycleCount=0`，`Recommendation=ReadyForVectorShadowEval`
- residual risk audit：A3 / Extended `ResidualRiskCount=0`
- vector shadow eval：A3 / Extended `RiskAfterPolicy=0`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`
- A3：`MustHitRecallAfterPolicy=71.21%`，`Recommendation=KeepPreviewOnly`
- Extended：`MustHitRecallAfterPolicy=84.38%`，`Recommendation=ReadyForRetrievalShadow`

结论：lifecycle metadata backfill 已恢复 coverage 并保持风险为 0；Extended 已具备 retrieval shadow 候选条件，但 A3 recall 仍不足，因此整体仍保持 preview / shadow-only，不接正式 retrieval。

V2 Service endpoints：

- `POST /api/vector/reindex-plan`
- `POST /api/vector/reindex-submit`
- `GET /api/vector/reindex-reports`
- `GET /api/vector/reindex-reports/{id}`

`reindex-plan` 只读生成 `VectorReindexPlan`，不写入 store。它会扫描 source context / memory items，计算 content hash，并对比当前 vector index entry：

- missing source embedding -> `Create`
- content hash / provider / model 变化 -> `Update`
- 当前 entry 已匹配 -> `Skip`
- 同一 `workspace + collection + itemId + provider + model` 下多 entry -> `Duplicate`
- vector entry 没有 source item -> `DeleteOrphan`

`reindex-submit` 只提交 `vector_reindex` job。写入必须满足：

- `Apply=true`
- `DryRun=false`
- `ConfirmApply=true`

第一版 apply 只对 `Create` / `Update` 执行 upsert。`Duplicate` / `DeleteOrphan` 只报告，不自动删除，避免误删或扩大影响面。

## Reindex Report Store

新增 `IVectorReindexReportStore`：

- `SaveAsync`
- `QueryAsync`
- `GetAsync`

实现：

- `InMemoryVectorReindexReportStore`
- `FileVectorReindexReportStore`

文件系统实现写入：

- `workspaces/{workspaceId}/collections/{collectionId}/vectors/reindex-reports.jsonl`

报告按 `ReportId` upsert，避免重复运行时盲目追加同一报告造成存储膨胀或诊断噪声。

## CLI

新增 eval CLI：

- `eval vector-reindex-plan`
- `eval vector-reindex-apply --confirm`
- `eval vector-index-diagnostics`
- `eval vector-index-coverage`
- `eval vector-query-preview [--profile <id>]`
- `eval vector-query-shadow-eval [--profile <id>]`
- `eval vector-query-profile-sweep`
- `eval vector-residual-risk-audit`
- `eval vector-recall-loss-audit`

默认输出位于项目内专用目录：

- `vector/reindex/vector-reindex-report.json`
- `vector/reindex/vector-reindex-report.md`
- `vector/reindex/vector-index-diagnostics.json`
- `vector/reindex/vector-index-diagnostics.md`
- `vector/reindex/vector-index-coverage-report.json`
- `vector/reindex/vector-index-coverage-report.md`
- `vector/query/vector-query-preview.json`
- `vector/query/vector-query-preview.md`
- `eval/vector-query-shadow-eval-a3.json`
- `eval/vector-query-shadow-eval-extended.json`
- `eval/vector-query-shadow-eval.md`
- `eval/vector-query-profile-sweep-a3.json`
- `eval/vector-query-profile-sweep-extended.json`
- `eval/vector-query-profile-sweep.md`
- `eval/vector-residual-risk-audit-a3.json`
- `eval/vector-residual-risk-audit-extended.json`
- `eval/vector-residual-risk-audit.md`
- `eval/vector-recall-loss-audit-a3.json`
- `eval/vector-recall-loss-audit-extended.json`
- `eval/vector-recall-loss-audit.md`
- `eval/vector-embedding-quality-baseline.json`
- `eval/vector-embedding-quality-baseline.md`
- `eval/vector-embedding-provider-comparison.json`
- `eval/vector-embedding-provider-comparison.md`

`vector-reindex-apply` 没有 `--confirm` / `--yes` 时直接退出，不执行写入。

V3.1 Eval CLI 默认 `--source eval-corpus`：

- 默认 workspace / collection：`eval-vector` / `corpus`。
- 默认读取 `eval/contexts/*/corpus*.json`，覆盖 A3 与 Extended 语料。
- 显式 `--source store` 时恢复 V2 的 context / memory store 扫描行为。
- 显式 `--workspace` / `--collection` 可覆盖默认 eval-corpus 索引空间。

Coverage report 推荐结论：

- `NeedsInitialIndexing`
- `NeedsReindex`
- `ReadyForVectorShadowEval`
- `BlockedByDiagnostics`

当前 V3.1 本地覆盖率基线：

- `TotalSourceItems=158`
- `IndexedItems=158`
- `CoverageRate=100.00%`
- `DuplicateCount=0`
- `OrphanCount=0`
- `DimensionMismatchCount=0`
- `ProviderUnavailableCount=0`
- `Recommendation=ReadyForVectorShadowEval`

`vector-query-shadow-eval` 会对 A3 / Extended 样本执行 query preview，并统计：

- `IndexedCoverage`
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
- `DeprecatedHitCount`
- `DuplicateHitCount`
- `AverageTopSimilarity`
- `NoCandidateCount`
- `LowConfidenceCount`
- `TopNoiseClusters`
- `BlockedByReason`

当 index 为空或覆盖不足时，recommendation 为 `NeedsMoreIndexedData`；该状态不会被包装成通过或 ready。V3.1 reindex 后当前 shadow eval 已不再为空索引：

- A3：`Samples=50`，`CandidateCount=500`，`IndexedCoverage=100.00%`，`Recommendation=BlockedByRisk`
- Extended：`Samples=113`，`CandidateCount=1130`，`IndexedCoverage=100.00%`，`Recommendation=BlockedByRisk`

V3.2 recommendation 规则：

- `RiskAfterPolicy > 0` -> `BlockedByRisk`
- `EligibleCandidateCount = 0` -> `NeedsPolicyTuning`
- index 覆盖不足 -> `NeedsMoreIndexedData`
- 风险为 0 且存在稳定 eligible candidate -> `ReadyForRetrievalShadow`

本阶段仍只记录 preview / shadow 结果，不做 scorer、retrieval、`PackingPolicy` 或 package 修复。

V3.3 recommendation 额外支持：

- `NeedsRealEmbeddingProvider`

该结论用于标记 deterministic hash embedding 的语义区分能力不足。当前本地质量基线：

- `PositiveAverageSimilarity=0.3661`
- `NegativeAverageSimilarity=0.3595`
- `SimilaritySeparation=0.0066`
- `MustHitRecallAt20=12.50%`
- `MustNotHitRiskAt20=4.10%`
- `Recommendation=NeedsRealEmbeddingProvider`

这说明现有 deterministic hash embedding 适合稳定测试和管线验证，但不足以作为语义向量召回质量判断依据；后续仍必须保持 preview / shadow-only，不能接入正式 retrieval。

V3.4 provider comparison report 包含：

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

默认 deterministic hash provider 的 comparison 会继续暴露 `NeedsRealEmbeddingProvider`。如果切到 `OnnxLocal` 但模型或 tokenizer 缺失，report / diagnostics 会显示 `ProviderUnavailable`、`ModelFileMissing` 或 `TokenizerUnavailable`，而不是静默降级或伪造结果。

## Client

`ContextCoreClient` 新增：

- `GetVectorStatusAsync`
- `GetVectorDiagnosticsAsync`
- `PreviewVectorReindexAsync`
- `CreateVectorReindexPlanAsync`
- `SubmitVectorReindexAsync`
- `GetVectorReindexReportsAsync`
- `GetVectorReindexReportAsync`
- `PreviewVectorQueryAsync`

ControlRoom Service Mode 通过这些强类型方法访问服务，不直接拼接存储路径或绕过 Service API。

## ControlRoom

Service Mode 新增 `37` VectorIndex 页面。

展示：

- provider
- model
- dimension
- indexed count
- stale count
- missing count
- duplicate count
- orphan count
- coverage rate
- coverage recommendation
- shadow quality recommendation
- best sweep profile / topK / minSimilarity
- riskAfterPolicy / similarity separation
- residual risk count / top risk types
- whyPolicyAllowed / expectedAction summary
- diagnostics
- reindex preview summary

V2 页面新增人工操作：

- `P` Reindex Plan
- `A` Apply Reindex
- `R` Reindex Reports
- `Q` Query Preview
- `D` Diagnostics / refresh

`A` 操作前会展示 plan，并要求输入 `YES`。未确认时不调用 submit endpoint。确认后只提交 `vector_reindex` job；不会改变正式 retrieval、planning、`PackingPolicy` 或 package 输出。

`Q` Query Preview 需要手动输入 query text / topK / profile / layer filter / minSimilarity。页面展示 raw / eligible / blocked candidates、blocked reasons、target section、riskBefore / riskAfter 和 diagnostics，不写入 index，不修改任何正式输出。

Shadow Quality Summary 只读展示本地 `eval/vector-query-profile-sweep-extended.json` 或 A3 sweep 报告中的最佳配置，不自动触发 sweep，不调用 service 写路径。V3.6 还会读取 `eval/vector-residual-risk-audit-extended.json` 或 A3 residual audit 报告，展示 residual risk count、top risk types、whyPolicyAllowed 和 expectedAction，便于人工判断是否继续 preview-only。

V3.6 使用 `onnx-local` 重新索引和重跑后，当前本地最佳结果：

- A3：`normal-v1:top5:min0.10:all`，`RecallAfter=62.12%`，`MRR=0.6667`，`RiskAfter=0`，`Recommendation=KeepPreviewOnly`
- Extended：`normal-v1:top5:min0.10:all`，`RecallAfter=78.75%`，`MRR=0.8171`，`RiskAfter=0`，`Recommendation=KeepPreviewOnly`

V3.4 provider comparison report 由 `eval vector-query-profile-sweep` 同步生成。可通过以下参数显式测试本地 ONNX provider：

- `--provider deterministic-hash|onnx-local`
- `--provider-type OnnxLocal`
- `--model-path <local.onnx>`
- `--tokenizer-path <vocab.txt>`
- `--embedding-model <model-name>`
- `--dimension <n>`
- `--pooling Mean|Cls`

V3.5 中 `vector-reindex-plan`、`vector-reindex-apply`、`vector-query-preview`、`vector-query-shadow-eval`、`vector-query-profile-sweep` 都支持 provider 参数。Provider-scoped query 会按 provider/model 过滤 index，不静默混用旧 index；dimension / normalization 不一致继续作为 diagnostics 暴露并由 policy 阻断。

`eval vector-query-profile-sweep` 会生成 multi-provider comparison，至少包含：

- `deterministic-hash`
- `onnx-local`

如果本地 ONNX 模型未配置，`onnx-local` 会以 `ProviderUnavailable` / `ModelFileMissing` / `TokenizerUnavailable` 等 diagnostics 形式出现在 comparison report 中，而不会伪造质量指标。

本地配置与操作步骤见：

- `appsettings.VectorEmbedding.sample.json`
- `docs/vector-embedding-provider-local-runbook.md`

上述命令仍只做离线报告；不会把 ONNX provider 接入正式 retrieval、scoring、`PackingPolicy` 或 package 输出。

## 验证

## Phase V3.6.4 - Safe Recall Recovery / V4 Readiness Gate

V3.6.4 新增离线 safe recall recovery 与 V4 readiness gate：

- DTO：
  - `VectorSafeRecallRecoveryReport`
  - `VectorSafeRecallRecoverySweepResult`
  - `VectorBlockedMustHitAuditRecord`
  - `VectorRetrievalShadowReadinessGateReport`
- Runner：`VectorSafeRecallRecoveryRunner`
- CLI：
  - `eval vector-safe-recall-recovery`
  - `eval vector-retrieval-shadow-readiness-gate`
- 输出：
  - `eval/vector-safe-recall-recovery-a3.json`
  - `eval/vector-safe-recall-recovery-extended.json`
  - `eval/vector-safe-recall-recovery.md`
  - `eval/vector-retrieval-shadow-readiness-gate.json`
  - `eval/vector-retrieval-shadow-readiness-gate.md`

safe recall recovery 行为：

- 对 `BelowTopK` miss 扫描 `topK=10/20/30/50`、`minSimilarity=0.05/0.10/0.15/0.20/0.30`、`stable-only / candidate+stable / exclude-historical` 与 `normal-v1 / current-task-v1 / audit-v1 / diagnostics-v1`。
- 对 `BlockedByEligibilityPolicy` miss 输出 blocked reasons、resolved lifecycle、metadata completeness、replacement state、target section、classification 与 recommended repair。
- 高召回但带 `RiskAfterPolicy` 的组合只作为 tuning 线索，不作为 V4 gate 通过依据。
- `onnx-local` 默认 CLI dimension 已校正为 `512`，匹配当前 `bge-small-zh-v1.5` provider-scoped index；其他模型仍可通过 `--dimension` 显式覆盖。

blocked mustHit 分类：

- `MetadataRepairNeeded`
- `ProfileTooNarrow`
- `LayerFilterTooStrict`
- `HistoricalMustHitRequiresAuditProfile`
- `DeprecatedMustHitBlockedCorrectly`
- `RequiresRankerFusion`
- `RequiresManualReview`
- `ShouldRemainBlocked`

V4 readiness gate 条件：

- A3 `RiskAfterPolicy = 0`
- A3 `MustNotHitRiskAfterPolicy = 0`
- A3 `LifecycleRiskAfterPolicy = 0`
- A3 `RecallAfterPolicy >= 80%`
- Extended `RiskAfterPolicy = 0`
- Extended `RecallAfterPolicy >= 80%`
- `FormalOutputChanged = 0`
- P15 gate 仍需单独通过

V3.6.4 当前结果：

- `eval vector-safe-recall-recovery --provider onnx-local`
  - A3 baseline：`RecallAfterPolicy=71.21%`，`MRR=0.6765`，`RiskAfterPolicy=0`
  - A3 miss：`BelowTopK=10`，`BlockedMustHit=9`
  - A3 高召回 sweep 可到 `88%`，但 `RiskAfterPolicy > 0`，因此不能作为 safe recovery 配置。
  - A3 最佳 risk-free sweep：`normal-v1:top10:min0.05:stable-only`，`RecallAfterPolicy=3.03%`
  - Extended baseline：`RecallAfterPolicy=84.38%`，`MRR=0.8229`，`RiskAfterPolicy=0`
  - Extended miss：`BelowTopK=16`，`BlockedMustHit=9`
  - blocked mustHit 当前均为 `DeprecatedMustHitBlockedCorrectly`，normal profile 不直接放行。
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`
  - `Passed=false`
  - `FailReasons=A3RecallAtLeast80Percent`
  - risk / mustNotHit / lifecycle / formal output 条件均未触发失败。

结论：Extended 仍保持 `ReadyForRetrievalShadow`；A3 因 recall 未达 80% 保持 `KeepPreviewOnly / NeedsProfileTuning`。本轮没有将 vector 接入正式 retrieval，没有改变 scoring、`PackingPolicy` 或 package 输出。

V3.6.4 当前验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`707 passed / 0 failed`
- `eval vector-safe-recall-recovery --provider onnx-local`：A3 / Extended safe recall recovery 已生成
- `eval vector-retrieval-shadow-readiness-gate --provider onnx-local`：gate 已生成，当前因 A3 recall 未达 80% 未通过
- `eval vector-query-shadow-eval --provider onnx-local`：报告已刷新
- `eval vector-query-profile-sweep --provider onnx-local`：报告已刷新

## Phase V3.6.3 - Recall Loss Audit / Intent Readiness

V3.6.3 新增只读召回损失审计：

- DTO：
  - `VectorRecallLossAuditReport`
  - `VectorRecallLossMiss`
  - `VectorIntentReadinessReport`
  - `VectorIntentReadinessBucket`
- Runner：`VectorRecallLossAuditRunner`
- CLI：`eval vector-recall-loss-audit`
- 输出：
  - `eval/vector-recall-loss-audit-a3.json`
  - `eval/vector-recall-loss-audit-extended.json`
  - `eval/vector-recall-loss-audit.md`

审计逻辑：

- 当前配置预览用于判断 policy 后是否命中。
- 宽口径诊断预览只用于解释 miss reason，例如 `BelowTopK`、`BelowSimilarityThreshold`、`BlockedByEligibilityPolicy`、`LayerFilterExcluded`、`ItemKindFilterExcluded`、`NoCandidateGenerated`。
- `wasIndexed` 使用 provider/model scoped vector index entries 判断；Service Mode 无法拉全量 index 时会写入 warning，不伪装为完整审计。
- intent 分组优先读取样本 metadata 中的 `intent`，否则复用现有 `PlanningIntentDetector` 做报告分组；该 intent 只用于 report，不进入 vector eligibility policy。

硬边界：

- 不放松 lifecycle / deprecated / historical / provider / duplicate / orphan / stale safety gate。
- 不使用 itemId / sampleId / fixture 文件名 / mustHit / mustNotHit label / 领域词表做 policy 特判。
- eval label 只用于 audit/report，不进入 `VectorCandidateEligibilityPolicy`。
- 不接正式 retrieval，不改 scoring，不改 `PackingPolicy`，不让 vector 进入 package 输出。

V3.6.3 当前结果：

- A3：`MustHitRecallAfterPolicy=71.21%`，`MustHitMrrAfterPolicy=0.6765`，`RiskAfterPolicy=0`，`MissedMustHitCount=19`，`Recommendation=NeedsProfileTuning`
  - miss reason：`BelowTopK=10`，`BlockedByEligibilityPolicy=9`
  - ready intents：`FuzzyQuestion`、`NovelGeneration`
  - needs tuning intents：`AuditDeprecated`、`AutomationRecovery`、`CodingTask`
- Extended：`MustHitRecallAfterPolicy=84.38%`，`MustHitMrrAfterPolicy=0.8229`，`RiskAfterPolicy=0`，`MissedMustHitCount=25`，`Recommendation=ReadyForRetrievalShadow`
  - miss reason：`BelowTopK=16`，`BlockedByEligibilityPolicy=9`
  - ready intents：`AutomationRecovery`、`CodingTask`、`ConflictCheck`、`CurrentTask`、`FuzzyQuestion`、`LongTermPreference`、`NovelGeneration`
  - needs tuning intent：`AuditDeprecated`

结论：Extended 已具备 retrieval shadow 候选质量；A3 仍需要 profile/topK 或 audit-profile 分流调优。由于本轮只做报告，正式 retrieval/package 输出保持不变。

V3.6.3 当前验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test --no-build`：通过，`703 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`，`MustNotHitViolation=0`，`LifecycleViolation=0`，`HardConstraintMissing=0`
- `eval vector-recall-loss-audit --provider onnx-local`：A3 / Extended recall loss audit 已生成
- `eval vector-query-shadow-eval --provider onnx-local`：报告已刷新
- `eval vector-query-profile-sweep --provider onnx-local`：报告已刷新

V3.6.2 当前验证：

- `dotnet build`：`0 warning / 0 error`
- `dotnet test`：通过，`701 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`：通过，A3 50 / Extended 113 均为 `0 failed / 100.00% pass`
- `eval embedding-provider-smoke --provider onnx-local`：`Succeeded=True`，`EmbeddingModel=bge-small-zh-v1.5`，`Dimension=512`，diagnostics=`0`
- `eval vector-reindex-apply --provider onnx-local --confirm`：`created=0`，`updated=158`，`failed=0`
- `eval vector-index-coverage --provider onnx-local`：`IndexedItems=158/158`，`CoverageRate=100.00%`，`DuplicateCount=0`，`OrphanCount=0`，`DimensionMismatchCount=0`，`ProviderUnavailableCount=0`，`Recommendation=ReadyForVectorShadowEval`
- `eval vector-lifecycle-metadata-backfill-plan --provider onnx-local`：`UnknownLifecycleBefore=9`，`AutoResolvableCount=9`，`ManualReviewRequiredCount=0`，`ExpectedCoverageAfter=100.00%`
- `eval vector-lifecycle-metadata-backfill-apply --provider onnx-local --confirm`：`updated=9`，`skipped=149`，`failed=0`
- `eval vector-lifecycle-metadata-coverage --provider onnx-local`：`KnownLifecycleCount=158/158`，`UnknownLifecycleCount=0`，`Recommendation=ReadyForVectorShadowEval`
- `eval vector-residual-risk-audit --provider onnx-local`：A3 / Extended residual audit 已生成
- `eval vector-query-profile-sweep --provider onnx-local`：报告已生成
- `eval vector-query-shadow-eval --provider onnx-local`：报告已刷新

V3.6.2 ONNX residual risk audit 当前结果：

- A3：`ResidualRiskCount=0`，`BeforeRepairRiskCount=174`，`AfterRepairRiskCount=0`，`BlockedByLifecycleMetadataGate=145`，`MustHitRecallAfterPolicy=71.21%`，`MustHitMrrAfterPolicy=0.6765`，`RecallLossAfterRepair=13.64%`，`Recommendation=KeepPreviewOnly`
- Extended：`ResidualRiskCount=0`，`BeforeRepairRiskCount=215`，`AfterRepairRiskCount=0`，`BlockedByLifecycleMetadataGate=185`，`MustHitRecallAfterPolicy=84.38%`，`MustHitMrrAfterPolicy=0.8229`，`RecallLossAfterRepair=5.63%`，`Recommendation=ReadyForRetrievalShadow`
- 剩余 residual risk 已归零；backfill 使用 runtime metadata / sidecar metadata，不使用 itemId / sampleId 特判，也不通过领域词表硬编码绕过。

V3.6.2 ONNX shadow eval 当前结果：

- A3：`RawCandidateCount=500`，`EligibleCandidateCount=326`，`BlockedCandidateCount=174`，`RiskBeforePolicy=174`，`RiskAfterPolicy=0`，`MustHitRecallAfterPolicy=71.21%`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`，`Recommendation=KeepPreviewOnly`
- Extended：`RawCandidateCount=1130`，`EligibleCandidateCount=915`，`BlockedCandidateCount=215`，`RiskBeforePolicy=215`，`RiskAfterPolicy=0`，`MustHitRecallAfterPolicy=84.38%`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`，`FormalOutputChanged=0`，`Recommendation=ReadyForRetrievalShadow`
- 主要阻断原因：`HistoricalSourceRequiresAuditProfile`、`DeprecatedCandidateBlocked`、`LifecycleMetadataIncompleteBlocked`、`ReplacementMetadataMissingBlocked`、`DiagnosticsOnlyItemKindBlocked`

V3.6.2 ONNX profile sweep 当前最佳配置：

- A3：`normal-v1:top10:min0.10:all`，`MustHitRecallAfterPolicy=71.21%`，`MustHitMrrAfterPolicy=0.6765`，`RiskAfterPolicy=0`，`Recommendation=KeepPreviewOnly`
- Extended：`normal-v1:top10:min0.10:all`，`MustHitRecallAfterPolicy=84.38%`，`MustHitMrrAfterPolicy=0.8229`，`RiskAfterPolicy=0`，`Recommendation=ReadyForRetrievalShadow`

V3.6.2 provider comparison 当前结果：

- 顶层：`Recommendation=ReadyForRetrievalShadow`
- deterministic-hash：`SimilaritySeparation=0.0066`，`MustHitRecallAfterPolicy=3.75%`，`MustHitMrrAfterPolicy=0.0104`，`MustNotHitRiskAfterPolicy=0`
- onnx-local：`SimilaritySeparation=0.0857`，`MustHitRecallAfterPolicy=84.38%`，`MustHitMrrAfterPolicy=0.8229`，`MustNotHitRiskAfterPolicy=0`，`LifecycleRiskAfterPolicy=0`
- 结论：ONNX provider 的语义区分和 must-hit recall 明显优于 deterministic hash；风险已为 0，但本轮仍不接正式 retrieval，继续保持 preview / shadow-only。

V3.4 / V3.5 provider diagnostics 当前覆盖：

- missing ONNX model -> `ProviderUnavailable` / `ModelFileMissing`
- missing tokenizer -> `TokenizerUnavailable`
- ONNX inference failed -> `OnnxSessionFailed`
- ONNX disabled -> `ProviderUnavailable`，且不要求模型文件
- dimension mismatch -> query preview candidate blocked
- provider/model changed -> diagnostics 标记 `RequiresReindex`
- deterministic hash provider -> 不要求模型文件
- provider-scoped search -> 按 provider/model 过滤，不混用旧 index
- provider comparison -> 同时输出 deterministic-hash 与 onnx-local 结果

新增测试覆盖：

- vector entry upsert / get / delete
- `DeterministicHashEmbeddingGenerator` 稳定输出
- brute-force cosine query 最近项
- stale embedding / content hash mismatch
- dimension mismatch
- duplicate entry diagnostics
- FileSystem / InMemory store
- ControlRoom VectorIndex 渲染与菜单入口
- reindex plan missing / stale / duplicate / orphan
- reindex apply create / update
- external eval source item reindex
- repeated external source apply does not create duplicate vector entries
- dry-run does not write vector entries
- apply requires explicit confirm
- ContextCoreClient reindex routes
- ContextCoreClient vector query preview route
- ControlRoom Apply requires `YES`
- query preview nearest candidate
- layer filter
- minSimilarity policy block
- empty index diagnostics
- duplicate / stale / orphan diagnostics surfaced
- normal-v1 blocks deprecated candidate
- normal-v1 blocks historical candidate
- audit-v1 routes historical candidate to audit_context
- duplicate / orphan diagnostics block candidate
- eligibility policy does not read eval labels
- vector query shadow eval mustNotHit risk
- vector query shadow eval formal output unchanged
- profile sweep generates profile / topK / minSimilarity combinations
- riskAfterPolicy 非 0 时 recommendation 为 `BlockedByRisk`
- low similarity separation returns `NeedsRealEmbeddingProvider`
- ControlRoom renders Shadow Quality Summary
- ONNX missing model diagnostics
- ONNX missing tokenizer smoke diagnostics
- deterministic provider smoke test
- provider-scoped vector search
- provider changed requires reindex diagnostics
- provider comparison report builder / multi-provider report
- residual risk audit records whyPolicyAllowed
- metadata gap risk classified as `LifecycleMetadataGap`
- semantic overmatch classified as `RequiresReranker`
- eligibility policy does not use itemId / sampleId shortcuts
- profile sweep includes risk type breakdown
- ControlRoom renders residual risk summary

## DB5.0 Postgres pgvector Provider Foundation

Vector preview/shadow 结论仍保持冻结；DB5.0 只新增 Postgres / pgvector 作为 VectorIndexStore 的 storage provider foundation。

新增能力：

- `PostgresVectorIndexStore`
- pgvector extension diagnostics
- provider/model/dimension compatibility report
- deterministic vector provider smoke
- nearest-neighbor query smoke

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-diagnostics
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-compatibility
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-smoke --cleanup-confirm
```

报告输出：

- `storage/postgres/postgres-vector-diagnostics.json`
- `storage/postgres/postgres-vector-compatibility.json`
- `storage/postgres/postgres-vector-provider-smoke-report.json`

DB5.0 边界：

- Postgres vector provider `UseForRuntime=false`
- 不接 formal retrieval
- 不放松 vector eligibility / lifecycle safety gate
- 不改变 vector readiness gate
- 不改变 scoring、`PackingPolicy` 或 package output
- provider/model/dimension 不兼容时报告 diagnostics 或阻断 query，不静默混用 index entries

当前本地结果：

- diagnostics `ReadyForVectorParityEval`，`PgVectorAvailable=true`，`SchemaVersion=cc-schema-v6`，`MissingIndexCount=0`
- compatibility `ReadyForVectorParityEval`，`ExistingCompatibleEntryCount=0`，`StaleProviderModelEntriesCount=0`
- provider smoke `ReadyForVectorParityEval`，`InsertedCount=3`，`UpsertedCount=1`，`QueryCount=2`，`MismatchCount=0`
- smoke 已验证 dimension mismatch / provider-model mismatch 被阻断，并执行 cleanup

## DB5.1 FileSystem/Postgres Vector Index Parity

DB5.1 只比较 FileSystem VectorIndexStore 与 Postgres pgvector provider 的行为一致性，不改变 vector preview/shadow readiness，也不接 formal retrieval。

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-parity --cleanup-confirm
```

输出：

- `storage/postgres/postgres-vector-parity-report.json`
- `storage/postgres/postgres-vector-parity-report.md`

当前结果：

- `Recommendation=ReadyForProviderScopedReindex`
- `MismatchCount=0`
- `OrderingMismatchCount=0`
- `ScoreDeltaMax=0.00000002`
- `MetadataMismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`
- `CleanupPerformed=true`
- `UseForRuntime=false`

归一化状态说明：当前 `IVectorIndexStore` 公共契约未暴露 normalized flag，parity report 以 `NormalizedFlagNotPartOfIVectorIndexStoreContract` 记录为 warning，不作为 runtime 启用依据。

## DB5.2 Provider-scoped pgvector Reindex

DB5.2 只做 provider-scoped reindex into pgvector，不改变 vector preview/shadow readiness，也不接 formal retrieval。

新增能力：

- `PostgresVectorProviderScopedReindexPlan`
- `PostgresVectorProviderScopedReindexResult`
- `PostgresVectorProviderScopedReindexReport`
- `PostgresVectorProviderScopedReindexRunner`

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-scoped-reindex-plan
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-scoped-reindex-apply --confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-scoped-reindex-quality
```

输出：

- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.md`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.md`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.md`

当前结果：

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

- 不绑定正式 `IVectorIndexStore`。
- 不写 FileSystem formal vector store。
- 不改变 `VectorIndexService` runtime path。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## DB5.3 PgVector Query Preview

DB5.3 在 pgvector provider-scoped index 上执行 query preview，并与临时 FileSystem baseline 做离线对比。本阶段仍为 storage/provider preview，不改变 VectorRetrieval readiness，不接 formal retrieval。

新增能力：

- `PostgresVectorQueryPreviewRunner`
- `PostgresVectorQueryPreviewReport`
- `postgres-vector-query-preview`

输出：

- `storage/postgres/postgres-vector-query-preview-report.json`
- `storage/postgres/postgres-vector-query-preview-report.md`

当前结果：

- `QueryCount=113`
- `CandidateCount=1130`
- `PgVectorCandidateCount=1130`
- `FileSystemCandidateCount=1130`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `ScoreDeltaMax=0.00000009`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`
- `Recommendation=ReadyForPgVectorShadowEval`
- `UseForRuntime=false`

边界：

- FileSystem baseline 使用临时目录，不写正式 vector store。
- pgvector query 必须限定 provider / model / dimension / normalized scope。
- target section / risk projection 只进入报告，不改变 package 输出。

## DB5.4 PgVector Shadow Eval

DB5.4 在 pgvector provider-scoped index 上执行 A3 / Extended vector shadow eval，并与临时 FileSystem baseline 对比。本阶段只验证 PostgresVectorIndexStore 的 shadow eval parity，不改变 VectorRetrieval readiness，不接 formal retrieval。

新增能力：

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

当前结果：

- Summary `Recommendation=ReadyForVectorPostgresFreeze`
- A3 `SampleCount=50`，`RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- A3 `TopKOverlapRate=100.00%`，`OrderingMismatchCount=0`，`MetadataMismatchCount=0`，`EligibilityMetadataMismatchCount=0`，`RiskProjectionMismatchCount=0`
- Extended `SampleCount=113`，`RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- Extended `TopKOverlapRate=100.00%`，`OrderingMismatchCount=0`，`MetadataMismatchCount=0`，`EligibilityMetadataMismatchCount=0`，`RiskProjectionMismatchCount=0`
- `UseForRuntime=false`

边界：

- pgvector 和 FileSystem 的 recall / risk parity 只用于 storage provider freeze。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 V4 VectorRetrieval readiness。

## DB5.F Vector Postgres Provider Freeze

DB5.F 新增 `VectorPostgresProviderFreezeGateReport` 和 `postgres-vector-freeze-gate`。

输出：

- `storage/postgres/postgres-vector-freeze-gate.json`
- `storage/postgres/postgres-vector-freeze-gate.md`

冻结结论：

- `VectorPostgresProvider=ReadyForPreviewShadowStorage`
- `VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- pgvector 只允许用于 preview / shadow / eval storage。

硬边界：

- 未通过 V4 readiness gate 前，不允许 pgvector formal retrieval switch。
- 未通过 V4 readiness gate 前，不允许将 `PostgresVectorIndexStore` 绑定为正式 `IVectorIndexStore`。
- 未通过 V4 readiness gate 前，不允许接入 `PackingPolicy` 或 package output。

## V3.10 Qwen3-Embedding Provider Comparison

V3.10 新增 Qwen3 provider scope，用于比较 `qwen3-embedding-0.6b-onnx` 与当前 ONNX provider 的离线 vector quality。该阶段只做 preview / shadow / eval，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 `PackingPolicy` 或 package output。

本地模型目录：

- `src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/model_int8.onnx`
- `src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/tokenizer.json`
- `src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/vocab.json`
- `src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/merges.txt`

新增能力：

- `QwenBpeEmbeddingTokenizer`
- `EmbeddingTokenizerFactory`
- Qwen3 provider preset alias：`--provider qwen3`（实际实现类型强制为 `OnnxLocal`，`--provider-type` 才表示 provider implementation）
- `vector-provider-comparison`
- `vector-qwen3-shadow-eval`
- `vector-qwen3-readiness-gate`
- `vector-provider-configuration-sanity-audit`

当前结果：

- embedding provider smoke：`Succeeded=true`
- FileSystem Qwen3 provider-scoped reindex：`158` entries
- A3 recall after policy：`54.55%`
- A3 MRR after policy：`0.6262`
- A3 risk after policy：`1`
- Extended recall after policy：`76.88%`
- Extended MRR after policy：`0.8058`
- Extended risk after policy：`1`
- pgvector Qwen3 reindex quality：`ReadyForPgVectorQueryPreview`
- pgvector query preview：`BlockedByRiskProjectionMismatch`
- pgvector shadow eval：`BlockedByProjectionMismatch`
- provider configuration sanity audit：`Passed=true`，`ProviderComparison=Conclusive`
- Qwen3 readiness gate：`Passed=false`，`Recommendation=BlockedByRisk`

结论：

- Qwen3 当前本地导出可运行，但未达到 V4 readiness：A3 recall 低于 `80%`，Extended recall 低于 `80%`，且 risk after policy 非 `0`。
- 因 provider metadata sanity audit 已通过，本轮冻结结论是质量未达标导致的 `DoNotPromote / BlockedByRisk`，不是配置不确定状态。
- pgvector / FileSystem projection parity 在 Qwen3 scope 下未通过，不能作为 formal retrieval storage。
- `UseForRuntime=false`，`FormalRetrievalAllowed=false` 保持不变。


## Hybrid Retrieval Preview (Lexical + Dense Candidate Union)

hybrid retrieval preview 在 dense 向量召回之外引入 lexical（词法）和 anchor（元数据锚点）两路候选来源，通过 union 策略合并去重后做 shadow eval。

候选来源：

- **dense**：复用 VectorQueryPreviewService，brute-force cosine 叹回。
- **lexical**：LexicalCandidateProvider，基于 query tokens（ASCII ≥ 2 + CJK bigram）与 indexed entry 文本的命中计数打分。
- **anchor**：AnchorCandidateProvider，基于 query tokens 与 entry 的 sourceTags / ItemKind / sourceRefs 匹配。

label-free 约束（硬规则）：

- 候选提供者不读 mustHit / mustNotHit 标签。
- 不特判 sampleId / itemId。
- 不依赖 fixture / domain 词表。
- 不使用 resolver alias。
- eval 标签仅在 HybridRetrievalPreviewRunner 层通过 EvalIdMatches 消费。

union 策略（HybridCandidateUnionPolicy）：

- 合并 dense / lexical / anchor 三路，按 ItemId 去重。
- 同一 ItemId 保留最高优先来源（dense > lexical > anchor）的候选主体。
- Metadata["hybridSources"] 记录贡献来源。
- 保留每个候选的 eligibility / risk / lifecycle 元数据（透传，不重新计算）。

变体覆盖：A3 + Extended 各 4 个变体（Dense / DenseLexical / DenseAnchor / DenseLexicalAnchor），共 8 条报告。

readiness gate 条件（任一不满足即 Passed=false）：

- A3RecallAfterPolicy ≥ 80%
- ExtendedRecallAfterPolicy ≥ 80%
- RiskAfterPolicy = 0
- MustNotHitRiskAfterPolicy = 0
- LifecycleRiskAfterPolicy = 0
- FormalOutputChanged = 0
- PolicyViolationFound = false
- P15 gate 通过

FormalRetrievalAllowed 恒为 false；UseForRuntime 恒为 false。

## Hybrid Retrieval Recall Regression Sanity Audit

当 hybrid preview recall 与 legacy dense baseline 出现明显偏差时，通过 HybridRetrievalRecallRegressionAuditRunner 做对齐诊断。

audit 覆盖 7 个 profile：

- legacy-dense-baseline（原 VectorQueryShadowEvalRunner 路径）
- hybrid-dense-only（hybrid runner Dense 变体）
- hybrid-dense-plus-lexical
- hybrid-dense-plus-anchor
- hybrid-dense-plus-lexical-anchor
- lexical-only
- anchor-only

对齐验证项：

- hybrid-dense-only recall 与 legacy dense baseline 对齐（差异在容忍范围内）
- hybrid union 不丢弃 dense candidate（DenseCandidateDroppedCount=0）
- union dedup 不错误覆盖 dense contribution（DedupOverwriteCount=0）
- eligibility policy 输入一致（EligibilityMismatchCount=0）
- provider/model/dimension/normalized 一致
- topK / minSimilarity / layer policy 一致
- sample count / query count 一致

gate 规则：

- hybrid-dense-only recall 必须等于 legacy dense baseline
- RiskAfterPolicy 必须保持 0
- FormalOutputChanged 必须为 0
- UseForRuntime=false
- P15 remains passing

Recommendation：ReadyForHybridFreeze / BlockedByDenseBaselineRegression / BlockedByEligibilityMismatch / BlockedByDedupBug / KeepPreviewOnly。

## Hybrid Retrieval Preview Freeze

V3.11.F 新增 `HybridRetrievalPreviewFreezeRunner` 和 `vector-hybrid-freeze-gate`，只冻结 hybrid preview 结论，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

输出：

- `vector/hybrid/vector-hybrid-freeze-gate.json`
- `vector/hybrid/vector-hybrid-freeze-gate.md`

当前冻结结论：

- `FreezePassed=true`
- `HybridRetrievalStatus=KeepPreviewOnly`
- `Recommendation=BlockedByA3Recall`
- `V4RecheckAllowed=false`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`

解释：

- dense-only 与 legacy dense 对齐，说明 V3.11 framework 没有造成 dense recall regression。
- lexical / anchor 未带来 recall 增益。
- 当前 A3 / Extended recall 仍低于 `80%` V4 gate。
- hybrid preview 不允许替代正式 retrieval source，不允许影响 `PackingPolicy` 或 package output。

## Retrieval Dataset / Query-Corpus Alignment Audit

V3.12 新增 `RetrievalDatasetAlignmentAuditRunner` 和 `vector-retrieval-dataset-alignment-audit`，只审计 eval dataset、query token、indexed corpus 与 provider scope 的对齐情况。不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

CLI：

- `eval vector-retrieval-dataset-alignment-audit`
- `eval vector-retrieval-dataset-alignment-audit-a3`
- `eval vector-retrieval-dataset-alignment-audit-extended`

输出：

- `vector/alignment/vector-retrieval-dataset-alignment-audit-a3.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-extended.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.md`

审计覆盖：

- mustHit 是否存在于当前 indexed corpus。
- mustHit 是否落在当前 provider / model / dimension scope。
- mustHit 是否被 lifecycle / eligibility policy 阻断。
- query 是否具备通用 token / CJK char-bigram anchor。
- query token 与 corpus token 的 overlap。
- sourceTags / itemKind / sourceKind anchor 覆盖率。
- 当前 corpus 与 eval source item 的覆盖差异。

当前结果：

- Summary recommendation：`KeepPreviewOnly`
- `AlignmentIssueCount=50`
- A3：`MustHitPresentInCorpus=66/66`，`ProviderScope=66/66`，`EligibilityBlocks=25`，`QueryTokenCoverage=100.00%`，`QueryCorpusTokenOverlap=72.00%`，`AnchorCoverage=100.00%`
- Extended：`MustHitPresentInCorpus=160/160`，`ProviderScope=160/160`，`EligibilityBlocks=25`，`QueryTokenCoverage=100.00%`，`QueryCorpusTokenOverlap=79.96%`，`AnchorCoverage=100.00%`
- issue breakdown：`MustHitLifecycleFiltered=50`

解释：

- 当前 recall 低并非由 mustHit 缺失 corpus 或 provider scope 不匹配直接导致。
- 主要对齐问题来自 mustHit 在 provider scope 中存在，但会被当前 lifecycle / eligibility policy 阻断。
- 本阶段仅提供 recall source repair 的定位输入；`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`。

## Eligibility Recall Loss Review / Lifecycle-Filtered MustHit Triage

V3.13 新增 `VectorEligibilityRecallLossTriageRunner` 和 `vector-eligibility-recall-loss-triage`，只对 V3.12 识别出的 `MustHitLifecycleFiltered` 样本做 lifecycle / eligibility triage 与 section-routing review。不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

CLI：

- `eval vector-eligibility-recall-loss-triage`
- `eval vector-eligibility-recall-loss-triage-a3`
- `eval vector-eligibility-recall-loss-triage-extended`

输出：

- `vector/eligibility/vector-eligibility-recall-loss-triage-a3.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-extended.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.md`

triage detail 覆盖：

- sample / mode / intent / query text。
- mustHit item id、itemKind、layer、lifecycle、reviewStatus、replacementState。
- sourceRefs / evidenceRefs。
- blocked reason、current target section、candidate target section。
- shouldRemainBlocked、canRouteToAuditOrHistorical、canRepairMetadata。
- triage category、recommended action、rationale。

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

安全结论：

- deprecated / historical / superseded mustHit 不允许直接进入 `normal_context`。
- deprecated mustHit 当前只可 route 到 `audit_context` / diagnostics，不作为 normal recall recovery。
- lifecycle / review / replacement metadata 不足时，先进入 metadata repair，不通过放松 eligibility policy 解决。
- eval label 仅用于 audit report，不进入 `VectorCandidateEligibilityPolicy`。
- `FormalRetrievalAllowed=false`，`UseForRuntime=false`，`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## Lifecycle Metadata Repair Plan for Vector Recall

V3.14 新增 `VectorLifecycleMetadataRepairPlanRunner` 和 `vector-lifecycle-metadata-repair-plan`，只针对 V3.13 中的 `MetadataLifecycleRepairNeeded` 生成 metadata repair preview plan。不处理 `CorrectlyBlockedDeprecated` / historical / superseded 为 normal recovery，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

CLI：

- `eval vector-lifecycle-metadata-repair-plan`
- `eval vector-lifecycle-metadata-repair-plan-a3`
- `eval vector-lifecycle-metadata-repair-plan-extended`

输出：

- `vector/eligibility/vector-lifecycle-metadata-repair-plan-a3.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-extended.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.md`

repair candidate 覆盖：

- sampleId / mustHitItemId。
- current / proposed lifecycle。
- current / proposed reviewStatus。
- current / proposed target section。
- evidenceRefs / sourceRefs。
- provenanceAvailable / relationEvidenceAvailable / reviewEvidenceAvailable。
- repairConfidence、repairReason。
- canAutoRepair、requiresHumanReview、forbiddenReason。

auto-repair 条件：

- runtime provenance 可用。
- reviewStatus 支持 Active / Stable / Current。
- replacementState 非 superseded。
- relation evidence 不显示 deprecated / historical。
- source item 不标记 rejected / deprecated。
- 不使用 sampleId / itemId / fixture / domain lexicon 特判。

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

- 当前没有候选满足自动修复条件。
- 32 个 metadata repair candidate 需要人工补充或确认 provenance / review / replacement evidence。
- `CorrectlyBlockedDeprecated=18` 被跳过，不进入 normal recovery。
- repair plan 不写入任何 store；`FormalRetrievalAllowed=false`，`UseForRuntime=false`，`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## Lifecycle Metadata Review Candidate Foundation

V3.15 在 V3.14 repair plan 之上新增人工 review candidate 层。该层只生成、查询、detail/explain review candidates，不自动 approve/reject，不写 sidecar metadata，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

CLI：

- `eval vector-lifecycle-metadata-review-candidates-generate`
- `eval vector-lifecycle-metadata-review-candidates`

输出：

- `vector/eligibility/vector-lifecycle-metadata-review-candidates.json`
- `vector/eligibility/vector-lifecycle-metadata-review-candidates.md`

生成规则：

- 默认读取 `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.json`。
- 只从 `RequiresHumanReview=true` 的 repair candidate 生成。
- 跳过 `CanAutoRepair=true`、硬 forbidden repair、以及 V3.14 已跳过的 correctly blocked deprecated / historical / superseded。
- candidate id 使用 review candidate 实例的稳定 hash；metadata 保留 `workspaceId|collectionId|mustHitItemId|proposedLifecycle|proposedTargetSection` dedupe key，避免不同 eval 来源被合并丢失。
- 重复生成时稳定 upsert，保留既有 `Status`，刷新 evidence/source/metadata 字段。
- `Status` 默认 `PendingReview`；`ApprovedForSidecar` / `Rejected` / `NeedsEvidence` / `Superseded` 仅作为可表达状态，本阶段不提供决策 mutation。

当前预期结果：

- `CandidateCount=32`
- `PendingCount=32`
- `CorrectlyBlockedSkippedCount=18`
- `Recommendation=NeedsHumanReview`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`

安全结论：

- review candidates 只作为人工 review 队列。
- `RiskIfApproved` 记录 sidecar 未来写入风险；`RiskIfRejected=RecallRemainsBlockedByLifecycleMetadata`。
- `Metadata` 明确标记 `reviewOnly=true`、`runtimeEffect=false`、`sidecarWrite=false`、`trainingUse=disabled_until_review`。
- `VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## V3.16 Vector Lifecycle Metadata Review / Sidecar Apply

V3.16 在 V3.15 review candidate 队列之上增加人工 review 决策和 sidecar apply。该阶段只写 review history 与 lifecycle metadata sidecar override，不修改业务 source item，不放松 eligibility policy，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增能力：

- `VectorLifecycleMetadataReviewRequest` / `Result` / `Record`
- `IVectorLifecycleMetadataReviewStore` 与 InMemory / FileSystem 实现
- `IVectorLifecycleSidecarMetadataStore` 与 InMemory / FileSystem 实现
- `VectorLifecycleMetadataReviewService`
- `eval vector-lifecycle-metadata-review-summary`
- `eval vector-lifecycle-metadata-sidecar-preview`
- `eval vector-lifecycle-metadata-review-smoke`

Review decision 支持 `ApproveForSidecar` / `Reject` / `NeedsEvidence` / `Supersede`。只有 `ApproveForSidecar` 会写 sidecar metadata，且必须带 reviewer、reason、proposed lifecycle / reviewStatus / targetSection、evidenceRefs 或 sourceRefs，以及显式确认。`Rejected` / `NeedsEvidence` / `Supersede` 只写 review history 和 candidate status，不写 sidecar。

安全约束：deprecated / historical / superseded 不允许 approve 到 `normal_context`；`normal_context` 只允许 Active / Current / Stable 且非 superseded。`audit_context` / `historical_context` / `diagnostics_only` 可作为非 normal recovery 的 sidecar 目标。V3.16 sidecar 仅作为未来重评估输入，不自动进入 V4。

## V3.17 Sidecar-aware Eligibility Re-evaluation Preview

V3.17 新增 sidecar-aware eligibility resolver 和 preview/recheck/quality 报告。该阶段只在 preview/eval 中计算 effective lifecycle / reviewStatus / targetSection，不修改 source item，不替换正式 `VectorCandidateEligibilityPolicy`，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-sidecar-eligibility-preview`
- `eval vector-sidecar-eligibility-recheck`
- `eval vector-sidecar-eligibility-quality`

安全规则：

- 无 sidecar 时 effective metadata 与 base eligibility 完全一致。
- ApprovedForSidecar sidecar 可在 preview/eval 中改变 effective target section。
- deprecated / historical / superseded 不允许通过 sidecar 提升到 `normal_context`。
- sidecar 缺 evidence/source refs 或存在冲突时 fail closed。
- 当前真实 ApprovedForSidecar=0 时，报告输出 `NoApprovedSidecarEntries`，不触发 V4。

## V3.18 Human Review Batch Workflow

V3.18 在 PendingReview candidates 之上新增人工 review batch workflow。该阶段只创建 batch、导出 review sheet、导入 reviewer decision、校验决策并生成 apply preview；不自动 approve，不写真实 sidecar，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-lifecycle-metadata-review-batch-create`
- `eval vector-lifecycle-metadata-review-batch-export`
- `eval vector-lifecycle-metadata-review-batch-import`
- `eval vector-lifecycle-metadata-review-batch-validate`
- `eval vector-lifecycle-metadata-review-batch-apply-preview`

输出目录：

- `vector/eligibility/review-batches/{batchId}/batch.json`
- `vector/eligibility/review-batches/{batchId}/review-sheet.jsonl`
- `vector/eligibility/review-batches/{batchId}/review-sheet.md`
- `vector/eligibility/review-batches/{batchId}/import-result.json`
- `vector/eligibility/review-batches/{batchId}/validation-report.json`
- `vector/eligibility/review-batches/{batchId}/apply-preview.json`

Apply preview 仅统计 would-write sidecar entry、unsafe blocked、target section 分布和 effective metadata changed count；`RealSidecarWritten=false`。

## V3.18.3 Ingestion Metadata Contract / Legacy Dataset Limitation Report

V3.18.3 停止继续人工 review 旧 batch，将方向调整为 Retrieval Dataset V2 metadata contract 与 legacy dataset limitation。该阶段只输出 contract、validator 和 limitation 报告；不生成正式数据，不写 sidecar，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-retrieval-dataset-v2-contract`
- `eval vector-retrieval-dataset-v2-validator`
- `eval vector-legacy-dataset-limitation-report`

输出：

- `vector/eligibility/vector-retrieval-dataset-v2-contract.json`
- `vector/eligibility/vector-retrieval-dataset-v2-contract.md`
- `vector/eligibility/vector-retrieval-dataset-v2-validation-report.json`
- `vector/eligibility/vector-retrieval-dataset-v2-validation-report.md`
- `vector/eligibility/vector-legacy-dataset-limitation-report.json`
- `vector/eligibility/vector-legacy-dataset-limitation-report.md`

当前 legacy limitation 结论：

- `ReviewCandidateCount=32`
- `MissingEvidenceSourceProvenanceCandidateCount=32`
- `EvidenceBackfillRecommendation=NeedsIngestionMetadataBackfill`
- `LegacyDatasetSuitableForPrimaryRecallRepair=false`
- `FormalRetrievalAllowed=false`
- `UseForRuntime=false`

Dataset V2 contract 要求 corpus item 和 query sample 在 ingestion 阶段携带 `sourceRefs`、`evidenceRefs`、`provenance`、`split`、lifecycle/reviewStatus/replacementState、targetSection 与 relation evidence。旧 vector eval corpus 可以用于解释 recall loss，但不能安全支撑 lifecycle metadata repair 决策；后续 recall repair 应转向 Dataset V2 ingestion metadata backfill。

## V3.19 LLM-generated Retrieval Dataset V2 Generator

V3.19 新增 Retrieval Dataset V2 离线生成器、确定性 validator 和 quality report。该阶段只生成 preview/confirmed dataset 文件与报告；不接 formal retrieval，不改变 `VectorRetrieval` readiness，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval retrieval-dataset-v2-generate --dry-run`
- `eval retrieval-dataset-v2-generate --confirm`
- `eval retrieval-dataset-v2-validate`
- `eval retrieval-dataset-v2-quality`

输出目录：

- `vector/dataset-v2/generated/generation-report.json/.md`
- `vector/dataset-v2/generated/validation-report.json/.md`
- `vector/dataset-v2/generated/quality-report.json/.md`
- `vector/dataset-v2/generated/corpus.jsonl`（仅 `--confirm` 写出）
- `vector/dataset-v2/generated/samples.jsonl`（仅 `--confirm` 写出）

当前 dry-run 结果：

- `CorpusItemCount=28`
- `SampleCount=21`
- `ValidationIssueCount=0`
- `MissingEvidenceCount=0`
- `MissingProvenanceCount=0`
- `ItemIdLeakageCount=0`
- `RelationInconsistencyCount=0`
- `JudgeWarningCount=0`
- `Recommendation=ReadyForDatasetV2ShadowEval`
- `UseForRuntime=false`

生成规则保持 Dataset V2 contract：mustHit/mustNot 来自同一 generated corpus，queryText 不包含 itemId，rationale 不进入 indexed text，lifecycle trap 必须带 relation evidence。默认 `RetrievalDatasetV2GenerationOptions.Enabled=false`；eval 命令显式启用离线 dry-run，不影响 runtime。

## V3.19.1 Dataset V2 Materialization / Immutability Gate

V3.19.1 将 V3.19 的 generated Dataset V2 从 dry-run 报告推进到显式确认的 artifact 物化。该阶段只写 repo-local dataset 文件、manifest、fingerprint 与 gate report；不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 `VectorRetrieval` readiness，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval retrieval-dataset-v2-generate --confirm`
- `eval retrieval-dataset-v2-materialization-gate`

输出：

- `vector/dataset-v2/generated/corpus.jsonl`
- `vector/dataset-v2/generated/samples.jsonl`
- `vector/dataset-v2/generated/dataset-v2-manifest.json`
- `vector/dataset-v2/generated/materialization-report.json/.md`
- `vector/dataset-v2/generated/materialization-gate.json/.md`

Materialization gate 校验：

- corpus / samples 文件存在。
- 最新 validation report issue count 为 0。
- 最新 quality recommendation 为 `ReadyForDatasetV2ShadowEval`。
- corpus / samples hash 与 manifest 稳定一致。
- missing evidence / provenance / itemId leakage / relation inconsistency 均为 0。
- `UseForRuntime=false`。
- `FormalRetrievalAllowed=false`。

ControlRoom Vector Index 页面新增 Dataset V2 Materialization Summary，展示 datasetId、corpus/sample hash、gate 状态、hash stability 和 recommendation。

## V3.20 Dataset V2 Dense / Hybrid / Eligibility Shadow Eval

V3.20 在 V3.19.1 已物化的 Dataset V2 artifact 上执行 dense / hybrid / eligibility shadow eval。输入固定为：

- `vector/dataset-v2/generated/corpus.jsonl`
- `vector/dataset-v2/generated/samples.jsonl`
- `vector/dataset-v2/generated/dataset-v2-manifest.json`

新增命令：

- `eval retrieval-dataset-v2-shadow-eval`
- `eval retrieval-dataset-v2-dense-shadow-eval`
- `eval retrieval-dataset-v2-hybrid-shadow-eval`
- `eval retrieval-dataset-v2-readiness-gate`

输出：

- `vector/dataset-v2/eval/dataset-v2-dense-shadow-eval.json/.md`
- `vector/dataset-v2/eval/dataset-v2-hybrid-shadow-eval.json/.md`
- `vector/dataset-v2/eval/dataset-v2-shadow-eval-summary.json/.md`
- `vector/dataset-v2/eval/dataset-v2-readiness-gate.json/.md`

覆盖 profile：

- `dense-filesystem-current-provider`
- `dense-pgvector-current-provider`
- `hybrid-dense-only`
- `hybrid-dense-plus-lexical`
- `hybrid-dense-plus-anchor`
- `hybrid-dense-plus-lexical-anchor`
- `lexical-only`
- `anchor-only`

当前结果：

- DatasetId：`rdsv2-9d8678d981f1aac1`
- dense best recall：`80.95%`
- hybrid best profile：`hybrid-dense-plus-anchor`
- hybrid best recall：`100.00%`
- best risk：`0`
- pgvector parity：`true`
- readiness gate：`passed`
- recommendation：`ReadyForDatasetV2RetrievalCandidate`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

说明：当前 pgvector profile 只作为 Dataset V2 shadow/eval parity projection，不绑定正式 `IVectorIndexStore`，不接 formal retrieval，不改变 `PackingPolicy` 或 package output。

## V3.21 Dataset V2 Stress / Holdout / Leakage Audit

V3.21 在独立 stress 数据集上执行 leakage audit、anchor dominance audit、ablation shadow eval 和 stress readiness gate。输出位置为 `vector/dataset-v2/stress/`，不覆盖 V3.19.1 已物化的 generated Dataset V2 artifact。

新增命令：

- `eval retrieval-dataset-v2-stress-generate --dry-run`
- `eval retrieval-dataset-v2-stress-generate --confirm`
- `eval retrieval-dataset-v2-leakage-audit`
- `eval retrieval-dataset-v2-anchor-dominance-audit`
- `eval retrieval-dataset-v2-stress-shadow-eval`
- `eval retrieval-dataset-v2-stress-readiness-gate`

stress 数据集覆盖：

- corpus：`120`
- samples：`120`
- split：train / dev / test / holdout
- difficulty：direct lexical、paraphrase semantic、metadata anchor、relation multi-hop、lifecycle trap、must-not constraint、ambiguous target section、near duplicate、cross-domain distractor、sparse query

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- ValidationIssueCount：`0`
- LeakageIssueCount：`0`
- ItemIdLeakageCount：`0`
- RationaleLeakageCount：`0`
- SplitLeakageCount：`0`
- AnchorDominanceScore：`0.0000`
- HybridRecall：`43.33%`
- HoldoutHybridRecall：`62.50%`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- Recommendation：`BlockedByHoldoutRecall`

结论：stress / holdout audit 发现当前 Dataset V2 stress profile 还不足以通过 holdout recall gate，因此保持 preview/eval only。`UseForRuntime=false`、`FormalRetrievalAllowed=false`，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## V3.22 Dataset V2 Stress Recall Failure Triage

V3.22 对 V3.21 stress / holdout recall failure 做只读 triage。该阶段只解释失败样本，不修复 ranking，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval retrieval-dataset-v2-stress-failure-triage`
- `eval retrieval-dataset-v2-stress-failure-triage-holdout`
- `eval retrieval-dataset-v2-stress-failure-clusters`

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
- Profile comparison：
  - `dense-only=47.50%`
  - `hybrid-full=43.33%`
  - `hybrid-with-anchor-shuffle=47.50%`
  - `hybrid-with-metadata-anchor-removed=47.50%`
  - `anchor-only=27.50%`
- MustHitMissingFromCandidateSetCount：`0`
- EligibilityBlockedCount：`0`
- Recommendation：`NeedsHybridUnionScoringRepair`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：失败不是 corpus/provider scope 缺失，也不是 eligibility block；主要问题是 must-hit 已在候选集合中但未进入 topK，以及 hybrid union scoring 未保留部分 dense-only wins。下一步应做 ranking repair preview，而不是放宽 eligibility 或接 formal retrieval。

## V3.23 Hybrid Union Scoring / Ranking Repair Preview

V3.23 在 Dataset V2 stress artifact 上新增 hybrid union scoring repair preview。该阶段只评估离线 profile，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval retrieval-dataset-v2-hybrid-scoring-repair-preview`
- `eval retrieval-dataset-v2-hybrid-scoring-repair-shadow-eval`
- `eval retrieval-dataset-v2-hybrid-scoring-repair-gate`

scoring profiles：

- `baseline-hybrid-full`
- `dense-preserving-union-v1`
- `dense-winner-floor-v1`
- `negative-distractor-penalty-v1`
- `post-scoring-risk-gated-v1`
- `anchor-score-capped-v1`
- `contribution-aware-rerank-v1`
- `combined-safe-v1`

当前结果：

- DatasetId：`rdsv2-stress-a9f2c86e8d1df488`
- baseline hybrid recall / holdout：`43.33%` / `62.50%`
- dense-preserving / dense-winner-floor recall：`47.50%`
- negative-distractor-penalty recall / holdout：`50.83%` / `75.00%`
- negative-distractor-penalty DenseWinnerLostCount：`0`
- negative-distractor-penalty MustHitBelowTopKCount：`59`
- all repair profiles RiskAfterPolicy：`>0`
- GatePassed：`false`
- Recommendation：`NeedsMoreRankingRepair`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

结论：V3.23 证明 dense winner preservation 和 negative distractor penalty 可以提升 recall / holdout recall，但当前所有 repair profile 仍存在 must-not risk，因此 freeze/stress gate 必须继续阻断。后续应继续做 label-free scoring repair，不能使用 mustHit / mustNot label、sampleId / itemId 特判或 fixture/domain 词表来调排序。

## V3.23.1 Hybrid Scoring Risk Regression Triage

V3.23.1 对 V3.23 best improving profile `negative-distractor-penalty-v1` 的 `RiskAfterPolicy=7` 做只读 triage。该阶段只解释风险来源，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval retrieval-dataset-v2-hybrid-scoring-risk-triage`
- `eval retrieval-dataset-v2-hybrid-scoring-risk-triage-holdout`

输出：

- `vector/dataset-v2/stress/hybrid-scoring-risk-triage.json/.md`
- `vector/dataset-v2/stress/hybrid-scoring-risk-triage-holdout.json/.md`

当前结果：

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

结论：当前风险不是 eligibility order、lifecycle gate 或 targetSection gate 失效；risk projection 与 V3.23 profile risk 对齐。风险集中在 final topK 的 must-not candidate，因此下一步应预览 post-scoring risk gate，并继续保持 label-free，不把 mustHit/mustNot label 作为生产排序特征。

### V3.23.1 Fix：Post-scoring Risk Gate Preview

本修复在离线 hybrid scoring repair preview 中新增 `post-scoring-risk-gated-v1`。该 profile 复用 `negative-distractor-penalty-v1` 的通用打分信号，但在最终 topK 前执行 eval-only risk projection gate，移除 must-not / lifecycle / targetSection risk candidate。该 gate 不接 formal retrieval，不作为生产排序特征，不绑定正式 `IVectorIndexStore`。

修复后结果：

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
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

修复后 risk triage 默认检查 `post-scoring-risk-gated-v1`，输出 `RiskCandidateCount=0`、`Recommendation=ReadyForSafeScoringRepair`。旧的 `negative-distractor-penalty-v1` 仍保留为问题定位 profile，可通过 `--profile negative-distractor-penalty-v1` 复现 V3.23.1 的风险报告。

## V3.24 Dataset V2 Stress Freeze

V3.24 新增 Dataset V2 stress freeze gate。该阶段只汇总既有 materialization / small-set readiness / stress leakage / failure triage / hybrid scoring repair / risk triage 报告，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval retrieval-dataset-v2-stress-freeze-gate`

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
- V4RecheckAllowed：`true`，仅表示可作为 evaluation input
- ReadyForFormalRetrieval：`false`
- FormalRetrievalAllowed：`false`
- Recommendation：`ReadyForV4RecheckInput`

冻结边界：`ReadyForV4RecheckInput` 不等于 `ReadyForFormalRetrieval`。`post-scoring-risk-gated-v1` 不允许直接接入 runtime；未通过 V4 formal readiness gate 前，不允许 formal retrieval switch、正式 `IVectorIndexStore` 绑定、`PackingPolicy` 或 package output integration。

## V4.R Vector Formal Retrieval Readiness Re-evaluation

V4.R 新增 Vector formal retrieval readiness recheck。该阶段只汇总既有 legacy/vector/postgres/provider/hybrid/Dataset V2/runtime-change gate 报告，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-v4-readiness-recheck`

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
- BestPreviewProfile：`post-scoring-risk-gated-v1`
- StressRecall / HoldoutRecall：`50.83%` / `75.00%`
- RiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- ReadyForGuardedFormalPreview：`true`
- ReadyForRuntimeSwitch：`false`
- FormalRetrievalAllowed：`false`

边界：V4.R 通过不等于 runtime switch。本阶段只允许进入下一步 GuardedFormalPreview 设计/评估；正式 retrieval、正式 vector store 绑定、`PackingPolicy` 和 package output 集成仍必须等待后续 formal readiness gate。

## V4.1 Guarded Formal Retrieval Preview

V4.1 新增 guarded formal retrieval preview。该阶段只把 V4.R 选定的 `post-scoring-risk-gated-v1` profile 作为离线候选预览，与当前 formal baseline 做 would-change 对比；不写正式 package，不改变 `PackingPolicy` 输入，不改变 package output，不绑定正式 `IVectorIndexStore`。

新增命令：

- `eval vector-guarded-formal-retrieval-preview`
- `eval vector-guarded-formal-retrieval-preview-gate`

输出：

- `vector/v4/vector-guarded-formal-retrieval-preview.json`
- `vector/v4/vector-guarded-formal-retrieval-preview.md`
- `vector/v4/vector-guarded-formal-retrieval-preview-gate.json`
- `vector/v4/vector-guarded-formal-retrieval-preview-gate.md`

当前结果：

- ProfileName：`post-scoring-risk-gated-v1`
- SampleCount / QueryCount：`120` / `120`
- BaselineCandidateCount：`600`
- PreviewVectorCandidateCount：`600`
- WouldAddCount / WouldRemoveCount / WouldRerankCount：`57` / `57` / `0`
- WouldChangeTargetSectionCount：`0`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- PackingPolicyChanged：`false`
- PackageOutputChanged：`false`
- FormalRetrievalAllowed：`false`
- ReadyForRuntimeSwitch：`false`
- GatePassed：`true`
- Recommendation：`ReadyForShadowPackageComparison`

边界：V4.1 通过只允许进入 Shadow Package Comparison，不允许 runtime switch。正式 retrieval、正式 vector store 绑定、`PackingPolicy` 和 package output integration 继续禁止。

## V4.2 Shadow Package Comparison

V4.2 新增 shadow package comparison。该阶段只构建离线 shadow package envelope，并与当前 formal baseline 做 diff；不写正式 package，不改变 `PackingPolicy`，不改变 package output，不启用 runtime retrieval。

新增命令：

- `eval vector-shadow-package-comparison`
- `eval vector-shadow-package-comparison-gate`

输出：

- `vector/v4/vector-shadow-package-comparison.json`
- `vector/v4/vector-shadow-package-comparison.md`
- `vector/v4/vector-shadow-package-comparison-gate.json`
- `vector/v4/vector-shadow-package-comparison-gate.md`

当前结果：

- ProfileName：`post-scoring-risk-gated-v1`
- SampleCount / QueryCount：`120` / `120`
- CandidateAddCount / CandidateRemoveCount / CandidateUnchangedCount：`57` / `57` / `543`
- SectionChangedCount：`0`
- TokenDeltaTotal / TokenDeltaMax：`55` / `10`
- ConstraintCoverageDelta：`0.0167`
- RelationCoverageDelta：`0.0569`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- PackageOutputChanged / PackingPolicyChanged：`false` / `false`
- ShadowPackageWritten / RuntimeMutated：`false` / `false`
- FormalRetrievalAllowed / ReadyForRuntimeSwitch：`false` / `false`
- GatePassed：`true`
- Recommendation：`ReadyForScopedFormalPreviewOptIn`

边界：V4.2 通过只允许进入后续 scoped formal preview opt-in 评估，不允许 runtime switch。正式 retrieval、正式 vector store 绑定、正式 package 写入、`PackingPolicy` 和 package output integration 继续禁止。

## V4.3 Scoped Formal Preview Opt-in

V4.3 新增 scoped formal preview opt-in。该阶段只允许显式 allowlisted workspace / collection / eval scope 使用 `PreviewOnly` 生成离线 preview package；非 allowlisted scope 必须保持 current formal baseline path。不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package。

新增命令：

- `eval vector-scoped-formal-preview-optin-plan`
- `eval vector-scoped-formal-preview-optin-smoke`
- `eval vector-scoped-formal-preview-optin-gate`

输出：

- `vector/v4/vector-scoped-formal-preview-optin-plan.json`
- `vector/v4/vector-scoped-formal-preview-optin-plan.md`
- `vector/v4/vector-scoped-formal-preview-optin-smoke.json`
- `vector/v4/vector-scoped-formal-preview-optin-smoke.md`
- `vector/v4/vector-scoped-formal-preview-optin-gate.json`
- `vector/v4/vector-scoped-formal-preview-optin-gate.md`

当前结果：

- Mode：`PreviewOnly`
- ProfileName：`post-scoring-risk-gated-v1`
- ScopeCount / AllowlistedScopeCount：`2` / `1`
- NonAllowlistedScopeChecked：`true`
- NonAllowlistedScopeLeakCount：`0`
- PreviewPackageCount / BaselinePackageCount：`120` / `120`
- CandidateAddCount / CandidateRemoveCount：`57` / `57`
- TokenDeltaTotal / TokenDeltaMax：`55` / `10`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- PackageOutputChanged / PackingPolicyChanged：`false` / `false`
- FormalPackageWritten / RuntimeMutated：`false` / `false`
- FormalRetrievalAllowed / ReadyForRuntimeSwitch：`false` / `false`
- GatePassed：`true`
- Recommendation：`ReadyForLimitedFormalPreviewObservation`

边界：V4.3 通过只允许进入 limited formal preview observation；不允许 runtime switch、正式 retrieval、正式 vector store 绑定、正式 package 写入或 `PackingPolicy` / package output integration。

## V4.4 Limited Formal Preview Observation

V4.4 新增 limited formal preview observation。该阶段只对显式 scoped preview opt-in 的结果做多轮 observation 聚合；不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-limited-formal-preview-observation`
- `eval vector-limited-formal-preview-observation-gate`

输出：

- `vector/v4/vector-limited-formal-preview-observation.json`
- `vector/v4/vector-limited-formal-preview-observation.md`
- `vector/v4/vector-limited-formal-preview-observation-gate.json`
- `vector/v4/vector-limited-formal-preview-observation-gate.md`

Gate 条件：

- V4.3 scoped formal preview opt-in gate passed。
- `ObservationRunCount` 达到配置值。
- `RiskAfterPolicy=0`、`MustNotHitRiskAfterPolicy=0`、`LifecycleRiskAfterPolicy=0`。
- `FormalOutputChanged=0`。
- `PackageOutputChanged=false`、`PackingPolicyChanged=false`。
- `FormalPackageWritten=false`、`RuntimeMutated=false`。
- `NonAllowlistedScopeLeakCount=0`。
- `UseForRuntime=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`。

边界：V4.4 通过只允许进入 Formal Preview Freeze；正式 retrieval、正式 store 绑定、正式 package 写入、`PackingPolicy` / package output integration 继续禁止。

## V4.F Formal Preview Freeze

V4.F 新增 formal preview freeze gate。该阶段只冻结 V4 scoped formal preview 的可用状态，不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-formal-preview-freeze-gate`

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

输出状态：

- VectorFormalPreview：`ReadyForScopedOptInPreview`
- AllowedMode：`ScopedPreviewOnly`
- FormalRetrievalAllowed：`false`
- ReadyForRuntimeSwitch：`false`
- UseForRuntime：`false`
- RuntimeSwitchAllowed：`false`

边界：V4.F 通过后也只允许 scoped preview opt-in preview；runtime switch、正式 package 写入、`PackingPolicy` / package output integration、non-allowlisted scope use 均继续禁止。

## V4.5 Explicit Scoped Runtime Experiment Planning

V4.5 新增 explicit scoped runtime experiment planning / dry-run / gate。该阶段只规划显式 scoped runtime experiment 的 dry-run，不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-scoped-runtime-experiment-plan`
- `eval vector-scoped-runtime-experiment-dry-run`
- `eval vector-scoped-runtime-experiment-gate`

Gate 汇总：

- `foundation-release-candidate-gate`
- `foundation-reproducibility-check`
- `service-foundation-freeze-gate`
- `vector-formal-preview-freeze-gate`
- `learning-runtime-change-readiness-gate`
- V4.1 / V4.2 / V4.3 / V4.4 gates

输出：

- `vector/v4/vector-scoped-runtime-experiment-plan.json`
- `vector/v4/vector-scoped-runtime-experiment-plan.md`
- `vector/v4/vector-scoped-runtime-experiment-dry-run.json`
- `vector/v4/vector-scoped-runtime-experiment-dry-run.md`
- `vector/v4/vector-scoped-runtime-experiment-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-gate.md`

边界：V4.5 只允许输出 scope allowlist、preview profile、rollback plan、observation metrics、dry-run package comparison plan 和 forbidden actions。`RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false` 必须保持不变。

## V4.6 Explicit Scoped Runtime Experiment Dry-run Observation

V4.6 新增 scoped runtime experiment dry-run observation / gate。该阶段只聚合显式 scope 的多轮 dry-run 观测，不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-scoped-runtime-experiment-dry-run-observation`
- `eval vector-scoped-runtime-experiment-dry-run-observation-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation.json`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation.md`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation-gate.md`

Gate 条件：

- V4.5 scoped runtime experiment gate passed。
- observation run count 达到配置值。
- `RiskAfterPolicy=0`、`MustNotHitRiskAfterPolicy=0`、`LifecycleRiskAfterPolicy=0`。
- `FormalOutputChanged=0`。
- `FormalPackageWritten=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`。
- `PackingPolicyChanged=false`、`PackageOutputChanged=false`。
- `NonAllowlistedScopeLeakCount=0`。
- rollback plan available。
- `UseForRuntime=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`。

边界：V4.6 通过只允许进入 scoped runtime experiment design freeze；正式 runtime switch、正式 store 绑定、正式 package 写入和 package output mutation 继续禁止。

## V4.7 Scoped Runtime Experiment Design Freeze

V4.7 新增 scoped runtime experiment design freeze gate。该阶段只冻结显式 scoped runtime experiment 的设计边界，不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-scoped-runtime-experiment-design-freeze-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.md`

Gate 汇总：

- `foundation-release-candidate-gate`
- `service-foundation-freeze-gate`
- `vector-formal-preview-freeze-gate`
- `vector-scoped-runtime-experiment-gate`
- `vector-scoped-runtime-experiment-dry-run-observation-gate`
- `learning-runtime-change-readiness-gate`
- P15 gate

冻结状态：

- `ScopedRuntimeExperimentDesign=Frozen`
- `AllowedMode=ExplicitScopedRuntimeExperimentOnly`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`
- `FormalPackageWriteAllowed=false`
- `PackingPolicyIntegrationAllowed=false`
- `GlobalDefaultOnAllowed=false`

边界：V4.7 通过只表示可以进入 selected scope runtime experiment proposal。runtime switch、正式 store 绑定、正式 package 写入、`PackingPolicy` / package output mutation、global default-on 和 non-allowlisted scope use 继续禁止。

## V4.8 Explicit Scoped Runtime Experiment Proposal

V4.8 新增 explicit scoped runtime experiment proposal / config patch preview / approval gate。该阶段只生成 proposal 和 preview artifact，不切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-scoped-runtime-experiment-proposal`
- `eval vector-scoped-runtime-experiment-config-preview`
- `eval vector-scoped-runtime-experiment-proposal-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-proposal.json`
- `vector/v4/vector-scoped-runtime-experiment-proposal.md`
- `vector/v4/vector-scoped-runtime-experiment-config-preview.json`
- `vector/v4/vector-scoped-runtime-experiment-config-preview.md`
- `vector/v4/vector-scoped-runtime-experiment-proposal-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-proposal-gate.md`

Gate 条件：

- foundation-release-candidate-gate passed。
- foundation-reproducibility-check passed。
- service-foundation-freeze-gate passed。
- vector-formal-preview-freeze-gate passed。
- vector-scoped-runtime-experiment-design-freeze-gate passed。
- learning-runtime-change-readiness-gate passed。
- selected workspace / collection / eval scope 显式配置。
- non-allowlisted scope leak 为 0。
- rollback plan、kill switch plan 和 observation metrics 存在。
- `ApprovalRequired=true` 且 `Approved=false`。
- `RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。

边界：V4.8 通过只表示 proposal 可进入人工 approval；它不写 appsettings、不改 DI binding、不写正式 package、不改变 `PackingPolicy` 或 package output。

## V4.9 Scoped Runtime Experiment Approval / No-op Harness

V4.9 在 V4.8 proposal 后新增人工 approval artifact 与 no-op harness gate。

新增 DTO / 服务：

- `ScopedRuntimeExperimentApprovalRecord`
- `ScopedRuntimeExperimentApprovalOptions`
- `IScopedRuntimeExperimentApprovalStore`
- `FileSystemScopedRuntimeExperimentApprovalStore`
- `InMemoryScopedRuntimeExperimentApprovalStore`
- `ScopedRuntimeExperimentNoOpHarnessOptions`
- `ScopedRuntimeExperimentNoOpHarnessReport`

新增 eval：

- `vector-scoped-runtime-experiment-approval-preview`
- `vector-scoped-runtime-experiment-approve`
- `vector-scoped-runtime-experiment-approval-summary`
- `vector-scoped-runtime-experiment-noop-harness`
- `vector-scoped-runtime-experiment-noop-harness-gate`

边界：

- approval mode 只允许 `NoOpHarnessOnly`。
- 没有 `--confirm` 时不写 approval record。
- no-op harness 不改 DI，不绑定正式 `IVectorIndexStore`，不写正式 package。
- `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false` 必须保持不变。

## V4.10 Scoped Runtime Experiment Dry-run Harness Freeze

V4.10 新增 `ScopedRuntimeExperimentHarnessFreezeReport` 和 `eval vector-scoped-runtime-experiment-harness-freeze-gate`。该 gate 只读取 V4.8/V4.9/V4.7、Service Foundation、Foundation RC、runtime-change gate 与 P15 结果，不切 runtime、不绑定正式 `IVectorIndexStore`、不写正式 package。

输出：

- `vector/v4/runtime-experiment/harness-freeze-gate.json`
- `vector/v4/runtime-experiment/harness-freeze-gate.md`

通过条件要求 `ApprovalMode=NoOpHarnessOnly`、approval 存在且未过期/未撤销、no-op harness passed，且 `RuntimeMutated=false`、`VectorStoreBindingChanged=false`、`FormalPackageWritten=false`、`PackingPolicyChanged=false`、`PackageOutputChanged=false`、`FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`RiskAfterPolicy=0`、`FormalOutputChanged=0`。

Freeze 通过后状态为 `ReadyForGuardedRuntimeExperimentPlanning`，只允许进入后续 `GuardedScopedRuntimeExperimentPlan`；它不授权 runtime switch，也不允许把 `NoOpHarnessOnly` 当成 runtime approval。

## V4.11 Guarded Scoped Runtime Experiment Plan

V4.11 新增 `GuardedScopedRuntimeExperimentPlanOptions`、`GuardedScopedRuntimeExperimentPlanReport` 和 `GuardedScopedRuntimeExperimentPlanRunner`。该阶段只生成 guarded scoped runtime experiment plan / activation contract，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-guarded-scoped-runtime-experiment-plan`
- `eval vector-guarded-scoped-runtime-experiment-plan-gate`

输出：

- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.md`

Gate 条件：

- Foundation RC、Service Foundation freeze、Vector Formal Preview freeze、V4.7 design freeze、V4.10 harness freeze 和 runtime-change gate 均已通过。
- selected workspace / collection / eval scope 显式配置。
- `RequiredApprovalMode=ScopedRuntimeExperiment`；现有 `NoOpHarnessOnly` approval 不得算作 runtime approval。
- kill switch plan、rollback plan、observation plan 和 stop conditions 必须存在。
- `RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。
- `MaxRiskCount=0`。

Stop conditions 至少覆盖 risk、must-not risk、lifecycle risk、formal output change、package output change、`PackingPolicy` change、unexpected runtime mutation、non-allowlisted scope leak、error threshold、latency P95 threshold 和 missing trace。

Observation plan 至少覆盖 request/package/candidate/token/risk/formal-output/package/`PackingPolicy`/scope/error/latency/kill-switch/rollback metrics。

边界：V4.11 通过后只表示可以准备后续显式 runtime experiment approval；它不授权 runtime switch、formal retrieval、正式 package write、DI / `IVectorIndexStore` 全局绑定 mutation、`PackingPolicy` mutation、package output mutation 或 global default-on。

## V4.12 Scoped Runtime Experiment Approval Gate

V4.12 新增 scoped runtime experiment approval request preview、runtime approval record 和 approval gate。该阶段只写 `ApprovalMode=ScopedRuntimeExperiment` 的人工审批 artifact，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-scoped-runtime-experiment-approval-request-preview`
- `eval vector-scoped-runtime-experiment-approve-runtime --proposal-id <id> --approved-by <name> --confirm`
- `eval vector-scoped-runtime-experiment-approval-gate`
- `eval vector-scoped-runtime-experiment-approval-summary`

输出：

- `vector/v4/runtime-experiment/runtime-approval-request-preview.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-record.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-gate.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-summary.json/.md`

Approve-runtime 必须提供 `ApprovedBy`、`Reason`、risk / rollback / kill switch / scope / observation plan acknowledgement，并显式 `--confirm`。缺少确认或任一 acknowledgement 时不写 runtime approval record。

Gate 条件：

- V4.11 guarded plan gate passed。
- runtime approval record exists。
- `ApprovalMode=ScopedRuntimeExperiment`。
- approval 未过期、未撤销。
- 所有 acknowledgement 存在。
- selected scope 与 V4.11 plan 一致。
- rollback plan、kill switch plan、observation plan 存在。
- `RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。

边界：V4.12 approval 不等于 runtime switch approval，只允许进入 V4.13 activation preflight。`NoOpHarnessOnly` approval 不能通过 V4.12 gate，runtime-change gate 仍必须阻断 runtime switch / formal retrieval / formal package write。

## V4.13 Activation Preflight + Guarded Runtime Dry-run Route

V4.13 新增 `ScopedRuntimeExperimentActivationPreflightOptions`、`ScopedRuntimeExperimentActivationPreflightReport` 和 `ScopedRuntimeExperimentActivationPreflightRunner`。本阶段只做 activation preflight 与 guarded runtime dry-run route，不真正切换 runtime，不绑定正式 `IVectorIndexStore`，不写正式 package，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增命令：

- `eval vector-scoped-runtime-experiment-activation-preflight`
- `eval vector-scoped-runtime-experiment-dry-run-route`
- `eval vector-scoped-runtime-experiment-activation-gate`

输出：

- `vector/v4/runtime-experiment/activation-preflight.json/.md`
- `vector/v4/runtime-experiment/dry-run-route-report.json/.md`
- `vector/v4/runtime-experiment/activation-gate.json/.md`
- `vector/v4/runtime-experiment/dry-run-route-traces.jsonl`

Gate 条件：

- Foundation RC、Service Foundation freeze、Vector Formal Preview freeze、V4.11 plan gate、V4.12 approval gate 和 runtime-change gate 已通过。
- `ProposalId` / `ApprovalId` 与 V4.11 / V4.12 artifact 匹配。
- `ApprovalMode=ScopedRuntimeExperiment`。
- kill switch、rollback plan、trace sink 均可用。
- selected scope 存在，non-allowlisted scope leak 为 0。
- `RuntimeMutated=false`、`VectorStoreBindingChanged=false`、`FormalPackageWritten=false`、`PackingPolicyChanged=false`、`PackageOutputChanged=false`。
- `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。
- `RiskAfterPolicy=0`、`FormalOutputChanged=0`。

预期状态为 `Recommendation=ReadyForGuardedScopedRuntimeExperiment`。V4.13 的 route trace 明确标记 `DryRunOnly`，不授权 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation 或 package output mutation。

## V4.14 Guarded Scoped Runtime Experiment

V4.14 是第一个真实 scoped runtime experiment，但仍是 shadow/observation experiment。它只允许 explicit allowlisted workspace / collection / eval scope 命中 experiment route；non-allowlisted scope 必须继续走 baseline。正式 retrieval result 不被替换，正式 package output 不改变，不写 formal package，不修改 DI 或全局 `IVectorIndexStore` binding，不修改 `PackingPolicy`，不做 global default-on。

新增：

- `GuardedScopedRuntimeExperimentOptions`
- `GuardedScopedRuntimeExperimentReport`
- `ScopedRuntimeExperimentTrace`
- `GuardedScopedRuntimeExperimentRunner`

新增命令：

- `eval vector-guarded-scoped-runtime-experiment`
- `eval vector-guarded-scoped-runtime-experiment-observation`
- `eval vector-guarded-scoped-runtime-experiment-rollback-smoke`
- `eval vector-guarded-scoped-runtime-experiment-gate`

输出：

- `vector/v4/runtime-experiment/guarded-runtime-experiment-report.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-observation.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-gate.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-rollback-smoke.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-traces.jsonl`

Gate 条件：

- V4.13 activation gate passed。
- V4.12 approval gate passed，且 `ApprovalMode=ScopedRuntimeExperiment`。
- selected scope 显式配置。
- `ExperimentRouteHitCount > 0`。
- `NonAllowlistedScopeLeakCount=0`。
- risk / must-not risk / lifecycle risk 全部为 0。
- `FormalOutputChanged=0`。
- `PackageOutputChanged=false`、`PackingPolicyChanged=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`、`FormalPackageWritten=false`。
- kill switch 可用，rollback smoke 通过。
- `learning-runtime-change-readiness-gate` 仍通过。

预期状态为 `Recommendation=ReadyForScopedRuntimeExperimentObservation`。V4.14 通过后仍不授权 runtime switch、formal retrieval、formal package write、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 或 global default-on。

## V4.15 Scoped Runtime Experiment Observation Window

V4.15 扩展 V4.14 的 scoped shadow runtime experiment observation window。该阶段继续只允许 explicit allowlisted workspace / collection / eval scope 生效，non-allowlisted scope 必须保持 baseline。正式 retrieval result、正式 package output、`PackingPolicy`、DI / 全局 `IVectorIndexStore` binding 均不改变，不写 formal package，不做 global default-on。

新增：

- `ScopedRuntimeExperimentObservationWindowOptions`
- `ScopedRuntimeExperimentObservationWindowReport`
- `ScopedRuntimeExperimentObservationWindowRunner`

新增命令：

- `eval vector-scoped-runtime-experiment-observation-window`
- `eval vector-scoped-runtime-experiment-observation-window-summary`
- `eval vector-scoped-runtime-experiment-observation-window-gate`

输出：

- `vector/v4/runtime-experiment/observation-window.json/.md`
- `vector/v4/runtime-experiment/observation-window-summary.json/.md`
- `vector/v4/runtime-experiment/observation-window-gate.json/.md`
- `vector/v4/runtime-experiment/observation-window-traces.jsonl`

Gate 条件：

- V4.14 guarded scoped runtime experiment gate passed。
- observation run count 和 request count 达到配置下限。
- `ExperimentRouteHitCount > 0`。
- `NonAllowlistedScopeLeakCount=0`。
- risk / must-not risk / lifecycle risk 全部为 0。
- `FormalOutputChanged=0`。
- `PackageOutputChanged=false`、`PackingPolicyChanged=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`、`FormalPackageWritten=false`。
- kill switch available 且 smoke passed。
- rollback verified。
- `TraceCompleteness=100`。
- error count 和 latency P95 不超过阈值。
- P15 与 `learning-runtime-change-readiness-gate` 仍通过。

预期状态为 `Recommendation=ReadyForScopedRuntimeExperimentObservationFreeze`。V4.15 通过后仍不授权 runtime switch、formal retrieval、formal package write、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 或 global default-on。

## V4.16 Scoped Runtime Experiment Observation Freeze / Promotion Decision

V4.16 只冻结 V4.14/V4.15 主线 observation 结论，并输出 promotion decision。该阶段不新增旁支能力，不接 formal retrieval，不切 global runtime，不绑定正式 `IVectorIndexStore`，不写 formal package，不改变 `PackingPolicy` 或 package output。

新增：

- `ScopedRuntimeExperimentObservationFreezeReport`
- `ScopedRuntimeExperimentObservationFreezeRunner`

新增命令：

- `eval vector-scoped-runtime-experiment-observation-freeze`
- `eval vector-scoped-runtime-experiment-promotion-decision`

输出：

- `vector/v4/runtime-experiment/observation-freeze.json/.md`
- `vector/v4/runtime-experiment/promotion-decision.json/.md`

Gate 条件：

- V4.14 guarded scoped runtime experiment gate passed。
- V4.15 observation window gate passed。
- P15 与 `learning-runtime-change-readiness-gate` 仍通过。
- risk / must-not risk / lifecycle risk 全部为 0。
- `FormalOutputChanged=0`。
- `NonAllowlistedScopeLeakCount=0`。
- `TraceCompleteness=100`。
- `FormalPackageWritten=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`、`PackingPolicyChanged=false`、`PackageOutputChanged=false`。
- kill switch 和 rollback 均通过。

通过时 `PromotionDecision=ReadyForFormalRetrievalIntegrationPlan`，但 `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false` 继续保持不变。

## V5.0 Formal Retrieval Integration Plan

V5.0 只规划 formal retrieval 的正式接入点，不实际接入。该阶段不切 runtime，不绑定正式 `IVectorIndexStore`，不写 formal package，不改变 `PackingPolicy` 或 package output。

新增：

- `FormalRetrievalIntegrationPlanReport`
- `FormalRetrievalIntegrationPlanRunner`

新增命令：

- `eval vector-formal-retrieval-integration-plan`
- `eval vector-formal-retrieval-integration-plan-gate`

输出：

- `vector/v5/formal-retrieval-integration-plan.json/.md`
- `vector/v5/formal-retrieval-integration-plan-gate.json/.md`

计划覆盖接入点：

- vector retrieval provider
- `IVectorIndexStore` binding
- package builder / context package assembly
- fallback path
- config switch
- trace / comparison output
- rollback / kill switch

Gate 条件：

- V4.16 promotion decision passed。
- P15 与 `learning-runtime-change-readiness-gate` passed。
- 无 formal output mutation、package output mutation、`PackingPolicy` mutation、DI / vector binding mutation。

通过时 `AllowedMode=PlanOnly`，`RequiredNextPhase=ShadowFormalRetrievalAdapter`，并继续保持 `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`。

## V5.11 Retrieval Evaluation Protocol Freeze / Candidate Source Discriminability Audit

V5.11 只冻结 retrieval eval 口径并审计候选来源区分度。该阶段不接 formal retrieval，不改变 selected set、`PackingPolicy`、package output 或 runtime binding。

新增：

- `RetrievalEvalProtocol`
- `RetrievalEvalProtocolAuditRunner`
- `RetrievalEvalProtocolAuditReport`
- `CandidateSourceDiscriminabilityAuditReport`
- `RetrievalEvalProtocolGateReport`

统一协议字段：

- `VectorTopK`
- `MergedTopK`
- `FinalTopK`
- score threshold
- deterministic tie-break：score desc、source precedence、candidate id ordinal
- train / holdout split

新增命令：

- `eval vector-retrieval-eval-protocol-audit`
- `eval vector-candidate-source-discriminability-audit`
- `eval vector-retrieval-eval-protocol-gate`

输出：

- `vector/v5/retrieval-eval-protocol-audit.json/.md`
- `vector/v5/candidate-source-discriminability-audit.json/.md`
- `vector/v5/retrieval-eval-protocol-gate.json/.md`

审计覆盖：

- 使用同一 `RetrievalEvalProtocol` 复跑 V5.7 / V5.10 baseline 口径，要求 recall / MRR 一致。
- 检查 deterministic tie-break 与 hash/order sensitivity。
- 按 dense、lexical、anchor、evidence/source、relation、metadata 输出 overlap、unique candidate、unique must-hit recovery、marginal recall / MRR。
- 按 split / difficulty 汇总 source overlap 与边际贡献。
- 检测模板同质化和 source non-discriminative 问题。

Gate 条件：

- baseline protocol reproducible。
- tie-break deterministic。
- `HashOrderSensitivityCount=0`。
- 不存在 eval-label scoring / candidate generation。
- risk / must-not risk / lifecycle risk 全部为 0。
- `FormalOutputChanged=0`。
- `FormalPackageWritten=false`、`PackageOutputChanged=false`、`PackingPolicyChanged=false`、`RuntimeMutated=false`、`VectorStoreBindingChanged=false`。
- `FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。

推荐值：

- `ReadyForSourceRepairRecheck`
- `NeedsSourceDiverseDataset`
- `NeedsInputMetadataEnrichment`
- `BlockedByProtocolMismatch`

## V5.12 Input Metadata Enrichment / Source Discriminability Preview

V5.12 只做 metadata enrichment preview。它生成 runtime-observable enriched projection，不写正式 ingestion/source item，不接 formal retrieval，不改变 selected set、`PackingPolicy`、package output 或 runtime binding。

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

Projection 内容：

- canonical source/evidence refs
- provenance record id / source fingerprint
- relation type / direction / confidence metadata
- itemKind / sourceKind
- lifecycle / reviewStatus / targetSection
- query-derived anchors

安全边界：

- enrichment projection 不读取 mustHit / mustNot / expectedTargetSection 来生成 metadata。
- 不使用 sampleId / itemId 特判。
- 不使用 fixture/domain 词表。
- V5.11 固定协议用于 before/after source discriminability 复跑。
- final risk / must-not / lifecycle risk 必须保持 `0`。

当前结果：

- `GatePassed=true`
- `Recommendation=ReadyForSourceRepairRecheck`
- `MetadataCoverageDelta=1680`
- `BeforeRecall=47.50%`
- `AfterRecall=47.50%`
- `IndependentNonDenseSourceCount=3`
- risk / mustNot / lifecycle risk 均为 `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants 均未改变

## V5.13 Enriched Candidate Source Repair Recheck

V5.13 使用 V5.12 enriched projection 重跑 query-driven candidate source repair，判断 enriched metadata 是否能转化为 retrieval quality 提升。本阶段仍然只读，不接 formal retrieval，不改变 selected set、`PackingPolicy`、package output、runtime binding 或 formal package。

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
- train derived MRR `18.44% -> 20.40%`
- holdout derived recall `50.00% -> 41.67%`
- holdout derived MRR `21.81% -> 19.03%`
- must-hit below topK `56 -> 52`
- risk / mustNot / lifecycle risk 均为 `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants 均未改变

结论：enriched metadata 能改变候选排序并改善训练集表现，但当前 source repair 不能稳定泛化到 holdout。下一步应优先做 source-aware ranking / holdout-safe scoring repair，而不是直接 promotion。

## V5.14 Holdout-safe Source-aware Ranking Repair

V5.14 在 V5.11 固定协议和 V5.12 enriched projection 之上新增 holdout-safe source-aware ranking repair。本阶段只做 preview/eval，不接 formal retrieval，不改变 selected set、`PackingPolicy`、package output、runtime binding、formal package 或正式 `IVectorIndexStore` binding。

新增：

- `SourceAwareRankingRepairRunner`
- `SourceAwareRankingRepairReport`
- locked blind holdout manifest / JSONL artifacts

新增 eval：

- `eval vector-source-aware-ranking-repair`
- `eval vector-source-aware-ranking-repair-gate`

输出：

- `vector/v5/source-aware-ranking-repair.json/.md`
- `vector/v5/source-aware-ranking-repair-gate.json/.md`
- `vector/v5/source-aware-ranking-blind-holdout-corpus.jsonl`
- `vector/v5/source-aware-ranking-blind-holdout-samples.jsonl`
- `vector/v5/source-aware-ranking-blind-holdout-manifest.json`

Repair profiles：

- `dense-baseline`
- `normalized-source`
- `confidence-gated`
- `dense-preserving`
- `combined-safe`

安全边界：

- profile 选择只使用 train/dev。
- test、holdout、blind-holdout 只用于不退化 gate。
- source scoring 不读取 mustHit / mustNot、sampleId / itemId 特判或 fixture/domain 词表。
- final risk gate 清除 must-not / lifecycle 风险。
- formal/package/`PackingPolicy`/runtime/vector binding invariants 必须保持 unchanged。

当前结果：

- `GatePassed=true`
- `Recommendation=ReadyForSourceAwareRankingFreeze`
- `SelectedProfile=combined-safe`
- train/dev recall delta `+43.59%`
- test recall delta `+88.89%`
- holdout recall delta `+33.33%`
- blind-holdout recall delta `+4.17%`
- `DenseWinnerLostCount=0`
- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants 均未改变

## V5.15 Output Token Budget / Priority Policy Shadow Gate

V5.15 锁定 V5.14 `combined-safe` profile 与 V5.11 `RetrievalEvalProtocol`，只做 package output policy shadow validation。本阶段构建 shadow package projection，对比 dense baseline 的 token、section occupancy、priority/order、mandatory/hard-constraint coverage、dropped required candidate、section routing mismatch 与 risk 指标；不写 formal package，不改变 formal selected set、`PackingPolicy`、package output、runtime binding 或 vector store binding。

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
- `FormalSelectedSetChanged=false`
- `FormalPackageWritten=false`
- `PackageOutputChanged=false`
- `PackingPolicyChanged=false`
- `RuntimeMutated=false`
- `VectorStoreBindingChanged=false`

结论：`combined-safe` 候选在 shadow package policy 层不触发 token budget、priority inversion、required-candidate drop、section routing 或 risk 阻断。该结果仍为 shadow/eval-only，不授权 formal retrieval、runtime switch 或 package output mutation。

## Formal Adapter Input Contract Enforcement

本阶段固定未来 `ShadowFormalRetrievalAdapter` 进入 formal integration 前允许读取的 runtime input contract。合同只允许读取 request scope、query text/runtime anchors、package context、candidate metadata、lifecycle/review/replacement state、target section、source/evidence refs、provenance、relation evidence 与 provider rank/score 等 runtime-observable 字段。

明确禁止进入 formal adapter runtime input：

- Dataset V2 sample / generated dataset DTO
- must-hit / must-not / expected target section 等 gold label
- split / difficulty / rationale / sample metadata 等 eval-only 字段
- shadow/gate/report artifact 字段

新增：

- `FormalAdapterRuntimeInputEnvelope`
- `FormalAdapterRuntimeCandidateInput`
- `FormalAdapterRuntimePackageContext`
- `FormalAdapterInputContractRunner`
- `FormalAdapterInputContractReport`

新增 eval：

- `eval vector-formal-adapter-input-contract`
- `eval vector-formal-adapter-input-contract-gate`

输出：

- `vector/v5/formal-adapter-input-contract.json/.md`
- `vector/v5/formal-adapter-input-contract-gate.json/.md`

边界：该阶段只做 contract enforcement，不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`，不写 formal package，不改变 `PackingPolicy` 或 package output。

## Formal Retrieval Integration Decision Gate

本阶段新增主线决策 gate，汇总 V5.0-V5.16 的关键结果，判断是否允许进入 `FormalRetrievalIntegrationFreeze / AdapterNoOpBindingPlan`。该 gate 只做决策汇总，不执行 adapter、不绑定正式 `IVectorIndexStore`、不写 formal package、不改变 `PackingPolicy` 或 package output。

汇总前置：

- V5.0 Project State Audit
- V5.0 Formal Retrieval Integration Plan
- V5.1 Shadow Formal Retrieval Adapter Plan
- V5.11 Retrieval Eval Protocol Gate
- V5.12 Input Metadata Enrichment Gate
- V5.13 Enriched Candidate Source Repair Recheck Gate
- V5.14 Source-aware Ranking Repair Gate
- V5.15 Output Token Priority Shadow Gate
- V5.16 Formal Adapter Input Contract Gate
- Runtime-change gate
- P15 gate

V5.13 的 `BlockedByQualityRegression` 可被 V5.14 `ReadyForSourceAwareRankingFreeze` 显式 supersede，但前提是 V5.13 自身 risk / must-not / lifecycle / formal output / package / `PackingPolicy` / runtime / vector binding 不变量全部保持安全。该 supersede 只代表中间 source repair 失败已由后续主线 ranking repair 修复，不授权 formal retrieval。

新增 eval：

- `eval vector-formal-retrieval-integration-decision`
- `eval vector-formal-retrieval-integration-decision-gate`

输出：

- `vector/v5/formal-retrieval-integration-decision.json/.md`
- `vector/v5/formal-retrieval-integration-decision-gate.json/.md`

通过后的含义：

- `ReadyForFormalRetrievalIntegrationFreeze=true`
- `ReadyForAdapterNoOpBindingPlan=true`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `FormalVectorStoreBindingAllowed=false`
- `FormalPackageWriteAllowed=false`

该决策只允许进入 no-op binding plan / freeze 设计，不授权正式 runtime 接入。

## V6.6 Source-diverse Shadow Adapter Delta Validation

本阶段新增 source-diverse shadow adapter validation，用于验证 V6.5 已确认的 shadow expanded pool 在具备 source/evidence/relation/metadata 区分度的 scoped validation set 上能否产生 topK delta。该阶段只运行 shadow validation，不接 formal retrieval，不改变 selected set，不写 formal package，不改变 `PackingPolicy`、package output、runtime 或 vector binding。

新增 eval：

- `eval vector-source-diverse-shadow-adapter-validation`
- `eval vector-source-diverse-shadow-adapter-validation-gate`

输出：

- `vector/v6/source-diverse-shadow-adapter-validation.json/.md`
- `vector/v6/source-diverse-shadow-adapter-validation-gate.json/.md`

Gate 要求 V6.5 delta diagnostics 通过，validation set 具备 source/evidence/relation anchor 区分度，allowlisted scope 使用明确 workspace / collection metadata，`shadowOnlyCount > 0`，hypothetical add/remove 均大于 0，applied add/remove 保持 0，risk / must-not / lifecycle 均为 0，formal/package/`PackingPolicy`/runtime/vector binding 不变量保持 unchanged，并且无 sample/item/domain shortcut。

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

- 新增 `eval vector-shadow-merge-stability-freeze` 与 `eval vector-shadow-merge-promotion-decision`。
- 输入固定来自 V6.6 source-diverse validation、V6.7 shadow candidate merge preview、V6.7 multi-run observation 与 runtime-change gate。
- 冻结条件要求 preview add/remove 多轮稳定、risk/mustNot/lifecycle 为 0、priority/section 不破坏、token delta 在预算内、applied delta 始终为 0。
- 通过后仅允许进入 `ControlledMergeProposal` 规划；`FormalRetrievalAllowed=false`、`RuntimeSwitchAllowed=false`、`ReadyForRuntimeSwitch=false` 保持不变。
- 明确禁止 applied merge、formal selected set mutation、formal package write、PackingPolicy/package output mutation、runtime switch、formal retrieval enable、formal `IVectorIndexStore` binding mutation 与 global default-on。

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
