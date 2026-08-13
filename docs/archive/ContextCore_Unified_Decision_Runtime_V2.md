# ContextCore Unified Decision Runtime V2 (R28-B 设计文档 v2)

> 更新时间：2026-07-22
> 状态：设计评审中（评审通过后才进入实现）
> 上游：R28 Context Runtime Convergence — Workstream A 已收口（commit `5b04a05`，schema v17）
> 下游：Workstream C（专属高性能 .NET Agent Kernel）必须建立在本 Runtime 之上
> 修订：v2 — 采纳评审反馈，拆分 Runtime/Engine 两层、修正 PolicySnapshot 类型冲突、引入 CanonicalCandidateKey + Material sidecar、Allocator 解耦、Shadow tee、B-1~B-5 重排

---

## 1. 目标与范围

### 1.1 目标

把当前**双重决策链**收敛为**唯一 Context Decision Runtime**：

- **当前态**：形式化 `DefaultContextDecisionEngine`（R18-R20 栈）是"暗代码"——完整实现并通过单元测试，但**未在 DI 注册、未被生产主链调用**；生产实际跑在 `HybridContextRetriever`（Retrieval）+ `BasicContextPackageBuilder`（Package）两条独立主链上。两条主链各自做 safety / scoring / budget，语义不一致。
- **V2 目标态**：引入 `IContextDecisionRuntime` 作为唯一 I/O 编排入口；`IContextDecisionEngine` 保持纯决策内核语义不变；Retrieval / Package / AgentContext 三种业务用途统一经 Runtime 编排，再由 Engine 做纯内存决策。

### 1.2 范围

- **In scope**：
  - 两层分层：`IContextDecisionRuntime`（I/O 编排）+ `IContextDecisionEngine`（纯决策，保持现有语义）
  - `EffectivePolicySnapshot` + `ResolvedPolicyReference`（不与已有 `ResolvedPolicySnapshot` 冲突）
  - `ContextDecisionPurpose` + `ContextDecisionRuntimeKind` 双轴
  - `ContextDecisionRuntimeRequest` + `SeedCandidates`
  - `CanonicalCandidateKey` + `CandidateWorkingSet` + `CandidateMaterial` sidecar
  - 8 个真 Candidate Expert + `ICandidateProvider`
  - Early Admission Gate + Decision Safety Gate 拆分
  - `CandidateAllocationDecision` 解耦的 Allocator + `MandatoryOverflowPolicy`
  - Shadow tee 模式（单次候选捕获，Legacy/V2 共享快照）
  - `DecisionExperimentPlane` 长期保留
  - rule-only 等价性保证（双层 parity：Hard + Diagnostic）
  - **diversity 仅提供 extension point，rule-only convergence 阶段禁用行为变更**（MMR / 跨 section diversity / learned allocation 留到 V2.1）
- **Out of scope**：Agent Kernel（Workstream C）、Model Execution Runtime、Memory Evolution PostgreSQL 持久化、Decision Replay Plane、真正的 diversity 算法（V2.1）。

### 1.3 验收门

WS-B 完成的硬验收（参见 §13）通过后，才允许：
1. 重新冻结 P0 correctness baseline；
2. 启动 Workstream C（Agent Kernel）。

---

## 2. 当前态映射

### 2.1 双重决策链问题

```
                     ┌── 形式化栈（暗代码，未接入）─────────────────┐
                     │  IContextDecisionEngine (纯决策)              │  ← 既有契约，语义保留
                     │  DefaultContextDecisionEngine.DecideAsync     │  ← Envelope → Safety → Score → Allocate
                     │  RetrievalCandidateAdapter / PackageCandidateAdapter
                     │  RetrievalResultProjector / PackageResultProjector
                     │  IRetrievalRouter（advisory，未被调用）       │
                     │  ResolvedPolicySnapshot (BundleId/Version/    │  ← 既有轻量引用
                     │                          ResolvedAt)         │     被 CandidateAdaptationContext 使用
                     └──────────────────────────────────────────────┘
                                ↓ 从未调用
                     ┌── 生产主链（实际运行）────────────────────────┐
   请求 → HybridContextRetriever.RetrieveAsync
                     │   ├── MandatoryRecallChannelExecutor ──┐
                     │   ├── ContextRecallChannelExecutor      │ 5 个 channel
                     │   ├── VectorRecallChannelExecutor       │ executor 按
                     │   ├── MemoryRecallChannelExecutor       │ boolean flag
                     │   └── RelationRecallChannelExecutor ─────┘ 并行执行
                     │   → RetrievalCandidateAccumulator → RetrievalResultAssembler
                     │
   请求 → BasicContextPackageBuilder.BuildAsync
                     │   ├── PackageInputLoader
                     │   ├── CandidateSelector（3 个 SectionCollectorBase）
                     │   ├── ResultProjector（reorder + pack）
                     │   └── PackageBudgetProjector + PackageSectionBudgetResolver
                     └──────────────────────────────────────────────┘
```

### 2.2 当前态关键缺口

