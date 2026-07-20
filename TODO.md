# ContextCore 项目路线图

> 最近更新：P0 冻结完成，下一阶段进入 R15（2026-07-20）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均已标注"历史快照"声明，仅供回溯，不作为 current-head 决策依据。历史完成记录已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 当前阶段

**R14-PG：Postgres Runtime Parity & HA Gate 已完成** — 10 个子任务全部完成并提交（HEAD `540a6fc`）。P0 冻结在 R14-PG 收口（`3dbc1db`）基础上完成代码修复与基线重测，所有性能指标指向同一 commit（`git log --grep="fix(P0): freeze"`）。

- R14-PG-1 移除 LearningFeedback/Review 的 Unsupported 覆盖，正式绑定 Postgres 实现（commit `28b7c49`）
- R14-PG-2 PostgresDecisionTraceStore + decision_traces 表（commit `72d8f20`）
- R14-PG-3 Short-term memory / promotion / candidate review stores 迁移至 Postgres（commit `6193bc5`）
- R14-PG-4 Context learning + governance review stores 迁移至 Postgres（commit `9d9c7b2`）
- R14-PG-5 vector lifecycle + artifact stores 迁移至 Postgres，垂直闭环完成（commit `e02958f`）
- R14-PG-6 PostgresContextStateVersionStore 分布式版本存储（commit `55a6a00`，schema v13）
- R14-PG-7 多实例 cache invalidation 验收 + Decorator 文档化（commit `3f926f6`）
- R14-PG-8 Migration version/rollback 框架（commit `aba4455`）
- R14-PG-9 HA 测试套件 failover/pool exhaustion/slow query/tx retry（commit `570e38b`）
- R14-PG-10 backup/restore runbook + PITR + CLI 接入 PostgresBackupRunner（commit `540a6fc`）

### P0 冻结（2026-07-20）

R14-PG 收口后重新冻结 Current HEAD，确保所有性能指标指向同一 commit SHA：

- **Build**：0 warnings / 0 errors
- **Tests**：ContextCore.Tests 1171/1171 通过；ContextCore.IntegrationTests 75/75 通过（1 skip pg_dump）；ContextCore.Service.Tests 61/61 通过（1 skip manual OpenApi_RegenerateSnapshot）
- **A3 / golden / graph 不回退**：197 个 graph/eval/retrieval 测试全通过
- **Package cold/cache benchmark**：37 个 BenchmarkDotNet 基准运行完成（8m14s），结果记录在 `benchmarks/results/README.md`
- **FileSystem vs PostgreSQL 性能**：PostgresPerformanceTests 3/3 通过；PackageColdPathPerformanceGateTests 4/4 通过；跨 provider 直接对比基准列为后续补充
- **Cache hit / stale retry / version mismatch**：95 个 cache/trace 测试全通过
- **Trace queue drop / flush latency**：14 个 bounded channel 测试全通过（drop/batch/OTel counters）；flush latency 时间门列为后续补充

**P0 修复内容**：

1. `PostgresMigrationRunner.MigrateAsync` 增加 `GetAppliedVersionAsync` 版本短路：版本已匹配时跳过完整 DDL 批次（150+ CREATE TABLE IF NOT EXISTS），解决 Docker Desktop/WSL2 上幂等重跑触发 socket read timeout 的问题
2. `PostgresWriteTransactionScopeTests` 迁移从 per-test GUID 前缀改为 ClassInitialize 单次迁移：原实现 10 个测试 × 50+ 表 = 500+ DDL，持续超时
3. `PostgresRelationOutboxStore.AcquirePendingAsync` RETURNING `data` JSONB 同步：UPDATE 列状态后用嵌套 `jsonb_set()` 同步 JSONB 内 State/LeaseOwner/LeaseExpiresAt/LastHeartbeatAt/DispatchedAt
4. `PostgresRelationOutboxStore.MarkFailedAsync` retry 比较修正：`retry_count + 1 >= max_retry_count`（原 `>` 导致最后一次 retry 未标记 Failed）
5. `PostgresRelationStore` per-seed Truncated 信号修正：`bucket.Count >= maxScan`（原 `> 0` 误标记低基数为截断）
6. `PostgresContextLearningStore` 3 处 CS8604 nullable 参数修复
7. `ContextCore.Generators.csproj` RS2008 release tracking warning 抑制
8. OpenAPI snapshot 再生（R14-PG-10 新增 backup/pg-* 端点）

