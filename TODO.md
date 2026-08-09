# ContextCore 项目路线图

> 最近更新：2026-08-10。
> **Current HEAD：`35be157f`**（R31-R39 生产语义收敛 + Learning/观测面/CLI 全部完成并推送；R40 剩余项见「下一阶段（R40）」）。
> **Current Phase：R31-R39 Agent Runtime / Quota / Evidence / Learning 生产语义收敛（R39 工作包已收口）** —— R30.1 与 P1 完善项全部完成；R31：租户复合键、Settlement exactly-once + 冻结、Attempt 状态分离、Committer 身份不变量、Quota Period 修复、Tool 幂等键作用域、Evidence 稳定点查、DatasetSnapshot、AuthorizationEpoch、Trace 批量写入、后台负载治理；R32：Evidence 三层架构、Adaptive Retrieval 原生消费 + 延迟归因、动态降速契约；R33：Decision Commit Durable Outbox；R34：决策提交可靠链接线 + 延迟归因三来源合并；R35：Learning 训练闭环端到端 + 动态降速探针契约收口；R36：Learning 闭环 Postgres 生产验收 + 自适应检索 Active 模式生产验收；R37：Learning 控制面端点 + 迁移故障注入；R38：Canary/Promotion 闭环末端 + 运行观测面端点；R39：OTLP 指标导出契约收口 + Learning 控制面 CLI，详见「当前阶段」。

> 本文件是 ContextCore 的**唯一当前路线图**，是后续 Agent 的当前状态真相源。docs/ 下的 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均已标注"历史快照"声明，仅供回溯，不作为 current-head 决策依据。已完成阶段的历史记录：R14-PG 及更早已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)；R27~R30 记录保留在本文件「历史快照」章节，同样不作为当前架构依据。

---

## 当前阶段

**R31 Agent Runtime / Quota / Evidence 生产语义收敛（进行中）**

### 已完成（HEAD `73cca7be`，6 个提交收口）

1. **租户复合键（WP-A）**：Tool Dispatch Journal / Durable Tool Result / Agent Run Lease / Quota Reservation 全部升级 (workspace_id, run_id, ...) 复合键（迁移 0014~0016，v66→v69）；RequestId 派生加入 workspaceId（V2 算法）；恢复路径优先使用持久化 RequestId（滚动升级 exactly-once 不破坏，V1→V2 Kill Matrix 9 项测试）。
2. **Attempt 状态分离（P0-3）**：新增 RetryPending（非终态、可重试、不结算）——Failed 变成重试预算耗尽后的真终态（不可重试、结算恰好一次）；状态机拒绝终态被 Failed/Cancelled 覆盖；DeadLettered 仅保留运维介入分类。
3. **AgentRunCommit 身份不变量（P0-4）**：提交负载统一 TenantRunKey，入口校验 events/snapshot/cursor/checkpoint 与复合键一致（SQL 执行前 fail），7 项负向测试。
4. **Quota Period 修复（P0-5）**：SetLimitAsync 只改上限不清 usage/reservation；周期轮转重新 SUM(active reservations)（跨周期长 Run 预留保留，阻断过度放行）。
5. **Settlement 生产语义（P1-七/八）**：outbox UNIQUE(workspace_id, run_id) + ON CONFLICT DO NOTHING（自身 exactly-once）；冻结 actual_tokens/actual_cost_usd/usage_revision/final_attempt/settlement_policy（结算 Worker 不再读可变 Run 实体）。
6. **Tool 幂等键作用域（P1-十一）**：ExternalIdempotencyKey 唯一范围 (workspace_id, tool_name, idempotency_key)——跨 Run 业务去重（重建 Run 重放同一业务操作被拒绝），三种身份正式分离（InvocationId / RequestId / ExternalIdempotencyKey）。
7. **Evidence Plane 加固（P1-十二/十三）**：stable 主键 GetAsync 点查（不受"最近 100 条"窗口限制）+ 审计前 FlushAsync（已接受即已可查证）+ Trace 批量写入 SaveBatchAsync（unnest 单次 roundtrip）。
8. **DatasetSnapshot（P1-十四）**：快照 ID 确定性派生（可重现）、InputEvidenceSet/MaterializedCount/CompletenessRatio、Dropped/missing reason、Policy/Model/Schema version、ContentHash、Lineage 决策数——CI 唯一 Learning skip 已启用为真实验收。
9. **Tool AuthorizationEpoch（P1-十七）**：撤权 epoch++ 后旧快照立即失效（轻量整数比较）；生产模式 Legacy Run 治理（基础无副作用 Tool 兼容放行，File/Process/Network 类要求重新授权）。
10. **后台负载治理（P1-十八）**：BackgroundDrainBudget 统一 burst 约束（批次数/时长上限 + yield），接入 Settlement / Relation / Compaction 三个续扫型 Worker。

### 已完成（HEAD `2bec0ec4`，R32 工作包收口）

