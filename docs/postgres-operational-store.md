# Postgres Operational Store

DB1 建立 PostgreSQL operational store 的基础能力：配置、连接工厂、schema baseline、migration runner、diagnostics、API、CLI 和 ControlRoom 状态展示。本阶段不迁移数据，不切换 provider，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Configuration

`PostgresStoreOptions` / `PostgresOptions` 字段：

- `Enabled`
- `ConnectionString`
- `SchemaName`
- `AutoMigrate`
- `CommandTimeoutSeconds`
- `ProviderId`

默认策略：

- `Enabled=false`
- `AutoMigrate=false`
- 连接串不写入 report 明文
- migration apply 必须显式 confirm

## Schema Baseline

DB1 baseline migration 建表范围：

- `context_schema_migrations`
- `workspaces`
- `collections`
- `context_items`
- `memory_short_term_items`
- `memory_candidate_items`
- `memory_stable_items`
- `memory_temporal_items`
- `memory_reviews`
- `relations`
- `relation_reviews`
- `constraints_active`
- `constraints_candidate`
- `constraint_gaps`
- `learning_feedback_events`
- `learning_feedback_reviews`
- `learning_feature_candidates`
- `context_jobs`
- `context_job_events`
- `vector_index_entries`
- `vector_index_manifests`

旧 `schema_versions` 表继续保留兼容读取；DB1 新增 `context_schema_migrations` 作为 operational schema migration table。

## API

- `GET /api/admin/storage/postgres/status`
- `GET /api/admin/storage/postgres/diagnostics`
- `POST /api/admin/storage/postgres/migrations/dry-run`
- `POST /api/admin/storage/postgres/migrations/apply`

`apply` 请求必须传 `confirm=true`，否则返回 `ConfirmRequired`，不写数据库。

## CLI

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-storage-diagnostics
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-migration-preview
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-migration-apply --confirm --connection-string "env or local secret"
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-migration-smoke --schema contextcore_smoke --confirm
```

默认未提供连接串时返回 `NotConfigured`，不会尝试连接数据库。

输出：

- `storage/postgres-storage-diagnostics.json`
- `storage/postgres-storage-diagnostics.md`
- `storage/postgres-migration-preview.json`
- `storage/postgres-migration-preview.md`
- `storage/postgres-migration-apply.json`
- `storage/postgres-migration-apply.md`
- `storage/postgres/postgres-schema-verification-report.json`
- `storage/postgres/postgres-schema-verification-report.md`

## Local Smoke Verification

DB1.1 新增 `postgres-migration-smoke`，用于独立 schema 的本地 smoke apply 与 schema verification。

- 默认 schema 建议使用 `contextcore_smoke`。
- 没有 `--confirm` 时只执行连接测试和 dry-run，并输出 `ConfirmRequired`。
- `--drop-confirm` 只允许在显式 schema 下清理 smoke schema。
- 报告检查 required tables、required indexes、applied migration count 和 schema version。
- 真实 connection string 不写入 report 明文。

本地步骤见 `docs/postgres-local-smoke-runbook.md`。

## ORM Strategy

DB1 的 schema / migration / diagnostics 保持 Npgsql 原生 SQL，以获得明确的 DDL 控制和最低依赖。后续 DB2+ 实现 operational store CRUD / query 时，可以引入高性能轻量 ORM，例如 Dapper 或同等级别的 mapper：

- 热路径默认禁用重型 change tracking。
- 查询必须显式投影，避免全量反序列化。
- 批量写入使用 bounded batch。
- 所有数据库访问保留 timeout / cancellation。
- ORM 不得引入 itemId / sampleId / fixture / 领域词表特判。

## Diagnostics

`PostgresOperationalStoreDiagnostics` 输出：

- provider enabled
- connection available
- schema exists
- current schema version
- pending migrations
- table count
- required table missing count
- provider capability status
- redacted connection string
- schema verification summary

连接失败、未配置和缺表都只产生 diagnostics，不自动迁移。

## DB2 RelationStore Provider

DB2 新增 `PostgresRelationStore` 的显式 provider 验证路径。默认 runtime 仍使用 `FileSystemRelationStore`：

- 不切换默认 provider。
- 不 dual-write。
- 不 shadow-read。
- 不迁移业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Postgres relation provider 使用 DB1 baseline 的 `relations` 表：

- `id` 对应 relation id。
- `source_id` / `target_id` 对应 source / target item。
- `relation_type`、`weight`、`confidence`、`created_at` 使用结构化列。
- 完整 relation payload 存在 `data jsonb`。
- `lifecycle` / `reviewStatus` 通过 `data.Metadata` 读取，用于 explicit diagnostics / parity 查询。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-store-diagnostics
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-store-parity --cleanup-confirm
```

输出：

- `storage/postgres/postgres-relation-store-diagnostics.json`
- `storage/postgres/postgres-relation-store-diagnostics.md`
- `storage/postgres/postgres-relation-store-parity-report.json`
- `storage/postgres/postgres-relation-store-parity-report.md`

Parity eval 在隔离 workspace / collection 中写入通用 relation fixture，对 FileSystem / Postgres 比较 get、list、source query、target query、type query、lifecycle query、reviewStatus query、replacement chain query 和 delete。`--cleanup-confirm` 才清理 Postgres fixture。

## DB2.1 Relation Review / Diagnostics Provider

DB2.1 补齐 relation review 与 diagnostics projection 的 Postgres provider。默认 runtime 仍为 FileSystem：

- `PostgresRelationReviewStore` 实现现有 `IRelationReviewStore` append / query-by-relation 契约。
- provider 额外支持 workspace / collection 查询、reviewStatus / reviewer / operationId filter、latest review lookup。
- `PostgresRelationDiagnosticsStore` 保存 relation diagnostics snapshot/projection，支持 relationId、itemId、diagnostic kind、severity filter。
- 新增 `relation_diagnostics` 表和索引，schema version 提升为 `cc-schema-v4`。
- 不 dual-write、不 shadow-read、不迁移业务数据、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-review-diagnostics
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-review-parity --cleanup-confirm
```

输出：

- `storage/postgres/postgres-relation-review-diagnostics.json`
- `storage/postgres/postgres-relation-review-diagnostics.md`
- `storage/postgres/postgres-relation-review-parity-report.json`
- `storage/postgres/postgres-relation-review-parity-report.md`

Parity eval 在隔离 workspace / collection 中写入 relation edge、review records 与 diagnostics snapshots，并比较 review list、latest review、reviewStatus / reviewer / operationId filter、diagnostics by relation / item / kind / severity。cleanup 必须显式 `--cleanup-confirm`。

## DB2.2 Relation Governance Readiness Gate

DB2.2 将 relation edge、relation review 和 relation diagnostics provider 合并为统一 governance readiness gate。该阶段仍然只做 provider readiness：

- 不切换 runtime provider。
- 不 dual-write。
- 不 shadow-read。
- 不迁移业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-governance-parity --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-governance-readiness-gate
```

