# Vector Query Shadow Eval Report

Generated: 2026-06-10T09:07:34.0241595+00:00

## A3 Summary

- Samples: `50`
- IndexedCoverage: `100.00%`
- RawCandidateCount: `500`
- EligibleCandidateCount: `326`
- BlockedCandidateCount: `174`
- RiskBeforePolicy: `174`
- RiskAfterPolicy: `0`
- MustHitRecallBeforePolicy: `84.85%`
- MustHitRecallAfterPolicy: `71.21%`
- MustNotHitRiskBeforePolicy: `54.39%`
- MustNotHitRiskAfterPolicy: `0.00%`
- LifecycleRiskBeforePolicy: `29.00%`
- LifecycleRiskAfterPolicy: `0.00%`
- DeprecatedHitCount: `145`
- DuplicateHitCount: `0`
- AverageTopSimilarity: `0.7282`
- NoCandidateCount: `0`
- LowConfidenceCount: `0`
- Recommendation: `KeepPreviewOnly`

| Noise Cluster | Count |
|---|---:|
| DeprecatedHit | 145 |
| LifecycleRiskBeforePolicy | 145 |
| MustNotHitBeforePolicy | 31 |
| NoEligibleCandidate | 1 |

| Blocked Reason | Count |
|---|---:|
| HistoricalSourceRequiresAuditProfile | 145 |
| DeprecatedCandidateBlocked | 136 |
| LifecycleMetadataIncompleteBlocked | 99 |
| ReplacementMetadataMissingBlocked | 99 |
| HistoricalCandidateBlocked | 55 |
| DiagnosticsOnlyItemKindBlocked | 29 |

## A3 Sample Details