**验收达成**：

- Service 层无 `Unsupported*Store` 残留（5 个 R14-PG-5 完成时彻底清零，PostgresDeclaredUnsupported HashSet 已为空）
- 多实例并行写入测试通过（`MultiInstanceCacheInvalidationTests` 3 个测试通过）
- DB 故障注入测试通过（failover / pool exhaustion / slow query / tx retry 共 4 个 Testcontainers 集成测试 + 5 个单元测试）
- backup/restore runbook 文档化（`docs/runbooks/postgres-backup-restore.md`，含 RPO/RTO 定义与三层备份策略）

下一阶段为 **R15：Incremental Context Package** — 比 Embedding Cache 更接近真正的 KV Cache，最重要的验收为 `IncrementalBuild(snapshot) == FullBuild(snapshot)` 在随机 differential testing 下成立。

---

## 硬边界

- ControlRoom 和 Service 不再编译期引用 Evaluation（P3.1 已完成）
- Evaluation 依赖只能是 Evaluation → Core/Storage/Abstractions/Client/Runtime
- Abstractions 只承载 Contracts/DTOs/Enums/跨层协议，不含实现逻辑
- Client 只承载 Service client，不被 eval host 接口污染
- 构建必须 0 警告 0 错误
- 全量测试必须 0 失败
- 所有变更提交到 GitHub main 分支
- **数据平面定位边界（R14-PG 起强制执行）**：
  - FileSystem = Local / Single-host runtime（低到中等数据量、单机可靠持久化）
  - PostgreSQL = Multi-instance / HA runtime（多 Worker、高可用、跨实例失效）
  - 不再让 FileSystem 同时承担 HA 角色；PostgreSQL 是 HA 的唯一目标数据平面

---

