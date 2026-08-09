using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

// ===========================================================================
// Adaptive Retrieval 接入 Agent 主链验收测试
//
// 覆盖：
// 1. ContextBuilding 阶段调用 IAdaptiveRetrievalPlanner.PlanAsync，计划 TokenBudget
//    注入决策请求（planner → Actor 上下文构建）；
// 2. 决策执行后记录检索结果反馈（命中数 / 预算超限 / 是否产出候选），闭环学习信号；
// 3. 规划器异常不阻断主链（自适应是增强层，降级为引擎默认预算）；
// 4. 未注入规划器时行为不变（TokenBudget=0，走引擎默认）。
// ===========================================================================

[TestClass]
[TestCategory("Agent-Run-Full-Loop")]
[TestCategory("R29")]
public sealed class R31D_AgentAdaptiveRetrievalTests
{
    private const string Purpose = "agent-context";

    [TestMethod]
    public async Task ContextBuilding_PlansWithAdaptivePlanner_AndFeedsBudget()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan { TokenBudget = 2048 });
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 2, effectiveTokens: 1500, tokenBudget: 2048);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 1. ContextBuilding 调用规划器：计划 TokenBudget 注入决策请求。
        Assert.AreEqual(1, planner.PlanCallCount, "ContextBuilding 应调用一次自适应规划器。");
        Assert.IsNotNull(decisionRuntime.LastRequest);
        Assert.AreEqual(2048, decisionRuntime.LastRequest!.TokenBudget,
            "计划 TokenBudget 应注入决策请求（planner → Actor 上下文构建）。");

        // 2. 决策执行后记录检索结果反馈（闭环学习信号——即时过程信号 Source=Runtime）。
        var runtimeRecords = planner.OutcomeRecords.Where(r => r.Source == RetrievalFeedbackSource.Runtime).ToList();
        Assert.AreEqual(1, runtimeRecords.Count, "每次上下文构建应记录一条即时反馈。");
        var record = runtimeRecords[0];
        Assert.AreEqual(run.WorkspaceId, record.WorkspaceId, "反馈应归属到 Run 的工作区。");
        Assert.AreEqual(2, record.HitsReturned, "命中数应为选中候选数。");
        Assert.IsTrue(record.Effective, "产出候选结果应视为有效信号。");
        Assert.AreEqual(
            AdaptiveRetrievalPlanSignature.Compute(new AgentRetrievalPlannerInput
            {
                OriginalTask = run.Task,
                LatestAssistantIntent = run.Task,
                UnresolvedGoals = new[] { run.Task },
                WorkspaceId = run.WorkspaceId,
                CollectionId = run.WorkspaceId,
                Purpose = Purpose
            }),
            record.PlanSignature, "反馈签名应与规划输入派生一致。");

        // 3. Run 正常完成（主链未被自适应层阻断）。
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State);
    }

    /// <summary>
    /// 验证（P1-十六）：Decision Runtime 原生消费受控计划——
    /// 受控查询文本（非 run.Task）、TopK、RequiredIds 全部注入决策请求，
    /// 不再"只拿 TokenBudget 后仍以 run.Task 检索"。
    /// </summary>
    [TestMethod]
    public async Task ContextBuilding_ConsumesControlledQuery_TopK_AndRequiredIds()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan
        {
            TokenBudget = 2048,
            TopK = 12,
            ControlledQueries = new[]
            {
                new AgentRetrievalQuery { Text = "受控查询-任务分解", Type = AgentRetrievalQueryType.Hybrid, Weight = 1.0 },
                new AgentRetrievalQuery { Text = "受控查询-意图补充", Type = AgentRetrievalQueryType.Keyword, Weight = 0.5 }
            },
            RequiredIds = new[] { "entity-required-1" }
        });
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 2, effectiveTokens: 1500, tokenBudget: 2048);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsNotNull(decisionRuntime.LastRequest);
        Assert.AreEqual("受控查询-任务分解", decisionRuntime.LastRequest!.QueryText,
            "QueryText 应为受控计划的首条查询（而非 run.Task 原文）。");
        Assert.AreNotEqual(run.Task, decisionRuntime.LastRequest.QueryText,
            "受控计划存在时不得回退 run.Task。");
        Assert.AreEqual(12, decisionRuntime.LastRequest.TopK,
            "TopK 应为受控计划产出值（非 0）。");
        CollectionAssert.Contains(
            decisionRuntime.LastRequest.AgentInput?.RequiredIds?.ToList() ?? new List<string>(), "entity-required-1",
            "RequiredIds 应注入决策请求（mandatory recall）。");

        // 反馈 QueryText 也应记录实际使用的受控查询文本（即时过程反馈）。
        Assert.AreEqual("受控查询-任务分解",
            planner.OutcomeRecords.First(r => r.Source == RetrievalFeedbackSource.Runtime).QueryText,
            "反馈应记录实际执行的受控查询文本。");
    }

    /// <summary>
    /// 验证（P1-十六）：反馈质量信号从选中候选真实分数派生——
    /// 不再用占位常量（Effective=result!=null / Confidence=1.0 / OutcomeQuality=1.0）
    /// 学习"有没有召回东西"；以"这些 Context 是否被采用及其质量"为学习信号。
    /// </summary>
    [TestMethod]
    public async Task ContextBuilding_FeedbackUsesSelectedQualitySignals()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan { TokenBudget = 2048 });
        // 两个选中候选：分数 0.9 与 0.5 → Confidence=0.9（最高）、OutcomeQuality=0.7（平均）。
        var decisionRuntime = new FakeDecisionRuntime(
            selectedCount: 2, effectiveTokens: 1500, tokenBudget: 2048,
            selectedScores: new[] { 0.9, 0.5 });
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var record = planner.OutcomeRecords.First(r => r.Source == RetrievalFeedbackSource.Runtime);
        Assert.IsTrue(record.Effective, "产出且被采用的候选 → 有效信号（非 result!=null 恒真）。");
        Assert.AreEqual(0.9, record.Confidence, 0.0001,
            "Confidence 应为最高选中候选分数（不再恒 1.0）。");
        Assert.AreEqual(0.7, record.OutcomeQuality, 0.0001,
            "OutcomeQuality 应为选中候选平均分（不再恒 1.0）。");
    }

    /// <summary>
    /// 验证（P1-十六）：未产出被采用候选时 Effective=false 且质量信号为中性/零——
    /// "召回为空"与"召回了但没有帮助"都不得伪造有效信号。
    /// </summary>
    [TestMethod]
    public async Task ContextBuilding_FeedbackNoSelected_NotEffective()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan { TokenBudget = 2048 });
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 0, effectiveTokens: 0, tokenBudget: 2048);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var record = planner.OutcomeRecords.First(r => r.Source == RetrievalFeedbackSource.Runtime);
        Assert.IsFalse(record.Effective, "无被采用候选时不得视为有效信号。");
        Assert.AreEqual(0.0, record.OutcomeQuality, 0.0001, "无候选时质量为零。");
        Assert.AreEqual(0.5, record.Confidence, 0.0001, "无分数时置信度为中性 0.5。");
    }

    /// <summary>
    /// 验证（P1-十六 延迟归因）：Run 到达终态后，把最终结果质量归因到本 Run
    /// 使用过的检索计划签名（Source=AutomatedEvaluation，幂等键含 runId）——
    /// 反馈是"这些 Context 是否帮助 Agent 完成任务"的结果信号。
    /// </summary>
    [TestMethod]
    public async Task DeferredAttribution_CompletedRun_AttributesHighQuality()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan
        {
            TokenBudget = 2048,
            ControlledQueries = new[]
            {
                new AgentRetrievalQuery { Text = "受控查询", Type = AgentRetrievalQueryType.Hybrid, Weight = 1.0 }
            }
        });
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 1, effectiveTokens: 100, tokenBudget: 2048);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        // 归因反馈：Run 完成 → 高质量归因（AutomatedEvaluation 来源）。
        var attribution = planner.OutcomeRecords.FirstOrDefault(r =>
            r.Source == RetrievalFeedbackSource.AutomatedEvaluation);
        Assert.IsNotNull(attribution, "Run 终态后应写入延迟归因反馈。");
        Assert.AreEqual(0.9, attribution!.OutcomeQuality, 0.0001, "Completed 归因质量应为 0.9。");
        Assert.AreEqual(0.9, attribution.Confidence, 0.0001, "终态归因高置信度。");
        Assert.IsTrue(attribution.Effective, "终态是可确认的结果 → 有效信号。");
        Assert.AreEqual(
            AdaptiveRetrievalPlanSignature.Compute(new AgentRetrievalPlannerInput
            {
                OriginalTask = run.Task,
                LatestAssistantIntent = run.Task,
                UnresolvedGoals = new[] { run.Task },
                WorkspaceId = run.WorkspaceId,
                CollectionId = run.WorkspaceId,
                Purpose = Purpose
            }),
            attribution.PlanSignature, "归因应落到本 Run 使用的检索计划签名。");
        Assert.AreEqual($"run:{run.RunId}:{attribution.PlanSignature}", attribution.IdempotencyKey,
            "归因幂等键 = run:runId:signature（重试/重放不重复归因）。");
    }

    /// <summary>
    /// 验证（P1-十六 延迟归因）：Run 失败 → 归因质量 0.2（检索未达成任务目标）；
    /// 非终态（RetryPending）不归因（还有后续 Attempt）。
    /// </summary>
    [TestMethod]
    public async Task DeferredAttribution_FailedRun_AttributesLowQuality()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan
        {
            TokenBudget = 2048,
            ControlledQueries = new[]
            {
                new AgentRetrievalQuery { Text = "受控查询", Type = AgentRetrievalQueryType.Hybrid, Weight = 1.0 }
            }
        });
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 1, effectiveTokens: 100, tokenBudget: 2048);
        var actor = new AgentRunActor(
            runStore, eventStore, new ThrowingModelRunTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var attribution = planner.OutcomeRecords.FirstOrDefault(r =>
            r.Source == RetrievalFeedbackSource.AutomatedEvaluation);
        Assert.IsNotNull(attribution, "Run 失败终态后也应写入延迟归因反馈。");
        Assert.AreEqual(0.2, attribution!.OutcomeQuality, 0.0001, "Failed 归因质量应为 0.2。");
    }

    /// <summary>模型传输 stub：调用即抛异常（触发 FailAsync → Failed 终态）。</summary>
    private sealed class ThrowingModelRunTransport : IAgentModelTransport
    {
        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated model failure");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated model failure");

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated model failure");
    }

    [TestMethod]
    public async Task ContextBuilding_RecordsBudgetExceeded_FromDecisionOutcome()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var planner = new FakeAdaptivePlanner(new AgentRetrievalPlan { TokenBudget = 1024 });
        // 决策结果：选中 5 个候选，但 3 个因预算超限被丢弃（BudgetExceededCount=3）。
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 5, effectiveTokens: 900, tokenBudget: 1024, budgetExceededCount: 3);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var record = planner.OutcomeRecords.FirstOrDefault(r => r.Source == RetrievalFeedbackSource.Runtime);
        Assert.IsNotNull(record, "应存在即时过程反馈。");
        Assert.IsTrue(record!.BudgetExceeded, "决策结果含预算拦截时应上报预算超限信号。");
    }

    [TestMethod]
    public async Task ContextBuilding_PlannerFailure_DoesNotBlockMainChain()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        // 规划器抛异常：自适应是增强层，主链必须继续（TokenBudget 回退为 0 = 引擎默认）。
        var planner = new ThrowingAdaptivePlanner(new InvalidOperationException("规划器故障"));
        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 1, effectiveTokens: 100, tokenBudget: 0);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsNotNull(decisionRuntime.LastRequest);
        Assert.AreEqual(0, decisionRuntime.LastRequest!.TokenBudget,
            "规划失败应回退为 0（引擎默认预算），不阻断主链。");
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State, "规划器故障不应阻止 Run 完成。");
    }

    [TestMethod]
    public async Task ContextBuilding_NoPlanner_UnchangedBehavior()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun();
        await runStore.CreateAsync(run);

        var decisionRuntime = new FakeDecisionRuntime(selectedCount: 1, effectiveTokens: 100, tokenBudget: 0);
        var actor = new AgentRunActor(
            runStore, eventStore, new FinalAnswerTransport(),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: decisionRuntime);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsNotNull(decisionRuntime.LastRequest);
        Assert.AreEqual(0, decisionRuntime.LastRequest!.TokenBudget,
            "未注入规划器时 TokenBudget 保持 0（引擎默认）。");
        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(finalRun);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State);
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun() => new()
    {
        RunId = "run-" + Guid.NewGuid().ToString("N"),
        WorkspaceId = "ws-adaptive",
        SessionId = "session-adaptive",
        Task = "分析 AlphaProtocol 部署状态",
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    /// <summary>一次性返回最终答案的模型传输（主链单轮完成）。</summary>
    private sealed class FinalAnswerTransport : IAgentModelTransport
    {
        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FinalAnswer());

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FinalAnswer());

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(FinalAnswer());

        private static AgentModelResponse FinalAnswer() => new()
        {
            Content = "分析完成",
            ToolCalls = Array.Empty<AgentToolCallRequest>(),
            IsFinalAnswer = true,
            TokensConsumed = 10,
            Duration = TimeSpan.FromMilliseconds(1)
        };
    }

    /// <summary>记录规划/反馈调用的自适应规划器桩。</summary>
    private sealed class FakeAdaptivePlanner : IAdaptiveRetrievalPlanner
    {
        private readonly AgentRetrievalPlan _plan;

        public FakeAdaptivePlanner(AgentRetrievalPlan plan) => _plan = plan;

        public int PlanCallCount { get; private set; }

        public List<RetrievalPlanFeedback> OutcomeRecords { get; } = new();

        public Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
        {
            PlanCallCount++;
            return Task.FromResult(_plan);
        }

        public ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
        {
            OutcomeRecords.Add(feedback);
            return ValueTask.CompletedTask;
        }

        public ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
            => ValueTask.FromResult(NeutralPolicy(input));

        public ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(string workspaceId, string planSignature, CancellationToken ct = default)
            => ValueTask.FromResult(NeutralPolicy(null));

        public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(
            string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RetrievalPlanFeedback>>(Array.Empty<RetrievalPlanFeedback>());

        public ValueTask<int> ResetAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default)
            => ValueTask.FromResult(0);

        private static AdaptiveRetrievalPolicy NeutralPolicy(AgentRetrievalPlannerInput? input) => new()
        {
            PlanSignature = input is null ? "sig:neutral" : AdaptiveRetrievalPlanSignature.Compute(input),
            TokenBudgetMultiplier = 1.0,
            QueryConvergenceMultiplier = 1.0,
            RecallBoostMultiplier = 1.0,
            FeedbackSampleCount = 0,
            ComputedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>PlanAsync 抛异常的规划器桩（验证主链不被阻断）。</summary>
    private sealed class ThrowingAdaptivePlanner : IAdaptiveRetrievalPlanner
    {
        private readonly Exception _exception;

        public ThrowingAdaptivePlanner(Exception exception) => _exception = exception;

        public Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
            => throw _exception;

        public ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
            => throw _exception;

        public ValueTask<AdaptiveRetrievalPolicy> GetPolicyAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
            => throw _exception;

        public ValueTask<AdaptiveRetrievalPolicy> GetPolicyForSignatureAsync(string workspaceId, string planSignature, CancellationToken ct = default)
            => throw _exception;

        public ValueTask<IReadOnlyList<RetrievalPlanFeedback>> ListFeedbackAsync(
            string workspaceId, string planSignature, int limit = 20, CancellationToken ct = default)
            => throw _exception;

        public ValueTask<int> ResetAsync(string? workspaceId, string? planSignature = null, CancellationToken ct = default)
            => throw _exception;
    }

    /// <summary>记录请求并返回固定决策结果的决策运行时桩。</summary>
    private sealed class FakeDecisionRuntime : IContextDecisionRuntime
    {
        private readonly ContextDecisionResult _result;

        public FakeDecisionRuntime(
            int selectedCount, int effectiveTokens, int tokenBudget, int budgetExceededCount = 0,
            double[]? selectedScores = null)
        {
            var selected = new List<ContextCandidateEnvelope>();
            for (var i = 0; i < selectedCount; i++)
            {
                selected.Add(new ContextCandidateEnvelope
                {
                    CandidateId = "cand-" + i,
                    Source = ContextCandidateSource.WorkingMemory,
                    CanonicalKey = CanonicalCandidateKey.Create("ws-adaptive", "ws-adaptive", "memory", "cand-" + i, "v1"),
                    Utility = new CandidateUtilityScore
                    {
                        DeterministicScore = selectedScores is not null && i < selectedScores.Length ? selectedScores[i] : 0.0,
                        FinalScore = selectedScores is not null && i < selectedScores.Length ? selectedScores[i] : 0.0
                    }
                });
            }

            _result = new ContextDecisionResult
            {
                SelectedEnvelopes = selected,
                Outcome = new ContextDecisionOutcomeSummary
                {
                    SelectedCount = selectedCount,
                    EffectiveTokens = effectiveTokens,
                    TokenBudget = tokenBudget,
                    BudgetExceededCount = budgetExceededCount
                }
            };
        }

        public ContextDecisionRuntimeRequest? LastRequest { get; private set; }

        public ValueTask<ContextDecisionResult> ExecuteAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(_result);
        }

        public ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return ValueTask.FromResult(R28BTestHelpers.MakeExecutionResult(_result));
        }
    }
}
