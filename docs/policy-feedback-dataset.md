# Policy Feedback Dataset

更新时间：2026-06-06

## 目标

Learning Loop Phase 2 新增只读 `PolicyFeedbackDataset`，把既有人工 review history 聚合成可导出的策略反馈样本。该数据集只用于人工分析和后续策略研究输入，不训练模型、不应用策略、不改变 retrieval / planning / `PackingPolicy` / attention / memory / constraints 业务逻辑。

## Baseline Reference

Dataset 固定记录 P15 baseline 引用：

- `docs/eval-baseline-p15.md`
- `eval/eval-report-p15-a3.json`
- `eval/eval-report-p15-extended.json`

P15 gate：

- A3 50 total / 0 failed / 100.00%
- Extended 113 total / 0 failed / 100.00%
- mustNotHit violation = 0
- lifecycle violation = 0
- hard constraint missing = 0

## DTO

`PolicyFeedbackRecord` 字段：

- `FeedbackRecordId`
- `WorkspaceId`
- `CollectionId`
- `SessionId`
- `SourceType`
- `SourceId`
- `Action`
- `Label`
- `Reason`
- `PositiveRefs`
- `NegativeRefs`
- `EvidenceRefs`
- `TargetLayer`
- `CreatedAt`
- `Reviewer`
- `PolicyVersion`
- `Metadata`

`PolicyFeedbackDataset` 字段：

- `DatasetId`
- `Name`
- `Scope`
- `CreatedAt`
- `Records`
- `PositiveCount`
- `NegativeCount`
- `NeutralCount`
- `SourceTypes`
- `PolicyVersion`
- `EvalBaselineRef`

## Aggregation Sources

`PolicyFeedbackDatasetService` 从以下 review history 聚合：

- `PromotionCandidateReviewRecord`
- `StableReviewRecord`
- `ConstraintGapReviewRecord`
- `CandidateConstraintReviewRecord`

第一版不强制落盘复制保存。FileSystem / InMemory store 中已有的 review records 是数据来源，dataset endpoint 每次按查询范围只读聚合。

## Label Mapping

- promotion accept -> `Positive`
- promotion reject -> `Negative`
- stable review accept -> `Positive`
- stable review reject -> `Negative`
- constraint gap accept -> `Positive`
- constraint gap reject -> `Negative`
- candidate constraint activate -> `Positive`
- candidate constraint reject -> `Negative`
- 其他 action -> `Neutral`

正样本 evidence refs 写入 `PositiveRefs`；负样本 evidence refs 写入 `NegativeRefs`；所有样本保留 `EvidenceRefs`。

## Service API

- `GET /api/learning/policy-feedback`
- `GET /api/learning/policy-feedback/export`

查询参数：

- `workspaceId`，必填
- `collectionId`
- `sessionId`
- `limit`
- `offset`

`/export` 返回 JSONL-compatible records，content type 为 `application/x-ndjson`。

## Client

`ContextCoreClient` 新增：

- `GetPolicyFeedbackAsync(workspaceId, collectionId, sessionId, limit, offset)`
- `ExportPolicyFeedbackAsync(workspaceId, collectionId, sessionId, limit, offset)`

## ControlRoom

Service Mode 新增 `Policy Feedback Dataset` 只读页面，入口为 `32`。

页面展示：

- dataset id / scope / policy version / baseline ref
- positive / negative / neutral counts
- source type distribution
- recent policy feedback records

页面没有 accept / reject / activate / train / apply 操作。

## Safety Boundary

本阶段保持：

- 不训练模型
- 不应用策略
- 不改变 retrieval scoring
- 不改变 planning opt-in
- 不改变 `PackingPolicy`
- 不改变 attention behavior
- 不改变 memory / constraints 业务逻辑
- 不写 stable memory
- 不写 ConstraintStore
