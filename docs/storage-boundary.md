# Storage Boundary

DB0 定义 artifact plane 与 operational/index store 的责任边界。本阶段只输出分类、诊断和迁移候选，不迁移数据库，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## Responsibility Kinds

- `ArtifactOnly`：评测、报告、冻结快照等只读 artifact，优先继续使用 FileSystem。
- `TraceOnly`：runtime trace、shadow trace、tool-call trace，优先继续使用 FileSystem。
- `ExportOnly`：JSONL / Markdown / report export，优先继续使用 FileSystem。
- `SnapshotOnly`：诊断快照、archive、compaction run 等不可作为主状态。
- `OperationalState`：ContextItem、MemoryItem、RelationItem、ConstraintState、JobRecord 等长期运行状态，推荐数据库承载。
- `IndexState`：VectorIndexEntry 等索引状态，推荐专用 index store；向量索引后续可评估 pgvector。
- `MigrationCandidate`：需要后续非破坏性迁移计划的候选。
- `DatabaseRecommended`：推荐长期迁移到数据库或索引存储。
- `FileSystemPreferred`：推荐保留为文件 artifact。

## Current Classification

- EvalReport / LearningReport / ReadinessGate：`ArtifactOnly` / `FileSystemPreferred`
- TraceRetrieval / ToolCallTrace / ShadowTrace：`TraceOnly` / `FileSystemPreferred`
- FeedbackExport：`ExportOnly` / `FileSystemPreferred`
- ContextItem / MemoryItem：`OperationalState` / `DatabaseRecommended`
- RelationItem / ConstraintState：`OperationalState` / `DatabaseRecommended`
- VectorIndexEntry：`IndexState` / `DatabaseRecommended`，后续可作为 pgvector candidate
- JobRecord：`OperationalState` / `DatabaseRecommended`

## Report

