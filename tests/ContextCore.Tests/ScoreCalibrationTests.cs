using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// 候选 provenance 与分数校准契约测试。
/// 覆盖：分桶校准器把各通道原始分映射到公共刻度（确定性、可解释、边界稳定），
/// 分配器不再直接比较语义不同的 lexical/vector/graph 原始分；
/// 校准保留原始分（ScoreBreakdown["raw"]）供审计；开关默认关闭时行为逐位不变；
/// 同一请求内 policy 只解析一次、feature 只计算一次。
/// </summary>
[TestClass]
[TestCategory("LR3A")]
[TestCategory("Retrieval")]
public sealed class ScoreCalibrationTests
{
    private const string Ws = "ws";
    private const string Col = "col";
    private const string Q1 = "分布式事务提交";

    // ── 分桶边界与确定性 ─────────────────────────────────────────────────────

    /// <summary>
    /// 验证：每个通道的桶边界稳定（左闭右开）、同输入同输出、通道内单调不降；
    /// 记忆通道按层映射到固定校准值；非召回通道原样返回。
    /// </summary>
    [TestMethod]
    public void Calibrator_BucketBoundaries_DeterministicAndMonotonic()
    {
        var calibrator = new DefaultCandidateScoreCalibrator();

        // Lexical：<40→35，[40,60)→50，[60,80)→65，≥80→85。
        Assert.AreEqual(35.0, calibrator.Calibrate(ContextCandidateSource.Lexical, 39.9), 0.001);
        Assert.AreEqual(50.0, calibrator.Calibrate(ContextCandidateSource.Lexical, 40.0), 0.001);
        Assert.AreEqual(50.0, calibrator.Calibrate(ContextCandidateSource.Lexical, 59.9), 0.001);
        Assert.AreEqual(65.0, calibrator.Calibrate(ContextCandidateSource.Lexical, 60.0), 0.001);
        Assert.AreEqual(85.0, calibrator.Calibrate(ContextCandidateSource.Lexical, 80.0), 0.001);
        Assert.AreEqual(85.0, calibrator.Calibrate(ContextCandidateSource.Lexical, 150.0), 0.001);

        // Semantic：<40→30，[40,60)→50，[60,80)→70，≥80→90。
        Assert.AreEqual(30.0, calibrator.Calibrate(ContextCandidateSource.Semantic, 39.9), 0.001);
        Assert.AreEqual(50.0, calibrator.Calibrate(ContextCandidateSource.Semantic, 40.0), 0.001);
        Assert.AreEqual(70.0, calibrator.Calibrate(ContextCandidateSource.Semantic, 60.0), 0.001);
        Assert.AreEqual(90.0, calibrator.Calibrate(ContextCandidateSource.Semantic, 80.0), 0.001);
        Assert.AreEqual(90.0, calibrator.Calibrate(ContextCandidateSource.Semantic, 100.0), 0.001);

        // Graph：<30→25，≥30→55。
        Assert.AreEqual(25.0, calibrator.Calibrate(ContextCandidateSource.Graph, 29.9), 0.001);
        Assert.AreEqual(55.0, calibrator.Calibrate(ContextCandidateSource.Graph, 30.0), 0.001);

        // 记忆层：Working→55，Stable→75。
        Assert.AreEqual(55.0, calibrator.Calibrate(ContextCandidateSource.WorkingMemory, 50.0), 0.001);
        Assert.AreEqual(75.0, calibrator.Calibrate(ContextCandidateSource.StableMemory, 80.0), 0.001);

        // 非召回通道原样返回（不参与校准）。
        Assert.AreEqual(1000.0, calibrator.Calibrate(ContextCandidateSource.Mandatory, 1000.0), 0.001);
        Assert.AreEqual(42.0, calibrator.Calibrate(ContextCandidateSource.Unknown, 42.0), 0.001);

        // 确定性：同输入两次调用结果一致。
        Assert.AreEqual(
            calibrator.Calibrate(ContextCandidateSource.Semantic, 77.0),
            calibrator.Calibrate(ContextCandidateSource.Semantic, 77.0),
            0.0);
    }

    /// <summary>
    /// 验证：校准后跨通道分数在同一可比刻度上，原始分顺序可以翻转——
    /// 高相似度语义命中（原始 90）高于关键词命中（原始 150），而原始分直接比较会得出相反结论。
    /// </summary>
    [TestMethod]
    public void Calibrator_CrossChannel_CommonScaleChangesRawOrdering()
    {
        var calibrator = new DefaultCandidateScoreCalibrator();

        var lexicalRaw = 150.0;
        var semanticRaw = 90.0;
        var lexicalCalibrated = calibrator.Calibrate(ContextCandidateSource.Lexical, lexicalRaw);
        var semanticCalibrated = calibrator.Calibrate(ContextCandidateSource.Semantic, semanticRaw);

        Assert.IsTrue(lexicalRaw > semanticRaw, "原始分：词法命中高于语义命中。");
        Assert.IsTrue(semanticCalibrated > lexicalCalibrated, "校准分：语义高相似度应高于词法命中（不再直接比较原始分）。");
    }

