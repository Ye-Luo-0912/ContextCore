# Learning Feature Dataset

更新时间：2026-06-06

## 目标

Learning Loop Phase 3 新增只读 feature extraction dataset，用于把人工 review feedback、P15 eval report 与 planning shadow report 投影成离线分析样本。

本阶段不训练模型，不应用策略，不改变 retrieval / planning / `PackingPolicy` / attention / memory / constraints 业务逻辑。

## DTO

`ContextPolicyFeatureExample` 表示单个策略特征样本：

- `ExampleId`
- `WorkspaceId`
- `CollectionId`
- `SourceType`
- `SourceId`
- `TaskKind`
- `Mode`
- `Intent`
- `Label`
- `InputSummary`
- `CandidateId`
- `CandidateKind`
- `CandidateLayer`
- `CandidateStatus`
- `CandidateImportance`
- `CandidateRecency`
- `ChannelSources`
- `RelationPathCount`
- `KeywordMatchScore`
- `SemanticAnchorMatchScore`
- `ShortTermMatchScore`
- `StableMatchScore`
- `ConstraintMatchScore`
- `LifecycleRisk`
- `Selected`
- `Accepted`
- `Rejected`
- `EvidenceRefs`
- `PolicyVersion`
- `CreatedAt`
- `Metadata`

`RankingPairExample` 表示 eval 中正负候选的 pair：

- `Query`
- `Mode`
- `Intent`
- `PositiveCandidateId`
- `NegativeCandidateId`
- `Reason`
- `EvalSampleId`
- `FeatureSnapshot`

## 数据来源

`LearningFeatureDatasetService` 当前只读聚合三类输入：

- `PolicyFeedbackDataset`
  - promotion accept/reject
  - stable review accept/reject
  - constraint gap accept/reject
  - candidate constraint activate/reject
- P15 eval reports
  - `eval/eval-report-p15-a3.json`
  - `eval/eval-report-p15-extended.json`
- planning proposal / shadow reports
  - `eval/planning-shadow-comparison-a3.json`
  - `eval/planning-shadow-comparison-extended.json`

## 映射规则

Policy feedback records 转为 `TaskKind=PolicyFeedback` 的 `ContextPolicyFeatureExample`：

- accept / activate / positive label -> `Accepted=true`
- reject / negative label -> `Rejected=true`
- `TargetLayer` 映射到 `CandidateLayer`
- review metadata 中的 candidate id / kind / status / score 字段进入 feature snapshot

Eval reports 转为 `RankingPairExample`：

- `mustHit` 作为 positive candidate
- `mustNotHit` 作为 negative candidate
- `FeatureSnapshot` 记录 selected 状态、rank、score、section、constraint/entity/uncertainty 覆盖状态

Planning shadow reports 转为 `TaskKind=RouterIntent` 的 `ContextPolicyFeatureExample`：

- proposal summary 中的 intent 作为 `Intent` / `Label`
- channel source counts 映射到 keyword / relation / short-term / stable 特征
- validator/fallback/repair 信息进入 metadata

## 导出

优先使用 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval export-learning-features
```

默认输出：

- `learning/features/policy-feedback-features.jsonl`
- `learning/features/ranking-pairs.jsonl`
- `learning/features/router-intent-examples.jsonl`

可选参数：

- `--out-dir <dir>`
- `--workspace <id>`
- `--collection <id>`
- `--session <id>`
- `--eval-reports <csv>`
- `--planning-shadow-reports <csv>`

## Dataset Quality Report

Learning Loop Phase 4 新增只读 dataset quality report：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-dataset-quality
```

默认读取：

- `learning/features/policy-feedback-features.jsonl`
- `learning/features/ranking-pairs.jsonl`
- `learning/features/router-intent-examples.jsonl`

默认输出：

- `learning/features/dataset-quality-report.json`
- `learning/features/dataset-quality-report.md`

报告字段：

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

当前风险枚举：

- `NoPolicyFeedback`
- `EvalOnlyDataset`
- `ClassImbalance`
- `MissingNegativeSamples`
- `LowIntentCoverage`
- `LowModeCoverage`

当前 readiness gates：

