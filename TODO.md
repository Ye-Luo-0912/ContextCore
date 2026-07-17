# ContextCore 项目路线图

> 最近更新：R12.4A 正确性闭环 + R12-F.1 Current-HEAD Rebaseline（2026-07-17）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 freeze / report / audit / plan 类文档均为历史快照，仅供回溯，不作为设计依据。历史完成记录已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 当前阶段

**R13 Cache Correctness Gate 启动期** — R12.4A Post-Refactor Correctness Closure（10 项行为正确性修复）已全部完成并提交（commit dbf963f）。R12-F.1 Current-HEAD Rebaseline 已完成：A3 语义不变性 100%、Retrieval golden ranking 30 样本全通过、GRAPH-09 图不变性 12 测试全通过、BenchmarkDotNet 37 个 benchmark 基线已采集（Package Cold 2.85ms / CacheHit 7.5μs / Allocation 924.54KB @ ItemCount=50）。下一阶段为 R13.0 Cache Correctness Gate。剩余 Domain/Api/Ports 全量重排与 Service DI 大改暂缓。

---

## 硬边界

- ControlRoom 和 Service 不再编译期引用 Evaluation（P3.1 已完成）
- Evaluation 依赖只能是 Evaluation → Core/Storage/Abstractions/Client/Runtime
- Abstractions 只承载 Contracts/DTOs/Enums/跨层协议，不含实现逻辑
- Client 只承载 Service client，不被 eval host 接口污染
- 构建必须 0 警告 0 错误
- 全量测试必须 0 失败
- 所有变更提交到 GitHub main 分支

---

## 当前验收指标（2026-07-17）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 生产代码总行数 | ~98,222 | < 220k |
| Abstractions 代码行数 | ~8,525 | 跨层契约 |
| ControlRoom 代码行数 | ~13,559 | < 20k |
| Core 代码行数 | ~29,101 | - |
| EvalCommand*.cs 单文件行数 | 2,820 | P1-5 已完成 |
| FoundationStatusService.cs 行数 | 606 | P4 已完成 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | 899+1skip / 44 / 6+27skip 通过 / 0 失败 | 0 失败 |
| A3 语义不变性 | PassRate 100%, Recall@10 100% | 与冻结基线一致 |
| Retrieval golden ranking | 30 样本全通过, Recall@10 100% | 与冻结基线一致 |
| GRAPH-09 图不变性 | 12 测试全通过 | 0 失败 |
| Package Build p95 (Cold, ItemCount=50) | 2.85ms (mean), Allocation 924.54KB | 基线已采集 |
| Package Build p95 (CacheHit, ItemCount=50) | 7.5μs (mean), Allocation 12.38KB | 基线已采集 |
| FileSystem Package Build (Cold, ItemCount=50) | 19.0ms (mean), Allocation 1538.63KB | 基线已采集 |
| CacheChurn WriteWithLruEviction (Capacity=10000) | 408ms (mean), Allocation 12470KB | 基线已采集 |

---

## 历史完成记录

历史完成记录（R7~R12 系列、P0~P5 系列、DTO-R1~R4 等）已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 下一阶段任务

### R12 路线图剩余项

**R12-3：Package Contribution Pipeline** — ✅ 已完成（commit c4bf7cb）。BasicContextPackageBuilder 519→257 行，提取 PackageRequestFingerprintBuilder + PackageBuildingTypes + PackagePolicyResolver。

**R12-4：Retrieval Batch & Parallel Read** — ✅ 已完成（commit a5cc42e）。新增 IContextStoreBatchLookup / IMemoryStoreBatchLookup 能力接口，FileSystem store 单次锁批量读取（N→1），Mandatory + Vector executor 检测能力+回退兼容。

**R12-5：Append Log Storage** — ✅ 已完成（commit 3a8614d）。QueryRecent 尾部读取优化（TraceQueryHelper budget=take*2 提前终止），retention/compaction（FileTraceJanitor 1 小时间隔触发 fail-open 清理 yyyyMMdd 过期分片）。

**R12-6：Evaluation I/O Consolidation** — ✅ 目标已满足。EvalCommand 当前 3199 行（低于 4000-5000 目标），gate execution shell 模式已在前序重构中消化，WriteJsonAsync 已泛型化（R17-F）。剩余 I/O helper 提取为可选优化，非阻塞项。

**R12.4A：Post-Refactor Correctness Closure** — ✅ 已完成（commit dbf963f，2026-07-17）。10 项行为正确性修复：
1. Memory Recall 与 Keyword Recall 解耦
2. Package 合并查询保持 per-layer/per-level 配额
3. Tokenizer 截断公式
4. Section 最终兜底截断后重新计算 attribution
5. Section refs 只引用真正进入输出的 segment
6. File Job Queue Ack/Nack 原子化
7. Retrieval deterministic CandidateId tie-break
8. Relation quota 语义修正（Pack 阶段显式预留 + rollover）
9. Event Sink fail-open（success-path emit 独立 try-catch）
10. Graph Writer fallback 最终删除（移除 4 处死分支）
- Build = 0/0 ✅；Tests = 899+1skip / 44 / 6+27skip ✅

