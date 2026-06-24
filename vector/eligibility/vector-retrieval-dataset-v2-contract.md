# Retrieval Dataset V2 Metadata Contract

- ContractVersion: `retrieval-dataset-v2`
- GeneratesFormalDataset: `False`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`
- Recommendation: `ReadyForDatasetV2Authoring`

## Corpus Item Required Fields
- itemId
- itemKind
- layer/sourceKind
- text
- sourceRefs
- evidenceRefs
- provenance.recordId
- provenance.sourceFingerprint
- lifecycle
- reviewStatus
- replacementState
- targetSection
- split

## Query Sample Required Fields
- sampleId
- queryText
- mode/intent
- mustHit
- mustNotHit
- split
- sourceRefs
- evidenceRefs
- provenance.recordId

## Lifecycle Rules
- normal_context requires lifecycle Active/Current/Stable and non-superseded replacementState.
- deprecated/historical/superseded items must not be expected in normal_context.
- reviewStatus must be compatible with lifecycle and targetSection.

## Target Section Rules
- normal_context is for active/current/stable data only.
- audit_context and historical_context are allowed for deprecated/historical review paths.
- diagnostics_only is allowed for evidence gaps and unsafe recovery candidates.

## Relation Evidence Rules
- replacement/deprecation/supersedes relations must have sourceRefs or evidenceRefs.
- mustHit repair candidates should carry relation evidence when lifecycle depends on graph state.
- relation review status must not contradict item lifecycle metadata.

## Split Isolation Rules
- corpus items and query samples must declare split.
- train/dev/test splits must not share sample ids.
- evaluation query text must not contain target item ids or label-only leakage.