| Sample | Mode | Raw | Eligible | Blocked | MustHit Before/After | Risk Before/After | TopSimilarity | Recommendation |
|---|---|---:|---:|---:|---|---|---:|---|
| automation-sample-001 | AutomationMode | 10 | 9 | 1 | memory:automation-last-error<br>doc:automation-guide/memory:automation-last-error<br>doc:automation-guide | 1/0 | 0.7296 | KeepPreviewOnly |
| automation-sample-002 | AutomationMode | 10 | 6 | 4 | memory:automation-noise-keyword/- | 4/0 | 0.7904 | NeedsPolicyTuning |
| automation-sample-003 | AutomationMode | 10 | 9 | 1 | doc:automation-guide/doc:automation-guide | 1/0 | 0.7462 | KeepPreviewOnly |
| automation-sample-004 | AutomationMode | 10 | 6 | 4 | memory:automation-conflict-v2/memory:automation-conflict-v2 | 4/0 | 0.7513 | KeepPreviewOnly |
| automation-sample-005 | AutomationMode | 10 | 6 | 4 | memory:automation-extra-active-1/memory:automation-extra-active-1 | 4/0 | 0.8621 | KeepPreviewOnly |
| automation-sample-006 | AutomationMode | 10 | 8 | 2 | memory:automation-extra-active-2/memory:automation-extra-active-2 | 2/0 | 0.8455 | KeepPreviewOnly |
| automation-sample-007 | AutomationMode | 10 | 5 | 5 | doc:automation-guide/doc:automation-guide | 5/0 | 0.6197 | KeepPreviewOnly |
| automation-sample-008 | AutomationMode | 10 | 6 | 4 | memory:automation-backup-strategy-v2/memory:automation-backup-strategy-v2 | 4/0 | 0.7429 | KeepPreviewOnly |
| automation-sample-009 | AutomationMode | 10 | 1 | 9 | memory:automation-stopped-cron/- | 9/0 | 0.7370 | NeedsPolicyTuning |
| automation-sample-010 | AutomationMode | 10 | 8 | 2 | doc:automation-guide<br>memory:automation-disk-error/doc:automation-guide<br>memory:automation-disk-error | 2/0 | 0.8831 | KeepPreviewOnly |
| chat-sample-001 | ChatMode | 10 | 8 | 2 | stable:preference-language/stable:preference-language | 2/0 | 0.6652 | KeepPreviewOnly |
| chat-sample-002 | ChatMode | 10 | 6 | 4 | memory:chat-active-plan/memory:chat-active-plan | 4/0 | 0.5958 | KeepPreviewOnly |
| chat-sample-003 | ChatMode | 10 | 9 | 1 | stable:preference-language/stable:preference-language | 1/0 | 0.7181 | KeepPreviewOnly |
| chat-sample-004 | ChatMode | 10 | 9 | 1 | memory:chat-version-conflict-v2/memory:chat-version-conflict-v2 | 1/0 | 0.7834 | KeepPreviewOnly |
| chat-sample-005 | ChatMode | 10 | 6 | 4 | -/- | 4/0 | 0.6681 | NeedsPolicyTuning |
| chat-sample-006 | ChatMode | 10 | 9 | 1 | memory:chat-extra-active-1/memory:chat-extra-active-1 | 1/0 | 0.7206 | KeepPreviewOnly |
| chat-sample-007 | ChatMode | 10 | 8 | 2 | memory:chat-extra-active-2/memory:chat-extra-active-2 | 2/0 | 0.7829 | KeepPreviewOnly |
| chat-sample-008 | ChatMode | 10 | 9 | 1 | memory:chat-drink-preference-v2/memory:chat-drink-preference-v2 | 1/0 | 0.7313 | KeepPreviewOnly |
| chat-sample-009 | ChatMode | 10 | 4 | 6 | memory:chat-deprecated-draft/- | 6/0 | 0.7083 | NeedsPolicyTuning |
| chat-sample-010 | ChatMode | 10 | 6 | 4 | memory:chat-delivery-date/memory:chat-delivery-date | 4/0 | 0.6258 | KeepPreviewOnly |
| coding-sample-001 | CodingMode | 10 | 8 | 2 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/doc:ipromotioncandidatestore<br>memory:coding-next-todo | 2/0 | 0.7168 | KeepPreviewOnly |
| coding-sample-002 | CodingMode | 10 | 2 | 8 | doc:coding-noise-keyword/- | 8/0 | 0.8240 | NeedsPolicyTuning |
| coding-sample-003 | CodingMode | 10 | 9 | 1 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/doc:ipromotioncandidatestore<br>memory:coding-next-todo | 1/0 | 0.7226 | KeepPreviewOnly |
| coding-sample-004 | CodingMode | 10 | 8 | 2 | memory:coding-conflict-v2/memory:coding-conflict-v2 | 2/0 | 0.7821 | KeepPreviewOnly |
| coding-sample-005 | CodingMode | 10 | 10 | 0 | memory:coding-extra-active-1/memory:coding-extra-active-1 | 0/0 | 0.7184 | KeepPreviewOnly |
| coding-sample-006 | CodingMode | 10 | 9 | 1 | memory:coding-extra-active-2/memory:coding-extra-active-2 | 1/0 | 0.8416 | KeepPreviewOnly |
| coding-sample-007 | CodingMode | 10 | 5 | 5 | -/- | 5/0 | 0.6610 | NeedsPolicyTuning |
| coding-sample-008 | CodingMode | 10 | 5 | 5 | memory:coding-timeout-v2/memory:coding-timeout-v2 | 5/0 | 0.7274 | KeepPreviewOnly |
| coding-sample-009 | CodingMode | 10 | 0 | 10 | memory:coding-deprecated-logger/- | 10/0 | 0.7314 | NeedsPolicyTuning |
| coding-sample-010 | CodingMode | 10 | 10 | 0 | memory:coding-test-naming/memory:coding-test-naming | 0/0 | 0.7628 | KeepPreviewOnly |
| novel-sample-001 | NovelMode | 10 | 7 | 3 | novel:world-cangqiong<br>novel:character-linfeng<br>memory:novel-current-plot/novel:world-cangqiong<br>novel:character-linfeng<br>memory:novel-current-plot | 3/0 | 0.8190 | KeepPreviewOnly |
| novel-sample-002 | NovelMode | 10 | 4 | 6 | memory:novel-plot-old-draft/- | 6/0 | 0.7595 | NeedsPolicyTuning |
| novel-sample-003 | NovelMode | 10 | 6 | 4 | memory:novel-plot-old-draft<br>memory:novel-current-plot/memory:novel-current-plot | 4/0 | 0.8101 | KeepPreviewOnly |
| novel-sample-004 | NovelMode | 10 | 7 | 3 | memory:novel-conflict-v2/memory:novel-conflict-v2 | 3/0 | 0.7906 | KeepPreviewOnly |
| novel-sample-005 | NovelMode | 10 | 7 | 3 | memory:novel-extra-active-1/memory:novel-extra-active-1 | 3/0 | 0.7149 | KeepPreviewOnly |
| novel-sample-006 | NovelMode | 10 | 8 | 2 | memory:novel-extra-active-2/memory:novel-extra-active-2 | 2/0 | 0.5086 | KeepPreviewOnly |
| novel-sample-007 | NovelMode | 10 | 6 | 4 | novel:character-linfeng/novel:character-linfeng | 4/0 | 0.6342 | KeepPreviewOnly |
| novel-sample-008 | NovelMode | 10 | 7 | 3 | memory:novel-weapon-v2/memory:novel-weapon-v2 | 3/0 | 0.8088 | KeepPreviewOnly |
| novel-sample-009 | NovelMode | 10 | 1 | 9 | memory:novel-plot-deprecated-villain/- | 9/0 | 0.7192 | NeedsPolicyTuning |
| novel-sample-010 | NovelMode | 10 | 9 | 1 | novel:character-linfeng<br>memory:novel-family-background/novel:character-linfeng<br>memory:novel-family-background | 1/0 | 0.6324 | KeepPreviewOnly |
| project-sample-001 | ProjectMode | 10 | 6 | 4 | doc:local-alpha-runbook/doc:local-alpha-runbook | 4/0 | 0.7177 | KeepPreviewOnly |
| project-sample-002 | ProjectMode | 10 | 4 | 6 | doc:postgres-not-ready/doc:postgres-not-ready | 6/0 | 0.6686 | KeepPreviewOnly |
| project-sample-003 | ProjectMode | 10 | 5 | 5 | doc:local-alpha-runbook/doc:local-alpha-runbook | 5/0 | 0.7261 | KeepPreviewOnly |
| project-sample-004 | ProjectMode | 10 | 9 | 1 | memory:project-conflict-v2/memory:project-conflict-v2 | 1/0 | 0.7599 | KeepPreviewOnly |
| project-sample-005 | ProjectMode | 10 | 7 | 3 | memory:project-current-step/memory:project-current-step | 3/0 | 0.5601 | KeepPreviewOnly |
| project-sample-006 | ProjectMode | 10 | 8 | 2 | memory:project-extra-active-2/memory:project-extra-active-2 | 2/0 | 0.7931 | KeepPreviewOnly |
| project-sample-007 | ProjectMode | 10 | 7 | 3 | -/- | 3/0 | 0.6596 | NeedsPolicyTuning |
| project-sample-008 | ProjectMode | 10 | 5 | 5 | memory:project-pool-v2/memory:project-pool-v2 | 5/0 | 0.7968 | KeepPreviewOnly |
| project-sample-009 | ProjectMode | 10 | 1 | 9 | memory:project-deprecated-gateway/- | 9/0 | 0.7315 | NeedsPolicyTuning |
| project-sample-010 | ProjectMode | 10 | 8 | 2 | doc:local-alpha-runbook<br>memory:project-required-env/doc:local-alpha-runbook<br>memory:project-required-env | 2/0 | 0.6049 | KeepPreviewOnly |

