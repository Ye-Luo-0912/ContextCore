# ContextCore 项目待办清单 / Roadmap

> 版本：v0.2  
> 定位：ContextCore 是一个独立运行的上下文管理基础设施，不隶属于小说生成工具、Agent 或 ChatApp。  
> 当前状态：基础项目结构、文件存储、Mock 压缩、ControlRoom 控制台面板、派生上下文隔离已初步完成。  
> 目标：逐步演进为可独立运行、可被外部系统调用、支持多层记忆、压缩、检索、固化、监控和后续向量检索的上下文核心系统。

---

## 0. 当前项目结构

```text
解决方案 'ContextCore'
├─ src
│  ├─ ContextCore.Abstractions
│  ├─ ContextCore.AppHost
│  ├─ ContextCore.ControlRoom
│  ├─ ContextCore.Core
│  ├─ ContextCore.ModelGateway
│  ├─ ContextCore.Storage.FileSystem
│  └─ ContextCore.Storage.InMemory
└─ tests
```

---

## 1. 目标项目结构

### 1.1 短期目标结构

短期只新增两个关键项目：

```text
解决方案 'ContextCore'
├─ src
│  ├─ ContextCore.Abstractions
│  ├─ ContextCore.Core
│  ├─ ContextCore.Service              # 新增：独立服务宿主
│  ├─ ContextCore.Client               # 新增：外部调用 SDK
│  ├─ ContextCore.ControlRoom
│  ├─ ContextCore.AppHost
│  ├─ ContextCore.ModelGateway
│  ├─ ContextCore.Storage.FileSystem
│  └─ ContextCore.Storage.InMemory
└─ tests
   └─ ContextCore.Tests
```

### 1.2 中长期目标结构

后续再补 PostgreSQL / pgvector 和本地 embedding 能力：

```text
解决方案 'ContextCore'
├─ src
│  ├─ ContextCore.Abstractions
│  ├─ ContextCore.Core
│  ├─ ContextCore.Service
│  ├─ ContextCore.Client
│  ├─ ContextCore.ControlRoom
│  ├─ ContextCore.AppHost
│  ├─ ContextCore.ModelGateway
│  ├─ ContextCore.Storage.FileSystem
│  ├─ ContextCore.Storage.InMemory
│  ├─ ContextCore.Storage.Postgres      # 后续新增：PostgreSQL / pgvector 后端
│  └─ ContextCore.Embedding             # 后续新增：ONNX / 本地 embedding
└─ tests
   ├─ ContextCore.Tests
   ├─ ContextCore.IntegrationTests       # 后续新增：集成测试
   └─ ContextCore.Service.Tests          # 后续新增：服务 API 测试
```

---

## 2. 项目职责边界

### 2.1 `ContextCore.Abstractions`

公共契约层。

**职责：**

- DTO
- 接口
- 枚举
- 常量
- 请求 / 响应模型
- 公共错误模型
- 公共配置模型

**不应包含：**

- 文件读写实现
- HTTP API
- 控制台 UI
- LLM 调用实现
- PostgreSQL 实现
- 具体业务领域概念，例如小说、角色、章节、伏笔

**建议目录：**

```text
ContextCore.Abstractions
├─ Contracts
│  ├─ Storage
│  ├─ Indexing
│  ├─ Retrieval
│  ├─ Packaging
│  ├─ Compression
│  ├─ Jobs
│  ├─ Models
│  ├─ Embedding
│  └─ Diagnostics
└─ Models
   ├─ Context
   ├─ Memory
   ├─ Relations
   ├─ Constraints
   ├─ Global
   ├─ Indexing
   ├─ Packaging
   ├─ Compression
   ├─ Jobs
   ├─ Models
   ├─ Embedding
   └─ Diagnostics
```

---

### 2.2 `ContextCore.Core`

核心逻辑层。

**职责：**

- 上下文摄取
- 上下文包构建
- 关系构建
- 记忆固化
- Working Memory 管理
- Stable Memory 管理
- Constraint 注入
- Global Context 选择
- Job Processor
- Compression Pipeline 编排
- Retrieval 编排
- Validation

**不应包含：**

- 文件路径细节
- PostgreSQL SQL 实现
- HTTP 端点
- 控制台渲染
- 具体模型 API 请求细节

**建议目录：**

```text
ContextCore.Core
├─ Infrastructure
│  ├─ MockContextCompressor.cs
│  ├─ SystemClock.cs
│  ├─ ChecksumService.cs
│  ├─ TokenEstimator.cs
│  └─ OperationIdGenerator.cs
└─ Services
   ├─ Ingestion
   ├─ Packaging
   ├─ Relations
   ├─ Memory
   ├─ Constraints
   ├─ Global
   ├─ Retrieval
   ├─ Compression
   ├─ Jobs
   └─ Validation
```

---

### 2.3 `ContextCore.Service`【新增项目】

独立服务宿主。

**职责：**

- 启动 ContextCore 独立服务
- 读取 `appsettings.json`
- 注册 DI
- 启动后台 Worker
- 暴露 HTTP API
- 管理服务生命周期