运行：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval storage-boundary-report
```

输出：

- `storage/storage-boundary-report.json`
- `storage/storage-boundary-report.md`

报告包含 artifact kind 总数、artifact-only / operational-state / index-state 计数、database-recommended / filesystem-preferred 计数、migration candidates、high-priority migration candidates、blocked reasons 和 recommended next phases。

## Boundary Rules

- FileSystem layout 继续承载 artifact、trace、export 和 snapshot。
- Operational state 和 index state 只标记迁移建议，不在 DB0 中迁移。
- 不删除 legacy 文件，不做破坏性迁移。
- 新的运行状态写入路径不得因为 FS layout 完成而默认继续强化为文件主存储。
- 任何数据库迁移必须先经过单独 phase 设计 provider、兼容读取、双写/回滚策略和验证 gate。

## DB1 Postgres Baseline

DB1 增加 PostgreSQL operational store foundation，但仍不迁移数据、不切换 provider。

- `PostgresStoreOptions` / `PostgresOptions` 增加 `Enabled`、`SchemaName`、`ProviderId` 等配置面。
- 默认 `AutoMigrate=false`。
- 新增 `context_schema_migrations` migration table。
- 新增 baseline schema migration 和 diagnostics。
- API / CLI 的 migration apply 必须显式 confirm。
- diagnostics 和 report 只输出 redacted connection string。

后续 DB2+ 的 CRUD / query store 可以引入高性能轻量 ORM；DB1 的 DDL / migration / diagnostics 保持 Npgsql 原生 SQL。

## DB2 RelationStore Provider Boundary

DB2 首个 operational provider 选择 `RelationStore`，但只进入显式测试和诊断面：

- runtime active provider 仍为 FileSystem。
- Postgres provider 不 dual-write、不 shadow-read。
- parity eval 使用隔离 workspace / collection，不使用业务 eval sampleId / itemId 特判。
- relation fixture 只包含通用 source / target / relation type / lifecycle metadata。
- cleanup 需要显式 `--cleanup-confirm`。

RelationStore 仍属于 `OperationalState / DatabaseRecommended`，但 DB2 只验证 provider parity，不启动迁移计划。

## DB2.1 Relation Review / Diagnostics Boundary

Relation review history 与 relation diagnostics projection 属于 operational governance state，长期推荐数据库承载。DB2.1 只完成 Postgres provider 与 parity：

- review / diagnostics provider 不作为默认 runtime provider。
- parity 数据写入隔离 workspace / collection。
- diagnostics snapshot 是 projection/report store，不替代即时 graph validation。
- 不使用业务 eval sampleId / itemId 特判。
- cleanup 需要显式 `--cleanup-confirm`。

`relation_diagnostics` 是 DB2.1 的新增 schema table；它只承载诊断快照，不改变 relation expansion、retrieval 或 package 输出。

## DB2.2 Relation Governance Boundary

DB2.2 增加统一 relation governance provider readiness gate。它把 DB2 的 relation edge parity 与 DB2.1 的 review / diagnostics parity 合并检查，但仍然不进入 runtime provider 切换。

边界规则：

- `PostgresRelationStore`、`PostgresRelationReviewStore`、`PostgresRelationDiagnosticsStore` 仍只用于 explicit diagnostics / parity / gate。
- runtime active provider 仍为 `FileSystemRelationStore`。
- gate 通过只允许进入后续 dual-write 方案设计，不代表已启用 dual-write。
- `CanShadowRead=false`、`CanRuntimeSwitch=false` 是当前阶段的固定结论。
- governance parity 使用隔离 workspace / collection 和通用 relation fixture，不使用业务 eval sampleId / itemId / fixture 特判。
- cleanup 必须显式 `--cleanup-confirm`，缺失 cleanup 会导致 readiness gate fail。

报告：

- `storage/postgres/postgres-relation-governance-parity-report.json`
- `storage/postgres/postgres-relation-governance-readiness-gate.json`

推荐结论 `ReadyForDualWrite` 只表示 provider 契约、schema 和清理条件已经满足，可以进入下一阶段的双写设计；它不改变任何 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## DB2.3 Relation Dual-write Boundary

DB2.3 只允许在显式 eval smoke 中启用 relation governance dual-write：

- FileSystem 仍是 relation edge / review / diagnostics 的正式 source of truth。
- Postgres 只作为 dual-write target，不参与 runtime read。
- `RelationGovernanceDualWriteOptions` 默认关闭，`WritePostgres=false`。
- `FallbackOnPostgresFailure=true` 时，Postgres 写失败只能记录 trace，不阻断 FileSystem 正式写。
- `TraceRelationDualWrite` 属于 `TraceOnly / FileSystemPreferred`，用于后续质量门禁。
- smoke 数据写入隔离 workspace / collection，cleanup 必须显式 `--cleanup-confirm`。
- 不 dual-read、不 shadow-read、不 runtime switch、不迁移历史数据。

双写质量报告：

- `storage/postgres/postgres-relation-dual-write-quality-report.json`
- `storage/postgres/postgres-relation-dual-write-quality-report.md`

报告出现 mismatch 或 Postgres failure 时，后续 shadow-read 阶段必须阻断。

## DB2.4 Relation Shadow-read Boundary

DB2.4 只允许在显式 eval smoke 中启用 relation governance shadow-read comparison：

- FileSystem read result 是正式返回值。
- Postgres read result 只用于 hash comparison 和 latency trace。
- Postgres read failure 只记录 fallback / mismatch，不影响 FileSystem 读。
- `RelationGovernanceShadowReadOptions` 默认关闭，`ReadPostgres=false`。
- `TraceRelationShadowRead` 属于 `TraceOnly / FileSystemPreferred`。
- 不启用 runtime provider switch。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Shadow-read 质量报告：

- `storage/postgres/postgres-relation-shadow-read-quality-report.json`
- `storage/postgres/postgres-relation-shadow-read-quality-report.md`

报告出现 mismatch 或 Postgres read failure 时，后续 guarded provider switch 必须阻断。

## DB2.5 Relation Provider Switch Boundary

DB2.5 只允许在显式 eval smoke / gate 中验证 relation governance guarded provider switch：

- 默认 mode 保持 `FileSystemPrimary`。
- `RelationGovernanceProviderSwitchOptions` 默认关闭，`Enabled=false`。
- `GuardedPostgresPrimary` 必须通过 readiness gate 和 shadow-read quality gate。
- `GuardedPostgresPrimary` 必须限制在 workspace / collection allowlist。
- Postgres failure 时可 fallback FileSystem。
- mismatch 必须写入 `TraceRelationProviderSwitch`，并按 `FailClosedOnMismatch` 阻断或 fallback。
- `TraceRelationProviderSwitch` 属于 `TraceOnly / FileSystemPreferred`。
- 不允许 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Provider switch gate 报告：

- `storage/postgres/postgres-relation-provider-switch-smoke-report.json`
- `storage/postgres/postgres-relation-provider-switch-smoke-report.md`
- `storage/postgres/postgres-relation-provider-switch-gate.json`
- `storage/postgres/postgres-relation-provider-switch-gate.md`

Gate 通过只表示 relation governance provider 具备受控切换能力，不代表全局 runtime provider 已切换。回滚路径是 `FileSystemPrimary` 或禁用 switch options。

## DB2.6 Relation Runtime Canary Boundary

DB2.6 只允许在隔离 canary scope 中启用 `GuardedPostgresPrimary`：

- 默认 canary options 关闭，`Enabled=false`。
- 默认 canary scope 为 `contextcore_canary/relation-governance-canary`。
- 必须通过 provider switch gate。
- 必须配置 workspace / collection allowlist。
- 只验证 relation edge、relation review、relation diagnostics。
- Postgres primary read/write 结果必须与 FileSystem fallback / comparison trace 一致。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Canary 报告：

- `storage/postgres/postgres-relation-runtime-canary-report.json`
- `storage/postgres/postgres-relation-runtime-canary-report.md`

`ReadyForScopedServiceMode` 只允许后续讨论 scoped service mode，不允许直接升级为全局默认 provider。

## DB2.7 Relation Scoped Service Mode Boundary

DB2.7 只允许在显式 allowlist scope 内启用 relation governance 的 `GuardedPostgresPrimary`：

- 默认 `RelationGovernanceProviderSwitchOptions.Enabled=false`。
- 未命中 allowlist 的 workspace / collection 仍由 FileSystem 处理。
- 命中 allowlist 时也必须通过 governance readiness gate、provider switch gate 和 runtime canary gate。
- mismatch / Postgres failure 必须写 trace；按配置 fail-closed 或 fallback。
- FileSystem fallback 保留，方便回滚。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Scoped service mode 报告：

- `storage/postgres/postgres-relation-scoped-service-mode-smoke-report.json`
- `storage/postgres/postgres-relation-scoped-service-mode-smoke-report.md`
- `storage/postgres/postgres-relation-scoped-service-mode-gate.json`
- `storage/postgres/postgres-relation-scoped-service-mode-gate.md`

Gate 通过只表示 relation governance 在受控 scope 内可使用 Postgres primary；其他 operational state 仍按 DB0 boundary 保持原 provider。

## DB2.8 Relation Scoped Extended Canary Boundary

DB2.8 继续限制在隔离 canary scope 内验证 relation governance：

- 默认 `RelationGovernanceExtendedCanaryOptions.Enabled=false`。
- 默认 scope 为 `contextcore_canary/relation-governance-extended-canary`。
- 必须通过 scoped service mode gate。
- 只覆盖 relation edge、review、diagnostics、replacement chain 与 graph expansion preview parity。
- Postgres primary result 必须与 FileSystem comparison trace 一致。
- graph preview section routing / blocked reason 不能因 provider 改变。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Extended canary 报告：

- `storage/postgres/postgres-relation-scoped-extended-canary-report.json`
- `storage/postgres/postgres-relation-scoped-extended-canary-report.md`
- `storage/postgres/postgres-relation-scoped-extended-canary-traces.jsonl`

`ReadyForSelectedWorkspaceCanary` 只允许进入选定 workspace canary 讨论，不允许升级为全局默认 provider。

## DB2.9 Relation Selected Workspace Canary Boundary

DB2.9 只允许一个受控 selected workspace / collection 使用 `GuardedPostgresPrimary` 做 canary：

- 默认 `RelationGovernanceSelectedWorkspaceCanaryOptions.Enabled=false`。
- selected workspace / collection 必须明确配置。
- 必须通过 DB2.2 - DB2.8 的全部 relation governance gate。
- 必须保持 P15 gate 通过。
- 非 selected scope 仍必须保持 FileSystem。
- mismatch / Postgres failure 必须写入 trace，并阻断 recommendation。
- 延迟超过阈值时 recommendation 为 `BlockedByLatency`。
- 不做 global default on。
- 不迁移所有历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Selected canary 报告：

- `storage/postgres/postgres-relation-selected-workspace-canary-report.json`
- `storage/postgres/postgres-relation-selected-workspace-canary-report.md`
- `storage/postgres/postgres-relation-selected-workspace-canary-traces.jsonl`

`ReadyForScopedServiceModeExpansion` 只表示可以扩大 scoped allowlist 讨论，不允许跳过边界直接切换全局 runtime provider。

## DB2.10 Relation Scoped Expansion Boundary

DB2.10 只允许多个显式 `ScopedRules` 命中的 workspace / collection 使用 relation governance `GuardedPostgresPrimary`：

- 默认仍不 global default on。
- 全局 `RelationGovernanceProviderSwitchOptions.Mode` 可以保持 `FileSystemPrimary`。
- 命中 `ScopedRules` 时才允许进入 `GuardedPostgresPrimary`。
- 每个 scope 必须有 `ScopeName`、`WorkspaceId`、`CollectionId`、`RolloutStage` 和启用状态。
- 非 allowlisted scope 必须保持 FileSystem，并作为 gate 条件验证。
- mismatch / Postgres failure 必须写 trace 并阻断 expansion gate。
- fallback 与 comparison trace 必须保持启用。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Scoped expansion 报告：

- `storage/postgres/postgres-relation-scoped-expansion-plan.json`
- `storage/postgres/postgres-relation-scoped-expansion-plan.md`
- `storage/postgres/postgres-relation-scoped-expansion-smoke-report.json`
- `storage/postgres/postgres-relation-scoped-expansion-smoke-report.md`
- `storage/postgres/postgres-relation-scoped-expansion-gate.json`
- `storage/postgres/postgres-relation-scoped-expansion-gate.md`
- `storage/postgres/postgres-relation-scoped-expansion-traces.jsonl`

`ReadyForScopedExpansion` 只允许扩大明确 scope 的 canary / service-mode 覆盖范围，不允许升级为全局默认 provider。

## DB2.11 Relation Scoped Observation Boundary

DB2.11 只允许对通过 scoped expansion gate 的显式 allowlisted scopes 做运行时观察窗口：

- 默认 `RelationGovernanceScopedObservationOptions.Enabled=false`。
- observation 必须依赖 scoped expansion gate。
- 至少覆盖 2 个 allowlisted scope 和 1 个 non-allowlisted scope。
- 非 allowlisted scope 必须保持 FileSystem。
- fallback 与 comparison trace 必须保持开启。
- mismatch / Postgres failure / scope leak / P95 latency 超阈值会阻断 quality gate。
- cleanup 默认关闭，必须显式 `--cleanup-confirm` 才清理 observation 数据。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Scoped observation 报告：

- `storage/postgres/postgres-relation-scoped-observation-report.json`
- `storage/postgres/postgres-relation-scoped-observation-report.md`
- `storage/postgres/postgres-relation-scoped-observation-traces.jsonl`
- `storage/postgres/postgres-relation-scoped-observation-quality-report.json`
- `storage/postgres/postgres-relation-scoped-observation-quality-report.md`

`ReadyForSelectedNormalWorkspace` 只允许进入更接近真实业务 scope 的受控观察，不允许切换全局 runtime provider。

## DB2.12 Relation Selected Normal Workspace Boundary

DB2.12 只允许一个显式 selected normal workspace / collection 使用 relation governance `GuardedPostgresPrimary` 做 canary：

- 默认 `RelationGovernanceSelectedNormalWorkspaceOptions.Enabled=false`。
- selected normal workspace / collection 必须明确配置。
- 必须通过 DB2.2 - DB2.11 的 relation governance gate。
- 必须保持 P15 gate 通过。
- 非 selected normal scope 必须保持 FileSystem。
- mismatch / Postgres failure / scope leak 必须写 trace 并阻断 recommendation。
- fallback 与 comparison trace 必须保持开启。
- cleanup 默认关闭；normal scope 不允许使用全 scope 删除作为常规清理手段。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Selected normal workspace canary 报告：

- `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.json`
- `storage/postgres/postgres-relation-selected-normal-workspace-canary-report.md`
- `storage/postgres/postgres-relation-selected-normal-workspace-canary-traces.jsonl`

`ReadyForLimitedNormalScope` 只允许继续在同一 selected normal scope 上做有限观察，不允许切换全局 runtime provider。

## DB2.13 Relation Limited Normal Scope Observation Boundary

DB2.13 只允许在 DB2.12 通过的 selected normal workspace / collection 上做更长 observation：

- 默认 `RelationGovernanceLimitedNormalScopeObservationOptions.Enabled=false`。
- observation 必须依赖 selected normal workspace canary passed。
- fallback 与 comparison trace 必须保持开启。
- 非 selected normal scope 必须保持 FileSystem。
- mismatch / Postgres failure / scope leak / P95 latency 超阈值会阻断 quality gate。
- `CleanupMode=None` 不删除 normal workspace 数据。
- 显式 cleanup 只允许删除 canary relation id，不允许按 normal scope 批量删除。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Limited normal scope observation 报告：

- `storage/postgres/postgres-relation-limited-normal-scope-observation-report.json`
- `storage/postgres/postgres-relation-limited-normal-scope-observation-report.md`
- `storage/postgres/postgres-relation-limited-normal-scope-quality-report.json`
- `storage/postgres/postgres-relation-limited-normal-scope-quality-report.md`
- `storage/postgres/postgres-relation-limited-normal-scope-traces.jsonl`

`ReadyForMultiNormalScopeCanary` 只允许进入多 normal scope canary，不允许切换全局 runtime provider。

## DB2.14 Relation Multi Normal Scope Boundary

DB2.14 只允许多个显式 normal scope 进入 relation governance canary：

- 默认 `RelationGovernanceMultiNormalScopeCanaryOptions.Enabled=false`。
- `Scopes` 至少包含 2 个启用的 `RelationGovernanceNormalScopeRule`。
- 每个 scope 必须明确 `ScopeName`、`WorkspaceId`、`CollectionId`、`RolloutStage` 和 `Enabled`。
- 必须先通过 limited normal scope observation quality。
- 非 allowlisted scope 必须保持 FileSystem。
- cross-scope 数据泄漏会阻断 recommendation。
- mismatch / Postgres failure / scope leak / P95 latency 超阈值会阻断 quality gate。
- fallback 与 comparison trace 必须保持开启。
- `CleanupMode=None` 不删除 normal workspace 数据；显式 cleanup 只删除 canary relation id。
- 不做 global default on。
- 不迁移历史业务数据。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

Multi normal scope canary 报告：

- `storage/postgres/postgres-relation-multi-normal-scope-canary-report.json`
- `storage/postgres/postgres-relation-multi-normal-scope-canary-report.md`
- `storage/postgres/postgres-relation-multi-normal-scope-quality-report.json`
- `storage/postgres/postgres-relation-multi-normal-scope-quality-report.md`
- `storage/postgres/postgres-relation-multi-normal-scope-traces.jsonl`

`ReadyForLimitedScopeExpansion` 只允许进入更多受控 scope 的扩展观察，不允许切换全局 runtime provider。

## DB2.F Relation Governance Freeze Boundary

Relation governance Postgres provider 当前冻结为 `ReadyForLimitedScopeExpansion`：

- 允许：`GuardedPostgresPrimary` only for allowlisted scopes。
- 必须：`FallbackToFileSystem=true`。
- 必须：`ContinueComparisonTrace=true`。
- 禁止：`GlobalDefaultOn`。
- 禁止：缺失 fallback 或 comparison trace 的 provider switch。

该冻结只扩大受控 scope 的观察权限，不代表 relation governance 可成为全局默认 provider。

## DB3 Learning Feedback Storage Boundary

Learning feedback、feedback review 和 feedback feature candidate 属于长期 operational learning state，目标方向是 DatabaseRecommended。但 DB3 只建立 Postgres provider 和 parity：

- 默认 FileSystem store 继续作为 source of truth。
- Postgres provider 只用于 explicit diagnostics / parity。
- 不 dual-write。
- 不 shadow-read。
- 不训练。
- 不自动改变 readiness。
- 不影响 retrieval、planning、scoring、`PackingPolicy` 或 package output。

DB3 provider 覆盖：

- `learning_feedback_events`
- `learning_feedback_reviews`
- `learning_feature_candidates`

当前阶段输出：

- `storage/postgres/postgres-learning-feedback-diagnostics.json`
- `storage/postgres/postgres-learning-feedback-parity-report.json`

## DB3.1 Learning Feedback Provider Smoke Boundary

DB3.1 允许 learning feedback Postgres provider 进入 explicit dual-write / shadow-read smoke，但仍不进入 runtime：

- `LearningFeedbackDualWriteOptions.Enabled=false` by default。
- `LearningFeedbackShadowReadOptions.Enabled=false` by default。
- FileSystem write/read 仍是正式路径。
- Postgres failure 不能破坏 FileSystem 正式写入。
- shadow-read mismatch 只写 trace / report。
- `UseForRuntime=false` 是 readiness gate 条件。
- quality 通过只表示可进入后续 scoped service mode 设计，不代表 runtime switch。

新增 trace artifact：

- `TraceLearningFeedbackDualWrite`
- `TraceLearningFeedbackShadowRead`

当前 smoke / quality 输出：

- `storage/postgres/postgres-learning-feedback-readiness-gate.json`
- `storage/postgres/postgres-learning-feedback-dual-write-smoke-report.json`
- `storage/postgres/postgres-learning-feedback-shadow-read-smoke-report.json`
- `storage/postgres/postgres-learning-feedback-provider-quality-report.json`

## DB3.2 Learning Feedback Scoped Service Mode Boundary

DB3.2 允许 learning feedback provider 在显式 allowlisted scope 内进入 `GuardedPostgresPrimary` smoke / gate，但仍禁止全局默认启用：

- `LearningFeedbackProviderSwitchOptions.Enabled=false` by default。
- 默认 `Mode=FileSystemPrimary`。
- allowlist / scoped rule 未命中时必须继续 FileSystem。
- `FallbackToFileSystem=true` 必须保留。
- `ContinueComparisonTrace=true` 必须保留。
- provider quality 未 ready 时禁止 scoped mode。
- 不训练、不调权、不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 trace artifact：

- `TraceLearningFeedbackProviderSwitch`

当前输出：

- `storage/postgres/postgres-learning-feedback-scoped-service-mode-smoke-report.json`
- `storage/postgres/postgres-learning-feedback-scoped-service-mode-gate.json`
- `storage/postgres/postgres-learning-feedback-provider-switch-traces.jsonl`

当前结果：

- scoped smoke `Recommendation=ReadyForSelectedFeedbackScope`
- scoped gate `Passed=true`
- mismatch / Postgres failure 均为 `0`

## DB3.3 Learning Feedback Selected Normal Scope Boundary

DB3.3 将 learning feedback provider 的 `GuardedPostgresPrimary` 仅用于一个 selected normal scope canary：

- 默认仍为 `FileSystemPrimary`。
- 只允许显式 `WorkspaceId` / `CollectionId` 命中的 scope 使用 Postgres primary。
- 非 selected scope 必须保持 FileSystem。
- 不允许 global default on。
- 不训练、不调权、不自动更新 readiness。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

该阶段输出：

- `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-report.json`
- `storage/postgres/postgres-learning-feedback-selected-normal-scope-canary-traces.jsonl`

训练边界：

- canary feedback / review / feature candidate 必须带有 smoke / excluded metadata。
- `smoke_test_only` 或 `excludedFromTraining=true` 的 candidate 不属于 trainable dataset。
- `CleanupMode=None` 不删除真实 normal scope 数据；显式 cleanup 只能清理 canary id 前缀数据。

当前本地结果：

- selected normal canary `Recommendation=ReadyForLimitedFeedbackScope`
- `MismatchCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- non-selected scope 保持 FileSystem

