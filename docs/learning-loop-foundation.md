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

## Router Intent Classifier Offline Baseline R1

R1 在 Policy Feedback / Feature Dataset / Offline Baseline 之后补充 router intent classifier 的独立离线评估。

新增内容：

- `RouterIntentClassifier` 抽象
- `ExistingRuleBasedRouterBaseline`
- `TokenCentroidRouterBaseline`
- `RouterIntentEvaluationRunner`
- CLI：`eval router-intent-baseline`

输入：

- `learning/features/router-intent-examples.jsonl`

输出：

- `learning/router/router-intent-baseline-report.json`
- `learning/router/router-intent-baseline-report.md`
- `learning/router/router-intent-confusion-matrix.json`

报告包含 accuracy、macroF1、per-intent precision / recall、confusion matrix、low confidence、abstain、关键 intent recall 与 recommendation。

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy`。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。

## Router Intent Shadow Trace / Disagreement Analysis R2

R2 将 R1 的 shadow classifier 接入旁路观测，但不替换 runtime router，也不改变 planning / retrieval / package 输出。

新增配置：

```json
{
  "Learning": {
    "RouterShadow": {
      "Enabled": false,
      "TraceCollectionEnabled": false,
      "ShadowClassifier": "TokenCentroidRouterBaseline",
      "RecordAgreements": true,
      "RecordDisagreements": true
    }
  }
}
```

新增 API：

- `GET /api/learning/router-shadow/traces`
- `GET /api/learning/router-shadow/traces?format=jsonl`

新增 CLI：

- `eval router-intent-shadow-eval`
- `eval router-shadow-trace-quality`

输出：

- `learning/router/router-intent-shadow-eval-a3.json`
- `learning/router/router-intent-shadow-eval-extended.json`
- `learning/router/router-intent-shadow-eval.md`
- `learning/router/router-shadow-trace-quality-report.json`
- `learning/router/router-shadow-trace-quality-report.md`

当前离线 shadow eval 结果：

- `A3 samples=35`
- `AgreementRate=88.57%`
- `ShadowFixesRuntime=1`
- `ShadowBreaksRuntime=3`
- `Recommendation=KeepRuleBased`

当前 runtime trace quality 结果：

- `TraceCount=0`
- `Recommendation=NeedsMoreRealTraces`

边界：

- 默认关闭。
- trace 只写旁路记录，`formalOutputChanged=false`。
- 不读取 mustHit / mustNotHit label。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。

## Router Disagreement Triage / Intent Boundary Repair R2.1

R2.1 在 R2 的离线 disagreement 结果上增加分诊、intent boundary 文档和 router hard negative 数据集。

新增 CLI：

- `eval router-disagreement-triage`

新增输出：

- `learning/router/router-disagreement-triage-a3.json`
- `learning/router/router-disagreement-triage-extended.json`
- `learning/router/router-disagreement-triage.md`
- `learning/router/router-hard-negatives.jsonl`
- `docs/router-intent-boundaries.md`

每条 disagreement 记录包含：

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

`router-hard-negatives.jsonl` 只用于离线分析。它记录 expected intent 作为正类，错误 runtime/shadow intent 作为负类，并按 query / mode / intent / source sample 去重，避免重复数据污染后续训练集。

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy` / package output。

## Candidate Reranker Shadow CR1

CR1 新增 candidate reranker / lifecycle-aware ranker 的 shadow eval 与 trace quality gate。该阶段只读取 eval diagnostics 或 runtime ranker shadow trace，不替换正式 scorer，不改变 retrieval selected set，不改变 `PackingPolicy`，不改变 package sections。

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

- A3：`NetGain=-49`，`WouldImprove=0`，`WouldRegress=49`，`RiskCount=178`，`Recommendation=BlockedByRisk`
- Extended：`NetGain=-18`，`WouldImprove=24`，`WouldRegress=42`，`RiskCount=50`，`Recommendation=BlockedByRisk`
- Runtime trace quality：`TraceCount=0`，`Recommendation=NeedsMoreRealTraces`

结论：

