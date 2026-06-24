# Architecture Cleanup Plan

生成: `2026-06-24T10:08:23.8534045+00:00`

## 核心指标
- Core runner files: `105`
- DTO classes: `311`
- EvalCommand lines: `15862`
- ControlRoomService lines: `12256`
- Renderer lines: `5804`
- Eval subcommand refs: `759`

## 建议迁移项
### [HIGH] EvalCommand 拆分
- 当前: EvalCommand.cs: ~24k 行，同一文件包含全部 ~50+ eval 子命令 dispatch + executor
- 建议: 按 V5/V6/架构拆分到 EvalCommand.V5.cs / EvalCommand.V6.cs / EvalCommand.Arch.cs，每个子命令保留 dispatch 一行，executor 移动到对应 phase 模块
- 风险: low — 只移动代码，不改行为

### [MEDIUM] Core 中 eval-only runner 分离 (OPT-004 已部分完成)
- 当前: eval-only runner 已按分类拆分到 Evaluation/V5 (37 files), Evaluation/V6 (17), Evaluation/Gates (11), Evaluation/Dataset (8), Legacy (11)；runtime 21 个文件保留在 Services/Vector/ 根目录
- 建议: 继续将 Evaluation/Gates 中的 gate runner 合并为统一 gate pipeline；将 V5 中已冻结的 runner 标记为 deprecated 或迁移到 Legacy
- 风险: low — 已有目录结构，后续只做少量文件再分配

### [MEDIUM] Abstractions DTO 拆分 (OPT-003 已完成)
- 当前: VectorIndexDtos 已拆分为 5 个文件: VectorIndexDtos (75), EvalReportDtos (153), GateReportDtos (33), SummaryDtos (5), LegacyDtos (45)；总计 311 类型
- 建议: 后续按 OPT-005 将 report/gate DTO 迁移到独立 ContextCore.Eval.Models 项目
- 风险: low — 已拆分，namespaces 和序列化行为未变

### [MEDIUM] ControlRoom loader/字段冗余
- 当前: ControlRoomService.cs: ~12k 行，每个 phase 的 loader 和 snapshot 字段重复 2 次（首屏 + 刷新）
- 建议: 将重复的双调用点合并为单次求值 + 共享；loader 按 phase 文件夹独立
- 风险: low — 纯重构

### [MEDIUM] Renderer 区块重复
- 当前: ServiceOperationalRenderer.cs: ~5.8k 行，每个 V5/V6 phase 的渲染块模式几乎一致
- 建议: 抽象 RenderBlock(phase, snapshot, condition) 辅助方法，减少重复
- 风险: low — 输出格式不变

### [MEDIUM] 阶段编号/文档索引
- 当前: V5.1–V5.10、V5.F、V6.10–V6.16、V6.F、OPT0 — 阶段编号已膨胀到 2 位数
- 建议: 冻结 V5/V6 阶段编号；OPT 阶段使用三位数字（如 OPT-001）；索引文档统一到 docs/ContextCore_Phase_Index.md
- 风险: low — 不影响运行时

### [LOW] P15 构建文件锁
- 当前: dotnet build -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false 是绕开文件锁的已知工作区
- 建议: 检查并行项目引用图，确保无循环引用导致锁冲突；长期将集成测试移到独立项目
- 风险: low — 已知工作区可用

- Repository root: .
- Core/Vector files (total): 105
-   Runtime: 21
-   Legacy: 11
-   Evaluation/Gates: 11
-   Evaluation/Dataset: 8
-   Evaluation/V5: 37
-   Evaluation/V6: 17
- DTO types (total): 311
-   VectorIndexDtos: 75
-   EvalReportDtos: 153
-   GateReportDtos: 33
-   SummaryDtos: 5
-   LegacyDtos: 45
- EvalCommand.cs lines: 15862
- ControlRoomService.cs lines: 12256
- ServiceOperationalRenderer.cs lines: 5804
- Eval subcommand refs: 759

OPT0 architecture cleanup plan. No runtime behavior change, no formal retrieval enable, no package/package policy/runtime/vector binding mutation.