11. **Evidence 三层架构（WP-B，P1-十五）**：Diagnostic Plane（IRetrievalTraceStore，可采样/可 Drop/Async + 批量写入）→ Decision Evidence Plane（IDecisionTraceStore 稳定主键 GetAsync：Durable/Point Lookup）→ Learning Artifact Plane（ILearningArtifactStore + DatasetSnapshotArtifact：迁移 0019 v71→v72 `dataset_snapshots` 表，(workspace_id, snapshot_id) 点查 = 可重建入口）。
12. **Adaptive Retrieval 原生消费（WP-C，P1-十六）**：AgentRetrievalPlan 增加 TopK/PlanSignature；Actor 原生消费（受控查询 QueryText / TopK / RequiredIds 注入 Decision Runtime）；反馈质量信号从选中候选真实分数派生；**延迟归因**——Run 终态时把最终结果质量（Completed 0.9 / Cancelled 0.5 / Failed 0.2）归因到本 Run 使用的检索计划签名（AutomatedEvaluation 来源，run:runId:signature 幂等）。
13. **后台负载动态降速（WP-D）**：BackgroundDrainBudget 支持负载缩放因子（ShouldContinueBurst 按因子收紧批次数；ComputeScaleFactor：池利用率 0%→1.0 / 80%→0.36 / 100%→0.2 保底）；IBackgroundLoadProbe 契约（DB 池利用率信号，可选注入，无信号回退静态预算）；Settlement Worker 接入。注：Npgsql 10 未公开池统计 API，生产探针实现待 Npgsql 提供统计能力时接入（契约与缩放已就绪）。

### 已完成（HEAD `471b2cf7`，R33 工作包收口）

14. **Decision Commit Durable Outbox 显式链（WP-E）**：新增 IDecisionCommitOutbox + DecisionCommitOutboxRecord（Decision Record 完整载荷 + Evidence 引用 + Materialization Intent），迁移 0020 v72→v73 `decision_commits` 表；入队幂等（(workspace_id, decision_id)）、领取 FOR UPDATE SKIP LOCKED + 租约、失败重试达上限转死信、未 Ack 崩溃可重放——决策提交（Record + Evidence Manifest 引用 + 物化意图）经 Durable Outbox 连成可靠链。

### 已完成（HEAD `c5b0c18d`，R34 工作包收口）

15. **Decision Commit 可靠链接线（WP-F）**：决策产生点（DefaultContextDecisionRuntime）执行完成即入队决策提交（Decision Record 完整载荷 + 物化意图，Evidence 引用预留）；新增 DecisionCommitWorker（BackgroundService）消费 outbox → IDecisionTraceStore 落库（Decision Evidence Plane durable 归档）→ Ack/重试/死信——崩溃后未 Ack 条目重放，决策记录不丢、可重建、可追责。端到端测试：产生点入队 → worker 落库 → Ack；无 outbox provider 时 worker 自退出 no-op。
16. **延迟归因全链合并**：反馈三来源统一加权——Runtime（即时过程信号：命中/预算超限/选中质量）+ AutomatedEvaluation（Run 终态归因：Completed 0.9 / Cancelled 0.5 / Failed 0.2）+ Operator（人工评测端点，既有）/ AdaptiveRetrievalEndpoints /feedback）→ ComputePolicy 按 Confidence × OutcomeQuality × 时间衰减 × 单主体封顶合并为自适应策略。

### 已完成（HEAD `d4dc6103`，R35 工作包收口）

17. **Learning 训练闭环端到端（WP-H）**：Decision → UtilityLedgerMaterializer 物化 → TrainingDataExporter 导出 DatasetSnapshot（Completeness/Lineage/ContentHash/版本追责）→ ILearningArtifactStore 工件落库 → 按 SnapshotId 重建（Replay 入口，内容哈希一致）→ CalibrationDataExporter 校准导出（正负样本比例）。闭环验收测试 2 项；Canary/Promotion 为既有组件（各自测试覆盖）。
18. **动态降速生产探针（WP-G，契约收口）**：BackgroundDrainBudget 负载缩放 + IBackgroundLoadProbe 契约已就绪（Settlement Worker 接入）；**Npgsql 10 不公开连接池统计 API（Statistics 为 protected），生产探针实现待 Npgsql 提供统计能力时接入**（不引入不可验证的实现）；Queue Lag / Worker Age 维度随契约预留。

### 已完成（HEAD `d1282150`，R36 工作包收口）

19. **Learning 闭环 Postgres 生产持久化验收（WP-I）**：PostgresLearningPipelineTests（Testcontainers）——DecisionCommitOutbox 租约 CAS 往返 + 幂等重放、LearningArtifactStore 快照持久化/点查重建/跨工作区隔离、全链路闭环（决策提交 → 记录落库 → Utility Ledger 物化 → DatasetSnapshot 工件 → 重建）。修复 Postgres outbox 领取 reader 未释放即 Commit 的缺陷。
20. **自适应检索生产形态验收（WP-J）**：R36_AdaptiveRetrievalProductionTests——Shadow→Active 切换（Shadow 计算不应用/计划无签名/预算不变；Active 应用+签名+预算收缩，同反馈状态行为差异可观测）、Active 计划签名与策略查询一致（运维端点可复现）、Disabled fail-closed（不写反馈存储、计划透传）、反馈不足回退中性（Note 可解释）。

### 已完成（HEAD `65efc053`，R37 工作包收口）

21. **Learning Artifact 控制面端点（WP-K）**：LearningArtifactEndpoints（/api/learning）——快照工件点查（Replay 重建入口）/ 按工作区列表（租户隔离）/ 训练导出+工件落库 / 决策记录点查（审计）；全部 Operator 角色 + 工作区取认证上下文；7 项端点测试（DefaultHttpContext 执行 IResult）。
22. **迁移链故障注入（WP-L）**：PostgresIntegrationTests 补并发迁移互斥（pg_advisory_lock 串行，步骤记录不翻倍）+ 部分表缺失恢复。**修复两个真实缺陷**：① MigrateAsync 短路仅查版本不查缺失表——表被删后版本仍匹配导致不重建（改为版本匹配 + 无缺失表才跳过）；② dataset_snapshots / decision_commits 未列入 RequiredOperationalTableSuffixes（缺失表检查不覆盖新表）。