## DB3.4 Learning Feedback Limited Scope Observation + Freeze

- limited observation 继续限定在 selected feedback workspace / collection，不做 global default on。
- freeze gate 仅把 `LearningFeedbackPostgres` 标记为 `ReadyForScopedServiceMode`。
- 默认 provider 仍是 FileSystem；允许 `GuardedPostgresPrimary` 只在 allowlisted scopes 生效。
- 必须保留 fallback 与 comparison trace。
- 禁止 auto-training、auto-readiness-change 和全局默认开启。

## DB4 Job Queue Postgres Provider Boundary

- JobRecord 仍归类为 `OperationalState / DatabaseRecommended`。
- DB4 只实现 Postgres provider、diagnostics、parity 和 lease contract smoke。
- 默认 runtime job queue provider 不切换，FileSystem/InMemory 仍是 source of truth。
- 不启用 runtime worker 切换，不做 dual-write/shadow-read，不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

## DB4.1 Job Queue Smoke Boundary

DB4.1 允许 job queue Postgres provider 进入 explicit dual-write / shadow-read smoke，但仍不进入 runtime worker：

- `JobQueueDualWriteOptions.Enabled=false` by default。
- `JobQueueShadowReadOptions.Enabled=false` by default。
- FileSystem/InMemory 仍是正式 source of truth。
- Postgres write/read 只用于 smoke comparison。
- mismatch / Postgres failure 只进入 trace 和 quality report。
- 不切换默认 job queue provider。
- 不接 runtime worker loop。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。

