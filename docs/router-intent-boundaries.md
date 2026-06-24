# Router Intent Boundaries

本文档定义 Router Intent 的离线评估边界，用于解释 disagreement triage、hard negative 生成和后续人工标注。它不替换 runtime router，也不改变 retrieval、planning、PackingPolicy 或 package output。

## 原则

- intent 定义必须描述通用行为，不绑定 sampleId、itemId、fixture 文件名或领域词表。
- hard negative 只能来自 expected intent 与错误预测 intent 的对比，用于离线训练/审计数据，不进入 runtime policy。
- 低置信或边界模糊样本优先补充定义与标注，不通过硬编码关键词修复。
- runtime router 在没有显式 opt-in 前继续保持当前规则路径。

## Intent 定义

| Intent | 定义 | 正例 | 反例 |
|---|---|---|---|
| `CurrentTask` | 用户询问当前工作、下一步、当前上下文或活动任务。 | “继续当前任务并给出下一步” | 要求审计旧版本、查冲突、写小说正文 |
| `AuditDeprecated` | 用户要求审计、解释或回看历史/过期/被替代内容。 | “查看旧方案为什么被替代” | 直接执行当前任务或生成新内容 |
| `ConflictCheck` | 用户要求比较冲突、找不一致、验证互斥关系。 | “检查这两个规则是否冲突” | 单纯问当前偏好或下一步 |
| `CodingTask` | 用户请求代码实现、修复、验证、构建、测试或接口调整。 | “修这个 API 并跑测试” | 纯聊天偏好、小说生成、自动化恢复 |
| `NovelGeneration` | 用户请求剧情、角色、世界观、物品状态或小说内容生成。 | “继续写下一章并保持人物状态” | 代码构建、系统恢复、偏好审计 |
| `AutomationRecovery` | 用户请求恢复失败流程、重试、失败步骤、dead-letter、checkpoint 或自动化运行状态。 | “恢复失败任务并检查重试策略” | 一般代码实现或普通检索 |
| `LongTermPreference` | 用户表达或确认长期偏好、稳定规则、全局习惯。 | “以后默认这样输出” | 只要求当前这一次的格式 |
| `FuzzyQuestion` | 输入意图不足、问题泛化或无法可靠归入其他 intent。 | “这个怎么看” | 明确的代码、审计、冲突、恢复或小说任务 |

## 常见混淆

| Confusion | 边界规则 |
|---|---|
| `CurrentTask` vs `CodingTask` | 如果用户明确要求改代码、跑 build/test 或处理接口，优先 `CodingTask`；如果只是问当前上下文和下一步，优先 `CurrentTask`。 |
| `AuditDeprecated` vs `ConflictCheck` | 回看旧内容、解释替代关系是 `AuditDeprecated`；比较两个仍需判定的互斥或不一致项是 `ConflictCheck`。 |
| `AutomationRecovery` vs `CodingTask` | 失败恢复、重试、checkpoint、运行链路状态优先 `AutomationRecovery`；代码实现和测试优先 `CodingTask`。 |
| `LongTermPreference` vs `CurrentTask` | 只有用户明确表达长期规则或偏好时才标为 `LongTermPreference`；一次性执行请求仍属于当前任务。 |
| `NovelGeneration` vs `CurrentTask` | 生成剧情/角色/设定内容是 `NovelGeneration`；询问当前小说项目下一步但未生成内容可保持 `CurrentTask`。 |

## Hard Negative 使用规则

- `positiveIntent` 来自人工/评测期望 intent。
- `negativeIntent` 来自 runtime 或 shadow 的错误预测 intent。
- `reason` 记录分诊建议，例如 `AddHardNegative`、`ReviewRuntimeBoundary` 或 `ClarifyIntentDefinition`。
- hard negative JSONL 只用于离线分析和后续数据增强，不允许直接改变 runtime router。

## R2.1 输出

- `learning/router/router-disagreement-triage-a3.json`
- `learning/router/router-disagreement-triage-extended.json`
- `learning/router/router-disagreement-triage.md`
- `learning/router/router-hard-negatives.jsonl`

运行：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval router-disagreement-triage
```
