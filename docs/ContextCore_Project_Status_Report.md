# ContextCore 项目当前可用性审计报告

生成时间：2026-05-25 17:43:40 +08:00  
审计对象：`D:\Users\Ye_Luo\AppData\Local\Context`  
报告依据：`ContextCore_TODO_Roadmap.md`、当前解决方案项目、服务配置、存储注册、测试项目和本次构建/测试结果。

## 1. 当前总体结论

ContextCore 当前已经从“本地存储框架”推进到了“可独立启动、可被 HTTP 调用、具备多层记忆、模型路由、embedding 抽象、本地向量检索、ControlRoom 管理和测试覆盖”的阶段。

从实际可用角度看，当前最完整、最可靠的运行方式是：

```text
ContextCore.Service + FileSystem Storage + 项目内 context-core-data + Mock/LLM 可配置压缩 + ControlRoom 调试
```

当前可以用于本机试运行、内部工具集成、上下文管理链路验证、上下文包构建、压缩作业验证、检索调试和控制台观察。但如果目标是“长期生产服务”，仍需要补齐 PostgreSQL 完整服务级 provider、真实数据库集成测试、认证授权、部署监控、备份恢复、性能压测和运维文档。

简要判断：

| 维度 | 当前状态 | 结论 |
|---|---|---|
| TODO 完成度 | 路线图 P0-P4 项均已勾选 | 功能推进完整 |
| 构建状态 | 通过，0 警告，0 错误 | 编译健康 |
| 自动化测试 | 137 个测试全部通过 | 基础回归稳定 |
| FileSystem 后端 | Service 已完整接入 | 当前主力可用后端 |
| InMemory 后端 | Service 已接入 | 适合测试/临时运行 |
| PostgreSQL 后端 | 项目和部分 store 已实现，Service 未接入 | 不能视为完整服务级 provider |
| LLM 路由 | 配置和适配器已具备 | 真实质量依赖外部 API 和评测 |
| Embedding/检索 | 本地模型和 Hybrid Retrieval 已具备 | 需要真实语料效果和性能验证 |
| 生产化 | 缺认证、监控、备份、部署方案 | 尚未达到生产闭环 |

## 2. 当前可用能力

### 2.1 服务化与 API

`ContextCore.Service` 已存在，并使用 ASP.NET Core Minimal API 暴露服务能力。当前服务端点文件包括：

- `StatusEndpoints.cs`
- `ContextEndpoints.cs`
- `PackageEndpoints.cs`
- `CompressionEndpoints.cs`
- `JobEndpoints.cs`
- `RelationEndpoints.cs`
- `ConstraintEndpoints.cs`
- `MemoryEndpoints.cs`
- `ModelEndpoints.cs`

已验证的服务能力包括：

- `GET /api/status` 返回服务状态、存储 provider、rootPath 和后台任务数量。
- `POST /api/context/ingest` 可写入上下文。
- `POST /api/context/query` 可查询上下文。
- `POST /api/jobs/compression` 可创建压缩作业。
- `GET /api/jobs/{id}` 可查询作业状态。
- Job Worker 可消费压缩作业并写回 summary。

服务默认配置：

```json
{
  "Storage": {
    "Provider": "filesystem",
    "RootPath": ""
  },
  "Compression": {
    "Provider": "llm"
  },
  "JobWorker": {
    "Enabled": true,
    "PollIntervalMilliseconds": 1000
  }
}
```

`Storage:RootPath` 为空时会解析到项目内专用数据目录，避免默认写入用户目录或各项目 `bin` 目录。

### 2.2 Client SDK

`ContextCore.Client` 已加入解决方案，提供外部系统调用 ContextCore.Service 的 SDK 入口。当前适合给外部项目做 HTTP 封装调用，不需要外部系统直接引用 Core 或具体 Storage 实现。

### 2.3 FileSystem 存储

`filesystem` 是当前最完整的可用后端，Service 已通过 `StorageExtensions` 完整注册以下能力：

- 上下文存储和 collection 存储
- 轻量索引
- 向量存储
- package build trace
- package policy 持久化
- retrieval trace
- memory store / working memory / promotion record
- constraint store
- relation store
- global context store
- job queue / job query store
- JSONL 事件日志

