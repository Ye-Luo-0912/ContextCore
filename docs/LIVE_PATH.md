# ContextCore 现行活路径

> 生成：2026-08-14。跟的是当时 HEAD 的 DI 与端点，不是 freeze 报告。
> 本文件只回答「现在一次请求实际走哪」。不消化历史快照。
> 当前开工清单：暂无（RF 重构/精简/性能阶段已收口并归档）。R46 与 Learning/Canary 新票仍延后。

读代码从这里开始。`TODO.md` 是一页式路线入口，不再承载历史完成记录。
做下一阶段改动时以新的执行清单为准（当前无；上一份 RF 阶段清单 [`NEXT_PHASE_REFACTOR.md`](NEXT_PHASE_REFACTOR.md) 已完成）。召回接线与多轮找回、RF 阶段均已收口，不要从 Git 历史发明架构。
文档入口见 `docs/README.md`。

仓库根上的 `vector/`、`learning/`、`eval/`（除 `contexts/`）、`foundation/`、`storage/` 报告、以及 `service/` 里除 `openapi/` 以外的产物，都是机器可读或可重新生成的历史证据。不要从那些 JSON 反推现行 DI。

**整理之后（2026-08-14）：** 活路径、误导注释与历史文档已收口。HTTP retrieve/package **缺省切流 100**，与 Agent 共用 `DefaultContextDecisionRuntime`。设 `CC_CUTOVER_PERCENTAGE=0` 可切回混合检索/基础打包器。RF 重构/精简/性能阶段已完成，当前无下一份执行清单。不做 R46、不接原型。

---

## 1. 怎么跑起来（默认）

宿主：`ContextCore.Service`（`src/ContextCore.Service/Program.cs`）。

`appsettings.json` 默认：

| 项 | 默认值 | 含义 |
| --- | --- | --- |
| `Storage:Provider` | `filesystem` | 本地数据面，最完整的本机路径 |
| `ContextCoreRuntime:Profile` | `Development` | 单进程开发 |
| `ContextCoreRuntime:ModelMode` | `Deterministic` | 决策打分不走真实 ONNX |
| `ContextCoreRuntime:AgentModelMode` | `Deterministic` | Agent 模型是确定性 transport |
| `ContextCoreRuntime:ToolMode` | `Echo` | 工具是 `EchoToolDispatcher`，不是真实文件系统/进程 |
| `Security:RequireApiKey` | `true` | 请求头 `X-ContextCore-Key` |
| `Observability:Enabled` | `false` | 默认不推 OTLP |

DI 唯一入口：`AddContextCoreRuntime`（`ProductionRuntimeExtensions.cs`）。
它内部仍调用 `CoreExtensions.AddContextCore`。无参 `AddContextCore()` 标了 Obsolete，不要再用。

Postgres 是另一套生产数据面，要显式改 `Storage:Provider`。不要从 InMemory 测试实现反推产品行为。

ControlRoom 是操作面，不是决策主链。先把 Service 的 HTTP 路径认清。

---

## 2. 先分清两条平面

仓库里有两套「运行时」，注释经常把它们写成一条。

```text
平面 A — 上下文决策（给 LLM 的包）
  摄入 → 存储 → 检索/打包 → ContextPackage / WorkingSet

平面 B — Agent Run（模型循环）
  AgentRunStore → AgentKernelHost → AgentRunActor
  ContextBuilding → Model → Tool → Observe → Checkpoint
```

平面 B 在 ContextBuilding 时会调用平面 A 的 `IContextDecisionRuntime`，但 **HTTP 检索/打包不走 Agent**，**Agent 构建也不走 Cutover 装饰器**。下面分开写。

---

## 3. 平面 A：一次上下文请求

启动注册顺序（`Program.cs`）：

```text
AddContextStorage
  → AddContextCoreRuntime          ← 现行唯一运行时入口
  → AddContextModelGateway
  → AddEmbeddingProviders
  → AddContextCoreSecurity
```