- `RouterIntentClassifier`
- `CandidateReranker`
- `PromotionJudge`
- `ConstraintGapJudge`
- `AttentionScorer`

这些 gate 只用于标记离线数据是否足够进入后续分析，不训练模型、不自动应用策略、不改变在线 retrieval / planning / package。

## Offline Baseline

Learning Loop Phase 5 新增离线 baseline CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-baseline --task router
dotnet run --project src\ContextCore.ControlRoom -- eval learning-baseline --task ranker
dotnet run --project src\ContextCore.ControlRoom -- eval learning-baseline
```

别名：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-baseline-router
dotnet run --project src\ContextCore.ControlRoom -- eval learning-baseline-ranker
```

Router baseline 默认读取：

- `learning/features/router-intent-examples.jsonl`

Ranker baseline 默认读取：

- `learning/features/ranking-pairs.jsonl`

默认输出：

- `learning/baselines/router-intent-baseline-report.json`
- `learning/baselines/router-intent-baseline-report.md`
- `learning/baselines/ranker-baseline-report.json`
- `learning/baselines/ranker-baseline-report.md`

当前 baseline：

- Router `MajorityClassBaseline`：accuracy `34.78 %`，macroF1 `0.086`
- Router `RuleBasedBaseline`：accuracy `56.52 %`，macroF1 `0.5088`
- Ranker `RuleScoreBaseline`：pairwise accuracy `84.13 %`
- Ranker `SimpleFeatureWeightedBaseline`：pairwise accuracy `90.48 %`

Baseline split 使用 `DeterministicGroupHash80_20`：

- router 按 `SourceId` 或 metadata `sampleId` 分组
- ranker 按 `EvalSampleId` 分组

同一 sample/batch group 不会同时进入 train 和 test，避免样本泄漏。

Phase 5 只做离线基线报告，不训练生产模型、不接入 runtime、不改变 retrieval / planning / `PackingPolicy` / attention / constraints 业务逻辑。

## Ranker Ablation / Sweep

Learning Loop Phase 6A 在 ranker baseline 之后新增两个离线分析命令：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-ablation
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-weight-sweep
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-residual-audit
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-analysis
```

默认读取：

- `learning/features/ranking-pairs.jsonl`

默认输出：

- `learning/baselines/ranker-ablation-report.json`
- `learning/baselines/ranker-ablation-report.md`
- `learning/baselines/ranker-weight-sweep-report.json`
- `learning/baselines/ranker-weight-sweep-report.md`
- `learning/baselines/ranker-residual-audit-report.json`
- `learning/baselines/ranker-residual-audit-report.md`

Feature ablation 关闭 `SimpleFeatureWeightedBaseline` 的离线特征组，报告 pairwise accuracy delta、failure clusters、top fixed / newly failed examples。

Weight sweep 只做单参数扫描，覆盖 lifecycle penalty、recency、current version、active status、noise keyword、relation evidence、stable preference 等权重。当前 sweep 没有找到优于 `90.48 %` baseline 的配置。

Failure cluster 分类：

- `VersionConflict`
- `DeprecatedNoise`
- `KeywordNoise`
- `LowRecency`
- `WrongLifecycle`
- `RelationEvidenceMissing`
- `Other`

Phase 6A 只输出离线建议，不改正式 scorer，不接 runtime，不改变 retrieval / planning / `PackingPolicy` / attention / constraints。

Phase 6B 新增 residual error audit，对 `SimpleFeatureWeightedBaseline` 剩余失败输出 feature delta、cluster、probable cause 和 hard negative recommendations。当前 residual failures 为 `DeprecatedNoise: 3`，建议补充 `DeprecatedSameKeyword`、`VersionConflict`、`HistoricalSelectedNoise`、`WeakLifecycleMarker`、`SemanticAnchorOvermatch` hard negatives。Phase 6B 不删除 keyword / semantic anchor feature，不修改正式 scorer。

## Hard Negative Features

Learning Loop Phase 6C 新增离线 hard negative 导出：

- `learning/features/hard-negatives.jsonl`
- `learning/baselines/hard-negative-report.json`
- `learning/baselines/hard-negative-report.md`

`HardNegativeExample` 从 `ranker-residual-audit-report.json` 自动生成，保留 source sample、positive / negative candidate、hard negative type、positive / negative feature snapshot、expected preference 与 metadata。当前从 3 个 `DeprecatedNoise` residual failures 生成 `18` 条 hard negative：

- `DeprecatedSameKeyword: 3`
- `VersionConflict: 3`
- `HistoricalSelectedNoise: 3`
- `WeakLifecycleMarker: 3`
- `SemanticAnchorOvermatch: 3`
- `KeywordNoise: 3`

Phase 6C 同时扩展离线 ranking feature 解释，新增 lifecycle-aware fields：`IsDeprecated`、`IsSuperseded`、`IsHistorical`、`IsRejected`、`HasReplacement`、`HasSupersedesRelation`、`VersionDistance`、`IsCurrentVersion`、`LifecycleConfidence`、`HistoricalSectionOnly`。

这些 feature 只存在于 offline dataset / baseline report，不写入 runtime scorer，不改变 retrieval、planning、`PackingPolicy`、attention 或 constraints 业务逻辑。

## Lifecycle-aware Ranker Shadow Features

Phase 6D 新增 `LifecycleAwareRankerShadowScorer`，只读取 retrieval/package eval diagnostics 中的 candidate snapshot，输出 shadow score trace：

- `legacyScore`
- `lifecycleAwareScore`
- `scoreDelta`
- `reason`
- `LifecycleAwareFeatureSet`

默认 shadow 配置仍关闭：

```json
{
  "Learning": {
    "RankerShadow": {
      "Enabled": false,
      "TraceCollectionEnabled": false,
      "DebugEndpointEnabled": true,
      "Profile": "lifecycle-aware-v1",
      "MaxCandidatesPerTrace": 50
    }
  }
}
```

Eval CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval lifecycle-ranker-shadow
dotnet run --project src\ContextCore.ControlRoom -- eval lifecycle-ranker-shadow --include-batches
```

