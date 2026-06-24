# Architecture Cleanup Plan

生成: `2026-06-23T03:47:33.4126037+00:00`

## 核心指标
- Core runner files: `0`
- DTO classes: `212`
- EvalCommand lines: `24413`
- ControlRoomService lines: `12430`
- Renderer lines: `5780`
- Eval subcommand refs: `1006`

## 建议迁移项
### [HIGH] EvalCommand 拆分
- 当前: EvalCommand.cs: ~24k 行，同一文件包含全部 ~50+ eval 子命令 dispatch + executor
- 建议: 按 V5/V6/架构拆分到 EvalCommand.V5.cs / EvalCommand.V6.cs / EvalCommand.Arch.cs，每个子命令保留 dispatch 一行，executor 移动到对应 phase 模块
- 风险: low — 只移动代码，不改行为

### [HIGH] Core 中 eval-only runner 分离
- 当前: ~100 个 runner 文件混在 ContextCore.Core/Services/Vector/，其中约 60% 是 eval-only shadow/preview/audit runner
- 建议: 将 eval-only runner 移到 ContextCore.Eval or ContextCore.Core/EvalRunners/ 子命名空间；留下 runtime 可用的 runner 在原位置
- 风险: medium — 需检查每个 runner 的引用链，确保 DI 注册不受影响

### [HIGH] Abstractions DTO 拆分
- 当前: VectorIndexDtos.cs 包含 ~212 个类，从 V5.0 到 V6.F 所有 report/decision/proposal 混在一起
- 建议: 按 V5/VMeta/V6/EvalProtocol/Arch 拆分到独立文件；runner 只需要 using 对应命名空间
- 风险: low — 只移动 DTO 定义，不改序列化行为

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

- Core/Vector runner files: ~0
- VectorIndexDtos classes: ~212
- EvalCommand.cs lines: ~24413
- ControlRoomService.cs lines: ~12430
- ServiceOperationalRenderer.cs lines: ~5780
- Eval subcommand refs: ~1006

OPT0 architecture cleanup plan. No runtime behavior change, no formal retrieval enable, no package/package policy/runtime/vector binding mutation.