### 已完成（HEAD `dd21efd1`，R38 工作包收口）

23. **Canary/Promotion Learning 闭环末端（WP-M）**：R38_LearningCanaryPromotionTests——DatasetSnapshot（ModelArtifactId 关联候选模型）→ 模型工件注册（基线/候选）→ Canary 阶梯推进（健康指标 1→50→100）→ Promoted（Cutover 100% 请求走 V2）→ 候选成为最新工件；回滚闭环（指标不达标 → Rollback → 基线保持最新）。
24. **运行观测面（WP-N）**：DiagnosticsEndpoints（/api/diagnostics/runtime）——Schema 版本/缺失表/索引（VerifySchemaAsync）、Learning 物化 outbox 积压（pending/processing/dead-letter）、后台负载预算配置；Operator 角色、依赖缺失不失败；2 项端点测试。

### 已完成（HEAD `35be157f`，R39 工作包收口）

25. **OpenTelemetry 指标导出收口（WP-O）**：ObservabilityContractTests——核心遥测 Meter 名（ContextCore.Storage.Postgres / ContextCore.Core 等）必须匹配 Service 的 AddMeter("ContextCore.*") OTLP 通配，防止新增组件用非前缀 Meter 名导致导出丢失（反射契约 + 关键组件存在性断言）。
26. **Learning 控制面 ControlRoom CLI（WP-P）**：LearningCommand——`learning artifact get/list/export`（快照工件点查/列表/导出落库）+ `learning decision get`（决策审计点查）；Direct 模式用本地 ILearningArtifactStore/IDecisionTraceStore（ControlRoomState 注入），Service 模式提示经 /api/learning 远程调用。

### 下一阶段（R40，按优先级）

- **WP-Q（Service API 集成验收）**：新端点（learning/diagnostics）的端到端 HTTP 验收（真实 Service 启动 + 认证 + 响应契约）。
- **WP-R（Learning 闭环压测/规模）**：大数据量下的快照导出/工件重建/outbox 吞吐基准与上限。

### Open P0（待办）

无（R30.1 16 项 P0 全部关闭；R31 已交付项无遗留 P0）。

## 历史快照（R27~R30 已完成记录，不作为当前架构依据）

> 以下 R28/R29 记录描述的是**已删除的旧 Agent Kernel / Durable Transport 执行平面**。执行平面已收敛为单一平面（`AgentRunStore → AgentKernelHost → AgentRunActor`，见 `CoreExtensions.cs` 与 `AgentKernelContracts.cs` 的注释），本章节仅作回溯，不得作为当前架构依据。

### R28 — 持久化可靠性基础（历史快照：已完成，HEAD `6fe3203` → `1502b72`）

R28 系列为 R29 Final Closure 奠定持久化可靠性基础，涵盖 Tool Journal、Durable Transport、Outbox、Checkpoint 哈希链、Canary Metrics 等核心组件。

- **R28-B Evolution Pipeline**：Canary Metrics Collector（ring buffer + DDSketch）+ CanaryProgressionHostedService + CanaryProgressionService（4 类回滚阈值 + 渐进晋升）
- **R28-C Agent Kernel**：极薄 .NET 决策循环（IAgentKernel + IAgentKernelTransport + IToolDispatcher）+ Tool Dispatch 状态机（Prepared→Dispatched→Committed→ResultDelivered）+ InMemory 持久化实现
- **R28-E Tool Journal**：IToolDispatchJournal 契约 + InMemoryToolDispatchJournal + 持久化标记接口 IPersistentToolDispatchJournal
- **R28-F Model Execution**：DeterministicBatchInferenceEngine（FNV-1a 64-bit hash，无装箱）+ FeatureBatch 连续内存路径 + ContentHash/CalibrationVersion 契约

### R29 — Final Closure Production Truth Gate（历史快照：已完成，HEAD `1502b72`）

R29 作为完整里程碑推进六条工作流，不再拆分小阶段。所有工作流已实现完成并通过硬验收测试。

#### 工作流 A：Durable Delivery（已完成）
- **P0-1 Durable Transport lease 模型**：PostgresDurableTransport 从破坏性 DELETE 改为 Pending→Leased→Acked 状态机（FOR UPDATE SKIP LOCKED + lease_token + lease_expires_at），26 个 Testcontainers 测试通过
- **P0-2 Result Outbox Ack/Retry/Lease**：IPersistentKernelResultOutbox 扩展 LeaseAsync/AckAsync/NackAsync/RenewLeaseAsync/RequeueExpiredAsync，schema v29→v30，18 个新测试
- **P0-4 Kernel 主输入链集成**：DurableTransportInstructionPumpService + ResultOutboxReplayService + LeaseReaperService 三个 HostedService
- **P1 批量 Lease + 指数退避**：LeaseBatchAsync(32) + AckBatchAsync + 本地 bounded channel + 指数退避（×1.5 上限 5s），未引入 LISTEN/NOTIFY（遵循项目设计决策）
- **P2 PendingCount 异步化**：GetPendingCountAsync + volatile 本地 counter（Enqueue±1），同步属性标记 Obsolete

