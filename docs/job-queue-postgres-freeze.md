# Job Queue Postgres Freeze

Generated: 2026-06-14

## Scope

DB4.F 冻结 Job Queue Postgres provider 的 scoped worker 结论。该冻结只允许后续在明确 allowlisted worker scopes 中使用 `GuardedPostgresPrimary`，不切换全局 job queue provider，不接真实生产 worker loop，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## DB4 - DB4.3 Results

- provider diagnostics: `ReadyForParityEval`
- parity mismatch: `0`
- lease smoke: passed
- dual-write / shadow-read provider quality: `ReadyForScopedWorkerCanary`
- scoped worker canary: `ReadyForLimitedWorkerScope`
- limited worker observation quality: `ReadyForJobQueueFreezeGate`
- duplicate execution: `0`
- lease violation: `0`
- retry violation: `0`
- dead-letter violation: `0`
- Postgres failure: `0`
- scope leak: `0`
- non-selected scope remains FileSystem: `true`
- global worker provider unchanged: `true`

## Freeze State

- `JobQueuePostgres = ReadyForScopedWorkerMode`
- `DefaultProvider = ExistingProvider`
- `AllowedMode = GuardedPostgresPrimary only for explicit allowlisted worker scopes`
- `Required = lease / heartbeat / retry / dead-letter quality gates`
- `Forbidden = GlobalWorkerProviderSwitch`
- `Forbidden = ProductionWorkerLoopSwitchWithoutGate`

## Reports

- `storage/postgres/postgres-job-queue-diagnostics.json`
- `storage/postgres/postgres-job-queue-provider-quality-report.json`
- `storage/postgres/postgres-job-queue-scoped-worker-canary-report.json`
- `storage/postgres/postgres-job-queue-limited-worker-scope-quality-report.json`
- `storage/postgres/postgres-job-queue-freeze-gate.json`

## Rollback

Keep `ExistingProvider` as the global worker provider and remove any scoped allowlist entry. Historical business jobs are not migrated by this phase.
