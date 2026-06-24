# Router Intent Shadow Freeze

本文冻结 Learning Loop R1 - R2.1 的 router intent shadow 结论，并定义 R3 guarded opt-in 的阻断状态。该冻结不替换 runtime router，不改变 retrieval、planning、PackingPolicy 或 package output。

## Frozen Results

| Phase | Metric | Result |
|---|---|---|
| R1 | Best classifier | `TokenCentroidRouterBaseline` |
| R2 | Shadow eval agreement rate | `88.57%` |
| R2 | Runtime trace count | `0` |
| R2 | Runtime trace recommendation | `NeedsMoreRealTraces` |
| R2.1 | Shadow fixes runtime | `1` |
| R2.1 | Shadow breaks runtime | `3` |
| R2.1 | Router hard negatives | `3` |
| R2.1 | Recommendation | `KeepRuleBased` |

## Current Conclusion

当前结论：`KeepRuleBased`。

R3 guarded opt-in 当前被阻断。主要原因：

- `ShadowBreaksRuntimeGreaterThanFixes`

该结果说明 TokenCentroid shadow 在离线样本上有少量修复，但引入的 regression 多于修复，因此不能进入 guarded opt-in。

## Readiness Gate

CLI：

```powershell
dotnet run --project src\ContextCore.ControlRoom -- eval router-guarded-optin-readiness-gate
```

输出：

- `learning/router/router-guarded-optin-readiness-gate.json`
- `learning/router/router-guarded-optin-readiness-gate.md`

Gate 条件：

- `ShadowBreaksRuntime = 0`
- `ShadowFixesRuntime > 0`
- `NetGain > 0`
- `PerIntentRegression = 0`
- `AgreementRate >= configured threshold`
- `LowConfidenceCount <= configured threshold`
- P15 gate remains passing

## Boundaries

- 不替换 runtime router。
- 不改变 retrieval / planning / `PackingPolicy` / package output。
- 不使用 sampleId / itemId / fixture 特判。
- 不使用领域词表硬编码。
- hard negatives 只用于离线数据增强建议。
