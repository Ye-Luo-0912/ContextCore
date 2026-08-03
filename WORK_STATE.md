# 工作状态（Work State）

> 用途：上下文压缩时快速恢复当前状态。只保留必要信息（状态、计划、目标、必要元信息）；任务完成立即更新，无关内容及时清理。
> 维护规则见 AGENTS.md「工作状态维护」；路线图与历史见 TODO.md。

## 目标与计划

- 当前目标：随用户指示推进，无既定进行中任务。
- 候选方向（详见 TODO.md）：R30 Self-Learning Agent Runtime —— Utility Ledger 物化 → Dataset Builder → 训练/校准 → Replay → Canary → Promotion。
- **性能优化优先级**（三组，实施前先核对现状，部分已有基础）：
  - **第一组 正确性+性能**：Agent 调度（队列满快速返回、DB claim 替代反复 Recovery 扫描、Workspace 公平队列、批量领取 Run、queue wait histogram）；Tool Effect（Prepare+Intent 单事务、Result 按 RequestId 点查、Reconciliation 批量扫描、Descriptor Frozen Cache、恢复避免重复模型调用）；Retrieval（正式 Keyset Cursor、Selected-only Content/Relation Hydration、Graph per-seed 配额、投影 DTO 避免 JSONB、Exact token 只对 Selected 执行）；Event Recovery（从 Checkpoint Cursor 起、Event page、Snapshot compaction、终态 SSE 全量 drain、Raw events 分页）。
  - **第二组 推理热路径**（Inference Scheduler 生命周期问题已基本关闭）：MaxConcurrency 按 CPU/CUDA/DirectML profile 配置（单 GPU 默认不用 ProcessorCount）；记录 batch fill ratio / queue wait / cancellation waste / session contention / shard count；避免每请求额外小数组与 continuation 分配。
  - **第三组 CI 性能（已完成）**：CI 产物瘦身——build-output 由全量 `**/bin`+`**/obj`（实测 ~3.4GB）改为 `tests/**/bin`+`**/obj`（实测 ~1.5GB，-55%）；应用/基准项目 bin 对 `--no-build` 测试执行无用，全量 obj 保留供资产解析；已验证移除 src/benchmarks bin 后解决方案级 `dotnet test --no-build --no-restore` 正常执行。
  - 注：批量领取 Run（R29 P1 LeaseBatch）、Checkpoint Cursor（R29 P4 Cursor>Delta>Full）已有基础。

## 当前状态

- **基线**：main @ `10c86ee2`（TODO.md 路线图更新已推送；R30.1 实施中）。
- **进行中**：R30.1 Production Semantics Stabilization（16 项 P0 阻断项，12 个 WP，计划见会话 plan.md）。
- **最近完成**：WP-A1 Tool 失败阶段 + 重试安全契约（P0-1 + P0-2）。

### R30.1 P0 阻断项修复进度

- **WP-A1 已完成**（P0-1 + P0-2）：Tool 失败阶段 + 重试安全契约。
  - 契约（Abstractions，PublicApi baseline 已更新）：`ToolFailurePhase`（BeforeIntent / AfterIntentBeforeProvider / ProviderCallAmbiguous / ProviderReturned / JournalCommitFailed）；`ToolRetrySafety`（Never / BeforeDispatchOnly / ProviderIdempotent / ProviderConfirmedNoEffect）；`ToolDescriptor.RetrySafety`（默认 Never）；`ToolExecutionResult.FailurePhase`（可空）+ `NoEffectConfirmed`；`ToolDispatchResult.NoEffectConfirmed`；`ToolHandlerResult.NoEffectConfirmed`（Core，Provider 侧确认无副作用）；`IToolDispatchJournal.GetStateAsync(workspaceId, runId, requestId)`（完整租户键查询真实状态）。
  - P0-1 修复（DefaultDurableToolExecutor）：Intent 持久化后的失败路径（Lease Fence 过期/缺失、Dispatcher 异常耗尽、内部不一致、Prepare 异常命中高级状态）不再伪造 `Prepared`——统一经 `QueryJournalStateAsync` 查询真实 Journal 状态并原样返回（查询失败 fail-closed 返回 DispatchingIntent 强制对账），失败结果保留 ExternalOperationId / IdempotencyKey / ReconciliationHandler / ReconciliationDeadline / FailurePhase；Reconcile 恢复决策回传真实 CurrentState（DispatchingIntent 不再伪装 Dispatched）；Commit CAS 失败标记 JournalCommitFailed。Actor 无需改动——`RequiresReconciliation(JournalState)` 命中即创建对账记录。
  - P0-2 修复（DefaultToolEffectPolicy.DecideRetry）：重试安全门改为显式契约驱动——None/ReadOnly 天然可重试；IdempotentWrite/Write 仅当 `RetrySafety=ProviderIdempotent` 且携带稳定幂等键，或 `RetrySafety=ProviderConfirmedNoEffect` 且 Provider 返回 `NoEffectConfirmed=true`（FencedWrite 同后者）时才自动重试；普通 Write 默认 Never → 即使 MaxRetries>0 也不自动重试（外部写失败/超时 ≠ 副作用未发生）。RealToolDispatcher 贯通 NoEffectConfirmed。
  - 测试：新增 `R30X_ToolFailurePhaseTests`（8 项：异常路径真实状态 + 对账字段 + ExternalOperationId 保留、重试耗尽、状态查询失败 fail-closed、Fence 过期、ProviderReturned 字段回传、ProviderConfirmedNoEffect 端到端重试）；反转 `Policy_Retry_WriteWithMaxRetries_RetriesWithDelay` 为默认不可重试并新增 6 项安全门矩阵；`Executor_RequiresLeaseFence_NoFence_FailsClosed` 断言从 Prepared 改为 DispatchingIntent；两处 Reconcile 路径断言从 Dispatched 改为 DispatchingIntent（真实状态）。
  - 验证：build 0 错误；定向 111 项全通过（含 PublicApi baseline）；Service.Tests 无受影响断言。
