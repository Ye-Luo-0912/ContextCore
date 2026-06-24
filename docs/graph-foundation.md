# Graph Foundation

Phase G1 adds relation type taxonomy and read-only graph validation.
Phase G2 adds relation evidence, confidence, lifecycle, review status, and explain diagnostics.
Phase G3 adds relation manual review / lifecycle operations and review history.

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
- `RejectedRelationHasActiveInverse`
- `DeprecatedRelationUsedByActiveChain`
- `NeedsEvidenceHighImpactRelation`
- `ReviewedRelationMissingReviewer`
- `ConfidenceChangedWithoutReview`
- `RelationReviewHistoryMissing`

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

## Relation Review / Lifecycle Operations

G3 introduces manual relation review without changing retrieval, relation expansion, `PackingPolicy`, planning, attention, or package output.

DTOs:

- `RelationReviewRequest`
- `RelationReviewResult`
- `RelationReviewRecord`

Supported actions:

- `Review`: sets `reviewStatus=Reviewed`
- `Reject`: sets `lifecycle=Rejected`, `reviewStatus=Rejected`
- `Deprecate`: sets `lifecycle=Deprecated`
- `MarkNeedsEvidence`: sets `reviewStatus=NeedsEvidence`

`RestoreActive` is intentionally deferred.

Validation before write:

- relation must exist
- relation type must be known
- source and target items must exist
- transition must be legal
- high-impact relation operations require a reason
- `superseded_by` / `replaces` inverse mismatch is diagnosed, not auto-repaired

Review operations update relation metadata with:

- `lifecycle`
- `reviewStatus`
- `lastReviewId`
- `reviewId`
- `lastReviewAction`
- `lastReviewedAt`
- `reviewedAt`
- `lastReviewer`
- `reviewer`
- `reviewReason`
- `operationId`
- `updatedFrom=relation_review`

Review history is stored through `IRelationReviewStore` with FileSystem and InMemory implementations. Postgres does not register the write path in this phase and returns a structured misconfigured response.

## G4 Relation Expansion Governance Preview

G4 adds graph-aware relation expansion governance as a preview / shadow layer only. It does not change formal retrieval, the existing relation expansion executor, `PackingPolicy`, planning, attention, or package output.

New profile DTO:

- `RelationExpansionProfile`

Default profiles:

- `normal-v1`
- `audit-v1`
- `conflict-v1`
- `current-task-v1`

`RelationExpansionProfileRegistry` exposes these default profiles. `RelationExpansionPolicyValidator` evaluates preview edges against:

- unknown relation type
- blocked relation type
- relation type not allowed by the profile
- confidence below `MinConfidence`
- missing evidence when required
- invalid lifecycle for candidate / deprecated / rejected relations
- audit-only relation in a normal profile
- fanout cap
- depth cap

The relation expansion preview service accepts `itemId + profileId` and returns:

- accepted relations
- blocked relations
- block reasons
- warnings

The preview walks relation out-edges for diagnostics only. It does not reorder candidates, mutate the selected set, write relation data, or call the formal retrieval/package path.

Shadow report:

- `eval/relation-expansion-profile-shadow-report.json`
- `eval/relation-expansion-profile-shadow-report.md`

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
- `GET /api/relations/expansion/profiles`
- `POST /api/relations/expansion/preview`
- `GET /api/relations/diagnostics?workspaceId=...&collectionId=...`
- `GET /api/relations/diagnostics/{itemId}?workspaceId=...&collectionId=...`
- `GET /api/relations/{relationId}/explain?workspaceId=...&collectionId=...`

Review endpoints:

- `POST /api/relations/{relationId}/review`
- `POST /api/relations/{relationId}/reject`
- `POST /api/relations/{relationId}/deprecate`
- `POST /api/relations/{relationId}/needs-evidence`
- `GET /api/relations/{relationId}/reviews`

Existing relation lookup endpoints are unchanged:

- `GET /api/relations/{workspaceId}/{collectionId}/{itemId}`
- `GET /api/relations/{itemId}?workspaceId=...&collectionId=...`

## Client Boundary

`ContextCoreClient` exposes:

