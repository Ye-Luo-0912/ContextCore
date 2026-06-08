# Retrieval Plan Proposal

更新时间：2026-06-05

## 目标

Phase P2 新增 Context Planning -> Retrieval Plan Preview。它基于 `ContextPlanningSnapshot` 和当前输入生成 `RetrievalPlanProposal`，用于展示可能的 intent、mode、召回通道和 TopK 参数。

该能力只做 preview：

- 不执行 retrieval
- 不创建或传递执行型 `RetrievalPlan`
- 不改变 retrieval scoring
- 不改变 `PackingPolicy`
- 不接 vector
- 不做 layered retrieval
- 不启用 LLM router
- 不影响 package 输出

## DTO

请求 DTO：`ContextPlanningProposalRequest`

- `WorkspaceId`
- `CollectionId`
- `SessionId`
- `CurrentInput`
- `Mode`

返回 DTO：`RetrievalPlanProposal`

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

安全 DTO：`RetrievalPlanSafetyProfile`

- `MaxFinalTopK`
- `MaxKeywordTopK`
- `MaxMemoryTopK`
- `MaxRelationTopK`
- `MaxVectorTopK`
- `AllowVector`
- `AllowDeprecatedInNormalMode`
- `AllowSupersededInNormalMode`
- `RequireLifecycleFilter`

执行开关 DTO：`RetrievalPlanningOptions`

- `Mode`: `Off` / `Shadow` / `ApplyGuarded`
- `ApplyMode`: 当前为 `IntentScoped`
- `OptInIntents`: 默认空列表
- `FallbackToLegacyOnViolation`: 默认 `true`
- `EmitComparisonTrace`: 默认 `true`

默认配置保持保守：

- `Mode=Off`
- `ApplyMode=IntentScoped`
- `OptInIntents=[]`
- `FallbackToLegacyOnViolation=true`
- vector 固定 disabled

## Intent Detector

`PlanningIntentDetector` 是规则型 detector，不调用模型。当前输出：

- `CurrentTask`
- `AuditDeprecated`
- `ConflictCheck`
- `CodingTask`
- `NovelGeneration`
- `AutomationRecovery`
- `LongTermPreference`
- `FuzzyQuestion`

规则优先级中，显式 conflict terms 优先于 audit/history terms，避免“历史冲突检查”被误判为 audit-only。

## Proposal Service

`RetrievalPlanProposalService` 流程：

1. 读取 `PlanningSnapshotService.GetSnapshotAsync(...)`。
2. 使用 `PlanningIntentDetector` 识别 intent。
3. 输出 proposal channel / TopK / reasons / warnings。

固定边界：

- `UseVector=false`
- `VectorTopK=0`
- warning 包含 preview-only 说明
- reasons 包含 snapshot 计数和关键 source ref

Phase P6 起，proposal service 直接按默认 `RetrievalPlanSafetyProfile` 生成原生合法 proposal，不再先生成超限 TopK 再交给 validator 修复：

- `MaxFinalTopK=10`
- `MaxKeywordTopK=24`
- `MaxMemoryTopK=24`
- `MaxRelationTopK=8`
- `UseVector=false`
- `VectorTopK=0`
- 非 audit / conflict 模式不生成 deprecated / superseded normal allowance
- `Reasons` 记录 vector disabled 与 lifecycle normal path block/filter 说明
- 原生 proposal 不再记录 `.clamped` repair reason

Intent-specific safe defaults：

| Intent | KeywordTopK | MemoryTopK | RelationTopK | FinalTopK | AuditMode | ConflictMode |
|---|---:|---:|---:|---:|---|---|
| `CurrentTask` | 18 | 20 | 8 | 10 | false | false |
| `AuditDeprecated` | 24 | 24 | 8 | 10 | true | false |
| `ConflictCheck` | 24 | 24 | 8 | 10 | false | true |
| `CodingTask` | 24 | 24 | 8 | 10 | false | false |
| `NovelGeneration` | 22 | 24 | 8 | 10 | false | false |
| `AutomationRecovery` | 22 | 22 | 8 | 10 | false | false |
| `LongTermPreference` | 18 | 20 | 0 | 10 | false | false |
| `FuzzyQuestion` | 22 | 22 | 8 | 10 | false | false |

## Shadow Validator

Phase P4 新增 `RetrievalPlanProposalValidator`。Phase P5 将其改为 repair-before-fallback。它不改变 proposal preview endpoint，也不让 proposal 影响正式 retrieval；只在 `eval planning-shadow` / shadow executor 中作为安全挡板使用。

Phase P6 保留 validator 作为最终安全闸门。手工构造的非法 proposal 仍会被 repair 或 fallback；正常由 proposal service 生成的 proposal 应优先成为 native valid plan。

强制规则：

- Rejected 永不进入 shadow selected。
- Deprecated / Superseded 在非 audit / conflict path 中不进入 normal selected。
- `AuditMode=false` 时，historical / deprecated evidence 不进入 normal selected。
- `UseVector=false` 和 `VectorTopK=0` 强制保持。
- Relation expansion 结果仍必须通过 eligibility / lifecycle 过滤。
- `FinalTopK` 不得超过 safe cap。
- 可修复问题先 repair；修复后仍不合法才 fallback 到 `LegacySafePlan`。

可修复问题：

