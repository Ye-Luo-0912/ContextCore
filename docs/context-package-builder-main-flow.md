# Context Package Builder 主流程

生成时间：2026-05-25  
对应阶段：A1 Context Package Builder 升级为核心主干

## 1. 目标

Context Package Builder 的目标不是返回 TopK 片段，而是在给定任务下构建：

```text
正确、分层、可追溯、低噪音、符合 token 预算的最小充分上下文包。
```

第一版主干采用可解释、低开销规则实现，后续可在同一流程内替换更强的语义评估器或模型辅助模块。

## 2. 标准流程

```text
Current Input
↓
Recent Filter
↓
Anchor Extraction
↓
Working Memory Recall
↓
Graph Expansion
↓
Stable Memory Injection
↓
Constraint Merge
↓
Conflict / Superseded Check
↓
Token Budget Packing
↓
Context Package Output
```

## 3. 已落地的 A1 起点

### Recent Filter

当前实现：`RecentContextFilter`

职责：

- 从最近原始上下文中筛选当前相关信息。
- 保留当前任务、运行时状态、强时序信号。
- 排除明显无关支线。
- 输出 `RecentContextItem`，包含：
  - `content`
  - `sourceTurnId`
  - `relevance`
  - `recencyWeight`
  - `reason`
  - `excludeReason`

性能约束：

- 不调用 LLM。
- 不做向量检索。
- 单条内容只扫描前 1024 个字符。
- 查询词最多取 12 个。
- PackageBuilder 查询最近原始上下文时设置候选上限，避免无界扫描。

### Anchor Extraction

当前实现：`ContextAnchorExtractor`

职责：

- 从当前请求、元数据、query、短期筛选结果中提取 anchors。
- 输出 `ContextAnchor`：

```csharp
public sealed record ContextAnchor(
    string Name,
    AnchorType Type,
    double Weight,
    string Source,
    IReadOnlyList<string> Aliases
);
```

当前支持的 `AnchorType`：

- `Entity`
- `Topic`
- `Task`
- `Constraint`
- `Intent`
- `Project`
- `TimeRange`
- `Mode`

性能约束：

- 不做深度语义解析。
- anchors 最多保留 32 个。
- recent anchors 最多从 8 条短期上下文提取。
- 相同名称和类型自动去重。

### Recall Signal Policy

当前实现：`ContextRecallSignalPolicy`

职责：

- 统一判断哪些 anchor 可以参与内容相关性打分。
- 将 workspace / collection 作为检索边界，而不是内容相关性信号。
- 从 working memory 提取有限辅助信号，供 stable memory injection 使用。
- 统一判断长期稳定记忆类别，例如偏好、项目背景、安全、规则、性能、测试、世界观等。
- 为 stable memory injection 输出可解释分数和“是否命中当前任务信号”标记。

性能约束：

- 不调用 LLM。
- working memory 辅助信号最多从 8 条记忆中提取，信号总数上限为 64。
- stable memory 的 working signal 加分有上限，避免长期层覆盖当前输入和中期工作记忆。

### Retrieval Plan

当前实现：`RetrievalPlanner`

职责：

- 基于短期快照生成 `RetrievalPlan`，作为 package build 和 hybrid retrieval 之间的意图载体。
- 当 `HybridContextRetriever` 未收到外部 Plan 时，使用同一个 `ContextAnchorExtractor` 从 `ContextRetrievalRequest` 派生 anchors。
- 保持 Builder 和 Retriever 的 mode、tag、type、metadata、query 解析规则一致。

性能约束：

- 不调用 LLM。
- 不额外访问存储。
- 只做轻量规则分类，输出 Primary / Support / Negative / Audit / Conflict 锚点角色。

### Retrieval Plan Execution Policy

当前实现：`RetrievalPlanExecutionPolicy`

职责：

- 集中解释 `RetrievalPlan` 在混合检索执行阶段的过滤和加权语义。
- 决定审计/冲突计划是否允许废弃条目进入候选集。
- 决定是否跳过 Stable Memory 查询。
- 计算 Working Memory 与 PrimaryAnchor 的匹配奖励。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 只做字符串匹配和常量上限加分，避免检索主流程出现额外 I/O。

### Retrieval Candidate Policy

当前实现：`RetrievalCandidatePolicy`

职责：

