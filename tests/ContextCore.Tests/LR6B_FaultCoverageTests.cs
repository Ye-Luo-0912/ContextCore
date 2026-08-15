using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// ===========================================================================
// Agent Run 故障与多节点：行为级覆盖补齐
//
// 与既有故障测试互补，补齐三类场景的行为级断言：
// 1. checkpoint 本体损坏（JSON 非法 / metadata 缺失）→ 恢复降级为全量事件重放，
//    不崩溃、不丢事件、Run 可完成（快路径 fail-open 降级，不误判数据损坏）。
// 2. outbox 重放：决策提交 worker 落库后崩溃（未 Ack）→ 租约过期重新领取重投递 →
//    决策记录不重复落库（决策记录存储按 decision_id 幂等覆盖，provider parity）。
// 3. 质量反馈一致性边界：延迟归因只在 Run 真终态发生；非终态（RetryPending）
//    不归因——反馈事件不先于业务结果产生，也不因归因失败阻塞终态持久化。
// ===========================================================================

[TestClass]
[TestCategory("LR6B")]
public sealed class LR6B_FaultCoverageTests
{
    private const string WorkspaceId = "ws-lr6b";
    private const string SessionId = "session-lr6b";

    // ── 场景 1：checkpoint 本体损坏 → 降级全量重放 ──────────────────────────

    [TestMethod]
    public async Task CheckpointBody_InvalidJson_FallsBackToFullReplay_Completes()
    {
        var runStore = new InMemoryAgentRunStore();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore, checkpointStore);
        var run = BuildRun("search 恢复验证");
        await runStore.CreateAsync(run);

        // 存在 checkpoint cursor，但本体 metadata 的 conversationJson 是非法 JSON
        //（模拟磁盘 / 序列化损坏）——恢复必须降级为全量事件重放，而不是崩溃
        // 或误判 RecoveryCorrupted（事件链本身完好，数据可重建）。
        var corruptBody = new AgentCheckpoint
        {
            CheckpointId = "ckpt-corrupt-json",
            Session = new AgentSessionId
            {
                Value = SessionId,
                WorkspaceId = WorkspaceId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            StateJson = "{}",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conversationJson"] = "{not-valid-json",
                ["toolObservationsJson"] = "[]",
                ["executionModelTurn"] = "1"
            }
        };
        await SeedEventsWithCheckpointAsync(eventStore, run, corruptBody);

        var resumedRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(resumedRun, "seed 后 Run 应存在。");

        var actor = new AgentRunActor(
            runStore, eventStore,
            new DeterministicAgentModelTransport(new Dictionary<string, string> { ["search"] = "echo" }),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            checkpointStore: checkpointStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(resumedRun!, cts.Token);

        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            $"checkpoint 本体损坏应降级全量重放并完成，实际 {finalRun.State}（{finalRun.FailureReason}）。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(finalRun.FinalAnswer), "最终答案应已持久化。");

        // 不丢事件、不重复播种：RunCreated 只应出现一次
        var events = await eventStore.ReadAsync(WorkspaceId, run.RunId);
        Assert.AreEqual(1, events.Count(e => e.EventType == AgentRunEventType.RunCreated),
            "恢复不得重放 RunCreated（事件流保持唯一锚点）。");
    }

    [TestMethod]
    public async Task CheckpointBody_MissingMetadata_FallsBackToFullReplay_Completes()
    {
        var runStore = new InMemoryAgentRunStore();
        var checkpointStore = new InMemoryAgentCheckpointStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore, checkpointStore);
        var run = BuildRun("search 恢复验证");
        await runStore.CreateAsync(run);

        // 旧格式 checkpoint：metadata 缺 executionModelTurn（不完整）→ 快路径不可用，
        // 降级全量事件重放，不崩溃、不误判损坏。
        var incompleteBody = new AgentCheckpoint
        {
            CheckpointId = "ckpt-missing-metadata",
            Session = new AgentSessionId
            {
                Value = SessionId,
                WorkspaceId = WorkspaceId,
                CreatedAt = DateTimeOffset.UtcNow
            },
            CreatedAt = DateTimeOffset.UtcNow,
            StateJson = "{}",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conversationJson"] = "[]",
                ["toolObservationsJson"] = "[]"
            }
        };
        await SeedEventsWithCheckpointAsync(eventStore, run, incompleteBody);

        var resumedRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(resumedRun);