- `FinalTopK` 超过 safe cap
- channel TopK 超过 cap
- `UseVector=true` / `VectorTopK>0`
- deprecated / superseded normal mode 进入 normal selected 的风险

`LegacySafePlan` 只用于 shadow：

- 继承 legacy lifecycle restrictions。
- 继承 legacy relation quota reserve。
- 继承 legacy packing safety caps。
- 保持 vector disabled。
- 记录 `validatorApplied`、`validPlan`、`repairedPlan`、`fallbackToLegacySafePlan`、`rejectedPlanReasons`、`validatorRepairReasons`、`afterRepairPlanSummary`。

## Intent-Scoped ApplyGuarded

Phase P9 在 `HybridContextRetriever` 中接入 limited opt-in planning execution。默认仍不启用，且第一批 opt-in intent 不写入默认配置。

执行规则：

- `Off`: 始终使用 legacy hybrid retrieval。
- `Shadow`: 生成 proposal 并执行 shadow comparison trace，但最终输出仍为 legacy selected。
- `ApplyGuarded`: 仅当 `ApplyMode=IntentScoped` 且 proposal `Intent` 命中 `OptInIntents` 时，才允许 proposal selected 成为最终 selected。
- 未命中 opt-in intent 时继续使用 legacy selected。
- 执行前后 vector 均保持 disabled，不接 vector channel。

ApplyGuarded 安全闸门：

- invalid proposal -> fallback legacy
- must-not-hit violation -> fallback legacy
- lifecycle violation -> fallback legacy
- hard constraint missing -> 先执行 mandatory constraint repair，repair 后仍缺失才 fallback legacy

## Mandatory Constraint Injection

Phase P11 在 proposal path 中增加 `MandatoryConstraintInjection`：

- 从 `eval.expectedConstraints` / `planning.expectedConstraints` 等 metadata 解析 required hard constraints。
- 优先从 scope 匹配的 `IConstraintStore` 读取 hard constraints；查不到时才回退到 proposal ranked candidates。
- 命中的 hard constraint 会被标记为 locked item：
  - `mandatory=true`
  - `lockedConstraint=true`
  - `section=constraints`
  - `planningSection=constraints`
- locked constraints 不参与 normal optional recall，也不允许被 normal budget trim 丢弃。
- budget pressure 下优先裁剪 diagnostics / historical / low-value selected items。

repair 后 safety check 仍要求 hard constraint 命中且位于 constraints section；否则 fallback legacy。

Trace metadata 记录：

- `planningMode`
- `planningApplyMode`
- `planningIntent`
- `planningProposalSummary`
- `planningOptInMatched`
- `planningFallbackUsed`
- `planningFallbackReason`
- `planningLegacySelected`
- `planningProposalSelected`
- `planningFinalSelected`
- `planningSafetyChecks`
- `planningShadow.constraintRepairStatus`
- `planningShadow.lockedConstraintItems`
- `planningShadow.constraintDroppedByBudget`
- `planningShadow.constraintWrongSection`

## Expansion Candidate Analysis

Phase P10 新增 `planning-optin-fallback-analysis`。该命令显式在 eval runner 中评估下一批候选 intent：

- `CodingTask`
- `LongTermPreference`

评估不等于启用：

- runtime 默认 `OptInIntents` 仍为空
- service 配置不会被命令修改
- candidate intent 只进入 report recommendation
- 若 fallback rate 高、quality regression、must-not-hit / lifecycle risk 存在，则 recommendation 会落到 `NeedsPolicyTuning`、`ShadowOnly` 或 `Blocked`

## API

```http
POST /api/context/planning/propose
```

请求体：

```json
{
  "workspaceId": "workspace-1",
  "collectionId": "collection-1",
  "sessionId": "session-1",
  "currentInput": "当前任务下一步",
  "mode": "Chat"
}
```

返回 `RetrievalPlanProposal`。

## Client

`ContextCoreClient`：

```csharp
Task<RetrievalPlanProposal> ProposeRetrievalPlanAsync(
    ContextPlanningProposalRequest request,
    CancellationToken cancellationToken = default)
```

也提供 workspace / collection / session / currentInput 参数重载。

## ControlRoom

Service Mode 新增 Planning Proposal 页面：

- 主 Dashboard Service Mode：`F` 或 `29`
- Service Dashboard：`F`

页面只读展示：

- proposed intent / mode
- exact / keyword / short-term / working / stable / relation / vector channels
- keyword / memory / relation / vector / final TopK
- reasons
- warnings

页面不做配置编辑，不执行 retrieval。

## 测试

新增覆盖：

- audit query proposes `AuditDeprecated`
- conflict query proposes `ConflictCheck`
- current task query proposes `CurrentTask`
- coding query proposes `CodingTask`
- novel query proposes `NovelGeneration`
- automation failure query proposes `AutomationRecovery`
- proposal includes snapshot context
- proposal does not affect retrieval output
- proposal service generates native safe TopK
- proposal service respects intent-specific safe defaults
- `UseVector` remains false
- service endpoint returns proposal
- client route uses `POST /api/context/planning/propose`
- ControlRoom renders proposal
- validator rejects non-audit deprecated plan
- validator rejects rejected lifecycle plan
- validator forces vector disabled
- validator repairs high `FinalTopK`
- fallback only happens when repair fails
- invalid proposal falls back to legacy safe plan