**建议目录：**

```text
ContextCore.Service
├─ Program.cs
├─ appsettings.json
├─ appsettings.Development.json
├─ Api
│  ├─ StatusEndpoints.cs
│  ├─ ContextEndpoints.cs
│  ├─ MemoryEndpoints.cs
│  ├─ PackageEndpoints.cs
│  ├─ CompressionEndpoints.cs
│  ├─ JobEndpoints.cs
│  ├─ RelationEndpoints.cs
│  ├─ ConstraintEndpoints.cs
│  └─ ModelEndpoints.cs
├─ Hosting
│  ├─ ContextCoreHostedService.cs
│  ├─ ContextJobWorker.cs
│  ├─ ModelHealthCheckWorker.cs
│  └─ EmbeddingWorker.cs                  # 后续阶段
├─ Configuration
│  ├─ ContextCoreOptions.cs
│  ├─ StorageOptions.cs
│  ├─ CompressionOptions.cs
│  ├─ JobWorkerOptions.cs
│  └─ ServiceOptions.cs
└─ Extensions
   ├─ ServiceCollectionExtensions.cs
   └─ WebApplicationExtensions.cs
```

---

### 2.4 `ContextCore.Client`【新增项目】

外部系统调用 SDK。

**职责：**

- 封装 ContextCore.Service 的 HTTP API
- 给小说生成工具、Agent、ChatApp、RAG 工具等外部系统调用
- 外部系统不应直接引用 `ContextCore.Core` 或 `Storage.*`

**建议目录：**

```text
ContextCore.Client
├─ ContextCoreClient.cs
├─ ContextCoreClientOptions.cs
├─ Contracts
│  ├─ SaveContextRequest.cs
│  ├─ QueryContextRequest.cs
│  ├─ BuildPackageRequest.cs
│  ├─ CompressionJobRequest.cs
│  └─ ContextCoreStatusResponse.cs
└─ Extensions
   └─ ContextCoreClientServiceCollectionExtensions.cs
```

---

### 2.5 `ContextCore.ControlRoom`

控制台控制室。

**职责：**

- 显示系统状态
- 查看上下文、记忆、任务、索引、关系、约束
- 预览上下文包
- 导出调试报告
- 后续支持 Direct File Mode 和 Service Client Mode

**不应包含：**

- 压缩算法
- 固化算法
- 检索算法
- 存储实现

**建议目录：**

```text
ContextCore.ControlRoom
├─ Program.cs
├─ Commands
├─ Rendering
├─ Screens
├─ Services
└─ Options
```

---

### 2.6 `ContextCore.AppHost`

Demo / Smoke Test 宿主。

**职责：**

- 注入演示数据
- 本地 Smoke Test
- 检查基础链路
- 不作为正式服务入口

**建议目录：**

```text
ContextCore.AppHost
├─ Program.cs
├─ Demo
│  ├─ DemoSeeder.cs
│  ├─ DemoScenarioRunner.cs
│  └─ DemoDataFactory.cs
└─ appsettings.Development.json
```

---

### 2.7 `ContextCore.ModelGateway`

模型调用与路由层。

**职责：**

- OpenAI-compatible API 适配
- GLM / DeepSeek / 本地 HTTP 模型适配
- fallback
- health check
- usage log
- API Key 解析

**不应包含：**

- 压缩 prompt 业务逻辑
- 具体上下文固化策略

**建议目录：**

```text
ContextCore.ModelGateway
├─ Adapters
│  ├─ MockModelAdapter.cs
│  ├─ OpenAiCompatibleModelAdapter.cs
│  ├─ LocalHttpModelAdapter.cs
│  ├─ BigModelAdapter.cs                 # 可选
│  └─ DeepSeekModelAdapter.cs            # 可选
├─ Services
│  ├─ ConfigurableModelGateway.cs
│  ├─ ModelRouteResolver.cs
│  ├─ ModelFallbackExecutor.cs
│  ├─ ModelHealthService.cs
│  ├─ ModelUsageLogger.cs
│  └─ ApiKeyResolver.cs
├─ Options
│  ├─ ModelGatewayOptions.cs
│  ├─ ModelEndpointOptions.cs
│  └─ ModelRouteOptions.cs
└─ Extensions
   └─ ModelGatewayServiceCollectionExtensions.cs
```

---

### 2.8 `ContextCore.Storage.FileSystem`

文件系统存储实现。

**职责：**

- 文件读写
- JSONL 存储
- 路径解析
- 文件锁
- 目录管理
- 文件存储事件日志

**建议目录：**

