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

## Router Intent Classifier R1

Learning Loop R1 新增独立的 router intent classifier offline baseline，不替换 runtime router，不改变 retrieval / planning / `PackingPolicy`。

命令：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval router-intent-baseline
```

默认输入：

- `learning/features/router-intent-examples.jsonl`

默认输出：

- `learning/router/router-intent-baseline-report.json`
- `learning/router/router-intent-baseline-report.md`
- `learning/router/router-intent-confusion-matrix.json`

R1 baseline：

- `ExistingRuleBasedRouterBaseline`：只测量现有 `PlanningIntentDetector` 的当前行为，不接入、不替换 runtime router。
- `TokenCentroidRouterBaseline`：从训练 split 的通用 token 特征建立 intent centroid，不读取 label / intent 作为预测输入，不读取 sampleId / itemId / fixture name，也不使用领域词表。

R1 report 指标：

- `Accuracy`
- `MacroF1`
- `PerIntentPrecision`
- `PerIntentRecall`
- `ConfusionMatrix`
- `LowConfidenceCount`
- `AbstainCount`
- `CurrentTaskRecall`
- `FuzzyQuestionRecall`
- `CodingTaskRecall`
- `NovelGenerationRecall`
- `AutomationRecoveryRecall`
- `Recommendation`

Recommendation：

- `KeepRuleBased`
- `ReadyForRouterShadow`
- `NeedsMoreExamples`
- `NeedsNegativeSamples`
- `NeedsIntentBoundaryClarification`
- `BlockedByLowRecall`

R1 约束：

- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。
- 不改变 runtime router。
- 不改变 retrieval / planning / `PackingPolicy`。

当前 R1 run：

| Baseline | Accuracy | MacroF1 | LowConfidence | Abstain | Recommendation |
|---|---:|---:|---:|---:|---|
| ExistingRuleBasedRouterBaseline | 97.14 % | 0.6176 | 0 | 0 | KeepRuleBased |
| TokenCentroidRouterBaseline | 91.43 % | 0.7027 | 0 | 0 | ReadyForRouterShadow |

R1 当前结论：

- `SampleCount=163`
- `BestBaseline=TokenCentroidRouterBaseline`
- `Recommendation=ReadyForRouterShadow`
- 仅表示可进入 router shadow 准备；不替换 runtime router。

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

## Router Intent Shadow R2

R2 新增 Router Intent Shadow Trace 与离线 disagreement analysis。该能力默认关闭，只做旁路观测，不替换 runtime router。

配置：

- `Learning:RouterShadow:Enabled=false`
- `Learning:RouterShadow:TraceCollectionEnabled=false`
- `Learning:RouterShadow:ShadowClassifier=TokenCentroidRouterBaseline`
- `Learning:RouterShadow:RecordAgreements=true`
- `Learning:RouterShadow:RecordDisagreements=true`

入口：

- query / package / planning 入口在配置开启时可写 `RouterIntentShadowTrace`
- `GET /api/learning/router-shadow/traces`
- `GET /api/learning/router-shadow/traces?format=jsonl`

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval router-intent-shadow-eval
dotnet run --project src\ContextCore.ControlRoom -- eval router-shadow-trace-quality
```

输出：

- `learning/router/router-intent-shadow-eval-a3.json`
- `learning/router/router-intent-shadow-eval-extended.json`
- `learning/router/router-intent-shadow-eval.md`
- `learning/router/router-shadow-trace-quality-report.json`
- `learning/router/router-shadow-trace-quality-report.md`

报告字段：

- `TraceCount`
- `AgreementRate`
- `DisagreementRate`
- `LowConfidenceCount`
- `AbstainCount`
- `DisagreementByIntent`
- `LowConfidenceByIntent`
- `TopConfusionPairs`
- `ShadowFixesRuntime`
- `ShadowBreaksRuntime`
- `NetGain`
- `PerIntentGain`
- `PerIntentRegression`
- `Recommendation`

当前结果：

- 离线 shadow eval：`AgreementRate=88.57%`，`ShadowFixesRuntime=1`，`ShadowBreaksRuntime=3`，`Recommendation=KeepRuleBased`
- runtime trace quality：`TraceCount=0`，`Recommendation=NeedsMoreRealTraces`

