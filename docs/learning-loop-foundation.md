# Learning Loop Foundation

更新时间：2026-06-06

## 当前阶段

Promotion Feedback & Learning Case Foundation - Phase 7、Stable Review Readiness - Phase 8、Stable Review Accept / Reject - Phase 9、Stable Provenance Verification - Phase 10 已完成。Learning Loop Phase 2 新增 Policy Feedback Dataset，只读聚合人工 review history，作为后续策略分析输入。Learning Loop Phase 3 新增 Feature Extraction Dataset，从 policy feedback、P15 eval reports 与 planning shadow reports 生成离线 feature examples / ranking pairs / router intent examples。Learning Loop Phase 4 新增 Dataset Quality & Label Coverage Report，用于诊断 feature dataset 的标签覆盖、风险和 task readiness。Learning Loop Phase 5 新增 Offline Baseline Experiment，对 Ready 的 router / ranker 数据集生成离线 majority / rule / simple weighted baseline。

本阶段只记录和聚合人工 review 后的反馈、draft learning case、stable review candidate/decision history、constraint review history、provenance diagnostics、只读 feature dataset、dataset quality report 与 offline baseline report，不训练模型、不应用策略、不改变 retrieval、不改变 planning、不改变 `PackingPolicy`、不改变 attention、不改变 constraints 业务逻辑。Stable Review accept 是显式人工动作，非自动提升。

## Feedback Signal

`PromotionFeedbackSignal` 表示一次短期晋升候选项人工 review 反馈。

字段：

- `FeedbackId`
- `CandidateId`
- `WorkspaceId`
- `CollectionId`
- `SessionId`
- `Action`
- `Reviewer`
- `Reason`
- `SourceWorkingItemId`
- `CreatedTargetItemId`
- `SuggestedTargetLayer`
- `ActualTargetLayer`
- `Confidence`
- `Importance`
- `EvidenceRefs`
- `CreatedAt`
- `Metadata`

生成规则：

- accept -> `Action=Accepted`
- reject -> `Action=Rejected`
- expire 不生成 promotion feedback signal

## Learning Case

`ContextLearningCase` 当前保留既有 learning loop 字段，并扩展 Phase 7 字段：

- `SourceType`
- `SourceId`
- `WorkspaceId`
- `CollectionId`
- `InputSummary`
- `ExpectedBehavior`
- `PositiveRefs`
- `NegativeRefs`
- `FailureType`
- `CorrectionReason`
- `Status`
- `CreatedAt`
- `Metadata`

生成规则：

- accepted candidate -> positive draft learning case
- rejected candidate -> negative draft learning case
- `Status=Draft`
- evidence refs 会进入 `EvidenceRefs`，并按正负样本进入 `PositiveRefs` / `NegativeRefs`

## Stores

InMemory / FileSystem learning store 当前支持：

- feedback signal 保存与查询
- learning record 保存与查询
- learning case 保存与查询

FileSystem 路径：

- `learning/feedback.jsonl`
- `learning/records.jsonl`
- `learning/cases.jsonl`

Policy Feedback Dataset 第一版使用只读聚合实现：从既有 promotion / stable / constraint gap / candidate constraint review stores 汇总，不强制复制保存为新的持久化副本。

Feature Extraction Dataset 第一版使用只读投影实现：从 `PolicyFeedbackDataset`、P15 eval reports 与 planning shadow reports 构造导出样本，不新增在线决策 store。

Dataset Quality Report 第一版只读取 `learning/features/*.jsonl`，输出风险与 readiness，不写入任何业务 store。

Offline Baseline 第一版只读取 `learning/features/router-intent-examples.jsonl` 与 `learning/features/ranking-pairs.jsonl`，输出 `learning/baselines/*baseline-report.*`，不写入任何业务 store，也不接入 runtime。

## Service API

只读 endpoint：

- `GET /api/learning/feedback`
- `GET /api/learning/policy-feedback`
- `GET /api/learning/policy-feedback/export`
- `GET /api/learning/features`
- `GET /api/learning/features/export`
- `GET /api/learning/features/quality`
- `GET /api/learning/cases`
- `GET /api/learning/cases/{id}`

