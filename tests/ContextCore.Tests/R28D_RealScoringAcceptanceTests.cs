using System.Diagnostics;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;
using ContextCore.Core.Services.ModelExecution;
using ContextCore.Core.Services.Policy;

namespace ContextCore.Tests;

// ===========================================================================
// R28-D：真实评分能力验收测试
//
// 覆盖范围（DefaultFeaturePipeline + DefaultUtilityScorer 端到端）：
//   §1 DefaultFeaturePipeline —— ScoreBreakdown 提升为强类型特征字段
//   §2 DefaultUtilityScorer   —— rule-only / model-weighted / fallback 三模式
//   §3 端到端集成              —— Enrich → Score 产出模型加权分数
//
// 验收点（对应 R28-D 任务描述）：
//   1. DefaultFeaturePipeline 是真实特征提升（不再是 identity transform）
//   2. DefaultUtilityScorer 是真实评分（不再是 no-op）
//   3. rule-only 模式等价性：FinalScore = DeterministicScore，输入不变
//   4. model-weighted 模式：FinalScore = w_d*Det + w_m*Model，ReasonCode="model-weighted"
//   5. 模型失败降级：推理异常 / Succeeded=false / 缺 schema / 缺依赖 → 回退 deterministic
//   6. 低置信度降级：confidence < threshold → FinalScore=Det，ReasonCode="fallback-to-deterministic"
//   7. 校准应用：calibration service 非空时使用校准后分数
//
// 设计原则：
//   - 优先使用真实默认实现（DeterministicBatchInferenceEngine / DefaultFeatureRegistry / PlattCalibrationService）
//   - 仅在需要控制推理输出 / 注入异常时使用手写 Stub
//   - 所有异步测试使用超时 CancellationTokenSource 防止挂起
// ===========================================================================

// ===========================================================================
// §1 DefaultFeaturePipeline 特征提升测试
// ===========================================================================

[TestClass]
[TestCategory("R28-D")]
public sealed class R28D_DefaultFeaturePipelineTests
{
    [TestMethod]
    public async Task Enrich_PromotesScoreBreakdown_ToStrongTypedFields()
    {
        var pipeline = new DefaultFeaturePipeline();
        var envelope = R28DTestHelpers.MakeEnvelope("c1", breakdown: new Dictionary<string, double>
        {
            ["rawTokenMatch"] = 0.85,
            ["semanticAnchor"] = 0.72,
            ["recency"] = 0.30,
            ["relation"] = 0.15
        });

        var result = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);