FileSystem 后端已加入读写分离、文件锁、JSONL append 加锁、临时文件原子替换和损坏 JSONL 检查工具。当前可作为单机运行和本地集成的主存储后端。

### 2.4 InMemory 存储

`memory` 后端已接入 Service，覆盖测试和临时运行所需的主要契约。它不适合持久化使用，但适合：

- 单元测试
- API 测试
- Demo
- 临时环境
- 快速验证 DI 与业务流程

### 2.5 多层记忆与上下文核心能力

P1 阶段的多层记忆能力已经具备：

- Relation 层：支持 `derived_from`、`summarizes`、`generated_by`、`included_in_package`、`related_to`。
- PackageBuilder：可返回 selected/dropped items、reason、score、estimated tokens，并写入 build trace。
- Working Memory：支持 recent memory、active context、current task。
- Stable Memory：支持 Candidate、Verified、Stable、Deprecated、Rejected。
- Memory Promotion：支持 promote、reject、deprecate，并记录 promotion log。
- Global Context：支持 Workspace、Collection、Session、Task scope。
- Constraint：支持 Hard、Soft、Runtime、System、User、Domain，并在包构建时注入。
- PackagePolicy：支持 section 顺序、section token budget 和 include flags。
- Collection Validation：支持重复 ID、孤立 refs、derivedFrom、循环引用和 relation source/target 检查。

这些能力已经达到“上下文管理系统”的核心雏形，而不是单纯的数据写入工具。

### 2.6 ModelGateway 与 LLM 路由

`ContextCore.ModelGateway` 已实现三层模型配置：

```text
ApiProviders -> ModelProfiles -> Routes
```

当前配置包含：

- DeepSeek API provider
- OpenAI-compatible 第三方 API provider
- Local HTTP provider 占位
- `deepseek-v4-flash`
- `deepseek-v4-pro`
- `pinai-gpt-5.4-mini`
- `pinai-gpt-5.4`
- `pinai-gpt-5.5`
- `local-qwen3.5-2b`

已具备的模型路由能力：

- primary / fallback
- retry
- timeout
- high risk task 禁止 fallback
- 按 role、task kind、thinking mode、capability、category 自动解析
- `/api/model/status`
- `/api/model/route/resolve`
- usage log
- fallbackUsed 记录

密钥读取方式已符合当前要求：优先从用户目录私有配置读取，不把密钥写入项目仓库。当前说明文件记录的私有配置位置：

```text
%USERPROFILE%\.contextcore\.env
%USERPROFILE%\.contextcore\secrets.json
```

### 2.7 LLM 压缩与质量报告

当前具备：

- `LlmContextCompressor`
- `CompressionPromptBuilder`
- Summarize
- ExtractKeyPoints
- GenerateIndexHints
- Light / Normal / Deep / Audit 深度
- 结构化 JSON 输出
- `CompressionResultValidator`
- `CompressionQualityReport`

质量评分当前是轻量可解释版本，适合作为链路打通和初步风控，不应被理解为完整语义评估器。

### 2.8 Embedding 与 Hybrid Retrieval

`ContextCore.Embedding` 已加入解决方案，并包含项目内本地模型：

```text
src/ContextCore.Embedding/Models/all-MiniLM-L6-v2
src/ContextCore.Embedding/Models/bge-small-zh-v1.5
```

当前上下文主要面向中文，默认使用 `bge-small-zh-v1.5` 是合理方向。

已具备：

- `IEmbeddingProvider`
- `IVectorStore`
- `IEmbeddingJobService`
- Mock embedding provider
- ONNX embedding provider
- ONNX Runtime session manager
- BERT WordPiece tokenizer
- batch embedding
- contentHash 缓存
- idle unload
- Hybrid Retriever
- Scope Filter
- Mandatory Injection
- Tag / Type / Ref / Keyword recall
- Relation Expansion
- Vector Recall
- Candidate Scoring
- Deduplication
- Token Budget Packing
- RetrievalTrace

当前功能链路已具备，但仍需要真实中文上下文语料下的召回率、准确率和性能评测。

