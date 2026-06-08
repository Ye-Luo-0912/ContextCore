# ControlRoom Service Mode

更新时间：2026-06-06

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

Service Memory / Service Constraints / Service Stable Review Candidates 页面均支持 `P <id>` provenance，只读展示 stable target 来源链、missing links 与 diagnostics。

Service Relations 页面展示 relation type taxonomy、global relation diagnostics，并支持输入 itemId 查看该 item 的 incoming / outgoing relations 与 item-level diagnostics。也支持 `E <relationId>` 查看 relation explain，展示 confidence、confidence reason、lifecycle、review status、source refs、evidence refs、inverse relation 与 relation-level diagnostics。该页面只读，不做 graph repair，不改变 relation expansion、retrieval、planning 或 `PackingPolicy`。

Service Constraint Gaps 页面展示从 eval/report 生成的 constraint corpus gap candidate，并支持人工 accept / reject。accept 只创建 `Status=Candidate` 的 CandidateConstraint，不创建 Active/Hard constraint，不直接影响 package。

Service Candidate Constraints 页面展示人工 accept gap 后生成的候选约束，并支持人工 activate / reject。activate 才会把 `Status=Candidate`、`Level=User` 的 CandidateConstraint 提升为 `Status=Active`、`Level=Hard`；reject 只更新状态，不删除。

Service Candidate Memory 页面展示中期候选记忆治理快照、recent candidates 与 diagnostics，并支持 detail / explain / manual review cleanup。人工操作只更新 candidate-layer 状态和 review history，不自动晋升 StableMemory，不改变 retrieval、planning、`PackingPolicy` 或 package 输出。

Service Stable Memory 页面展示长期记忆治理快照、recent stable items 与 diagnostics，并支持 detail / explain / provenance / replacement chain / lifecycle review history。页面支持人工 Deprecate / Supersede / Reject，操作前要求输入 `YES`；Supersede 会写入 `superseded_by` / `replaces` relation。该页面不编辑内容、不自动合并、不改变 retrieval、planning、`PackingPolicy` 或 package 输出。

Service Learning Features 页面展示由 policy feedback、P15 eval reports 与 planning shadow reports 只读投影出的 feature dataset summary，包括 feature count、ranking pair count、router intent example count、label distribution、source type distribution、latest export path 与 recent feature examples。Phase 4 增加 Dataset Quality 区块，展示 policy/ranking/router counts、label risks、task readiness 和 recommended next action。该页面不训练模型、不应用策略、不改变 retrieval / planning / package。

Service Ranker Shadow Debug 页面通过 `34` 从主菜单或 Service Dashboard 进入。页面要求输入 query，可选 mode，然后调用 `POST /api/retrieval/ranker-shadow/debug` 展示 candidate score comparison、deprecated / historical demotion reason、current / active promotion reason、version conflict fixes，以及 `FormalOutputChanged=false` / `SelectedSetChanged=false` 状态。页面还会只读展示 Trace Quality Summary 与 Recent Shadow Traces，显示 trace count、candidate score count、risk count、recommended next step、最近 retrieval id、profile、candidate score count、deprecated demotion count 与 query。该页面只读，不编辑配置，不接 runtime scorer，不改变 retrieval output 或 selected set。

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