- 集中处理混合检索候选项的生命周期过滤。
- 集中处理关键词召回、向量召回和关系扩展的基础评分。
- 集中处理记忆候选的中文轻量查询匹配。
- 保持 `HybridContextRetriever` 聚焦阶段编排、存储访问和 trace 生成。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 使用常量权重、字符串匹配和有界分数计算，避免在候选层引入额外 I/O。

### Retrieval Channel Result

当前实现：`RetrievalChannelResult`

职责：

- 统一承载 mandatory、keyword、memory、vector、relation 通道的输出。
- 为每个通道提供统一的候选列表、阶段候选数和 metadata。
- 让 `HybridContextRetriever` 只消费统一结果，而不感知各通道内部构造细节。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 只做轻量对象封装，不改变召回分数和过滤语义。

### Retrieval Channel Context

当前实现：`RetrievalChannelContext`

职责：

- 统一承载单次检索过程中传给各通道执行器的运行时上下文。
- 提供 request、plan、共享 metadata、当前候选集、queryText 和 candidateTake。
- 让 executor 不再直接依赖 `HybridContextRetriever` 的局部变量组织方式。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 只做轻量上下文封装和派生字段缓存。

### Retrieval Channel Executor

当前实现：`IRetrievalChannelExecutor`

职责：

- 定义统一的召回通道执行接口。
- 让 Context / Memory / Vector / Relation 四类召回都按统一协议返回 `RetrievalChannelResult`。
- 让 `HybridContextRetriever` 只做 orchestration，不再保存每个通道的执行细节。

性能约束：

- 不限制具体执行器内部实现。
- 要求保持阶段结果结构一致，避免为每个通道引入额外包装成本。

### Recall Channel Executors

当前实现：`ContextRecallChannelExecutor`、`MemoryRecallChannelExecutor`、`VectorRecallChannelExecutor`、`RelationRecallChannelExecutor`

职责：

- `ContextRecallChannelExecutor`：组织 context keyword recall。
- `MemoryRecallChannelExecutor`：组织 working/stable memory recall。
- `VectorRecallChannelExecutor`：包装现有向量查询路径；disabled 时返回 empty result，unavailable 时返回 diagnostic。
- `RelationRecallChannelExecutor`：包装 relation recall，内部继续使用 `RelationFrontierBuilder` 和 `RelationExpansionService`。

性能约束：

- 不改变既有 scoring 公式。
- 不改变 lifecycle policy。
- 不改变 relation expansion 语义。

### Retrieval Trace Assembler

当前实现：`RetrievalTraceAssembler`

职责：

- 组装 retrieval trace。
- 保留 stage metadata、selected、dropped 和 diagnostics。
- 不参与 scoring、filtering 或 packing。

### Retrieval Result Assembler

当前实现：`RetrievalResultAssembler`

职责：

- 组装最终 retrieval result。
- 保留 selected、excluded、usage、trace 和 candidate metadata。
- 不参与 scoring、filtering 或 packing。

### Retrieval Candidate Builder

当前实现：`RetrievalCandidateBuilder`

职责：

- 统一从 `ContextItem`、`MemoryItem`、`RelationTarget` 构建最终 `RetrievalCandidate`。
- 聚合同一候选来自多个通道的 reasons、channel sources、relation paths、matched tokens / anchors。
- 通过 metadata 保留 `alsoReferencedBy` 和 `scoreBreakdown`。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 只做字符串集合合并和 metadata 序列化。

### Retrieval Candidate Accumulator

当前实现：`RetrievalCandidateAccumulator`

职责：

- 统一处理候选去重和增量合并。
- 按 `kind + sourceId` 维护唯一候选。
- 将多个通道的命中收敛到同一个 builder，避免 Retriever 内部散落合并逻辑。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 仅使用字典去重和线性合并。

### Input Pipeline

当前实现：`ContextInputCommand`、`ContextInputNormalizer`、`ContextInputValidator`、`ContextInputHasher`、`ContextInputSequencer`、`ContextInputIngestionService`

职责：

- 统一输入命令模型。
- 在持久化前完成标准化、校验、contentHash、sequenceId 和幂等检测。
- 旧 `ContextItem` ingest 先适配到 `ContextInputCommand`，保留兼容层。
- `POST /api/context/ingest` 作为推荐业务入口，显式支持 `ContextInputCommand`。
- `POST /api/admin/ingest` 作为管理/调试入口，走同一条输入 pipeline。
- 旧 `ContextItem` 请求体仅作为兼容保留，不再是推荐的长期入口。

