# ControlRoom Service Mode

更新时间：2026-06-10

## 1. 模式说明

ControlRoom 支持：

- Direct Mode
- Service Mode

Service Mode 只通过 `ContextCore.Service` 的 HTTP API 工作，不直接读取本地存储。

## 2. Runtime Dashboard

当前 dashboard 展示：

- service status / ready status
- storage provider / root path
- retrieval baseline
- provider capabilities
- probe checks
- short-term maintenance worker 状态

maintenance 区块显示：

- enabled
- running
- runOnStartup
- intervalSeconds
- lastError
- lastRun

## 3. Short-Term Memory 页面

当前展示：

- active raw / working summary
- archive summary
- archive raw / working detail
- recent compaction runs
- maintenance worker 状态

当前操作：

- `C` manual compact
- `A` archive summary + detail

## 4. Promotion Candidates 页面

当前展示：

- candidate list
- current filters
- reason
- confidence
- target layer
- evidence refs
- source working item / raw events（explain）
- rule / policy info（explain）
- dedupe key / source fingerprint / generatedBy（detail / explain）

当前交互：

- `G` generate
- `F` filter
- `S <id>` detail
- `E <id>` explain
- `A <id>` accept
- `R <id>` reject
- `X <id>` expire
- `H <id>` review history

explain 当前展示：

- source working item
- source raw events
- evidence refs
- reason
- confidence
- target layer
- rule / policy info
- dedupe key / source fingerprint / generatedBy

review 当前展示：

- target item id
- review id
- action / status
- reviewer / reason / reviewedAt
- warnings / errors
- review history

accept / reject / expire 前会展示 detail + explain，并要求输入 `YES` 确认。accept 只写候选记忆或约束候选，不写 StableMemory；reject 只更新状态并记录 history，不删除 candidate。

Phase 6 本轮补齐 review / accept DTO 命名、result 字段与 ControlRoom 确认流程；仍不改变 retrieval、scoring、PackingPolicy 或 stable promotion 行为。

## 5. Service Mode 当前页面

当前已接入：

- Service Dashboard
- Service Ingest
- Service Query
- Service Package
- Service Jobs
- Service Model
- Service Admin / Runtime
- Service Memory
- Service Constraints
- Service Relations
- Service Policy
- Service Short-Term Memory
- Service Promotion Candidates
- Service Stable Review Candidates
- Service Learning
- Service Planning Snapshot
- Service Planning Proposal
- Service Constraint Gaps
- Service Candidate Constraints
- Service Candidate Memory
- Service Stable Memory
- Service Policy Feedback Dataset
- Service Learning Features
- Service Ranker Shadow Debug
- Service Vector Index

Service Memory / Service Constraints / Service Stable Review Candidates 页面均支持 `P <id>` provenance，只读展示 stable target 来源链、missing links 与 diagnostics。

Service Relations 页面展示 relation type taxonomy、global relation diagnostics，并支持输入 itemId 查看该 item 的 incoming / outgoing relations 与 item-level diagnostics。也支持 `E <relationId>` 查看 relation explain，展示 confidence、confidence reason、lifecycle、review status、source refs、evidence refs、inverse relation 与 relation-level diagnostics。G3 增加人工 review / lifecycle 操作：`V <relationId>` review、`R <relationId>` reject、`X <relationId>` deprecate、`N <relationId>` mark needs evidence、`H <relationId>` review history。G4 增加 relation expansion governance 只读能力：`P` 展示 expansion profiles，单独输入 `X` 执行 expansion preview 并展示 accepted / blocked relations、reasons 与 warnings。G5 增加 `eval relation-expansion-shadow-eval` 离线样本级 shadow report，不新增运行时页面操作。G5.3 的 expansion preview 还会展示 `targetSection`、`sectionReason`、`riskIfNormalSelected`、`riskAfterSectionRouting`，用于确认 audit / historical / conflict evidence 分区是否避免 normal-context 风险。G6 在 Relations 页面增加 Recent Graph Shadow Traces / Graph Shadow Trace Quality Summary 只读区块，读取 `/api/learning/graph-expansion-shadow/traces` 并在 ControlRoom 本地聚合 quality summary。G6.1 采集流程见 `docs/graph-shadow-trace-collection-runbook.md`，采集脚本 `scripts/collect-graph-expansion-shadow-traces.ps1` 默认 dry-run，只有传入 `-Execute` 才调用已运行的 Service；进入 G7 的 trace quality 不能用重复 query 或容易合格的单一夹具补量，必须覆盖不同 operationId、不同业务场景和可解释 blocked relation。G7 增加 package / retrieval debug 中的 graph expansion 状态展示，显示 `Off` / `Shadow` / `ApplyGuarded`、Applied / Fallback、added graph items、target sections 和 risk checks。页面不做 graph repair，不改变正式 relation expansion、retrieval、planning 或 `PackingPolicy`。

Service Constraint Gaps 页面展示从 eval/report 生成的 constraint corpus gap candidate，并支持人工 accept / reject。accept 只创建 `Status=Candidate` 的 CandidateConstraint，不创建 Active/Hard constraint，不直接影响 package。

Service Candidate Constraints 页面展示人工 accept gap 后生成的候选约束，并支持人工 activate / reject。activate 才会把 `Status=Candidate`、`Level=User` 的 CandidateConstraint 提升为 `Status=Active`、`Level=Hard`；reject 只更新状态，不删除。

Service Candidate Memory 页面展示中期候选记忆治理快照、recent candidates 与 diagnostics，并支持 detail / explain / manual review cleanup。人工操作只更新 candidate-layer 状态和 review history，不自动晋升 StableMemory，不改变 retrieval、planning、`PackingPolicy` 或 package 输出。

Service Stable Memory 页面展示长期记忆治理快照、recent stable items 与 diagnostics，并支持 detail / explain / provenance / replacement chain / lifecycle review history。页面支持人工 Deprecate / Supersede / Reject，操作前要求输入 `YES`；Supersede 会写入 `superseded_by` / `replaces` relation。该页面不编辑内容、不自动合并、不改变 retrieval、planning、`PackingPolicy` 或 package 输出。

Service Learning Features 页面展示由 policy feedback、P15 eval reports 与 planning shadow reports 只读投影出的 feature dataset summary，包括 feature count、ranking pair count、router intent example count、label distribution、source type distribution、latest export path 与 recent feature examples。Phase 4 增加 Dataset Quality 区块，展示 policy/ranking/router counts、label risks、task readiness 和 recommended next action。该页面不训练模型、不应用策略、不改变 retrieval / planning / package。

Service Ranker Shadow Debug 页面通过 `34` 从主菜单或 Service Dashboard 进入。页面要求输入 query，可选 mode，然后调用 `POST /api/retrieval/ranker-shadow/debug` 展示 candidate score comparison、deprecated / historical demotion reason、current / active promotion reason、version conflict fixes，以及 `FormalOutputChanged=false` / `SelectedSetChanged=false` 状态。页面还会只读展示 Trace Quality Summary 与 Recent Shadow Traces，显示 trace count、candidate score count、risk count、recommended next step、最近 retrieval id、profile、candidate score count、deprecated demotion count 与 query。该页面只读，不编辑配置，不接 runtime scorer，不改变 retrieval output 或 selected set。

Service Vector Index 页面通过 `37` 从主菜单或 Service Dashboard 进入。页面调用 `GET /api/vector/status`、`GET /api/vector/diagnostics`、`POST /api/vector/reindex-preview`，展示 provider、model、dimension、indexed / stale / missing / duplicate / orphan 计数、Coverage Summary、Shadow Quality Summary、Residual Risk Summary、diagnostics 与 reindex preview 摘要。

Coverage Summary 展示：

- source item count
- indexed count
- coverage rate
- missing / stale / duplicate / orphan
- recommendation

Shadow Quality Summary 展示：

- current recommendation
- best profile
- best topK
- best minSimilarity
- riskAfterPolicy
- similarity separation

Residual Risk Summary 展示：

- residual risk count
- top risk types
- whyPolicyAllowed
- expectedAction
- lifecycle metadata coverage
- unknown lifecycle / missing review status / missing replacement info
- blocked by lifecycle metadata gate
- lifecycle metadata backfill plan summary
- manual review required count
- coverage before / after

Shadow Quality Summary 只读取本地 `eval/vector-query-profile-sweep-extended.json` 或 A3 sweep 报告；Residual Risk Summary 只读取本地 `eval/vector-residual-risk-audit-extended.json` 或 A3 residual audit 报告，并可读取 `eval/vector-lifecycle-metadata-coverage.json` 展示 lifecycle metadata coverage，读取 `eval/vector-lifecycle-metadata-backfill-plan.json` 展示 backfill plan summary。Recall Loss / Intent Readiness Summary 只读取 `eval/vector-recall-loss-audit-a3.json` 与 `eval/vector-recall-loss-audit-extended.json`。Safe Recall Recovery / V4 Readiness Summary 只读取 `eval/vector-safe-recall-recovery-a3.json`、`eval/vector-safe-recall-recovery-extended.json` 与 `eval/vector-retrieval-shadow-readiness-gate.json`。Fusion Shadow Summary 只读取 `eval/vector-ranker-fusion-shadow-a3.json` 与 `eval/vector-ranker-fusion-shadow-extended.json`，展示 best strategy、A3 / Extended fusion recall、risk、recall gain 和 fusion gate 状态。Representation Benchmark Summary 只读取 `eval/vector-representation-benchmark-a3.json` 与 `eval/vector-representation-benchmark-extended.json`，展示 best document profile、best query profile、A3 / Extended recall、risk、recovered miss count 和 representation gate 状态。V3.9 新增 Query Expansion Shadow Summary，只读取 `eval/vector-query-expansion-shadow-a3.json` 与 `eval/vector-query-expansion-shadow-extended.json`，展示 best expansion profile、A3 / Extended recall before/after、risk、recovered miss count 和 expansion gate 状态。页面不自动执行 sweep / audit / coverage / backfill / fusion / representation benchmark / query expansion shadow，不调用 reindex，不写 vector index，也不改变 retrieval、planning、`PackingPolicy` 或 package 输出。当前 V3.9 本地 ONNX 结果中 Extended query expansion shadow 为 `ReadyForRetrievalShadow`，A3 `RiskAfterPolicy=0` 但 `RecallAfter=71.21%`，`vector-retrieval-shadow-readiness-gate` 仍因 `A3RecallAtLeast80Percent`、`A3FusionRecallAtLeast80Percent` 与 `A3ExpandedRecallAtLeast80Percent` 未通过，因此 UI 继续按 preview / shadow-only 展示。

V3.4 / V3.5 增加 provider comparison 输出：

- `eval/vector-embedding-provider-comparison.json`
- `eval/vector-embedding-provider-comparison.md`

该报告由 `eval vector-query-profile-sweep` 同步生成，用于比较当前 embedding provider 的 semantic separation、must-hit recall 与风险。V3.5 report 至少包含 `deterministic-hash` 和 `onnx-local` 两个 provider 结果；当前本地结果中 deterministic-hash `SimilaritySeparation=0.0066`，onnx-local `SimilaritySeparation=0.0857`、`MustHitRecallAfterPolicy=84.38%`、`MustNotHitRiskAfterPolicy=4.92%`。ONNX 本地模型路径必须通过本地配置或 CLI 参数传入，模型文件和 tokenizer 文件不应提交到仓库。ControlRoom 页面只展示已生成摘要，不会加载模型或自动切换 provider。

