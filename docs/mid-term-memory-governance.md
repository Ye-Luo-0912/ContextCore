# Mid-Term Memory Governance

Phase M1 adds a read-only governance layer for candidate / medium-term memory.
Phase M2 adds manual review / cleanup operations for CandidateMemory.

It does not change retrieval scoring, `PackingPolicy`, planning, attention, vector behavior, LLM judge flow, or NamedPipe integration.

## Scope

Candidate Memory governance observes candidate-layer records from existing stores:

- `ContextMemoryItem` with `Status=Candidate`
- `ContextConstraint` with `Status=Candidate`
- candidate decisions identified from candidate memory type or metadata
- source metadata from promotion / stable review / constraint gap / learning stores

The governance layer does not rank, pack, retrieve, auto-promote, or write stable memory. Phase M2 only mutates candidate-layer status / lifecycle metadata through explicit human review operations.

## Aggregation Service

`CandidateMemorySnapshotService` builds a read-only view over existing stores:

- candidate memory records from `IMemoryStore`
- candidate constraints from `IConstraintStore`
- candidate decisions inferred from candidate memory kind / metadata
- source promotion candidates and review history
- source stable review candidates and review history
- source constraint gaps and review history
- feedback signals and draft learning cases
- candidate constraint review history
- candidate memory review history

The service normalizes those records into `CandidateMemoryRecord` so Service API, client, ControlRoom, and tests share one candidate-layer shape.

`CandidateMemoryReviewService` handles explicit human review actions:

- `MarkReadyForStableReview`
- `NeedsMoreEvidence`
- `Reject`
- `Expire`
- `Supersede`

It validates candidate existence, legal status transition, supersede target existence, and rejects stable / active targets. Missing provenance becomes a warning, not an automatic block.

## DTOs

`CandidateMemorySnapshot` includes:

- `CandidateMemoryCount`
- `CandidateConstraintCount`
- `CandidateDecisionCount`
- `PendingReviewCount`
- `AcceptedFromPromotionCount`
- `ExpiredCandidateCount`
- `DuplicateCandidateCount`
- `ConflictCandidateCount`
- `RecentCandidates`
- `Warnings`

`CandidateMemoryExplanation` returns:

- candidate record
- source promotion candidate
- source stable review candidate
- source constraint gap
- source feedback signal
- source learning case
- evidence refs
- promotion / stable / constraint gap / candidate constraint review history
- candidate memory review history
- provenance chain
- risk flags

`CandidateMemoryDiagnosticsReport` checks:

- duplicate candidate
- stale candidate
- candidate without evidence
- candidate with rejected source
- candidate conflicts with active stable memory
- candidate superseded by newer candidate

Diagnostic records include severity, candidate id, title, reason, evidence refs, source refs, and a suggested action. They are advisory only.

`CandidateMemoryReviewRequest` includes:

- `OperationId`
- `WorkspaceId`
- `CollectionId`
- `Reviewer`
- `Reason`
- `SupersedeTargetCandidateId`
- `Metadata`

`CandidateMemoryReviewResult` returns the action, status transition, review id, reviewer, reason, updated candidate record, warnings, and the persisted `CandidateMemoryReviewRecord`.

## Service API

Read-only endpoints:

- `GET /api/memory/candidates/snapshot`
- `GET /api/memory/candidates/diagnostics`
- `GET /api/memory/candidates/{id}`
- `GET /api/memory/candidates/{id}/explain`
- `GET /api/memory/candidates/{id}/reviews`

All endpoints use `workspaceId` and optional `collectionId` query parameters.

`snapshot` additionally accepts `take` for recent candidate count.

Manual review / cleanup endpoints:

- `POST /api/memory/candidates/{id}/ready-for-stable-review`
- `POST /api/memory/candidates/{id}/needs-more-evidence`
- `POST /api/memory/candidates/{id}/reject`
- `POST /api/memory/candidates/{id}/expire`
- `POST /api/memory/candidates/{id}/supersede`

These endpoints record review history. They do not create StableMemory, do not run stable review generation, and do not affect retrieval or package output.

## ControlRoom

Service Mode adds a Candidate Memory page:

- main dashboard shortcut: `35`
- shows snapshot counts
- shows recent candidates
- shows diagnostics
- supports detail by entering candidate id
- supports explain with `E <id>`
- supports `Ready <id>`
- supports `N <id>` / `Needs <id>`
- supports `Reject <id>`
- supports `Expire <id>` / `X <id>`
- supports `Supersede <id>` / `U <id>`
- supports `H <id>` review history

Every mutation first renders detail + explain and requires typing `YES`.

The page uses `ContextCoreClient` through `ControlRoomService`; it does not hand-build service URLs in the screen.

## Client Boundary

`ContextCoreClient` exposes:

- `GetCandidateMemorySnapshotAsync(...)`
- `GetCandidateMemoryAsync(...)`
- `ExplainCandidateMemoryAsync(...)`
- `GetCandidateMemoryDiagnosticsAsync(...)`
- `MarkCandidateMemoryReadyForStableReviewAsync(...)`
- `MarkCandidateMemoryNeedsMoreEvidenceAsync(...)`
- `RejectCandidateMemoryAsync(...)`
- `ExpireCandidateMemoryAsync(...)`
- `SupersedeCandidateMemoryAsync(...)`
- `GetCandidateMemoryReviewsAsync(...)`

ControlRoom uses these methods through `ControlRoomService`, keeping HTTP routing outside the screen implementation.

## Regression Gate

Because M2 does not touch retrieval/package/scoring behavior, the P15 gate remains the safety check:

- A3 50 remains `100%`
- Extended 113 remains `100%`
- `MustNotHitViolationCount=0`
- `LifecycleViolationCount=0`
- `HardConstraintMissingCount=0`
