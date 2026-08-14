using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Agent-Run-Full-Loop")]
public sealed class AgentTurnSearchQueryTests
{
    [TestMethod]
    public void Compose_NoNewTerms_KeepsBaseQuery()
    {
        Assert.AreEqual("summarize notes", AgentTurnSearchQuery.Compose("summarize notes", null));
        Assert.AreEqual("summarize notes", AgentTurnSearchQuery.Compose(
            "summarize notes",
            Array.Empty<ToolObservation>()));
        Assert.AreEqual("summarize notes", AgentTurnSearchQuery.Compose(
            "summarize notes",
            new[]
            {
                new ToolObservation { ToolName = "echo", Succeeded = true, Result = "summarize notes" }
            }));
        Assert.AreEqual(
            "task extra-intent",
            AgentTurnSearchQuery.MergeQueries(
                new[]
                {
                    new AgentRetrievalQuery { Text = "task", Type = AgentRetrievalQueryType.Hybrid },
                    new AgentRetrievalQuery { Text = "extra-intent", Type = AgentRetrievalQueryType.Keyword }
                },
                "fallback"));
        Assert.AreEqual("fallback", AgentTurnSearchQuery.MergeQueries(Array.Empty<AgentRetrievalQuery>(), "fallback"));
        CollectionAssert.AreEqual(
            new[] { "task", "AmberCompass-17" },
            AgentTurnSearchQuery.CollectQueries(
                new[] { new AgentRetrievalQuery { Text = "task", Type = AgentRetrievalQueryType.Hybrid } },
                "fallback",
                new[]
                {
                    new ToolObservation { ToolName = "echo", Succeeded = true, Result = "AmberCompass-17 found" }
                }).ToList());
    }

