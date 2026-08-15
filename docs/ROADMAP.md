# ContextCore 高召回、高准确率长期路线图

> 基线：2026-08-15，HEAD `4f60254b`。LR-0A..LR-7E 全部工作包已按 §12 协议执行完毕并验证（全量测试 4222 通过 / 6 跳过 / 0 失败；容器构建与冒烟通过），已推送 origin/main。本文仍是唯一活动路线；下一轮工作包从 §2 阶段与 §1.2/§1.3 缺口中指定。现行行为以 [`LIVE_PATH.md`](LIVE_PATH.md)、源码和测试为准。已完成阶段不保留单独文档，细节从 Git 历史查询。

## 0. 北极星目标

ContextCore 的长期目标是：**在给定 token、时延和成本预算内，稳定找回当前任务真正需要的证据，并把最相关、可信、可用的内容准确排在前面，最终提高 Agent/LLM 的任务成功率。**

自学习、图检索、向量检索、重排、缓存、批处理和代码重构都只是实现这个目标的手段，不是独立目标：

- 自学习只有在反馈可信、离线评测稳定、可回放、可回滚时才进入主链；
- 高召回不能靠无限扩大候选集，高准确也不能靠过早截断关键证据；核心指标必须带 `K`、token budget、时延和成本；
- 性能优化不得以漏召回为代价，精简代码不得删除尚未完成质量归因的能力；
- 代码量、Public API、DTO 和 DI 数量是维护性指标，优先级低于质量、安全和持久化正确性；
- 无生产消费者、无质量贡献、长期关闭的能力最终应删除，而不是永久保留成“以后也许有用”的实验代码。

这里的“准确率”同时包含检索结果的精度/排序质量，以及最终答案或行动的正确性。目标层级为：

1. **不可交换的硬约束**：权限、租户、排除、生命周期、时效性、引用与持久化语义不回退；
2. **最终结果**：任务完成率、答案/行动正确率、工具调用成功率；
3. **检索质量**：Required-Evidence Recall@K、Recall@TokenBudget、Precision@K、MRR/nDCG、关键证据漏失率；
4. **效率**：p50/p95/p99、首包时间、embedding/数据库调用数、token 与内存成本；
5. **工程性**：生产 LOC、Public API、DTO/mapping、DI descriptors、测试重复度和变更复杂度。

任何工作包都必须说明它影响哪一层指标。只减少代码、但无法保护或改善更高层指标的“大重构”，不进入主线。

## 1. 当前证据与缺口

### 1.1 工程规模

| 项目/指标 | 当前值 |
| --- | ---: |
| `ContextCore.Core` | 239 文件 / 81,789 行 |
| `ContextCore.Abstractions` | 110 文件 / 34,319 行 |
| `ContextCore.Storage.Postgres` | 110 文件 / 32,240 行 |
| `ContextCore.Service` | 70 文件 / 26,786 行 |
| 单元、Service、Integration 测试 | 约 152,700 行 |
| Abstractions Public API baseline | 12,966 行（LR-7A 删除 10 个零引用类型后重生成） |
| 组合根 DI 注册（按 profile） | Development ≈ 351 / SingleNode ≈ 356 / ProductionHA ≈ 361（预算 390/395/400） |
| Hosted service 注册 | AddHostedService 28 处（全 profile 合计），逐 profile 组合由 R29H 测试锁定 |
| SDK 固定 | `global.json` 固定 .NET 10.0.301（rollForward=latestFeature，禁止 preview） |

最大维护热点：

- `UnifiedRuntimeDefaults.cs`：4,481 行、18 个运行时类型；
- `PostgresMigrationRunner.cs`：3,568 行，且是近期高频修改文件；
- `AgentRunActor.cs`：626 行（LR-6A 已拆出 Recovery/ContextModel/ToolDispatch/EventBuffer/ExecutionState 协作者，低于 2,200 行目标）；
- `AgentRunContracts.cs`：3,181 行，仍是 AgentRun 区域最高 churn 文件；
- Service endpoints 合计约 11,354 行；`CoreExtensions.cs` 1,216 行。

这些数字用于判断责任集中度和变化成本。拆文件、改名、搬 namespace 不算精简。

### 1.2 已知性能证据

原始基线见 [`../benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`](../benchmarks/results/MULTIQUERY_RECALL_BASELINE.md) 与同目录 CSV：

