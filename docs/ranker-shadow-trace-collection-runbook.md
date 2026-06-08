# Ranker Shadow Trace Collection Runbook

Learning Loop Phase 6H 用于采集 lifecycle-aware ranker shadow traces，并用质量报告判断是否有足够真实样本进入后续 guarded opt-in 讨论。本 runbook 不启用正式 scorer，不改变 retrieval scoring，不改变 selected set，不修改 `PackingPolicy`，不训练模型。

## 开启采集

在 Service 配置中显式开启 trace collection：

```json
{
  "Learning": {
    "RankerShadow": {
      "Enabled": false,
      "TraceCollectionEnabled": true,
      "DebugEndpointEnabled": true,
      "Profile": "lifecycle-aware-v1",
      "MaxCandidatesPerTrace": 50
    }
  }
}
```

关键点：

- `Learning:RankerShadow:Enabled=false` 必须保持，避免接入正式 scorer。
- `Learning:RankerShadow:TraceCollectionEnabled=true` 只把 lifecycle-aware shadow score 写入 retrieval/package trace。
- `MaxCandidatesPerTrace` 建议先用 `50`，如果 trace 过大可降到 `30`。
- 配置修改后重启 `ContextCore.Service`。

环境变量方式也可以使用：

```powershell
$env:Learning__RankerShadow__TraceCollectionEnabled = "true"
$env:Learning__RankerShadow__MaxCandidatesPerTrace = "50"
```

## 推荐采样场景

固定采样至少覆盖以下场景：

| 场景 | Mode | 目标 |
|---|---|---|
| Chat fuzzy preference | ChatMode | 模糊偏好、长期偏好、版本冲突 |
| Chat deprecated noise | ChatMode | 废弃偏好和当前偏好同关键词竞争 |
| Novel character state | NovelMode | 角色当前状态、旧设定排除 |
| Novel item state | NovelMode | 物品状态、old-vs-current setting |
| Project current task | ProjectMode | 当前任务、废弃设计草案 |
| Automation recovery | AutomationMode | last error / recovery point / retry / dead-letter |
| Coding verification | CodingMode | verification / deprecated interface |

## 采集脚本

脚本假设 `ContextCore.Service` 已运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-ranker-shadow-traces.ps1 -Execute
```

默认 dry-run 不调用服务：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-ranker-shadow-traces.ps1
```

常用参数：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/collect-ranker-shadow-traces.ps1 `
  -Execute `
  -BaseUrl http://localhost:5079 `
  -WorkspaceId default `
  -CollectionId test `
  -MaxCandidatesPerTrace 50 `
  -TraceTake 200
```

脚本会执行：

1. 检查 `/api/status`。该端点在某些构建中可能不存在，失败只作为 warning。
2. 检查 `/api/health/ready`。失败会停止采集。
3. 对固定场景调用 `/api/context/retrieve`。
4. 调用 `/api/package/build-detailed` 做 package build 采样。
5. 调用 `/api/retrieval/ranker-shadow/debug` 做只读 debug 采样。
6. 导出 `/api/learning/ranker-shadow/traces?format=jsonl`。
7. 运行 `eval ranker-shadow-trace-quality`。
8. 输出 trace JSONL、quality JSON 和 quality Markdown 路径。

## 手动导出 traces

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri "http://localhost:5079/api/learning/ranker-shadow/traces?workspaceId=default&collectionId=test&take=200&format=jsonl" `
  | Set-Content -Encoding UTF8 learning/baselines/ranker-shadow-traces.jsonl
```

## 运行质量报告

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval ranker-shadow-trace-quality `
  --workspace default `
  --collection test `
  --take 200 `
  --out learning/baselines/ranker-shadow-trace-quality-report.json `
  --md-out learning/baselines/ranker-shadow-trace-quality-report.md
```

## 进入下一阶段门槛

必须全部满足：

- `TraceCount >= 30`
- `CandidateScoreCount > 0`
- `MustHitDemotedCount = 0`
- `MustNotHitPromotedCount = 0`
- `LifecycleViolationCount = 0`
- `DeprecatedDemotionCount > 0` 或 `VersionConflictFixCount > 0`

Recommendation 解释：

- `NeedsMoreRealTraces`：真实 trace 不足，通常是 trace collection 未开启或样本量不足。
- `BlockedByRisk`：出现 must-hit demotion、must-not-hit promotion 或 lifecycle violation。
- `KeepShadowOnly`：trace 足够且无风险，但缺少 deprecated / version conflict 有效信号。
- `ReadyForGuardedOptIn`：达到下一阶段讨论门槛；仍不代表自动启用。

## 关闭采集

采集结束后恢复默认配置：

```json
{
  "Learning": {
    "RankerShadow": {
      "Enabled": false,
      "TraceCollectionEnabled": false,
      "DebugEndpointEnabled": true,
      "Profile": "lifecycle-aware-v1",
      "MaxCandidatesPerTrace": 50
    }
  }
}
```

如果使用环境变量：

```powershell
Remove-Item Env:Learning__RankerShadow__TraceCollectionEnabled -ErrorAction SilentlyContinue
Remove-Item Env:Learning__RankerShadow__MaxCandidatesPerTrace -ErrorAction SilentlyContinue
```

重启 `ContextCore.Service` 后确认新 trace 不再增长。
