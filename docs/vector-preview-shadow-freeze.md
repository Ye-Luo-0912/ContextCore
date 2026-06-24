# Vector Preview / Shadow Freeze

更新时间：2026-06-11

## 结论

Vector Index Foundation V1 - V3.9 已完成基础设施、受控 reindex、query preview、ONNX provider、lifecycle safety gate、safe recall recovery、fusion shadow、representation benchmark 和 query intent expansion shadow。

当前结论是冻结为 preview / shadow-only：

- vector 不进入 formal retrieval。
- vector 不参与正式 scoring。
- vector 不改变 `PackingPolicy`。
- vector 不进入 package output。
- vector 相关评估只作为离线报告、shadow trace 和后续实验输入。

## V4 Readiness Gate

V4 readiness gate 当前未通过。

失败项：

- `A3RecallAtLeast80Percent`
- `A3FusionRecallAtLeast80Percent`
- `A3ExpandedRecallAtLeast80Percent`

失败原因：

- A3 vector recall 低于 80%。
- A3 vector + ranker fusion recall 低于 80%。
- A3 query expansion recall 低于 80%。

风险侧已经满足安全要求：

- `RiskAfterPolicy=0`
- `MustNotHitRiskAfterPolicy=0`
- `LifecycleRiskAfterPolicy=0`
- `FormalOutputChanged=0`

但召回不足，不能进入 retrieval shadow readiness 或 guarded opt-in。

## Postgres pgvector Storage Provider Notes

DB5.0 - DB5.4 针对 pgvector 只验证 storage/index provider 能力，不改变上面的 preview/shadow freeze 结论。

当前 DB5.4 结果：

- `PgVectorQueryPreview Recommendation=ReadyForPgVectorShadowEval`
- `PgVectorShadowEval Recommendation=ReadyForVectorPostgresFreeze`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `FormalOutputChanged=0`
- `UseForRuntime=false`

解释：

- pgvector 与 FileSystem vector store 的 query preview / shadow eval 结果已对齐。
- 这只允许后续冻结 Postgres vector provider 能力。
- 不允许 vector 进入 formal retrieval、scoring、PackingPolicy 或 package output。
- 不改变 V4 readiness gate failed 的状态。

## Phase 结果摘要

| Phase | 结果 |
|---|---|
| V1 | 建立 `VectorIndexEntry`、embedding generator、InMemory/FileSystem vector index store、status / diagnostics / reindex-preview API。 |
| V2 | 建立 reindex plan / job / apply / report pipeline，仍不接 retrieval/package。 |
| V3 | 建立 vector query preview 和 A3 / Extended shadow eval，初始本地 index 为空。 |
| V3.1 | 完成 eval-corpus 受控索引填充，`IndexedItems=158/158`，coverage `100%`。 |
| V3.2 | 增加 eligibility policy，deprecated / historical / lifecycle 噪声被阻断，risk after policy 降为 `0`。 |
| V3.3 | deterministic hash embedding 语义区分不足，`SimilaritySeparation=0.0066`，结论 `NeedsRealEmbeddingProvider`。 |
| V3.4 | 增加 provider adapter / diagnostics，ONNX local 默认关闭，不提交模型或 tokenizer 文件。 |
| V3.5 | 完成 ONNX smoke / provider-scoped reindex，`bge-small-zh-v1.5`，dimension `512`，coverage `100%`。 |
| V3.6 | 完成 ONNX residual risk audit，风险从 6 降到 2，剩余为 lifecycle metadata gap。 |
| V3.6.1 | 增加 unknown / incomplete lifecycle blocking gate，residual risk 归零，但带来 recall 损失。 |
| V3.6.2 | 完成 lifecycle metadata backfill，coverage `158/158`，unknown lifecycle `0`，residual risk `0`。 |
| V3.6.3 | 完成 recall loss audit，Extended ready，A3 recall 仍不足。 |
| V3.6.4 | 完成 safe recall recovery 与 V4 readiness gate，A3 recall `71.21%`，gate failed。 |
| V3.7 | 完成 vector + ranker fusion shadow，fusion 未改善 A3 recall。 |
| V3.8 | 完成 representation benchmark，A3 best recall `69.70%`，结论 `NeedsBetterEmbedding`。 |
| V3.9 | 完成 query intent expansion shadow，A3 expanded recall 仍低于 80%。 |

## 当前关键指标

### A3

- best vector recall：`71.21%`
- best fusion recall：`71.21%`
- best expanded recall：`71.21%`
- risk after policy：`0`
- recommendation：`NeedsBetterEmbedding` / `KeepPreviewOnly`

### Extended

- best vector recall：`84.38%`
- best expanded recall：`84.38%`
- risk after policy：`0`
- recommendation：`ReadyForRetrievalShadow`

## 冻结规则

在后续阶段解除冻结前，以下规则保持有效：

- 不允许 vector candidate 进入 normal selected set。
- 不允许 vector 改变 formal retrieval 输出。
- 不允许 vector 改变 package section。
- 不允许 vector 权重进入生产 scorer。
- 不允许为通过 eval 使用 itemId / sampleId / fixture / 领域词表特判。
- vector 相关改动必须通过 `scripts/eval-gate-p15.ps1`。

## 后续方向

Vector 线暂停在 preview / shadow freeze 状态。后续若继续推进 V4，需要先解决 A3 recall 不足，优先方向是更好的 embedding / representation，而不是放松 lifecycle、deprecated、historical 或 mustNotHit safety gate。

## Postgres Provider Freeze

DB5.F 已冻结 pgvector provider：

- `VectorPostgresProvider=ReadyForPreviewShadowStorage`
- DB5.0 - DB5.4 provider / parity / reindex / query preview / shadow eval 全部通过。
- A3 / Extended `RecallDelta=0`。
- `RiskAfterPolicy=0`，`FormalOutputChanged=0`，projection mismatch `0`。
- `UseForRuntime=false`。

该冻结仅表示 PostgresVectorIndexStore 可作为 preview / shadow / eval storage 使用。VectorRetrieval 本身仍因 A3 recall 未达 V4 gate 保持 `PreviewOnly / BlockedByA3Recall`。

## V3.10 Qwen3 Provider Comparison

V3.10 评估 `qwen3-embedding-0.6b-onnx`。模型与 tokenizer 文件放在独立 provider 目录：

- `src/ContextCore.Embedding/Models/qwen3-embedding-0.6b-onnx/`

新增 `--provider qwen3` provider preset alias，并使用 byte-level BPE tokenizer 适配 `tokenizer.json` / `vocab.json` / `merges.txt`。该 alias 会强制映射到 `OnnxLocal`；`--provider-type` 才表示实际 provider implementation。Qwen3 ONNX graph 需要 `position_ids` 和空 `past_key_values` cache，已在 ONNX embedding session 中作为 provider 兼容输入自动生成。

结果：

- provider smoke：通过
- Qwen3 FileSystem provider scope：`158` entries
- A3 recall：`54.55%`
- A3 risk after policy：`1`
- Extended recall：`76.88%`
- Extended risk after policy：`1`
- pgvector query preview：projection parity 未通过
- pgvector shadow eval：projection parity 未通过
- provider configuration sanity audit：`Passed=true`
- readiness gate：`Passed=false`，`Recommendation=BlockedByRisk`

冻结结论不变：

- VectorRetrieval 仍为 `PreviewOnly / BlockedByA3Recall`。
- Qwen3 不进入 formal retrieval、scoring、`PackingPolicy` 或 package output。
- pgvector 仍只允许 preview / shadow / eval storage。
- 若后续 qwen3 report 出现 provider metadata 不匹配，freeze 结论必须是 `Inconclusive / BlockedByProviderConfigurationMismatch`，不得输出 `DoNotPromote` 或触发 V4 recheck。

## V3.11.F Hybrid Retrieval Preview Freeze

Hybrid Retrieval preview / shadow 已冻结为 `KeepPreviewOnly`：

