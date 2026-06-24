# Postgres Relation Scoped Expansion Plan

- GatePassed: `True`
- ScopeCount: `2`
- AllowlistedScopeCount: `2`
- NonAllowlistedScopeChecked: `False`
- OperationCount: `0`
- PostgresPrimaryReadCount: `0`
- PostgresPrimaryWriteCount: `0`
- FileSystemScopeReadCount: `0`
- FallbackCount: `0`
- ComparisonTraceCount: `0`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- AveragePostgresReadMs: `0`
- AveragePostgresWriteMs: `0`
- Recommendation: `ReadyForScopedExpansion`

## Plans
- selected-canary-alpha: `contextcore_scoped_expansion_alpha/relation-governance-scoped-expansion-alpha` mode=`GuardedPostgresPrimary` gate=`Passed` canary=`ReadyForScopedServiceModeExpansion`
  rollback: Disable scope `selected-canary-alpha` or set RelationGovernanceProviderSwitchOptions.Enabled=false.
- selected-canary-beta: `contextcore_scoped_expansion_beta/relation-governance-scoped-expansion-beta` mode=`GuardedPostgresPrimary` gate=`Passed` canary=`ReadyForScopedServiceModeExpansion`
  rollback: Disable scope `selected-canary-beta` or set RelationGovernanceProviderSwitchOptions.Enabled=false.

## Per Scope Status
- none

## Blocked Reasons
- none

## Diagnostics
- none