| 能力 | 形式化栈状态 | 生产主链状态 | V2 要求 |
|---|---|---|---|
| **唯一入口** | Engine 存在但未接入 | 两条主链各自决策 | Runtime 为唯一 I/O 入口，Engine 保持纯决策 |
| **Adapter 位置** | 在最终结果边界（未启用） | N/A | 迁到 CandidateProvider 输出边界 |
| **Router** | advisory，Engine 不调用 | 不存在（用 boolean flag） | Runtime 调用，真正控制 Provider 执行 |
| **Canonical Merge** | 不存在（adapter 预合并） | 各自 assembler/collector 内合并 | 显式独立步骤，按 `CanonicalCandidateKey` 合并多 Expert 来源 |
| **Feature Pipeline** | 不存在（adapter 静态填充） | Package 用 13 维 ItemScoreBreakdown / Retrieval 用单一 Score | 显式独立步骤，`EnrichAsync` 纯转换 |
| **Safety Gate** | 内联 Engine，RequiredTags/ForbiddenTags 未实际检查 | 分散在 collector/quality calculator | 拆为 Early Admission Gate + Decision Safety Gate |
| **Lifecycle Gate** | 不存在（仅字段） | 分散 | 显式独立步骤 |
| **Utility Scorer** | 不存在（仅 fallback 逻辑） | 上游预计算 | 显式独立步骤，计算 FinalScore |
| **Global Allocator** | 全局硬上限，Package section 分层未实现 | PackageSectionBudgetResolver 独立 | 统一 Allocator，产出 `CandidateAllocationDecision`，不污染 Envelope |
| **Material / 正文** | Envelope 不含 Content | 主链直接访问 Store | `CandidateMaterial` sidecar，Projector 不访问 Store |
| **Projector** | 2 个（Retrieval/Package），纯投影 | N/A | 新增 AgentContextProjector，3 个纯投影 |
| **rule-only 等价** | 隐式（EnableModel=false） | N/A | 显式 Hard + Diagnostic 双层 parity |
| **ContextDecisionResult** | envelope 集合，无 Agent Context | N/A | 不直接产出 DTO，由 Projector + Material 投影 |

---

## 3. V2 分层架构

### 3.1 两层分工（评审反馈 #1）

```
   ┌──────────────────────────────────────────────────────────────┐
   │ IContextDecisionRuntime  (唯一 I/O 入口，新增)                  │
   │   负责：Policy resolution → Router → CandidateProviders →      │
   │         Canonical Merge → Early Gate → Feature Pipeline →      │
   │         调用 Engine → 产出 ContextDecisionResult                │
   │   可 I/O：读 Store（通过 Provider）、调用 Router、加载 Policy    │
   └──────────────────────────────────────────────────────────────┘
                              │ 调用
                              ▼
   ┌──────────────────────────────────────────────────────────────┐
   │ IContextDecisionEngine  (纯决策内核，保持现有语义不变)          │
   │   负责：Decision Safety Gate → Utility Scoring → Allocate      │
   │   纯内存：不依赖 Store，不调用 Router，不加载 Policy            │
   │   现有 DecideAsync(request) → ContextDecisionResult 签名保留   │
   └──────────────────────────────────────────────────────────────┘
```

**关键不变量**：
- `IContextDecisionEngine` 的现有语义（"对候选 envelope 集合执行 safety gate → utility scoring → budget allocation 决策"，不依赖存储，候选由调用方传入）**保持不变**。
- `IContextDecisionRuntime` 是新增的 I/O 编排层，负责把外部请求转换为 Engine 可消费的纯内存输入。
- Agent Kernel 只依赖 `IContextDecisionRuntime`，不直接依赖 Engine。

### 3.2 V2 主链图

```
   ContextDecisionRuntimeRequest  (RequestId, Scope, Purpose,
                                   QueryText, TokenBudget, TopK, SeedCandidates)
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 1. Effective Policy Snapshot                │  IResolvedPolicyProvider
   │   (Reference + Effective Safety/Budget/      │  请求生命周期内不可变
   │    Routing + FeatureSchemaVersion +           │
   │    RouterModelHash/RankerModelHash)           │
   └─────────────────────────────────────────────┘
            │  Scope 一致性校验（Request scope ≡ Snapshot scope，否则 fail-closed）
            ▼
   ┌─────────────────────────────────────────────┐
   │ 2. Router                                    │  IRouter.RouteAsync(request, snapshot)
   │   基于 IExpertCatalog.AvailableExperts       │  产出 ExpertMask + per-Expert (TopK, Tokens)
   │   EnabledExperts 真正生效；未注册 Expert      │  ReasonCode = expert-not-registered
   │   被显式 disable（不注册 no-op）              │
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 3. Candidate Providers (单次执行)            │  ICandidateProvider[]
   │   仅执行 mask 命中的 Provider；并行；          │  Adapter 位于此边界
   │   每个 Provider 输出 ExpertExecutionResult    │  (Envelopes + Materials)
   │   产出 (Envelope, Material) 二元组            │
   └─────────────────────────────────────────────┘
            │
            ▼  ←── Tee：同一 raw candidate snapshot 同时供 Legacy / V2 消费
   ┌─────────────────────────────────────────────┐
   │ 4. Canonical Candidate Merge                 │  ICanonicalCandidateMerger
   │   按 CanonicalCandidateKey 合并多 Expert 来源；│
   │   Envelope.Origins = union(ExpertOrigin[])；  │
   │   ExpertContributions = per-Expert 权重       │
   │   Materials 按 Key 去重保留                  │
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 5. Early Admission Gate                      │  IEarlyAdmissionGate
   │   scope mismatch / superseded / archived /    │
   │   rejected / forbidden tag / illegal evidence│
   │   / hard lifecycle block                     │
   │   → 早期剔除，不计算完整特征                  │
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 6. Feature Pipeline                          │  IFeaturePipeline.EnrichAsync
   │   计算/标准化 CandidateFeatureVector；        │  纯转换：返回新 Envelope 列表
   │   不计分，只计算特征                          │  (immutable record 友好)
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ ── 进入 IContextDecisionEngine (纯决策) ──   │
   │ 7. Decision Safety Gate                     │  ISafetyGate
   │   duplicate / required coverage /            │  + ILifecycleGate
   │   cross-candidate conflict /                │
   │   full evidence rules                        │
   │   (Mandatory/Hard Constraint 免预算，        │
   │    不免 Safety/Lifecycle)                    │
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 8. Utility Scorer                            │  IUtilityScorer
   │   FinalScore = w_d * Det + w_m * Model       │  rule-only: w_d=1, w_m=0
   │   （仅当 ModelConfidence >= threshold）       │
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 9. Global Allocator                          │  IGlobalAllocator
   │   消费 SectionRatios + TopK + TokenBudget；   │  产出 AllocationResult
   │   per-section 分配 + deterministic 排序；     │  (Selected/Dropped/Decisions/Outcome)
   │   diversity extension point 存在但禁用行为变更│
   │   MandatoryOverflowPolicy 显式定义           │
   └─────────────────────────────────────────────┘
            │
            ▼
   ┌─────────────────────────────────────────────┐
   │ 10. ContextDecisionResult                   │
   │   (SelectedEnvelopes, DroppedEnvelopes,      │
   │    AllocationDecisions, Outcome,             │
   │    Purpose, RuntimeKind=UnifiedV2,           │
   │    PolicyReference, ModelVersion)            │
   └─────────────────────────────────────────────┘
            │
            ├──────────────┬──────────────┐
            ▼              ▼              ▼
   ┌─────────────┐ ┌─────────────┐ ┌─────────────┐
   │ Retrieval   │ │ Package     │ │ Agent       │
   │ Projector   │ │ Projector   │ │ Context     │
   │             │ │             │ │ Projector   │
   │ 输入：Result│ │ 输入：Result│ │ 输入：Result│
   │ + WorkingSet│ │ + WorkingSet│ │ + WorkingSet│
   │ (Materials)│ │ (Materials)│ │ (Materials)│
   │ → Context   │ │ → Context   │ │ → Agent     │
   │   Retrieval │ │   Package   │ │   Context   │
   │   Result    │ │   Build     │ │   Snapshot  │
   │             │ │   Result    │ │  (复用 R23) │
   └─────────────┘ └─────────────┘ └─────────────┘
        纯投影         纯投影         纯投影
```

