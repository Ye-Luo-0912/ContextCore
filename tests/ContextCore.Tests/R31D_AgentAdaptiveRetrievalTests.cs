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

        // 2. 决策执行后记录检索结果反馈（闭环学习信号）。
        Assert.AreEqual(1, planner.OutcomeRecords.Count, "每次上下文构建应记录一条反馈。");
        var record = planner.OutcomeRecords[0];
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

        var record = planner.OutcomeRecords.Single();
        Assert.IsTrue(record.BudgetExceeded, "决策结果含预算拦截时应上报预算超限信号。");
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

        public FakeDecisionRuntime(int selectedCount, int effectiveTokens, int tokenBudget, int budgetExceededCount = 0)
        {
            var selected = new List<ContextCandidateEnvelope>();
            for (var i = 0; i < selectedCount; i++)
            {
                selected.Add(new ContextCandidateEnvelope
                {
                    CandidateId = "cand-" + i,
                    Source = ContextCandidateSource.WorkingMemory,
                    CanonicalKey = CanonicalCandidateKey.Create("ws-adaptive", "ws-adaptive", "memory", "cand-" + i, "v1")
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
