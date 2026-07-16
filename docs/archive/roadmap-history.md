# ContextCore 路线图历史记录

> 本文件归档 ContextCore 已完成的历史工作记录，从 TODO.md 迁入（2026-07-16）。仅供回溯，不作为设计依据。

---

## 已完成工作

### R12 系列：Context State 缓存边界返工 + P1 残留删除

基于缓存边界评审，在缓存接入任何生产读路径之前完成全部返工；同步清理 ControlRoom 失效菜单、router-shadow 文档残留与 EvalGateReportDtos 孤立 DTO。

**R12-P0：缓存边界返工（5 子任务）**：

- **P0-1 Abstractions 契约重设计** — 新增 `StateCacheKey`（readonly record struct，强制非空）与 `DependencyScopeSet`（至少一个 scope，杜绝无 scope 写入无法失效的脏条目）。`IContextStateCache` 接口删除无 scope `SetAsync`，统一为 `SetAsync<T>(key, value, scopes, ct)`，写入必须声明依赖 scope 集合。
- **P0-2 InMemoryContextStateCache 重写** — 新增 `Dictionary<ScopeIndexKey, HashSet<string>>` scope 反向索引，失效从 O(N) 全量扫描降为 O(M)（M 为该 scope 下条目数）。单条目可绑定多 scope（解决 Package Builder 跨 Context/Memory/Constraint/Global/Relation 组合依赖）。新增 4 项指标：Hits/Misses/Evictions/VersionMismatches。`ScopeMatchesInvalidation` 实现 EntityId 匹配规则（null scope 匹配任意；null 失效匹配任意；否则相等匹配）。
- **P0-3 ContextStateCacheAccessor 改造** — 依赖 `IContextStateCache` 接口（不再依赖具体类 `InMemoryContextStateCache`，可替换为分布式实现）。删除无 scope `GetOrAddAsync` 重载。新增 per-key `SemaphoreSlim` single-flight：快速路径 → per-key 信号量 → double-check → factory → SetAsync，避免热点 miss 并发击穿。
- **P0-4 commit point 安全** — `InvalidatingStoreDecorator` 中 74 处机械替换：`_inner.XxxAsync` 成功后的 `InvalidateAsync` 与 `BumpVersionAsync` 全部改用 `CancellationToken.None`（共 37 个 InvalidateAsync + 37 个 BumpVersionAsync）。提交后取消不再跳过失效与版本递增。
- **P0-5 指标 + 并发测试** — 新增 `ContextStateCacheTests`（18 个测试）：覆盖 scope 索引失效、多 scope 组合依赖、版本感知、single-flight、LRU 淘汰指标、EntityId 匹配规则、commit point 安全、高并发混合操作。

**R12-P1：残留删除（3 子任务）**：

- **P1-1 Dashboard 失效菜单** — `DashboardRenderer` service 模式菜单删除无解析映射的 `[32]PolicyFeedback`/`[33]LearningFeatures`/`[X]Planning`/`[F]Proposal`/`[34]RankerDebug`；`[C] Gaps` → `[30] Gaps`、`[E] Candidates` → `[31] CandConstraints` 修复与 direct 模式快捷键冲突。
- **P1-2 router-shadow 文档残留** — 4 个历史文档标题后添加弃用声明块（新阶段执行报告/controlroom-service-mode/filesystem-layout/learning-offline-baseline），标注 router-shadow 接口已从代码库移除。
- **P1-3 EvalGateReportDtos 孤立 DTO 删除** — `EvalGateReportDtos.cs` 从 1627 行降至 41 行（-1586 行）：保留 2 个 USED 类型（`RetrievalDatasetAlignmentAuditSummaryReport`、`ContextEvalCorpus`），删除 63 个孤立类型（61 个完全无引用 + 2 个仅 JSON 合约 deny-list 字符串引用，不涉及类型加载）。

验证：构建 0 警告 0 错误，测试 1882 通过 0 失败。

### R11 系列：Context State 失效边界 P4-P6 完成

3 个任务完成，合计 25 个 Store 接口全覆盖失效边界 + 扩展事件结构 + 引入 ContextStateCache。