新增 trace artifact：

- `TraceJobQueueDualWrite`
- `TraceJobQueueShadowRead`

当前输出：

- `storage/postgres/postgres-job-queue-dual-write-smoke-report.json`
- `storage/postgres/postgres-job-queue-shadow-read-smoke-report.json`
- `storage/postgres/postgres-job-queue-provider-quality-report.json`
- `storage/postgres/postgres-job-queue-dual-write-traces.jsonl`
- `storage/postgres/postgres-job-queue-shadow-read-traces.jsonl`

## DB4.2 Job Queue Scoped Worker Canary Boundary

DB4.2 允许在隔离 canary scope 中执行 scoped worker canary，但不改变真实 worker runtime：

- `JobQueueScopedWorkerCanaryOptions.Enabled=false` by default。
- `Mode=GuardedPostgresPrimary` 只允许 selected canary workspace / collection。
- non-selected scope 必须继续 FileSystem/InMemory。
- `TraceJobQueueScopedWorkerCanary` 属于 `TraceOnly / FileSystemPreferred`。
- safe canary job 只使用 `ContextJobKind.Custom` 与 canary payload，不运行 compression、model、retrieval 或 vector reindex 真实 job processor。
- provider quality 非 `ReadyForScopedWorkerCanary` 时禁止 canary。
- mismatch、Postgres failure、scope leak、lease/retry/dead-letter violation 都会阻断 quality。
- global job queue provider 和 runtime worker loop 保持 unchanged。