- Candidate reranker 数据集 readiness 仍可用于离线分析。
- 当前 lifecycle-aware candidate reranker shadow 不能进入 guarded opt-in。
- 下一步需要真实 trace 采集和 feature/risk tuning，而不是接入正式 ranking。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。
- hard negative 只做离线数据增强建议，不自动进入 runtime policy。

## Candidate Reranker Shadow CR1.1

CR1.1 新增 candidate reranker shadow failure audit 与 score contract audit。该阶段只解释 CR1 中的回归、风险候选和 score contract 状态，不进入 runtime trace，不替换正式 scorer，不改变 retrieval / selected set / `PackingPolicy` / package output。

新增 CLI：

- `eval candidate-reranker-shadow-failure-audit`

输出：

- `learning/ranker/candidate-reranker-shadow-failure-audit-a3.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit-extended.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit.md`

报告新增：

- `ScoreContractStatus`
- `RankableCandidateCount`
- `BlockedCandidateCount`
- `RiskCandidateInShadowTopK`
- `RegressionReasonSummary`
- 每条 regression 的 `whyShadowPromoted` / `regressionReason` / `recommendedAction`

边界：

- eval label 只用于报告评估，不进入 policy。
- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 不为通过率调正式 scorer 或 package 行为。

## Candidate Reranker Metadata Alignment CR1.2

CR1.2 新增候选特征 envelope、rerank 前 eligibility guard 与 feature completeness eval。该阶段只对 shadow runner 的候选元数据进行对齐和诊断，不替换正式 scorer，不改变 retrieval selected set，不改变 `PackingPolicy`，不改变 package output。

新增模型与服务：

- `CandidateFeatureEnvelope`
- `CandidateFeatureEnvelopeBuilder`
- `RankerCandidateEligibilityGuard`
- `RankerCandidateEligibilityDecision`
- `CandidateRerankerFeatureCompletenessReport`

新增 CLI：

- `eval candidate-reranker-feature-completeness`

输出：

- `learning/ranker/candidate-reranker-feature-completeness-a3.json`
- `learning/ranker/candidate-reranker-feature-completeness-extended.json`
- `learning/ranker/candidate-reranker-feature-completeness.md`

报告字段：

- `RawCandidateCount`
- `RankableCandidateCount`
- `BlockedCandidateCount`
- `FeatureCompletenessRate`
- `MissingFeatureMetadataCount`
- `RiskCandidateInRawTopK`
- `RiskCandidateInShadowTopK`
- `RiskCandidateBlockedBeforeRerank`
- `EligibilityGuardStatus`
- `ScoreContractStatus`

当前结果：

- A3 feature completeness：`88.02%`，`RiskCandidateBlockedBeforeRerank=152`
- Extended feature completeness：`95.02%`，`RiskCandidateBlockedBeforeRerank=373`
- A3 shadow eval：`RiskCandidateInShadowTopK=0`，`ScoreContractStatus=Passed`，`Recommendation=KeepFormalRanking`
- Extended shadow eval：`RiskCandidateInShadowTopK=0`，`ScoreContractStatus=Passed`，`Recommendation=KeepFormalRanking`

边界：

- Feature envelope builder 只读取 runtime metadata、lifecycle / reviewStatus、provenance、replacement chain、diagnostics 和 source refs。
- Eligibility guard 只把 shadow rerank 的候选分为 `Rankable`、`Blocked`、`AuditOnly`、`DiagnosticsOnly`。
- eval label 只用于报告评估，不进入 guard 或 feature builder。
- 不使用 sampleId / itemId / fixture / 领域词表特判。
- 当前仍保持 formal ranking，不进入 runtime trace 或 guarded opt-in。

## Candidate Reranker Calibration CR1.3

CR1.3 新增 ranker score distribution 与 listwise calibration audit。该阶段只读取 CR1.2 guard-aware shadow eval，分析 score scale、top1 margin、listwise regression 和 formal ranking implicit priority，不替换正式 scorer，不改变 retrieval selected set，不改变 `PackingPolicy`，不改变 package output。

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

