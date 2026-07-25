# R29 — Production Intelligence & Runtime Hardening (设计文档)

> 更新时间：2026-07-25
> 状态：设计评审中
> 上游：R28 Unified Decision Runtime V2（已交付）+ R28-C Agent Kernel + R28-D 真实评分 + R28-E Kernel 可靠性 + R28-F 模型执行增强 + R28-G P1 性能优化（已交付 commit `85325bf`）
> 下游：R30+ 生产化（Observability / SLO / 多租户 / 灾备）依赖 R29 完成后的稳定基线
> 修订：v1 — 初版规划

---

## 1. 目标与范围

### 1.1 目标

把 R28 已完成的"形式化但未完全接线"的能力**收敛到生产主链**，并补齐 R28 暴露的 6 类真实性缺口：

- **A. Scoring Truth** — 真实特征/模型/校准链路（R28-D/R28-F 的能力需从"可注入"升级到"被实际消费 + 严格验证"）
- **B. Agent Execution Semantics** — 单一 Transport + Tool Journal + Ack + Durable Outbox（R28-C/R28-E 的内存实现需替换为 durable）
- **C. Canary Truth** — 生产采样接线 + 真实双路径延迟 + 质量指标 + 自动回滚（R28-B.8 的 CanaryMetricsCollector 需接入 AuthoritativeRuntime 的 shadow 路径）
- **D. Token & Allocator Convergence** — TokenCost 权威化 + V2.1 主链接入 + quota/rollover/MMR（R28-G P1-2..P1-4 的优化需进入生产路径）
- **E. Learning Feedback Loop** — Decision/Tool 结果写入 Utility Ledger，生成训练与校准数据（全新）
- **F. Performance Gate** — R28-C/D 专属 benchmark + 自动回退阈值（R28-G P1 优化需建立持续性能门禁）

### 1.2 范围

- **In scope**：
  - V2.1 Allocator 接入主链（替换 `IGlobalAllocator` 的注入点）
  - `EstimatedTokens` 全面下线，`CandidateTokenCost.ContentTokens` 成为唯一权威 token 输入
  - V2.1 接入 `MandatoryOverflowPolicy` 语义（修复与 V2.0 的语义裂缝）
  - Durable Tool Dispatch Journal + Result Outbox（PostgreSQL 持久化）
  - Durable Agent Checkpoint Store（替换 InMemoryAgentCheckpointStore 的生产路径）
  - Canary 采样接入 AuthoritativeRuntime 的真实 shadow 路径
  - 真实模型 artifact 注册 + 校准验证链路
  - Utility Ledger 写入 + 训练数据导出
  - Benchmark 套件 + CI 性能阈值
- **Out of scope**：
  - 多租户隔离（R30+）
  - 跨 region 灾备（R30+）
  - 在线学习 / 增量模型更新（R30+）
  - 真实向量索引重建（已有独立 workstream）

### 1.3 验收门

R29 完成的硬验收：
1. 生产路径 100% 走 V2.1 Allocator（`AllocateWithDiversity`），`IGlobalAllocator.Allocate`（V2.0）仅作为 V2.1 的内部 fallback；
2. 所有 token 计算使用 `CandidateTokenCost.ContentTokens`，`EstimatedTokens` 标记 `[Obsolete]` 且仅作诊断字段；
3. Agent Kernel 在崩溃后可从 durable checkpoint 恢复，Tool 结果 exactly-once 投递由 durable outbox 保证；
4. Canary 指标来自真实生产请求（非测试调用），自动回滚在真实流量下触发；
5. Benchmark 套件在 CI 中运行，性能回退 > 10% 自动阻断合并。

---

## 2. 当前态映射

### 2.1 R28 已完成 vs R29 待做