因此 R2 结论是继续保留 runtime rule-based router，后续只有在真实 trace 足量且 disagreement analysis 没有回归风险时才讨论 shadow readiness。R2 不改变 retrieval、planning、`PackingPolicy`、runtime router 或 package output。

## Router Disagreement Triage R2.1

R2.1 新增 router disagreement triage、intent boundary 文档和 hard negative 导出。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval router-disagreement-triage
```

输出：

- `learning/router/router-disagreement-triage-a3.json`
- `learning/router/router-disagreement-triage-extended.json`
- `learning/router/router-disagreement-triage.md`
- `learning/router/router-hard-negatives.jsonl`
- `docs/router-intent-boundaries.md`

Triage category：

- `ShadowFixesRuntime`
- `ShadowBreaksRuntime`
- `BothWrong`
- `BothPlausible`
- `IntentBoundaryAmbiguous`
- `LowConfidenceCentroid`
- `SparseIntentExamples`
- `NeedsHardNegative`
- `NeedsIntentDefinition`
- `KeepRuleBased`

Hard negatives：

- `positiveIntent` 使用 expected intent。
- `negativeIntent` 使用错误的 runtime / shadow intent。
- JSONL 按 query / mode / intent / source sample 去重。
- 只用于离线数据增强，不替换 runtime router，不改变 retrieval、planning、`PackingPolicy` 或 package output。

## Router Shadow Freeze R2.F

R2.F 将 R1 - R2.1 的 router shadow 结论冻结，并新增 guarded opt-in readiness gate。

冻结结果：

- R1 best classifier：`TokenCentroidRouterBaseline`
- R2 agreement rate：`88.57%`
- R2 runtime trace：`TraceCount=0`，`NeedsMoreRealTraces`
- R2.1：`fixes=1`，`breaks=3`，`hardNegatives=3`
- 当前结论：`KeepRuleBased`
- R3 guarded opt-in：blocked

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval router-guarded-optin-readiness-gate
```

输出：

- `learning/router/router-guarded-optin-readiness-gate.json`
- `learning/router/router-guarded-optin-readiness-gate.md`
- `docs/router-intent-shadow-freeze.md`

当前 gate 应失败，原因包含：

- `ShadowBreaksRuntimeGreaterThanFixes`

该 gate 只读取已有 R2/R2.1 报告与 P15 eval report，不替换 runtime router，不改变 retrieval、planning、`PackingPolicy` 或 package output。

## Candidate Reranker Shadow CR1

CR1 新增 candidate reranker shadow baseline 与 runtime trace quality report。它复用 lifecycle-aware ranker shadow score 作为离线 baseline，不把 score 接入正式 retrieval scoring，也不改变 selected set、`PackingPolicy` 或 package output。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-eval
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-trace-quality
```

输出：

- `learning/ranker/candidate-reranker-shadow-eval-a3.json`
- `learning/ranker/candidate-reranker-shadow-eval-extended.json`
- `learning/ranker/candidate-reranker-shadow-eval.md`
- `learning/ranker/candidate-reranker-shadow-trace-quality-report.json`
- `learning/ranker/candidate-reranker-shadow-trace-quality-report.md`

指标：

- `FormalTop1Accuracy`
- `ShadowTop1Accuracy`
- `FormalMRR`
- `ShadowMRR`
- `WouldChangeTop1Count`
- `WouldImproveCount`
- `WouldRegressCount`
- `LifecycleRiskCount`
- `DeprecatedRiskCount`
- `MustNotRiskCount`
- `NetGain`
- `Recommendation`

当前结果：

- A3：`Recommendation=BlockedByRisk`
- Extended：`Recommendation=BlockedByRisk`
- Runtime trace quality：`TraceCount=0`，`Recommendation=NeedsMoreRealTraces`

CR1 结论是继续 shadow-only，不进入 guarded opt-in。

## Candidate Reranker Failure Audit CR1.1

CR1.1 在 CR1 shadow eval 基础上新增 failure audit 和 score contract audit。它只解释回归原因和候选 metadata 对齐问题，不替换 runtime scorer，不改变 retrieval、selected set、`PackingPolicy` 或 package output。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-failure-audit
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-eval
```

输出：

- `learning/ranker/candidate-reranker-shadow-failure-audit-a3.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit-extended.json`
- `learning/ranker/candidate-reranker-shadow-failure-audit.md`

审计字段：

