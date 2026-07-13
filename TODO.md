# ContextCore 项目路线图

> 最近更新：R7-0/R7-1/R7-2 三阶段 Shadow 证据清除与机械不可达删除（2026-07-14）

> 本文件是 ContextCore 的**唯一当前路线图**。docs/ 下的 freeze / report / audit / plan 类文档均为历史快照，仅供回溯，不作为设计依据。

---

## 当前阶段

**架构收口与不可达代码清除期** — P5（P5-0 ~ P5-6）、P0 并发锁修复、P1-5 分发重构、DTO-R1~R5 报告 DTO 治理、P1 ControlRoom 历史报告矩阵删除、P2 Evaluation/Core 不可达切片删除、P4 FoundationStatusService 收口（孤立方法删除 + 别名链删除 + 仅测试调用方法删除 + 跨项目迁移）、P3-deferred eval-only DTO 迁移回 Evaluation 均已完成。剩余 Domain/Api/Ports 全量重排与 Service DI 大改暂缓。

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

## 当前验收指标（2026-07-14）

| 指标 | 当前值 | 目标 |
|------|--------|------|
| 生产代码总行数 | ~118,273 | < 220k |
| Evaluation 代码行数 | ~23,226 | < 70k |
| Abstractions 代码行数 | ~14,731 | 跨层契约 |
| ControlRoom 代码行数 | ~16,683 | < 20k |
| Core 代码行数 | ~32,629 | - |
| EvalCommand.cs 单文件行数 | 7,987 | P1-5 已完成 |
| FoundationStatusService.cs 行数 | 606 | P4 已完成 |
| 构建 | 0 警告 / 0 错误 | 0 / 0 |
| 测试 | 926 通过 / 0 失败 | 0 失败 |

---

## 已完成工作

### R7-2：机械不可达删除（commit `5886c70`）

删除 9 个无生产消费者源文件 + 1 个仅测试引用的整文件测试 + 清理 2 个测试文件，12 文件变更，-1,471 行。

**已删除源文件（9 个）**：
- Core/Services/Vector：RuntimeRelationIntentDeriver、HybridCandidateUnionPolicy、VectorLifecycleSidecarResolver、QueryAnchorExtractor、AnchorCandidateProvider、CanonicalRuntimeAnchorResolver
- Core/Infrastructure：AsyncTraceStores、NoOpContextRetrievalAdapter、UnavailableContextCompressor

**保留**：
- Core/Services/Learning/RouterIntentDatasetProvider.cs（FileRouterIntentDatasetProvider 生产实现，含 SHA-256 hash、error count、version observability，受硬约束保护）

**测试清理**：
- 删除 ContextCoreAdapterNoOpBindingTests.cs（整文件仅测试已删 NoOp 适配器）
- AsyncTraceWriterTests.cs 移除 4 个 decorator 测试 + 2 个失去引用的 fake store（保留 AsyncTraceWriter<T> 自身测试）
- ContextCorePhase0Tests.cs 移除 UnavailableContextCompressor 测试方法

**引用验证**：检查 public API、反射（Type.GetType/Activator）、序列化（JsonDerivedType）、DI 注册，确认 9 个删除文件均无生产消费者。

验证：构建 0 警告 0 错误，测试 926 通过 0 失败。

### R7-1：DTO-R6 级联收口删除 Shadow 零值证据（commit `12660ac`）

删除 Runtime scorer 已撤出后残留的 Shadow 零值证据 DTO 及级联引用，13 文件变更，+1/-567 行。

**A. LifecycleAwareRankerShadow 系列（7 DTO + 2 属性）**：
- RetrievalDtos.cs：DebugRequest/DebugResponse
- EvalDtos.cs：Options/CandidateScore/Trace
- EvalGateReportDtos.cs：Sample/Report
- Client/Service：DebugLifecycleAwareRankerAsync 方法 + DI 注册 + 配置类
- 保留 LifecycleAwareFeatureSet（被 LearningOfflineBaselineRunner 生产使用）

