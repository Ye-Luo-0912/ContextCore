# Postgres Vector Shadow Eval

- DatasetName: `A3`
- Recommendation: `ReadyForVectorPostgresFreeze`
- WorkspaceId: `eval-vector`
- CollectionId: `corpus`
- ProviderId: `deterministic-hash`
- ModelId: `deterministic-hash-v1`
- Dimension: `16`
- Normalized: `True`
- ProfileId: `normal-v1`
- TopK: `10`
- SampleCount: `50`
- QueryCount: `50`
- PgVectorCandidateCount: `500`
- FileSystemCandidateCount: `500`
- RecallAfterPolicy: `4.55%`
- MrrAfterPolicy: `0.0122`
- FileSystemRecallAfterPolicy: `4.55%`
- RecallDelta: `0`
- RiskAfterPolicy: `0`
- MustNotHitRiskAfterPolicy: `0.00%`
- LifecycleRiskAfterPolicy: `0.00%`
- FormalOutputChanged: `0`
- TopKOverlapRate: `100.00%`
- OrderingMismatchCount: `0`
- ScoreDeltaMax: `0.00000008`
- MetadataMismatchCount: `0`
- EligibilityMetadataMismatchCount: `0`
- RiskProjectionMismatchCount: `0`
- UseForRuntime: `False`

| Sample | PgVector | FileSystem | Overlap | Ordering | ScoreDeltaMax | MetadataMismatch | EligibilityMismatch | RiskMismatch |
|---|---:|---:|---:|---|---:|---:|---:|---:|
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
| coding-sample-001 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| coding-sample-002 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| coding-sample-003 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| coding-sample-004 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| coding-sample-005 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| coding-sample-006 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| coding-sample-007 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| coding-sample-008 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| coding-sample-009 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| coding-sample-010 | 10 | 10 | 10 | True | 0.00000007 | 0 | 0 | 0 |
| novel-sample-001 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| novel-sample-002 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| novel-sample-003 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| novel-sample-004 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| novel-sample-005 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| novel-sample-006 | 10 | 10 | 10 | True | 0.00000004 | 0 | 0 | 0 |
| novel-sample-007 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| novel-sample-008 | 10 | 10 | 10 | True | 0.00000002 | 0 | 0 | 0 |
| novel-sample-009 | 10 | 10 | 10 | True | 0.00000007 | 0 | 0 | 0 |
| novel-sample-010 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| project-sample-001 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| project-sample-002 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| project-sample-003 | 10 | 10 | 10 | True | 0.00000002 | 0 | 0 | 0 |
| project-sample-004 | 10 | 10 | 10 | True | 0.00000005 | 0 | 0 | 0 |
| project-sample-005 | 10 | 10 | 10 | True | 0.00000007 | 0 | 0 | 0 |
| project-sample-006 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| project-sample-007 | 10 | 10 | 10 | True | 0.00000008 | 0 | 0 | 0 |
| project-sample-008 | 10 | 10 | 10 | True | 0.00000006 | 0 | 0 | 0 |
| project-sample-009 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
| project-sample-010 | 10 | 10 | 10 | True | 0.00000003 | 0 | 0 | 0 |
