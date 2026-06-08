# Context Attention Scoring

更新时间：2026-06-03

## 当前范围

Phase 4 引入 guarded attention rerank experiment：

- 只计算并记录 attention score
- 记录 current rank vs attention rank 的差异
- 记录 would-select shadow report
- 对多个 attention profile 做 shadow 对比
- 记录 profile 级 eval metrics 和 regression diagnostics
- 记录重点回归样本的 candidate-level breakdown
- 记录 mustNotHit promotion diagnostics
- 默认不改变 retrieval 排序
- 仅在显式配置 `Retrieval:AttentionRerank:Enabled=true` 时启用 guarded rerank
- guarded rerank 只允许调整 PackingPolicy 已选中 item 的内部顺序
- 不改变 packing policy
- 不改变 package 输出
- 不替换旧 scoring 逻辑
- 不调用模型
- 不训练模型

## 数据模型

`ContextAttentionFeatureVector` 记录候选特征：

- candidate kind / type
- memory layer / lifecycle
- importance / recency
- channel hits
- relation paths
- scope
- matched tokens / anchors
- learning feedback counts

`ContextAttentionScore` 记录 shadow 分数：

- `FinalAttentionScore`
- `QueryMatchScore`
- `ShortTermMatchScore`
- `RelationScore`
- `RecencyScore`
- `ImportanceScore`
- `ChannelScore`
- `LearningFeedbackScore`
- `LifecyclePenalty`
- `ScopePenalty`
- `NoiseRiskScore`
- `Reasons`
- `ProfileId`
- `PolicyVersion`

`ContextAttentionProfile` 当前 profile variants：

- `default-shadow-v1`
- `conservative-v1`
- `relation-balanced-v1`
- `learning-light-v1`
- `lifecycle-strict-v1`
- `old-score-anchored-v1`
- `delta-limited-v1`
- `guarded-shadow-v1`

权重集中在 profile 内配置，不散落在 scorer 逻辑中。

Phase 3.2 新增的保守 profile：

- `old-score-anchored-v1`：以 current score / current rank 为主锚点，attention 只作为小幅 delta。
- `delta-limited-v1`：限制 attention rank delta，并保护 current rank 1 / top 3 candidate。
- `guarded-shadow-v1`：在 shadow would-select 中过滤 mustNotHit，强惩罚 lifecycle risk，并保护 current top / exact anchor / hard constraint。

`AttentionShadowRank` 记录单个候选的排序差异：

- `CandidateId`
- `CurrentRank`
- `AttentionRank`
- `RankDelta`
- `CurrentScore`
- `AttentionScore`
- `Lifecycle`
- `ChannelSources`
- `RelationPaths`
- `ScoreBreakdown`
- `SelectedByCurrentPolicy`
- `WouldBeSelectedByAttention`
- `IsMustHit`
- `IsMustNotHit`
- `Reasons`

`AttentionShadowReport` 记录整体 shadow diff：

- `CandidateCount`
- `SelectedCount`
- `WouldChangeSelectedSet`
- `AddedByAttention`
- `DroppedByAttention`
- `TopPromotedCandidates`
- `TopDemotedCandidates`
- `MustNotHitPromotedCount`
- `SelectedSetChangeRatio`
- `Warnings`

`AttentionProfileExperimentReport` 记录多个 profile 的 shadow 对比：

- `OperationId`
- `Profiles`
- 每个 profile 包含 `AttentionScores` 和对应的 `AttentionShadowReport`

`AttentionRerankComparisonReport` 记录 guarded rerank 实验结果：

- `Applied` / `Skipped` / `Blocked`
- `SkippedReason` / `BlockedReason`
- `AddedItems` / `DroppedItems`
- `OrderChanges`
- `SectionChanges`
- `MustHitRankDeltas`
- `MustNotHitRankDeltas`
- `SelectedSetChangeCount`
- `SelectedSetChangeRatio`
- `Warnings`

## Scorer

当前实现：

- `IContextAttentionScorer`
- `RuleBasedContextAttentionScorer`
- `AttentionProfileExperimentRunner`

规则型 scorer 只根据候选 metadata、relation path、channel sources 和 learning records 计算 shadow 分数。

## Learning Feedback

第一版只做简单统计：

- `PromotionAccepted / Positive`：增加 attention boost
- `PromotionRejected / Negative`：增加 noise penalty
- `PromotionExpired / Stale`：增加 stale penalty

匹配依据包括：

- `SourceId`
- `CandidateId`
- `targetItemId`
- `sourceCandidateId`
- `EvidenceRefs`
- candidate `SourceRefs`

## Retrieval 接入

接入点：

- `HybridContextRetriever`
- `RetrievalPackingPolicy.BuildRankedCandidates(...)` 之后
- `RetrievalPackingPolicy.Pack(...)` 之前计算 `AttentionScores`
- `RetrievalPackingPolicy.Pack(...)` 之后组装 `AttentionShadowReport`
- 同一候选池上运行多个 profile 的 shadow comparison
- `RetrievalTraceAssembler` 之前