### 2.9 ControlRoom

`ContextCore.ControlRoom` 已具备较完整的观察和管理命令：

- status
- list / show
- jobs
- memory
- relations
- constraints
- package preview
- policy
- retrieval debug
- report export
- model
- index

ControlRoom 当前适合作为本地调试、状态观察、检索解释、策略查看和报告导出工具。

### 2.10 测试覆盖

当前解决方案包含三个测试项目：

```text
tests/ContextCore.Tests
tests/ContextCore.IntegrationTests
tests/ContextCore.Service.Tests
```

已覆盖：

- 核心逻辑单元测试
- FileSystem 端到端持久化重载
- 模型网关 fallback
- PackageBuilder policy 顺序和预算
- ControlRoom Markdown 报告导出
- Service API 状态、摄取、查询
- Service Job Worker 压缩作业处理
- PostgreSQL 部分 store 的构造/SQL 级能力测试

## 3. 与 TODO 清单一致的完成项

路线图中的 P0-P4 项均已勾选，且从当前代码结构看，主要项目均已落地：

```text
src/ContextCore.Abstractions
src/ContextCore.AppHost
src/ContextCore.Client
src/ContextCore.ControlRoom
src/ContextCore.Core
src/ContextCore.Embedding
src/ContextCore.ModelGateway
src/ContextCore.Service
src/ContextCore.Storage.FileSystem
src/ContextCore.Storage.InMemory
src/ContextCore.Storage.Postgres
```

解决方案中也已包含对应测试项目：

```text
tests/ContextCore.Tests
tests/ContextCore.IntegrationTests
tests/ContextCore.Service.Tests
```

按阶段看：

- P0 服务化地基：已完成。
- P1 多层记忆与上下文核心能力：已完成。
- P2 真实智能处理与模型路由：工程链路已完成。
- P3 语义检索与向量层：本地模型、向量抽象、Hybrid Retrieval 和 PostgreSQL 部分后端已完成。
- P4 生产加固与长期维护：FileSystem 并发安全、日志/诊断、集成测试、token 估算和 policy 持久化已完成。

## 4. 勾选但仍存在工程风险的部分

### 4.1 PostgreSQL 不能直接视为完整 Service Provider

当前 `ContextCore.Storage.Postgres` 已存在，并提供：

- `PostgresContextStore`
- `PostgresMemoryStore`
- `PostgresRelationStore`
- `PostgresVectorStore`
- `PostgresRetrievalTraceStore`
- `PostgresMigrationRunner`
- `PostgresServiceCollectionExtensions`

但 `ContextCore.Service` 当前没有引用 `ContextCore.Storage.Postgres`，`StorageExtensions` 只支持：

```text
filesystem
memory
```

PostgreSQL DI 扩展当前注册的契约包括：

- `IContextStore`
- `IContextCollectionStore`
- `IMemoryStore`
- `IRelationStore`
- `IVectorStore`
- `IRetrievalTraceStore`

相较 Service 完整运行所需能力，PostgreSQL 后端仍缺少或未接入：

- `IContextIndex`
- `IContextPackageBuildTraceStore`
- `IContextPackagePolicyStore`
- `IWorkingMemoryService`
- `IPromotionRecordStore`
- `IConstraintStore`
- `IGlobalContextStore`
- `IContextJobQueue`
- `IContextJobQueryStore`
- 文件/数据库事件日志 sink
- Service 级 `Storage:Provider=postgres` 配置和启动校验
- 真实 PostgreSQL + pgvector 集成测试

因此当前不能把 PostgreSQL 当作生产级完整 provider。直接把 `postgres` 接入 Service 会造成部分数据在 PostgreSQL、部分数据 fallback 到其他后端的数据分裂风险。

### 4.2 LLM 真实质量尚未完成业务验收

模型网关和压缩链路已经打通，但当前自动化测试主要验证配置、路由和 fallback 行为。真实 LLM 能否稳定输出高质量 JSON、能否满足上下文压缩质量要求，还需要：

- 使用真实 DeepSeek / OpenAI-compatible API 的端到端测试。
- 使用真实中文上下文样本做压缩质量评估。
- 统计 invalid JSON、timeout、fallback、重试和 RequiresReview 比例。
- 明确不同任务类型对应的模型成本和延迟。

