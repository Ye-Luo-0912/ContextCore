# Context Input API

更新时间：2026-05-29

## 1. 入口约定

推荐业务入口：

- `POST /api/context/ingest`
  - 推荐给业务调用方使用
  - 显式支持 `ContextInputCommand`
  - 旧 `ContextItem` 请求体仅作兼容保留

管理/调试入口：

- `POST /api/admin/ingest`
  - 推荐给管理、运维、调试脚本使用
  - 只接收 `ContextInputCommand`

两者共同点：

- 都走同一条 `ContextInputCommand` pipeline
- 都会执行 normalizer / validator / contentHash / sequenceId / dedupe
- 成功返回 `ContextInputIngestionResult`

## 2. ContextInputCommand

字段说明：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---:|---|
| `operationId` | `string` | 否 | 调用方提供的操作 ID；为空时服务端生成。 |
| `workspaceId` | `string` | 是 | 工作区 ID。 |
| `collectionId` | `string` | 是 | 集合 ID。 |
| `source` | `string` | 是 | 输入来源，例如 `chat`、`service-test`、`admin`。 |
| `inputKind` | `string` | 是 | 输入类型，例如 `note`、`task`、`rule`。 |
| `contentFormat` | `enum` | 否 | 默认 `PlainText`。 |
| `content` | `string` | 是 | 输入正文；非 `BinaryRef` 时不能为空白。 |
| `sessionId` | `string?` | 否 | 会话 ID，会写入 metadata。 |
| `mode` | `string?` | 否 | 模式标记，会写入 metadata。 |
| `sourceRefs` | `string[]` | 否 | 来源引用；为空时会按 `source` 自动补默认值。 |
| `metadata` | `Dictionary<string,string>` | 否 | 额外元数据，服务端会保留。 |

## 3. ContextInputIngestionResult

字段说明：

| 字段 | 类型 | 说明 |
|---|---|---|
| `item` | `ContextItem` | 最终持久化或命中的上下文条目。 |
| `created` | `bool` | 本次是否创建了新条目。 |
| `deduped` | `bool` | 本次是否命中了幂等去重。 |
| `contentHash` | `string` | 输入内容的稳定哈希。 |
| `sequenceId` | `long` | 当前 `workspaceId + collectionId` 下的顺序号。 |
| `operationId` | `string` | 最终生效的操作 ID。 |

## 4. 幂等规则

幂等判断规则：

- `same sourceRef + same contentHash`：直接去重，返回已存在条目
- `different sourceRef + same contentHash`：创建新条目

说明：

- 当前去重粒度是 `workspaceId + collectionId + sourceRef + contentHash`
- `contentHash` 由输入内容计算
- 返回 `deduped=true` 时，`item.id` 为已有条目 ID

## 5. sequenceId / operationId / createdAt

规则说明：

- `sequenceId`
  - 在单进程内按 `workspaceId + collectionId` 单调递增
  - 写入条目 metadata：`sequenceId`

- `operationId`
  - 请求提供则透传
  - 未提供则服务端生成
  - 写入条目 metadata：`operationId`

- `createdAt`
  - 新条目由输入 pipeline/ingestion service 赋值
  - legacy `ContextItem` 请求若带时间戳，会通过兼容层保留

## 6. 校验失败

校验失败行为：

- 返回 `400 BadRequest`
- 错误体使用统一 `ContextCoreErrorResponse` 契约
- `duplicate` 不属于错误，不会返回该契约
- 详细字段定义见 [error-contract.md](error-contract.md)

```json
{
  "operationId": "ctx-op-001",
  "errorCode": "validation_failed",
  "message": "Input validation failed.",
  "target": "context.ingest",
  "traceId": "trace-id",
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

常见失败原因：

- `workspaceId` 为空
- `collectionId` 为空
- `source` 为空
- `inputKind` 为空
- `content` 为空白且 `contentFormat != BinaryRef`

## 7. 示例

### 7.1 新建输入

请求：

```json
{
  "operationId": "ctx-op-001",
  "workspaceId": "workspace-test",
  "collectionId": "collection-test",
  "source": "chat",
  "inputKind": "note",
  "content": "这是一次新的上下文输入。",
  "sourceRefs": ["source:chat-001"],
  "metadata": {
    "custom": "preserved"
  }
}
```

响应：

```json
{
  "item": {
    "id": "generated-or-legacy-id",
    "workspaceId": "workspace-test",
    "collectionId": "collection-test",
    "type": "note",
    "content": "这是一次新的上下文输入。",
    "sourceRefs": ["source:chat-001"],
    "metadata": {
      "custom": "preserved",
      "source": "chat",
      "inputKind": "note",
      "contentHash": "…",
      "sequenceId": "1",
      "operationId": "ctx-op-001"
    }
  },
  "created": true,
  "deduped": false,
  "contentHash": "…",
  "sequenceId": 1,
  "operationId": "ctx-op-001"
}
```

### 7.2 重复输入

请求：

```json
{
  "workspaceId": "workspace-test",
  "collectionId": "collection-test",
  "source": "chat",
  "inputKind": "note",
  "content": "这是一次新的上下文输入。",
  "sourceRefs": ["source:chat-001"]
}
```

响应要点：

- `created = false`
- `deduped = true`
- `item.id` 返回已存在条目 ID

### 7.3 legacy ContextItem 兼容

请求：

```json
{
  "id": "legacy-item-1",
  "workspaceId": "workspace-test",
  "collectionId": "collection-test",
  "type": "note",
  "content": "legacy item body",
  "sourceRefs": ["source:legacy-1"]
}
```

说明：

- 该请求体仍可发送到 `POST /api/context/ingest`
- 服务端会先适配成 `ContextInputCommand`
- 返回体仍是 `ContextInputIngestionResult`

### 7.4 校验失败

请求：

```json
{
  "workspaceId": "workspace-test",
  "collectionId": "collection-test",
  "source": "chat",
  "inputKind": "note",
  "content": " "
}
```

响应：

```json
{
  "operationId": "generated-or-request-operation-id",
  "errorCode": "validation_failed",
  "message": "Input validation failed.",
  "target": "context.ingest",
  "traceId": "trace-id",
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
