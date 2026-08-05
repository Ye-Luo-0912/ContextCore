using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Core.Services.Agent;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

// ===========================================================================
// Agent 上下文构建 fail-closed 测试
//
// 验证 AgentRunActor 在 mandatory 上下文缺失/超预算/依赖异常时不再 fail-open：
// - MandatoryContextWindowExceededException / hydration.budgetExceeded → BudgetUnsatisfiable → Failed；
// - MandatoryHydrationFailedException / mandatory 正文缺失 → SafetyBlocked → ContextSafetyBlocked（终态）；
// - Decision Runtime 通用异常 → DependencyUnavailable → RecoveryDependencyUnavailable（可重试）；
// - 仅 Ready / OptionalRetrievalDegraded 允许调用模型。
// 所有阻断路径模型调用次数必须为 0（模型绝不能在缺失 mandatory 上下文时运行）。
// ===========================================================================

[TestClass]
[TestCategory("Agent-Actor")]
public sealed class R30X_AgentContextFailClosedTests
{
    private const string Ws = "ws-ctx-failclosed";
    private const string RunId = "run-ctx-failclosed";

    /// <summary>验证：mandatory 窗口溢出异常 → BudgetUnsatisfiable → Run Failed，模型不被调用。</summary>
    [TestMethod]
    public async Task MandatoryWindowExceededException_FailsRun_ModelNotCalled()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("mandatory 超预算验证");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(FinalAnswer("已处理"));
        var runtime = new ThrowingDecisionRuntime(new MandatoryContextWindowExceededException(5000, 1000, new[] { "mandatory-1" }));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: runtime);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.Failed, stored!.State, "mandatory 窗口溢出 → Failed。");
        Assert.AreEqual(0, transport.CapturedCalls.Count, "模型绝不能在缺失 mandatory 上下文时运行。");
        Assert.IsNotNull(stored.FailureReason, "失败原因已记录。");
    }

    /// <summary>验证：hydration 后 mandatory 独占仍超预算（诊断标记）→ BudgetUnsatisfiable → Failed。</summary>
    [TestMethod]
    public async Task HydrationBudgetExceeded_FailsRun_ModelNotCalled()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("hydration 超预算验证");
        await runStore.CreateAsync(run);

        var decision = new ContextDecisionResult
        {
            RequestId = "req-budget",
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 0,
                Diagnostics = new Dictionary<string, string> { ["hydration.budgetExceeded"] = "true" }
            }
        };
        var transport = new RecordingModelTransport(FinalAnswer("已处理"));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: new RecordingDecisionRuntime(decision));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.Failed, stored!.State, "budgetExceeded → Failed。");
        Assert.AreEqual(0, transport.CapturedCalls.Count, "模型绝不能在缺失 mandatory 上下文时运行。");
    }

    /// <summary>验证：mandatory 水合异常 → SafetyBlocked → ContextSafetyBlocked（终态），模型不被调用。</summary>
    [TestMethod]
    public async Task MandatoryHydrationFailed_SafetyBlocks_ModelNotCalled()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("mandatory 水合失败验证");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(FinalAnswer("已处理"));
        var runtime = new ThrowingDecisionRuntime(new MandatoryHydrationFailedException(
            new[] { "mandatory-1" },
            new Dictionary<string, string> { ["mandatory-1"] = "hydrate timeout" }));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: runtime);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.ContextSafetyBlocked, stored!.State, "mandatory 水合失败 → ContextSafetyBlocked。");
        Assert.AreEqual(0, transport.CapturedCalls.Count, "模型绝不能在缺失 mandatory 上下文时运行。");
    }

    /// <summary>验证：选中 mandatory 候选正文缺失 → SafetyBlocked → ContextSafetyBlocked（终态），模型不被调用。</summary>
    [TestMethod]
    public async Task MandatoryContentMissing_SafetyBlocks_ModelNotCalled()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("mandatory 正文缺失验证");
        await runStore.CreateAsync(run);

        // 选中 mandatory 候选，但 WorkingSet.Materials 为空（正文未水合）→ fail-closed。
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "mandatory-1",
            Source = ContextCandidateSource.Constraint,
            CanonicalKey = CanonicalCandidateKey.Create(Ws, Ws, "constraint", "mandatory-1", "v1"),
            Safety = new CandidateSafetyState { IsMandatory = true }
        };
        var decision = new ContextDecisionResult
        {
            RequestId = "req-missing-content",
            SelectedEnvelopes = new[] { envelope },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1 }
        };
        var transport = new RecordingModelTransport(FinalAnswer("已处理"));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: new RecordingDecisionRuntime(decision));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.ContextSafetyBlocked, stored!.State, "mandatory 正文缺失 → ContextSafetyBlocked。");
        Assert.AreEqual(0, transport.CapturedCalls.Count, "模型绝不能在缺失 mandatory 上下文时运行。");
    }

    /// <summary>验证：Decision Runtime 通用异常 → DependencyUnavailable → RecoveryDependencyUnavailable（可重试），模型不被调用。</summary>
    [TestMethod]
    public async Task RuntimeGenericException_DependencyUnavailable_ModelNotCalled()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("决策依赖异常验证");
        await runStore.CreateAsync(run);

        var transport = new RecordingModelTransport(FinalAnswer("已处理"));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: new ThrowingDecisionRuntime(new InvalidOperationException("store unavailable")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        var stored = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.RecoveryDependencyUnavailable, stored!.State,
            "Decision Runtime 异常 → RecoveryDependencyUnavailable（依赖恢复后可重试）。");
        Assert.AreEqual(0, transport.CapturedCalls.Count, "模型绝不能在缺失 mandatory 上下文时运行。");
    }

    /// <summary>验证：正常决策结果（无 mandatory 违规）→ Ready → 模型被调用，Run 正常完成。</summary>
    [TestMethod]
    public async Task NormalDecisionResult_ModelCalled_Completes()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = BuildRun("正常决策验证");
        await runStore.CreateAsync(run);

        var decision = new ContextDecisionResult
        {
            RequestId = "req-ok",
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 0 }
        };
        var transport = new RecordingModelTransport(FinalAnswer("最终答案"));
        var actor = new AgentRunActor(
            runStore, eventStore, transport,
            new DefaultAgentLoopPolicy(),
            new EchoToolDispatcher(),
            decisionRuntime: new RecordingDecisionRuntime(decision));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.AreEqual(1, transport.CapturedCalls.Count, "正常决策 → 模型被调用。");
        var stored = await runStore.GetAsync(Ws, RunId);
        Assert.AreEqual(AgentRunState.Completed, stored!.State, "正常路径 Run 完成。");
    }

    // ── 测试辅助 ─────────────────────────────────────────────────────────────

    private static AgentRun BuildRun(string task) => new()
    {
        RunId = RunId,
        WorkspaceId = Ws,
        SessionId = "session-ctx-failclosed",
        Task = task,
        State = AgentRunState.Created,
        Turn = 0,
        ModelCallsUsed = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        TurnBudget = new AgentTurnBudget
        {
            MaxTurns = 10,
            TurnsUsed = 0,
            MaxModelCalls = 10
        }
    };

    private static AgentModelResponse FinalAnswer(string content) => new()
    {
        Content = content,
        ToolCalls = Array.Empty<AgentToolCallRequest>(),
        IsFinalAnswer = true,
        TokensConsumed = 10,
        Duration = TimeSpan.FromMilliseconds(1)
    };

    /// <summary>录制模型调用入参的 IAgentModelTransport stub（调用即失败断言可用）。</summary>
    private sealed class RecordingModelTransport : IAgentModelTransport
    {
        private readonly AgentModelResponse _response;
        public List<(string RunId, IReadOnlyList<AgentMessage> Messages)> CapturedCalls { get; } = new();

        public RecordingModelTransport(AgentModelResponse response)
        {
            _response = response;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("应调用结构化 messages 重载。");

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            CapturedCalls.Add((runId, messages.ToList()));
            return ValueTask.FromResult(_response);
        }

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
            => CallAsync(request.RunId, request.Messages, cancellationToken);
    }
}
