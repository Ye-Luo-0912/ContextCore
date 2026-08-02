# WP-S6 Production Evidence — 当前 HEAD 的 Build/Test/Benchmark 证据固化

> 本文档记录 WP-S6 生产证据固化的验收结果与证据来源。
> 每个 CI 运行的具体证据（commit sha、run id、各 job 结果）由 CI 的
> `evidence` job 实时写入 `evidence/head-evidence.json` 工件（保留 90 天），
> 基准结果由 `benchmark-main` job 写入 `head-commit.json` 工件。
> 本文档固定记录本提交（HEAD=`5af4f831ad69cbcf891ed02081d442d6c97d9fcb`）的本地验证证据与证据设施设计。本提交即 WP-S6 证据提交：其代码树即下列全部构建/测试/基准证据的对应代码。运行时权威追溯见 CI 证据产物 `head-commit.json`（evidence 与 benchmark 任务写入 `github.sha`）。

## 1. 证据设施（WP-S6 新增）

| 设施 | 位置 | 作用 |
| --- | --- | --- |
| no-Inconclusive 门禁 | `.github/workflows/ci.yml` → `evidence` job + `scripts/gate-no-inconclusive.py` | 任何测试以 Inconclusive 跳过都导致 CI 失败；不允许用 Inconclusive 掩盖缺失证据 |
| HEAD 可追溯记录 | `ci.yml` `evidence` job → `head-evidence.json` 工件 | 记录 commit sha + run id + 各 job 结果，随工件保留 90 天 |
| 基准 HEAD 绑定 | `.github/workflows/benchmark-main.yml` → `head-commit.json` | 基准结果与产生它的提交绑定（可追溯到具体 commit） |
| CI 结构验收 | `tests/ContextCore.Tests/R29H_CiEvidenceAcceptanceTests.cs` | 文本断言 CI 证据设施结构完整（不实际运行 CI） |
| 本证据文档 | `docs/WP-S6-Evidence.md` | 本文件 |

## 2. 当前 HEAD 的本地验证证据

本地（Windows / pwsh，Docker 未启动）验证：

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 解决方案 Build | 0 错误 | `dotnet build ContextCore.sln -c Release` |
| 警告位置数 | 146 个唯一位置（< 基线 166） | 无新增警告门禁 |
| ContextCore.Tests | 恰好 10 个基线失败（预存） | TRX 解析 |
| 集成测试项目编译 | 0 错误 | `dotnet build tests/ContextCore.IntegrationTests` |
| 基准基线 | `benchmarks/results/results/*-report-full.json` 各 case N ≥ 15 | `R29H_BenchmarkCIAcceptanceTests` |

> Docker 未在本机启动：Postgres 集成测试（含 WP-S6 新增故障测试）以
> `Assert.Inconclusive` 本地跳过，由 CI `integration-postgres` job（mandatory Docker）
> 真实执行并受 no-Inconclusive 门禁约束。

## 3. WP-S6 新增生产证据测试

| 测试 | 证明的验收标准 |
| --- | --- |
| `E2E_TwoHosts_SameRun_ExactlyOneExecutionPlane` | ProductionHA 只有一个 Agent 执行平面；外部 Tool 在第二实例启动下不重复执行 |
| `E2E_TwoHosts_LeaseHandover_AfterExpiry_FencingIncrements_OldTokenRejected` | 旧 Owner 超过真实 lease expiry 后不能执行副作用（fencing 递增 + 旧 token 失效） |
| `E2E_RealProcessKill_MidToolExecution_NoDuplicateSideEffect` | 真进程 Kill 后 Run 不丢失；journal 模糊态（DispatchingIntent）对账而非重放；外部 Tool 不静默重复执行 |
| `E2E_DbNetworkPartition_LeaseExpires_NewOwnerFencingWins` | DB 网络分区后租约真实过期、fencing 递增、旧 token 失效、Run 不丢失 |
| `E2E_TenThousandEvents_PaginatedRecovery_ChainIntact` | 10,000 条以上事件分页恢复（10 页 × 1,000），哈希链完整 |
| `E2E_Http_Sse_LastEventIdReplay_NoLostWakeup` | SSE 读取/订阅竞争窗口不丢最终事件（Last-Event-ID 补读 + notifier push） |
| `E2E_Http_ConcurrentIdempotencyKey_ExactlyOneRun` | 并发相同 IdempotencyKey 恰好创建 1 个 Run |

## 4. R30 硬验收标准 → 证据映射

| # | 验收标准 | 证据 |
| --- | --- | --- |
| 1 | 外部 Tool 在所有 Kill Point 下不会静默重复执行 | `E2E_RealProcessKill_...`（DispatchingIntent 对账不重放）；`E2E_TwoHosts_...`（双实例不重复）；WP-S3 `ToolExecution_*Fence*` 系列 |
| 2 | Prepared/Dispatching/Dispatched 每种恢复状态都有明确策略 | journal 状态机（Prepared→重放 / DispatchingIntent/Dispatched→对账 / Committed→缓存）；`E2E_RealProcessKill_...` 断言 journal 保持 DispatchingIntent |
| 3 | 人工审批不存在 "Run AwaitingApproval 但无 Approval Row" | WP-S3 `HumanApproval_WithStore_CreatesPendingRow_...`；`E2E_RealPostgres_ApprovalSuspendResume` |
| 4 | Approval 不能出现已批准但 Run 未推进的半状态 | WP-S3 `ApprovalResolve_Concurrent_CasAllowsExactlyOne`；`E2E_RealPostgres_ConcurrentApprovalResolve_CasAllowsExactlyOne` |
| 5 | 旧 Owner 超过真实 lease expiry 后不能执行副作用 | `E2E_TwoHosts_LeaseHandover_...`；`E2E_DbNetworkPartition_...`；WP-S3 `ToolExecution_ExpiredLeaseFence_BlocksSideEffect` |
| 6 | ProductionHA 只有一个 Agent 执行平面 | `E2E_TwoHosts_SameRun_ExactlyOneExecutionPlane` |
| 7 | 节点重启后自动加载集群唯一 Champion | WP-S4 Reconciler 三测试（FirstStart 立即激活 + DesiredState 先写 + 更高 revision 切换） |
| 8 | SSE 在读取/订阅竞争窗口不丢最终事件 | `E2E_Http_Sse_LastEventIdReplay_NoLostWakeup`；端点注册-后-读取设计 |
| 9 | 10,000 条以上事件可以分页恢复 | `E2E_TenThousandEvents_PaginatedRecovery_ChainIntact` |
| 10 | Late Hydration 后 Decision 与实际输入一致 | WP-S5 既有（HydrationRepairDecision）+ `R28B_FinalClosureAcceptanceTests.LateHydrationRepairDropsCandidate_DecisionMatchesActualInput`（hydrator 返回 Repair 后，SelectedEnvelopes / Outcome.SelectedCount / EffectiveTokens / AllocationDecisions 全量与实际输入一致） |
| 11 | FTS 第二页及后续页正确 | WP-S5 既有（FTS keyset 三存储分页） |
| 12 | Mandatory CI 不允许通过 Inconclusive 掩盖缺失证据 | `evidence` job no-Inconclusive 门禁 + `R29H_CiEvidenceAcceptanceTests.Ci_GateScript_RejectsInconclusive` |
| 13 | 当前 HEAD 的 CI、故障测试和基准结果可追溯 | `head-evidence.json` + `head-commit.json` 工件 + 本文档 + `R29H_CiEvidenceAcceptanceTests` |