- **WP-C1 已完成**（P0-10 即时安全）：事件压缩仅限终态 Run。`PostgresAgentRunEventCompactor.FindCandidatesAsync` JOIN `agent_runs` 过滤——终态（Completed/Cancelled/LeaseLost/ReconciliationRejected/RecoveryBlocked/RecoveryCorrupted/DeadLettered）直接可压缩，Failed 仅在重试已耗尽（retry_count >= max_retries）时可压缩（仍可重试的 Failed 会被调度器重放事件流）；新增公共静态 `IsCompactableRunState`（Storage.Postgres，非 Abstractions，无 baseline 影响）；操作员 `POST /{id}/compact` 端点增加同规则守卫（非终态 409）。背景：当前 Recovery 不读快照/归档，非终态压缩会导致重启恢复判定 RecoveryCorrupted。验证：build 0 错误；新测试 4/4 + R30S/R29S 压缩相关 18 项全通过。

### 性能优化工作包进度

- **WP-P1 已完成**：`ILeasedJobQueue.AcquireLeaseBatchAsync`（批量领取 + per-workspace 公平，两阶段 ROW_NUMBER）；`ContextJobWorker` 改为批量领取并按空闲槽位分派；新增 `PostgresJobQueueMetrics.QueueWait` 直方图（入队→领取等待时长）；`RealToolDispatcher.GetToolDefinitions` 冻结缓存；`JobWorkerOptions.MaxPerWorkspaceClaim`（默认 10）。验证：build 0 错误；ContextCore.Tests 11 既有失败不变（+2 新测试通过）；Service.Tests 64 通过；集成测试本地跳过（Docker 不可用，CI 覆盖）。
- **WP-P2 已完成**：检索 Selected-only 正文水合 + Exact token 只对 Selected。keyword/mandatory 通道保留 IncludeContent=true（评分依赖正文，不改基线）；向量通道改为元数据投影召回（探测 `IContextStoreMetadataLookup`/`IMemoryStoreMetadataLookup`，缺失则回退批量/单条）；Postgres context/memory 元数据投影补齐存储元数据字典（`data->'Metadata'`，修正 metadata 路径下 deprecated 过滤）；pack 阶段 token 估算回退摄取持久化的 `__content_token_cost`；新增 `SelectedCandidateContentHydrator`，Pack 后仅对 Selected 候选批量水合正文并重算 token；`HybridContextRetriever` 可选接入 tokenizer。验证：build 0 错误（无新增警告）；全量 ContextCore.Tests 3379 总数 / 11 既有失败不变（+4 新测试通过）；Service.Tests 0 失败 64 通过；集成测试本地跳过（CI 覆盖）。
- **WP-P3 已完成**：推理热路径。MaxConcurrency 按 profile 配置（CPU=ProcessorCount，CUDA/TensorRT/DirectML 单 GPU 默认 1）；新增 `OnnxExecutionProvider.DirectML`（`AppendExecutionProvider_DML(deviceId)`）；`InferencePhaseTimingCallback` 接线到 `DefaultComponentHealthRegistry.RecordInferencePhaseTime`（scopeKey "default"，两处生产配置构造点）；新增 `InferenceMetrics`（Meter "ContextCore.Inference.Onnx"：session_contention / shards_executed / queue_wait / batch_fill_ratio / cancellation_waste 五项）；减少每请求分配（stackalloc int[2]、rowOffsets 走 ArrayPool；NamedOnnxValue[] 因 ORT 无 span 重载保留）。验证：build 0 错误（仅既有 CS8625 警告）；R30 新测试 6/6；既有推理测试 39 通过 1 跳过；Service.Tests 0 失败 64 通过（1 跳过）。
- **WP-P4 已完成**：Event 快照自动压缩后台 worker。`IAgentRunEventCompactor` 新增 `FindCandidatesAsync`（热表按 Run 分组统计事件数，HAVING 阈值 + 限量降序）；新增 `AgentRunCompactionCandidate`（含 LastSequence，worker 以之为折叠上界全量折叠——避免 -1 哨兵只锚定首事件）；新增 `AgentRunEventCompactionWorker`（profile-agnostic，非 Postgres 自退出；简单阈值策略：每轮扫描 ≥MinEventCount(默认 1000) 的 Run，按事件数降序最多压缩 MaxRunsPerPass(20) 个；单 Run 失败不影响本轮其他 Run，连续失败指数退避）；新增 `AgentRunEventCompactionOptions`（"EventCompaction" 配置节）；抽取 `WorkerBackoff` 共享退避，`ModelStateReconcilerWorker.ComputeBackoffDelay` 委托复用；三 profile 统一注册 + workerRegistry。验证：build 0 错误（无新增警告）；R30S 新测试 10/10；PublicApi baseline 通过；R29S/R29H 注册与对账相关定向测试通过（唯一失败为 11 个既有项之一）。
- **WP-P5 已完成**：CI Artifact 瘦身。实测全量 `**/bin`+`**/obj` ≈ 3.4GB（大头：benchmarks bin 560MB、IntegrationTests/Tests/Service.Tests bin 460-490MB、Service/Evaluation/ControlRoom bin 418-424MB，均为 OnnxRuntime 原生库传递复制）；改为上传 `tests/**/bin`+`**/obj` ≈ 1.5GB（-55%）——下游 test job 仅执行 3 个测试项目，其 bin 自含全部传递依赖；`**/obj` 全量保留（project.assets.json 资产解析）。验证：YAML 结构完整（7 job 不变）；移除 src/benchmarks 全部 17 个 bin 后解决方案级 `dotnet test ContextCore.sln --no-build --no-restore` 定向测试 10/10 通过；evidence/TRX 门禁不变。附带修复 WP-P4 引入的回归：`AgentRunEventCompactionWorker` 的 `IAgentRunEventCompactor` 改为可选参数（默认 null）注入（MS DI 可空注解不构成可选注入，未注册时 ValidateOnBuild 导致宿主构建失败，Service.Tests 42 失败）。最终全量验证：ContextCore.Tests 3395 总数 / **恰好 11 个既有失败**（3370 通过，14 跳过）；Service.Tests **0 失败 / 64 通过** / 1 跳过。

