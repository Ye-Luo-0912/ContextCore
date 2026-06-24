# Architecture Cleanup Freeze

**生成:** `2026-06-24T12:22:18.7683441+00:00`

**ArchitectureCleanup:** Frozen
**FreezePassed:** True
**Recommendation:** CleanupFrozen
**NextAllowedPhase:** None (ArchitectureCleanup frozen)

## 校准指标口径

### Runner 分布
- Total runners: `104`
- Runtime runners: `18`
- Eval runners: `55`
- Gate runners: `12`
- Dataset runners: `8`
- Legacy runners: `11`

### DTO 分布
- Total DTO types: `315`
- Core runtime DTO: `75`
- Non-runtime DTO (eval/gate/summary/legacy): `240`

### 代码行数
- EvalCommand.cs main: `15912`
- EvalCommand partial family total: `24005`
- ControlRoomService.cs: `12235`
- ServiceOperationalRenderer.cs: `5806`

## 已完成项

### EvalCommand 拆分 (OPT-001)
- Result: EvalCommand.cs: 15912 lines; partial 文件: EvalCommand.VectorV6.cs, EvalCommand.VectorV5.cs, EvalCommand.Learning.cs, EvalCommand.DtoSplit.cs; total family: 24005 lines; subcommand refs: 763
- Artifacts: `src/ContextCore.ControlRoom/Commands/EvalCommand.cs`, `src/ContextCore.ControlRoom/Commands/EvalCommand.VectorV6.cs`, `src/ContextCore.ControlRoom/Commands/EvalCommand.VectorV5.cs`, `src/ContextCore.ControlRoom/Commands/EvalCommand.Learning.cs`, `src/ContextCore.ControlRoom/Commands/EvalCommand.DtoSplit.cs`

### Abstractions DTO 拆分 (OPT-003)
- Result: VectorIndexDtos (75 runtime) 拆分为 5 文件; EvalReportDtos: 154, GateReportDtos: 36, SummaryDtos: 5, LegacyDtos: 45; 总计 315 类型
- Artifacts: `src/ContextCore.Abstractions/Models/VectorIndexDtos.cs`, `src/ContextCore.Abstractions/Models/VectorEvalReportDtos.cs`, `src/ContextCore.Abstractions/Models/VectorGateReportDtos.cs`, `src/ContextCore.Abstractions/Models/VectorControlRoomSummaryDtos.cs`, `src/ContextCore.Abstractions/Models/VectorLegacyDtos.cs`

### Vector eval-only runner 目录隔离 (OPT-004)
- Result: Runtime: 18, Eval: 55 (V5: 37, V6: 18), Gates: 12, Dataset: 8, Legacy: 11; 总计 104 runners
- Artifacts: `src/ContextCore.Core/Services/Vector/Evaluation/V5/`, `src/ContextCore.Core/Services/Vector/Evaluation/V6/`, `src/ContextCore.Core/Services/Vector/Evaluation/Gates/`, `src/ContextCore.Core/Services/Vector/Evaluation/Dataset/`, `src/ContextCore.Core/Services/Vector/Legacy/`

### P15 build/test 文件锁加固 (OPT-005)
- Result: Build retry + test retry + stale cleanup; P15 pass verified by diagnostics: True
- Artifacts: `scripts/eval-gate-p15.ps1`, `eval/p15-build-lock-diagnostics.json`, `eval/p15-build-lock-diagnostics.md`

### Path hygiene 静态+动态执法 (OPT-002)
- Result: Hygiene gate: Passed
- Artifacts: `src/ContextCore.Abstractions/PathHygiene.cs`, `eval/generated-artifact-path-hygiene-gate.json`, `eval/generated-artifact-path-hygiene-audit.json`

### ControlRoom summary registry 合并 (OPT-006)
- Result: Registry descriptors: 37 (V6: 11, V5: 19, OPT: 2); TryLoadFromDescriptor + TryBeginReportSection consolidations
- Artifacts: `src/ContextCore.ControlRoom/Models/ControlRoomReportDescriptor.cs`, `src/ContextCore.ControlRoom/Models/ReportSummaryRegistry.cs`

## 保留债务

- Future ContextCore.Evaluation project split — eval DTOs + runners 迁移到独立项目
- Deeper ControlRoom cleanup — loader 双调用点合并为单次求值 + 共享
- Performance profiling — 大方法 profiling + 热点优化
- Phase index cleanup — 统一到 docs/ContextCore_Phase_Index.md (当前 V5.1–V5.10, V6.10–V6.16 编号已膨胀)
- V5 deprecated runner 归档 — 已冻结的 V5 runner 标记为 deprecated 或迁移到 Legacy
- Gate runner pipeline 统一 — Evaluation/Gates 中的 gate runner 合并为统一 gate pipeline

## 延迟清理项

- ContextCore.Evaluation 独立项目 — 等待 Evaluation report/gate DTO + runner 量足够大后再拆分
- ControlRoom phase loader 重构 — 等待更多 phase 稳定后再合并双调用点
- Renderer block 抽象 — 等待 V5/V6 渲染区块更多稳定后再抽象 RenderBlock 公用方法

## 子报告状态

- ArchitectureCleanupPlanPassed: True
- DtoSplitPlanGenerated: True
- PathHygieneGatePassed: True
- P15BuildLockHardened: True
- ControlRoomRegistryConsolidated: True
- EvalCommandSplit: True
- VectorRunnerDirectoryIsolated: True

## Gate 规则合规

- FormalRetrievalNotEnabled: True
- NoRuntimeSwitch: True
- NoFormalPackageWrite: True
- NoPackagePackingPolicyVectorBindingMutation: True

## Diagnostics

- Repository root: .
- FreezePassed: True
- PlanPassed: True
- DtoSplitPlanGenerated: True
- PathHygieneGatePassed: True
- P15BuildLockHardened: True
- ArchitectureCleanupPlan: present
- DtoSplitPlan: present
- HygieneGate: present
- P15Diagnostics: present
- Runners — Total: 104, Runtime: 18, Eval: 55 (V5:37+V6:18), Gates: 12, Dataset: 8, Legacy: 11
- DTOs — Total: 315, Runtime: 75, Eval: 154, Gate: 36, Summary: 5, Legacy: 45
- EvalCommand.cs lines: 15912, Family total: 24005
- ControlRoomService.cs lines: 12235
- ServiceOperationalRenderer.cs lines: 5806
- Eval subcommand refs: 763
- ControlRoom registry descriptors: 37
- Gate rules: FormalRetrievalNotEnabled=true, NoRuntimeSwitch=true, NoFormalPackageWrite=true, NoMutation=true

Architecture cleanup freeze report. No runtime behavior change, no formal retrieval enable, no package/packing policy/runtime/vector binding mutation.
