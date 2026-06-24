# Stable Memory Governance

Phase S3 adds relation-aware replacement workflow for long-term memory objects:

- StableMemory
- StableConstraint
- DecisionRecord
- GlobalMemory

This phase keeps the S2 lifecycle-only operator actions (`Deprecate`, `Supersede`, `Reject`) and upgrades `Supersede` into a relation-aware workflow. It does not edit memory content, auto-merge items, auto-promote candidates, change retrieval scoring, change `PackingPolicy`, change planning, change attention, enable vector behavior, call an LLM judge, change runtime scorer behavior, or add NamedPipe integration.

## Scope

Stable governance observes existing long-term objects from existing stores:

- `ContextMemoryItem` with `Layer=Stable`
- non-candidate `ContextConstraint` records
- decision records inferred from stable memory type / metadata
- `ContextGlobalItem` records
- replacement relations from `IRelationStore`
- provenance data exposed through `ContextProvenanceService`

Candidate-layer records remain under `docs/mid-term-memory-governance.md`.

S3 writes are limited to:

- stable lifecycle status / lifecycle metadata
- `supersededBy` on the old item
- `replaces` on the replacement item
- `StableLifecycleReviewRecord` history
- replacement relations:
  - `oldItem --superseded_by--> replacementItem`
  - `replacementItem --replaces--> oldItem`

Stable review does not modify the item content, summary, title, evidence text, retrieval score, package section, or selected set.

Replacement relation metadata contains:

- `source=stable_lifecycle_review`
- `reviewId`
- `reviewer`
- `reason`
- `createdAt`
- `confidence=1.0`
- `lifecycle=Active`

Phase G1 adds graph-level validation for these S3 replacement relations. The graph validator checks that `superseded_by` has a `replaces` inverse, the replacement target exists and is not inactive, and the replacement graph does not form a cycle. These checks are read-only diagnostics and do not change stable lifecycle state.

## DTOs

`StableMemorySnapshot` includes:

- `StableMemoryCount`
- `StableConstraintCount`
- `DecisionRecordCount`
- `GlobalMemoryCount`
- `ActiveCount`
- `SupersededCount`
- `DeprecatedCount`
- `RejectedCount`
- `MissingProvenanceCount`
- `DuplicateCandidateCount`
- `ConflictCandidateCount`
- `WeakEvidenceCount`
- `RecentStableItems`
- `Warnings`

`StableMemoryDiagnostic` supports:

- `DuplicateStableMemory`
- `PossibleConflict`
- `MissingProvenance`
- `MissingEvidenceRefs`
- `StableWithoutReviewSource`
- `StableConstraintWithoutScope`
- `DecisionRecordWithoutSource`
- `DeprecatedStillActive`
- `SupersededWithoutReplacement`
- `GlobalMemoryScopeRisk`
- `SupersededWithoutRelation`
- `MetadataRelationMismatch`
- `BrokenReplacementLink`
- `ReplacementTargetMissing`
- `ReplacementTargetInactive`
- `ReplacementCycle`
- `MultipleActiveReplacements`
- `ScopeMismatchInReplacement`

`StableMemoryExplanation` returns:

- normalized stable item record
- provenance response when available
- evidence refs
- item-local diagnostics
- warnings / missing provenance notes

`StableLifecycleReviewRequest` includes:

- `OperationId`
- `WorkspaceId`
- `CollectionId`
- `Reviewer`
- `Reason`
- `ReplacementItemId`
- `AllowDeprecatedSupersededDeprecation`
- `Metadata`

`StableLifecycleReviewResult` returns:

- action
- previous / new status
- previous / new lifecycle
- reviewer / reason / reviewedAt
- replacement item id
- updated stable item
- review record
- warnings / errors

`StableLifecycleReviewRecord` stores the audit trail for each lifecycle action.

`StableReplacementChainResponse` returns:

- `ItemId`
- `CurrentItem`
- `PreviousItems`
- `NextItems`
- `RootItem`
- `LatestItem`
- `Relations`
- `Warnings`

## Service

`StableMemoryGovernanceService` builds read-only views over:

- `IMemoryStore`
- `IConstraintStore`
- `IGlobalContextStore`
- `IRelationStore`
- `ContextProvenanceService`

It normalizes stable memory, stable constraints, decision records, and global memory into `StableMemoryRecord`.

Diagnostics are advisory only. They never trigger cleanup or state transitions.

`StableLifecycleReviewService` handles manual lifecycle review:

- validates the target stable item exists
- rejects illegal transitions
- requires `replacementItemId` for `Supersede`
- validates replacement exists and is not rejected / deprecated / superseded
- writes `supersededBy` to the old item and `replaces` to the replacement item
- writes bidirectional replacement relations to `IRelationStore`
- writes replacement relation metadata required by Graph Foundation G2: `confidence=1.0`, `confidenceReason=stable_lifecycle_review`, `lifecycle=Active`, `reviewStatus=Reviewed`, `reviewId`, `sourceOperationId`, `sourceItemId`, `sourceRefs`, `evidenceRefs`, `createdBy`, `createdFrom`, and `policyVersion`
- records provenance warnings without blocking the review
- appends review history through `IStableLifecycleReviewStore`

FileSystem and InMemory providers support review history and replacement relations. Postgres still does not register the StableLifecycle review write path in S3; the API returns a structured misconfigured response for that provider before any metadata-only stable lifecycle write can occur.

Graph Foundation G5.1 adds `RelationTypeNormalizer` for eval corpus hygiene and shadow-only analysis. Stable lifecycle review continues to write canonical `superseded_by` / `replaces` relations directly with reviewed evidence metadata. G5.1 does not rewrite stable review relations or alter retrieval/package behavior.

Graph Foundation G5.2 adds replacement-direction traversal policy for preview / shadow relation expansion. Normal and current-task profiles follow replacement chains only toward latest active items and block `replaces` traversal from a replacement back to a deprecated / historical item. Audit and conflict profiles may inspect historical replacement targets in their own shadow sections. This governance does not change stable lifecycle records, formal retrieval, package output, or replacement-chain storage.

## Service API

Read-only endpoints:

- `GET /api/memory/stable/snapshot`
- `GET /api/memory/stable/diagnostics`
- `GET /api/memory/stable/{id}/explain`
- `GET /api/memory/stable/{id}/replacement-chain`
- `GET /api/memory/stable/{id}/reviews`

Lifecycle review endpoints:

- `POST /api/memory/stable/{id}/deprecate`
- `POST /api/memory/stable/{id}/supersede`
- `POST /api/memory/stable/{id}/reject`

All endpoints require `workspaceId`; `collectionId` is optional. `snapshot` also accepts `take`. `supersede` requires `replacementItemId`.

## Client Boundary

`ContextCoreClient` exposes:

- `GetStableMemorySnapshotAsync(...)`
- `GetStableMemoryDiagnosticsAsync(...)`
- `ExplainStableMemoryAsync(...)`
- `GetStableReplacementChainAsync(...)`
- `DeprecateStableMemoryAsync(...)`
- `SupersedeStableMemoryAsync(...)`
- `RejectStableMemoryAsync(...)`
- `GetStableMemoryReviewsAsync(...)`

ControlRoom calls these through `ControlRoomService`; screens do not hand-build Service URLs.

## ControlRoom

Service Mode adds a Stable Memory page:

- main dashboard shortcut: `36`
- shows snapshot counts
- shows recent stable items
- shows diagnostics
- supports detail by entering stable item id
- supports explain with `E <id>`
- supports provenance with `P <id>`
- supports replacement chain with `C <id>`
- supports deprecate with `X <id>`
- supports supersede with `S <id>`
- supports reject with `R <id>`
- supports review history with `H <id>`

Before lifecycle operations, the page shows detail / explain / diagnostics and requires entering `YES`. The page does not edit content, auto-merge, activate candidates, or change retrieval / planning / package output.

## Regression Gate

Because S3 only changes explicit lifecycle review state / relation records and does not touch retrieval/package logic, the P15 gate remains the regression check:

- A3 50 remains `100%`
- Extended 113 remains `100%`
- `MustNotHitViolationCount=0`
- `LifecycleViolationCount=0`
- `HardConstraintMissingCount=0`