### 3.1 摄入（写）

`POST /api/context/ingest`

```text
ContextEndpoints
  → IContextRuntimeService.IngestAsync
      → ContextInputIngestionService
          → IContextStore（filesystem / postgres）
```

这是写路径。不经过检索，不经过 V2 决策引擎。

### 3.2 查询（读存储，不是检索）

`POST /api/context/query`

```text
ContextEndpoints
  → IContextStore.QueryAsync / QueryPageAsync
```

**不经过** `IContextRetriever`、`IContextPackageBuilder`、`IContextDecisionRuntime`。
这是条目列表 + keyset 游标，不是「给模型的工作集」。

跟代码时不要从 `/query` 去找决策引擎。

### 3.3 检索（给模型的候选）

`POST /api/context/retrieve`

```text
ContextEndpoints
  → IContextRetriever
      = AuthoritativeRetrievalRuntime     （装饰器）
          ├─ 默认 100（>= 100） → DefaultContextDecisionRuntime（V2）
          ├─ 0                  → HybridContextRetriever（Legacy）
          └─ 中间值             → Legacy + V2 shadow/parity
```

`IContextRetriever` 在 DI 里注册成 `AuthoritativeRetrievalRuntime`。
未设环境变量时切流是 100，装饰器走 V2。

`CutoverConfiguration.FromEnvironment()` 读 `CC_CUTOVER_PERCENTAGE`，
**缺省是 100**。`CutoverController.ShouldUseV2` 在 100 时返回 true，于是：

```text
AuthoritativeRetrievalRuntime.RetrieveAsync
  → DefaultContextDecisionRuntime.ExecuteWithWorkingSetAsync
  → RetrievalResultProjector
```

`HybridContextRetriever` 仍注册为具体类型，切流 0、灰度中间值、Kill Switch 才会用到。

V2 决策链（HTTP retrieve/package 与 Agent ContextBuilding 共用）：

```text
DefaultContextDecisionRuntime
  Policy → Router → ICandidateProvider[] → Merge
  → EarlyGate → Feature → Safety/Lifecycle → UtilityScorer
  → DefaultContextDecisionEngine（唯一分配点）
```

Provider 列表（`CoreExtensions`）：Mandatory、Constraint、Lexical、Semantic、
WorkingMemory、StableMemory、Graph。Store 为 null 时对应 Provider 返回空，不抛。

`CutoverController` 无参构造仍是 0，给 canary 每轮隔离用。HTTP 进程默认百分比来自 `FromEnvironment()`，不是无参构造。

HTTP retrieve 请求体可带可选 `QueryTexts`（分条词法查询，Lexical 按条检索再按 ID 合并最高分）；
为空时回退单条 `QueryText`（`RewrittenQueryText` 优先），行为与改前一致。
HTTP retrieve/package 请求体均可传可选 `ExcludedIds`（确认不存在的 ID，不参与召回），空列表 = 旧行为。
Semantic 通道：无 embedding / 向量存储时为空（预期）；有向量时同样按 `QueryTexts` 逐条检索并按来源 ID 合并最高分，与 Lexical 分条对齐。

### 3.4 打包（给 LLM 的 ContextPackage）

`POST /api/package/build` 与 `/build-detailed`

```text
PackageEndpoints
  → IContextRuntimeService.BuildPackageAsync
      → IContextPackageBuilder
          = AuthoritativePackageRuntime
              默认 100% → 投影 V2 决策结果
              0%        → BasicContextPackageBuilder（Legacy）
```

`/build-detailed` 多返回 selected/dropped 与 `RetrievalPlan`，可再塞进 `/api/context/retrieve` 的 `Plan`。

打包请求体同样可带可选 `QueryTexts`：打包路径的 Lexical 按条检索再按 ID 合并；为空时回退单条 `QueryText`。

### 3.5 压缩作业（旁路，但是活的）