#### 工作流 B：Tool Effect Safety（已完成）
- **P0-3 Tool Journal CAS + 幂等唯一性**：idempotency_key 升级为 UNIQUE partial index（WHERE idempotency_key IS NOT NULL），schema v27→v28；expected-state CAS 语义（缺失前驱抛 InvalidOperationException，不 auto-create stub）；InMemoryToolDispatchJournal 对齐 Postgres 语义

#### 工作流 C：Model Activation（已完成）
- **P0-7 ModelActivationManager**：权威模型激活管理器，编排 IModelArtifactRegistry→ICalibrationValidator→IFeatureRegistry→IOnnxInferenceSessionFactory→OnnxInferenceEngine，线程安全引擎切换（lock + volatile），fallback 代理（未激活时委托 DeterministicBatchInferenceEngine）
- **P0-8 Validator 集成**：ICalibrationValidator + IFeatureSchemaValidator 注入生产加载路径，校准参数非法（Platt A=0）拒绝激活，schema 未注册拒绝激活
- **P0-6 真实 ONNX E2E**：7 个测试加载真实 BGE/MiniLM ONNX 文件，验证张量名校验、完整激活流程、真实 ONNX Runtime 调用、SHA-256 ContentHash、ActivateLatestAsync、fail-safe 回退
- **P3 ONNX 连续 FeatureBatch**：DefaultUtilityScorer 直构 row-major float[]（无 boxing）+ OnnxRuntimeInferenceSession 零拷贝（MemoryMarshal.TryGetArray + ArrayPool 回退）+ MaxBatchSize 分片 + lazy warmup

#### 工作流 D：Agent Intelligence（已完成）
- **6 个核心接口**：IAgentModelTransport / IAgentLoopPolicy / IAgentRunStore(+IPersistent) / IAgentApprovalGate / IAgentToolCallValidator / IAgentRunEventStore(+IPersistent)
- **AgentRunState 状态机**：10 状态（Created→ContextBuilding→ModelCalling→AwaitingApproval→ToolDispatching→Observing→Checkpointing→Completed/Failed/Cancelled），复用 ToolDispatchState CAS 模式
- **AgentRunActor + AgentKernelHost**：per-run 执行者实现 Task→BuildContext→Model→ToolCall→ToolResult→Model→Final 完整循环；AgentKernelHost 替代 Singleton Kernel 实现多 Session 隔离
- **AgentRunEvent 哈希链**：ContentHash/PrevChainHash SHA-256 防篡改，复用 Checkpoint 哈希链模式
- **DefaultAgentLoopPolicy / DefaultAgentToolCallValidator / DefaultAgentApprovalGate**：默认策略实现

#### 工作流 E：Canary Truth（已完成）
- **外部结果指标**：ExternalResultMetrics（10 个指标：TaskSuccessRate/ToolSuccessRate/RepairRate/SafetyViolationRate/ContextPrecision/ContextRecallProxy/UserAcceptance/AnswerQuality/TokenCost/InferenceCost）替代仅依赖 token budget + FinalScore 的 quality_score
- **CanaryObservationMetrics 扩展**：新增外部指标字段 + RecordObservationWithExternalMetrics 重载 + 阈值门控（MinTaskSuccessRate/MaxSafetyViolationRate/MinUserAcceptance）
- **HA 聚合**：ICanaryMetricsAggregator + PostgresCanaryMetricsAggregator（跨实例 AVG/SUM/MAX rollup + canary_metrics_samples 表）
- **Leader Lease**：ICanaryLeaderLease + PostgresCanaryLeaderLease（FOR UPDATE SKIP LOCKED 租约模式）+ CanaryLeaderHostedService（leader 选举 + 心跳续约）

#### 工作流 F：Performance Truth（已完成）
- **P0-9 Benchmark CI 消除假阳性**：集中 BenchmarkDotNet Job 配置（N≥15）+ 四层假阳性抑制（噪声底 3% + 最小样本 5 + 置信区间 2σ + I/O 宽松阈值 30%）+ CI 环境归一化 + benchmark-selftest.yml 7 个合成 case
- **P4 Checkpoint cursor 模式**：Cursor>Delta>Full 三模式优先级，Cursor 仅记 last_event_sequence+snapshotId+budget 不复制完整结果集，从 AgentRunEventStore 重建 CommittedResults
- **P5 性能回退组件归因**：7 组件 Stopwatch 拆分（provider/merge/feature/inference/scoring/allocation/projection）+ IComponentHealthRegistry + 组件级回退（Inference→Deterministic、Allocation→V2.0、Semantic→Lexical、Graph→Disabled）

#### 硬验收测试（30 项全部通过）
`tests/ContextCore.Tests/R29_FinalClosureAcceptanceTests.cs` — 6 个测试类对应六条工作流，每类 5 个测试方法，统一 `[TestCategory("R29-Closure")]`：
- Workflow-A：FIFO 出队、PendingCount 同步/异步一致性、Transport 指令与结果往返
- Workflow-B：Prepare 写入、完整状态机推进、P0-3 expected-state CAS、状态不可逆退
- Workflow-C：TaskSuccessRate/ToolSuccessRate/SafetyViolationRate 计算与空数据优雅降级
- Workflow-D：fallback 代理、未知 artifact 失败、schema 未注册拒绝、session 创建失败 fail-safe、真实 ONNX E2E
- Workflow-E：合法流转、终态短路、SHA-256 哈希链计算与校验、CAS 状态不匹配抛异常
- Workflow-F：默认 Healthy、连续失败触发 FallbackActive、自愈机制、scope 隔离、脚本四层假阳性抑制参数