```text
ContextCore.Storage.FileSystem
├─ Infrastructure
│  ├─ FileStorageOptions.cs
│  ├─ FilePathResolver.cs
│  ├─ FileFormatSerializer.cs
│  ├─ FileJsonlStore.cs
│  ├─ FileLockProvider.cs                  # P2
│  └─ FileContextEventSink.cs
├─ Stores
│  ├─ FileContextStore.cs
│  ├─ FileContextCollectionStore.cs
│  ├─ FileContextIndex.cs
│  ├─ FileRelationStore.cs
│  ├─ FileMemoryStore.cs
│  ├─ FileWorkingMemoryStore.cs
│  ├─ FileStableMemoryStore.cs
│  ├─ FileConstraintStore.cs
│  ├─ FileGlobalContextStore.cs
│  ├─ FileContextPackageStore.cs
│  ├─ FileContextPackagePolicyStore.cs      # P3
│  ├─ FileRetrievalTraceStore.cs            # 后续
│  └─ FileContextJobQueue.cs
└─ Extensions
   └─ FileSystemServiceCollectionExtensions.cs
```

---

### 2.9 `ContextCore.Storage.InMemory`

内存存储实现。

**职责：**

- 单元测试
- Demo
- 临时运行
- 不用于生产

**建议目录：**

```text
ContextCore.Storage.InMemory
├─ Stores
│  ├─ InMemoryContextStore.cs
│  ├─ InMemoryContextCollectionStore.cs
│  ├─ InMemoryContextIndex.cs
│  ├─ InMemoryRelationStore.cs
│  ├─ InMemoryMemoryStore.cs
│  ├─ InMemoryConstraintStore.cs
│  ├─ InMemoryGlobalContextStore.cs
│  ├─ InMemoryContextPackageStore.cs
│  └─ InMemoryJobQueue.cs
└─ Extensions
   └─ InMemoryServiceCollectionExtensions.cs
```

---

### 2.10 `ContextCore.Storage.Postgres`【后续新增项目】

PostgreSQL / pgvector 后端。

**职责：**

- PostgreSQL metadata store
- 关系存储
- 记忆存储
- 约束存储
- 任务队列
- pgvector 向量存储
- 检索 Trace 存储

**建议目录：**

```text
ContextCore.Storage.Postgres
├─ Infrastructure
│  ├─ PostgresOptions.cs
│  ├─ NpgsqlConnectionFactory.cs
│  ├─ PostgresMigrationRunner.cs
│  └─ SqlScripts
│     ├─ 001_init.sql
│     ├─ 002_relations.sql
│     ├─ 003_memory.sql
│     ├─ 004_jobs.sql
│     └─ 005_pgvector.sql
├─ Stores
│  ├─ PostgresContextStore.cs
│  ├─ PostgresMemoryStore.cs
│  ├─ PostgresRelationStore.cs
│  ├─ PostgresConstraintStore.cs
│  ├─ PostgresContextIndex.cs
│  ├─ PostgresVectorStore.cs
│  ├─ PostgresJobQueue.cs
│  └─ PostgresRetrievalTraceStore.cs
└─ Extensions
   └─ PostgresServiceCollectionExtensions.cs
```

---

### 2.11 `ContextCore.Embedding`【后续新增项目】

本地 embedding 与向量生成层。

**职责：**

- ONNX embedding provider
- embedding job
- embedding cache
- 按需加载模型
- 空闲释放模型
- 远程 embedding provider
- mock embedding provider

**建议目录：**

```text
ContextCore.Embedding
├─ Providers
│  ├─ OnnxEmbeddingProvider.cs
│  ├─ RemoteEmbeddingProvider.cs
│  └─ MockEmbeddingProvider.cs
├─ Onnx
│  ├─ OnnxEmbeddingSessionManager.cs
│  ├─ OnnxTokenizer.cs
│  ├─ PoolingStrategy.cs
│  └─ EmbeddingNormalization.cs
├─ Services
│  ├─ EmbeddingJobService.cs
│  ├─ EmbeddingCacheService.cs
│  └─ EmbeddingModelUnloadService.cs
└─ Options
   └─ EmbeddingOptions.cs
```

---

## 3. 依赖关系规则

### 3.1 基本依赖方向

```text
ContextCore.Abstractions
  ↑
ContextCore.Core
  ↑
ContextCore.Service
```

### 3.2 具体依赖规则

```text
ContextCore.Abstractions
  - 不依赖任何 ContextCore 项目

ContextCore.Core
  - 依赖 ContextCore.Abstractions

ContextCore.Storage.FileSystem
  - 依赖 ContextCore.Abstractions

ContextCore.Storage.InMemory
  - 依赖 ContextCore.Abstractions

ContextCore.Storage.Postgres
  - 依赖 ContextCore.Abstractions

ContextCore.ModelGateway
  - 依赖 ContextCore.Abstractions

ContextCore.Embedding
  - 依赖 ContextCore.Abstractions

ContextCore.Service
  - 依赖 ContextCore.Abstractions
  - 依赖 ContextCore.Core
  - 依赖 ContextCore.Storage.FileSystem
  - 依赖 ContextCore.Storage.InMemory
  - 后续可依赖 ContextCore.Storage.Postgres
  - 依赖 ContextCore.ModelGateway
  - 后续可依赖 ContextCore.Embedding

ContextCore.Client
  - 依赖 ContextCore.Abstractions
  - 不依赖 Core
  - 不依赖 Storage

ContextCore.ControlRoom
  - 依赖 ContextCore.Abstractions
  - 可依赖 ContextCore.Client
  - 可依赖 ContextCore.Storage.FileSystem 用于 DirectFileMode

ContextCore.AppHost
  - 可依赖用于 demo 的所有项目
```