        Assert.AreEqual(1, result.Count);
        var features = result[0].Features;
        Assert.AreEqual(0.85, features.LexicalScore, 1e-12);
        Assert.AreEqual(0.72, features.SemanticScore, 1e-12);
        Assert.AreEqual(0.30, features.RecencyScore, 1e-12);
        Assert.AreEqual(0.15, features.RelationBoost, 1e-12);
    }

    [TestMethod]
    public async Task Enrich_AcceptsAlternativeBreakdownKeys()
    {
        // adapter 可能使用 "lexical" / "semantic" / "relation_boost" 别名
        var pipeline = new DefaultFeaturePipeline();
        var envelope = R28DTestHelpers.MakeEnvelope("c1", breakdown: new Dictionary<string, double>
        {
            ["lexical"] = 0.50,
            ["semantic"] = 0.40,
            ["relation_boost"] = 0.20
        });

        var result = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);

        Assert.AreEqual(0.50, result[0].Features.LexicalScore, 1e-12);
        Assert.AreEqual(0.40, result[0].Features.SemanticScore, 1e-12);
        Assert.AreEqual(0.20, result[0].Features.RelationBoost, 1e-12);
    }

    [TestMethod]
    public async Task Enrich_MandatoryCandidate_SetsMandatoryWeight()
    {
        var pipeline = new DefaultFeaturePipeline();
        var envelope = R28DTestHelpers.MakeEnvelope("c1", safety: new CandidateSafetyState { IsMandatory = true });

        var result = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);

        Assert.AreEqual(1.0, result[0].Features.MandatoryWeight, 1e-12);
    }

    [TestMethod]
    public async Task Enrich_NonMandatoryCandidate_HasZeroMandatoryWeight()
    {
        var pipeline = new DefaultFeaturePipeline();
        var envelope = R28DTestHelpers.MakeEnvelope("c1");

        var result = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);

        Assert.AreEqual(0.0, result[0].Features.MandatoryWeight, 1e-12);
    }

    [TestMethod]
    public async Task Enrich_AlreadyPopulatedFields_NotOverwritten()
    {
        // 强类型字段已非零时不应被 breakdown 覆盖
        var pipeline = new DefaultFeaturePipeline();
        var envelope = R28DTestHelpers.MakeEnvelope("c1");
        envelope = envelope with
        {
            Features = envelope.Features with
            {
                LexicalScore = 0.99,
                SemanticScore = 0.88,
                RecencyScore = 0.77,
                RelationBoost = 0.66,
                MandatoryWeight = 1.0,
                ScoreBreakdown = new Dictionary<string, double>
                {
                    ["rawTokenMatch"] = 0.10,  // 应被忽略（强类型字段已填充）
                    ["semanticAnchor"] = 0.20
                }
            }
        };

        var result = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);

        Assert.AreEqual(0.99, result[0].Features.LexicalScore, 1e-12);
        Assert.AreEqual(0.88, result[0].Features.SemanticScore, 1e-12);
        Assert.AreEqual(0.77, result[0].Features.RecencyScore, 1e-12);
        Assert.AreEqual(0.66, result[0].Features.RelationBoost, 1e-12);
        Assert.AreEqual(1.0, result[0].Features.MandatoryWeight, 1e-12);
    }

    [TestMethod]
    public async Task Enrich_EmptyInput_ReturnsEmpty()
    {
        var pipeline = new DefaultFeaturePipeline();
        var result = await pipeline.EnrichAsync(Array.Empty<ContextCandidateEnvelope>(), R28DTestHelpers.BuildContext(), default);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Enrich_NullEnvelopes_Throws()
    {
        var pipeline = new DefaultFeaturePipeline();
        Assert.ThrowsException<ArgumentNullException>(() =>
            pipeline.EnrichAsync(null!, R28DTestHelpers.BuildContext(), default).AsTask());
    }

    [TestMethod]
    public void Enrich_NullContext_Throws()
    {
        var pipeline = new DefaultFeaturePipeline();
        Assert.ThrowsException<ArgumentNullException>(() =>
            pipeline.EnrichAsync(Array.Empty<ContextCandidateEnvelope>(), null!, default).AsTask());
    }

    [TestMethod]
    public async Task Enrich_CancellationRequested_Throws()
    {
        var pipeline = new DefaultFeaturePipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var envelope = R28DTestHelpers.MakeEnvelope("c1");

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), cts.Token));
    }
}

// ===========================================================================
// §2 DefaultUtilityScorer 评分模式测试
// ===========================================================================