`POST /api/jobs/compression` + `ContextJobWorker`（`JobWorker:Enabled=true`）。
走 ModelGateway 配置的 LLM 路由，**不是**决策引擎，也不是 Agent Run。

---

## 4. 平面 B：一次 Agent Run

`MapAgentExecutionEndpoints` → `AgentKernelHost` → 每个 Run 一个 `AgentRunActor`。

ContextBuilding（`AgentRunActor.BuildContextAsync`），**每个模型轮次都会跑一遍**：

```text
AgentKernelHost 解析 IContextDecisionRuntime
  → AgentRunActor.TryExecuteDecisionAsync
      → IContextDecisionRuntime.ExecuteWithWorkingSetAsync
          = DefaultContextDecisionRuntime（直接 V2，不经过 CutoverController）
```

请求从第二轮起带 `SeedWorkingSet`：上一轮 **SelectedEnvelopes + 对应 Materials**。
`QueryText` 是规划器查询的诊断拼接。真正词法召回走 `RetrievalInput.QueryTexts`，按条检索再按 ID 合并（保留对该条问句的最高分），避免拼成一句后词元上限截掉工具观察、标题也对不上。成功工具观察只抽出还没搜过的实体词（带数字或连字符的优先），**按时间倒序占查询名额**（最新工具结果优先，最旧让位），不把 found/notes 整段拿去 OR；成功观察里的显式 `id:`/`ref:`/`uuid:` 引用按条加成 Keyword 问句（工具在说这个 ID 存在，不在工作集时靠搜索找回），失败观察里的 ID 只进排除集、不变成问句。
规划器图种子文本只在查询名额有空且**尚未被已有问句覆盖**时加成 Keyword 查询；任务里已有的套话不再重复搜，名额留给观察实体或引号实体。图种子文本与图 Expert 无关（不是关系图节点 ID）。
投影材料顺序保持分配器 `SelectedEnvelopes` 原顺序，不用 FinalScore 重排（正文缺失的降级摘要也不写分数）；best-fit 仍按预算逐个尝试、跳过太大的材料。
规划输入带上 `ToolObservations`、`TurnBudget`、上一轮检索诊断；规划与排除只消费**最近 8 条**观察（复用排除上限；旧观察不再占查询名额、不拖低成功率），对话历史仍按预算投影。未解决目标 = 上一轮被分配器裁掉条目的实体词，逐条加成 Keyword 找回问句（不钉 ID、不复制任务原文）。上一轮检索 0 命中时，任务/意图里尚未单独成问句的实体样词逐条拆成 Keyword 问句再搜（不加向量），计划说明如实标注 0 命中。
自适应模式默认仍是 **Disabled**：不改 Token/TopK 乘数。准不准跟工具观察走，不跟固定乘数或打分器分数走。诊断里的 `HighestScore` 只是上一轮选中项的 FinalScore 快照（诊断字段），不是质量信号，不送进自适应。
未选中的不进种子；也不把选中 ID 写成 `RequiredIds`，这样分配器仍能按预算忘掉。
失败工具观察确认不存在的 ID 写入 `RetrievalInput.ExcludedIds`，并从 Resident 种子拿掉。
Resident 写进 `AgentRun.ResidentWorkingSetJson`，上下文构建成功后随 Run 快照落进 data jsonb。
崩溃恢复时 `LastDecisionResult` 仍清成 null，但会从 Run 上的 JSON 恢复种子。
上下文构建成功后 Resident 已在 Run 快照上，模型第一次调用中取消也能恢复种子；未构建完的当轮仍会丢（决策还没跑完）。

`AuthoritativeAgentContextRuntime` 会把 caller WorkingSet 当种子，但 **Actor 不调用它**（只在 DI 里注册，给测试用）。

`WorkingMemoryCandidateProvider` 读的是记忆存储 `Layer=Working`，不是 Run 里那份 `CandidateWorkingSet`。Actor 不往 `IWorkingMemoryService` 写。