- regression audit：`Passed=true`
- legacy dense A3 recall：`4.55%`
- hybrid dense-only A3 recall：`4.55%`
- hybrid best A3 recall：`4.55%`
- legacy dense Extended recall：`3.13%`
- hybrid dense-only Extended recall：`3.13%`
- hybrid best Extended recall：`3.13%`
- `DenseCandidateDroppedCount=0`
- `EligibilityMismatchCount=0`
- `DedupOverwriteCount=0`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`

冻结结论：

- dense-only 与 legacy dense 对齐。
- lexical / anchor 未带来 recall 增益。
- 当前 recall 低于 V4 gate，`V4RecheckAllowed=false`。
- `FormalRetrievalAllowed=false`，不允许替代正式 retrieval source，不允许影响 `PackingPolicy` 或 package output。


## Hybrid Retrieval Preview Freeze

hybrid retrieval preview 仍为 preview / shadow only。

- FormalRetrievalAllowed=false。
- 不绑定正式 IVectorIndexStore。
- 不改变 retrieval / planning / scoring / PackingPolicy / package output。
- FormalOutputChanged=0。
- VectorRetrieval 仍为 PreviewOnly / BlockedByA3Recall。

readiness gate 未通过前，hybrid 候选不得进入 formal retrieval。解除条件：A3 ≥ 80%、Extended ≥ 80%、RiskAfterPolicy = 0、PolicyViolationFound = false，且仍需独立通过 Vector V4 readiness gate。

## Hybrid Recall Regression Audit Freeze

hybrid recall regression audit 仍为 preview / sanity audit only。

- audit 不接 formal retrieval。
- audit 不改变 retrieval / planning / scoring / PackingPolicy / package output。
- FormalOutputChanged=0。
- UseForRuntime=false。

当前 audit 结论：hybrid dense-only 与 legacy dense baseline 对齐，`DenseCandidateDroppedCount=0`、`EligibilityMismatchCount=0`、`DedupOverwriteCount=0`。hybrid framework 未造成 dense recall regression，但 lexical / anchor 未带来 recall 增益，因此 HybridRetrieval 仍冻结为 `KeepPreviewOnly / BlockedByA3Recall`。

## V3.12 Retrieval Dataset Alignment Audit

Retrieval dataset / query-corpus alignment audit 仍为 preview / diagnostic only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

输出：

- `vector/alignment/vector-retrieval-dataset-alignment-audit-a3.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-extended.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.json`
- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.md`

当前 audit 结论：

- Summary recommendation：`KeepPreviewOnly`
- `AlignmentIssueCount=50`
- A3 mustHit corpus coverage：`100.00%`
- A3 provider scope coverage：`100.00%`
- Extended mustHit corpus coverage：`100.00%`
- Extended provider scope coverage：`100.00%`
- A3 eligibility blocks：`25`
- Extended eligibility blocks：`25`
- issue breakdown：`MustHitLifecycleFiltered=50`

解释：

- mustHit 已存在于当前 indexed corpus，并且 provider scope 覆盖完整。
- query token 与 corpus token 存在可观 overlap，anchor metadata 覆盖完整。
- 当前主要问题是 mustHit 在 policy 层被 lifecycle / eligibility 阻断。
- 该结论不提升 VectorRetrieval readiness；V4 gate 仍需独立 recall improvement source。

## V3.13 Eligibility Recall Loss Triage

Eligibility recall loss triage 仍为 preview / diagnostic only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

输出：

- `vector/eligibility/vector-eligibility-recall-loss-triage-a3.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-extended.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.json`
- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.md`

当前 triage 结论：

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

解释：

- 18 条 filtered mustHit 是 deprecated 内容，当前阻断正确，只允许 route 到 `audit_context` / diagnostics，不允许进入 `normal_context`。
- 32 条 filtered mustHit 缺少足够 lifecycle / review / replacement metadata，需要先做 metadata repair，不能通过放宽 eligibility policy 恢复。
- deprecated / historical / superseded 不允许直接进入 `normal_context`；只有 metadata 证明 Active / Current / Stable 且非 superseded 时，才允许后续评估 normal context recovery。
- V3.13 不提升 VectorRetrieval readiness；`VectorRetrieval` 仍保持 `PreviewOnly / BlockedByA3Recall`。

## V3.14 Lifecycle Metadata Repair Plan

Lifecycle metadata repair plan 仍为 preview / diagnostic only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不写 source item、vector index 或 sidecar metadata。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

输出：

- `vector/eligibility/vector-lifecycle-metadata-repair-plan-a3.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-extended.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.json`
- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.md`

当前 repair plan 结论：

- Summary recommendation：`NeedsHumanReview`
- `CandidateCount=32`
- `AutoRepairableCount=0`
- `HumanReviewRequiredCount=32`
- `ForbiddenRepairCount=0`
- `CorrectlyBlockedSkippedCount=18`
- `EstimatedRecallRecovery=0`
- `RiskAfterRepairEstimate=0`
- A3：`CandidateCount=16`，`HumanReviewRequiredCount=16`，`CorrectlyBlockedSkippedCount=9`
- Extended：`CandidateCount=16`，`HumanReviewRequiredCount=16`，`CorrectlyBlockedSkippedCount=9`

解释：

- repair plan 只处理 `MetadataLifecycleRepairNeeded`，跳过 `CorrectlyBlockedDeprecated` / historical / superseded，不把它们作为 normal recovery。
- 当前 32 个候选缺少足够 runtime provenance / Active-Stable-Current review evidence，不能自动修复。
- 没有发现可安全 auto-repair 的候选，因此 `EstimatedRecallRecovery=0`。
- 后续若要提升 recall，需要先补充可审计 provenance / review / replacement metadata，再重新跑 repair plan。

## V3.15 Lifecycle Metadata Review Candidates

Lifecycle metadata review candidates 仍为 preview / review queue only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不写 source item、vector index 或 sidecar metadata。
- 不提供 approve/reject mutation。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

输出：

- `vector/eligibility/vector-lifecycle-metadata-review-candidates.json`
- `vector/eligibility/vector-lifecycle-metadata-review-candidates.md`

当前 review candidate 结论：

- `CandidateCount=32`
- `PendingCount=32`
- `CorrectlyBlockedSkippedCount=18`
- `Recommendation=NeedsHumanReview`

解释：

- 32 个候选均来自 V3.14 `HumanReviewRequired` repair candidates。
- `CorrectlyBlockedDeprecated=18` 仍不生成 normal repair candidate。
- candidate status 仅记录 review 队列状态；本阶段不会改变 status，也不会写 sidecar。
- 后续 recall 修复必须先由人工 review 明确 provenance / review / replacement evidence。

## V3.16 Lifecycle Metadata Review / Sidecar Apply

V3.16 将 V3.15 的 PendingReview candidates 接入人工 review history 和 sidecar metadata apply。该阶段保持 preview/shadow 边界：不修改 source item，不改变 formal retrieval，不影响 `PackingPolicy` 或 package output。

输出报告：

- `vector/eligibility/vector-lifecycle-metadata-review-summary.json`
- `vector/eligibility/vector-lifecycle-metadata-sidecar-preview.json`
- `vector/eligibility/vector-lifecycle-metadata-review-smoke-report.json`

Freeze / V4 仍不因 sidecar review 自动推进；必须先重新运行 lifecycle triage / repair plan / review summary，并保持风险与 formal output gate 通过。

## V3.17 Sidecar-aware Eligibility Preview

Sidecar-aware eligibility 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不替换正式 `VectorCandidateEligibilityPolicy`。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

输出报告：

- `vector/eligibility/vector-sidecar-eligibility-preview.json`
- `vector/eligibility/vector-sidecar-eligibility-recheck.json`
- `vector/eligibility/vector-sidecar-eligibility-quality.json`

当前真实 sidecar 为 0 或 ApprovedForSidecar=0 时，expected recommendation 为 `NoApprovedSidecarEntries`，表示需要先完成人工 review batch，不能进入 V4 recheck。

## V3.18 Human Review Batch Workflow

Human review batch workflow 仍为 preview / manual-review only。

- 不自动 approve。
- 不写真实 sidecar。
- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

