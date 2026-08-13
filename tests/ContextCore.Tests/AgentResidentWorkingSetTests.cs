using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Agent-Run-Full-Loop")]
public sealed class AgentResidentWorkingSetTests
{
    [TestMethod]
    public void FromLastDecision_NullOrEmpty_ReturnsNull()
    {
        Assert.IsNull(AgentResidentWorkingSet.FromLastDecision(null));

        var empty = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 0 }
        });
        Assert.IsNull(AgentResidentWorkingSet.FromLastDecision(empty));
    }

    [TestMethod]
    public void FromLastDecision_KeepsSelected_DropsUnselected()
    {
        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var dropKey = CanonicalCandidateKey.Create("ws", "col", "note", "drop-1", "v1");
        var keep = MakeEnvelope("keep-1", keepKey);
        var drop = MakeEnvelope("drop-1", dropKey);
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { keep },
            DroppedEnvelopes = new[] { drop },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
        }) with
        {
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep, drop },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "resident body", NativeKind = "note" },
                    [dropKey] = new CandidateMaterial { Key = dropKey, Content = "forgotten body", NativeKind = "note" }
                }
            }
        };

        var seed = AgentResidentWorkingSet.FromLastDecision(execution);
        Assert.IsNotNull(seed);
        Assert.AreEqual(1, seed!.Envelopes.Count);
        Assert.AreEqual("keep-1", seed.Envelopes[0].CandidateId);
        Assert.AreEqual(1, seed.Materials.Count);
        Assert.AreEqual("resident body", seed.Materials[keepKey].Content);
        Assert.IsFalse(seed.Materials.ContainsKey(dropKey));
    }

    [TestMethod]
    public async Task SecondTurn_SeedsSelected_StillSearches_DoesNotPinRequiredIds()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-resident",
            CollectionId = "demo",
            SessionId = "session-resident",
            Task = "search the resident notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 5, TurnsUsed = 0, MaxModelCalls = 5 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-resident", "demo", "note", "keep-1", "v1");
        var dropKey = CanonicalCandidateKey.Create("ws-resident", "demo", "note", "drop-1", "v1");
        var keep = MakeEnvelope("keep-1", keepKey);
        var drop = MakeEnvelope("drop-1", dropKey);
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { keep },
            DroppedEnvelopes = new[] { drop },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
        }) with
        {
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep, drop },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "resident body", NativeKind = "note" },
                    [dropKey] = new CandidateMaterial { Key = dropKey, Content = "forgotten body", NativeKind = "note" }
                }
            }
        };

        var runtime = new RecordingDecisionRuntime(execution);
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };
        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: runtime);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsTrue(runtime.Requests.Count >= 2, "工具轮之后应再构建一次上下文。");
        Assert.IsNull(runtime.Requests[0].SeedWorkingSet, "首轮没有 Resident 种子。");

        var second = runtime.Requests[1];
        Assert.IsNotNull(second.SeedWorkingSet, "第二轮应带上上一轮选中项。");
        Assert.AreEqual(1, second.SeedWorkingSet!.Envelopes.Count);
        Assert.AreEqual("keep-1", second.SeedWorkingSet.Envelopes[0].CandidateId);
        Assert.AreEqual("resident body", second.SeedWorkingSet.Materials[keepKey].Content);
        Assert.IsFalse(second.SeedWorkingSet.Materials.ContainsKey(dropKey), "未选中项不得进入种子。");
        Assert.AreEqual(run.Task, runtime.Requests[0].QueryText, "首轮按任务搜索。");
        StringAssert.Contains(second.QueryText, run.Task, "第二轮仍包含原任务。");
        var required = second.AgentInput?.RequiredIds ?? Array.Empty<string>();
        CollectionAssert.DoesNotContain(required.ToList(), "keep-1", "选中 ID 不得钉死为 RequiredIds。");

        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(finalRun.ResidentWorkingSetJson), "Turn 提交后 Resident 应写进 Run。");
        StringAssert.Contains(finalRun.ResidentWorkingSetJson, "keep-1");
        StringAssert.Contains(finalRun.ResidentWorkingSetJson, "resident body");
        Assert.IsFalse(finalRun.ResidentWorkingSetJson.Contains("drop-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WithoutIds_DropsMatchingEntity_LeavesOthers()
    {
        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var dropKey = CanonicalCandidateKey.Create("ws", "col", "note", "drop-1", "v1");
        var keep = MakeEnvelope("Lexical:keep-1", keepKey);
        var drop = MakeEnvelope("Lexical:drop-1", dropKey);
        var seed = new CandidateWorkingSet
        {
            Envelopes = new[] { keep, drop },
            Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
            {
                [keepKey] = new CandidateMaterial { Key = keepKey, Content = "keep", NativeKind = "note" },
                [dropKey] = new CandidateMaterial { Key = dropKey, Content = "drop", NativeKind = "note" }
            }
        };

        Assert.AreSame(seed, AgentResidentWorkingSet.WithoutIds(seed, Array.Empty<string>()));
        Assert.IsNull(AgentResidentWorkingSet.WithoutIds(seed, new[] { "keep-1", "drop-1" }));

        var filtered = AgentResidentWorkingSet.WithoutIds(seed, new[] { "drop-1" });
        Assert.IsNotNull(filtered);
        Assert.AreEqual(1, filtered!.Envelopes.Count);
        Assert.AreEqual("Lexical:keep-1", filtered.Envelopes[0].CandidateId);
        Assert.IsTrue(filtered.Materials.ContainsKey(keepKey));
        Assert.IsFalse(filtered.Materials.ContainsKey(dropKey));
    }

    [TestMethod]
    public async Task SecondTurn_FailedTool_ExcludesIdFromSearchAndSeed()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-exclude",
            CollectionId = "demo",
            SessionId = "session-exclude",
            Task = "search the resident notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 5, TurnsUsed = 0, MaxModelCalls = 5 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-exclude", "demo", "note", "keep-1", "v1");
        var keep = MakeEnvelope("keep-1", keepKey);
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { keep },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1 }
        }) with
        {
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "resident body", NativeKind = "note" }
                }
            }
        };

        var runtime = new RecordingDecisionRuntime(execution);
        var planner = new DelegatingAdaptivePlanner();
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };
        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(),
            new FailingDispatcher("未找到 id:keep-1"),
            decisionRuntime: runtime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsTrue(runtime.Requests.Count >= 2, "失败工具之后应再构建一次上下文。");
        Assert.AreEqual(0, runtime.Requests[0].RetrievalInput?.ExcludedIds.Count ?? 0);
        CollectionAssert.Contains(
            runtime.Requests[1].RetrievalInput!.ExcludedIds.ToList(),
            "keep-1",
            "失败观察里的 ID 应写入排除列表。");
        Assert.IsNull(
            runtime.Requests[1].SeedWorkingSet,
            "排除 ID 对应的 Resident 种子应被拿掉。");
    }

    [TestMethod]
    public void Serialize_Roundtrip_DropsUnselected_TryParseBadJson()
    {
        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var dropKey = CanonicalCandidateKey.Create("ws", "col", "note", "drop-1", "v1");
        var keep = MakeEnvelope("keep-1", keepKey);
        var drop = MakeEnvelope("drop-1", dropKey);
        var execution = MakeKeepDropExecution(keep, drop, keepKey, dropKey);

        var json = AgentResidentWorkingSet.Serialize(AgentResidentWorkingSet.FromLastDecision(execution));
        Assert.IsFalse(string.IsNullOrWhiteSpace(json));
        var parsed = AgentResidentWorkingSet.TryParse(json);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("keep-1", parsed!.Envelopes[0].CandidateId);
        Assert.AreEqual("resident body", parsed.Materials[keepKey].Content);
        Assert.IsFalse(parsed.Materials.ContainsKey(dropKey));

        Assert.IsNull(AgentResidentWorkingSet.TryParse(null));
        Assert.IsNull(AgentResidentWorkingSet.TryParse("{not-json"));
        Assert.IsNotNull(AgentResidentWorkingSet.ResolveSeed(null, json));
        Assert.IsNull(AgentResidentWorkingSet.ResolveSeed(null, null));
    }

    [TestMethod]
    public async Task ResumeAfterCrash_UsesPersistedResidentSeed()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-resident",
            CollectionId = "demo",
            SessionId = "session-resident-resume",
            Task = "search the resident notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 8, TurnsUsed = 0, MaxModelCalls = 8 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-resident", "demo", "note", "keep-1", "v1");
        var dropKey = CanonicalCandidateKey.Create("ws-resident", "demo", "note", "drop-1", "v1");
        var execution = MakeKeepDropExecution(
            MakeEnvelope("keep-1", keepKey),
            MakeEnvelope("drop-1", dropKey),
            keepKey,
            dropKey);

        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };
        // 超时仅作防挂死兜底，取消时机由 CancelAfterTransport 在指定调用后触发，
        // 放宽到分钟级避免全量负载下线程被抢占导致兜底提前触发。
        using var phase1Cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var phase1Transport = new CancelAfterTransport(
            new DeterministicAgentModelTransport(echoTriggers),
            cancelAfterCall: 2,
            phase1Cts);
        var phase1Runtime = new RecordingDecisionRuntime(execution);
        var actor1 = new AgentRunActor(
            runStore, eventStore, phase1Transport,
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: phase1Runtime);
        await actor1.ExecuteAsync(run, phase1Cts.Token);

        var crashed = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(crashed);
        Assert.IsFalse(AgentRunStateMachine.IsTerminalState(crashed!.State),
            $"崩溃后应停留在非终态，实际 {crashed.State}。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(crashed.ResidentWorkingSetJson),
            "第一轮 flush 后 Resident 应已在 Run 上。");

        var phase2Runtime = new RecordingDecisionRuntime(execution);
        using var phase2Cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var actor2 = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: phase2Runtime);
        await actor2.ExecuteAsync(crashed, phase2Cts.Token);

        Assert.IsTrue(phase2Runtime.Requests.Count >= 1, "恢复后应再次构建上下文。");
        var resumedSeed = phase2Runtime.Requests[0].SeedWorkingSet;
        Assert.IsNotNull(resumedSeed, "新 Actor 应从 Run 上的 Resident 恢复种子。");
        Assert.AreEqual("keep-1", resumedSeed!.Envelopes[0].CandidateId);
        Assert.AreEqual("resident body", resumedSeed.Materials[keepKey].Content);

        var finalRun = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.AreEqual(AgentRunState.Completed, finalRun!.State);
    }

    [TestMethod]
    public async Task FirstModelCallCancelled_ResidentPersistedBeforeCall_NewActorRecoversSeed()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-resident",
            CollectionId = "demo",
            SessionId = "session-resident-first-cancel",
            Task = "search the resident notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 8, TurnsUsed = 0, MaxModelCalls = 8 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-resident", "demo", "note", "keep-1", "v1");
        var dropKey = CanonicalCandidateKey.Create("ws-resident", "demo", "note", "drop-1", "v1");
        var execution = MakeKeepDropExecution(
            MakeEnvelope("keep-1", keepKey),
            MakeEnvelope("drop-1", dropKey),
            keepKey,
            dropKey);

        // 第一次模型调用就取消：上下文已构建、Turn 未正常结束，模拟模型返回前崩溃。
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = "echo"
        };
        // 超时仅作防挂死兜底，取消时机由 CancelAfterTransport 在指定调用后触发，
        // 放宽到分钟级避免全量负载下线程被抢占导致兜底提前触发。
        using var phase1Cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var phase1Transport = new CancelAfterTransport(
            new DeterministicAgentModelTransport(echoTriggers),
            cancelAfterCall: 1,
            phase1Cts);
        var phase1Runtime = new RecordingDecisionRuntime(execution);
        var actor1 = new AgentRunActor(
            runStore, eventStore, phase1Transport,
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: phase1Runtime);
        await actor1.ExecuteAsync(run, phase1Cts.Token);

        var crashed = await runStore.GetAsync(run.WorkspaceId, run.RunId);
        Assert.IsNotNull(crashed);
        Assert.IsFalse(AgentRunStateMachine.IsTerminalState(crashed!.State),
            $"取消后应停留在非终态，实际 {crashed.State}。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(crashed.ResidentWorkingSetJson),
            $"模型第一次调用前 Resident 应已随 Run 快照落库（state={crashed.State}）。");
        StringAssert.Contains(crashed.ResidentWorkingSetJson, "keep-1");
        StringAssert.Contains(crashed.ResidentWorkingSetJson, "resident body");

        // 新 Actor 从 store 上的 Run 恢复：第一轮决策请求的种子应含上一轮选中项。
        var phase2Runtime = new RecordingDecisionRuntime(execution);
        using var phase2Cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var actor2 = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(), new EchoToolDispatcher(),
            decisionRuntime: phase2Runtime);
        await actor2.ExecuteAsync(crashed, phase2Cts.Token);

        Assert.IsTrue(phase2Runtime.Requests.Count >= 1, "恢复后应再次构建上下文。");
        var resumedSeed = phase2Runtime.Requests[0].SeedWorkingSet;
        Assert.IsNotNull(resumedSeed, "新 Actor 应从 Run 上的 Resident 恢复种子。");
        Assert.AreEqual("keep-1", resumedSeed!.Envelopes[0].CandidateId);
        Assert.AreEqual("resident body", resumedSeed.Materials[keepKey].Content);
    }

    private static ContextDecisionExecutionResult MakeKeepDropExecution(
        ContextCandidateEnvelope keep,
        ContextCandidateEnvelope drop,
        CanonicalCandidateKey keepKey,
        CanonicalCandidateKey dropKey)
        => R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { keep },
            DroppedEnvelopes = new[] { drop },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
        }) with
        {
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep, drop },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "resident body", NativeKind = "note" },
                    [dropKey] = new CandidateMaterial { Key = dropKey, Content = "forgotten body", NativeKind = "note" }
                }
            }
        };

    private static ContextCandidateEnvelope MakeEnvelope(string id, CanonicalCandidateKey key)
        => new()
        {
            CandidateId = id,
            Source = ContextCandidateSource.Lexical,
            CanonicalKey = key,
            Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8 }
        };

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

    private sealed class CancelAfterTransport : IAgentModelTransport
    {
        private readonly IAgentModelTransport _inner;
        private readonly CancellationTokenSource _cts;
        private readonly int _cancelAfterCall;
        private int _callCount;

        public CancelAfterTransport(IAgentModelTransport inner, int cancelAfterCall, CancellationTokenSource cts)
        {
            _inner = inner;
            _cancelAfterCall = cancelAfterCall;
            _cts = cts;
        }

        public ValueTask<AgentModelResponse> CallAsync(string runId, string context, CancellationToken cancellationToken = default)
            => _inner.CallAsync(runId, context, cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(string runId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
            => _inner.CallAsync(runId, messages, cancellationToken);

        public ValueTask<AgentModelResponse> CallAsync(AgentModelRequest request, CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _callCount);
            var result = _inner.CallAsync(request, cancellationToken);
            if (n >= _cancelAfterCall)
            {
                _cts.Cancel();
            }
            return result;
        }
    }

    private sealed class DelegatingAdaptivePlanner : IAdaptiveRetrievalPlanner
    {
        private readonly DefaultAgentRetrievalQueryPlanner _inner = new();

        public Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
            => Task.FromResult(_inner.Plan(input, ct));

        public ValueTask RecordOutcomeAsync(RetrievalPlanFeedback feedback, CancellationToken ct = default)
            => ValueTask.CompletedTask;

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

    private sealed class FailingDispatcher : IToolDispatcher
    {
        private readonly string _error;

        public FailingDispatcher(string error) => _error = error;

        public IReadOnlySet<string> SupportedTools { get; } =
            new HashSet<string>(StringComparer.Ordinal) { "echo" };

        public ToolDescriptor? GetDescriptor(string toolName) => null;

        public ValueTask<ToolDispatchResult> DispatchAsync(
            ToolDispatchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ToolDispatchResult
            {
                Succeeded = false,
                Error = _error,
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
        }
    }
}
