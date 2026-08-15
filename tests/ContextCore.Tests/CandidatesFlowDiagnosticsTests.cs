using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine.FlowDiagnostics;

namespace ContextCore.Tests;

/// <summary>
/// 候选流诊断契约测试。
/// <para>
/// 验证目标：
/// 1. 结局分类：选中 / gate 丢弃 / 排序过低 / 预算裁掉 / 排除矛盾 / 未召回 / 未生成
/// 2. 通道归因：哪个通道产生唯一有效命中、跨通道重复与分数范围
/// 3. 语义检查：excluded / required / forbidden / held 语义破坏
/// 4. hydration 成本与通道摘要
/// 5. 确定性：同一输入重复构建逐位一致
/// 6. 无正文泄露：报告类型不含 Content 字段
/// 7. 装饰器：关闭透传、采样写文件、失败静默、请求级 RequiredIds 归因
/// </summary>
[TestClass]
[TestCategory("LR1B")]
public sealed class CandidatesFlowDiagnosticsTests
{
    private const double Tolerance = 1e-9;

    // =========================================================================
    // 1. 结局分类
    // =========================================================================

    [TestMethod]
    public void SelectedEvidence_OutcomeAndChannelAttribution()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var evidence = report.RequiredEvidence.Single(e => e.EvidenceId == "req-a");
        Assert.AreEqual(CandidateFlowOutcome.Selected, evidence.Outcome);
        CollectionAssert.AreEquivalent(new[] { "Lexical", "Semantic" }, evidence.Channels.ToArray(),
            "req-a 由 Lexical 与 Semantic 双通道产出并选中。");

