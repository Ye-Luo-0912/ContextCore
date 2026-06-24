# Vector Query Shadow Eval Report

Generated: 2026-06-14T18:08:42.1640944+00:00

## A3 Summary

- Samples: `50`
- IndexedCoverage: `100.00%`
- RawCandidateCount: `500`
- EligibleCandidateCount: `337`
- BlockedCandidateCount: `163`
- RiskBeforePolicy: `164`
- RiskAfterPolicy: `1`
- MustHitRecallBeforePolicy: `87.88%`
- MustHitRecallAfterPolicy: `54.55%`
- MustNotHitRiskBeforePolicy: `50.88%`
- MustNotHitRiskAfterPolicy: `1.75%`
- LifecycleRiskBeforePolicy: `19.80%`
- LifecycleRiskAfterPolicy: `0.00%`
- DeprecatedHitCount: `99`
- DuplicateHitCount: `0`
- AverageTopSimilarity: `0.8332`
- NoCandidateCount: `0`
- LowConfidenceCount: `0`
- Recommendation: `BlockedByRisk`

| Noise Cluster | Count |
|---|---:|
| DeprecatedHit | 99 |
| LifecycleRiskBeforePolicy | 99 |
| MustNotHitBeforePolicy | 29 |
| MustNotHitAfterPolicy | 1 |

| Blocked Reason | Count |
|---|---:|
| DeprecatedCandidateBlocked | 99 |
| HistoricalSourceRequiresAuditProfile | 99 |
| LifecycleMetadataIncompleteBlocked | 93 |
| ReplacementMetadataMissingBlocked | 62 |
| HistoricalCandidateBlocked | 37 |
| DiagnosticsOnlyItemKindBlocked | 33 |
| UnknownLifecycleBlocked | 31 |

## A3 Sample Details

