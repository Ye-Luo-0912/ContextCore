# Postgres Vector Shadow Eval

- DatasetName: `Extended`
- Recommendation: `ReadyForVectorPostgresFreeze`
- WorkspaceId: `eval-vector`
- CollectionId: `corpus`
- ProviderId: `deterministic-hash`
- ModelId: `deterministic-hash-v1`
- Dimension: `16`
- Normalized: `True`
- ProfileId: `normal-v1`
- TopK: `10`
- SampleCount: `113`
- QueryCount: `113`
- PgVectorCandidateCount: `1130`
- FileSystemCandidateCount: `1130`
- RecallAfterPolicy: `3.12%`
- MrrAfterPolicy: `0.0081`
- FileSystemRecallAfterPolicy: `3.12%`
- RecallDelta: `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0.00%`
- LifecycleRiskAfterPolicy: `0.00%`
- FormalOutputChanged: `0`
- TopKOverlapRate: `100.00%`
- OrderingMismatchCount: `0`
- ScoreDeltaMax: `0.00000009`
- MetadataMismatchCount: `0`
- EligibilityMetadataMismatchCount: `0`
- RiskProjectionMismatchCount: `0`
- UseForRuntime: `False`

| Sample | PgVector | FileSystem | Overlap | Ordering | ScoreDeltaMax | MetadataMismatch | EligibilityMismatch | RiskMismatch |
|---|---:|---:|---:|---|---:|---:|---:|---:|
| automation-20260529-001 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| automation-20260529-002 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| automation-20260529-003 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| automation-20260529-004 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| automation-20260529-005 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| automation-20260529-006 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| automation-20260529-007 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| automation-20260529-008 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| automation-20260529-009 | 10 | 10 | 10 | True | 0.00000007 | 0 | 0 | 0 |
| automation-20260529-010 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| automation-sample-001 | 10 | 10 | 10 | True | 0.00000002 | 0 | 0 | 0 |
| automation-sample-002 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| automation-sample-003 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| automation-sample-004 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| automation-sample-005 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| automation-sample-006 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| automation-sample-007 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| automation-sample-008 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| automation-sample-009 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| automation-sample-010 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-001 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| chat-20260529-002 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| chat-20260529-003 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-004 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-005 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-006 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-007 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-008 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-009 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| chat-20260529-010 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| chat-20260529-011 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| chat-20260529-012 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| chat-20260529-013 | 10 | 10 | 10 | True | 0.00000008 | 0 | 0 | 0 |
| chat-20260529-014 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| chat-20260529-015 | 10 | 10 | 10 | True | 0.00000009 | 0 | 0 | 0 |
| chat-20260529-016 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| chat-20260529-017 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| chat-20260529-018 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-019 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-20260529-020 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| chat-sample-001 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| chat-sample-002 | 10 | 10 | 10 | True | 0.00000007 | 0 | 0 | 0 |
| chat-sample-003 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-sample-004 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| chat-sample-005 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| chat-sample-006 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| chat-sample-007 | 10 | 10 | 10 | True | 0.00000008 | 0 | 0 | 0 |
| chat-sample-008 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| chat-sample-009 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| chat-sample-010 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
