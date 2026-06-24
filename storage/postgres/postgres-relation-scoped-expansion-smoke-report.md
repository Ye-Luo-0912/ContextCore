# Postgres Relation Scoped Expansion Smoke

- GatePassed: `True`
- ScopeCount: `2`
- AllowlistedScopeCount: `2`
- NonAllowlistedScopeChecked: `True`
- OperationCount: `59`
- PostgresPrimaryReadCount: `28`
- PostgresPrimaryWriteCount: `26`
- FileSystemScopeReadCount: `1`
- FallbackCount: `0`
- ComparisonTraceCount: `58`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- AveragePostgresReadMs: `4.286`
- AveragePostgresWriteMs: `14.759`
- Recommendation: `ReadyForScopedExpansion`

## Plans
- selected-canary-alpha: `contextcore_scoped_expansion_alpha/relation-governance-scoped-expansion-alpha` mode=`GuardedPostgresPrimary` gate=`Passed` canary=`ReadyForScopedServiceModeExpansion`
  rollback: Disable scope `selected-canary-alpha` or set RelationGovernanceProviderSwitchOptions.Enabled=false.
- selected-canary-beta: `contextcore_scoped_expansion_beta/relation-governance-scoped-expansion-beta` mode=`GuardedPostgresPrimary` gate=`Passed` canary=`ReadyForScopedServiceModeExpansion`
  rollback: Disable scope `selected-canary-beta` or set RelationGovernanceProviderSwitchOptions.Enabled=false.

## Per Scope Status
- selected-canary-alpha: operations=`29` reads=`14` writes=`13` mismatches=`0` failures=`0` recommendation=`ReadyForSelectedWorkspaceCanary`
- selected-canary-beta: operations=`29` reads=`14` writes=`13` mismatches=`0` failures=`0` recommendation=`ReadyForSelectedWorkspaceCanary`

## Blocked Reasons
- none

## Diagnostics
- selected-canary-alpha:ExtendedCanaryScopedOnly
- selected-canary-beta:ExtendedCanaryScopedOnly