可选：`AdaptiveRetrievalPlanner`。默认模式 **Disabled**（fail-closed，透传、不写反馈）。
Active 只调 Token/查询条数/权重乘数，那是固定参数，不是召回准不准。
Actor 把工具观察交给规划器生成查询；质量反馈只在有工具观察时有效（成功率），不用 `FinalScore`。

之后：

```text
IAgentLoopPolicy.DecideAsync
  → IAgentModelTransport.CallAsync     默认 Deterministic
  → IAgentToolCallValidator + IAgentApprovalGate
  → IDurableToolExecutor               默认 Echo 工具
  → Checkpoint / 下一轮 ContextBuilding
```

默认 `ToolMode=Echo`、`AgentModelMode=Deterministic`。
`ProductionHA` 会强制 `RealModel` + `RealDispatch`。本机默认配置下，Agent 循环是**真状态机 + 假模型/假工具**。

和平面 A 的对齐：

| 入口 | 实际决策实现（默认配置、无 `CC_CUTOVER_PERCENTAGE`） |
| --- | --- |
| `POST /api/context/retrieve` | V2 `DefaultContextDecisionRuntime` |
| `POST /api/package/build` | V2 `DefaultContextDecisionRuntime`（投影） |
| Agent Run ContextBuilding | V2 `DefaultContextDecisionRuntime` |

HTTP retrieve/package 与 Agent 默认共用决策运行时。`CC_CUTOVER_PERCENTAGE=0` 时 HTTP 两条切回 Legacy；Agent 仍直接调决策运行时。

---

## 5. 存储

| Provider | 定位 |
| --- | --- |
| FileSystem | 默认本机路径，数据面最完整 |
| Postgres | 生产语义（租约、outbox、复合键、Learning 工件）。`AddContextStorage` 先于 Runtime 注册，TryAdd 不会盖掉 |
| InMemory | 测试 |

Learning 物化：Postgres 走 durable outbox；FileSystem/InMemory 走进程内 Channel（崩溃会丢）。

---

## 6. 旁路（先当沉积，别从这里读架构）

活着但不是「一次 query/retrieve」主链：

- `Learning/V14_0/`（`BasicContextPackageBuilder` 仍引用其中类型）
- 向量 Evaluation V5/V6 runner、Gate runner（评测沉积）
- `CutoverController` / `ShadowDecisionRuntime` / `DecisionExperimentPlane`（灰度与抽样；HTTP 缺省切流 100，Shadow 仍按采样率跑 Legacy）
- `CanaryProgressionService`（可改 Cutover 百分比；Development 默认不是生产推进器）
- ControlRoom `EvalCommand*` 超大文件
- `MapRetrievalEndpoints` 已空（shadow debug 端点删了）
- Git 历史里的 freeze / 阶段报告 / 过期设计稿

可以读、可以测，不要当现行合同改。

---

## 7. 文档怎么用

| 文件 | 角色 |
| --- | --- |
| **本文件 `docs/LIVE_PATH.md`** | 现行导航 |
| `docs/NEXT_PHASE_REFACTOR.md` | 已完成的重构/精简/性能阶段清单（RF-1…RF-7，含基线数据） |
| `docs/README.md` | 文档索引 |
| `README.md` | 仓库入口 |
| `TODO.md` | 一页式当前路线与历史入口 |
| `vector/` `learning/` `foundation/` `storage/` | 机器可读或可重新生成的历史证据 |
| `eval/contexts/` | 评测语料（测试会读） |
| `service/openapi/` | OpenAPI 快照（漂移测试会读） |
| `AGENTS.md` | 注释与测试约定，不是架构 |

不新写 `ContextCore_Unified_V3.md`。活路径变了就更新本文件；阶段安排只更新 `NEXT_PHASE_REFACTOR.md`。

---

## 8. 和原型仓库的接缝（暂不实现）

`context-agent-prototype` 的 ContextCore 适配器协议是进程内 JSON-lines：

`ingest` / `materialize` / `acknowledge_consumption`