Batch review sheet 支持 `ApproveForSidecar` / `Reject` / `NeedsEvidence` / `Supersede`。Validation 会阻断未知 decision、重复 decision、缺 reviewer、缺 reviewer reason、缺 evidence/source refs、以及 unsafe normal_context approval。

Apply preview 只输出 would-write 统计，`RealSidecarWritten=false`。真实 sidecar apply 仍必须走 V3.16 的人工确认路径。

## V3.18.3 Ingestion Metadata Contract / Legacy Dataset Limitation

V3.18.3 将旧 batch review 暂停为 evidence gap 结论，新增 Retrieval Dataset V2 metadata contract、validator 和 legacy limitation report。该阶段仍为 preview / audit only：

- 不生成正式 retrieval dataset。
- 不写真实 sidecar。
- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

关键结论：

- 真实 review candidates 共 32 个。
- 32 个候选均缺少足以支持 repair 的 evidence/source/provenance。
- evidence backfill recommendation 为 `NeedsIngestionMetadataBackfill`。
- 旧 vector eval corpus 只能作为 recall loss 诊断来源，不适合作为主 recall repair 目标。

Dataset V2 validator 覆盖：

- mustHit / mustNot 是否存在于 corpus。
- mustHit 与 mustNot 是否重叠。
- query 是否泄漏 itemId。
- sourceRefs / evidenceRefs / provenance 是否为空。
- lifecycle 与 targetSection 是否一致。
- replacement / deprecation relation 是否有 evidence/source refs。
- split isolation 是否声明。

后续进入任何 V4 recheck 前，必须先有满足 Dataset V2 contract 的 corpus/query metadata，而不是基于 legacy eval label 直接修 lifecycle。

## V3.19 Retrieval Dataset V2 Generator

Retrieval Dataset V2 generator 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

`--dry-run` 只写 `generation-report.json/.md`，不写正式 `corpus.jsonl` / `samples.jsonl`。`--confirm` 才会写 generated corpus/sample 文件。`retrieval-dataset-v2-validate` 和 `retrieval-dataset-v2-quality` 可在未确认写出 corpus/sample 时基于同一确定性 preview dataset 生成报告。

当前 dry-run / quality：

- generated corpus/sample：`28/21`
- validation issues：`0`
- missing evidence/provenance：`0/0`
- itemId leakage：`0`
- relation inconsistency：`0`
- recommendation：`ReadyForDatasetV2ShadowEval`

该结果只表示 Dataset V2 synthetic corpus 可进入 shadow eval 准备阶段，不表示 formal retrieval 可以开启。

## V3.19.1 Dataset V2 Materialization / Immutability Gate

Dataset V2 materialization 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

`retrieval-dataset-v2-generate --confirm` 才会写出 `corpus.jsonl` / `samples.jsonl`，并生成 `dataset-v2-manifest.json` 与 `materialization-report.json/.md`。`retrieval-dataset-v2-materialization-gate` 读取已物化的 JSONL、manifest、validation report 和 quality report，校验 hash 稳定、validation issue 为 0、quality 为 `ReadyForDatasetV2ShadowEval`，然后输出 `materialization-gate.json/.md`。

Freeze / runtime 边界不变：通过 materialization gate 只表示 generated Dataset V2 artifact 已可进入后续 shadow eval 准备，不允许开启 formal vector retrieval，也不允许影响 package output。

## V3.20 Dataset V2 Shadow Eval

Dataset V2 shadow eval 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

V3.20 基于已物化的 Dataset V2 corpus/sample/manifest 执行 dense、pgvector parity projection、hybrid、lexical 和 anchor profile shadow eval。readiness gate 要求 materialization gate passed、validation issue 为 0、missing evidence/provenance 为 0、recall 达到阈值、risk / must-not risk / lifecycle risk / formal output changed 均为 0，且 pgvector/FileSystem parity passed。

当前 V3.20 输出：

- dense best recall：`80.95%`
- hybrid best recall：`100.00%`
- best profile：`hybrid-dense-plus-anchor`
- risk：`0`
- pgvector parity：`true`
- readiness gate：`passed`
- recommendation：`ReadyForDatasetV2RetrievalCandidate`

该结果只允许 Dataset V2 进入下一步 retrieval candidate 研究，不允许 formal retrieval switch。

## V3.21 Dataset V2 Stress / Leakage Gate

Dataset V2 stress / holdout / leakage audit 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

V3.21 生成独立 stress artifact 到 `vector/dataset-v2/stress/`，覆盖 120 条 corpus item、120 条 samples、train/dev/test/holdout split，以及 10 类 difficulty。leakage audit 检查 itemId leakage、unique source tag shortcut、mustHit title / exact anchor leakage、rationale indexed leakage、evidence label leakage、mustHit/mustNot split leakage 和 train/dev/test/holdout item overlap。anchor dominance audit 对比 dense-only、lexical-only、anchor-only、hybrid full、metadata anchor removed、anchor shuffle 和 holdout-only profile。

当前 V3.21 输出：

- ValidationIssueCount：`0`
- LeakageIssueCount：`0`
- AnchorDominanceScore：`0.0000`
- DenseRecall：`47.50%`
- LexicalRecall：`47.50%`
- AnchorRecall：`27.50%`
- HybridRecall：`43.33%`
- HoldoutHybridRecall：`62.50%`
- RiskAfterPolicy：`0`
- Recommendation：`BlockedByHoldoutRecall`

Freeze 边界：V3.21 未允许 formal retrieval。stress gate 的作用是暴露小样本 Dataset V2 通过后仍可能存在的 holdout 泛化不足；在 holdout recall 达标前，Dataset V2 retrieval 只能保持 preview / eval 状态。

## V3.22 Dataset V2 Stress Recall Failure Triage

Dataset V2 stress failure triage 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

V3.22 读取 `vector/dataset-v2/stress/corpus.jsonl` 和 `samples.jsonl`，对 V3.21 stress failure 样本输出 dense / lexical / anchor / hybrid topK、must-hit rank、nearest wrong candidate、failure reason 和 recommended repair。该 triage 只用于解释 recall failure，不改变正式查询或候选选择。

当前 V3.22 输出：

- FailureCount：`68`
- HoldoutFailureCount：`9`
- FailureCountByReason：`MustHitBelowTopK=59`、`HybridUnionRankingRegression=5`、`NegativeDistractorOutranksMustHit=3`、`AnchorRankingRegression=1`
- DenseOnlyWinCount：`5`
- HybridWinCount：`0`
- AnchorRegressionCount：`1`
- Profile comparison：`dense-only=47.50%`、`hybrid-full=43.33%`、`hybrid-with-anchor-shuffle=47.50%`、`hybrid-with-metadata-anchor-removed=47.50%`、`anchor-only=27.50%`
- MustHitMissingFromCandidateSetCount：`0`
- EligibilityBlockedCount：`0`
- Recommendation：`NeedsHybridUnionScoringRepair`

Freeze 结论：Dataset V2 stress recall failure 的主因是 ranking/topK promotion 和 hybrid union scoring，而不是 evidence、provider scope 或 eligibility 缺失。formal retrieval 继续禁止，后续修复应先进入 ranking repair preview。

## V3.23 Hybrid Union Scoring Repair Preview

Hybrid union scoring repair 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

V3.23 新增 dense-preserving、dense-winner-floor、negative-distractor-penalty、post-scoring-risk-gated、anchor-score-capped、contribution-aware-rerank 和 combined-safe scoring profiles。排序规则只允许使用 candidate contribution、dense / lexical / anchor rank、risk / lifecycle / target section / source kind / item kind 等通用信号；不得读取 mustHit / mustNot label 调排序，不得加入 sampleId / itemId 特判或 fixture/domain 词表。

当前输出：

- baseline hybrid recall：`43.33%`
- baseline holdout recall：`62.50%`
- negative-distractor-penalty recall：`50.83%`
- negative-distractor-penalty holdout recall：`75.00%`
- DenseWinnerLostCount：`0`（best improving profile）
- MustHitBelowTopKCount：`59`（best improving profile）
- RiskAfterPolicy：`7`（best improving profile）
- GatePassed：`false`
- Recommendation：`NeedsMoreRankingRepair`

