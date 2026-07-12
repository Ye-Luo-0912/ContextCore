# ContextCore 项目路线图

> 最近更新：P1-5 EvalCommand 分发重构 + P1-4 报告 DTO 瘦身 + P0 并发锁修复 + P2 治理文档清理（2026-07-13）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 freeze / report / audit / plan 类文档均为历史快照，仅供回溯，不作为设计依据。

---

## 当前阶段

**架构治理收口后维护期** — P5（P5-0 ~ P5-6）全量推进、P0 并发正确性修复、P1 边界收尾、P1-5 分发重构均已完成。剩余 P1-4 DTO 深度瘦身属高风险大型重构，暂缓。

---

## 硬边界

- ControlRoom 和 Service 不再编译期引用 Evaluation（P3.1 已完成）
- Evaluation 依赖只能是 Evaluation → Core/Storage/Abstractions/Client/Runtime/Evaluation.Contracts
- Abstractions 只承载 Contracts/DTOs/Enums/跨层协议，不含实现逻辑
- Client 只承载 Service client，不被 eval host 接口污染
- 构建必须 0 警告 0 错误
- 全量测试必须 0 失败
- 所有变更提交到 GitHub main 分支

---

## 当前验收指标（2026-07-13）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 生产代码总行数 | ~181,010 | < 220k |
| Evaluation 代码行数 | ~51,687 | < 70k |
| ControlRoom 代码行数 | ~23,509 | < 20k（接近） |
| EvalCommand.cs 单文件行数 | 7,987 | P1-5 已完成 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | 1265 通过 / 0 失败 | 0 失败 |

---

## 已完成工作

### P0：FileSystem 并发锁与缓存一致性修复（commit `d9c5fd8`）

1. **Mutex 泄漏修复** — `FileRelationStore` 命名 Mutex 在 async/await 线程切换下 `ReleaseMutex` 静默失败。改用进程级 `SemaphoreSlim` 字典（非线程亲和），`ProcessLockLease.Dispose` 正确 Release。
2. **DeleteAsync 跨实例锁统一** — `DeleteAsync` 原先未进入与 `BatchUpsertAsync` 相同的跨实例锁，可能丢失更新。现已统一走同一进程级文件锁。
3. **FileContextStore 跨文件一致性** — 原子替换只保证单文件完整，不保证 content + metadata 同一快照。读路径恢复 SemaphoreSlim 读锁。
4. **mtime 缓存竞态** — 读前取 mtime → 读文件 → 读后复核 mtime，防止读取期间文件被替换时缓存脏数据。
5. **缓存容量上限** — `MaxCacheEntries = 256`，超过时清空，防止按路径无限增长。
6. **并发测试** — 新增 `FileSystemStoreConcurrencyTests`（9 个测试：双实例并发 upsert/delete、取消、超时、无死锁、无 Mutex 泄漏）。

### P5：架构治理与代码精简

| 阶段 | 内容 | 结果 |
|------|------|------|
| P5-0 | 热路径修复：ONNX Session 并发泄漏、Embedding 排序 O(n²)、关系高权重截断、Trace 写阻塞 | 完成 |
| P5-1 | Evaluation 代码删除：119,983 → 51,649 行（-57%），命令条目 418 → 40（-90%） | 完成 |
| P5-2 | Evaluation 独立 CLI 工具，移出 ControlRoom 默认交付 | 完成 |
| P5-3 | ControlRoom 报告模型统一（ReportDescriptor/Loader/Snapshot） | 完成 |
| P5-4 | 拆分 DirectControlRoomState / RemoteControlRoomState，移除 Remote 假运行时 | 完成 |
| P5-5 | FileSystem Store 优化（后被 P0 重新校准缓存一致性） | 完成 |
| P5-6 | 清理 RuntimeCapabilityProfile / InMemory 引用 / AppHost / NullEvolutionAgent | 完成 |

### P1：ControlRoom / Evaluation 边界收尾