本地 ONNX smoke / reindex / shadow eval 操作见 `docs/vector-embedding-provider-local-runbook.md`。ControlRoom Service Mode 不编辑 provider 配置，不自动启用 `OnnxLocal`。

Phase V2 / V3 页面新增受控 reindex 与 query preview 操作：

- `P` Reindex Plan：调用 `POST /api/vector/reindex-plan`，只读生成 plan。
- `A` Apply Reindex：先展示 plan，再要求输入 `YES`，确认后调用 `POST /api/vector/reindex-submit` 提交 `vector_reindex` job。
- `R` Reindex Reports：调用 `GET /api/vector/reindex-reports` 展示历史报告。
- `Q` Query Preview：手动输入 query text / topK / profile / layer filter / minSimilarity，调用 `POST /api/vector/query-preview` 展示 raw / eligible / blocked candidates、similarity、blocked reasons、target section、riskBefore / riskAfter 与 diagnostics。
- `D` Diagnostics：刷新当前 diagnostics / preview。

该页面不删除 vector entry，不编辑配置，不改变 retrieval、planning、`PackingPolicy` 或 package 输出。`Apply` 只写入独立 V1 vector index，并且必须显式确认；未输入 `YES` 时不会调用 submit endpoint。`Q` Query Preview 是手动只读操作，不随页面刷新自动执行，避免重复 preview 数据造成诊断噪声。V3.6 的 vector eligibility 只使用运行时 metadata 和 diagnostics，不读取 eval label、sampleId、fixture 名称、itemId 特判或领域词表。

## 6. Stable Review Candidates 页面

当前展示：

- stable review candidate list
- validation status
- risk flags
- suggested stable target
- source promotion candidate id
- source target item id
- source learning case id
- evidence refs
- explain 中的 source promotion candidate / source learning case / source memory target / source constraint target

当前交互：

- `Z` 或 `27` 从主菜单进入
- `G` generate
- `F` filter
- `S <id>` detail
- `E <id>` explain
- `P <id>` provenance
- `A <id>` accept
- `R <id>` reject
- `H <id>` review history
- `R` refresh
- `B/0` back

accept / reject 前会展示 detail + explain / validation / risk flags，并要求输入 `YES` 确认。accept 会调用 Stable Review accept endpoint，在服务端重新 validation 通过后写入 StableMemory / StableConstraint / DecisionRecord；reject 只更新状态并记录 history。该页面不做配置编辑、不训练模型、不改变 retrieval。

## 7. Provenance 查看

Provenance 查看调用：

- `GET /api/provenance/{itemId}`
- ControlRoom 使用当前 `workspaceId` / `collectionId` 作为可选 scope

展示内容：

- target memory / constraint item
- stable review candidate
- promotion candidate
- feedback signal
- learning case
- source working item
- evidence refs
- stable review history
- promotion review history
- duplicate stable / possible conflict / missing source link diagnostics

缺失来源以 warning / missing link 展示，不把缺失上游链接渲染为普通异常。

## 8. Learning 页面

当前展示：

- learning records
- promotion feedback signals
- positive / negative / stale 统计
- recent feedback
- learning cases
- failure type summary
- learning summary
- active regression cases

当前交互：

- `H` 或 `26` 从主菜单进入
- `S <recordId>` 查看单条 learning record
- `C <caseId>` 查看单条 learning case
- `R` refresh
- `B/0` back

Learning 页面只读展示 feedback / records / cases，不训练模型，不自动调参，不改变 retrieval。

Phase 7 的 Learning Feedback 展示为只读观测：显示 accept / reject 生成的 feedback signals 与 draft learning cases，不触发训练、不应用策略、不改 retrieval。

## 9. Policy Feedback Dataset 页面

当前展示：

- dataset id / scope / policy version / P15 eval baseline ref
- positive / negative / neutral 统计
- source type 分布
- recent policy feedback records

当前交互：

- `32` 从主菜单或 Service Dashboard 进入
- `R` refresh
- `B/0` back

Policy Feedback Dataset 页面只读展示从 promotion / stable / constraint gap / candidate constraint review history 聚合出的策略反馈样本，不训练模型，不应用策略，不改变 retrieval / planning / package。

## 10. Planning 页面

Planning Snapshot 页面：

- `X` 或 `28` 从主菜单进入
- Service Dashboard 输入 `X`
- 展示 active tasks / recent decisions / open questions / known issues / stable constraints / stable preferences / decision records / learning signals summary

Planning Proposal 页面：

- `F` 或 `29` 从主菜单进入
- Service Dashboard 输入 `F`
- 输入 current input
- 展示 proposed intent / mode / channels / TopK / reasons / warnings

Planning Proposal 只做 preview，不执行 retrieval，不修改配置，不写入 `RetrievalPlan`，不影响 package 或 selected set。

Ranker Shadow Debug 页面：

- `34` 从主菜单或 Service Dashboard 进入
- 输入 query
- 可选输入 mode，默认 `ChatMode`
- 展示 candidate `legacyScore` / `lifecycleAwareScore` / `scoreDelta`
- 展示 deprecated / historical demotion reason
- 展示 current / active promotion reason
- 展示 version conflict fixes、must-hit demotions、must-not-hit promotions
- 展示 Trace Quality Summary 只读区块
- 展示 Recent Shadow Traces 只读区块

Ranker Shadow Debug 只做 debug opt-in；Trace Quality Summary 和 Recent Shadow Traces 只读取 `GET /api/learning/ranker-shadow/traces` 的 recent records 并在本地聚合。它们都不启用 `Learning:RankerShadow:Enabled`，不改变正式 retrieval scoring，不改变 selected set，不修改 `PackingPolicy`。

Ranker shadow trace 采集流程见 `docs/ranker-shadow-trace-collection-runbook.md`。采集脚本 `scripts/collect-ranker-shadow-traces.ps1` 默认 dry-run，只有显式传入 `-Execute` 才会调用已运行的 Service。

Learning Features 页面新增 Candidate Reranker Shadow Summary。该区块读取：

- `learning/ranker/candidate-reranker-shadow-eval-a3.json`
- `learning/ranker/candidate-reranker-shadow-eval-extended.json`
- `learning/ranker/candidate-reranker-shadow-trace-quality-report.json`

展示 runtime trace count、eval net gain、would improve / regress、risk count 和 recommendation。该区块只读，不启用正式 reranker，不改变 retrieval output、selected set、`PackingPolicy` 或 package sections。

Learning Features 页面新增 Candidate Feature Completeness / Eligibility Guard Summary。该区块读取：

- `learning/ranker/candidate-reranker-feature-completeness-a3.json`
- `learning/ranker/candidate-reranker-feature-completeness-extended.json`

展示 feature completeness rate、missing metadata count、blocked before rerank count、rankable / blocked candidate count、eligibility guard status 和 recommendation。该区块只读，只解释 shadow rerank 前的 candidate metadata envelope 与 eligibility guard，不替换正式 scorer，不改变 retrieval output、selected set、`PackingPolicy` 或 package sections。

Learning Features 页面同时新增 Candidate Reranker Failure Audit Summary。该区块读取：

- `learning/ranker/candidate-reranker-shadow-failure-audit-a3.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit-extended.json`

展示 top regression reasons、score contract status、risk in shadow topK 和 recommended next action。该区块只读，只解释 CR1 回归原因，不进入 runtime trace，不替换正式 scorer，不改变 retrieval output、selected set、`PackingPolicy` 或 package sections。

Learning Features 页面新增 Ranker Calibration Summary。该区块读取：

- `learning/ranker/candidate-reranker-score-distribution-a3.json`
- `learning/ranker/candidate-reranker-score-distribution-extended.json`
- `learning/ranker/candidate-reranker-listwise-calibration-a3.json`
- `learning/ranker/candidate-reranker-listwise-calibration-extended.json`

展示 score mean / stddev、low-margin decisions、top calibration issues、formal priority mismatch 和 recommendation。该区块只读，只解释 CR1.3 的 score scale / listwise calibration，不进入 runtime trace，不替换正式 scorer，不改变 retrieval output、selected set、`PackingPolicy` 或 package sections。

Learning Features 页面新增 Formal Priority Alignment Summary。该区块读取：

- `learning/ranker/candidate-reranker-formal-priority-alignment-a3.json`
- `learning/ranker/candidate-reranker-formal-priority-alignment-extended.json`

展示 recovered count、unexplained mismatch、abstain count、net gain after abstain、按 formal priority feature 的 recovered breakdown 和 recommendation。该区块只读，只解释 CR1.4 的 shadow-only formal priority alignment，不进入 runtime trace，不替换正式 scorer，不改变 retrieval output、selected set、`PackingPolicy` 或 package sections。

Graph shadow trace 采集流程见 `docs/graph-shadow-trace-collection-runbook.md`。采集脚本 `scripts/collect-graph-expansion-shadow-traces.ps1` 默认 dry-run，只有显式传入 `-Execute` 才会调用已运行的 Service。采集只写 trace/export/report，不启用 graph opt-in，不改变正式 retrieval、relation expansion、selected set、`PackingPolicy` 或 package output。

Candidate Memory 页面：

- `35` 从主菜单或 Service Dashboard 进入
- 展示 candidate memory / constraint / decision counts
- 展示 pending review、promotion accept、expired、duplicate、conflict 统计
- 展示 recent candidates
- 展示 diagnostics，包括 duplicate、stale、without evidence、rejected source、stable conflict、superseded
- diagnostics 展示 suggestedAction，但不自动执行
- 直接输入 candidate id 查看 detail
- `E <candidateId>` 查看 explain
- explain 展示 source promotion candidate、source stable review candidate、source constraint gap、source feedback signal、source learning case、evidence refs、review history、provenance chain、risk flags
- `Ready <candidateId>` 标记 ready for stable review
- `N <candidateId>` 或 `Needs <candidateId>` 标记 needs more evidence
- `Reject <candidateId>` 拒绝 candidate
- `Expire <candidateId>` 或 `X <candidateId>` 过期 candidate
- `Supersede <candidateId>` 或 `U <candidateId>` 标记被另一个 candidate supersede
- `H <candidateId>` 查看 CandidateMemory review history
- Ready / NeedsMoreEvidence / Reject / Expire / Supersede 前展示 detail + explain，并要求输入 `YES`
- 页面不自动 stable promotion，不 activate constraint，不改变 retrieval / planning / package

Stable Memory 页面：

- `36` 从主菜单或 Service Dashboard 进入
- 展示 stable memory / stable constraint / decision record / global memory counts
- 展示 active、superseded、deprecated、rejected、missing provenance、duplicate、conflict、weak evidence 统计
- 展示 recent stable items
- 展示 diagnostics，包括 duplicate stable memory、possible conflict、missing provenance、missing evidence refs、stable without review source、stable constraint without scope、decision record without source、deprecated still active、superseded without replacement、global memory scope risk
- 展示 replacement relation diagnostics，包括 superseded without relation、metadata/relation mismatch、broken replacement link、replacement target missing/inactive、replacement cycle、multiple active replacements、scope mismatch
- 直接输入 stable item id 查看 detail
- `E <stableItemId>` 查看 explain
- `P <stableItemId>` 查看完整 provenance
- `C <stableItemId>` 查看 replacement chain、root/latest item、relations、warnings
- `X <stableItemId>` deprecate stable item
- `S <stableItemId>` supersede stable item，必须输入 replacement stable item id
- `R <stableItemId>` reject stable item
- `H <stableItemId>` 查看 Stable lifecycle review history
- Deprecate / Supersede / Reject 前展示 detail + explain / diagnostics，并要求输入 `YES`
- 页面不编辑 stable 内容，不自动合并，不自动 promotion，不改变 retrieval / planning / package