[TestClass]
[TestCategory("R28-D")]
public sealed class R28D_DefaultUtilityScorerTests
{
    // -------------------------------------------------------------------------
    // rule-only 模式（EnableModelScoring=false，默认）
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Score_RuleOnlyMode_ReturnsEnvelopesUnchanged()
    {
        // 无推理引擎、无 registry → 即便 EnableModelScoring=true 也回退；
        // 默认 snapshot EnableModelScoring=false → 直接返回不变
        var scorer = new DefaultUtilityScorer();
        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.7);
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: false);

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, result.Count);
        // rule-only 模式 FinalScore 应保持 DeterministicScore 不变
        Assert.AreEqual(0.7, result[0].Utility.FinalScore, 1e-12);
        Assert.AreEqual("deterministic-only", result[0].Utility.ReasonCode);
        Assert.IsNull(result[0].Utility.ModelScore);
    }

    // -------------------------------------------------------------------------
    // model-weighted 模式（高置信度）
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Score_ModelWeighted_ComputesWeightedFinalScore()
    {
        // w_d=0.6, w_m=0.4, Det=0.5, Model=0.8, confidence=0.95（高于阈值 0.70）
        // FinalScore = 0.6*0.5 + 0.4*0.8 = 0.30 + 0.32 = 0.62
        var engine = new StubBatchInferenceEngine(modelVersion: "test-model-v1")
            .WithOutput(score: 0.8, confidence: 0.95);
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, calibrationService: null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.6,
            modelWeight: 0.4,
            modelArtifactId: "test-model-v1",
            featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, result.Count);
        var utility = result[0].Utility;
        Assert.AreEqual(0.8, utility.ModelScore!.Value, 1e-12);
        Assert.AreEqual(0.95, utility.ModelConfidence, 1e-12);
        Assert.AreEqual(0.62, utility.FinalScore, 1e-9);
        Assert.AreEqual("model-weighted", utility.ReasonCode);
        Assert.AreEqual("test-model-v1", utility.ModelArtifactRef);
    }

    [TestMethod]
    public async Task Score_ModelWeighted_PreservesOrderAndCount()
    {
        var engine = new StubBatchInferenceEngine("test-model-v1")
            .WithOutput(0.8, 0.95)
            .WithOutput(0.6, 0.90)
            .WithOutput(0.4, 0.85);
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelopes = new[]
        {
            R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5),
            R28DTestHelpers.MakeEnvelope("c2", detScore: 0.3),
            R28DTestHelpers.MakeEnvelope("c3", detScore: 0.1)
        };
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: true, deterministicWeight: 1.0, modelWeight: 0.0, featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(envelopes, snapshot, default);

        Assert.AreEqual(3, result.Count);
        Assert.AreEqual("c1", result[0].CandidateId);
        Assert.AreEqual("c2", result[1].CandidateId);
        Assert.AreEqual("c3", result[2].CandidateId);
        Assert.AreEqual(0.8, result[0].Utility.ModelScore!.Value, 1e-12);
        Assert.AreEqual(0.6, result[1].Utility.ModelScore!.Value, 1e-12);
        Assert.AreEqual(0.4, result[2].Utility.ModelScore!.Value, 1e-12);
    }

    // -------------------------------------------------------------------------
    // 低置信度降级
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Score_LowConfidence_FallsBackToDeterministic()
    {
        // confidence=0.50 < threshold=0.70 → FinalScore=Det, ReasonCode="fallback-to-deterministic"
        var engine = new StubBatchInferenceEngine("test-model-v1")
            .WithOutput(score: 0.9, confidence: 0.50);
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.4);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.6,
            modelWeight: 0.4,
            confidenceThreshold: 0.70,
            featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        var utility = result[0].Utility;
        Assert.AreEqual(0.9, utility.ModelScore!.Value, 1e-12);
        Assert.AreEqual(0.50, utility.ModelConfidence, 1e-12);
        Assert.AreEqual(0.4, utility.FinalScore, 1e-12); // 回退到 Det
        Assert.AreEqual("fallback-to-deterministic", utility.ReasonCode);
    }

    [TestMethod]
    public async Task Score_ConfidenceEqualsThreshold_UsesModelWeighted()
    {
        // confidence == threshold（边界）：不满足 < threshold，应走 model-weighted
        var engine = new StubBatchInferenceEngine("test-model-v1")
            .WithOutput(score: 0.8, confidence: 0.70);
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.5,
            modelWeight: 0.5,
            confidenceThreshold: 0.70,
            featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual("model-weighted", result[0].Utility.ReasonCode);
        // FinalScore = 0.5*0.5 + 0.5*0.8 = 0.25 + 0.40 = 0.65
        Assert.AreEqual(0.65, result[0].Utility.FinalScore, 1e-9);
    }

    // -------------------------------------------------------------------------
    // 模型失败降级（fail-open）
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Score_InferenceThrows_ReturnsEnvelopesUnchanged()
    {
        var engine = new StubBatchInferenceEngine("test-model-v1").WithThrow(new InvalidOperationException("model down"));
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: true, featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        // fail-open：异常被吞，返回原 envelope（ModelScore 保持 null）
        Assert.AreEqual(1, result.Count);
        Assert.IsNull(result[0].Utility.ModelScore);
        // P0-1：模型已尝试但失败 → 标记 fallback-to-deterministic 并记录原因
        Assert.IsTrue(result[0].Utility.ModelAttempted, "ModelAttempted 应为 true（模型已尝试）");
        Assert.IsFalse(result[0].Utility.ModelApplied, "ModelApplied 应为 false（模型未实际应用）");
        Assert.AreEqual("inference-failed", result[0].Utility.ModelFallbackReason);
        Assert.AreEqual("fallback-to-deterministic", result[0].Utility.ReasonCode);
    }

    [TestMethod]
    public async Task Score_InferenceSucceededFalse_ReturnsEnvelopesUnchanged()
    {
        var engine = new StubBatchInferenceEngine("test-model-v1").WithFailure("timeout");
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: true, featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, result.Count);
        Assert.IsNull(result[0].Utility.ModelScore);
        // P0-1：模型已尝试但返回 Succeeded=false → 标记 fallback-to-deterministic 并记录原因
        Assert.IsTrue(result[0].Utility.ModelAttempted, "ModelAttempted 应为 true（模型已尝试）");
        Assert.IsFalse(result[0].Utility.ModelApplied, "ModelApplied 应为 false（模型未实际应用）");
        Assert.AreEqual("inference-succeeded-false", result[0].Utility.ModelFallbackReason);
        Assert.AreEqual("fallback-to-deterministic", result[0].Utility.ReasonCode);
    }

    [TestMethod]
    public async Task Score_MissingSchema_ReturnsEnvelopesUnchanged()
    {
        // R28-F P3-1：Scorer 按 snapshot.FeatureSchemaVersion 解析 schema（不再用 engine.ModelVersion）。
        // registry 中无 snapshot.FeatureSchemaVersion 对应的 schema → 标记降级。
        var engine = new StubBatchInferenceEngine("unknown-model-v1").WithOutput(0.8, 0.95);
        var registry = R28DTestHelpers.BuildRegistryWithSchema("different-model-v1");
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: true, featureSchemaVersion: "nonexistent-schema");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, result.Count);
        Assert.IsNull(result[0].Utility.ModelScore);
        Assert.IsTrue(result[0].Utility.ModelAttempted, "ModelAttempted 应为 true（模型已尝试）");
        Assert.IsFalse(result[0].Utility.ModelApplied, "ModelApplied 应为 false（schema 未找到）");
        Assert.AreEqual("schema-not-found", result[0].Utility.ModelFallbackReason);
    }

    [TestMethod]
    public async Task Score_MissingEngineAndRegistry_ReturnsEnvelopesUnchanged()
    {
        // null 依赖 → 即便 EnableModelScoring=true 也回退 rule-only
        var scorer = new DefaultUtilityScorer(inferenceEngine: null, calibrationService: null, featureRegistry: null);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: true);

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        Assert.AreEqual(1, result.Count);
        Assert.IsNull(result[0].Utility.ModelScore);
    }

    // -------------------------------------------------------------------------
    // 校准应用
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Score_CalibrationApplied_UsesCalibratedScore()
    {
        // R28-F P3-3：默认 calibration 现在是 Identity（raw 原样返回）。
        // 要复用旧 sigmoid 语义，需显式注册 Platt(A=1, B=0) 参数。
        // raw=0.8 → sigmoid(0.8) = 1/(1+e^-0.8) ≈ 0.689974
        var engine = new StubBatchInferenceEngine("test-model-v1")
            .WithOutput(score: 0.8, confidence: 0.95);
        var registry = R28DTestHelpers.BuildRegistryWithSchema("test-model-v1");
        var calibration = new PlattCalibrationService();
        calibration.RegisterPlattParameters(a: 1.0, b: 0.0, modelName: "test-model-v1");
        var scorer = new DefaultUtilityScorer(engine, calibration, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.5);
        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.5,
            modelWeight: 0.5,
            featureSchemaVersion: "test-model-v1");

        var result = await scorer.ScoreAsync(new[] { envelope }, snapshot, default);

        var utility = result[0].Utility;
        var expectedCalibrated = 1.0 / (1.0 + Math.Exp(-0.8));
        Assert.AreEqual(expectedCalibrated, utility.ModelScore!.Value, 1e-9);
        Assert.AreEqual("model-weighted", utility.ReasonCode);
        // FinalScore = 0.5*0.5 + 0.5*calibrated
        Assert.AreEqual(0.5 * 0.5 + 0.5 * expectedCalibrated, utility.FinalScore, 1e-9);
    }

    // -------------------------------------------------------------------------
    // 边界 / 异常
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Score_EmptyInput_ReturnsEmpty()
    {
        var scorer = new DefaultUtilityScorer();
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: false);
        var result = await scorer.ScoreAsync(Array.Empty<ContextCandidateEnvelope>(), snapshot, default);
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task Score_NullEnvelopes_Throws()
    {
        var scorer = new DefaultUtilityScorer();
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: false);
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            scorer.ScoreAsync(null!, snapshot, default).AsTask());
    }

    [TestMethod]
    public async Task Score_NullSnapshot_Throws()
    {
        var scorer = new DefaultUtilityScorer();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            scorer.ScoreAsync(Array.Empty<ContextCandidateEnvelope>(), null!, default).AsTask());
    }
}