---

## R27 Evolution Pipeline Postgres 持久化（已完成）

**R27 Evolution Pipeline Postgres 持久化已完成**（HEAD `6fe3203`）。延续 R26 in-memory → Postgres 模式，将 `DefaultGuardedOptimizationPipeline` 的 in-memory `ConcurrentDictionary` 状态扩展到 PostgreSQL，支持 HA 场景下的跨进程恢复。

- **R22 Bounded Context Orchestrator** 完成（HEAD `03b42fa`，3 commits）：在线 Agent 主轴 Plan→Decide→Build→Quality Evaluate→Optional Single Repair→Finalize，仅一次修复且针对 7 类确定性异常，定义 `ContextRepairBudget`。1751 测试通过（+97 PublicApi baseline）。
- **R23 Agent Runtime Integration** 完成（HEAD `8573aed`，4 commits，180 tests）：5 interfaces + 3 enums + 7 records + `AgentContextSnapshot`/`AgentContextDelta` + `GenericToolAgentAdapter`/`CodexAgentRuntimeAdapter`/`ClaudeCodeAgentRuntimeAdapter` + `AgentRuntimeBase` 抽象基类。1931 测试通过（+122 PublicApi baseline）。
- **R24 Agent Context Bridge + Task State Store** 完成（commit `c93e4a3`）：连接 Agent Runtime 到 ContextCore retrieval，`AgentTaskState` + `IAgentTaskStateStore` 契约。
- **R25 Bridging Agent Workspace Context Provider** 完成（commit `eba7bf9`）：合并 ContextCore retrieval + session injection。
- **R26 Agent Runtime Postgres Persistence** 完成（HEAD `802592d`）：`agent_checkpoints` + `agent_task_states` 表 + `PostgresAgentCheckpointStore` + `PostgresAgentTaskStateStore`，SchemaVersion v13 → v14。2069 测试通过。
- **R27 Evolution Pipeline Postgres Persistence** 完成（HEAD `6fe3203`）：`pipeline_runs` + 3 audit tables（`pipeline_canary_assignments`/`pipeline_rollback_records`/`pipeline_baseline_comparisons`）+ `IPipelineRunStore` 接口（9 methods）+ `PipelineRunSnapshot` record（11 fields，immutable）+ `PostgresPipelineRunStore` + `InMemoryPipelineRunStore`，SchemaVersion v14 → v15。`DefaultGuardedOptimizationPipeline` 重构为注入 `IPipelineRunStore`（默认 InMemory，Postgres provider 注册后覆盖）。2103 测试通过（+34 自 R26）。

下一阶段为 **R18-R21 路线图**（统一决策内核 → Policy Bundle → Multi-Expert → Memory Evolution）— **全部 14 子阶段已完成**（commits `e987e19` → `d4df506`），详见文末章节。既定路线图全部收口；后续任务待用户决定方向（候选：端到端集成 R17 V3 / Service DI 收敛 / DTO-R4 / 性能基准扩展）。

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

## 当前验收指标（2026-08-04 R30.1 Production Semantics Stabilization）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 当前 HEAD | `51f4d0d3`（P1 完善项 11 项全部完成并推送；8 个 WP：WP-G1/G2/G3/G4/G5/G6/G7/G8） | - |
| PublicApi baseline 行数 | R29 新增 AgentRunContracts + CanaryHAAggregationContracts + PerformanceAttributionContracts 三大契约集（IAgentModelTransport/IAgentLoopPolicy/IAgentRunStore/IAgentApprovalGate/IAgentToolCallValidator/IAgentRunEventStore + ICanaryExternalMetricsSource/ICanaryMetricsAggregator/ICanaryLeaderLease + IComponentHealthRegistry 等）；R30.1 各 WP 陆续追加（ToolFailurePhase/ToolRetrySafety/ReconciliationLease/ClaimLease/RecoverableSnapshot/NodeMembership/AdaptiveRetrieval 等） | 单一事实源 |
| 构建 | 0 错误 / 7 既有警告（benchmarks CS0618 5 处 + IntegrationTests 2 处，均非本轮引入） | 0 / 0 |
| 测试 | P1 收口最终全量验证：ContextCore.Tests **3539 总数 / 6 失败**（全部命中既有 7 项名单，与 P1 前基线对比无新增）/ 3519 通过 / 14 跳过；Service.Tests **0 失败 / 64 通过**（1 跳过）；ContextCore.IntegrationTests **21 失败 / 111 通过**（21 个失败全部命中 P1 前基线 111 项失败名单，非回归；P1 修复其中 90 项）。既有失败名单见 WORK_STATE.md | 除既有与环境性失败外 0 失败 |
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
| Postgres schema 版本 | cc-schema-v62（P1 WP-G1 迁移 0009 工具对账 DecisionRequestId 列） | 稳定 |
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

> R30.1 16 项 P0 已全部关闭，P1 完善项 11 项（WP-G1..G8）已全部完成，当前无待办 P0/P1。下一阶段待用户决定方向（候选：R30 Self-Learning Agent Runtime——Utility Ledger 物化 → Dataset Builder → 训练/校准 → Replay → Canary → Promotion）。以下小节为已完成阶段的记录与候选方向，供回溯参考。

### R30 候选方向（历史设计，暂缓启动）— Self-Learning Agent Runtime