    // ── Scorer 接入：启用保留原始分，禁用逐位不变 ────────────────────────────

    /// <summary>
    /// 验证：启用开关且注入校准器时，FinalScore 为校准分、原始分保留在
    /// ScoreBreakdown["raw"]、原因码标记为校准路径。
    /// </summary>
    [TestMethod]
    public async Task Scorer_Calibration_Enabled_PreservesRawAndAppliesCalibrated()
    {
        var scorer = new DefaultUtilityScorer(
            new DefaultFeatureSchemaValidator(),
            scoreCalibrator: new DefaultCandidateScoreCalibrator());
        var envelopes = new[]
        {
            MakeEnvelope("lex-only", ContextCandidateSource.Lexical, raw: 150.0),
            MakeEnvelope("sem-only", ContextCandidateSource.Semantic, raw: 85.0)
        };

        var scored = await scorer.ScoreAsync(envelopes, BuildSnapshot(calibrationEnabled: true), CancellationToken.None);

        var lexical = scored.Single(e => e.CanonicalKey.EntityId == "lex-only");
        Assert.AreEqual(85.0, lexical.Utility.FinalScore, 0.001, "词法原始 150 应校准到 85。");
        Assert.AreEqual(150.0, lexical.Features.ScoreBreakdown["raw"], 0.001, "原始分应保留供审计。");
        Assert.AreEqual("calibrated-deterministic", lexical.Utility.ReasonCode);

        var semantic = scored.Single(e => e.CanonicalKey.EntityId == "sem-only");
        Assert.AreEqual(90.0, semantic.Utility.FinalScore, 0.001, "语义原始 85 应校准到 90。");
        Assert.AreEqual(85.0, semantic.Features.ScoreBreakdown["raw"], 0.001);
    }

    /// <summary>
    /// 验证：开关默认关闭（或未注入校准器）时，评分逐位不变（无行为回退）。
    /// </summary>
    [TestMethod]
    public async Task Scorer_Calibration_Disabled_Unchanged()
    {
        var scorer = new DefaultUtilityScorer(
            new DefaultFeatureSchemaValidator(),
            scoreCalibrator: new DefaultCandidateScoreCalibrator());
        var envelope = MakeEnvelope("lex-only", ContextCandidateSource.Lexical, raw: 150.0);

        var scored = await scorer.ScoreAsync([envelope], BuildSnapshot(calibrationEnabled: false), CancellationToken.None);

        var result = scored.Single();
        Assert.AreEqual(150.0, result.Utility.FinalScore, 0.001, "关闭开关时 FinalScore 不变。");
        Assert.IsFalse(result.Features.ScoreBreakdown.ContainsKey("raw"), "关闭开关时不应写入 raw 键。");
        Assert.AreEqual("deterministic-only", result.Utility.ReasonCode);
    }

    // ── 运行时端到端：校准生效 + 单次计算 ─────────────────────────────────────