// ===========================================================================
// §3 端到端集成：Enrich → Score
// ===========================================================================

[TestClass]
[TestCategory("R28-D")]
public sealed class R28D_EndToEndIntegrationTests
{
    [TestMethod]
    public async Task EnrichThenScore_WithDeterministicEngine_ProducesModelWeightedScores()
    {
        // 端到端：FeaturePipeline 提升 ScoreBreakdown → UtilityScorer 用真实 Deterministic 引擎推理
        var pipeline = new DefaultFeaturePipeline();
        var engine = new DeterministicBatchInferenceEngine();
        var registry = R28DTestHelpers.BuildRegistryWithSchema(engine.ModelVersion);
        var scorer = new DefaultUtilityScorer(engine, calibrationService: null, registry);

        var envelopes = new[]
        {
            R28DTestHelpers.MakeEnvelope("c1", breakdown: new Dictionary<string, double>
            {
                ["rawTokenMatch"] = 0.9,
                ["semanticAnchor"] = 0.7
            }, detScore: 0.5),
            R28DTestHelpers.MakeEnvelope("c2", breakdown: new Dictionary<string, double>
            {
                ["rawTokenMatch"] = 0.3,
                ["semanticAnchor"] = 0.2
            }, detScore: 0.3)
        };

        var snapshot = R28DTestHelpers.BuildSnapshot(
            enableModelScoring: true,
            deterministicWeight: 0.5,
            modelWeight: 0.5,
            modelArtifactId: engine.ModelVersion,
            confidenceThreshold: 0.0, // Deterministic 引擎的 confidence 可能低于默认 0.70，置 0 确保 model-weighted 路径
            allowDeterministicReplayScoring: true, // R28-D P0-1：显式允许 DeterministicReplay 参与评分（测试/预览场景）
            featureSchemaVersion: engine.ModelVersion);

        // 阶段 1：特征提升
        var enriched = await pipeline.EnrichAsync(envelopes, R28DTestHelpers.BuildContext(), default);
        Assert.AreEqual(0.9, enriched[0].Features.LexicalScore, 1e-12);
        Assert.AreEqual(0.7, enriched[0].Features.SemanticScore, 1e-12);
        Assert.AreEqual(0.3, enriched[1].Features.LexicalScore, 1e-12);
        Assert.AreEqual(0.2, enriched[1].Features.SemanticScore, 1e-12);

        // 阶段 2：模型评分
        var scored = await scorer.ScoreAsync(enriched, snapshot, default);
        Assert.AreEqual(2, scored.Count);

        // 每个候选都应被模型评分填充
        foreach (var e in scored)
        {
            Assert.IsNotNull(e.Utility.ModelScore, $"{e.CandidateId} 应有 ModelScore");
            Assert.AreEqual("model-weighted", e.Utility.ReasonCode);
            Assert.AreEqual(engine.ModelVersion, e.Utility.ModelArtifactRef);
        }

        // 不同特征应产出不同模型分数（验证特征向量确实被消费）
        Assert.AreNotEqual(scored[0].Utility.ModelScore!.Value, scored[1].Utility.ModelScore!.Value, 1e-12);
    }

