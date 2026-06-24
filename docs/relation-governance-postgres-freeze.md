# Relation Governance Postgres Freeze

Generated: 2026-06-13

## Scope

DB2.F freezes the relation governance PostgreSQL provider rollout state after DB2.2 through DB2.14.

This freeze does not enable global default-on, does not migrate all historical business data, and does not change retrieval, planning, scoring, PackingPolicy, or package output.

## Frozen Result

- Readiness gate: passed
- Dual-write quality: passed
- Shadow-read quality: passed
- Scoped service mode gate: passed
- Selected workspace canary: passed
- Multi normal scope canary: passed
- MismatchCount: `0`
- PostgresFailureCount: `0`
- ScopeLeakCount: `0`

## Readiness Registry

- CapabilityId: `RelationGovernance`
- CurrentPhase: `DB2.F`
- Status: `ReadyForLimitedScopeExpansion`
- Recommendation: `ReadyForLimitedScopeExpansion`
- AllowedMode: `GuardedPostgresPrimary` only for explicitly allowlisted scopes
- Required: `FallbackToFileSystem=true`
- Required: `ContinueComparisonTrace=true`
- Forbidden: `GlobalDefaultOn`
- Forbidden: `GuardedPostgresPrimary` without fallback
- Forbidden: `GuardedPostgresPrimary` without comparison trace

## Rollout Policy

Relation governance can use PostgreSQL as primary only inside explicitly configured workspace / collection scopes that passed prior gates.

FileSystem remains the fallback and the control provider outside allowlisted scopes. Comparison trace remains enabled so provider divergence is visible.

Global default-on remains blocked until a separate rollout phase explicitly proves it.

## Latest Evidence

- `storage/postgres/postgres-relation-governance-readiness-gate.json`
- `storage/postgres/postgres-relation-dual-write-quality-report.json`
- `storage/postgres/postgres-relation-shadow-read-quality-report.json`
- `storage/postgres/postgres-relation-scoped-service-mode-gate.json`
- `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.json`
- `storage/postgres/postgres-relation-multi-normal-scope-quality-report.json`

## Runtime Boundary

- Runtime retrieval output: unchanged
- Planning output: unchanged
- Scoring: unchanged
- PackingPolicy: unchanged
- Package sections: unchanged
- Historical migration: not performed
- Global default-on: forbidden