| Sample | Mode | Raw | Eligible | Blocked | MustHit Before/After | Risk Before/After | TopSimilarity | Recommendation |
|---|---|---:|---:|---:|---|---|---:|---|
| automation-sample-001 | AutomationMode | 10 | 8 | 2 | doc:automation-guide/- | 2/0 | 0.8481 | NeedsPolicyTuning |
| automation-sample-002 | AutomationMode | 10 | 7 | 3 | memory:automation-noise-keyword/- | 4/1 | 0.8820 | BlockedByRisk |
| automation-sample-003 | AutomationMode | 10 | 8 | 2 | doc:automation-guide/- | 2/0 | 0.8290 | NeedsPolicyTuning |
| automation-sample-004 | AutomationMode | 10 | 8 | 2 | memory:automation-conflict-v2/memory:automation-conflict-v2 | 2/0 | 0.8805 | KeepPreviewOnly |
| automation-sample-005 | AutomationMode | 10 | 6 | 4 | memory:automation-extra-active-1/memory:automation-extra-active-1 | 4/0 | 0.9223 | KeepPreviewOnly |
| automation-sample-006 | AutomationMode | 10 | 9 | 1 | memory:automation-extra-active-2/memory:automation-extra-active-2 | 1/0 | 0.8749 | KeepPreviewOnly |
| automation-sample-007 | AutomationMode | 10 | 8 | 2 | -/- | 2/0 | 0.7977 | NeedsPolicyTuning |
| automation-sample-008 | AutomationMode | 10 | 7 | 3 | memory:automation-backup-strategy-v2/memory:automation-backup-strategy-v2 | 3/0 | 0.8650 | KeepPreviewOnly |
| automation-sample-009 | AutomationMode | 10 | 6 | 4 | memory:automation-stopped-cron/- | 4/0 | 0.8085 | NeedsPolicyTuning |
| automation-sample-010 | AutomationMode | 10 | 7 | 3 | doc:automation-guide<br>memory:automation-disk-error/memory:automation-disk-error | 3/0 | 0.9230 | KeepPreviewOnly |
| chat-sample-001 | ChatMode | 10 | 9 | 1 | stable:preference-language/stable:preference-language | 1/0 | 0.7511 | KeepPreviewOnly |
| chat-sample-002 | ChatMode | 10 | 9 | 1 | memory:chat-active-plan/memory:chat-active-plan | 1/0 | 0.7646 | KeepPreviewOnly |
| chat-sample-003 | ChatMode | 10 | 7 | 3 | memory:chat-active-plan/memory:chat-active-plan | 3/0 | 0.7429 | KeepPreviewOnly |
| chat-sample-004 | ChatMode | 10 | 8 | 2 | memory:chat-version-conflict-v2/memory:chat-version-conflict-v2 | 2/0 | 0.8245 | KeepPreviewOnly |
| chat-sample-005 | ChatMode | 10 | 7 | 3 | memory:chat-active-plan/memory:chat-active-plan | 3/0 | 0.7705 | KeepPreviewOnly |
| chat-sample-006 | ChatMode | 10 | 8 | 2 | memory:chat-extra-active-1/memory:chat-extra-active-1 | 2/0 | 0.8130 | KeepPreviewOnly |
| chat-sample-007 | ChatMode | 10 | 8 | 2 | memory:chat-extra-active-2/memory:chat-extra-active-2 | 2/0 | 0.8307 | KeepPreviewOnly |
| chat-sample-008 | ChatMode | 10 | 7 | 3 | memory:chat-drink-preference-v2/memory:chat-drink-preference-v2 | 3/0 | 0.8562 | KeepPreviewOnly |
| chat-sample-009 | ChatMode | 10 | 5 | 5 | memory:chat-deprecated-draft/- | 5/0 | 0.7327 | NeedsPolicyTuning |
| chat-sample-010 | ChatMode | 10 | 7 | 3 | memory:chat-active-plan<br>memory:chat-delivery-date/memory:chat-active-plan<br>memory:chat-delivery-date | 3/0 | 0.7628 | KeepPreviewOnly |
| coding-sample-001 | CodingMode | 10 | 7 | 3 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/memory:coding-next-todo | 3/0 | 0.8886 | KeepPreviewOnly |
| coding-sample-002 | CodingMode | 10 | 4 | 6 | doc:coding-noise-keyword/- | 6/0 | 0.9175 | NeedsPolicyTuning |
| coding-sample-003 | CodingMode | 10 | 7 | 3 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/memory:coding-next-todo | 3/0 | 0.8246 | KeepPreviewOnly |
| coding-sample-004 | CodingMode | 10 | 5 | 5 | memory:coding-conflict-v2/memory:coding-conflict-v2 | 5/0 | 0.8827 | KeepPreviewOnly |
| coding-sample-005 | CodingMode | 10 | 9 | 1 | memory:coding-extra-active-1/memory:coding-extra-active-1 | 1/0 | 0.8967 | KeepPreviewOnly |
| coding-sample-006 | CodingMode | 10 | 9 | 1 | memory:coding-extra-active-2/memory:coding-extra-active-2 | 1/0 | 0.8933 | KeepPreviewOnly |
| coding-sample-007 | CodingMode | 10 | 7 | 3 | -/- | 3/0 | 0.8113 | NeedsPolicyTuning |
| coding-sample-008 | CodingMode | 10 | 7 | 3 | memory:coding-timeout-v2/memory:coding-timeout-v2 | 3/0 | 0.8792 | KeepPreviewOnly |
| coding-sample-009 | CodingMode | 10 | 3 | 7 | memory:coding-deprecated-logger/- | 7/0 | 0.7870 | NeedsPolicyTuning |
| coding-sample-010 | CodingMode | 10 | 10 | 0 | memory:coding-test-naming/memory:coding-test-naming | 0/0 | 0.8193 | KeepPreviewOnly |
| novel-sample-001 | NovelMode | 10 | 4 | 6 | novel:world-cangqiong<br>novel:character-linfeng<br>memory:novel-current-plot/memory:novel-current-plot | 6/0 | 0.9307 | KeepPreviewOnly |
| novel-sample-002 | NovelMode | 10 | 7 | 3 | memory:novel-plot-old-draft/- | 3/0 | 0.8107 | NeedsPolicyTuning |
| novel-sample-003 | NovelMode | 10 | 4 | 6 | memory:novel-plot-old-draft<br>memory:novel-current-plot/memory:novel-current-plot | 6/0 | 0.8645 | KeepPreviewOnly |
| novel-sample-004 | NovelMode | 10 | 4 | 6 | memory:novel-conflict-v2/memory:novel-conflict-v2 | 6/0 | 0.8880 | KeepPreviewOnly |
| novel-sample-005 | NovelMode | 10 | 6 | 4 | memory:novel-extra-active-1/memory:novel-extra-active-1 | 4/0 | 0.8331 | KeepPreviewOnly |
| novel-sample-006 | NovelMode | 10 | 6 | 4 | memory:novel-extra-active-2/memory:novel-extra-active-2 | 4/0 | 0.7111 | KeepPreviewOnly |
| novel-sample-007 | NovelMode | 10 | 7 | 3 | -/- | 3/0 | 0.7805 | NeedsPolicyTuning |
| novel-sample-008 | NovelMode | 10 | 5 | 5 | memory:novel-weapon-v2/memory:novel-weapon-v2 | 5/0 | 0.9006 | KeepPreviewOnly |
| novel-sample-009 | NovelMode | 10 | 4 | 6 | memory:novel-plot-deprecated-villain/- | 6/0 | 0.7867 | NeedsPolicyTuning |
| novel-sample-010 | NovelMode | 10 | 6 | 4 | novel:character-linfeng<br>memory:novel-family-background/memory:novel-family-background | 4/0 | 0.7592 | KeepPreviewOnly |
| project-sample-001 | ProjectMode | 10 | 5 | 5 | doc:local-alpha-runbook/- | 5/0 | 0.8579 | NeedsPolicyTuning |
| project-sample-002 | ProjectMode | 10 | 5 | 5 | doc:postgres-not-ready/- | 5/0 | 0.8360 | NeedsPolicyTuning |
| project-sample-003 | ProjectMode | 10 | 4 | 6 | doc:local-alpha-runbook<br>doc:postgres-not-ready/- | 6/0 | 0.8753 | NeedsPolicyTuning |
| project-sample-004 | ProjectMode | 10 | 7 | 3 | memory:project-conflict-v2/memory:project-conflict-v2 | 3/0 | 0.8096 | KeepPreviewOnly |
| project-sample-005 | ProjectMode | 10 | 8 | 2 | memory:project-current-step<br>memory:project-extra-active-1/memory:project-current-step<br>memory:project-extra-active-1 | 2/0 | 0.8368 | KeepPreviewOnly |
| project-sample-006 | ProjectMode | 10 | 9 | 1 | memory:project-extra-active-2/memory:project-extra-active-2 | 1/0 | 0.8249 | KeepPreviewOnly |
| project-sample-007 | ProjectMode | 10 | 8 | 2 | memory:project-current-step/memory:project-current-step | 2/0 | 0.7781 | KeepPreviewOnly |
| project-sample-008 | ProjectMode | 10 | 8 | 2 | memory:project-pool-v2/memory:project-pool-v2 | 2/0 | 0.9021 | KeepPreviewOnly |
| project-sample-009 | ProjectMode | 10 | 4 | 6 | memory:project-deprecated-gateway/- | 6/0 | 0.7811 | NeedsPolicyTuning |
| project-sample-010 | ProjectMode | 10 | 9 | 1 | doc:local-alpha-runbook<br>memory:project-required-env/memory:project-required-env | 1/0 | 0.8134 | KeepPreviewOnly |