输出：

- `storage/postgres/postgres-relation-governance-parity-report.json`
- `storage/postgres/postgres-relation-governance-parity-report.md`
- `storage/postgres/postgres-relation-governance-readiness-gate.json`
- `storage/postgres/postgres-relation-governance-readiness-gate.md`

Governance parity 在隔离 workspace / collection 中一次性覆盖：

- relation edge upsert / get / list
- source / target / type / lifecycle / reviewStatus query
- replacement chain query
- relation review append / latest / status filter
- diagnostics snapshot write / relation / item / kind / severity filter
- cleanup

Readiness gate 通过条件包括：Postgres storage `Status=Ready`、schema version 至少 `cc-schema-v4`、relation / relation_reviews / relation_diagnostics 表存在、缺失索引为 0、relation store parity 通过、relation review parity 通过、diagnostics parity 通过、governance parity 通过、mismatch 为 0、cleanup 已执行、`UseForRuntime=false`、P15 gate 最新报告通过。

当前 gate 通过后只表示可以进入后续显式 dual-write 设计阶段：

- `CanDualWrite=true`
- `CanShadowRead=false`
- `CanRuntimeSwitch=false`

shadow-read 和 runtime switch 必须由后续独立 phase 设计回滚、差异审计和 opt-in gate。

## DB2.3 Relation Governance Dual-write Trace Collection

DB2.3 只实现 relation governance 的显式 dual-write smoke 与质量报告。该阶段仍保持：

- FileSystem 是 source of truth。
- Postgres 只是 `eval postgres-relation-dual-write-smoke` 中显式启用的 dual-write target。
- Postgres 写失败时，在 `FallbackOnPostgresFailure=true` 下不影响 FileSystem 正式写。
- 不从 Postgres 读取 runtime 数据。
- 不启用 shadow-read。
- 不切换 runtime provider。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增配置模型：

- `RelationGovernanceDualWriteOptions`
- 默认 `Enabled=false`
- 默认 `WritePostgres=false`
- 默认 `TraceEnabled=true`
- 默认 `FallbackOnPostgresFailure=true`
- 默认 `FailOnMismatch=false`

新增 trace artifact：

- `ArtifactKind.TraceRelationDualWrite`
- 标准路径：`traces/relation-dual-write/{date}`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-dual-write-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-dual-write-quality
```

输出：

- `storage/postgres/postgres-relation-dual-write-smoke-report.json`
- `storage/postgres/postgres-relation-dual-write-smoke-report.md`
- `storage/postgres/postgres-relation-dual-write-traces.jsonl`
- `storage/postgres/postgres-relation-dual-write-quality-report.json`
- `storage/postgres/postgres-relation-dual-write-quality-report.md`

Smoke 在隔离 workspace / collection 中覆盖 relation edge、relation review 和 relation diagnostics snapshot 写入，并比较 FileSystem 与 Postgres 内容一致性。cleanup 必须显式 `--cleanup-confirm`。

Quality report 统计 trace count、FileSystem / Postgres 写入成功数、Postgres failure、mismatch、fallback 和平均耗时。`ReadyForShadowRead` 只表示 trace 质量满足进入下一阶段 shadow-read 设计，不代表当前阶段启用 shadow-read。

## DB2.4 Relation Governance Shadow-read Comparison

DB2.4 只实现 relation governance 的影子读比较。该阶段仍保持：

- FileSystem 结果是正式返回值。
- Postgres 结果只用于 comparison。
- mismatch 和 latency 写入 trace。
- Postgres 读失败不影响 FileSystem 正式读。
- 不切换 runtime provider。
- 不迁移业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增配置模型：

- `RelationGovernanceShadowReadOptions`
- 默认 `Enabled=false`
- 默认 `TraceEnabled=true`
- 默认 `ReadPostgres=false`
- 默认 `CompareResults=true`
- 默认 `FailOnMismatch=false`

新增 trace artifact：

- `ArtifactKind.TraceRelationShadowRead`
- 标准路径：`traces/relation-shadow-read/{date}`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-shadow-read-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-shadow-read-quality
```

Smoke 先通过 dual-write 在隔离 workspace / collection 写入 relation edge、review 与 diagnostics，再执行 get/list/source/target/type/lifecycle/reviewStatus/replacement-chain/review/diagnostics 查询比较。cleanup 必须显式 `--cleanup-confirm`。

输出：

- `storage/postgres/postgres-relation-shadow-read-smoke-report.json`
- `storage/postgres/postgres-relation-shadow-read-smoke-report.md`
- `storage/postgres/postgres-relation-shadow-read-traces.jsonl`
- `storage/postgres/postgres-relation-shadow-read-quality-report.json`
- `storage/postgres/postgres-relation-shadow-read-quality-report.md`

`ReadyForGuardedProviderSwitch` 只表示 shadow-read comparison 满足进入下一阶段 guarded provider switch 设计，不代表当前阶段切换 provider。

## DB2.5 Relation Governance Guarded Provider Switch

DB2.5 只实现 relation governance 的显式 guarded provider switch 验证路径。该阶段仍保持：

- 默认 provider mode 是 `FileSystemPrimary`。
- `GuardedPostgresPrimary` 只能在显式 smoke / gate 中启用。
- 必须通过 governance readiness gate 与 shadow-read quality gate。
- 必须配置 workspace / collection allowlist。
- Postgres failure 时 fallback 到 FileSystem。
- mismatch 写入 trace，并按 `FailClosedOnMismatch` 阻断或 fallback。
- 不允许 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增配置模型：

- `RelationGovernanceProviderMode`
  - `FileSystemPrimary`
  - `DualWriteOnly`
  - `ShadowRead`
  - `GuardedPostgresPrimary`
- `RelationGovernanceProviderSwitchOptions`
  - 默认 `Enabled=false`
  - 默认 `Mode=FileSystemPrimary`
  - 默认 `FallbackToFileSystem=true`
  - 默认 `ContinueComparisonTrace=true`
  - 默认 `FailClosedOnMismatch=true`
  - 默认 `RequireReadinessGate=true`

新增 provider router：

- `RelationGovernanceProviderRouter`
- 根据 mode 决定 write path、read path、fallback path 和 comparison trace path。
- 当前仅用于 relation edge、relation review、relation diagnostics 的显式 smoke / gate；不替换全局 runtime provider。

新增 trace artifact：