### 3.3 主链不变量

- **顺序固定**：阶段 1→10 严格顺序（Early Gate 在 Feature 之前；Decision Safety Gate 在 Engine 内部）。
- **单一入口**：所有 Context 决策必须经 `IContextDecisionRuntime.ExecuteAsync`。`HybridContextRetriever` / `BasicContextPackageBuilder` 退化为 Runtime 的 Provider 编排器 + Projector 调用方。
- **Engine 纯净**：Engine 不依赖 Store、不调用 Router、不加载 Policy；其输入由 Runtime 准备完毕。
- **快照稳定**：`EffectivePolicySnapshot` 在整个请求生命周期内不可变。
- **Scope 一致**：Request.Scope 与 EffectivePolicySnapshot 的 ResolutionScope 必须一致，否则 fail-closed。

---

## 4. 硬边界

每条边界都由测试强制（见 §13）。

### 4.1 Adapter 位于 CandidateProvider 输出边界

- **语义**：`RetrievalCandidateAdapter` / `PackageCandidateAdapter` 在 `ICandidateProvider` 内部被调用，把 Provider 的原生输出转换为 `ContextCandidateEnvelope` + `CandidateMaterial`。Runtime/Engine 入口接收的候选必须已是 envelope。
- **强制**：`AllAdapterPathsRequireAdaptationContext`（已存在）+ `AdapterNotInvokedInsideRuntime`（反射/静态分析 Runtime/Engine 不引用 Adapter 类型）。

### 4.2 Projector 不得访问 Store

- **语义**：3 个 Projector 只能从 `ContextDecisionResult` + `CandidateWorkingSet.Materials` 投影为 DTO。**禁止**注入任何 Store。
- **强制**：`ProjectorsCannotChangeSelectedSet` + 构造函数签名审计。

### 4.3 Projector 不得重新排序、过滤、截断或计分

- **语义**：Projector 必须保持 Engine 输出的 `SelectedEnvelopes` + `AllocationDecisions` 顺序与集合不变。
- **强制**：`ProjectorsCannotChangeSelectedSet`。

### 4.4 Package section allocation 属于统一 Allocator

- **语义**：section 分配由阶段 9 的 `IGlobalAllocator` 统一计算，产出 `CandidateAllocationDecision`；Projector 只读取已分配好的 Decision。
- **强制**：`PackageSectionAllocationMatchesFrozenBaseline`。

### 4.5 Router 决策必须真正控制 Provider 是否执行

- **语义**：Runtime 在阶段 3 调用 Provider 时，必须先查询 `ExpertRoutingDecisionSet.IsEnabled(expert)`，仅对启用的 Provider 调用 `ExecuteAsync`。`RoutingProfile.EnabledExperts`（空 = 全部启用）由 Router 真实消费。未注册的 Expert（如 Recency）由 Router 基于 `IExpertCatalog.AvailableExperts` 显式 disable，ReasonCode = `expert-not-registered`。
- **强制**：`RouterMaskControlsActualExpertExecution`。

### 4.6 rule-only 模式和当前 frozen baseline 完全等价

- **语义**：当 `RoutingProfile.EnableModelScoring=false`（默认值）时，V2 主链输出必须与当前生产主链在相同输入下集合等价。
- **强制**：`PackageSectionAllocationMatchesFrozenBaseline` + `UnifiedRuntimeRuleOnlyMatchesBaseline`。
- **允许的差异**：仅 `DecidedAt` 时间戳、`RequestId`、内部 trace ID 等非语义字段。

### 4.7 Shadow 执行不得双倍读取 Store（评审反馈 #5）

- **语义**：B-2 阶段影子执行必须使用 Tee 模式——候选捕获只执行一次，Legacy 与 V2 消费同一 raw candidate snapshot。**禁止** V2 重新执行 Provider / 重新查询 Store / 重新计算 embedding。
- **强制**：
  - `ShadowExecutionDoesNotDoubleReadStores`
  - `CandidateProvidersExecuteAtMostOncePerRequest`
  - `LegacyAndUnifiedConsumeSameCandidateSnapshot`

---

## 5. 核心契约

### 5.1 两层入口（评审反馈 #1）

```csharp
// 唯一 I/O 入口（新增）
public interface IContextDecisionRuntime
{
    ValueTask<ContextDecisionResult> ExecuteAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}

// 纯决策内核（既有，保持现有语义不变）
public interface IContextDecisionEngine
{
    Task<ContextDecisionResult> DecideAsync(
        ContextDecisionRequest request,
        CancellationToken cancellationToken = default);
}
```

### 5.2 Policy 双类型（评审反馈 #2）

