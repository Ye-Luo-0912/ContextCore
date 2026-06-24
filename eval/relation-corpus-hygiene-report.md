# Relation Corpus Hygiene Report

Generated: 2026-06-08T12:40:58.5843032+00:00

## Summary

- Corpus files: `11`
- Relations: `21`
- Unknown relation types: `0`
- Legacy relation types: `11`
- Missing evidence relations: `21`
- Missing confidence relations: `0`
- Missing lifecycle relations: `21`
- Missing review status relations: `21`
- Migration candidates: `11`
- Backfill candidates: `21`

## Legacy Relation Types

| Legacy | Normalized | Count |
|---|---|---:|
| supersedes | replaces | 11 |

## Unknown Relation Types

| Type | Count |
|---|---:|
| - | 0 |

## Missing Evidence Relations

| Category | Relation | Type | Normalized | Reason | Suggestion |
|---|---|---|---|---|---|
| automation | rel:auto-backup-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| automation | rel:auto-retry-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| chat | rel:chat-drink-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| chat | rel:chat-location-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| chat | rel:chat-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| coding-mode | rel:coding-sig-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| coding-mode | rel:coding-timeout-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| novel | rel:novel-concept-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| novel | rel:novel-conflict | conflicts_with | conflicts_with | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| novel | rel:novel-weapon-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| project | rel:project-conflict-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| project | rel:project-pool-supersede | supersedes | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:arch-ci | related_to | related_to | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:audit-log-retention | related_to | related_to | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:embed-cfg-supersedes | replaces | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:hnsw-prereq | depends_on | depends_on | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:hybrid-supersedes | replaces | replaces | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:pipeline-cfg | related_to | related_to | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:sprint-retrieval-task | related_to | related_to | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:sprint-storage-check | related_to | related_to | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |
| retrieval | rel:task-sideeffect | related_to | related_to | missing evidence metadata | backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate |

## Backfill Candidates

| Category | Relation | Type | Missing Fields | Can Backfill Evidence | Policy |
|---|---|---|---|---|---|
| automation | rel:auto-retry-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| automation | rel:auto-backup-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| chat | rel:chat-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| chat | rel:chat-location-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| chat | rel:chat-drink-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| coding-mode | rel:coding-sig-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| coding-mode | rel:coding-timeout-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| novel | rel:novel-conflict | conflicts_with | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| novel | rel:novel-concept-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| novel | rel:novel-weapon-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| project | rel:project-conflict-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| project | rel:project-pool-supersede | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:hnsw-prereq | depends_on | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:task-sideeffect | related_to | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:pipeline-cfg | related_to | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:hybrid-supersedes | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:embed-cfg-supersedes | replaces | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:audit-log-retention | related_to | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:sprint-storage-check | related_to | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:sprint-retrieval-task | related_to | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
| retrieval | rel:arch-ci | related_to | evidenceRefs/sourceRefs, lifecycle, reviewStatus | True | relation_corpus_fixture_backfill |