| 能力 | R28 完成态 | R29 目标态 |
|---|---|---|
| V2.1 Allocator | 实现完整，DI 注册为 `IAllocatorV2_1`，**主链不调用** | 主链 100% 走 V2.1 |
| Token 计算 | `GetEffectiveTokens` 优先 `ContentTokens`，回退 `EstimatedTokens` | `ContentTokens` 唯一权威，`EstimatedTokens` 标记 `[Obsolete]` |
| MandatoryOverflowPolicy | V2.0 完整接入；V2.1 忽略 `context.MandatoryOverflowPolicy` | V2.1 完整接入三档策略 |
| Agent Checkpoint | InMemory + delta 机制（R28-G P1-5） | Durable（PostgreSQL），delta 链路不变 |
| Tool Dispatch | InMemory journal + exactly-once 状态机（R28-E） | Durable journal + outbox（PostgreSQL） |
| Canary Metrics | ring buffer + DDSketch（R28-G P1-6），**采样靠测试调用** | 接入 AuthoritativeRuntime 真实 shadow 路径 |
| Model Execution | ModelExecutionSnapshot + 校准类型（R28-F） | 真实模型 artifact 注册 + 校准验证 + 推理引擎接入 |
| Performance | P1 优化完成（commit `85325bf`），**无持续门禁** | Benchmark 套件 + CI 阈值 |

### 2.2 V2.1 Allocator 当前态（Workstream D 起点）

```
                     ┌── 主链（实际运行）─────────────────────────┐
   AuthoritativeRuntime → IContextDecisionRuntime.ExecuteWithWorkingSetAsync
                     │   → DefaultContextDecisionEngine.ExecuteV2PathAsync
                     │   → _globalAllocator.Allocate(...)  ← 注入 IGlobalAllocator（V2.0）
                     │                                       V2.1 完全不可达
                     └──────────────────────────────────────────────┘

                     ┌── 形式化栈（暗代码，未接入）────────────────┐
                     │  IAllocatorV2_1.AllocateWithDiversity       │  ← 仅测试调用（28 例）
                     │  DefaultAllocatorV2_1                      │  ← DI 注册但不替换 IGlobalAllocator
                     │  MMR + section rollover + reserve          │  ← R28-G P1-3/P1-4 已优化
                     └──────────────────────────────────────────────┘
```

**关键调用点**（唯一）：`src/ContextCore.Core/Services/DecisionEngine/DefaultContextDecisionEngine.cs:380-382`
```csharp
var allocation = request.AllocationContext is not null
    ? _globalAllocator!.Allocate(lifecyclePassed, effectiveSnapshot, request.AllocationContext)
    : _globalAllocator!.Allocate(lifecyclePassed, effectiveSnapshot);
```

---

## 3. Workstream 概要

| ID | 名称 | 主要目标 | 依赖 | 优先级 |
|---|---|---|---|---|
| D | Token & Allocator Convergence | V2.1 主链接入 + TokenCost 权威化 | 无（起点） | P0 |
| F | Performance Gate | Benchmark + CI 阈值 | D（提供优化基线） | P0（与 D 并行） |
| B | Agent Execution Semantics | Durable Transport + Journal + Outbox | 无 | P1 |
| C | Canary Truth | 生产采样接线 | B（durable kernel 提供 shadow 路径） | P1 |
| A | Scoring Truth | 真实模型 + 校准验证 | D（Allocator 消费 ModelScore） | P2 |
| E | Learning Feedback Loop | Utility Ledger + 训练数据 | A（评分作为 ledger 输入） | P2 |

### 3.1 实施顺序（"先搭框架然后并行在框架上建设"）

```
Phase 1（框架）:
  D（Token & Allocator Convergence）
  F（Performance Gate — 与 D 并行，提供基线）

Phase 2（框架上并行建设）:
  B（Agent Execution — durable）
  C（Canary Truth — 接入真实路径）

Phase 3（依赖 Phase 2 的数据流）:
  A（Scoring Truth — 真实模型）
  E（Learning Feedback Loop — 消费 A 的评分）
```

---

## 4. Workstream D — Token & Allocator Convergence（详细设计）

### 4.1 目标

1. **V2.1 主链接入**：让 `DefaultContextDecisionEngine` 注入 `IAllocatorV2_1`，在 V2 路径调用 `AllocateWithDiversity`。
2. **TokenCost 权威化**：`CandidateTokenCost.ContentTokens` 成为唯一 token 输入；`EstimatedTokens` 标记 `[Obsolete]`，仅保留诊断用途。
3. **MandatoryOverflowPolicy 接入 V2.1**：修复 V2.1 内部忽略 `context.MandatoryOverflowPolicy` 的语义裂缝。
4. **DiversityOptions 默认值接入策略**：从 `EffectivePolicySnapshot` 读取 diversity 配置，而非硬编码。

### 4.2 工作包拆分

#### WP-D-1: V2.1 主链接入

**改动点**：