    [TestMethod]
    public void Compose_AppendsSuccessfulEntityTerms_SkipsFailures()
    {
        var observations = new[]
        {
            new ToolObservation { ToolName = "echo", Succeeded = true, Result = "stale-id-1" },
            new ToolObservation { ToolName = "echo", Succeeded = false, Result = "AmberCompass-17", Error = "id:missing" },
            new ToolObservation { ToolName = "echo", Succeeded = true, Result = "  AmberCompass-17   found  " }
        };

        var query = AgentTurnSearchQuery.Compose("summarize project notes", observations);
        StringAssert.Contains(query, "summarize project notes");
        StringAssert.Contains(query, "stale-id-1");
        StringAssert.Contains(query, "AmberCompass-17");
        Assert.IsFalse(query.Contains("found", StringComparison.Ordinal));
        Assert.IsFalse(query.Contains("id:missing", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToolEvidence_UsesObservationSuccessRate()
    {
        Assert.IsFalse(AgentTurnSearchQuery.ToolEvidence(null).Effective);
        Assert.AreEqual(0.0, AgentTurnSearchQuery.ToolEvidence(Array.Empty<ToolObservation>()).Quality);
        var mixed = AgentTurnSearchQuery.ToolEvidence(new[]
        {
            new ToolObservation { ToolName = "echo", Succeeded = true, Result = "ok" },
            new ToolObservation { ToolName = "echo", Succeeded = false, Error = "missing" }
        });
        Assert.IsTrue(mixed.Effective);
        Assert.AreEqual(0.5, mixed.Quality, 0.0001);
    }

    [TestMethod]
    public void ToolEvidence_UsesRecentWindow_NotAncientFailures()
    {
        var observations = new List<ToolObservation>();
        for (var i = 0; i < 8; i++)
        {
            observations.Add(new ToolObservation { ToolName = "echo", Succeeded = false, Error = $"miss-{i}" });
        }
        for (var i = 0; i < 8; i++)
        {
            observations.Add(new ToolObservation { ToolName = "echo", Succeeded = true, Result = $"ok-{i}" });
        }

        var evidence = AgentTurnSearchQuery.ToolEvidence(observations);
        Assert.IsTrue(evidence.Effective);
        Assert.AreEqual(1.0, evidence.Quality, 0.0001,
            "质量只看最近窗口的成功率，古代失败不打没。");
    }

    [TestMethod]
    public void Compose_TruncatesLongSnippet()
    {
        var longBody = new string('a', AgentTurnSearchQuery.MaxObservationSnippetChars + 40);
        var query = AgentTurnSearchQuery.Compose("task", new[]
        {
            new ToolObservation { ToolName = "echo", Succeeded = true, Result = longBody }
        });
        Assert.IsTrue(query.Length <= "task ".Length + AgentTurnSearchQuery.MaxObservationSnippetChars);
        Assert.IsTrue(query.StartsWith("task ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DiagnosticsFrom_LastDecision_MapsHitsAndBudget()
    {
        Assert.AreEqual(0, AgentTurnSearchQuery.DiagnosticsFrom(null).Count);

        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var last = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[]
            {
                new ContextCandidateEnvelope
                {
                    CandidateId = "keep-1",
                    Source = ContextCandidateSource.Lexical,
                    CanonicalKey = keepKey,
                    Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8 }
                }
            },
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 1,
                EffectiveTokens = 900,
                TokenBudget = 800,
                BudgetExceededCount = 2
            }
        }) with
        {
            NormalizedRequest = new ContextDecisionRuntimeRequest
            {
                RequestId = "req-1",
                Scope = new ContextDecisionScope("ws", "col"),
                Purpose = ContextDecisionPurpose.AgentContext,
                QueryText = "summarize notes"
            }
        };

        var diagnostics = AgentTurnSearchQuery.DiagnosticsFrom(last);
        Assert.AreEqual(1, diagnostics.Count);
        Assert.AreEqual("summarize notes", diagnostics[0].QueryText);
        Assert.AreEqual(1, diagnostics[0].HitsReturned);
        Assert.AreEqual(0.8, diagnostics[0].HighestScore);
        Assert.IsTrue(diagnostics[0].BudgetExceeded);
    }

    [TestMethod]
    public async Task SecondTurn_QueryTextIncludesNewToolTerms_PlannerSeesObservations()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-search",
            CollectionId = "demo",
            SessionId = "session-search",
            Task = "summarize project notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 5, TurnsUsed = 0, MaxModelCalls = 5 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-search", "demo", "note", "keep-1", "v1");
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[]
            {
                new ContextCandidateEnvelope
                {
                    CandidateId = "keep-1",
                    Source = ContextCandidateSource.Lexical,
                    CanonicalKey = keepKey,
                    Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8 }
                }
            },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1 }
        });

        var runtime = new RecordingDecisionRuntime(execution);
        var planner = new RecordingAdaptivePlanner(new AgentRetrievalPlan
        {
            ControlledQueries = new[]
            {
                new AgentRetrievalQuery { Text = run.Task, Type = AgentRetrievalQueryType.Hybrid }
            }
        });
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["summarize"] = "echo"
        };
        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(), new FixedResultDispatcher("AmberCompass-17 found in notes"),
            decisionRuntime: runtime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsTrue(runtime.Requests.Count >= 2, "工具轮之后应再构建一次上下文。");
        Assert.AreEqual(run.Task, runtime.Requests[0].QueryText, "首轮没有工具观察，问句就是任务。");
        StringAssert.Contains(runtime.Requests[1].QueryText, run.Task);
        StringAssert.Contains(runtime.Requests[1].QueryText, "AmberCompass-17");
        CollectionAssert.Contains(
            runtime.Requests[1].RetrievalInput!.QueryTexts.ToList(),
            "summarize project notes");
        CollectionAssert.Contains(
            runtime.Requests[1].RetrievalInput!.QueryTexts.ToList(),
            "AmberCompass-17");
        Assert.IsFalse(
            runtime.Requests[1].RetrievalInput!.QueryTexts.Any(text =>
                text.Contains("found", StringComparison.OrdinalIgnoreCase)
                || text.Contains("notes", StringComparison.OrdinalIgnoreCase) && text != "summarize project notes"),
            "观察查询不能把 found/notes 整段拿去搜。");
        Assert.IsFalse(
            (runtime.Requests[1].QueryText ?? string.Empty).Contains("found", StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(planner.Inputs.Count >= 2);
        Assert.AreEqual(0, planner.Inputs[0].ToolObservations.Count);
        Assert.IsTrue(planner.Inputs[1].ToolObservations.Any(item =>
            item.Succeeded && item.Result != null && item.Result.Contains("AmberCompass-17", StringComparison.Ordinal)));
        Assert.IsNotNull(planner.Inputs[1].TurnBudget);
        Assert.AreEqual(5, planner.Inputs[1].TurnBudget!.MaxTurns);
        Assert.AreEqual(1, planner.Inputs[1].PreviousRetrievalDiagnostics.Count);

        var secondRuntime = planner.Outcomes.Where(item => item.Source == RetrievalFeedbackSource.Runtime).Skip(1).FirstOrDefault();
        Assert.IsNotNull(secondRuntime, "第二轮检索应记录反馈。");
        Assert.IsTrue(secondRuntime!.Effective, "已有工具观察才是有效质量信号。");
        Assert.AreEqual(1.0, secondRuntime.OutcomeQuality, 0.0001, "工具全部成功时质量为 1。");
        Assert.IsFalse(planner.Outcomes[0].Effective, "首轮还没有工具观察，不能用打分器分数当准。");
    }

    [TestMethod]
    public async Task SecondTurn_UnresolvedGoals_FromDroppedEnvelopes()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-search",
            CollectionId = "demo",
            SessionId = "session-recover",
            Task = "summarize project notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 5, TurnsUsed = 0, MaxModelCalls = 5 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-search", "demo", "note", "keep-1", "v1");
        var amberKey = CanonicalCandidateKey.Create("ws-search", "demo", "note", "AmberCompass-17", "v1");
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { MakeEnvelope("keep-1", keepKey) },
            DroppedEnvelopes = new[] { MakeEnvelope("AmberCompass-17", amberKey) },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 1 }
        });

        var runtime = new RecordingDecisionRuntime(execution);
        var planner = new RecordingAdaptivePlanner(new AgentRetrievalPlan
        {
            ControlledQueries = new[]
            {
                new AgentRetrievalQuery { Text = run.Task, Type = AgentRetrievalQueryType.Hybrid }
            }
        });
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["summarize"] = "echo"
        };
        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(), new FixedResultDispatcher("nothing found"),
            decisionRuntime: runtime, adaptivePlanner: planner);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsTrue(planner.Inputs.Count >= 2, "工具轮之后应再次构建上下文。");
        Assert.AreEqual(0, planner.Inputs[0].UnresolvedGoals.Count, "首轮没有上一轮决策，无找回问句。");
        Assert.IsTrue(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("AmberCompass-17", StringComparison.Ordinal)),
            "上一轮被分配器裁掉的条目实体词应进入下一轮找回问句。");
        Assert.IsFalse(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("keep-1", StringComparison.Ordinal)),
            "选中的条目不是未解决目标。");
    }

    [TestMethod]
    public async Task SecondTurn_UnresolvedGoals_IncludeProjectionSkipped()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-search",
            CollectionId = "demo",
            SessionId = "session-proj-skip",
            Task = "summarize project notes",
            State = AgentRunState.Created,
            Turn = 0,
            ModelCallsUsed = 0,
            ModelContextTokenBudget = 40,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            TurnBudget = new AgentTurnBudget { MaxTurns = 5, TurnsUsed = 0, MaxModelCalls = 5 }
        };
        await runStore.CreateAsync(run);

        var keepKey = CanonicalCandidateKey.Create("ws-search", "demo", "note", "keep-1", "v1");
        var amberKey = CanonicalCandidateKey.Create("ws-search", "demo", "note", "AmberCompass-17", "v1");
        var keep = MakeEnvelope("keep-1", keepKey);
        var amber = MakeEnvelope("AmberCompass-17", amberKey);
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { keep, amber },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 2 }
        }) with
        {
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep, amber },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "short body", NativeKind = "note" },
                    [amberKey] = new CandidateMaterial { Key = amberKey, Content = new string('x', 300), NativeKind = "note" }
                }
            }
        };

        var runtime = new RecordingDecisionRuntime(execution);
        var planner = new RecordingAdaptivePlanner(new AgentRetrievalPlan
        {
            ControlledQueries = new[]
            {
                new AgentRetrievalQuery { Text = run.Task, Type = AgentRetrievalQueryType.Hybrid }
            }
        });
        var echoTriggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["summarize"] = "echo"
        };
        var actor = new AgentRunActor(
            runStore, eventStore, new DeterministicAgentModelTransport(echoTriggers),
            new DefaultAgentLoopPolicy(), new FixedResultDispatcher("nothing found"),
            decisionRuntime: runtime, adaptivePlanner: planner,
            modelContextProjector: new DefaultAgentModelContextProjector());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await actor.ExecuteAsync(run, cts.Token);

        Assert.IsTrue(planner.Inputs.Count >= 2, "工具轮之后应再次构建上下文。");
        Assert.IsTrue(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("AmberCompass-17", StringComparison.Ordinal)),
            "投影因预算跳过的材料应进入下一轮找回问句。");
        Assert.IsFalse(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("keep-1", StringComparison.Ordinal)),
            "投影能放下的材料不是找回目标。");
    }

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

    private sealed class RecordingAdaptivePlanner : IAdaptiveRetrievalPlanner
    {
        private readonly AgentRetrievalPlan _plan;

        public RecordingAdaptivePlanner(AgentRetrievalPlan plan) => _plan = plan;

        public List<AgentRetrievalPlannerInput> Inputs { get; } = new();

        public List<RetrievalPlanFeedback> Outcomes { get; } = new();

        public Task<AgentRetrievalPlan> PlanAsync(AgentRetrievalPlannerInput input, CancellationToken ct = default)
        {
            Inputs.Add(input);
            return Task.FromResult(_plan);
        }

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

    private sealed class FixedResultDispatcher : IToolDispatcher
    {
        private readonly string _result;

        public FixedResultDispatcher(string result) => _result = result;

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
                Succeeded = true,
                Result = _result,
                Duration = TimeSpan.Zero,
                SideEffect = ToolSideEffect.None
            });
        }
    }
}
