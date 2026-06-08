# 短期记忆 Promotion 策略

## 1. 基本原则

ContextCore 的短期对话默认只作为运行时上下文参与本次 Context Package 构建，不默认持久化，不默认向量化。

这一约束的目标是避免把一次性对话、临时尝试、过程噪音写入中期或长期记忆层，从而降低存储膨胀、检索污染和后续上下文漂移风险。

## 2. 层级边界

| 层级 | 默认行为 | 写入条件 |
|---|---|---|
| 短期上下文 | 只在本次请求中筛选、排序和裁剪 | 原始上下文可进入 `recent_context`，但不等于记忆入库 |
| 工作记忆 | 不由短期对话自动生成 | 调用方显式写入 `IWorkingMemoryService.AddAsync` |
| 稳定记忆 | 不由短期对话自动生成 | 经过 Promotion 判断、审核或明确服务调用后写入 |
| 向量索引 | 不对短期对话默认生成 | 进入可索引层后，再由 Embedding 作业显式处理 |

## 3. API 边界

- `IContextPackageBuilder.BuildAsync` / `BuildDetailedAsync` 只负责读取、筛选、排序、预算裁剪和组包。
- `recent_context` 是运行时筛选结果，不代表中期或长期记忆。
- `IWorkingMemoryService.AddAsync` 表示调用方明确决定写入工作记忆。
- `IContextRuntimeOperations.PromoteMemoryAsync` 表示显式提升到目标记忆层，并需要记录来源、原因、置信度和目标层。
- `IEmbeddingJobService` / `IVectorStore` 只应处理已经被明确纳入可索引范围的内容，不应由短期对话构建过程隐式触发。

## 4. 性能与质量要求

- Context Package 构建阶段不得因为短期内容自动产生额外写请求。
- 构建阶段不得因为短期内容自动触发 Embedding 调用或向量写入。
- Promotion 判断应优先使用结构化规则和轻量评分，避免在热路径中进行不可控的模型调用。
- 后续如果引入自动 Promotion 候选生成，必须先进入 candidate/review 状态，不能直接污染稳定层。

## 5. Promotion 条件

### 5.1 可提升到中期工作记忆

以下内容可以提升到中期工作记忆层，默认目标层为 `ContextMemoryLayer.Working`：

- 新的架构原则；
- 阶段性结论；
- 任务状态变化；
- 方案被否决；
- 约束新增或变更；
- 当前项目路线更新；
- 自动化流程进入完成、阻塞或失败状态；
- 小说剧情线、人物状态、伏笔发生变化。

### 5.2 可提升到长期稳定记忆

以下内容可以提升到长期稳定记忆层，默认目标层为 `ContextMemoryLayer.Stable`：

- 用户明确长期偏好；
- 项目长期定位；
- 长期稳定约束；
- 跨场景通用规则；
- 多次重复出现并稳定成立的模式；
- 已验证事实或领域知识。

### 5.3 不应提升

以下内容默认不应提升；如果同时命中提升规则和禁止提升规则，禁止提升优先：

- 普通寒暄；
- 临时情绪；
- 重复解释；
- 无后续价值支线；
- 已被后续覆盖的表达修正；
- 明显一次性上下文；
- 临时猜测、未验证结论或待确认信息。

## 6. 当前实现状态

- `BasicContextPackageBuilder` 只读取原始上下文、记忆、约束、全局上下文和关系，不接收 `IVectorStore`，因此不会在组包阶段写入向量库。
- `BasicWorkingMemoryService.AddAsync` 是显式写入工作记忆的 API，不会由 `recent_context` 自动调用。
- `BasicPromotionPolicyEvaluator` 已提供轻量、可解释、只读的 Promotion 条件评估能力。
- `PromotionCandidateStatus` 已定义 candidate、accepted、rejected、needs_review、superseded 对应状态。
- `BasicPromotionCandidateFactory` 已能把评估结果转换为 Review 候选项，但尚未持久化。
- `IPromotionCandidateStore` 已提供候选项保存、查询和状态更新能力，FileSystem 与 InMemory 后端已实现。
- ControlRoom 已提供 `promotion list/show/accept/reject/deprecate/explain` 审核命令。
- `ContextPromotionRecord` 已记录 source、reason、confidence、reviewer、timestamp（`CreatedAt`）和 target layer。
- `docs/eval/promotion-eval-samples.json` 已提供短期内容 Promotion Eval 初始样本集。
- `PromotionEvalRunner` 已能输出正确提升率、错误提升率、漏提升率、长期层污染率和 needs_review 比例。