性能约束：

- 同一 `sourceRef + contentHash` 去重。
- 不同 `sourceRef + 相同 contentHash` 允许创建新条目。
- `sequenceId` 在单进程内按 workspace + collection 单调递增。

### Context Object Resolver

当前实现：`IContextObjectResolver`、`DefaultContextObjectResolver`

职责：

- 统一解析 relation target 对应的 `ContextItem` 或 `MemoryItem`。
- 支持单条 `ResolveAsync` 和批量 `ResolveManyAsync`。
- 找不到目标时返回诊断信息，而不是直接抛异常。
- 不负责生命周期过滤、评分和 section 策略。

性能约束：

- 不调用 LLM。
- 批量解析保持输入顺序，不改变 target 解析优先级（ContextItem 优先，MemoryItem 兜底）。

### Relation Frontier Builder

当前实现：`RelationFrontierBuilder`

职责：

- 从当前候选集中选择可用于关系扩展的 seed。
- 支持 `ContextItem` 和 `MemoryItem` 作为 seed。
- 按现有 lifecycle 语义过滤 rejected / deprecated / superseded seed。
- 输出 `maxDepth`、`maxFanout`、允许关系类型和初始 frontier。

性能约束：

- 不访问 relation store。
- 不调用 LLM。
- 只做候选过滤、排序和数量裁剪。

### Relation Expansion Service

当前实现：`RelationExpansionService`

职责：

- 根据 frontier 查询 relation store。
- 使用 `IContextObjectResolver` 解析 target。
- 构造统一的 `RetrievalChannelResult`。
- 保留 `relationPaths`、`scoreBreakdown` 和 unresolved target diagnostics。
- 保持现有 relation scoring 和 relation traversal 语义不变。

性能约束：

- 不调用 LLM。
- 批量解析当前 frontier 的 target，避免每条 relation 单独解析。
- 继续保留 visited node / edge 去重和 frontier fanout 限制。

### Retrieval Packing Policy

当前实现：`RetrievalPackingPolicy`

职责：

- 合并主召回通道和仅关系扩展通道的候选项。
- 统一处理强制项优先、分数排序和关系独有候选预留。
- 在 `TopK` 和 token budget 约束下输出选中/丢弃决策。
- 保持 `HybridContextRetriever` 聚焦召回阶段，而不是结果组装和预算裁剪。

性能约束：

- 不访问存储。
- 不调用 LLM。
- 只做数组排序、集合去重和线性预算扫描。

### Graph Expansion

当前实现：`BasicContextPackageBuilder`、`HybridContextRetriever`

职责：

- 基于候选节点沿上下文关系图做 source -> target 扩展。
- `HybridContextRetriever` 支持 `RelationExpansionDepth` 控制跳数，默认 1 跳。
- `HybridContextRetriever` 支持 `AllowedRelationTypes` 白名单；为空表示不限制类型。
- Package Builder 路径继续通过 policy metadata 控制关系扩展深度、节点上限和最低置信度。

性能约束：

- 不调用 LLM。
- Hybrid Retrieval 的关系扩展深度上限为 3。
- 每层 frontier 受 `CandidateTake` 限制，并记录已访问节点和边，避免循环扩展。
- 二跳及以上关系分数递减，避免远距离关系覆盖直接召回结果。

## 4. 当前输出

PackageBuilder policy 模式当前会：

- 使用 Recent Filter 构建 `recent_context`。
- 将被排除的短期上下文写入 `DroppedItems`，并保留排除原因。
- 将 anchors 写入 package metadata：
  - `anchor.count`
  - `anchor.names`
  - `anchor.types`

这些字段用于 ControlRoom 预览、trace 分析和后续 Working Memory Recall / Graph Expansion。

## 5. 后续待补

下一步继续推进：

- Working Memory Recall 基于 anchors 召回中期记忆。
- Graph Expansion 继续补充 relation type 语义策略和真实语料评测。
- Stable Memory Injection 遵守“长期记忆只补充，不覆盖当前输入和中期 active 状态”。
- Constraint Merge 独立构建 constraints section 并处理优先级。
- Conflict / Superseded Check 输出 `uncertainties` 和 `excluded` section。
- Token Budget Packing 输出 token waste 报告。