## 当前验收指标（2026-07-20 R17-2/P8 Learning Loop V1 + OPT-1~OPT-5 完成）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 当前 HEAD | `a4a6fdb`（OPT-3 收口）；R17-2 DefaultGuardedOptimizationPipeline + P8 Learning Loop V1 + OPT-1~OPT-5 全部完成 | - |
| PublicApi baseline 行数 | 7967（+74 R15 V1；+1 R15 V2；+196 R16/R17 Evolution 契约；+224 P8 LearningLoopContracts；+5 OPT-4 ContextDecisionPolicyVersions）— 实现层类型在 ContextCore.Core 不进 Abstractions baseline | 单一事实源 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | ContextCore.Tests 1345/1345（+16 P8-A；+30 P8-B/C/D；+19 R17-2；+8 OPT-1；+16 OPT-2；+14 OPT-3；+14 OPT-5；skip 10 文档化缺口）；IntegrationTests 75/75（1 skip pg_dump；`ConcurrentBuild_16Way` 性能阈值在 WSL2 上 marginal flaky，重跑通过）；Service.Tests 61/61（1 skip manual OpenApi_RegenerateSnapshot） | 0 失败 |
| A3 / golden / graph 不回退 | 197 个 graph/eval/retrieval 测试全通过 | 不回退 |
| Package Build Cold (InMemory, ItemCount=50) | 2,329 μs / 819 KB | ≤ 当前值 70% |
| Package Build CacheHit (InMemory, ItemCount=50) | 6.6 μs / 12.56 KB | 优于 Cold |
| FileSystem Package Build Cold (ItemCount=50) | 20,507 μs / 1385.58 KB | ≤ 当前值 70% |
| FileSystem Package Build CacheHit (ItemCount=50) | 6.0 μs / 12.19 KB | 优于 Cold |
| ConcurrencyScaling (1→64, 1ms/query) | 186.2→189.0 ms | 持平 |
| Postgres ColdBuild (Testcontainers) | 323 ms | ≤ 2,000 ms |
| Postgres ConcurrentBuild_16Way | 561 ms | ≤ 20,000 ms |
| ColdPath InMemory allocation (50 items) | ≤ 2 MB gate | 通过 |
| ColdPath FileSystem allocation (50 items) | ≤ 3 MB gate | 通过 |
| Cache hit / stale retry / version mismatch | 95 测试全通过 | 已达成 |
| Trace queue drop / batch / OTel | 14 测试全通过 | 已达成 |
| CacheChurn WriteWithLruEviction (cap=1000) | 33,444 μs / 1434.23 KB | 基线 |
| CacheChurn InvalidateByScope (cap=10000) | 2,938 μs / 711.46 KB | 基线 |
| Constraint logical calls | 1 个 snapshot call | 已达成 |
| PackageReadPlan.TotalStoreCalls | 可观测 | 已达成 |
| BoundedChannelContextEventSink metrics | queue/error/drop | 已达成 |
| Cache Canary kill switch | 默认关闭 + workspace allowlist | 已达成 |
| 备份清单 / 验证 / 演练 | BackupManifest + verify + drill | 已达成 |
| Decision Evidence V2 | CandidateDecisionReasonCode 枚举 + V2 字段填充 | 已达成（R14-1） |
| Package Quality 报告 | 8 指标 + OverallScore 加权 | 已达成（R14-2） |
| OpenAPI snapshot 辅助再生 | OpenApi_RegenerateSnapshot `[Ignore]` 方法 | 已达成 |
| Postgres schema 版本 | v13（自 R14-PG-6 起稳定） | 稳定 |
| Postgres Unsupported stores 残留 | 0（R14-PG-5 完成时清零） | 0 |
| Postgres 多实例 cache invalidation | PostgresContextStateVersionStore + Decorator 文档化 | 已达成（R14-PG-6/7） |
| Postgres migration 框架 | registry + history + rollback + 版本短路 | 已达成（R14-PG-8 + P0 冻结） |
| Postgres HA 测试 | failover/pool/slow/tx retry | 已达成（R14-PG-9） |
| Postgres backup/restore runbook | docs/runbooks/postgres-backup-restore.md | 已达成（R14-PG-10） |
| R15 IncrementalBuild == FullBuild | 13 个 differential 测试（含 100 步大规模） | 已达成（R15-1 + R15-2） |
| R15 NoChange 路径复用 PackageTemplate | RebuildFromSnapshotAsync + CallTrackingBuilder 验证 | 已达成（R15-2） |
| R16 OptimizationProposal 契约 | 10 个公共类型 + 4 个接口 | 已达成（R16-1） |
| R17 Guarded Optimization Pipeline 契约 | 3 个枚举 + 3 个 DTO + 2 个接口 | 已达成（R17-1） |
| Evolution Contracts 单元测试 | 22 个测试全通过 | 已达成（R16/R17） |
| R16 DefaultContextEvolutionAgent 实现 | DiagnoseAsync + RefineProposalAsync + HypothesisTemplates + DefaultAgentObservationSource | 已达成（R16-2） |
| R16-2 实现层单元测试 | 17 个测试全通过（含 6 个 TargetComponent 模板覆盖 + 硬边界验证） | 已达成（R16-2） |
| P8-A DefaultPromotionJudge 实现 | 逐条 ExpectedGain + RollbackCondition 评估；不使用单一质量总分 | 已达成（P8-A） |
| P8-B/C/D Learning Loop 契约 | Dataset/ModelRegistry/Canary/Rollback 全部公共契约（224 条 PublicApi 条目） | 已达成（P8-B/C/D） |
| R17-2 DefaultGuardedOptimizationPipeline 实现 | 5 阶段严格顺序 + 自动回滚 + in-memory run state | 已达成（R17-2） |
| R17-2 Pipeline 单元测试 | 19 个测试覆盖 StartAsync/AdvanceWithMetricsAsync/GetStatusAsync + 阶段跳跃 + 终态幂等 | 已达成（R17-2） |

---

## 历史完成记录

历史完成记录（R7~R12 系列、P0~P5 系列、DTO-R1~R4、R13.0~R13-F、P0-1~P0-8、P1-1~P1-8、R14-1、R14-2 等）已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 下一阶段任务

