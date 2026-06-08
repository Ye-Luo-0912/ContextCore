# ContextCore 项目待办清单

> 生成时间：基于代码审查自动整理
> 说明：P0 = 必须完成才能稳定运行；P1 = 核心功能完整性；P2 = 生产可用；P3 = 长期维护优化

---

## 阶段一：可独立稳定运行的最低条件（P0）

这些问题不解决，项目**无法在真实场景中使用**。

### P0-1 替换 MockContextCompressor 为真实 LLM 压缩实现

- **文件**：`src/ContextCore.Core/Infrastructure/MockContextCompressor.cs`
- **问题**：当前实现仅拼接原始文本，不调用任何模型 API，生成的"摘要"无实际语义压缩效果。
- **AppHost** 中 `compressor = new MockContextCompressor()` 直接使用此类，整个压缩流程为演示性质。
- **需要**：实现 `IContextCompressor`，通过 `ConfigurableModelGateway` 调用 LLM，对输入条目执行真实摘要/压缩。

### P0-2 作业队列缺少后台处理器（Worker）

- **文件**：`src/ContextCore.Storage.FileSystem/Stores/FileContextJobQueue.cs`、`src/ContextCore.Storage.InMemory/Stores/InMemoryJobQueue.cs`
- **问题**：`IContextJobQueue` 只负责入队/出队，没有任何 Worker 从队列中取出作业并执行。ControlRoom 的 `JobMonitorScreen` 只能展示队列状态，无法触发任何实际处理。
- **需要**：实现 `IContextJobProcessor` 接口及后台 Worker（`BackgroundService` 或类似机制），能够消费并执行已排队的压缩/索引任务。

### P0-3 IContextIndex 不支持语义搜索

- **文件**：`src/ContextCore.Storage.FileSystem/Stores/FileContextIndex.cs`、`src/ContextCore.Storage.InMemory/Stores/InMemoryContextIndex.cs`
- **问题**：当前索引实现仅支持关键词字符串匹配（`Contains`），不支持向量相似度搜索，与"智能上下文检索"定位严重不符。
- **需要**：接入向量嵌入模型（通过 `IModelAdapter`），存储 embedding 向量，实现余弦相似度或近似最近邻检索。

### P0-4 没有依赖注入容器 / 托管服务框架

- **文件**：`src/ContextCore.AppHost/Program.cs`
- **问题**：所有依赖手动 `new` 创建，无法支持配置热重载、生命周期管理、多环境切换。若需要后台 Worker（P0-2）则必须先解决此项。
- **需要**：引入 `Microsoft.Extensions.Hosting`，将所有服务注册到 DI，支持 `appsettings.json` 配置。

### P0-5 没有 HTTP API 入口（若作为服务使用）

- **当前状态**：项目仅有 CLI（ControlRoom）和演示脚本（AppHost）。
- **问题**：上层系统（Agent、IDE 插件等）无法通过网络调用 ContextCore。
- **需要**：添加 ASP.NET Core Minimal API 或 gRPC 接口，暴露 Ingest / Query / BuildPackage 等核心操作。

---

## 阶段二：核心功能完整性（P1）

基本可运行后需要补全的核心能力。

### P1-1 ModelGateway 缺少真实 API Key 管理

- **文件**：`src/ContextCore.ModelGateway/Infrastructure/ModelGatewayDefaults.cs`
- **问题**：`ApiKey = "env:BIGMODEL_API_KEY"` 仅约定了一个前缀规则，但 `ConfigurableModelGateway` / `HttpChatCompletionAdapterBase` 中是否真正从环境变量读取尚未验证。
- **需要**：在 `HttpChatCompletionAdapterBase` 中完善 `env:` 前缀解析逻辑，并添加配置校验。

### P1-2 RelationBuilder 缺少对 Package Relations 的双向索引

- **文件**：`src/ContextCore.Core/Infrastructure/RelationBuilder.cs`
- **问题**：`BuildForPackage` 仅建立"包含"方向的关系，ControlRoom 关系查看器只能展示单向图，无法反向追溯。
- **需要**：补充双向关系保存逻辑，或在 `IRelationStore.QueryAsync` 中支持双向查询。

### P1-3 ContextValidationService 缺少集合级别的跨条目校验

- **文件**：`src/ContextCore.Core/Services/ContextValidationService.cs`
- **问题**：当前只做字段级空值检查，不检查重复 ID、循环引用、孤立 Ref 等结构性问题。
- **需要**：添加 `ValidateCollection(IReadOnlyList<ContextItem>)` 方法。

### P1-4 ControlRoom 报告导出功能不完整

- **文件**：`src/ContextCore.ControlRoom/Commands/ReportCommand.cs`、`src/ContextCore.ControlRoom/Screens/ReportScreen.cs`
- **问题**：`report export --out` 参数已在帮助文档中声明，但导出到文件的完整路径处理与错误反馈需验证。
- **需要**：端到端测试并修复文件写入逻辑。

### P1-5 BasicContextPackageBuilder 的 Policy 扩展点不完整

- **文件**：`src/ContextCore.Core/Services/BasicContextPackageBuilder.cs`
- **问题**：`ContextPackagePolicy.SectionPriorities` 字段已定义但 `GetPriority` 方法的回退逻辑未完全实现，自定义优先级不生效。
- **需要**：完善 `GetPriority` 实现。