当前输出：

- `storage/postgres/postgres-job-queue-scoped-worker-canary-report.json`
- `storage/postgres/postgres-job-queue-scoped-worker-quality-report.json`
- `storage/postgres/postgres-job-queue-scoped-worker-traces.jsonl`

## DB4.3 Job Queue Limited Worker Scope Observation Boundary

DB4.3 只在 isolated selected worker scope 中扩大观察窗口：

- `JobQueueLimitedWorkerScopeObservationOptions.Enabled=false` by default。
- 前置要求 scoped worker canary quality 已通过。
- 只运行 `canary.noop`、`canary.fail-once-then-succeed`、`canary.always-fail-to-dead-letter`、`canary.long-running-heartbeat`、`canary.cancel-before-acquire` 这类安全测试任务。
- `TraceJobQueueLimitedWorkerScopeObservation` 属于 `TraceOnly / FileSystemPreferred`。
- Duplicate execution、lease violation、retry violation、dead-letter violation、Postgres failure、scope leak 都会阻断 quality。
- non-selected scope 必须继续 FileSystem/InMemory。
- global job queue provider 和 runtime worker loop 保持 unchanged。

当前输出：

- `storage/postgres/postgres-job-queue-limited-worker-scope-observation-report.json`
- `storage/postgres/postgres-job-queue-limited-worker-scope-quality-report.json`
- `storage/postgres/postgres-job-queue-limited-worker-scope-traces.jsonl`

