# Postgres Vector Query Preview

- Recommendation: `BlockedByRiskProjectionMismatch`
- WorkspaceId: `eval-vector`
- CollectionId: `corpus`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ModelId: `qwen3-embedding-0.6b`
- Dimension: `1024`
- Normalized: `True`
- ProfileId: `normal-v1`
- TopK: `10`
- QueryCount: `113`
- CandidateCount: `1130`
- PgVectorCandidateCount: `1130`
- FileSystemCandidateCount: `1130`
- TopKOverlapCount: `992`
- TopKOverlapRate: `87.79%`
- OrderingMismatchCount: `112`
- ScoreDeltaMax: `0.03943568`
- MetadataMismatchCount: `138`
- EligibilityMetadataMismatchCount: `138`
- RiskProjectionMismatchCount: `138`
- DimensionMismatchBlocked: `True`
- ProviderModelMismatchBlocked: `True`
- UseForRuntime: `False`

## Diagnostics

- UseForRuntime=false
- RetrievalProviderUnchanged
- FileSystemPreviewUsesTemporaryIndexOnly

## Sample Preview

| Sample | PgVector | FileSystem | Overlap | Ordering | ScoreDeltaMax | MetadataMismatch | EligibilityMismatch | RiskMismatch |
|---|---:|---:|---:|---|---:|---:|---:|---:|
| automation-20260529-001 | 10 | 10 | 9 | False | 0.01027458 | 1 | 1 | 1 |
| automation-20260529-002 | 10 | 10 | 9 | False | 0.01850497 | 1 | 1 | 1 |
| automation-20260529-003 | 10 | 10 | 10 | False | 0.0221089 | 0 | 0 | 0 |
| automation-20260529-004 | 10 | 10 | 9 | False | 0.01692463 | 1 | 1 | 1 |
| automation-20260529-005 | 10 | 10 | 9 | False | 0.03943568 | 1 | 1 | 1 |
| automation-20260529-006 | 10 | 10 | 10 | False | 0.01402174 | 0 | 0 | 0 |
| automation-20260529-007 | 10 | 10 | 10 | False | 0.01570001 | 0 | 0 | 0 |
| automation-20260529-008 | 10 | 10 | 9 | False | 0.01746389 | 1 | 1 | 1 |
| automation-20260529-009 | 10 | 10 | 8 | False | 0.01336038 | 2 | 2 | 2 |
| automation-20260529-010 | 10 | 10 | 9 | False | 0.0131287 | 1 | 1 | 1 |
| automation-sample-001 | 10 | 10 | 9 | False | 0.00934903 | 1 | 1 | 1 |
| automation-sample-002 | 10 | 10 | 9 | False | 0.01199593 | 1 | 1 | 1 |
| automation-sample-003 | 10 | 10 | 10 | False | 0.01459565 | 0 | 0 | 0 |
| automation-sample-004 | 10 | 10 | 10 | False | 0.01934094 | 0 | 0 | 0 |
| automation-sample-005 | 10 | 10 | 9 | False | 0.0138735 | 1 | 1 | 1 |
| automation-sample-006 | 10 | 10 | 8 | False | 0.01358172 | 2 | 2 | 2 |
| automation-sample-007 | 10 | 10 | 8 | False | 0.01192476 | 2 | 2 | 2 |
| automation-sample-008 | 10 | 10 | 8 | False | 0.01511496 | 2 | 2 | 2 |
| automation-sample-009 | 10 | 10 | 9 | False | 0.01831243 | 1 | 1 | 1 |
| automation-sample-010 | 10 | 10 | 8 | False | 0.01945801 | 2 | 2 | 2 |
| chat-20260529-001 | 10 | 10 | 8 | False | 0.01942347 | 2 | 2 | 2 |
| chat-20260529-002 | 10 | 10 | 7 | False | 0.01321102 | 3 | 3 | 3 |
| chat-20260529-003 | 10 | 10 | 10 | False | 0.01256008 | 0 | 0 | 0 |
| chat-20260529-004 | 10 | 10 | 9 | False | 0.01574243 | 1 | 1 | 1 |
| chat-20260529-005 | 10 | 10 | 8 | False | 0.02023157 | 2 | 2 | 2 |
| chat-20260529-006 | 10 | 10 | 9 | False | 0.01721342 | 1 | 1 | 1 |
| chat-20260529-007 | 10 | 10 | 9 | False | 0.02349115 | 1 | 1 | 1 |
| chat-20260529-008 | 10 | 10 | 7 | False | 0.00741085 | 3 | 3 | 3 |
| chat-20260529-009 | 10 | 10 | 9 | False | 0.02415627 | 1 | 1 | 1 |
| chat-20260529-010 | 10 | 10 | 9 | False | 0.0138709 | 1 | 1 | 1 |
| chat-20260529-011 | 10 | 10 | 8 | False | 0.01774388 | 2 | 2 | 2 |
| chat-20260529-012 | 10 | 10 | 6 | False | 0.00990152 | 4 | 4 | 4 |
| chat-20260529-013 | 10 | 10 | 7 | False | 0.02252542 | 3 | 3 | 3 |
| chat-20260529-014 | 10 | 10 | 8 | False | 0.01222633 | 2 | 2 | 2 |
| chat-20260529-015 | 10 | 10 | 8 | False | 0.01249058 | 2 | 2 | 2 |
| chat-20260529-016 | 10 | 10 | 8 | False | 0.02330091 | 2 | 2 | 2 |
| chat-20260529-017 | 10 | 10 | 8 | False | 0.01394711 | 2 | 2 | 2 |
| chat-20260529-018 | 10 | 10 | 8 | False | 0.01775774 | 2 | 2 | 2 |
| chat-20260529-019 | 10 | 10 | 8 | False | 0.01943056 | 2 | 2 | 2 |
| chat-20260529-020 | 10 | 10 | 9 | False | 0.02962714 | 1 | 1 | 1 |
| chat-sample-001 | 10 | 10 | 8 | False | 0.01824055 | 2 | 2 | 2 |
| chat-sample-002 | 10 | 10 | 9 | False | 0.02710372 | 1 | 1 | 1 |
| chat-sample-003 | 10 | 10 | 9 | False | 0.02745182 | 1 | 1 | 1 |
| chat-sample-004 | 10 | 10 | 8 | False | 0.01490581 | 2 | 2 | 2 |
| chat-sample-005 | 10 | 10 | 8 | False | 0.01196133 | 2 | 2 | 2 |
| chat-sample-006 | 10 | 10 | 9 | False | 0.03753072 | 1 | 1 | 1 |
| chat-sample-007 | 10 | 10 | 8 | False | 0.00992219 | 2 | 2 | 2 |
| chat-sample-008 | 10 | 10 | 8 | False | 0.0168019 | 2 | 2 | 2 |
| chat-sample-009 | 10 | 10 | 7 | False | 0.02487504 | 3 | 3 | 3 |
| chat-sample-010 | 10 | 10 | 9 | False | 0.02783924 | 1 | 1 | 1 |