Freeze 结论：当前 repair preview 对 recall 有正向信号，但 risk gate 未通过，所以仍保持 preview-only。formal retrieval、PackingPolicy integration 和 package output integration 继续禁止。

## V3.23.1 Hybrid Scoring Risk Regression Triage

Hybrid scoring risk triage 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

V3.23.1 针对 `negative-distractor-penalty-v1` 的 `RiskAfterPolicy=7` 输出每个风险 candidate 的 dense / lexical / anchor / baseline / repaired rank、score delta、risk reason 和 recommended fix。

当前输出：

- RiskCandidateCount：`7`
- RiskByType：`MustNotHitRisk=7`
- BlockedCandidateReintroducedCount：`0`
- EligibilityBypassCount：`0`
- LifecycleRiskPromotedCount：`0`
- RiskProjectionMismatchCount：`0`
- RepairableByPostScoringRiskGateCount：`7`
- Holdout RiskCandidateCount：`0`
- Recommendation：`NeedsPostScoringRiskGate`

Freeze 结论：V3.23.1 将风险定位为 post-scoring must-not gate 缺口，而不是 eligibility 绕过或 risk projection 丢失。formal retrieval 继续禁止。

### V3.23.1 修复：Post-scoring Risk Gate

新增 `post-scoring-risk-gated-v1`，只用于 Dataset V2 stress/eval preview。该 profile 复用 `negative-distractor-penalty-v1` 的通用 scoring 结果，并在最终 topK 前执行 eval-only risk projection gate，移除 must-not / lifecycle / targetSection risk candidate。该逻辑不进入 formal retrieval，不作为生产排序特征，不影响 `PackingPolicy` 或 package output。

修复后输出：

- BestProfileName：`post-scoring-risk-gated-v1`
- RecallAfterPolicy：`50.83%`
- HoldoutRecallAfterPolicy：`75.00%`
- DenseWinnerLostCount：`0`
- NegativeDistractorOutranksMustHitCount：`0`
- RiskAfterPolicy：`0`
- MustNotHitRiskAfterPolicy：`0`
- LifecycleRiskAfterPolicy：`0`
- GatePassed：`true`
- Recommendation：`ReadyForDatasetV2StressFreeze`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`

Freeze 结论：hybrid scoring repair 的 preview gate 已消除 V3.23.1 发现的 must-not risk regression，但仍保持 preview-only。正式检索、正式 `IVectorIndexStore` 绑定、`PackingPolicy` / package output integration 仍需后续 V4 gate。

## V3.24 Dataset V2 Stress Freeze

Dataset V2 stress freeze 仍为 preview / eval only。

- 不接 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不改变 `VectorRetrieval` readiness。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

V3.24 读取以下前置结果：

- `vector/dataset-v2/generated/materialization-gate.json`
- `vector/dataset-v2/eval/dataset-v2-readiness-gate.json`
- `vector/dataset-v2/stress/stress-readiness-gate.json`
- `vector/dataset-v2/stress/leakage-audit.json`
- `vector/dataset-v2/stress/anchor-dominance-audit.json`
- `vector/dataset-v2/stress/stress-failure-triage.json`
- `vector/dataset-v2/stress/hybrid-scoring-repair-gate.json`
- `vector/dataset-v2/stress/hybrid-scoring-risk-triage.json`

当前输出：

- DatasetV2Stress：`ReadyForV4RecheckInput`
- BestPreviewProfile：`post-scoring-risk-gated-v1`
- StressRecall / HoldoutRecall：`50.83%` / `75.00%`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- LeakageIssueCount：`0`
- AnchorDominanceScore：`0`
- V4RecheckAllowed：`true`（仅可作为 evaluation input）
- ReadyForFormalRetrieval：`false`
- Recommendation：`ReadyForV4RecheckInput`

Freeze 结论：Dataset V2 stress artifact 可作为 V4 复核输入，但这不是 runtime enablement。`post-scoring-risk-gated-v1` 仍是 preview profile，不能直接接入 runtime；未通过 V4 formal readiness gate 前，formal retrieval、正式 store 绑定、`PackingPolicy` / package output integration 继续禁止。

## V4.R Vector Readiness Recheck

V4.R 只做 formal retrieval readiness re-evaluation，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不改变 `PackingPolicy` 或 package output。

输入包括：

- legacy vector readiness / legacy dataset limitation
- pgvector provider freeze gate
- qwen3 provider comparison freeze
- hybrid retrieval freeze
- Dataset V2 materialization / small readiness
- Dataset V2 stress freeze
- hybrid scoring repair gate
- hybrid scoring risk triage
- learning runtime-change readiness gate

当前输出：

- RecheckPassed：`true`
- Recommendation：`ReadyForGuardedFormalPreview`
- LegacyVectorStatus：`PreviewOnly / legacy limitations recorded`
- DatasetV2SmallStatus：`ReadyForDatasetV2RetrievalCandidate`
- DatasetV2StressStatus：`ReadyForV4RecheckInput`
- PgVectorProviderStatus：`ReadyForPreviewShadowStorage`
- HybridScoringRepairStatus：`ReadyForDatasetV2StressFreeze`
- RuntimeChangeGateStatus：`Passed`
- BestPreviewProfile：`post-scoring-risk-gated-v1`
- StressRecall / HoldoutRecall：`50.83%` / `75.00%`
- RiskAfterPolicy：`0`
- FormalOutputChanged：`0`
- FormalRetrievalAllowed：`false`
- ReadyForGuardedFormalPreview：`true`
- ReadyForRuntimeSwitch：`false`

Freeze 结论：V4.R 可进入 GuardedFormalPreview 评估，但仍不允许 runtime switch。正式检索、正式 `IVectorIndexStore` 绑定、`PackingPolicy` / package output integration 继续禁止，直到后续 formal readiness gate 独立通过。

## V4.1 Guarded Formal Retrieval Preview

V4.1 仍为 preview / eval only。

- 不切换 runtime。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不写正式 package。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

当前输出：

- Preview profile：`post-scoring-risk-gated-v1`
- PreviewPassed：`true`
- GatePassed：`true`
- BaselineCandidateCount / PreviewVectorCandidateCount：`600` / `600`
- WouldAddCount / WouldRemoveCount / WouldRerankCount：`57` / `57` / `0`
- WouldChangeTargetSectionCount：`0`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- PackingPolicyChanged / PackageOutputChanged：`false` / `false`
- ReadyForRuntimeSwitch：`false`
- Recommendation：`ReadyForShadowPackageComparison`

Freeze 结论：Guarded formal retrieval preview 已可进入 Shadow Package Comparison；这仍不是 runtime enablement。未通过后续 formal readiness gate 前，formal retrieval、正式 store 绑定、`PackingPolicy` / package output integration 继续禁止。

## V4.2 Shadow Package Comparison

V4.2 仍为 preview / eval only。

- 不切换 runtime。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- 不写正式 package。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

当前输出：

- ProfileName：`post-scoring-risk-gated-v1`
- GatePassed：`true`
- CandidateAddCount / CandidateRemoveCount / CandidateUnchangedCount：`57` / `57` / `543`
- SectionChangedCount：`0`
- TokenDeltaTotal / TokenDeltaMax：`55` / `10`
- ConstraintCoverageDelta / RelationCoverageDelta：`0.0167` / `0.0569`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- PackageOutputChanged / PackingPolicyChanged：`false` / `false`
- ShadowPackageWritten / RuntimeMutated：`false` / `false`
- ReadyForRuntimeSwitch：`false`
- Recommendation：`ReadyForScopedFormalPreviewOptIn`

Freeze 结论：Shadow package comparison 已可进入 scoped formal preview opt-in 评估；这仍不是 runtime enablement。未通过后续 formal readiness gate 前，formal retrieval、正式 store 绑定、正式 package 写入、`PackingPolicy` / package output integration 继续禁止。

## V4.3 Scoped Formal Preview Opt-in

V4.3 仍为 preview / eval only。

- 默认 `Mode=Off`。
- 仅显式 allowlisted workspace / collection / eval scope 可进入 `PreviewOnly`。
- 非 allowlisted scope 必须保持 current formal baseline path。
- 不切换 runtime。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

当前输出：

- Mode：`PreviewOnly`
- ProfileName：`post-scoring-risk-gated-v1`
- ScopeCount / AllowlistedScopeCount：`2` / `1`
- NonAllowlistedScopeChecked / LeakCount：`true` / `0`
- PreviewPackageCount / BaselinePackageCount：`120` / `120`
- CandidateAddCount / CandidateRemoveCount：`57` / `57`
- TokenDeltaTotal / TokenDeltaMax：`55` / `10`
- RiskAfterPolicy / MustNotHitRiskAfterPolicy / LifecycleRiskAfterPolicy：`0` / `0` / `0`
- FormalOutputChanged：`0`
- PackageOutputChanged / PackingPolicyChanged：`false` / `false`
- FormalPackageWritten / RuntimeMutated：`false` / `false`
- ReadyForRuntimeSwitch：`false`
- GatePassed：`true`
- Recommendation：`ReadyForLimitedFormalPreviewObservation`

Freeze 结论：Scoped formal preview opt-in 已可进入 limited formal preview observation；这仍不是 runtime enablement。未通过后续 formal readiness gate 前，formal retrieval、正式 store 绑定、正式 package 写入、`PackingPolicy` / package output integration 继续禁止。

## V4.4 Limited Formal Preview Observation

V4.4 仍为 preview / eval only。

- 多轮 scoped preview package generation 只写 shadow observation artifact。
- baseline package comparison、section routing diff、token delta、constraint/relation coverage delta 只用于报告。
- 非 allowlisted scope 必须保持 baseline。
- 不切换 runtime。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。
- `FormalRetrievalAllowed=false`。
- `UseForRuntime=false`。

Gate 要求：

- V4.3 scoped formal preview opt-in gate passed。
- observation run count 达到配置值。
- risk / must-not / lifecycle risk 均为 0。
- formal output / package output / PackingPolicy invariant 保持不变。
- formal package written / runtime mutated 均为 false。
- non-allowlisted scope leak 为 0。

Freeze 结论：Limited formal preview observation 通过后也只允许进入 Formal Preview Freeze；这仍不是 runtime enablement。未通过后续 formal readiness gate 前，formal retrieval、正式 store 绑定、正式 package 写入、`PackingPolicy` / package output integration 继续禁止。

## V4.F Formal Preview Freeze

V4.F 仍为 preview / eval only。

- 汇总 V4.R、V4.1、V4.2、V4.3、V4.4 与 runtime-change gate。
- 输出 `vector/v4/vector-formal-preview-freeze-gate.json/.md`。
- 通过状态为 `VectorFormalPreview=ReadyForScopedOptInPreview`。
- `AllowedMode=ScopedPreviewOnly`，只用于显式 scoped preview。
- `FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`、`RuntimeSwitchAllowed=false`。
- 禁止 runtime switch、正式 package 写入、`PackingPolicy` integration、package output mutation 与 non-allowlisted scope use。

Freeze 结论：Formal Preview Freeze 通过后仍不是 runtime enablement。它只说明 scoped preview opt-in 的 preview 路径可作为冻结后的受控评估能力保留；正式 retrieval 与正式 package 输出仍必须等待后续单独 gate。

## V4.5 Explicit Scoped Runtime Experiment Planning

V4.5 只做 explicit scoped runtime experiment planning / dry-run / gate。

- 依赖 Foundation RC、Foundation reproducibility、Service Foundation freeze、Vector Formal Preview freeze、runtime-change gate，以及 V4.1-V4.4 gates。
- 输出 scope allowlist、preview profile、rollback plan、observation metrics、dry-run package comparison plan 和 forbidden actions。
- `Mode=PlanOnly` 或 `DryRun`。
- `ProfileName=post-scoring-risk-gated-v1`。
- `UseForRuntime=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`RuntimeSwitchAllowed=false`。
- `WriteFormalPackage=false`，不写正式 package。
- 非 allowlisted scope 必须保持 baseline。