- `ArtifactKind.TraceRelationProviderSwitch`
- 标准路径：`traces/relation-provider-switch/{date}`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-provider-switch-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-provider-switch-gate
```

Smoke 在隔离 workspace / collection 中启用 `GuardedPostgresPrimary`，写入 relation edge、review、diagnostics，从 Postgres primary 读取，并验证 FileSystem fallback 与 comparison trace。cleanup 必须显式 `--cleanup-confirm`。

输出：

- `storage/postgres/postgres-relation-provider-switch-smoke-report.json`
- `storage/postgres/postgres-relation-provider-switch-smoke-report.md`
- `storage/postgres/postgres-relation-provider-switch-gate.json`
- `storage/postgres/postgres-relation-provider-switch-gate.md`

Gate 通过条件包括 governance readiness gate passed、dual-write quality `ReadyForShadowRead`、shadow-read quality `ReadyForGuardedProviderSwitch`、mismatch 为 0、Postgres read/write failure 为 0、fallback path 已验证、allowlist scope 已配置、最新 P15 gate 通过。

回滚方式：将 mode 设为 `FileSystemPrimary` 或禁用 `RelationGovernanceProviderSwitchOptions.Enabled`。DB2.5 不提供全局默认开启路径。

## DB2.6 Relation Governance Runtime Canary

DB2.6 只在隔离 workspace / collection 中执行 relation governance runtime canary。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- canary scope 默认 `contextcore_canary/relation-governance-canary`。
- 只有 canary scope 进入 `GuardedPostgresPrimary`。
- Postgres failure 时 fallback FileSystem。
- mismatch 写入 provider switch trace，并按 fail-closed 策略阻断。
- cleanup 必须显式 `--cleanup-confirm`。

新增配置模型：

- `RelationGovernanceCanaryOptions`
  - 默认 `Enabled=false`
  - 默认 `Mode=GuardedPostgresPrimary`
  - 默认 `FallbackToFileSystem=true`
  - 默认 `ContinueComparisonTrace=true`
  - 默认 `FailClosedOnMismatch=true`
  - 默认 `RequireProviderSwitchGate=true`

新增 runner / report：

- `RelationGovernanceCanaryRunner`
- `PostgresRelationRuntimeCanaryReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-runtime-canary --cleanup-confirm
```

Canary preflight 会检查 Postgres storage diagnostics、governance readiness gate、dual-write quality、shadow-read quality 和 provider switch gate。通过后，canary 在隔离 scope 中写 relation edge、relation review、diagnostics snapshot，并执行 get/list/source/target/type/lifecycle/reviewStatus/replacement-chain/latest-review/diagnostics relation-item-kind-severity 查询。

输出：

- `storage/postgres/postgres-relation-runtime-canary-report.json`
- `storage/postgres/postgres-relation-runtime-canary-report.md`

`ReadyForScopedServiceMode` 只表示 canary scope 具备进入后续 scoped service mode 的条件，不代表全局 runtime provider 已切换。

## DB2.7 Relation Governance Scoped Service Mode

DB2.7 将 `GuardedPostgresPrimary` 接入显式配置的 scoped service mode。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- allowlist 命中的 workspace / collection 可使用 Postgres primary。
- allowlist 未命中的 scope 保持 `FileSystemPrimary`。
- governance readiness gate、provider switch gate 和 runtime canary 未通过时拒绝启用 scoped mode。
- Postgres failure 时 fallback FileSystem。
- comparison trace 继续写入，用于后续质量检查。

配置仍使用 `RelationGovernanceProviderSwitchOptions`：

- `Enabled=false`
- `Mode=GuardedPostgresPrimary`
- `WorkspaceAllowlist`
- `CollectionAllowlist`
- `FallbackToFileSystem=true`
- `ContinueComparisonTrace=true`
- `FailClosedOnMismatch=true`
- `RequireReadinessGate=true`
- `RequireRuntimeCanaryPassed=true`

Service API：

- `GET /api/admin/storage/relation-provider/status`
- `GET /api/admin/storage/relation-provider/scoped-diagnostics`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-service-mode-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-service-mode-gate
```

输出：

- `storage/postgres/postgres-relation-scoped-service-mode-smoke-report.json`
- `storage/postgres/postgres-relation-scoped-service-mode-smoke-report.md`
- `storage/postgres/postgres-relation-scoped-service-mode-gate.json`
- `storage/postgres/postgres-relation-scoped-service-mode-gate.md`

回滚方式：移除 scoped allowlist，或将 `RelationGovernanceProviderSwitchOptions.Enabled=false` / `Mode=FileSystemPrimary`。DB2.7 不提供全局默认开启路径。

## DB2.8 Relation Governance Scoped Extended Canary

DB2.8 只执行更完整的 scoped relation governance extended canary。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 默认 canary scope 为 `contextcore_canary/relation-governance-extended-canary`。
- canary scope 使用 `GuardedPostgresPrimary`，fallback 和 comparison trace 保留。
- 必须先通过 scoped service mode gate。
- cleanup 必须显式 `--cleanup-confirm`。

新增模型 / runner：

- `RelationGovernanceExtendedCanaryOptions`
- `RelationGovernanceExtendedCanaryRunner`
- `PostgresRelationScopedExtendedCanaryReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-extended-canary --cleanup-confirm
```

Extended canary 覆盖：

- relation edge create / update / delete / source / target / type / lifecycle / reviewStatus 查询。
- relation review review / reject / deprecate / needs-evidence / latest / history。
- diagnostics snapshot 写入与 relation / item / kind / severity 查询。
- replacement chain `superseded_by` / `replaces` 查询。
- graph expansion preview 的 `audit-v1`、`conflict-v1`、`normal-v1`、`current-task-v1` parity。

输出：

- `storage/postgres/postgres-relation-scoped-extended-canary-report.json`
- `storage/postgres/postgres-relation-scoped-extended-canary-report.md`
- `storage/postgres/postgres-relation-scoped-extended-canary-traces.jsonl`

`ReadyForSelectedWorkspaceCanary` 只表示 relation governance provider 在更完整的隔离 scope 中通过扩展 canary，不代表可以全局默认启用 Postgres。

## DB2.9 Relation Governance Selected Workspace Canary

DB2.9 只在一个受控 selected workspace / collection 中执行较真实的 service-mode canary。该阶段仍保持：

- 不做 global default on。
- 不迁移所有历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- selected workspace / collection 必须明确配置。
- 必须通过 governance readiness gate、provider switch gate、runtime canary、scoped service mode gate、extended canary 和 P15 gate。
- Postgres failure 时 fallback FileSystem。
- comparison trace 继续写入。
- canary 只写自身 relation IDs，并在运行结束清理 Postgres canary 数据。

新增模型 / runner：

- `RelationGovernanceSelectedWorkspaceCanaryOptions`
- `RelationGovernanceSelectedWorkspaceCanaryRunner`
- `PostgresRelationSelectedWorkspaceCanaryReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-selected-workspace-canary
```

输出：

- `storage/postgres/postgres-relation-selected-workspace-canary-report.json`
- `storage/postgres/postgres-relation-selected-workspace-canary-report.md`
- `storage/postgres/postgres-relation-selected-workspace-canary-traces.jsonl`

报告包含 operation count、Postgres primary read/write count、fallback count、comparison trace count、mismatch/failure count、latency summary、graph/review/diagnostics/replacement parity、ControlRoom read path、client API roundtrip path 和 rollback instruction。

`ReadyForScopedServiceModeExpansion` 只表示可讨论扩大 scoped service mode 范围，不代表全局默认 provider 可开启。