- FileSystem lexical 在 q=8 时约 1.5–2.2 秒/op，逐问句重复扫描和 materialize；
- semantic/combined 的 embedding 与 vector search 都是 q 次；
- q=8、TopK=100 时 semantic 合并最多 800 个候选；
- 当前基线没有 Postgres；
- async 路径使用 `GetAllocatedBytesForCurrentThread`，出现负值，分配数据不可信。

因此近期性能方向明确是批处理和单次读取，但必须先修正测量，并用质量夹具证明结果集合和排序没有意外变化。

### 1.3 最大产品缺口

当前已有大量行为测试和召回夹具，但还没有统一回答这些问题的质量系统：

- 一次请求真正需要哪些证据，哪些只是可选相关项；
- 哪个通道贡献了唯一有效候选，候选在哪个阶段丢失；
- 召回结果在 token budget 下是否仍保留关键证据；
- 工具成功、重试、用户纠正与检索结果之间如何归因；
- Learning/Adaptive 的改变究竟提高了任务结果，还是只改变内部得分；
- 离线提升能否在 shadow/canary 中复现并安全回滚。

在这些问题可测之前，不应打开 Active Learning，也不应大规模删除 Learning/Evolution 相关代码。

### 1.4 已知结构债务

已解决（LR-0..LR-7 收敛）：

- obsolete 公共类型（`IContextRetrievalAdapter`、`IShadowRetrievalAdapter`、`IModelRegistry`、`IRetrievalRouter` 等）已删除（LR-7A）；
- Service → ControlRoom 直接依赖已解除，备份能力迁入 `ContextCore.Storage.Postgres.Backup`（LR-7B）；
- Learning/Evolution/DecisionExperiment/旧 VectorIndex/ControlRoom 已逐项「正式支持并隔离」，默认关闭能力不启动 worker、不创建重对象（LR-7C）；
- 报告 Markdown 回流已由 `artifacts/` 忽略 + CI 门阻断（LR-0B）。

仍保留：

- `AgentRunContracts.cs` 3,181 行仍为 AgentRun 区域最高 churn 文件；
- `UnifiedRuntimeDefaults.cs` 4,481 行、18 个运行时类型仍集中；
- Service endpoints 合计约 11,354 行仍厚，未达 LR-7B「变薄」目标；
- eval/learning/vector/foundation 历史证据 JSON 仍被个别测试与 SourceRefs 提及（机器契约，删除前需同步测试）。

## 2. 长期阶段

阶段可以在时间上部分重叠，但数据门不能跳过：

| 阶段 | 建议跨度 | 主结果 |
| --- | --- | --- |
| LR-0 基线治理 | 1–2 个迭代 | 构建、测试、性能和质量口径可复现 |
| LR-1 质量评测与可观测 | 2–4 个迭代 | 能量化“召回了什么、漏在哪里、结果是否有用” |
| LR-2 高效召回覆盖 | 4–8 个迭代 | 扩大有效覆盖并消除多问句线性放大 |
| LR-3 排序准确与预算分配 | 6–12 个迭代 | 在固定预算内把关键证据稳定排在前面 |
| LR-4 反馈数据闭环 | 8–16 个迭代 | 线上结果转成可审计、可回放的训练/评测数据 |
| LR-5 受控自学习 | 12–24 个迭代 | 离线、shadow、canary、active 的安全学习闭环 |
| LR-6 可靠性与规模化 | 12–24 个迭代 | Agent Run、存储、多节点和迁移可长期演进 |
| LR-7 主版本收敛 | 18 个迭代以上 | 公共 API、DTO、可选能力和默认宿主显著缩小 |

依赖关系：

```text
LR-0 可复现基线
  └─→ LR-1 质量评测/归因
        ├─→ LR-2 召回覆盖与性能
        │     └─→ LR-3 排序/预算准确率
        └─→ LR-4 反馈数据闭环
              └─→ LR-5 受控自学习

LR-6 可靠性/规模化从 LR-1 后持续推进，为 LR-2…LR-5 托底。
LR-7 只在质量贡献和消费者清点完成后做删除与主版本边界调整。
```

## 3. LR-0：可复现基线（现在开始）

### LR-0A 工作树收口

- 在干净 checkout 上复跑 full unit、Service、Integration 可运行集合；
- 将已完成实现、文档清理和本路线保持为可独立审阅的变更边界；
- 删除根目录 `diffcheck.txt`、`fullstatus.txt`、`gitstatus_*.txt` 等临时文件；
- 记录 HEAD、SDK、OS、测试数量和跳过原因，不沿用旧数字；
- 由用户/主 Agent 决定提交，执行 Agent 不自行提交。