- `GetRelationTypesAsync(...)`
- `GetRelationExpansionProfilesAsync(...)`
- `PreviewRelationExpansionAsync(...)`
- `GetRelationDiagnosticsAsync(...)`
- `GetItemRelationDiagnosticsAsync(...)`
- `ExplainRelationAsync(...)`
- `ReviewRelationAsync(...)`
- `RejectRelationAsync(...)`
- `DeprecateRelationAsync(...)`
- `MarkRelationNeedsEvidenceAsync(...)`
- `GetRelationReviewsAsync(...)`

ControlRoom calls these through `ControlRoomService`; screens do not hand-build Service URLs.

## ControlRoom

Service Relations page now shows:

- relation type definitions
- global relation diagnostics
- item incoming / outgoing relations when an item id is entered
- item relation diagnostics
- relation explain with `E <relationId>`
- relation expansion profiles with `P`
- relation expansion preview with `X` alone
- relation review with `V <relationId>`
- relation reject with `R <relationId>`
- relation deprecate with `X <relationId>`
- relation needs-evidence with `N <relationId>`
- relation review history with `H <relationId>`

Review operations first show relation explain and require typing `YES`. The page does not perform graph repair.

## G4 Validation

G4 generated:

- `eval/relation-expansion-profile-shadow-report.json`
- `eval/relation-expansion-profile-shadow-report.md`

Shadow summary:

- profiles: `4`
- profile/sample combinations: `12`
- accepted relations: `26`
- blocked relations: `40`

Validation:

- `dotnet build`: passed, `0 warning / 0 error`
- `dotnet test`: passed, `584 passed / 0 failed`
- `scripts/eval-gate-p15.ps1`: passed

## G5 Relation Expansion Shadow Eval

G5 evaluates relation expansion profiles against the existing eval samples. It is still shadow-only:

- formal retrieval output is not changed
- formal relation expansion executor is not changed
- `PackingPolicy` is not changed
- package sections and selected set are not changed

New runner / CLI:

- `RelationExpansionShadowEvalRunner`
- `eval relation-expansion-shadow-eval`

Generated reports:

- `eval/relation-expansion-shadow-eval-a3.json`
- `eval/relation-expansion-shadow-eval-extended.json`
- `eval/relation-expansion-shadow-eval.md`

Sample-level output includes:

- sample id, mode, detected intent, profile id
- seed items from the formal selected output
- expanded / accepted / blocked relations
- would-add candidates
- would-add must-hit / must-not-hit / lifecycle-risk candidates
- blocked reasons
- fanout / depth trim counts
- recommendation

Profile-level output includes:

- samples
- accepted / blocked relations
- would-add candidates
- must-hit gain
- must-not-hit risk
- lifecycle risk
- blocked-by-type / lifecycle / confidence / missing-evidence
- fanout / depth trim counts
- recommendation

Current G5 report result:

- A3: `50` eval samples, `200` profile/sample rows, `FormalOutputChanged=0`, `SelectedSetChanged=0`
- Extended: `113` eval samples, `452` profile/sample rows, `FormalOutputChanged=0`, `SelectedSetChanged=0`
- Before G5.1, all current profile recommendations were `NeedsPolicyTuning`
- The original blockers were legacy eval relation type mismatch (`supersedes`) and missing relation evidence in eval corpus fixtures.

## G5.1 Relation Corpus Normalization And Evidence Backfill

G5.1 normalizes eval relation corpus types and backfills deterministic fixture metadata for shadow-only analysis. It does not rewrite corpus files, does not write runtime `IRelationStore`, and does not affect formal retrieval/package output.

Added components:

- `RelationTypeNormalizer`
- `RelationCorpusHygieneReport`
- `RelationCorpusHygieneReportBuilder`
- `eval relation-corpus-hygiene`

Legacy type normalization:

- `supersedes` -> `replaces`
- `is_superseded_by` -> `superseded_by`
- `replacedBy` -> `replaced_by`
- `dependsOn` -> `depends_on`
- `evidenceFor` -> `evidence_for`

Generated hygiene reports:

- `eval/relation-corpus-hygiene-report.json`
- `eval/relation-corpus-hygiene-report.md`

