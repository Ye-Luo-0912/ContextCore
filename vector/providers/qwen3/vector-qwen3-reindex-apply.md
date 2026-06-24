# Vector Reindex Report

- ReportId: `a2471f2fdffc487bb34829bf95168b46`
- OperationId: `vector-reindex-cli-8810116c3bdb4de6b24ab466891bd5ca`
- Workspace: `eval-vector`
- Collection: `corpus`
- DryRun: `False`
- Applied: `True`

## Summary

- TotalCandidates: `158`
- Created: `0`
- Updated: `158`
- Skipped: `0`
- Failed: `0`
- Duplicate: `0`
- Orphan: `0`
- EstimatedEmbeddingCount: `158`

## Plan

- ToCreate: `0`
- ToUpdate: `158`
- ToSkip: `0`
- ToDeleteOrphan: `0`

| Action | ItemId | Kind | Layer | Reason |
|---|---|---|---|---|
| Update | api:public-contract | api-contract | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | arc:imperial-city | story-arc | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | assertion:expected-output | assertion | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | belief:protagonist-doubts-prophecy | belief | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | build:last-error-file | build-error | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | candidate:promotion-working | promotion-candidate | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | capability:storage-matrix | capability-matrix | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | comment:avoid-noise | comment-rule | Stable | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | comment:core-logic-chinese | comment-rule | Stable | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | compatibility:backward-check | compatibility | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | confirmation:required-before-delete | confirmation | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | constraint:magic-cost | world-rule | Stable | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | constraint:no-secret-commit | security-constraint | Stable | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | ctx:chat-system-intro | system-intro | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | deal:emperor-priest | deal | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:automation-guide | guide | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:coding-noise-keyword | documentation | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:ipromotioncandidatestore | interface | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:legacy-code-structure | documentation | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:legacy-project-info | documentation | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:local-alpha-runbook | documentation | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:postgres-not-ready | documentation | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | doc:project-noise-keyword | documentation | context | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | ending:current-plan | ending-plan | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | event:recent-failures | event-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | foreshadow:bell-sound | foreshadow | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | item:sword-broken | item-state | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | job:dedupe-token | dedupe | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | job:failed-requeue-candidate | job-state | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | job:last-failed | job-state | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | job:max-retry-exceeded | retry-state | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | knowledge:mentor-letter-exists | character-knowledge | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | location:party-outside-city | location | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | log:last-error | error-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:active-character-state | character-state | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-backup-strategy-v1 | config-detail | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-backup-strategy-v2 | config-detail | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-budget-stress | stress-test | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-conflict-v1 | config-detail | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-conflict-v2 | config-detail | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-disk-error | error-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-extra-active-1 | env-info | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-extra-active-2 | policy | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-last-error | error-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-noise-keyword | run-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-old-success | run-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-recovery | recovery-point | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:automation-stopped-cron | cron-log | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:chat-active-plan | plan | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
| Update | memory:chat-budget-stress | stress-test | Working | embedding 配置变化，需要 reindex：EmbeddingProviderChanged, EmbeddingModelChanged, DimensionChanged。 |