## 必要元信息

- 解决方案：`ContextCore.sln`（.NET 10，Windows / pwsh）。
- 规则文件：`AGENTS.md`（注释规范、工程化原则、工作状态维护）。
- 路线图：`TODO.md`；历史归档 `docs/archive/roadmap-history.md`。
- 提交约定：commit 消息写入 UTF-8 临时文件后用 `git commit -F`（pwsh 内联中文会乱码）；push main 已预授权。
- 验证门禁：build 0 错误；build 与 test **串行**执行；开发中只跑定向测试，**全量测试在全部任务完成后统一跑一次**（ContextCore.Tests 失败须**恰好 11 个既有项**，名单见下）。

## 既有 11 个测试失败（勿当回归处理）

`Benchmark_Baseline_Contains_AtLeast_15_Samples_PerCase`、`DI_Registration_EngineResolvesWithAllocatorV2_1`、`Journal_StateTransition_ThrowsOnRegression`、`MandatoryProvider_NoTokenizer_EmptyContent_Succeeds`、`MemoryOnlyBatchLookup_SatisfiesHydrationPipeline`、`PostgresMigrationSql_ShouldExposeVectorIndexProviderSchema`、`ProductionComposition_Postgres_NoNonDecoratorUnexpectedDuplicates`、`ProductionHA_AllNineMandatoryChecksPass_WhenFullyConfigured`、`ReadinessService_Development_GetRegisteredWorkers_ReturnsExpectedList`、`RequiredIndexes_IncludeUtilityLedgerIndexes`、`SchemaVersion_IsV30`
