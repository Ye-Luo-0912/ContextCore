# Learning Offline Baseline

更新时间：2026-06-06

## 目标

Learning Loop Phase 5 新增离线 baseline experiment，用于在不训练生产模型、不接入 runtime 的前提下评估两个已 Ready 的任务：

- `RouterIntentClassifier`
- `CandidateReranker`

本阶段只读取 Phase 3/4 生成的离线 feature JSONL，不修改 retrieval、planning、`PackingPolicy`、attention、memory、constraints 或任何在线业务逻辑。

## CLI

统一命令：

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

可选参数：

- `--task router|ranker|all`
- `--features-dir <dir>`，默认 `learning/features`
- `--out-dir <dir>`，默认 `learning/baselines`

## 输入

Router baseline 默认读取：

- `learning/features/router-intent-examples.jsonl`

Ranker baseline 默认读取：

- `learning/features/ranking-pairs.jsonl`

## 输出

Router report：

- `learning/baselines/router-intent-baseline-report.json`
- `learning/baselines/router-intent-baseline-report.md`

Ranker report：

- `learning/baselines/ranker-baseline-report.json`
- `learning/baselines/ranker-baseline-report.md`

## 防止数据泄漏

Baseline 使用 deterministic grouped holdout split：

- split strategy：`DeterministicGroupHash80_20`
- router group key：`SourceId`，缺失时使用 metadata 中的 `sampleId`
- ranker group key：`EvalSampleId`

同一个 sample/batch group 不会同时进入 train 和 test。

## Router Baselines

`MajorityClassBaseline`：

- 在 train split 中选择出现次数最高的 intent。
- 对 test split 统一预测该 intent。

`RuleBasedBaseline`：

- 使用 mode、channel、score 等离线 feature 进行规则预测。
- 不读取真实 label 作为预测输入。
- 不训练模型。

报告指标：

- `Accuracy`
- `MacroF1`
- `PerIntentPrecision`
- `PerIntentRecall`
- `ConfusionMatrix`
- train/test split summary

当前 Phase 5 run：

| Baseline | Accuracy | MacroF1 |
|---|---:|---:|
| MajorityClassBaseline | 34.78 % | 0.086 |
| RuleBasedBaseline | 56.52 % | 0.5088 |

## Ranker Baselines

`RuleScoreBaseline`：

- 使用 ranking pair feature snapshot 中的 candidate score / selected / rank 信息。
- 用 positive score 是否高于 negative score 计算 pairwise outcome。

`SimpleFeatureWeightedBaseline`：

- 在 rule score 基础上增加 selected、rank、candidate kind、section、constraint/entity/stable/working 等显式 feature 权重。
- 不训练模型。

报告指标：

- `PairwiseAccuracy`
- `AUC`
- `WinRateOverRule`
- `FalsePositiveRate`
- `FalseNegativeRate`
- `TopFailureExamples`
- train/test split summary

当前 Phase 5 run：

| Baseline | PairwiseAccuracy | AUC | WinRateOverRule | FPR | FNR |
|---|---:|---:|---:|---:|---:|
| RuleScoreBaseline | 84.13 % | 0.8413 | 0.00 % | 15.87 % | 15.87 % |
| SimpleFeatureWeightedBaseline | 90.48 % | 0.9048 | 6.35 % | 9.52 % | 9.52 % |

## Ranker Feature Ablation

Learning Loop Phase 6A 新增 ranker feature ablation，只对 `SimpleFeatureWeightedBaseline` 做离线分析：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-ablation
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-analysis
```

默认输入：

- `learning/features/ranking-pairs.jsonl`

默认输出：

- `learning/baselines/ranker-ablation-report.json`
- `learning/baselines/ranker-ablation-report.md`

Ablation 覆盖：

- `lifecycle`
- `recency`
- `channel source`
- `relation path`
- `short-term match`
- `stable match`
- `constraint match`
- `keyword match`
- `semantic anchor match`
- `importance`

报告输出每个 disabled feature 的 pairwise accuracy、delta、FPR/FNR、failure clusters、top fixed examples 和 top newly failed examples。

当前 Phase 6A run：

| Disabled Feature | PairwiseAccuracy | Delta | Top Cluster |
|---|---:|---:|---|
| lifecycle | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| recency | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| channel source | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| relation path | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| short-term match | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| stable match | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| constraint match | 90.48 % | 0.00 % | DeprecatedNoise (3) |
| keyword match | 100.00 % | +9.52 % | - |
| semantic anchor match | 100.00 % | +9.52 % | - |
| importance | 90.48 % | 0.00 % | DeprecatedNoise (3) |

离线解释：当前 ranking pairs 的 residual failures 主要聚类为 `DeprecatedNoise`。关闭 keyword/semantic 分量能修复这些 eval-only pair，但这只是离线诊断，不代表可以修改正式 scorer。

## Ranker Weight Sweep

Learning Loop Phase 6A 新增 ranker weight sweep：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-weight-sweep
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-analysis
```

