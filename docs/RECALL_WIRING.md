# 召回接线阶段

> 生成：2026-08-14。给后续 agent 。 现行调用链仍以 `[LIVE_PATH.md](LIVE_PATH.md)` 为准。本文件是**下一阶段开工清单**，不是第二份架构。
> 改完一个工作包后，同步改 `LIVE_PATH.md` 对应段落，再勾本文件清单。不要新写 `ContextCore_Unified_V3.md`。

---

## 0. 你是谁、先读什么

你在仓库 `ContextCore`（.NET）。产品是编码 Agent 运行时：**模型输入 = 工作集 + 搜索**，不是 append-only 对话。

**忘掉靠工作集，找回靠搜索。搜到 ≠ 进工作集。** 召回准不准是产品，不能靠调固定参数假装变准。

开工前按顺序读（不要读 archive / `TODO.md` 后半当合同）：

1. `[AGENTS.md](../AGENTS.md)`
2. `[docs/LIVE_PATH.md](LIVE_PATH.md)`
3. **本文件**「硬禁令」+ 你被指派的**一个**工作包全文
4. 工作包列出的源码（先读再改）

一次只做**一个**工作包。做完停下来，更新文档，跑定向测试。不要顺手做下一个，不要「优化」打分公式。

复制给执行模型的提示词：

```text
执行 docs/RECALL_WIRING.md 的工作包 RW-1（只做这一个）。
先读 AGENTS.md、docs/LIVE_PATH.md、本文件第 1–3 节和 RW-1。
注释用中文，不要写任务编号。不要打开 Adaptive/Learning，不要改 50/+50，不要做 R46。
做完后按工作包「收口」更新 LIVE_PATH，只跑工作包写明的定向测试。
```

把 `RW-1` 换成你的编号。

---



## 1. 产品约束（必须记住）


| 原则        | 含义                                                                |
| --------- | ----------------------------------------------------------------- |
| 工作集是现在记住的 | 只有上一轮 **SelectedEnvelopes + Materials** 作为 `SeedWorkingSet`       |
| 未选中就忘掉    | 不进种子；本轮搜索命中才能再入选                                                  |
| 搜索不是加载    | Lexical 命中仍要过分配器；搜到可以进工作集，不是自动 admit                              |
| 问句跟外部结果走  | 成功工具观察抽出**新实体词**写成单独 `QueryTexts` 条目                              |
| 失败只排除     | 失败观察里确认不存在的 ID → `ExcludedIds`，并从种子拿掉                             |
| 准不准跟工具走   | 质量 = 工具成功率；没有工具就不要发明分数                                            |
| 固定参数不是策略  | 禁止把词元上限、标题 +50、TopK 公式、1.25× 乘数、`FinalScore`、Completed=0.9 当成召回方案 |


已经做完、**禁止重做/推翻**的（细节见 `LIVE_PATH.md` §9–§10）：

- Store `QueryText` 词元匹配（`ContextQueryTextMatcher`）
- Agent `CollectionId`；空则回退工作区
- `IContextStoreBatchLookup` 已注册（Late Hydration）
- HTTP retrieve/package 缺省切流 100
- Agent Resident 工作集（选中留下，不钉 `RequiredIds`）
- `RetrievalInput.QueryTexts` 分条词法检索（Agent 已接线）
- 成功观察 → 实体词查询；失败观察 → 只排除 ID
- 自适应默认 **Disabled**；Actor 质量反馈已改成工具成功率

---



## 2. 硬禁令（违反即停）

**不要做：**

