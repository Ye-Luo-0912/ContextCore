# ContextCore 新阶段 TODO 清单：上下文基础设施化、真实评测与生产边界固化

> 生成时间：2026-05-25  
> 依据：`ContextCore 项目当前可用性审计报告`、本轮关于短期/中期/长期记忆、Context Package Builder、Agent/自动化、Coding Mode 边界的架构讨论。  
> 当前判断：ContextCore 已经完成“功能链路具备”的阶段，下一阶段重点应从“继续堆功能”转向“边界固化、真实样本验收、上下文包可靠性、最小生产闭环”。

---

## 0. 当前阶段定位

ContextCore 当前不应再定位为“聊天记忆插件”或“普通 RAG 封装”，而应明确定位为：

```text
面向聊天、小说生成、自动化流程、Coding 工具管理等上层系统的
上下文状态管理与语义检索基础服务。
```

核心目标不是“记住更多内容”，而是：

```text
在给定任务下，输出正确、分层、可追溯、低噪音、符合 token 预算的最小充分上下文包。
```

---

## 1. 新阶段总原则

### 1.1 不再优先堆新功能

- [ ] 暂停新增大功能优先级，优先验证已有链路是否真实可用。
- [ ] 将下一阶段目标从“功能完成度”改为“可依赖程度”。
- [ ] 所有新增功能必须说明：
  - 解决哪个真实上下文问题；
  - 是否影响 Context Package 构建质量；
  - 是否能被评测；
  - 是否有可回溯证据。

### 1.2 三层记忆职责固定

- [ ] 在项目文档中明确以下原则：

```text
短期记忆决定下一步；
中期记忆决定怎么走；
长期记忆决定不能忘什么。
```

- [ ] 短期记忆：
  - [ ] 不默认持久化；
  - [ ] 不默认向量化；
  - [ ] 以时序、当前意图、最近反馈、临时约束为核心；
  - [ ] 主要用于 Context Package 的起点、路由和筛选。

- [ ] 中期记忆：
  - [ ] 持久化；
  - [ ] 可检索；
  - [ ] 保存任务过程、阶段性决策、方案演化、未完成事项、当前框架；
  - [ ] 作为 ContextCore 最重要的工作记忆层。

- [ ] 长期记忆：
  - [ ] 保存稳定事实、偏好、长期约束、跨项目背景；
  - [ ] 只作为补充，不得覆盖当前输入和中期过程；
  - [ ] 必须支持过期、替代、降级、冲突标记。

### 1.3 证据优先

- [ ] 所有摘要、标签、关系、稳定记忆必须保留来源信息。
- [ ] LLM 生成的中间结果不得直接成为不可追溯事实。
- [ ] `stable` 层写入必须记录：
  - `sourceId`
  - `chunkId`
  - `evidenceSpan`
  - `modelProfile`
  - `promptVersion`
  - `confidence`
  - `createdAt`
  - `supersededBy`

---

## 2. A0：Alpha 可用边界固化（最高优先级）

目标：明确当前推荐运行模式，避免用户或上层系统误用半成品能力。

### 2.1 明确 FileSystem 是当前推荐持久化后端

- [x] 在 README / docs / TODO 中明确当前推荐模式：

```text
ContextCore.Service + FileSystem Storage + 项目内 context-core-data + ControlRoom 调试
```

- [x] 明确 `memory` provider 只用于：
  - [x] 单元测试；
  - [x] Demo；
  - [x] 临时验证；
  - [x] 不可用于持久化。

- [x] 明确 `postgres` provider 当前状态为：
  - [x] Experimental；
  - [x] Partial；
  - [x] Not Service Ready；
  - [x] 不允许被误认为完整生产后端。

### 2.2 Service Provider 启动保护

- [x] 如果配置 `Storage:Provider=postgres`，在完整实现前应：
  - [x] fail-fast；
  - [x] 或返回明确错误；
  - [ ] 或只允许显式 `AllowExperimentalPostgres=true` 时启动。
- [x] 禁止半成品 provider 造成数据分裂：
  - [x] 部分数据写 PostgreSQL；
  - [x] 部分数据 fallback 到 FileSystem；
  - [x] 部分能力不可追踪。

### 2.3 Provider Capability Matrix

- [x] 新增 `docs/storage-provider-capability-matrix.md`。
- [x] 对比 FileSystem / InMemory / PostgreSQL 覆盖的契约：

```text
IContextStore
IContextCollectionStore
IContextIndex
IContextPackageBuildTraceStore
IContextPackagePolicyStore
IMemoryStore
IWorkingMemoryService
IPromotionRecordStore
IConstraintStore
IGlobalContextStore
IContextJobQueue
IContextJobQueryStore
IRelationStore
IVectorStore
IRetrievalTraceStore
EventLogSink
```

- [x] 对每个 provider 标记：
  - [x] Supported；
  - [x] Partial；
  - [x] Missing；
  - [x] Test-only；
  - [x] Service-ready。