验收：`git diff --check` 通过；无不明临时文件；失败/跳过有代码级原因。

### LR-0B 生成物不回流

- Markdown/评测报告默认写到被忽略的 `artifacts/`；机器契约只保留必要 JSON/CSV；
- 修改脚本和 Evaluation 默认输出路径，避免再次生成已删除的 gate/freeze/report；
- 删除或改写依赖旧文档存在性的测试；
- 清理 foundation JSON、SourceRefs、源码注释中的失效文档路径；
- CI 阻止活动文档白名单外的新 Markdown 回流。

验收：运行评测/生成脚本后 `git status` 不新增 Markdown；活动文档链接无失效。

### LR-0C SDK 与依赖可复现

- 增加 `global.json`，固定受支持的 .NET 10 SDK 和 roll-forward 策略；
- CI 与本机使用同一 SDK；禁止用 .NET 11 preview 结果作为发布基线；
- benchmark 记录 CPU、GC、SDK、commit、数据规模和 provider 配置。

验收：新环境按单一命令 restore/build/test；SDK 漂移明确失败或告警。

### LR-0D 性能测量可信度

- 用 BenchmarkDotNet、EventPipe 或全局 allocation counter 替换 async 线程分配统计；
- 增加 Postgres、连接池、预热、数据库版本和数据规模维度；
- 分离冷启动、热缓存和稳定态；
- 基线只提交 runner、必要原始数据和简短当前结论。

验收：分配无负值；同机重复三次偏差可解释；Postgres roundtrip 与连接池占用可观测。

### LR-0E 质量指标契约

先定义而不是先调参：

- `RequiredEvidenceIds`：完成任务不可缺少的证据；
- `RelevantEvidenceIds`：有帮助但非必需的证据，可带相关等级；
- `Forbidden/ExcludedIds`：不应出现的证据；
- `Recall@K`、`Recall@TokenBudget`、`Precision@K`、MRR/nDCG 和关键证据漏失率；
- task/tool outcome、重试次数、人工纠正只作为结果信号，不拿 `FinalScore` 充当质量标签。

验收：指标公式、空集合语义、聚合方式、切片维度和误差区间固定；同一输入重复执行得到一致结论。

## 4. LR-1：质量评测与可观测

### LR-1A 分层评测集

建立 train/dev/test 隔离和版本化数据集，至少覆盖：

- 精确实体与关键词；
- 同义改写、语义匹配和 hard negatives；
- 多问句、工具观察、忘掉再找回；
- 生命周期、时效性、排除与权限边界；
- 图关系、多跳证据和证据冲突；
- FileSystem/Postgres/provider parity。

测试集不得被调参或训练直接读取；每条样本记录来源、期望证据、标注理由和版本。

### LR-1B 候选流归因

为一次请求记录内部诊断：query → channel → candidate → merge → gate → score/rank → allocate → package。诊断必须可采样、可关闭，不泄露正文或敏感数据。

至少回答：

- 哪个通道产生了唯一有效命中；
- 候选是未生成、未召回、被 gate 丢弃、排序过低还是预算裁掉；
- 重复候选、跨通道分数和 selected hydration 花在哪里；
- held/excluded/required 的语义是否被破坏。

### LR-1C 线上结果信号

- Agent Run 记录工具成功/失败、重试、纠正、终态和所用证据 ID；
- HTTP 调用只采集明确提供的反馈，不从点击或内部 score 猜标签；
- 建立隐私、租户隔离、保留期和删除策略；
- 质量 dashboard 以数据集/租户/通道/版本切片，不能只看全局平均。

### LR-1D 质量回归门

- PR 跑小型确定性套件；夜间跑完整质量、Postgres 和多节点套件；
- 硬约束任何回退均阻断；质量指标使用样本数和置信区间，不用单个平均数拍板；
- 性能提升若让 Required-Evidence Recall 或 Recall@TokenBudget 回退，不合入；
- 每个阶段保存机器可读基线，不再生成阶段报告文档。

阶段出口：能够对一次失败给出可复现的“漏失位置”，并以固定基线判断优化是否真实有效。

## 5. LR-2：高效召回覆盖

### LR-2A 请求内去重与批量 embedding

`EmbeddingRequest.Inputs` 已支持批量。`SemanticCandidateProvider` 先规范化、去重 QueryTexts，再一次 `EmbedAsync` 生成全部向量并按 input ID 映射。