1. `src/ContextCore.Core/Services/DecisionEngine/DefaultContextDecisionEngine.cs`:
   - 字段 `IGlobalAllocator? _globalAllocator` → 新增 `IAllocatorV2_1? _allocatorV2_1`
   - 构造函数注入 `IAllocatorV2_1?`（可选；null 时回退到 `IGlobalAllocator`）
   - `ExecuteV2PathAsync` 阶段 4：根据 `request.AllocationContext` + `request.DiversityOptions` 选择路径：
     - `IAllocatorV2_1` 注入且 `DiversityOptions` 非空 → `AllocateWithDiversity`
     - 否则 → `IGlobalAllocator.Allocate`（V2.0 fallback）

2. `src/ContextCore.Service/Extensions/CoreExtensions.cs`:
   - `DefaultContextDecisionEngine` 构造注入 `IAllocatorV2_1`（`sp.GetService<IAllocatorV2_1>()`）
   - 保留 `IGlobalAllocator` 注册（V2.1 内部仍需委托）

3. `src/ContextCore.Abstractions/Contracts/UnifiedRuntimeContracts.cs`:
   - `ContextDecisionRequest` 新增 `DiversityOptions? DiversityOptions` 字段（可选；null 时 V2 路径走 V2.0）

4. `src/ContextCore.Core/Services/DecisionEngine/UnifiedRuntimeDefaults.cs`:
   - `DefaultContextDecisionRuntime.ExecuteWithWorkingSetAsync` 从 `EffectivePolicySnapshot` 读取 `DiversityOptions` 并填入 `ContextDecisionRequest`

**验收**：
- 单元测试：V2 路径走 `AllocateWithDiversity`，诊断信息含 `AllocatorVersion=V2.1`
- 回归测试：`DiversityOptions=null` 时回退到 V2.0，行为不变
- PublicAPI baseline 更新（`ContextDecisionRequest.DiversityOptions` 字段）

#### WP-D-2: MandatoryOverflowPolicy 接入 V2.1

**改动点**：

`src/ContextCore.Core/Services/DecisionEngine/DefaultAllocatorV2_1.cs`:
- `AllocateWithinSectionWithTracking` 内 mandatory 候选处理：
  - `FailClosed` → mandatory 超预算时抛 `MandatoryContextWindowExceededException`
  - `RejectLowestAuthorityMandatory` → 拒绝当前 mandatory（生成 dropped decision）
  - `AllowOverflowWithDiagnostic` → 当前放行 + 诊断（现有行为）
- `AllocateSectionsWithRollover` 透传 `context.MandatoryOverflowPolicy` 给 `AllocateWithinSectionWithTracking`

**验收**：
- 新增测试：V2.1 路径 + `FailClosed` + mandatory 超预算 → 抛异常
- 新增测试：V2.1 路径 + `RejectLowestAuthorityMandatory` → mandatory 被拒绝
- 回归测试：`AllowOverflowWithDiagnostic` 行为不变

#### WP-D-3: TokenCost 权威化

**改动点**：

1. `src/ContextCore.Abstractions/Contracts/DecisionEngineContracts.cs`:
   - `ContextCandidateEnvelope.EstimatedTokens` 标记 `[Obsolete("Use TokenCost.ContentTokens. Fallback to length/4 is inaccurate for CJK/code/JSON.")]`

2. `src/ContextCore.Core/Services/DecisionEngine/UnifiedRuntimeDefaults.cs`:
   - `DefaultGlobalAllocator.GetEffectiveTokens` 保持回退（向后兼容），但增加诊断：回退时写入 `Outcome.Diagnostics["tokenFallback"] = envelope.CandidateId`
   - `ContextDecisionOutcomeSummary.EstimatedTokens` 重命名为 `EffectiveTokens`（保留旧字段 `[Obsolete]` 别名）

3. `src/ContextCore.Core/Services/DecisionEngine/RetrievalResultProjector.cs`:
   - 所有 `envelope.EstimatedTokens` 改为 `GetEffectiveTokens(envelope)`（统一 helper）

4. `src/ContextCore.Core/Services/DecisionEngine/PackageResultProjector.cs`:
   - 同上

5. `src/ContextCore.Core/Services/DecisionEngine/DefaultContextDecisionEngine.cs` Legacy 路径:
   - 行 189/225/239/256：`EstimatedTokens` → `GetEffectiveTokens` helper