    /// <summary>
    /// 验证：完整运行时在启用校准后，决策候选携带校准分且原始分可审计；
    /// 同一请求内 policy 只解析一次、feature 只计算一次（每请求单次计算不变量）。
    /// </summary>
    [TestMethod]
    public async Task Runtime_Calibration_AppliesCalibratedScores_SinglePolicyAndFeaturePass()
    {
        var context = new InMemoryContextStore();
        await context.SaveAsync(new ContextItem
        {
            Id = "lex-only",
            WorkspaceId = Ws,
            CollectionId = Col,
            Type = "note",
            Title = "事务调度规则",
            Content = "事务调度 幂等重试 租约续期",
            Metadata = new Dictionary<string, string>
            {
                [ContentMetadataKeys.TsRank] = "150"
            }
        });
        await context.SaveAsync(new ContextItem
        {
            Id = "sem-only",
            WorkspaceId = Ws,
            CollectionId = Col,
            Type = "note",
            Title = "Two-Phase Atomic Commit",
            Content = "protocol design note"
        });

        var vectors = new InMemoryVectorStore();
        await vectors.UpsertAsync(new VectorRecord
        {
            Id = "vec-sem",
            WorkspaceId = Ws,
            CollectionId = Col,
            SourceId = "sem-only",
            SourceKind = "context",
            ModelName = "codebook",
            Dimensions = 64,
            Vector = BasisVector(0)
        });

        var policyProvider = new CountingPolicyProvider(BuildSnapshot(calibrationEnabled: true));
        var featurePipeline = new CountingFeaturePipeline(new DefaultFeaturePipeline());
        var scorer = new DefaultUtilityScorer(
            new DefaultFeatureSchemaValidator(),
            scoreCalibrator: new DefaultCandidateScoreCalibrator());
        var providers = new ICandidateProvider[]
        {
            new LexicalCandidateProvider(context, new DefaultContextTokenizerResolver()),
            new SemanticCandidateProvider(context, memoryStore: null, embeddingProvider: null, vectors, new DefaultContextTokenizerResolver())
        };
        var runtime = BuildRuntime(providers, policyProvider, featurePipeline, scorer);

        var result = await runtime.ExecuteWithWorkingSetAsync(
            new ContextDecisionRuntimeRequest
            {
                RequestId = "req-calibration",
                Scope = new ContextDecisionScope(Ws, Col),
                Purpose = ContextDecisionPurpose.Retrieval,
                QueryText = Q1,
                TopK = 10,
                TokenBudget = 4096,
                RetrievalInput = new RetrievalInput
                {
                    QueryTexts = new[] { Q1 },
                    QueryVector = BasisVector(0),
                    MinVectorScore = 0.5
                }
            }, CancellationToken.None);

        // 每请求只计算一次。
        Assert.AreEqual(1, policyProvider.ResolveCalls, "同一请求内 policy 只解析一次。");
        Assert.AreEqual(1, featurePipeline.EnrichCalls, "同一请求内 feature 只计算一次。");

        // 决策候选携带校准分 + 原始分可审计。
        var selected = result.Decision.SelectedEnvelopes.ToDictionary(e => e.CanonicalKey.EntityId, StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual(85.0, selected["lex-only"].Utility.FinalScore, 0.001, "词法原始 150 校准到 85。");
        Assert.AreEqual(150.0, selected["lex-only"].Features.ScoreBreakdown["raw"], 0.001);
        Assert.AreEqual(90.0, selected["sem-only"].Utility.FinalScore, 0.001, "语义原始 100 校准到 90。");
        Assert.AreEqual(100.0, selected["sem-only"].Features.ScoreBreakdown["raw"], 0.001);
        Assert.IsTrue(
            selected["sem-only"].Utility.FinalScore > selected["lex-only"].Utility.FinalScore,
            "校准后语义高相似度应高于词法命中（不再直接比较原始分）。");
    }

    // ── 构造 ─────────────────────────────────────────────────────────────────

    private static EffectivePolicySnapshot BuildSnapshot(bool calibrationEnabled)
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
            Routing = bundle.Routing with { EnableScoreCalibration = calibrationEnabled },
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

    private static DefaultContextDecisionRuntime BuildRuntime(
        IReadOnlyList<ICandidateProvider> providers,
        IResolvedPolicyProvider policyProvider,
        IFeaturePipeline featurePipeline,
        IUtilityScorer utilityScorer)
    {
        var engine = new DefaultContextDecisionEngine(
            policyRegistry: null,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: utilityScorer,
            globalAllocator: new DefaultGlobalAllocator());

        return new DefaultContextDecisionRuntime(
            engine: engine,
            policyProvider: policyProvider,
            router: new DefaultRouter(new DefaultExpertCatalog()),
            expertCatalog: new DefaultExpertCatalog(),
            candidateProviders: providers,
            canonicalMerger: new DefaultCanonicalCandidateMerger(),
            earlyAdmissionGate: new DefaultEarlyAdmissionGate(),
            featurePipeline: featurePipeline,
            safetyGate: new DefaultSafetyGate(),
            lifecycleGate: new DefaultLifecycleGate(),
            utilityScorer: utilityScorer);
    }

    private static float[] BasisVector(int index)
    {
        var vector = new float[64];
        vector[index] = 1f;
        return vector;
    }

    private sealed class CountingPolicyProvider : IResolvedPolicyProvider
    {
        private readonly EffectivePolicySnapshot _snapshot;

        public CountingPolicyProvider(EffectivePolicySnapshot snapshot) => _snapshot = snapshot;

        public int ResolveCalls { get; private set; }

        public ValueTask<EffectivePolicySnapshot> ResolveAsync(
            ContextDecisionRuntimeRequest request,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return ValueTask.FromResult(_snapshot);
        }
    }

    private sealed class CountingFeaturePipeline : IFeaturePipeline
    {
        private readonly IFeaturePipeline _inner;

        public CountingFeaturePipeline(IFeaturePipeline inner) => _inner = inner;

        public int EnrichCalls { get; private set; }

        public ValueTask<IReadOnlyList<ContextCandidateEnvelope>> EnrichAsync(
            IReadOnlyList<ContextCandidateEnvelope> envelopes,
            FeaturePipelineContext context,
            CancellationToken cancellationToken = default)
        {
            EnrichCalls++;
            return _inner.EnrichAsync(envelopes, context, cancellationToken);
        }
    }
}