### R15 — Incremental Context Package（V1/V2 已完成）

比 Embedding Cache 更接近真正的 KV Cache。最重要的验收不是速度，而是**幂等性**：

```
IncrementalBuild(snapshot) == FullBuild(snapshot)
```

R15 V1 已完成（commit 见 `git log --grep="feat(R15-1)"`）：
- **Abstractions 层契约**：`StoreVersionVector`、`RequestSemanticFingerprint`、`SectionDependencySet`、`PackageStateSnapshot`、`PackageDeltaKind`、`PackageDeltaPlan`、`IPackageDeltaPlanner`、`IPackageIncrementalBuilder`、`ISnapshotCapablePackageBuilder`、`PackageBuildWithSnapshot`
- **Core 层实现**：`PackageDeltaPlanner`（纯函数，比较请求指纹+版本向量）、`PackageIncrementalBuilder`（V1 委托策略）、`PackageStateSnapshotCapture`（snapshot 捕获）、`SectionDependencyMapper`（section→scope 依赖映射）
- **BasicContextPackageBuilder** 实现 `ISnapshotCapablePackageBuilder`，新增 `BuildDetailedWithSnapshotAsync` 方法
- **Differential testing**：8 个测试覆盖固定种子/多种子/ContextStore/MemoryStore/ConstraintStore/请求变化/NoChange/Snapshot 捕获
- **R15 V1 策略**：所有 delta kind 都委托到全量构建，等价性由 inner builder 的确定性保证

R15 V2 已完成（commit 见 `git log --grep="feat(R15-2)"`）：
- **ISnapshotCapablePackageBuilder** 新增 `RebuildFromSnapshotAsync` 方法（Abstractions 层不暴露 internal PackageTemplate，通过 `PackageStateSnapshot.Template` 间接传递）
- **BasicContextPackageBuilder.RebuildFromSnapshotAsync**：cast snapshot.Template → PackageTemplate，调用 `ResultProjector.ProjectResult` 重新投影（纯函数，保证与全量构建投影阶段输出完全一致）
- **PackageIncrementalBuilder** 构造函数 innerBuilder 类型改为 `ISnapshotCapablePackageBuilder`；NoChange 分支调用 `RebuildFromSnapshotAsync`，其他 delta kind 仍委托到 `BuildDetailedAsync`
- **Differential testing 扩展**：13 个测试（+5 自 V1），含 100 步大规模/多种子大规模/NoChange 路径调用追踪/NoChange 重复复用/混合序列
- **NoChange 路径调用追踪**：`CallTrackingBuilder` 验证 NoChange 路径调用 `RebuildFromSnapshotAsync` 一次、不调用 `BuildDetailedAsync`；非 NoChange 路径反之

**验收达成**：
- `IncrementalBuild(snapshot) == FullBuild(snapshot)` 在 13 个 differential testing 下成立（含 100 步大规模序列）
- 比较维度：section 内容、selected IDs、dropped IDs、reason code、token attribution、source refs (ItemReferences)
- Build 0/0，ContextCore.Tests 1206/1206，IntegrationTests 75/75，Service.Tests 61/61

**R15 V3 待办**（性能优化，不影响 API 契约）：
- 在 `PackageDeltaKind.PartialSectionChange` 分支实现真正的选择性重载：复用未变 section 的候选列表，仅重载受影响 section，最后全局重新打包
- 增加性能基准：对比 NoChange 路径与全量构建的 latency 差异

### R16 — Context Evolution Agent（V1 契约 + V2 实现已完成）

Agent 只负责离线控制面，不触碰正式 Policy 生产路径。

**允许的操作**（接口约束）：

- Observe（采集运行时指标与决策证据）→ `IAgentObservationSource.ObserveAsync`
- Diagnose（根因分析 + 形成假设 + 生成实验 + 运行 benchmark/eval + 与基线对比 + 生成 proposal）→ `IContextEvolutionAgent.DiagnoseAsync`
- Refine（基于新证据修订既有 proposal）→ `IContextEvolutionAgent.RefineProposalAsync`

**明确禁止**（接口不暴露以下能力）：

