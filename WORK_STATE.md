# 工作状态（Work State）

> 用途：上下文压缩时快速恢复当前状态。只保留必要信息（状态、计划、目标、必要元信息）；任务完成立即更新，无关内容及时清理。
> 维护规则见 AGENTS.md「工作状态维护」；路线图与历史见 TODO.md。

## 目标与计划

- 当前目标：随用户指示推进，无既定进行中任务。
- 候选方向（详见 TODO.md）：R30 Self-Learning Agent Runtime —— Utility Ledger 物化 → Dataset Builder → 训练/校准 → Replay → Canary → Promotion。

## 当前状态

- **HEAD**：`bc46c079`（已推送 main）。
- **进行中**：无。
- **最近完成**：WORK_STATE.md 增补计划/目标与必要元信息，AGENTS.md 维护规则同步。

## 必要元信息

- 解决方案：`ContextCore.sln`（.NET 10，Windows / pwsh）。
- 规则文件：`AGENTS.md`（注释规范、工程化原则、工作状态维护）。
- 路线图：`TODO.md`；历史归档 `docs/archive/roadmap-history.md`。
- 提交约定：commit 消息写入 UTF-8 临时文件后用 `git commit -F`（pwsh 内联中文会乱码）；push main 已预授权。
- 验证门禁：build 0 错误；build 与 test **串行**执行；ContextCore.Tests 失败须**恰好 11 个既有项**（名单见下）。

## 既有 11 个测试失败（勿当回归处理）

`Benchmark_Baseline_Contains_AtLeast_15_Samples_PerCase`、`DI_Registration_EngineResolvesWithAllocatorV2_1`、`Journal_StateTransition_ThrowsOnRegression`、`MandatoryProvider_NoTokenizer_EmptyContent_Succeeds`、`MemoryOnlyBatchLookup_SatisfiesHydrationPipeline`、`PostgresMigrationSql_ShouldExposeVectorIndexProviderSchema`、`ProductionComposition_Postgres_NoNonDecoratorUnexpectedDuplicates`、`ProductionHA_AllNineMandatoryChecksPass_WhenFullyConfigured`、`ReadinessService_Development_GetRegisteredWorkers_ReturnsExpectedList`、`RequiredIndexes_IncludeUtilityLedgerIndexes`、`SchemaVersion_IsV30`