Current hygiene result:

- Corpus files: `11`
- Relations: `21`
- Legacy relation types: `11`
- Unknown relation types: `0`
- Missing evidence relations: `21`
- Missing lifecycle relations: `21`
- Missing review status relations: `21`
- Backfill candidates: `21`

Shadow backfill behavior:

- deterministic / eval fixture relations receive `evidenceRefs`, `sourceRefs`, `sourceOperationId`, `createdFrom=relation_corpus_fixture_backfill`, `confidence`, `confidenceReason`, `lifecycle=Active`, `reviewStatus=Reviewed`, and `policyVersion=graph-foundation-g5.1`
- relations that cannot be evidenced are not given fabricated evidence; they remain `NeedsEvidence` / `Candidate` or keep `MissingEvidence` diagnostics

Updated shadow eval result after G5.1:

- A3: `FormalOutputChanged=0`, `SelectedSetChanged=0`, `BlockedByMissingEvidence=0`
- Extended: `FormalOutputChanged=0`, `SelectedSetChanged=0`, `BlockedByMissingEvidence=0`
- `normal-v1` no longer degrades due legacy type or missing metadata; it now surfaces `BlockedByRisk` because normalized `replaces` edges would add deprecated / old target items
- `current-task-v1` no longer degrades due legacy type or missing metadata; G5.2 further moves remaining replacement / historical target blocks into lifecycle-safe traversal policy

## G5.2 Replacement Direction And Lifecycle-safe Traversal

G5.2 adds relation traversal governance for replacement-like edges. It remains preview / shadow only:

- formal retrieval output is not changed
- formal relation expansion executor is not changed
- `PackingPolicy` is not changed
- package sections and selected set are not changed

New DTO / policy fields:

- `RelationTraversalPolicy`
- `RelationExpansionProfile.TraversalPolicies`
- preview relation fields: `traversalDirection`, `targetLifecycle`, `targetSection`

Default replacement traversal policy:

- `normal-v1`
  - allows `superseded_by` / `replaced_by` from old item toward latest item
  - blocks `replaces` traversal from new item back to old item
  - blocks deprecated / superseded / historical / rejected targets
- `current-task-v1`
  - allows only toward-latest replacement traversal
  - blocks toward-historical traversal
  - blocks historical targets
- `audit-v1`
  - allows toward-latest and toward-historical traversal
  - places historical targets in the `audit/historical` target section
- `conflict-v1`
  - allows both replacement directions when evidence and confidence pass
  - places accepted replacement evidence in the `conflict_evidence` target section

New validator reasons / counters:

- `BackwardReplacementTraversalBlocked`
- `DeprecatedTargetBlocked`
- `HistoricalTargetBlocked`
- `AuditOnlyHistoricalTraversal`
- `ReplacementTargetInactive`
- `ReplacementTargetRejected`
- `ReplacementTargetMissing`
- `AllowedTowardLatest`
- `BlockedTowardHistorical`
- `HistoricalAllowedOnlyInAudit`

Updated shadow eval result after G5.2:

- A3: `FormalOutputChanged=0`, `SelectedSetChanged=0`, `BlockedByMissingEvidence=0`
- Extended: `FormalOutputChanged=0`, `SelectedSetChanged=0`, `BlockedByMissingEvidence=0`
- `normal-v1`: recommendation `KeepPreviewOnly`, `MustNotHitRisk=0`, `LifecycleRisk=0`
- `current-task-v1`: recommendation `KeepPreviewOnly`, `MustNotHitRisk=0`, `LifecycleRisk=0`
- `audit-v1` and `conflict-v1`: recommendation remains `BlockedByRisk` because they intentionally expose historical / conflict-direction shadow risks for review

## G5.3 Section-aware Graph Expansion Risk Routing

G5.3 separates graph expansion target section from normal selected context risk. It is still preview / shadow only:

- formal retrieval output is not changed
- formal relation expansion executor is not changed
- `PackingPolicy` is not changed
- package output and selected set are not changed

Target sections:

- `normal_context`
- `working_context`
- `stable_context`
- `historical_context`
- `audit_context`
- `conflict_evidence`
- `diagnostics_only`
- `excluded`

Preview relation output now includes:

- `targetSection`
- `sectionReason`
- `riskIfNormalSelected`
- `riskAfterSectionRouting`

Profile routing:

- `audit-v1`
  - routes deprecated / historical targets to `audit_context`
  - does not count correctly routed historical targets as normal must-not-hit / lifecycle risk
  - reports `BlockedByWrongSectionRisk` if an audit-risk target is routed to `normal_context`
- `conflict-v1`
  - routes `conflicts_with`, `superseded_by`, `replaces`, and `replaced_by` targets to `conflict_evidence`
  - keeps evidence / confidence failures blocked
  - reports `BlockedByWrongSectionRisk` if conflict evidence is routed to `normal_context`
- `normal-v1` and `current-task-v1`
  - continue to block toward-historical replacement traversal
  - continue to block deprecated / historical targets
  - continue to report backward replacement traversal as blocked

Shadow report additions:

- accepted relation counts by section
- `RiskIfNormalSelected`
- `RiskAfterSectionRouting`
- `HistoricalAuditExpansion`
- `ConflictEvidenceExpansion`
- `WrongSectionRisk`
- recommendations `ReadyForAuditShadow`, `ReadyForConflictShadow`, `ReadyForSectionAwareShadow`, and `BlockedByWrongSectionRisk`

Updated shadow eval result after G5.3:

- A3: `FormalOutputChanged=0`, `SelectedSetChanged=0`
- Extended: `FormalOutputChanged=0`, `SelectedSetChanged=0`
- `audit-v1`: recommendation `ReadyForAuditShadow`, `RiskIfNormalSelected>0`, `RiskAfterSectionRouting=0`
- `conflict-v1`: recommendation `ReadyForConflictShadow`, `RiskIfNormalSelected>0`, `RiskAfterSectionRouting=0`
- `normal-v1` / `current-task-v1`: recommendation `KeepPreviewOnly`, `MustNotHitRisk=0`, `LifecycleRisk=0`

## G6 Relation Expansion Shadow in Retrieval Trace

G6 attaches the section-aware graph expansion shadow path to retrieval/package trace collection only. It does not change formal retrieval, selected set, candidate order, `PackingPolicy`, or package sections.

New runtime option:

- `Graph:ExpansionShadow:Enabled=false`
- `Graph:ExpansionShadow:TraceCollectionEnabled=false`
- `Graph:ExpansionShadow:Profiles=["audit-v1","conflict-v1"]`
- `Graph:ExpansionShadow:MaxRelationsPerTrace=50`

Compatibility note:

- `Learning:GraphExpansionShadow` is still read when `Graph:ExpansionShadow` is absent.

Trace fields:

- `graphExpansionShadowEnabled`
- `graphExpansionProfiles`
- `acceptedRelations`
- `blockedRelations`
- `targetSections`
- `riskIfNormal`
- `riskAfterRouting`
- `historicalAuditCount`
- `conflictEvidenceCount`
- `wrongSectionRisk`

Export and quality report:

- `GET /api/learning/graph-expansion-shadow/traces`
- JSONL export via `format=jsonl`
- `eval graph-expansion-shadow-trace-quality`
- `learning/graph-shadow/graph-expansion-shadow-trace-quality-report.json`
- `learning/graph-shadow/graph-expansion-shadow-trace-quality-report.md`

Quality report metrics:

- `TraceCount`
- `AcceptedRelationCount`
- `BlockedRelationCount`
- `AuditContextCount`
- `ConflictEvidenceCount`
- `RiskAfterRoutingCount`
- `WrongSectionRiskCount`
- `MustNotHitRiskCount`
- `LifecycleRiskCount`
- `MissingEvidenceCount`
- `TopRelationTypes`
- `TopBlockedReasons`
- `Recommendation`

Recommendations:

- `NeedsMoreRealTraces`
- `ReadyForAuditShadowOnly`
- `ReadyForConflictShadowOnly`
- `ReadyForGuardedOptIn`
- `BlockedByRisk`

## G6.1 Graph Shadow Trace Collection Runbook