因此：

- 默认配置下 attention 不参与候选排序
- attention 不参与 TopK
- attention 不参与 token budget
- 默认配置下 attention 不改变 `SelectedItems`
- attention 不改变 `DroppedItems`
- attention 只写入 `ContextRetrievalTrace.AttentionScores`
- attention rank/diff 只写入 `ContextRetrievalTrace.AttentionShadowReport`
- profile 对比只写入 `ContextRetrievalTrace.AttentionProfileComparison`
- guarded rerank 启用时只允许对 `SelectedItems` 内部排序做 selected-set-preserving 调整

trace metadata 会记录：

- `attentionShadowMode=true`
- `attentionProfileId`
- `attentionPolicyVersion`
- `attentionShadowCandidateCount`
- `attentionShadowWouldChangeSelectedSet`
- `attentionShadowSelectedSetChangeRatio`
- `attentionProfileComparisonCount`
- `attentionRerankEnabled`
- `attentionRerankMode`
- `attentionRerankProfileId`
- `attentionRerankApplied`
- `attentionRerankBlocked`
- `attentionRerankSkippedReason`
- `attentionRerankBlockedReason`

## Phase 4 Guarded Rerank

配置：

```json
{
  "Retrieval": {
    "AttentionRerank": {
      "Enabled": false,
      "Mode": "SelectedSetPreserving",
      "ProfileId": "old-score-anchored-v1"
    }
  }
}
```

规则：

- 默认 `Enabled=false`，所有 retrieval 输出保持旧顺序。
- `Mode=SelectedSetPreserving` 时，`RetrievalPackingPolicy.Pack(...)` 仍决定 selected item 集合。
- rerank 只对 `SelectedCandidates` 和对应 selected decisions 做内部重排。
- `DroppedItems` 不变。
- selected set change 必须为 0。
- 即使启用 rerank，也继续记录 `AttentionShadowReport` 和 `AttentionProfileComparison`。

Safety guard：

- mustNotHit 不允许被上推。
- hard constraint / mandatory item 不允许被降级。
- rejected / deprecated / superseded lifecycle risk 不允许被上推。
- selected set 发生新增或删除时整次 rerank blocked，并回退原始 packing 顺序。

实验报告：

```powershell
dotnet run --project src/ContextCore.ControlRoom/ContextCore.ControlRoom.csproj --no-build -- eval guarded-rerank-comparison --out eval/guarded-attention-rerank-comparison-report.json
```

该报告只汇总 trace 中的 comparison 信息，不参与样本通过率判定。

## Eval Shadow Metrics

评测报告新增 shadow 指标：

- `AvgAttentionMrr`
- `AvgAttentionRecall3`
- `AvgAttentionRecall5`
- `AttentionImprovedSamples`
- `AttentionRegressedSamples`
- `MustNotHitPromotedCount`
- `SelectedSetChangeRatio`

Phase 3 新增 profile 级评测：

- 每个 profile 输出 Attention MRR
- 每个 profile 输出 Attention Recall@3 / Recall@5
- 每个 profile 输出 Improved / Regressed sample count
- 每个 profile 输出 MustNotHitPromotedCount
- 每个 profile 输出 SelectedSetChangeRatio
- 每个 profile 输出 category breakdown

Phase 3 新增 diagnostics：

- top regressed samples
- mustHit demoted samples
- mustNotHit promoted samples
- selected set changed samples
- candidate-level breakdown for focused regressions
- currentMRR=1 regression count
- promoted mustNotHit candidate diagnostics

这些指标只用于观察 attention 排序潜力，不参与样本通过率、不替代 package 输出。

Phase 3.2 重点关注样本：

- `project-sample-009`
- `coding-sample-009`
- `novel-sample-002`

输出内容包括 candidate source id、current/attention rank、rank delta、score breakdown、channel sources、relation paths 和 reasons。

## Phase 3.2 Selection Result

最新 selection report：

- recommendedProfile：`old-score-anchored-v1`
- recommendedMode：`guarded-rerank-candidate`
- riskLevel：`low`
- blockingIssues：空

说明：`guarded-rerank-candidate` 只是下一步实验建议。当前实现仍只写 shadow trace/eval report，没有启用实际 rerank。

## ControlRoom

Retrieval Debug 页面新增 `Attention Shadow Trace` 和 `Attention Shadow Diff`：

- current rank
- attention rank
- rank delta
- current selected / would select
- final attention score
- score breakdown
- reasons
- learning / noise contribution

Phase 3 额外展示 `Attention Profile Comparison`：

- profile 名称
- current rank vs attention rank
- rank delta
- selected / would select
- top promoted / top demoted
- reasons / score breakdown

## 当前边界

当前不做：

- attention rerank
- attention-driven packing
- vector attention model
- LLM judge
- 自动调参
- 自动改 retrieval scoring
