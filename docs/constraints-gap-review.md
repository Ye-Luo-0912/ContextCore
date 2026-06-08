# Constraint Corpus Gap Review

更新时间：2026-06-05

## 目标

Phase P12 用于把 eval / planning opt-in 中发现的 hard constraint 缺口整理成可复核候选项。

Phase P13 增加 gap 人工 review / accept / reject。默认生成流程仍只生成 `ConstraintGapCandidate`，不写入 `ConstraintStore`；只有人工 accept 后才创建 `Status=Candidate` 的 `CandidateConstraint`。该候选约束不是 Active/Verified/Stable，不直接影响 package。

Phase P14 增加 CandidateConstraint 人工 review / activate / reject。只有人工 activate 后，CandidateConstraint 才会变为 `Status=Active`、`Level=Hard` 的 active hard constraint。activate 前会检查状态、来源字段与 duplicate active constraint。

Phase P15 使用该正式链路关闭 `chat-20260529-003` 的 extended eval constraint gap：eval fixture 只提供 `ConstraintGapCandidate`，由 `accept -> CandidateConstraint`、`activate -> Active/Hard Constraint` 产生约束，不在 resolver 中写 alias 硬判通过。

本能力不改变 retrieval scoring、PackingPolicy、planning opt-in intent 或 vector/router 配置。

## 数据模型

`ConstraintGapCandidate` 字段包括：

- `GapId`
- `WorkspaceId` / `CollectionId` / `SessionId`
- `Source` / `SourceSampleId` / `SourceOperationId`
- `ExpectedConstraintText`
- `MatchedConstraintIds`
- `SuggestedConstraintTitle`
- `SuggestedConstraintScope`
- `SuggestedConstraintType`
- `Severity`
- `Reason`
- `EvidenceRefs`
- `Status`
- `CreatedAt`
- `Metadata`

默认状态为 `Pending`。review 后状态变为 `Accepted` 或 `Rejected`。默认 suggested type 为 `Hard`，scope 为 `Collection`。

P13 还新增：

- `ConstraintGapReviewRequest`
- `ConstraintGapReviewResult`
- `ConstraintGapReviewRecord`

P14 还新增：

- `CandidateConstraintQuery`
- `CandidateConstraintReviewRequest`
- `CandidateConstraintReviewResult`
- `CandidateConstraintReviewRecord`

## 生成规则

`ConstraintGapCandidateService` 从以下 report 中读取 missing hard constraint：

- `planning-optin-constraint-safety-report-*.json`
- `extended-failure-triage-report.json`

生成前会查询 `IConstraintStore`：

- 如果 scope 匹配的 hard constraint 内容已覆盖 expected text，则跳过。
- 如果没有匹配项，则创建 Pending gap。
- 去重键为：`workspace + collection + expectedConstraintText + sourceSampleId`。

generate 流程不会调用 `IConstraintStore.SaveAsync`。

## Review 行为

Accept：

- `Pending -> Accepted`
- 创建一个 `ContextConstraint` 候选项。
- 约束 `Status=Candidate`，`Level=User`，metadata 保留 `suggestedConstraintType=Hard`，用于避免直接进入 hard / soft constraint 常规 package 路径。
- 返回 `CreatedConstraintId`。
- 记录 `ConstraintGapReviewRecord`。

CandidateConstraint metadata 包含：

- `sourceConstraintGapId`
- `sourceConstraintGapReviewId`
- `sourceSampleId`
- `sourceOperationId`
- `expectedConstraintText`
- `reviewer`
- `reviewReason`
- `evidenceRefs`
- `createdFrom=constraint_gap_accept`
- `status=Candidate`

Reject：

- `Pending -> Rejected`
- 不删除 gap。
- 不写 `ConstraintStore`。
- 记录 reviewer / reason / reviewedAt 和 review history。

非 `Pending` gap 不能重复 accept / reject。

## CandidateConstraint Review 行为

Query / detail：

- 读取人工 accept 生成的 CandidateConstraint。
- 默认查询 `Status=Candidate`。
- 支持按 workspace / collection / status / limit / offset 查询。

Activate：

