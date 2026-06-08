# Context Learning Loop

更新时间：2026-06-03

## 当前范围

Phase 2 建立规则型 learning case 生成与人工状态流转：

- 记录 promotion review 产生的反馈
- 保存 learning records / learning cases
- 从 learning records 生成可回放 learning cases
- 支持 Draft / ActiveRegression / Archived / Rejected 状态
- 提供 Service API、Client 和 ControlRoom 操作入口
- 不训练模型
- 不自动调参
- 不改变 retrieval 语义

## 模型

`ContextLearningRecord` 用于记录一次反馈：

- `accept -> PromotionAccepted / Positive`
- `reject -> PromotionRejected / Negative`
- `expire -> PromotionExpired / Stale`

`ContextLearningCase` 用于沉淀可回放案例：

- `PromotionAccepted -> PositivePromotionSample`
- `PromotionRejected -> PromotionFalsePositive`
- `PromotionExpired -> StaleContextSample`

`ContextLearningCaseStatus`：

- `Draft`
- `Candidate`
- `ActiveRegression`
- `Archived`
- `Rejected`

`ContextFeedbackSignal`：

- `Positive`
- `Negative`
- `Stale`

`ContextFailureType`：

- `None`
- `PromotionFalsePositive`
- `PromotionFalseNegative`
- `PromotionExpired`
- `StaleCandidate`
- `Unknown`

## 存储

已支持：

- `IContextLearningStore`
- `FileContextLearningStore`
- `InMemoryContextLearningStore`

FileSystem 路径：

- `learning/records.jsonl`
- `learning/cases.jsonl`

## API

Service endpoint：

- `GET /api/learning/records`
- `GET /api/learning/records/{id}`
- `GET /api/learning/cases`
- `GET /api/learning/cases/{id}`
- `POST /api/learning/cases`
- `POST /api/learning/cases/generate`
- `POST /api/learning/cases/{id}/activate`
- `POST /api/learning/cases/{id}/archive`
- `POST /api/learning/cases/{id}/reject`
- `GET /api/learning/summary`
- `GET /api/learning/regression/cases`

Client 方法：

- `QueryLearningRecordsAsync`
- `GetLearningRecordAsync`
- `QueryLearningCasesAsync`
- `GetLearningCaseAsync`
- `CreateLearningCaseAsync`
- `GenerateLearningCasesAsync`
- `ActivateLearningCaseAsync`
- `ArchiveLearningCaseAsync`
- `RejectLearningCaseAsync`
- `GetLearningSummaryAsync`
- `GetRegressionLearningCasesAsync`

错误响应继续统一使用 `ContextCoreErrorResponse`。

## ControlRoom

Service Mode 新增 Learning 页面：

- 展示 records / cases
- 展示 positive / negative / stale 统计
- 展示 recent feedback
- 展示 failure type summary
- 展示 learning summary
- 展示 active regression cases
- 支持查看单条 record / case
- 支持生成 cases
- 支持 activate / archive / reject case

主菜单入口：

- `H`
- `26`

## 当前边界

当前不做：

- trace interpreter
- LLM judge
- 模型训练
- 自动调参
- 自动 promotion
- 自动写 StableMemory

## Attention Shadow Feedback

`ContextAttentionScorer` 会以 shadow mode 读取 learning records：

- `Positive` 反馈提供 attention boost
- `Negative` 反馈提供 noise penalty
- `Stale` 反馈提供 stale penalty

该反馈只写入 retrieval trace 的 attention breakdown 和 shadow rank/diff report，不改变 retrieval 排序、packing 或 package 输出。

Phase 2 的 eval report 会记录：

- attention MRR / Recall@3 / Recall@5
- attention improved / regressed sample count
- must-not-hit 被 attention 上推的次数
- selected set change ratio

这些指标用于人工判断 feedback 对 attention scorer 的影响，不触发自动调参。
