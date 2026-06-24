# Vector Recall Loss Audit Report

Generated: 2026-06-10T08:03:28.7275813+00:00

## A3 Summary

- Samples: `50`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Profile: `normal-v1`
- TopK: `10`
- MinSimilarity: `-`
- MustHitRecallAfterPolicy: `71.21%`
- MustHitMrrAfterPolicy: `0.6765`
- RiskAfterPolicy: `0`
- MissedMustHitCount: `19`
- Recommendation: `NeedsProfileTuning`

| Miss Reason | Count |
|---|---:|
| BelowTopK | 10 |
| BlockedByEligibilityPolicy | 9 |

## A3 Mode Readiness

- Recommendation: `KeepPreviewOnly`

| Key | Samples | RecallAfter | MRR | RiskAfter | NoCandidate | TopMissReasons | Recommendation |
|---|---:|---:|---:|---:|---:|---|---|
| AutomationMode | 10 | 71.43% | 0.6700 | 0 | 0 | BelowTopK:2<br>BlockedByEligibilityPolicy:2 | NeedsProfileTuning |
| ChatMode | 10 | 66.67% | 0.5958 | 0 | 0 | BelowTopK:3<br>BlockedByEligibilityPolicy:1 | NeedsProfileTuning |
| CodingMode | 10 | 69.23% | 0.6500 | 0 | 0 | BelowTopK:2<br>BlockedByEligibilityPolicy:2 | NeedsProfileTuning |
| NovelMode | 10 | 78.57% | 0.6667 | 0 | 0 | BlockedByEligibilityPolicy:3 | NeedsProfileTuning |
| ProjectMode | 10 | 69.23% | 0.8000 | 0 | 0 | BelowTopK:3<br>BlockedByEligibilityPolicy:1 | NeedsProfileTuning |


## A3 Intent Readiness

- Recommendation: `KeepPreviewOnly`

| Key | Samples | RecallAfter | MRR | RiskAfter | NoCandidate | TopMissReasons | Recommendation |
|---|---:|---:|---:|---:|---:|---|---|
| AuditDeprecated | 9 | 20.00% | 0.2222 | 0 | 0 | BlockedByEligibilityPolicy:8 | NeedsProfileTuning |
| AutomationRecovery | 8 | 75.00% | 0.8125 | 0 | 0 | BelowTopK:2<br>BlockedByEligibilityPolicy:1 | NeedsProfileTuning |
| CodingTask | 11 | 71.43% | 0.6091 | 0 | 0 | BelowTopK:4 | NeedsProfileTuning |
| FuzzyQuestion | 15 | 80.00% | 0.8639 | 0 | 0 | BelowTopK:4 | ReadyForRetrievalShadow |
| NovelGeneration | 7 | 100.00% | 0.8095 | 0 | 0 | - | ReadyForRetrievalShadow |

## A3 Missed MustHit Details

| Sample | Mode | Intent | MustHit | Indexed | RawRank | RawSim | EligibleRank | MissReason | BlockedReason |
|---|---|---|---|---:|---:|---:|---:|---|---|
| automation-sample-001 | AutomationMode | AutomationRecovery | memory:automation-recovery | yes | 16 | 0.5977 | 0 | BelowTopK | - |
| automation-sample-002 | AutomationMode | AutomationRecovery | memory:automation-noise-keyword | yes | 1 | 0.7904 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| automation-sample-003 | AutomationMode | AutomationRecovery | memory:automation-last-error | yes | 27 | 0.5486 | 0 | BelowTopK | - |
| automation-sample-009 | AutomationMode | AuditDeprecated | memory:automation-stopped-cron | yes | 1 | 0.7370 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| chat-sample-003 | ChatMode | FuzzyQuestion | memory:chat-active-plan | yes | 48 | 0.4223 | 0 | BelowTopK | - |
| chat-sample-005 | ChatMode | CodingTask | memory:chat-active-plan | yes | 45 | 0.4284 | 0 | BelowTopK | - |
| chat-sample-009 | ChatMode | AuditDeprecated | memory:chat-deprecated-draft | yes | 2 | 0.7005 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| chat-sample-010 | ChatMode | FuzzyQuestion | memory:chat-active-plan | yes | 15 | 0.4773 | 0 | BelowTopK | - |
| coding-sample-002 | CodingMode | AuditDeprecated | doc:coding-noise-keyword | yes | 1 | 0.8240 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| coding-sample-007 | CodingMode | CodingTask | doc:ipromotioncandidatestore | yes | 19 | 0.4952 | 0 | BelowTopK | - |
| coding-sample-009 | CodingMode | AuditDeprecated | memory:coding-deprecated-logger | yes | 1 | 0.7314 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| coding-sample-010 | CodingMode | CodingTask | memory:coding-next-todo | yes | 36 | 0.5026 | 0 | BelowTopK | - |
| novel-sample-002 | NovelMode | AuditDeprecated | memory:novel-plot-old-draft | yes | 1 | 0.7595 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| novel-sample-003 | NovelMode | AuditDeprecated | memory:novel-plot-old-draft | yes | 3 | 0.6494 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| novel-sample-009 | NovelMode | AuditDeprecated | memory:novel-plot-deprecated-villain | yes | 1 | 0.7192 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| project-sample-003 | ProjectMode | FuzzyQuestion | doc:postgres-not-ready | yes | 41 | 0.5073 | 0 | BelowTopK | - |
| project-sample-005 | ProjectMode | FuzzyQuestion | memory:project-extra-active-1 | yes | 36 | 0.4615 | 0 | BelowTopK | - |
| project-sample-007 | ProjectMode | CodingTask | memory:project-current-step | yes | 45 | 0.4249 | 0 | BelowTopK | - |
| project-sample-009 | ProjectMode | AuditDeprecated | memory:project-deprecated-gateway | yes | 2 | 0.6937 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |

