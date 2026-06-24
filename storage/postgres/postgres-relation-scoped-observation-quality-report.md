# Postgres Relation Scoped Observation Quality

- GatePassed: `True`
- ScopeCount: `2`
- ObservationWindowMinutes: `30`
- OperationIntervalSeconds: `1`
- MaxOperations: `100`
- OperationCount: `59`
- PostgresPrimaryReadCount: `28`
- PostgresPrimaryWriteCount: `26`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `58`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- NonAllowlistedScopeLeakCount: `0`
- AveragePostgresReadMs: `4.547`
- P95PostgresReadMs: `17.049`
- AveragePostgresWriteMs: `17.584`
- P95PostgresWriteMs: `24.157`
- FallbackPathTested: `True`
- CleanupPerformed: `False`
- Recommendation: `ReadyForSelectedNormalWorkspace`
- RollbackInstruction: `Disable affected scoped rule or set RelationGovernanceProviderSwitchOptions.Enabled=false.`

## Per Scope Status
- selected-canary-alpha: operations=`29` reads=`14` writes=`13` mismatches=`0` failures=`0` recommendation=`NeedsMoreCanaryRuns`
- selected-canary-beta: operations=`29` reads=`14` writes=`13` mismatches=`0` failures=`0` recommendation=`NeedsMoreCanaryRuns`

## Blocked Reasons
- none

## Diagnostics
- selected-canary-alpha:ExtendedCanaryScopedOnly
- selected-canary-beta:ExtendedCanaryScopedOnly