Constraint Gaps 页面：

- `30` 从主菜单进入
- Service Dashboard 输入 `C`
- 展示 expected constraint、source sample / operation、suggested scope / type、evidence refs
- `S <gapId>` 或直接输入 gap id 查看单条 gap
- `A <gapId>` accept
- `R <gapId>` reject
- `H <gapId>` review history
- accept / reject 前展示 detail，并要求输入 `YES`
- accept 只写 CandidateConstraint，不自动写 Active/Hard 约束库

Candidate Constraints 页面：

- `31` 从主菜单进入
- Service Dashboard 输入 `E`
- 默认展示 `Status=Candidate` 的 CandidateConstraint
- `F` filter status / limit / offset
- `S <constraintId>` 查看 detail
- `A <constraintId>` activate
- `R <constraintId>` reject
- `H <constraintId>` review history
- activate / reject 前展示 detail，并要求输入 `YES`
- activate 会调用 `/api/constraints/candidates/{id}/activate`，服务端校验非 Candidate 状态、duplicate active constraint 与 source refs 后才写为 Active/Hard
- reject 只更新状态，不删除 constraint

Package Preview / Retrieval Debug 现在只读展示 planning execution 状态：

- `Legacy`
- `Shadow`
- `ApplyGuarded`
- `FallbackUsed`
- `FallbackReason`

展示字段来自 retrieval trace metadata。ControlRoom 不提供 planning 配置编辑，也不默认填充 opt-in intents。

Package Preview / Retrieval Debug 也只读展示 graph expansion guarded opt-in 状态：

- `Off` / `Shadow` / `ApplyGuarded`
- `Applied` / `FallbackUsed`
- `Profiles`
- `AddedItems`
- `TargetSections`
- `ExpectedDelta`
- `UnexpectedWarn`
- `RiskChecks`

`ExpectedDelta` 表示合法辅助 graph section 增量，例如 `audit_context`、`conflict_evidence`、`historical_context` 或 `diagnostics_only`。`UnexpectedWarn` 表示 fallback 或风险触发后的非预期 warning delta。ControlRoom 只展示状态，不提供 graph expansion 配置编辑，也不允许把 graph item 注入 `normal_context`。

Learning Features 页面现在只读展示 Router Intent Baseline R1 摘要：

- `33` 从主菜单或 Service Dashboard 进入
- 数据来自本地 `learning/router/router-intent-baseline-report.json`
- 展示 status、sample count、best baseline、recommendation
- 展示每个 baseline 的 accuracy、macroF1、low confidence、abstain 与关键 intent recall
- 如果报告尚未生成，页面只显示 `not generated`
- 页面不替换 runtime router，不修改 planning、retrieval、`PackingPolicy` 或 package output

Learning Features 页面也只读展示 Router Shadow Summary：

- 数据来自本地 `learning/router/router-shadow-trace-quality-report.json`
- 展示 trace count、agreement / disagreement rate、low confidence、abstain、recommendation
- 展示 top confusion pairs
- 若报告尚未生成，页面显示 `not generated` 并提示运行 `eval router-shadow-trace-quality`
- Router shadow trace 只在 `Learning:RouterShadow:Enabled=true` 且 `TraceCollectionEnabled=true` 时写入
- 即使启用 trace collection，也只写旁路 trace，不改变 runtime router、planning、retrieval、`PackingPolicy` 或 package output

Learning Features 页面还只读展示 Router Disagreement Triage Summary：

- 数据来自本地 `learning/router/router-disagreement-triage-a3.json`
- 同时读取 `learning/router/router-disagreement-triage-extended.json`
- 展示 disagreement count、shadow fixes、shadow breaks、top confusion pairs、hard negative count 与 recommendation
- 若报告尚未生成，页面显示 `not generated` 并提示运行 `eval router-disagreement-triage`
- hard negatives 来自 `learning/router/router-hard-negatives.jsonl`，只用于离线数据增强建议
- 页面不替换 runtime router，不修改 planning、retrieval、`PackingPolicy` 或 package output

Learning Features 页面新增 Router Opt-in Readiness Summary：

- 数据来自本地 `learning/router/router-guarded-optin-readiness-gate.json`
- 展示 passed、fixes、breaks、net gain、agreement、blocked reason 与 recommendation
- 若报告尚未生成，页面显示 `not generated` 并提示运行 `eval router-guarded-optin-readiness-gate`
- 当前 R2.F 结论应为 `KeepRuleBased`，R3 guarded opt-in blocked
- 页面只读，不提供 router opt-in 配置编辑，不替换 runtime router

Learning Features 页面新增 Learning Readiness Dashboard：

- 数据来自本地 `learning/readiness/learning-readiness-freeze-report.json`
- 展示 capability、status、gate、recommendation、blocked reasons、allowed modes、forbidden modes 与 last report path
- 覆盖 `GraphExpansion`、`VectorRetrieval`、`RouterIntentClassifier`、`CandidateReranker`、`AttentionRerank`、`PlanningProposal`
- 当前冻结结论中，Graph 仅允许 `audit-v1` / `conflict-v1` 的 guarded opt-in；Vector 保持 `PreviewOnly`；Router 保持 `KeepRuleBased`；CandidateReranker 保持 `KeepFormalRanking`
- 若报告尚未生成，页面显示 `not generated` 并提示运行 `eval learning-readiness-freeze-report`
- 页面只读，不提供 runtime mode 配置编辑

Learning Features 页面新增 Learning Runtime Change Gate：

- 数据来自本地 `learning/readiness/learning-runtime-change-readiness-gate.json`
- 展示 passed、recommendation 与 failed conditions
- 当前 gate 通过表示 freeze registry 正确禁止了不允许的 runtime 模式，不代表任何新能力默认启用
- 若报告尚未生成，页面显示 `not generated` 并提示运行 `eval learning-runtime-change-readiness-gate`

Learning Features 页面新增 Runtime Feedback 区块：

- 数据来自 `GET /api/learning/feedback/summary`
- 展示 feedback count、capability 分布、kind 分布、recent feedback 和 export path
- 展示 target type 分布、metadata-only count、trainingUse disabled count
- 反馈事件只用于采集、审计和离线导出
- 页面不训练模型、不调权、不启用 shadow opt-in、不改变 retrieval / planning / `PackingPolicy` / package output
- 对应 CLI：`eval learning-feedback-summary`、`eval export-learning-feedback`

Learning Features 页面新增 `F feedback` 提交动作：

- 操作员显式输入 `CapabilityId`、`TargetType`、`TargetId`、`FeedbackKind`、`SourceOperationId` 和 reason
- 默认按 metadata-only / `disabled_until_review` 提交
- 提交动作只写 runtime feedback store，不修改 package、retrieval、planning 或任何 scorer
- 当前支持目标类型包括 `PackageItem`、`RetrievalCandidate`、`RouterPrediction`、`VectorCandidate`、`GraphExpansionCandidate`、`RankerCandidate`、`PromotionCandidate`、`ConstraintGapCandidate`、`StableReviewCandidate`

Learning Features 页面新增 Runtime Feedback Review / Feature Candidate 摘要：

- Review summary 数据来自 `GET /api/learning/feedback/reviews/summary`
- 展示 pending、approved、rejected、needs redaction、needs evidence 计数
- Feature candidate summary 只读本地 `learning/feedback/learning-feedback-feature-candidates-report.json`
- 展示 generated candidate count、scanned feedback count 与 candidates by capability
- 若候选报告尚未生成，页面提示运行 `eval learning-feedback-feature-candidates`
- 页面不生成候选、不训练模型、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output

Learning Features 页面新增 Feedback Quality / Dataset Readiness Summary：

- 数据来自本地 `learning/feedback/learning-feedback-quality-report.json`
- 展示 review coverage、redaction coverage、recommendation 和 feature candidate count
- 按 capability 展示 ready、candidate count 与 blocked reasons
- 若报告尚未生成，页面提示运行 `eval learning-feedback-quality`
- 页面只读，不触发训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output

Learning Features 页面新增 Runtime Feedback Review 操作：

- `A Approve`
- `R Reject`
- `N NeedsRedaction`
- `E NeedsEvidence`
- `H ReviewHistory`

操作前会展示 `FeedbackId`、`CapabilityId`、`TargetType`、`TargetId`、`SourceOperationId`、`FeedbackKind`、`metadataOnly`、`redactionMode`、`trainingUse` 和 `reason`，并要求输入 `YES`。这些操作只写 feedback review store，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Learning Features 页面还会展示 Approved Dataset Gate：

- 数据来自本地 `learning/feedback/learning-approved-feedback-dataset-gate.json`
- 展示 passed、trainable candidate count、smoke excluded count、recommendation 和 fail reasons
- 若报告尚未生成，页面提示运行 `eval learning-approved-feedback-dataset-gate`
- Smoke review flow 记录必须以 `smoke_test_only` / `excludedFromTraining=true` 排除在 trainable dataset 之外

## 10. 当前边界

当前仍不做：

- direct-only 本地文件编辑
- destructive admin 操作
- NamedPipe
- automatic StableMemory promotion
- LLM judge
- LLM router
- vector retrieval 接入
- layered retrieval
- lifecycle-aware ranker runtime scorer
## File Layout Status

Service Admin / Runtime 页面增加 File Layout Status，只读展示：

- data root
- artifact categories
- resolved path samples
- manifest count
- report count
- diagnostics

该区块来自 FS1 的 filesystem layout registry。它只观察 artifact routing 状态，不修改 retrieval、planning、scoring、PackingPolicy 或 package output。

## Memory Layout Status

Service Admin / Runtime 与 Service Memory 页面增加 Memory Layout Status，只读展示：

- memory layer paths
- short-term / candidate / stable artifact count
- legacy fallback count
- temporal placeholder readiness
- missing directory count
- diagnostics

该区块来自 FS2 的 memory layer store routing。它只解析和统计文件布局，不触发迁移、不删除旧文件、不改变 memory 业务逻辑、retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Trace Layout Status

Service Admin / Runtime 页面增加 Trace Layout Status，只读展示：

- trace root
- retrieval / router-shadow / ranker-shadow / graph-shadow / vector-shadow trace count
- tool-call placeholder readiness
- legacy fallback count
- operation/date shard path examples
- diagnostics

该区块来自 FS3 的 trace / tool-call artifact routing。它只解析和统计 trace 布局，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Report Layout Status

Service Admin / Runtime 页面增加 Report Layout Status，只读展示：

- report categories
- latest reports
- legacy mirror count
- missing standard artifact count
- missing legacy artifact count
- duplicate content hash count
- largest reports
- sample resolved paths
- diagnostics

该区块来自 FS4 的 eval / report artifact migration。旧报告路径继续写出，标准 layout 同步 mirror，并通过 manifest 记录 `legacyPath`、`relativePath`、`contentHash`、`sizeBytes`、`isLatest` 和 `isSnapshot`。页面只读取 manifest 和文件状态，不触发 eval，不修改 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Storage Boundary Status

Service Admin / Runtime 页面增加 Storage Boundary Status，只读展示：

- artifact kind count
- artifact-only / operational-state / index-state count
- database-recommended / filesystem-preferred count
- migration candidate count
- high-priority migration candidates
- recommended next phases
- diagnostics

该区块来自 DB0 的 `StorageResponsibilityRegistry`。它只说明 artifact plane 与 operational/index store 的责任边界，不执行数据库迁移，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Postgres Operational Store Status