### 3.3 禁止依赖

```text
Core 不应依赖 Service
Core 不应依赖 ControlRoom
Core 不应直接依赖 FileSystem / Postgres 实现
Client 不应依赖 Storage
Storage 不应依赖 Core
ControlRoom 不应包含核心业务逻辑
Service 不应包含核心算法实现
```

---

## 4. 阶段路线图

## Phase 0：服务化地基（P0）

目标：让 ContextCore 成为可独立启动、可配置、可被外部调用、可后台处理任务的服务。

### P0-1 新增 `ContextCore.Service`

**类型：新增项目**

**任务：**

- [x] 创建 `ContextCore.Service`
- [x] 引入 `Microsoft.Extensions.Hosting`
- [x] 引入 ASP.NET Core Minimal API
- [x] 新增 `appsettings.json`
- [x] 新增 `appsettings.Development.json`
- [x] 注册 Core / Storage / ModelGateway 服务
- [x] 支持 `FileSystem` 和 `InMemory` 存储切换
- [x] 启动时输出 storage provider、root path、service url

**建议路径：**

```text
src/ContextCore.Service/
```

---

### P0-2 统一 rootPath 配置

**类型：修改现有项目**

**涉及项目：**

- `ContextCore.Service`
- `ContextCore.ControlRoom`
- `ContextCore.AppHost`
- `ContextCore.Storage.FileSystem`

**任务：**

- [x] 定义统一配置项 `Storage:RootPath`
- [x] 默认 rootPath 不再使用各项目 bin 目录
- [x] 默认值：

```text
./context-core-data
```

- [x] ControlRoom 启动时显示绝对路径
- [x] AppHost / Service / ControlRoom 默认指向同一数据目录
- [x] 支持 CLI 参数覆盖 rootPath

---

### P0-3 新增 Minimal API

**类型：新增功能**

**位置：**

```text
ContextCore.Service/Api
```

**第一版 API：**

```text
GET  /api/status

POST /api/context/ingest
POST /api/context/query
GET  /api/context/{id}

POST /api/package/build
POST /api/package/preview

POST /api/compression/sync
POST /api/jobs/compression
GET  /api/jobs
GET  /api/jobs/{id}

GET  /api/relations/{itemId}
GET  /api/constraints
GET  /api/model/status
```

**任务：**

- [x] 建立 StatusEndpoints
- [x] 建立 ContextEndpoints
- [x] 建立 PackageEndpoints
- [x] 建立 CompressionEndpoints
- [x] 建立 JobEndpoints
- [x] 建立 RelationEndpoints
- [x] 建立 ConstraintEndpoints
- [x] 建立 ModelEndpoints

---

### P0-4 新增 `ContextCore.Client`

**类型：新增项目**

**任务：**

- [x] 创建 `ContextCore.Client`
- [x] 实现 `ContextCoreClient`
- [x] 封装 HTTP 调用
- [x] 支持 Ingest / Query / BuildPackage / Jobs / Status
- [x] 支持 `HttpClientFactory`
- [x] 支持 DI 注册扩展

**建议路径：**

```text
src/ContextCore.Client/
```

---

### P0-5 实现 Job Worker 基础框架

**类型：核心能力**

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Service`
- `ContextCore.Storage.FileSystem`
- `ContextCore.Storage.InMemory`

**新增接口：**

```text
IContextJobProcessor
IContextJobDispatcher
```

**新增实现：**

```text
ContextJobDispatcher
CompressionJobProcessor
IndexBuildJobProcessor
PackageRefreshJobProcessor
ContextJobWorker : BackgroundService
```

**任务：**

- [x] Worker 从 `IContextJobQueue` Dequeue
- [x] 根据 `ContextJobKind` 分发处理器
- [x] 支持 Succeeded / Failed / WaitingRetry 状态
- [x] 支持 RetryCount / MaxRetryCount
- [x] 支持错误日志
- [x] ControlRoom 可看到 Job 状态变化

---

### P0-6 保留 MockCompressor，但标记为 Demo/Test

**类型：整理现有组件**

**涉及项目：**

- `ContextCore.Core`

**任务：**

- [x] 保留 `MockContextCompressor`
- [x] 明确注释：仅用于 demo/test
- [x] 生产配置默认不使用 mock
- [x] 配置项：

```json
{
  "compression": {
    "provider": "mock"
  }
}
```

后续可改为：

```json
{
  "compression": {
    "provider": "llm"
  }
}
```

---

## Phase 1：多层记忆与上下文核心能力（P1）

目标：让 ContextCore 从“存储框架”升级为“多层上下文记忆系统”。

### P1-1 Relation 层完善

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Storage.FileSystem`
- `ContextCore.Storage.InMemory`
- `ContextCore.ControlRoom`

