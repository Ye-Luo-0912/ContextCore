# ContextCore 项目路线图

> 最近更新：R13.0-C 状态与基线收口（2026-07-20）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均已标注"历史快照"声明，仅供回溯，不作为 current-head 决策依据。历史完成记录已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 当前阶段

**R13.0-C 状态与基线收口已完成** — R12.4A、R13.0~R13-F、P0-1~P0-8、P1-1~P1-8 全部完成并提交（HEAD `35784f9`）。

- R13.0 Cache Correctness Gate：10 项行为正确性修复完成
- R13.1 FileSystem Correctness Boundary：6 子任务全部完成（锁 retired-entry 竞态、真正反向尾读、retention 自然日、Janitor 移出 Save 热路径、JobId→Path 索引、单实例/多进程边界）
- R13.3 Store Capability Model：StoreRuntimeCapabilities + 能力驱动 fanout 完成
- R13.2 Package Read Plan：merged constraint 去重 + current_task 并行 + ReadPlan + Provider 内快照复用 + cold path gate 完成
- R13.4 Runtime Observability Pipeline：BestEffort BoundedChannel + OTel metrics 完成
- R13-F Cache Canary Freeze：配置开关 + workspace allowlist + 单实例检查完成
- P0-1~P0-8：8 项正确性修复完成（ingest pipeline、Postgres SQL bug、Transactional UoW、worker lease、baseline migration、Postgres Unsupported Store 行为契约、TraceBackedDecisionEvidenceProvider、审计事件绕过通道）
- P1-1~P1-8：8 项数据完整性增强完成（FileSystem 读改写跨进程锁、ingest 死代码、Truncated 信号、批量邻居、图写入 outbox、流式图诊断、路径衰减传播、备份清单/SHA-256/PITR/演练）
- 全量测试 1074 通过 + 1 skip / 0 失败（ContextCore.Tests）；PublicApi baseline 7351 行
- 15 份历史 freeze/report/plan 文档统一添加"历史快照"声明

下一阶段为 **R14 Decision Evidence V2**。

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

## 当前验收指标（2026-07-20）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 当前 HEAD | `35784f9` | - |
| PublicApi baseline 行数 | 7351 | 单一事实源 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | 1074+1skip 通过 / 0 失败 | 0 失败 |
| A3 语义不变性 | PassRate 100%, Recall@10 100% | 与冻结基线一致 |
| Retrieval golden ranking | 30 样本全通过, Recall@10 100% | 与冻结基线一致 |
| GRAPH-09 图不变性 | 12 测试全通过 | 0 失败 |
| FileSystem Package Build (Cold, ItemCount=50) | ~19ms / 1538KB | ≤ 当前值 70% |
| Package Build p95 (CacheHit, ItemCount=50) | ~7.5μs / 12.38KB | 优于 Cold |
| Constraint logical calls | 1 个 snapshot call | 已达成 |
| PackageReadPlan.TotalStoreCalls | 可观测 | 已达成 |
| BoundedChannelContextEventSink metrics | queue/error/drop | 已达成 |
| Cache Canary kill switch | 默认关闭 + workspace allowlist | 已达成 |
| 备份清单 / 验证 / 演练 | BackupManifest + verify + drill | 已达成 |

---

## 历史完成记录

历史完成记录（R7~R12 系列、P0~P5 系列、DTO-R1~R4、R13.0~R13-F、P0-1~P0-8、P1-1~P1-8 等）已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 下一阶段任务

### R13.0-C：状态与基线收口 ✅（2026-07-20 完成）

- 更新 TODO.md：R13.0~R13-F 与 P0/P1 系列均已完成，当前阶段改为 R14
- 清理已失效的 Cache known-gap 注释（已在代码中搜索，无残留）
- 标记历史 Attention/Router/Vector freeze 文档不得用于 current-head 决策：15 份缺失标记的文档已统一添加"历史快照"声明
  - Vector: vector-preview-shadow-freeze / vector-hybrid-retrieval-freeze / vector-embedding-provider-comparison-freeze / vector-postgres-provider-freeze
  - Router: router-intent-shadow-freeze
  - JobQueue: job-queue-postgres-freeze
  - Relation: relation-governance-postgres-freeze
  - Report: extended-eval-triage-report / planning-shadow-quality-report / attention-order-quality-report / attention-profile-selection-report
  - Plan: retrieval-plan-shadow-execution / retrieval-plan-proposal / planning-optin-fallback-analysis / planning-context-snapshot
