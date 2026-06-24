# Postgres Job Queue Scoped Worker Canary

- Recommendation: `ReadyForLimitedWorkerScope`
- WorkspaceId: `postgres-job-queue-scoped-worker-canary`
- CollectionId: `jobs`
- ProviderMode: `GuardedPostgresPrimary`
- ProviderQualityReady: `True`
- JobCount: `6`
- CompletedCount: `4`
- FailedCount: `1`
- RetriedCount: `2`
- DeadLetterCount: `1`
- LeaseAcquireCount: `8`
- LeaseConflictCount: `1`
- LeaseExpiredReacquireCount: `1`
- HeartbeatCount: `1`
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`
- NonSelectedScopeRemainsFileSystem: `True`
- RuntimeWorkerGlobalProviderUnchanged: `True`
- CleanupPerformed: `True`

## BlockedReasons
- none

## Mismatches
- none

## Diagnostics
- RuntimeWorkerGlobalProviderUnchanged
- GlobalDefaultOn=false