验收：q=8 embedding 调用从 8 降到 1；空白/重复问句不调用；模型、instruction、顺序和错误语义不变；Recall@K 不下降；semantic p95 至少下降 30%。

### LR-2B 多问句 lexical 单次读取

- 增加窄的多问句 store capability，不能复用只按 ID 的 `IContextStoreBatchLookup` 冒充；
- FileSystem 在一次 snapshot 内完成全部问句过滤，只 materialize 合并后的候选；
- InMemory 单次枚举；Postgres 用单条 CTE/LATERAL 保留 per-query TopK 与 refs 语义；
- 不用并行 q 次 `QueryAsync` 放大文件 I/O 或连接池。

验收：FileSystem q=8 combined p95 至少下降 60%，目标低于 500 ms；Postgres lexical roundtrip 从 q 降到 1；Required-Evidence Recall、结果集合和最高分合并规则不变。

### LR-2C 批量 vector search

先测批量 embedding 后的剩余占比。只有 vector search 成为主导时才增加 provider capability：FileSystem 一次快照、多 query 计算；Postgres 单 roundtrip；InMemory 单次记录枚举。不要为了一个 provider 扩大公共契约。

### LR-2D 查询覆盖改进

根据 LR-1 的真实漏失分类，逐项处理实体、别名、短语、观察信息、新旧时间范围和多跳关系。每种扩展都必须保留 query provenance，并用 hard negatives 防止“召回多了但噪声更多”。

不做无证据的全局同义词膨胀，不把成功观察全文 OR，不把失败 ID 重新召回，不用固定 TopK 乘数冒充自适应。

### LR-2E 通道覆盖决策

统计 Lexical、Semantic、Graph、Working/Stable Memory 的唯一有效命中率和成本。没有独立质量贡献的通道应关闭或删除；有贡献但默认不可用的通道要补齐生产配置、降级和可观测性。

阶段出口：主要召回漏失已有明确分类；多问句不再线性放大外部调用；提升覆盖不破坏精度与硬约束。

## 6. LR-3：排序准确与预算分配

### LR-3A 候选 provenance 与分数校准

- 保留每个候选的 query、channel、原始分数、命中原因和去重路径；
- 不直接比较语义不同的 lexical/vector/graph 原始分数；
- 用 LR-1 数据做分桶校准，先采用可解释的确定性方法；
- 同一 policy snapshot、feature 和 selected hydration 每请求只计算一次。

### LR-3B 两阶段排序

第一阶段以低成本保召回，第二阶段只重排有限候选。先建立 deterministic reranker；只有它达到瓶颈且数据足够，才在 LR-5 引入学习式 reranker。

验收同时看 Recall@候选集、Recall@TokenBudget、Precision@K、nDCG、p95 和 token 成本，禁止只看内部 score。

### LR-3C 预算、去重与多样性

- 以 required evidence、来源多样性、冲突覆盖和 token 成本共同分配预算；
- 对近重复内容合并，但保留引用和来源；
- 候选上限只通过对照实验调整，不能因为 q×TopK 数字大就提前截断；
- 被预算裁掉的重要实体进入下一轮显式查询，而不是钉死 ID。

### LR-3D Runtime 收敛

`UnifiedRuntimeDefaults.cs` 按 Normalize/Policy、Merge/Gate/Score、Allocate/Truncate、Project 收敛责任。必须同时删除重复解析、投影和 hydration；只拆文件不算成果。

目标：DecisionEngine 区域生产 LOC 净减 10%，默认运行时只有一个正式入口，HTTP 与 Agent 的可比较部分使用同一质量口径。

阶段出口：固定预算下关键证据排序显著改善，且能解释每个 selected/dropped 的原因。

## 7. LR-4：反馈数据闭环

### LR-4A 反馈事件模型

建立版本化、追加式事件：请求和策略版本、query/candidate/selected IDs、工具/任务结果、人工纠正、延迟反馈和撤销。正文默认不进入反馈事件。

### LR-4B 归因与标签质量

- 区分“证据存在但没召回”“召回但没选”“已选但模型未使用”“工具自身失败”；
- 成功工具观察可以提供正向线索，失败 ID 只提供排除事实；
- 弱标签带来源和置信度，不能与人工金标等权；
- 监控选择偏差、位置偏差和只观察已展示候选造成的偏差。

### LR-4C 可回放数据集

