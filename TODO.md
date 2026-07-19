# ContextCore 项目路线图

> 最近更新：R14 Decision Evidence V2 + Package Quality 完成（2026-07-20）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均已标注"历史快照"声明，仅供回溯，不作为 current-head 决策依据。历史完成记录已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 当前阶段

**R14 Decision Evidence V2 + Package Quality 已完成** — R14-1、R14-2 全部完成并提交（HEAD `1d0c2a6`）。

- R14-1 CandidateDecisionReasonCode 枚举 + Decision Evidence V2 DTO + V17.0 自由文本映射器（commit `38efc0d`）
- R14-2 Package Quality 8 指标 + Projector 集成 + 27 单元测试 + PublicApi baseline +29 entries（commit `1d0c2a6`）
- 修复 P1-7 遗留 OpenAPI snapshot 漂移：新增 `OpenApi_RegenerateSnapshot` 辅助测试方法
- 全量测试 1134 + 1 skip 通过 / 0 失败（ContextCore.Tests）；61 + 1 skip 通过 / 0 失败（ContextCore.Service.Tests）
- PublicApi baseline 7456 行（+105 vs R13.0-C 基线 7351 行）
- 保持非激活投影契约：所有 Risk 标志位恒为 false，不触发运行时变更

下一阶段为 **R15 Incremental Context Package**（Previous Template + Context Delta → Selective Reload → Incremental Candidate Update → Incremental Repack）。最接近外部 KV Cache 的 ContextOS 能力。

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

### R15 — Incremental Context Package

最接近外部 KV Cache 的 ContextOS 能力，分四步：

1. **Previous Template** — 复用上次 Package 构建结果作为基线模板
2. **Context Delta** — 计算自上次构建以来的输入变化（新增/删除/修改的 context items、constraints、memory）
3. **Selective Reload** — 仅重新读取发生变化的输入源，未变化的复用快照
4. **Incremental Candidate Update** — 基于 Delta 增量更新候选集，避免全量重新评分
5. **Incremental Repack** — 增量重新打包，保留未受影响 section 的已生成内容

### 后续功能路线（R16-R17）

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
- `docs/` 下的所有 `*_Freeze*.md`、`*_Report*.md`、`*_Audit*.md`、`*_Plan*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均为**历史快照**，顶部已统一标注"历史快照（Historical Snapshot）"声明块。仅供回溯，不作为 current-head 决策依据。
- 如需根据陈旧报告做设计，应先在本文件中确认对应任务是否已完成或已被取代。