**任务：**

- [x] 确认 `ContextRelation`
- [x] 确认 `ContextRelationTypes`
- [x] 完善 `IRelationStore`
- [x] 实现 `FileRelationStore`
- [x] 实现 `InMemoryRelationStore`
- [x] `RelationBuilder` 支持：
  - [x] `derived_from`
  - [x] `summarizes`
  - [x] `generated_by`
  - [x] `included_in_package`
  - [x] `related_to`
- [x] ControlRoom Relations 页面展示 incoming / outgoing relations

---

### P1-2 PackageBuilder 决策日志

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.ControlRoom`
- `ContextCore.Storage.FileSystem`

**新增模型：**

```text
ContextPackageBuildResult
ContextPackageDecision
DroppedContextItem
```

**任务：**

- [x] PackageBuilder 返回 selected items
- [x] PackageBuilder 返回 dropped items
- [x] 记录 reason
- [x] 记录 score
- [x] 记录 estimated tokens
- [x] 写入 package build trace
- [x] ControlRoom Package Preview 显示 selected / dropped reason

---

### P1-3 Working Memory

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Storage.FileSystem`
- `ContextCore.Storage.InMemory`
- `ContextCore.ControlRoom`

**任务：**

- [x] 新增 `WorkingMemoryItem`
- [x] 新增 `IWorkingMemoryService`
- [x] 实现 `WorkingMemoryService`
- [x] 支持 Add
- [x] 支持 GetRecent
- [x] 支持 Clear
- [x] 支持 active context
- [x] 文件存储路径：

```text
working/
  recent-memory.jsonl
  active-context.json
  current-task.json
```

---

### P1-4 Stable Memory 与 Memory Store

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Storage.FileSystem`
- `ContextCore.Storage.InMemory`
- `ContextCore.ControlRoom`

**新增模型：**

```text
ContextMemoryItem
ContextMemoryStatus
ContextMemoryLayer
```

**任务：**

- [x] 新增 `IMemoryStore`
- [x] 实现 `FileMemoryStore`
- [x] 实现 `InMemoryMemoryStore`
- [x] 支持 Candidate / Verified / Stable / Deprecated / Rejected
- [x] ControlRoom Memory Layers 页面显示各状态数量
- [x] 支持查看 MemoryItem 来源

---

### P1-5 Memory Promotion 固化机制

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Storage.FileSystem`
- `ContextCore.ControlRoom`

**新增模型：**

```text
ContextPromotionRecord
PromotionStrategy
```

**任务：**

- [x] 新增 `IMemoryPromotionService`
- [x] 实现 `MemoryPromotionService`
- [x] 支持 Promote
- [x] 支持 Reject
- [x] 支持 Deprecate
- [x] 记录 promotion-log.jsonl
- [x] ControlRoom 支持 memory promote / reject
- [x] 固化时保留 sourceRefs / relationRefs

---

### P1-6 Global Context

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Storage.FileSystem`
- `ContextCore.ControlRoom`

**新增模型：**

```text
ContextGlobalItem
ContextScope
```

**任务：**

- [x] 新增 `IGlobalContextStore`
- [x] 实现 `FileGlobalContextStore`
- [x] 支持 Workspace / Collection / Session / Task scope
- [x] PackageBuilder 支持注入 global context
- [x] ControlRoom Dashboard 显示 global items 数量

---

### P1-7 Constraint 层

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Storage.FileSystem`
- `ContextCore.ControlRoom`

**新增模型：**

```text
ContextConstraint
ConstraintLevel
```

**任务：**

- [x] 新增 `IConstraintStore`
- [x] 实现 `FileConstraintStore`
- [x] 支持 Hard / Soft / Runtime / System / User / Domain
- [x] PackageBuilder 强制注入 Hard Constraints
- [x] PackageBuilder 可选注入 Soft Constraints
- [x] ControlRoom Constraints 页面显示约束详情

---

