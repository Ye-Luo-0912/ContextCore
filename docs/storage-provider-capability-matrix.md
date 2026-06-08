# ContextCore 存储 Provider 能力矩阵

生成时间：2026-05-25  
适用阶段：A0 Alpha 可用边界固化

## 1. 结论

当前推荐持久化后端：

```text
ContextCore.Service + FileSystem Storage + 项目内 context-core-data
```

Provider 状态：

| Provider | 当前定位 | Service-ready | 持久化 | 说明 |
|---|---|---:|---:|---|
| FileSystem | Alpha 推荐后端 | 是 | 是 | 当前覆盖服务运行所需主要契约，适合本机和可信内网试运行。 |
| InMemory | 测试/临时后端 | 是 | 否 | 仅用于单元测试、Demo、临时验证，进程重启后数据丢失。 |
| PostgreSQL | Experimental / Partial | 否 | 部分 | 已有部分 store 和 pgvector 能力，但未覆盖完整服务契约，不允许作为完整 Service provider 启动。 |

## 2. 契约覆盖

状态说明：

| 标记 | 含义 |
|---|---|
| Supported | 当前实现覆盖该契约。 |
| Partial | 有部分实现或后端项目能力，但不足以声明完整服务可用。 |
| Missing | 当前 provider 未覆盖该契约。 |
| Test-only | 仅适合测试或临时验证。 |
| Service-ready | 可作为当前 Service 启动路径的一部分。 |

| 契约 / 能力 | FileSystem | InMemory | PostgreSQL |
|---|---|---|---|
| `IContextStore` | Supported / Service-ready | Supported / Test-only | Supported / Partial |
| `IContextCollectionStore` | Supported / Service-ready | Supported / Test-only | Supported / Partial |
| `IContextIndex` | Supported / Service-ready | Supported / Test-only | Missing |
| `IContextPackageBuildTraceStore` | Supported / Service-ready | Missing | Missing |
| `IContextPackagePolicyStore` | Supported / Service-ready | Supported / Test-only | Missing |
| `IMemoryStore` | Supported / Service-ready | Supported / Test-only | Supported / Partial |
| `IWorkingMemoryService` | Supported / Service-ready | Supported / Test-only | Missing |
| `IPromotionRecordStore` | Supported / Service-ready | Supported / Test-only | Missing |
| `IConstraintStore` | Supported / Service-ready | Supported / Test-only | Missing |
| `IGlobalContextStore` | Supported / Service-ready | Supported / Test-only | Missing |
| `IContextJobQueue` | Supported / Service-ready | Supported / Test-only | Missing |
| `IContextJobQueryStore` | Supported / Service-ready | Supported / Test-only | Missing |
| `IRelationStore` | Supported / Service-ready | Supported / Test-only | Supported / Partial |
| `IVectorStore` | Supported / Service-ready | Supported / Test-only | Supported / Partial |
| `IRetrievalTraceStore` | Supported / Service-ready | Supported / Test-only | Supported / Partial |
| `EventLogSink` | Supported / Service-ready | Missing | Missing |
| Migration history store | 不适用 | 不适用 | Missing |

## 3. Provider 使用边界

### FileSystem

推荐用途：

- 本机 Alpha 运行。
- 可信内网试运行。
- 上下文包构建与检索调试。
- ControlRoom 本地管理。
- 真实中文样本评测的初始后端。

限制：

- 需要持续验证多进程并发、数据增长后的查询性能和备份恢复流程。
- 不适合直接声明为多租户生产数据库。

### InMemory

推荐用途：

- 单元测试。
- API 测试。
- Demo。
- 临时验证。

限制：

- 不持久化。
- 不应承载真实上下文资产。
- 不应作为长期运行服务后端。

### PostgreSQL

当前用途：

- 后续生产化开发基础。
- pgvector 能力验证。
- 部分 store 的工程验证。

限制：

- 当前 `ContextCore.Service` 不引用 `ContextCore.Storage.Postgres`。
- 当前 `Storage:Provider=postgres` 会 fail-fast。
- 不允许 fallback 到 FileSystem 或 InMemory 混合运行，避免数据分裂。

## 4. 下一步

PostgreSQL 要进入 Service-ready 状态，至少需要补齐：

- `IContextIndex`
- `IContextPackageBuildTraceStore`
- `IContextPackagePolicyStore`
- `IWorkingMemoryService`
- `IPromotionRecordStore`
- `IConstraintStore`
- `IGlobalContextStore`
- `IContextJobQueue`
- `IContextJobQueryStore`
- `EventLogSink`
- Migration history store
- PostgreSQL + pgvector 真实集成测试

完成前，默认生产边界继续以 FileSystem Alpha 模式为准。
