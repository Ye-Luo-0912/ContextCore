# ContextCore DTO Split Plan

> 历史快照（Historical Snapshot）— 生成于 2026-06。
> 文中 DTO 分类与拆分建议已由 P3-05（eval-only DTO 迁移）与 P5-1（Evaluation 代码删除）落地或取代。
> 当前路线图请见根目录 `TODO.md`。本文档仅供回溯，不作为设计依据。

PlanGenerated: `True`
Source: `src/ContextCore.Abstractions/Models/VectorIndexDtos.cs`
TotalClasses: `75`

## 分类统计
- RuntimeContract: `73` — runtime adapter request/result/contract/envelope
- EvalReport: `2` — phase eval report DTO（不含 gate）
- GateReport: `0` — gate/freeze/decision/plan report DTO
- ControlRoomSummary: `0` — ControlRoom summary/snapshot 用 DTO
- Legacy: `0` — 已废弃或无法明确分类的 DTO

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