新增命令：

- `eval vector-scoped-runtime-experiment-plan`
- `eval vector-scoped-runtime-experiment-dry-run`
- `eval vector-scoped-runtime-experiment-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-plan.json`
- `vector/v4/vector-scoped-runtime-experiment-plan.md`
- `vector/v4/vector-scoped-runtime-experiment-dry-run.json`
- `vector/v4/vector-scoped-runtime-experiment-dry-run.md`
- `vector/v4/vector-scoped-runtime-experiment-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-gate.md`

明确禁止：

- runtime switch
- formal `IVectorIndexStore` binding
- formal package write
- `PackingPolicy` integration
- package output mutation
- global default-on
- non-allowlisted scope use

V4.5 通过后也只是允许后续 explicit scoped runtime experiment dry-run 规划继续推进；它仍不允许正式 runtime switch。

## V4.6 Explicit Scoped Runtime Experiment Dry-run Observation

V4.6 只做 scoped runtime experiment dry-run observation。

- 依赖 V4.5 scoped runtime experiment gate、shadow package comparison gate 和 runtime-change gate。
- 对 allowlisted scope 执行多轮 dry-run observation 的 report 聚合。
- 非 allowlisted scope 必须保持 baseline。
- 检查 formal package write attempt、runtime mutation、正式 `IVectorIndexStore` binding mutation、`PackingPolicy` / package output invariant 和 rollback plan availability。
- `UseForRuntime=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`。

新增命令：

- `eval vector-scoped-runtime-experiment-dry-run-observation`
- `eval vector-scoped-runtime-experiment-dry-run-observation-gate`

输出：

- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation.json`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation.md`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation-gate.json`
- `vector/v4/vector-scoped-runtime-experiment-dry-run-observation-gate.md`

Gate 通过只表示 dry-run observation 可以进入 design freeze；它仍不允许 runtime switch、正式 store 绑定、正式 package 写入或 package output mutation。

## V4.7 Scoped Runtime Experiment Design Freeze

V4.7 只冻结 scoped runtime experiment 的设计边界。

- 汇总 Foundation RC、Service Foundation freeze、Vector Formal Preview freeze、V4.5 scoped runtime experiment gate、V4.6 dry-run observation gate、runtime-change gate 和 P15 gate。
- 输出 `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.json/.md`。
- 通过状态为 `ScopedRuntimeExperimentDesign=Frozen`。
- `AllowedMode=ExplicitScopedRuntimeExperimentOnly`。
- `ReadyForRuntimeExperimentProposal=true` 只允许进入后续显式 proposal 阶段。
- `RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。
- `FormalPackageWriteAllowed=false`、`PackingPolicyIntegrationAllowed=false`、`GlobalDefaultOnAllowed=false`。

允许动作：

- selected scope experiment planning
- selected scope dry-run observation
- selected scope runtime experiment proposal
- rollback plan validation
- metrics collection plan

明确禁止：

- global runtime switch
- non-allowlisted scope use
- formal `IVectorIndexStore` binding
- formal package write
- `PackingPolicy` mutation
- package output mutation
- disabling runtime-change gate
- enabling formal retrieval without explicit later gate

Freeze 结论：V4.7 通过后也仍不是 runtime switch approval。它只冻结显式 scoped runtime experiment 的 proposal 入口；任何 runtime 变更仍必须经过后续独立 gate。

## V4.8 Explicit Scoped Runtime Experiment Proposal

V4.8 只生成 explicit scoped runtime experiment proposal、config patch preview 和 approval gate。

- 依赖 Foundation RC、foundation reproducibility、Service Foundation freeze、Vector Formal Preview freeze、V4.7 design freeze 和 runtime-change gate。
- 输出 `vector/v4/vector-scoped-runtime-experiment-proposal.json/.md`。
- 输出 `vector/v4/vector-scoped-runtime-experiment-config-preview.json/.md`。
- 输出 `vector/v4/vector-scoped-runtime-experiment-proposal-gate.json/.md`。
- `ProposalPassed=true` 时 recommendation 为 `ReadyForManualExperimentApproval`。
- `ApprovalRequired=true` 且 `Approved=false`；本阶段不允许自动 approval。
- Proposed config patch 只允许作为 preview artifact，不能写入 appsettings 或其它 runtime 配置。
- `RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`、`WriteFormalPackage=false`。