## Extended Summary

- Samples: `113`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- Profile: `normal-v1`
- TopK: `10`
- MinSimilarity: `-`
- MustHitRecallAfterPolicy: `84.38%`
- MustHitMrrAfterPolicy: `0.8229`
- RiskAfterPolicy: `0`
- MissedMustHitCount: `25`
- Recommendation: `ReadyForRetrievalShadow`

| Miss Reason | Count |
|---|---:|
| BelowTopK | 16 |
| BlockedByEligibilityPolicy | 9 |

## Extended Mode Readiness

- Recommendation: `KeepPreviewOnly`

| Key | Samples | RecallAfter | MRR | RiskAfter | NoCandidate | TopMissReasons | Recommendation |
|---|---:|---:|---:|---:|---:|---|---|
| AutomationMode | 20 | 88.24% | 0.8100 | 0 | 0 | BelowTopK:2<br>BlockedByEligibilityPolicy:2 | ReadyForRetrievalShadow |
| ChatMode | 30 | 82.35% | 0.8042 | 0 | 0 | BelowTopK:5<br>BlockedByEligibilityPolicy:1 | ReadyForRetrievalShadow |
| CodingMode | 20 | 85.71% | 0.8250 | 0 | 0 | BelowTopK:3<br>BlockedByEligibilityPolicy:2 | ReadyForRetrievalShadow |
| NovelMode | 30 | 86.84% | 0.8389 | 0 | 0 | BlockedByEligibilityPolicy:3<br>BelowTopK:2 | ReadyForRetrievalShadow |
| ProjectMode | 13 | 73.68% | 0.8462 | 0 | 0 | BelowTopK:4<br>BlockedByEligibilityPolicy:1 | NeedsProfileTuning |


## Extended Intent Readiness

- Recommendation: `KeepPreviewOnly`

| Key | Samples | RecallAfter | MRR | RiskAfter | NoCandidate | TopMissReasons | Recommendation |
|---|---:|---:|---:|---:|---:|---|---|
| AuditDeprecated | 10 | 16.67% | 0.2000 | 0 | 0 | BlockedByEligibilityPolicy:8<br>BelowTopK:2 | NeedsProfileTuning |
| AutomationRecovery | 17 | 90.00% | 0.8824 | 0 | 0 | BelowTopK:2<br>BlockedByEligibilityPolicy:1 | ReadyForRetrievalShadow |
| CodingTask | 24 | 87.50% | 0.8208 | 0 | 0 | BelowTopK:5 | ReadyForRetrievalShadow |
| ConflictCheck | 2 | 100.00% | 1.0000 | 0 | 0 | - | ReadyForRetrievalShadow |
| CurrentTask | 6 | 88.89% | 0.8611 | 0 | 0 | BelowTopK:1 | ReadyForRetrievalShadow |
| FuzzyQuestion | 31 | 84.21% | 0.9019 | 0 | 0 | BelowTopK:6 | ReadyForRetrievalShadow |
| LongTermPreference | 1 | 100.00% | 1.0000 | 0 | 0 | - | ReadyForRetrievalShadow |
| NovelGeneration | 22 | 100.00% | 0.9167 | 0 | 0 | - | ReadyForRetrievalShadow |

