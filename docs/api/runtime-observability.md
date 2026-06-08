# Runtime Observability

更新时间：2026-05-30

## 1. 目标

ContextCore Service 运行时观测接口分为三层：

- `GET /api/status`
- `GET /api/health/ready`
- `GET /api/status/deep`

三者使用统一的 runtime observability contract：

- `RuntimeStatusResponse`
- `RuntimeReadinessResponse`
- `RuntimeProbeCheckResponse`
- `ProviderCapabilityResponse`
- `RuntimeSnapshotResponse`

## 2. 三类接口职责

### 2.1 `/api/status`

用途：

- 轻量状态查看
- 默认只读
- 不执行业务存储写探针

适合：

- UI 面板轮询
- ControlRoom 状态摘要
- 人工巡检

### 2.2 `/api/health/ready`

用途：

- 运行时就绪探针
- 默认执行中等强度检查
- 允许低副作用探针
- 默认不写业务数据

当前低副作用项：

- `storage-root` 会创建并删除根目录临时探针文件

### 2.3 `/api/status/deep`

用途：

- 深度读写探针
- 允许真实写入验证
- 必须与业务数据隔离

隔离规则：

- `workspaceId=__system__`
- `collectionId=__health__`
- 使用固定 ID 覆盖

## 3. DTO

### 3.1 `RuntimeStatusResponse`

主要字段：

- `status`
- `utc`
- `storage`
- `jobs`
- `retrievalBaseline`
- `capabilities`
- `readiness`

### 3.2 `RuntimeReadinessResponse`

主要字段：

- `status`
- `message`
- `checkedAt`
- `storageProvider`
- `productionReady`
- `providerState`
- `retrievalBaseline`
- `fromCache`
- `cacheTtlSeconds`
- `probeScope`
- `capabilities`
- `checks`
- `warnings`

说明：

- `/api/health/ready` 返回该 DTO
- `/api/status/deep` 也返回该 DTO
- `/api/status` 在 `readiness` 字段中嵌套该 DTO

### 3.3 `RuntimeSnapshotResponse`

用途：

- 聚合 `status`
- 聚合 `readiness`
- 可选聚合 `deepStatus`

说明：

- 该 DTO 当前主要供 `ContextCoreClient.GetRuntimeSnapshotAsync(...)` 和 ControlRoom Service Dashboard 使用
- 它是客户端侧聚合契约，不要求服务端新增独立 endpoint
### 3.4 `RuntimeProbeCheckResponse`

字段：

- `name`
- `status`
- `message`
- `severity`
- `hasSideEffect`
- `durationMs`
- `warning`
- `detail`

### 3.5 `ProviderCapabilityResponse`

字段：

- `name`
- `state`
- `active`
- `message`

## 4. Cache 语义

### 4.1 `fromCache`

- `true`：本次响应来自 runtime probe cache
- `false`：本次响应为实时执行结果

### 4.2 `cacheTtlSeconds`

- 表示当前接口的缓存 TTL
- 当前 ready / deep 为短时缓存

当前约定：

- `status.readiness.cacheTtlSeconds = 0`
- `ready.cacheTtlSeconds = 8`
- `deep.cacheTtlSeconds = 8`

### 4.3 `refresh`

- `/api/status/deep?refresh=true` 可跳过 deep cache 强制重跑

## 5. Severity 语义

`RuntimeProbeCheckResponse.severity` 当前语义：

- `info`
- `warning`
- `error`

建议解释：

- `info`：正常或无风险说明
- `warning`：存在降级、缺配置或能力受限，但不一定阻断服务
- `error`：关键探针失败，应视为未就绪或降级

## 6. Provider Capability 语义

当前稳定约定：

- `filesystem: AlphaSupported`
- `memory: TestOnly`
- `postgres: Experimental`
- `vector-store: Missing / NotConfigured`

说明：

- `active=true` 表示当前实例正在使用该能力
- `active=false` 表示当前实例未启用，但该能力状态仍会被描述

## 7. Side Effect 语义

`RuntimeProbeCheckResponse.hasSideEffect` 表示该检查是否可能产生外部副作用。

约定：

- `status` 应尽量全部为 `false`
- `ready` 允许少量 `true`
- `deep` 应允许并明确标记 `true`

当前例子：

- `storage-root` 在 ready 中可能为 `true`
- deep 中各写探针项应为 `true`

## 8. 建议使用方式

- 面板或控制室优先调用 `/api/status`
- 健康检查调用 `/api/health/ready`
- 仅在排障时调用 `/api/status/deep`