G6.1 adds operational collection guidance for real graph expansion shadow traces. It still does not enable graph opt-in and does not change formal retrieval, formal relation expansion, `PackingPolicy`, selected set, order, or package output.

New runbook and script:

- `docs/graph-shadow-trace-collection-runbook.md`
- `scripts/collect-graph-expansion-shadow-traces.ps1`

The collection script:

- defaults to dry-run and only prints the sampling plan
- requires `-Execute` before calling a running `ContextCore.Service`
- checks `/api/status` and `/api/health/ready`
- executes fixed retrieval / query / package sampling calls
- exports `/api/learning/graph-expansion-shadow/traces?format=jsonl`
- runs `eval graph-expansion-shadow-trace-quality`
- writes outputs under `learning/graph-shadow`

Sampling scenarios cover at least 30 distinct scenario intents:

- Chat version conflict / deprecated preference / audit old topic / overwritten style / old-session scope / long-term preference conflict
- Project deprecated design / superseded pool / old storage choice / migration conflict / retired policy / previous release plan
- Novel old plot / weapon v1-v2 conflict / world rule conflict / character-state retcon / superseded location rule / foreshadowing conflict
- Automation old backup strategy / conflict recovery config / dead-letter policy conflict / superseded retry limit / old credential rotation / failed-step history
- Coding deprecated interface / old timeout config / obsolete API contract / test policy conflict / legacy build path / deprecated schema field

Sampling integrity requirements:

- `TraceCount >= 30` must come from distinct operation ids and distinct scenario intents.
- Re-running the same query or duplicating easy fixtures can validate trace plumbing only; it must not be counted as G7 readiness.
- Samples should include audit/historical routing, conflict evidence routing, and explainable blocked relations.
- If the corpus cannot produce enough diverse traces, keep `NeedsMoreRealTraces` instead of inflating the pass rate.
- Runtime graph shadow traces now include `traceSignature`. Repeated graph shadow payloads in the same workspace / collection are suppressed with `duplicateSuppressed=true`, and export / quality reporting deduplicates by signature so repeated sampling cannot waste graph-shadow storage or inflate readiness metrics.

G7 readiness gate:

- `TraceCount >= 30`
- `AcceptedRelationCount > 0`
- `AuditContextCount > 0` or `ConflictEvidenceCount > 0`
- `RiskAfterRoutingCount = 0`
- `WrongSectionRiskCount = 0`
- `MustNotHitRiskCount = 0`
- `LifecycleRiskCount = 0`
- `MissingEvidenceCount = 0`

## G7 Audit / Conflict Guarded Opt-in

G7 adds an explicit guarded opt-in path for graph expansion auxiliary sections. It remains disabled by default and does not enable `normal-v1` or `current-task-v1`.

Configuration:

- `Graph:ExpansionApply:Mode`: `Off`, `Shadow`, or `ApplyGuarded`
- `Graph:ExpansionApply:ApplyMode`: `ProfileScoped`
- `Graph:ExpansionApply:OptInProfiles`: empty by default; guarded apply only accepts `audit-v1` and `conflict-v1`
- `Graph:ExpansionApply:AllowedTargetSections`: `audit_context`, `conflict_evidence`, `historical_context`, `diagnostics_only`
- `Graph:ExpansionApply:DisallowNormalContextInjection`: `true` by default
- `Graph:ExpansionApply:FallbackOnRisk`: `true` by default
- `Graph:ExpansionApply:MaxAddedItemsPerPackage`: default `20`
- `Graph:ExpansionApply:EmitComparisonTrace`: default `true`

Guarded apply policy:

- `audit-v1` may append routed audit / historical graph items outside normal context.
- `conflict-v1` may append routed conflict evidence graph items outside normal context.
- `normal_context` injection is rejected.
- `normal-v1` and `current-task-v1` are not valid apply profiles.
- `riskAfterRouting`, `wrongSection`, `mustNotHit`, `lifecycle`, or `missingEvidence` risk causes fallback when `FallbackOnRisk=true`.
- Added graph items are deduplicated by `targetSection + itemId`; duplicate graph payloads are not used to inflate readiness or waste package/trace storage.