## Extended Summary

- Samples: `113`
- IndexedCoverage: `100.00%`
- RawCandidateCount: `1130`
- EligibleCandidateCount: `915`
- BlockedCandidateCount: `215`
- RiskBeforePolicy: `215`
- RiskAfterPolicy: `0`
- MustHitRecallBeforePolicy: `90.00%`
- MustHitRecallAfterPolicy: `84.38%`
- MustNotHitRiskBeforePolicy: `25.41%`
- MustNotHitRiskAfterPolicy: `0.00%`
- LifecycleRiskBeforePolicy: `16.37%`
- LifecycleRiskAfterPolicy: `0.00%`
- DeprecatedHitCount: `185`
- DuplicateHitCount: `0`
- AverageTopSimilarity: `0.7791`
- NoCandidateCount: `0`
- LowConfidenceCount: `0`
- Recommendation: `ReadyForRetrievalShadow`

| Noise Cluster | Count |
|---|---:|
| DeprecatedHit | 185 |
| LifecycleRiskBeforePolicy | 185 |
| MustNotHitBeforePolicy | 31 |
| NoEligibleCandidate | 1 |

| Blocked Reason | Count |
|---|---:|
| HistoricalSourceRequiresAuditProfile | 185 |
| DeprecatedCandidateBlocked | 176 |
| LifecycleMetadataIncompleteBlocked | 130 |
| ReplacementMetadataMissingBlocked | 130 |
| HistoricalCandidateBlocked | 64 |
| DiagnosticsOnlyItemKindBlocked | 30 |