**R12-F.1：Current-HEAD Rebaseline** — ✅ 已完成（2026-07-17）。
- A3 语义不变性 ✅（50 样本 PassRate 100%, Recall@10 100%, MustNotHit=0, HardConstraintMissing=0 — 与冻结基线完全不变）
- Retrieval golden ranking ✅（30 样本全通过, Recall@10 100%, 覆盖 vector/keyword/deprecated-filter/relation-expansion/cross-layer 5 维度）
- GRAPH-09 图不变性 ✅（12 测试全通过）
- BenchmarkDotNet 基线 ✅（37 个 benchmark, 详见 `benchmarks/results/README.md`）

**R12-F：Lean Runtime Freeze** — ✅ 验收完成（2026-07-17）。
- Build = 0/0 ✅
- Tests = 0 failed ✅（ContextCore.Tests 899+1skip, Service.Tests 44, IntegrationTests 6+27skip）
- PublicAPI 基线干净 ✅（仅 R12-4 新增 2 接口+2 方法，R12-5 无变化）
- A3 语义不变性 ✅（50 条样本 PassRate 100%, Recall@10 100%, Failed 0 — 与基线完全一致）
- Evaluation 净减 2,347 行 ✅（>= 2,000 目标）
- File trace 不随历史恶化 ✅（R12-5 TraceQueryHelper budget 提前终止 + retention 清理）
- 生产代码净减 3,484 行 ⚠️（< 8,000 目标，因 R12-R18 系列新增基础设施：批量查询接口、retention 清理、trace 分片、源生成器等；纯删除 14,861 行满足目标，净减被新增抵消）
- Retrieval p95 / Package allocated bytes ✅（R12-F.1 基线已采集，见上方验收指标表）

### R13 路线图

**R13.0：Cache Correctness Gate** — Cache 保持生产关闭，先建立正确性闸门。
- factory 前后版本向量比较
- stale computation 丢弃或单次重试
- PackageTemplate 强不可变
- factory shutdown token 与 timeout
- version bump 先于 physical eviction
- Cache TTL
- semantic metadata fingerprint
- mutable result isolation tests
- write-during-build race tests
- Cache 继续保持生产关闭

**R13.1：FileSystem Concurrency Closure** — 修复并发边界。
- FileLockProvider retired-entry 竞态
- 真正 reverse tail
- retention 自然日语义
- Janitor 移出 Save 热路径
- JobId 到文件路径索引
- FileSystem 单实例与多进程支持边界

**R13.2：Package Read Plan** — 消除重复查询。
- merged constraint 重复查询
- Provider 内按 Level/Layer 复用快照
- current task 与其他读取并行
- 查询计划记录 Store call count
- Package cold path p95 与 allocation gate

**R13.3：Store Capability Model** — 替代 namespace 字符串检测。
- IStoreRuntimeCapabilities / StorageExecutionProfile
- batch size / max concurrency / parallel read safety / consistency / transaction support

**R13.4：Runtime Observability Pipeline** — 统一 Trace 与 Event retention。
- BestEffort Sink 有界 Channel / File/Postgres 批量写入 / Required audit 同步
- queue/error/drop metrics

**R13-F：Cache Canary Freeze** — 仅单实例 + InMemory version store + 指定 workspace + 配置开关下启用 Package Template Cache。
验收：Cold 与 Hit 输出完全相同 / 写入期间不得缓存旧结果 / 调用方修改不得污染后续结果 / factory fault 不得 poisoned key / shutdown 不得留下无限任务 / Cache p95 明确优于 Cold / allocation 明确下降。

### 后续功能路线（R14-R17）

- **R14 — Decision Evidence V2**：建立可靠的决策证据和质量审计。
- **R15 — Incremental Context Package**：Previous Template + Context Delta → Selective Reload → Incremental Candidate Update → Incremental Repack。最接近外部 KV Cache 的 ContextOS 能力。
- **R16 — Context Evolution Agent V1**：仅开放 Observe / Diagnose / Form Hypothesis / Run Benchmark / Generate Proposal。不允许自动修改正式 Policy。
- **R17 — Guarded Optimization**：Offline Experiment → Shadow → Scoped Canary → Automatic Rollback。

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
- `docs/` 下的 `*_Freeze*.md`、`*_Plan*.md`、`*_Audit*.md`、`*_Report*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均为**历史快照**，顶部已标注。仅供回溯，不作为设计依据。
- 如需根据陈旧报告做设计，应先在本文件中确认对应任务是否已完成或已被取代。