**B. Router Shadow 系列（6 DTO 删除，3 保留）**：
- 删除：RouterShadowOptions/DisagreementTypes/Recommendations/TraceQualityReport/EvalReport/EvalSample
- 保留：RouterIntentShadowTopPrediction（被 RouterIntentClassifier 生产使用）、RouterIntentShadowTrace/TraceQuery（被 IRouterIntentShadowTraceStore 接口使用，R7-0 明确保留 trace store 读取）

**C. Artifact Shadow trace counters（3 字段）**：
- TraceLayoutDiagnostics：RankerShadowTraceCount/GraphShadowTraceCount/VectorShadowTraceCount
- 级联清理 ServiceOperationalRenderer 和 ContextCoreDataLayout

验证：构建 0 警告 0 错误，测试 933 通过 0 失败。

### R7-0：删除虚假 Graph gate 和 Attention 零值证据（commit `5e97f01`）

R7-0 阻塞项：移除基于错误报告继续决策的虚假 gate 和零值证据，30 文件变更，+20/-5054 行（净减 5,034 行）。

**Graph gate 链清理**：
- 删除 `GraphExpansionOptInComparisonRunner`（572 行虚假 Graph gate 实现）
- 删除 `RelationGraphDtos.cs` 中 11 个 Graph gate 类型（GraphExpansionShadowOptions/ApplyOptions/ApplyRiskChecks 等）
- 删除 Core 项目中 GraphExpansionSectionContribution 引用链（GraphExpansionCoordinator/PackageMetadataBuilder/BasicContextPackageBuilder 的 stub 方法和死字段）

**Attention 零值证据清理**：
- 删除 `ContextAttentionDtos.cs`（722 行）
- 删除 `AttentionProfileSelectionRunner`/`AttentionProfileSelectionDtos`/`GuardedAttentionEvalDtos`
- 删除 `RetrievalDtos.cs` 中 `ContextRetrievalTrace` 的 6 个零值字段（AttentionScores/AttentionShadowReport/AttentionProfileComparison/AttentionRerankComparison/RankerShadowTrace/GraphExpansionShadowTrace）+ 5 个死类型
- 删除 `EvalDtos.cs` 中 Attention 类（6 个）和 ContextEvalResult/Report/ModeSummary 中的 Attention 字段块
- 清理 PostgresRetrievalTraceStore、ControlRoomService、ContextEvalRunner、Service Program.cs、EvalCommand 中的零值赋值
- 清理 Client 中 RankerShadow/GraphExpansionShadow 客户端方法

**测试清理**：
- 删除 `ContextCoreAttentionProfileSelectionTests`（整个文件）
- 精简 `ContextCoreRelationExpansionShadowEvalTests`（424→107 行，保留 3 个有效测试）
- 删除 `ContextCoreEvalRunnerTests` 中 Attention 诊断测试
- 删除 `ContextCoreClientTests` 中 RankerShadow/GraphExpansionShadow 客户端测试

验证：构建 0 警告 0 错误，测试 934 通过 0 失败。

### 新任务3：撤出 Runtime 中 Shadow/Experiment 能力（commit `28f9d75` + `142dca5` + `f9e1115`）

撤出生产 Runtime 中的全部 Shadow/Freeze/Gate/Experiment 能力，5 个阶段共删除约 11,078 行：
- **阶段 1**：撤出 Runtime 热路径（-5,704 行）
  - HybridContextRetriever 移除 7 处 Shadow 调用，effectivePacked = packed
  - 删除 Attention 全链（ContextAttentionScorer/ScoringPolicy/ShadowReportBuilder/ExperimentRunner/GuardedRerankPolicy）
  - 删除 Ranker Shadow（LifecycleAwareRankerShadowScorer/TraceBuilder/DebugService）
  - 删除 Graph Shadow（GraphExpansionShadowTraceBuilder/ApplyPolicy）
- **阶段 2+3**：撤出 Service 和 ControlRoom（-4,210 行）
  - 删除 Router Shadow 服务（RouterIntentShadowService/ReportBuilder）
  - 删除 Ranker/Graph Shadow Export 服务
  - 删除 ControlRoom Shadow 读取和渲染
  - 删除 Storage 实现（InMemory/FileSystem RouterIntentShadowTraceStore）