```csharp
// 既有：轻量引用（保持不变，被 CandidateAdaptationContext / EvidenceRef 使用）
// public sealed record ResolvedPolicySnapshot { BundleId; Version; ResolvedAt; }
// 不修改，不重定义。

// 新增：不可变引用（用于 Envelope provenance / Allocation / Evidence）
public sealed record ResolvedPolicyReference
{
    public required string BundleId { get; init; }
    public required string BundleVersion { get; init; }
    public required string BundleContentHash { get; init; }
    public required long ActivationEpoch { get; init; }
}

// 新增：请求生命周期内的有效策略快照
public sealed record EffectivePolicySnapshot
{
    public required ResolvedPolicyReference Reference { get; init; }
    public required SafetyProfile Safety { get; init; }       // 不允许 override
    public required BudgetProfile Budget { get; init; }        // 已合并 BudgetOverride
    public required RoutingProfile Routing { get; init; }     // 已合并 RoutingOverride
    public required string FeatureSchemaVersion { get; init; }
    public string? RouterModelHash { get; init; }
    public string? RankerModelHash { get; init; }
    public required ContextScope ResolutionScope { get; init; }  // 与 Request.Scope 校验
}

public interface IResolvedPolicyProvider
{
    ValueTask<EffectivePolicySnapshot> ResolveAsync(
        ContextDecisionRuntimeRequest request,
        CancellationToken cancellationToken = default);
}
```

### 5.3 双轴语义（评审反馈 #3）

```csharp
// 业务用途（不废弃）
public enum ContextDecisionPurpose : byte
{
    Retrieval,
    Package,
    AgentContext
}

// 运行实现
public enum ContextDecisionRuntimeKind : byte
{
    Legacy,
    UnifiedV2
}

// ContextDecisionResult 同时记录两轴：
//   Purpose = ContextDecisionPurpose.Package
//   RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2
// 不再使用 ContextDecisionSource.Unified（维度混淆）
```

### 5.4 Runtime 请求（评审反馈 #4）

```csharp
public sealed record ContextDecisionRuntimeRequest
{
    public required string RequestId { get; init; }
    public required ContextScope Scope { get; init; }
    public required ContextDecisionPurpose Purpose { get; init; }
    public string? QueryText { get; init; }
    public int TokenBudget { get; init; }
    public int TopK { get; init; }
    // 仅用于 Replay / 测试 / 调用方显式注入 / 已有候选复用
    // 正常生产路径由 CandidateProviders 产出
    public IReadOnlyList<ContextCandidateEnvelope> SeedCandidates { get; init; }
        = Array.Empty<ContextCandidateEnvelope>();
}

public readonly record struct ContextScope(string WorkspaceId, string CollectionId);
```

### 5.5 Canonical Identity + Material（评审反馈 #5 + #6）

```csharp
public readonly record struct CanonicalCandidateKey(
    string WorkspaceId,
    string CollectionId,
    string EntityKind,
    string EntityId,
    string EntityVersion);

public sealed record ExpertOrigin(
    ExpertKind Expert,
    double Contribution,
    DateTimeOffset ObservedAt);

// Envelope 扩展（不破坏现有字段，只增不减）
public sealed record ContextCandidateEnvelope
{
    // ... 既有字段保留 ...
    public required CanonicalCandidateKey CanonicalKey { get; init; }
    public IReadOnlyList<ExpertOrigin> Origins { get; init; } = [];
    public IReadOnlyDictionary<ExpertKind, double> ExpertContributions { get; init; }
        = new Dictionary<ExpertKind, double>();
    public ResolvedPolicyReference? PolicyReference { get; init; }  // provenance
    // 不含 Content / AllocatedSection / AllocatedTokens
}

// Material sidecar（正文与决策分离）
public sealed record CandidateMaterial
{
    public required CanonicalCandidateKey Key { get; init; }
    public required string Content { get; init; }
    public required string NativeKind { get; init; }
    public IReadOnlyList<string> SourceRefs { get; init; } = [];
}

public sealed record CandidateWorkingSet
{
    public required IReadOnlyList<ContextCandidateEnvelope> Envelopes { get; init; }
    public required IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> Materials { get; init; }
}

public sealed record ExpertExecutionResult(
    IReadOnlyList<ContextCandidateEnvelope> Envelopes,
    IReadOnlyDictionary<CanonicalCandidateKey, CandidateMaterial> Materials);
```

**Merge 规则（强制）**：
- **Provenance**：`Origins` = union + stable dedup（按 ExpertKind）
- **Safety**：采用最严格状态（任一来源 blocked → 整体 blocked）
- **Mandatory**：只能由有权限的来源（Mandatory/Constraint Expert）提升 IsMandatory
- **Token**：采用统一 tokenizer 结果（不取 max/min）
- **Features**：保留 per-Expert contribution，不合并为单一值
- **同一实体不同版本**：不直接合并（EntityVersion 不同 → 不同 Key）
- **相同 EntityId 不同 EntityKind**：不得碰撞（Key 包含 EntityKind）

### 5.6 Feature Pipeline 纯转换（评审反馈 #7）

```csharp
public interface IFeaturePipeline
{
    // 纯转换：返回新 Envelope 列表，不修改输入（immutable record 友好）
    ValueTask<IReadOnlyList<ContextCandidateEnvelope>> EnrichAsync(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        FeaturePipelineContext context,
        CancellationToken cancellationToken = default);
}

public sealed record FeaturePipelineContext(
    EffectivePolicySnapshot Policy,
    CandidateAdaptationContext AdaptationContext);
```

### 5.7 Router + Provider

