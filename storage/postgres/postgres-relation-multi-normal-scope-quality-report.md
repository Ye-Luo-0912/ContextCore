# Postgres Relation Multi Normal Scope Canary Quality

- GatePassed: `True`
- ScopeCount: `2`
- EnabledScopeCount: `2`
- OperationCount: `210`
- PostgresPrimaryReadCount: `102`
- PostgresPrimaryWriteCount: `90`
- FileSystemFallbackCount: `0`
- ComparisonTraceCount: `210`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- NonAllowlistedScopeChecked: `True`
- AveragePostgresReadMs: `2.274`
- P95PostgresReadMs: `3.398`
- AveragePostgresWriteMs: `16.333`
- P95PostgresWriteMs: `22.488`
- GraphExpansionPreviewParityPassed: `True`
- ReviewLifecycleParityPassed: `True`
- DiagnosticsParityPassed: `True`
- ReplacementChainParityPassed: `True`
- CleanupPerformed: `False`
- Recommendation: `ReadyForLimitedScopeExpansion`
- RollbackInstruction: `Disable affected normal scope rule or set RelationGovernanceProviderSwitchOptions.Enabled=false.`

## Operation Count By Scope
- multi-normal-alpha: `105`
- multi-normal-beta: `105`

## Per Scope Status
- multi-normal-alpha: `contextcore_multi_normal_alpha/relation-governance-multi-normal-alpha-19ec0598ef3` stage=`db2.14-multi-normal` operations=`105` reads=`51` writes=`45` mismatches=`0` failures=`0` leaks=`0` recommendation=`ReadyForMultiNormalScopeCanary`
- multi-normal-beta: `contextcore_multi_normal_beta/relation-governance-multi-normal-beta-19ec0598ef3` stage=`db2.14-multi-normal` operations=`105` reads=`51` writes=`45` mismatches=`0` failures=`0` leaks=`0` recommendation=`ReadyForMultiNormalScopeCanary`

## Blocked Reasons
- none

## Diagnostics
- multi-normal-alpha:ExtendedCanaryScopedOnly
- multi-normal-beta:ExtendedCanaryScopedOnly