> 状态：R30 系列已更名为 **R30.1 Production Semantics Stabilization**（当前进行中）。本节为更名前的候选设计，不再作为下一阶段任务；其闭环设计（Utility Ledger → Dataset Builder → 训练/校准 → Replay → Canary → Promotion）保留为候选方向（见 WORK_STATE.md「候选方向」）。

R29 Final Closure 完成后，下一阶段原规划为 R30 Self-Learning Agent Runtime。核心闭环：

```
Execution Artifact
+ Tool Outcome
+ User/Task Feedback
→ Utility Ledger
→ Dataset Builder
→ Training / Calibration
→ Replay
→ Canary
→ Promotion
```

**关键组件**（复用 R29 已有基础）：
- **Utility Ledger 物化**：从 AgentRunEventStore（R29 工作流 D）+ ExecutionArtifact（R18）+ Tool Outcome 自动写入 MemoryUtilityLedger（R21-2 已有契约），异步批量物化不影响热路径
- **Dataset Builder**：从 Utility Ledger + AgentRunEvent 构建训练数据集，复用 R29 EventStore 哈希链保证数据完整性
- **Training / Calibration**：对接 ICalibrationService（R29 工作流 C 已有），产出 Platt/Isotonic 校准参数
- **Replay**：从 AgentRunEventStore 重放 Run（R29 Cursor checkpoint 已支持从 EventStore 重建状态）
- **Canary**：复用 R29 工作流 E 的 Canary HA + Leader Lease + 外部指标，对校准参数变更做 canary 推进
- **Promotion**：复用 R29 工作流 E 的 CanaryProgressionService 渐进晋升 + DefaultPromotionJudge 跨阶段晋升

**与 R29 的衔接**：
- R29 工作流 D 的 AgentRunActor 产出 AgentRunEvent → R30 Utility Ledger 物化输入
- R29 工作流 C 的 ICalibrationValidator → R30 训练后校准参数验证
- R29 工作流 E 的 Canary + 外部指标 → R30 校准参数 canary 推进的决策依据
- R29 工作流 F 的组件归因 → R30 训练后性能回归检测

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
- ~~Pipeline run 持久化（替换 in-memory ConcurrentDictionary 为 PostgresContextLearningStore 或新 store）~~ **已完成（R27，HEAD `6fe3203`）** — `DefaultGuardedOptimizationPipeline` 重构为注入 `IPipelineRunStore`，Postgres 实现通过 `PostgresPipelineRunStore` 持久化到 `pipeline_runs` + 3 audit tables，SchemaVersion v15。
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

## R18-R21 路线图（统一决策内核 → Policy Bundle → Multi-Expert → Memory Evolution）

> **状态：全部 12 子阶段已完成**（commits `e987e19` → `cad1f0c`，HEAD `cad1f0c`）。R21-4/5 统一 MemoryState + decay evaluator + Utility Stats + Conflict Resolution 已完成（commit `d4df506`）。下面章节保留作为设计参考，不再作为 "下一阶段任务"。

### 诊断（基于代码核实）

当前存在两套独立的选择系统（已用代码验证 6 项分裂证据）：

| 维度 | Retrieval 路径 | Package 路径 |
|------|----------------|--------------|
| 入口 | `HybridContextRetriever.RetrieveAsync` | `BasicContextPackageBuilder.BuildDetailedAsync` |
| Channel 编排 | 自行编排 5 个 `IRetrievalChannelExecutor`（Mandatory/Context/Memory/Vector/Relation） | 通过 `PackageInputLoader` 间接调用 store |
| Ranking | `RetrievalPackingPolicy.OrderCandidates`（mandatory + score + tokens + ID） | `ResultProjector.ProjectTemplate` + `PackageUncertaintyBuilder`（uncertainty + score + ID） |
| 候选类型 | `ContextRetrievalCandidate` / `RetrievalChannelCandidate` | `PackageTraceCandidate` / `ContextPackageDecision` |
| Drop reason | 5 个自由文本字符串（"强制选中"/"超过 token 预算"/...） | 16 个 `CandidateDecisionReasonCode` 枚举值 |
| Drop reason 翻译 | 需要 `CandidateDecisionReasonCodeMapper` 反向翻译 | 原生枚举 |
| TokenBudget | 全局候选级累计硬上限 | section 级分层比例分配 + 部分截断接受 |
| 已有可复用资产 | `DecisionEvidenceV2`（含 ChannelSources/ScoreBreakdown/FinalScore，仅 trace 投影） | 同上 |

**关键发现**：`DecisionEvidenceV2` 已实现提案中 `CandidateFeatureVector` + `CandidateUtilityScore` 的核心字段，但**目前仅作为 trace 投影**，未驱动运行时决策。R18 的本质工作是把这份 trace-only 契约"提升"为运行时 envelope。

### R18 — 统一决策内核（4 子阶段，**全部已完成**）

**目标**：建立 `IContextDecisionEngine` + `ContextCandidateEnvelope`，让 Retrieval 与 Package 共享候选身份、特征、safety gate、utility score、reason code、token cost、policy/model version、decision evidence。**不**强行合并输出格式 — 通过不同 Projector 输出。

**接口契约**：
```csharp
public interface IContextDecisionEngine
{
    Task<ContextDecisionResult> DecideAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken = default);
}
```