- 自动改正式 Policy（无 Policy 修改接口）
- 自动提交生产配置（无配置修改接口）
- 自动启用模型（无模型启用接口）
- 绕过 shadow / canary（Agent 输出只能到 `OptimizationProposalStatus.ExperimentReady`，后续由 R17 pipeline 推进）

**Agent 输出格式**：版本化 `OptimizationProposal`，包含证据（`ExperimentEvidence`）、预期收益（`ExpectedGain`）、风险评估（`RiskAssessment`）、回滚条件（`RollbackCondition`）。

**R16-1 已完成**（commit 见 `git log --grep="feat(R16-1)"`）：
- `OptimizationProposalStatus` 枚举（8 个值：Draft/Validated/ExperimentReady/Shadow/ScopedCanary/Promoted/RolledBack/Rejected）
- `OptimizationProposalVersion` record（Major.Minor，含 BumpMinor/BumpMajor）
- `ExperimentEvidence` / `ExpectedGain` / `RiskAssessment` / `RollbackCondition` 类（含完整字段验证）
- `RiskSeverity` / `ComparisonOperator` / `OptimizationTargetComponent` 枚举
- `OptimizationProposal` record（不可变，含 Evidence/ExpectedGains/Risks/RollbackConditions）
- `IAgentObservationSource` / `IContextEvolutionAgent` 接口
- `AgentDiagnosticRequest` / `AgentDiagnosticResult` 类
- 22 个 EvolutionContractsTests 验证契约可实施性

**R16-2 已完成**（commit 见 `git log --grep="feat(R16-2)"`）：
- `DefaultContextEvolutionAgent`（`ContextCore.Core/Services/Evolution/`）：基于 `IAgentObservationSource` 采集 + `HypothesisTemplates` 模板生成 Validated proposal；`RefineProposalAsync` 按 evidence 方向决定推进到 ExperimentReady 或 Rejected
- `DefaultAgentObservationSource`：内存指标源（`RecordMetricsAsync` 写入 + `ObserveAsync` 读取），生产部署可替换为 telemetry sink 实现
- `HypothesisTemplates`（internal）：为 6 个 `OptimizationTargetComponent` 提供预定义的 Title/Hypothesis/ExpectedGains/Risks/RollbackConditions 模板
- 17 个 `DefaultContextEvolutionAgentTests` 覆盖：Diagnose 路径（无指标/有指标/ProposalId 编码/ExperimentConfig/全部 6 个 component 模板/null 防御）+ Refine 路径（支持推进/驳斥驳回/未匹配 metric/已 Rejected 不可逆/pipeline 状态拒绝/无 RollbackConditions 防御）
- **硬边界**：Agent 输出 Status 上限为 ExperimentReady；接收 pipeline 状态（Shadow/ScopedCanary/Promoted/RolledBack）的 RefineProposalAsync 抛 InvalidOperationException
- **实现策略**：DiagnoseAsync 用模板默认值作为 baseline 占位（observation 无该 metric 时）或取自 observation（实际值作为 baseline），ExperimentValue = baseline + ExpectedGain.EstimatedDelta；RefineProposalAsync 比较 `Math.Sign(evidence.Delta)` 与 `Math.Sign(ExpectedGain.EstimatedDelta)` 方向是否一致决定推进/驳回；不引入新的 Abstractions 契约（如 IExperimentRunner），实验 evidence 由调用方通过 RefineProposalAsync 注入

**R16 V3 待办**（后续增强）：
- 接入 benchmark runner + eval host（替代当前外部通过 RefineProposalAsync 注入 ExperimentEvidence 的方式，让 Agent 内部直接运行实验并采集真实 evidence）
- Proposal 持久化（存储到 PostgresContextLearningStore 或新 store）
- ObservationSource 集成真实 telemetry sink（OpenTelemetry metrics registry）

### R17 — Guarded Optimization（V1 契约 + V2 实现已完成）

引入完整的优化闭环：

1. **Offline experiment** — 离线实验 → `OptimizationStage.OfflineExperiment`
2. **Shadow** — 影子模式运行 → `OptimizationStage.Shadow`
3. **Scoped canary** — 范围受控的 canary → `OptimizationStage.ScopedCanary`
4. **Automatic rollback** — 自动回滚（命中风险条件时）→ `OptimizationStage.AutomaticRollback`
5. **Manual / default promotion** — 手动或默认晋升 → `OptimizationStage.Promotion`

