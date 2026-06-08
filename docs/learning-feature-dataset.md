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