**统一中间模型**（不与现有 `ContextRetrievalCandidate` / `ContextPackageDecision` 冲突）：
```csharp
public sealed record ContextCandidateEnvelope
{
    public required string CandidateId { get; init; }
    public required ContextCandidateSource Source { get; init; }
    public CandidateFeatureVector Features { get; init; }
    public CandidateSafetyState Safety { get; init; }
    public CandidateUtilityScore Utility { get; init; }
    public int EstimatedTokens { get; init; }
    public IReadOnlyList<EvidenceRef> ProvenanceRefs { get; init; } = [];
}
```

**子阶段**（全部已完成）：
- **R18-1 契约设计**（commit `e987e19`，+96 PublicApi）：定义 `EvidenceRef` + `ContextCandidateEnvelope` + `CandidateFeatureVector` + `CandidateSafetyState` + `CandidateUtilityScore` 5 个契约，基于 `DecisionEvidenceV2` 提升。21 个 DecisionEngineContractsTests 验证可实施性。
- **R18-2 Engine 接口 + Planner**（commit `d752310`）：定义 `IContextDecisionEngine` + `ContextDecisionPlanner` + `RetrievalResultProjector` + `PackageResultProjector`，在 `ContextDecisionProjector` 内部增加 envelope-to-decision 投影路径。
- **R18-3 Retrieval adapter**（commit `26cfc36`）：在 Retrieval 路径增加 envelope adapter（`ContextRetrievalCandidate` → `ContextCandidateEnvelope`），验证 golden baseline 不变。
- **R18-4 Package adapter**（commit `268589a`）：在 Package 路径增加 envelope adapter，验证 golden baseline 不变。
- **R18 V2**（后续）：真正统一执行链（Candidate Collectors → Feature Pipeline → Safety Gate → Utility Scorer → Budget Allocator），两条路径共享。

**验收标准**：
1. Retrieval 与 Package 使用同一个 Candidate ID 和 feature schema
2. 正式规则关闭模型时，输出与当前 golden baseline 完全一致
3. 同一评分逻辑不再分别存在于 Retrieval 和 Package
4. 不新增存储 I/O
5. p95 和 allocation 不允许明显回退
6. Model failure 时可精确回退到 deterministic policy

### R19 — Policy Bundle 与 Model Runtime（3 子阶段，**全部已完成**）

**目标**：建立版本化策略包 `ContextPolicyBundle`，让规则权重、relation profile、budget、router、模型和 rollout 状态集中管理。

**契约**：
```csharp
public sealed record ContextPolicyBundle
{
    public required string PolicyId { get; init; }
    public required string Version { get; init; }
    public required string FeatureSchemaVersion { get; init; }
    public required RetrievalPolicyProfile Retrieval { get; init; }
    public required BudgetPolicyProfile Budget { get; init; }
    public required SafetyPolicyProfile Safety { get; init; }
    public ModelArtifactReference? RouterModel { get; init; }
    public ModelArtifactReference? RankerModel { get; init; }
    public required RolloutPolicy Rollout { get; init; }
    public required string ContentHash { get; init; }
}
```

**必须支持**：deterministic fallback / model load failure fallback / workspace-scoped rollout / immutable version / atomic activation / previous-version rollback / feature schema compatibility / policy hash + model hash / 禁止运行中修改已激活 bundle

**子阶段**（全部已完成）：
- **R19-1 契约 + Registry**（commit `60fd1fe`）：定义 `ContextPolicyBundle` + 3 个 Profile + `RolloutPolicy` + `ModelArtifactReference`，内嵌 `ContextDecisionPolicyVersions`（复用 OPT-4 解耦的 5 个能力版本）。实现 `PolicyRegistry` + immutable snapshot 加载 + `ContentHash` 验证。
- **R19-2 PolicyBundle Provider 适配**（commit `5031ea1`）：所有现有 policy source（`RetrievalPolicyProfiles` / `ModeBudgetProfile` / `ContextPackagePolicy`）适配为 PolicyBundle provider。`DefaultPolicyRegistry`（in-memory `IPolicyRegistry` 实现）+ `DefaultPolicyBundleFactory`（基于 `ContextDecisionPolicyVersions` 生成默认 bundle）。
- **R19-3 Pipeline 集成**（commit `6a13af1`）：接入 R17 GuardedOptimizationPipeline — bundle 切换走 shadow/canary，不绕过 rollback。Engine 读取 PolicyBundle 通过 `IPolicyRegistry`。

### R20 — Budget-Aware Multi-Expert Selection（2 子阶段，**全部已完成**）

**专家划分**：Mandatory / Lexical / Semantic / WorkingMemory / StableMemory / Graph / Recency / Constraint（8 个）

**Router 输出**：
```csharp
public sealed record ExpertRoutingDecision
{
    public RetrievalExpertMask EnabledExperts { get; init; }
    public IReadOnlyDictionary<RetrievalExpert, int> CandidateBudgets { get; init; }
    public IReadOnlyDictionary<RetrievalExpert, int> TokenBudgets { get; init; }
    public float Confidence { get; init; }
    public string ReasonCode { get; init; } = "";
}
```

**关键约束**：
- Mandatory 和 Hard Constraint expert 永远不能关闭
- Router 低置信度时执行完整安全路径
- Router 只能减少可选检索成本，不能绕过 lifecycle gate
- 先 shadow 运行完整专家集，再模拟不同 mask
- 训练标签来自 counterfactual contribution，而不是当前 router 自己的决定