## DB2.10 Relation Governance Scoped Service Mode Expansion

DB2.10 只把 relation governance 的 `GuardedPostgresPrimary` 扩大到多个明确 allowlisted workspace / collection。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不关闭 FileSystem fallback。
- 不关闭 comparison trace。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 非 allowlisted scope 必须继续保持 FileSystem。

配置模型：

- `RelationGovernanceProviderSwitchOptions`
  - `WorkspaceAllowlist` / `CollectionAllowlist` 继续兼容旧配置。
  - 新增 `ScopedRules`，每条 rule 显式声明 `ScopeName`、`WorkspaceId`、`CollectionId`、`Mode`、`RolloutStage` 和 `Enabled`。
  - 全局 `Mode` 可保持 `FileSystemPrimary`；命中 `ScopedRules` 的 scope 才进入 `GuardedPostgresPrimary`。
- `RelationGovernanceScopedExpansionPlan`
- `PostgresRelationScopedExpansionReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-expansion-plan
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-expansion-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-expansion-gate
```

Expansion smoke 默认覆盖两个显式 scope：

- `contextcore_scoped_expansion_alpha/relation-governance-scoped-expansion-alpha`
- `contextcore_scoped_expansion_beta/relation-governance-scoped-expansion-beta`

同时检查一个 non-allowlisted scope 不写 Postgres。每个 allowlisted scope 复用 extended canary 的 relation edge、review、diagnostics、replacement chain 和 graph expansion preview parity 矩阵。

输出：

- `storage/postgres/postgres-relation-scoped-expansion-plan.json`
- `storage/postgres/postgres-relation-scoped-expansion-plan.md`
- `storage/postgres/postgres-relation-scoped-expansion-smoke-report.json`
- `storage/postgres/postgres-relation-scoped-expansion-smoke-report.md`
- `storage/postgres/postgres-relation-scoped-expansion-gate.json`
- `storage/postgres/postgres-relation-scoped-expansion-gate.md`
- `storage/postgres/postgres-relation-scoped-expansion-traces.jsonl`

`ReadyForScopedExpansion` 只表示当前显式 scope 列表可以继续扩大 canary 范围，不代表 Postgres relation governance 可以全局默认启用。

## DB2.11 Relation Governance Scoped Runtime Observation Window

DB2.11 只在已有 scoped expansion gate 通过后，对多个显式 allowlisted scope 做运行时观测窗口。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不关闭 FileSystem fallback。
- 不关闭 comparison trace。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 非 allowlisted scope 必须继续保持 FileSystem。
- mismatch、Postgres failure、scope leak 或 P95 latency 超阈值都会阻断 quality recommendation。

新增模型 / runner：

- `RelationGovernanceScopedObservationOptions`
- `RelationGovernanceScopedObservationRunner`
- `PostgresRelationScopedObservationReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-observation-window
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-scoped-observation-quality
```

Observation window 默认不清理数据；只有显式传入 `--cleanup-confirm` 时才清理 scoped observation 数据。Quality gate 默认 P95 阈值为 5000ms，可通过 `--p95-threshold-ms` 调整。

输出：

- `storage/postgres/postgres-relation-scoped-observation-report.json`
- `storage/postgres/postgres-relation-scoped-observation-report.md`
- `storage/postgres/postgres-relation-scoped-observation-traces.jsonl`
- `storage/postgres/postgres-relation-scoped-observation-quality-report.json`
- `storage/postgres/postgres-relation-scoped-observation-quality-report.md`

`ReadyForSelectedNormalWorkspace` 只表示可进入下一个受控正常 workspace 观察讨论，不代表全局默认 provider 可开启。

## DB2.12 Relation Governance Selected Normal Workspace Canary

DB2.12 只在一个明确配置的 normal workspace / collection 中运行 `GuardedPostgresPrimary` canary。该阶段仍保持：

- 不做 global default on。
- 不迁移所有历史业务数据。
- 不关闭 FileSystem fallback。
- 不关闭 comparison trace。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 非 selected normal scope 必须继续保持 FileSystem。
- cleanup 默认关闭；对 normal scope 不执行按 workspace / collection 的批量清理，避免误删正常数据。

新增模型 / runner：

- `RelationGovernanceSelectedNormalWorkspaceOptions`
- `RelationGovernanceSelectedNormalWorkspaceRunner`
- `PostgresRelationSelectedNormalWorkspaceCanaryReport`

