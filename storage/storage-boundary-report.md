# Storage Boundary Report

Generated: 2026-06-12T13:14:27.9114373+00:00

## Summary

- TotalArtifactKinds: `40`
- ArtifactOnly: `8`
- OperationalState: `21`
- IndexState: `2`
- DatabaseRecommended: `23`
- FileSystemPreferred: `30`
- MigrationCandidates: `23`
- HighPriorityMigrationCandidates: `18`

## Migration Candidates

| Subject | Responsibility | Current | Preferred | Priority | Risk | Notes |
|---|---|---|---|---|---|---|
| Constraint | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | constraint lifecycle state |
| ConstraintState | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | constraint lifecycle state should move to database |
| ContextItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | context item state should move to operational store |
| Job | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | job record operational state |
| JobRecord | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | job queue state should move to operational store |
| MemoryCandidate | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | candidate memory reviewable operational state |
| MemoryCandidateItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | candidate memory item state |
| MemoryCandidateReview | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | candidate memory review history |
| MemoryItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | memory item state should move to operational store |
| MemoryStable | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | stable memory lifecycle state |
| MemoryStableItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | stable memory item state |
| MemoryStableLifecycleReview | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | stable lifecycle review history |
| MemoryStableProvenance | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | Medium | stable provenance chain |
| MemoryStableReplacementChain | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | stable replacement chain state |
| Relation | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | relation graph operational state |
| RelationItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | High | High | relation graph should move to database-backed operational store |
| Vector | IndexState | FileSystem/InMemory | DatabaseRecommended | High | Medium | vector index state, pgvector candidate |
| VectorIndexEntry | IndexState | FileSystem/InMemory | DatabaseRecommended | High | Medium | pgvector candidate; filesystem remains preview/export only |
| MemoryCandidateEvidence | OperationalState | FileSystem/InMemory | DatabaseRecommended | Medium | Medium | candidate evidence provenance |
| MemoryShort | OperationalState | FileSystem/InMemory | DatabaseRecommended | Medium | Medium | short-term memory operational state |
| MemoryShortTermRawEvent | OperationalState | FileSystem/InMemory | DatabaseRecommended | Medium | Medium | raw memory event stream |
| MemoryShortTermWorkingItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | Medium | Medium | working memory state |
| MemoryTemporalItem | OperationalState | FileSystem/InMemory | DatabaseRecommended | Medium | Medium | temporal memory placeholder state |

## Recommended Next Phases

- DB1: operational state provider abstraction audit
- DB2: relation/constraint database store design
- DB3: pgvector-backed vector index store design
- DB4: job/feedback operational store migration plan
