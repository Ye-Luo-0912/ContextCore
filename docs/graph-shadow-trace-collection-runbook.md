# Graph Shadow Trace Collection Runbook

Graph Foundation G6.1 用于采集 section-aware graph expansion shadow traces，并用质量报告判断 `audit-v1` / `conflict-v1` 是否具备进入 G7 的真实样本基础。本 runbook 不启用 graph opt-in，不改变正式 retrieval，不改变正式 relation expansion，不改变 selected set，不修改 `PackingPolicy`，不改变 package output。

## 开启采集

推荐使用 `Graph:ExpansionShadow` 配置节：

```json
{
  "Graph": {
    "ExpansionShadow": {
      "Enabled": true,
      "TraceCollectionEnabled": true,
      "Profiles": [ "audit-v1", "conflict-v1" ],
      "MaxRelationsPerTrace": 50
    }
  }
}
```

说明：

- `Graph:ExpansionShadow:Enabled=true` 只允许 graph expansion shadow trace builder 运行。
- `Graph:ExpansionShadow:TraceCollectionEnabled=true` 只把 preview/shadow 结果写入 retrieval trace。
- `Profiles` 第一阶段只建议 `audit-v1` 和 `conflict-v1`，不要把 `normal-v1` / `current-task-v1` 接入 runtime trace collection。
- `MaxRelationsPerTrace` 建议先用 `50`，trace 过大时可降到 `30`。
- 配置修改后重启 `ContextCore.Service`。
- 兼容路径：旧配置 `Learning:GraphExpansionShadow` 在未配置 `Graph:ExpansionShadow` 时仍会被读取。

环境变量方式：

```powershell
$env:Graph__ExpansionShadow__Enabled = "true"
$env:Graph__ExpansionShadow__TraceCollectionEnabled = "true"
$env:Graph__ExpansionShadow__Profiles__0 = "audit-v1"
$env:Graph__ExpansionShadow__Profiles__1 = "conflict-v1"
$env:Graph__ExpansionShadow__MaxRelationsPerTrace = "50"
```

## 推荐采样场景

固定采样至少覆盖以下场景。采集 readiness 时不得用重复 query、重复 operationId 或只挑容易合格的单一场景刷高 `TraceCount`；少于 30 条不同业务场景时应保持 `NeedsMoreRealTraces`。

| 场景 | Mode | 目标 |
|---|---|---|
| Chat version conflict | ChatMode | 当前偏好与旧版偏好存在同关键词冲突 |
| Chat deprecated preference | ChatMode | 废弃 preference 不应进入 normal context |
| Chat audit old topic | ChatMode | 明确审计旧话题时应路由到 audit/historical |
| Chat overwritten style rule | ChatMode | 已覆盖的交互规则与当前规则边界对比 |
| Chat scope boundary old session | ChatMode | 旧 session 范围证据不得进入 normal context |
| Chat long-term preference conflict | ChatMode | 当前长期偏好与废弃偏好冲突 |
| Project deprecated design | ProjectMode | 废弃设计草案与当前方案并存 |
| Project superseded pool | ProjectMode | superseded pool / replacement chain 检查 |
| Project old storage choice | ProjectMode | 旧存储选择与当前 provider 决策对比 |
| Project migration conflict | ProjectMode | 旧队列设计与当前 service mode policy 冲突 |
| Project retired policy | ProjectMode | 退役 package policy 只作审计证据 |
| Project audit previous release plan | ProjectMode | 旧 release plan 路由到 audit context |
| Novel old plot | NovelMode | 旧剧情线应进入 audit/historical，不进入 normal |
| Novel weapon v1-v2 conflict | NovelMode | 武器设定 v1/v2 替代关系冲突 |
| Novel world rule conflict | NovelMode | 世界规则冲突应进入 conflict evidence |
| Novel character state retcon | NovelMode | 角色状态 retcon 的旧状态隔离 |
| Novel location rule superseded | NovelMode | 被替代地点规则与当前世界约束对比 |
| Novel foreshadowing conflict | NovelMode | 旧伏笔只作为历史/冲突证据 |
| Automation old backup strategy | AutomationMode | 旧备份策略与当前恢复策略冲突 |
| Automation conflict recovery config | AutomationMode | recovery config 冲突证据路由 |
| Automation dead-letter policy conflict | AutomationMode | 当前 dead-letter policy 与旧 retry policy 对比 |
| Automation retry limit superseded | AutomationMode | 被替代 retry limit 路由到非 normal section |
| Automation old credential rotation | AutomationMode | 旧凭据轮换计划与当前恢复安全边界对比 |
| Automation audit failed step history | AutomationMode | 历史失败步骤只做审计证据 |
| Coding deprecated interface | CodingMode | 废弃接口设计不能进入 normal context |
| Coding old timeout config | CodingMode | 旧 timeout config 与当前配置冲突 |
| Coding obsolete API contract | CodingMode | 旧 API contract 与当前 endpoint contract 冲突 |
| Coding test policy conflict | CodingMode | 废弃测试捷径不得替代当前 regression gate |
| Coding build script legacy path | CodingMode | legacy build path 只做审计证据 |
| Coding deprecated schema field | CodingMode | 废弃 DTO 字段与当前 contract 冲突 |