前置 gate 包括 governance readiness gate、provider switch gate、runtime canary、scoped service mode gate、extended canary、selected workspace canary、scoped expansion gate、scoped observation quality gate、显式 workspace / collection 配置和 P15 gate。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-selected-normal-workspace-canary
```

输出：

- `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.json`
- `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.md`
- `storage/postgres/postgres-relation-selected-normal-workspace-canary-traces.jsonl`

当前本地 smoke 结果：

- `Recommendation=ReadyForLimitedNormalScope`
- `OperationCount=35`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- graph / review / diagnostics / replacement parity 均通过

`ReadyForLimitedNormalScope` 只表示该 selected normal scope 可继续有限观察，不允许升级为全局默认 provider。

## DB2.13 Relation Governance Limited Normal Scope Observation

DB2.13 只在 DB2.12 通过的同一个 selected normal workspace / collection 上执行更长 observation window。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不关闭 FileSystem fallback。
- 不关闭 comparison trace。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 非 selected normal scope 必须继续保持 FileSystem。
- `CleanupMode=None` 默认不删除 normal workspace 数据；显式 cleanup 也只允许删除 canary relation id。

新增模型 / runner：

- `RelationGovernanceLimitedNormalScopeObservationOptions`
- `RelationGovernanceLimitedNormalScopeObservationRunner`
- `PostgresRelationLimitedNormalScopeObservationReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-limited-normal-scope-observation
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-limited-normal-scope-quality
```

输出：

- `storage/postgres/postgres-relation-limited-normal-scope-observation-report.json`
- `storage/postgres/postgres-relation-limited-normal-scope-observation-report.md`
- `storage/postgres/postgres-relation-limited-normal-scope-quality-report.json`
- `storage/postgres/postgres-relation-limited-normal-scope-quality-report.md`
- `storage/postgres/postgres-relation-limited-normal-scope-traces.jsonl`

当前本地 quality 结果：

- `Recommendation=ReadyForMultiNormalScopeCanary`
- `OperationCount=105`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- `FallbackRate=0`
- `P95PostgresReadMs=7.6726`
- `P95PostgresWriteMs=22.5711`

`ReadyForMultiNormalScopeCanary` 只表示可进入多 normal scope canary 讨论，不允许升级为全局默认 provider。

## DB2.14 Relation Governance Multi Normal Scope Canary

DB2.14 只在多个显式配置的低风险 normal workspace / collection 中启用 `GuardedPostgresPrimary` 做 canary。该阶段仍保持：

- 不做 global default on。
- 不迁移历史业务数据。
- 不关闭 FileSystem fallback。
- 不关闭 comparison trace。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- 至少需要 2 个 normal scope 明确配置。
- 非 allowlisted scope 必须继续保持 FileSystem。
- scope A 数据不得出现在 scope B 查询结果中。
- `CleanupMode=None` 默认不删除 normal workspace 数据；显式 cleanup 只允许删除 canary relation id。

新增模型 / runner：

- `RelationGovernanceMultiNormalScopeCanaryOptions`
- `RelationGovernanceNormalScopeRule`
- `RelationGovernanceMultiNormalScopeCanaryRunner`
- `PostgresRelationMultiNormalScopeCanaryReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-multi-normal-scope-canary
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-relation-multi-normal-scope-quality
```

输出：

- `storage/postgres/postgres-relation-multi-normal-scope-canary-report.json`
- `storage/postgres/postgres-relation-multi-normal-scope-canary-report.md`
- `storage/postgres/postgres-relation-multi-normal-scope-quality-report.json`
- `storage/postgres/postgres-relation-multi-normal-scope-quality-report.md`
- `storage/postgres/postgres-relation-multi-normal-scope-traces.jsonl`

Quality gate 检查 limited normal scope observation quality、P15 gate、至少 2 个启用 scope、mismatch、Postgres failure、scope leak、non-allowlisted scope、P95 latency 和 graph / review / diagnostics / replacement parity。

`ReadyForLimitedScopeExpansion` 只表示多个 normal scope canary 可继续扩大受控 scope，不允许升级为全局默认 provider。

## DB2.F Relation Governance Postgres Freeze

DB2.F 冻结 DB2.2 - DB2.14 的 relation governance Postgres provider 结论：

- readiness、dual-write、shadow-read、scoped service mode、selected workspace canary 和 multi normal scope canary 均已通过。
- 当前冻结指标：`MismatchCount=0`、`PostgresFailureCount=0`、`ScopeLeakCount=0`。
- readiness registry 中 `RelationGovernance=ReadyForLimitedScopeExpansion`。
- `GuardedPostgresPrimary` 只允许用于明确 allowlisted workspace / collection scope。
- `GlobalDefaultOn` 仍然禁止。
- fallback 与 comparison trace 是强制要求，缺失任一项必须 gate fail。

冻结说明见：

- `docs/relation-governance-postgres-freeze.md`
- `learning/readiness/learning-readiness-freeze-report.json`
- `learning/readiness/learning-runtime-change-readiness-gate.json`

## DB3 Learning Feedback Postgres Provider

DB3 新增 learning feedback / review / feature candidate 的 Postgres provider，只用于 diagnostics 和 parity，不切换默认 runtime provider：

- `PostgresLearningFeedbackStore`
- `PostgresLearningFeedbackReviewStore`
- `PostgresLearningFeatureCandidateStore`

默认 `FileSystem` 仍为 source of truth。DB3 不 dual-write、不 shadow-read、不训练、不改变 readiness、不影响 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-diagnostics
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-parity --cleanup-confirm
```

输出：

- `storage/postgres/postgres-learning-feedback-diagnostics.json`
- `storage/postgres/postgres-learning-feedback-diagnostics.md`
- `storage/postgres/postgres-learning-feedback-parity-report.json`
- `storage/postgres/postgres-learning-feedback-parity-report.md`

Parity 覆盖 feedback submit / list / filter / summary、review approve / reject / needs-redaction / needs-evidence、feature candidate upsert / list / export projection、metadataOnly / redactionMode / trainingUse roundtrip、duplicate feedback stable upsert 和 cleanup。

## DB3.1 Learning Feedback Readiness / Dual-write / Shadow-read Smoke

DB3.1 只验证 learning feedback Postgres provider 的 readiness、dual-write smoke、shadow-read smoke 和 provider quality，不切换默认 provider：

- FileSystem 仍为 source of truth。
- Postgres 只作为显式 smoke / comparison target。
- 不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- dual-write / shadow-read 默认关闭，只能通过 eval smoke 显式执行。

新增 DTO / coordinator：

- `LearningFeedbackPostgresReadinessGateReport`
- `LearningFeedbackDualWriteOptions`
- `LearningFeedbackShadowReadOptions`
- `LearningFeedbackDualWriteTrace`
- `LearningFeedbackShadowReadTrace`
- `LearningFeedbackDualWriteCoordinator`
- `LearningFeedbackShadowReadCoordinator`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-readiness-gate
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-dual-write-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-shadow-read-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-provider-quality
```

输出：

- `storage/postgres/postgres-learning-feedback-readiness-gate.json`
- `storage/postgres/postgres-learning-feedback-readiness-gate.md`
- `storage/postgres/postgres-learning-feedback-dual-write-smoke-report.json`
- `storage/postgres/postgres-learning-feedback-dual-write-smoke-report.md`
- `storage/postgres/postgres-learning-feedback-shadow-read-smoke-report.json`
- `storage/postgres/postgres-learning-feedback-shadow-read-smoke-report.md`
- `storage/postgres/postgres-learning-feedback-provider-quality-report.json`
- `storage/postgres/postgres-learning-feedback-provider-quality-report.md`
- `storage/postgres/postgres-learning-feedback-dual-write-traces.jsonl`
- `storage/postgres/postgres-learning-feedback-shadow-read-traces.jsonl`

当前本地结果：

- readiness gate: `GatePassed=true`
- dual-write smoke: `Mismatches=0`，`PostgresFailures=0`
- shadow-read smoke: `Mismatches=0`，`PostgresFailures=0`
- provider quality: `TraceCount=17`，`Recommendation=ReadyForScopedServiceMode`

## DB3.2 Learning Feedback Scoped Service Mode

DB3.2 只把 learning feedback Postgres provider 放入显式 scoped service mode smoke / gate。默认 runtime 仍是 FileSystem，不做训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增模型：

- `LearningFeedbackProviderMode`
- `LearningFeedbackProviderSwitchOptions`
- `LearningFeedbackScopedRule`
- `LearningFeedbackProviderSwitchTrace`
- `LearningFeedbackScopedServiceModeSmokeReport`
- `LearningFeedbackScopedServiceModeGateReport`

新增 artifact kind：

- `TraceLearningFeedbackProviderSwitch`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-scoped-service-mode-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-scoped-service-mode-gate
```

输出：

- `storage/postgres/postgres-learning-feedback-scoped-service-mode-smoke-report.json`
- `storage/postgres/postgres-learning-feedback-scoped-service-mode-smoke-report.md`
- `storage/postgres/postgres-learning-feedback-scoped-service-mode-gate.json`
- `storage/postgres/postgres-learning-feedback-scoped-service-mode-gate.md`
- `storage/postgres/postgres-learning-feedback-provider-switch-traces.jsonl`

GuardedPostgresPrimary 规则：

- provider quality 必须是 `ReadyForScopedServiceMode`。
- scope 必须命中 allowlist / scoped rule。
- 非 allowlisted scope 必须继续走 FileSystem。
- Postgres failure 时保留 FileSystem fallback。
- comparison trace 必须保留。
- 不允许 global default-on。