- `TODO.md` 里的 **R46**（WP-AC Postgres 迁移恢复、WP-AD Learning 质量闸门生产策略）
- 打开 Adaptive **Active** / 把 Mode 默认改成 Shadow 或 Active
- 打开模型打分、ONNX、Learning 训练、embedding 默认开发路径
- 原型仓库的 `materialize` / Warm-Cold / admit
- 把选中 ID 写成 `RequiredIds`（会变成钉死，分配器忘不掉）
- 把 Agent Resident 写入 `IWorkingMemoryService`（那是另一层记忆存储）
- 改 `ScoreLexicalItem` 的 `50` / 标题整句 `+50`（见 RW 说明：那是诊断排序，不是召回策略）
- 为「将来」加配置项、抽象、新 Planner、新 Expert
- 把观察结果整段再拼进问句（`found in notes` 会污染 OR 匹配）
- 按连字符拆 `AmberCompass-17`（实体词必须保持完整）
- 把规划器的文本 `GraphSeeds` 当成图数据库的 ItemId 塞进 `GraphCandidateProvider`
- 全量测试刷屏（定向即可；全量只在阶段全部完成后跑一次）
- 提交 git（除非用户明确要求）
- 新建 canvas / 第二份架构圣经

**注释（**`AGENTS.md`**）：** 中文，无 `// 目标：` 这种标签，无 R46/RW-1/WP-A/P1 等编号。`TestCategory`、断言消息、commit 消息可以带编号。

**测试：** 只跑本工作包写明的 filter。`ContextCore.Tests` 全量失败数须恰好 **11** 个既有项（阶段全部完成后再跑）。

**API 基线：** 改了 Abstractions 公共表面，要更新 `tests/ContextCore.Tests/Baselines/ContextCore.Abstractions.PublicApi.txt`。可跑 `PublicApiBaselineTests`；不要用会改行为的方式「修」基线。HTTP 契约变了才动 `service/openapi/`（有 Ignore 的再生测试，不要手改无关快照）。

**构建锁：** `ContextCore.Service\bin` 可能被已启动的 Service 锁住。改 Core 时：

```bash
dotnet build src/ContextCore.Abstractions -v q
dotnet build src/ContextCore.Core --no-dependencies -v q
dotnet test tests/ContextCore.Tests --filter "<工作包写明的 filter>" --no-dependencies --nologo
```

---



## 3. 活路地图（改之前对一下）

宿主：`src/ContextCore.Service/Program.cs` → `AddContextCoreRuntime`。

```text
平面 A  HTTP ingest / query / retrieve / package
平面 B  Agent Run：KernelHost → Actor → 每轮 ContextBuilding
```

Agent ContextBuilding **直接**调 `IContextDecisionRuntime`（不经 Cutover）。
HTTP retrieve/package **经** Cutover，默认 100% 也落到同一个 `DefaultContextDecisionRuntime`。

Agent 本轮检索（已接线）：

```text
DefaultAgentRetrievalQueryPlanner / AdaptiveRetrievalPlanner(Disabled 透传)
  → AgentTurnSearchQuery.CollectQueries
  → RetrievalInput.QueryTexts          ← Lexical 按条搜，按 ID 合并最高分
  → QueryText                          ← 仅诊断拼接
  → SeedWorkingSet                     ← 上一轮选中项（Resident）
  → ExcludedIds                        ← 失败工具确认不存在的 ID
```

关键文件：


| 文件                                                                             | 职责                                                  |
| ------------------------------------------------------------------------------ | --------------------------------------------------- |
| `src/ContextCore.Core/Services/AgentRun/AgentRunActor.cs`                      | 每轮构建请求、Resident 写入、flush                            |
| `src/ContextCore.Core/Services/AgentRun/AgentResidentWorkingSet.cs`            | 选中项种子的抽出/序列化/排除 ID                                  |
| `src/ContextCore.Core/Services/AgentRun/AgentTurnSearchQuery.cs`               | 收集 QueryTexts、诊断拼接、工具证据                             |
| `src/ContextCore.Core/Services/Retrieval/ObservationQueryText.cs`              | 成功观察 → 新实体词                                         |
| `src/ContextCore.Core/Services/Retrieval/DefaultAgentRetrievalQueryPlanner.cs` | 受控查询 / 排除 ID / 图种子文本                                |
| `src/ContextCore.Core/Services/Retrieval/AdaptiveRetrievalPlanner.cs`          | 默认 Disabled；Active 只调固定乘数                           |
| `src/ContextCore.Core/Services/DecisionEngine/CandidateProviders.cs`           | Lexical `QueryTexts`；`ScoreLexicalItem`             |
| `src/ContextCore.Core/Services/DecisionEngine/AuthoritativeRuntime.cs`         | HTTP retrieve/package → V2 请求                       |
| `src/ContextCore.Abstractions/Models/RetrievalDtos.cs`                         | HTTP `ContextRetrievalRequest`（目前只有单条 `QueryText`）  |
| `src/ContextCore.Abstractions/Contracts/UnifiedRuntimeContracts.cs`            | `RetrievalInput.QueryTexts` / `ExcludedIds`         |
| `src/ContextCore.Storage.Shared/ContextQueryTextMatcher.cs`                    | 存储层词元匹配（`MaxQueryTerms=12` 只约束存储 query，不是 Agent 策略） |


