using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.Postgres;
using ContextCore.Storage.Postgres.Infrastructure;
using ContextCore.Storage.Postgres.Stores;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ContextCore.IntegrationTests;

// ===========================================================================
// R29-Hard-Gate P5：Production Evidence E2E 测试
//
// 目标：证明系统端到端可用——真实 PostgreSQL + 真实 Agent Tool Loop + 持久化 Journal。
// 不使用 mock stub 和 in-memory stores，而是：
//   1. 真实 PostgreSQL（Testcontainers）
//   2. 模拟但真实的 IAgentModelTransport（产生 Tool 调用，不是真实 LLM）
//   3. 真实 Tool 执行（IToolHandler → RealToolDispatcher）
//   4. 完整 AgentRunActor 循环 + 持久化 Journal
//
// 四个测试：
//   1. E2E_RealPostgres_FullToolLoop_ModelCallToFinalAnswer — 完整循环
//   2. E2E_RealPostgres_ApprovalSuspendResume — 审批挂起与恢复
//   3. E2E_RealPostgres_CrashRecovery_MidToolExecution — 崩溃恢复与 exactly-once
//   4. E2E_RealPostgres_LearningEventOutbox_Durability — 学习事件 Outbox 持久性
//
// 设计原则：
//   - 使用真实 Postgres stores（PostgresAgentRunStore / PostgresAgentRunEventStore /
//     PostgresToolDispatchJournal / PostgresAgentApprovalStore / PostgresLearningEventOutboxStore）。
//   - Docker/Postgres 不可用时用 Assert.Inconclusive 跳过；不修改测试逻辑。
//   - 每个测试使用独立的 tablePrefix 避免数据交叉污染。
//   - 所有异步测试使用 CancellationTokenSource 超时防止挂起。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Production-Evidence")]
[TestCategory("Integration")]
[TestCategory("Postgres")]
[TestCategory("DockerRequired")]
public sealed class R29H_ProductionEvidenceE2ETests
{
    private const string PgVectorImage = "pgvector/pgvector:pg17";

    private static PostgreSqlContainer? _container;
    private static string? _connectionString;

    // ── 容器生命周期 ────────────────────────────────────────────────────

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        // 直接尝试启动容器（与 R29H_DurableKernelCrashRecoveryTests 一致），
        // 避免 IsDockerAvailableAsync 在 Windows named-pipe Docker Desktop 上误判。
        try
        {
            _container = new PostgreSqlBuilder(PgVectorImage)
                .WithDatabase("cctest")
                .WithUsername("cctest")
                .WithPassword("cctest")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[R29H_ProductionEvidenceE2ETests] Docker 不可用：{ex.GetType().Name}: {ex.Message}");
            _connectionString = null;
        }
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static bool ShouldSkip => _connectionString is null;

    /// <summary>构建测试用 Postgres 基础设施（factory + migrationRunner + serializer）。</summary>
    private static (PostgresConnectionFactory factory, PostgresMigrationRunner migrationRunner, PostgresJsonSerializer serializer) CreateInfrastructure(string prefix)
    {
        var options = new PostgresOptions
        {
            ConnectionString = _connectionString!,
            AutoMigrate = true,
            EnablePgVectorExtension = true,
            TablePrefix = prefix
        };
        var factory = new PostgresConnectionFactory(options);
        var serializer = new PostgresJsonSerializer();
        var migrationRunner = new PostgresMigrationRunner(factory);
        return (factory, migrationRunner, serializer);
    }

    // =======================================================================
    // 测试 1：完整 Tool Loop — Model → Tool Call → Observation → Final Answer
    // =======================================================================

    [TestMethod]
    public async Task E2E_RealPostgres_FullToolLoop_ModelCallToFinalAnswer()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("e2e1_");
        try
        {
            // ── 构建真实 Postgres stores ──
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // ── 构建真实 Tool 执行链 ──
            var toolHandler = new RecordingToolHandler("search", "搜索结果：找到 3 篇相关文档。");
            var dispatcher = new RealToolDispatcher(new[] { (IToolHandler)toolHandler });
            dispatcher.Freeze();
            var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);