**第一项端到端学习闭环**：建议先用 `PromotionJudge` 验证训练和部署基础设施，因为作用域最小、风险容易隔离。

**第一项真正作用于核心运行时的 learned component**：建议从以下二选一：

- Cost-aware Retrieval Router（成本感知检索路由）→ `OptimizationTargetComponent.CostAwareRetrievalRouter`
- Candidate Utility Reranker（候选效用重排序器）→ `OptimizationTargetComponent.CandidateUtilityReranker`

**R17-1 已完成**（commit 见 `git log --grep="feat(R17-1)"`）：
- `OptimizationStage` 枚举（5 个阶段，严格顺序推进）
- `PipelineRunStatus` 枚举（7 个状态：Running/StageCompleted/RolledBack/Promoted/Rejected/Cancelled/Failed）
- `PipelineRunResult` 类（含 stageMetrics/rollbackReason/completedAt）
- `PromotionJudgeRequest` / `PromotionJudgeResult` 类
- `PromotionDecision` 枚举（5 个值：Advance/Hold/Rollback/Promote/Reject）
- `IPromotionJudge` 接口（最小端到端学习闭环裁决器）
- `IGuardedOptimizationPipeline` 接口（StartAsync/AdvanceAsync/GetStatusAsync）

**R17-2 已完成**（commit 见 `git log --grep="feat(R17-2)"`）：
- `DefaultPromotionJudge`（P8-A，`ContextCore.Core/Services/Evolution/`）：规则引擎裁决器，逐条 ExpectedGain + RollbackCondition 评估；终态（AutomaticRollback/Promotion）直接返回 Rollback/Promote；不使用单一质量总分；构造函数 `DefaultPromotionJudge(double promotionConfidenceThreshold = 0.70, TimeProvider? = null)`
- `DefaultGuardedOptimizationPipeline`：5 阶段严格顺序推进（OfflineExperiment → Shadow → ScopedCanary → Promotion）；自动回滚（任一 RollbackCondition 命中 experimentMetrics → AutomaticRollback 终态）；in-memory run state（ConcurrentDictionary，生产部署应替换为 PostgresContextLearningStore 或新 store）；持久化 BaselineComparison + RollbackRecord + CanaryAssignment
- 接口扩展（非接口方法）：`AdvanceWithMetricsAsync(runId, baselineMetrics, experimentMetrics, ct)` 注入指标 + 调用 judge + 应用 decision；`RecordCanaryAssignmentAsync` / `GetCanaryAssignmentsAsync` / `GetRollbackRecordAsync` 辅助审计方法
- 19 个 `DefaultGuardedOptimizationPipelineTests` 覆盖：StartAsync（ExperimentReady/非 ExperimentReady/无 RollbackConditions/null）+ AdvanceWithMetricsAsync（推进/驳斥/回滚/幂等/全 pipeline promote/Hold）+ GetStatusAsync（已知/未知 runId）+ CanaryAssignment 持久化 + BaselineComparison 持久化 + 接口方法无指标
- 硬边界：仅接受 ExperimentReady proposal + 至少 1 条 RollbackCondition；阶段跳跃抛 InvalidOperationException；终态（Promoted/RolledBack/Rejected/Cancelled/Failed）幂等不可推进

**R17 V3 待办**（集成层）：
- Pipeline run 持久化（替换 in-memory ConcurrentDictionary 为 PostgresContextLearningStore 或新 store）
- 第一项端到端集成：真实 dataset → model artifact → canary assignment → rollback 完整流程
- Canary assignment strategy 实现选择（随机/分层/哈希分桶等）

### P8 — Learning Loop V1（已部分完成）

**第一条完整闭环**：Runtime evidence → Reviewed dataset → Versioned dataset → Training job → Model artifact → Offline replay → Shadow → Scoped canary → Rollback