当前本地结果：

- observation `Recommendation=ReadyForJobQueueFreezeGate`
- quality `Passed=True`
- `JobCount=11`
- `CompletedCount=8`
- `RetriedCount=4`
- `DeadLetterCount=2`
- `CancelledCount=1`
- `DuplicateExecutionCount=0`
- `LeaseViolationCount=0`
- `RetryViolationCount=0`
- `DeadLetterViolationCount=0`
- `PostgresFailureCount=0`
- `ScopeLeakCount=0`
- `NonSelectedScopeRemainsFileSystem=true`

## DB4.F Job Queue Postgres Freeze Boundary

DB4.F 将 Job Queue Postgres 冻结为 `ReadyForScopedWorkerMode`，但仍然属于 scoped-only operational provider path：

- 默认 provider 仍是 `ExistingProvider`。
- 只允许 explicit allowlisted worker scopes 使用 `GuardedPostgresPrimary`。
- 必须保留 lease / heartbeat / retry / dead-letter quality gates。
- 禁止 `GlobalWorkerProviderSwitch`。
- 禁止 `ProductionWorkerLoopSwitchWithoutGate`。
- runtime-change gate 对缺 scoped allowlist、缺 lease quality gate、缺 retry/dead-letter quality gate 均必须 fail。

当前输出：