### 2.4 Readiness 状态细分

- [x] `/api/status` 增加 readiness 字段。
- [x] 区分以下状态：

```text
Started：服务进程启动成功；
Ready：核心依赖可读写；
Degraded：部分依赖不可用；
NotProductionReady：可运行但不满足生产条件；
ExperimentalProvider：当前 provider 未达到完整支持。
```

- [x] ControlRoom `status` 同步展示 readiness。
- [ ] 增加存储可读写检查：
  - [x] context 写入/读取；
  - [x] memory 写入/读取；
  - [x] relation 写入/读取；
  - [x] constraint 写入/读取；
  - [x] job queue enqueue/dequeue；
  - [x] retrieval trace 写入。
- [x] `/api/status/deep` 端点（Service 层，6 项存储深度探针，15s 超时保护）。
- [x] `eval storage-check` 命令（ControlRoom 直连存储，表格输出，6/6 通过）。

### 2.5 本地运行手册

- [x] 新增 `docs/local-alpha-runbook.md`。
- [x] 内容包括：
  - [x] 启动命令；
  - [x] 数据目录；
  - [x] 私有配置目录；
  - [x] API key 配置；
  - [x] ControlRoom 常用命令；
  - [x] 如何清理测试数据；
  - [x] 如何导入真实样本；
  - [x] 当前不支持事项。

---

## 3. A1：Context Package Builder 升级为核心主干

目标：从“检索 TopK 片段”升级为“构建最小充分上下文包”。

### 3.1 明确 Context Package Builder 主流程

- [x] 将打包流程文档化：

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

### 3.2 Recent Filter：短期上下文筛选

- [x] 从最近上下文中筛选当前相关信息。
- [x] 排除无关支线。
- [x] 保留强时序信息：
  - [x] 用户刚刚提出的意图；
  - [x] 最近确认的约束；
  - [x] 最近工具结果；
  - [x] 最近状态变化；
  - [x] 当前未完成动作。
- [x] 输出 `RecentContextItem`：
  - [x] content；
  - [x] sourceTurnId；
  - [x] relevance；
  - [x] recencyWeight；
  - [x] reason；
  - [x] excludeReason。

### 3.3 Anchor Extraction：锚点提取

- [x] 从当前输入和短期筛选结果中提取：
  - [x] task kind；
  - [x] project/workspace；
  - [x] entities；
  - [x] topics；
  - [x] constraints；
  - [x] time range；
  - [x] user intent；
  - [x] desired output format。
- [x] 设计 `ContextAnchor`：

```csharp
public sealed record ContextAnchor(
    string Name,
    AnchorType Type,
    double Weight,
    string Source,
    IReadOnlyList<string> Aliases
);
```

- [x] Anchor 类型至少包括：
  - [x] Entity；
  - [x] Topic；
  - [x] Task；
  - [x] Constraint；
  - [x] Intent；
  - [x] Project；
  - [x] TimeRange；
  - [x] Mode。

### 3.4 Working Memory Recall：中期过程召回

- [x] 基于 anchors 召回中期记忆。
- [ ] 优先召回：
  - [x] 当前项目状态；
  - [x] 阶段性决策；
  - [x] 未完成事项；
  - [ ] 被否决方案；
  - [x] 设计原则；
  - [x] 任务状态；
  - [x] 过程摘要；
  - [x] 近期报告结论。
- [x] 中期记忆必须带状态：
  - [x] active；
  - [x] completed；
  - [x] blocked；
  - [x] deprecated；
  - [x] superseded；
  - [x] rejected。

### 3.5 Graph Expansion：图谱扩展

- [x] 基于中期召回结果提取实体节点。
- [x] 默认只扩展 1 跳。
- [x] 高价值任务可配置扩展 2 跳。
- [x] 支持 relation type whitelist：
  - [x] depends_on；
  - [x] derived_from；
  - [x] summarizes；
  - [x] generated_by；
  - [x] included_in_package；
  - [x] related_to；
  - [x] supersedes；
  - [x] conflicts_with。
- [x] 增加扩展限制：
  - [x] max nodes；
  - [x] max relations；
  - [x] min confidence；
  - [x] max token budget；
  - [x] relation type filter。
- [x] 图谱结果只作为补充，不得绕过证据层。

### 3.6 Stable Memory Injection：长期记忆补充

- [x] 长期记忆不得一开始大范围召回。
- [x] 长期记忆应由中期召回结果决定是否补充。
- [x] 只注入：
  - [x] 稳定偏好；
  - [x] 长期项目背景；
  - [x] 风格约束；
  - [x] 安全边界；
  - [x] 跨项目通用原则。
- [ ] 若当前输入和长期记忆冲突，优先当前输入。
- [ ] 若中期 active 状态和长期记忆冲突，优先中期 active 状态。