两条容易混的「图种子」：


| 名字                                  | 实际是什么                                                  | 现在有没有进检索                                                                      |
| ----------------------------------- | ------------------------------------------------------ | ----------------------------------------------------------------------------- |
| 规划器 `AgentRetrievalPlan.GraphSeeds` | 从任务文本抽出的词（引号实体优先，否则长词元）                                | **会**在查询名额还剩时加成 Keyword 查询；**不会**变成图 Expert 的节点 ID                            |
| `GraphCandidateProvider`            | 从 `SeedCandidates` 的 **ItemId** 经 `IRelationStore` BFS | 默认 filesystem **没有关系边 → 空结果**。有 Resident 种子时，运行时会把 Phase1 合并结果当图扩展种子，但仍要有关系存储 |


不要把这两套接成一套。

---



## 4. 本阶段目标

**把「工作集遗忘 + 搜索召回」补到：崩了还能找回刚选中的，HTTP 也能分条问句，规划器名额不被任务套话占满。**

不解决：语义通道（没 embedding）、Learning、Canary 自动推进、原型协议。

建议顺序（有依赖，不要并行同一份 Actor）：


| 顺序  | ID       | 标题                                                   | 类型    |
| --- | -------- | ---------------------------------------------------- | ----- |
| 1   | **RW-1** | 上下文构建后立刻持久化 Resident                                 | 行为    |
| 2   | **RW-2** | HTTP retrieve/package 写入 `RetrievalInput.QueryTexts` | 行为    |
| 3   | **RW-3** | 规划器图种子查询跳过已被覆盖的词                                     | 行为    |
| 4   | **RW-4** | 图通道诚实说明（文档 + 可选冒烟，不造假图）                              | 文档/核实 |
| 5   | **RW-5** | 每个包收口后对 LIVE_PATH（本文件清单）                             | 文档    |


RW-4 不改检索算法。RW-2 与 RW-3 互不依赖，但不要两个模型同时改规划器。

---



## 5. 工作包



### RW-1 上下文构建后立刻持久化 Resident



#### 为什么

`BuildContextAsync` 把 `ResidentWorkingSetJson` 写进**内存里的** `AgentRun`。`FlushPendingEventsAsync` 在 `_pendingTurnEvents.Count == 0` 时直接返回；有事件时也要等 CallModel/Turn 结束（或满 32 条）才提交。

`CallModelAsync` 顺序是：切到 `ModelCalling` → **BuildContext（此时 Resident 只在内存）** → 调模型。模型调用中途崩溃时：

- 新 Actor 会把 `LastDecisionResult` 清成 null
- 只能从 store 上的 `ResidentWorkingSetJson` 恢复种子
- 若还没 flush，种子是空的或上一轮的 → **刚选中的正文丢了**

现有测试 `ResumeAfterCrash_UsesPersistedResidentSeed` 是在**至少完成一轮并 flush 之后**再取消，**盖不住**「第一轮上下文已建成、模型尚未返回」这个洞。

#### 改哪里

