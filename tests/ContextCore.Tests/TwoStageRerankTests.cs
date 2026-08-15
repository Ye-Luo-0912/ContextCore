using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Tests;

/// <summary>
/// 两阶段排序契约测试。
/// 覆盖：第二阶段确定性 reranker 只对有限窗口重排（窗口外保持第一阶段顺序）、
/// 来源多样性加成（唯一通道不被挤占）、确定性 tie-break、provenance 记录重排分量；
/// 引擎挂载点在评分之后分配之前，开关默认关闭时顺序逐位不变；
/// 验收指标（Precision@K / Recall@K / nDCG@K）在受控标注集上可计算且重排提升精度。
/// </summary>
[TestClass]
[TestCategory("LR3B")]
[TestCategory("Retrieval")]
public sealed class TwoStageRerankTests
{
    private const string Ws = "ws";
    private const string Col = "col";

    // ── reranker 机制 ────────────────────────────────────────────────────────

    /// <summary>
    /// 验证：只重排有限窗口（前 N 个），窗口外候选保持输入顺序且不写重排 provenance；
    /// 同输入两次调用结果一致（确定性）。
    /// </summary>
    [TestMethod]
    public async Task Reranker_WindowBounded_Deterministic_NonWindowPreserved()
    {
        var reranker = new DefaultCandidateReranker();
        // 40 个候选，分数 100..61 降序输入；窗口内重排为升序输入下的降序输出。
        var candidates = Enumerable.Range(0, 40)
            .Select(i => MakeEnvelope($"c{i}", ContextCandidateSource.Lexical, raw: 100.0 - i))
            .ToArray();

        var first = await reranker.RerankAsync(candidates, BuildSnapshot(twoStageRerank: true), CancellationToken.None);
        var second = await reranker.RerankAsync(candidates, BuildSnapshot(twoStageRerank: true), CancellationToken.None);

        // 确定性按候选顺序比较（候选记录的 ScoreBreakdown 字典实例每次重排会重建，
        // 记录相等性对字典成员是引用比较，因此只比较候选顺序）。
        CollectionAssert.AreEqual(
            first.Select(e => e.CandidateId).ToArray(),
            second.Select(e => e.CandidateId).ToArray(),
            "重排必须确定性。");

        // 窗口内（前 32 条）按 FinalScore 降序重排，且带重排 provenance。
        var window = first.Take(DefaultCandidateReranker.RerankWindowSize).ToArray();
        for (var i = 1; i < window.Length; i++)
        {
            Assert.IsTrue(
                window[i - 1].Utility.FinalScore >= window[i].Utility.FinalScore,
                "窗口内应按评分降序。");
            Assert.IsTrue(window[i].Features.ScoreBreakdown.ContainsKey("rerank_diversity"),
                "窗口内候选应记录重排分量。");
        }

        // 窗口外（后 8 条，最低分）保持输入顺序（未被重排），且无重排 provenance。
        var rest = first.Skip(DefaultCandidateReranker.RerankWindowSize).ToArray();
        Assert.AreEqual(8, rest.Length);
        for (var i = 0; i < rest.Length; i++)
        {
            Assert.AreEqual($"c{32 + i}", rest[i].CanonicalKey.EntityId, "窗口外候选保持第一阶段顺序。");
            Assert.IsFalse(rest[i].Features.ScoreBreakdown.ContainsKey("rerank_diversity"),
                "窗口外候选不参与重排、不写 provenance。");
        }
    }

    /// <summary>
    /// 验证：同分情况下，窗口内唯一通道的候选获得多样性加成，不被单一通道挤占。
    /// </summary>
    [TestMethod]
    public async Task Reranker_DiversityBoostsUniqueChannel()
    {
        var reranker = new DefaultCandidateReranker();
        var candidates = new[]
        {
            MakeEnvelope("lex-a", ContextCandidateSource.Lexical, raw: 100.0),
            MakeEnvelope("lex-b", ContextCandidateSource.Lexical, raw: 100.0),
            MakeEnvelope("graph-c", ContextCandidateSource.Graph, raw: 100.0)
        };

        var reranked = await reranker.RerankAsync(candidates, BuildSnapshot(twoStageRerank: true), CancellationToken.None);

        Assert.AreEqual("graph-c", reranked[0].CanonicalKey.EntityId,
            "唯一通道候选（Graph）应因多样性加成排到同分词法候选之前。");
        Assert.AreEqual(3.0, reranked[0].Features.ScoreBreakdown["rerank_diversity"], 0.001,
            "重排分量应记录多样性加成。");
        Assert.AreEqual(0.0, reranked[1].Features.ScoreBreakdown["rerank_diversity"], 0.001);
    }