- **阶段 4+5**：迁移 Evaluation 引用和清理测试（-1,164 行）
  - 迁移 RelationExpansionProfileShadowReportBuilder 到 Evaluation
  - 删除 ShadowFormalRetrievalAdapter
  - 清理测试残留
- 保留：Graph 稳定主干（关系查询、遍历、写入边界）
- 保留：Evaluation 中有效的 benchmark/gate（LearningReadinessFreezeRunner、ContextCoreFoundationFreezeRunner、EvalGateReportDtos）
- 总计 -11,078 行，0 警告 0 错误，949 测试通过

### 新任务2：Planning 功能退回空壳（commit `d31035f` + `4e16750`）

将 Planning 功能退回空壳，只保留基础检索管线：
- **阶段 1**：删除 12 个 Planning 源文件 + 2 个整文件测试 + 清理 11 个测试文件（-7,857 行）
  - 删除 PlanningIntentDetector/SnapshotService/ShadowDiffTriageReportBuilder
  - 删除 RetrievalPlanProposalService/Validator/ShadowRetrievalPlanExecutor/ComparisonReportBuilder
  - 删除 PlanningShadowEvalRunner、ControlRoom Planning screens
  - 删除 ContextPlanningDtos.cs、RetrievalPlanShadowDtos.cs
- **阶段 2**：修改 27 个文件删除 Planning 引用（-1,178 行）
  - HybridContextRetriever 移除 Planning 参数、ApplyPlanningAsync 方法
  - Runtime composition 移除 Planning 服务（保留 PlanningIntentDetector 空壳）
  - Service 删除 Planning endpoints 和 DI 注册
  - Client 删除 Planning 方法
  - ControlRoom 合并为静态占位入口
  - Evaluation 删除 planning-shadow 命令
- 保留：RetrievalPlanner、RetrievalPlan、RetrievalPlanExecutionPolicy（Data Plane）
- 保留：PlanningIntentDetector 最小化空壳（被 11 个文件引用，无法完全删除）
- 总计 -9,035 行，0 警告 0 错误，1027 测试通过

### 新任务1：低风险不可达代码删除（commit `65d3fae`）

删除 12 个不可达源文件（4 个孤儿 + 8 个仅测试引用）+ 2 个整文件测试 + 3 个部分测试清理：
- 孤儿文件：VectorFormalPreviewFreezeRunner、ControlledAppliedMergeReports、CapabilityRegistry、IGateEvaluator
- 仅测试引用：PlanningOptInFallbackAnalysisReportBuilder、PlanningShadowRecallLossReportBuilder、PlanningOptInConstraintSafetyReportBuilder、ScopedShadowRetrievalAdapter、PromotionEvalRunner、VectorQueryExpansionService、HybridUnionScoringRepairRunner、ScopedRuntimeExperimentReports
- 清理 13 个测试方法 + 8 个孤立测试辅助方法
- 总计 -4,171 行，0 警告 0 错误，1114 测试通过

### P1：ControlRoom 历史 Postgres 报告矩阵删除

删除 `ServiceAdminRuntimeSnapshot` 中 50 个历史 Postgres 报告字段及其构造/读取/渲染链：
- `ControlRoomService.cs` — `ServiceAdminRuntimeSnapshot` 50 个历史字段删除（保留 11 个实时字段）
- `ControlRoomService.ServiceAdmin.cs` — 50 个字段赋值 + 4 个孤立 Build 方法删除
- `ControlRoomService.Storage.cs` — `BuildPostgresRelationStoreDiagnostics` + 23 个 ReadPostgresReport 包装 + `ReadPostgresReport<T>` helper 删除
- `ServiceOperationalRenderer.cs` — `AppendPostgresRelationStoreStatus`（~480 行）+ `AppendPostgresVectorIndexStatus`（~100 行）删除
- 0 警告 0 错误

### P2：Evaluation/Core 不可达切片删除