6. `src/ContextCore.Core/Services/DecisionEngine/CandidateProviders.cs`:
   - Provider 产出 envelope 时**必须**调用 `EnrichTokenCost` 填充 `CandidateTokenCost`（当前是可选）；若 `IContextTokenizerResolver` 不可用则抛 `InvalidOperationException`（fail-fast，不再静默回退到 `length/4`）

**验收**：
- 单元测试：Provider 未注入 tokenizer 时抛异常
- 回归测试：所有现有测试的 `EstimatedTokens` 字段读取改为 `GetEffectiveTokens`
- PublicAPI baseline 更新（`[Obsolete]` 标记）

#### WP-D-4: DiversityOptions 从 Policy 读取

**改动点**：

`src/ContextCore.Abstractions/Contracts/UnifiedRuntimeContracts.cs`:
- `EffectivePolicySnapshot` 新增 `DiversityOptions? DiversityOptions` 字段（可选）

`src/ContextCore.Core/Services/Policy/DefaultPolicyBundleFactory.cs`:
- 默认 bundle 设置 `DiversityOptions = new DiversityOptions()`（默认值：Lambda=0.5, EnableSectionRollover=true, SectionReserveRatio=0.1）

`src/ContextCore.Core/Services/DecisionEngine/UnifiedRuntimeDefaults.cs`:
- `DefaultContextDecisionRuntime.ExecuteWithWorkingSetAsync` 从 `effectiveSnapshot.DiversityOptions` 读取并填入 `ContextDecisionRequest.DiversityOptions`

**验收**：
- 单元测试：Policy 配置 `Lambda=0.0` → V2 路径走纯 diversity 排序
- 单元测试：Policy 未配置 `DiversityOptions` → 默认值

### 4.3 验收清单（Workstream D）

- [ ] V2 主链 100% 走 `AllocateWithDiversity`
- [ ] `MandatoryOverflowPolicy.FailClosed` 在 V2.1 路径生效
- [ ] `EstimatedTokens` 标记 `[Obsolete]`，所有主链读取改为 `GetEffectiveTokens`
- [ ] Provider 未注入 tokenizer 时 fail-fast
- [ ] `DiversityOptions` 从 `EffectivePolicySnapshot` 读取
- [ ] PublicAPI baseline 更新
- [ ] 全量测试通过（ContextCore + Service + Integration）
- [ ] Benchmark：V2.1 路径性能 ≥ V2.0（由 Workstream F 提供 benchmark）

---

## 5. Workstream F — Performance Gate（详细设计）

### 5.1 目标

1. 为 R28-C（Agent Kernel）和 R28-D（Decision Engine）建立专属 benchmark 套件。
2. CI 中运行 benchmark，性能回退 > 10% 自动阻断合并。
3. 提供"自动回退阈值"（性能回退时自动回退到 V2.0 Allocator）。

### 5.2 工作包

#### WP-F-1: Benchmark 套件

**新建** `benchmarks/ContextCore.Benchmarks/`:
- `DecisionEngineBenchmarks.cs`：Allocator / MMR / CanonicalMerge / Projector 微基准
- `AgentKernelBenchmarks.cs`：Tool dispatch / Checkpoint / Resume 微基准
- `CanaryMetricsBenchmarks.cs`：ring buffer / DDSketch 微基准

每个 benchmark 覆盖：
- 小规模（n=10）/ 中规模（n=100）/ 大规模（n=1000）候选集
- 记录：Mean / Median / StdDev / P95 / Allocated

#### WP-F-2: CI 集成

**新建** `.github/workflows/benchmark.yml`:
- PR 触发：运行 benchmark 套件
- 与 main 分支基线对比：任一 benchmark 回退 > 10% → 标记 `performance-regression` label
- 主分支合并后：更新基线

#### WP-F-3: 自动回退阈值

**改动** `DefaultContextDecisionEngine`:
- 注入 `IPerformanceMonitor`（新增接口）
- V2 路径执行时间 > 阈值（可配置，默认 500ms）时，记录诊断 + 下次请求回退到 V2.0

### 5.3 验收清单

- [ ] Benchmark 套件覆盖 3 个子系统
- [ ] CI 自动运行 + 回退检测
- [ ] 自动回退阈值可配置

---

## 6. Workstream B — Agent Execution Semantics（概要设计）

### 6.1 目标

