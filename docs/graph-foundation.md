# Graph Foundation

Phase G1 adds relation type taxonomy and read-only graph validation.
Phase G2 adds relation evidence, confidence, lifecycle, review status, and explain diagnostics.

This phase does not change retrieval scoring, relation expansion, `PackingPolicy`, planning, attention, vector behavior, LLM judge flow, or package output. Diagnostics are advisory only and never auto-repair graph data.

## Relation Type Taxonomy

`RelationTypeDefinition` describes:

- `Type`
- `IsDirectional`
- `InverseType`
- `DefaultWeight`
- `RequiresEvidence`
- `AuditOnly`
- `AllowsNormalExpansion`
- `AllowedSourceKinds`
- `AllowedTargetKinds`
- `Warnings`

First registry version includes:

- `contains`
- `references`
- `derived_from`
- `evidence_for`
- `supports`
- `depends_on`
- `requires`
- `blocks`
- `conflicts_with`
- `applies_to`
- `superseded_by`
- `replaces`
- `replaced_by`
- `same_as`
- `related_to`

`related_to` remains valid but is intentionally weak. Diagnostics can warn when generic `related_to` dominates the graph.

## Graph Validation

`RelationGraphValidationService` validates stored `ContextRelation` records against the taxonomy and known item stores.

Diagnostics include:

- `UnknownRelationType`
- `MissingInverseRelation`
- `BrokenSource`
- `BrokenTarget`
- `MissingEvidence`
- `InvalidDirection`
- `InvalidSourceKind`
- `InvalidTargetKind`
- `DuplicateRelation`
- `ConflictingRelation`
- `SupersedeCycle`
- `WeakRelatedToOveruse`
- `AuditOnlyRelationInNormalPath`
- `LowConfidence`
- `UnreviewedHighImpactRelation`
- `RejectedRelationStillActive`
- `DeprecatedRelationUsedInNormalPath`
- `CandidateRelationUsedInNormalPath`
- `RelationConfidenceMissing`
- `RelationEvidenceBroken`
- `RelationLifecycleMismatch`

Item existence is resolved from:

- `IContextStore`
- `IMemoryStore`
- `IConstraintStore`
- `IGlobalContextStore`

## Relation Metadata Standard

G2 standardizes relation metadata for reviewed or generated relations:

- `evidenceRefs`
- `sourceRefs`
- `sourceOperationId`
- `sourceItemId`
- `createdBy`
- `createdFrom`
- `confidence`
- `confidenceReason`
- `lifecycle`
- `reviewStatus`
- `policyVersion`

`RelationEvidence` is a read-only DTO used by explain and diagnostics. It records `EvidenceId`, `RelationId`, `SourceRefs`, `EvidenceRefs`, `SourceOperationId`, `SourceItemId`, `EvidenceText`, `EvidenceKind`, `CreatedAt`, and `Metadata`.

Confidence defaults:

- deterministic system relation: `1.0`
- stable lifecycle review relation: `1.0`
- manual reviewed relation: `0.9` to `1.0`
- rule-generated relation: `0.6` to `0.8`
- LLM extracted relation: not integrated

G2 does not feed these values into retrieval scoring or relation expansion. They are surfaced only in diagnostics and explain views.

## S3 Replacement Validation

Stable Memory Governance S3 replacement relations get special validation:

- `superseded_by` must have a `replaces` inverse.
- replacement target must exist.
- replacement target must not be rejected / deprecated / superseded.
- replacement graph must not form a cycle.
- lifecycle review replacement relations include `confidence=1.0`, `confidenceReason=stable_lifecycle_review`, `lifecycle=Active`, `reviewStatus=Reviewed`, `evidenceRefs`, `sourceRefs`, `sourceOperationId`, `sourceItemId`, `createdFrom`, `createdBy`, `policyVersion`, and `reviewId`.

This complements, but does not replace, the stable replacement-chain diagnostics in `docs/stable-memory-governance.md`.

## Service API

Read-only endpoints:

- `GET /api/relations/types`
- `GET /api/relations/diagnostics?workspaceId=...&collectionId=...`
- `GET /api/relations/diagnostics/{itemId}?workspaceId=...&collectionId=...`
- `GET /api/relations/{relationId}/explain?workspaceId=...&collectionId=...`

Existing relation lookup endpoints are unchanged:

- `GET /api/relations/{workspaceId}/{collectionId}/{itemId}`
- `GET /api/relations/{itemId}?workspaceId=...&collectionId=...`

## Client Boundary

`ContextCoreClient` exposes:

- `GetRelationTypesAsync(...)`
- `GetRelationDiagnosticsAsync(...)`
- `GetItemRelationDiagnosticsAsync(...)`
- `ExplainRelationAsync(...)`

ControlRoom calls these through `ControlRoomService`; screens do not hand-build Service URLs.

## ControlRoom

Service Relations page now shows:

- relation type definitions
- global relation diagnostics
- item incoming / outgoing relations when an item id is entered
- item relation diagnostics
- relation explain with `E <relationId>`

The page is read-only and does not perform graph repair.

## Regression Gate

G1/G2 are read-only for graph diagnostics and explain, so the P15 gate remains:

- A3 50 remains `100%`
- Extended 113 remains `100%`
- `MustNotHitViolationCount=0`
- `LifecycleViolationCount=0`
- `HardConstraintMissingCount=0`