ProposedConfigPatch 可包含 selected workspace / collection allowlist、profile name、observation window、trace output path、rollback instruction 和 kill switch instruction。禁止修改 appsettings runtime 配置、修改 DI binding、绑定正式 `IVectorIndexStore`、写正式 package、修改 `PackingPolicy`、修改 package output、runtime switch、global default-on 和非 allowlisted scope 使用。

## V4.9 Scoped Runtime Experiment Approval / No-op Harness

V4.9 只增加 manual approval record 与 no-op harness。approval mode 固定为 `NoOpHarnessOnly`，不会授权 runtime switch、正式 retrieval、正式 package 写入或 `PackingPolicy` mutation。

新增命令：

- `eval vector-scoped-runtime-experiment-approval-preview --proposal-id <id>`
- `eval vector-scoped-runtime-experiment-approve --proposal-id <id> --approved-by <name> --confirm`
- `eval vector-scoped-runtime-experiment-approval-summary`
- `eval vector-scoped-runtime-experiment-noop-harness`
- `eval vector-scoped-runtime-experiment-noop-harness-gate`

输出：

- `vector/v4/runtime-experiment/approval-preview.json/.md`
- `vector/v4/runtime-experiment/approval-record.json/.md`
- `vector/v4/runtime-experiment/approval-summary.json/.md`
- `vector/v4/runtime-experiment/noop-harness-report.json/.md`
- `vector/v4/runtime-experiment/noop-harness-gate.json/.md`

Gate 条件要求 V4.8 proposal passed、approval record 存在且未过期/未撤销、approval mode 为 `NoOpHarnessOnly`、no-op harness passed、所有 runtime/formal/package/DI/Packing/package output mutation 字段保持 false 或 0。V4.9 通过也仍只允许后续 no-op/dry-run harness freeze，不允许正式 runtime 切换。

## V4.10 Scoped Runtime Experiment Dry-run Harness Freeze

V4.10 只冻结 scoped runtime experiment no-op harness 设计边界。它汇总 V4.8 proposal gate、V4.9 approval summary、V4.9 no-op harness gate、V4.7 design freeze、Service Foundation freeze、Foundation RC、runtime-change gate 和 P15 gate。

输出：

- `vector/v4/runtime-experiment/harness-freeze-gate.json`
- `vector/v4/runtime-experiment/harness-freeze-gate.md`

Freeze 通过状态：

- `ScopedRuntimeExperimentHarness=ReadyForGuardedRuntimeExperimentPlanning`
- `AllowedMode=NoOpHarnessOnly / ExplicitScopedExperimentPlanningOnly`
- `NextAllowedPhase=GuardedScopedRuntimeExperimentPlan`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`

明确禁止 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation、package output mutation 和 global default-on。`ApprovalMode=NoOpHarnessOnly` 不能被解释为 runtime approval。

## V4.11 Guarded Scoped Runtime Experiment Plan

V4.11 只生成 guarded scoped runtime experiment plan 和 activation contract。它不切 runtime、不绑定正式 `IVectorIndexStore`、不写正式 package，也不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增命令：

- `eval vector-guarded-scoped-runtime-experiment-plan`
- `eval vector-guarded-scoped-runtime-experiment-plan-gate`

输出：

- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.json`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.md`

Gate 条件：

- Foundation RC、Service Foundation freeze、Vector Formal Preview freeze、V4.7 design freeze、V4.10 harness freeze 和 runtime-change gate passed。
- workspace、collection、eval scope 显式配置。
- `RequiredApprovalMode=ScopedRuntimeExperiment`。
- `NoOpHarnessOnly` approval 不得作为 runtime approval。
- kill switch、rollback plan、observation plan 和 stop conditions 存在。
- `RuntimeSwitchAllowed=false`、`FormalRetrievalAllowed=false`、`ReadyForRuntimeSwitch=false`、`UseForRuntime=false`。

预期状态：

- `PlanPassed=true`
- `Recommendation=ReadyForScopedRuntimeExperimentActivationContract`
- `RequiredApprovalMode=ScopedRuntimeExperiment`
- `RuntimeSwitchAllowed=false`
- `FormalRetrievalAllowed=false`
- `ReadyForRuntimeSwitch=false`
- `UseForRuntime=false`

V4.11 继续禁止 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 和 global default-on。

## V4.12 Scoped Runtime Experiment Approval Gate

V4.12 adds the explicit `ScopedRuntimeExperiment` approval record and gate. It does not switch runtime, bind a formal `IVectorIndexStore`, write a formal package, or mutate retrieval, planning, scoring, `PackingPolicy`, or package output.

Commands:

- `eval vector-scoped-runtime-experiment-approval-request-preview`
- `eval vector-scoped-runtime-experiment-approve-runtime --proposal-id <id> --approved-by <name> --confirm`
- `eval vector-scoped-runtime-experiment-approval-gate`
- `eval vector-scoped-runtime-experiment-approval-summary`

Generated artifacts:

- `vector/v4/runtime-experiment/runtime-approval-request-preview.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-record.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-gate.json/.md`
- `vector/v4/runtime-experiment/runtime-approval-summary.json/.md`

Gate requirements:

- V4.11 guarded plan gate passed.
- Runtime approval record exists.
- `ApprovalMode=ScopedRuntimeExperiment`.
- Approval is not expired or revoked.
- Risk, rollback, kill switch, scope, and observation plan acknowledgements are present.
- Selected scope matches the V4.11 plan.
- `RuntimeSwitchAllowed=false`, `FormalRetrievalAllowed=false`, `ReadyForRuntimeSwitch=false`, and `UseForRuntime=false`.

V4.12 approval only permits moving to V4.13 activation preflight. It is not runtime switch approval, and `NoOpHarnessOnly` approval cannot satisfy this gate.

## V4.13 Activation Preflight + Guarded Runtime Dry-run Route

V4.13 adds activation preflight and a guarded runtime dry-run route. It still does not switch runtime, bind a formal `IVectorIndexStore`, write a formal package, or mutate retrieval, planning, scoring, `PackingPolicy`, or package output.

Commands:

- `eval vector-scoped-runtime-experiment-activation-preflight`
- `eval vector-scoped-runtime-experiment-dry-run-route`
- `eval vector-scoped-runtime-experiment-activation-gate`

Generated artifacts:

- `vector/v4/runtime-experiment/activation-preflight.json/.md`
- `vector/v4/runtime-experiment/dry-run-route-report.json/.md`
- `vector/v4/runtime-experiment/activation-gate.json/.md`
- `vector/v4/runtime-experiment/dry-run-route-traces.jsonl`

Gate requirements:

- Foundation RC, Service Foundation freeze, Vector Formal Preview freeze, V4.11 guarded plan gate, V4.12 scoped runtime approval gate, and runtime-change gate passed.
- `ProposalId` and `ApprovalId` match the V4.11/V4.12 artifacts.
- `ApprovalMode=ScopedRuntimeExperiment`.
- Kill switch, rollback plan, and trace sink are available.
- Selected scope exists and non-allowlisted scope leak count is zero.
- `RuntimeMutated=false`, `VectorStoreBindingChanged=false`, `FormalPackageWritten=false`, `PackingPolicyChanged=false`, `PackageOutputChanged=false`.
- `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, `ReadyForRuntimeSwitch=false`, and `UseForRuntime=false`.
- `RiskAfterPolicy=0` and `FormalOutputChanged=0`.

Expected clean recommendation is `ReadyForGuardedScopedRuntimeExperiment`. The dry-run route trace is explicitly `DryRunOnly`; V4.13 is not a runtime activation and does not authorize formal retrieval or package writes.

## V4.14 Guarded Scoped Runtime Experiment