---

## 阶段三：生产可用（P2）

### P2-1 缺少统一的错误处理与结构化日志

- **文件**：`src/ContextCore.Storage.FileSystem/Infrastructure/FileContextEventSink.cs`
- **问题**：日志只写入本地 JSONL 文件，无法接入 OpenTelemetry、Seq、Application Insights 等可观测性平台。
- **需要**：将 `IContextEventSink` 适配到 `ILogger<T>` 或 OpenTelemetry ActivitySource。

### P2-2 FileSystem 存储缺少并发冲突处理

- **文件**：`src/ContextCore.Storage.FileSystem/Stores/FileContextStore.cs` 等所有 Store
- **问题**：使用 `SemaphoreSlim(1,1)` 做进程内互斥，但多进程场景下会产生文件冲突。
- **需要**：引入文件锁（`FileStream` + `FileShare.None`）或迁移到 SQLite。

### P2-3 InMemory 存储无持久化，重启数据丢失

- **文件**：`src/ContextCore.Storage.InMemory/Stores/` 全部
- **问题**：内存存储仅用于测试，但 ControlRoom `--storage memory` 选项对用户可见，可能误用。
- **需要**：在 ControlRoom 中为 memory 模式添加明确的"仅用于测试"警告提示。

### P2-4 缺少集成测试覆盖

- **文件**：`tests/ContextCore.Tests/ContextCoreMvpTests.cs`
- **问题**：现有测试主要覆盖 InMemory 存储的 MVP 场景，FileSystem 存储、ModelGateway 重试/回退逻辑均无自动化测试。
- **需要**：添加 FileSystem 存储集成测试、ModelGateway 回退路径单元测试。

### P2-5 没有配置文件支持（当前全为 CLI 参数）

- **文件**：`src/ContextCore.AppHost/Program.cs`、`src/ContextCore.ControlRoom/Program.cs`
- **问题**：所有配置通过 CLI 参数传入，不支持 `appsettings.json` / 环境变量的统一配置。
- **需要**：引入 `IConfiguration` 统一读取配置，支持配置文件覆盖。

---

## 阶段四：长期维护优化（P3）

### P3-1 ContextPackagePolicy 应支持持久化存储

- **文件**：`src/ContextCore.Abstractions/Models/MemoryDtos.cs`
- **问题**：Policy 当前是内存对象，每次通过代码创建，无法在 ControlRoom 中编辑保存。
- **需要**：添加 `IContextPackagePolicyStore` 接口及实现。

### P3-2 ControlRoom 界面应支持彩色终端输出

- **文件**：`src/ContextCore.ControlRoom/Rendering/` 全部
- **问题**：当前全部使用 `Console.WriteLine` 纯文本输出，可读性差。
- **需要**：引入 Spectre.Console 或 ANSI 转义码提升可读性。

### P3-3 BasicContextPackageBuilder 中 Token 估算算法过于粗糙

- **文件**：`src/ContextCore.Core/Services/BasicContextPackageBuilder.cs`
- **问题**：`EstimateTokens` 以 `(length + 1) / 2` 估算，误差大，对中文内容尤其不准。
- **需要**：接入 tiktoken 或模型方提供的 tokenizer。

### P3-4 CompressionDtos 中 CompressionDepth / CompressionTaskKind 枚举未在压缩器中充分使用

- **文件**：`src/ContextCore.Abstractions/Models/CompressionDtos.cs`
- **问题**：枚举已定义 Light/Medium/Deep 等档位，但 `MockContextCompressor` 完全忽略了这些参数。
- **需要**：在真实压缩实现中，依据 Depth 调整 prompt 和 token 预算策略。

---

## 当前仅用于演示的组件（已在代码内标注 TODO-DEMO）

| 组件 | 文件 | 说明 |
|------|------|------|
| `MockContextCompressor` | `Core/Infrastructure/MockContextCompressor.cs` | 拼接文本，无语义压缩 |
| `MockModelAdapter` | `ModelGateway/Adapters/MockModelAdapter.cs` | 返回固定字符串，不调用任何 API |
| `SeedDemoItemsAsync` | `AppHost/Program.cs` | 注入3条硬编码演示数据 |
| `InMemoryContextIndex` 搜索 | `Storage.InMemory/Stores/InMemoryContextIndex.cs` | 仅关键词 Contains 匹配 |
| `FileContextIndex` 搜索 | `Storage.FileSystem/Stores/FileContextIndex.cs` | 仅关键词 Contains 匹配 |
| `BasicModelGateway` | `ModelGateway/Services/BasicModelGateway.cs` | 顺序遍历适配器，无路由策略 |

---

## 快速参考：项目可运行的最小路径

```
当前状态（可构建、可演示）
	│
	├─ [P0-4] 引入 IHost / DI
	├─ [P0-1] 接入真实 LLM 压缩
	├─ [P0-2] 实现 Job Worker
	└─ [P0-5] 暴露 HTTP API
		 │
		 └─ 可作为独立服务运行 ✓
			  │
			  ├─ [P1] 补全核心功能
			  └─ [P2] 生产加固
```