### 4.3 Embedding 效果和性能需要真实语料验证

本地中文 embedding 模型已放入项目内，并且 ONNX Runtime 链路可用。但实际可用性仍依赖：

- 中文上下文语料的召回质量。
- query instruction 与 pooling 策略是否符合目标模型。
- 大量上下文下的构建索引耗时。
- 向量存储规模增长后的查询延迟。
- 模型首次加载耗时和内存占用。

### 4.4 集成测试覆盖还偏“工程链路”，不是生产验收

当前 137 个测试全部通过，这是很好的基础。但测试仍有明显边界：

- 没有真实 PostgreSQL/pgvector 容器级集成测试。
- 没有真实外部 LLM API 的可选集成测试。
- 没有长时间后台 Worker 稳定性测试。
- 没有多进程/多实例同时写入的压力测试。
- 没有权限、认证、错误注入、断电恢复和备份恢复测试。

### 4.5 安全和运维能力尚未闭环

当前服务缺少生产服务常见能力：

- HTTP API 认证授权。
- 多租户隔离策略。
- API rate limit。
- 审计日志查询接口。
- OpenTelemetry Collector / Seq / Grafana 接入示例。
- 数据备份与恢复流程。
- 部署配置模板。
- 版本迁移策略。

## 5. 实际生产可用缺口

如果目标是“本机或可信内网工具可用”，当前已经接近可用，建议优先使用：

```text
filesystem provider
项目内 context-core-data
ControlRoom 调试
Mock 压缩或已配置 LLM 压缩
```

如果目标是“可长期运行、可多人/多服务调用、可承载重要上下文资产的生产服务”，还需要推进以下部分：

### 高优先级缺口

1. 补齐 PostgreSQL 全量存储契约，或明确生产第一阶段只支持 FileSystem。
2. 为 Service 增加 `Storage:Provider=postgres` 前置条件校验，未完整支持前不要开放半成品 provider。
3. 增加真实 PostgreSQL + pgvector 集成测试。
4. 增加 API 认证授权，至少支持本地 token / API key。
5. 增加真实 LLM 可选集成测试和压缩质量样本集。
6. 增加 embedding 检索质量评估集。

### 中优先级缺口

1. 制定生产部署文档：端口、环境变量、私有配置、数据目录、日志目录。
2. 增加健康检查细分：存储可读写、模型可用性、embedding 模型加载、Worker 状态。
3. 增加后台作业可观测性：失败原因统计、重试次数、耗时分布。
4. 增加数据备份/恢复工具。
5. 增加 package/retrieval/compression 的真实业务样本回归测试。

### 低优先级但长期必要

1. 多租户和 workspace 隔离策略。
2. API versioning。
3. 管理端 UI 或更完整的 ControlRoom service mode。
4. 模型成本统计和预算控制。
5. 数据迁移版本化。

## 6. 风险分级

| 风险 | 等级 | 说明 | 建议 |
|---|---:|---|---|
| PostgreSQL 被误认为完整 provider | 高 | 当前 Service 未接入，且契约覆盖不足 | 报告和 TODO 中明确标记，后续单独立项 |
| 真实 LLM 输出不稳定 | 高 | 压缩质量依赖外部模型和结构化输出稳定性 | 建立真实样本评测与 fallback 指标 |
| 缺少 API 认证 | 高 | 服务一旦暴露到非本机环境会有数据风险 | 加 API key 或本地 token |
| Embedding 检索质量未知 | 中 | 链路已通，但召回质量未被真实语料证明 | 建评测集和指标 |
| FileSystem 生产规模上限未知 | 中 | 单机可用，但大规模并发和数据增长需压测 | 做容量和并发测试 |
| 运维闭环不足 | 中 | 缺部署、备份、监控文档 | 补生产运行手册 |
| 私有配置依赖本机文件 | 低 | 合理，但需要部署时标准化 | 增加环境模板与检查命令 |

## 7. 后续推进路线

### 7.1 近期：把“可用边界”定清楚

建议先做以下任务：