        var actor = new AgentRunActor(
            runStore, eventStore,
            new DeterministicAgentModelTransport(new Dictionary<string, string> { ["search"] = "echo" }),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            checkpointStore: checkpointStore);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(resumedRun!, cts.Token);

        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            $"checkpoint metadata 不完整应降级全量重放并完成，实际 {finalRun.State}（{finalRun.FailureReason}）。");
    }

    // ── 场景 2：outbox 重放不重复落库 ──────────────────────────────────────

    [TestMethod]
    public async Task OutboxReplay_WorkerCrashAfterPersistBeforeAck_RedeliveryDoesNotDuplicate()
    {
        var root = Path.Combine(Path.GetTempPath(), "lr6b-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 文件系统 provider 的决策记录存储：重投递必须幂等覆盖，与 InMemory / Postgres 一致。
            var trace = new FileDecisionTraceStore(new FileStorageOptions { RootPath = root });
            var outbox = new InMemoryDecisionCommitOutbox();
            var commit = BuildCommit("decision-replay-1");
            await outbox.EnqueueAsync(commit);

            // worker-1 领取并落库，随后崩溃（落库成功但 Ack 未发出）
            var first = await outbox.AcquirePendingAsync(10, "worker-1", TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(1, first.Count, "首次领取应取到待处理提交。");
            await trace.SaveAsync(first[0].Record, CancellationToken.None);

            // 租约过期后 worker-2 重新领取（重投递）并再次落库
            await Task.Delay(60);
            var redelivered = await outbox.AcquirePendingAsync(10, "worker-2", TimeSpan.FromMinutes(1));
            Assert.AreEqual(1, redelivered.Count, "未 Ack 且租约过期 → 应重投递。");
            Assert.AreEqual(commit.DecisionId, redelivered[0].DecisionId, "重投递应为同一条决策提交。");
            await trace.SaveAsync(redelivered[0].Record, CancellationToken.None);

            // 决策记录唯一：重投递不得重复落库
            var records = await trace.QueryRecentAsync(commit.WorkspaceId, commit.CollectionId, 50, CancellationToken.None);
            Assert.AreEqual(1, records.Count, "outbox 重投递后决策记录应唯一（不重复应用）。");
            var point = await trace.GetAsync(commit.WorkspaceId, commit.CollectionId, commit.DecisionId, CancellationToken.None);
            Assert.IsNotNull(point, "决策记录应可按稳定主键点查。");

            // worker-2 正常 Ack 后无积压
            Assert.IsTrue(await outbox.AckAsync(redelivered[0].OutboxId, redelivered[0].LeaseToken!),
                "正确租约 token 应 Ack 成功。");
            var pending = await outbox.AcquirePendingAsync(10, "probe", TimeSpan.FromMinutes(1));
            Assert.AreEqual(0, pending.Count, "Ack 后不应再有待处理条目。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不阻塞断言 */ }
        }
    }

    [TestMethod]
    public async Task FileDecisionTraceStore_SameDecision_UpsertSingleRecord_LatestWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "lr6b-trace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trace = new FileDecisionTraceStore(new FileStorageOptions { RootPath = root });
            var v1 = BuildRecord("decision-u1", "query-v1");
            await trace.SaveAsync(v1, CancellationToken.None);
            await trace.SaveAsync(v1, CancellationToken.None);   // 重放同一条
            var v2 = BuildRecord("decision-u1", "query-v2");     // 同 decision_id 更新
            await trace.SaveAsync(v2, CancellationToken.None);

            var all = await trace.QueryRecentAsync(WorkspaceId, "col-trace", 50, CancellationToken.None);
            Assert.AreEqual(1, all.Count, "同 decision_id 多次保存应只保留一条（幂等覆盖）。");
            Assert.AreEqual("query-v2", all[0].QueryText, "最新内容应覆盖旧记录。");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 清理失败不阻塞断言 */ }
        }
    }

    // ── 场景 3：质量反馈与业务结果的一致性边界 ────────────────────────────

    [TestMethod]
    public async Task DeferredAttribution_TerminalRun_RecordsAutomatedEvaluationFeedback()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("search 完成验证");
        await runStore.CreateAsync(run);

        var runtime = new RecordingDecisionRuntime(R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 0, DroppedCount = 0 }
        }));
        var planner = new RecordingAdaptivePlanner();
        var actor = new AgentRunActor(
            runStore, eventStore,
            new DeterministicAgentModelTransport(new Dictionary<string, string> { ["search"] = "echo" }),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: runtime,
            adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State,
            $"Run 应完成，实际 {finalRun.State}（{finalRun.FailureReason}）。");

        // 终态延迟归因：反馈事件带 AutomatedEvaluation 来源标记（业务结果落库后才归因），
        // 并携带本 Run 使用的检索计划签名。
        Assert.IsTrue(
            planner.Outcomes.Any(o => o.Source == RetrievalFeedbackSource.AutomatedEvaluation
                && !string.IsNullOrWhiteSpace(o.PlanSignature)),
            "终态 Run 应产生带计划签名的延迟归因反馈（AutomatedEvaluation 来源）。");
    }

    [TestMethod]
    public async Task DeferredAttribution_NonTerminalRetryPending_DoesNotAttribute()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("search 失败验证", maxRetries: 1);
        await runStore.CreateAsync(run);

        var runtime = new RecordingDecisionRuntime(R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 0, DroppedCount = 0 }
        }));
        var planner = new RecordingAdaptivePlanner();
        var actor = new AgentRunActor(
            runStore, eventStore,
            new ThrowingModelTransport(),
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: runtime,
            adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await actor.ExecuteAsync(run, cts.Token);

        var finalRun = await runStore.GetAsync(WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.RetryPending, finalRun!.State,
            "重试预算未耗尽且模型故障 → 应停在 RetryPending（非终态）。");

        // 一致性边界：非终态不归因——质量反馈事件不得先于业务结果产生。
        Assert.IsFalse(
            planner.Outcomes.Any(o => o.Source == RetrievalFeedbackSource.AutomatedEvaluation),
            "非终态（RetryPending）不得产生延迟归因反馈。");
    }

    // ── 测试辅助 ──────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string task, int maxRetries = 0) => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        MaxRetries = maxRetries,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 5
        }
    };

    /// <summary>预置合法事件流（RunCreated + StateTransition→ContextBuilding），
    /// 并随同一批写入 checkpoint cursor + 损坏/不完整的 checkpoint 本体。</summary>
    private static async Task SeedEventsWithCheckpointAsync(
        InMemoryAgentRunEventStore eventStore,
        AgentRun run,
        AgentCheckpoint checkpointBody)
    {
        var seq0 = AgentRunEventChain.BuildEvent(
            run.RunId, run.WorkspaceId, sequence: 0,
            type: AgentRunEventType.RunCreated,
            state: AgentRunState.Created,
            payload: """{"runId":"seed"}""",
            prevChainHash: null);
        var seq1 = AgentRunEventChain.BuildEvent(
            run.RunId, run.WorkspaceId, sequence: 1,
            type: AgentRunEventType.StateTransition,
            state: AgentRunState.ContextBuilding,
            payload: """{"from":"Created","to":"ContextBuilding"}""",
            prevChainHash: seq0.ContentHash);

        var cursor = new AgentCheckpointCursor
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            CheckpointId = checkpointBody.CheckpointId,
            LastEventSequence = 1
        };
        var runStateUpdate = new AgentRunStateUpdate
        {
            WorkspaceId = run.WorkspaceId,
            RunId = run.RunId,
            ExpectedCurrentState = AgentRunState.Created,
            NewState = AgentRunState.ContextBuilding,
            RunSnapshot = run with { State = AgentRunState.ContextBuilding, UpdatedAt = DateTimeOffset.UtcNow }
        };
        await eventStore.AppendBatchAsync(
            [seq0, seq1], runStateUpdate, cursor, checkpointBody, CancellationToken.None);
    }

    private static DecisionCommitOutboxRecord BuildCommit(string decisionId) => new()
    {
        DecisionId = decisionId,
        WorkspaceId = WorkspaceId,
        CollectionId = "col-trace",
        CommitType = DecisionCommitType.RecordAndMaterialize,
        Record = BuildRecord(decisionId, "test query"),
        EvidenceRef = "sig:abc",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static ContextDecisionRecord BuildRecord(string decisionId, string queryText) => new()
    {
        DecisionId = decisionId,
        Source = ContextDecisionSource.Retrieval,
        WorkspaceId = WorkspaceId,
        CollectionId = "col-trace",
        QueryText = queryText,
        Candidates = Array.Empty<ContextDecisionCandidate>(),
        CreatedAt = DateTimeOffset.UtcNow,
        PolicyVersion = "policy/v1"
    };

    /// <summary>录制反馈的 IAdaptiveRetrievalPlanner 桩（不应用策略，只收集信号）。</summary>
    private sealed class RecordingAdaptivePlanner : IAdaptiveRetrievalPlanner
    {
        public List<RetrievalPlanFeedback> Outcomes { get; } = new();

        public Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
            => Task.FromResult(new AgentRetrievalPlan { Reason = "录制规划器" });

        public ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
        {
            Outcomes.Add(feedback);
            return ValueTask.CompletedTask;
        }

        public ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
            => ValueTask.FromResult(new AdaptiveRetrievalPolicy
            {
                PlanSignature = AdaptiveRetrievalPlanSignature.Compute(input),
                TokenBudgetMultiplier = 1.0,
                QueryConvergenceMultiplier = 1.0,
                RecallBoostMultiplier = 1.0,
                FeedbackSampleCount = 0,
                ComputedAtUtc = DateTimeOffset.UtcNow
            });

        public ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(
            string workspaceId, string planSignature, CancellationToken ct = default)
            => GetPolicyAsync(new AgentRetrievalPlannerInput { OriginalTask = planSignature }, ct);

        public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(
            string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());

        public ValueTask<int> ResetAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default)
            => ValueTask.FromResult(0);
    }

    /// <summary>始终抛异常的模型通道（模拟模型故障 → Run 走 FailAsync 分类）。</summary>
    private sealed class ThrowingModelTransport : IAgentModelTransport
    {
        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟模型通道故障。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟模型通道故障。");

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("模拟模型通道故障。");
    }

    /// <summary>固定返回构造时指定执行结果的 IContextDecisionRuntime 桩（录制请求）。</summary>
    private sealed class RecordingDecisionRuntime : IContextDecisionRuntime
    {
        private readonly ContextDecisionExecutionResult _execution;

        public RecordingDecisionRuntime(ContextDecisionExecutionResult execution) => _execution = execution;

        public List<ContextDecisionRuntimeRequest> Requests { get; } = new();

        public ValueTask<ContextDecisionResult> ExecuteAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_execution.Decision);
        }

        public ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_execution);
        }
    }
}