- A3 score distribution：`ScoreMean=55.4941`，`ScoreStdDev=30.1902`，`LowMarginDecisionCount=0`，`Recommendation=NeedsFeatureTuning`
- Extended score distribution：`ScoreMean=37.2669`，`ScoreStdDev=24.1415`，`LowMarginDecisionCount=3`，`Recommendation=NeedsFeatureTuning`
- A3 listwise calibration：`RegressionCount=20`，`LowMarginDecisionCount=5`，`FormalPriorityMismatchCount=3`，`Recommendation=NeedsFeatureTuning`
- Extended listwise calibration：`RegressionCount=30`，`LowMarginDecisionCount=1`，`FormalPriorityMismatchCount=27`，`Recommendation=NeedsFeatureTuning`

结论：

- Score contract 已通过，但 listwise ranking 仍缺少足够的 intent / mode / formal priority 表达。
- 当前仍保持 `KeepFormalRanking`，不进入 runtime trace 或 guarded opt-in。
- eval label 只用于 audit/report，不进入 ranker policy。
- 不使用 sampleId / itemId / fixture / 领域词表特判。

## Candidate Reranker Formal Priority Alignment CR1.4

CR1.4 新增 formal priority feature alignment 与 shadow-only listwise repair。该阶段只在 shadow eval 中提取 formal priority features，并通过 shadow profile 对比 baseline / formal-priority-aware / formal-priority-aware-with-abstain，不替换正式 scorer，不改变 retrieval selected set，不改变 `PackingPolicy`，不改变 package output。

新增能力：

- `FormalPriorityFeatureExtractor`
- `CandidateRerankerFormalPriorityAlignmentRunner`
- shadow-only profiles：
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

当前结果：

- A3 formal priority alignment：`RegressionCount=20`，`RecoveredCount=0`，`UnexplainedMismatchCount=20`，`AbstainCount=0`，`NetGainAfterAbstain=3`，`Recommendation=NeedsFeatureTuning`
- Extended formal priority alignment：`RegressionCount=30`，`RecoveredCount=0`，`UnexplainedMismatchCount=30`，`AbstainCount=2`，`NetGainAfterAbstain=23`，`Recommendation=NeedsFeatureTuning`
- 默认 baseline shadow eval：A3 `NetGain=-17`，`NetGainAfterAbstain=3`，`Recommendation=KeepFormalRanking`
- 默认 baseline shadow eval：Extended `NetGain=-3`，`NetGainAfterAbstain=27`，`Recommendation=KeepFormalRanking`

结论：

- formal-priority-aware profile 目前没有真正追回 baseline regression。
- margin-aware abstain 只用于 shadow recommendation，不改变 formal output。
- 即使 `NetGainAfterAbstain` 为正，raw regression 仍存在，因此当前继续 `KeepFormalRanking`。
- 不进入 runtime trace 或 guarded opt-in。
- 不使用 sampleId / itemId / fixture / 领域词表特判。

## Router Shadow Freeze / Opt-in Readiness Gate R2.F

R2.F 冻结当前 router shadow 结论，并新增 guarded opt-in readiness gate。

新增文档：

- `docs/router-intent-shadow-freeze.md`

新增 CLI：

- `eval router-guarded-optin-readiness-gate`

输出：

- `learning/router/router-guarded-optin-readiness-gate.json`
- `learning/router/router-guarded-optin-readiness-gate.md`

Gate 条件：

- `ShadowBreaksRuntime = 0`
- `ShadowFixesRuntime > 0`
- `NetGain > 0`
- `PerIntentRegression = 0`
- `AgreementRate >= configured threshold`
- `LowConfidenceCount <= configured threshold`
- P15 gate remains passing

当前冻结结论：

- R1 best classifier：`TokenCentroidRouterBaseline`
- R2 shadow eval agreement：`88.57%`
- R2 runtime trace：`TraceCount=0`，`NeedsMoreRealTraces`
- R2.1：`fixes=1`，`breaks=3`，`hardNegatives=3`
- 当前结论：`KeepRuleBased`
- R3 guarded opt-in：blocked
- gate 当前失败原因包含 `ShadowBreaksRuntimeGreaterThanFixes`