- `storage/postgres/postgres-job-queue-freeze-gate.json`
- `storage/postgres/postgres-job-queue-freeze-gate.md`

当前本地结果：

- freeze gate `Passed=True`
- `JobQueuePostgres=ReadyForScopedWorkerMode`
- `GlobalSwitchAllowed=false`
- `ScopedWorkerCanaryAllowed=true`

## DB5.0 Vector Index Store Boundary

Vector index entries 继续归类为 `IndexState / DatabaseRecommended`，Postgres pgvector 是后续 provider parity / smoke 的目标存储。

DB5.0 的边界：

- 只新增 `PostgresVectorIndexStore`、schema、diagnostics、compatibility 和 provider smoke。
- `UseForRuntime=false`，不切换正式 vector store。
- 不接 retrieval，不改变 vector shadow / preview readiness。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- `vector_index_entries` 查询必须带 provider/model/dimension 兼容条件，避免把 deterministic hash、ONNX 或未来模型的 embedding space 混用。

DB5.0 标准输出：

- `storage/postgres/postgres-vector-diagnostics.json`
- `storage/postgres/postgres-vector-compatibility.json`
- `storage/postgres/postgres-vector-provider-smoke-report.json`

下一阶段必须先做 parity / dual-write / shadow-read gating，不能直接把 pgvector 接入 formal retrieval。

## DB5.1 Vector Index Parity Boundary

DB5.1 完成 FileSystem / Postgres vector index parity eval 后，VectorIndexStore 仍保持 `IndexState / DatabaseRecommended`：

- Postgres pgvector provider 只允许 explicit eval / diagnostics / parity 使用。
- 不注册为 runtime `IVectorIndexStore`。
- 不接 formal retrieval，不改变 vector readiness。
- parity 通过只代表可进入 provider-scoped reindex 准备阶段。
- 后续仍需 provider-scoped reindex、dual-write、shadow-read 和 readiness gate，才能考虑 scoped runtime。

当前输出：