默认输出：

- `learning/baselines/ranker-weight-sweep-report.json`
- `learning/baselines/ranker-weight-sweep-report.md`

Sweep 覆盖：

- `lifecyclePenaltyWeight`
- `recencyWeight`
- `currentVersionBoost`
- `activeStatusBoost`
- `noiseKeywordPenalty`
- `relationEvidenceBoost`
- `stablePreferenceBoost`

当前 Phase 6A run 中，所有单参数 sweep 均未超过 `SimpleFeatureWeightedBaseline` 的 `90.48 %` pairwise accuracy；best result 为 neutral，不作为上线候选。

Failure cluster report 当前内嵌于 ablation / weight sweep report：

- `VersionConflict`
- `DeprecatedNoise`
- `KeywordNoise`
- `LowRecency`
- `WrongLifecycle`
- `RelationEvidenceMissing`
- `Other`

这些报告只输出离线建议，不修改正式 scorer，不影响 retrieval output。

## Ranker Residual Error Audit

Learning Loop Phase 6B 新增 residual error audit，用于解释 `SimpleFeatureWeightedBaseline` 的剩余失败，并生成 hard negative 数据增强建议：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-residual-audit
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-analysis
```

默认输出：

- `learning/baselines/ranker-residual-audit-report.json`
- `learning/baselines/ranker-residual-audit-report.md`

Residual failure detail 包含：

- sample / mode / intent
- positive / negative candidate id
- positive / negative final offline score 与 margin
- keyword match score
- semantic anchor match score
- selected / rank
- kind / section
- failure cluster
- probable cause

当前 Phase 6B run：

- residual failures：`3`
- failure cluster：`DeprecatedNoise: 3`
- average margin：`-10.1`
- affected samples：`chat-sample-002`、`chat-sample-004`、`novel-sample-008`
- feature conflict：
  - `KeywordMatch` average delta：`-13.05`
  - `SemanticAnchorMatch` average delta：`-13.05`

DeprecatedNoise hard negative recommendations：

- `DeprecatedSameKeyword`
- `VersionConflict`
- `HistoricalSelectedNoise`
- `WeakLifecycleMarker`
- `SemanticAnchorOvermatch`

Phase 6B 不删除 keyword / semantic anchor feature，不修改正式 scorer，只将 residual failure 转成后续离线数据增强建议。

## Hard Negative Dataset / Lifecycle-aware Ranker

Learning Loop Phase 6C 将 Phase 6B residual audit 进一步转为离线 hard negative dataset，并新增 lifecycle-aware ranker baseline：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval learning-hard-negatives
dotnet run --project src\ContextCore.ControlRoom -- eval learning-lifecycle-aware-ranker
dotnet run --project src\ContextCore.ControlRoom -- eval learning-ranker-analysis
```

默认输入：

- `learning/baselines/ranker-residual-audit-report.json`
- `learning/features/ranking-pairs.jsonl`

默认输出：

- `learning/features/hard-negatives.jsonl`
- `learning/baselines/hard-negative-report.json`
- `learning/baselines/hard-negative-report.md`
- `learning/baselines/lifecycle-aware-ranker-report.json`
- `learning/baselines/lifecycle-aware-ranker-report.md`

Hard negative 类型：

- `DeprecatedSameKeyword`
- `VersionConflict`
- `HistoricalSelectedNoise`
- `WeakLifecycleMarker`
- `SemanticAnchorOvermatch`
- `KeywordNoise`

Lifecycle-aware features：

- `IsDeprecated`
- `IsSuperseded`
- `IsHistorical`
- `IsRejected`
- `HasReplacement`
- `HasSupersedesRelation`
- `VersionDistance`
- `IsCurrentVersion`
- `LifecycleConfidence`
- `HistoricalSectionOnly`