    /// <summary>
    /// 验证：重排只改顺序不改 FinalScore（评分与排序职责分离）。
    /// </summary>
    [TestMethod]
    public async Task Reranker_ChangesOrderOnly_NotFinalScore()
    {
        var reranker = new DefaultCandidateReranker();
        var candidates = new[]
        {
            MakeEnvelope("lex-a", ContextCandidateSource.Lexical, raw: 42.0),
            MakeEnvelope("graph-b", ContextCandidateSource.Graph, raw: 42.0)
        };

        var reranked = await reranker.RerankAsync(candidates, BuildSnapshot(twoStageRerank: true), CancellationToken.None);

        Assert.AreEqual(42.0, reranked.Single(e => e.CanonicalKey.EntityId == "lex-a").Utility.FinalScore, 0.001,
            "重排不得改写 FinalScore。");
        Assert.AreEqual("deterministic-only", reranked.Single(e => e.CanonicalKey.EntityId == "graph-b").Utility.ReasonCode,
            "重排不得改写评分原因码。");
    }

    // ── 引擎挂载：开关默认关闭逐位不变，启用后影响分配结果 ───────────────────

    /// <summary>
    /// 验证：注入 reranker 但开关关闭时，决策顺序与不注入时一致（无行为回退）。
    /// </summary>
    [TestMethod]
    public async Task Engine_Rerank_Disabled_Unchanged()
    {
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator());
        var withReranker = BuildEngine(scorer, reranker: new DefaultCandidateReranker());
        var withoutReranker = BuildEngine(scorer, reranker: null);

        var candidates = new[]
        {
            MakeEnvelope("lex-a", ContextCandidateSource.Lexical, raw: 100.0),
            MakeEnvelope("lex-b", ContextCandidateSource.Lexical, raw: 99.0),
            MakeEnvelope("graph-c", ContextCandidateSource.Graph, raw: 97.0)
        };

        var disabled = await withReranker.DecideAsync(BuildRequest(candidates, twoStageRerank: false), CancellationToken.None);
        var baseline = await withoutReranker.DecideAsync(BuildRequest(candidates, twoStageRerank: false), CancellationToken.None);