```csharp
public interface IRouter
{
    ValueTask<ExpertRoutingDecisionSet> RouteAsync(
        ContextDecisionRuntimeRequest request,
        EffectivePolicySnapshot snapshot,
        CancellationToken cancellationToken = default);
}

// Provider 能力目录（替代 no-op Expert 注册）
public interface IExpertCatalog
{
    IReadOnlySet<ExpertKind> AvailableExperts { get; }
}

public interface ICandidateProvider
{
    ExpertKind Kind { get; }
    ValueTask<ExpertExecutionResult> ExecuteAsync(
        CandidateProviderContext context,
        CancellationToken cancellationToken = default);
}

public sealed record CandidateProviderContext(
    ContextDecisionRuntimeRequest Request,
    EffectivePolicySnapshot Policy,
    ExpertRoutingDecision Routing,
    CandidateAdaptationContext AdaptationContext);

public enum ExpertKind : byte
{
    // 真正的 Candidate Experts（评审反馈 §二）
    Mandatory = 0,
    Constraint = 1,
    Lexical = 2,
    Semantic = 3,
    WorkingMemory = 4,
    StableMemory = 5,
    Graph = 6,
    Recency = 7,  // 枚举值保留，但默认不注册到 Catalog（Router disable + ReasonCode）
    // 不再定义 PackageShortTermSignal / PackageRecallSection / PackageExpansionDiagnostics
}
```

### 5.8 Early + Decision Gate 拆分（评审反馈 §三）

```csharp
// Early Admission Gate（Runtime 层，Feature Pipeline 之前）
public interface IEarlyAdmissionGate
{
    AdmissionResult Evaluate(ContextCandidateEnvelope envelope, EffectivePolicySnapshot snapshot);
}
// 检查：scope mismatch / superseded / archived / rejected / forbidden tag /
//       illegal evidence / hard lifecycle block

// Decision Safety Gate（Engine 层，Feature Pipeline 之后）
public interface ISafetyGate
{
    SafetyGateResult Evaluate(ContextCandidateEnvelope envelope, SafetyProfile profile);
}
// 检查：duplicate / required coverage / cross-candidate conflict / full evidence rules

public interface ILifecycleGate
{
    LifecycleGateResult Evaluate(ContextCandidateEnvelope envelope);
}

// 原则：Mandatory/Hard Constraint 免预算，不免 Safety/Lifecycle
```

### 5.9 Allocator 解耦（评审反馈 §四）

```csharp
public interface IGlobalAllocator
{
    AllocationResult Allocate(
        IReadOnlyList<ContextCandidateEnvelope> envelopes,
        EffectivePolicySnapshot snapshot);
}

public sealed record CandidateAllocationDecision
{
    public required CanonicalCandidateKey CandidateKey { get; init; }
    public required string Section { get; init; }
    public required int IncludedTokens { get; init; }
    public bool IsTruncated { get; init; }
    public required CandidateDecisionReasonCode ReasonCode { get; init; }
}

public sealed record AllocationResult(
    IReadOnlyList<ContextCandidateEnvelope> Selected,
    IReadOnlyList<ContextCandidateEnvelope> Dropped,
    IReadOnlyList<CandidateAllocationDecision> AllocationDecisions,
    ContextDecisionOutcomeSummary Outcome);

// Mandatory 超预算策略（评审反馈 §四）
public enum MandatoryOverflowPolicy
{
    FailClosed,                       // Agent/model context 硬窗口
    AllowOverflowWithDiagnostic,     // 普通 Package（默认）
    RejectLowestAuthorityMandatory
}
```

**Envelope 不再承载分配结果**：`AllocatedSection` / `AllocatedTokens` 不进入 Envelope，由独立的 `CandidateAllocationDecision` 承载，利于 Replay / counterfactual / 多预算方案比较。

---

## 6. Candidate Provider 划分（评审反馈 §二）

### 6.1 真正的 Candidate Experts

只保留 8 个真 Candidate Expert：

| ExpertKind | 当前实现来源 | V2 Provider |
|---|---|---|
| Mandatory | `MandatoryRecallChannelExecutor` | `MandatoryCandidateProvider` |
| Constraint | `MandatoryRecallChannelExecutor`（约束分支） | `ConstraintCandidateProvider` |
| Lexical | `ContextRecallChannelExecutor` | `LexicalCandidateProvider` |
| Semantic | `VectorRecallChannelExecutor` | `SemanticCandidateProvider` |
| WorkingMemory | `MemoryRecallChannelExecutor`（短期部分）+ `ShortTermSignalCollector` | `WorkingMemoryCandidateProvider` |
| StableMemory | `MemoryRecallChannelExecutor`（长期部分） | `StableMemoryCandidateProvider` |
| Graph | `RelationRecallChannelExecutor` | `GraphCandidateProvider` |
| Recency | （混在 MemoryRecall 内） | **不注册到 Catalog**（Router disable + ReasonCode=`expert-not-registered`） |

### 6.2 不作为 Candidate Expert 的部分

- **`RecallSectionCollector`** → 重构为 **section composition / selection**（消费已合并候选，不重新召回）
- **`ExpansionDiagnosticsCollector`** → 重构为 **诊断 / feature / evidence contributor**（不产出可选候选）
- **`ShortTermSignalCollector`** → 折叠进 `WorkingMemoryCandidateProvider`（不新增 Package 专用 Expert）

### 6.3 Package section 角色

Package section 不再触发独立召回，只作为：
- **`SectionHint`**：Provider 标记候选的建议 section
- **`AllocationDomain`**：Allocator 在阶段 9 按 `BudgetProfile.SectionRatios` 分配

---

## 7. Shadow 迁移与 Tee 模式（评审反馈 #5）

### 7.1 Tee 架构

```
   ContextDecisionRuntimeRequest
            │
            ▼
   ┌─────────────────────────────────────────┐
   │ Candidate Providers (单次执行)            │
   │ 产出 raw ExpertExecutionResult snapshot  │
   └─────────────────────────────────────────┘
            │
            ▼ Tee
   ┌────────────┴────────────┐
   ▼                         ▼
   Legacy decision        V2 decision
   (HybridContextRetriever (IContextDecisionRuntime
    / BasicContextPackageBuilder  → Engine → AllocationResult)
    老路径)                  
            │                         │
            ▼                         ▼
   Legacy Result          V2 Result + AllocationDecisions
            │                         │
            └──────────┬──────────────┘
                       ▼
              Parity Comparator
              (Hard parity + Diagnostic parity)
                       │
                       ▼
              DecisionExperimentPlane
              (sampled shadow / replay corpus / classifier)
```

