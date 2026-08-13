# service/

这不是 `src/ContextCore.Service` 源码。这里是宿主相关的契约快照和旧冒烟报告。

| 路径 | 角色 |
| --- | --- |
| [`openapi/`](openapi/) | **现行 OpenAPI 快照**。`OpenApiSnapshotDriftTests` 对比 `service/openapi/service-api.openapi.json` |
| [`hosted/`](hosted/) 与本目录其它报告 | 历史部署/安全/foundation 冒烟，不当现行依据 |

跑服务：`dotnet run --project src/ContextCore.Service`。
现行调用链：[`../docs/LIVE_PATH.md`](../docs/LIVE_PATH.md)。