当前 Phase 6C run：

| Baseline | PairwiseAccuracy | ResidualFailures | DeprecatedNoise | FPR | FNR |
|---|---:|---:|---:|---:|---:|
| RuleScoreBaseline | 84.13 % | 5 | 5 | 15.87 % | 15.87 % |
| SimpleFeatureWeightedBaseline | 90.48 % | 3 | 3 | 9.52 % | 9.52 % |
| LifecycleAwareFeatureBaseline | 100.00 % | 0 | 0 | 0.00 % | 0.00 % |

Hard negative dataset 当前生成 `18` 条样本，六类各 `3` 条。Lifecycle-aware baseline 达成本轮离线目标：PairwiseAccuracy 高于 `90.48 %`，ResidualFailures `0`，DeprecatedNoise `0`，FPR/FNR 未上升。

Phase 6C 仍保持纯离线分析：不训练模型，不接 runtime，不修改 retrieval / planning / `PackingPolicy` / attention / constraints，也不把 lifecycle-aware 权重接入正式 scorer。

## Lifecycle-aware Ranker Shadow Evaluation

Learning Loop Phase 6D 将 Phase 6C 的 lifecycle-aware 离线信号接入 shadow evaluation，但不接 runtime scorer、不改变 retrieval scoring、不改变 `PackingPolicy`、不训练模型、不接 ONNX。

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval lifecycle-ranker-shadow
dotnet run --project src\ContextCore.ControlRoom -- eval lifecycle-ranker-shadow --include-batches
```

默认输出：

- `learning/baselines/lifecycle-aware-ranker-shadow-report-a3.json`
- `learning/baselines/lifecycle-aware-ranker-shadow-report-extended.json`

Shadow trace 包含：

- `rankerShadowEnabled`
- `rankerShadowProfile`
- candidate shadow scores：`legacyScore` / `lifecycleAwareScore` / `scoreDelta` / `reason`
- `deprecatedDemotions`
- `versionConflictFixes`
- `mustHitDemotions`
- `mustNotHitPromotions`

当前 Phase 6D run：

| Scope | Samples | FormalOutputChanged | SelectedSetChanged | LifecycleViolationCount | DeprecatedNoiseDemoted | VersionConflictFixed | MustHitDemoted | MustNotHitPromoted | PotentialMRRDelta | PotentialPairwiseWinRate |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| A3 | 50 | 0 | 0 | 0 | 178 | 164 | 23 | 30 | +0.0290 | 99.00 % |
| Extended | 113 | 0 | 0 | 0 | 406 | 402 | 52 | 30 | +0.0516 | 99.56 % |

结果评估：

- 安全性达标：formal output、selected set、lifecycle violation 均保持 `0`。
- Shadow scorer 对 deprecated / historical noise 有明显 demotion 信号，且不会改变正式排序或 selected set。
- `mustHitDemotions` 和 `mustNotHitPromotions` 作为风险信号保留在 trace/report 中；本阶段不据此自动调权、不上线、不改变 runtime。

## 解释

当前离线结果说明：

- Router baseline 已能提供比 majority 更强的规则基线，但仍只作为后续实验参照。
- Ranker weighted baseline 优于 rule score baseline，可作为后续离线模型实验的最低对照线。
- PromotionJudge、ConstraintGapJudge、AttentionScorer 仍保持 NotReady，不进入本阶段 baseline。

这些结果不会接入 runtime，不改变正式 retrieval output，也不会自动应用到 policy。

## Lifecycle-aware Ranker Debug Opt-in

Learning Loop Phase 6E 新增只读 debug opt-in endpoint，用于在服务模式下手动输入 query 并查看 lifecycle-aware shadow score 对候选的解释。本阶段仍不接 runtime scorer，不修改 retrieval scoring，不改变 selected set，不改 `PackingPolicy`。

Endpoint：

- `POST /api/retrieval/ranker-shadow/debug`

请求字段：

- `query`
- `workspaceId`
- `collectionId`
- `mode`
- `candidateIds`（可选过滤）
- `includeLifecycleDetails`

响应字段包括：

- `legacyScore`
- `lifecycleAwareScore`
- `scoreDelta`
- lifecycle-aware feature breakdown
- `deprecatedDemotions`
- `historicalDemotions`
- `currentActivePromotions`
- `versionConflictFixes`
- `mustHitDemotions`
- `mustNotHitPromotions`
- `FormalOutputChanged=false`
- `SelectedSetChanged=false`

默认配置：

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

`DebugEndpointEnabled` 只控制只读 debug endpoint 是否可调用；它不启用 runtime scorer，也不让 shadow score 影响正式 retrieval output。

## Runtime Shadow Trace Collection

Learning Loop Phase 6F 新增 runtime trace collection 的只读观察能力。默认仍关闭：

- `Learning:RankerShadow:TraceCollectionEnabled=false`
- `Learning:RankerShadow:Profile=lifecycle-aware-v1`
- `Learning:RankerShadow:MaxCandidatesPerTrace=50`

当 `TraceCollectionEnabled=true` 时，retrieval/package trace 会附加 `RankerShadowTrace`，记录最多 `MaxCandidatesPerTrace` 个 candidate 的：

- `candidateId`
- `legacyScore`
- `lifecycleAwareScore`
- `scoreDelta`
- `lifecycleFeatures`
- `demotionReasons`
- `promotionReasons`

该 trace 在正式 selected set、排序和 package sections 已经确定之后生成，不回写 scorer，不参与 packing，不改变正式输出。trace metadata 固定记录 `rankerShadowFormalOutputChanged=false`、`rankerShadowSelectedSetChanged=false`、`rankerShadowPackageSectionsChanged=false`。

导出入口：

- `GET /api/learning/ranker-shadow/traces?workspaceId=...&collectionId=...`
- `GET /api/learning/ranker-shadow/traces?workspaceId=...&collectionId=...&format=jsonl`

JSONL/NDJSON 导出用于离线审计 recent shadow traces；它只读取 retrieval trace store，不触发新的 retrieval，也不改变业务逻辑。

## Ranker Shadow Trace Quality Report

Learning Loop Phase 6G 新增 runtime shadow trace quality report，用于统计 6F 捕获的 lifecycle-aware ranker shadow traces。报告只读取 `RankerShadowTrace`，不触发 retrieval，不修改 scorer，不改变 selected set、排序、package sections 或 `PackingPolicy`。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval ranker-shadow-trace-quality
```