## Extended Summary

- Samples: `113`
- IndexedCoverage: `100.00%`
- RawCandidateCount: `1130`
- EligibleCandidateCount: `880`
- BlockedCandidateCount: `250`
- RiskBeforePolicy: `251`
- RiskAfterPolicy: `1`
- MustHitRecallBeforePolicy: `90.62%`
- MustHitRecallAfterPolicy: `76.88%`
- MustNotHitRiskBeforePolicy: `23.77%`
- MustNotHitRiskAfterPolicy: `0.82%`
- LifecycleRiskBeforePolicy: `12.83%`
- LifecycleRiskAfterPolicy: `0.00%`
- DeprecatedHitCount: `145`
- DuplicateHitCount: `0`
- AverageTopSimilarity: `0.8521`
- NoCandidateCount: `0`
- LowConfidenceCount: `0`
- Recommendation: `BlockedByRisk`

| Noise Cluster | Count |
|---|---:|
| DeprecatedHit | 145 |
| LifecycleRiskBeforePolicy | 145 |
| MustNotHitBeforePolicy | 29 |
| MustNotHitAfterPolicy | 1 |

| Blocked Reason | Count |
|---|---:|
| DeprecatedCandidateBlocked | 145 |
| HistoricalSourceRequiresAuditProfile | 145 |
| LifecycleMetadataIncompleteBlocked | 145 |
| ReplacementMetadataMissingBlocked | 102 |
| DiagnosticsOnlyItemKindBlocked | 62 |
| HistoricalCandidateBlocked | 43 |
| UnknownLifecycleBlocked | 43 |