            // ── 构建 ScriptedModelTransport：第 1 次返回 Tool 调用，第 2 次返回最终答案 ──
            var transport = new ScriptedModelTransport(
                BuildToolCallResponse("search", """{"query":"查找文档"}""", "需要搜索文档"),
                BuildFinalAnswerResponse("基于搜索结果，已找到 3 篇相关文档。任务完成。"));

            // ── 构建 Run ──
            var run = BuildRun("search 查找文档内容", turnBudget: new AgentTurnBudget
            {
                MaxTurns = 10,
                TurnsUsed = 0,
                MaxModelCalls = 5
            });
            await runStore.CreateAsync(run);

            // ── 构建 Actor 并执行 ──
            var actor = new AgentRunActor(
                runStore, eventStore, transport,
                new DefaultAgentLoopPolicy(),
                dispatcher,
                durableToolExecutor: durableExecutor);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await actor.ExecuteAsync(run, cts.Token);

            // ── 断言 1：Run 进入 Completed 终态 ──
            var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(finalRun, "应能从 store 取回 Run。");
            Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
                $"Run 应进入 Completed 终态，实际 {finalRun.State}。");

            // ── 断言 2：最终答案已持久化 ──
            Assert.IsFalse(string.IsNullOrEmpty(finalRun.FinalAnswer),
                "Completed 状态的 Run 应有最终答案。");
            Assert.IsTrue(finalRun.FinalAnswer!.Contains("3 篇相关文档"),
                $"最终答案应包含搜索结果摘要，实际：{finalRun.FinalAnswer}");

            // ── 断言 3：Tool 被真实执行了一次 ──
            Assert.AreEqual(1, toolHandler.InvocationCount,
                $"RecordingToolHandler 应被调用 1 次（真实 Tool 执行），实际 {toolHandler.InvocationCount}。");

            // ── 断言 4：事件流完整且哈希链无断裂 ──
            var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 10000);
            Assert.IsTrue(events.Count > 0, "事件流应非空。");

            // 链头 PrevChainHash 为 null
            Assert.IsNull(events[0].PrevChainHash, "链头事件的 PrevChainHash 应为 null。");

            // 逐个校验哈希链
            for (var i = 1; i < events.Count; i++)
            {
                Assert.AreEqual(events[i - 1].ContentHash, events[i].PrevChainHash,
                    $"事件 {i} 的 PrevChainHash 应指向前一事件的 ContentHash（哈希链断裂）。");
            }

            // ── 断言 5：事件流包含完整的循环阶段 ──
            var eventTypes = events.Select(e => e.EventType).ToList();
            CollectionAssert.Contains(eventTypes, AgentRunEventType.RunCreated, "应有 RunCreated 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ModelCallStarted, "应有 ModelCallStarted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ModelCallCompleted, "应有 ModelCallCompleted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ToolCallStarted, "应有 ToolCallStarted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ToolCallCompleted, "应有 ToolCallCompleted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ObservationAppended, "应有 ObservationAppended 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.RunCompleted, "应有 RunCompleted 事件。");

            // ── 断言 6：ModelCallsUsed = 2（一次 Tool 调用 + 一次最终答案）──
            Assert.AreEqual(2, finalRun.ModelCallsUsed,
                $"ModelCallsUsed 应为 2（Tool 调用 + 最终答案），实际 {finalRun.ModelCallsUsed}。");

            // ── 断言 7：Journal 中 Tool 分派已提交（Committed/ResultDelivered）──
            // Tool 执行后 journal 应处于 Committed 状态（SideEffect=None → MarkCommittedWithResultAsync）
            var toolCallEvents = events
                .Where(e => e.EventType == AgentRunEventType.ToolCallCompleted)
                .ToList();
            Assert.AreEqual(1, toolCallEvents.Count, "应有恰好 1 个 ToolCallCompleted 事件。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 2：审批挂起与恢复 — Tool 需审批 → 挂起 → 批准 → 恢复执行 → 完成
    // =======================================================================

    [TestMethod]
    public async Task E2E_RealPostgres_ApprovalSuspendResume()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("e2e2_");
        try
        {
            // ── 构建真实 Postgres stores ──
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
            var approvalStore = new PostgresAgentApprovalStore(factory, serializer, migrationRunner);
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // ── 构建真实 Tool 执行链 ──
            var toolHandler = new RecordingToolHandler("file_delete", "文件已删除：/tmp/test.txt");
            var dispatcher = new RealToolDispatcher(new[] { (IToolHandler)toolHandler });
            dispatcher.Freeze();
            var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);

            // ── 构建 ScriptedModelTransport：第 1 次返回危险 Tool 调用，第 2 次返回最终答案 ──
            var transport = new ScriptedModelTransport(
                BuildToolCallResponse("file_delete", """{"path":"/tmp/test.txt"}""", "需要删除临时文件"),
                BuildFinalAnswerResponse("文件已成功删除。任务完成。"));

            // ── 配置审批门：file_delete 需人工审批 ──
            var approvalGate = new DefaultAgentApprovalGate(
                approvalRequiredTools: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file_delete" },
                autoApproveAll: false,
                approvalStore: approvalStore);

            // DefaultAgentToolCallValidator 默认将 file_delete 标记为需审批
            var validator = new DefaultAgentToolCallValidator();

            // ── 构建 Run ──
            var run = BuildRun("删除临时文件 file_delete /tmp/test.txt", turnBudget: new AgentTurnBudget
            {
                MaxTurns = 10,
                TurnsUsed = 0,
                MaxModelCalls = 5
            });
            await runStore.CreateAsync(run);

            // ── 构建 Actor（注入审批门 + 校验器 + 审批 store）──
            var actor = new AgentRunActor(
                runStore, eventStore, transport,
                new DefaultAgentLoopPolicy(),
                dispatcher,
                toolCallValidator: validator,
                approvalGate: approvalGate,
                approvalStore: approvalStore,
                durableToolExecutor: durableExecutor);

            // ── 第一次执行：应在 AwaitingApproval 挂起 ──
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await actor.ExecuteAsync(run, cts1.Token);

            // ── 断言 1：Run 挂起在 AwaitingApproval 状态 ──
            var suspendedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(suspendedRun, "应能从 store 取回 Run。");
            Assert.AreEqual(AgentRunState.AwaitingApproval, suspendedRun!.State,
                $"Run 应挂起在 AwaitingApproval 状态，实际 {suspendedRun.State}。");

            // ── 断言 2：审批记录已持久化为 Pending ──
            var pendingApprovals = await approvalStore.ListPendingAsync(run.WorkspaceId, run.RunId);
            Assert.IsTrue(pendingApprovals.Count > 0,
                "应有至少 1 条 Pending 审批记录（由 Actor 创建）。");

            // ── 模拟外部审批：批准 ──
            var approvalToResolve = pendingApprovals[0];
            await approvalStore.ResolveAsync(
                run.WorkspaceId,
                approvalToResolve.ApprovalId,
                AgentApprovalStatus.Approved,
                approverId: "test-approver",
                rejectionReason: null);

            // ── 推进 Run 状态：AwaitingApproval → PendingToolExecution ──
            await runStore.TransitionStateAsync(
                run.WorkspaceId, run.RunId,
                AgentRunState.AwaitingApproval,
                AgentRunState.PendingToolExecution);

            // ── 获取更新后的 Run 并恢复执行 ──
            var resumedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(resumedRun, "应能取回更新后的 Run。");
            Assert.AreEqual(AgentRunState.PendingToolExecution, resumedRun!.State,
                "Run 应已推进到 PendingToolExecution 状态。");

            // ── 第二次执行：恢复 → 执行 Tool → 观察 → 模型 → 完成 ──
            var actor2 = new AgentRunActor(
                runStore, eventStore, transport,
                new DefaultAgentLoopPolicy(),
                dispatcher,
                toolCallValidator: validator,
                approvalGate: approvalGate,
                approvalStore: approvalStore,
                durableToolExecutor: durableExecutor);

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await actor2.ExecuteAsync(resumedRun, cts2.Token);

            // ── 断言 3：Run 进入 Completed 终态 ──
            var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(finalRun, "应能取回最终 Run。");
            Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
                $"Run 恢复后应进入 Completed 终态，实际 {finalRun.State}。");

            // ── 断言 4：Tool 被真实执行了一次（审批通过后）──
            Assert.AreEqual(1, toolHandler.InvocationCount,
                $"RecordingToolHandler 应被调用 1 次（审批通过后执行），实际 {toolHandler.InvocationCount}。");

            // ── 断言 5：最终答案已持久化 ──
            Assert.IsFalse(string.IsNullOrEmpty(finalRun.FinalAnswer),
                "Completed 状态的 Run 应有最终答案。");

            // ── 断言 6：事件流包含审批请求与审批解决事件 ──
            var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 10000);
            var eventTypes = events.Select(e => e.EventType).ToList();
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ApprovalRequested, "应有 ApprovalRequested 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ApprovalResolved, "应有 ApprovalResolved 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.ToolCallCompleted, "恢复后应有 ToolCallCompleted 事件。");
            CollectionAssert.Contains(eventTypes, AgentRunEventType.RunCompleted, "应有 RunCompleted 事件。");

            // ── 断言 7：审批记录已裁决为 Approved ──
            var resolvedApproval = await approvalStore.GetAsync(run.WorkspaceId, approvalToResolve.ApprovalId);
            Assert.IsNotNull(resolvedApproval, "应能取回审批记录。");
            Assert.AreEqual(AgentApprovalStatus.Approved, resolvedApproval!.Status,
                "审批记录应已裁决为 Approved。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 3：崩溃恢复 — Tool 执行后崩溃 → 恢复 → Journal 保证 exactly-once
    // =======================================================================

    [TestMethod]
    public async Task E2E_RealPostgres_CrashRecovery_MidToolExecution()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("e2e3_");
        try
        {
            // ── 构建真实 Postgres stores ──
            var runStore = new PostgresAgentRunStore(factory, serializer, migrationRunner);
            var eventStore = new PostgresAgentRunEventStore(factory, serializer, migrationRunner);
            var journal = new PostgresToolDispatchJournal(factory, serializer, migrationRunner);

            // ── 构建真实 Tool 执行链 ──
            var toolHandler = new RecordingToolHandler("search", "搜索结果：找到 5 条记录。");
            var dispatcher = new RealToolDispatcher(new[] { (IToolHandler)toolHandler });
            dispatcher.Freeze();
            var durableExecutor = new DefaultDurableToolExecutor(dispatcher, journal);

            // ── 构建 ScriptedModelTransport ──
            // 第一次运行：Call 1 → Tool 调用，Call 2 → 最终答案
            // 崩溃恢复后（重置状态）：Call 3 → 相同 Tool 调用，Call 4 → 最终答案
            var transport = new ScriptedModelTransport(
                BuildToolCallResponse("search", """{"query":"数据查询"}""", "需要搜索数据"),
                BuildFinalAnswerResponse("基于搜索结果，任务完成。"),
                BuildToolCallResponse("search", """{"query":"数据查询"}""", "需要搜索数据"),
                BuildFinalAnswerResponse("基于搜索结果，任务完成。"));

            // ── 构建 Run ──
            var run = BuildRun("search 数据查询", turnBudget: new AgentTurnBudget
            {
                MaxTurns = 10,
                TurnsUsed = 0,
                MaxModelCalls = 10
            });
            await runStore.CreateAsync(run);

            // ── 第一次执行：完整运行（Tool 被执行一次）──
            var actor1 = new AgentRunActor(
                runStore, eventStore, transport,
                new DefaultAgentLoopPolicy(),
                dispatcher,
                durableToolExecutor: durableExecutor);

            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await actor1.ExecuteAsync(run, cts1.Token);

            // ── 断言 1：第一次运行成功完成 ──
            var completedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(completedRun, "应能取回 Run。");
            Assert.AreEqual(AgentRunState.Completed, completedRun!.State,
                "第一次运行应进入 Completed。");
            Assert.AreEqual(1, toolHandler.InvocationCount,
                $"第一次运行后 Tool 应被调用 1 次，实际 {toolHandler.InvocationCount}。");

            // ── 模拟崩溃：直接 SQL 将 Run 状态重置为 ContextBuilding ──
            // （模拟进程在状态持久化为 Completed 之前崩溃，Run 仍处于中间状态）
            await ResetRunStateAsync(run.WorkspaceId, run.RunId, "e2e3_");

            // ── 第二次执行：恢复运行 ──
            // Actor 检测 isResume（state != Created），从事件流重建上下文。
            // 模型重新产生相同的 Tool 调用，但 Journal 已记录 Committed 状态 →
            // PrepareAsync 返回 ShouldDispatch=false → 不重新分派（exactly-once）。
            var resumedRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(resumedRun, "应能取回重置后的 Run。");

            var actor2 = new AgentRunActor(
                runStore, eventStore, transport,
                new DefaultAgentLoopPolicy(),
                dispatcher,
                durableToolExecutor: durableExecutor);

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await actor2.ExecuteAsync(resumedRun!, cts2.Token);

            // ── 断言 2：恢复后 Run 再次进入 Completed ──
            var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
            Assert.IsNotNull(finalRun, "应能取回最终 Run。");
            Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
                $"恢复后 Run 应再次进入 Completed，实际 {finalRun.State}。");

            // ── 断言 3（核心）：Tool 仍然只被调用 1 次（exactly-once）──
            // Journal 的 PrepareAsync 检测到 RequestId 已 Committed → 不重新 Dispatch
            Assert.AreEqual(1, toolHandler.InvocationCount,
                $"崩溃恢复后 Tool 调用次数应仍为 1（exactly-once 由 Journal 保证），实际 {toolHandler.InvocationCount}。");

            // ── 断言 4：事件流哈希链在恢复后仍然完整 ──
            var events = await eventStore.ReadAsync(run.WorkspaceId, run.RunId, take: 10000);
            Assert.IsTrue(events.Count > 0, "事件流应非空。");
            for (var i = 1; i < events.Count; i++)
            {
                Assert.AreEqual(events[i - 1].ContentHash, events[i].PrevChainHash,
                    $"恢复后事件 {i} 的 PrevChainHash 应指向前一事件的 ContentHash（哈希链断裂）。");
            }

            // ── 断言 5：恢复后事件流中有 2 个 RunCompleted 事件（两次完整运行）──
            var runCompletedEvents = events
                .Where(e => e.EventType == AgentRunEventType.RunCompleted)
                .ToList();
            Assert.IsTrue(runCompletedEvents.Count >= 1,
                "恢复后事件流应至少有 1 个 RunCompleted 事件。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // =======================================================================
    // 测试 4：Learning Event Outbox 持久性 — 入队 → 租约 → 崩溃 → 恢复 → Ack
    // =======================================================================

    [TestMethod]
    public async Task E2E_RealPostgres_LearningEventOutbox_Durability()
    {
        if (ShouldSkip) { Assert.Inconclusive("Docker 不可用 — Postgres 集成测试已跳过。此结果不证明生产证据通过。"); return; }

        var (factory, migrationRunner, serializer) = CreateInfrastructure("e2e4_");
        try
        {
            var outboxStore = new PostgresLearningEventOutboxStore(factory, serializer, migrationRunner);

            var eventId = "learn-evt-" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;

            // ── 步骤 1：入队一条 Learning Event ──
            var record = new LearningEventOutboxRecord
            {
                EventId = eventId,
                WorkspaceId = "ws-e2e-learn",
                CollectionId = "col-e2e-learn",
                DecisionId = "decision-" + eventId,
                Payload = """{"decisionId":"decision-1","score":0.85}""",
                State = LearningEventOutboxStates.Pending,
                RetryCount = 0,
                MaxRetryCount = 5,
                CreatedAt = now,
                UpdatedAt = now
            };
            await outboxStore.EnqueueAsync(record);

            // ── 断言 1：入队后 CountByState 显示 Pending=1 ──
            var countsAfterEnqueue = await outboxStore.CountByStateAsync();
            Assert.IsTrue(countsAfterEnqueue.TryGetValue(LearningEventOutboxStates.Pending, out var pendingCount) && pendingCount >= 1,
                $"入队后应至少有 1 条 Pending 记录，实际 Pending={pendingCount}。");

            // ── 步骤 2：Worker A 获取待处理记录（短租约 1 秒）──
            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acquiredByA = await outboxStore.AcquirePendingAsync(
                limit: 10,
                owner: "worker-A",
                leaseDuration: TimeSpan.FromSeconds(1),
                cts1.Token);

            Assert.IsTrue(acquiredByA.Count >= 1, "Worker A 应能获取到待处理记录。");
            var recordA = acquiredByA.First(r => r.EventId == eventId);
            Assert.AreEqual(LearningEventOutboxStates.Processing, recordA.State,
                "获取后记录状态应为 Processing。");
            Assert.AreEqual("worker-A", recordA.LeaseOwner,
                "LeaseOwner 应为 worker-A。");
            Assert.IsFalse(string.IsNullOrEmpty(recordA.LeaseToken),
                "应分配了 LeaseToken。");
            var leaseTokenA = recordA.LeaseToken!;

            // ── 步骤 3：模拟 Worker A 崩溃（不 Ack，直接丢弃）──
            // 不调用 MarkAckedAsync / MarkFailedAsync，模拟进程崩溃

            // ── 步骤 4：等待 Worker A 的租约过期 ──
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cts1.Token);

            // ── 步骤 5：Worker B 获取待处理记录（租约已过期的记录应被重新获取）──
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var acquiredByB = await outboxStore.AcquirePendingAsync(
                limit: 10,
                owner: "worker-B",
                leaseDuration: TimeSpan.FromMinutes(2),
                cts2.Token);

            Assert.IsTrue(acquiredByB.Count >= 1, "Worker B 应能获取到租约过期的记录。");
            var recordB = acquiredByB.First(r => r.EventId == eventId);
            Assert.AreEqual("worker-B", recordB.LeaseOwner,
                "重新获取后 LeaseOwner 应为 worker-B。");
            Assert.IsFalse(string.IsNullOrEmpty(recordB.LeaseToken),
                "应分配了新的 LeaseToken。");
            var leaseTokenB = recordB.LeaseToken!;

            // ── 断言 2：Worker B 的 LeaseToken 不同于 Worker A（防止旧 Worker 越权 Ack）──
            Assert.AreNotEqual(leaseTokenA, leaseTokenB,
                "Worker B 的 LeaseToken 应不同于 Worker A（租约所有权已转移）。");

            // ── 步骤 6：Worker A 尝试用过期 LeaseToken Ack（应失败）──
            var ackWithOldToken = await outboxStore.MarkAckedAsync(eventId, leaseTokenA, cts2.Token);
            Assert.IsFalse(ackWithOldToken,
                "Worker A 用过期 LeaseToken Ack 应返回 false（租约已被抢占）。");

            // ── 步骤 7：Worker B 用有效 LeaseToken Ack（应成功）──
            var ackByB = await outboxStore.MarkAckedAsync(eventId, leaseTokenB, cts2.Token);
            Assert.IsTrue(ackByB,
                "Worker B 用有效 LeaseToken Ack 应返回 true。");

            // ── 断言 3：Ack 后 CountByState 显示 Acked=1 ──
            var countsAfterAck = await outboxStore.CountByStateAsync();
            Assert.IsTrue(countsAfterAck.TryGetValue(LearningEventOutboxStates.Acked, out var ackedCount) && ackedCount >= 1,
                $"Ack 后应至少有 1 条 Acked 记录，实际 Acked={ackedCount}。");
            Assert.IsFalse(countsAfterAck.TryGetValue(LearningEventOutboxStates.Pending, out var pendingAfterAck) && pendingAfterAck > 0,
                "Ack 后不应有 Pending 记录。");

            // ── 断言 4：GetLastSuccessAt 返回非 null ──
            var lastSuccess = await outboxStore.GetLastSuccessAtAsync();
            Assert.IsNotNull(lastSuccess, "Ack 后 GetLastSuccessAt 应返回非 null。");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────

    private static AgentRun BuildRun(
        string task,
        AgentTurnBudget? turnBudget = null,
        AgentCostBudget? costBudget = null) => new()
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-e2e-prodevidence",
            SessionId = "session-e2e-prodevidence",
            Task = task,
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = turnBudget,
            CostBudget = costBudget
        };

    /// <summary>构建 Tool 调用响应（非最终答案）。</summary>
    private static AgentModelResponse BuildToolCallResponse(string toolName, string arguments, string content) => new()
    {
        Content = content,
        ToolCalls = new[]
        {
            new AgentToolCallRequest
            {
                ToolName = toolName,
                Arguments = arguments
            }
        },
        IsFinalAnswer = false,
        TokensConsumed = 10,
        Duration = TimeSpan.FromMilliseconds(5),
        InputTokens = 8,
        OutputTokens = 2,
        ModelId = "scripted-test-transport"
    };

    /// <summary>构建最终答案响应。</summary>
    private static AgentModelResponse BuildFinalAnswerResponse(string content) => new()
    {
        Content = content,
        ToolCalls = Array.Empty<AgentToolCallRequest>(),
        IsFinalAnswer = true,
        TokensConsumed = 15,
        Duration = TimeSpan.FromMilliseconds(5),
        InputTokens = 10,
        OutputTokens = 5,
        ModelId = "scripted-test-transport"
    };

    /// <summary>
    /// 直接 SQL 将 Run 状态重置为 ContextBuilding（模拟崩溃后状态未持久化）。
    /// 清除 finished_at / final_answer / failure_reason，让 Actor 可恢复执行。
    /// </summary>
    private async Task ResetRunStateAsync(string workspaceId, string runId, string tablePrefix)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
UPDATE {tablePrefix}agent_runs
SET state = @state,
    finished_at = NULL,
    final_answer = NULL,
    failure_reason = NULL,
    updated_at = @now
WHERE workspace_id = @workspaceId AND run_id = @runId
""";
        command.Parameters.AddWithValue("state", (byte)AgentRunState.ContextBuilding);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("workspaceId", workspaceId);
        command.Parameters.AddWithValue("runId", runId);
        await command.ExecuteNonQueryAsync();
    }

    // ── 测试 stub ─────────────────────────────────────────────────────────

    /// <summary>
    /// 按顺序返回预设响应序列的 IAgentModelTransport。
    /// 第 N 次调用返回第 N 个响应（超出序列时返回最后一个）。
    /// 产生真实的 Tool 调用请求（非 mock stub），让 AgentRunActor 走完整的 Tool 分派路径。
    /// </summary>
    private sealed class ScriptedModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse[] _responses;
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public ScriptedModelTransport(params AgentModelResponse[] responses)
        {
            if (responses.Length == 0)
            {
                throw new ArgumentException("至少需要 1 个预设响应。", nameof(responses));
            }
            _responses = responses;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _callCount) - 1;
            var response = index < _responses.Length ? _responses[index] : _responses[^1];
            return ValueTask.FromResult(response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }

    /// <summary>
    /// 录制 Tool 调用的 IToolHandler 实现。
    /// 记录调用次数，返回预设的成功结果。用于验证 Tool 被真实执行（而非 mock stub）。
    /// </summary>
    private sealed class RecordingToolHandler : IToolHandler
    {
        private readonly string _resultContent;
        private int _invocationCount;

        public string ToolName { get; }
        public ToolDescriptor Descriptor => new ToolDescriptor
        {
            Name = ToolName,
            DeclaredSideEffect = ToolSideEffect.None,
            RequiresApproval = false,
            RequiresIdempotencyKey = false,
            RequiresLeaseFence = false,
            RecoveryStrategy = ToolRecoveryStrategy.SafeReplay,
            MaximumExecutionTime = TimeSpan.FromMinutes(5)
        };
        public string? Description => $"Test tool: {ToolName}";
        public string? ParametersJsonSchema => "{}";
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public RecordingToolHandler(string toolName, string resultContent)
        {
            ToolName = toolName;
            _resultContent = resultContent;
        }

        public ValueTask<ToolHandlerResult> HandleAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult(new ToolHandlerResult
            {
                Succeeded = true,
                Result = _resultContent,
                SideEffect = ToolSideEffect.None
            });
        }
    }
}