- `storage/postgres/postgres-vector-parity-report.json`
- `storage/postgres/postgres-vector-parity-report.md`

当前结果：

- `Recommendation=ReadyForProviderScopedReindex`
- `MismatchCount=0`
- `OrderingMismatchCount=0`
- `MetadataMismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`

## DB5.2 Provider-scoped pgvector Reindex Boundary

DB5.2 将 eval/context corpus 的 provider-scoped entries 写入 pgvector，但仍是 explicit eval/apply path，不是 runtime provider switch：

- Postgres pgvector 只作为 `postgres-vector-provider-scoped-reindex-*` 的显式目标。
- 不注册为 runtime `IVectorIndexStore`。
- 不写 FileSystem formal vector store。
- 不改变 `VectorIndexService` runtime path。
- `UseForRuntime=false`。
- `ReadyForPgVectorQueryPreview` 只允许后续 pgvector query preview，不允许自动启用 formal retrieval。

当前输出：

- `storage/postgres/postgres-vector-provider-scoped-reindex-plan.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-apply-report.json`
- `storage/postgres/postgres-vector-provider-scoped-reindex-quality-report.json`

当前结果：

- `CandidateCount=158`
- `IndexedEntryCountAfterApply=158`
- `MetadataRoundtripMismatchCount=0`
- `Recommendation=ReadyForPgVectorQueryPreview`

## DB5.3 PgVector Query Preview Boundary

DB5.3 只做 pgvector query preview 和 FileSystem 临时 baseline 对比，仍属于 `IndexState / DatabaseRecommended` 的显式 provider 验证阶段：

- Postgres pgvector 只作为 `postgres-vector-query-preview` 的只读查询目标。
- 不注册为 runtime `IVectorIndexStore`。
- 不接 formal retrieval，不改变 VectorRetrieval readiness。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- FileSystem baseline 使用临时目录，报告完成后清理，不写正式 vector store。
- `ReadyForPgVectorShadowEval` 只允许后续 shadow eval，不允许 global/default runtime switch。

当前输出：

- `storage/postgres/postgres-vector-query-preview-report.json`
- `storage/postgres/postgres-vector-query-preview-report.md`

当前结果：

- `QueryCount=113`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `DimensionMismatchBlocked=true`
- `ProviderModelMismatchBlocked=true`
- `Recommendation=ReadyForPgVectorShadowEval`
- `UseForRuntime=false`

## DB5.4 PgVector Shadow Eval Boundary

DB5.4 只做 pgvector shadow eval 和 FileSystem 临时 baseline 对比，仍属于 `IndexState / DatabaseRecommended` 的显式 provider 验证阶段：

- Postgres pgvector 只作为 `postgres-vector-shadow-eval*` 的只读 shadow eval 目标。
- 不注册为 runtime `IVectorIndexStore`。
- 不接 formal retrieval，不改变 VectorRetrieval readiness。
- 不改变 retrieval、planning、scoring、`PackingPolicy` 或 package output。
- FileSystem baseline 使用临时目录，报告完成后清理，不写正式 vector store。
- `ReadyForVectorPostgresFreeze` 只允许进入 Postgres vector provider freeze，不允许 global/default runtime switch。

当前输出：

- `storage/postgres/postgres-vector-shadow-eval-a3.json`
- `storage/postgres/postgres-vector-shadow-eval-extended.json`
- `storage/postgres/postgres-vector-shadow-eval-summary.json`

当前结果：

- `Recommendation=ReadyForVectorPostgresFreeze`
- `A3 RecallDelta=0`
- `Extended RecallDelta=0`
- `RiskAfterPolicy=0`
- `FormalOutputChanged=0`
- `TopKOverlapRate=100.00%`
- `OrderingMismatchCount=0`
- `MetadataMismatchCount=0`
- `EligibilityMetadataMismatchCount=0`
- `RiskProjectionMismatchCount=0`
- `UseForRuntime=false`

## DB5.F Vector Postgres Provider Freeze Boundary

DB5.F 将 pgvector provider 标记为 `ReadyForPreviewShadowStorage`，但仍属于 `IndexState / DatabaseRecommended` 的 preview/shadow storage 能力：

- Postgres pgvector 不注册为正式 runtime `IVectorIndexStore`。
- `DefaultVectorStore=unchanged`。
- `UseForRuntime=false`。
- `FormalRetrievalAllowed=false`。
- 只允许 preview / shadow / eval。
- 正式 retrieval、`PackingPolicy`、package output 集成都必须等待 Vector V4 readiness gate。

当前输出：

- `storage/postgres/postgres-vector-freeze-gate.json`
- `storage/postgres/postgres-vector-freeze-gate.md`