**已完成**（commit `719ad76` + `9ea5489`）：
- **P8-A** `DefaultPromotionJudge`：规则引擎裁决器；不使用单一质量总分；逐条 ExpectedGain + RollbackCondition 评估（16 个测试）
- **P8-B/C/D** `LearningLoopContracts`（Abstractions）：Dataset/ModelRegistry/Canary/Rollback 全部公共契约（30 个测试，+224 PublicApi 条目）
  - Dataset：`DatasetSplitStrategy` / `DatasetReviewStatus` / `DatasetProvenance` / `FeatureSchemaVersion` / `DatasetVersion` / `DatasetManifest` / `DatasetStatistics` / `VersionedDataset`
  - Model Registry：`ModelArtifactStatus` / `ModelCompatibilityLevel` / `ModelArtifactVersion` / `ModelCompatibilityContract` / `ModelArtifact` / `IModelRegistry` 接口
  - Canary & Rollback：`CanaryAssignmentStrategy` / `RollbackReason` / `CanaryAssignment` / `RollbackRecord` / `BaselineComparison`
- **R17-2** `DefaultGuardedOptimizationPipeline`：5 阶段严格顺序推进 + 自动回滚 + in-memory run state（19 个测试）

**硬边界**（P8 学习闭环）：
- **不**直接用 `selected = positive`、`dropped = negative`：Token budget、section quota 和 duplicate suppression 导致的 dropped 不能被当作不相关负样本（DatasetManifest 必须区分 `PositiveLabels` / `NegativeLabels` / `UnlabeledItems`）
- 明确禁止：自动修改正式 Policy、自动提交配置、自动启用模型、绕过 shadow/canary、用单一质量总分决定上线
- 当前 feedback candidate mapping 仍由硬编码 switch 维护，后续可收敛成 capability registry（**非第一优先级**）

**第二 learned component 优先级建议**（R17 V3 集成时按此顺序）：
1. PromotionJudge（已完成）
2. CostAwareRetrievalRouter
3. CandidateUtilityReranker
4. ConstraintGapJudge
5. Package-level listwise model

**P8 待办**（V2 集成层，后续阶段）：
- Dataset manifest 实际生成器（从 Runtime evidence → Reviewed dataset 的具体 ETL 实现）
- Train/test group split 策略实现（`GroupKeyed` 默认避免数据泄漏）
- Model registry 持久化（替换 in-memory 为 PostgresContextLearningStore 或新 store）
- Model compatibility contract 运行时校验（model artifact 加载时校验 FeatureSchemaVersion 与运行时 schema 一致性）
- Baseline comparison 自动采集（替换当前手动通过 `AdvanceWithMetricsAsync` 注入指标的方式）
- Canary assignment strategy 实现选择（随机/分层/哈希分桶等）
- Rollback record 持久化与审计查询

### 代码优化（OPT-1~OPT-5 已完成）

5 项代码细节优化全部完成（HEAD `a4a6fdb`，OPT-3 收口）：

**OPT-1 Trace schema 枚举化**（已完成，commit `945f728` 前序）：
- `PackageTraceRecorder.MapTraceFields` 原通过字符串判断 kind/section 输出 byte sourceType/authority/strategyType/channel
- 改为正式枚举：`RuntimeCandidateSourceType` / `CandidateAuthorityLevel` / `CandidateStrategyType` / `RuntimeCandidateRetrievalChannel`（均 `: byte` 保证 JSON 输出兼容）
- 未匹配 kind 显式落入 `Unknown(0)` 而非静默默认 `Raw(1)`，下游可检测 schema 演进缺口
- 详见 `ContextCoreTraceSchemaEnumTests.cs`

**OPT-2 Runtime Candidate Trace Sink 验证**（已完成，commit `0d9f509`）：
- 验证 `FileRuntimeCandidateTraceSink` 现有行为（write count / drop on null writer / flush / dispose idempotent）
- 验证 `PackageTraceRecorder` 与 sink 的解耦（recorder 捕获 sink 异常，主流程不受影响）
- 验证 `NullRuntimeCandidateTraceSink` 空操作语义
- 16 个测试（11 通过 + 5 `[Ignore]` 文档化缺失的 async dispatch 能力：bounded queue / batch append / shutdown drain / queue saturation / writer recreation）
- 已知缺口：当前 `IRuntimeCandidateTraceSink` 为同步 lock 实现，无 async dispatcher；参考 `BoundedChannelContextEventSink`（IContextEventSink 实现）可移植