- `src/ContextCore.Core/Services/AgentRun/AgentRunActor.cs`
  - `CallModelAsync`：`BuildContextAsync` 成功（Ready / OptionalRetrievalDegraded）之后、调用 `_modelTransport.CallAsync` **之前**，把当前 `state.Run` 持久化。
  - 推荐：调用已有 `FlushPendingEventsAsync(state.Run, …)`（此时已有 RunCreated / StateTransition 等缓冲事件，会把带 Resident 的 `RunSnapshot` 写进 store）。不要新发明 `ContextBuilt` 事件类型（会改公共枚举）。
  - 不要为了 Resident 去写 `IWorkingMemoryService`。
- `src/ContextCore.Core/Services/AgentRun/AgentResidentWorkingSet.cs` 文件头注释：改成「上下文构建成功后就会随 Run 快照落库」，不要再写得像「只有 Turn 结束才落库」。
- 若 `FlushPendingEventsAsync` 在「无事件但 Run 上 Resident 变了」时仍需要兜底，可以再走 `IAgentRunStore.UpdateAsync`；优先复用 flush，避免两套写入互相覆盖状态机。先读 `FlushPendingEventsAsync` 与 InMemory/Postgres 的 CAS 语义，确认中途 flush 后 `_turnStartState` 会更新，后续 Turn 结束 flush 不会用过期 expected state。



#### 不要改

- 种子内容规则（仍只带 Selected + Materials）
- 恢复时仍清 `LastDecisionResult`（这是现设计；靠 JSON 恢复）
- 检查点格式、事件哈希链算法



#### 验收

在 `tests/ContextCore.Tests/AgentResidentWorkingSetTests.cs` 增加测试，例如：

1. 决策运行时返回带 `keep-1` 正文的选中项。
2. 模型通道在**第一次** `CallAsync` 时取消（上下文已构建、Turn 未正常结束）。
3. `runStore.GetAsync` 得到的 Run：`ResidentWorkingSetJson` 含 `keep-1` 正文。
4. 新 `AgentRunActor` 对这条 Run `ExecuteAsync`：第一次决策请求的 `SeedWorkingSet` 含 `keep-1`。

保留原有 `ResumeAfterCrash_UsesPersistedResidentSeed`。

#### 定向测试

```bash
dotnet test tests/ContextCore.Tests --filter "FullyQualifiedName~AgentResidentWorkingSetTests" --no-dependencies --nologo
```



#### 收口

`LIVE_PATH.md` §4 / §9.4「未 flush 的当轮仍会丢」改为：上下文构建成功后 Resident 已在 Run 快照上；模型调用中途崩溃也可恢复种子。未构建完的当轮仍会丢（决策还没跑完）。

本文件 RW-1 标完成。

---



### RW-2 HTTP retrieve/package 写入 QueryTexts



#### 为什么

Agent 已把多条问句放进 `RetrievalInput.QueryTexts`，Lexical 按条检索再按 ID 合并。HTTP 缺省切流 100 也走同一个 Runtime，但：

- `ContextRetrievalRequest` 只有单条 `QueryText`
- `AuthoritativeRuntime.BuildV2RetrievalRequest` **不设置** `RetrievalInput.QueryTexts`
- `BuildV2PackageRequest` **整段不设** `RetrievalInput`，打包路径的 Lexical 只能吃 `request.QueryText`

调用方若把「自然语言任务 + 实体 ID」拼成一句，仍会撞上存储/词法的词元上限，标题整句包含也对不上短标题。

Agent 路径不要在这一包里再改（已经接线）。

#### 改哪里

1. `src/ContextCore.Abstractions/Models/RetrievalDtos.cs`
  `ContextRetrievalRequest` 增加 `IReadOnlyList<string> QueryTexts { get; init; } = Array.Empty<string>();`  
   空 = 旧行为（Lexical 回退 `RewrittenQueryText` / `QueryText`）。
2. `src/ContextCore.Core/Services/DecisionEngine/AuthoritativeRuntime.cs`
  `BuildV2RetrievalRequest`：把 `request.QueryTexts` 拷进 `RetrievalInput.QueryTexts`。  
   若 HTTP 只填了 `QueryText`、`QueryTexts` 为空，**不要**在这里手工拆句；现有 `ResolveLexicalQueryTexts` 会回退单条。  
   若两者都有：与 Agent 一致，**以** `QueryTexts` **为准**（已实现于 `CandidateProviderHelpers.ResolveLexicalQueryTexts`）。