    [TestMethod]
    public async Task EnrichThenScore_RuleOnlyMode_PreservesDeterministicScore()
    {
        // rule-only 模式：Enrich 提升特征但 Score 不调用模型，FinalScore 保持 DeterministicScore
        var pipeline = new DefaultFeaturePipeline();
        var engine = new DeterministicBatchInferenceEngine();
        var registry = R28DTestHelpers.BuildRegistryWithSchema(engine.ModelVersion);
        var scorer = new DefaultUtilityScorer(engine, null, registry);

        var envelope = R28DTestHelpers.MakeEnvelope("c1", detScore: 0.42, breakdown: new Dictionary<string, double>
        {
            ["rawTokenMatch"] = 0.9
        });
        var snapshot = R28DTestHelpers.BuildSnapshot(enableModelScoring: false);

        var enriched = await pipeline.EnrichAsync(new[] { envelope }, R28DTestHelpers.BuildContext(), default);
        var scored = await scorer.ScoreAsync(enriched, snapshot, default);

        Assert.AreEqual(0.42, scored[0].Utility.FinalScore, 1e-12);
        Assert.IsNull(scored[0].Utility.ModelScore);
        Assert.AreEqual("deterministic-only", scored[0].Utility.ReasonCode);
        // 特征仍被提升（供 trace）
        Assert.AreEqual(0.9, scored[0].Features.LexicalScore, 1e-12);
    }
}