输出：

- `learning/baselines/lifecycle-aware-ranker-shadow-report-a3.json`
- `learning/baselines/lifecycle-aware-ranker-shadow-report-extended.json`

Phase 6D 只做 shadow evaluation。报告中的 `ShadowSelectedIds` 必须与 `LegacySelectedIds` 完全一致，`FormalOutputChanged` 与 `SelectedSetChanged` 必须为 `0`。这些 shadow features 不写入正式 retrieval scoring，不改变 `PackingPolicy`，不改变 selected set，也不参与 runtime ranker。

Phase 6E 在 Service API 中增加只读 debug endpoint：

- `POST /api/retrieval/ranker-shadow/debug`

该 endpoint 复用 retrieval debug / candidate collection 结果，返回 candidate-level `legacyScore`、`lifecycleAwareScore`、`scoreDelta` 和 lifecycle-aware feature breakdown。它只用于人工诊断，不改变正式 retrieval output，不改变 selected set，不修改 `PackingPolicy`，也不把 lifecycle-aware baseline 接入 runtime scorer。

Phase 6F 增加 runtime shadow trace collection。该能力默认关闭，只在 `Learning:RankerShadow:TraceCollectionEnabled=true` 时把 lifecycle-aware shadow score 附加到 retrieval/package trace：

- `candidateId`
- `legacyScore`
- `lifecycleAwareScore`
- `scoreDelta`
- `lifecycleFeatures`
- `demotionReasons`
- `promotionReasons`

trace collection 在正式结果确定后执行，只做观测，不改变 selected set、排序、package sections 或 `PackingPolicy`。导出入口：

- `GET /api/learning/ranker-shadow/traces`
- `GET /api/learning/ranker-shadow/traces?format=jsonl`

Phase 6G 在此基础上新增 trace quality report。报告读取 runtime `RankerShadowTrace` records 并统计 demotion / promotion / risk 信号：

