# Eval Baseline P15

更新时间：2026-06-05

## Baseline Freeze

P15 baseline 已冻结为当前 regression gate 的基准。

| Suite | Report | Total | Failed | PassRate |
|---|---|---:|---:|---:|
| A3 baseline | `eval/eval-report-p15-a3.json` | 50 | 0 | 100.00% |
| Extended baseline | `eval/eval-report-p15-extended.json` | 113 | 0 | 100.00% |

当前验证状态：

- `dotnet build`：通过，`0 warning / 0 error`
- `dotnet test`：通过
  - `ContextCore.Tests`：`401 passed`
  - `ContextCore.Service.Tests`：`36 passed`
  - `ContextCore.IntegrationTests`：`19 passed`

## chat-20260529-003 Closure

`chat-20260529-003` 的根因是 constraint corpus gap：expected constraint text 没有稳定进入 constraints/package sections。

修复路径使用正式 review / activation 链路：

1. `ConstraintGapCandidate accept`
2. `CandidateConstraint activate`
3. `Active/Hard Constraint`
4. package builder 注入 constraints section

约束语义：

> 重复解释、重复澄清、重复说明本身不应被提升为长期偏好或稳定事实；只有用户明确确认其为长期规则时才可提升。

明确边界：

- 没有通过 resolver alias 放行。
- 没有加入 eval 特判。
- 没有直接把 fixture 写入 corpus `constraints`。
- 没有改变 retrieval scoring、`PackingPolicy`、planning opt-in、attention、memory、constraints 业务逻辑。

## Regression Gate

B0 之后，以下 gate 必须保持：

| Gate | Required |
|---|---:|
| A3 pass rate | 100.00% |
| Extended pass rate | 100.00% |
| mustNotHit violation | 0 |
| lifecycle violation | 0 |
| hard constraint missing | 0 |

`mustNotHit violation` 以 formal eval result 的 `MustNotHitRecalledCount` 聚合计算。`hard constraint missing` 以 `PackageHasAllConstraints=false` 的样本计数计算。`lifecycle violation` 只统计 explicit lifecycle violation，不把正常的 lifecycle exclusion diagnostic 视为 violation。

## Extended Eval Required Changes

以下改动必须重新跑 Extended 113 eval：

- retrieval scoring
- `PackingPolicy`
- planning opt-in
- attention behavior
- uncertainty resolver
- constraint injection
- memory layer selection
- vector / graph / relation expansion

建议同时跑 A3 baseline，确保 quick baseline 与 extended baseline 一致。

## Commands

手动 gate：

```powershell
dotnet build
dotnet test
dotnet run --project src\ContextCore.ControlRoom -- eval run --out eval\eval-report-p15-a3.json
dotnet run --project src\ContextCore.ControlRoom -- eval run --include-batches --out eval\eval-report-p15-extended.json
```

可选脚本：

```powershell
.\scripts\eval-gate-p15.ps1
```

脚本只执行 build/test/eval 并解析 JSON gate，不修改业务逻辑。