3. 打包：给 `ContextPackageRequest`（在 Abstractions 里找定义）同样加可选 `QueryTexts`，`BuildV2PackageRequest` 增加 `RetrievalInput = new RetrievalInput { QueryTexts = …, IncludeContent = … }`。
  **最小改动**：只补 Lexical 需要的 `RetrievalInput.QueryTexts`（以及打包路径本来就会用到的 IncludeContent 等，若你发现不设会导致正文丢失，保持与现网打包行为一致，不要顺手改默认）。  
   若现网打包不经 Lexical、只靠 Recent，先读 `LIVE_PATH` §3.4 和 `BuildV2PackageRequest` 实际执行链，**证实 Lexical 会跑**再接线；不要为了对称空接一个改变 selected 集合的 RetrievalInput。
4. 更新 PublicApi 基线。
5. 测试：可扩展 `tests/ContextCore.Tests/LexicalQueryTextsTests.cs`，或新测试类（不要新项目）：
  - 构造 `ContextRetrievalRequest`：`QueryText = "summarize project notes AmberCompass-17"`（对照）以及 `QueryTexts = ["summarize project notes", "AmberCompass-17"]` + 短标题笔记。
  - 走 `AuthoritativeRetrievalRuntime` 或直接 `BuildV2RetrievalRequest` 的可测缝（若方法是 private static：测公共 `RetrieveAsync` / 决策 Runtime，不要为测试把方法改成 public）。
  - 断言分条时标题 `AmberCompass-17` 能进 selected；不要断言具体 50/+50 数值。

OpenAPI：若快照测试失败，按仓库现有 Ignore 再生方法更新 `service/openapi/`，不要手改无关路径。

#### 不要改

- `HybridContextRetriever`（切流 0 的 Legacy）。本阶段默认切流 100。
- Lexical 合并算法、`ScoreLexicalItem` 常数
- Agent Actor 的 CollectQueries



#### 验收

- 只设 `QueryText`、不设 `QueryTexts`：与改前同一夹具仍能命中（`LIVE_PATH` §9.2 `PurpleBicycle-42` 语义）。
- 设 `QueryTexts = ["summarize project notes", "AmberCompass-17"]`：短标题笔记进结果。
- PublicApi 基线绿。



#### 定向测试

```bash
dotnet test tests/ContextCore.Tests --filter "FullyQualifiedName~LexicalQueryTextsTests|FullyQualifiedName~PublicApiBaselineTests" --no-dependencies --nologo
```

若你新增了测试类，把类名加进 filter。

#### 收口

`LIVE_PATH.md` §3.3：写明 HTTP retrieve 可传 `QueryTexts`；空则单条 `QueryText`。§10 把「HTTP 仍是单条 QueryText」移到已完成。

---



### RW-3 规划器图种子查询跳过已覆盖的词



#### 为什么

`DefaultAgentRetrievalQueryPlanner` 在成功观察之后，把 `GraphSeeds` 加成 Keyword 查询，上限 `MaxControlledQueries = 4`。

任务 `summarize project notes` 抽种子时，引号没有实体，就会按长度把 `summarize` / `project` 等长词元塞进剩余名额。这些词**已经在第一条任务查询里**。Lexical 再搜一遍没有新信息，却可能把本可留给第二条观察实体或真正引号实体的名额占掉。

`ObservationQueryText` 已经按「出现在 alreadyCovered 里则跳过」。图种子 Keyword **没有**同样的覆盖检查。

#### 改哪里

`src/ContextCore.Core/Services/Retrieval/DefaultAgentRetrievalQueryPlanner.cs` 里向 `queries` 追加图种子的循环：

- 若某 seed 已被现有 `query.Text` 覆盖（整段包含，或按与 `ObservationQueryText` 相同的词元规则已经出现），**跳过**。
- 引号 / 书名号抽出的实体若**尚未**出现，仍应加入（这是图种子的真正价值）。
- 不要提高 `MaxControlledQueries`，不要新配置。
- 不要把这些文本 seed 传给 `GraphCandidateProvider`。