从反馈事件生成不可变 snapshot，能够对任一策略版本离线重放；记录数据 lineage、特征版本、删除请求和训练/评测隔离。任何训练结果都必须能追到输入 snapshot。

### LR-4D 无学习的策略改进

先用反馈修复明显规则、查询生成、通道预算和错误分类。若确定性策略已解决问题，不为“用了 AI”额外训练模型。

阶段出口：线上结果可以安全转成带置信度的离线数据，并能重放比较当前策略与候选策略。

## 8. LR-5：受控自学习

### LR-5A 学习边界

优先学习：查询扩展选择、通道预算、候选重排、记忆晋升建议。禁止模型自行修改权限、租户、排除、生命周期、安全 gate、迁移或持久化规则。

### LR-5B 离线训练与评测

- 训练只读 train，调参只读 dev，最终报告只读隔离 test；
- 与当前 deterministic baseline 和简单 heuristic 比较；
- 除平均指标外检查长尾、租户、语言、数据新旧和 hard negatives；
- 模型/策略工件包含数据版本、代码版本、特征 schema 和可重复构建信息。

### LR-5C Shadow 与 canary

候选策略先 shadow：不影响结果，只记录差异和成本；达到样本门槛后进入小流量 canary。必须有自动回滚、kill switch、版本固定和并行基线。

### LR-5D Active 与持续学习

只有 Required-Evidence Recall@TokenBudget、任务结果和安全门均有统计可信提升才 Active。上线后监控漂移、反馈延迟和模型退化；失败时退回上一稳定 deterministic/learned 策略。

阶段出口：学习能力可以证明优于稳定基线、可解释版本、可回放、可停止、可回滚；否则保持关闭或删除。

## 9. LR-6：可靠性与规模化

### LR-6A Agent Run 行为刻画与重构

先锁定恢复、RetryPending、取消、工具 reconciliation、checkpoint、终态结算和事件顺序。再依次提取 Recovery、Context/Model、Tool Dispatch、Checkpoint/Event Buffer 协作者；终态保持显式，不建立万能 terminal 方法。

目标：`AgentRunActor.cs` 低于 2,200 行，可变字段下降 30%，AgentRun 区域总 LOC 不增加超过 5%，事件顺序、质量反馈和持久化兼容不变。

### LR-6B 故障与多节点

覆盖进程杀死、租约过期接管、重复工具结果、checkpoint 损坏、outbox 重放和结算 exactly-once。质量反馈事件必须与业务结果具有明确的一致性边界。

### LR-6C FileSystem/Postgres 热路径

- FileSystem 复用受限的不可变 metadata snapshot，多问句只扫描一次，只 materialize 最终候选；
- Postgres 为 multiquery、vector、hydration 建立真实 EXPLAIN/roundtrip 基线；
- 监控连接池、锁等待、dead tuple、worker lag 和 shutdown drain；
- outbox/lease 保留业务专用状态机，不重建万能抽象。

### LR-6D 迁移生命周期

将 fresh install baseline、baseline 后增量、旧版本支持窗口和 schema verify 分层。只有最低支持 schema 明确后，才在主版本删除旧迁移代码；不得修改已发布 migration。

阶段出口：质量链在生产 provider、多节点故障和升级期间仍可追踪、可恢复、不重复学习。

## 10. LR-7：主版本收敛与长期精简

### LR-7A 公共契约与 DTO

- 删除零引用 obsolete router/adapter/model registry 和专属 DTO；
- API DTO、Domain command、Persistence record、Internal projection 明确所有权；
- 只合并字段、校验和生命周期完全相同的纯透传对象；
- 禁止万能 nullable DTO，也禁止 API DTO 直接充当数据库记录；
- 优先删除 mapper 和边界穿透，不以减少类型数量为唯一目标。

若存在外部 NuGet 消费者，兼容删除集中到主版本；否则可在 LR-1 质量基线完成后提前做零引用清理。

### LR-7B 宿主与依赖收敛

- 将备份能力移到窄操作边界，解除 Service → 整个 ControlRoom 依赖；
- 清点 Development、Production、ProductionHA 的 descriptors 和 workers；
- 默认关闭能力不创建重对象、不启动 worker；
- 仍由一个权威组合入口编排，不新增公开 builder。

目标：默认 DI descriptors 下降 30%，Service endpoint 变薄，HTTP JSON/OpenAPI 不变。

### LR-7C 可选能力去留