**R11-P4：剩余 Store decorator（commit `49e5f29`，+973 行）**：
- 新增 19 个 Decorator（合计 25 个 Store 接口全覆盖）
- 覆盖 ContextCollectionStore/PackageBuildTraceStore/PackagePolicyStore/DecisionTraceStore
  /StableLifecycleReviewStore/CandidateConstraintReviewStore/ConstraintGapCandidateStore
  /PromotionRecordStore/PromotionCandidateStore/WorkingMemoryService/RelationReviewStore
  /VectorStore/VectorReindexReportStore/VectorLifecycleMetadataReviewStore
  /VectorLifecycleSidecarMetadataStore/VectorLifecycleMetadataReviewCandidateStore
  /LearningFeedbackStore/LearningFeedbackReviewStore/ShortTermPromotionCandidateStore
- 三个 Register 方法（FileSystem/InMemory/Postgres）全部改造
- IRelationReviewStore 保留 ScopedRelationGovernanceReviewStore 内层，Decorator 套最外层

**R11-P5：扩展 IContextEventSink 事件结构（commit `874ffc9`）**：
- ContextOperationEvent 新增 EntityType/EntityId/Operation nullable 字段
- ContextRuntimeService 各操作方法填充新字段
- LoggingContextEventSink scope 新增三个字段
- PostgresContextEventSink schema v6→v7（ALTER TABLE 新增三列）
- 保持向后兼容

**R11-P6：引入 ContextStateCache（commit `874ffc9`）**：
- 新增 IContextStateCache 接口（GetAsync/SetAsync/InvalidateAsync）
- InMemoryContextStateCache 实现（同时实现 IContextStateCache 和 IStateCacheInvalidator）
  - ConcurrentDictionary + LinkedList LRU，上限 10000 项
  - 版本检查：通过 IContextStateVersionStore 验证 version
  - 按 StoreKind/WorkspaceId/CollectionId 匹配失效
- ContextStateCacheAccessor：GetOrAddAsync 模式，读路径可选使用
- DI 注册：InMemoryContextStateCache 替换 NullStateCacheInvalidator
- 不自动应用到现有读路径，只提供基础设施

新增文件（2 个）：
- ContextStateCache.cs（275 行）
- ContextStateCacheAccessor.cs（86 行）

验证：构建 0 警告 0 错误，测试 792 通过 0 失败。

### R10 系列：RetrievalPolicyProfiles 词表改造、Context State 失效边界落地

2 个任务完成。

**R10-1：RetrievalPolicyProfiles 模式专属词表改造（commit `248f853`，+205/-213 行）**：
- 删除 8 个模式专属领域词表（LongTermMemoryKeywords/ChatModeBoost/NovelModeReserve/AutomationMode 等）
- 新增 ModeReserveWeightProfile：按 Chat/Novel/Automation 三模式显式声明三阶段权重
- 信号来源为 Tags 与 Metadata["signal"]/["reserve-signal"]，不再依赖内容关键词匹配
- 长期记忆判断从关键词匹配改为 Layer==Stable 或 Tags 含 long-term 或 Metadata 标记
- 保留 DeprecatedContentHardRejectionKeywords/SoftRejectionKeywords（内容安全过滤，语义正确）
- 更新快照测试添加显式 signal 元数据

**R10-2：Context State 失效边界落地 P1-P3（commit `41498b4`，+543 行）**：
- P1：IStateCacheInvalidator 接口 + CacheInvalidationKey 四元组 + NullStateCacheInvalidator
- P2：6 个核心 Store Decorator（ContextStore/MemoryStore/RelationStore/ConstraintStore/ContextIndex/GlobalContextStore）
  - 每个 Decorator 在写入成功后触发 InvalidateAsync + BumpVersionAsync
  - Decorator 位于最外层，在 dual-write Decorator 之上
  - StorageExtensions 三个 Register 方法全部改造
- P3：IContextStateVersionStore 接口 + InMemoryContextStateVersionStore（ConcurrentDictionary + Interlocked.Increment）
- 新增 5 个文件，修改 2 个文件
- 不引入 ContextStateCache（P6 才允许），只建立失效边界

验证：构建 0 警告 0 错误，测试 792 通过 0 失败。

### R9 系列：Foundation 旧报告链、Artifact 布局、显式审计、DTO 最后一轮、失效边界评估

5 个任务完成，累计净减约 9,206 行（含行为改造 +27 行）。

**R9-2：Foundation 旧报告链整链删除（commit `5083513`，-2,737 行）**：
- 删除 FoundationStatusService（8 个历史报告文件无生产器）
- 删除 3 个 Service endpoint + 4 个 Client 方法
- 删除 EvalCommand.Service.cs（service-api-contract-report 命令）
- 删除 FoundationReportBuilder/MarkdownRenderer/FoundationStatusDtos
- 删除 19 个 DTO（ContextCoreFoundationFreezeReport/FoundationReproducibilityReport/FoundationApi* 等）
- 删除 5 个测试方法