## Extended Missed MustHit Details

| Sample | Mode | Intent | MustHit | Indexed | RawRank | RawSim | EligibleRank | MissReason | BlockedReason |
|---|---|---|---|---:|---:|---:|---:|---|---|
| automation-sample-001 | AutomationMode | AutomationRecovery | memory:automation-recovery | yes | 16 | 0.5977 | 0 | BelowTopK | - |
| automation-sample-002 | AutomationMode | AutomationRecovery | memory:automation-noise-keyword | yes | 1 | 0.7904 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| automation-sample-003 | AutomationMode | AutomationRecovery | memory:automation-last-error | yes | 27 | 0.5486 | 0 | BelowTopK | - |
| automation-sample-009 | AutomationMode | AuditDeprecated | memory:automation-stopped-cron | yes | 1 | 0.7370 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| chat-20260529-002 | ChatMode | FuzzyQuestion | task:current-user-request | yes | 59 | 0.5374 | 0 | BelowTopK | - |
| chat-20260529-003 | ChatMode | CurrentTask | candidate:promotion-working | yes | 14 | 0.6013 | 0 | BelowTopK | - |
| chat-sample-003 | ChatMode | FuzzyQuestion | memory:chat-active-plan | yes | 48 | 0.4223 | 0 | BelowTopK | - |
| chat-sample-005 | ChatMode | CodingTask | memory:chat-active-plan | yes | 45 | 0.4284 | 0 | BelowTopK | - |
| chat-sample-009 | ChatMode | AuditDeprecated | memory:chat-deprecated-draft | yes | 2 | 0.7005 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| chat-sample-010 | ChatMode | FuzzyQuestion | memory:chat-active-plan | yes | 15 | 0.4773 | 0 | BelowTopK | - |
| coding-20260529-001 | CodingMode | CodingTask | schema:context-eval-sample | yes | 21 | 0.6046 | 0 | BelowTopK | - |
| coding-sample-002 | CodingMode | AuditDeprecated | doc:coding-noise-keyword | yes | 1 | 0.8240 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| coding-sample-007 | CodingMode | CodingTask | doc:ipromotioncandidatestore | yes | 19 | 0.4952 | 0 | BelowTopK | - |
| coding-sample-009 | CodingMode | AuditDeprecated | memory:coding-deprecated-logger | yes | 1 | 0.7314 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| coding-sample-010 | CodingMode | CodingTask | memory:coding-next-todo | yes | 36 | 0.5026 | 0 | BelowTopK | - |
| novel-20260529-001 | NovelMode | AuditDeprecated | plot:previous-chapter-hook | yes | 28 | 0.6075 | 0 | BelowTopK | - |
| novel-20260529-001 | NovelMode | AuditDeprecated | memory:active-character-state | yes | 45 | 0.5769 | 0 | BelowTopK | - |
| novel-sample-002 | NovelMode | AuditDeprecated | memory:novel-plot-old-draft | yes | 1 | 0.7595 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| novel-sample-003 | NovelMode | AuditDeprecated | memory:novel-plot-old-draft | yes | 3 | 0.6494 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| novel-sample-009 | NovelMode | AuditDeprecated | memory:novel-plot-deprecated-villain | yes | 1 | 0.7192 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |
| project-20260529-001 | ProjectMode | FuzzyQuestion | report:new-stage-execution | yes | 41 | 0.5708 | 0 | BelowTopK | - |
| project-sample-003 | ProjectMode | FuzzyQuestion | doc:postgres-not-ready | yes | 41 | 0.5073 | 0 | BelowTopK | - |
| project-sample-005 | ProjectMode | FuzzyQuestion | memory:project-extra-active-1 | yes | 36 | 0.4615 | 0 | BelowTopK | - |
| project-sample-007 | ProjectMode | CodingTask | memory:project-current-step | yes | 45 | 0.4249 | 0 | BelowTopK | - |
| project-sample-009 | ProjectMode | AuditDeprecated | memory:project-deprecated-gateway | yes | 2 | 0.6937 | 0 | BlockedByEligibilityPolicy | LifecycleMetadataIncompleteBlocked,ReplacementMetadataMissingBlocked,HistoricalSourceRequiresAuditProfile,DeprecatedCandidateBlocked |