        CollectionAssert.AreEqual(
            baseline.SelectedEnvelopes.Select(e => e.CanonicalKey.EntityId).ToArray(),
            disabled.SelectedEnvelopes.Select(e => e.CanonicalKey.EntityId).ToArray(),
            "开关关闭时启用 reranker 不应改变决策。");
    }

    /// <summary>
    /// 验证：启用两阶段重排后，来源多样性候选进入 TopK 分配（开关改变分配结果，
    /// 而不是只改内部顺序）。
    /// </summary>
    [TestMethod]
    public async Task Engine_Rerank_Enabled_ChangesAllocationOutcome()
    {
        var scorer = new DefaultUtilityScorer(new DefaultFeatureSchemaValidator());
        var engine = BuildEngine(scorer, reranker: new DefaultCandidateReranker());

        var candidates = new[]
        {
            MakeEnvelope("lex-a", ContextCandidateSource.Lexical, raw: 100.0),
            MakeEnvelope("lex-b", ContextCandidateSource.Lexical, raw: 99.0),
            MakeEnvelope("graph-c", ContextCandidateSource.Graph, raw: 97.0)
        };

        var disabled = await engine.DecideAsync(BuildRequest(candidates, twoStageRerank: false), CancellationToken.None);
        var enabled = await engine.DecideAsync(BuildRequest(candidates, twoStageRerank: true), CancellationToken.None);

        var disabledIds = disabled.SelectedEnvelopes.Select(e => e.CanonicalKey.EntityId).ToArray();
        var enabledIds = enabled.SelectedEnvelopes.Select(e => e.CanonicalKey.EntityId).ToArray();

        CollectionAssert.AreEquivalent(new[] { "lex-a", "lex-b" }, disabledIds,
            "关闭重排：TopK=2 选中两条高分词法候选。");
        CollectionAssert.AreEquivalent(new[] { "lex-a", "graph-c" }, enabledIds,
            "启用重排：多样性加成让唯一通道候选进入 TopK。");
    }

    // ── 验收指标：Precision@K / Recall@K / nDCG@K ───────────────────────────

    /// <summary>
    /// 验证：在受控标注集上计算验收指标——重排后 Precision@K 与 nDCG@K 提升、
    /// Recall@K 不下降（重排不丢相关证据，只改善排序）。
    /// </summary>
    [TestMethod]
    public async Task Metrics_RecallPrecisionNDCG_RerankImprovesRankingQuality()
    {
        var reranker = new DefaultCandidateReranker();
        var candidates = new[]
        {
            MakeEnvelope("lex-a", ContextCandidateSource.Lexical, raw: 100.0),
            MakeEnvelope("lex-b", ContextCandidateSource.Lexical, raw: 99.0),
            MakeEnvelope("graph-c", ContextCandidateSource.Graph, raw: 97.0)
        };
        // 标注：graph-c 是唯一高相关证据；两条词法候选均低相关（Precision@K 基线为 0）。
        var relevance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["lex-a"] = 0,
            ["lex-b"] = 0,
            ["graph-c"] = 3
        };

        var baseline = candidates.Select(e => e.CanonicalKey.EntityId).ToArray();
        var reranked = (await reranker.RerankAsync(candidates, BuildSnapshot(twoStageRerank: true), CancellationToken.None))
            .Select(e => e.CanonicalKey.EntityId)
            .ToArray();

        var baselineMetrics = ComputeMetrics(baseline, relevance, k: 2);
        var rerankedMetrics = ComputeMetrics(reranked, relevance, k: 2);

        Assert.IsTrue(rerankedMetrics.PrecisionAtK > baselineMetrics.PrecisionAtK,
            "重排应提升 Precision@K（高相关证据前移）。");
        Assert.IsTrue(rerankedMetrics.NdcgAtK > baselineMetrics.NdcgAtK,
            "重排应提升 nDCG@K。");
        Assert.IsTrue(rerankedMetrics.RecallAtK >= baselineMetrics.RecallAtK,
            "重排不应降低 Recall@K。");
    }

    // ── 构造与指标计算 ───────────────────────────────────────────────────────

    private static DefaultContextDecisionEngine BuildEngine(IUtilityScorer scorer, ICandidateReranker? reranker)
        => new(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: scorer,
            globalAllocator: new DefaultGlobalAllocator(),
            allocatorV2_1: null,
            performanceMonitor: null,
            componentHealthRegistry: null,
            reranker: reranker);

    private static ContextDecisionRequest BuildRequest(IReadOnlyList<ContextCandidateEnvelope> candidates, bool twoStageRerank)
    {
        var snapshot = BuildSnapshot(twoStageRerank);
        return new ContextDecisionRequest
        {
            RequestId = "req-rerank",
            DecisionSource = ContextDecisionSource.Retrieval,
            WorkspaceId = Ws,
            CollectionId = Col,
            Candidates = candidates,
            TokenBudget = 4096,
            TopK = 2,
            PolicySnapshot = snapshot,
            AllocationContext = new AllocationContext
            {
                Purpose = ContextDecisionPurpose.Retrieval,
                Budget = snapshot.Budget,
                MandatoryOverflowPolicy = MandatoryOverflowPolicy.AllowOverflowWithDiagnostic
            }
        };
    }

    private static EffectivePolicySnapshot BuildSnapshot(bool twoStageRerank)
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
            Routing = bundle.Routing with { EnableTwoStageRerank = twoStageRerank },
            FeatureSchemaVersion = bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope(Ws, Col)
        };
    }

    private static ContextCandidateEnvelope MakeEnvelope(string id, ContextCandidateSource source, double raw)
        => new()
        {
            CandidateId = $"{source}:{id}",
            Source = source,
            Type = "note",
            WorkspaceId = Ws,
            CollectionId = Col,
            CanonicalKey = CanonicalCandidateKey.Create(Ws, Col, "context", id, "1"),
            Features = new CandidateFeatureVector(),
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = raw,
                FinalScore = raw,
                ReasonCode = "deterministic-only"
            }
        };

    private static (double PrecisionAtK, double RecallAtK, double NdcgAtK) ComputeMetrics(
        IReadOnlyList<string> ranking,
        IReadOnlyDictionary<string, int> relevance,
        int k)
    {
        var topK = ranking.Take(k).ToArray();
        var relevantTotal = relevance.Values.Count(rel => rel >= 1);

        var relevantInTopK = topK.Count(id => relevance.GetValueOrDefault(id) >= 1);
        var precision = k == 0 ? 0.0 : (double)relevantInTopK / k;
        var recall = relevantTotal == 0 ? 0.0 : (double)relevantInTopK / relevantTotal;

        var dcg = 0.0;
        for (var i = 0; i < topK.Length; i++)
        {
            dcg += relevance.GetValueOrDefault(topK[i]) / Math.Log2(i + 2);
        }
        var ideal = relevance.Values.OrderByDescending(rel => rel).Take(k).ToArray();
        var idcg = 0.0;
        for (var i = 0; i < ideal.Length; i++)
        {
            idcg += ideal[i] / Math.Log2(i + 2);
        }
        var ndcg = idcg <= 0 ? 0.0 : dcg / idcg;

        return (precision, recall, ndcg);
    }
}