删除 54 个无生产引用的源文件 + 10 个测试文件清理：
- 18 个完全无引用文件（~5,230 行）：9 个 `RelationGovernance*Runner`、6 个 ArchitectureCleanup/DtoSplitPlan/HybridRetrieval/FormalRetrievalIntegration Runner、1 个 `RuntimeCandidateTraceContractValidator`、1 个 `Core/BasicWorkingMemoryService`
- 36 个仅测试引用文件（~14,718 行）：11 个 `Runners/*`、7 个 `Vector/Evaluation/*`、17 个 `Learning/*`、1 个 `Core/PlanningShadowQualityReportBuilder`
- 6 个测试文件外科手术式清理 + 4 个整文件删除
- 0 警告 0 错误，1155 测试通过

### P3：DTO-R5 孤立类型收口

删除 4 个 `*Dtos.cs` 文件中 83 个完全孤立的 DTO 类型：
- `ServiceSuccessResponseDtos.cs` — 27 个孤立类型（-926 行）
- `VectorEvalSharedReportDtos.cs` — 30 个孤立类型（-1,321 行）
- `ContextLearningDtos.cs` — 18 个孤立类型（-390 行）
- `VectorGateReportDtos.cs` — 8 个孤立类型（-280 行）
- 保留 17 个被同文件存活类型引用的孤立类型
- 0 警告 0 错误

### P4-partial：FoundationStatusService 孤立方法删除 + 别名链删除

**孤立方法删除**：删除 8 个完全无调用方的静态 Markdown 构建器：
- `BuildSmokeMarkdown`、`BuildSecurityDiagnosticsMarkdown`、`BuildReportNavigationSmokeMarkdown`
- `BuildOpenApiContractMarkdown`、`BuildHostedServiceSmokeMarkdown`
- `BuildAuthDiagnosticsMarkdown`、`BuildAuthEnforcementSmokeMarkdown`、`BuildDeploymentProfileGateMarkdown`

**别名链删除**：删除 5 个重复的 statusKind 路由别名（release-candidate / reproducibility / runtime-change-gate / vector-formal-preview / postgres-freeze-status），这些别名返回与 `/foundation/status` 完全相同的数据（仅 StatusKind 标签字段不同）：
- `AdminEndpoints.cs` — 删除 5 条 MapGet 路由注册
- `FoundationStatusService.cs` — EndpointContracts 从 8 条减到 3 条、ClientMethodContracts 从 8 条减到 3 条、ClientAliasMethodContracts 清空
- `ContextCoreClient.cs` — 删除 10 个别名客户端方法（5 个 `GetXxxStatusAsync` + 5 个 `GetXxxAsync` 快捷别名）
- 硬编码计数断言修正（8→3）
- 3 个测试文件同步清理
- `FoundationStatusService.cs` 从 2,510 行减到 2,038 行（-472 行）
- 0 警告 0 错误，1154 测试通过

**仅测试调用方法删除**：删除 11 个仅被测试调用的公开方法 + 8 个孤立私有辅助方法：
- 删除 `BuildAuthDiagnostics`、`BuildAuthEnforcementSmokeReport`、`BuildDeploymentProfileGateReport`、`BuildReportNavigationSmokeReport`
- 删除 `BuildOpenApiDocument`、`BuildApiContractSnapshot`、`BuildClientContractSnapshot`、`GetFoundationEndpointContracts`、`BuildOpenApiContractReport`
- 删除 `BuildHostedServiceSmokeReport`、`BuildSmokeReport`
- 删除孤立私有辅助方法：`BuildOpenApiSchemas`、`ToStringPropertyMap`、`ToJsonArray`、`ToOperationId`、`BuildOpenApiContractRecommendation`、`BuildHostedSmokeRecommendation`、`NormalizeHostedBaseUrlForReport`、`BuildAuthRecommendation`
- `BuildContractReport` 和 `BuildServiceFoundationFreezeReport` 降级为 private（仅被 Async 版本内部调用）
- 删除 24 个引用已删除方法的测试方法 + 13 个孤立测试辅助方法
- `FoundationStatusService.cs` 从 2,038 行减到 1,245 行（-793 行）
- 0 警告 0 错误，1130 测试通过