Service Admin / Runtime 页面增加 Postgres Operational Store Status，只读展示：

- enabled
- provider id
- connection available
- current schema version
- pending migrations
- required table missing count
- schema verification summary
- required / missing indexes
- provider capability status
- diagnostics

该区块来自 DB1 的 PostgreSQL operational store diagnostics。默认未启用时显示 `NotConfigured`；页面不执行 migration apply，不显示明文 connection string，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB1.1 增加 Schema Verification Summary，只读展示 schema name、schema version、required tables、missing tables/indexes 和 recommendation。该摘要来自 diagnostics，不会在 ControlRoom 页面触发 schema apply 或 cleanup。

## RelationStore Provider Status

Service Admin / Runtime 页面增加 RelationStore Provider Status，只读展示：

- active runtime provider
- Postgres provider enabled / connection
- schema version
- relation table / relation reviews table
- relation count / review count
- missing indexes
- recommendation
- warning：Postgres RelationStore 未用于 runtime，除非后续阶段显式启用

该区块来自 DB2 的 `postgres-relation-store-diagnostics` 报告。页面只展示最近报告，不执行 parity、不写数据库、不切换 provider，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB2.1 增加 relation review / diagnostics provider status 和 parity status，只读展示：

- relation review table / diagnostics table
- review count
- diagnostics count
- missing indexes
- review parity recommendation
- mismatch count
- cleanup status

该区块读取 `postgres-relation-review-diagnostics` 与 `postgres-relation-review-parity` 最近报告，不触发数据库写入，不改变 runtime provider。

DB2.2 增加 Governance Readiness，只读展示：

- edge parity
- review parity
- diagnostics parity
- governance parity
- readiness gate passed
- can dual-write
- can shadow-read
- can runtime switch
- blocked reasons

该区块读取 `postgres-relation-governance-parity` 与 `postgres-relation-governance-readiness-gate` 最近报告。当前阶段 gate 通过时只显示 `CanDualWrite=true`，`CanShadowRead=false`，`CanRuntimeSwitch=false`；ControlRoom 不执行 dual-write、不执行 shadow-read、不切换 runtime provider。

DB2.3 增加 Dual-write Status，只读展示：

- dual-write 默认关闭
- write target
- trace count
- mismatch count
- Postgres failure count
- fallback count
- average duration
- recommendation

该区块读取 `postgres-relation-dual-write-quality-report` 最近报告。ControlRoom 不主动执行 dual-write smoke，不从 Postgres 读取 runtime relation 数据，不切换 runtime provider。

DB2.4 增加 Shadow-read Status，只读展示：

- shadow-read 默认关闭
- trace count
- mismatch count
- Postgres read failure count
- average FileSystem latency
- average Postgres latency
- recommendation
- warning：runtime provider remains FileSystem

该区块读取 `postgres-relation-shadow-read-quality-report` 最近报告。ControlRoom 不主动执行 shadow-read smoke，不从 Postgres 返回 runtime relation 数据，不切换 runtime provider。

DB2.5 增加 Provider Switch Status，只读展示：

- current mode
- allowed workspaces / collections
- primary provider
- fallback enabled
- readiness gate
- switch gate
- recent switch trace count
- mismatch / fallback 状态
- recommendation
- rollback instruction

该区块读取 `postgres-relation-provider-switch-smoke-report` 与 `postgres-relation-provider-switch-gate` 最近报告。ControlRoom 不主动执行 provider switch smoke，不做 global default on，不迁移历史业务数据。rollback instruction 固定提示切回 `FileSystemPrimary` 或禁用 `RelationGovernanceProviderSwitchOptions`。

DB2.6 增加 Runtime Canary Status，只读展示：

- canary enabled
- canary scope
- provider mode
- gate status
- primary read/write count
- fallback count
- mismatch count
- recommendation
- rollback instruction

该区块读取 `postgres-relation-runtime-canary-report` 最近报告。ControlRoom 不主动执行 runtime canary，不扩大 allowlist，不切换全局 provider。rollback instruction 固定提示移除 canary scope allowlist 或切回 `FileSystemPrimary`。

DB2.7 增加 Scoped Service Mode 区块，只读展示：

- mode
- active provider
- allowlist
- fallback enabled
- comparison trace enabled
- governance / switch / canary gate status
- mismatch count
- postgres failure count
- recommendation
- rollback instruction

该区块读取 `postgres-relation-scoped-service-mode-smoke-report` 与 `postgres-relation-scoped-service-mode-gate` 最近报告。ControlRoom 不主动启用 scoped mode，不修改 allowlist，不做 global default on。rollback instruction 固定提示移除 scoped allowlist，或禁用 `RelationGovernanceProviderSwitchOptions.Enabled`。

DB2.8 增加 Extended Canary Summary，只读展示：

- canary scope
- provider mode
- operation count
- mismatch count
- fallback count
- graph preview parity
- review lifecycle parity
- diagnostics parity
- replacement chain parity
- recommendation

该区块读取 `postgres-relation-scoped-extended-canary-report` 最近报告。ControlRoom 不主动执行 extended canary，不扩大 allowlist，不做 global default on，不迁移历史业务数据。

DB2.9 增加 Selected Workspace Canary Summary，只读展示：

- selected workspace / collection
- provider mode
- operation count
- mismatch count
- fallback count
- Postgres read/write latency
- recommendation
- rollback instruction

该区块读取 `postgres-relation-selected-workspace-canary-report` 最近报告。ControlRoom 不主动启用 selected workspace canary，不修改 allowlist，不做 global default on，不迁移历史业务数据。

DB2.10 增加 Scoped Expansion Summary，只读展示：

- scope list
- per-scope mode
- per-scope mismatch / fallback / failure
- non-allowlisted scope status
- rollout stage
- operation count
- Postgres read / write latency
- recommendation
- rollback instruction

该区块读取 `postgres-relation-scoped-expansion-smoke-report` 最近报告。ControlRoom 不主动扩大 scope，不修改 `ScopedRules`，不做 global default on，不迁移历史业务数据。rollback instruction 固定提示禁用对应 scope rule，或将 `RelationGovernanceProviderSwitchOptions.Enabled=false`。

DB2.11 增加 Scoped Observation Summary，只读展示：

- observation window minutes
- operation count
- primary read / write count
- fallback count
- comparison trace count
- mismatch / Postgres failure / scope leak count
- average / P95 Postgres read latency
- average / P95 Postgres write latency
- per-scope status
- recommendation
- rollback instruction

该区块读取 `postgres-relation-scoped-observation-quality-report` 最近报告。ControlRoom 不主动启动 observation window，不修改 allowlist，不做 global default on，不迁移历史业务数据。rollback instruction 固定提示禁用对应 scope rule，或切回 `FileSystemPrimary`。

DB2.12 增加 Selected Normal Workspace Canary Summary，只读展示：

- workspace / collection
- provider mode
- operation count
- primary read / write count
- fallback count
- comparison trace count
- mismatch / Postgres failure / scope leak count
- average / P95 Postgres read latency
- average / P95 Postgres write latency
- graph / review / diagnostics / replacement parity
- recommendation
- rollback instruction

该区块读取 `postgres-relation-selected-normal-workspace-canary-report` 最近报告。ControlRoom 不主动启用 selected normal workspace canary，不修改 allowlist，不做 global default on，不迁移历史业务数据。rollback instruction 固定提示移除 selected normal scope allowlist，或切回 `FileSystemPrimary`。

DB2.13 增加 Limited Normal Scope Observation Summary，只读展示：

- selected workspace / collection
- observation window minutes
- operation count
- primary read / write count
- fallback count / fallback rate
- comparison trace count
- mismatch / Postgres failure / scope leak count
- average / P95 Postgres read latency
- average / P95 Postgres write latency
- graph / review / diagnostics / replacement parity
- recommendation
- rollback instruction

该区块读取 `postgres-relation-limited-normal-scope-quality-report` 最近报告。ControlRoom 不主动启动 limited normal observation，不修改 allowlist，不做 global default on，不迁移历史业务数据。rollback instruction 固定提示移除 limited normal scope allowlist，或切回 `FileSystemPrimary`。

DB2.14 增加 Multi Normal Scope Canary Summary，只读展示：

- scope count / enabled scope count
- per-scope operation count
- primary read / write count
- fallback count
- comparison trace count
- mismatch / Postgres failure / scope leak count
- non-allowlisted scope status
- average / P95 Postgres read latency
- average / P95 Postgres write latency
- graph / review / diagnostics / replacement parity
- recommendation
- rollback instruction

该区块读取 `postgres-relation-multi-normal-scope-quality-report` 最近报告。ControlRoom 不主动启动 multi normal scope canary，不修改 allowlist，不做 global default on，不迁移历史业务数据。rollback instruction 固定提示移除受影响 scope rule，或切回 `FileSystemPrimary`。

DB2.F 增加 Relation Governance Freeze 状态，只读展示：

- freeze status
- allowed mode
- forbidden modes
- fallback required
- comparison trace required
- latest readiness report path

该状态来自 `learning-readiness-freeze-report` 和 `relation-governance-postgres-freeze.md`，ControlRoom 不主动启用全局默认 provider。

DB3 增加 Learning Feedback Provider Status，只读展示：

- provider enabled
- connection available
- schema version
- feedback / review / feature candidate table status
- feedback count
- review count
- feature candidate count
- `UseForRuntime=false`

DB3 同时增加 Learning Feedback Parity 状态，只读展示：

- feedback parity
- review parity
- feature candidate parity
- metadata roundtrip
- duplicate feedback upsert
- cleanup performed
- mismatches
- recommendation

ControlRoom 只读取 `postgres-learning-feedback-diagnostics` 和 `postgres-learning-feedback-parity-report` 最近报告，不切换 runtime provider，不训练，不修改 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB3.1 增加 Learning Feedback Readiness / Smoke / Quality 状态，只读展示：

- readiness gate passed / recommendation
- dual-write recommendation
- dual-write mismatch / Postgres failure
- shadow-read recommendation
- shadow-read mismatch / Postgres failure
- provider quality trace count
- provider quality recommendation
- runtime provider still FileSystem

该区块读取以下最近报告：

- `postgres-learning-feedback-readiness-gate`
- `postgres-learning-feedback-dual-write-smoke-report`
- `postgres-learning-feedback-shadow-read-smoke-report`
- `postgres-learning-feedback-provider-quality-report`

DB3.2 后，Postgres Status 还展示 Learning Feedback Scoped Service Mode：

- current mode
- allowlisted workspace / collection
- primary provider
- fallback enabled
- comparison trace enabled
- scoped gate status
- mismatch / failure / fallback count
- export / summary parity
- rollback instruction

该区块只读取：

- `postgres-learning-feedback-scoped-service-mode-smoke-report`
- `postgres-learning-feedback-scoped-service-mode-gate`

ControlRoom 不主动启用 scoped mode，不修改 allowlist，不训练，不调权，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。runtime provider 在非 allowlisted scope 下仍为 FileSystem。

DB3.3 后，Postgres Status 增加 Learning Feedback Selected Normal Scope Canary Summary：

- selected workspace / collection
- provider mode
- operation count
- Postgres primary read / write count
- mismatch / failure / fallback / scope leak count
- export / summary / review summary / feature candidate parity
- recommendation
- rollback instruction

该区块只读取：

- `postgres-learning-feedback-selected-normal-scope-canary-report`