**子阶段**（全部已完成）：
- **R20-1 Expert 概念对齐**（commit `87718d6`）：定义 `RetrievalExpert` 枚举（8 值）+ `ExpertRoutingDecision` + `RetrievalExpertMask`。把现有 5 个 `IRetrievalChannelExecutor` 重命名/拆分对齐到 expert 概念。**不**改变运行时行为。
- **R20-2 Router 实现**（commit `9c65e8d`）：实现 `IRetrievalRouter` 接口，输入 envelope + PolicyBundle，输出 `ExpertRoutingDecision`。Budget-Aware TopK 分配。Router 模型未加载时 fallback 到 deterministic policy（"启用所有 expert"）。

**优化目标**：Quality - λ1×Latency - λ2×Allocation - λ3×TokenCost - λ4×Risk（不是单一最高质量配置，而是质量—成本 Pareto frontier）

### R21 — Memory Evolution Engine（3 子阶段 + R21-4/5，**全部已完成**）

**目标**：让记忆系统具备长期演化能力（不只是 Promotion）。

**新能力**：
1. **Consolidation** — 多个 task update → evidence merge → 新版本 working memory → 旧版本 superseded
2. **Forgetting 与降权** — 7 状态机：Fresh / Active / Cooling / Dormant / Archived / Superseded / Rejected；降权因素：长期未命中/已有新版本/evidence 失效/任务已完成/与当前状态冲突/多次被选择但未产生有效贡献
3. **Conflict Resolution** — ConflictSet + evidence comparison + resolution status + chosen authority
4. **Memory Utility Ledger** — Recall/Selected/Useful/Correction/Conflict/TokenCost/Anchor/LastUsefulTime

**子阶段**（全部已完成）：
- **R21-1 Superseded 状态 + Consolidation**（commit `d3303eb`）：扩展 `ContextMemoryStatus` 增加 `Superseded` 一个状态（避免一次性迁移 7 状态）。实现 Consolidation ETL（多 task update → 新版本 working memory + 旧版本 superseded）。
- **R21-2 Utility Ledger + ConflictSet 契约**（commit `8ad07e0`）：定义 `MemoryUtilityLedger` record + `IConflictSet` 契约。Ledger 由 trace 被动填充，不主动查询。模型可建议 promotion/demotion/merge/archive，但正式写入仍经 R17 Pipeline。per-Expert contribution store 为只读。
- **R21-3 完整状态机**（commit `cad1f0c`）：扩展状态机增加 Cooling/Dormant/Archived。Ledger 驱动状态转换，但状态写入仍受规则和审查边界约束。Memory Evolution Engine 完整实现（full state machine + ETL + Utility Ledger materializer）。
- **R21-4 统一 MemoryState**（commit `d4df506`）：8-state enum（Fresh/Active/Cooling/Dormant/Superseded/Replaced/Archived/Rejected）+ `MemoryStateEventRecord` + `IMemoryStateStore` + `MemoryStateExtensions` state machine（IsTerminal/CanTransitionTo/NeedsConsolidation/IsDecaying/IsActiveOrFresh/CanReheat）。`InMemoryMemoryStateStore` 替换 `InMemorySupersededItemStore`。`DefaultConsolidationETL` 适配新状态机支持 Dormant→Archived 终极降级。15 files / +1558 / -970。
- **R21-5 Memory Utility Stats + Conflict Resolution**（commit `d4df506`）：`MemoryUtilityStats` record（recall/selected/dropped/useful/correction/conflict/token/anchor + 计算属性 SelectionRate/UsefulRate/CorrectionRate/AverageTokenCost）+ `IMemoryUtilityStatsStore` + `InMemoryMemoryUtilityStatsStore`。Conflict Resolution：`ConflictResolutionStatus` enum（5 values）+ `ConflictSet` 4 字段（ResolutionStatus/ChosenAuthority/ResolvedAt/Resolver）+ `ConflictSetQuery` 过滤 + Materializer 自动填充。`ChosenAuthority` 支持 "highest-score"/"lowest-token-cost"/...

### R18-R21 跨阶段澄清（8 项）

| 问题 | 决定 |
|------|------|
| Envelope Evidence | 共享 `EvidenceRef` 类型；Envelope 使用 `ProvenanceRefs`，V2 在其上追加决策引用 |
| PolicyBundle scope | Bundle 全局不可变；Activation 按 workspace/collection；暂不增加 tenant |
| Request Policy | 改为受限 override，不允许替换安全边界和正式模型 |
| Utility Ledger | 新增独立 Store，但由 Trace/Event 异步批量物化（不影响热路径） |
| Router 标签 | Expert-level ablation 为主，不做全量 candidate LOO |
| Expert 重叠 | 删除该 Expert 的特征贡献；只有无其他来源时才删除 Candidate |
| Budget 标签 | 模拟各 Expert 的 Top-K 质量—成本曲线 |
| 交互归因 | 普通样本 LOO，困难样本 pair ablation，少量样本近似 Shapley |

### R18-R21 实施顺序

R18-1 → R18-2 → R18-3 → R18-4 → R19-1 → R19-2 → R19-3 → R20-1 → R20-2 → R21-1 → R21-2 → R21-3 → R21-4 → R21-5

**严格顺序**：R19 PolicyBundle 需要 R18 envelope；R20 Router 需要 R19 PolicyBundle；R21 Ledger 复用 R17 Pipeline + R18 envelope trace 投影。不并行推进以避免 PR 巨大且契约间依赖混乱。

**状态：全部 14 子阶段已按严格顺序完成**（commits `e987e19` → `d4df506`）。

###

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