当前本地结果：

- scoped smoke `Recommendation=ReadyForSelectedFeedbackScope`
- scoped gate `Passed=true`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ExportProjectionParityPassed=true`
- `SummaryParityPassed=true`

## DB3.3 Learning Feedback Selected Normal Scope Canary

DB3.3 只在一个显式 selected normal workspace / collection 内运行 learning feedback `GuardedPostgresPrimary` canary。该阶段不做 global default on，不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增模型：

- `LearningFeedbackSelectedNormalScopeOptions`
- `LearningFeedbackSelectedNormalScopeCleanupMode`
- `LearningFeedbackSelectedNormalScopeCanaryReport`

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-learning-feedback-selected-normal-scope-canary
```

输出：

- `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-report.json`
- `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-report.md`
- `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-traces.jsonl`

Canary 覆盖：

- feedback submit / duplicate stable upsert
- `metadataOnly` / `redactionMode` / `trainingUse` roundtrip
- approve / reject / needs-redaction / needs-evidence
- feature candidate build / upsert / list
- feedback summary / review summary
- feature candidate export projection
- non-selected scope FileSystem check

安全边界：

- 前置要求 DB3.1 readiness、dual-write、shadow-read、provider quality 和 DB3.2 scoped gate 全部通过。
- selected scope 必须显式配置。
- non-selected scope 必须保持 FileSystem。
- fallback 和 comparison trace 必须保留。
- canary 生成的候选标记 `smoke_test_only` / `excludedFromTraining=true`，不得进入真实 trainable dataset。
- `CleanupMode=None` 不删除真实 feedback 数据；显式 cleanup 只按 canary feedback id 前缀清理。

当前本地结果：

- `Recommendation=ReadyForLimitedFeedbackScope`
- `OperationCount=18`
- `PostgresPrimaryReadCount=7`
- `PostgresPrimaryWriteCount=10`
- `ComparisonTraceCount=18`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- export / summary / review summary / feature candidate parity 均通过
## DB3.4 Learning Feedback Limited Scope Observation + Freeze Gate

- 新增 `eval postgres-learning-feedback-limited-scope-observation`、`eval postgres-learning-feedback-limited-scope-quality`、`eval postgres-learning-feedback-freeze-gate`。
- limited observation 只在 selected feedback scope 内执行反馈提交、重复 upsert、review 操作、feature candidate export/summary parity 和 non-selected scope FileSystem 检查。
- freeze gate 通过后状态为 `LearningFeedbackPostgres=ReadyForScopedServiceMode`，但默认 provider 仍是 `FileSystem`。
- 允许模式仅为 allowlisted scope 下的 `GuardedPostgresPrimary`，必须保留 fallback 与 comparison trace。
- 禁止 global default-on、auto-training、auto-readiness-change。

## DB4 Job Queue Postgres Provider

- 新增 Postgres job queue provider diagnostics、parity 和 lease contract smoke。
- schema 升级为 `cc-schema-v5`，`context_jobs` 增加 lease/heartbeat 字段与 kind/lease/attempt 索引。
- 新增 eval：
  - `postgres-job-queue-diagnostics`
  - `postgres-job-queue-parity --cleanup-confirm`
  - `postgres-job-queue-lease-smoke --cleanup-confirm`
- 报告输出：
  - `storage/postgres/postgres-job-queue-diagnostics.json`
  - `storage/postgres/postgres-job-queue-parity-report.json`
  - `storage/postgres/postgres-job-queue-lease-smoke-report.json`
- 默认 job queue provider 不变，`UseForRuntime=false`；不接 worker loop，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## DB4.1 Job Queue Dual-write / Shadow-read Smoke

DB4.1 只为 job queue provider 增加 explicit smoke coordinator、trace 和 provider quality gate。默认 job queue provider 不切换，不接 runtime worker loop。

新增 options：

- `JobQueueDualWriteOptions`
- `JobQueueShadowReadOptions`

默认均为 `Enabled=false`，Postgres write/read 均关闭；trace 默认开启，Postgres failure 只进入 fallback/trace，不影响 FileSystem 正式路径。

新增 trace artifact：

- `TraceJobQueueDualWrite`
- `TraceJobQueueShadowRead`

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-dual-write-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-shadow-read-smoke --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-provider-quality
```

输出：

- `storage/postgres/postgres-job-queue-dual-write-smoke-report.json`
- `storage/postgres/postgres-job-queue-dual-write-smoke-report.md`
- `storage/postgres/postgres-job-queue-shadow-read-smoke-report.json`
- `storage/postgres/postgres-job-queue-shadow-read-smoke-report.md`
- `storage/postgres/postgres-job-queue-provider-quality-report.json`
- `storage/postgres/postgres-job-queue-provider-quality-report.md`
- `storage/postgres/postgres-job-queue-dual-write-traces.jsonl`
- `storage/postgres/postgres-job-queue-shadow-read-traces.jsonl`

通过条件：

- mismatch count = 0
- Postgres failure count = 0
- lease / retry / dead-letter parity passed
- count/list/filter parity passed
- cleanup performed

通过后 recommendation 为 `ReadyForScopedWorkerCanary`，仍不表示 runtime worker 可以切换。

## DB4.2 Job Queue Scoped Worker Canary

DB4.2 只在隔离 canary workspace / collection 中模拟 scoped worker，验证 Postgres queue 作为 selected scope primary 的 lease / retry / dead-letter 合约。全局 job queue provider 不切换，真实 runtime worker loop 不接入。

新增类型：

- `JobQueueWorkerProviderMode`
- `JobQueueScopedWorkerCanaryOptions`
- `JobQueueScopedWorkerCanaryTrace`
- `PostgresJobQueueScopedWorkerCanaryReport`
- `PostgresJobQueueScopedWorkerQualityReport`

新增 trace artifact：

- `TraceJobQueueScopedWorkerCanary`

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-scoped-worker-canary --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-scoped-worker-quality
```

输出：

- `storage/postgres/postgres-job-queue-scoped-worker-canary-report.json`
- `storage/postgres/postgres-job-queue-scoped-worker-canary-report.md`
- `storage/postgres/postgres-job-queue-scoped-worker-quality-report.json`
- `storage/postgres/postgres-job-queue-scoped-worker-quality-report.md`
- `storage/postgres/postgres-job-queue-scoped-worker-traces.jsonl`

Canary job 只使用安全测试载荷：

- `canary.noop`
- `canary.fail-once-then-succeed`
- `canary.always-fail-to-dead-letter`
- lease conflict / expired lease reacquire / cancel pending

边界：

- selected canary scope 使用 Postgres queue。
- non-selected scope 继续 FileSystem/InMemory。
- provider quality 必须为 `ReadyForScopedWorkerCanary`。
- Postgres failure / mismatch / scope leak 会阻断 recommendation。
- runtime worker global provider 必须保持 unchanged。

当前本地结果：

