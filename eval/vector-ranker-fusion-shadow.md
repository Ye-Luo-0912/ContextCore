# Vector + Ranker Fusion Shadow Report

Generated: 2026-06-10T13:57:04.9852465+00:00

## A3 Summary

- Samples: `50`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Profile: `normal-v1`
- TopK: `10`
- MinSimilarity: `-`
- Recommendation: `NeedsFusionTuning`
- BestStrategy: `VectorOnly`
- BestRecallFusion: `71.21%`
- BestMRRFusion: `0.6765`
- BestRiskFusion: `0.00%`
- BestLifecycleRisk: `0.00%`

| Strategy | Vector Recall | Fusion Recall | Vector MRR | Fusion MRR | Fusion Risk | Lifecycle Risk | Gain | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| VectorOnly | 71.21% | 71.21% | 0.6765 | 0.6765 | 0.00% | 0.00% | 0.00% | NeedsFusionTuning |
| RankerFilteredVector | 71.21% | 72.73% | 0.6765 | 0.6765 | 4.00% | 0.00% | 1.52% | BlockedByRisk |
| RankerOnly | 71.21% | 51.52% | 0.6765 | 0.5664 | 4.00% | 0.00% | -19.70% | BlockedByRisk |
| UnionThenRank | 71.21% | 51.52% | 0.6765 | 0.5664 | 4.00% | 0.00% | -19.70% | BlockedByRisk |
| VectorBoostedRanker | 71.21% | 51.52% | 0.6765 | 0.5664 | 4.00% | 0.00% | -19.70% | BlockedByRisk |
| LifecycleAwareFusion | 71.21% | 51.52% | 0.6765 | 0.5664 | 4.00% | 0.00% | -19.70% | BlockedByRisk |

### Top Fixed Samples

| Strategy | Sample | Mode | Intent | Gained | RecallGain |
|---|---|---|---|---|---:|
| RankerFilteredVector | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| RankerOnly | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| UnionThenRank | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| VectorBoostedRanker | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| LifecycleAwareFusion | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |

### Newly Risky Samples

| Strategy | Sample | Mode | Intent | RiskDelta | Items |
|---|---|---|---|---:|---|
| RankerFilteredVector | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| RankerFilteredVector | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| RankerOnly | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| RankerOnly | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| UnionThenRank | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| UnionThenRank | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| VectorBoostedRanker | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| VectorBoostedRanker | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| LifecycleAwareFusion | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| LifecycleAwareFusion | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |

### Warnings
- fusion shadow 使用候选池 topK=50，最终评估 topK=10；报告不改变正式 retrieval/package 输出。
- best fusion 策略尚未满足 retrieval shadow readiness，不得接入正式检索。

## Extended Summary

- Samples: `113`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Profile: `normal-v1`
- TopK: `10`
- MinSimilarity: `-`
- Recommendation: `ReadyForRetrievalShadow`
- BestStrategy: `VectorOnly`
- BestRecallFusion: `84.38%`
- BestMRRFusion: `0.8229`
- BestRiskFusion: `0.00%`
- BestLifecycleRisk: `0.00%`

| Strategy | Vector Recall | Fusion Recall | Vector MRR | Fusion MRR | Fusion Risk | Lifecycle Risk | Gain | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| VectorOnly | 84.38% | 84.38% | 0.8229 | 0.8229 | 0.00% | 0.00% | 0.00% | ReadyForRetrievalShadow |
| RankerFilteredVector | 84.38% | 85.00% | 0.8229 | 0.8229 | 1.77% | 0.00% | 0.63% | BlockedByRisk |
| RankerOnly | 84.38% | 76.25% | 0.8229 | 0.7742 | 1.77% | 0.00% | -8.13% | BlockedByRisk |
| UnionThenRank | 84.38% | 76.25% | 0.8229 | 0.7742 | 1.77% | 0.00% | -8.13% | BlockedByRisk |
| VectorBoostedRanker | 84.38% | 76.25% | 0.8229 | 0.7742 | 1.77% | 0.00% | -8.13% | BlockedByRisk |
| LifecycleAwareFusion | 84.38% | 76.25% | 0.8229 | 0.7742 | 1.77% | 0.00% | -8.13% | BlockedByRisk |

### Top Fixed Samples

| Strategy | Sample | Mode | Intent | Gained | RecallGain |
|---|---|---|---|---|---:|
| RankerFilteredVector | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| RankerOnly | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| UnionThenRank | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| VectorBoostedRanker | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |
| LifecycleAwareFusion | chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 50.00% |

### Newly Risky Samples

| Strategy | Sample | Mode | Intent | RiskDelta | Items |
|---|---|---|---|---:|---|
| RankerFilteredVector | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| RankerFilteredVector | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| RankerOnly | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| RankerOnly | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| UnionThenRank | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| UnionThenRank | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| VectorBoostedRanker | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| VectorBoostedRanker | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |
| LifecycleAwareFusion | automation-sample-009 | AutomationMode | Unknown | 1 | memory:automation-backup-strategy-v2 |
| LifecycleAwareFusion | project-sample-009 | ProjectMode | Unknown | 1 | memory:project-pool-v2 |

### Warnings
- fusion shadow 使用候选池 topK=50，最终评估 topK=10；报告不改变正式 retrieval/package 输出。