### 7.2 Tee 不变量

- 候选捕获**只执行一次**：Provider 调用计数 = 1（每个启用的 Provider）
- Store 读取**只发生一次**：embedding / vector query / memory read 不重复
- Legacy 与 V2 消费**同一** `ExpertExecutionResult` snapshot（深拷贝或不可变引用）
- Shadow 样本与生产输入**严格一致**

### 7.3 Parity 双层（评审反馈 #七-4）

**Hard parity（必须零差异，阻断切换）**：
- selected CandidateId 集合
- selected 顺序
- mandatory coverage
- per-section 分配
- included tokens
- dropped reason class
- lifecycle/safety violation

**Diagnostic parity（仅告警，不阻断）**：
- feature vector
- score breakdown
- provenance
- intermediate ranking

---

## 8. rule-only 等价性保证

### 8.1 等价性定义

当 `RoutingProfile.EnableModelScoring=false` 时，对相同输入 `(request, snapshot)`：

- **Hard parity**：参见 §7.3
- **diversity extension point**：存在但禁用行为变更（rule-only convergence 阶段不引入 MMR / 跨 section diversity / learned allocation）

### 8.2 实现机制

- `IUtilityScorer` rule-only：`w_d=1.0, w_m=0.0`，`FinalScore = DeterministicScore`
- `IGlobalAllocator` rule-only：确定性排序（与现有 `ResultProjector` 排序键一致）+ per-section 配额；diversity extension point 调用但 no-op
- `IFeaturePipeline` 计算的特征在 rule-only 下**不参与计分**（仅记录到 `envelope.Features` 供 trace）
- Tee 影子执行作为运行时等价性回归门

### 8.3 例外（非语义性差异）

- `DecidedAt` 时间戳
- `RequestId` / 内部 trace ID
- `Outcome.Sections` 在 V2 有值（当前为 `Array.Empty<string>()`），baseline 比较时忽略

---

## 9. 已确认决策（评审反馈 #七）

| 问题 | 决策 |
|---|---|
| AdaptationContext.Scope 来源 | 从 Request 获取，但必须与 EffectivePolicySnapshot.ResolutionScope 校验一致；不一致 → fail-closed。统一值对象 `ContextScope(WorkspaceId, CollectionId)` |
| AgentContextProjector 输出 DTO | 复用已有 `AgentContextSnapshot`（R23），不新增 `AgentContextPackage`。前提：Projector 获得 `DecisionResult + AllocationPlan + CandidateMaterial sidecar` |
| Recency Expert | **不注册 no-op**。枚举值保留，Router 基于 `IExpertCatalog.AvailableExperts` 过滤，未注册 → disable + ReasonCode=`expert-not-registered` |
| Shadow 比较粒度 | 双层：Hard parity（阻断切换）+ Diagnostic parity（仅告警）。见 §7.3 |
| Diversity V1 | 仅 deterministic baseline allocator；MMR / 跨 section diversity / learned allocation 留到 V2.1。In scope 改为"提供 diversity extension point，但 rule-only convergence 阶段禁用行为变更" |

---

## 10. 与已有契约的关系

### 10.1 保留（不修改）

- `IContextDecisionEngine` + `DefaultContextDecisionEngine`（纯决策语义不变）
- `ResolvedPolicySnapshot`（既有轻量引用，被 `CandidateAdaptationContext` / `EvidenceRef` 使用）
- `IPolicyRegistry`（WS-A 已收口）
- `ContextPolicyBundle` / `BudgetProfile` / `RoutingProfile` / `SafetyProfile`
- `CandidateAdaptationContext`（WS-A 强制必传）
- `ContextCandidateEnvelope`（字段只增不减）
- `CandidateFeatureVector` / `CandidateUtilityScore` / `CandidateSafetyState`
- `ContextDecisionResult` / `ContextDecisionOutcomeSummary`（字段扩展：+ Purpose / RuntimeKind / AllocationDecisions / PolicyReference）
- `IResultProjector<TResult>` 接口
- `RetrievalResultProjector` / `PackageResultProjector`（实现保留）
- `ContextDecisionProjector`（trace 投影，纯只读）
- `AgentContextSnapshot`（R23，被 AgentContextProjector 复用）

### 10.2 替换

- `IRetrievalRouter` → `IRouter`（`DefaultRetrievalRouter` 算法搬进 `DefaultRouter`）
- `DefaultContextDecisionEngine` 内联的 safety/scoring/budget 逻辑 → 拆分为 `ISafetyGate` / `ILifecycleGate` / `IUtilityScorer` / `IGlobalAllocator`（Engine 内部调用这些组件）
- `BasicContextPackageBuilder` 内部 `ResultProjector` / `PackageBudgetProjector` 的**分配逻辑** → 移到 `IGlobalAllocator`

### 10.3 新增

- `IContextDecisionRuntime` + `ContextDecisionRuntimeRequest`
- `EffectivePolicySnapshot` + `ResolvedPolicyReference` + `IResolvedPolicyProvider`
- `ContextDecisionPurpose` + `ContextDecisionRuntimeKind` + `ContextScope`
- `CanonicalCandidateKey` + `ExpertOrigin` + `CandidateMaterial` + `CandidateWorkingSet` + `ExpertExecutionResult`
- `ICandidateProvider` + `CandidateProviderContext` + `IExpertCatalog`
- `ICanonicalCandidateMerger`
- `IFeaturePipeline` + `FeaturePipelineContext`（`EnrichAsync` 纯转换）
- `IEarlyAdmissionGate` + `ISafetyGate` + `ILifecycleGate`（拆分）
- `IUtilityScorer`
- `IGlobalAllocator` + `AllocationResult` + `CandidateAllocationDecision` + `MandatoryOverflowPolicy`
- `AgentContextProjector`
- `DecisionExperimentPlane`（长期保留，见 §11）

