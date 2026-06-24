# Graph Expansion Shadow Trace Quality Report

Generated: 2026-06-08T16:37:24.5073963+00:00
PolicyVersion: `graph-expansion-shadow-trace-quality/v1`

## Summary

- Workspace: `graph-shadow-samples`
- Collection: `test`
- TraceCount: `30`
- AcceptedRelationCount: `150`
- BlockedRelationCount: `30`
- AuditContextCount: `120`
- ConflictEvidenceCount: `30`
- RiskAfterRoutingCount: `0`
- WrongSectionRiskCount: `0`
- MustNotHitRiskCount: `0`
- LifecycleRiskCount: `0`
- MissingEvidenceCount: `0`
- Recommendation: `ReadyForGuardedOptIn`

## Top Relation Types

| Key | Count |
|---|---:|
| `conflicts_with` | 60 |
| `references` | 60 |
| `same_as` | 60 |

## Top Blocked Reasons

| Key | Count |
|---|---:|
| `ConfidenceTooLow` | 30 |
| `RelationTypeNotAllowed` | 30 |

## G7 Readiness Gate

进入 G7 前必须全部满足：

- `TraceCount >= 30`
- `AcceptedRelationCount > 0`
- `AuditContextCount > 0` 或 `ConflictEvidenceCount > 0`
- `RiskAfterRoutingCount = 0`
- `WrongSectionRiskCount = 0`
- `MustNotHitRiskCount = 0`
- `LifecycleRiskCount = 0`
- `MissingEvidenceCount = 0`

采样完整性要求：

- `TraceCount >= 30` 必须来自不同 operationId 和不同采样意图。
- 重复 query 或重复 fixture 只能验证采集链路，不能作为 readiness 依据。
- 样本应覆盖 audit/historical routing、conflict evidence routing，以及可解释的 blocked relation。
- 被标记为 `duplicateSuppressed=true` 的重复 graph shadow payload 不计入质量评估。