**R9-3：Artifact 布局保护已删除能力清理（commit `668cfc5`，-85 行）**：
- 删除 ArtifactKind 枚举成员：TracePlanning/TraceRankerShadow/TraceVectorShadow/TraceGraphShadow
- 删除 EvalReportPaths.cs（已无消费者）
- 清理 TraceArtifactDescriptorFactory/ContextCoreDataLayout/StorageResponsibilityRegistry

**R9-1：显式审计模式真正完成（commit `510ebab`，+92/-65 行）**：
- IsAuditMode 缺失时默认 false，不再读取 QueryText 关键词推断
- 删除 DomainKeywordProfile.AuditModeKeywords（12 个审计关键词）
- ContextEvalRunner 新增 IsAuditModeQuery() 适配 seed 数据
- 消除 WorkingMemoryRecaller 热路径 12 处 ToArray 分配
- ContainsAny 签名从 params string[] 改为 IReadOnlyList<string>
- 保留词表（被快照测试锁定，建议后续独立任务改造）

**R9-4：DTO 最后一轮删除（commit `5427922`，-6,411 行）**：
- 删除 171 个无生产消费者的 DTO 类型
- VectorIndexDtos 43 个、ServiceSuccessResponseDtos 36 个、VectorEvalSharedReportDtos 43 个
- VectorLegacyDtos 30 个、VectorGateReportDtos 16 个、RelationGraphDtos 7 个
- 恢复 4 个文件内/跨文件引用遗漏的类型
- 删除 44 个引用已删类型的测试方法

**R9-5：Context State 统一失效边界架构评估（无代码变更）**：
- 确认当前无 ContextStateCache（系统处于"无缓存"状态）
- 识别 23 个直接写入 Store 不触发 EventSink 的路径
- 推荐方案 A：Store 边界 Decorator（技术先例已存在）
- 推荐方案 B：Version Store / Generation Token（第二阶段）
- 分阶段实施建议：P1 定义抽象 → P2 核心 Store decorator → P3 version store → P6 才允许引入 ContextStateCache
- 紧急程度：中高（不阻塞当前工作，但任何缓存优化任务必须等 P2 完成）

验证：构建 0 警告 0 错误，测试 792 通过 0 失败。

### R8-5：统一 Package Pipeline 删除 Legacy 路径（commit `556e459`）

将 BasicContextPackageBuilder 的 Legacy 路径转换为默认 Policy 委托，统一为单一打包流水线，并将审计模式改为显式信号。7 文件变更，+75/-152 行（净减 77 行）。

**A. Legacy 路径删除**：
- 删除 BuildLegacyAsync 方法（约 80 行）
- BuildDetailedCoreAsync 改为 `request.Policy ?? CreateDefaultProductionPolicy(request)` 委托到 BuildWithPolicyAsync
- 新增 CreateDefaultProductionPolicy：仅启用 IncludeRecentRawContext（与原 Legacy 行为一致），约束/记忆/全局上下文需调用方显式提供 Policy

**B. 显式 IsAuditMode 信号（渐进迁移）**：
- ContextPackageRequest/ContextPackagePolicy 各新增 `bool? IsAuditMode` 字段
- PackagePolicyResolver.ResolveIsAuditMode：任一 true 即启用，任一 false 即关闭，均 null 回退 QueryText 关键词推断（向后兼容）
- 兑现 R8-0 子任务 5 的后续改造建议（"建议后续向 ContextPackagePolicy 新增 IsAuditMode 字段渐进改造"）

**C. 级联清理**：
- 删除 PackageMetadataBuilder.CreatePackage（仅 Legacy 调用，22 行）
- 删除 LegacyPackageScorer.CountMatchingTags/CalculateLegacyScore（仅 Legacy 调用，27 行）
- 更新 ContextCoreMvpTests 2 处断言：Kind/SectionName raw/large→recent_context，Reason token budget exhausted→candidate not retained after token budget truncation

BasicContextPackageBuilder.cs 从 1338 行降至 1145 行（-193 行）。
验证：构建 0 警告 0 错误，测试 841 通过 0 失败（792+43+6）。

### R8-0：补齐残留删除（commit `9795195`）

5 个子任务完成，消除 R7 系列遗留的循环依赖、404 接口、失效菜单和硬编码词表。46 文件变更，+56/-2163 行（净减 2,107 行）。

