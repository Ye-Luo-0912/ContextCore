# Postgres Vector Provider-scoped Reindex Plan

- Recommendation: `ReadyForPgVectorQueryPreview`
- WorkspaceId: `eval-vector`
- CollectionId: `corpus`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ModelId: `qwen3-embedding-0.6b`
- Dimension: `1024`
- Normalized: `True`
- UseForRuntime: `False`
- SourceKindFilter: `-`
- DryRun: `True`
- CandidateCount: `158`
- PlannedInsertCount: `158`
- PlannedUpdateCount: `0`
- PlannedDeleteCount: `0`
- PlannedSkipCount: `0`
- StaleEntryCount: `0`
- OrphanEntryCount: `0`
- DuplicateSourceCount: `0`
- DimensionMismatchCount: `0`
- ProviderModelMismatchCount: `0`

## Planned Items

| Action | SourceId | SourceKind | ItemKind | Layer | Reason |
|---|---|---|---|---|---|
| Insert | api:public-contract | memory | api-contract | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | arc:imperial-city | memory | story-arc | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | assertion:expected-output | memory | assertion | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | belief:protagonist-doubts-prophecy | memory | belief | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | build:last-error-file | memory | build-error | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | candidate:promotion-working | memory | promotion-candidate | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | capability:storage-matrix | memory | capability-matrix | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | comment:avoid-noise | memory | comment-rule | Stable | source item 尚未写入当前 pgvector provider scope。 |
| Insert | comment:core-logic-chinese | memory | comment-rule | Stable | source item 尚未写入当前 pgvector provider scope。 |
| Insert | compatibility:backward-check | memory | compatibility | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | confirmation:required-before-delete | memory | confirmation | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | constraint:magic-cost | memory | world-rule | Stable | source item 尚未写入当前 pgvector provider scope。 |
| Insert | constraint:no-secret-commit | memory | security-constraint | Stable | source item 尚未写入当前 pgvector provider scope。 |
| Insert | ctx:chat-system-intro | context | system-intro | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | deal:emperor-priest | memory | deal | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:automation-guide | context | guide | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:coding-noise-keyword | context | documentation | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:ipromotioncandidatestore | context | interface | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:legacy-code-structure | context | documentation | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:legacy-project-info | context | documentation | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:local-alpha-runbook | context | documentation | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:postgres-not-ready | context | documentation | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | doc:project-noise-keyword | context | documentation | context | source item 尚未写入当前 pgvector provider scope。 |
| Insert | ending:current-plan | memory | ending-plan | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | event:recent-failures | memory | event-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | foreshadow:bell-sound | memory | foreshadow | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | item:sword-broken | memory | item-state | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | job:dedupe-token | memory | dedupe | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | job:failed-requeue-candidate | memory | job-state | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | job:last-failed | memory | job-state | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | job:max-retry-exceeded | memory | retry-state | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | knowledge:mentor-letter-exists | memory | character-knowledge | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | location:party-outside-city | memory | location | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | log:last-error | memory | error-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:active-character-state | memory | character-state | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-backup-strategy-v1 | memory | config-detail | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-backup-strategy-v2 | memory | config-detail | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-budget-stress | memory | stress-test | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-conflict-v1 | memory | config-detail | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-conflict-v2 | memory | config-detail | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-disk-error | memory | error-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-extra-active-1 | memory | env-info | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-extra-active-2 | memory | policy | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-last-error | memory | error-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-noise-keyword | memory | run-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-old-success | memory | run-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-recovery | memory | recovery-point | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:automation-stopped-cron | memory | cron-log | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:chat-active-plan | memory | plan | Working | source item 尚未写入当前 pgvector provider scope。 |
| Insert | memory:chat-budget-stress | memory | stress-test | Working | source item 尚未写入当前 pgvector provider scope。 |

## Diagnostics

- UseForRuntime=false
- RetrievalProviderUnchanged
- FileSystemVectorStoreUnchanged
