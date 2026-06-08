# Service Success Responses

更新时间：2026-05-30

## 1. 范围

当前 Service 对外成功响应已统一为显式 DTO。

已收口的主要端点：

- `GET /api/admin/status`
- `GET /api/admin/backup/status`
- `POST /api/admin/backup/create`
- `GET /api/admin/backup/validate`
- `GET /api/admin/schema-version`
- `GET /api/health/live`
- `GET /api/health/ready`
- `GET /api/jobs/stats`
- `GET /api/jobs/dead-letter`
- `POST /api/jobs/{id}/requeue`
- `POST /api/model/route/resolve`
- `GET /api/model/status`
- `GET /api/status`
- `GET /api/status/deep`
- `GET /api/relations/{workspaceId}/{collectionId}/{itemId}`
- `GET /api/relations/{itemId}`

说明：

- `POST /api/package/build`
- `POST /api/package/build-detailed`
- `POST /api/package/preview`

以上三个 package endpoint 在本轮之前已经使用显式 DTO，无需再改。

## 2. 成功响应 DTO

| Endpoint | 成功 DTO |
|---|---|
| `/api/admin/status` | `ContextCoreAdminStatusResponse` |
| `/api/admin/backup/status` | `ContextCoreBackupStatusResponse` |
| `/api/admin/backup/create` | `ContextCoreBackupCreateResponse` |
| `/api/admin/backup/validate` | `ContextCoreBackupValidateResponse` |
| `/api/admin/schema-version` | `ContextCoreSchemaVersionResponse` |
| `/api/health/live` | `ContextCoreHealthLiveResponse` |
| `/api/health/ready` | `RuntimeReadinessResponse` |
| `/api/jobs/stats` | `ContextCoreJobStatsResponse` |
| `/api/jobs/dead-letter` | `ContextCoreDeadLetterJobsResponse` |
| `/api/jobs/{id}/requeue` | `ContextCoreRequeueJobResponse` |
| `/api/model/route/resolve` | `ContextCoreModelRouteResolveResponse` |
| `/api/model/status` | `ContextCoreModelStatusResponse` |
| `/api/status` | `RuntimeStatusResponse` |
| `/api/status/deep` | `RuntimeReadinessResponse` |
| `/api/relations/*` | `ContextCoreRelationLookupResponse` |

## 3. 统一约定

- 成功响应使用具体 DTO。
- 错误响应统一使用 `ContextCoreErrorResponse`。
- `duplicate` 不属于错误，仍然返回成功 DTO。
- 不改变既有 HTTP status code 语义。
- `src/ContextCore.Service/Endpoints` 中原有匿名成功响应已完成收口。