ControlRoom 不主动执行 canary，不修改 selected scope，不训练，不调权，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。non-selected scope 仍必须展示为 FileSystem。

ControlRoom 不主动执行 smoke，不切换 runtime provider，不训练，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB3.4 后，Postgres Status 增加 Learning Feedback Limited Scope Observation Summary 和 Learning Feedback Freeze Status：

- selected workspace / collection
- observation window / operation count
- mismatch / failure / fallback / scope leak
- trainable candidate leak / smoke excluded count
- export / summary / review summary / feature candidate parity
- freeze gate passed / default provider / allowed mode / forbidden modes
- recommendation

该区块只读取：

- `postgres-learning-feedback-limited-scope-observation-report`
- `postgres-learning-feedback-limited-scope-quality-report`
- `postgres-learning-feedback-freeze-gate`

ControlRoom 不主动执行 observation，不修改 allowlist，不训练，不调权，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB4 后，Postgres Status 增加 Job Queue Provider Status：

- provider enabled / connection available
- schema version
- job table exists
- lease / attempt / status index status
- pending / running / failed / dead-letter / stale lease count
- parity recommendation
- lease smoke recommendation
- lease acquire / conflict / expired reacquire count
- retry / dead-letter transition status
- runtime provider still unchanged

该区块只读取：

- `postgres-job-queue-diagnostics`
- `postgres-job-queue-parity-report`
- `postgres-job-queue-lease-smoke-report`

ControlRoom 不主动启用 Postgres job queue，不接入 worker loop，不切换默认 job queue provider，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB4.1 后，Postgres Status 增加 Job Queue Dual-write / Shadow-read Status：

- dual-write smoke recommendation
- dual-write trace count
- dual-write mismatch / Postgres failure / fallback count
- shadow-read smoke recommendation
- shadow-read trace count
- shadow-read mismatch / Postgres failure / fallback count
- provider quality recommendation
- lease / retry / dead-letter / count parity
- runtime worker still unchanged

该区块只读取：

- `postgres-job-queue-dual-write-smoke-report`
- `postgres-job-queue-shadow-read-smoke-report`
- `postgres-job-queue-provider-quality-report`

ControlRoom 不主动执行 smoke，不修改 job queue provider，不接 runtime worker loop，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB4.2 后，Postgres Status 增加 Job Queue Scoped Worker Canary Summary：

- selected workspace / collection
- provider mode
- job count
- completed / retried / dead-letter count
- lease acquire / conflict / expired reacquire count
- heartbeat count
- mismatch / Postgres failure / scope leak count
- non-selected scope FileSystem 状态
- scoped worker quality recommendation
- runtime global provider still unchanged

该区块只读取：

- `postgres-job-queue-scoped-worker-canary-report`
- `postgres-job-queue-scoped-worker-quality-report`

ControlRoom 不主动执行 scoped worker canary，不启动真实 worker loop，不修改全局 job queue provider，不影响非 canary scope，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB4.3 后，Postgres Status 增加 Job Queue Limited Worker Scope Observation Summary：

- selected workspace / collection
- observation window
- completed / retried / dead-letter / cancelled count
- lease conflict / expired reacquire / heartbeat count
- duplicate execution / lease violation count
- Postgres failure / scope leak count
- non-selected scope FileSystem 状态
- limited worker scope quality recommendation
- runtime global provider still unchanged

该区块只读取：

- `postgres-job-queue-limited-worker-scope-observation-report`
- `postgres-job-queue-limited-worker-scope-quality-report`

ControlRoom 不主动执行 limited observation，不启动真实 worker loop，不修改全局 job queue provider，不影响非 canary scope，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB4.F 后，Postgres Status 增加 Job Queue Freeze Status：

- freeze state
- default provider
- allowed mode
- forbidden changes
- last quality gate result
- rollback instruction

该区块只读取：

- `postgres-job-queue-freeze-gate`
- `postgres-job-queue-limited-worker-scope-quality-report`

ControlRoom 不主动启用 scoped worker mode，不修改全局 worker provider，不启动真实生产 worker loop。Rollback 指令是保持 `ExistingProvider` 并移除 scoped allowlist。

DB5.0 后，Postgres Status 增加 Vector Index Provider Status：

- pgvector extension status
- schema / table / index status
- indexed entry count
- supported dimensions
- provider / model / dimension distribution
- compatibility recommendation
- provider smoke recommendation
- dimension mismatch blocked status
- runtime provider still disabled

该区块只读取：

- `postgres-vector-diagnostics`
- `postgres-vector-compatibility`
- `postgres-vector-provider-smoke-report`

ControlRoom 不主动执行 vector reindex，不启用 pgvector retrieval，不修改 vector readiness，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB5.1 后，该区块增加 Vector Parity Summary：

- operation count
- FileSystem / Postgres entry count
- inserted / upserted / deleted count
- query count
- mismatch / ordering mismatch / metadata mismatch
- max score delta
- dimension mismatch blocked
- provider/model mismatch blocked
- cleanup performed
- recommendation

新增读取：

- `postgres-vector-parity-report`

ControlRoom 仍只读展示报告，不绑定 `IVectorIndexStore`，不启用 formal retrieval。

DB5.2 后，该区块增加 PgVector Reindex Summary：

- provider / model / dimension / normalized
- plan insert / update / skip count
- applied insert / update count
- stale / orphan / duplicate count
- metadata roundtrip mismatch count
- indexed entry count after apply
- quality recommendation
- `UseForRuntime=false`

新增读取：

- `postgres-vector-provider-scoped-reindex-plan`
- `postgres-vector-provider-scoped-reindex-apply-report`
- `postgres-vector-provider-scoped-reindex-quality-report`

ControlRoom 仍只读展示报告，不触发 provider-scoped reindex，不绑定 `IVectorIndexStore`，不启用 formal retrieval。

## DB5.3 PgVector Query Preview Summary

ControlRoom 的 Postgres Vector Index Provider Status 增加 PgVector Query Preview 只读摘要。

当前展示：

- query count
- pgvector / temporary FileSystem candidate count
- topK overlap
- ordering mismatch
- score delta max
- metadata mismatch
- eligibility metadata mismatch
- risk projection mismatch
- dimension / provider-model blocking
- `UseForRuntime=false`
- recommendation

当前本地结果：

- `QueryCount=113`
- `PgVectorCandidateCount=1130`
- `FileSystemCandidateCount=1130`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `Recommendation=ReadyForPgVectorShadowEval`

交互边界：

- ControlRoom 只读加载 `storage/postgres/postgres-vector-query-preview-report.json`。
- 不触发 pgvector query preview 执行。
- 不启用 formal retrieval。
- 不改变 package sections 或 `PackingPolicy`。

## DB5.4 PgVector Shadow Eval Summary

ControlRoom 的 Postgres Vector Index Provider Status 增加 PgVector Shadow Eval 只读摘要。

当前展示：

- summary recommendation
- A3 / Extended recall
- A3 / Extended risk after policy
- formal output changed
- topK overlap
- ordering mismatch
- score delta max
- metadata / eligibility / risk projection mismatch
- runtime disabled state

当前本地结果：

- Summary `Recommendation=ReadyForVectorPostgresFreeze`
- A3 `RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- Extended `RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `UseForRuntime=false`

交互边界：

- ControlRoom 只读加载 `storage/postgres/postgres-vector-shadow-eval-*.json`。
- 不触发 pgvector shadow eval 执行。
- 不启用 formal retrieval。
- 不改变 package sections 或 `PackingPolicy`。

## DB5.F Vector Postgres Freeze Status

ControlRoom 的 Postgres Vector Index Provider Status 增加 Vector Postgres Freeze 只读摘要。

当前展示：

- freeze state
- default vector store
- `UseForRuntime`
- formal retrieval allowed
- A3 / Extended recall delta
- risk / formal output changed
- projection mismatch count
- V4 gate requirement
- recommendation

当前冻结状态：

- `VectorPostgresProvider=ReadyForPreviewShadowStorage`
- `DefaultVectorStore=unchanged`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- `FormalOutputChanged=0`
- projection mismatch `0`

交互边界：

- ControlRoom 只读加载 `storage/postgres/postgres-vector-freeze-gate.json`。
- 不触发 freeze gate 执行。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Dataset V2 Generation Summary

ControlRoom Vector Index 页面展示 V3.19 Dataset V2 generation 只读摘要：

- generated corpus/sample count
- validation issue count
- missing evidence/provenance count
- difficulty breakdown
- split breakdown
- recommendation

报告缺失时显示 - status : NoDatasetV2GenerationReport + - action : run eval retrieval-dataset-v2-generate --dry-run。

交互边界：

- ControlRoom 只读加载 `vector/dataset-v2/generated/quality-report.json`，缺失时退回 `generation-report.json`。
- 不触发 dataset 生成。
- 不写 `corpus.jsonl` / `samples.jsonl`。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

## V3.10 Provider Comparison Summary

ControlRoom Vector Index / Postgres Vector Status 可只读展示 Qwen3 provider comparison 报告：

- `vector/providers/qwen3/embedding-provider-smoke.json`
- `vector/providers/qwen3/vector-provider-comparison.json`
- `vector/providers/qwen3/vector-qwen3-readiness-gate.json`
- `vector/providers/qwen3/vector-provider-configuration-sanity-audit.json`
- `vector/providers/qwen3/vector-provider-comparison-freeze.json`
- `vector/providers/qwen3/postgres-vector-query-preview-report.json`
- `vector/providers/qwen3/postgres-vector-shadow-eval-summary.json`

当前展示重点：

- current provider vs Qwen3 provider
- provider configuration sanity status
- provider comparison conclusion
- A3 / Extended recall and MRR
- risk after policy
- pgvector projection parity
- readiness gate recommendation
- formal retrieval allowed=false

交互边界：

- ControlRoom 只读加载 Qwen3 报告。
- 不触发 Qwen3 reindex 或 shadow eval。
- 不启用 formal retrieval，不改变 package sections 或 `PackingPolicy`。
- sanity audit 未通过时只展示 `Inconclusive / BlockedByProviderConfigurationMismatch`，不得把 provider freeze 显示为 `DoNotPromote`。


## Hybrid Retrieval Preview Summary

ControlRoom Vector Index 页面新增 "Hybrid Retrieval Preview Summary" section，展示：

- dense baseline
- hybrid A3 / Extended recall（P2 格式）
- risk after policy
- readiness recommendation
- readiness gate passed 状态

报告缺失时显示 - status : NoHybridPreviewReport + - action : run eval vector-hybrid-preview。

## Hybrid Recall Regression Audit Summary

ControlRoom Vector Index 页面新增 "Hybrid Recall Regression Audit Summary" section，展示：

- audit report source path
- passed 状态
- dense candidate dropped count
- eligibility mismatch count
- dedup overwrite count
- recommendation

报告缺失时显示 - status : NoAuditReport + - action : run eval vector-hybrid-recall-regression-audit。

## Hybrid Retrieval Freeze Status

ControlRoom Vector Index 页面新增 "Hybrid Retrieval Freeze Status" section，展示：

- freeze status
- A3 / Extended recall（来自 preview / audit summary）
- regression audit pass
- block reasons
- V4 recheck allowed
- formal retrieval allowed=false

报告缺失时显示 - status : NoFreezeReport + - action : run eval vector-hybrid-freeze-gate。

交互边界：

- ControlRoom 只读加载 `vector/hybrid/vector-hybrid-freeze-gate.json`。
- 不触发 hybrid preview / shadow eval。
- 不启用 formal retrieval。
- 不改变 package sections 或 `PackingPolicy`。