- `ScoreContractStatus`
- `RankableCandidateCount`
- `BlockedCandidateCount`
- `RiskCandidateInShadowTopK`
- `RegressionReasonSummary`
- `WhyShadowPromoted`
- `RegressionReason`
- `RecommendedAction`

score contract 测试覆盖：

- higher score ranks first
- deprecated candidate cannot outrank active equivalent
- lifecycle penalty direction correct
- missing lifecycle metadata not treated as positive
- risk candidate cannot be safe improvement

CR1.1 继续保持 shadow-only。报告中的 regression reason 用于后续离线 feature tuning / metadata alignment，不作为 runtime policy。

## Candidate Reranker Metadata Alignment CR1.2

CR1.2 在 CR1.1 failure audit 结论上新增 candidate metadata alignment、feature completeness report 和 rerank eligibility guard。该阶段只修正 shadow runner 的 feature envelope 输入边界，不接入正式 scorer，不改变 retrieval、selected set、`PackingPolicy` 或 package output。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-feature-completeness
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-eval
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-failure-audit
```

输出：

- `learning/ranker/candidate-reranker-feature-completeness-a3.json`
- `learning/ranker/candidate-reranker-feature-completeness-extended.json`
- `learning/ranker/candidate-reranker-feature-completeness.md`
- refreshed `learning/ranker/candidate-reranker-shadow-eval-a3.json`
- refreshed `learning/ranker/candidate-reranker-shadow-eval-extended.json`
- refreshed `learning/ranker/candidate-reranker-shadow-failure-audit-a3.json`
- refreshed `learning/ranker/candidate-reranker-shadow-failure-audit-extended.json`

新增字段：

- `CandidateFeatureEnvelope`
- `RankerCandidateEligibilityDecision`
- `RawCandidateCount`
- `FeatureCompletenessRate`
- `RiskCandidateInRawTopK`
- `RiskCandidateInShadowTopK`
- `RiskCandidateBlockedBeforeRerank`
- `EligibilityGuardStatus`

当前结果：

- A3 feature completeness：`88.02%`，`MissingFeatureMetadataCount=204`，`RiskCandidateBlockedBeforeRerank=152`
- Extended feature completeness：`95.02%`，`MissingFeatureMetadataCount=658`，`RiskCandidateBlockedBeforeRerank=373`
- A3 shadow eval：`RiskCandidateInShadowTopK=0`，`ScoreContractStatus=Passed`，`NetGain=-17`，`Recommendation=KeepFormalRanking`
- Extended shadow eval：`RiskCandidateInShadowTopK=0`，`ScoreContractStatus=Passed`，`NetGain=-3`，`Recommendation=KeepFormalRanking`
- Failure audit 剩余 regression：A3 `20`，Extended `30`；主要仍为 `MissingFeatureMetadata`，后续应补 runtime metadata，而不是调正式 scorer。

CR1.2 结论：

- shadow topK 风险已由 guard 阻断到 `0`。
- score contract 不再处于 `NeedsAudit`。
- 当前质量仍不足以替换 formal ranking，继续保持离线 shadow-only。
- 不使用 sampleId / itemId / fixture / 领域词表特判。

## Candidate Reranker Calibration CR1.3

CR1.3 在 CR1.2 的 guard-aware shadow eval 基础上新增 score distribution 与 listwise calibration audit。该阶段只分析离线 shadow score scale、top1 margin、listwise regression、formal ranking implicit priority，不替换 runtime scorer，不改变 retrieval、selected set、`PackingPolicy` 或 package output。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-score-distribution
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-listwise-calibration
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-eval
```

输出：

- `learning/ranker/candidate-reranker-score-distribution-a3.json`
- `learning/ranker/candidate-reranker-score-distribution-extended.json`
- `learning/ranker/candidate-reranker-score-distribution.md`
- `learning/ranker/candidate-reranker-listwise-calibration-a3.json`
- `learning/ranker/candidate-reranker-listwise-calibration-extended.json`
- `learning/ranker/candidate-reranker-listwise-calibration.md`

指标：

- `ScoreMean`
- `ScoreStdDev`
- `Top1MarginAverage`
- `Top1MarginForRegressions`
- `Top1MarginForImprovements`
- `ScoreOverlapMustHitVsNonHit`
- `FeatureContributionByType`
- `DominantFeatureCount`
- `LowMarginDecisionCount`
- `FormalPriorityComparison`
- `CalibrationIssue`

当前结果：