### 3.7 Constraint Merge：约束合并

- [x] 独立构建 constraints section。
- [x] 区分：
  - [x] Hard；
  - [x] Soft；
  - [x] Runtime；
  - [x] System；
  - [x] User；
  - [x] Domain；
  - [x] Project；
  - [x] Mode。
- [x] 冲突时排序：
  - [x] System / Safety；
  - [x] Current Input；
  - [x] Runtime；
  - [x] Project；
  - [x] User Stable；
  - [x] Domain Soft。

### 3.8 Conflict / Superseded Check

- [x] 在最终打包前检查：
  - [x] 是否存在被后续内容替代的信息；
  - [x] 是否存在 deprecated/rejected 信息；
  - [x] 是否存在同一实体多版本冲突；
  - [x] 是否存在低置信度关系；
  - [x] 是否有证据缺失。
- [x] 输出 `uncertainties` section。
- [x] 输出 `excluded` section，用于解释被排除项。

### 3.9 Token Budget Packing

- [x] 对包内内容分配预算：
  - [x] current task；
  - [x] recent；
  - [x] working memory；
  - [x] stable memory；
  - [x] constraints；
  - [x] relations；
  - [x] evidence；
  - [x] uncertainties。
- [x] 支持 mode-based budget：
  - [x] ChatMode；
  - [x] NovelMode；
  - [x] AutomationMode；
  - [x] CodingMode。
- [x] 增加 token waste 报告。

### 3.10 Context Package 输出结构

- [x] 明确标准输出 schema：

```json
{
  "currentTask": {},
  "recentContext": [],
  "workingState": [],
  "stableBackground": [],
  "constraints": [],
  "entities": [],
  "relations": [],
  "evidence": [],
  "excluded": [],
  "uncertainties": [],
  "budget": {}
}
```

- [x] PackageBuilder 输出必须可被 ControlRoom 预览。
- [x] PackageBuilder 输出必须可被测试断言。

---

## 4. A2：短期记忆 Promotion 机制

目标：避免短期内容全量入库，同时确保重要结论被提升。

### 4.1 明确短期内容默认不入库

- [x] 文档中明确：

```text
短期对话不默认持久化，不默认向量化。
```

- [x] 短期内容仅在满足 promotion 条件时写入中期或长期层。

### 4.2 Promotion 条件

- [x] 以下内容可以提升到中期记忆：
  - [x] 新的架构原则；
  - [x] 阶段性结论；
  - [x] 任务状态变化；
  - [x] 方案被否决；
  - [x] 约束新增或变更；
  - [x] 当前项目路线更新；
  - [x] 自动化流程进入完成/阻塞/失败状态；
  - [x] 小说剧情线、人物状态、伏笔发生变化。

- [x] 以下内容可以提升到长期记忆：
  - [x] 用户明确长期偏好；
  - [x] 项目长期定位；
  - [x] 长期稳定约束；
  - [x] 跨场景通用规则；
  - [x] 多次重复出现并稳定成立的模式。

- [x] 以下内容不应提升：
  - [x] 普通寒暄；
  - [x] 临时情绪；
  - [x] 重复解释；
  - [x] 无后续价值支线；
  - [x] 已被后续覆盖的表达修正；
  - [x] 明显一次性上下文。

### 4.3 Promotion Review

- [x] 新增 promotion candidate 状态：
  - [x] candidate；
  - [x] accepted；
  - [x] rejected；
  - [x] needs_review；
  - [x] superseded。
- [x] ControlRoom 增加 promotion review 命令：
  - [x] list candidates；
  - [x] show candidate；
  - [x] accept；
  - [x] reject；
  - [x] deprecate；
  - [x] explain source。
- [x] Promotion log 必须记录：
  - [x] source；
  - [x] reason；
  - [x] confidence；
  - [x] reviewer；
  - [x] timestamp；
  - [x] target layer。

### 4.4 Promotion Eval

- [x] 建立短期内容提升评测集。
- [x] 指标：
  - [x] 正确提升率；
  - [x] 错误提升率；
  - [x] 漏提升率；
  - [x] 长期层污染率；
  - [x] needs_review 比例。

---

## 5. A3：真实中文上下文评测集

目标：从“链路能跑”转向“上下文结果可信”。

### 5.1 评测集分类

- [x] 建立 `eval/contexts/chat/`。
- [x] 建立 `eval/contexts/project/`。
- [x] 建立 `eval/contexts/novel/`。
- [x] 建立 `eval/contexts/automation/`。
- [x] 建立 `eval/contexts/coding-mode/`。

### 5.2 初始样本数量

- [x] 建立各类场景的中文种子样本评测集，包含 corpus 语料与 seed_samples 样本。
- [x] 当前最低数量已达标：ChatMode 30、ProjectMode 43、NovelMode 30、AutomationMode 20、CodingMode 20。

### 5.3 每条评测样本结构

