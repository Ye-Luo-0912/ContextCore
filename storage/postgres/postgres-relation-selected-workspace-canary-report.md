# Postgres Relation Selected Workspace Canary

- GatePassed: `True`
- WorkspaceId: `contextcore_selected_canary`
- CollectionId: `relation-governance-selected-canary`
- ProviderMode: `GuardedPostgresPrimary`
- OperationCount: `35`
- PostgresPrimaryReadCount: `17`
- PostgresPrimaryWriteCount: `15`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `35`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- AveragePostgresReadMs: `6.163`
- AveragePostgresWriteMs: `21.843`
- AverageFileSystemFallbackMs: `0`
- GraphExpansionPreviewParityPassed: `True`
- ReviewLifecycleParityPassed: `True`
- DiagnosticsParityPassed: `True`
- ReplacementChainParityPassed: `True`
- ControlRoomReadPathPassed: `True`
- ClientApiRoundtripPathPassed: `True`
- NonSelectedScopeRemainsFileSystem: `True`
- CleanupPerformed: `True`
- Recommendation: `ReadyForScopedServiceModeExpansion`
- RollbackInstruction: `Set RelationGovernanceProviderSwitchOptions.Enabled=false or remove the selected workspace/collection from allowlist.`

## Blocked Reasons
- none

## Diagnostics
- ExtendedCanaryScopedOnly