// ===========================================================================
// 测试辅助
// ===========================================================================

internal static class R28DTestHelpers
{
    public static ContextCandidateEnvelope MakeEnvelope(
        string candidateId,
        double detScore = 0.0,
        IReadOnlyDictionary<string, double>? breakdown = null,
        CandidateSafetyState? safety = null)
    {
        return new ContextCandidateEnvelope
        {
            CandidateId = candidateId,
            CanonicalKey = CanonicalCandidateKey.Create(
                workspaceId: "test-ws",
                collectionId: "test-col",
                entityKind: "test-entity",
                entityId: candidateId,
                entityVersion: "v1"),
            Source = ContextCandidateSource.Semantic,
            Type = "test-type",
            EstimatedTokens = 100,
            Safety = safety ?? new CandidateSafetyState(),
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = detScore,
                FinalScore = detScore,
                ReasonCode = "deterministic-only"
            },
            Features = new CandidateFeatureVector
            {
                ScoreBreakdown = breakdown ?? new Dictionary<string, double>()
            }
        };
    }

    public static FeaturePipelineContext BuildContext()
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        return new FeaturePipelineContext(
            Policy: BuildSnapshot(enableModelScoring: false),
            AdaptationContext: new CandidateAdaptationContext
            {
                WorkspaceId = "test-ws",
                CollectionId = "test-col",
                ObservedAt = DateTimeOffset.UtcNow
            });
    }

    public static EffectivePolicySnapshot BuildSnapshot(
        bool enableModelScoring = false,
        double deterministicWeight = 1.0,
        double modelWeight = 0.0,
        double confidenceThreshold = 0.70,
        string? modelArtifactId = null,
        bool allowDeterministicReplayScoring = false,
        string? featureSchemaVersion = null)
    {
        var bundle = DefaultPolicyBundleFactory.Create();
        var routing = bundle.Routing with
        {
            EnableModelScoring = enableModelScoring,
            DeterministicWeight = deterministicWeight,
            ModelWeight = modelWeight,
            ModelConfidenceThreshold = confidenceThreshold,
            ModelArtifactId = modelArtifactId
        };
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
            Routing = routing,
            // R28-F P3-1：FeatureSchemaVersion 可显式传入（默认使用 bundle 的 DecisionSchemaVersion）。
            // 测试中应与 BuildRegistryWithSchema 的 schemaVersion 参数保持一致。
            FeatureSchemaVersion = featureSchemaVersion ?? bundle.Policies.DecisionSchemaVersion,
            ResolutionScope = new ContextDecisionScope("test-ws", "test-col"),
            AllowDeterministicReplayScoring = allowDeterministicReplayScoring
        };
    }

    public static IFeatureRegistry BuildRegistryWithSchema(string schemaVersion)
    {
        var registry = new DefaultFeatureRegistry();
        registry.Register(new FeatureSchema
        {
            Version = schemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Features = new[]
            {
                new FeatureDefinition { Name = "lexical_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "semantic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "recency_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "relation_boost", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "mandatory_weight", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" },
                new FeatureDefinition { Name = "deterministic_score", Type = FeatureType.Numeric, IsRequired = false, DefaultValue = "0" }
            }
        });
        return registry;
    }

    /// <summary>
    /// R28-F P3-1：构造 registry + snapshot 配对（同 schemaVersion），保证两者对齐。
    /// 旧测试调用 BuildRegistryWithSchema("test-model-v1") + BuildSnapshot() 会导致
    /// registry 的 schema 版本与 snapshot.FeatureSchemaVersion 不一致（前者 "test-model-v1"，
    /// 后者 bundle.Policies.DecisionSchemaVersion）。新测试应使用此方法避免不一致。
    /// </summary>
    public static (IFeatureRegistry registry, EffectivePolicySnapshot snapshot) BuildRegistryAndSnapshot(
        string schemaVersion,
        bool enableModelScoring = true,
        double deterministicWeight = 0.5,
        double modelWeight = 0.5,
        double confidenceThreshold = 0.70,
        string? modelArtifactId = null,
        bool allowDeterministicReplayScoring = false)
    {
        return (
            BuildRegistryWithSchema(schemaVersion),
            BuildSnapshot(
                enableModelScoring: enableModelScoring,
                deterministicWeight: deterministicWeight,
                modelWeight: modelWeight,
                confidenceThreshold: confidenceThreshold,
                modelArtifactId: modelArtifactId,
                allowDeterministicReplayScoring: allowDeterministicReplayScoring,
                featureSchemaVersion: schemaVersion));
    }
}