**跨项目迁移**：将 5 个历史产物解析方法从 ContextCore.Core 迁移到 ContextCore.Evaluation：
- 新建 `src/ContextCore.Evaluation/Services/FoundationReportMarkdownRenderer.cs` — 迁移 `BuildContractMarkdown`、`BuildServiceFoundationFreezeMarkdown`、`AppendList`
- 新建 `src/ContextCore.Evaluation/Services/FoundationReportBuilder.cs` — 迁移 `BuildSecurityDiagnostics`、`BuildContractReportAsync`+`BuildContractReport`、`BuildServiceFoundationFreezeReportAsync`+`BuildServiceFoundationFreezeReport` 及相关私有辅助方法
- `FoundationStatusService.cs` 仅保留实时健康方法（GetStatusEnvelopeAsync、GetStatusAsync、报告导航等），从 1,245 行减到 606 行（-639 行）
- 修改 `EvalCommand.Service.cs` 和测试文件调用方
- 0 警告 0 错误，1130 测试通过

### 删除 tests-only Postgres runner（commit `9fa2b48`）

删除两个已从 CLI 退役的 Postgres eval runner 及其级联依赖：
- `PostgresJobQueueProviderEvalRunner.cs`（2740 行）+ `PostgresLearningFeedbackProviderEvalRunner.cs`（2605 行）
- 3 个伴生辅助文件（LearningFeedbackProviderRouter / Coordinators / DiagnosticsBuilder）
- 7 个 eval-only DTO（保留 2 个 FreezeGate DTO 被 FoundationStatusService 生产消费）
- ControlRoom 4 文件清理（snapshot 属性 / Build 方法 / wiring / renderer 参数）
- 29 个 runner-only 测试方法
- 总计 -7497 行，0 警告 0 错误，1294 测试通过

### ControlRoom 历史报告链折叠（commit `c825eda`）

引入 `OperationalReportSnapshot` 紧凑模型，将约 43 个 V4/V5 历史 sweep/freeze/repair/audit 报告折叠为统一列表：
- `ServiceVectorShadowQualitySummary` 属性从 ~400 减到 ~60 + OperationalReports 列表
- `LoadVectorShadowQualitySummary` 从 1465 行减到 ~400 行
- `TryLoad*` 方法从 59 个减到 ~12 个
- `RenderVectorIndex` 从 1173 行减到 ~400 行
- 保留约 13 个当前运维能力渲染分支（index diagnostics / coverage / sweep 核心 / residual risk / lifecycle coverage / provider / readiness / hybrid preview/freeze/audit / reindex / actions）
- 总计 -4057 行，0 警告 0 错误，1294 测试通过

### DTO 报告治理（DTO-R1 ~ DTO-R4）

| 阶段 | Commit | 内容 | 结果 |
|------|--------|------|------|
| DTO-R1 | `eaa7c40` | 删除 VectorGateReportDtos 中 100 个死类型（ControlledAppliedMergeRuntimePreview*/ScopedRuntimePreview*/FormalRetrievalPromotion* 三大家族 + 6 个孤立类型） | 3818 → 1140 行（-2678） |
| DTO-R2 | `306678a` | 消除 ControlRoom 报告 DTO 复制：删除 Contracts/EvalGateReportDtos.cs（63 类型），创建 9 个轻量 snapshot record + JsonDocument 解析 | -1133 行净减 |
| DTO-R3 | `5831342` | 收回 Evaluation 边界：修正 46 个错误命名空间，迁移 63 个 EvalGate 类型 + IEvalHost/IEvalState 到 Evaluation，删除 Evaluation.Contracts 项目 | 82 文件变更 |
| DTO-R4 | `7c13f95` | 拆分 VectorEvalReportDtos.cs（5226 行）：15 个 eval-only 类型移入 Evaluation/Models/，79 个共享类型留 Abstractions 分两文件 | 原 5226 行文件删除 |

### P0：FileSystem 并发锁与缓存一致性修复（commit `d9c5fd8`）

1. **Mutex 泄漏修复** — `FileRelationStore` 命名 Mutex 在 async/await 线程切换下 `ReleaseMutex` 静默失败。改用进程级 `SemaphoreSlim` 字典。
2. **DeleteAsync 跨实例锁统一** — 统一走同一进程级文件锁。
3. **FileContextStore 跨文件一致性** — 读路径恢复 SemaphoreSlim 读锁。
4. **mtime 缓存竞态** — 读前取 mtime → 读文件 → 读后复核 mtime。
5. **缓存容量上限** — `MaxCacheEntries = 256`。
6. **并发测试** — 新增 `FileSystemStoreConcurrencyTests`（9 个测试）。

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

