# 工作状态（Work State）

> 用途：上下文压缩时快速恢复当前状态。只保留必要信息（状态、计划、目标、必要元信息）；任务完成立即更新，无关内容及时清理。
> 维护规则见 AGENTS.md「工作状态维护」；路线图与历史见 TODO.md。

## 目标与计划

- 当前目标：随用户指示推进，无既定进行中任务。
- 候选方向（详见 TODO.md）：R30 Self-Learning Agent Runtime —— Utility Ledger 物化 → Dataset Builder → 训练/校准 → Replay → Canary → Promotion。
- **性能优化优先级**（三组，实施前先核对现状，部分已有基础）：
  - **第一组 正确性+性能**：Agent 调度（队列满快速返回、DB claim 替代反复 Recovery 扫描、Workspace 公平队列、批量领取 Run、queue wait histogram）；Tool Effect（Prepare+Intent 单事务、Result 按 RequestId 点查、Reconciliation 批量扫描、Descriptor Frozen Cache、恢复避免重复模型调用）；Retrieval（正式 Keyset Cursor、Selected-only Content/Relation Hydration、Graph per-seed 配额、投影 DTO 避免 JSONB、Exact token 只对 Selected 执行）；Event Recovery（从 Checkpoint Cursor 起、Event page、Snapshot compaction、终态 SSE 全量 drain、Raw events 分页）。
  - **第二组 推理热路径**（Inference Scheduler 生命周期问题已基本关闭）：MaxConcurrency 按 CPU/CUDA/DirectML profile 配置（单 GPU 默认不用 ProcessorCount）；记录 batch fill ratio / queue wait / cancellation waste / session contention / shard count；避免每请求额外小数组与 continuation 分配。
  - **第三组 CI 性能**：当前 CI 多 Job 上传下载整个 `**/bin`、`**/obj`；先实测 Artifact 大小，再选方案（每 Job restore/build、只上传测试所需输出、分项目 Artifact、或 reusable build cache）。
  - 注：批量领取 Run（R29 P1 LeaseBatch）、Checkpoint Cursor（R29 P4 Cursor>Delta>Full）已有基础。

## 当前状态

- **基线**：main @ `c94f8298`（工作树干净）。
- **进行中**：性能优化 WP-P2 已完成，待提交（见下）。
- **最近完成**：WP-P2 检索 Selected-only 正文水合 + Exact token 只对 Selected。

### 性能优化工作包进度

- **WP-P1 已完成**：`ILeasedJobQueue.AcquireLeaseBatchAsync`（批量领取 + per-workspace 公平，两阶段 ROW_NUMBER）；`ContextJobWorker` 改为批量领取并按空闲槽位分派；新增 `PostgresJobQueueMetrics.QueueWait` 直方图（入队→领取等待时长）；`RealToolDispatcher.GetToolDefinitions` 冻结缓存；`JobWorkerOptions.MaxPerWorkspaceClaim`（默认 10）。验证：build 0 错误；ContextCore.Tests 11 既有失败不变（+2 新测试通过）；Service.Tests 64 通过；集成测试本地跳过（Docker 不可用，CI 覆盖）。
- **WP-P2 已完成**：检索 Selected-only 正文水合 + Exact token 只对 Selected。keyword/mandatory 通道保留 IncludeContent=true（评分依赖正文，不改基线）；向量通道改为元数据投影召回（探测 `IContextStoreMetadataLookup`/`IMemoryStoreMetadataLookup`，缺失则回退批量/单条）；Postgres context/memory 元数据投影补齐存储元数据字典（`data->'Metadata'`，修正 metadata 路径下 deprecated 过滤）；pack 阶段 token 估算回退摄取持久化的 `__content_token_cost`；新增 `SelectedCandidateContentHydrator`，Pack 后仅对 Selected 候选批量水合正文并重算 token；`HybridContextRetriever` 可选接入 tokenizer。验证：build 0 错误（无新增警告）；全量 ContextCore.Tests 3379 总数 / 11 既有失败不变（+4 新测试通过）；Service.Tests 0 失败 64 通过；集成测试本地跳过（CI 覆盖）。
- **WP-P3（推理热路径）**、**WP-P4（快照自动压缩）**、**WP-P5（CI Artifact）** 待做。

## 必要元信息

- 解决方案：`ContextCore.sln`（.NET 10，Windows / pwsh）。
- 规则文件：`AGENTS.md`（注释规范、工程化原则、工作状态维护）。
- 路线图：`TODO.md`；历史归档 `docs/archive/roadmap-history.md`。
- 提交约定：commit 消息写入 UTF-8 临时文件后用 `git commit -F`（pwsh 内联中文会乱码）；push main 已预授权。
- 验证门禁：build 0 错误；build 与 test **串行**执行；开发中只跑定向测试，**全量测试在全部任务完成后统一跑一次**（ContextCore.Tests 失败须**恰好 11 个既有项**，名单见下）。

## 既有 11 个测试失败（勿当回归处理）

`Benchmark_Baseline_Contains_AtLeast_15_Samples_PerCase`、`DI_Registration_EngineResolvesWithAllocatorV2_1`、`Journal_StateTransition_ThrowsOnRegression`、`MandatoryProvider_NoTokenizer_EmptyContent_Succeeds`、`MemoryOnlyBatchLookup_SatisfiesHydrationPipeline`、`PostgresMigrationSql_ShouldExposeVectorIndexProviderSchema`、`ProductionComposition_Postgres_NoNonDecoratorUnexpectedDuplicates`、`ProductionHA_AllNineMandatoryChecksPass_WhenFullyConfigured`、`ReadinessService_Development_GetRegisteredWorkers_ReturnsExpectedList`、`RequiredIndexes_IncludeUtilityLedgerIndexes`、`SchemaVersion_IsV30`
