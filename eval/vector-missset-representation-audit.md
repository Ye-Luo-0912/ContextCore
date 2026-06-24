# Vector Miss-set Representation Audit

Generated: 2026-06-10T14:34:11.0240907+00:00

## A3 Summary

- Samples: `50`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- MissedMustHitCount: `19`
- Recommendation: `NeedsProfileTuning`

| Diagnosis | Count |
|---|---:|
| QueryIntentMissing | 19 |

| Sample | Mode | Intent | MustHit | RawRank | RawSim | EligibleRank | MissReason | Diagnosis | Repair |
|---|---|---|---|---:|---:|---:|---|---|---|
| automation-sample-001 | AutomationMode | Unknown | memory:automation-recovery | 16 | 0.5977 | 15 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| automation-sample-002 | AutomationMode | Unknown | memory:automation-noise-keyword | 1 | 0.7904 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| automation-sample-003 | AutomationMode | Unknown | memory:automation-last-error | 27 | 0.5486 | 24 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| automation-sample-009 | AutomationMode | Unknown | memory:automation-stopped-cron | 1 | 0.7370 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| chat-sample-003 | ChatMode | Unknown | memory:chat-active-plan | 48 | 0.4223 | 38 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| chat-sample-005 | ChatMode | Unknown | memory:chat-active-plan | 45 | 0.4284 | 34 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| chat-sample-009 | ChatMode | Unknown | memory:chat-deprecated-draft | 2 | 0.7005 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 15 | 0.4773 | 9 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| coding-sample-002 | CodingMode | Unknown | doc:coding-noise-keyword | 1 | 0.8240 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| coding-sample-007 | CodingMode | Unknown | doc:ipromotioncandidatestore | 19 | 0.4952 | 12 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| coding-sample-009 | CodingMode | Unknown | memory:coding-deprecated-logger | 1 | 0.7314 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| coding-sample-010 | CodingMode | Unknown | memory:coding-next-todo | 36 | 0.5026 | 31 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| novel-sample-002 | NovelMode | Unknown | memory:novel-plot-old-draft | 1 | 0.7595 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| novel-sample-003 | NovelMode | Unknown | memory:novel-plot-old-draft | 3 | 0.6494 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| novel-sample-009 | NovelMode | Unknown | memory:novel-plot-deprecated-villain | 1 | 0.7192 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| project-sample-003 | ProjectMode | Unknown | doc:postgres-not-ready | 41 | 0.5073 | 25 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-005 | ProjectMode | Unknown | memory:project-extra-active-1 | 36 | 0.4615 | 29 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-007 | ProjectMode | Unknown | memory:project-current-step | 45 | 0.4249 | 33 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-009 | ProjectMode | Unknown | memory:project-deprecated-gateway | 2 | 0.6937 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |

## Extended Summary

- Samples: `113`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- MissedMustHitCount: `25`
- Recommendation: `NeedsProfileTuning`

| Diagnosis | Count |
|---|---:|
| QueryIntentMissing | 25 |

| Sample | Mode | Intent | MustHit | RawRank | RawSim | EligibleRank | MissReason | Diagnosis | Repair |
|---|---|---|---|---:|---:|---:|---|---|---|
| automation-sample-001 | AutomationMode | Unknown | memory:automation-recovery | 16 | 0.5977 | 15 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| automation-sample-002 | AutomationMode | Unknown | memory:automation-noise-keyword | 1 | 0.7904 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| automation-sample-003 | AutomationMode | Unknown | memory:automation-last-error | 27 | 0.5486 | 24 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| automation-sample-009 | AutomationMode | Unknown | memory:automation-stopped-cron | 1 | 0.7370 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| chat-20260529-002 | ChatMode | Unknown | task:current-user-request | 59 | 0.5374 | 52 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| chat-20260529-003 | ChatMode | Unknown | candidate:promotion-working | 14 | 0.6013 | 14 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| chat-sample-003 | ChatMode | Unknown | memory:chat-active-plan | 48 | 0.4223 | 38 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| chat-sample-005 | ChatMode | Unknown | memory:chat-active-plan | 45 | 0.4284 | 34 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| chat-sample-009 | ChatMode | Unknown | memory:chat-deprecated-draft | 2 | 0.7005 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| chat-sample-010 | ChatMode | Unknown | memory:chat-active-plan | 15 | 0.4773 | 9 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| coding-20260529-001 | CodingMode | Unknown | schema:context-eval-sample | 21 | 0.6046 | 19 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| coding-sample-002 | CodingMode | Unknown | doc:coding-noise-keyword | 1 | 0.8240 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| coding-sample-007 | CodingMode | Unknown | doc:ipromotioncandidatestore | 19 | 0.4952 | 12 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| coding-sample-009 | CodingMode | Unknown | memory:coding-deprecated-logger | 1 | 0.7314 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| coding-sample-010 | CodingMode | Unknown | memory:coding-next-todo | 36 | 0.5026 | 31 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| novel-20260529-001 | NovelMode | Unknown | plot:previous-chapter-hook | 28 | 0.6075 | 21 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| novel-20260529-001 | NovelMode | Unknown | memory:active-character-state | 45 | 0.5769 | 34 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| novel-sample-002 | NovelMode | Unknown | memory:novel-plot-old-draft | 1 | 0.7595 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| novel-sample-003 | NovelMode | Unknown | memory:novel-plot-old-draft | 3 | 0.6494 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| novel-sample-009 | NovelMode | Unknown | memory:novel-plot-deprecated-villain | 1 | 0.7192 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |
| project-20260529-001 | ProjectMode | Unknown | report:new-stage-execution | 41 | 0.5708 | 35 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-003 | ProjectMode | Unknown | doc:postgres-not-ready | 41 | 0.5073 | 25 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-005 | ProjectMode | Unknown | memory:project-extra-active-1 | 36 | 0.4615 | 29 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-007 | ProjectMode | Unknown | memory:project-current-step | 45 | 0.4249 | 33 | BelowTopK | QueryIntentMissing | 补齐通用 intent metadata 或使用 mode-intent-query-v1 做离线验证。 |
| project-sample-009 | ProjectMode | Unknown | memory:project-deprecated-gateway | 2 | 0.6937 | 0 | BlockedByEligibilityPolicy | QueryIntentMissing | 保持 eligibility safety gate，转入 metadata/profile audit，不放松 lifecycle gate。 |