1. 在 TODO 清单中新增“PostgreSQL 生产化”后续阶段，不把 P3-3 的完成误解为完整 provider 完成。
2. 明确当前推荐运行模式：`filesystem`。
3. 为 `/api/status` 或 `/api/model/status` 增加更细的就绪状态，区分“服务启动成功”和“生产依赖可用”。
4. 增加一份本地运行手册，说明数据目录、私有配置、启动命令和 ControlRoom 使用方式。

### 7.2 中期：补生产级后端和真实评测

建议推进：

1. 补齐 PostgreSQL 全量 store：
   - index
   - constraint
   - global context
   - package build trace
   - package policy
   - working memory / promotion record
   - job queue / job query
   - event log
2. 增加 Testcontainers 或本机 PostgreSQL 的可选集成测试。
3. 建立真实中文上下文评测集：
   - 压缩质量评测
   - 检索召回评测
   - package 构建稳定性评测
4. 加入 API key 认证和基础权限模型。

### 7.3 长期：运维闭环和规模化

建议推进：

1. OpenTelemetry Collector / Seq / Grafana 示例配置。
2. 数据备份、恢复、迁移工具。
3. 压测脚本和容量基线。
4. 多 workspace / 多租户隔离。
5. 成本统计、模型调用预算和限流。
6. ControlRoom Service Mode 或 Web 管理端。

## 8. 验证记录

### 8.1 构建验证

执行命令：

```powershell
dotnet build ContextCore.sln -p:UseSharedCompilation=false
```

结果：

```text
已成功生成。
0 个警告
0 个错误
```

构建项目包括：

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Client`
- `ContextCore.Storage.FileSystem`
- `ContextCore.Embedding`
- `ContextCore.Storage.InMemory`
- `ContextCore.Storage.Postgres`
- `ContextCore.ModelGateway`
- `ContextCore.AppHost`
- `ContextCore.ControlRoom`
- `ContextCore.Service`
- `ContextCore.IntegrationTests`
- `ContextCore.Service.Tests`
- `ContextCore.Tests`

### 8.2 测试验证

执行命令：

```powershell
dotnet test ContextCore.sln -p:UseSharedCompilation=false --no-build
```

结果：

```text
ContextCore.IntegrationTests: 失败 0，通过 5，跳过 0，总计 5
ContextCore.Service.Tests:    失败 0，通过 2，跳过 0，总计 2
ContextCore.Tests:            失败 0，通过 130，跳过 0，总计 130
```

总计：

```text
失败 0，通过 137，跳过 0
```

### 8.3 Service API 验证范围

`ContextCore.Service.Tests` 已覆盖：

- `/api/status`
- `/api/context/ingest`
- `/api/context/query`
- `/api/jobs/compression`
- `/api/jobs/{id}`
- FileSystem provider 下的 API 写入与查询
- Job Worker 处理压缩作业并生成 summary

因此本次未额外启动独立服务进程做重复 smoke test。

### 8.4 仓库状态说明

当前目录执行 `git status --short` 返回：

```text
fatal: not a git repository (or any of the parent directories): .git
```

因此本次无法基于 Git 工作区状态判断未提交改动。报告生成过程只新增本报告文件，不修改代码文件。

## 9. 明确结论

ContextCore 当前已经具备可运行、可测试、可调试的上下文核心系统形态。对于本地、单机、可信内网、研发集成和上下文能力验证，当前版本可以开始实际试用，推荐使用 `filesystem` provider。

但它还不应被标记为完整生产可用版本。主要原因不是功能链路缺失，而是生产依赖和运行边界尚未闭环：PostgreSQL 不是完整 Service provider，真实 LLM/embedding 效果需要业务语料验证，API 缺认证授权，部署监控和备份恢复还未形成标准方案。

下一步最有价值的推进不是继续堆新功能，而是把生产边界做实：

1. 先明确 FileSystem 是当前默认可用后端。
2. 单独推进 PostgreSQL 全量 provider。
3. 建立真实中文上下文评测集。
4. 补 API 认证、部署文档、监控和备份恢复。

完成这些后，ContextCore 才能从“功能完整的研发可用系统”升级为“可长期运行的上下文基础设施”。
