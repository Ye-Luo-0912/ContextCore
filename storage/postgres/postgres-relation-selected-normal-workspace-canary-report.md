# Postgres Relation Selected Normal Workspace Canary

- GatePassed: `True`
- WorkspaceId: `contextcore_selected_normal`
- CollectionId: `relation-governance-selected-normal`
- ProviderMode: `GuardedPostgresPrimary`
- OperationCount: `35`
- PostgresPrimaryReadCount: `17`
- PostgresPrimaryWriteCount: `15`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `35`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- AveragePostgresReadMs: `6.077`
- P95PostgresReadMs: `46.25`
- AveragePostgresWriteMs: `22.405`
- P95PostgresWriteMs: `109.295`
- GraphExpansionPreviewParityPassed: `True`
- ReviewLifecycleParityPassed: `True`
- DiagnosticsParityPassed: `True`
- ReplacementChainParityPassed: `True`
- ControlRoomReadPathPassed: `True`
- ClientApiRoundtripPathPassed: `True`
- NonSelectedNormalScopeRemainsFileSystem: `True`
- CleanupPerformed: `False`
- Recommendation: `ReadyForLimitedNormalScope`
- RollbackInstruction: `Remove the selected normal workspace/collection from allowlist or set RelationGovernanceProviderSwitchOptions.Enabled=false.`

## Blocked Reasons
- none

## Diagnostics
- ExtendedCanaryScopedOnly