- [x] 评测样本包含：

```json
{
  "query": "",
  "mode": "ChatMode | NovelMode | AutomationMode | CodingMode | ProjectMode",
  "mustHit": [],
  "mustNotHit": [],
  "expectedScopes": [],
  "expectedEntities": [],
  "expectedConstraints": [],
  "expectedUncertainties": [],
  "goldenNotes": ""
}
```

### 5.4 评测类型

- [x] Retrieval Eval：
  - [x] 是否召回正确材料；
  - [x] Recall@5；
  - [x] Recall@10；
  - [x] MRR；
  - [x] 无关结果比例（Noise violation ratio）。

- [x] Package Eval：
  - [x] 上下文包是否足够；
  - [x] 是否混入无关支线；
  - [x] 是否注入正确约束；
  - [x] 是否排除过期信息；
  - [x] token 是否浪费（Waste ratio）。

- [ ] Compression Eval：
  - [ ] 摘要是否忠实；
  - [ ] 是否丢关键约束；
  - [ ] 是否引入不存在事实；
  - [ ] 是否保留 source chunk。

- [ ] Promotion Eval：
  - [ ] 短期内容是否正确提升；
  - [ ] 是否污染长期层；
  - [ ] 是否过度保守。

- [ ] Graph Eval：
  - [ ] 图谱扩展是否补充有用关系；
  - [ ] 是否引入噪音；
  - [ ] 是否遵守跳数和置信度限制。

- [ ] Generation Eval：
  - [ ] 上层 LLM 使用 Context Package 后是否回答正确；
  - [ ] 是否承认不确定；
  - [ ] 是否引用证据；
  - [ ] 是否出现 hallucination。

### 5.5 ControlRoom Eval Runner

- [x] 增加 `controlroom eval run`。
- [x] 增加 `controlroom eval report`。
- [x] 支持输出：
  - [x] Markdown 报表；
  - [x] JSON/Console 诊断输出。
- [x] JSON/Console/Markdown 报告固化按 mode 汇总指标，包含样本数、通过率、Recall、MRR、Noise、Waste 等。
- [x] 支持 `eval run --include-batches` 显式纳入 seed*.json / corpus*.json 扩展批次，默认稳定回归仍只跑 `seed_samples.json`。
- [x] 在隔离的内存状态下自动化运行并统计检索/打包各项指标。

---

## 6. A4：LLM 压缩与结构化输出质量控制

目标：避免 LLM 压缩结果污染中期/长期记忆。

### 6.1 质量指标记录

- [x] 为每次 LLM 压缩记录：
  - [x] model profile；
  - [x] prompt version；
  - [x] schema version；
  - [x] latency；
  - [x] token usage；
  - [x] estimated cost；
  - [x] retry count；
  - [x] fallback used；
  - [x] timeout；
  - [x] invalid JSON；
  - [x] schema validation failed；
  - [x] requires review；
  - [x] quality score。

### 6.2 压缩输出证据绑定

- [x] 每个 compression result 必须带：
  - [x] source chunk ids；
  - [x] source hash；
  - [x] source version；
  - [x] generatedAt；
  - [x] generatedBy；
  - [x] confidence；
  - [x] review status。
- [x] 禁止只存摘要不存原文引用。

### 6.3 Fallback 策略

- [x] 高风险任务禁止 fallback。
- [x] fallbackUsed 必须进入 trace。
- [x] fallback 输出默认进入 needs_review。
- [x] 增加 fallback 质量对比报告。

### 6.4 压缩样本集

- [x] 建立真实中文上下文压缩样本集。
- [x] 包含：
  - [x] 项目报告；
  - [x] 长聊天摘要；
  - [x] 小说设定；
  - [x] 自动化任务日志；
  - [x] 设计决策文档。
- [x] 为每类样本提供人工 golden summary。

---

## 7. A5：Embedding / Hybrid Retrieval 真实质量验证

目标：验证当前本地中文 embedding + Hybrid Retrieval 是否满足真实上下文召回。

### 7.1 召回评测

- [x] 针对真实中文上下文建立 query 集（`eval/contexts/retrieval/corpus.json` 20 items，`seed_samples.json` 20 queries，5 维度）。
- [x] 实现 `RetrievalEvalRunner`（使用 OnnxEmbeddingProvider，纯检索层评测，bge-small-zh-v1.5 真实语义向量）。
- [x] 新增 `eval retrieval` 子命令（输出 `eval-retrieval-report.json`）。
- [x] 执行评测并记录基线结果（Recall@10 各维度通过率）。
  - **基线结果（2026-05-28）**：20/20 通过（100%），Recall@10=100%，MRR=0.702，Recall@5=92.5%，噪音违规=0
  - 修复了 `HybridContextRetriever` 五处缺陷：
    - 关键词/向量/关系扩展路径均未过滤 Metadata[status]=deprecated 的 context 条目
    - Working 层 memory 未过滤 `ContextMemoryStatus.Deprecated/Rejected`
    - importance < 0.05 的极低重要性条目未被排除（噪声占位符）
    - `MatchesQuery` 对 memory 使用整串匹配（应为 token/bigram 分词匹配，与 InMemoryContextStore 对齐）
    - 关系扩展路径只查 context store，关系目标为 memory 条目时未 fallback 到 memory store