### 10.4 删除（B-5 阶段）

- `IRetrievalRouter`（合并进 `IRouter`）
- `BasicContextPackageBuilder` 内部 `ResultProjector` / `PackageBudgetProjector` 的分配逻辑
- 影子执行双返回路径（但保留 comparator / replay fixtures / experiment runner / sampled shadow，见 §11）

---

## 11. DecisionExperimentPlane（长期保留，评审反馈 #6）

B-5 不删除以下基础设施，转为长期 `DecisionExperimentPlane`：

- **parity comparator**：Hard + Diagnostic 双层比较器
- **sampled shadow runner**：按 workspace/task lineage 分层采样
- **golden replay corpus**：frozen 测试语料
- **difference classifier**：差异分类器

**后续用途**：
- Router 模型上线
- Allocator 策略变更
- Feature schema 升级
- Agent Kernel 回归
- Counterfactual 实验

`DecisionExperimentPlane` 是 R28-B 之后的长期资产，不属于"迁移代码"。

---

## 12. 阶段拆分（实现计划，评审反馈 #八）

WS-B 拆分为 5 个子阶段，每个子阶段独立可验证：

### B-1：Contracts correction（无行为变更）

**新增所有契约**：
- `IContextDecisionRuntime` + `ContextDecisionRuntimeRequest` + `ContextScope`
- `EffectivePolicySnapshot` + `ResolvedPolicyReference` + `IResolvedPolicyProvider`
- `ContextDecisionPurpose` + `ContextDecisionRuntimeKind`
- `CanonicalCandidateKey` + `ExpertOrigin` + `CandidateMaterial` + `CandidateWorkingSet` + `ExpertExecutionResult`
- `CandidateAllocationDecision` + `MandatoryOverflowPolicy`
- `ICandidateProvider` + `IExpertCatalog`
- `IFeaturePipeline`（`EnrichAsync`）
- `IEarlyAdmissionGate` / `ISafetyGate` / `ILifecycleGate`
- `IUtilityScorer` / `IGlobalAllocator` / `IRouter`
- `AgentContextProjector`（复用 `AgentContextSnapshot`）

**不改生产行为**。`DefaultContextDecisionEngine` 实现 `IContextDecisionEngine`（既有接口），新增契约仅有默认实现骨架。

**验收**：build 通过；PublicApi baseline 更新；全量测试通过；无运行时行为变更。

### B-2：Candidate capture + pure Runtime

- 按 §6 迁移 8 个 `ICandidateProvider`
- 实现 Tee：单次候选捕获，Legacy 与 V2 共享 snapshot
- 实现 `ICanonicalCandidateMerger`（按 `CanonicalCandidateKey` 合并 + Origins union）
- 实现 `IEarlyAdmissionGate`
- 实现 `IFeaturePipeline.EnrichAsync`（纯转换）
- 实现 `ISafetyGate` / `ILifecycleGate` / `IUtilityScorer` / `IGlobalAllocator`（rule-only 路径）
- Runtime 内部调用 Engine（Engine 保持纯决策）
- 影子执行 + Parity comparator（Hard + Diagnostic）

**硬边界测试**：
- `ShadowExecutionDoesNotDoubleReadStores`
- `CandidateProvidersExecuteAtMostOncePerRequest`
- `LegacyAndUnifiedConsumeSameCandidateSnapshot`
- `RouterMaskControlsActualExpertExecution`

**验收**：`PackageSectionAllocationMatchesFrozenBaseline` 通过；Hard parity 零 mismatch。

### B-3：Shadow Gate

**不只依赖"连续 100 次"**，需多维度 gate：

- frozen corpus 全量通过
- 每种 `ContextDecisionPurpose`（Retrieval / Package / AgentContext）都覆盖
- 零 Hard parity mismatch
- 线上 shadow 按 workspace/task lineage 分层采样
- latency / allocation / Store call 不回退
- 积累足够的边界场景（不仅按次数）

**验收**：Shadow Gate 报告全绿；B-4 切换前最后一道门。

### B-4：Authoritative cutover

**按顺序切换**（每次保留独立 kill switch）：

1. Retrieval（最低风险，候选集最简单）
2. Package（section allocation 复杂）
3. AgentContext（最新契约）

每次切换：
- `ContextDecisionRuntimeKind = UnifiedV2` 标记
- kill switch 可一键回退到 Legacy
- 切换后持续 shadow 监控

**验收**：§13 全部硬验收测试通过；生产 trace 显示 `RuntimeKind=UnifiedV2`。

### B-5：Legacy removal + ExperimentPlane 保留

**删除**：
- §10.4 列出的老契约
- `BasicContextPackageBuilder` 内部分配逻辑
- `IRetrievalRouter`
- 影子执行双返回路径

**保留**（转为 `DecisionExperimentPlane`）：
- parity comparator
- replay fixtures
- experiment runner
- sampled shadow 能力

**验收**：PublicApi baseline 反映删除；全量测试通过；build 无新增 Obsolete warning（除已知 `RecordCanaryAssignmentAsync`）。

---

## 13. R28 硬验收测试映射