**子任务 1：移除 PlanningIntentDetector 循环依赖链**：
- 删除 Core/Services/Planning/PlanningIntentDetector.cs
- 从 ContextRuntimeBuilder/RuntimeServices/CoreExtensions 移除创建和注册
- 新建 Evaluation/Learning/RouterIntentLabels.cs（标签常量留 Evaluation）
- Evaluation 消费者改为显式 Intent 或 Unknown，不依赖关键词自动制造标签
- 清理 15+ 文件的 using ContextCore.Core.Services.Planning
- **纠正 R7-3 错误结论**：PlanningIntentDetector 是循环依赖（Runtime 创建暴露，消费者全在 Evaluation），不是真正的生产依赖

**子任务 2：移除 Router Shadow 404 接口链**：
- 删除 Client GetRouterShadowTracesAsync/ExportRouterShadowTracesAsync
- 删除 IRouterIntentShadowTraceStore 接口和 UnsupportedStore
- 删除 RouterIntentShadowTrace/TraceQuery DTO
- 删除 ArtifactKind.TraceRouterShadow 和 RouterShadowTraceCount 字段
- 清理 FilePathResolver/ContextCoreDataLayout/StorageResponsibilityRegistry 路径
- 保留 RouterIntentShadowTopPrediction（被 RouterIntentClassifier 生产使用）

**子任务 3：清理失效菜单、脚本、Runbook**：
- ServiceDashboardRenderer 删除 [32]PolicyFeedback/[33]LearningFeatures/[F]Proposal/[34]RankerDebug
- 保留 [X]Planning 静态空壳
- 删除 collect-ranker-shadow-traces.ps1 和 collect-graph-expansion-shadow-traces.ps1
- 删除 ContextCoreRunbookTests.cs（保护 404 接口的测试）
- 删除 2 个孤立 runbook 文档

**子任务 4：删除无消费者类型**：
- RouterIntentDatasetProvider（仅测试引用）
- AsyncTraceWriter<T>（仅测试引用）
- LexicalCandidateProvider（无消费者）
- 同步删除对应测试文件

**子任务 5：删除生产领域关键词**：
- RetrievalPolicyProfiles 删除特定领域词「断剑」
- BasicContextPackageBuilder 审计模式推断保留（深度耦合 ~15 处评分决策和 5+ 测试文件，建议后续向 ContextPackagePolicy 新增 IsAuditMode 字段渐进改造）

验证：构建 0 警告 0 错误，测试 911 通过 0 失败。

### R7-3：把 Learning Plane 离线能力迁回 Evaluation（commit `9e96ae7`）

将 Learning Plane 的离线能力从 Core/Service 迁回 Evaluation，彻底分离离线实验与运行时。22 文件变更，+10/-2496 行（净减 2,486 行）。

**A. 迁移离线服务（5 个，Core/Services/Learning → Evaluation/Learning）**：
- PolicyFeedbackDatasetService
- LearningFeatureDatasetService
- LearningDatasetQualityReportBuilder
- RouterIntentClassifier
- RouterIntentDatasetProvider（FileRouterIntentDatasetProvider，保留 SHA-256 hash、error count、version observability 机制）
- 命名空间从 ContextCore.Core.Services 改为 ContextCore.Evaluation.Learning

**B. 删除离线 API 和 Client 方法**：
- LearningEndpoints.cs 删除 5 个离线端点（features/export 等）
- ContextCoreClient.cs 删除 5 个离线 Client 方法
- 保留运行时 feedback ingest API

**C. 删除 ControlRoom 实验页面（2 文件）**：
- ServiceLearningFeaturesScreen.cs、ServicePolicyFeedbackScreen.cs
- 级联清理 Program/Renderer/DashboardScreen/Interaction/ServiceAdmin/Storage

**D. 级联清理死代码**：
- ControlRoomService.Storage.cs 删除 16 个 dead Read*ReportAsync 方法
- ControlRoomService.ServiceAdmin.cs 删除 5 个 dead Read 方法
- CoreExtensions.cs 移除 3 个离线服务 DI 注册

**保留在 Core 的运行时能力**：
- LearningFeedbackService、LearningFeedbackReviewService（feedback ingest/review）
- LearningFeedbackFeatureCandidateBuilder
- ContextLearningCaseGenerator、V14_0 trace sink

**说明**：PlanningIntentDetector 当时判断为"Runtime 生产依赖"有误，实际是循环依赖，已在 R8-0 中彻底删除。

验证：构建 0 警告 0 错误，测试 923 通过 0 失败。

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
