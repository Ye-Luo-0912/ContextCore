# ContextCore 项目路线图

> 最近更新：R12-1 低风险净减（删除无消费者代码 + 统一 Trace 失败指标）、R17/R18 系列收尾（2026-07-16）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 freeze / report / audit / plan 类文档均为历史快照，仅供回溯，不作为设计依据。历史完成记录已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 当前阶段

**架构收口与不可达代码清除期** — P5（P5-0 ~ P5-6）、P0 并发锁修复、P1-5 分发重构、DTO-R1~R5 报告 DTO 治理、P1 ControlRoom 历史报告矩阵删除、P2 Evaluation/Core 不可达切片删除、P4 FoundationStatusService 收口、P3-deferred eval-only DTO 迁移回 Evaluation 均已完成。R12 Context State 缓存边界返工（scope 索引/single-flight/commit point 安全/并发测试）与 P1 残留删除均已完成。R17/R18 系列完成 Package CandidateSegment 精确 attribution、FileSystem Trace append-only 分片、Evaluation WriteJsonAsync 泛型化、Decision Evidence 纠偏、Runtime 删除伪依赖、UnsupportedStoreExceptionFactory 工厂抽取。R12-1 低风险净减完成无消费者代码清理与 Trace 统一失败指标。剩余 Domain/Api/Ports 全量重排与 Service DI 大改暂缓。

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

## 当前验收指标（2026-07-16）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 生产代码总行数 | ~98,222 | < 220k |
| Abstractions 代码行数 | ~8,525 | 跨层契约 |
| ControlRoom 代码行数 | ~13,559 | < 20k |
| Core 代码行数 | ~29,101 | - |
| EvalCommand*.cs 单文件行数 | 2,820 | P1-5 已完成 |
| FoundationStatusService.cs 行数 | 606 | P4 已完成 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | 809+43+6 通过 / 0 失败 | 0 失败 |

---

## 历史完成记录

历史完成记录（R7~R12 系列、P0~P5 系列、DTO-R1~R4 等）已迁入 [docs/archive/roadmap-history.md](docs/archive/roadmap-history.md)。

---

## 下一阶段任务

### R12 路线图剩余项

**R12-3：Package Contribution Pipeline** — Section Collector、Candidate Segment、Section Packer、Exact candidate attribution、Package assembler、Builder 保留 facade。目标：BasicContextPackageBuilder ~1,250 行 → 300–500 行。R17-A+B 已完成 CandidateSegment 精确 attribution，剩余 Section Collector + Package assembler 进一步拆分。

**R12-4：Retrieval Batch & Parallel Read** — Mandatory batch lookup、Vector hit batch hydration、Working/Stable 并发查询、Keyword/Memory/Vector 并行 channel、Deterministic merge、provider capability 控制并行度。R17-C 已完成 Retrieval 消除 N+1，剩余 provider capability 控制并行度。

**R12-5：Append Log Storage** — Decision trace 日期分片、append、QueryRecent 尾部读取、retention/compaction。R17-E 已完成 Decision trace 日期分片 + append，剩余 QueryRecent 尾部读取 + retention。

**R12-6：Evaluation I/O Consolidation** — 公共 artifact writer、公共 gate execution shell、Runner 保留业务逻辑、EvalCommand 目标降至 4,000–5,000 行。R17-F 已完成 WriteJsonAsync 泛型化。

**R12-F：Lean Runtime Freeze** — 验收：Build = 0/0、Tests = 0 failed、PackageOutputChanged = false、SelectedSetChanged = false、RetrievalOrderingChanged = false、PackingPolicyChanged = false、生产代码净减 >= 8,000 行、Evaluation 净减 >= 2,000 行、Retrieval p95 有明确下降、Package allocated bytes 不上升、File trace 写入不再随历史总量线性恶化。

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