本仓库对外 HTTP 是：

`/api/context/ingest` / `/api/context/retrieve` / `/api/package/build`

两边还没有同一套 `materialize` 语义。整理阶段不对齐协议、不在原型里接真 ContextCore。

---

## 9. 本机核对（2026-08-13）

默认 Development + filesystem，临时关掉 API Key / RBAC。

### 9.1 端点能否打通

| 调用 | 结果 |
| --- | --- |
| `POST /api/context/ingest` | 200，写入一条 note |
| `POST /api/context/query` | 200，按 workspace/collection 读回同一条（存储，不是决策） |
| `POST /api/context/retrieve` | 200，缺省走决策运行时（无 Hybrid stages）。无 embedding 时语义通道为空 |
| `POST /api/package/build` | 200，路径在 `/api/package/build`，不在 `/api/context/` 下。缺省切流 100，`runtimeKind=UnifiedV2` |
| `POST /api/agents/runs` | 201 → 一轮后 Completed。确定性模型，Echo 工具 |

切回 Legacy：进程设 `CC_CUTOVER_PERCENTAGE=0`。对比见 §9.3。

### 9.2 同一条笔记：ingest → retrieve → package → Agent

夹具正文含 `PurpleBicycle-42`。自然语言问句是 `Summarize PurpleBicycle-42 project context approach`。写入后立刻测，没有等待。

**修复前（整句 substring + Agent collection 写死为 workspace）：**

| 调用 | query | 结果 |
| --- | --- | --- |
| store `query` 不带 QueryText | — | 立刻命中 |
| store `query` / HTTP `retrieve` | 自然语言问句 | 0 命中 |
| store `query` / HTTP `retrieve` | `PurpleBicycle-42` | 1 命中 |
| `package/build` | 自然语言问句 | 不带笔记 |
| Agent Run | 自然语言问句 | 模型上下文约 58 字符，没有笔记正文 |

**修复后（词元匹配 + `CreateRunRequest.collectionId` + 注册 `IContextStoreBatchLookup`）：**

| 调用 | 结果 |
| --- | --- |
| store `query` / HTTP `retrieve` | 自然语言问句立刻 1 命中 |
| `package/build` | `recent_context` 带笔记 |
| Agent Run（`collectionId=demo`） | Completed；确定性模型因笔记正文含 `search` 走了工具轮（3 次模型调用），说明材料已进入上下文 |

根因与改动：

- `FileContextStore.MatchesQueryText` 曾把整段 `QueryText` 当 substring。现与 InMemory 共用 `ContextQueryTextMatcher`（整句或任一词元命中）。
- Agent 曾用 `ContextDecisionScope(workspace, workspace)`。现 `AgentRun.CollectionId` 可指定，空则回退工作区。
- Agent 以 `IncludeContent=false` 召回，但 DI 未注册 `IContextStoreBatchLookup`，Late Hydration 是空操作。现 FileSystem / InMemory / Postgres 都转发该接口。

### 9.3 同一条链：切流 0 与切流 100

夹具与问句同 §9.2。先在临时进程上对比，随后把仓库缺省改成 100。

| 调用 | 切流 0 | 切流 100（现为缺省） |
| --- | --- | --- |
| HTTP `retrieve` | Hybrid：有 `关键词召回` 等 stages，selected=1 | V2：无 Hybrid stages；`diag.AllocatorVersion=V2.1`，`diag.performance.v21_path_used=true`；selected=1，正文命中 |
| HTTP `package/build` | `recent_context` 带笔记 | `runtimeKind=UnifiedV2`，section=`default`，正文命中 |
| `/package/build-detailed` | — | selected=1 |
| Agent（`collectionId=demo`） | Completed，3 次模型调用 | Completed，3 次模型调用（Agent 从不走 Cutover） |

结论：匹配修好之后，HTTP 切到决策运行时也能立刻命中同一条笔记，并与 Agent 走同一套 Runtime。空结果不是 V2 的固有行为。仓库缺省现为 100；要 Hybrid 时设 `CC_CUTOVER_PERCENTAGE=0`。

