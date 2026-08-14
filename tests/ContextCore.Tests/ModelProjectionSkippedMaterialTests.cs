using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Tests;

// 投影因预算跳过 ≠ 分配器选中：跳过的材料 ID 要回报给 Actor，
// 下一轮找回问句才能覆盖「选了但没投影」的条目。

[TestClass]
[TestCategory("Agent-Run-Full-Loop")]
public sealed class ModelProjectionSkippedMaterialTests
{
    [TestMethod]
    public void Project_ReportsSkippedMaterialIds_WhenBudgetTooSmall()
    {
        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var amberKey = CanonicalCandidateKey.Create("ws", "col", "note", "AmberCompass-17", "v1");
        var keep = MakeEnvelope("keep-1", keepKey, finalScore: 0.8);
        var amber = MakeEnvelope("AmberCompass-17", amberKey, finalScore: 0.7);

        var execution = new ContextDecisionExecutionResult
        {
            Decision = new ContextDecisionResult
            {
                SelectedEnvelopes = new[] { keep, amber },
                Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 2 }
            },
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep, amber },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "short body", NativeKind = "note" },
                    [amberKey] = new CandidateMaterial { Key = amberKey, Content = new string('x', 400), NativeKind = "note" }
                }
            },
            Policy = MakePolicySnapshot(),
            Routing = new ExpertRoutingDecisionSet
            {
                Decisions = Array.Empty<ExpertRoutingDecision>()
            }
        };

        var projector = new DefaultAgentModelContextProjector();
        var projection = projector.Project(
            MakeRun(), execution, new AgentContextState { CurrentTask = "task" }, modelContextTokenBudget: 30);

        CollectionAssert.Contains(projection.SkippedMaterialIds.ToList(), "Lexical:AmberCompass-17",
            "预算不足时被跳过的材料应列入跳过列表。");
        Assert.IsFalse(projection.SkippedMaterialIds.Contains("Lexical:keep-1"),
            "能放下的材料不应出现在跳过列表。");
    }

    [TestMethod]
    public void Project_NoSkip_WhenBudgetUnbounded()
    {
        var keepKey = CanonicalCandidateKey.Create("ws", "col", "note", "keep-1", "v1");
        var keep = MakeEnvelope("keep-1", keepKey, finalScore: 0.8);
        var execution = new ContextDecisionExecutionResult
        {
            Decision = new ContextDecisionResult
            {
                SelectedEnvelopes = new[] { keep },
                Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 1 }
            },
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { keep },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [keepKey] = new CandidateMaterial { Key = keepKey, Content = "short body", NativeKind = "note" }
                }
            },
            Policy = MakePolicySnapshot(),
            Routing = new ExpertRoutingDecisionSet
            {
                Decisions = Array.Empty<ExpertRoutingDecision>()
            }
        };

        var projector = new DefaultAgentModelContextProjector();
        var projection = projector.Project(
            MakeRun(), execution, new AgentContextState { CurrentTask = "task" }, modelContextTokenBudget: 0);

        Assert.AreEqual(0, projection.SkippedMaterialIds.Count,
            "预算不限制时不应有跳过材料。");
    }

    /// <summary>
    /// 验证：材料投影顺序 = 分配器 SelectedEnvelopes 原顺序，不用 FinalScore 重排。
    /// 低分材料在 Selected 里更靠前时，投影先尝试靠前那条（低分先进模型）。
    /// </summary>
    [TestMethod]
    public void Project_KeepsAllocatorOrder_NotFinalScoreOrder()
    {
        var lowKey = CanonicalCandidateKey.Create("ws", "col", "note", "low-1", "v1");
        var highKey = CanonicalCandidateKey.Create("ws", "col", "note", "high-2", "v1");
        var low = MakeEnvelope("low-1", lowKey, finalScore: 0.7);
        var high = MakeEnvelope("high-2", highKey, finalScore: 0.9);

        var execution = new ContextDecisionExecutionResult
        {
            Decision = new ContextDecisionResult
            {
                // 分配器顺序：FinalScore 低的反倒靠前
                SelectedEnvelopes = new[] { low, high },
                Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 2 }
            },
            WorkingSet = new CandidateWorkingSet
            {
                Envelopes = new[] { low, high },
                Materials = new Dictionary<CanonicalCandidateKey, CandidateMaterial>
                {
                    [lowKey] = new CandidateMaterial { Key = lowKey, Content = "alpha body", NativeKind = "note" },
                    [highKey] = new CandidateMaterial { Key = highKey, Content = "beta body", NativeKind = "note" }
                }
            },
            Policy = MakePolicySnapshot(),
            Routing = new ExpertRoutingDecisionSet
            {
                Decisions = Array.Empty<ExpertRoutingDecision>()
            }
        };

        var projector = new DefaultAgentModelContextProjector();
        var projection = projector.Project(
            MakeRun(), execution, new AgentContextState { CurrentTask = "task" }, modelContextTokenBudget: 0);

        var messages = projection.Messages.ToList();
        var alphaIndex = messages.FindIndex(msg => msg.Content.Contains("alpha body", StringComparison.Ordinal));
        var betaIndex = messages.FindIndex(msg => msg.Content.Contains("beta body", StringComparison.Ordinal));
        Assert.IsTrue(alphaIndex >= 0 && betaIndex >= 0, "两条材料都应投影进上下文。");
        Assert.IsTrue(alphaIndex < betaIndex,
            "材料顺序应保持分配器 Selected 顺序（低分在前），不是 FinalScore 降序。");
    }

    private static EffectivePolicySnapshot MakePolicySnapshot()
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
            ResolutionScope = new ContextDecisionScope("ws", "col")
        };
    }

    private static AgentRun MakeRun() => new()
    {
        RunId = "run-proj-skip",
        WorkspaceId = "ws",
        SessionId = "session-proj-skip",
        Task = "summarize",
        State = AgentRunState.ContextBuilding,
        Turn = 0,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static ContextCandidateEnvelope MakeEnvelope(string id, CanonicalCandidateKey key, double finalScore)
        => new()
        {
            CandidateId = "Lexical:" + id,
            Source = ContextCandidateSource.Lexical,
            CanonicalKey = key,
            Utility = new CandidateUtilityScore { DeterministicScore = finalScore, FinalScore = finalScore }
        };
}