## Dataset Alignment Audit Summary

ControlRoom Vector Index 页面新增 "Dataset Alignment Audit Summary" section，展示：

- alignment audit source path
- A3 / Extended mustHit corpus coverage
- A3 / Extended provider scope coverage
- eligibility block count
- anchor coverage
- alignment issue count
- top issue breakdown
- recommendation

报告缺失时显示 - status : NoAlignmentAuditReport + - action : run eval vector-retrieval-dataset-alignment-audit。

当前展示来源：

- `vector/alignment/vector-retrieval-dataset-alignment-audit-summary.json`

交互边界：

- ControlRoom 只读加载 alignment audit report。
- 不触发 audit、reindex、shadow eval 或 freeze gate。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

## Eligibility Recall Loss Triage Summary

ControlRoom Vector Index 页面新增 "Eligibility Recall Loss Triage Summary" section，展示：

- triage source path
- filtered mustHit count
- correctly blocked count
- route-to-historical count
- route-to-audit count
- metadata repair needed count
- eval expectation review needed count
- unsafe to recover count
- recommendation

报告缺失时显示 - status : NoEligibilityRecallLossTriageReport + - action : run eval vector-eligibility-recall-loss-triage。

当前展示来源：

- `vector/eligibility/vector-eligibility-recall-loss-triage-summary.json`

交互边界：

- ControlRoom 只读加载 eligibility recall loss triage report。
- 不触发 triage、reindex、shadow eval 或 readiness gate。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

## Lifecycle Metadata Repair Plan Summary

ControlRoom Vector Index 页面新增 "Lifecycle Metadata Repair Plan Summary" section，展示：

- repair plan source path
- candidate count
- auto repairable count
- human review required count
- forbidden repair count
- estimated recall recovery
- risk after repair estimate
- recommendation

报告缺失时显示 - status : NoLifecycleMetadataRepairPlanReport + - action : run eval vector-lifecycle-metadata-repair-plan。

当前展示来源：

- `vector/eligibility/vector-lifecycle-metadata-repair-plan-summary.json`

交互边界：

- ControlRoom 只读加载 lifecycle metadata repair plan。
- 不触发 repair plan、metadata write、reindex、shadow eval 或 readiness gate。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

## Lifecycle Metadata Review Candidates

ControlRoom Vector Index 页面新增 "Lifecycle Metadata Review Candidates" section，展示：

- review candidate report source path
- candidate count
- pending count
- correctly blocked skipped count
- count by layer
- count by itemKind
- recent candidates
- risk if approved / rejected
- recommendation

报告缺失时显示 - status : NoLifecycleMetadataReviewCandidateReport + - action : run eval vector-lifecycle-metadata-review-candidates-generate。

当前展示来源：

- `vector/eligibility/vector-lifecycle-metadata-review-candidates.json`

交互边界：

- ControlRoom 只读加载 lifecycle metadata review candidates report。
- 不提供 approve / reject / needs evidence 等决策操作。
- 不触发 sidecar metadata 写入。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Vector Lifecycle Metadata Review / Sidecar Apply

ControlRoom Vector Index 页面展示 V3.16 review / sidecar 只读摘要：approved / rejected / needs-evidence / superseded 数量、sidecar entry 数量、unsafe approval blocked 数量，以及 normal / audit / historical / diagnostics-only approval 分布。

当前页面不提供 approve/reject 操作入口；人工操作必须经过 detail / explain / evidence/source refs 和 YES confirmation 的专用流程。默认展示仍强调 formal retrieval disabled。

### Sidecar-aware Eligibility Preview Summary

ControlRoom Vector Index 页面展示 V3.17 sidecar-aware eligibility 只读摘要：

- candidate count
- sidecar entry count
- approved sidecar count
- pending review count
- effective metadata changed count
- unsafe / conflict blocked count
- source item unchanged
- recommendation

报告缺失时显示 - status : NoSidecarEligibilityPreview + - action : run eval vector-sidecar-eligibility-preview。

交互边界：

- ControlRoom 只读加载 sidecar eligibility preview/quality report。
- 不触发 sidecar 写入或 review 决策。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Human Review Batch Summary

ControlRoom Vector Index 页面展示 V3.18 human review batch 摘要：

- pending candidate count
- latest batch id / status
- batch candidate count
- validation error count
- apply preview would-write sidecar count
- unsafe blocked count
- recommendation

报告缺失时显示 - status : NoReviewBatch + - action : run eval vector-lifecycle-metadata-review-batch-create。

交互边界：

- ControlRoom 只读加载最新 batch / validation / apply preview 文件。
- 不自动 approve。
- 不写真实 sidecar。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Dataset V2 Materialization Summary

ControlRoom Vector Index 页面展示 V3.19.1 Dataset V2 物化摘要：

- materialization report / gate source
- dataset id
- corpus hash
- samples hash
- gate passed
- corpus / samples hash stable
- recommendation

报告缺失时显示 - status : NoDatasetV2MaterializationReport + - action : run eval retrieval-dataset-v2-materialization-gate。

交互边界：

- ControlRoom 只读加载 materialization report / gate 文件。
- 不执行 Dataset V2 生成。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Dataset V2 Shadow Eval Summary

ControlRoom Vector Index 页面展示 V3.20 Dataset V2 shadow eval 摘要：

- report source
- dataset id
- best profile
- recall / MRR
- risk
- pgvector parity
- recommendation

报告缺失时显示 - status : NoDatasetV2ShadowEvalReport + - action : run eval retrieval-dataset-v2-shadow-eval。

交互边界：

- ControlRoom 只读加载 Dataset V2 shadow eval summary / readiness gate 文件。
- 不执行 shadow eval。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Dataset V2 Stress / Leakage Summary

ControlRoom Vector Index 页面展示 V3.21 Dataset V2 stress / leakage 摘要：

- report source
- dataset id
- corpus / sample count
- split breakdown
- difficulty breakdown
- leakage issue count
- anchor dominance score
- dense / lexical / anchor recall
- hybrid / holdout recall
- recommendation

报告缺失时显示 - status : NoDatasetV2StressReport + - action : run eval retrieval-dataset-v2-stress-shadow-eval。

交互边界：

- ControlRoom 只读加载 stress / leakage / readiness report 文件。
- 不执行 stress generator。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Dataset V2 Stress Failure Triage Summary

ControlRoom Vector Index 页面展示 V3.22 stress recall failure triage 摘要：

- report source
- failure count
- holdout failure count
- failure by split
- failure by difficulty
- failure by reason
- dense-only wins
- hybrid wins
- anchor regression count
- profile comparison summary
- recommendation

报告缺失时显示 - status : NoDatasetV2StressFailureTriageReport + - action : run eval retrieval-dataset-v2-stress-failure-triage。

交互边界：

- ControlRoom 只读加载 stress failure triage report 文件。
- 不执行 triage runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Hybrid Scoring Repair Summary

ControlRoom Vector Index 页面展示 V3.23 hybrid union scoring / ranking repair preview 摘要：

- report source
- best profile
- recall / holdout recall
- dense winner lost count
- mustHit below topK count
- negative distractor / risk count
- recommendation

当前 `post-scoring-risk-gated-v1` 修复后，summary 预期展示：

- best profile : `post-scoring-risk-gated-v1`
- recall / holdout recall : `50.83%` / `75.00%`
- dense winner lost count : `0`
- risk count : `0`
- recommendation : `ReadyForDatasetV2StressFreeze`

报告缺失时显示 - status : NoHybridScoringRepairReport + - action : run eval retrieval-dataset-v2-hybrid-scoring-repair-preview。

交互边界：

- ControlRoom 只读加载 hybrid scoring repair preview / shadow-eval / gate report 文件。
- 不执行 repair runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Hybrid Scoring Risk Triage Summary

ControlRoom Vector Index 页面展示 V3.23.1 hybrid scoring risk regression triage 摘要：

- report source
- repaired profile
- risk count
- risk by type
- risk by split
- must-not promoted count
- eligibility bypass count
- risk projection mismatch count
- recommendation

当前默认 risk triage profile 为 `post-scoring-risk-gated-v1`，预期展示：

- risk count : `0`
- must-not promoted count : `0`
- recommendation : `ReadyForSafeScoringRepair`

旧 `negative-distractor-penalty-v1` 可通过 `--profile negative-distractor-penalty-v1` 复现 V3.23.1 风险定位报告。

报告缺失时显示 - status : NoHybridScoringRiskTriageReport + - action : run eval retrieval-dataset-v2-hybrid-scoring-risk-triage。

交互边界：

- ControlRoom 只读加载 hybrid scoring risk triage report 文件。
- 不执行 risk triage runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。

### Dataset V2 Stress Freeze Status

ControlRoom Vector Index 页面展示 V3.24 Dataset V2 stress freeze 摘要：

- report source
- freeze passed
- DatasetV2Stress 状态
- best preview profile
- stress / holdout recall
- risk / must-not / lifecycle risk
- formal output changed
- leakage / anchor dominance
- V4 recheck input 状态
- formal retrieval allowed=false
- blocked reasons
- recommendation

报告缺失时显示 - status : NoDatasetV2StressFreezeGate + - action : run eval retrieval-dataset-v2-stress-freeze-gate。

交互边界：

- ControlRoom 只读加载 stress freeze gate 文件。
- 不执行 freeze runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。
- `ReadyForV4RecheckInput` 只表示可作为 V4 evaluation input，不表示 runtime ready。

### V4 Readiness Recheck Summary

ControlRoom Vector Index 页面展示 V4.R readiness recheck 摘要：

- report source
- recheck passed
- legacy vector status
- Dataset V2 small / stress status
- pgvector provider status
- runtime-change gate status
- best preview profile
- stress / holdout recall
- risk / formal output changed
- guarded formal preview allowed
- runtime switch allowed=false
- formal retrieval allowed=false
- blocked reasons
- recommendation
- next allowed action

报告缺失时显示 - status : NoVectorV4ReadinessRecheckReport + - action : run eval vector-v4-readiness-recheck。

交互边界：

- ControlRoom 只读加载 `vector/v4/vector-v4-readiness-recheck.json`。
- 不执行 V4.R runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。
- V4.R 通过后只允许进入 GuardedFormalPreview；`ReadyForRuntimeSwitch=false`。

### Guarded Formal Retrieval Preview Summary

ControlRoom Vector Index 页面展示 V4.1 guarded formal retrieval preview 摘要：

- report source
- V4.R status
- preview gate passed
- preview profile
- would add / remove / rerank count
- would route section count
- risk / must-not / lifecycle risk
- formal output changed
- PackingPolicy changed=false
- package output changed=false
- runtime switch=false
- formal retrieval allowed=false
- blocked reasons
- recommendation

报告缺失时显示 - status : NoGuardedFormalRetrievalPreviewReport + - action : run eval vector-guarded-formal-retrieval-preview-gate。

交互边界：

- ControlRoom 只读加载 `vector/v4/vector-guarded-formal-retrieval-preview-gate.json`。
- 不执行 V4.1 runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不改变 package sections 或 `PackingPolicy`。
- V4.1 通过后只允许进入 Shadow Package Comparison；`ReadyForRuntimeSwitch=false`。

### Shadow Package Comparison Summary

ControlRoom Vector Index 页面展示 V4.2 shadow package comparison 摘要：

- report source
- gate passed
- profile
- candidate add / remove / unchanged count
- section changed count
- token delta total / max
- constraint / relation coverage delta
- risk / must-not / lifecycle risk
- formal output changed
- package output changed=false
- PackingPolicy changed=false
- runtime mutation=false
- runtime switch=false
- formal retrieval allowed=false
- blocked reasons
- recommendation