// ===========================================================================
// 可控 Stub 推理引擎
// ===========================================================================

/// <summary>
/// 可配置的批量推理引擎 Stub：按预设输出返回，或抛异常 / 报告失败。
/// </summary>
internal sealed class StubBatchInferenceEngine : IBatchInferenceEngine
{
    private readonly List<InferenceOutput> _outputs = new();
    private Exception? _throw;
    private string? _failureError;

    public StubBatchInferenceEngine(string modelVersion)
    {
        ModelVersion = modelVersion;
    }

    public string ModelVersion { get; }

    // R28-D P0-1：Stub 默认为 RealModel，让测试可验证 model-weighted 路径
    public InferenceEngineKind Kind { get; set; } = InferenceEngineKind.RealModel;

    // R28-F P3-1：Stub 默认 ContentHash/CalibrationVersion（测试可控）
    public string ContentHash { get; set; } = "stub-content-hash";
    public string CalibrationVersion { get; set; } = "stub-calibration-v1";

    public StubBatchInferenceEngine WithOutput(double score, double confidence)
    {
        _outputs.Add(new InferenceOutput { Score = score, Confidence = confidence });
        return this;
    }

    public StubBatchInferenceEngine WithThrow(Exception ex)
    {
        _throw = ex;
        return this;
    }

    public StubBatchInferenceEngine WithFailure(string error)
    {
        _failureError = error;
        return this;
    }

    public ValueTask<BatchInferenceResult> InferAsync(BatchInferenceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startedAt = Stopwatch.GetTimestamp();
        if (ct.IsCancellationRequested)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "cancelled",
                Duration = TimeSpan.Zero
            });
        }

        if (_throw is not null)
        {
            throw _throw;
        }

        if (_failureError is not null)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = _failureError,
                Duration = TimeSpan.Zero
            });
        }

        // 按输入数量填充输出（若预设不足则补齐默认值）
        var outputs = new InferenceOutput[request.Inputs.Count];
        for (var i = 0; i < request.Inputs.Count; i++)
        {
            outputs[i] = i < _outputs.Count
                ? _outputs[i]
                : new InferenceOutput { Score = 0.0, Confidence = 0.0 };
        }

        // R28-F P3-2：返回真实执行时间（非零），避免推理验证器误判 timeout 未执行。
        return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
        {
            Outputs = outputs,
            Succeeded = true,
            Error = null,
            Duration = Stopwatch.GetElapsedTime(startedAt)
        });
    }

    // R28-F P4-1：Stub 的 FeatureBatch 路径直接复用 InferAsync 的输出策略
    public ValueTask<BatchInferenceResult> InferBatchAsync(FeatureBatch batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var startedAt = Stopwatch.GetTimestamp();
        if (ct.IsCancellationRequested)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = "cancelled",
                Duration = TimeSpan.Zero
            });
        }

        if (_throw is not null)
        {
            throw _throw;
        }

        if (_failureError is not null)
        {
            return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
            {
                Outputs = Array.Empty<InferenceOutput>(),
                Succeeded = false,
                Error = _failureError,
                Duration = TimeSpan.Zero
            });
        }

        // 按行数填充输出
        var outputs = new InferenceOutput[batch.RowCount];
        for (var i = 0; i < batch.RowCount; i++)
        {
            outputs[i] = i < _outputs.Count
                ? _outputs[i]
                : new InferenceOutput { Score = 0.0, Confidence = 0.0 };
        }

        return new ValueTask<BatchInferenceResult>(new BatchInferenceResult
        {
            Outputs = outputs,
            Succeeded = true,
            Error = null,
            Duration = Stopwatch.GetElapsedTime(startedAt)
        });
    }
}