| 测试名 | 验证边界 | 阶段 |
|---|---|---|
| `UnifiedRuntimeIsOnlyDecisionSource` | 主链不变量：所有决策经 Runtime | B-4 |
| `ProjectorsCannotChangeSelectedSet` | 硬边界 4.2 + 4.3 | B-1 |
| `RouterMaskControlsActualExpertExecution` | 硬边界 4.5 | B-2 |
| `PackageSectionAllocationMatchesFrozenBaseline` | 硬边界 4.4 + 4.6 | B-2 |
| `ResolvedPolicySnapshotRemainsStableDuringRequest` | 主链不变量：快照稳定 | B-1 |
| `ShadowExecutionDoesNotDoubleReadStores` | 硬边界 4.7 | B-2 |
| `CandidateProvidersExecuteAtMostOncePerRequest` | 硬边界 4.7 | B-2 |
| `LegacyAndUnifiedConsumeSameCandidateSnapshot` | 硬边界 4.7 | B-2 |
| `AdapterNotInvokedInsideRuntime` | 硬边界 4.1 | B-1 |
| `RetrievalSoftConstraintIsNotBudgetExempt` | ConstraintLevel 语义 | B-2（已有，保留） |
| `MergedConstraintDoesNotBecomeHardConstraint` | ConstraintLevel 合并不升硬 | B-2（已有，保留） |
| `AllAdapterPathsRequireAdaptationContext` | 硬边界 4.1 | WS-A 已完成 |
| `DroppedEnvelopePreservesWorkspaceAndProvenance` | dropped 作用域完整 | WS-A 已完成 |
| `PolicyActivationPinsBundleVersionAndHash` | Policy 版本固定 | WS-A 已完成 |
| `RegisteringNewBundleVersionDoesNotChangeActiveRequests` | CAS 激活隔离 | WS-A 已完成 |
| `UnconditionalActivationPathIsUnavailable` | ActivateAsync 已删除 | WS-A 已完成 |
| `CreatePipelineRunIsInsertOnly` | Pipeline insert-only | WS-A 已完成 |
| `SameTransitionIdIsAppliedExactlyOnce` | TransitionId 幂等 | WS-A 已完成 |
| `RetryAfterAmbiguousCommitDoesNotAdvanceTwice` | 重试幂等 | WS-A 已完成 |
| `CanaryAssignmentCommitsWithStageTransition` | Canary 原子提交 | WS-A 已完成 |
| `CancelledAgentRunProducesResumableCheckpoint` | Agent Kernel（WS-C） | WS-C |
| `ResumeDoesNotDuplicateCommittedToolResult` | Agent Kernel（WS-C） | WS-C |
| `UnknownSideEffectIsNotAutomaticallyReplayed` | Agent Kernel（WS-C） | WS-C |
| `BoundedStreamMaintainsMemoryCeiling` | Agent Kernel（WS-C） | WS-C |
| `ModelTransportFailureFallsBackAccordingToPolicy` | Agent Kernel（WS-C） | WS-C |
| `AgentFinalContextNeverExceedsTokenBudget` | Agent Kernel（WS-C） | WS-C |

---

## 14. 防退化规则

- **build**：不允许新增 error；PublicApi baseline 变更必须与本设计文档 §5/§10 一致。
- **test**：不允许新增失败；`PackageSectionAllocationMatchesFrozenBaseline` 在 B-2 通过后不允许回退。
- **行为**：rule-only 场景下，V2 主链输出与 frozen baseline Hard parity 零 mismatch（§8）。
- **契约**：硬边界 §4.1–4.7 由测试强制，任何 PR 违反硬边界必须被拒绝。
- **schema**：WS-B 不修改 PostgreSQL schema（schema v17 由 WS-A 冻结）。
- **trace**：`ContextDecisionProjector` 只读语义不变，`ContextDecisionRecord` 字段只增不减。
- **Engine 纯净**：`IContextDecisionEngine` 不依赖 Store / 不调用 Router / 不加载 Policy 的约束由反射测试强制。
- **Tee**：B-2 及之后，候选捕获必须单次执行（`CandidateProvidersExecuteAtMostOncePerRequest`）。
- **ExperimentPlane**：B-5 不得删除 parity comparator / replay fixtures / experiment runner / sampled shadow。

---

## 15. 最终结构

修正后，R28-B 从"将现有 Engine 扩大成唯一主链"变成更稳定的分层结构：

```
Unified Context Decision Runtime
    ├── I/O orchestration          (IContextDecisionRuntime)
    ├── pure Decision Engine       (IContextDecisionEngine, 既有语义不变)
    ├── immutable Policy Snapshot  (EffectivePolicySnapshot + ResolvedPolicyReference)
    ├── canonical Candidate Working Set  (CanonicalCandidateKey + Material sidecar)
    ├── pure Projectors            (Retrieval / Package / AgentContext)
    └── DecisionExperimentPlane    (长期 parity / replay / counterfactual)
```

这也更适合作为后续专属高性能 .NET Agent Kernel（Workstream C）的基础。

---

## 16. 参考

- R28 规划原文：本会话用户消息（2026-07-22）
- R28-B 评审反馈：本会话用户消息（2026-07-22，7 个阻断问题 + §二~§八）
- WS-A 收口：commit `5b04a05`（schema v17，PostgresPolicyRegistry）
- 既有契约：
  - [IContextDecisionEngine](file:///d:/Users/Ye_Luo/AppData/Local/Context/src/ContextCore.Abstractions/Contracts/DecisionEngineInterfaceContracts.cs#L193-L207)（纯决策内核，语义保留）
  - [ResolvedPolicySnapshot](file:///d:/Users/Ye_Luo/AppData/Local/Context/src/ContextCore.Abstractions/Contracts/DecisionEngineContracts.cs#L394-L404)（既有轻量引用，不重定义）
  - [DefaultContextDecisionEngine](file:///d:/Users/Ye_Luo/AppData/Local/Context/src/ContextCore.Core/Services/DecisionEngine/DefaultContextDecisionEngine.cs#L54)（实现保留）
- 相关设计文档：
  - [strategy-scoring-design.md](file:///d:/Users/Ye_Luo/AppData/Local/Context/docs/strategy-scoring-design.md)
  - [retrieval-orchestration-baseline-v1.md](file:///d:/Users/Ye_Luo/AppData/Local/Context/docs/retrieval-orchestration-baseline-v1.md)
  - [router-intent-boundaries.md](file:///d:/Users/Ye_Luo/AppData/Local/Context/docs/router-intent-boundaries.md)
  - [router-intent-shadow-freeze.md](file:///d:/Users/Ye_Luo/AppData/Local/Context/docs/router-intent-shadow-freeze.md)
  - [storage-boundary.md](file:///d:/Users/Ye_Luo/AppData/Local/Context/docs/storage-boundary.md)
