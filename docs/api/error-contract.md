# Error Contract

更新时间：2026-05-30

## 1. 目标

ContextCore Service 对外统一错误响应契约，避免不同 endpoint 返回不同 shape 的匿名错误 body。

当前约束：

- 成功响应优先使用具体 DTO，Phase 1 见 [service-success-responses-phase1.md](service-success-responses-phase1.md)
- 非成功 HTTP 响应优先返回 `ContextCoreErrorResponse`
- 成功响应保持原有业务 DTO
- `duplicate` 不是错误，继续返回 `200 + ContextInputIngestionResult`

## 2. Error Response Shape

响应模型：`ContextCoreErrorResponse`

| 字段 | 类型 | 说明 |
|---|---|---|
| `operationId` | `string` | 本次请求的操作 ID；为空时服务端生成。 |
| `errorCode` | `string` | 稳定错误码。 |
| `message` | `string` | 面向调用方的主错误消息。 |
| `target` | `string?` | 出错目标，例如 `context.ingest`。 |
| `traceId` | `string` | 当前请求 traceId 或 ASP.NET trace 标识。 |
| `details` | `ContextCoreErrorDetail[]` | 结构化明细。 |
| `warnings` | `string[]` | 预留警告列表。 |

明细模型：`ContextCoreErrorDetail`

| 字段 | 类型 | 说明 |
|---|---|---|
| `code` | `string` | 明细级错误码。 |
| `field` | `string?` | 字段名。 |
| `target` | `string?` | 明细目标。 |
| `message` | `string` | 明细消息。 |

## 3. Error Codes

当前稳定错误码：

- `validation_failed`
- `invalid_request`
- `not_found`
- `storage_unavailable`
- `store_write_failed`
- `misconfigured`
- `internal_error`

## 4. HTTP Status 语义

| HTTP Status | ErrorCode | 说明 |
|---|---|---|
| `400` | `validation_failed` | 输入校验失败。 |
| `400` | `invalid_request` | 请求参数或路由参数不合法。 |
| `404` | `not_found` | 资源不存在。 |
| `409` | `invalid_request` | 请求状态冲突，例如作业状态不允许重入队。 |
| `503` | `storage_unavailable` | 存储或依赖不可用、超时或未就绪。 |
| `500` | `store_write_failed` | 存储写入失败。 |
| `500` | `misconfigured` | 服务配置或依赖注册异常。 |
| `500` | `internal_error` | 未分类内部异常。 |

## 5. Context Ingest 约定

适用端点：

- `POST /api/context/ingest`
- `POST /api/admin/ingest`

约定：

- 成功：返回 `200 + ContextInputIngestionResult`
- 去重命中：仍返回 `200 + ContextInputIngestionResult`
- 校验失败：返回 `400 + ContextCoreErrorResponse`
- 两个 ingest 入口使用同一套错误响应 shape

## 6. 示例

### 6.1 validation failure

```json
{
  "operationId": "ctx-op-001",
  "errorCode": "validation_failed",
  "message": "Input validation failed.",
  "target": "context.ingest",
  "traceId": "00-6fd5f5d9b0e74ad3a7e7f2f0f7f2d8ab-5d7c81f6c5d6a7e2-01",
  "details": [
    {
      "code": "ContentRequired",
      "field": "Content",
      "target": "context.ingest",
      "message": "Content is required unless ContentFormat is BinaryRef."
    }
  ],
  "warnings": []
}
```

### 6.2 not found

```json
{
  "operationId": "generated-operation-id",
  "errorCode": "not_found",
  "message": "未找到上下文条目：item-404",
  "target": "context.get",
  "traceId": "trace-id",
  "details": [
    {
      "code": "context_item_not_found",
      "field": null,
      "target": "context.get",
      "message": "未找到上下文条目：item-404"
    }
  ],
  "warnings": []
}
```

### 6.3 duplicate is not an error

```json
{
  "item": {
    "id": "existing-item-id"
  },
  "created": false,
  "deduped": true,
  "contentHash": "hash-value",
  "sequenceId": 12,
  "operationId": "ctx-op-002"
}
```

说明：

- `deduped=true` 仍然是成功响应
- 不返回 `ContextCoreErrorResponse`