Package assembly behavior:

- The normal selected set is not changed.
- `RetrievalPackingPolicy` is not changed.
- Graph contributions are appended only as auxiliary package sections.
- Added section content is marked with `source=graph_expansion_guarded`.
- Package metadata records `graphExpansionMode`, `graphExpansionApplied`, `graphExpansionProfiles`, `graphExpansionAddedItems`, `graphExpansionTargetSections`, `graphExpansionFallbackUsed`, `graphExpansionFallbackReason`, and `graphExpansionRiskChecks`.

New eval command:

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval graph-expansion-optin-comparison
```

Outputs:

- `eval/graph-expansion-optin-comparison-a3.json`
- `eval/graph-expansion-optin-comparison-extended.json`
- `eval/graph-expansion-optin-comparison.md`

The comparison report tracks normal selected-set changes separately from auxiliary graph section changes.

## G7.1 Guarded Opt-in Warning Classification & Freeze Gate

G7.1 classifies `graph-expansion-optin-comparison` warning deltas and adds a guarded opt-in freeze gate. It does not expand graph expansion scope and does not enable `normal-v1` / `current-task-v1`.

New warning kinds:

- `AuxiliaryGraphSectionAdded`
- `ExpectedAuditContextAdded`
- `ExpectedConflictEvidenceAdded`
- `GraphContributionDeduplicated`
- `UnexpectedPackageWarningDelta`
- `NormalSelectedSetChanged`
- `DisallowedNormalContextInjection`
- `RiskFallbackTriggered`
- `MissingEvidenceDetected`
- `LifecycleRiskDetected`
- `WrongSectionRiskDetected`

Expected warning delta:

- legal `audit_context` additions are expected
- legal `conflict_evidence` additions are expected
- legal `historical_context` / `diagnostics_only` additions are expected
- `normal_context` additions are never expected

The comparison report now includes:

- `WarningDelta`
- `ExpectedWarningDelta`
- `UnexpectedWarningDelta`
- `WarningDeltaByKind`
- `AuxiliaryGraphSectionChanged`
- `NormalSelectedSetChanged`
- `DisallowedNormalContextInjection`
- `GuardStatus`

Freeze gate command:

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval graph-expansion-guarded-optin-gate
```

Gate outputs:

- `eval/graph-expansion-guarded-optin-gate.json`
- `eval/graph-expansion-guarded-optin-gate.md`
- refreshed `eval/graph-expansion-optin-comparison-a3.json`
- refreshed `eval/graph-expansion-optin-comparison-extended.json`
- refreshed `eval/graph-expansion-optin-comparison.md`

Gate pass conditions:

- `NormalSelectedSetChanged = 0`
- `RiskAfterRoutingCount = 0`
- `WrongSectionRiskCount = 0`
- `MustNotHitRiskCount = 0`
- `LifecycleRiskCount = 0`
- `MissingEvidenceCount = 0`
- `UnexpectedWarningDelta = 0`
- `DisallowedNormalContextInjection = 0`

ControlRoom Package Preview / Retrieval Debug now separates expected graph section delta from unexpected warning delta.

## Regression Gate

G1/G2 are read-only for graph diagnostics and explain. G3 writes relation governance metadata and review history only. G4 adds preview / shadow expansion governance only. G5 adds eval-sample shadow evaluation only. G5.1 adds corpus hygiene reporting plus shadow-only normalization/backfill only. G5.2 adds replacement direction and lifecycle-safe traversal policy only. G5.3 adds section-aware shadow risk routing only. G6 adds optional trace collection only. G6.1 adds runbook/script-based collection guidance only. G7 adds explicit guarded auxiliary section opt-in only for audit/conflict profiles and still protects the normal selected set. G7.1 adds warning classification and a freeze gate only. The P15 gate remains:

- A3 50 remains `100%`
- Extended 113 remains `100%`
- `MustNotHitViolationCount=0`
- `LifecycleViolationCount=0`
- `HardConstraintMissingCount=0`
- `eval graph-expansion-guarded-optin-gate` must pass before any future guarded opt-in expansion.