1. **Durable Transport**：`InProcessTransport` 替换为 `IDurableTransport`（PostgreSQL-backed Channel）。
2. **Durable Tool Journal**：`InMemoryToolDispatchJournal` 替换为 `IPersistentToolDispatchJournal`（PostgreSQL）。
3. **Durable Outbox**：`InMemoryKernelResultOutbox` 替换为 `IPersistentKernelResultOutbox`（PostgreSQL）。
4. **Durable Checkpoint Store**：`InMemoryAgentCheckpointStore` 生产路径替换为 `IPersistentAgentCheckpointStore`。

### 6.2 工作包

- WP-B-1: `IPersistentToolDispatchJournal` + PostgreSQL 实现
- WP-B-2: `IPersistentKernelResultOutbox` + PostgreSQL 实现
- WP-B-3: `IPersistentAgentCheckpointStore` + PostgreSQL 实现（复用 R28-G P1-5 delta 链路）
- WP-B-4: `IDurableTransport` + 配置开关（开发环境保留 InMemory）

### 6.3 验收

- 崩溃恢复测试：Kernel 执行中崩溃 → 重启后从 durable checkpoint 恢复 → Tool 结果 exactly-once 投递
- 性能：durable 路径延迟 ≤ InMemory × 3（可接受）

---

## 7. Workstream C — Canary Truth（概要设计）

### 7.1 目标

1. **生产采样接线**：`AuthoritativeRuntime` 的 shadow 路径自动调用 `ICanaryMetricsCollector.RecordObservation`。
2. **真实双路径延迟**：V2 / Legacy 路径分别计时，写入 `RecordObservation(v2Duration, legacyDuration)`。
3. **质量指标**：除了 latency / error_rate / divergence_rate，新增 `quality_score`（由 Projector 输出的 section 覆盖率 + 候选相关性）。
4. **自动回滚**：`CanaryProgressionService` 在真实流量下触发回滚（已有逻辑，需接入生产 metrics）。

### 7.2 工作包

- WP-C-1: `AuthoritativeRuntime` shadow 路径调用 `RecordObservation`
- WP-C-2: V2 / Legacy 真实计时（`Stopwatch`）
- WP-C-3: 质量指标计算 + 接入 `CanaryProgressionService`
- WP-C-4: 生产环境自动回滚验证（shadow 流量 + 真实 metrics）

---

## 8. Workstream A — Scoring Truth（概要设计）

### 8.1 目标

1. **真实模型 artifact 注册**：`IModelArtifactRegistry` + PostgreSQL 持久化（model_id / version / feature_schema / calibration / content_hash）。
2. **真实推理引擎接入**：`IInferenceEngine` 实现接入真实模型（ONNX Runtime / 本地 Python 服务）。
3. **校准验证**：`ICalibrationValidator` 在模型加载时验证校准参数（Platt / Temperature / Isotonic）的统计有效性。
4. **特征 Schema 严格匹配**：`FeatureSchemaValidator` 在推理前验证输入特征与 `FeatureSchemaVersion` 一致。

### 8.2 工作包

- WP-A-1: `IModelArtifactRegistry` + PostgreSQL 实现
- WP-A-2: `IInferenceEngine` ONNX Runtime 实现
- WP-A-3: `ICalibrationValidator` + 校准统计验证
- WP-A-4: `FeatureSchemaValidator` + 严格匹配
- WP-A-5: 真实模型端到端测试（feature → inference → calibration → score）

---

## 9. Workstream E — Learning Feedback Loop（概要设计）

### 9.1 目标

1. **Utility Ledger**：每次决策的 `CandidateUtilityScore` + `ModelExecutionSnapshot` + 最终用户反馈写入持久化 ledger。
2. **训练数据导出**：ledger 数据按模型 artifact 版本导出为训练集（feature / label / calibration_target）。
3. **校准数据导出**：ledger 数据按模型版本导出为校准集（predicted / observed）。
4. **反馈接入**：用户显式反馈（thumbs up/down）写入 ledger 的 `user_feedback` 字段。

### 9.2 工作包

- WP-E-1: `IUtilityLedger` 接口 + PostgreSQL 实现
- WP-E-2: `DefaultContextDecisionRuntime` 在决策完成后写入 ledger
- WP-E-3: 训练数据导出工具（CLI + SQL）
- WP-E-4: 校准数据导出工具
- WP-E-5: 用户反馈接入（API + ledger 写入）

---

## 10. 依赖关系图

