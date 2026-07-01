# Storage Boundary — Current State (V13)

## Scope

This document clarifies what data types are owned by FileSystem vs Database in the current ContextCore architecture. It is descriptive (not prescriptive) and serves as a reference for future storage boundary decisions.

## FileSystem Responsibility

The FileSystem is the **primary ownership layer** for:

- **Raw content & documents**: Original user/LLM/tool/web/file inputs stored as JSONL or JSON files
- **Manifests**: Ingestion manifests, rollback manifests, snapshot manifests
- **Artifacts**: Evaluation reports, gate reports, audit reports, shadow reports
- **Learning/eval data**: ranking-pairs.jsonl, hard-negatives.jsonl, router-intent-examples.jsonl, shadow-eval JSON
- **Pilot/audit archives**: V11-V12 pilot, wider-pilot, closeout, and audit artifacts
- **Configuration**: Context package policies (stored as files for versioning)
- **Runtime traces**: Retrieval trace records (JSONL)
- **Job queues**: Context job queue (file-backed)

FileSystem providers exist for ALL data types (see `src/ContextCore.Storage.FileSystem/Stores/`).

## Database (Postgres) Responsibility

The Database is the **primary ownership layer** for:

- **Vector index**: `VectorRecord` embeddings and `VectorIndex` entries (pgvector)
- **Relation/graph index**: `ContextRelation` records with graph traversal queries
- **Index lookups**: Fast access patterns for the above two index types

Database providers also exist for most data types (see `src/ContextCore.Storage.Postgres/Stores/`), but many are implemented for **completeness/testing** rather than as primary storage. These are marked as:

- **Future**: Not yet promoted to primary storage
- **Diagnostic**: Available for querying but FileSystem is authoritative

## InMemory Responsibility

InMemory storage is used for:

- **Testing**: Full provider matrix for integration tests
- **Caching**: Transient in-memory caches for hot paths

InMemory is never the authoritative source for any data type in production.

## Current Overlap

The following data types have both FileSystem and Postgres providers but FileSystem is authoritative:

| Data Type | FileSystem | Postgres | Postgres Status |
|---|---|---|---|
| `ContextItem` (context entries) | Primary | Available | Diagnostic |
| `ContextRelation` | Available | **Primary** | Graph index |
| `VectorRecord` | Primary | **Primary** | Vector index (pgvector) |
| `VectorIndex` | Primary | **Primary** | Vector index |
| `LearningFeedbackEvent` | Primary | Available | Diagnostic |
| `LearningFeedbackReview` | Primary | Available | Diagnostic |
| `Constraint` | Primary | Available | Diagnostic |
| `ContextPackagePolicy` | Primary | Available | Diagnostic |
| `ContextJobQueue` | Primary | Available | Diagnostic |
| `ContextIndex` | Primary | Available | Diagnostic |
| `RetrievalTrace` | Primary | Available | Diagnostic |
| `RouterIntentShadowTrace` | FileSystem only | N/A | — |
| `Memory` (working/short-term) | Primary | Available | Diagnostic |
| `RelationReview` | Available | Available | Diagnostic |
| `ContextPackageBuildTrace` | Primary | Available | Diagnostic |
| `CandidateReview` | FileSystem only | N/A | — |
| `ContextLearning` | FileSystem only | N/A | — |

## Simplified Target (V13+)

```
FileSystem owns: content, documents, manifests, artifacts, learning/eval data, traces, policies
Database owns:  vector index, graph/relation index
```

Database may hold additional data for query convenience, but FileSystem is the **authoritative source** for all non-index data types.

## Implementation Status

- All FileSystem providers are implemented and functional
- Postgres providers exist for completeness but are marked diagnostic (non-authoritative) for non-index data types
- Vector and Graph indexes in Postgres are primary (pgvector + graph queries)