- A3 score distribution：`ScoreMean=55.4941`，`ScoreStdDev=30.1902`，`Top1MarginAverage=31.2068`，`ScoreOverlapMustHitVsNonHit=0.9053`，`Recommendation=NeedsFeatureTuning`
- Extended score distribution：`ScoreMean=37.2669`，`ScoreStdDev=24.1415`，`Top1MarginAverage=27.5602`，`ScoreOverlapMustHitVsNonHit=0.6023`，`Recommendation=NeedsFeatureTuning`
- A3 listwise calibration：`RegressionCount=20`，`LowMarginDecisionCount=5`，`FormalPriorityMismatchCount=3`，`AverageTopKOverlap=0.5732`，`Recommendation=NeedsFeatureTuning`
- Extended listwise calibration：`RegressionCount=30`，`LowMarginDecisionCount=1`，`FormalPriorityMismatchCount=27`，`AverageTopKOverlap=0.6666`，`Recommendation=NeedsFeatureTuning`

CR1.3 结论：

- 当前问题不是 score direction 或 risk candidate 进入 topK，而是 listwise calibration 与 formal implicit priority 表达不足。
- `current_version_boost` 是当前离线贡献里的 dominant feature，后续需要补 intent / mode / formal priority 特征，而不是把权重接入 runtime。
- 当前保持 `KeepFormalRanking` / `NeedsFeatureTuning`，不进入 runtime trace 或 guarded opt-in。
- eval label 只用于报告评估，不进入 policy。
- 不使用 sampleId / itemId / fixture / 领域词表特判。

## Candidate Reranker Formal Priority Alignment CR1.4

CR1.4 新增 formal priority feature alignment 和 shadow-only listwise repair。它把 formal ranking 中可观测的 layer / source / current-task / constraint / relation / freshness / package-policy / lifecycle priority 提取为离线特征，再用 shadow profile 对比是否能减少 listwise regression。该阶段仍不替换 runtime scorer，不改变 retrieval、selected set、`PackingPolicy` 或 package output。

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-formal-priority-alignment
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-eval --profile baseline-lifecycle-aware
dotnet run --project src\ContextCore.ControlRoom -- eval candidate-reranker-shadow-eval --profile formal-priority-aware-with-abstain-v1
```

输出：

- `learning/ranker/candidate-reranker-formal-priority-alignment-a3.json`
- `learning/ranker/candidate-reranker-formal-priority-alignment-extended.json`
- `learning/ranker/candidate-reranker-formal-priority-alignment.md`
- refreshed `learning/ranker/candidate-reranker-shadow-eval-a3.json`
- refreshed `learning/ranker/candidate-reranker-shadow-eval-extended.json`

Formal priority features：

- `LayerPriority`
- `SourcePriority`
- `CurrentTaskBoost`
- `ConstraintRelevance`
- `RelationEvidenceBoost`
- `StableMemoryBias`
- `WorkingMemoryBias`
- `CandidateMemoryPenalty`
- `FreshnessPriority`
- `PackagePolicyPriority`
- `LifecyclePriority`

当前结果：

- A3 alignment：`RegressionCount=20`，`RecoveredCount=0`，`UnexplainedMismatchCount=20`，`AbstainCount=0`，`NetGainAfterAbstain=3`，`Recommendation=NeedsFeatureTuning`
- Extended alignment：`RegressionCount=30`，`RecoveredCount=0`，`UnexplainedMismatchCount=30`，`AbstainCount=2`，`NetGainAfterAbstain=23`，`Recommendation=NeedsFeatureTuning`
- 默认 baseline shadow eval：A3 `NetGain=-17`，`NetGainAfterAbstain=3`，`Recommendation=KeepFormalRanking`
- 默认 baseline shadow eval：Extended `NetGain=-3`，`NetGainAfterAbstain=27`，`Recommendation=KeepFormalRanking`

CR1.4 结论：

- formal-priority-aware profile 没有追回 baseline regression，说明当前缺口不只是简单 layer/source/freshness 对齐。
- `NetGainAfterAbstain` 只说明部分 would-apply 场景可由 abstain 避免回归，不是 runtime opt-in 条件。
- 当前继续保持 `KeepFormalRanking`，不进入 runtime trace 或 guarded opt-in。
- feature extractor 只使用 runtime metadata，不读取 eval label、sampleId、itemId、fixture 或领域词表。