- [ ] 测试各维度通过情况：
  - [x] vector recall（纯语义，无关键词重叠）— 4/4 ✅
  - [x] keyword recall（精确技术词匹配）— 4/4 ✅
  - [x] deprecated-filter（noise/deprecated 抑制）— 4/4 ✅
  - [x] relation expansion（图谱邻居召回）— 4/4 ✅
  - [x] cross-layer（Stable + Working 混合层）— 4/4 ✅

### 7.2 模型与配置验证

- [x] 验证 `bge-small-zh-v1.5` 的 query instruction 是否合适。
- [x] 验证 pooling 策略（A5.2 实测：CLS > Mean > EOS；默认采用 CLS pooling）。
- [x] 验证 chunk size 与召回质量关系（A5 §7.2 消融：64/128/256/512 chars，Recall@10 均 35–37%，MRR≈0.36；小 chunk 无显著优势，建议维持整条 item 不拆分）。
- [x] 验证 contentHash 缓存命中率（A5.2 实测：相同内容 100% 命中，0 冗余计算）。
- [x] 验证 batch embedding 性能（A5.3 实测：~36 texts/s，p50=24.9ms@1k/29ms@10k）。
- [x] 验证 idle unload 策略对延迟的影响（A5 §7.2 实测：热路径 avg=37ms；ForceUnload 后冷启动 avg=98ms，额外开销≈61ms；建议 IdleUnloadAfter≥10min）。

### 7.3 性能基线

- [x] 记录（`eval perf` 命令，输出 `eval-perf-baseline.json`）：
  - [x] 首次模型加载耗时；（237 ms）
  - [x] 单条 embedding 耗时；（avg 27 ms）
  - [x] batch embedding 吞吐；（~36 texts/s, batch-16/32）
  - [/] 1k / 10k / 100k chunk 查询延迟；（1k/10k：ONNX 路径；100k：合成向量，p50=104ms，p95=187ms，p99=271ms，见 A5 §7.3 100k 节）
  - [x] 内存占用；（模型加载 +56 MB WorkingSet）
  - [x] index build 时间；（FileSystem O(N) 直写：1k=137ms/4.7MB，5k=444ms/23.7MB；InMemory 100k 合成写入=643ms）
  - [x] FileSystem vector store 增长后的查询性能。（1k p50=133ms，5k p50=393ms；每次查询需反序列化整个 JSONL + 线性扫描，O(N) 扩展；超过 2k 条建议迁移至 PostgreSQL/pgvector）

---

## 8. A6：最小安全闭环

目标：本地或可信内网之外不裸奔。

### 8.1 API Key

- [x] 增加最小 API key 认证。
- [x] 配置项：

```json
{
  "Security": {
    "RequireApiKey": true,
    "ApiKeyHeaderName": "X-ContextCore-Key",
    "AllowedHosts": ["localhost", "127.0.0.1"]
  }
}
```

- [x] 所有写接口必须校验 API key。
- [x] 读接口默认也校验 API key。
- [x] ControlRoom 从私有配置读取 API key。

### 8.2 默认监听策略

- [x] 默认只监听 localhost。
- [x] 如需外部访问，必须显式配置：
  - [x] host；
  - [x] API key；
  - [ ] allowed origins；
  - [x] warning log。

### 8.3 审计日志

- [x] 记录：
  - [x] request id；
  - [x] caller；
  - [x] endpoint；
  - [x] workspace；
  - [x] operation kind；
  - [x] result；
  - [x] duration；
  - [x] error。
- [x] 不在日志中输出密钥和完整敏感内容。

---

## 9. B1：PostgreSQL 全量 Provider 生产化

目标：将 PostgreSQL 从 partial provider 推进为完整 service-ready provider。

### 9.1 契约补齐

- [x] 实现或接入：
  - [x] `IContextIndex`
  - [x] `IContextPackageBuildTraceStore`
  - [x] `IContextPackagePolicyStore`
  - [x] `IWorkingMemoryService`
  - [x] `IPromotionRecordStore`
  - [x] `IConstraintStore`
  - [x] `IGlobalContextStore`
  - [x] `IContextJobQueue`
  - [x] `IContextJobQueryStore`
  - [ ] EventLogSink
  - [ ] Migration history store

### 9.2 Service 接入

- [x] `ContextCore.Service` 引用 `ContextCore.Storage.Postgres`。
- [x] `StorageExtensions` 支持 `postgres`。
- [x] 启动时验证全部必要契约。
- [x] 未满足时 fail-fast。
- [x] `/api/status` 显示 postgres readiness。