### 9.4 Agent 工作集：选中留下，未选中靠搜索找回

| 对象 | 跨轮 | 行为 |
| --- | --- | --- |
| 上一轮 `SelectedEnvelopes` | 是 | 作为下一轮 `SeedWorkingSet`；随 `AgentRun` 提交写进 `ResidentWorkingSetJson` |
| 上一轮未选中项 | 否 | 不进种子；实体词写入下一轮找回问句（逐条 Keyword），搜索命中则可再入选 |
| 检索问句 | 每轮仍搜索 | 计划查询与观察实体词写入 `QueryTexts` 分条检索；拼句只用于诊断 |
| `RequiredIds` | 不钉死选中 ID | 预算裁掉 = 忘掉 |
| 失败工具确认不存在的 ID | 否 | 写入 `RetrievalInput.ExcludedIds`，从 Resident 种子拿掉，Lexical/Mandatory 不再召回 |
| 成功工具观察里的显式 `id:` | 否（搜索线索） | 按条加成 Keyword 问句，不在工作集时靠搜索找回；不钉 `RequiredIds`、不进 `ExcludedIds` |
| 已持有 ID（种子里） | 是 | 保留在种子；Lexical/Semantic 召回跳过，不再占 TopK；与 `ExcludedIds`（确认不存在）分开 |
| 投影因预算跳过 | 是（仍在种子） | 不等于进模型；其实体词写入下一轮找回问句，靠搜索换问句找回 |
| 召回质量信号 | 有工具才有效 | 工具成功率；不用选中项 `FinalScore`，也不用 Completed=0.9 |
| `IWorkingMemoryService` | Agent 仍不写 | 记忆层不是这条 Resident |
| 崩溃恢复 | 是（Run jsonb） | 新 Actor 从 `ResidentWorkingSetJson` 恢复种子；上下文构建成功后已落库，模型返回前崩溃也能恢复；未构建完的当轮仍会丢 |
| `AgentContextState.Conversation` | 是 | 工具轮协议单元仍按预算裁剪 |

不做原型那套 Warm/Cold/admit/`materialize`。只接种子、不做驱逐会变成 append-only；这里驱逐就是「没选中就不带入 + 分配器预算」。

### 9.6 「忘掉再搜回」端到端夹具（MR-11 验收结论）

`MultiTurnForgetAndRecallTests`：库两条笔记（长正文 keep-budget 占预算、短标题实体 AmberCompass-17），InMemory store + 确定性脚本模型 + Echo 工具，不调真实 LLM / 不用 embedding。

| 轮次 | 发生了什么 | 结论 |
| --- | --- | --- |
| 第一轮 | 任务同时命中两条；分配器预算只够长正文，短条目被裁（Dropped） | 选中 = 记住；未选中 = 忘掉，不进下一轮种子 |
| 第二轮 | 被裁条目的实体词自动写进找回问句（分条 Keyword）；搜索再次召回并选中它 | 忘掉靠搜索找回，不钉 `RequiredIds` |
| 第三轮 | 失败工具 `id:gone` 进入排除集；种子与决策都不再出现 gone | 失败只排除：确认不存在的 ID 不召回、不进种子 |

### 9.5 图通道：默认空是预期，有关系边才扩展

- 文本种子（`DefaultAgentRetrievalQueryPlanner` 的 `GraphSeeds`）：只作为受控 Keyword 查询（有空位且未被已有问句覆盖才加），**不会**变成图 Expert 的节点 ID。
- 图 Expert（`GraphCandidateProvider.ExecuteAsync`）：`IRelationStore` 为 null、没有种子 ItemId、或 `IncludeRelationExpansion=false` 时直接返回空。默认 Development + filesystem **没有关系边 → 空结果是预期**。
- 运行时已在 Phase2（`UnifiedRuntimeDefaults.InvokeEnabledProvidersWithDagAsync`）把 Phase1 合并信封 + `SeedWorkingSet` 信封作为图扩展种子传给 `GraphCandidateProvider`。有 Resident 种子 ID **且**有关系存储时，图扩展已经能跑。
- 不从自然语言造边、不接外部知识图谱。只有 `CanonicalKey.EntityId` 会作为种子 ItemId 参与 BFS；任务/观察里的 `AmberCompass-17` 这类字符串不会当 ItemId 去 BFS。

