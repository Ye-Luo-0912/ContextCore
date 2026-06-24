# Retrieval Dataset V2 Validation Report

- CorpusItemCount: `158`
- QuerySampleCount: `113`
- MustHitCount: `160`
- MustNotCount: `122`
- MissingSourceRefsCount: `271`
- MissingEvidenceRefsCount: `271`
- MissingProvenanceCount: `271`
- IssueCount: `1173`
- GeneratesFormalDataset: `False`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- Recommendation: `NeedsRelationEvidenceBackfill`

## Issue Breakdown

| Issue | Count |
|---|---:|
| MissingEvidenceRefs | 271 |
| MissingProvenance | 271 |
| MissingSourceRefs | 271 |
| SplitIsolationViolation | 271 |
| MustNotMissingFromCorpus | 67 |
| RelationEvidenceMissing | 22 |

| Issue | Sample | Item | Split | Message |
|---|---|---|---|---|
| MissingEvidenceRefs |  | api:public-contract |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | arc:imperial-city |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | assertion:expected-output |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | belief:protagonist-doubts-prophecy |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | build:last-error-file |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | candidate:promotion-working |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | capability:storage-matrix |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | comment:avoid-noise |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | comment:core-logic-chinese |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | compatibility:backward-check |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | confirmation:required-before-delete |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | constraint:magic-cost |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | constraint:no-secret-commit |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | ctx:chat-system-intro |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | deal:emperor-priest |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:automation-guide |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:coding-noise-keyword |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:ipromotioncandidatestore |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:legacy-code-structure |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:legacy-project-info |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:local-alpha-runbook |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:postgres-not-ready |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | doc:project-noise-keyword |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | ending:current-plan |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | event:recent-failures |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | foreshadow:bell-sound |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | item:sword-broken |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | job:dedupe-token |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | job:failed-requeue-candidate |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | job:last-failed |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | job:max-retry-exceeded |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | knowledge:mentor-letter-exists |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | location:party-outside-city |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | log:last-error |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:active-character-state |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-backup-strategy-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-backup-strategy-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-budget-stress |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-conflict-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-conflict-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-disk-error |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-extra-active-1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-extra-active-2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-last-error |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-noise-keyword |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-old-success |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-recovery |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:automation-stopped-cron |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-active-plan |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-budget-stress |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-delivery-date |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-deprecated-draft |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-drink-preference-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-drink-preference-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-extra-active-1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-extra-active-2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-noise-keyword-1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-old-topic |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-version-conflict-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:chat-version-conflict-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-budget-stress |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-conflict-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-conflict-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-deprecated-logger |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-extra-active-1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-extra-active-2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-next-todo |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-test-naming |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-timeout-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:coding-timeout-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:long-term-user-preference |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-budget-stress |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-conflict-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-conflict-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-current-plot |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-extra-active-1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-extra-active-2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-family-background |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-plot-deprecated-villain |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-plot-noise-keyword |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-plot-old-draft |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-weapon-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:novel-weapon-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-budget-stress |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-conflict-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-conflict-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-current-step |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-deprecated-gateway |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-extra-active-1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-extra-active-2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-pool-v1 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-pool-v2 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:project-required-env |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | memory:session-conclusion |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | motive:villain-true |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | novel:character-linfeng |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | novel:world-cangqiong |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | pattern:repeated-user-preference |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | performance:avoid-full-deserialize |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | performance:avoid-repeated-io |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | plot:mainline-current |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | plot:previous-chapter-hook |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | policy:no-promote-joke |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | policy:no-promote-oneoff |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | policy:no-promote-temporary-emotion |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | pref:actionable-concise |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | pref:admit-uncertainty |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | pref:answer-conclusion-first |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | pref:interaction-style |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | pref:zh-output |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | queue:dead-letter |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | relation:post-argument-distance |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | relation:protagonist-mentor-current |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | report:actionable-fix |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | report:new-stage-execution |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | revision:latest-expression |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | risk:postgres-partial-provider |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | roadmap:A3 |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | rule:naming-confirmed |  | corpus item must include evidenceRefs. |
| MissingEvidenceRefs |  | rule:royal-bloodline |  | corpus item must include evidenceRefs. |
