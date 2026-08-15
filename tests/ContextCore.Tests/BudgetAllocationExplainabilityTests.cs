using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 预算分配可解释性与找回精度测试。
/// 覆盖：分配裁掉的 dropped envelope 携带原因码与中文详情（V2.0 与 V2.1 路径，
/// 兑现 DroppedEnvelopes 契约「包含 BlockReasonCode」）；找回问句只包含预算裁掉
/// （可恢复）条目、不含 gate 拦截条目；投影把预算裁掉原因透出。
/// </summary>
[TestClass]
[TestCategory("LR3C")]
[TestCategory("Retrieval")]
public sealed class BudgetAllocationExplainabilityTests
{
    private const string Ws = "ws";
    private const string Col = "col";

    // ── 引擎：分配裁掉的 dropped envelope 带原因 ────────────────────────────

    /// <summary>
    /// 验证：TopK 截断的候选以 SectionQuotaExceeded 原因出现在 DroppedEnvelopes，
    /// 且仍标记为通过 safety gate（预算裁掉 ≠ gate 拦截）。
    /// </summary>
    [TestMethod]
    public async Task Engine_DroppedEnvelopes_CarryTopKReason()
    {
        var engine = BuildEngine(allocatorV2_1: null);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Lexical, raw: 100.0),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, raw: 99.0),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, raw: 97.0)
        };

        var result = await engine.DecideAsync(
            BuildRequest(candidates, topK: 2, tokenBudget: 4096, diversity: false), CancellationToken.None);

        var dropped = result.DroppedEnvelopes.Single();
        Assert.AreEqual("c3", dropped.CanonicalKey.EntityId);
        Assert.IsTrue(dropped.Safety.PassesSafetyGate, "预算裁掉的候选仍通过 safety gate。");
        Assert.AreEqual(CandidateDecisionReasonCode.SectionQuotaExceeded, dropped.Safety.BlockReasonCode,
            "TopK 截断的候选应带 SectionQuotaExceeded 原因。");
        Assert.IsFalse(string.IsNullOrEmpty(dropped.Safety.BlockReasonDetail),
            "TopK 截断应有可读详情。");
    }

    /// <summary>
    /// 验证：Token 预算超限的候选以 TokenBudgetExceeded 原因出现在 DroppedEnvelopes。
    /// </summary>
    [TestMethod]
    public async Task Engine_DroppedEnvelopes_CarryTokenBudgetReason()
    {
        var engine = BuildEngine(allocatorV2_1: null);
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Lexical, raw: 100.0, tokens: 10),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, raw: 99.0, tokens: 10),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, raw: 97.0, tokens: 10)
        };

        var result = await engine.DecideAsync(
            BuildRequest(candidates, topK: 10, tokenBudget: 5, diversity: false), CancellationToken.None);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count, "首个候选部分截断选入。");
        var dropped = result.DroppedEnvelopes.ToArray();
        Assert.AreEqual(2, dropped.Length);
        Assert.IsTrue(dropped.All(d => d.Safety.PassesSafetyGate), "预算裁掉的候选仍通过 safety gate。");
        Assert.IsTrue(dropped.All(d => d.Safety.BlockReasonCode == CandidateDecisionReasonCode.TokenBudgetExceeded),
            "Token 预算超限的候选应带 TokenBudgetExceeded 原因。");
        Assert.IsTrue(dropped.All(d => !string.IsNullOrEmpty(d.Safety.BlockReasonDetail)),
            "Token 预算超限应有可读详情。");
    }

    /// <summary>
    /// 验证：V2.1（diversity）路径分配裁掉的候选同样带原因，不因分配器差异丢失可解释性。
    /// </summary>
    [TestMethod]
    public async Task Engine_V21Path_DroppedEnvelopes_CarryReason()
    {
        var engine = BuildEngine(allocatorV2_1: new DefaultAllocatorV2_1(new DefaultGlobalAllocator()));
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Lexical, raw: 100.0, tokens: 10),
            MakeEnvelope("c2", ContextCandidateSource.Graph, raw: 99.0, tokens: 10),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, raw: 97.0, tokens: 10)
        };

        var result = await engine.DecideAsync(
            BuildRequest(candidates, topK: 10, tokenBudget: 5, diversity: true), CancellationToken.None);

        var dropped = result.DroppedEnvelopes.ToArray();
        Assert.IsTrue(dropped.Length >= 1, "V2.1 预算紧张时应裁掉至少一个候选。");
        Assert.IsTrue(dropped.All(d => d.Safety.PassesSafetyGate), "预算裁掉的候选仍通过 safety gate。");
        Assert.IsTrue(dropped.All(d => d.Safety.BlockReasonCode != CandidateDecisionReasonCode.Unknown),
            "V2.1 裁掉的候选应带具体原因码。");
        Assert.IsTrue(dropped.All(d => !string.IsNullOrEmpty(d.Safety.BlockReasonDetail)),
            "V2.1 裁掉的候选应有可读详情。");
    }

    // ── 找回问句精度：只恢复预算裁掉的条目 ──────────────────────────────────

    /// <summary>
    /// 验证：下一轮找回问句只包含预算裁掉（通过 gate）的条目实体词，
    /// gate 拦截（superseded 等）的条目不再被重新查询。
    /// </summary>
    [TestMethod]
    public async Task RecoveryGoals_SkipGateBlocked_IncludeBudgetDropped()
    {
        var runStore = new InMemoryAgentRunStore();
        var eventStore = new InMemoryAgentRunEventStore(runStore);
        var run = new AgentRun
        {
            RunId = "run-" + Guid.NewGuid().ToString("N"),
            WorkspaceId = "ws-search",
            CollectionId = "demo",
            SessionId = "session-recover-precise",
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
        var blockedKey = CanonicalCandidateKey.Create("ws-search", "demo", "note", "stale-v2", "v1");
        var execution = R28BTestHelpers.MakeExecutionResult(new ContextDecisionResult
        {
            SelectedEnvelopes = new[] { MakeEnvelope("keep-1", keepKey) },
            DroppedEnvelopes = new[]
            {
                // 预算裁掉（通过 gate、被分配器放弃）→ 应进入找回问句
                MakeEnvelope("AmberCompass-17", amberKey) with
                {
                    Safety = new CandidateSafetyState
                    {
                        PassesSafetyGate = true,
                        BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded,
                        BlockReasonDetail = "Token 预算超限：有效 token 超出剩余预算"
                    }
                },
                // gate 拦截（superseded）→ 不可恢复，不应进入找回问句
                MakeEnvelope("stale-v2", blockedKey) with
                {
                    Safety = new CandidateSafetyState
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.SupersededByCurrentVersion,
                        BlockReasonDetail = "superseded by newer version"
                    }
                }
            },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1, DroppedCount = 2 }
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
        Assert.IsTrue(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("AmberCompass-17", StringComparison.Ordinal)),
            "预算裁掉的条目实体词应进入下一轮找回问句。");
        Assert.IsFalse(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("stale-v2", StringComparison.Ordinal)),
            "gate 拦截的条目不应进入找回问句。");
        Assert.IsFalse(
            planner.Inputs[1].UnresolvedGoals.Any(goal =>
                goal.Contains("keep-1", StringComparison.Ordinal)),
            "选中的条目不是未解决目标。");
    }

    // ── 投影：预算裁掉原因透出 ──────────────────────────────────────────────

    /// <summary>
    /// 验证：投影器把预算裁掉的 dropped item 原因透出（即使候选通过 safety gate，
    /// 只要 BlockReasonCode 已填充就展示具体原因，而不是泛化的 "budget exceeded"）。
    /// </summary>
    [TestMethod]
    public void Projector_DroppedItem_ReasonSurfacesBudgetDrop()
    {
        var result = new ContextDecisionResult
        {
            RequestId = "retrieve-1",
            DecisionSource = ContextDecisionSource.Retrieval,
            SelectedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("c1", ContextCandidateSource.Lexical, raw: 10.0) with
                {
                    Safety = new CandidateSafetyState
                    {
                        PassesSafetyGate = true,
                        BlockReasonCode = CandidateDecisionReasonCode.SectionQuotaExceeded,
                        BlockReasonDetail = "TopK 截断：候选数超过 TopK 上限"
                    }
                }
            },
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 0, DroppedCount = 1 }
        };

        var dto = new RetrievalResultProjector().Project(result);

        Assert.AreEqual(1, dto.DroppedItems.Count);
        StringAssert.Contains(dto.DroppedItems[0].Reason, "SectionQuotaExceeded",
            "预算裁掉原因应透出，而不是泛化的 budget exceeded。");
        StringAssert.Contains(dto.DroppedItems[0].Reason, "TopK 截断",
            "预算裁掉详情应透出。");
    }

    // ── 构造与桩 ───────────────────────────────────────────────────────────

    private static DefaultContextDecisionEngine BuildEngine(IAllocatorV2_1? allocatorV2_1)
        => new(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator(),
            allocatorV2_1: allocatorV2_1,
            performanceMonitor: null,
            componentHealthRegistry: null,
            reranker: null);

    private static ContextDecisionRequest BuildRequest(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int topK,
        int tokenBudget,
        bool diversity)
    {
        var snapshot = BuildSnapshot();
        return new ContextDecisionRequest
        {
            RequestId = "req-budget",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = Ws,
            CollectionId = Col,
            Candidates = candidates,
            TokenBudget = tokenBudget,
            TopK = topK,
            PolicySnapshot = snapshot,
            DiversityOptions = diversity ? new DiversityOptions() : null,
            AllocationContext = new AllocationContext
            {
                Purpose = ContextDecisionPurpose.Retrieval,
                Budget = snapshot.Budget,
                MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
            }
        };
    }

    private static EffectivePolicySnapshot BuildSnapshot()
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        return new EffectivePolicySnapshot
        {
            Reference = new ResolvedPolicyReference
            {
                BundleId = bundle.BundleId,
                BundleVersion = bundle.Version,
                BundleContentHash = DefaultResolvedPolicyProvider.DefaultContentHash,
                ActivationEpoch = DefaultResolvedPolicyProvider.DefaultActivationEpoch
            },
            Safety = bundle.Safety,
            Budget = bundle.Budget,
            Routing = bundle.Routing,
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope(Ws, Col)
        };
    }

    private static ContextCandidateEnvelope MakeEnvelope(string id, ContextCandidateSource source, double raw, int tokens = 0)
        => new()
        {
            CandidateId = $"{source}:{id}",
            Source = source,
            Type = "note",
            WorkspaceId = Ws,
            CollectionId = Col,
            CanonicalKey = CanonicalCandidateKey.Create(Ws, Col, "context", id, "1"),
            Features = new CandidateFeatureVector(),
            TokenCost = tokens > 0
                ? new CandidateTokenCost
                {
                    ContentTokens = tokens,
                    TokenizerId = "test",
                    IsEstimated = false
                }
                : null,
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = raw,
                FinalScore = raw,
                ReasonCode = "deterministic-only"
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
