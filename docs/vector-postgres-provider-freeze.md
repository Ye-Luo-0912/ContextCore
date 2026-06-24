# Vector Postgres Provider Freeze

Generated: 2026-06-14

## Scope

DB5.F freezes the pgvector storage provider as preview/shadow/eval-only infrastructure. It does not bind `PostgresVectorIndexStore` as the formal `IVectorIndexStore`, does not enable formal vector retrieval, and does not connect vector output to scoring, `PackingPolicy`, or package output.

## Frozen Results

- DB5.0 diagnostics: pgvector extension available, schema `cc-schema-v6`, provider smoke passed.
- DB5.1 FileSystem/Postgres parity: passed, `MismatchCount=0`, `OrderingMismatchCount=0`, `MetadataMismatchCount=0`.
- DB5.2 provider-scoped reindex: passed, `CandidateCount=158`, `IndexedEntryCountAfterApply=158`, metadata roundtrip mismatch `0`.
- DB5.3 query preview parity: passed, `TopKOverlapRate=100%`, ordering/projection mismatch `0`.
- DB5.4 shadow eval parity: passed, summary `ReadyForVectorPostgresFreeze`.
- A3: `RecallDelta=0`, `RiskAfterPolicy=0`, `FormalOutputChanged=0`.
- Extended: `RecallDelta=0`, `RiskAfterPolicy=0`, `FormalOutputChanged=0`.
- Projection mismatch: `0`.
- `UseForRuntime=false`.

## Freeze State

- `VectorPostgresProvider = ReadyForPreviewShadowStorage`
- `DefaultVectorStore = unchanged`
- `FormalRetrievalAllowed = false`
- Allowed: preview, shadow, eval only.
- Required: Vector V4 readiness gate before any formal retrieval integration.
- Forbidden: formal retrieval switch, formal `IVectorIndexStore` binding, `PackingPolicy` integration, or package output integration without V4 gate.

## Boundary

This freeze proves pgvector storage parity with the existing FileSystem vector preview/shadow path. It does not change `VectorRetrieval` readiness: vector retrieval remains `PreviewOnly / BlockedByA3Recall`.