- **P1-1 命名空间迁移**（commit `2bf93bc`）— 39 个 Evaluation 文件从 `ContextCore.ControlRoom.Services` 迁移到 `ContextCore.Evaluation.Runners` / `ContextCore.Evaluation`，7 个 EvalCommand partial 与 8 个测试同步更新。完成。
- **P1-2 移除过时 eval 帮助**（commit `f706c8e`）— ControlRoom `Program.cs` 删除已移除的 `context room eval ...` 帮助文本。完成。
- **P1-3 删除无消费者模型**（commit `f706c8e`）— `ReportSnapshot.cs` 无消费者，已删除。完成。

### P1-5：EvalCommand 分发重构（commit `da575ac`）

`EvalCommand.cs` 从 10,338 行降至 7,987 行（-2,351 行）。`BuildSubcommandRegistry` 现在直接为全部 40 个子命令注册 handler，`ExecuteAsync` 用 `registry.TryGetEntry` 替代 470 行 if-chain。44 个孤立 Execute 方法（P5-1 遗留死代码）已删除。

### P1-4：精简 ControlRoom 报告 DTO（部分完成）

`ReportSummaryRegistry` 23 个 descriptor 中 21 个无外部消费者，已删除（272 → 28 行，仅保留 2 个 OPT descriptor）。

`Contracts/EvalGateReportDtos.cs` 中的 39 个报告 DTO 类型仍被 `ControlRoomService.Storage.cs` 用于 JSON 反序列化和 dashboard 字段映射，无法直接删除。进一步瘦身需将强类型反序列化替换为轻量 `JsonDocument` 解析，属高风险大型重构，暂缓。

### P2：治理文档清理（commit `40f127f`）

重写 `TODO.md` 为唯一当前路线图。9 个历史治理文档（Freeze/Plan/Audit/Report/Gap_Map/新阶段）顶部标注历史快照。

### 早期阶段（P3 / P4）

| Commit | 描述 |
|--------|------|
| `56a66d0` | P4 后架构纠偏：建立 Storage.Shared/Evaluation.Contracts/Runtime，统一 composition，迁移 eval host 接口 |
| `01ef145` | 治理修复：审计 runner、graph 写入边界、package dedup、trace 可观测性 |
| `1a49eb9` | P4: 删除 eval 历史代码、精简报告体系、提取共享存储工具、拆分 PackageBuilder |
| `ba17bb8` | P3.1: 断开 ControlRoom/Service 对 Evaluation 的编译期依赖，迁移 Eval CLI |
| `ed8a710` | P3-05: 迁移 eval-only DTO 到 Evaluation 项目 |
| `ce21f1f` | P3-04: 提取 BasicContextPackageBuilder 硬编码值到 Profile 类 |
| `bca10be` | P3-03: 拆分 ControlRoomService 为 partial 类 |
| `c7aa989` | P3-02/03: RuntimeBuilder 共享程序集层 + eval 命令 dispatch 清理 |
| `65b61cd` | P3-01: 物理提取 ContextCore.Evaluation 项目 |

---

## 下一阶段任务

### P1-4 深度瘦身（暂缓，高风险）

将 `ControlRoomService.Storage.cs` 中强类型报告 DTO 反序列化替换为轻量 `JsonDocument` 解析，然后删除 `Contracts/EvalGateReportDtos.cs`（39 个类型、1608 行）。风险：需逐字段验证 dashboard 显示不受影响。

### 延迟项

- **Service DI 收敛到 ContextRuntimeBuilder** — Service ASP.NET DI 仍由 CoreExtensions.AddContextCore 自行注册 80+ 服务，是三套 composition 中唯一未收敛到 ContextCore.Runtime 的宿主。风险较高（生产路径），需单独评估。
- **IEvalState 上帝接口拆分** — 当前暴露 32 个成员，Evaluation 实际只用 ~8-10 个。可拆为 `IEvalStateCore` / `IEvalStateServiceMode`。
- **eval-only DTO 迁移** — P5-1 遗留，部分 eval-only DTO 仍在非 Evaluation 项目中。

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
- `docs/` 下的 `*_Freeze*.md`、`*_Plan*.md`、`*_Audit*.md`、`*_Report*.md`、`*_Gap_Map*.md`、`新阶段*` 类文档均为**历史快照**，顶部已标注。仅供回溯，不作为设计依据。
- 如需根据陈旧报告做设计，应先在本文件中确认对应任务是否已完成或已被取代。