### 9.3 PostgreSQL + pgvector 集成测试

- [x] 使用 Testcontainers 或本机可选配置。
- [x] 覆盖：
  - [x] context ingest/query；
  - [x] memory promotion；
  - [x] relation build；
  - [x] vector insert/search；
  - [x] constraint injection；
  - [x] job enqueue/dequeue；
  - [x] package build trace；
  - [x] retrieval trace；
  - [x] migration apply/rollback。

### 9.4 数据一致性

- [x] 明确事务边界（`DequeueAsync` 使用 `BEGIN TRANSACTION` + `FOR UPDATE SKIP LOCKED` 原子状态切换；跨 store 写失败由 `NackAsync` 触发 job 重试，各 store ON CONFLICT 保证幂等）。
- [x] Job queue 支持并发 worker（`JobWorkerOptions.Concurrency` + `SemaphoreSlim` 槽位，队列层 `FOR UPDATE SKIP LOCKED` 确保多槽位/多实例无重复消费）。
- [x] 避免重复消费（`SELECT FOR UPDATE SKIP LOCKED` + 出队即原子置 Running）。
- [x] relation 与 memory 写入失败时可回滚或可补偿（处理器抛出 → worker NackAsync → 队列重试 → 所有 store 写入幂等）。
- [x] 支持 migration versioning（`cc_schema_versions` 表记录已应用版本；`GetAppliedVersionAsync()` + `/api/admin/schema-version` 返回 `codeVersion` / `appliedVersion` / `upToDate`）。

---

## 10. B2：运维与长期运行能力

### 10.1 健康检查

- [x] 增加 `/api/health/live`。
- [x] 增加 `/api/health/ready`。
- [x] ready 检查：
  - [x] storage（filesystem root 存在 / postgres ping / memory=ok）；
  - [x] job worker（Enabled 标志）；
  - [x] model gateway（启用模型数）；
  - [x] embedding model（IEmbeddingProvider DI presence）；
  - [x] vector store（IVectorStore DI presence）；
  - [ ] event log（待 §10.2 后补充）。
- [ ] ControlRoom 显示 health detail。

### 10.2 Worker 可观测性

- [x] 统计（`GET /api/jobs/stats`）：
  - [x] pending jobs（Queued + WaitingRetry）；
  - [x] running jobs；
  - [x] failed jobs；
  - [x] retry count（总计）；
  - [x] average duration（Succeeded 作业 CompletedAt - StartedAt）；
  - [x] last error（最近失败作业的 errorMessage + time）；
  - [x] last success time。
- [x] 支持失败任务重新入队（`POST /api/jobs/{id}/requeue`，生成新作业 ID）。
- [x] 支持 dead-letter queue（`GET /api/jobs/dead-letter`，Failed 状态作业列表）。

### 10.3 备份与恢复

- [x] FileSystem provider：
  - [x] 备份数据目录（`POST /api/admin/backup/create` → ZIP；ControlRoom `backup create`）；
  - [x] 校验 JSONL（`GET /api/admin/backup/validate`；ControlRoom `backup validate`）；
  - [x] 恢复工具（ControlRoom `backup restore <file> --confirm`）；
  - [x] 损坏文件隔离（`backup validate --isolate`：`*.jsonl.corrupt` + 净版本）。
- [x] PostgreSQL provider：
  - [x] pg_dump 方案（`GET /api/admin/backup/status` 返回 pg_dump 示例命令）；
  - [x] migration version（`PostgresMigrationRunner.SchemaVersion = "cc-schema-v2"`，`GET /api/admin/schema-version`）；
  - [ ] restore smoke test（待 §9.3 Testcontainers 集成测试时补充）。
- [x] ControlRoom 增加 backup/restore 命令（`backup create / validate / restore`）。

### 10.4 日志与监控

- [x] OpenTelemetry 管道集成（`Observability:Enabled` 条件启用，OTLP 推送）。
  - [x] `ContextCore.Service` → `ContextCoreMetrics`（Service 层，`Meter "ContextCore.Service"`）
  - [x] `ContextCore.Core` → `CoreMetrics`（static，`Meter "ContextCore.Core"`）
  - [x] `ContextCore.Embedding` → `EmbeddingMetrics`（static，`Meter "ContextCore.Embedding"`）
- [x] 记录（均通过 `System.Diagnostics.Metrics`，OTel 订阅或内存查询）：
  - [x] API latency（`AuditLogMiddleware` → `ContextCoreMetrics.RecordRequest`，tag: http.method / status_code）
  - [x] package build latency（`BasicContextPackageBuilder.BuildDetailedAsync` Stopwatch）
  - [x] retrieval latency（`HybridContextRetriever.RetrieveAsync` Stopwatch）
  - [x] compression latency + model cost（`LlmContextCompressor.CompressAsync` try/finally，记录 InputTokens+OutputTokens）
  - [x] embedding latency + batch size + cache hits（`OnnxEmbeddingProvider.EmbedAsync` try/finally）
  - [x] error rate（`ContextCoreMetrics` 内存滚动窗口：totalErrors4xx / totalErrors5xx）