### P1-8 PackagePolicy 完善

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`

**任务：**

- [x] 完善 `ContextPackagePolicy`
- [x] 完善 `SectionPriorities`
- [x] 修复 `GetPriority` 回退逻辑
- [x] 支持自定义 section 顺序
- [x] 支持每个 section token budget
- [x] 支持 include flags：
  - [x] IncludeGlobalContext
  - [x] IncludeHardConstraints
  - [x] IncludeSoftConstraints
  - [x] IncludeWorkingMemory
  - [x] IncludeStableMemory
  - [x] IncludeRecentRawContext

---

### P1-9 Collection 级校验

**涉及项目：**

- `ContextCore.Core`

**任务：**

- [x] 新增 `CollectionValidationService`
- [x] 检查重复 ID
- [x] 检查孤立 refs
- [x] 检查 derivedFrom 是否存在
- [x] 检查循环引用
- [x] 检查 relation target/source 是否存在
- [x] ControlRoom Reports 输出 validation report

---

## Phase 2：真实智能处理与模型路由（P2）

目标：引入真实 LLM 压缩，但保持 Mock 可用于测试。

### P2-1 ModelGateway API Key 与配置

**涉及项目：**

- `ContextCore.ModelGateway`
- `ContextCore.Service`

**任务：**

- [x] 实现 `ApiKeyResolver`
- [x] 支持 `env:BIGMODEL_API_KEY`
- [x] 支持 `env:DEEPSEEK_API_KEY`
- [x] 支持用户目录私有 JSON / env 配置，避免密钥写入项目仓库
- [x] 支持 `ApiProviders` / `ModelProfiles` / `Routes` 三层模型配置
- [x] 启动时校验启用模型是否有 API Key
- [x] ControlRoom Model Status 显示模型配置状态，但不显示明文 Key
- [x] `/api/model/status` 输出 API 平台、模型 Profile、路由和物化模型状态，且不显示明文 Key

---

### P2-2 ConfigurableModelGateway

**涉及项目：**

- `ContextCore.ModelGateway`

**任务：**

- [x] 实现模型路由
- [x] 支持 primary / fallback
- [x] 支持 retry
- [x] 支持 timeout
- [x] 支持 highRiskTask 不 fallback
- [x] 支持按 category / capabilities / roles / taskKinds / thinkingModes 自动解析模型
- [x] 新增 `/api/model/route/resolve` 路由预览端点
- [x] ControlRoom 展示三层模型路由与实际命中模型
- [x] 记录 usage log
- [x] 记录 fallbackUsed

---

### P2-3 OpenAI-compatible Adapter

**涉及项目：**

- `ContextCore.ModelGateway`

**任务：**

- [x] 实现 `OpenAiCompatibleModelAdapter`
- [x] 支持 GLM
- [x] 支持 DeepSeek
- [x] 支持本地 OpenAI-compatible server
- [x] 支持 JSON response format
- [x] 支持 usage 解析

---

### P2-4 LLM Context Compressor

**涉及项目：**

- `ContextCore.Core`
- `ContextCore.ModelGateway`

**任务：**

- [x] 新增 `LlmContextCompressor`
- [x] 新增 `CompressionPromptBuilder`
- [x] 支持 Summarize
- [x] 支持 ExtractKeyPoints
- [x] 支持 GenerateIndexHints
- [x] 支持 Depth：
  - [x] Light
  - [x] Normal
  - [x] Deep
  - [x] Audit
- [x] 输出结构化 JSON
- [x] 使用 `CompressionResultValidator` 校验

---

### P2-5 压缩质量报告

**涉及项目：**

- `ContextCore.Core`

**新增模型：**

```text
CompressionQualityReport
```

**任务：**

- [x] 完整性评分
- [x] 一致性评分
- [x] 可用性评分
- [x] 压缩率
- [x] 风险评分
- [x] 是否 RequiresReview
- [x] ControlRoom 显示最近压缩质量

---

## Phase 3：语义检索与向量层（P3）

目标：引入 embedding、向量存储和 Hybrid Retrieval。

### P3-1 Embedding 抽象

**涉及项目：**

- `ContextCore.Abstractions`

**新增接口：**

```text
IEmbeddingProvider
IVectorStore
IEmbeddingJobService
```

**新增模型：**

```text
EmbeddingRequest
EmbeddingResult
EmbeddingJob
VectorRecord
```

**任务：**

- [x] 新增 `IEmbeddingProvider`
- [x] 新增 `IVectorStore`
- [x] 新增 `IEmbeddingJobService`
- [x] 新增 `EmbeddingRequest`
- [x] 新增 `EmbeddingResult`
- [x] 新增 `EmbeddingJob`
- [x] 新增 `VectorRecord`

---

### P3-2 新增 `ContextCore.Embedding`

**类型：新增项目**

**任务：**

- [x] 创建 `ContextCore.Embedding`
- [x] 实现 `MockEmbeddingProvider`
- [x] 实现 `OnnxEmbeddingProvider`
- [x] 实现 `OnnxEmbeddingSessionManager`
- [x] 支持按需加载模型
- [x] 支持 idle unload
- [x] 支持 batch embedding
- [x] 支持 contentHash 缓存
- [x] 引入 `Microsoft.ML.OnnxRuntime`
- [x] 下载项目内置 `Xenova/all-MiniLM-L6-v2` 量化 ONNX 模型
- [x] 下载项目内置 `Xenova/bge-small-zh-v1.5` 中文量化 ONNX 模型
- [x] 实现 BERT WordPiece tokenizer
- [x] 实现真实 ONNX Runtime 会话工厂
- [x] 支持从模型配置推断维度、tokenizer lower case 与 pooling 策略
- [x] 添加项目内模型 smoke test

备注：当前 ONNX provider 已支持真实本地 ONNX Runtime，默认使用项目内 `src/ContextCore.Embedding/Models/bge-small-zh-v1.5/` 中文模型目录；英文场景可手动切换到 `all-MiniLM-L6-v2`；测试环境仍可通过 `IOnnxEmbeddingSessionFactory` 注入 fake session。

---

### P3-3 新增 `ContextCore.Storage.Postgres`

**类型：新增项目**

**任务：**

- [x] 创建 `ContextCore.Storage.Postgres`
- [x] 引入 Npgsql
- [x] 支持 PostgreSQL metadata store
- [x] 支持 pgvector
- [x] 实现 `PostgresVectorStore`
- [x] 实现 `PostgresRelationStore`
- [x] 实现 `PostgresMemoryStore`
- [x] 实现 `PostgresRetrievalTraceStore`

备注：已新增 `ContextCore.Storage.Postgres`，使用 Npgsql 连接 PostgreSQL。迁移 Runner 会创建 `cc_collections`、`cc_context_items`、`cc_memory_items`、`cc_relations`、`cc_vectors`、`cc_retrieval_traces`，完整 DTO 使用 `jsonb` 保存，常用筛选字段单独建列和索引；向量列使用 pgvector `vector` 类型，查询通过 `<=>` 余弦距离排序并返回 `1 - distance` 分数。当前版本提供 DI 扩展但不接入默认 FileSystem/InMemory 启动路径，避免影响现有服务运行。

---

### P3-4 Hybrid Retriever

**涉及项目：**

- `ContextCore.Core`

**任务：**

- [x] Scope Filter
- [x] Mandatory Injection
- [x] Tag / Type / Ref / Keyword recall
- [x] Relation Expansion
- [x] Vector Recall
- [x] Candidate Scoring
- [x] Deduplication
- [x] Token Budget Packing
- [x] RetrievalTrace

备注：已新增 `IContextRetriever` / `IRetrievalTraceStore`、`HybridContextRetriever`、`InMemoryVectorStore`、`FileVectorStore`、`InMemoryRetrievalTraceStore`、`FileRetrievalTraceStore`。当前 Vector Recall 支持请求直接传入 query vector；若注册 `IEmbeddingProvider`，会按 BGE query instruction 生成查询向量。

---

### P3-5 ControlRoom Retrieval Debug

**涉及项目：**

- `ContextCore.ControlRoom`

**任务：**

- [x] 显示原始 query
- [x] 显示 rewritten query
- [x] 显示候选项
- [x] 显示分数
- [x] 显示 selected items
- [x] 显示 dropped items
- [x] 显示 drop reason
- [x] 显示最终 package sections

备注：已新增 `retrieval debug` 命令与 ControlRoom 面板入口，调试视图会展示检索阶段、候选项、选中项、丢弃项、最终 ContextPackage sections 和最近 RetrievalTrace。已通过 `ContextCore.Tests` 的 Retrieval Debug 覆盖测试。

---

## Phase 4：生产加固与长期维护（P4）

### P4-1 FileSystem 并发安全

**涉及项目：**

- `ContextCore.Storage.FileSystem`

**任务：**

- [x] 引入 `FileLockProvider`
- [x] JSONL append 加锁
- [x] 多进程写入保护
- [x] 异常恢复
- [x] 损坏 JSONL 检查工具

备注：已新增 `FileSystemReader` / `FileSystemWriter` 实现读写分离；JSONL 覆盖写使用临时文件原子替换，Upsert 在写锁内完成读改写，事件日志追加统一走写入侧。`FileContextIndex`、`FileContextStore`、`FileMemoryStore` 的直接文件读写也已迁移到 Reader/Writer。新增 `FileJsonLineInspector` 用于扫描损坏 JSONL 行。

---

### P4-2 OpenTelemetry / ILogger

**涉及项目：**

- `ContextCore.Core`
- `ContextCore.Service`
- `ContextCore.Storage.FileSystem`
- `ContextCore.ModelGateway`

**任务：**

- [x] 将 `IContextEventSink` 适配到 `ILogger<T>`
- [x] 增加 ActivitySource
- [x] 保留 JSONL 日志
- [x] 支持后续接 Seq / OpenTelemetry Collector

备注：已新增 `LoggingContextEventSink` 并纳入复合事件链，`FileContextEventSink` 仍保留以持续写入 JSONL。`ContextCoreDiagnostics` 提供统一 `ActivitySource`，运行时操作、基础模型网关和可配置模型网关均写入 Activity 标签，后续可直接由 Seq 或 OpenTelemetry Collector 订阅 `ContextCore` ActivitySource 与 ILogger 输出。

---

### P4-3 集成测试

**新增测试项目：**

```text
tests/ContextCore.IntegrationTests
tests/ContextCore.Service.Tests
```

**任务：**

- [x] FileSystem 端到端测试
- [x] Service API 测试
- [x] Job Worker 测试
- [x] ModelGateway fallback 测试
- [x] PackageBuilder policy 测试
- [x] ControlRoom report export 测试

备注：已新增 `tests/ContextCore.IntegrationTests` 与 `tests/ContextCore.Service.Tests`。集成测试覆盖文件系统持久化重载、模型网关 fallback、PackageBuilder policy 顺序与预算、ControlRoom Markdown 报告导出；服务测试覆盖 `/api/status`、上下文摄取/查询、压缩后台作业入队与 worker 处理。

---

### P4-4 精确 Token 估算

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Core`
- `ContextCore.Service`
- `ContextCore.ControlRoom`