`GET /api/learning/feedback` 支持：

- `workspaceId`
- `collectionId`
- `sessionId`
- `candidateId`
- `action`
- `limit`
- `offset`

`GET /api/learning/policy-feedback` 支持：

- `workspaceId`
- `collectionId`
- `sessionId`
- `limit`
- `offset`

`GET /api/learning/policy-feedback/export` 返回 JSONL-compatible records，content type 为 `application/x-ndjson`。

`GET /api/learning/features` 支持：

- `workspaceId`
- `collectionId`
- `sessionId`
- `limit`
- `offset`

`GET /api/learning/features/export` 默认导出：

- `learning/features/policy-feedback-features.jsonl`
- `learning/features/ranking-pairs.jsonl`
- `learning/features/router-intent-examples.jsonl`

`GET /api/learning/features/quality` 默认读取 `learning/features`，也支持 `featureDirectory`。

## Client

`ContextCoreClient` 当前提供：

- `GetLearningFeedbackAsync`
- `GetPolicyFeedbackAsync`
- `ExportPolicyFeedbackAsync`
- `GetLearningFeaturesAsync`
- `ExportLearningFeaturesAsync`
- `GetLearningDatasetQualityAsync`
- `GetLearningCasesAsync`
- `GetLearningCaseAsync`

## ControlRoom

Service Mode Learning 页面展示：

- promotion feedback signals
- learning records
- draft learning cases
- active regression cases
- failure type summary

Service Mode Policy Feedback Dataset 页面展示：

- dataset id / scope / policy version / P15 eval baseline ref
- positive / negative / neutral counts
- source type distribution
- recent policy feedback records

Service Mode Learning Features 页面展示：

- feature count / ranking pair count / router intent example count
- label distribution
- source type distribution
- latest export path
- recent feature examples
- dataset quality counts / risks / task readiness / recommended next action

Phase 7 展示是只读观测，不触发训练、不应用策略、不改变 retrieval。

Phase 2 Policy Feedback Dataset 同样是只读观测，不训练模型、不应用策略、不改变 retrieval / planning / package。

Phase 3 Feature Extraction Dataset 也是只读投影，不训练模型、不应用策略、不改变 retrieval / planning / `PackingPolicy` / attention / constraints。

Phase 4 Dataset Quality Report 只做离线质量诊断，不训练模型、不应用策略、不改变 retrieval / planning / `PackingPolicy` / attention / constraints。

Phase 5 Offline Baseline Experiment 只做离线基线评估，不训练生产模型、不接入 runtime、不改变 retrieval / planning / `PackingPolicy` / attention / constraints。

## Policy Feedback Dataset

`PolicyFeedbackRecord` 表示从人工 review history 派生的一条策略反馈样本：

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

`PolicyFeedbackDataset` 聚合字段：

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

映射规则：

- promotion accept -> positive feedback
- promotion reject -> negative feedback
- stable review accept -> positive feedback
- stable review reject -> negative feedback
- constraint gap accept -> positive feedback
- constraint gap reject -> negative feedback
- candidate constraint activate -> positive feedback
- candidate constraint reject -> negative feedback

Dataset 的 `EvalBaselineRef` 指向 P15 freeze：`docs/eval-baseline-p15.md`、`eval/eval-report-p15-a3.json`、`eval/eval-report-p15-extended.json`。

## Feature Extraction Dataset

`ContextPolicyFeatureExample` 表示从 feedback / planning shadow 派生的策略特征样本，字段覆盖 source、task kind、mode、intent、label、candidate layer/status/importance/recency、channel sources、match scores、lifecycle risk、selected/accepted/rejected 与 evidence refs。

`RankingPairExample` 表示从 eval report 派生的 ranking pair：

- `mustHit` -> positive candidate
- `mustNotHit` -> negative candidate
- `FeatureSnapshot` 记录 selected、rank、score、section、constraint/entity/uncertainty 覆盖状态

`LearningFeatureDatasetService` 当前生成：

- policy feedback features
- eval ranking pairs
- router intent examples

