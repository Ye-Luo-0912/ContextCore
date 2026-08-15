using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Tests;

/// <summary>
/// HTTP（检索）与 Agent（打包）可比较部分使用同一质量口径的锁定测试。
/// 引擎是唯一正式运行时入口，两条路径共享同一评分、分配与汇总逻辑；
/// 本测试锁定：相同候选集下两条路径产出完全一致的质量指标（FinalScore 排序、
/// token 口径、outcome 汇总），投影层从同一共享函数导出 Score 与 EstimatedTokens。
/// 防止未来按 DecisionSource 分裂质量指标。
/// </summary>
[TestClass]
[TestCategory("LR3D")]
[TestCategory("DecisionEngine")]
public sealed class RuntimeQualityMetricConsistencyTests
{
    private const string Ws = "ws";
    private const string Col = "col";

    /// <summary>
    /// 验证：相同候选集与预算下，Retrieval 与 Package 两条路径的引擎输出完全一致——
    /// 选中顺序、每个候选的 FinalScore、token 汇总与 outcome 计数均相同。
    /// </summary>
    [TestMethod]
    public async Task Engine_RetrievalAndPackage_ShareQualityMetric()
    {
        var engine = BuildEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Mandatory, raw: 100.0, tokens: 4),
            MakeEnvelope("c2", ContextCandidateSource.Graph, raw: 95.0, tokens: 4),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, raw: 90.0, tokens: 4),
            MakeEnvelope("c4", ContextCandidateSource.Semantic, raw: 88.0, tokens: 4),
            MakeEnvelope("c5", ContextCandidateSource.RelatedContext, raw: 70.0, tokens: 4)
        };

        var retrieval = await engine.DecideAsync(
            BuildRequest(candidates, topK: 10, tokenBudget: 20, ContextDecisionSource.Retrieval, ContextDecisionPurpose.Retrieval),
            CancellationToken.None);
        var package = await engine.DecideAsync(
            BuildRequest(candidates, topK: 10, tokenBudget: 20, ContextDecisionSource.Package, ContextDecisionPurpose.Package),
            CancellationToken.None);

        // 选中集合与顺序一致（同一质量分排序）
        CollectionAssert.AreEqual(
            retrieval.SelectedEnvelopes.Select(e => e.CandidateId).ToArray(),
            package.SelectedEnvelopes.Select(e => e.CandidateId).ToArray(),
            "两条路径的选中顺序应一致。");

        // 每个选中候选的 FinalScore 一致（同一质量分）
        CollectionAssert.AreEqual(
            retrieval.SelectedEnvelopes.Select(e => e.Utility.FinalScore).ToArray(),
            package.SelectedEnvelopes.Select(e => e.Utility.FinalScore).ToArray(),
            "两条路径的 FinalScore 应一致。");

        // outcome 汇总一致（token 口径与计数）
        Assert.AreEqual(retrieval.Outcome.EffectiveTokens, package.Outcome.EffectiveTokens,
            "两条路径的有效 token 汇总应一致。");
        Assert.AreEqual(retrieval.Outcome.SelectedCount, package.Outcome.SelectedCount,
            "两条路径的选中计数应一致。");
        Assert.AreEqual(retrieval.Outcome.DroppedCount, package.Outcome.DroppedCount,
            "两条路径的丢弃计数应一致。");
        Assert.AreEqual(retrieval.Outcome.SafetyGateBlockedCount, package.Outcome.SafetyGateBlockedCount,
            "两条路径的 gate 拦截计数应一致。");
        Assert.AreEqual(retrieval.Outcome.BudgetExceededCount, package.Outcome.BudgetExceededCount,
            "两条路径的预算拦截计数应一致。");
    }

    /// <summary>
    /// 验证：同一决策结果经 Retrieval 与 Package 投影后，每个条目的 Score 与
    /// EstimatedTokens 来自同一口径（envelope 的 FinalScore + 共享的 token 计算），
    /// 两个投影路径不会各自实现一套质量换算。
    /// </summary>
    [TestMethod]
    public void Projectors_RetrievalAndPackage_ReportSameScoreAndTokens()
    {
        var result = new ContextDecisionResult
        {
            RequestId = "req-shared",
            DecisionSource = ContextDecisionSource.Retrieval,
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("c1", ContextCandidateSource.Mandatory, raw: 100.0, tokens: 5),
                MakeEnvelope("c2", ContextCandidateSource.Graph, raw: 95.0, tokens: 7)
            },
            DroppedEnvelopes = Array.Empty<ContextCandidateEnvelope>(),
            Outcome = new ContextDecisionOutcomeSummary { SelectedCount = 2, DroppedCount = 0, EffectiveTokens = 12 }
        };

        var retrievalDto = new RetrievalResultProjector().Project(result);
        var packageDto = new PackageResultProjector().Project(result);

        Assert.AreEqual(retrievalDto.SelectedItems.Count, packageDto.SelectedItems.Count,
            "两个投影路径应产出相同数量的选中条目。");
        for (var i = 0; i < retrievalDto.SelectedItems.Count; i++)
        {
            Assert.AreEqual(
                retrievalDto.SelectedItems[i].Score,
                packageDto.SelectedItems[i].Score,
                1e-9,
                "两个投影路径的 Score 应来自同一 FinalScore。");
            Assert.AreEqual(
                retrievalDto.SelectedItems[i].EstimatedTokens,
                packageDto.SelectedItems[i].EstimatedTokens,
                "两个投影路径的 EstimatedTokens 应来自同一 token 口径。");
        }
    }

    // ── 构造与桩 ───────────────────────────────────────────────────────────

    private static DefaultContextDecisionEngine BuildEngine()
        => new(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: new DefaultUtilityScorer(new DefaultFeatureSchemaValidator()),
            globalAllocator: new DefaultGlobalAllocator(),
            allocatorV2_1: null,
            performanceMonitor: null,
            componentHealthRegistry: null,
            reranker: null);

    private static ContextDecisionRequest BuildRequest(
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int topK,
        int tokenBudget,
        ContextDecisionSource source,
        ContextDecisionPurpose purpose)
    {
        var snapshot = BuildSnapshot();
        return new ContextDecisionRequest
        {
            RequestId = "req-shared-metric",
            DecisionSource = source,
            WorkspaceId = Ws,
            CollectionId = Col,
            Candidates = candidates,
            TokenBudget = tokenBudget,
            TopK = topK,
            PolicySnapshot = snapshot,
            AllocationContext = new AllocationContext
            {
                Purpose = purpose,
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

    private static ContextCandidateEnvelope MakeEnvelope(string id, ContextCandidateSource source, double raw, int tokens)
        => new()
        {
            CandidateId = $"{source}:{id}",
            Source = source,
            Type = "note",
            WorkspaceId = Ws,
            CollectionId = Col,
            CanonicalKey = CanonicalCandidateKey.Create(Ws, Col, "context", id, "1"),
            Features = new CandidateFeatureVector(),
            TokenCost = new CandidateTokenCost
            {
                ContentTokens = tokens,
                TokenizerId = "test",
                IsEstimated = false
            },
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = raw,
                FinalScore = raw,
                ReasonCode = "deterministic-only"
            }
        };
}
