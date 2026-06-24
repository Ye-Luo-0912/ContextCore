# Vector Lifecycle Metadata Backfill Result

- ResultId: `70c7a826c7e4441abdf6bd139d95981e`
- OperationId: `vector-lifecycle-metadata-backfill-ace1d1217fba4a51b783333a280b5799`
- Applied: `True`
- UpdatedEntries: `9`
- SkippedEntries: `149`
- ManualReviewRequiredCount: `0`
- CannotResolveCount: `0`
- FailedCount: `0`

# Vector Lifecycle Metadata Backfill Plan

Generated: 2026-06-10T07:13:19.6854200+00:00

## Summary

- Workspace: `eval-vector`
- Collection: `corpus`
- Provider: `onnx-local`
- Model: `bge-small-zh-v1.5`
- TotalVectorSourceItems: `158`
- UnknownLifecycleBefore: `9`
- AutoResolvableCount: `9`
- ManualReviewRequiredCount: `0`
- CannotResolveCount: `0`
- ExpectedKnownLifecycleAfter: `158`
- ExpectedCoverageAfter: `100.00%`
- RecallRecoveryEstimate: `100.00%`
- RiskImpact: `将仅回填 9 个有运行时 metadata 证据的 source；0 个无证据 source 仍需人工复核。`

## Candidates

| Action | ItemId | Kind | Layer | ProposedLifecycle | Confidence | Reason | EvidenceMetadataKeys |
|---|---|---|---|---|---:|---|---|
| AutoResolve | ctx:chat-system-intro | system-intro | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
| AutoResolve | doc:automation-guide | guide | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
| AutoResolve | doc:ipromotioncandidatestore | interface | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
| AutoResolve | doc:legacy-code-structure | documentation | context | Historical | 0.90 | sourceTags runtime metadata 包含生命周期标记，可推断为 Historical。 | sourceTags |
| AutoResolve | doc:legacy-project-info | documentation | context | Historical | 0.90 | sourceTags runtime metadata 包含生命周期标记，可推断为 Historical。 | sourceTags |
| AutoResolve | doc:local-alpha-runbook | documentation | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
| AutoResolve | doc:postgres-not-ready | documentation | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
| AutoResolve | novel:character-linfeng | character | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
| AutoResolve | novel:world-cangqiong | world-setting | context | Active | 0.75 | sourceKind/layer 表示 normal context source，且没有历史/拒绝/替代 metadata。 | sourceKind, layer |