可把「是否已被现有查询覆盖」做成规划器私有小函数，**不要**新抽象到 Abstractions。

#### 测试

`tests/ContextCore.Tests/R29M_AgentRetrievalQueryPlannerTests.cs`（或同目录新方法，TestCategory 可沿用文件现有）：

1. 任务 `summarize project notes`，无观察：ControlledQueries 第一条是任务；**不应**再出现与任务重复的 `summarize` / `project` 单独条目（若当前会加，改完后应消失或减少）。
2. 任务同上 + 成功观察 `AmberCompass-17 found in notes`：仍有一条观察查询等于 `AmberCompass-17`；图种子条目不得把 `found`/`notes` 整段带上。
3. 任务含 `《AmberCompass-17》` 或引号实体：该实体仍应出现在查询集或 GraphSeeds 里（显式锚点优先）。



#### 不要改

- `ExtractGraphSeeds` 的抽取规则可以保持；只改「写进 ControlledQueries 时是否跳过」。
- `MaxGraphSeeds` / `MaxControlledQueries` 数值
- Adaptive 乘数



#### 定向测试

```bash
dotnet test tests/ContextCore.Tests --filter "FullyQualifiedName~R29M_AgentRetrievalQueryPlannerTests|FullyQualifiedName~AgentTurnSearchQueryTests" --no-dependencies --nologo
```



#### 收口

`LIVE_PATH.md` §4：规划器图种子文本只在查询名额有空且**尚未被已有问句覆盖**时加成 Keyword。与图 Expert 无关。

---



### RW-4 图通道诚实说明（不造假图）



#### 为什么

后续模型常会把「GraphSeeds 没用上」理解成要写一套新图检索。实际上：

- 文本种子：见 RW-3（Keyword 查询）。
- 图 Expert：`GraphCandidateProvider` 在 `IRelationStore is null` 或没有种子 ItemId 时返回空；默认 Development + filesystem **没有关系边**。
- Runtime 已在 Phase2 把 Phase1 合并信封 + `SeedWorkingSet` 当作图扩展种子（`UnifiedRuntimeDefaults`）。有 Resident ID **且** 有关系存储时，图扩展已经能跑。

本包**不实现**从自然语言造边、不接外部知识图谱、不把 `AmberCompass-17` 字符串当 ItemId 去 BFS。

#### 要做

1. 读 `GraphCandidateProvider.ExecuteAsync`、`UnifiedRuntimeDefaults` Phase2 注释，用 `LIVE_PATH.md` 新小节（建议 §3.3 后或 §9.4 后）写清上面三句话，附文件名。
2. 可选冒烟（不要新评测框架）：filesystem 默认 retrieve，诊断里 Graph Expert 空/跳过；**不要**为了让测试变绿去 mock 假边。
3. 若发现 Phase2 **没有**把 Agent 的 `SeedWorkingSet` 信封传进 Graph（代码与注释不符），这才是代码缺陷：只补种子信封传递，仍不要造边。先在本包写清复现，再小补丁；吃不准就停，不要「顺便」加 RelationStore 实现。



#### 不要做

- 新 GraphSeed 字段到 `RetrievalInput`
- 用 Lexical 命中冒充图命中
- 打开 `IncludeRelationExpansion` 以外的新开关（默认已是 true）



#### 收口

`LIVE_PATH.md` 写明：默认本机路径图通道为空是预期；有关系边时从工作集 ID 扩展。本文件「仍未拍板」里删除含糊的「GraphSeeds unused」。

---



### RW-5 文档与代码同步（每个包都要做）

每个行为包结束后：

1. 只改 `docs/LIVE_PATH.md` 被该包影响的段落（日期改成当天）。
2. 本文件对应 RW 打勾。
3. 代码注释与行为一致（中文、无编号）。尤其禁止留下「Completed=0.9」「整段观察并进 QueryText」「未 flush 当轮必丢」这种过时句（若 RW-1 已修）。
4. 不要更新 `TODO.md` 后半 R31–R45 史诗；只需保证文首指向本文件。
5. 不要把 archive / vector / learning 报告改成现行合同。