- `TraceCount`
- `CandidateScoreCount`
- `DeprecatedDemotionCount`
- `HistoricalDemotionCount`
- `VersionConflictFixCount`
- `CurrentVersionPromotionCount`
- `MustHitDemotedCount`
- `MustNotHitPromotedCount`
- `LifecycleViolationCount`
- `ModeBreakdown`
- `IntentBreakdown`
- `RiskSamples`
- `RecommendedNextStep`

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval ranker-shadow-trace-quality
```

输出：

- `learning/baselines/ranker-shadow-trace-quality-report.json`
- `learning/baselines/ranker-shadow-trace-quality-report.md`

当前默认 trace collection 关闭，因此本地报告通常会显示 `NeedsMoreRealTraces`。该结果不是失败，而是表示缺少真实 runtime traces。

进入下一阶段门槛：

- `TraceCount >= 30`
- `CandidateScoreCount > 0`
- `MustHitDemotedCount = 0`
- `MustNotHitPromotedCount = 0`
- `LifecycleViolationCount = 0`
- `DeprecatedDemotionCount > 0` 或 `VersionConflictFixCount > 0`

Phase 6H 新增采集 runbook 与脚本：

- `docs/ranker-shadow-trace-collection-runbook.md`
- `scripts/collect-ranker-shadow-traces.ps1`

## Service API

可选只读 endpoint：

- `GET /api/learning/features`
- `GET /api/learning/features/export`
- `GET /api/learning/features/quality`
- `GET /api/learning/ranker-shadow/traces`

`GET /api/learning/features` 支持：

- `workspaceId`
- `collectionId`
- `sessionId`
- `limit`
- `offset`

`GET /api/learning/features/export` 支持：

- `workspaceId`
- `collectionId`
- `sessionId`
- `outputDirectory`

`GET /api/learning/features/quality` 支持：

- `featureDirectory`

`GET /api/learning/ranker-shadow/traces` 支持：

- `workspaceId`
- `collectionId`
- `take`
- `format=jsonl`

## Client

`ContextCoreClient` 新增：

- `GetLearningFeaturesAsync(...)`
- `ExportLearningFeaturesAsync(...)`
- `GetLearningDatasetQualityAsync(...)`
- `GetRankerShadowTracesAsync(...)`
- `ExportRankerShadowTracesAsync(...)`

## ControlRoom

Service Mode 新增 Learning Features 只读页面：

- 展示 feature count
- 展示 ranking pair count
- 展示 router intent example count
- 展示 label distribution
- 展示 source type distribution
- 展示 latest export path
- 展示 recent feature examples
- 展示 dataset quality counts / risks / task readiness / recommended next action

该页面只读展示，不触发训练、不导入策略、不改变在线检索或打包。

Ranker Shadow Debug 页面新增 Recent Shadow Traces 只读区块，展示 recent retrieval id、profile、candidate score count、deprecated demotion count 与 query。该区块只读取 trace store，不触发新的 retrieval，不改变正式输出。

Phase 6G 进一步在 Ranker Shadow Debug 页面增加 Trace Quality Summary，只读展示 trace count、candidate score count、demotion / promotion 统计、risk count 和 recommended next step。该 summary 仍只读取 recent traces，不训练模型、不应用权重、不改变 runtime retrieval 或 package。

## Runtime Feedback Source

F1 新增 `LearningFeedbackEvent` 作为运行时反馈采集源。它与 `PolicyFeedbackDataset`、feature export 保持解耦：反馈事件不会自动生成训练样本，不会自动进入 reranker/router/attention 数据集，也不会自动改变任何 policy。

采集接口：

- `POST /api/learning/feedback`
- `GET /api/learning/feedback?runtimeFeedback=true`
- `GET /api/learning/feedback/summary`
- `GET /api/learning/feedback/export`

导出文件：

- `learning/feedback/learning-feedback-summary.json`
- `learning/feedback/learning-feedback-summary.md`
- `learning/feedback/learning-feedback-events.jsonl`

数据治理要求：

- 反馈事件按稳定 `FeedbackId` upsert，避免重复数据污染。
- 默认 metadata 写入 `trainingUse=disabled_until_review`。
- 支持 metadata-only / redaction 模式，避免用户原始敏感内容直接成为训练样本。
- 后续如果要转为 feature dataset，必须新增显式评审或脱敏步骤。

F1.1 补充反馈提交入口与 smoke flow：

- `LearningFeedbackTargetType` 限定目标绑定类型。
- `LearningFeedbackSubmitRequest` 作为显式提交请求。
- `eval submit-learning-feedback` 可提交单条 smoke / manual feedback，必须显式传入 capability、target type、target id 和 feedback kind。
- `eval learning-feedback-smoke` 验证 submit、duplicate upsert、metadata-only redaction、summary 和 export。
- smoke 仍只写 `learning/feedback` 与 runtime feedback store，不进入 feature export，不改变任何 runtime policy。

## Feedback Review Feature Candidates

F2 新增 runtime feedback 到离线 dataset candidate 的审核闸门：

- `LearningFeedbackReviewRecord` 记录 reviewer、review status、approved capability、approved label kind、redaction checked 与 training use。
- `LearningFeedbackReviewService` 负责 approve / reject / needs-redaction / needs-evidence，不修改任何 runtime policy。
- `LearningFeedbackFeatureCandidateBuilder` 只读取已审核 feedback，生成 `FeedbackFeatureCandidate` JSONL。

候选生成规则：

- 只处理 `ApprovedForDataset`。
- `TrainingUse` 必须不是 `disabled_until_review`。
- `RedactionChecked` 必须为 true。
- `metadataOnly=true` 时不导出原始 query / reason / correction，只保留 metadata-safe refs。
- 缺少 `SourceOperationId`、`TargetId` 或 `CapabilityId` 时标记为 evidence 不足，不生成候选。
- candidate id 稳定生成，重复运行不会制造重复样本噪声。

CLI：

- `eval learning-feedback-review-summary`
- `eval learning-feedback-feature-candidates`

输出：

- `learning/feedback/learning-feedback-review-summary.json`
- `learning/feedback/learning-feedback-review-summary.md`
- `learning/feedback/learning-feedback-feature-candidates.jsonl`
- `learning/feedback/learning-feedback-feature-candidates.md`
- `learning/feedback/learning-feedback-feature-candidates-report.json`

该阶段仍不训练模型、不调权、不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

## Feedback Quality Readiness

F3 新增 `eval learning-feedback-quality`，用于评估 runtime feedback 是否已经足够进入后续离线 dataset export / offline baseline。

输入：

- runtime feedback events
- feedback review records
- feature candidates
- existing learning feature dataset summary

输出：

- `learning/feedback/learning-feedback-quality-report.json`
- `learning/feedback/learning-feedback-quality-report.md`

报告会按 capability 输出 readiness：

- approved candidate count
- positive / negative label count
- metadata-only count
- needs more evidence count
- ready / not ready
- blocked reasons

`metadataOnly=true` 的候选可以保留为 metadata-safe candidate，但如果某个 capability 只有 metadata-only 候选，会被标记为 `MetadataOnlyInsufficient`。缺正样本或负样本分别标记 `MissingPositiveSamples` / `MissingNegativeSamples`。未 review 的 feedback 只计入 `PendingReviewCount`，不会生成候选。

## Feedback Review Operations & Approved Dataset Gate

F3.1 增加 review 操作 smoke 和 approved dataset gate，用于把“操作链路可用”和“真实训练候选可用”分开评估。该阶段仍不训练、不调权、不改变 retrieval / planning / scoring / `PackingPolicy` / package output。

新增 CLI：

- `eval learning-feedback-review-smoke`
- `eval learning-approved-feedback-dataset-gate`

`learning-feedback-review-smoke` 验证 submit、duplicate upsert、needs-redaction、reject、approve metadata-safe feedback、feature candidate export 和 quality report refresh。Smoke feedback 必须写入 `TrainingUse=smoke_test_only` 与 `excludedFromTraining=true`，因此可用于验证链路，但不会进入真实 trainable dataset。

`learning-approved-feedback-dataset-gate` 输出：

- `learning/feedback/learning-approved-feedback-dataset-gate.json`
- `learning/feedback/learning-approved-feedback-dataset-gate.md`

Gate 条件：

- approved feedback 数量大于 0
- redaction coverage 为 100%
- feature candidate 数量大于 0
- trainable candidate 不得使用 `disabled_until_review`
- trainable dataset 中不得包含 `smoke_test_only`
- capability label coverage 必须具备正负样本

当前本地真实数据没有 approved feedback，gate 应保持失败，原因是 `NoApprovedFeedback` 或 `NeedsReviewedFeedback`。