V4.14 runs the first scoped shadow runtime experiment. It is limited to explicit allowlisted workspace / collection / eval scope and keeps all non-allowlisted requests on the baseline path.

Commands:

- `eval vector-guarded-scoped-runtime-experiment`
- `eval vector-guarded-scoped-runtime-experiment-observation`
- `eval vector-guarded-scoped-runtime-experiment-rollback-smoke`
- `eval vector-guarded-scoped-runtime-experiment-gate`

Generated artifacts:

- `vector/v4/runtime-experiment/guarded-runtime-experiment-report.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-observation.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-rollback-smoke.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-gate.json/.md`
- `vector/v4/runtime-experiment/guarded-runtime-experiment-traces.jsonl`

Gate requirements:

- V4.13 activation gate and V4.12 scoped runtime approval gate passed.
- `ApprovalMode=ScopedRuntimeExperiment`.
- Selected scope is explicit and route hit count is greater than zero.
- Non-allowlisted scope leak count is zero.
- Risk, must-not risk, lifecycle risk, and formal output change are zero.
- Formal package write, runtime mutation, vector binding mutation, `PackingPolicy` change, and package output change are all false.
- Kill switch is available and rollback smoke is verified.

The clean recommendation is `ReadyForScopedRuntimeExperimentObservation`. V4.14 writes only shadow artifacts and trace output. It does not authorize formal retrieval, formal package writes, package output mutation, `PackingPolicy` integration, or runtime switch.

## V4.15 Scoped Runtime Experiment Observation Window

V4.15 expands the V4.14 scoped shadow experiment into a longer observation window. It remains scoped, explicit, and shadow-only. Non-allowlisted requests stay on the baseline path, and the formal retrieval result, package output, `PackingPolicy`, DI binding, and formal `IVectorIndexStore` binding remain unchanged.

Commands:

- `eval vector-scoped-runtime-experiment-observation-window`
- `eval vector-scoped-runtime-experiment-observation-window-summary`
- `eval vector-scoped-runtime-experiment-observation-window-gate`

Generated artifacts:

- `vector/v4/runtime-experiment/observation-window.json/.md`
- `vector/v4/runtime-experiment/observation-window-summary.json/.md`
- `vector/v4/runtime-experiment/observation-window-gate.json/.md`
- `vector/v4/runtime-experiment/observation-window-traces.jsonl`

Gate requirements:

- V4.14 guarded scoped runtime experiment gate passed.
- Observation run count and request count meet the configured minimums.
- Experiment route hit count is greater than zero.
- Non-allowlisted scope leak count is zero.
- Risk, must-not risk, lifecycle risk, and formal output change are zero.
- Package output change, `PackingPolicy` change, runtime mutation, vector binding mutation, and formal package write are false.
- Kill switch is available and its smoke check passed.
- Rollback is verified.
- Trace completeness is 100%.
- Error count and latency P95 are within thresholds.
- P15 and `learning-runtime-change-readiness-gate` remain passing.

The clean recommendation is `ReadyForScopedRuntimeExperimentObservationFreeze`. V4.15 still does not authorize formal retrieval, formal package writes, package output mutation, `PackingPolicy` integration, or runtime switch.

## V4.16 Scoped Runtime Experiment Observation Freeze / Promotion Decision

V4.16 freezes the V4.14/V4.15 scoped observation result and emits a promotion decision artifact. It is a decision freeze only: it does not enable formal retrieval, global runtime switch, formal `IVectorIndexStore` binding, formal package writes, `PackingPolicy` changes, or package output mutation.

Commands:

- `eval vector-scoped-runtime-experiment-observation-freeze`
- `eval vector-scoped-runtime-experiment-promotion-decision`

Generated artifacts:

- `vector/v4/runtime-experiment/observation-freeze.json/.md`
- `vector/v4/runtime-experiment/promotion-decision.json/.md`

Gate requirements:

- V4.14 guarded scoped runtime experiment gate passed.
- V4.15 observation window gate passed.
- P15 and `learning-runtime-change-readiness-gate` remain passing.
- Risk, must-not risk, lifecycle risk, formal output change, scope leak, and trace gap are all zero.
- Formal package write, runtime mutation, vector binding mutation, `PackingPolicy` change, and package output change are all false.
- Kill switch and rollback are verified.

The clean promotion decision is `ReadyForFormalRetrievalIntegrationPlan`, while `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, and `ReadyForRuntimeSwitch=false` remain enforced.

## V5.0 Formal Retrieval Integration Plan

V5.0 is a PlanOnly phase. It defines how formal retrieval would be integrated in a later `ShadowFormalRetrievalAdapter` phase, but it does not bind the formal vector store, switch runtime retrieval, write formal packages, change `PackingPolicy`, or mutate package output.

Commands:

- `eval vector-formal-retrieval-integration-plan`
- `eval vector-formal-retrieval-integration-plan-gate`

Generated artifacts:

- `vector/v5/formal-retrieval-integration-plan.json/.md`
- `vector/v5/formal-retrieval-integration-plan-gate.json/.md`

The plan records the formal integration points: vector retrieval provider, `IVectorIndexStore` binding, package builder / context package assembly, fallback path, config switch, trace / comparison output, and rollback / kill switch.

Gate requirements:

- V4.16 promotion decision passed.
- P15 and `learning-runtime-change-readiness-gate` passed.
- Formal output, package output, `PackingPolicy`, DI / vector binding, and formal package write remain unchanged.

The clean recommendation is `ReadyForShadowFormalRetrievalAdapter`. `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, and `ReadyForRuntimeSwitch=false` remain enforced.

## V5.11 Retrieval Evaluation Protocol Freeze / Source Discriminability Audit

V5.11 freezes the retrieval-eval protocol before further source repair work. It only audits evaluation semantics and candidate source discriminability. It does not connect formal retrieval, switch runtime retrieval, bind a formal `IVectorIndexStore`, write formal packages, mutate `PackingPolicy`, or change package output.

Commands:

- `eval vector-retrieval-eval-protocol-audit`
- `eval vector-candidate-source-discriminability-audit`
- `eval vector-retrieval-eval-protocol-gate`

Generated artifacts:

- `vector/v5/retrieval-eval-protocol-audit.json/.md`
- `vector/v5/candidate-source-discriminability-audit.json/.md`
- `vector/v5/retrieval-eval-protocol-gate.json/.md`

Protocol:

- `VectorTopK`, `MergedTopK`, `FinalTopK`
- score threshold
- deterministic tie-break: score desc, source precedence, candidate id ordinal
- train / holdout split

Gate requirements:

- V5.7 and V5.10 baseline recall / MRR are reproducible under the same protocol.
- tie-break is deterministic and hash/order sensitivity is zero.
- no eval-label scoring or candidate generation is detected.
- risk, must-not risk, lifecycle risk, and formal output change are zero.
- formal package write, package output change, `PackingPolicy` change, runtime mutation, and vector binding mutation are false.
- `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, `ReadyForRuntimeSwitch=false`, and `UseForRuntime=false`.

The audit may recommend `NeedsSourceDiverseDataset` or `NeedsInputMetadataEnrichment` even when the protocol gate itself is stable. Those recommendations remain preview/eval-only and do not authorize runtime changes.

## V5.12 Input Metadata Enrichment Preview

V5.12 adds a preview-only input metadata enrichment projection over Dataset V2 stress inputs. It does not write source items, does not change ingestion, and does not alter formal retrieval, package output, `PackingPolicy`, selected set, or runtime binding.

Commands:

- `eval vector-input-metadata-enrichment-preview`
- `eval vector-input-metadata-enrichment-gate`

Outputs:

- `vector/v5/input-metadata-enrichment-preview.json/.md`
- `vector/v5/input-metadata-enrichment-gate.json/.md`

The projection only uses runtime-observable fields: source/evidence refs, provenance/source fingerprint, relation type/direction/confidence, itemKind/sourceKind, lifecycle/reviewStatus/targetSection, and query-derived anchors. It does not use mustHit/mustNot labels, expected target section, sampleId/itemId special casing, or fixture/domain lexicons.

Current gate result:

- `GatePassed=true`
- `Recommendation=ReadyForSourceRepairRecheck`
- `MetadataCoverageDelta=1680`
- `Recall=47.50% -> 47.50%`
- `IndependentNonDenseSourceCount=3`
- risk / must-not / lifecycle risk remain `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants remain unchanged