阶段全部 RW-1～RW-4 完成后（由用户决定是否跑全量）：

```bash
dotnet test tests/ContextCore.Tests --nologo
```

失败须恰好 11 个既有项。不要「修」这 11 个除非用户要求。

---



## 6. 明确不做（本阶段之后才考虑）

排期以外、看到也不要做：


| 项                             | 原因                                              |
| ----------------------------- | ----------------------------------------------- |
| 打开 Adaptive Active            | 1.25× / 0.75× 是固定乘数，不提高准不准                      |
| 用 `FinalScore` 当质量            | Actor 已改为工具成功率；不要改回去                            |
| 延迟归因 Completed=0.9            | 代码已用 `ToolEvidence`；注释若仍写 0.9 只改注释              |
| Semantic / embedding          | 默认 Dev 无 embedding；空通道是预期                       |
| HTTP 会话工作集                    | HTTP retrieve 是无状态一次请求；种子是 Agent 的事             |
| 调 `ScoreLexicalItem` 的 50/+50 | 分条实体问句已能让「标题包含整句」打在 `AmberCompass-17` 上；改权重不是策略 |
| `MaxQueryTerms=12` 当召回策略      | 那是单条存储匹配的截断；Agent 已分条                           |
| R46 WP-AC / WP-AD             | 延后                                              |
| 原型 `materialize`              | 两边协议未对齐                                         |
| Canary 自动改切流百分比               | Development 不是生产推进器                             |
| DTO-R4 / Service DI 大重构       | 高风险，与召回无关                                       |


---



## 7. 打分与证据（防止「调参当产品」）

Lexical 当前（`CandidateProviders.ScoreLexicalItem`）：

- 基础分：`ts_rank ?? 50`
- 若 **Title 包含本条 query 的完整字符串** → +50

这只在**已经词法命中**的候选之间排序。命中规则在 Store matcher / FTS，不在这两个数字。

**允许：** 分条 `QueryTexts` 让短实体问句与短标题对齐（已做 + RW-2 给 HTTP）。  
**禁止：** 把 50 改成 42、加第三段加权、用模型分替代工具观察。

质量信号（已接线，不要改回）：

- 有工具观察：`AgentTurnSearchQuery.ToolEvidence` → 成功率
- 无工具：`Effective=false`，Adaptive 不得把启发式分数学成「准」
- 延迟归因同样走工具证据，**不是** Completed=0.9 / Failed=0.2

---



## 8. 阶段完成定义

同时满足：

- [x] RW-1：模型第一次调用中取消，store 上已有 Resident；新 Actor 能种上
- [x] RW-2：HTTP 可选 `QueryTexts`；旧单条 `QueryText` 不回退
- [x] RW-3：任务套话不再占用 ControlledQueries 名额；引号实体仍在
- [x] RW-4：LIVE_PATH 写清图通道；没有假图实现
- [x] LIVE_PATH 与代码一致；本文件清单已勾
- [x] 定向测试绿；未擅自改 50/+50、Adaptive Mode、RequiredIds

完成后问用户是否跑全量测试 / 是否提交。**不要自己 commit。**

---



## 9. 再后面的大阶段（只登记，不开工）

本阶段收口后，由用户挑，不要自行进入：

1. **外部证据排序**（仍不是调 50）：只有稳定的工具成败标签时，才考虑用外部结果影响排序；不是打开 Learning 开关。
2. **Semantic 接通**：真实 embedding 配置 + 索引回填 + 与 Lexical 分条问句对齐；默认 Dev 仍可关。
3. **R46**：Postgres 迁移中断恢复；Learning 闸门生产策略。
4. **原型协议**：仅当有调用方真在等 `materialize`。

`TODO.md` 里 R15 V3 / DTO-R4 / Service DI 收敛等仍是历史候选，不是本阶段依赖。