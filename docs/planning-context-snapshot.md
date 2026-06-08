# Planning Context Snapshot

更新时间：2026-06-05

## 目标

Context Planning Snapshot 是规划层的只读输入快照，用于把当前任务、决策、开放问题、稳定约束、稳定偏好、决策记录和 learning signals 汇总给后续 planning 组件。

P1 只做基础输入视图，不改变 retrieval scoring，不接 vector，不做 layered retrieval，不启用 LLM router，也不让 planning snapshot 影响 package 结果。

P2 在 snapshot 之上新增 Retrieval Plan Proposal 只读预览：它根据 snapshot 和当前输入生成规则型 proposal，但不执行 retrieval，不写入 `ContextRetrievalRequest.Plan`，不改变 retrieval/package 输出。

P3 将 proposal 接入 shadow retrieval execution：shadow 路径只生成 comparison report，正式输出仍使用 legacy hybrid retrieval。

## DTO

`ContextPlanningSnapshot` 字段：

- `WorkspaceId`
- `CollectionId`
- `SessionId`
- `ActiveTasks`
- `RecentDecisions`
- `OpenQuestions`
- `KnownIssues`
- `StableConstraints`
- `StablePreferences`
- `DecisionRecords`
- `LearningSignalsSummary`
- `PolicyVersion`
- `CreatedAt`

`ContextPlanningProposalRequest` 字段：

- `WorkspaceId`
- `CollectionId`
- `SessionId`
- `CurrentInput`
- `Mode`

`RetrievalPlanProposal` 字段：

- `OperationId`
- `WorkspaceId`
- `CollectionId`
- `Intent`
- `Mode`
- `UseExact`
- `UseKeyword`
- `UseShortTermMemory`
- `UseWorkingMemory`
- `UseStableMemory`
- `UseRelations`
- `UseVector`
- `AuditMode`
- `ConflictMode`
- `KeywordTopK`
- `MemoryTopK`
- `RelationTopK`
- `VectorTopK`
- `FinalTopK`
- `Confidence`
- `Reasons`
- `Warnings`

P3 新增：

- `ShadowRetrievalResult`
- `ShadowRetrievalComparisonReport`
- `ShadowRetrievalComparisonItem`
- `ShadowRetrievalRankDelta`

当前 `PolicyVersion` 为：

- `context-planning-snapshot-policy/v1`

当前 proposal policy reason 为：

- `retrieval-plan-proposal-policy/v1`

## 聚合来源

`PlanningSnapshotService` 从现有 store 只读聚合：

- `IShortTermMemoryStore.GetSummaryAsync`
  - active tasks
  - recent decisions
  - open questions
  - known issues
- `IMemoryStore.QueryAsync`
  - stable preferences
  - decision records
- `IConstraintStore.QueryAsync`
  - stable constraints
- `IContextLearningStore.QueryRecordsAsync`
- `IContextLearningStore.QueryCasesAsync`
  - learning signals summary

该服务不新增存储，不写任何记录，不参与 retrieval/package 选择。

`RetrievalPlanProposalService` 只读调用 `PlanningSnapshotService`，再用规则型 `PlanningIntentDetector` 判断：

- `CurrentTask`
- `AuditDeprecated`
- `ConflictCheck`
- `CodingTask`
- `NovelGeneration`
- `AutomationRecovery`
- `LongTermPreference`
- `FuzzyQuestion`

P2 proposal 固定 `UseVector=false`、`VectorTopK=0`，只输出 preview 参数和原因，不执行任意 retrieval channel。

`ShadowRetrievalPlanExecutor` 在 P3 中复用现有 retrieval channel executors 和 `RetrievalPackingPolicy`，按 proposal flags 运行 shadow request。Shadow request 强制关闭 vector，非法 proposal fallback 到 legacy request flags，并记录 warning。

## HTTP Endpoint

```http
GET /api/context/planning/snapshot?workspaceId={workspaceId}&collectionId={collectionId}&sessionId={sessionId}
```

参数：