```
                    ┌─────────────────────────────┐
                    │  Phase 1: 框架              │
                    │                             │
                    │  D ──────────── F           │
                    │  (Token &     (Perf Gate)   │
                    │   Allocator)                │
                    └─────────┬───────────────────┘
                              │
                    ┌─────────▼───────────────────┐
                    │  Phase 2: 并行建设           │
                    │                             │
                    │  B ──────────── C           │
                    │  (Durable      (Canary      │
                    │   Kernel)       Truth)       │
                    └─────────┬───────────────────┘
                              │
                    ┌─────────▼───────────────────┐
                    │  Phase 3: 数据流闭环         │
                    │                             │
                    │  A ──────────── E           │
                    │  (Scoring      (Learning    │
                    │   Truth)       Loop)        │
                    └─────────────────────────────┘
```

**关键依赖**：
- F 依赖 D（提供优化基线）
- C 依赖 B（durable kernel 提供 shadow 路径的可靠性）
- E 依赖 A（评分作为 ledger 输入）

---

## 11. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| V2.1 接入主链后性能回退 | 中 | 高 | F 提供 benchmark + 自动回退阈值（WP-F-3） |
| TokenCost 权威化破坏向后兼容 | 中 | 中 | `GetEffectiveTokens` 保留回退路径 + `[Obsolete]` 渐进迁移 |
| Durable 路径延迟过高 | 低 | 高 | 开发环境保留 InMemory + 配置开关（WP-B-4） |
| 真实模型推理不可用 | 中 | 中 | `IInferenceEngine` 注入失败时回退到 deterministic（R28-F 已有 fallback） |
| Canary 误触发回滚 | 中 | 高 | `CanaryProgressionService` 阈值保守 + 最小观察期 |

---

## 12. 实施计划

### Phase 1（框架）— 预计 2-3 个工作周期

- **WP-D-1** V2.1 主链接入
- **WP-D-2** MandatoryOverflowPolicy 接入 V2.1
- **WP-D-3** TokenCost 权威化
- **WP-D-4** DiversityOptions 从 Policy 读取
- **WP-F-1** Benchmark 套件（与 D 并行）
- **WP-F-2** CI 集成
- **WP-F-3** 自动回退阈值

### Phase 2（并行建设）— 预计 3-4 个工作周期

- **WP-B-1..B-4** Durable Kernel
- **WP-C-1..C-4** Canary Truth

### Phase 3（数据流闭环）— 预计 2-3 个工作周期

- **WP-A-1..A-5** Scoring Truth
- **WP-E-1..E-5** Learning Feedback Loop

---

## 13. 验收门

### 13.1 Phase 1 验收（Workstream D + F）

- [ ] 生产路径 100% 走 V2.1 Allocator
- [ ] `EstimatedTokens` 标记 `[Obsolete]`
- [ ] V2.1 路径 `MandatoryOverflowPolicy` 三档生效
- [ ] Benchmark 套件覆盖 3 个子系统
- [ ] CI 性能回退检测生效
- [ ] 全量测试通过

### 13.2 Phase 2 验收（Workstream B + C）

- [ ] Agent Kernel 崩溃后从 durable checkpoint 恢复
- [ ] Tool 结果 exactly-once 投递（durable outbox 保证）
- [ ] Canary 指标来自真实生产请求
- [ ] 自动回滚在真实流量下触发

### 13.3 Phase 3 验收（Workstream A + E）

- [ ] 真实模型 artifact 注册 + 加载
- [ ] 校准验证在模型加载时生效
- [ ] Utility Ledger 写入每次决策
- [ ] 训练数据 / 校准数据可导出

---

## 附录 A：R28-G P1 优化基线（Workstream D/F 起点）

| 组件 | R28-G P1 优化 | 当前状态 |
|---|---|---|
| Canonical Merge | 单 accumulator + hash 缓存 | 已交付（commit `85325bf`） |
| Default Allocator | mandatory partition + TopK heap | 已交付 |
| MMR | O(n²) + 增量 maxSimilarity | 已交付 |
| Allocator V2.1 | 两 phase + reserve + pool | 已交付（但未接入主链） |
| Agent Checkpoint | FIFO + delta | 已交付（InMemory） |
| Canary Metrics | ring buffer + DDSketch | 已交付（未接入生产采样） |

Workstream D 的工作是在此基线上把"已优化但未接入"的能力接入主链；Workstream F 的工作是为这些优化建立持续门禁。
