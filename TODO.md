# ContextCore 项目路线图

> 最近更新：R14 收口 + 新增高可用路线 R14-PG（2026-07-20）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均已标注"历史快照"声明，仅供回溯，不作为 current-head 决策依据。历史完成记录已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 当前阶段

**R14 Decision Evidence V2 + Package Quality 已完成** — R14-1、R14-2、R14-3 全部完成并提交（HEAD `b7bc070`）。

- R14-1 CandidateDecisionReasonCode 枚举 + Decision Evidence V2 DTO + V17.0 自由文本映射器（commit `38efc0d`）
- R14-2 Package Quality 8 指标 + Projector 集成 + 27 单元测试 + PublicApi baseline +29 entries（commit `1d0c2a6`）
- R14-3 收口：TODO.md 切换至 R15，R14/R13/P0/P1 历史迁入 archive（commit `b7bc070`）
- 修复 P1-7 遗留 OpenAPI snapshot 漂移：新增 `OpenApi_RegenerateSnapshot` 辅助测试方法
- 全量测试 1134 + 1 skip 通过 / 0 失败（ContextCore.Tests）；61 + 1 skip 通过 / 0 失败（ContextCore.Service.Tests）
- PublicApi baseline 7456 行（+105 vs R13.0-C 基线 7351 行）
- 保持非激活投影契约：所有 Risk 标志位恒为 false，不触发运行时变更

下一阶段为 **R14-PG：Postgres Runtime Parity & HA Gate** — 完成已有 PostgreSQL provider 的纵向闭环，建立明确的高可用边界。R14-PG 之后才进入 R15 Incremental Context Package。

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

## 当前验收指标（2026-07-20）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 当前 HEAD | `1d0c2a6` | - |
| PublicApi baseline 行数 | 7456 | 单一事实源 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | ContextCore.Tests 1134+1skip / 0 失败；Service.Tests 61+1skip / 0 失败 | 0 失败 |
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
| Decision Evidence V2 | CandidateDecisionReasonCode 枚举 + V2 字段填充 | 已达成（R14-1） |
| Package Quality 报告 | 8 指标 + OverallScore 加权 | 已达成（R14-2） |
| OpenAPI snapshot 辅助再生 | OpenApi_RegenerateSnapshot `[Ignore]` 方法 | 已达成 |

---

## 历史完成记录

历史完成记录（R7~R12 系列、P0~P5 系列、DTO-R1~R4、R13.0~R13-F、P0-1~P0-8、P1-1~P1-8、R14-1、R14-2 等）已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 下一阶段任务

### R14-PG — Postgres Runtime Parity & HA Gate

完成已有 PostgreSQL provider 的纵向闭环，不是新做一个 provider。当前 PostgreSQL 已覆盖核心 Data Plane（context / memory / working memory / constraints / relations / global context / vector / retrieval & package trace / job queue），但 Service 层仍把以下接口覆盖为 Unsupported：

- decision trace
- short-term memory
- short-term promotion
- learning feedback
- learning review
- context learning
- 多种 governance / review store

**核心任务**：

1. **PostgresDecisionTraceStore** — 接入真实 Postgres 后端，不再 Unsupported
2. **short-term memory / promotion stores** — Postgres 实现，绑定 `IShortTermMemoryStore` / `IShortTermPromotionCandidateStore` / `IShortTermPromotionRecordStore`
3. **learning feedback / review** — 接口正式绑定 Postgres 实现，去除 Unsupported
4. **context learning** — Postgres 实现
5. **governance / review stores** — 接入 Postgres 实现（relation review / vector lifecycle review / promotion candidate review / candidate constraint review 等）
6. **分布式 IContextStateVersionStore** — Postgres 后端，支持跨实例 cache invalidation
7. **migration version 与 rollback** — 版本化迁移脚本，支持回滚到上一版本
8. **多实例 cache invalidation** — 跨 Service 实例的失效信号传播
9. **HA 测试** — DB failover、连接池耗尽、慢查询、事务重试测试
10. **backup/restore runbook** — 与 P1-2 备份清单/SHA-256/PITR/演练整合，明确恢复点目标与恢复时间目标

**验收**：

- Service 层无 `Unsupported*Store` 残留（除明确标记为 Local-only 的 store 之外）
- 多实例并行写入测试通过
- DB 故障注入测试通过（failover / pool exhaustion / slow query / tx retry）
- backup/restore runbook 文档化并至少执行一次演练

### R15 — Incremental Context Package

比 Embedding Cache 更接近真正的 KV Cache。最重要的验收不是速度，而是**幂等性**：

```
IncrementalBuild(snapshot) == FullBuild(snapshot)
```

应使用随机状态序列进行 differential testing，而不是只写几个固定测试。

**步骤**：

1. **Previous Template** — 复用上次 Package 构建结果作为不可变基线模板
2. **Store version delta** — 计算自上次构建以来的输入变化（新增/删除/修改的 context items、constraints、memory）
3. **Determine affected sections** — 基于 Delta 推导受影响 section 范围
4. **Selective reload** — 仅重新读取发生变化的输入源，未变化的复用快照
5. **Incremental candidate update** — 基于 Delta 增量更新候选集，避免全量重新评分
6. **Global repack** — 增量重新打包，保留未受影响 section 的已生成内容

**验收**：

- `IncrementalBuild(snapshot) == FullBuild(snapshot)` 在随机 differential testing 下成立
- 性能提升作为副产品，不是首要目标

### R16 — Context Evolution Agent V1

Agent 只负责离线控制面，不触碰正式 Policy 生产路径。

**允许的操作**：

- Observe（采集运行时指标与决策证据）
- Cluster failures（聚类失败模式）
- Diagnose（根因分析）
- Form hypothesis（形成假设）
- Generate experiment（设计实验）
- Run benchmark/eval（执行 benchmark 或 eval）
- Compare baseline（与基线对比）
- Generate proposal（生成版本化 OptimizationProposal）

**明确禁止**：

- 自动改正式 Policy
- 自动提交生产配置
- 自动启用模型
- 绕过 shadow / canary

**Agent 输出格式**：版本化 `OptimizationProposal`，包含证据、预期收益、风险、实验结果和回滚条件。

### R17 — Guarded Optimization

引入完整的优化闭环：

1. **Offline experiment** — 离线实验
2. **Shadow** — 影子模式运行
3. **Scoped canary** — 范围受控的 canary
4. **Automatic rollback** — 自动回滚（命中风险条件时）
5. **Manual / default promotion** — 手动或默认晋升

**第一项端到端学习闭环**：建议先用 `PromotionJudge` 验证训练和部署基础设施，因为作用域最小、风险容易隔离。

**第一项真正作用于核心运行时的 learned component**：建议从以下二选一：

- Cost-aware Retrieval Router（成本感知检索路由）
- Candidate Utility Reranker（候选效用重排序器）

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