## Extended Sample Details

| Sample | Mode | Raw | Eligible | Blocked | MustHit Before/After | Risk Before/After | TopSimilarity | Recommendation |
|---|---|---:|---:|---:|---|---|---:|---|
| automation-20260529-001 | AutomationMode | 10 | 8 | 2 | job:last-failed<br>run:blocker-current/job:last-failed<br>run:blocker-current | 2/0 | 0.9233 | KeepPreviewOnly |
| automation-20260529-002 | AutomationMode | 10 | 10 | 0 | queue:dead-letter<br>job:failed-requeue-candidate/queue:dead-letter<br>job:failed-requeue-candidate | 0/0 | 0.8649 | KeepPreviewOnly |
| automation-20260529-003 | AutomationMode | 10 | 7 | 3 | worker:stats-latest/worker:stats-latest | 3/0 | 0.9119 | KeepPreviewOnly |
| automation-20260529-004 | AutomationMode | 10 | 8 | 2 | run:latest-recovery-point/run:latest-recovery-point | 2/0 | 0.8785 | KeepPreviewOnly |
| automation-20260529-005 | AutomationMode | 10 | 10 | 0 | confirmation:required-before-delete<br>safety:destructive-operation/confirmation:required-before-delete<br>safety:destructive-operation | 0/0 | 0.8499 | KeepPreviewOnly |
| automation-20260529-006 | AutomationMode | 10 | 9 | 1 | tool:last-timeout<br>log:last-error/tool:last-timeout<br>log:last-error | 1/0 | 0.9505 | KeepPreviewOnly |
| automation-20260529-007 | AutomationMode | 10 | 10 | 0 | worker:concurrency<br>job:dedupe-token/worker:concurrency<br>job:dedupe-token | 0/0 | 0.9241 | KeepPreviewOnly |
| automation-20260529-008 | AutomationMode | 10 | 9 | 1 | job:max-retry-exceeded<br>queue:dead-letter/job:max-retry-exceeded<br>queue:dead-letter | 1/0 | 0.9288 | KeepPreviewOnly |
| automation-20260529-009 | AutomationMode | 10 | 10 | 0 | run:environment-snapshot<br>run:resume-precheck/run:environment-snapshot<br>run:resume-precheck | 0/0 | 0.9165 | KeepPreviewOnly |
| automation-20260529-010 | AutomationMode | 10 | 8 | 2 | run:current-failed-steps/run:current-failed-steps | 2/0 | 0.8717 | KeepPreviewOnly |
| automation-sample-001 | AutomationMode | 10 | 8 | 2 | doc:automation-guide/- | 2/0 | 0.8481 | NeedsPolicyTuning |
| automation-sample-002 | AutomationMode | 10 | 7 | 3 | memory:automation-noise-keyword/- | 4/1 | 0.8820 | BlockedByRisk |
| automation-sample-003 | AutomationMode | 10 | 8 | 2 | doc:automation-guide/- | 2/0 | 0.8290 | NeedsPolicyTuning |
| automation-sample-004 | AutomationMode | 10 | 8 | 2 | memory:automation-conflict-v2/memory:automation-conflict-v2 | 2/0 | 0.8805 | KeepPreviewOnly |
| automation-sample-005 | AutomationMode | 10 | 6 | 4 | memory:automation-extra-active-1/memory:automation-extra-active-1 | 4/0 | 0.9223 | KeepPreviewOnly |
| automation-sample-006 | AutomationMode | 10 | 9 | 1 | memory:automation-extra-active-2/memory:automation-extra-active-2 | 1/0 | 0.8749 | KeepPreviewOnly |
| automation-sample-007 | AutomationMode | 10 | 8 | 2 | -/- | 2/0 | 0.7977 | NeedsPolicyTuning |
| automation-sample-008 | AutomationMode | 10 | 7 | 3 | memory:automation-backup-strategy-v2/memory:automation-backup-strategy-v2 | 3/0 | 0.8650 | KeepPreviewOnly |
| automation-sample-009 | AutomationMode | 10 | 6 | 4 | memory:automation-stopped-cron/- | 4/0 | 0.8085 | NeedsPolicyTuning |
| automation-sample-010 | AutomationMode | 10 | 7 | 3 | doc:automation-guide<br>memory:automation-disk-error/memory:automation-disk-error | 3/0 | 0.9230 | KeepPreviewOnly |
| chat-20260529-001 | ChatMode | 10 | 9 | 1 | pref:zh-output/pref:zh-output | 1/0 | 0.8309 | KeepPreviewOnly |
| chat-20260529-002 | ChatMode | 10 | 8 | 2 | -/- | 2/0 | 0.7667 | NeedsPolicyTuning |
| chat-20260529-003 | ChatMode | 10 | 8 | 2 | memory:session-conclusion<br>candidate:promotion-working/memory:session-conclusion<br>candidate:promotion-working | 2/0 | 0.7770 | KeepPreviewOnly |
| chat-20260529-004 | ChatMode | 10 | 10 | 0 | pref:answer-conclusion-first/pref:answer-conclusion-first | 0/0 | 0.9004 | KeepPreviewOnly |
| chat-20260529-005 | ChatMode | 10 | 7 | 3 | policy:no-promote-temporary-emotion/policy:no-promote-temporary-emotion | 3/0 | 0.7093 | KeepPreviewOnly |
| chat-20260529-006 | ChatMode | 10 | 9 | 1 | task:current-active/task:current-active | 1/0 | 0.8926 | KeepPreviewOnly |
| chat-20260529-007 | ChatMode | 10 | 9 | 1 | pattern:repeated-user-preference/pattern:repeated-user-preference | 1/0 | 0.8821 | KeepPreviewOnly |
| chat-20260529-008 | ChatMode | 10 | 9 | 1 | uncertainty:pending-confirmation/uncertainty:pending-confirmation | 1/0 | 0.8426 | KeepPreviewOnly |
| chat-20260529-009 | ChatMode | 10 | 9 | 1 | policy:no-promote-joke/policy:no-promote-joke | 1/0 | 0.7911 | KeepPreviewOnly |
| chat-20260529-010 | ChatMode | 10 | 9 | 1 | constraint:no-secret-commit/constraint:no-secret-commit | 1/0 | 0.9185 | KeepPreviewOnly |
| chat-20260529-011 | ChatMode | 10 | 9 | 1 | scope:current-session/scope:current-session | 1/0 | 0.8452 | KeepPreviewOnly |
| chat-20260529-012 | ChatMode | 10 | 9 | 1 | rule:naming-confirmed/rule:naming-confirmed | 1/0 | 0.8078 | KeepPreviewOnly |
| chat-20260529-013 | ChatMode | 10 | 8 | 2 | revision:latest-expression/revision:latest-expression | 2/0 | 0.8383 | KeepPreviewOnly |
| chat-20260529-014 | ChatMode | 10 | 9 | 1 | pref:interaction-style/pref:interaction-style | 1/0 | 0.8506 | KeepPreviewOnly |
| chat-20260529-015 | ChatMode | 10 | 8 | 2 | policy:no-promote-oneoff/policy:no-promote-oneoff | 2/0 | 0.8055 | KeepPreviewOnly |
| chat-20260529-016 | ChatMode | 10 | 10 | 0 | pref:actionable-concise/pref:actionable-concise | 0/0 | 0.8454 | KeepPreviewOnly |
| chat-20260529-017 | ChatMode | 10 | 10 | 0 | pref:admit-uncertainty/pref:admit-uncertainty | 0/0 | 0.8199 | KeepPreviewOnly |
| chat-20260529-018 | ChatMode | 10 | 9 | 1 | source:reference-only/source:reference-only | 1/0 | 0.8448 | KeepPreviewOnly |
| chat-20260529-019 | ChatMode | 10 | 10 | 0 | task:resume-context/task:resume-context | 0/0 | 0.9177 | KeepPreviewOnly |
| chat-20260529-020 | ChatMode | 10 | 8 | 2 | scope:project-only/scope:project-only | 2/0 | 0.9300 | KeepPreviewOnly |
| chat-sample-001 | ChatMode | 10 | 9 | 1 | stable:preference-language/stable:preference-language | 1/0 | 0.7511 | KeepPreviewOnly |
| chat-sample-002 | ChatMode | 10 | 9 | 1 | memory:chat-active-plan/memory:chat-active-plan | 1/0 | 0.7646 | KeepPreviewOnly |
| chat-sample-003 | ChatMode | 10 | 7 | 3 | memory:chat-active-plan/memory:chat-active-plan | 3/0 | 0.7429 | KeepPreviewOnly |
| chat-sample-004 | ChatMode | 10 | 8 | 2 | memory:chat-version-conflict-v2/memory:chat-version-conflict-v2 | 2/0 | 0.8245 | KeepPreviewOnly |
| chat-sample-005 | ChatMode | 10 | 7 | 3 | memory:chat-active-plan/memory:chat-active-plan | 3/0 | 0.7705 | KeepPreviewOnly |
| chat-sample-006 | ChatMode | 10 | 8 | 2 | memory:chat-extra-active-1/memory:chat-extra-active-1 | 2/0 | 0.8130 | KeepPreviewOnly |
| chat-sample-007 | ChatMode | 10 | 8 | 2 | memory:chat-extra-active-2/memory:chat-extra-active-2 | 2/0 | 0.8307 | KeepPreviewOnly |
| chat-sample-008 | ChatMode | 10 | 7 | 3 | memory:chat-drink-preference-v2/memory:chat-drink-preference-v2 | 3/0 | 0.8562 | KeepPreviewOnly |
| chat-sample-009 | ChatMode | 10 | 5 | 5 | memory:chat-deprecated-draft/- | 5/0 | 0.7327 | NeedsPolicyTuning |
| chat-sample-010 | ChatMode | 10 | 7 | 3 | memory:chat-active-plan<br>memory:chat-delivery-date/memory:chat-active-plan<br>memory:chat-delivery-date | 3/0 | 0.7628 | KeepPreviewOnly |
| coding-20260529-001 | CodingMode | 10 | 9 | 1 | todo:A3/todo:A3 | 1/0 | 0.8965 | KeepPreviewOnly |
| coding-20260529-002 | CodingMode | 10 | 10 | 0 | test:last-failure<br>assertion:expected-output/test:last-failure<br>assertion:expected-output | 0/0 | 0.9209 | KeepPreviewOnly |
| coding-20260529-003 | CodingMode | 10 | 10 | 0 | verification:build<br>verification:test<br>verification:secret-scan/verification:build<br>verification:test<br>verification:secret-scan | 0/0 | 0.8612 | KeepPreviewOnly |
| coding-20260529-004 | CodingMode | 10 | 10 | 0 | build:last-error-file<br>task:fix-build-failure/build:last-error-file<br>task:fix-build-failure | 0/0 | 0.9245 | KeepPreviewOnly |
| coding-20260529-005 | CodingMode | 10 | 9 | 1 | api:public-contract<br>compatibility:backward-check/api:public-contract<br>compatibility:backward-check | 1/0 | 0.9483 | KeepPreviewOnly |
| coding-20260529-006 | CodingMode | 10 | 9 | 1 | comment:core-logic-chinese<br>comment:avoid-noise/comment:core-logic-chinese<br>comment:avoid-noise | 1/0 | 0.9288 | KeepPreviewOnly |
| coding-20260529-007 | CodingMode | 10 | 6 | 4 | performance:avoid-repeated-io<br>performance:avoid-full-deserialize/performance:avoid-repeated-io<br>performance:avoid-full-deserialize | 4/0 | 0.8599 | KeepPreviewOnly |
| coding-20260529-008 | CodingMode | 10 | 8 | 2 | test:risk-focused<br>test:avoid-brittle-snapshot/test:risk-focused<br>test:avoid-brittle-snapshot | 2/0 | 0.8913 | KeepPreviewOnly |
| coding-20260529-009 | CodingMode | 10 | 9 | 1 | security:private-config-dir<br>security:no-secret-in-repo/security:private-config-dir<br>security:no-secret-in-repo | 1/0 | 0.9624 | KeepPreviewOnly |
| coding-20260529-010 | CodingMode | 10 | 10 | 0 | verification:build<br>verification:test<br>verification:secret-scan/verification:build<br>verification:test<br>verification:secret-scan | 0/0 | 0.8365 | KeepPreviewOnly |
| coding-sample-001 | CodingMode | 10 | 7 | 3 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/memory:coding-next-todo | 3/0 | 0.8886 | KeepPreviewOnly |
| coding-sample-002 | CodingMode | 10 | 4 | 6 | doc:coding-noise-keyword/- | 6/0 | 0.9175 | NeedsPolicyTuning |
| coding-sample-003 | CodingMode | 10 | 7 | 3 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/memory:coding-next-todo | 3/0 | 0.8246 | KeepPreviewOnly |
| coding-sample-004 | CodingMode | 10 | 5 | 5 | memory:coding-conflict-v2/memory:coding-conflict-v2 | 5/0 | 0.8827 | KeepPreviewOnly |
| coding-sample-005 | CodingMode | 10 | 9 | 1 | memory:coding-extra-active-1/memory:coding-extra-active-1 | 1/0 | 0.8967 | KeepPreviewOnly |
| coding-sample-006 | CodingMode | 10 | 9 | 1 | memory:coding-extra-active-2/memory:coding-extra-active-2 | 1/0 | 0.8933 | KeepPreviewOnly |
| coding-sample-007 | CodingMode | 10 | 7 | 3 | -/- | 3/0 | 0.8113 | NeedsPolicyTuning |
| coding-sample-008 | CodingMode | 10 | 7 | 3 | memory:coding-timeout-v2/memory:coding-timeout-v2 | 3/0 | 0.8792 | KeepPreviewOnly |
| coding-sample-009 | CodingMode | 10 | 3 | 7 | memory:coding-deprecated-logger/- | 7/0 | 0.7870 | NeedsPolicyTuning |
| coding-sample-010 | CodingMode | 10 | 10 | 0 | memory:coding-test-naming/memory:coding-test-naming | 0/0 | 0.8193 | KeepPreviewOnly |
| novel-20260529-001 | NovelMode | 10 | 8 | 2 | plot:previous-chapter-hook/plot:previous-chapter-hook | 2/0 | 0.8347 | KeepPreviewOnly |
| novel-20260529-002 | NovelMode | 10 | 9 | 1 | relation:protagonist-mentor-current<br>scene:last-conflict/relation:protagonist-mentor-current<br>scene:last-conflict | 1/0 | 0.8357 | KeepPreviewOnly |
| novel-20260529-003 | NovelMode | 10 | 8 | 2 | constraint:magic-cost<br>world:rules-current/constraint:magic-cost<br>world:rules-current | 2/0 | 0.8477 | KeepPreviewOnly |
| novel-20260529-004 | NovelMode | 10 | 8 | 2 | state:protagonist-injured/state:protagonist-injured | 2/0 | 0.8978 | KeepPreviewOnly |
| novel-20260529-005 | NovelMode | 10 | 10 | 0 | motive:villain-true/motive:villain-true | 0/0 | 0.7798 | KeepPreviewOnly |
| novel-20260529-006 | NovelMode | 10 | 8 | 2 | relation:post-argument-distance/relation:post-argument-distance | 2/0 | 0.8958 | KeepPreviewOnly |
| novel-20260529-007 | NovelMode | 10 | 10 | 0 | world:city-lockdown-active/world:city-lockdown-active | 0/0 | 0.8413 | KeepPreviewOnly |
| novel-20260529-008 | NovelMode | 10 | 10 | 0 | knowledge:mentor-letter-exists/knowledge:mentor-letter-exists | 0/0 | 0.8356 | KeepPreviewOnly |
| novel-20260529-009 | NovelMode | 10 | 6 | 4 | foreshadow:bell-sound/foreshadow:bell-sound | 4/0 | 0.8368 | KeepPreviewOnly |
| novel-20260529-010 | NovelMode | 10 | 6 | 4 | subplot:active-trade-route/subplot:active-trade-route | 4/0 | 0.8726 | KeepPreviewOnly |