- `workspaceId` 必填
- `collectionId` 可选
- `sessionId` 可选

返回：

- `ContextPlanningSnapshot`

错误：

- 缺少 `workspaceId` 返回 `ContextCoreErrorResponse`
- store 异常走统一 error mapper

```http
POST /api/context/planning/propose
```

请求：

- `ContextPlanningProposalRequest`

返回：

- `RetrievalPlanProposal`

该 endpoint 只做 proposal preview，不触发 retrieval、不构建 package、不写入存储。

P3 暂不新增 Service endpoint；shadow execution 先通过 eval CLI 和 Core executor 验证。

## Client

`ContextCoreClient` 新增：

```csharp
Task<ContextPlanningSnapshot> GetPlanningSnapshotAsync(
    string workspaceId,
    string? collectionId = null,
    string? sessionId = null,
    CancellationToken cancellationToken = default)
```

```csharp
Task<RetrievalPlanProposal> ProposeRetrievalPlanAsync(
    ContextPlanningProposalRequest request,
    CancellationToken cancellationToken = default)
```

## ControlRoom

Service Mode 新增 Planning Snapshot 只读页面：

- Service Dashboard 输入 `X`
- 主 Dashboard Service Mode 输入 `X` 或 `28`

页面展示：

- active tasks
- recent decisions
- open questions
- known issues
- stable constraints
- stable preferences
- decision records
- learning signals summary

页面不做编辑、不生成 plan、不调用 package build、不影响 retrieval。

Service Mode 新增 Planning Proposal 只读页面：

- Service Dashboard 输入 `F`
- 主 Dashboard Service Mode 输入 `F` 或 `29`

页面输入 current input 后展示：

- proposed intent / mode
- proposed channels
- TopK
- reasons / warnings

页面不做配置编辑，不执行 retrieval，不影响输出顺序或 selected set。

## Eval CLI

P3 新增 planning-shadow；P4 新增 validator 与 diff triage：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow
dotnet run --project src\ContextCore.ControlRoom -- eval planning-shadow --include-batches
```

输出：

- `eval/planning-shadow-comparison-a3.json`
- `eval/planning-shadow-comparison-extended.json`
- `eval/planning-shadow-diff-triage-a3.json`
- `eval/planning-shadow-diff-triage-extended.json`

comparison report 包含 selected set diff、added/dropped、mustHit delta、mustNotHit violation、lifecycle violation、constraint/entity/uncertainty delta、budget pressure delta 和 rank delta。

diff triage report 逐样本输出 legacy/shadow selected、added/dropped、mustNotHitAdded、mustHitDropped、lifecycleRiskAdded、channel plan、channel TopK、suspectedCause 和 suggestedFix。

## 测试

新增覆盖：

- snapshot includes active tasks
- snapshot includes stable constraints
- snapshot includes decision records
- snapshot includes learning feedback summary
- service endpoint returns snapshot
- planning proposal endpoint returns proposal
- `ContextCoreClient.GetPlanningSnapshotAsync(...)` route
- `ContextCoreClient.ProposeRetrievalPlanAsync(...)` route
- ControlRoom renders planning snapshot
- ControlRoom renders planning proposal
- Service Mode input exposes Planning Snapshot
- Service Mode input exposes Planning Proposal
- audit query proposes `AuditDeprecated`
- conflict query proposes `ConflictCheck`
- current task query proposes `CurrentTask`
- coding query proposes `CodingTask`
- novel query proposes `NovelGeneration`
- automation failure query proposes `AutomationRecovery`
- proposal includes snapshot context
- proposal does not affect retrieval output
- shadow execution does not affect legacy output
- invalid proposal falls back safely
- invalid proposal falls back to LegacySafePlan
- validator rejects non-audit deprecated plan
- validator rejects rejected lifecycle plan
- validator forces vector disabled
- lifecycle filter still applies in shadow
- mustNotHit violation is reported
- mustNotHit added by shadow is reported in diff triage
- comparison report contains added/dropped/rank delta
- A3 planning-shadow runs successfully
