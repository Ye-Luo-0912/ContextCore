# Postgres Relation Limited Normal Scope Observation Quality

- GatePassed: `True`
- WorkspaceId: `contextcore_selected_normal`
- CollectionId: `relation-governance-selected-normal`
- ObservationWindowMinutes: `60`
- OperationIntervalSeconds: `0`
- MaxOperations: `100`
- ProviderMode: `GuardedPostgresPrimary`
- OperationCount: `105`
- PostgresPrimaryReadCount: `51`
- PostgresPrimaryWriteCount: `45`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `105`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- AveragePostgresReadMs: `3.426`
- P95PostgresReadMs: `6.958`
- AveragePostgresWriteMs: `19.383`
- P95PostgresWriteMs: `23.19`
- ErrorRate: `0`
- FallbackRate: `0`
- GraphExpansionPreviewParityPassed: `True`
- ReviewLifecycleParityPassed: `True`
- DiagnosticsParityPassed: `True`
- ReplacementChainParityPassed: `True`
- ControlRoomReadPathPassed: `True`
- ClientApiRoundtripPathPassed: `True`
- NonSelectedNormalScopeRemainsFileSystem: `True`
- CleanupPerformed: `False`
- Recommendation: `ReadyForMultiNormalScopeCanary`
- RollbackInstruction: `Remove the selected normal workspace/collection from allowlist or set RelationGovernanceProviderSwitchOptions.Enabled=false.`

## Blocked Reasons
- none

## Diagnostics
- ExtendedCanaryScopedOnly