- canary `Recommendation=ReadyForLimitedWorkerScope`
- quality `Passed=True`
- `JobCount=6`
- `CompletedCount=4`
- `RetriedCount=2`
- `DeadLetterCount=1`
- `LeaseAcquireCount=8`
- `LeaseConflictCount=1`
- `LeaseExpiredReacquireCount=1`
- `HeartbeatCount=1`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- `NonSelectedScopeRemainsFileSystem=true`

## DB4.3 Job Queue Limited Worker Scope Observation

DB4.3 继续限定在同一个 isolated selected worker scope 中执行更长 observation window。全局 job queue provider 不切换，不接真实生产 worker loop，非 canary scope 继续 FileSystem/InMemory。

新增类型：

- `JobQueueLimitedWorkerScopeObservationOptions`
- `JobQueueLimitedWorkerScopeObservationTrace`
- `PostgresJobQueueLimitedWorkerScopeObservationReport`
- `PostgresJobQueueLimitedWorkerScopeQualityReport`

新增 trace artifact：

- `TraceJobQueueLimitedWorkerScopeObservation`

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-limited-worker-scope-observation --cleanup-confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-limited-worker-scope-quality
```

输出：

- `storage/postgres/postgres-job-queue-limited-worker-scope-observation-report.json`
- `storage/postgres/postgres-job-queue-limited-worker-scope-observation-report.md`
- `storage/postgres/postgres-job-queue-limited-worker-scope-quality-report.json`
- `storage/postgres/postgres-job-queue-limited-worker-scope-quality-report.md`
- `storage/postgres/postgres-job-queue-limited-worker-scope-traces.jsonl`

Observation 覆盖：

- 多个 noop jobs complete
- fail-once jobs retry 后 complete
- always-fail jobs 达到 max retry 后 dead-letter
- long-running heartbeat 续租
- expired lease reacquire
- lease conflict 不重复执行
- pending job cancel
- non-selected scope 保持 FileSystem/InMemory

当前本地结果：

- observation `Recommendation=ReadyForJobQueueFreezeGate`
- quality `Passed=True`
- `JobCount=11`
- `CompletedCount=8`
- `RetriedCount=4`
- `DeadLetterCount=2`
- `CancelledCount=1`
- `LeaseAcquireCount=15`
- `LeaseConflictCount=1`
- `LeaseExpiredReacquireCount=1`
- `HeartbeatCount=2`
- `DuplicateExecutionCount=0`
- `LeaseViolationCount=0`
- `RetryViolationCount=0`
- `DeadLetterViolationCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- `NonSelectedScopeRemainsFileSystem=true`
- `RuntimeWorkerGlobalProviderUnchanged=true`

## DB4.F Job Queue Postgres Scoped Worker Freeze Gate

DB4.F 只冻结 Job Queue Postgres provider 的 scoped worker 结论，不切换全局 job queue provider，不接真实生产 worker loop。

新增文档：

- `docs/job-queue-postgres-freeze.md`

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-job-queue-freeze-gate
```

输出：

- `storage/postgres/postgres-job-queue-freeze-gate.json`
- `storage/postgres/postgres-job-queue-freeze-gate.md`

Freeze 状态：

- `JobQueuePostgres=ReadyForScopedWorkerMode`
- `DefaultProvider=ExistingProvider`
- `AllowedMode=GuardedPostgresPrimary only for explicit allowlisted worker scopes`
- Required: lease / heartbeat / retry / dead-letter quality gates
- Forbidden: `GlobalWorkerProviderSwitch`
- Forbidden: `ProductionWorkerLoopSwitchWithoutGate`

当前本地结果：

- freeze gate `Passed=True`
- `DuplicateExecutionCount=0`
- `LeaseViolationCount=0`
- `RetryViolationCount=0`
- `DeadLetterViolationCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- `NonSelectedScopeRemainsFileSystem=true`
- `RuntimeWorkerGlobalProviderUnchanged=true`

## DB5.0 VectorIndexStore pgvector Provider Foundation

DB5.0 进入 VectorIndexStore / pgvector provider 基础阶段，只实现 schema、provider、diagnostics、compatibility smoke 和 basic query smoke。

新增 provider：

- `PostgresVectorIndexStore`

覆盖操作：

- upsert vector entry
- get by entry id
- delete by entry id
- list by workspace / collection / provider / model / dimension
- nearest neighbor query
- cleanup test entries

schema 基线升级到 `cc-schema-v6`，`vector_index_entries` 补齐：

- `source_id`
- `source_kind`
- `provider_id`
- `model_id`
- `normalized`
- `metadata_json`

新增索引：

- workspace / collection scope
- provider / model / dimension
- source id

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-diagnostics
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-compatibility
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-smoke --cleanup-confirm
```

输出：

- `storage/postgres/postgres-vector-diagnostics.json`
- `storage/postgres/postgres-vector-diagnostics.md`
- `storage/postgres/postgres-vector-compatibility.json`
- `storage/postgres/postgres-vector-compatibility.md`
- `storage/postgres/postgres-vector-provider-smoke-report.json`
- `storage/postgres/postgres-vector-provider-smoke-report.md`

安全边界：

- `UseForRuntime=false`
- 不接正式 retrieval
- 不改变 vector readiness
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output
- query 必须显式 provider/model/dimension，维度不匹配直接阻断，避免静默混用 embedding space

当前本地结果：

- diagnostics `Recommendation=ReadyForVectorParityEval`
- `ProviderEnabled=true`
- `ConnectionAvailable=true`
- `PgVectorAvailable=true`
- `SchemaVersion=cc-schema-v6`
- `TableExists=true`
- `MissingIndexCount=0`
- compatibility `Recommendation=ReadyForVectorParityEval`
- smoke `Recommendation=ReadyForVectorParityEval`
- `InsertedCount=3`
- `UpsertedCount=1`
- `QueryCount=2`
- `MismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`
- `CleanupPerformed=true`

## DB5.1 VectorIndexStore FileSystem/Postgres Parity Eval

DB5.1 只做 FileSystem / Postgres vector index parity eval，不绑定 `IVectorIndexStore`，不接 formal retrieval。

新增 eval：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-parity --cleanup-confirm
```

输出：

- `storage/postgres/postgres-vector-parity-report.json`
- `storage/postgres/postgres-vector-parity-report.md`

## DB5.2 Provider-scoped Reindex into pgvector

DB5.2 只做 provider-scoped reindex into pgvector。该阶段不绑定 `IVectorIndexStore`，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` / package output，也不改变 VectorRetrieval readiness。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-scoped-reindex-plan
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-scoped-reindex-apply --confirm
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-provider-scoped-reindex-quality
```

报告：

- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.md`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.md`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.md`

当前本地结果：

- CandidateCount: `158`
- PlannedInsertCount: `0`
- PlannedUpdateCount: `0`
- PlannedSkipCount: `158`
- AppliedInsertCount: `0`
- AppliedUpdateCount: `0`
- IndexedEntryCountAfterApply: `158`
- MetadataRoundtripMismatchCount: `0`
- Recommendation: `ReadyForPgVectorQueryPreview`
- UseForRuntime: `false`

说明：首次 DB5.2 apply 已将 158 条 deterministic provider-scoped entries 写入 pgvector；最终验证重跑为幂等 no-op，因此最新 apply report 显示 insert/update 均为 0。

安全边界：

- apply 必须显式 `--confirm`。
- plan / apply / quality 都要求 provider / model / dimension / normalization 兼容。
- metadata_json 保留 source / lifecycle / eligibility 相关 metadata。
- FileSystem formal vector store 不写入，现有 `VectorIndexService` runtime path 不改变。

Parity 覆盖：

- deterministic vector entries upsert
- duplicate upsert semantics
- get by entry id
- list by workspace / collection / provider / model / dimension
- nearest-neighbor topK query
- similarity score ordering parity
- delete by entry id
- metadata / sourceId / sourceKind roundtrip
- dimension mismatch blocked
- provider/model mismatch does not silently mix entries
- cleanup test entries

当前本地结果：

- `Recommendation=ReadyForProviderScopedReindex`
- `OperationCount=14`
- `FileSystemEntryCount=3`
- `PostgresEntryCount=3`
- `InsertedCount=4`
- `UpsertedCount=1`
- `DeletedCount=1`
- `QueryCount=3`
- `MismatchCount=0`
- `OrderingMismatchCount=0`
- `ScoreDeltaMax=0.00000002`
- `MetadataMismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`
- `CleanupPerformed=true`
- `UseForRuntime=false`

## DB5.3 PgVector Query Preview

DB5.3 只做 pgvector query preview 和 FileSystem 临时 baseline 对比。该阶段不绑定正式 `IVectorIndexStore`，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` / package output，也不改变 VectorRetrieval readiness。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-query-preview
```

报告：

- `storage/postgres/postgres-vector-query-preview-report.json`
- `storage/postgres/postgres-vector-query-preview-report.md`

当前本地结果：

- QueryCount: `113`
- CandidateCount: `1130`
- PgVectorCandidateCount: `1130`
- FileSystemCandidateCount: `1130`
- TopKOverlapCount: `1130`
- TopKOverlapRate: `100.00%`
- OrderingMismatchCount: `0`
- ScoreDeltaMax: `0.00000009`
- MetadataMismatchCount: `0`
- EligibilityMetadataMismatchCount: `0`
- RiskProjectionMismatchCount: `0`
- DimensionMismatchBlocked: `true`
- ProviderModelMismatchBlocked: `true`
- Recommendation: `ReadyForPgVectorShadowEval`
- UseForRuntime: `false`

安全边界：

- provider / model / dimension 必须显式匹配，不静默混用 embedding space。
- FileSystem 对比只使用临时 index，不写正式 FileSystem vector store。
- eligibility / risk / target section 投影复用现有 vector preview policy，只用于报告，不进入正式 retrieval。

## DB5.4 PgVector Shadow Eval

DB5.4 只做 pgvector shadow eval 和 FileSystem 临时 baseline 对比。该阶段复用现有 vector shadow eval 数据集、profile 和 candidate eligibility policy，报告 recall / MRR / risk / formal output / projection parity；不绑定正式 `IVectorIndexStore`，不接 formal retrieval，不改变 retrieval / planning / scoring / `PackingPolicy` / package output，也不改变 VectorRetrieval readiness。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-shadow-eval
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-shadow-eval-a3
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-shadow-eval-extended
```

报告：

- `storage/postgres/postgres-vector-shadow-eval-a3.json`
- `storage/postgres/postgres-vector-shadow-eval-a3.md`
- `storage/postgres/postgres-vector-shadow-eval-extended.json`
- `storage/postgres/postgres-vector-shadow-eval-extended.md`
- `storage/postgres/postgres-vector-shadow-eval-summary.json`
- `storage/postgres/postgres-vector-shadow-eval-summary.md`

当前本地结果：

- Summary Recommendation: `ReadyForVectorPostgresFreeze`
- A3: `SampleCount=50`，`RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- A3 parity: `TopKOverlapRate=100.00%`，`OrderingMismatchCount=0`，`MetadataMismatchCount=0`，`EligibilityMetadataMismatchCount=0`，`RiskProjectionMismatchCount=0`
- Extended: `SampleCount=113`，`RecallDelta=0`，`RiskAfterPolicy=0`，`FormalOutputChanged=0`
- Extended parity: `TopKOverlapRate=100.00%`，`OrderingMismatchCount=0`，`MetadataMismatchCount=0`，`EligibilityMetadataMismatchCount=0`，`RiskProjectionMismatchCount=0`
- UseForRuntime: `false`

安全边界：

- shadow eval 只证明 pgvector 与 FileSystem vector store 的读路径和投影一致。
- 该结论不提升 VectorRetrieval readiness，不允许正式 retrieval runtime switch。
- FileSystem baseline 仍为临时 index，不写正式 FileSystem vector store。

## DB5.F Vector Postgres Provider Freeze

DB5.F 冻结 pgvector provider 为 preview / shadow / eval-only storage。

新增 CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval postgres-vector-freeze-gate
```

输出：

- `storage/postgres/postgres-vector-freeze-gate.json`
- `storage/postgres/postgres-vector-freeze-gate.md`

冻结状态：

- `VectorPostgresProvider=ReadyForPreviewShadowStorage`
- `DefaultVectorStore=unchanged`
- `UseForRuntime=false`
- `FormalRetrievalAllowed=false`
- Allowed：preview / shadow / eval only。
- Required：正式 retrieval 前必须先通过 Vector V4 readiness gate。
- Forbidden：未通过 V4 gate 前禁止 formal retrieval switch、正式 `IVectorIndexStore` 绑定、`PackingPolicy` / package output 集成。

## V3.10 Qwen3 PgVector Scope

Qwen3 provider 也执行了 pgvector provider-scoped reindex / query preview / shadow eval。该 scope 使用：

- ProviderId：`qwen3-embedding-0.6b-onnx`
- ModelId：`qwen3-embedding-0.6b`
- Dimension：`1024`
- Normalized：`true`

当前结果：

- provider-scoped reindex quality：`ReadyForPgVectorQueryPreview`
- IndexedEntryCountAfterApply：`158`
- MetadataRoundtripMismatchCount：`0`
- query preview：`BlockedByRiskProjectionMismatch`
- shadow eval summary：`BlockedByProjectionMismatch`
- provider metadata sanity audit：`Passed=true`
- UseForRuntime：`false`

结论：

- Qwen3 pgvector scope 当前不能提升 VectorRetrieval readiness。
- 该结果只作为 provider comparison / storage projection 诊断，不允许 formal retrieval switch。
- 若 pgvector qwen3 report 的 `ProviderType` / `ProviderId` / `ModelId` / `Dimension` / model path 不匹配，V3.10.F freeze 必须转为 `Inconclusive / BlockedByProviderConfigurationMismatch`。
