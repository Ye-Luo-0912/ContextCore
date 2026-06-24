# Vector Safe Recall Recovery Report

Generated: 2026-06-10T09:17:07.0307153+00:00

## A3 Summary

- Samples: `50`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- BaselineRecallAfterPolicy: `71.21%`
- BaselineMRR: `0.6765`
- BaselineRiskAfterPolicy: `0`
- BelowTopKMissCount: `10`
- BlockedMustHitCount: `9`
- Recommendation: `KeepPreviewOnly`
- BestSafeSweep: `normal-v1:top10:min0.05:stable-only`
- BestRecallAfterPolicy: `3.03%`
- BestRiskAfterPolicy: `0`
- RecoveredBelowTopK: `0/10`

| Configuration | RecallAfter | MRR | RiskAfter | MustNotRisk | LifecycleRisk | RecoveredBelowTopK | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---|
| audit-v1:top50:min0.05:exclude-historical | 87.88% | 0.6994 | 104 | 24.56% | 4.29% | 10/10 | BlockedByRisk |
| audit-v1:top50:min0.10:exclude-historical | 87.88% | 0.6994 | 104 | 24.56% | 4.29% | 10/10 | BlockedByRisk |
| audit-v1:top50:min0.15:exclude-historical | 87.88% | 0.6994 | 104 | 24.56% | 4.29% | 10/10 | BlockedByRisk |
| audit-v1:top50:min0.20:exclude-historical | 87.88% | 0.6994 | 104 | 24.56% | 4.29% | 10/10 | BlockedByRisk |
| audit-v1:top50:min0.30:exclude-historical | 87.88% | 0.6994 | 104 | 24.56% | 4.33% | 10/10 | BlockedByRisk |
| diagnostics-v1:top50:min0.05:exclude-historical | 87.88% | 0.6960 | 109 | 33.33% | 4.04% | 10/10 | BlockedByRisk |
| diagnostics-v1:top50:min0.10:exclude-historical | 87.88% | 0.6960 | 109 | 33.33% | 4.04% | 10/10 | BlockedByRisk |
| diagnostics-v1:top50:min0.15:exclude-historical | 87.88% | 0.6960 | 109 | 33.33% | 4.04% | 10/10 | BlockedByRisk |
| diagnostics-v1:top50:min0.20:exclude-historical | 87.88% | 0.6960 | 109 | 33.33% | 4.04% | 10/10 | BlockedByRisk |
| diagnostics-v1:top50:min0.30:exclude-historical | 87.88% | 0.6960 | 109 | 33.33% | 4.08% | 10/10 | BlockedByRisk |
| normal-v1:top50:min0.05:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| normal-v1:top50:min0.10:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| normal-v1:top50:min0.15:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| normal-v1:top50:min0.20:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| normal-v1:top50:min0.30:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| current-task-v1:top50:min0.05:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| current-task-v1:top50:min0.10:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| current-task-v1:top50:min0.15:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| current-task-v1:top50:min0.20:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |
| current-task-v1:top50:min0.30:exclude-historical | 86.36% | 0.6794 | 3 | 5.26% | 0.00% | 10/10 | BlockedByRisk |

### Blocked MustHit Classification

| Classification | Count |
|---|---:|
| DeprecatedMustHitBlockedCorrectly | 9 |

| Sample | Intent | MustHit | Reasons | Lifecycle | Complete | Replacement | CanAllow | Classification | Repair |
|---|---|---|---|---|---|---|---:|---|---|
| automation-sample-002 | AutomationRecovery | memory:automation-noise-keyword | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| automation-sample-009 | AuditDeprecated | memory:automation-stopped-cron | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| chat-sample-009 | AuditDeprecated | memory:chat-deprecated-draft | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| coding-sample-002 | AuditDeprecated | doc:coding-noise-keyword | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| coding-sample-009 | AuditDeprecated | memory:coding-deprecated-logger | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| novel-sample-002 | AuditDeprecated | memory:novel-plot-old-draft | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| novel-sample-003 | AuditDeprecated | memory:novel-plot-old-draft | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| novel-sample-009 | AuditDeprecated | memory:novel-plot-deprecated-villain | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| project-sample-009 | AuditDeprecated | memory:project-deprecated-gateway | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |

## Extended Summary

- Samples: `113`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- BaselineRecallAfterPolicy: `84.38%`
- BaselineMRR: `0.8229`
- BaselineRiskAfterPolicy: `0`
- BelowTopKMissCount: `16`
- BlockedMustHitCount: `9`
- Recommendation: `ReadyForRetrievalShadow`
- BestSafeSweep: `normal-v1:top10:min0.05:stable-only`
- BestRecallAfterPolicy: `16.88%`
- BestRiskAfterPolicy: `0`
- RecoveredBelowTopK: `0/16`

| Configuration | RecallAfter | MRR | RiskAfter | MustNotRisk | LifecycleRisk | RecoveredBelowTopK | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---|
| audit-v1:top50:min0.05:exclude-historical | 94.37% | 0.8335 | 146 | 11.48% | 2.62% | 15/16 | BlockedByRisk |
| audit-v1:top50:min0.10:exclude-historical | 94.37% | 0.8335 | 146 | 11.48% | 2.62% | 15/16 | BlockedByRisk |
| audit-v1:top50:min0.15:exclude-historical | 94.37% | 0.8335 | 146 | 11.48% | 2.62% | 15/16 | BlockedByRisk |
| audit-v1:top50:min0.20:exclude-historical | 94.37% | 0.8335 | 146 | 11.48% | 2.62% | 15/16 | BlockedByRisk |
| audit-v1:top50:min0.30:exclude-historical | 94.37% | 0.8335 | 146 | 11.48% | 2.63% | 15/16 | BlockedByRisk |
| diagnostics-v1:top50:min0.05:exclude-historical | 94.37% | 0.8319 | 151 | 15.57% | 2.53% | 15/16 | BlockedByRisk |
| diagnostics-v1:top50:min0.10:exclude-historical | 94.37% | 0.8319 | 151 | 15.57% | 2.53% | 15/16 | BlockedByRisk |
| diagnostics-v1:top50:min0.15:exclude-historical | 94.37% | 0.8319 | 151 | 15.57% | 2.53% | 15/16 | BlockedByRisk |
| diagnostics-v1:top50:min0.20:exclude-historical | 94.37% | 0.8319 | 151 | 15.57% | 2.53% | 15/16 | BlockedByRisk |
| diagnostics-v1:top50:min0.30:exclude-historical | 94.37% | 0.8319 | 151 | 15.57% | 2.54% | 15/16 | BlockedByRisk |
| normal-v1:top50:min0.05:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| normal-v1:top50:min0.10:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| normal-v1:top50:min0.15:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| normal-v1:top50:min0.20:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| normal-v1:top50:min0.30:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| current-task-v1:top50:min0.05:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| current-task-v1:top50:min0.10:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| current-task-v1:top50:min0.15:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| current-task-v1:top50:min0.20:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |
| current-task-v1:top50:min0.30:exclude-historical | 93.75% | 0.8246 | 3 | 2.46% | 0.00% | 15/16 | BlockedByRisk |

### Blocked MustHit Classification

| Classification | Count |
|---|---:|
| DeprecatedMustHitBlockedCorrectly | 9 |

| Sample | Intent | MustHit | Reasons | Lifecycle | Complete | Replacement | CanAllow | Classification | Repair |
|---|---|---|---|---|---|---|---:|---|---|
| automation-sample-002 | AutomationRecovery | memory:automation-noise-keyword | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| automation-sample-009 | AuditDeprecated | memory:automation-stopped-cron | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| chat-sample-009 | AuditDeprecated | memory:chat-deprecated-draft | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| coding-sample-002 | AuditDeprecated | doc:coding-noise-keyword | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| coding-sample-009 | AuditDeprecated | memory:coding-deprecated-logger | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| novel-sample-002 | AuditDeprecated | memory:novel-plot-old-draft | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| novel-sample-003 | AuditDeprecated | memory:novel-plot-old-draft | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| novel-sample-009 | AuditDeprecated | memory:novel-plot-deprecated-villain | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |
| project-sample-009 | AuditDeprecated | memory:project-deprecated-gateway | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked | Deprecated | Incomplete | MissingReplacement | False | DeprecatedMustHitBlockedCorrectly | 保持 normal profile 阻断；仅在 audit path 中观察。 |