报告缺失时显示 - status : NoVectorShadowPackageComparisonReport + - action : run eval vector-shadow-package-comparison-gate。

交互边界：

- ControlRoom 只读加载 `vector/v4/vector-shadow-package-comparison-gate.json`。
- 不执行 V4.2 runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 package sections 或 `PackingPolicy`。
- V4.2 通过后只允许进入 scoped formal preview opt-in 评估；`ReadyForRuntimeSwitch=false`。

### Scoped Formal Preview Opt-in Summary

ControlRoom Vector Index 页面展示 V4.3 scoped formal preview opt-in 摘要：

- report source
- gate passed
- mode / profile
- workspace allowlist
- collection allowlist
- eval scope allowlist
- preview / baseline package count
- non-allowlisted scope checked / leak count
- risk / formal output changed
- package output changed=false
- PackingPolicy changed=false
- formal package written=false
- runtime mutation=false
- rollback instruction
- blocked reasons
- recommendation

报告缺失时显示 - status : NoScopedFormalPreviewOptInReport + - action : run eval vector-scoped-formal-preview-optin-gate。

交互边界：

- ControlRoom 只读加载 `vector/v4/vector-scoped-formal-preview-optin-gate.json`。
- 不执行 V4.3 runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 package sections 或 `PackingPolicy`。
- V4.3 通过后只允许进入 limited formal preview observation；`ReadyForRuntimeSwitch=false`。

## Vector Index - Limited Formal Preview Observation Summary

Service Vector Index 页面新增 Limited Formal Preview Observation Summary，只读展示：

- observation run count
- preview / baseline package count
- candidate add / remove / section changed count
- token delta total / max / p95
- risk / formal output changed
- package output changed=false
- PackingPolicy changed=false
- formal package written=false
- runtime mutation=false
- non-allowlisted scope leak count
- blocked reasons
- recommendation

报告缺失时显示 - status : NoLimitedFormalPreviewObservationReport + - action : run eval vector-limited-formal-preview-observation-gate。

交互边界：

- ControlRoom 只读加载 `vector/v4/vector-limited-formal-preview-observation-gate.json`。
- 不执行 V4.4 runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 package sections 或 `PackingPolicy`。
- V4.4 通过后只允许进入 formal preview freeze；`ReadyForRuntimeSwitch=false`。

## Vector Index - Formal Preview Freeze Status

Service Vector Index 页面新增 Formal Preview Freeze Status，只读展示：

- freeze passed / status
- allowed mode
- formal retrieval allowed
- ready for runtime switch
- use for runtime
- runtime switch allowed
- risk / formal output
- package output / PackingPolicy invariant
- formal package written / runtime mutated
- non-allowlisted scope leak count
- forbidden changes
- recommendation / blocked reasons

报告缺失时显示：

- status : NoVectorFormalPreviewFreezeReport
- action : run eval vector-formal-preview-freeze-gate

边界：

- ControlRoom 只读加载 `vector/v4/vector-formal-preview-freeze-gate.json`。
- 不执行 V4.F runner。
- 不启用 formal retrieval。
- 不绑定正式 `IVectorIndexStore`。
- 不写正式 package。
- 不改变 package sections 或 `PackingPolicy`。
- V4.F 通过后仍只允许 scoped preview opt-in preview；`ReadyForRuntimeSwitch=false`。

## ControlRoom - Foundation Freeze Summary

Service operational page 新增 Foundation Freeze Summary，只读展示：

- ContextCoreFoundation / StorageFoundation / VectorFoundation
- RelationGovernancePostgres、LearningFeedbackPostgres、JobQueuePostgres、VectorPostgresProvider、VectorFormalPreview 状态
- runtime-change gate / P15 gate 状态
- RuntimeSwitchAllowed / FormalRetrievalAllowed
- package output / `PackingPolicy` invariant
- missing report / missing doc count
- blocked reasons
- next allowed phase

报告缺失时显示：

- status : not generated
- action : run eval foundation-release-candidate-gate

边界：

- ControlRoom 只读加载 `foundation/foundation-release-candidate-gate.json` 或 `foundation/foundation-freeze-report.json`。
- 不执行 foundation runner。
- 不启用 runtime switch。
- 不写正式 package。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Foundation - RC0 Reproducibility Check

RC0 新增 `foundation-reproducibility-check`，用于 release candidate cleanup 和可复现性检查。该命令输出：

- `foundation/foundation-reproducibility-check.json`
- `foundation/foundation-reproducibility-check.md`

检查内容：

- `git status` 分类：source code、tests、docs、generated reports、local config / secrets、model files、temporary files。
- 关键报告存在性：foundation release candidate gate、runtime-change gate、vector formal preview freeze、P15、foundation freeze doc。
- release 边界：formal retrieval、runtime switch、`PackingPolicy`、package output、`UseForRuntime` 均保持 disabled / unchanged。
- local secrets、local DB config、model binaries、temp traces 不应出现在待提交变更中。

RC0 不执行 runtime mutation，不接 formal retrieval，不绑定正式 `IVectorIndexStore`，不写正式 package。

## Service/API - Frozen Foundation Read-only Status

SVC1 新增 frozen foundation 只读 API，供 ControlRoom 和外部运维客户端统一读取 release candidate 状态：

- `GET /api/admin/foundation/status`
- `GET /api/admin/foundation/release-candidate`
- `GET /api/admin/foundation/reproducibility`
- `GET /api/admin/foundation/runtime-change-gate`
- `GET /api/admin/foundation/vector-formal-preview`
- `GET /api/admin/foundation/postgres-freeze-status`

统一 DTO：

- `CapabilityStatus`
- `FoundationServiceStatusResponse`

ControlRoom Service Operational 页面新增统一摘要：

- Foundation / Runtime Gate / Vector Formal Preview / Storage Freeze Summary
- capability count and per-capability state / recommendation
- runtime switch / formal retrieval / package output / `PackingPolicy` invariant

边界：

- API 只读读取既有 JSON 报告。
- 不执行 build/test/P15。
- 不执行 migration、provider switch、formal package write。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

Smoke：

- `eval service-foundation-status-smoke`
- `eval service-readiness-api-smoke`

输出：

- `foundation/service-foundation-status-smoke.json`
- `foundation/service-foundation-status-smoke.md`
- `foundation/service-readiness-api-smoke.json`
- `foundation/service-readiness-api-smoke.md`

## Service/API - Auth, Report Navigation, Degraded Status

SVC2 在 SVC1 只读 API 上增加服务硬化：

- foundation/readiness API 继续走现有 API key middleware；未配置时诊断为 `NotConfigured` / `DevelopmentOnly`，不回显 API key。
- foundation 只读 API 响应统一 envelope：
  - `Success`
  - `CapabilityId`
  - `Status`
  - `Recommendation`
  - `Data`
  - `Diagnostics`
  - `GeneratedAt`
  - `SchemaVersion=foundation-api-envelope-v1`
- 缺失报告返回 `Status=Degraded`、`Recommendation=RegenerateReport`，并在 `Diagnostics.MissingReportIds` 中列出缺失项。
- 报告导航只暴露 repo 相对路径，不返回本地绝对路径、secret 路径或模型二进制路径。

新增只读 API：

- `GET /api/admin/foundation/reports`
- `GET /api/admin/foundation/reports/{reportId}`

新增 DTO：

- `FoundationApiResponseEnvelope<T>`
- `FoundationApiSecurityDiagnosticsReport`
- `FoundationReportNavigationEntry`
- `FoundationReportNavigationResponse`
- `ServiceReportNavigationSmokeReport`

ControlRoom Service Operational 页面新增 Service API Hardening Summary，展示：

- auth configured / api key configured / development mode
- secret leak / absolute path leak
- report navigation count
- degraded report count
- recommendation

Smoke：

- `eval service-api-security-diagnostics`
- `eval service-report-navigation-smoke`

输出：

- `service/service-api-security-diagnostics.json`
- `service/service-api-security-diagnostics.md`
- `service/service-report-navigation-smoke.json`
- `service/service-report-navigation-smoke.md`

边界：

- SVC2 不读取或输出 secret 值。
- API 仍只读读取既有 JSON/Markdown 报告。
- 不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Service/API - OpenAPI and Client Contract Freeze

SVC3 固化 frozen foundation 只读 API / client contract：

- 8 个 foundation endpoint 全部返回 `FoundationApiResponseEnvelope<T>`。
- Envelope schema 固定为 `foundation-api-envelope-v1`。
- Client contract 固定 8 个强类型方法。
- 缺失报告必须返回 degraded envelope，不抛未处理异常。
- Development mode 可显式 `AuthConfigured=false`，production mode 缺 auth 必须 fail。
- forbidden actions 必须在 status/contract 中表达：runtime switch、formal retrieval、formal package write、`PackingPolicy` integration、package output mutation。

新增 CLI：

- `eval service-api-contract-report`
- `eval service-api-contract-freeze-gate`

输出：

- `service/service-api-contract-report.json`
- `service/service-api-contract-report.md`
- `service/service-api-contract-freeze-gate.json`
- `service/service-api-contract-freeze-gate.md`

ControlRoom Service Operational 页面新增 Service API Contract Freeze Summary，展示 endpoint count、client method count、schema version、auth contract、degraded behavior 和 freeze recommendation。

边界：

- SVC3 只做 read-only API/client contract freeze。
- 不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Service/API - Auth Enforcement and Deployment Profile

SVC4 为 frozen foundation read-only API 增加显式部署 profile：

- `Development`
- `Service`
- `Production`

新增配置契约：

- `FoundationServiceAuthOptions.Enabled`
- `FoundationServiceAuthOptions.DeploymentProfile`
- `FoundationServiceAuthOptions.RequireApiKey`
- `FoundationServiceAuthOptions.ApiKeyHeaderName`
- `FoundationServiceAuthOptions.AllowDevelopmentNoAuth`
- `FoundationServiceAuthOptions.RedactSecrets=true`
- `FoundationServiceAuthOptions.FailOnSecretLeak=true`

规则：

- Development profile 允许 no-auth，但必须明确标记 `DevelopmentOnly` / `NotConfigured`。
- Service profile 下 `RequireApiKey=true` 必须配置 API key。
- Production profile 下 `AuthConfigured=false` 必须 gate fail。
- API response / report / log 不允许输出 API key value。
- 报告只允许展示 API key header name，不展示 key value 或本地 secret path。

新增 CLI：

- `eval service-auth-diagnostics`
- `eval service-auth-enforcement-smoke`
- `eval service-deployment-profile-gate`

输出：

- `service/service-auth-diagnostics.json`
- `service/service-auth-diagnostics.md`
- `service/service-auth-enforcement-smoke.json`
- `service/service-auth-enforcement-smoke.md`
- `service/service-deployment-profile-gate.json`
- `service/service-deployment-profile-gate.md`

## SVC5 OpenAPI / Client SDK Contract Snapshot

SVC5 exports and freezes the read-only foundation API contract without changing runtime behavior.

Eval commands:

- `eval service-openapi-contract-export`
- `eval service-client-contract-snapshot`
- `eval service-api-contract-drift-gate`

Generated artifacts:

- `service/openapi/foundation-api.openapi.json`
- `service/openapi/foundation-api-contract-snapshot.json`
- `service/openapi/foundation-client-contract-snapshot.json`
- `service/openapi/service-openapi-contract-report.json`
- `service/openapi/service-openapi-contract-report.md`
- `service/openapi/service-api-contract-drift-gate.json`
- `service/openapi/service-api-contract-drift-gate.md`

