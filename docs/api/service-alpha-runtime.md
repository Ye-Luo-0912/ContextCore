# Service Alpha Runtime

更新时间：2026-06-02

## 1. 当前目标

当前 Service Alpha runtime 说明覆盖：

- `/api/status`
- `/api/health/ready`
- `/api/status/deep`
- 短期记忆 archive / compaction / maintenance

## 2. 本地启动

```powershell
dotnet run --project src\ContextCore.Service\ContextCore.Service.csproj
```

项目内数据目录：

```powershell
dotnet run --project src\ContextCore.Service\ContextCore.Service.csproj -- --root .\context-core-data
```

## 3. Runtime Observability

当前三类运行时接口：

- `/api/status`：轻量只读
- `/api/health/ready`：低副作用 readiness probe
- `/api/status/deep`：隔离到 `__system__/__health__` 的深度 probe

这三类响应现在都可带 `shortTermMaintenance`：

- `enabled`
- `isRunning`
- `runOnStartup`
- `intervalSeconds`
- `lastError`
- `lastRun`

## 4. Short-Term Maintenance Worker

配置项：

- `ShortTermMaintenance:Enabled`
- `ShortTermMaintenance:RunOnStartup`
- `ShortTermMaintenance:IntervalSeconds`

当前行为：

- 默认关闭
- 开启后按 scope 周期性执行 `Trigger=Scheduled` 的 short-term compact
- 不做 purge
- 不做 promotion

## 5. Short-Term Runtime API

只读：

- `GET /api/memory/short-term/raw`
- `GET /api/memory/short-term/working`
- `GET /api/memory/short-term/summary`
- `GET /api/memory/short-term/archive/summary`
- `GET /api/memory/short-term/archive/items`
- `GET /api/memory/short-term/compact/runs`
- `GET /api/memory/short-term/compact/runs/{runId}`

维护：

- `POST /api/memory/short-term/compact`

## 6. 最小验证

建议验证顺序：

1. `GET /api/status`
2. `GET /api/health/ready`
3. `POST /api/context/ingest`
4. `POST /api/memory/short-term/compact`
5. `GET /api/memory/short-term/archive/items`
6. `GET /api/memory/short-term/compact/runs`

## 7. 当前边界

当前仍不覆盖：

- vector 真实接入
- retrieval scoring 调整
- layered retrieval
- NamedPipe
- PostgreSQL 短期记忆后端