- `Candidate -> Active`
- 不允许非 `Candidate` 状态激活。
- 不允许内容重复的 `Active` + `Hard` constraint。
- 激活后 `Status=Active`，`Level=Hard`。
- metadata 写入 `createdFrom=candidate_constraint_activate`。
- 原始 `createdFrom=constraint_gap_accept` 保留为 `candidateCreatedFrom`。
- 必须保留 `sourceConstraintGapId` / `sourceConstraintGapReviewId` / `sourceSampleId` / `sourceOperationId` / `evidenceRefs`。
- metadata 写入 `sourceCandidateConstraintReviewId`，用于追踪 activate review。
- 记录 reviewer / reviewReason / activatedAt 和 review history。

Reject：

- `Candidate -> Rejected`
- 不删除 constraint。
- 记录 reviewer / reason / reviewedAt 和 review history。

Activate 是显式人工操作；系统不会从 gap accept 自动激活 CandidateConstraint。

## P15 Eval Closure Fixture

`chat-20260529-003` 的明确 hard constraint fixture 语义为：

> 重复解释、重复澄清、重复说明本身不应被提升为长期偏好或稳定事实；只有用户明确确认其为长期规则时才可提升。

该 fixture 存在于 extended corpus 的 `activatedConstraintGaps`，而不是 `constraints`。eval runner 在目标 sample 运行时执行与 Service 相同的正式链路：

1. 保存 `ConstraintGapCandidate`。
2. 调用 `ConstraintGapCandidateService.AcceptAsync(...)` 创建 `Status=Candidate` 的 CandidateConstraint。
3. 调用 `CandidateConstraintReviewService.ActivateAsync(...)` 激活为 `Status=Active`、`Level=Hard`。
4. package builder 从 active hard constraint store 注入 constraints section。

隔离规则：

- fixture 按 `SourceSampleId` 只作用于对应 sample。
- sample package build 完成后重置 fixture constraint，避免影响同一 category 中其他样本。
- 不修改 retrieval scoring，不修改 `PackingPolicy`，不扩 planning opt-in intent。
- 不在 eval resolver 中加入 alias 硬判；expected constraint 必须真实出现在 constraints/package section。

当 expected constraint 仍缺失时，eval diagnostic 会输出：

- `constraintExists`
- `constraintSelected`
- `constraintRenderedInConstraintsSection`
- `constraintDroppedByBudget`
- `constraintTextMatchedExpected`

## API

- `POST /api/constraints/gaps/generate`
- `GET /api/constraints/gaps`
- `GET /api/constraints/gaps/{id}`
- `POST /api/constraints/gaps/{id}/accept`
- `POST /api/constraints/gaps/{id}/reject`
- `GET /api/constraints/gaps/{id}/reviews`
- `GET /api/constraints/candidates`
- `GET /api/constraints/candidates/{id}`
- `POST /api/constraints/candidates/{id}/activate`
- `POST /api/constraints/candidates/{id}/reject`
- `GET /api/constraints/candidates/{id}/reviews`

`generate` request 可传入 report 路径；未传入时默认尝试读取 repo 当前工作目录下的 `eval` report。

## ControlRoom

Service Mode 新增 Constraint Gaps 页面：

- Service Dashboard 输入 `C`
- 主 dashboard 可用编号 `30`
- 展示 expected constraint、source sample / operation、suggested scope / type、evidence refs
- `S <id>` detail
- `A <id>` accept
- `R <id>` reject
- `H <id>` history
- accept / reject 前展示 detail，并要求输入 `YES`

ControlRoom 不提供自动批量 accept，也不把 CandidateConstraint 提升为 Active/Hard。

Service Mode 还新增 Candidate Constraints 页面：

- Service Dashboard 输入 `E`
- 主 dashboard 可用编号 `31`
- 默认展示 `Status=Candidate` 的 CandidateConstraint
- `F` filter status / limit / offset
- `S <id>` detail
- `A <id>` activate
- `R <id>` reject
- `H <id>` history
- activate / reject 前展示 detail，并要求输入 `YES`

ControlRoom 不提供自动批量 activate；activate / reject 都必须人工确认。