对 Learning、Evolution/Canary、DecisionExperiment、旧 VectorIndex 和 ControlRoom 分别统计：质量贡献、默认状态、生产消费者、SLO、owner、代码/DI/存储成本。

每项只能选择“正式支持并隔离”或“删除”。无可信质量贡献和维护 owner 时优先删除。

### LR-7D 测试与构建精简

- 用 provider parity fixture 和参数化矩阵替代复制测试，不减少行为组合；
- 删除无输入的文档/gate 测试和历史阶段命名；
- 按 fast、contract、filesystem、postgres、quality、kill/chaos 分层并支持 CI 分片；
- 增加 Public API、DI、生产 LOC、复杂度和 generated artifacts 预算。

目标：测试代码净减 20%，覆盖组合不降；PR 快速门时间下降 30%。

### LR-7E 部署优化

模块边界稳定后再评估 trimming、ReadyToRun/PGO、容器镜像和冷启动。AOT 只有在反射、JSON、插件兼容矩阵明确后才做。

长期目标：在不删除正式质量能力的情况下，生产源码净减至少 20%；若删除无消费者实验能力，争取 30%；Public API 至少下降 20%。

## 11. 跨阶段指标

质量指标在 LR-0E/LR-1A 建立真实基线后填写绝对目标，当前不伪造百分比：

| 指标 | 当前 | 中期目标 | 长期目标 |
| --- | ---: | ---: | ---: |
| Required-Evidence Recall@K | 评测集与候选流诊断已建（LR-1A/1B），绝对值待评测运行 | 分切片持续提升 | 稳定预算与回归门 |
| Recall@TokenBudget | 同上 | 排序阶段显著提升 | 与任务成功率联动 |
| Precision@K / nDCG | 同上 | hard negatives 不回退 | 可按场景校准 |
| 失败漏失可归因率 | 候选流诊断已建（LR-1B） | >90% | 接近全量可归因 |
| q=8 embedding 调用 | 1（LR-2A 批量 embedding 完成） | 1 | 1 |
| FileSystem q=8 combined p95 | lexical ≈1.5–2.2s（见 MULTIQUERY_RECALL_BASELINE） | <500ms 或 -60% | 持续预算 |
| Public API baseline | 12,966 行（LR-7A 删 10 类型） | -8% | -20% 以上 |
| 默认 DI singleton 注册 | 351–361（按 profile，预算 390–400） | -15% | -30% |
| `AgentRunActor.cs` | 626 行（LR-6A 拆分后） | <2,700 | <2,200 |
| tracked 生成 Markdown | 0 回流（LR-0B 后） | 0 回流 | 0 |
| 测试代码 | 约 152,700 行 | -10% | -20%，覆盖不降 |

质量门优先于代码量和速度。任何优化若没有可测改善，或让更高层指标回退，立即停止。

## 12. Agent 执行协议

```text
一次只执行 docs/ROADMAP.md 指定的一个 LR-X 工作包，不扩展到相邻阶段。
先读 docs/LIVE_PATH.md、AGENTS.md、工作包涉及源码和现有测试。
先说明工作包影响的北极星指标和必须保持的质量/协议不变量。
保留工作树中他人的改动；先记录基线，再修改。
新增抽象必须说明删除了什么或隔离了什么；拆文件/改名不计精简。
完成后报告：改动文件、质量变化、行为不变量、生产/测试/Public API 净增减、测试命令、性能前后数据和剩余风险。
不要生成阶段报告 Markdown，不要自行 commit，不要顺手执行其他工作包。
```

## 13. 当前执行顺序

LR-0A..LR-7E 全部工作包已按 §12 协议执行完毕并验证：全量测试 4222 通过 / 6 跳过 / 0 失败，容器构建与冒烟通过，已推送 origin/main。当前无进行中工作包。

下一轮工作包从 §2 长期目标与 §1.2/§1.3 缺口中指定，候选方向：

- 质量基线落地：按 LR-0E 契约跑真实评测，填 §11 绝对目标（Recall@K、Recall@TokenBudget、Precision@K、p95 等）；
- 真实模型打分与 Learning 四道门（离线/shadow/canary/active）逐级打开；
- §10 长期精简目标：生产源码净减 ≥20%、Public API -20%、测试代码 -10%..-20%。

未达门槛前不打开 Adaptive Active / Learning 训练，不接原型 materialize；零引用契约清理与宿主收敛已随 LR-7 完成，不重复执行。