**任务：**

- [x] 保留当前粗略估算作为 fallback
- [x] 引入 tokenizer 抽象
- [x] 根据模型选择 tokenizer
- [x] PackageBuilder 使用 tokenizer 估算预算
- [x] ControlRoom 显示估算来源

备注：已新增 `IContextTokenizer` / `IContextTokenizerResolver`，默认 resolver 按 OpenAI/GPT、DeepSeek、Qwen 和未知模型选择中文友好的估算器；旧版“字符数 / 2”算法保留为 `legacy-char-half-v1` fallback。`BasicContextPackageBuilder` 会把估算源、模型名和 fallback 状态写入 package metadata；ControlRoom 仪表盘、包详情和 Markdown 报告会显示这些信息。

---

### P4-5 Policy 持久化与编辑

**涉及项目：**

- `ContextCore.Abstractions`
- `ContextCore.Storage.FileSystem`
- `ContextCore.ControlRoom`

**任务：**

- [x] 新增 `IContextPackagePolicyStore`
- [x] 支持保存 policy
- [x] 支持加载 policy
- [x] ControlRoom 查看 policy
- [x] 后续支持编辑 policy

备注：已新增 `FileContextPackagePolicyStore` / `InMemoryContextPackagePolicyStore`，文件系统策略保存到集合目录 `packages/policies.jsonl`。ControlRoom 支持 `policy list`、`policy show <id>`、`policy save-default <id>` 和 `policy edit <id>`；`package-preview --policy <id>` 可直接加载已保存策略。测试覆盖内存策略加载、文件系统策略持久化重载/查询、ControlRoom policy 编辑命令。

