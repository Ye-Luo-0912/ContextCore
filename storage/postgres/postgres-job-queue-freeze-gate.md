# Postgres Job Queue Freeze Gate

- Passed: `True`
- JobQueuePostgres: `ReadyForScopedWorkerMode`
- DefaultProvider: `ExistingProvider`
- AllowedMode: `GuardedPostgresPrimary only for explicit allowlisted worker scopes`
- DiagnosticsReady: `True`
- ProviderQualityReady: `True`
- ScopedWorkerCanaryPassed: `True`
- LimitedWorkerScopeQualityPassed: `True`
- DuplicateExecutionCount: `0`
- LeaseViolationCount: `0`
- RetryViolationCount: `0`
- DeadLetterViolationCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- NonSelectedScopeRemainsFileSystem: `True`
- RuntimeWorkerGlobalProviderUnchanged: `True`
- GlobalSwitchAllowed: `False`
- ScopedWorkerCanaryAllowed: `True`
- P15GatePassed: `True`
- Recommendation: `ReadyForScopedWorkerMode`

## Required
- lease quality gate
- heartbeat quality gate
- retry quality gate
- dead-letter quality gate

## Forbidden
- GlobalWorkerProviderSwitch
- ProductionWorkerLoopSwitchWithoutGate

## BlockedReasons
- none

## Diagnostics
- DefaultProviderExistingProvider
- AllowlistedGuardedPostgresPrimaryOnly
- RuntimeWorkerGlobalProviderUnchanged