## 10. 认路后还没拍板的

已经能回答的：

- 刚 ingest 完 retrieve 为空：曾是整句 substring 匹配，不是落盘延迟；FileSystem 已改为词元匹配
- Agent 与 HTTP 可用同一个 collection（请求体 `collectionId`）；未指定仍回退工作区
- Agent 要看见正文，还需要 `IContextStoreBatchLookup`（已注册）
- HTTP retrieve/package 缺省切流 100，与 Agent 都走 `DefaultContextDecisionRuntime`；同一夹具能命中
- HTTP retrieve/package 可传 `QueryTexts` 分条词法检索；空则回退单条 `QueryText`
- Agent 跨轮 Resident = 上一轮选中项；未选中靠搜索召回；随 Run 落库，崩溃后可恢复
- 上下文构建成功后 Resident 即随 Run 快照落库；模型第一次调用中取消/崩溃也能恢复种子
- 规划器图种子文本只在名额有空且未被已有问句覆盖时加成 Keyword；任务套话不占名额
- 图通道默认空是预期（filesystem 无关系边）；有关系边时从工作集 ID 扩展，文本种子不冒充图节点
- 工具观察里的新实体词会写成单独查询；成功观察不把 found/notes 整段拿去 OR
- 失败工具观察里确认不存在的 ID 不再召回，也不再当 Resident 种子
- 召回准不准跟工具观察走，不跟词元上限、+50 标题分、自适应乘数或 `FinalScore` 走
- 切回 Legacy HTTP：`CC_CUTOVER_PERCENTAGE=0`

仍未拍板、先不要改架构的：

- 有没有调用方在等 `materialize` 协议
- 要不要做原型那套 Warm/Cold / admit
- 打开 Adaptive Active（只调固定乘数，不解决准不准）
- 打开模型打分 / Learning 训练（默认链上没有 embedding，也没有可用的外部标签）

多轮找回阶段（MR-1…MR-12）已完成：观察实体词最新优先、规划/排除只看最近 8 条观察、已持有 ID 不占 Lexical/Semantic TopK、分配器/投影裁掉的条目实体词写进下一轮分条找回问句、空召回换实体词问句且 Reason 不提向量、成功 `id:` 是搜索线索而失败 `id:` 只排除、投影顺序 = 分配器顺序、HTTP retrieve/package 可传 `excludedIds`、Semantic 有 embedding 时按 `QueryTexts` 分条、「忘掉再搜回」端到端夹具绿。代码缺口清单已清空；剩余疑问见上。

重构精简与性能阶段（RF-1…RF-7）已完成：已持有 ID 下推到三 provider 向量检索（排序/截断前排除，162 组合欠召回均为 0）；通用 LeasedWork 租约层删除，Canary 与 AgentRun 各依赖专用租约接口；无引用的旧执行产物工厂删除；HTTP 冲突错误统一走 `ContextCoreHttpResultMapper.Conflict`；RF-4（outbox 去重）与 RF-6（Actor 终态模板）经净删除门槛评估后取消。多问句召回基线见 `benchmarks/results/MULTIQUERY_RECALL_BASELINE.md`：embedding/vector search 与 FileSystem lexical roundtrip 均随问句数线性放大，优先做批量 embedding/query 去重与单请求批量读取；TopK 与 held 数量对时延影响小。全量测试 4075 通过 / 0 失败 / 7 跳过。

R46（Postgres 迁移恢复、Learning 质量闸门生产策略）仍延后。