- 生成新的 architecture baseline：PublicApi 7351 行

### R13.1：FileSystem Correctness Boundary ✅（已完成）

- ✅ #1 FileLockProvider retired-entry 竞态修复（commit `46fef1c`）：用 `lock(LocalLocks)` 全局锁包裹 `GetOrAdd + RefCount++`，消除双 entry 窗口
- ✅ #2 真正 reverse tail（commit `2094c4e`）：反向 I/O 块读取，仅读取尾部所需字节，newest-first
- ✅ #3 retention 自然日语义（commit `dd898bc`）：cutoff 对齐今日 UTC 午夜再减 retentionDays，分片按 yyyyMMdd 边界比较
- ✅ #4 Janitor 移出 Save 热路径（commit `990c72a`）：MaybePurge 命中槽位后 fire-and-forget 到线程池
- ✅ #5 JobId 到文件路径索引（commit `7079cd6`）：ConcurrentDictionary<JobId, FilePath>，未命中回退扫描
- ✅ #6 FileSystem 单实例与多进程支持边界（commit `0021ee5`）：advisory sentinel lock，独占失败仅标记多进程，不阻断操作

### R13.3：Store Capability Model ✅（已完成）

- ✅ #1 StoreRuntimeCapabilities（commit `39988c2`）：替代 namespace 字符串检测
- ✅ #2 能力驱动 fanout（commit `8762f82`）：基于 SupportsParallelReads / SupportsTransactions / RecommendedReadFanout 调整并发度

### R13.2：Package Read Plan ✅（已完成）

- ✅ #1+#3+#4 merged constraint 去重 + current_task 并行 + ReadPlan 跟踪（commit `d45ce7a`）
- ✅ #2 Provider 内按 Level/Layer 复用快照（commit `e4f854b`）：FileConstraintStore 文件内容快照缓存
- ✅ #5 Package cold path p95 与 allocation gate（commit `0a1523a`）

### R13.4：Runtime Observability Pipeline ✅（已完成）

- ✅ #1 BestEffort Sink 有界 Channel / File/Postgres 批量写入 / Required audit 同步（commit `7d81313`）
- ✅ #2 queue/error/drop metrics via OTel Counter（commit `0b2bf15`）

### R13-F：Cache Canary Freeze ✅（已完成）

- ✅ 配置开关 + 工作空间 allowlist + 单实例检查下启用 Package Template Cache（commit `7b1f721`）
- 默认关闭；启用前置条件：单 Service 实例 + InMemory version store + 显式 AllowedWorkspaces + 指定 workspace + 明确 kill switch

### 后续功能路线（R14-R17）

- **R14 — Decision Evidence V2**：见下方"R14 路线规划"。
- **R15 — Incremental Context Package**：Previous Template + Context Delta → Selective Reload → Incremental Candidate Update → Incremental Repack。最接近外部 KV Cache 的 ContextOS 能力。
- **R16 — Context Evolution Agent V1**：仅开放 Observe / Diagnose / Form Hypothesis / Run Benchmark / Generate Proposal。不允许自动修改正式 Policy。
- **R17 — Guarded Optimization**：Offline Experiment → Shadow → Scoped Canary → Automatic Rollback。

### R14 路线规划

**R14-1：CandidateDecisionReasonCode 枚举** — 替代自由文本 `ContextDecisionCandidate.Reason`
- 新增 `CandidateDecisionReasonCode` 枚举：SelectedMandatory / SelectedHighestUtility / SelectedRelationReserve / LifecycleBlocked / DeprecatedBlocked / RequiredTagMismatch / DuplicateSuppressed / SectionQuotaExceeded / TokenBudgetExceeded / ScoreBelowThreshold / SupersededByCurrentVersion / EvidenceMissing
- 每个候选保存：Candidate identity / Input fingerprint / Policy version / Channel sources / Score breakdown / Matched anchors/tokens / Relation paths / Lifecycle state / Rank before/after / Decision reason code / Token budget before/after / Alternatives considered / Evidence refs

**R14-2：Package Quality** — 第一版保持确定性
- AnchorCoverage / HardConstraintSatisfaction / RequiredItemCoverage / Redundancy / ProvenanceCompleteness / LifecycleRisk / TokenEfficiency / SectionBalance
- 作为后续 Agent / Router / Reranker / 学习闭环可依赖的数据基础

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
