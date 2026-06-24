# DTO Split Readiness Gate

PlanGenerated: `True`
Source: `src/ContextCore.Abstractions/Models/VectorIndexDtos.cs`
TotalClasses: `311`

## 分类统计
- RuntimeContract: `73` — runtime adapter request/result/contract/envelope
- EvalReport: `155` — phase eval report DTO（不含 gate）
- GateReport: `33` — gate/freeze/decision/plan report DTO
- ControlRoomSummary: `5` — ControlRoom summary/snapshot 用 DTO
- Legacy: `45` — 已废弃或无法明确分类的 DTO

## 目标拆分文件
- `VectorRuntimeDtos.cs — runtime adapter request/result/contract/envelope/options`
- `VectorEvalReportDtos.cs — phase eval report DTO（不含 gate）`
- `VectorGateReportDtos.cs — gate/freeze/decision/plan report DTO`
- `VectorControlRoomSummaryDtos.cs — ControlRoom summary/snapshot 用 DTO`
- `VectorLegacyDtos.cs — 已废弃或无法明确分类的 DTO（逐步淘汰）`

## 不可迁移项
- IContextRetrievalAdapter / IShadowRetrievalAdapter / NoOpContextRetrievalAdapter（runtime adapter contract）
- RetrievalAdapterRequest / RetrievalAdapterResult（runtime adapter request/result DTO）
- FormalAdapterInputContract（formal adapter input contract）
- public API client DTO（ContextCoreClient DTO）

## 可延后项
- V5.1 ~ V5.3 phase reports（旧阶段报告——冻结后可归档）
- V4 runtime experiment reports（V4 实验报告——只读）
- Superseded eval policy/recommendation DTO（已被后续阶段替代）

## Blocked
- (empty)