- **P1-1 命名空间迁移**（commit `2bf93bc`）— 39 个 Evaluation 文件命名空间迁移。完成。
- **P1-2 移除过时 eval 帮助**（commit `f706c8e`）— ControlRoom `Program.cs` 删除已移除的 eval 帮助文本。完成。
- **P1-3 删除无消费者模型**（commit `f706c8e`）— `ReportSnapshot.cs` 无消费者，已删除。完成。
- **P1-4 精简 ReportSummaryRegistry**（commit `a10e91e`）— 23 个 descriptor 删除 21 个，272 → 28 行。完成。
- **P1-5 EvalCommand 分发重构**（commit `da575ac`）— Registry 直接持有 handler，消除 470 行 if-chain，44 个孤立方法删除，10286 → 7987 行。完成。

### P2：治理文档清理（commit `40f127f`）

重写 `TODO.md` 为唯一当前路线图。9 个历史治理文档顶部标注历史快照。

### 早期阶段（P3 / P4）

| Commit | 描述 |
|--------|------|
| `56a66d0` | P4 后架构纠偏：建立 Storage.Shared/Evaluation.Contracts/Runtime，统一 composition |
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

### R7-1：DTO-R6 级联收口（已完成 commit `12660ac`）

删除 LifecycleAwareRankerShadow 系列（7 DTO + 2 属性）、Router Shadow 零值 DTO（6 个，保留 3 个被 trace store/生产使用的）、Artifact Shadow trace counters（3 字段）。+1/-567 行。保留 LifecycleAwareFeatureSet（生产使用）。

### R7-2：机械不可达删除（已完成 commit `5886c70`）

删除 9 个无生产消费者源文件 + 1 个仅测试引用的整文件测试 + 清理 2 个测试文件。-1,471 行。保留 RouterIntentDatasetProvider（FileRouterIntentDatasetProvider 生产实现，受硬约束保护）。

### DTO-R4 剩余部分（暂缓，高风险）

**Domain/Api/Ports 重新划分** — 将 Abstractions 的 50+ 文件按 Domain（ContextItem/Memory/Relation/Constraint）、Api（Service/Client request/response）、Ports（接口和跨层命令）重新组织。风险：涉及上百个消费者文件的命名空间变更，Abstractions 是最底层项目。需单独评估。

**进一步合并模式** — 将 Relation/LearningFeedback/JobQueue/Vector 重复定义的 diagnostics/parity/smoke/quality/gate/freeze 模型收敛为少量内部组合模型（OperationalReport<TDetails>、GateDecision、ProviderCheck、OperationScope、ProviderIdentity）。风险：大型设计任务，需逐类型验证。不要退化为 Dictionary<string,object> 或万能 nullable DTO。

### 延迟项

- **Service DI 收敛到 ContextRuntimeBuilder** — Service ASP.NET DI 仍由 CoreExtensions.AddContextCore 自行注册 80+ 服务。风险较高（生产路径），需单独评估。
- ~~**IEvalState 上帝接口拆分**~~ — 已完成：已拆分为 `IEvalStateCore` / `IEvalStateServiceMode`（DTO-R3 已迁移到 Evaluation.Hosting）。
- ~~**eval-only DTO 迁移**~~ — 已完成：4 批次共迁移 80 个 eval-only DTO 类型从 Abstractions 到 Evaluation/Models（批次1: 12 类型、批次2: 22 类型、批次3: 9 类型、批次4: 37 类型）。Abstractions 从 ~15,600 行减到 14,731 行。
- ~~**P4 FoundationStatusService 跨项目迁移**~~ — 已完成：别名链删除 + 11 个仅测试调用方法删除 + 8 个孤立私有方法删除 + 5 个历史产物解析方法迁移到 Evaluation。FoundationStatusService 从 2,510 行减到 606 行（-76%）。

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