- [x] `GET /api/admin/metrics`：P50/P95/P99 + 错误率（内存滚动窗口 2000 请求，无需 OTel 即可查询）
- [x] `appsettings.json` 增加 `Observability` 节（含 Seq/Grafana 接入说明注释）
- [ ] Seq 接入示例文档（appsettings 注释已提供，正式文档可选）。
- [ ] Grafana dashboard JSON 模板（可选，待评测阶段补充）。

---

## 11. C1：多场景 Mode 定义

目标：ContextCore 支持不同上层系统使用不同记忆权重和打包策略。

**实现状态 ✅**：`ContextPackageMode` 枚举（Chat/Novel/Automation/Coding）已加入 Abstractions；
`ContextPackageRequest.Mode` 和 `ContextPackagePolicy.Mode` 强类型属性已添加；
`BasicContextPackageBuilder` 优先读取枚举，兼容旧 metadata 字符串。

### 11.1 ChatMode

- [x] 短期权重最高（recent_context 28% / working_memory 24%，默认预算 2400 tokens）。
- [x] 中期用于关系/话题过程（working_memory section）。
- [x] 长期只补充稳定偏好（stable_memory 10%）。
- [x] 重点防止长期记忆喧宾夺主（stable_memory 占比低于 recent_context）。
- [x] 重点防止无关近期支线混入（RecentContextFilter + anchor 过滤）。

### 11.2 NovelMode

- [x] 当前章节/场景作为短期（recent_context 18%）。
- [x] 剧情进展、人物状态、伏笔作为中期（working_memory 16%）。
- [x] 世界观、人设、文风、禁忌作为长期（stable_memory 34% / global_context 24%，默认预算 6000 tokens）。
- [x] Package 包含：
  - [x] 当前场景目标（current_task section）；
  - [x] 相关人物状态（working_memory section）；
  - [x] 已埋伏笔（stable_memory / relation）；
  - [x] 禁止吃书设定（hard_constraints section）；
  - [x] 风格约束（soft_constraints section）；
  - [x] 待推进冲突（working_memory / related_context）。

### 11.3 AutomationMode

- [x] 当前执行节点作为短期（working_memory 30%，默认预算 3200 tokens）。
- [x] 任务状态、步骤历史、失败原因作为中期（working_memory + historical_context）。
- [x] 用户偏好、工具能力、安全边界作为长期（stable_memory + constraints）。
- [x] Package 包含：current step / previous results / pending decisions / last error / recovery point / required confirmations / tool constraints。

### 11.4 CodingMode

ContextCore 不做 Coding Agent，而做 Coding Workflow Memory Manager。

- [x] 明确边界（`ContextPackageMode.Coding` 注释中已说明）。
- [x] CodingMode 负责：项目上下文管理、任务状态追踪、工具选择建议、约束注入、执行记录、失败原因归档、跨工具上下文交接（working_memory 28% / stable_memory 22%，默认预算 4000 tokens）。
- [x] CodingMode Package 包含：project summary / current goals / relevant decisions / constraints / suggested files / known risks / tool instructions / validation steps。

---

## 12. C2：上层真实集成验证

### 12.1 聊天上下文集成

- [x] 导入真实聊天上下文摘要。
- [x] 测试：
  - [x] 当前话题判断；
  - [x] 关系线索召回；
  - [x] 边界/约束注入；
  - [x] 无关内容排除；
  - [x] 近期优先级。

### 12.2 项目上下文集成

- [x] 导入 ContextCore 审计报告。
- [x] 导入 TODO Roadmap。
- [x] 导入本轮架构讨论摘要。
- [x] 测试：
  - [x] 当前项目状态；
  - [x] 下一步优先级；
  - [x] 风险项；
  - [x] 被替代结论；
  - [x] 推荐路线。

### 12.3 小说生成集成

- [x] 准备一个最小小说项目样本。
- [x] 包含：
  - [x] 世界观；
  - [x] 人物卡；
  - [x] 剧情大纲；
  - [x] 当前章节；
  - [x] 伏笔；
  - [x] 禁忌设定。
- [x] 测试：
  - [x] 续写上下文包；
  - [x] 人物一致性；
  - [x] 伏笔召回；
  - [x] 风格约束注入。

### 12.4 自动化流程集成

- [x] 准备一个最小自动化任务样本。
- [x] 包含：
  - [x] 多步骤；
  - [x] 工具调用；
  - [x] 失败重试；
  - [x] 中断恢复；
  - [x] 人工确认节点.
