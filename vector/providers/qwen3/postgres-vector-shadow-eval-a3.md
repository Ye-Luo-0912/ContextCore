# Postgres Vector Shadow Eval

- DatasetName: `A3`
- Recommendation: `BlockedByProjectionMismatch`
- WorkspaceId: `eval-vector`
- CollectionId: `corpus`
- ProviderId: `qwen3-embedding-0.6b-onnx`
- ModelId: `qwen3-embedding-0.6b`
- Dimension: `1024`
- Normalized: `True`
- ProfileId: `normal-v1`
- TopK: `10`
- SampleCount: `50`
- QueryCount: `50`
- PgVectorCandidateCount: `500`
- FileSystemCandidateCount: `500`
- RecallAfterPolicy: `54.55%`
- MrrAfterPolicy: `0.5187`
- FileSystemRecallAfterPolicy: `54.55%`
- RecallDelta: `0`
- RiskAfterPolicy: `1`
- MustNotHitRiskAfterPolicy: `1.75%`
- LifecycleRiskAfterPolicy: `0.00%`
- FormalOutputChanged: `0`
- TopKOverlapRate: `88.60%`
- OrderingMismatchCount: `49`
- ScoreDeltaMax: `0.03753072`
- MetadataMismatchCount: `57`
- EligibilityMetadataMismatchCount: `57`
- RiskProjectionMismatchCount: `57`
- UseForRuntime: `False`

| Sample | PgVector | FileSystem | Overlap | Ordering | ScoreDeltaMax | MetadataMismatch | EligibilityMismatch | RiskMismatch |
|---|---:|---:|---:|---|---:|---:|---:|---:|
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
| coding-sample-001 | 10 | 10 | 8 | False | 0.02178416 | 2 | 2 | 2 |
| coding-sample-002 | 10 | 10 | 10 | False | 0.01805896 | 0 | 0 | 0 |
| coding-sample-003 | 10 | 10 | 9 | False | 0.01618005 | 1 | 1 | 1 |
| coding-sample-004 | 10 | 10 | 9 | False | 0.02401924 | 1 | 1 | 1 |
| coding-sample-005 | 10 | 10 | 8 | False | 0.00981534 | 2 | 2 | 2 |
| coding-sample-006 | 10 | 10 | 9 | False | 0.01430484 | 1 | 1 | 1 |
| coding-sample-007 | 10 | 10 | 9 | False | 0.01154614 | 1 | 1 | 1 |
| coding-sample-008 | 10 | 10 | 10 | False | 0.01562795 | 0 | 0 | 0 |
| coding-sample-009 | 10 | 10 | 8 | False | 0.01374895 | 2 | 2 | 2 |
| coding-sample-010 | 10 | 10 | 9 | False | 0.01341331 | 1 | 1 | 1 |
| novel-sample-001 | 10 | 10 | 10 | False | 0.01767485 | 0 | 0 | 0 |
| novel-sample-002 | 10 | 10 | 10 | False | 0.01620747 | 0 | 0 | 0 |
| novel-sample-003 | 10 | 10 | 10 | False | 0.01029017 | 0 | 0 | 0 |
| novel-sample-004 | 10 | 10 | 10 | False | 0.0177721 | 0 | 0 | 0 |
| novel-sample-005 | 10 | 10 | 8 | False | 0.00694022 | 2 | 2 | 2 |
| novel-sample-006 | 10 | 10 | 10 | False | 0.01461273 | 0 | 0 | 0 |
| novel-sample-007 | 10 | 10 | 9 | False | 0.01320838 | 1 | 1 | 1 |
| novel-sample-008 | 10 | 10 | 10 | True | 0.0138315 | 0 | 0 | 0 |
| novel-sample-009 | 10 | 10 | 7 | False | 0.02225645 | 3 | 3 | 3 |
| novel-sample-010 | 10 | 10 | 10 | False | 0.01694177 | 0 | 0 | 0 |
| project-sample-001 | 10 | 10 | 10 | False | 0.01151076 | 0 | 0 | 0 |
| project-sample-002 | 10 | 10 | 10 | False | 0.01434639 | 0 | 0 | 0 |
| project-sample-003 | 10 | 10 | 10 | False | 0.01720374 | 0 | 0 | 0 |
| project-sample-004 | 10 | 10 | 9 | False | 0.01569543 | 1 | 1 | 1 |
| project-sample-005 | 10 | 10 | 8 | False | 0.0357292 | 2 | 2 | 2 |
| project-sample-006 | 10 | 10 | 8 | False | 0.01221974 | 2 | 2 | 2 |
| project-sample-007 | 10 | 10 | 9 | False | 0.01186621 | 1 | 1 | 1 |
| project-sample-008 | 10 | 10 | 9 | False | 0.01345175 | 1 | 1 | 1 |
| project-sample-009 | 10 | 10 | 9 | False | 0.02778467 | 1 | 1 | 1 |
| project-sample-010 | 10 | 10 | 7 | False | 0.01322211 | 3 | 3 | 3 |