## V5.13 Enriched Candidate Source Repair Recheck

V5.13 reruns query-driven candidate source repair over the V5.12 enriched projection. It is still preview/eval-only and does not authorize formal retrieval, runtime switch, formal package write, `PackingPolicy` mutation, package output mutation, or vector binding changes.

Commands:

- `eval vector-enriched-candidate-source-repair-recheck`
- `eval vector-enriched-candidate-source-repair-recheck-gate`

Outputs:

- `vector/v5/enriched-candidate-source-repair-recheck.json/.md`
- `vector/v5/enriched-candidate-source-repair-recheck-gate.json/.md`

Current gate result:

- `RecheckPassed=false`
- `GatePassed=false`
- `Recommendation=BlockedByQualityRegression`
- `QualityImproved=true`
- train recall improves `41.67% -> 45.83%`
- holdout recall regresses `50.00% -> 41.67%`
- must-hit below topK improves `56 -> 52`
- risk / must-not / lifecycle risk remain `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants remain unchanged

Decision: enriched metadata is useful but not stable enough for source repair promotion. The next repair must preserve holdout recall while keeping risk and runtime invariants unchanged.

## V5.14 Holdout-safe Source-aware Ranking Repair

V5.14 introduces a preview-only source-aware ranking repair over the V5.12 enriched projection. It keeps formal retrieval disabled and does not mutate selected set, package output, `PackingPolicy`, runtime binding, vector store binding, or formal package artifacts.

Commands:

- `eval vector-source-aware-ranking-repair`
- `eval vector-source-aware-ranking-repair-gate`

Outputs:

- `vector/v5/source-aware-ranking-repair.json/.md`
- `vector/v5/source-aware-ranking-repair-gate.json/.md`
- `vector/v5/source-aware-ranking-blind-holdout-corpus.jsonl`
- `vector/v5/source-aware-ranking-blind-holdout-samples.jsonl`
- `vector/v5/source-aware-ranking-blind-holdout-manifest.json`

Current gate result:

- `GatePassed=true`
- `Recommendation=ReadyForSourceAwareRankingFreeze`
- `SelectedProfile=combined-safe`
- train/dev recall delta `+43.59%`
- test recall delta `+88.89%`
- holdout recall delta `+33.33%`
- blind-holdout recall delta `+4.17%`
- `DenseWinnerLostCount=0`
- risk / must-not / lifecycle risk remain `0`
- formal/package/`PackingPolicy`/runtime/vector binding invariants remain unchanged

Decision: source-aware ranking can now convert enriched source/evidence metadata into retrieval quality lift without test/holdout/blind-holdout regression. This is still preview/eval-only and does not authorize formal retrieval or runtime switch.

## V5.15 Output Token Budget / Priority Policy Shadow Gate

V5.15 locks the V5.14 `combined-safe` profile and the V5.11 eval protocol, then validates package-output policy behavior as a shadow projection only. The gate compares token totals, token max/P95, section occupancy, priority ordering, mandatory/hard-constraint coverage, dropped required candidates, section routing mismatch, and risk counters while keeping formal selected set, formal package, `PackingPolicy`, package output, runtime binding, and vector store binding unchanged.

Commands:

- `eval vector-output-token-priority-shadow`
- `eval vector-output-token-priority-shadow-gate`

Outputs:

- `vector/v5/output-token-priority-shadow.json/.md`
- `vector/v5/output-token-priority-shadow-gate.json/.md`

Current gate result:

- `GatePassed=true`
- `Recommendation=ReadyForOutputPolicyShadowFreeze`
- `ProfileName=combined-safe`
- `SampleCount=144`
- token delta total/max/P95 `126 / 8 / 8`
- `PriorityInversionCount=0`
- `DroppedRequiredCandidateCount=0`
- `SectionMismatchCount=0`
- risk / must-not / lifecycle risk remain `0`
- formal selected set, formal package, package output, `PackingPolicy`, runtime, and vector binding invariants remain unchanged

Decision: output token and priority policy shadow validation passes for `combined-safe`. This does not authorize formal retrieval, runtime switch, formal package write, `PackingPolicy` mutation, or package output mutation.

## Formal Adapter Input Contract Enforcement

Formal adapter integration now has a frozen runtime input contract before implementation work begins. The allowed input surface is limited to runtime-observable request, scope, package context, candidate, lifecycle, routing, provenance, relation evidence, and source contribution fields.

Denied inputs are explicit and gate-enforced:

- Dataset/eval sample DTOs
- gold labels such as must-hit, must-not, expected target section, and negative distractors
- sample metadata such as split, difficulty, task kind, rationale, and free-form sample metadata
- shadow, gate, recommendation, and report artifacts

Commands:

- `eval vector-formal-adapter-input-contract`
- `eval vector-formal-adapter-input-contract-gate`

Outputs:

- `vector/v5/formal-adapter-input-contract.json/.md`
- `vector/v5/formal-adapter-input-contract-gate.json/.md`

This remains contract-only. It does not authorize formal retrieval, runtime switch, formal package writes, `PackingPolicy` mutation, package output mutation, or formal vector-store binding.

## Formal Retrieval Integration Decision Gate

The mainline decision gate now summarizes V5.0 through V5.16 and decides whether the project can proceed to `FormalRetrievalIntegrationFreeze / AdapterNoOpBindingPlan`.

Commands:

- `eval vector-formal-retrieval-integration-decision`
- `eval vector-formal-retrieval-integration-decision-gate`

Outputs:

- `vector/v5/formal-retrieval-integration-decision.json/.md`
- `vector/v5/formal-retrieval-integration-decision-gate.json/.md`

The decision requires all relevant V5 gates, runtime-change gate, and P15 to pass, with risk, formal output change, package output change, `PackingPolicy` change, runtime mutation, and vector-store binding mutation all remaining zero/false.

V5.13 `BlockedByQualityRegression` can be explicitly superseded by V5.14 `ReadyForSourceAwareRankingFreeze` only when the V5.13 report itself keeps risk, must-not, lifecycle, formal output, package, `PackingPolicy`, runtime, and vector-binding invariants safe. This records that an intermediate source-repair failure was repaired by the later mainline ranking gate; it does not authorize formal retrieval.

Passing this gate only allows a formal integration freeze and adapter no-op binding plan. It still does not allow formal retrieval, runtime switch, formal package writes, `PackingPolicy` integration, package output mutation, or formal vector-store binding.

## V6.6 Source-diverse Shadow Adapter Delta Validation

V6.6 adds a source-diverse validation set and gate for the shadow adapter delta path. It separates baseline topK, shadow expanded pool, and shadow final topK so the adapter can be validated on runtime-observable source/evidence/relation/metadata signals without using gold labels for scoring or candidate generation.

Commands:

- `eval vector-source-diverse-shadow-adapter-validation`
- `eval vector-source-diverse-shadow-adapter-validation-gate`

Outputs:

- `vector/v6/source-diverse-shadow-adapter-validation.json/.md`
- `vector/v6/source-diverse-shadow-adapter-validation-gate.json/.md`

The gate remains shadow-only: applied add/remove must stay zero, and formal selected set, formal package, package output, `PackingPolicy`, runtime, and vector binding must remain unchanged. Passing V6.6 does not authorize formal retrieval or runtime switch.

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

## V6.7 Shadow Merge Stability Freeze

Shadow merge stability freeze 将 preview merge 的多轮观察结果固化为只读决策报告。报告输出到：

- `vector/v6/shadow-merge-stability-freeze.json/.md`
- `vector/v6/shadow-merge-promotion-decision.json/.md`

当前阶段只做 promotion decision：允许下一阶段编写 controlled merge proposal；不应用 add/remove，不写正式 package，不改变 formal selected set、PackingPolicy、package output、runtime 或 vector binding。

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
