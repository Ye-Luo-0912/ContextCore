# Postgres Vector Provider-scoped Reindex Plan

- Recommendation: `ReadyForPgVectorQueryPreview`
- WorkspaceId: `eval-vector`
- CollectionId: `corpus`
- ProviderId: `deterministic-hash`
- ModelId: `deterministic-hash-v1`
- Dimension: `16`
- Normalized: `True`
- UseForRuntime: `False`
- SourceKindFilter: `-`
- DryRun: `True`
- CandidateCount: `158`
- PlannedInsertCount: `0`
- PlannedUpdateCount: `0`
- PlannedDeleteCount: `0`
- PlannedSkipCount: `158`
- StaleEntryCount: `0`
- OrphanEntryCount: `0`
- DuplicateSourceCount: `0`
- DimensionMismatchCount: `0`
- ProviderModelMismatchCount: `0`

## Planned Items

| Action | SourceId | SourceKind | ItemKind | Layer | Reason |
|---|---|---|---|---|---|
| Skip | api:public-contract | memory | api-contract | Working | pgvector entry 已是当前 provider scope。 |
| Skip | arc:imperial-city | memory | story-arc | Working | pgvector entry 已是当前 provider scope。 |
| Skip | assertion:expected-output | memory | assertion | Working | pgvector entry 已是当前 provider scope。 |
| Skip | belief:protagonist-doubts-prophecy | memory | belief | Working | pgvector entry 已是当前 provider scope。 |
| Skip | build:last-error-file | memory | build-error | Working | pgvector entry 已是当前 provider scope。 |
| Skip | candidate:promotion-working | memory | promotion-candidate | Working | pgvector entry 已是当前 provider scope。 |
| Skip | capability:storage-matrix | memory | capability-matrix | Working | pgvector entry 已是当前 provider scope。 |
| Skip | comment:avoid-noise | memory | comment-rule | Stable | pgvector entry 已是当前 provider scope。 |
| Skip | comment:core-logic-chinese | memory | comment-rule | Stable | pgvector entry 已是当前 provider scope。 |
| Skip | compatibility:backward-check | memory | compatibility | Working | pgvector entry 已是当前 provider scope。 |
| Skip | confirmation:required-before-delete | memory | confirmation | Working | pgvector entry 已是当前 provider scope。 |
| Skip | constraint:magic-cost | memory | world-rule | Stable | pgvector entry 已是当前 provider scope。 |
| Skip | constraint:no-secret-commit | memory | security-constraint | Stable | pgvector entry 已是当前 provider scope。 |
| Skip | ctx:chat-system-intro | context | system-intro | context | pgvector entry 已是当前 provider scope。 |
| Skip | deal:emperor-priest | memory | deal | Working | pgvector entry 已是当前 provider scope。 |
| Skip | doc:automation-guide | context | guide | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:coding-noise-keyword | context | documentation | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:ipromotioncandidatestore | context | interface | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:legacy-code-structure | context | documentation | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:legacy-project-info | context | documentation | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:local-alpha-runbook | context | documentation | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:postgres-not-ready | context | documentation | context | pgvector entry 已是当前 provider scope。 |
| Skip | doc:project-noise-keyword | context | documentation | context | pgvector entry 已是当前 provider scope。 |
| Skip | ending:current-plan | memory | ending-plan | Working | pgvector entry 已是当前 provider scope。 |
| Skip | event:recent-failures | memory | event-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | foreshadow:bell-sound | memory | foreshadow | Working | pgvector entry 已是当前 provider scope。 |
| Skip | item:sword-broken | memory | item-state | Working | pgvector entry 已是当前 provider scope。 |
| Skip | job:dedupe-token | memory | dedupe | Working | pgvector entry 已是当前 provider scope。 |
| Skip | job:failed-requeue-candidate | memory | job-state | Working | pgvector entry 已是当前 provider scope。 |
| Skip | job:last-failed | memory | job-state | Working | pgvector entry 已是当前 provider scope。 |
| Skip | job:max-retry-exceeded | memory | retry-state | Working | pgvector entry 已是当前 provider scope。 |
| Skip | knowledge:mentor-letter-exists | memory | character-knowledge | Working | pgvector entry 已是当前 provider scope。 |
| Skip | location:party-outside-city | memory | location | Working | pgvector entry 已是当前 provider scope。 |
| Skip | log:last-error | memory | error-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:active-character-state | memory | character-state | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-backup-strategy-v1 | memory | config-detail | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-backup-strategy-v2 | memory | config-detail | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-budget-stress | memory | stress-test | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-conflict-v1 | memory | config-detail | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-conflict-v2 | memory | config-detail | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-disk-error | memory | error-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-extra-active-1 | memory | env-info | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-extra-active-2 | memory | policy | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-last-error | memory | error-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-noise-keyword | memory | run-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-old-success | memory | run-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-recovery | memory | recovery-point | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:automation-stopped-cron | memory | cron-log | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:chat-active-plan | memory | plan | Working | pgvector entry 已是当前 provider scope。 |
| Skip | memory:chat-budget-stress | memory | stress-test | Working | pgvector entry 已是当前 provider scope。 |

## Diagnostics

- UseForRuntime=false
- RetrievalProviderUnchanged
- FileSystemVectorStoreUnchanged