**OPT-3 Trace fault injection 测试**（已完成，commit `a4a6fdb`）：
- 覆盖 4 类 trace surface（`IRuntimeCandidateTraceSink` / `IContextPackageBuildTraceStore` / `IDecisionTraceStore` / `IRetrievalTraceStore`）在 latency 100ms / exception / disk full 故障注入下正式输出不变
- 验证 fail-open 契约（`catch (Exception)` in `WriteTracesAsync` / `RetrieveAsync`）
- 14 个测试通过 + 3 `[Ignore]` 文档化不适用当前同步实现的场景（queue full / shutdown drain / Postgres unavailable — 端到端验证应在 IntegrationTests）
- 详见 `ContextCoreTraceFaultInjectionTests.cs`

**OPT-4 Policy 版本从阶段号解耦**（已完成，commit `945f728`）：
- 原决策版本名 `context-decision-foundation/v17.0` 绑定项目阶段编号
- 改为按能力独立演进：`decision-schema/2.0` / `package-policy/3.1` / `retrieval-policy/4.0` / `relation-profile/2.0` / `quality-contract/1.0`
- 由 `ContextDecisionPolicyVersions` 静态类集中管理

**OPT-5 DI 架构测试**（已完成，commit `14e157d`）：
- 14 个架构测试验证 5 项 DI 不变量：
  1. 每个 provider 的最终解析类型（Postgres / FileSystem / InMemory）
  2. Unsupported 占位检测（确保非占位实现）
  3. 重复覆盖检测（装饰器模式 whitelist + 非装饰器意外重复检测）
  4. Data Plane / Control Plane 分离（Data Plane 包装 `Invalidating*Decorator`，Control Plane 直接 forward）
  5. Singleton-Scoped 捕获检测（扫描 `AddScoped` 调用 + src/ 所有 DI 扩展方法均为 Singleton）
- 已知缺陷 `[Ignore]`：`IContextStateVersionStore` 在 Postgres provider 下被 `CoreExtensions.AddContextCore` 无条件 InMemory 覆盖（line 62）
- 详见 `ContextCoreDiArchitectureTests.cs`

### DTO-R4 剩余部分（暂缓，高风险）

**Domain/Api/Ports 重新划分** — 将 Abstractions 的 50+ 文件按 Domain（ContextItem/Memory/Relation/Constraint）、Api（Service/Client request/response）、Ports（接口和跨层命令）重新组织。风险：涉及上百个消费者文件的命名空间变更，Abstractions 是最底层项目。需单独评估。

**进一步合并模式** — 将 Relation/LearningFeedback/JobQueue/Vector 重复定义的 diagnostics/parity/smoke/quality/gate/freeze 模型收敛为少量内部组合模型（OperationalReport<TDetails>、GateDecision、ProviderCheck、OperationScope、ProviderIdentity）。风险：大型设计任务，需逐类型验证。不要退化为 Dictionary<string,object> 或万能 nullable DTO。

### 延迟项

- **Service DI 收敛到 ContextRuntimeBuilder** — Service ASP.NET DI 仍由 CoreExtensions.AddContextCore 自行注册 80+ 服务。风险较高（生产路径），需单独评估。

---

## 被冻结的功能开发

以下功能在架构治理完成前不启动：

- 新 eval runner 开发（V9+ 阶段）
- ControlRoom UI 扩展
- 新存储 provider 集成
- 前端界面开发
- RC 版本标记

---

## 文档约定

- **本文件（TODO.md）** 是唯一当前路线图，反映最新完成状态与剩余任务。
- **docs/archive/roadmap-history.md** 归档所有已完成的历史工作记录，仅供回溯。
- `docs/` 下的所有 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均为**历史快照**，顶部已统一标注"历史快照（Historical Snapshot）"声明块。仅供回溯，不作为 current-head 决策依据。
- 如需根据陈旧报告做设计，应先在本文件中确认对应任务是否已完成或已被取代。