- [x] 测试：
  - [x] 当前 step 识别；
  - [x] resume package；
  - [x] last error 召回；
  - [x] 工具约束注入；
  - [x] 不确定项输出。

---

## 13. D：长期 Backlog

这些不是下一阶段核心，但需要保留。

### 13.1 多租户 / 多 workspace 隔离

- [ ] workspace 级权限。
- [ ] collection 级权限。
- [ ] API key scope。
- [ ] 数据隔离测试。
- [ ] tenant-aware retrieval。

### 13.2 API Versioning

- [ ] `/api/v1`。
- [ ] schema version。
- [ ] migration compatibility。
- [ ] client SDK 版本兼容。

### 13.3 ControlRoom Service Mode / Web 管理端

- [ ] ControlRoom 连接远程 service。
- [ ] 支持 API key。
- [ ] 支持 eval report 查看。
- [ ] 支持 package trace 查看。
- [ ] 支持 promotion review。
- [ ] Web UI 暂不优先，除非 ControlRoom 不够用。

### 13.4 模型成本与预算控制

- [ ] 每 workspace 模型调用统计。
- [ ] 每 task kind 成本统计。
- [ ] 每 model profile 成本统计。
- [ ] token budget hard limit。
- [ ] monthly budget。
- [ ] over-budget warning。

---

## 14. 建议实施顺序

### 第 1 阶段：1–3 天

- [x] 明确 FileSystem Alpha 边界。
- [x] 给 Postgres 加 experimental / fail-fast 标记。
- [x] 增加 Provider Capability Matrix。
- [x] 增加本地运行手册。
- [x] 增加 status/readiness 细分。

### 第 2 阶段：3–7 天

- [ ] 增强 Context Package Builder。
- [x] 实现 Recent Filter。
- [x] 实现 Anchor Extraction。
- [x] 完善 Working Memory Recall。
- [x] 增加 excluded/uncertainties/budget 输出。

### 第 3 阶段：1–2 周

- [ ] 建立真实中文评测集。
- [ ] 增加 eval runner。
- [ ] 增加 retrieval/package/compression/promotion 评测。
- [ ] 用真实项目报告跑回归。

### 第 4 阶段：2–4 周

- [ ] 增加 API key。
- [ ] 增加 LLM 质量统计。
- [ ] 增加 embedding 性能/质量报告。
- [ ] 增加 worker observability。
- [ ] 建立备份恢复文档。

### 第 5 阶段：后续单独立项

- [ ] PostgreSQL 全量 Provider。
- [ ] Testcontainers + pgvector。
- [ ] 多 workspace 隔离。
- [ ] OpenTelemetry / Seq / Grafana。
- [ ] ControlRoom Service Mode。

---

## 15. 当前最重要的 10 个任务

1. [x] 明确 FileSystem 是当前唯一推荐持久化后端。
2. [x] 阻止 PostgreSQL 被误用为完整 Service Provider。
3. [x] 增加 Provider Capability Matrix。
4. [x] 增加 `/api/status` readiness 细分。
5. [ ] 将 Context Package Builder 升级为主干模块。
6. [x] 实现短期 Recent Filter 与 Anchor Extraction。
7. [ ] 建立短期 → 中期 → 图谱 → 长期的分层打包流程。
8. [ ] 建立真实中文上下文评测集。
9. [ ] 增加 API key 最小安全闭环。
10. [ ] 增加 LLM / Embedding / Package 质量指标统计。

---

## 16. 阶段完成判定

当以下条件满足时，可以认为 ContextCore 从“研发可用”进入“Alpha 稳定可用”：

- [ ] FileSystem 模式边界清楚。
- [ ] Postgres 不会被误用。
- [ ] 本地运行手册完整。
- [ ] Context Package Builder 能输出结构化上下文包。
- [ ] 至少 50 条真实 query 通过评测。
- [ ] Package Eval 能报告 mustHit / mustNotHit / tokenWaste。
- [ ] LLM 压缩结果可追溯。
- [ ] Embedding 检索质量有基础指标。
- [ ] API key 已启用。
- [ ] ControlRoom 能查看 package trace / retrieval trace / eval report。

当以下条件满足时，可以认为 ContextCore 进入“最小生产可用候选”：

- [ ] PostgreSQL 全量 provider 完成，或明确 FileSystem 单机生产边界。
- [ ] 真实 PostgreSQL + pgvector 集成测试通过。
- [ ] 备份恢复流程可执行。
- [ ] 监控与健康检查可用。
- [ ] 多 workspace 隔离策略清楚。
- [ ] 真实业务样本回归稳定。
- [ ] API 安全、审计日志、限流具备基础能力。

---

## 17. 一句话路线

```text
先固化 FileSystem Alpha 边界，
再把 Context Package Builder 做成主干，
然后用真实中文样本评测上下文包质量，
最后再推进 PostgreSQL 与生产化闭环。
```