## 采样完整性

进入 G7 的 trace quality report 必须来自有区分度的采样：

- `TraceCount >= 30` 必须对应至少 30 个不同 `operationId` 和不同采样意图。
- 重复执行同一批 query 只能用于验证采集链路连通性，不能作为 readiness 依据。
- 不能只构造全都容易通过的 accepted relation；样本应同时覆盖 audit/historical、conflict evidence，以及可解释的 blocked relation。
- 如果真实或夹具关系语料不足，应保留 `NeedsMoreRealTraces` 或 `NeedsPolicyTuning`，不要用重复数据补齐数量。

后端会对 graph shadow trace 生成 `traceSignature`，并在同一 workspace / collection 内抑制重复 payload：

- 同一 query、profiles、accepted / blocked relation 指纹一致时，后续重复 trace 只保留 `duplicateSuppressed=true` 与 `duplicateOfRetrievalId`。
- 正式 retrieval trace 仍会保留，用于排查请求本身；重复的 graph shadow accepted / blocked relation 大对象不会再次写入。
- trace export 和 quality report 会跳过被抑制的重复 shadow payload，并按唯一 `traceSignature` 去重。
- 因此重复运行同一批 query 不会提高 readiness 指标，也不会继续制造 graph shadow 噪声。

## 采集脚本

脚本假设 `ContextCore.Service` 已运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-graph-expansion-shadow-traces.ps1 -Execute
```

默认 dry-run 不调用服务：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-graph-expansion-shadow-traces.ps1
```

查看完整采样清单：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-graph-expansion-shadow-traces.ps1 -ListScenarios
```

常用参数：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-graph-expansion-shadow-traces.ps1 `
  -Execute `
  -BaseUrl http://localhost:5079 `
  -WorkspaceId default `
  -CollectionId test `
  -Profiles audit-v1,conflict-v1 `
  -MaxRelationsPerTrace 50 `
  -TraceTake 200
```

脚本会执行：

1. 检查 `/api/status`。该端点失败只作为 warning。
2. 检查 `/api/health/ready`。失败会停止采集。
3. 对固定场景调用 `/api/context/retrieve`。
4. 对固定场景调用 `/api/context/query`。
5. 对固定场景调用 `/api/package/build-detailed`。
6. 导出 `/api/learning/graph-expansion-shadow/traces?format=jsonl`。
7. 运行 `eval graph-expansion-shadow-trace-quality`。
8. 输出 trace JSONL、quality JSON 和 quality Markdown 路径。

## 手动导出 traces

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri "http://localhost:5079/api/learning/graph-expansion-shadow/traces?workspaceId=default&collectionId=test&take=200&format=jsonl" `
  | Set-Content -Encoding UTF8 learning/graph-shadow/graph-expansion-shadow-traces.jsonl
```

## 运行质量报告

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval graph-expansion-shadow-trace-quality `
  --workspace default `
  --collection test `
  --take 200 `
  --out learning/graph-shadow/graph-expansion-shadow-trace-quality-report.json `
  --md-out learning/graph-shadow/graph-expansion-shadow-trace-quality-report.md
```

## 进入 G7 的门槛

必须全部满足：

- `TraceCount >= 30`
- `AcceptedRelationCount > 0`
- `AuditContextCount > 0` 或 `ConflictEvidenceCount > 0`
- `RiskAfterRoutingCount = 0`
- `WrongSectionRiskCount = 0`
- `MustNotHitRiskCount = 0`
- `LifecycleRiskCount = 0`
- `MissingEvidenceCount = 0`

Recommendation 解释：

- `NeedsMoreRealTraces`：真实 trace 不足，通常是 trace collection 未开启或样本量不足。
- `ReadyForAuditShadowOnly`：audit context trace 足够且无 routing 风险，但 conflict evidence 不足。
- `ReadyForConflictShadowOnly`：conflict evidence trace 足够且无 routing 风险，但 audit context 不足。
- `ReadyForGuardedOptIn`：达到 G7 讨论门槛；仍不代表自动启用。
- `BlockedByRisk`：出现 routing 后风险、wrong section、must-not-hit risk、lifecycle risk 或其它硬风险。

## 关闭采集

采集结束后恢复默认配置：

```json
{
  "Graph": {
    "ExpansionShadow": {
      "Enabled": false,
      "TraceCollectionEnabled": false,
      "Profiles": [ "audit-v1", "conflict-v1" ],
      "MaxRelationsPerTrace": 50
    }
  }
}
```

如果使用环境变量：

```powershell
Remove-Item Env:Graph__ExpansionShadow__Enabled -ErrorAction SilentlyContinue
Remove-Item Env:Graph__ExpansionShadow__TraceCollectionEnabled -ErrorAction SilentlyContinue
Remove-Item Env:Graph__ExpansionShadow__Profiles__0 -ErrorAction SilentlyContinue
Remove-Item Env:Graph__ExpansionShadow__Profiles__1 -ErrorAction SilentlyContinue
Remove-Item Env:Graph__ExpansionShadow__MaxRelationsPerTrace -ErrorAction SilentlyContinue
```

重启 `ContextCore.Service` 后确认新 trace 不再增长。