## Extended Sample Details

| Sample | Mode | Raw | Eligible | Blocked | MustHit Before/After | Risk Before/After | TopSimilarity | Recommendation |
|---|---|---:|---:|---:|---|---|---:|---|
| automation-20260529-001 | AutomationMode | 10 | 10 | 0 | job:last-failed<br>run:blocker-current/job:last-failed<br>run:blocker-current | 0/0 | 0.8867 | KeepPreviewOnly |
| automation-20260529-002 | AutomationMode | 10 | 9 | 1 | queue:dead-letter<br>job:failed-requeue-candidate/queue:dead-letter<br>job:failed-requeue-candidate | 1/0 | 0.7690 | KeepPreviewOnly |
| automation-20260529-003 | AutomationMode | 10 | 8 | 2 | worker:stats-latest<br>event:recent-failures/worker:stats-latest<br>event:recent-failures | 2/0 | 0.8651 | KeepPreviewOnly |
| automation-20260529-004 | AutomationMode | 10 | 8 | 2 | run:latest-recovery-point<br>step:last-succeeded/run:latest-recovery-point<br>step:last-succeeded | 2/0 | 0.8426 | KeepPreviewOnly |
| automation-20260529-005 | AutomationMode | 10 | 8 | 2 | confirmation:required-before-delete<br>safety:destructive-operation/confirmation:required-before-delete<br>safety:destructive-operation | 2/0 | 0.8225 | KeepPreviewOnly |
| automation-20260529-006 | AutomationMode | 10 | 7 | 3 | tool:last-timeout<br>log:last-error/tool:last-timeout<br>log:last-error | 3/0 | 0.9012 | KeepPreviewOnly |
| automation-20260529-007 | AutomationMode | 10 | 9 | 1 | worker:concurrency<br>job:dedupe-token/worker:concurrency<br>job:dedupe-token | 1/0 | 0.8558 | KeepPreviewOnly |
| automation-20260529-008 | AutomationMode | 10 | 10 | 0 | job:max-retry-exceeded<br>queue:dead-letter/job:max-retry-exceeded<br>queue:dead-letter | 0/0 | 0.8103 | KeepPreviewOnly |
| automation-20260529-009 | AutomationMode | 10 | 9 | 1 | run:environment-snapshot<br>run:resume-precheck/run:environment-snapshot<br>run:resume-precheck | 1/0 | 0.9292 | KeepPreviewOnly |
| automation-20260529-010 | AutomationMode | 10 | 9 | 1 | run:current-failed-steps<br>report:actionable-fix/run:current-failed-steps<br>report:actionable-fix | 1/0 | 0.7431 | KeepPreviewOnly |
| automation-sample-001 | AutomationMode | 10 | 9 | 1 | memory:automation-last-error<br>doc:automation-guide/memory:automation-last-error<br>doc:automation-guide | 1/0 | 0.7296 | KeepPreviewOnly |
| automation-sample-002 | AutomationMode | 10 | 6 | 4 | memory:automation-noise-keyword/- | 4/0 | 0.7904 | NeedsPolicyTuning |
| automation-sample-003 | AutomationMode | 10 | 9 | 1 | doc:automation-guide/doc:automation-guide | 1/0 | 0.7462 | KeepPreviewOnly |
| automation-sample-004 | AutomationMode | 10 | 6 | 4 | memory:automation-conflict-v2/memory:automation-conflict-v2 | 4/0 | 0.7513 | KeepPreviewOnly |
| automation-sample-005 | AutomationMode | 10 | 6 | 4 | memory:automation-extra-active-1/memory:automation-extra-active-1 | 4/0 | 0.8621 | KeepPreviewOnly |
| automation-sample-006 | AutomationMode | 10 | 8 | 2 | memory:automation-extra-active-2/memory:automation-extra-active-2 | 2/0 | 0.8455 | KeepPreviewOnly |
| automation-sample-007 | AutomationMode | 10 | 5 | 5 | doc:automation-guide/doc:automation-guide | 5/0 | 0.6197 | KeepPreviewOnly |
| automation-sample-008 | AutomationMode | 10 | 6 | 4 | memory:automation-backup-strategy-v2/memory:automation-backup-strategy-v2 | 4/0 | 0.7429 | KeepPreviewOnly |
| automation-sample-009 | AutomationMode | 10 | 1 | 9 | memory:automation-stopped-cron/- | 9/0 | 0.7370 | NeedsPolicyTuning |
| automation-sample-010 | AutomationMode | 10 | 8 | 2 | doc:automation-guide<br>memory:automation-disk-error/doc:automation-guide<br>memory:automation-disk-error | 2/0 | 0.8831 | KeepPreviewOnly |
| chat-20260529-001 | ChatMode | 10 | 10 | 0 | pref:zh-output<br>memory:long-term-user-preference/pref:zh-output<br>memory:long-term-user-preference | 0/0 | 0.8261 | KeepPreviewOnly |
| chat-20260529-002 | ChatMode | 10 | 9 | 1 | -/- | 1/0 | 0.6775 | NeedsPolicyTuning |
| chat-20260529-003 | ChatMode | 10 | 10 | 0 | memory:session-conclusion/memory:session-conclusion | 0/0 | 0.6885 | KeepPreviewOnly |
| chat-20260529-004 | ChatMode | 10 | 10 | 0 | pref:answer-conclusion-first/pref:answer-conclusion-first | 0/0 | 0.9036 | KeepPreviewOnly |
| chat-20260529-005 | ChatMode | 10 | 10 | 0 | policy:no-promote-temporary-emotion/policy:no-promote-temporary-emotion | 0/0 | 0.6877 | KeepPreviewOnly |
| chat-20260529-006 | ChatMode | 10 | 10 | 0 | task:current-active/task:current-active | 0/0 | 0.8633 | KeepPreviewOnly |
| chat-20260529-007 | ChatMode | 10 | 9 | 1 | pattern:repeated-user-preference/pattern:repeated-user-preference | 1/0 | 0.8553 | KeepPreviewOnly |
| chat-20260529-008 | ChatMode | 10 | 10 | 0 | uncertainty:pending-confirmation/uncertainty:pending-confirmation | 0/0 | 0.7265 | KeepPreviewOnly |
| chat-20260529-009 | ChatMode | 10 | 10 | 0 | policy:no-promote-joke/policy:no-promote-joke | 0/0 | 0.7543 | KeepPreviewOnly |
| chat-20260529-010 | ChatMode | 10 | 8 | 2 | constraint:no-secret-commit/constraint:no-secret-commit | 2/0 | 0.8656 | KeepPreviewOnly |
| chat-20260529-011 | ChatMode | 10 | 9 | 1 | scope:current-session/scope:current-session | 1/0 | 0.7945 | KeepPreviewOnly |
| chat-20260529-012 | ChatMode | 10 | 9 | 1 | rule:naming-confirmed/rule:naming-confirmed | 1/0 | 0.7939 | KeepPreviewOnly |
| chat-20260529-013 | ChatMode | 10 | 10 | 0 | revision:latest-expression/revision:latest-expression | 0/0 | 0.7695 | KeepPreviewOnly |
| chat-20260529-014 | ChatMode | 10 | 8 | 2 | pref:interaction-style/pref:interaction-style | 2/0 | 0.8159 | KeepPreviewOnly |
| chat-20260529-015 | ChatMode | 10 | 10 | 0 | policy:no-promote-oneoff/policy:no-promote-oneoff | 0/0 | 0.8387 | KeepPreviewOnly |
| chat-20260529-016 | ChatMode | 10 | 10 | 0 | pref:actionable-concise/pref:actionable-concise | 0/0 | 0.8441 | KeepPreviewOnly |
| chat-20260529-017 | ChatMode | 10 | 10 | 0 | pref:admit-uncertainty/pref:admit-uncertainty | 0/0 | 0.7863 | KeepPreviewOnly |
| chat-20260529-018 | ChatMode | 10 | 10 | 0 | source:reference-only/source:reference-only | 0/0 | 0.8484 | KeepPreviewOnly |
| chat-20260529-019 | ChatMode | 10 | 10 | 0 | task:resume-context/task:resume-context | 0/0 | 0.8607 | KeepPreviewOnly |
| chat-20260529-020 | ChatMode | 10 | 10 | 0 | scope:project-only/scope:project-only | 0/0 | 0.8910 | KeepPreviewOnly |
| chat-sample-001 | ChatMode | 10 | 8 | 2 | stable:preference-language/stable:preference-language | 2/0 | 0.6652 | KeepPreviewOnly |
| chat-sample-002 | ChatMode | 10 | 6 | 4 | memory:chat-active-plan/memory:chat-active-plan | 4/0 | 0.5958 | KeepPreviewOnly |
| chat-sample-003 | ChatMode | 10 | 9 | 1 | stable:preference-language/stable:preference-language | 1/0 | 0.7181 | KeepPreviewOnly |
| chat-sample-004 | ChatMode | 10 | 9 | 1 | memory:chat-version-conflict-v2/memory:chat-version-conflict-v2 | 1/0 | 0.7834 | KeepPreviewOnly |
| chat-sample-005 | ChatMode | 10 | 6 | 4 | -/- | 4/0 | 0.6681 | NeedsPolicyTuning |
| chat-sample-006 | ChatMode | 10 | 9 | 1 | memory:chat-extra-active-1/memory:chat-extra-active-1 | 1/0 | 0.7206 | KeepPreviewOnly |
| chat-sample-007 | ChatMode | 10 | 8 | 2 | memory:chat-extra-active-2/memory:chat-extra-active-2 | 2/0 | 0.7829 | KeepPreviewOnly |
| chat-sample-008 | ChatMode | 10 | 9 | 1 | memory:chat-drink-preference-v2/memory:chat-drink-preference-v2 | 1/0 | 0.7313 | KeepPreviewOnly |
| chat-sample-009 | ChatMode | 10 | 4 | 6 | memory:chat-deprecated-draft/- | 6/0 | 0.7083 | NeedsPolicyTuning |
| chat-sample-010 | ChatMode | 10 | 6 | 4 | memory:chat-delivery-date/memory:chat-delivery-date | 4/0 | 0.6258 | KeepPreviewOnly |
| coding-20260529-001 | CodingMode | 10 | 8 | 2 | todo:A3/todo:A3 | 2/0 | 0.8651 | KeepPreviewOnly |
| coding-20260529-002 | CodingMode | 10 | 9 | 1 | test:last-failure<br>assertion:expected-output/test:last-failure<br>assertion:expected-output | 1/0 | 0.8886 | KeepPreviewOnly |
| coding-20260529-003 | CodingMode | 10 | 10 | 0 | verification:build<br>verification:test<br>verification:secret-scan/verification:build<br>verification:test<br>verification:secret-scan | 0/0 | 0.7516 | KeepPreviewOnly |
| coding-20260529-004 | CodingMode | 10 | 9 | 1 | build:last-error-file<br>task:fix-build-failure/build:last-error-file<br>task:fix-build-failure | 1/0 | 0.8576 | KeepPreviewOnly |
| coding-20260529-005 | CodingMode | 10 | 9 | 1 | api:public-contract<br>compatibility:backward-check/api:public-contract<br>compatibility:backward-check | 1/0 | 0.8252 | KeepPreviewOnly |
| coding-20260529-006 | CodingMode | 10 | 10 | 0 | comment:core-logic-chinese<br>comment:avoid-noise/comment:core-logic-chinese<br>comment:avoid-noise | 0/0 | 0.8557 | KeepPreviewOnly |
| coding-20260529-007 | CodingMode | 10 | 10 | 0 | performance:avoid-repeated-io<br>performance:avoid-full-deserialize/performance:avoid-repeated-io<br>performance:avoid-full-deserialize | 0/0 | 0.8198 | KeepPreviewOnly |
| coding-20260529-008 | CodingMode | 10 | 10 | 0 | test:risk-focused<br>test:avoid-brittle-snapshot/test:risk-focused<br>test:avoid-brittle-snapshot | 0/0 | 0.8724 | KeepPreviewOnly |
| coding-20260529-009 | CodingMode | 10 | 10 | 0 | security:private-config-dir<br>security:no-secret-in-repo/security:private-config-dir<br>security:no-secret-in-repo | 0/0 | 0.9161 | KeepPreviewOnly |
| coding-20260529-010 | CodingMode | 10 | 9 | 1 | verification:build<br>verification:test<br>verification:secret-scan/verification:build<br>verification:test<br>verification:secret-scan | 1/0 | 0.7555 | KeepPreviewOnly |
| coding-sample-001 | CodingMode | 10 | 8 | 2 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/doc:ipromotioncandidatestore<br>memory:coding-next-todo | 2/0 | 0.7168 | KeepPreviewOnly |
| coding-sample-002 | CodingMode | 10 | 2 | 8 | doc:coding-noise-keyword/- | 8/0 | 0.8240 | NeedsPolicyTuning |
| coding-sample-003 | CodingMode | 10 | 9 | 1 | doc:ipromotioncandidatestore<br>memory:coding-next-todo/doc:ipromotioncandidatestore<br>memory:coding-next-todo | 1/0 | 0.7226 | KeepPreviewOnly |
| coding-sample-004 | CodingMode | 10 | 8 | 2 | memory:coding-conflict-v2/memory:coding-conflict-v2 | 2/0 | 0.7821 | KeepPreviewOnly |
| coding-sample-005 | CodingMode | 10 | 10 | 0 | memory:coding-extra-active-1/memory:coding-extra-active-1 | 0/0 | 0.7184 | KeepPreviewOnly |
| coding-sample-006 | CodingMode | 10 | 9 | 1 | memory:coding-extra-active-2/memory:coding-extra-active-2 | 1/0 | 0.8416 | KeepPreviewOnly |
| coding-sample-007 | CodingMode | 10 | 5 | 5 | -/- | 5/0 | 0.6610 | NeedsPolicyTuning |
| coding-sample-008 | CodingMode | 10 | 5 | 5 | memory:coding-timeout-v2/memory:coding-timeout-v2 | 5/0 | 0.7274 | KeepPreviewOnly |
| coding-sample-009 | CodingMode | 10 | 0 | 10 | memory:coding-deprecated-logger/- | 10/0 | 0.7314 | NeedsPolicyTuning |
| coding-sample-010 | CodingMode | 10 | 10 | 0 | memory:coding-test-naming/memory:coding-test-naming | 0/0 | 0.7628 | KeepPreviewOnly |
| novel-20260529-001 | NovelMode | 10 | 8 | 2 | -/- | 2/0 | 0.7060 | NeedsPolicyTuning |
| novel-20260529-002 | NovelMode | 10 | 10 | 0 | relation:protagonist-mentor-current<br>scene:last-conflict/relation:protagonist-mentor-current<br>scene:last-conflict | 0/0 | 0.7601 | KeepPreviewOnly |
| novel-20260529-003 | NovelMode | 10 | 10 | 0 | constraint:magic-cost<br>world:rules-current/constraint:magic-cost<br>world:rules-current | 0/0 | 0.7249 | KeepPreviewOnly |
| novel-20260529-004 | NovelMode | 10 | 10 | 0 | state:protagonist-injured/state:protagonist-injured | 0/0 | 0.8319 | KeepPreviewOnly |
| novel-20260529-005 | NovelMode | 10 | 10 | 0 | motive:villain-true/motive:villain-true | 0/0 | 0.6524 | KeepPreviewOnly |
| novel-20260529-006 | NovelMode | 10 | 9 | 1 | relation:post-argument-distance/relation:post-argument-distance | 1/0 | 0.9024 | KeepPreviewOnly |
| novel-20260529-007 | NovelMode | 10 | 9 | 1 | world:city-lockdown-active/world:city-lockdown-active | 1/0 | 0.8811 | KeepPreviewOnly |
| novel-20260529-008 | NovelMode | 10 | 10 | 0 | knowledge:mentor-letter-exists/knowledge:mentor-letter-exists | 0/0 | 0.8290 | KeepPreviewOnly |
| novel-20260529-009 | NovelMode | 10 | 9 | 1 | foreshadow:bell-sound/foreshadow:bell-sound | 1/0 | 0.8534 | KeepPreviewOnly |
| novel-20260529-010 | NovelMode | 10 | 7 | 3 | subplot:active-trade-route/subplot:active-trade-route | 3/0 | 0.8080 | KeepPreviewOnly |