---

## 5. 当前 Demo 组件处理策略

| 组件 | 当前用途 | 后续处理 |
|---|---|---|
| `MockContextCompressor` | Demo / Test | 保留，但生产配置禁用 |
| `MockModelAdapter` | Demo / Test | 保留 |
| `SeedDemoItemsAsync` | AppHost 演示 | 移到 `DemoSeeder` |
| `InMemoryContextIndex` | 测试 | 保留 |
| `FileContextIndex` | 轻量索引 | 后续增强，不直接替代为向量 |
| `BasicModelGateway` | 简单模型调用 | 后续由 `ConfigurableModelGateway` 替代 |
| `AppHost` | 演示入口 | 降级为 Smoke Test，不作为正式入口 |

---

## 6. 近期优先级清单

### 最高优先级

```text
[x] 新增 ContextCore.Service
[x] 新增 ContextCore.Client
[x] 引入 Host / DI / Configuration
[x] 统一 rootPath
[x] 新增 Minimal API
[x] 实现 Job Worker 基础框架
```

### 第二优先级

```text
[x] Relation 层完善
[x] PackageBuilder 决策日志
[x] Working Memory
[x] Stable Memory
[x] Memory Promotion
[x] Constraint Store
[x] Global Context Store
```

### 第三优先级

```text
[x] ModelGateway 真实 API 适配
[x] LlmContextCompressor
[x] CompressionQualityReport
[x] Model Status / Usage Log
```

### 第四优先级

```text
[x] IEmbeddingProvider
[x] OnnxEmbeddingProvider
[x] PostgreSQL + pgvector
[x] HybridRetriever
[x] RetrievalTrace
```

---

## 7. 最小可运行目标

服务化后的最小目标：

```text
contextcore service 启动
  ↓
读取 appsettings.json
  ↓
注册 FileSystem Storage
  ↓
外部 POST /api/context/ingest 写入上下文
  ↓
POST /api/package/build 构建上下文包
  ↓
POST /api/jobs/compression 创建压缩任务
  ↓
Worker 消费任务
  ↓
生成 summary
  ↓
写入 store
  ↓
ControlRoom 可看到 item / job / package / relation / log
```

---

## 8. 架构原则

1. **ContextCore 独立运行。**  
   外部项目通过 API / Client 调用。

2. **Core 不依赖具体存储。**  
   Core 只依赖 Abstractions。

3. **Storage 只做存储实现。**  
   不放压缩、固化、检索策略。

4. **ControlRoom 只做观察和管理。**  
   不放核心算法。

5. **AppHost 只做演示。**  
   正式入口是 `ContextCore.Service`。

6. **Mock 保留用于测试。**  
   不要删除 Mock，但生产配置默认不用。

7. **向量检索不是第一优先级。**  
   先做好规则检索、关系、分层记忆、Package Trace。

8. **上下文包构建必须可解释。**  
   每次选中和丢弃都应有 reason。

9. **压缩结果不能直接污染长期记忆。**  
   LLM 输出先进入 Candidate，再经 Promotion 固化。

10. **模型调用通过 ModelGateway。**  
    不在业务逻辑里写死 GLM、DeepSeek 或本地模型。