边界：

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy` / package output。

## Shadow Baseline Freeze Registry / Readiness Dashboard S0

S0 新增统一 readiness registry、freeze report 与 runtime-change gate，用于把 Graph、Vector、Router、CandidateReranker、AttentionRerank、PlanningProposal 的 shadow / opt-in 结论集中冻结。该阶段只读取已有 eval / learning 报告，不改变任何正式 runtime 行为，不启用新的 scorer、router、retrieval 或 package 输出路径。

新增 DTO / runner：

- `LearningReadinessRegistry`
- `ShadowCapabilityReadiness`
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

当前冻结结论：

- `GraphExpansion`：`ReadyForGuardedOptIn`，仅限 `audit-v1` / `conflict-v1`；`normal-v1` / `current-task-v1` 禁止默认启用。
- `VectorRetrieval`：`PreviewOnly` / `BlockedByRecall`；V4 gate 失败原因包含 `A3RecallAtLeast80Percent`、`A3FusionRecallAtLeast80Percent`、`A3ExpandedRecallAtLeast80Percent`。
- `RouterIntentClassifier`：`KeepRuleBased`；R2.F gate 失败原因包含 `ShadowBreaksRuntimeGreaterThanFixes`。
- `CandidateReranker`：`KeepFormalRanking`；CR1.4 后 risk 为 0，但 raw `NetGain` 仍为负。
- `AttentionRerank`：仅允许 explicit `ApplyGuarded` opt-in，默认 off。
- `PlanningProposal`：仅允许 intent-scoped opt-in，默认 off。

runtime-change gate 检查：

- 未通过 readiness 的 capability 不允许 `ApplyGuarded` / `RuntimeShadow` / `DefaultOn`。
- Vector V4 gate 未通过时禁止 VectorRetrieval runtime shadow。
- Router breaks > fixes 时禁止 Router guarded opt-in。
- CandidateReranker `netGain <= 0` 时禁止 runtime shadow / opt-in。
- Graph `normal-v1` / `current-task-v1` 禁止 default on。

当前 `eval learning-runtime-change-readiness-gate` 通过，含义是：冻结 registry 正确阻断了不允许的 runtime 模式；不代表新增能力已被启用。

## Runtime Feedback Collection Foundation F1

F1 新增运行时反馈事件采集基础设施，用于把用户或操作员对 shadow/runtime 输出的反馈记录成可审计事件。该阶段只收集、查询、汇总和导出反馈，不自动训练模型，不自动调权，不自动启用 shadow opt-in，也不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增 DTO / store / service：

- `LearningFeedbackEvent`
- `LearningFeedbackEventQuery`
- `LearningFeedbackSubmitResult`
- `LearningFeedbackSummaryReport`
- `ILearningFeedbackStore`
- `InMemoryLearningFeedbackStore`
- `FileLearningFeedbackStore`
- `LearningFeedbackService`

支持的 `FeedbackKind`：

- `Useful`
- `NotUseful`
- `WrongIntent`
- `WrongCandidate`
- `MissingContext`
- `DeprecatedContext`
- `ConstraintMissing`
- `ConstraintIncorrect`
- `RankingWrong`
- `PromotionWrong`
- `ShouldPromote`
- `ShouldReject`
- `NeedsMoreEvidence`

API：

- `POST /api/learning/feedback`
- `GET /api/learning/feedback?runtimeFeedback=true`
- `GET /api/learning/feedback/summary`
- `GET /api/learning/feedback/export`

兼容性：

- 原 `GET /api/learning/feedback` 默认仍查询 promotion feedback。
- 只有显式 `runtimeFeedback=true` 或 runtime feedback 专用过滤参数时才查询 `LearningFeedbackEvent`。

安全边界：

- 运行时反馈默认写入 repo-local / storage-local 的 `runtime-feedback-events.jsonl`。
- 提交时会按稳定 `FeedbackId` upsert，避免重复反馈浪费存储和制造噪声。
- 支持 `metadataOnly=true` / `redactionMode=metadata-only`，不会把用户原始敏感内容直接变成训练样本。
- metadata 中记录 `trainingUse=disabled_until_review`，后续数据集生成必须显式评审后再使用。

CLI：

- `eval learning-feedback-summary`
- `eval export-learning-feedback`

输出：

- `learning/feedback/learning-feedback-summary.json`
- `learning/feedback/learning-feedback-summary.md`
- `learning/feedback/learning-feedback-events.jsonl`

## Feedback Capture Surfaces & Submission Smoke Flow F1.1

F1.1 在 F1 的反馈存储基础上补齐显式提交入口、目标绑定、ControlRoom 提交动作和 smoke flow。该阶段仍只做反馈采集与验证，不训练模型、不自动调权、不自动启用任何 shadow / opt-in，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增目标类型枚举：

- `PackageItem`
- `RetrievalCandidate`
- `RouterPrediction`
- `VectorCandidate`
- `GraphExpansionCandidate`
- `RankerCandidate`
- `PromotionCandidate`
- `ConstraintGapCandidate`
- `StableReviewCandidate`

新增提交请求：

- `LearningFeedbackSubmitRequest`

提交请求和事件均保留：

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

Smoke 检查：

- submit works
- duplicate `FeedbackId` upsert works
- `metadataOnly=true` works
- `redactionMode=metadata-only` preserved
- `trainingUse=disabled_until_review`
- summary count updated
- export JSONL contains feedback

ControlRoom：

- Learning Features 页面新增 `F feedback` 提交动作。
- 提交时要求显式输入 `CapabilityId`、`TargetType`、`TargetId`、`FeedbackKind`、`SourceOperationId` 和 reason。
- 默认使用 metadata-only / `disabled_until_review`，避免把原始敏感内容直接作为训练样本。

Runtime Feedback Summary 增强：

- recent feedback
- feedback by target type
- feedback by capability
- metadataOnly count
- trainingUse disabled count

## Feedback Review & Feature Candidate Builder F2

F2 在 F1/F1.1 的 runtime feedback 之上增加人工审核和离线 feature candidate 生成。该阶段只做审核、脱敏校验、候选导出和报告，不训练模型、不调权、不自动启用 shadow / opt-in，不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增审核状态：

- `PendingReview`
- `ApprovedForDataset`
- `Rejected`
- `NeedsRedaction`
- `NeedsMoreEvidence`

新增模型与存储：

- `LearningFeedbackReviewRecord`
- `LearningFeedbackReviewRequest`
- `LearningFeedbackReviewResult`
- `LearningFeedbackReviewSummaryReport`
- `ILearningFeedbackReviewStore`
- `InMemoryLearningFeedbackReviewStore`
- `FileLearningFeedbackReviewStore`
- `LearningFeedbackReviewService`

新增审核 API：

- `POST /api/learning/feedback/{feedbackId}/review/approve`
- `POST /api/learning/feedback/{feedbackId}/review/reject`
- `POST /api/learning/feedback/{feedbackId}/review/needs-redaction`
- `POST /api/learning/feedback/{feedbackId}/review/needs-evidence`
- `GET /api/learning/feedback/reviews`
- `GET /api/learning/feedback/reviews/summary`

新增离线候选：

- `FeedbackFeatureCandidate`
- `LearningFeedbackFeatureCandidateReport`
- `LearningFeedbackFeatureCandidateBuilder`

Builder 只处理 `ApprovedForDataset` 且 `TrainingUse != disabled_until_review`、`RedactionChecked=true` 的 feedback。`metadataOnly=true` 的反馈只生成 metadata-safe candidate，不导出原始 `Reason` / `UserCorrection`。缺少 `SourceOperationId`、`TargetId` 或 `CapabilityId` 时不会生成候选，可标记为 `NeedsMoreEvidence`。

支持的 capability 映射：

- `RouterIntentClassifier`: `WrongIntent` -> router intent correction candidate
- `CandidateReranker`: `RankingWrong` / `WrongCandidate` -> ranking pair candidate
- `VectorRetrieval`: `MissingContext` / `DeprecatedContext` -> vector recall/risk candidate
- `GraphExpansion`: `ConstraintMissing` / `DeprecatedContext` -> graph section/routing candidate
- `PromotionJudge`: `ShouldPromote` / `PromotionWrong` -> promotion label candidate
- `ConstraintGapJudge`: `ConstraintMissing` / `ConstraintIncorrect` -> constraint gap label candidate

新增 CLI：

- `eval learning-feedback-review-summary`
- `eval learning-feedback-feature-candidates`

输出：

- `learning/feedback/learning-feedback-review-summary.json`
- `learning/feedback/learning-feedback-review-summary.md`
- `learning/feedback/learning-feedback-feature-candidates.jsonl`
- `learning/feedback/learning-feedback-feature-candidates.md`
- `learning/feedback/learning-feedback-feature-candidates-report.json`

质量边界：

- 未审核 feedback 不生成 candidate。
- rejected / needs redaction / needs more evidence 不生成 candidate。
- candidate id 基于 feedback / capability / target / label 稳定生成，避免重复导出噪声。
- production path 不允许 sampleId / itemId / fixture / 领域词表特判。

## Feedback Quality & Dataset Readiness F3

F3 在 F2 的 review / feature candidate builder 之后新增 feedback quality 与 dataset readiness 报告。该阶段只聚合 runtime feedback、review records、feature candidates 与既有 learning feature dataset 摘要，不训练、不调权、不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增 DTO / builder：

- `LearningFeedbackQualityReport`
- `LearningFeedbackDatasetReadiness`
- `LearningFeedbackQualityReportBuilder`

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

Capability readiness 覆盖：

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

新增 CLI：

- `eval learning-feedback-quality`

输出：

- `learning/feedback/learning-feedback-quality-report.json`
- `learning/feedback/learning-feedback-quality-report.md`

推荐结论：

- `NeedsReviewedFeedback`
- `NeedsMoreFeedback`
- `NeedsRedactionReview`
- `NeedsLabelCoverage`
- `ReadyForDatasetExport`
- `ReadyForOfflineBaseline`
- `NotReady`

ControlRoom Learning Features 页面新增 Feedback Quality / Dataset Readiness 摘要，只读展示 review coverage、redaction coverage、feature candidate count、capability readiness 与 blocked reasons。

## Feedback Review Operations & Approved Dataset Gate F3.1

F3.1 在 F3 quality report 之后补齐人工 review 操作入口、review smoke flow 和 approved dataset gate。该阶段仍只处理 runtime feedback 的离线准入，不训练、不调权、不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

ControlRoom Learning Features 页面新增 Runtime Feedback review 操作：

- `A Approve`
- `R Reject`
- `N NeedsRedaction`
- `E NeedsEvidence`
- `H ReviewHistory`

每次操作前必须展示 `FeedbackId`、`CapabilityId`、`TargetType`、`TargetId`、`SourceOperationId`、`FeedbackKind`、`metadataOnly`、`redactionMode`、`trainingUse` 和 `reason`，并要求输入 `YES`。Approve 默认写入 `approved_for_dataset`，其余状态保持 `disabled_until_review`。

新增 CLI：

- `eval learning-feedback-review-smoke`
- `eval learning-approved-feedback-dataset-gate`

Smoke flow 会覆盖 submit、needs-redaction、reject、approve metadata-safe feedback、feature candidate export 和 quality report refresh。Smoke feedback 固定写入 `trainingUse=smoke_test_only` 与 `excludedFromTraining=true`，可验证链路但不会进入真实 trainable dataset。

Approved dataset gate 输出：

- `learning/feedback/learning-approved-feedback-dataset-gate.json`
- `learning/feedback/learning-approved-feedback-dataset-gate.md`

Gate 条件：

- `ApprovedCount > 0`
- `RedactionCoverageRate = 100%`
- `FeatureCandidateCount > 0`
- `TrainingUse != disabled_until_review`
- trainable dataset 中不能包含 `smoke_test_only` 记录
- capability label coverage 必须同时具备正负样本

当前本地真实数据仍应因为 `NoApprovedFeedback` / `NeedsReviewedFeedback` 失败；这是预期状态，不应通过 smoke 数据绕过。

## Feedback Postgres Provider Smoke DB3.1

DB3.1 将 F1-F3.1 的 feedback / review / feature candidate 数据结构接入 Postgres provider 的 readiness、dual-write smoke 和 shadow-read smoke。该阶段只验证 provider 可用性与 parity：

- 不训练。
- 不调权。
- 不自动改变 learning readiness。
- 不切换 runtime feedback store。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 smoke 覆盖：

- feedback submit / duplicate stable upsert
- `metadataOnly=true`
- `redactionMode`
- `trainingUse=disabled_until_review`
- approve / reject / needs-redaction / needs-evidence
- feature candidate build / upsert
- summary / export projection parity
- cleanup

当前质量门槛：

- readiness gate passed
- dual-write mismatch = 0
- shadow-read mismatch = 0
- Postgres failure = 0
- provider quality `Recommendation=ReadyForScopedServiceMode`

`ReadyForScopedServiceMode` 仅表示可以设计后续受控 scope provider 模式，不允许直接 runtime default on。

## Feedback Postgres Scoped Service Mode DB3.2

DB3.2 为 feedback / review / feature candidate provider 增加 scoped service mode smoke 和 gate。该阶段仍只验证 provider 路由能力：

- 不训练。
- 不调权。
- 不改变 learning readiness。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 非 allowlisted scope 保持 FileSystem。
- scoped `GuardedPostgresPrimary` 必须保留 fallback 和 comparison trace。

新增命令：

- `eval postgres-learning-feedback-scoped-service-mode-smoke --cleanup-confirm`
- `eval postgres-learning-feedback-scoped-service-mode-gate`

当前 gate 条件：

- DB3.1 readiness gate passed。
- dual-write / shadow-read smoke passed。
- provider quality `ReadyForScopedServiceMode`。
- scoped allowlist configured。
- non-allowlisted scope remains FileSystem。
- mismatch count = 0。
- Postgres failure count = 0。
- export / summary parity passed。
- fallback tested。

当前本地结果：

- scoped smoke `ReadyForSelectedFeedbackScope`
- scoped gate `Passed=true`
- mismatch / Postgres failure 均为 `0`

## Feedback Postgres Selected Normal Scope Canary DB3.3

DB3.3 在一个显式 selected normal workspace / collection 内运行 learning feedback `GuardedPostgresPrimary` canary。该阶段仍不训练、不调权、不改变 learning readiness，不影响 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增命令：

- `eval postgres-learning-feedback-selected-normal-scope-canary`

前置 gate：

- Postgres storage diagnostics `Ready`。
- learning feedback readiness gate passed。
- dual-write smoke passed。
- shadow-read smoke passed。
- provider quality `ReadyForScopedServiceMode`。
- scoped service mode gate passed。
- selected workspace / collection 显式配置。
- P15 gate passed。

Canary 数据规则：

- feedback / review / feature candidate 只用于 provider canary。
- approved canary review 使用 `trainingUse=smoke_test_only`。
- feature candidate metadata 必须包含 `excludedFromTraining=true`。
- trainable dataset gate 必须继续排除 smoke / excluded candidate。

当前本地结果：

- selected normal canary `Recommendation=ReadyForLimitedFeedbackScope`
- `OperationCount=18`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- export / summary / review summary / feature candidate parity 均通过

## DB3.4 Learning Feedback Limited Scope Observation + Freeze Gate

- 新增 limited scope observation、quality report 和 freeze gate。
- observation 只覆盖 selected feedback scope 的 feedback submit/upsert、review、feature candidate build/list/export、summary parity 和 non-selected scope FileSystem check。
- `smoke_test_only` 与 `excludedFromTraining=true` 必须保留，`TrainableCandidateLeakCount` 必须为 0。
- freeze gate 通过后只允许 allowlisted scope 的 `GuardedPostgresPrimary`，不允许 global default-on、auto-training 或自动 readiness 改写。
- 本阶段不训练、不调权，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