输出：

- `learning/baselines/ranker-shadow-trace-quality-report.json`
- `learning/baselines/ranker-shadow-trace-quality-report.md`

报告字段包括：

- `TraceCount`
- `CandidateScoreCount`
- `DeprecatedDemotionCount`
- `HistoricalDemotionCount`
- `VersionConflictFixCount`
- `CurrentVersionPromotionCount`
- `MustHitDemotedCount`
- `MustNotHitPromotedCount`
- `LifecycleViolationCount`
- `AverageScoreDelta`
- `MaxPositiveDelta`
- `MaxNegativeDelta`
- `ModeBreakdown`
- `IntentBreakdown`
- `RiskSamples`
- `RecommendedNextStep`

推荐结论：

- `NeedsMoreRealTraces`：无 trace、无 candidate score，或 `TraceCount < 30`。
- `BlockedByRisk`：出现 must-hit demotion、must-not-hit promotion 或 lifecycle violation。
- `KeepShadowOnly`：trace 数足量且无风险，但缺少 deprecated / version conflict 有效信号。
- `ReadyForGuardedOptIn`：达到下一阶段讨论门槛；仍不代表自动启用。

进入下一阶段门槛：

- `TraceCount >= 30`
- `CandidateScoreCount > 0`
- `MustHitDemotedCount = 0`
- `MustNotHitPromotedCount = 0`
- `LifecycleViolationCount = 0`
- `DeprecatedDemotionCount > 0` 或 `VersionConflictFixCount > 0`

当前本地生成结果：

- `TraceCount=0`
- `CandidateScoreCount=0`
- `RecommendedNextStep=NeedsMoreRealTraces`

这符合 `TraceCollectionEnabled=false` 的默认状态。后续如果需要评估真实 runtime traces，应先显式开启 trace collection，再运行该 CLI；即便开启，trace 仍只用于观测，不影响正式输出。

Phase 6H 新增采集 runbook 与脚本：

- `docs/ranker-shadow-trace-collection-runbook.md`
- `scripts/collect-ranker-shadow-traces.ps1`

脚本默认 dry-run；使用 `-Execute` 才会调用已运行的 `ContextCore.Service`，采样固定 query、导出 traces 并运行 trace quality report。