        var candidate = report.Candidates.Single(c => c.CandidateId == "req-a");
        Assert.AreEqual(95.0, candidate.FinalScore!.Value, Tolerance);
        Assert.IsNull(candidate.ReasonCode);
    }

    [TestMethod]
    public void GateDropped_ClassifiedByReason()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var lifecycle = report.RequiredEvidence.Single(e => e.EvidenceId == "req-gate");
        Assert.AreEqual(CandidateFlowOutcome.GateDropped, lifecycle.Outcome);
        Assert.AreEqual(nameof(CandidateDecisionReasonCode.LifecycleBlocked), lifecycle.ReasonCode);

        var duplicate = report.Candidates.Single(c => c.CandidateId == "dup");
        Assert.AreEqual(CandidateFlowOutcome.GateDropped, duplicate.Outcome);
        Assert.AreEqual(nameof(CandidateDecisionReasonCode.DuplicateSuppressed), duplicate.ReasonCode);
    }

    [TestMethod]
    public void RankedTooLow_And_BudgetCut()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var ranked = report.RequiredEvidence.Single(e => e.EvidenceId == "req-b");
        Assert.AreEqual(CandidateFlowOutcome.RankedTooLow, ranked.Outcome);
        Assert.AreEqual(nameof(CandidateDecisionReasonCode.ScoreBelowThreshold), ranked.ReasonCode);

        var budget = report.RequiredEvidence.Single(e => e.EvidenceId == "req-budget");
        Assert.AreEqual(CandidateFlowOutcome.BudgetCut, budget.Outcome);
        Assert.AreEqual(nameof(CandidateDecisionReasonCode.TokenBudgetExceeded), budget.ReasonCode);
    }

    [TestMethod]
    public void ExcludedRequiredEvidence_ExcludedContradiction_AndViolation()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var evidence = report.RequiredEvidence.Single(e => e.EvidenceId == "excluded-cand");
        Assert.AreEqual(CandidateFlowOutcome.ExcludedContradiction, evidence.Outcome, "必需证据同时被排除是矛盾。");

        var violation = report.Violations.Single(v => v.Kind == "excluded-in-candidates");
        Assert.AreEqual("excluded-cand", violation.EvidenceId);
    }

    [TestMethod]
    public void NotRecalled_WhenNoProviderFailed()
    {
        var (request, result) = DemoFlow.Build(graphFailed: false);
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, ["req-never-produced"], null);

        Assert.AreEqual(CandidateFlowOutcome.NotRecalled, report.RequiredEvidence.Single().Outcome,
            "全部 Provider 正常但未产出 → 未召回。");
    }

    [TestMethod]
    public void NotGenerated_WhenProviderFailed()
    {
        var (request, result) = DemoFlow.Build(graphFailed: true);
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, ["req-never-produced"], null);

        Assert.AreEqual(CandidateFlowOutcome.NotGenerated, report.RequiredEvidence.Single().Outcome,
            "存在 Provider 失败时未产出证据无法排除未生成。");
        Assert.IsTrue(report.IsDegraded);
    }

    [TestMethod]
    public void ForbiddenSelected_And_HeldDropped_Violations()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var forbidden = report.Violations.Single(v => v.Kind == "forbidden-selected");
        Assert.AreEqual("forbidden-cand", forbidden.EvidenceId);

        var held = report.Violations.Single(v => v.Kind == "held-dropped");
        Assert.AreEqual("held-1", held.EvidenceId);
        StringAssert.Contains(held.Detail, "TokenBudgetExceeded");
    }

    // =========================================================================
    // 2. 通道归因
    // =========================================================================

    [TestMethod]
    public void Duplicates_CrossChannelScores()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var dup = report.Duplicates.Single(d => d.CandidateId == "dup");
        CollectionAssert.AreEquivalent(new[] { "Lexical", "Semantic" }, dup.Channels.ToArray());
        Assert.AreEqual(70.0, dup.ScoreMin, Tolerance);
        Assert.AreEqual(85.0, dup.ScoreMax, Tolerance);

        Assert.AreEqual(2, report.Duplicates.Count, "dup 与 req-a 都跨通道。");
    }

    [TestMethod]
    public void ChannelSummary_UniqueHitAttribution()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        var lexical = report.Channels.Single(c => c.Channel == "Lexical");
        Assert.AreEqual(6, lexical.Produced);
        Assert.AreEqual(4, lexical.Unique, "仅 Lexical 产出的候选：req-b / excluded-cand / req-gate / req-budget。");
        Assert.AreEqual(1, lexical.Selected, "Lexical 产出且选中的只有 req-a。");

        var semantic = report.Channels.Single(c => c.Channel == "Semantic");
        Assert.AreEqual(3, semantic.Produced);
        Assert.AreEqual(1, semantic.Unique, "仅 Semantic 产出的候选：req-c。");
        Assert.AreEqual(2, semantic.Selected, "Semantic 产出且选中的：req-a 与 req-c。");

        var graph = report.Channels.Single(c => c.Channel == "Graph");
        Assert.AreEqual(0, graph.Produced);
    }

    // =========================================================================
    // 3. hydration 与摘要
    // =========================================================================

    [TestMethod]
    public void HydrationCost_SelectedTokensAndFinalTotal()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        Assert.AreEqual(3, report.Hydration.SelectedCount);
        Assert.AreEqual(300, report.Hydration.EstimatedTokens, "三个选中候选各 100 token。");
        Assert.AreEqual(320, report.Hydration.FinalTotalTokens);
        Assert.IsTrue(report.Hydration.WithinBudget);
        Assert.AreEqual(4000, report.Hydration.BudgetLimit);
    }

    // =========================================================================
    // 4. 确定性
    // =========================================================================

    [TestMethod]
    public void SameInput_RepeatedBuild_IdenticalReport()
    {
        var (request, result) = DemoFlow.Build();
        var first = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);
        var second = CandidatesFlowDiagnosticBuilder.Build(request, result, DemoFlow.Required, DemoFlow.Forbidden);

        Assert.AreEqual(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second),
            "同一输入重复构建必须逐位一致（含 CreatedAt 取自输入而非时钟）。");
    }

    // =========================================================================
    // 5. 无正文泄露
    // =========================================================================

    [TestMethod]
    public void ReportTypes_ContainNoContentFields()
    {
        var reportTypes = new[]
        {
            typeof(CandidatesFlowDiagnostics),
            typeof(CandidateOutcomeDiagnostic),
            typeof(EvidenceAttributionDiagnostic),
            typeof(ChannelHitSummary),
            typeof(DuplicateCandidateDiagnostic),
            typeof(SemanticsViolation),
            typeof(SelectedHydrationCost)
        };
        foreach (var type in reportTypes)
        {
            var leak = type.GetProperties()
                .Select(p => p.Name)
                .FirstOrDefault(n => n.Contains("Content", StringComparison.OrdinalIgnoreCase));
            Assert.IsNull(leak, $"{type.Name} 不得携带正文字段（发现 {leak}）。");
        }
    }

    // =========================================================================
    // 6. 装饰器
    // =========================================================================

    [TestMethod]
    public void Decorator_Disabled_Transparent_NoFile()
    {
        using var temp = new TempDir();
        var options = new FlowDiagnosticsOptions { Enabled = false, SampleRate = 1.0, OutputDirectory = temp.Path };
        var inner = new StubRuntime();
        var decorator = new FlowDiagnosticsRuntimeDecorator(inner, options);

        var result = decorator.ExecuteWithWorkingSetAsync(DemoFlow.Request(), default).AsTask().GetAwaiter().GetResult();

        Assert.AreSame(inner.LastResult, result, "关闭时纯透传。");
        Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length, "关闭时不应写文件。");
    }

    [TestMethod]
    public void Decorator_EnabledSampled_WritesSanitizedFile()
    {
        using var temp = new TempDir();
        var options = new FlowDiagnosticsOptions { Enabled = true, SampleRate = 1.0, OutputDirectory = temp.Path };
        var inner = new StubRuntime();
        var decorator = new FlowDiagnosticsRuntimeDecorator(inner, options);

        decorator.ExecuteWithWorkingSetAsync(DemoFlow.Request(), default).AsTask().GetAwaiter().GetResult();

        var files = Directory.GetFiles(temp.Path, "flow-*.json");
        Assert.AreEqual(1, files.Length, "采样命中应写一份报告。");
        var json = File.ReadAllText(files[0]);
        Assert.IsFalse(json.Contains("\"Content\"", StringComparison.OrdinalIgnoreCase), "写出的报告不得含正文。");
        StringAssert.Contains(json, "\"RequestId\"");
    }

    [TestMethod]
    public void Decorator_EnabledUnsampled_NoFile()
    {
        using var temp = new TempDir();
        var options = new FlowDiagnosticsOptions { Enabled = true, SampleRate = 0.0, OutputDirectory = temp.Path };
        var decorator = new FlowDiagnosticsRuntimeDecorator(new StubRuntime(), options);

        decorator.ExecuteWithWorkingSetAsync(DemoFlow.Request(), default).AsTask().GetAwaiter().GetResult();

        Assert.AreEqual(0, Directory.GetFiles(temp.Path).Length, "采样率 0 不应写文件。");
    }

    [TestMethod]
    public void Decorator_WriteFailure_Swallowed_ResultReturned()
    {
        using var temp = new TempDir();
        // 输出目录指向一个已存在的文件 → Directory.CreateDirectory 抛异常 → 必须被吞掉。
        var blocker = Path.Combine(temp.Path, "blocker");
        File.WriteAllText(blocker, "x");
        var options = new FlowDiagnosticsOptions { Enabled = true, SampleRate = 1.0, OutputDirectory = blocker };
        var inner = new StubRuntime();
        var decorator = new FlowDiagnosticsRuntimeDecorator(inner, options);

        var result = decorator.ExecuteWithWorkingSetAsync(DemoFlow.Request(), default).AsTask().GetAwaiter().GetResult();

        Assert.AreSame(inner.LastResult, result, "诊断写失败绝不影响主流程。");
    }

    [TestMethod]
    public void Options_ShouldSample_StablePerRequestId()
    {
        var options = new FlowDiagnosticsOptions { Enabled = true, SampleRate = 0.5 };

        Assert.AreEqual(options.ShouldSample("req-1"), options.ShouldSample("req-1"), "同一请求 ID 采样结论稳定。");
        Assert.IsFalse(new FlowDiagnosticsOptions { Enabled = false }.ShouldSample("req-1"));
        Assert.IsTrue(new FlowDiagnosticsOptions { Enabled = true, SampleRate = 1.0 }.ShouldSample("req-1"));
        Assert.IsFalse(new FlowDiagnosticsOptions { Enabled = true, SampleRate = 0.0 }.ShouldSample("req-1"));
    }

    // =========================================================================
    // 7. 请求级 RequiredIds 归因
    // =========================================================================

    [TestMethod]
    public void RequestLevelRequiredIds_Attributed_WhenNoExpectationPassed()
    {
        var (request, result) = DemoFlow.Build();
        var report = CandidatesFlowDiagnosticBuilder.Build(request, result);

        var evidence = report.RequiredEvidence.Single(e => e.EvidenceId == "req-a");
        Assert.AreEqual(CandidateFlowOutcome.Selected, evidence.Outcome, "未传期望时用请求级 RequiredIds 归因。");
    }

    // =========================================================================
    // 测试夹具
    // =========================================================================

    /// <summary>固定演示场景（与 CLI smoke 一致），可关闭 Graph 失败以区分未召回/未生成。</summary>
    private static class DemoFlow
    {
        public static readonly string[] Required =
            ["req-a", "req-b", "req-c", "excluded-cand", "req-gate", "req-budget", "req-notrecalled", "req-notgenerated"];

        public static readonly string[] Forbidden = ["forbidden-cand"];

        public static ContextDecisionRuntimeRequest Request()
        {
            return new ContextDecisionRuntimeRequest
            {
                RequestId = "demo-flow-1",
                Scope = new ContextDecisionScope("ws-demo", "col-demo"),
                Purpose = ContextDecisionPurpose.Retrieval,
                QueryText = "演示查询",
                TokenBudget = 4000,
                TopK = 10,
                RetrievalInput = new RetrievalInput
                {
                    ExcludedIds = ["excluded-cand"],
                    RequiredIds = ["req-a", "req-missing"]
                },
                SeedCandidates =
                [
                    Env("held-1", 95, ContextCandidateSource.WorkingMemory, CandidateDecisionReasonCode.TokenBudgetExceeded, 800)
                ]
            };
        }

        public static (ContextDecisionRuntimeRequest Request, ContextDecisionExecutionResult Result) Build(bool graphFailed = true)
        {
            var request = Request();
            var scope = request.Scope;

            var lexical = new[]
            {
                Env("req-a", 90, ContextCandidateSource.Lexical),
                Env("req-b", 80, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.ScoreBelowThreshold),
                Env("dup", 70, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.DuplicateSuppressed),
                Env("excluded-cand", 60, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.LifecycleBlocked),
                Env("req-gate", 55, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.LifecycleBlocked),
                Env("req-budget", 88, ContextCandidateSource.Lexical, CandidateDecisionReasonCode.TokenBudgetExceeded, 2000)
            };
            var semantic = new[]
            {
                Env("req-a", 95, ContextCandidateSource.Semantic),
                Env("dup", 85, ContextCandidateSource.Semantic, CandidateDecisionReasonCode.DuplicateSuppressed),
                Env("req-c", 75, ContextCandidateSource.Semantic)
            };
            var forbidden = Env("forbidden-cand", 99, ContextCandidateSource.Mandatory);

            var graphReports = new[]
            {
                new ProviderExecutionReport { Kind = ExpertKind.Lexical, Succeeded = true, TimedOut = false, Duration = TimeSpan.FromMilliseconds(3), CandidateCount = lexical.Length },
                new ProviderExecutionReport { Kind = ExpertKind.Semantic, Succeeded = true, TimedOut = false, Duration = TimeSpan.FromMilliseconds(5), CandidateCount = semantic.Length },
                new ProviderExecutionReport
                {
                    Kind = ExpertKind.Graph,
                    Succeeded = !graphFailed,
                    TimedOut = graphFailed,
                    Duration = graphFailed ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(1),
                    CandidateCount = 0,
                    ErrorCode = graphFailed ? "timeout" : null
                }
            };
            var graphSnapshots = new[]
            {
                new ProviderOutputSnapshot { Kind = ExpertKind.Lexical, Envelopes = lexical, Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(), Succeeded = true, Duration = TimeSpan.FromMilliseconds(3) },
                new ProviderOutputSnapshot { Kind = ExpertKind.Semantic, Envelopes = semantic, Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(), Succeeded = true, Duration = TimeSpan.FromMilliseconds(5) },
                new ProviderOutputSnapshot
                {
                    Kind = ExpertKind.Graph,
                    Envelopes = Array.Empty<ContextCandidateEnvelope>(),
                    Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>(),
                    Succeeded = !graphFailed,
                    Duration = graphFailed ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(1),
                    ErrorCode = graphFailed ? "timeout" : null
                }
            };

            var workingSet = lexical.Concat(semantic).Append(forbidden).Append(request.SeedCandidates[0]).ToArray();
            var result = new ContextDecisionExecutionResult
            {
                Decision = new ContextDecisionResult
                {
                    RequestId = request.RequestId,
                    DecisionSource = ContextDecisionSource.Retrieval,
                    Purpose = ContextDecisionPurpose.Retrieval,
                    RuntimeKind = ContextDecisionRuntimeKind.UnifiedV2,
                    SelectedEnvelopes = [semantic[0], semantic[2], forbidden],
                    DroppedEnvelopes = [lexical[1], lexical[2], lexical[3], lexical[4], lexical[5], request.SeedCandidates[0]]
                },
                WorkingSet = new CandidateWorkingSet { Envelopes = workingSet, Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>() },
                Policy = new EffectivePolicySnapshot
                {
                    Reference = new ResolvedPolicyReference { BundleId = "demo", BundleVersion = "v1", BundleContentHash = "demo-hash", ActivationEpoch = 1 },
                    Safety = new SafetyProfile { ProfileId = "safety-demo" },
                    Budget = new BudgetProfile { ProfileId = "budget-demo" },
                    Routing = new RoutingProfile { ProfileId = "routing-demo" },
                    FeatureSchemaVersion = "v1",
                    ResolutionScope = scope
                },
                Routing = new ExpertRoutingDecisionSet { Decisions = Array.Empty<ExpertRoutingDecision>() },
                NormalizedRequest = request,
                RequestSemanticHash = "demo-hash",
                Scope = scope,
                FeatureSchemaVersion = "v1",
                AllocatorVersion = "v2.1",
                IsDegraded = graphFailed,
                ProviderReports = graphReports,
                ProviderOutputSnapshots = graphSnapshots,
                FinalTokenCost = new FinalArtifactTokenCost
                {
                    Sections = Array.Empty<SectionTokenCost>(),
                    TotalTokens = 320,
                    TokenizerId = "unicode-cjk-v1",
                    WithinBudget = true,
                    BudgetLimit = 4000
                }
            };
            return (request, result);
        }

        private static ContextCandidateEnvelope Env(string id, double score, ContextCandidateSource source,
            CandidateDecisionReasonCode? drop = null, int tokens = 100) =>
            new()
            {
                CandidateId = id,
                Source = source,
                CanonicalKey = CanonicalCandidateKey.Create("ws-demo", "col-demo", "note", id, "v1"),
                Utility = new CandidateUtilityScore { DeterministicScore = score, FinalScore = score },
                TokenCost = new CandidateTokenCost { ContentTokens = tokens, TokenizerId = "unicode-cjk-v1", IsEstimated = false },
                Safety = drop is null
                    ? new CandidateSafetyState()
                    : new CandidateSafetyState { PassesSafetyGate = false, BlockReasonCode = drop.Value }
            };
    }

    private sealed class StubRuntime : IContextDecisionRuntime
    {
        public ContextDecisionExecutionResult? LastResult { get; private set; }

        public ValueTask<ContextDecisionResult> ExecuteAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            var (_, result) = DemoFlow.Build();
            return ValueTask.FromResult(result.Decision);
        }

        public ValueTask<ContextDecisionExecutionResult> ExecuteWithWorkingSetAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            var (_, result) = DemoFlow.Build();
            LastResult = result;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-lr1b-tests", Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论。
            }
        }
    }
}