CLI 导出：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval export-learning-features
```

默认输出：

- `learning/features/policy-feedback-features.jsonl`
- `learning/features/ranking-pairs.jsonl`
- `learning/features/router-intent-examples.jsonl`

## Dataset Quality & Label Coverage

Phase 4 新增 `LearningDatasetQualityReport`：

- `PolicyFeedbackFeatureCount`
- `RankingPairCount`
- `RouterIntentExampleCount`
- `PositiveCount`
- `NegativeCount`
- `NeutralCount`
- `SourceTypeCounts`
- `ModeCounts`
- `IntentCounts`
- `LabelCounts`
- `DataRisks`
- `TaskReadiness`

风险枚举：

- `NoPolicyFeedback`
- `EvalOnlyDataset`
- `ClassImbalance`
- `MissingNegativeSamples`
- `LowIntentCoverage`
- `LowModeCoverage`

Readiness gates：

- `RouterIntentClassifier`
- `CandidateReranker`
- `PromotionJudge`
- `ConstraintGapJudge`
- `AttentionScorer`

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-dataset-quality
```

默认输出：

- `learning/features/dataset-quality-report.json`
- `learning/features/dataset-quality-report.md`

## Target Consistency

accept 后执行 target consistency validation：

- `CreatedTargetItemId` 必须存在
- target item 必须带 source candidate ref
- target status 必须是 `Candidate`
- target 不得直接 Stable

## Stable Review Readiness

Phase 8 新增 `StableReviewCandidate`，从 accepted promotion candidate 与 candidate-layer target 派生，作为进入 Stable review 的人工准备视图。

生成来源：

- accepted promotion candidate
- accept 创建的 candidate-layer memory / constraint target
- positive draft learning case（如存在，则记录 `SourceLearningCaseId`）

规则：

- `CandidateMemory -> StableMemory`
- `ConstraintCandidate -> StableConstraint`
- `DecisionRecord -> DecisionRecord`
- `OpenIssue / KnownIssue` 默认不直接生成，除非 candidate metadata 显式允许 stable review

validation：

- evidence refs missing -> `NeedsMoreEvidence`
- source target missing -> `SourceTargetMissing`
- target not `Candidate` -> `TargetNotCandidate`
- existing stable memory / constraint already references same source -> `DuplicateStableCandidate`
- target scope 与 source candidate 不一致 -> `ScopeMismatch`
- non-accepted source candidate -> `LifecycleConflict`

Service API：

- `POST /api/memory/stable-review/candidates/generate`
- `GET /api/memory/stable-review/candidates`
- `GET /api/memory/stable-review/candidates/{id}`
- `GET /api/memory/stable-review/candidates/{id}/explain`
- `POST /api/memory/stable-review/candidates/{id}/accept`
- `POST /api/memory/stable-review/candidates/{id}/reject`
- `GET /api/memory/stable-review/candidates/{id}/reviews`

Stable Review accept 会重新执行 validation，通过后写入 stable target：

- `StableMemory`
- `StableConstraint`
- `DecisionRecord`

写入 stable item 时保留 stable review candidate、promotion candidate、learning case、working item、evidence refs、reviewer、review reason 与 policy version。reject 只更新 Stable Review candidate status 并记录 `StableReviewRecord`，不删除 candidate，不写 stable target。

该链路不触发训练或策略更新，不改变 retrieval。

## Stable Provenance Verification

Phase 10 为 stable review accept 生成的 stable target 标准化 provenance metadata：

- `sourceStableReviewCandidateId`
- `sourcePromotionCandidateId`
- `sourceLearningCaseId`
- `sourceWorkingItemId`
- `sourceFeedbackId`
- `evidenceRefs`
- `reviewer`
- `reviewReason`
- `policyVersion`
- `createdFrom=stable_review_accept`

统一查询入口：

- `GET /api/provenance/{itemId}`
- `ContextCoreClient.GetProvenanceAsync(itemId)`

provenance response 聚合 target item、Stable Review candidate、promotion candidate、feedback signal、learning case、source working item、evidence refs、review history、warnings / missing links。

只读 stable diagnostics：

- duplicate stable warning
- possible conflict warning
- missing source link warning

Diagnostics 只用于 review readiness 和审计展示，不自动修复、不训练模型、不应用策略。