The drift gate requires all 8 foundation read-only endpoints, the unified `foundation-api-envelope-v1` envelope, report navigation/degraded schemas, `ApiKeyAuth` header-name-only auth metadata, all frozen client methods, and all forbidden runtime action markers.

ControlRoom shows the Service OpenAPI / Client Contract Snapshot Summary with endpoint count, client method count, schema version, auth scheme, drift status, leak status, and recommendation.

## SVC6 Hosted Service Deployment Smoke

SVC6 adds read-only hosted runtime checks for the same frozen foundation endpoints.

Eval commands:

- `eval service-hosted-deployment-smoke [--base-url <url>]`
- `eval service-readonly-runtime-smoke [--base-url <url>]`
- `eval service-hosted-api-contract-smoke [--base-url <url>]`

Generated artifacts:

- `service/hosted/service-hosted-deployment-smoke.json`
- `service/hosted/service-hosted-deployment-smoke.md`
- `service/hosted/service-readonly-runtime-smoke.json`
- `service/hosted/service-readonly-runtime-smoke.md`
- `service/hosted/service-hosted-api-contract-smoke.json`
- `service/hosted/service-hosted-api-contract-smoke.md`

The hosted smoke is explicitly configuration-sensitive: without `--base-url` or `CONTEXTCORE_SERVICE_BASE_URL`, it reports `NeedsHostedServiceConfig`. With a hosted service URL, it calls all 8 foundation read-only endpoints and verifies auth, envelope schema, no secret/path leak, no runtime mutation, `FormalRetrievalAllowed=false`, `RuntimeSwitchAllowed=false`, `ReadyForRuntimeSwitch=false`, `PackingPolicyChanged=false`, and `PackageOutputChanged=false`.

ControlRoom shows Hosted Service Smoke Summary with base URL, deployment profile, endpoint success/fail, auth check, envelope status, runtime mutation status, and recommendation.

ControlRoom Service Operational 页面新增 Service Auth / Deployment Profile Summary。

边界：

- SVC4 只做 service auth enforcement / deployment profile。
- 不接 formal retrieval，不切 runtime，不绑定正式 `IVectorIndexStore`。
- 不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## SVC.F Service Foundation Freeze

SVC.F 汇总 SVC1-SVC6 和 foundation gates，生成最终 read-only service freeze gate。

新增 CLI：

- `eval service-foundation-freeze-gate`

输出：

- `service/service-foundation-freeze-gate.json`
- `service/service-foundation-freeze-gate.md`

Gate 依赖：

- `service-foundation-status-smoke`
- `service-readiness-api-smoke`
- `service-api-security-diagnostics`
- `service-report-navigation-smoke`
- `service-api-contract-freeze-gate`
- `service-deployment-profile-gate`
- `service-api-contract-drift-gate`
- `service-hosted-deployment-smoke`
- `service-readonly-runtime-smoke`
- `service-hosted-api-contract-smoke`
- `foundation-release-candidate-gate`
- `foundation-reproducibility-check`
- `learning-runtime-change-readiness-gate`
- P15 gate

冻结状态：

- `ServiceFoundation=Frozen`
- `FoundationApi=ReadyForHostedReadOnlyService`
- `OpenApiContract=Frozen`
- `AuthDeploymentProfile=Ready`
- `RuntimeMutationAllowed=false`
- `FormalRetrievalAllowed=false`
- `RuntimeSwitchAllowed=false`
- `ReadyForRuntimeSwitch=false`

ControlRoom Service Operational 页面新增 Service Foundation Freeze Status，展示 SVC1-SVC6 状态、hosted smoke、auth profile、contract drift、runtime mutation 和下一阶段。SVC.F 通过后停止继续扩 SVC 小阶段；下一阶段进入 V4.5 Explicit Scoped Runtime Experiment Planning。

## V4.5 Explicit Scoped Runtime Experiment Plan Summary

ControlRoom Vector Index 页面新增 Explicit Scoped Runtime Experiment Plan Summary。

展示内容：

- selected workspace / collection / eval scope allowlists
- mode：`PlanOnly` / `DryRun`
- preview profile：`post-scoring-risk-gated-v1`
- required gate summary
- allowed actions
- forbidden actions
- rollback plan
- recommendation

该 summary 只读取 `vector/v4/vector-scoped-runtime-experiment-*.json` artifact，不写 runtime，不写正式 package，不改变 `PackingPolicy` 或 package output。缺失报告时展示 `NoExplicitScopedRuntimeExperimentPlanReport` 和对应 eval action。

## V4.6 Scoped Runtime Experiment Dry-run Observation Summary

ControlRoom Vector Index 页面新增 Scoped Runtime Experiment Dry-run Observation Summary。

展示内容：

- observation run count
- workspace / collection / eval scope allowlists
- dry-run / baseline package count
- candidate add / remove
- token delta
- risk / formal output
- formal package / runtime / vector binding mutation
- `PackingPolicy` / package output invariant
- rollback plan availability
- recommendation

该 summary 只读取 `vector/v4/vector-scoped-runtime-experiment-dry-run-observation*.json` artifact，不写 runtime，不写正式 package，不改变 `PackingPolicy` 或 package output。缺失报告时展示 `NoScopedRuntimeExperimentDryRunObservationReport` 和对应 eval action。

## V4.7 Scoped Runtime Experiment Design Freeze Status

ControlRoom Vector Index 页面新增 Scoped Runtime Experiment Design Freeze Status。

展示内容：

- design status
- allowed mode
- forbidden actions
- observation run count
- risk / formal output
- formal package / runtime / vector binding mutation
- `PackingPolicy` / package output invariant
- rollback plan availability
- runtime experiment proposal readiness
- runtime switch status
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/vector-scoped-runtime-experiment-design-freeze-gate.json` artifact，不写 runtime，不写正式 package，不改变 `PackingPolicy` 或 package output。缺失报告时展示 `NoScopedRuntimeExperimentDesignFreezeReport` 和对应 eval action。

## V4.8 Explicit Scoped Runtime Experiment Proposal Summary

ControlRoom Vector Index 页面新增 Explicit Scoped Runtime Experiment Proposal Summary。

展示内容：

- proposal id
- selected workspace / collection / eval scope
- preview profile
- approval required / approved
- runtime / formal retrieval / ready-for-runtime-switch status
- config patch write / DI binding status
- rollback plan
- kill switch plan
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/vector-scoped-runtime-experiment-proposal-gate.json` artifact，不写 runtime，不写正式 package，不改变 `PackingPolicy` 或 package output。缺失报告时展示 `NoScopedRuntimeExperimentProposalReport` 和对应 eval action。

## V4.9 Scoped Runtime Experiment Approval / No-op Harness Summary

ControlRoom Vector Index 页面新增 Scoped Runtime Experiment Approval / No-op Harness Summary。

展示内容：

- proposal id / approval id
- approval count / approval mode
- expired / revoked status
- no-op harness passed status
- no-op trace count
- runtime mutation / vector store binding mutation
- formal package / `PackingPolicy` / package output mutation
- formal retrieval / runtime switch / ready-for-runtime-switch status
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/runtime-experiment/approval-summary.json` 和 `vector/v4/runtime-experiment/noop-harness-*.json` artifact，不提供 approve/reject 操作入口，不写 runtime，不写正式 package，不改变 `PackingPolicy` 或 package output。缺失报告时展示 `NoScopedRuntimeExperimentApprovalReport` 和对应 eval action。

## V4.10 Scoped Runtime Experiment Harness Freeze Status

ControlRoom Vector Index 页面新增 Scoped Runtime Experiment Harness Freeze Status。

展示内容：

- proposal id / approval id
- approval mode
- harness status
- allowed mode
- forbidden actions
- next allowed phase
- runtime / vector binding mutation status
- formal package / `PackingPolicy` / package output invariant
- formal retrieval / runtime switch / ready-for-runtime-switch status
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/runtime-experiment/harness-freeze-gate.json` artifact，不提供 runtime switch、formal retrieval、formal package write 或 DI/vector binding 操作入口。缺失报告时展示 `NoScopedRuntimeExperimentHarnessFreezeReport` 和对应 eval action。

## V4.11 Guarded Scoped Runtime Experiment Plan Summary

ControlRoom Vector Index 页面新增 Guarded Scoped Runtime Experiment Plan Summary。

展示内容：

- proposal id
- selected scopes
- required approval mode
- max request / max duration
- kill switch plan
- rollback plan
- observation plan
- stop conditions
- forbidden actions
- runtime / formal retrieval / ready-for-runtime-switch status
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/runtime-experiment/guarded-runtime-experiment-plan-gate.json` artifact，并在缺失时回退读取 `guarded-runtime-experiment-plan.json`。页面不提供 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation 或 package output mutation 操作入口。缺失报告时展示 `NoGuardedScopedRuntimeExperimentPlanReport` 和对应 eval action。

## V4.12 Scoped Runtime Experiment Approval Gate Summary

ControlRoom Vector Index 页面新增 Scoped Runtime Experiment Approval Gate Summary。

展示内容：

- proposal id / approval id
- approval mode
- approved by
- approval exists / expired / revoked
- acknowledgement status
- runtime / formal retrieval / ready-for-runtime-switch / use-for-runtime status
- formal package write / `PackingPolicy` integration status
- gate result
- next allowed phase
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/runtime-experiment/runtime-approval-gate.json` artifact，并在缺失时回退读取 `runtime-approval-summary.json`。页面不提供 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation 或 package output mutation操作入口。缺失报告时展示 `NoScopedRuntimeExperimentRuntimeApprovalReport` 和对应 eval action。

## V4.13 Activation Preflight / Dry-run Route Summary

ControlRoom Vector Index 页面新增 Activation Preflight / Dry-run Route Summary。

展示内容：

- proposal id / approval id
- mode 与 selected scopes
- kill switch / rollback / trace sink availability
- config patch previewed / written
- dry-run route executed / route hit count
- non-allowlisted scope check / leak count
- runtime mutation / vector store binding mutation / formal package write status
- `PackingPolicy` / package output invariant
- formal retrieval / runtime switch / ready-for-runtime-switch status
- risk / formal output change
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/runtime-experiment/activation-gate.json`，并在缺失时回退读取 `dry-run-route-report.json` 或 `activation-preflight.json`。页面只展示 activation preflight 和 dry-run route 状态，不提供 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation 或 package output mutation操作入口。

## V4.14 Guarded Scoped Runtime Experiment Summary

ControlRoom Vector Index 页面新增 Guarded Scoped Runtime Experiment Summary。

展示内容：

- proposal id / approval id
- mode 与 selected scopes
- request count / experiment route hit count
- non-allowlisted scope leak count
- risk / formal output change
- package output / `PackingPolicy` invariant
- runtime mutation / vector binding mutation / formal package write status
- kill switch availability / triggered status
- rollback verified / error count
- recommendation / blocked reasons

该 summary 只读取 `vector/v4/runtime-experiment/guarded-runtime-experiment-gate.json`，并在缺失时回退读取 `guarded-runtime-experiment-observation.json` 或 `guarded-runtime-experiment-report.json`。页面只展示 shadow runtime experiment 观测状态，不提供 runtime switch、formal retrieval、formal package write、DI/vector binding mutation、`PackingPolicy` mutation、package output mutation、non-allowlisted scope use 或 global default-on 操作入口。
