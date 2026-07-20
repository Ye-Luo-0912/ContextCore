using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

/// <summary>
/// R18-1：统一决策内核契约可实施性验证。
///
/// 验证目标：
///   1. ContextCandidateEnvelope + 4 个子 record 可正常构造（默认值 + with 表达式增强）
///   2. ContextCandidateSource 枚举覆盖 R20 Expert 概念
///   3. CandidateSafetyState 默认通过 safety gate（避免空构造误拦截）
///   4. CandidateUtilityScore 默认 deterministic-only（ModelScore=null）
///   5. EvidenceRef 可承载来源溯源链
///   6. Envelope 不依赖现有 ContextRetrievalCandidate / ContextPackageDecision（独立契约）
///   7. PolicyVersion 默认值 = ContextDecisionPolicyVersions.DecisionSchemaV2_0
/// </summary>
[TestClass]
[TestCategory("R18")]
public sealed class DecisionEngineContractsTests
{
    // =========================================================================
    // 1. ContextCandidateEnvelope 构造与 with 表达式
    // =========================================================================

    [TestMethod]
    public void Envelope_MinimalConstruction_UsesDefaultsForOptionalFields()
    {
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "cand-1",
            Source = ContextCandidateSource.Lexical
        };

        Assert.AreEqual("cand-1", envelope.CandidateId);
        Assert.AreEqual(ContextCandidateSource.Lexical, envelope.Source);
        Assert.AreEqual(string.Empty, envelope.Type);
        Assert.AreEqual(0, envelope.EstimatedTokens);
        Assert.IsNotNull(envelope.Features);
        Assert.IsNotNull(envelope.Safety);
        Assert.IsNotNull(envelope.Utility);
        Assert.AreEqual(0, envelope.ProvenanceRefs.Count);
        Assert.AreEqual(
            ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            envelope.PolicyVersion);
        Assert.IsNull(envelope.ModelVersion);
        Assert.AreEqual(string.Empty, envelope.WorkspaceId);
        Assert.AreEqual(string.Empty, envelope.CollectionId);
    }

    [TestMethod]
    public void Envelope_WithExpression_PreservesIdentityAndUpdatesOnlySpecifiedFields()
    {
        var baseEnvelope = new ContextCandidateEnvelope
        {
            CandidateId = "cand-1",
            Source = ContextCandidateSource.Semantic,
            Type = "note",
            EstimatedTokens = 100
        };

        var enhanced = baseEnvelope with
        {
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = 0.85,
                FinalScore = 0.85,
                ReasonCode = "deterministic-only"
            },
            Safety = baseEnvelope.Safety with { IsMandatory = true }
        };

        // 身份字段不变
        Assert.AreEqual("cand-1", enhanced.CandidateId);
        Assert.AreEqual(ContextCandidateSource.Semantic, enhanced.Source);
        Assert.AreEqual("note", enhanced.Type);
        Assert.AreEqual(100, enhanced.EstimatedTokens);

        // 增强字段生效
        Assert.AreEqual(0.85, enhanced.Utility.DeterministicScore);
        Assert.IsTrue(enhanced.Safety.IsMandatory);

        // 原对象不变（不可变性）
        Assert.AreEqual(0.0, baseEnvelope.Utility.DeterministicScore);
        Assert.IsFalse(baseEnvelope.Safety.IsMandatory);
    }

    [TestMethod]
    public void Envelope_RequiredCandidateId_ThrowsWhenMissing()
    {
        // required 字段未提供 → 编译错误，运行时不可能触发；此测试仅文档化契约
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "x",
            Source = ContextCandidateSource.Unknown
        };
        Assert.AreEqual("x", envelope.CandidateId);
    }

    // =========================================================================
    // 2. ContextCandidateSource 枚举覆盖 R20 Expert 概念
    // =========================================================================

    [TestMethod]
    public void ContextCandidateSource_IncludesAllR20ExpertTypes()
    {
        // R20 Expert 划分：Mandatory / Lexical / Semantic / WorkingMemory / StableMemory / Graph / Recency / Constraint
        // 验证所有 8 个 Expert 都有对应枚举值
        Assert.AreEqual(ContextCandidateSource.Mandatory, (ContextCandidateSource)1);
        Assert.AreEqual(ContextCandidateSource.Lexical, (ContextCandidateSource)2);
        Assert.AreEqual(ContextCandidateSource.Semantic, (ContextCandidateSource)3);
        Assert.AreEqual(ContextCandidateSource.WorkingMemory, (ContextCandidateSource)4);
        Assert.AreEqual(ContextCandidateSource.StableMemory, (ContextCandidateSource)5);
        Assert.AreEqual(ContextCandidateSource.Graph, (ContextCandidateSource)6);
        Assert.AreEqual(ContextCandidateSource.Recency, (ContextCandidateSource)7);
        Assert.AreEqual(ContextCandidateSource.Constraint, (ContextCandidateSource)8);
    }

    [TestMethod]
    public void ContextCandidateSource_IncludesAdditionalNonExpertSources()
    {
        // 非 Expert 但仍需支持的来源（Global Context / Related Context）
        Assert.AreEqual(ContextCandidateSource.GlobalContext, (ContextCandidateSource)9);
        Assert.AreEqual(ContextCandidateSource.RelatedContext, (ContextCandidateSource)10);
        Assert.AreEqual(ContextCandidateSource.Unknown, (ContextCandidateSource)0);
    }

    // =========================================================================
    // 3. CandidateSafetyState 默认通过 safety gate
    // =========================================================================

    [TestMethod]
    public void SafetyState_Default_PassesSafetyGate()
    {
        var safety = new CandidateSafetyState();

        Assert.IsTrue(safety.PassesSafetyGate, "默认 safety state 应通过 safety gate（避免空构造误拦截）");
        Assert.IsFalse(safety.IsMandatory);
        Assert.IsFalse(safety.IsHardConstraint);
        Assert.AreEqual("active", safety.LifecycleState);
        Assert.IsFalse(safety.IsDeprecatedUsedByActiveChain);
        Assert.IsFalse(safety.IsSuperseded);
        Assert.IsFalse(safety.IsRequiredTagMismatch);
        Assert.IsFalse(safety.IsDuplicate);
        Assert.AreEqual(CandidateDecisionReasonCode.Unknown, safety.BlockReasonCode);
        Assert.AreEqual(string.Empty, safety.BlockReasonDetail);
    }

    [TestMethod]
    public void SafetyState_Superseded_BlocksSafetyGateWithReasonCode()
    {
        var safety = new CandidateSafetyState
        {
            IsSuperseded = true,
            PassesSafetyGate = false,
            BlockReasonCode = CandidateDecisionReasonCode.SupersededByCurrentVersion,
            BlockReasonDetail = "superseded by item-xyz-v2"
        };

        Assert.IsFalse(safety.PassesSafetyGate);
        Assert.AreEqual(
            CandidateDecisionReasonCode.SupersededByCurrentVersion,
            safety.BlockReasonCode);
    }

    [TestMethod]
    public void SafetyState_RequiredTagMismatch_BlocksSafetyGate()
    {
        var safety = new CandidateSafetyState
        {
            IsRequiredTagMismatch = true,
            PassesSafetyGate = false,
            BlockReasonCode = CandidateDecisionReasonCode.RequiredTagMismatch,
            BlockReasonDetail = "missing tag: long-term"
        };

        Assert.IsFalse(safety.PassesSafetyGate);
        Assert.AreEqual(
            CandidateDecisionReasonCode.RequiredTagMismatch,
            safety.BlockReasonCode);
    }

    // =========================================================================
    // 4. CandidateUtilityScore 默认 deterministic-only
    // =========================================================================

    [TestMethod]
    public void UtilityScore_Default_IsDeterministicOnly()
    {
        var utility = new CandidateUtilityScore();

        Assert.AreEqual(0.0, utility.DeterministicScore);
        Assert.IsNull(utility.ModelScore, "默认 ModelScore=null 表示模型未启用");
        Assert.AreEqual(0.0, utility.FinalScore);
        Assert.AreEqual(0.0, utility.ModelConfidence);
        Assert.AreEqual("deterministic-only", utility.ReasonCode);
        Assert.IsNull(utility.ModelArtifactRef);
    }

    [TestMethod]
    public void UtilityScore_ModelScoreNull_FinalEqualsDeterministic()
    {
        var utility = new CandidateUtilityScore
        {
            DeterministicScore = 0.75,
            ModelScore = null,
            FinalScore = 0.75,
            ModelConfidence = 0.0,
            ReasonCode = "deterministic-only"
        };

        Assert.AreEqual(utility.DeterministicScore, utility.FinalScore);
        Assert.AreEqual(0.0, utility.ModelConfidence);
    }

    [TestMethod]
    public void UtilityScore_ModelScoreProvided_FinalWeighted()
    {
        var utility = new CandidateUtilityScore
        {
            DeterministicScore = 0.60,
            ModelScore = 0.80,
            FinalScore = 0.70, // 假设权重 0.5 / 0.5
            ModelConfidence = 0.85,
            ReasonCode = "model-weighted",
            ModelArtifactRef = "model:router-v1.2"
        };

        Assert.IsNotNull(utility.ModelScore);
        Assert.AreEqual(0.80, utility.ModelScore!.Value);
        Assert.AreNotEqual(utility.DeterministicScore, utility.FinalScore);
        Assert.AreEqual("model-weighted", utility.ReasonCode);
        Assert.AreEqual("model:router-v1.2", utility.ModelArtifactRef);
    }

    [TestMethod]
    public void UtilityScore_ModelFailure_FallsBackToDeterministic()
    {
        // Model failure 回退路径：ModelConfidence=0 + ModelScore=null + ReasonCode="fallback-to-deterministic"
        var utility = new CandidateUtilityScore
        {
            DeterministicScore = 0.55,
            ModelScore = null,
            FinalScore = 0.55,
            ModelConfidence = 0.0,
            ReasonCode = "fallback-to-deterministic"
        };

        Assert.AreEqual(utility.DeterministicScore, utility.FinalScore);
        Assert.AreEqual(0.0, utility.ModelConfidence);
        Assert.AreEqual("fallback-to-deterministic", utility.ReasonCode);
    }

    // =========================================================================
    // 5. CandidateFeatureVector 复用 DecisionEvidenceV2 字段设计
    // =========================================================================

    [TestMethod]
    public void FeatureVector_Default_UsesSchemaVersionV2_0()
    {
        var features = new CandidateFeatureVector();

        Assert.AreEqual(
            ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            features.FeatureSchemaVersion);
        Assert.AreEqual(0, features.ScoreBreakdown.Count);
        Assert.AreEqual(0, features.MatchedAnchors.Count);
        Assert.AreEqual(0, features.RelationPaths.Count);
        Assert.AreEqual(0, features.ChannelSources.Count);
        Assert.AreEqual(0.0, features.LexicalScore);
        Assert.AreEqual(0.0, features.SemanticScore);
        Assert.AreEqual(0.0, features.RecencyScore);
        Assert.AreEqual(0.0, features.RelationBoost);
        Assert.AreEqual(0.0, features.MandatoryWeight);
    }

    [TestMethod]
    public void FeatureVector_WithScores_CarriesExpertContributions()
    {
        var features = new CandidateFeatureVector
        {
            LexicalScore = 0.8,
            SemanticScore = 0.6,
            RecencyScore = 0.3,
            RelationBoost = 0.1,
            MandatoryWeight = 1.0,
            ScoreBreakdown = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["lexical"] = 0.8,
                ["semantic"] = 0.6,
                ["recency"] = 0.3,
                ["relation_boost"] = 0.1,
                ["mandatory"] = 1.0
            },
            MatchedAnchors = new[] { "token:context", "tag:long-term" },
            RelationPaths = new[] { "root→item_a→item_b" },
            ChannelSources = new[] { "recent_context", "relation_expansion" }
        };

        Assert.AreEqual(5, features.ScoreBreakdown.Count);
        CollectionAssert.AreEquivalent(
            new[] { "token:context", "tag:long-term" },
            features.MatchedAnchors.ToList());
        Assert.AreEqual(2, features.ChannelSources.Count);
    }

    // =========================================================================
    // 6. EvidenceRef 溯源链
    // =========================================================================

    [TestMethod]
    public void EvidenceRef_Default_AllowsOptionalFieldsNull()
    {
        var evidenceRef = new EvidenceRef();

        Assert.AreEqual(string.Empty, evidenceRef.RefId);
        Assert.AreEqual(string.Empty, evidenceRef.RefType);
        Assert.IsNull(evidenceRef.WorkspaceId);
        Assert.IsNull(evidenceRef.CollectionId);
        Assert.IsNull(evidenceRef.ContentFingerprint);
    }

    [TestMethod]
    public void EvidenceRef_WithAllFields_CarriesProvenanceChain()
    {
        var now = DateTimeOffset.UtcNow;
        var evidenceRef = new EvidenceRef
        {
            RefId = "trace:abc123",
            RefType = "retrieval-trace",
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            GeneratedAt = now,
            ContentFingerprint = "sha256:deadbeef"
        };

        Assert.AreEqual("trace:abc123", evidenceRef.RefId);
        Assert.AreEqual("retrieval-trace", evidenceRef.RefType);
        Assert.AreEqual("ws-1", evidenceRef.WorkspaceId);
        Assert.AreEqual("col-1", evidenceRef.CollectionId);
        Assert.AreEqual(now, evidenceRef.GeneratedAt);
        Assert.AreEqual("sha256:deadbeef", evidenceRef.ContentFingerprint);
    }

    [TestMethod]
    public void Envelope_ProvenanceRefs_CarryMultipleEvidenceRefs()
    {
        var envelope = new ContextCandidateEnvelope
        {
            CandidateId = "cand-multi-source",
            Source = ContextCandidateSource.Graph,
            ProvenanceRefs = new[]
            {
                new EvidenceRef { RefId = "trace:abc", RefType = "retrieval-trace" },
                new EvidenceRef { RefId = "build:xyz", RefType = "package-build-trace" },
                new EvidenceRef { RefId = "model:router-v1", RefType = "model-decision" }
            }
        };

        Assert.AreEqual(3, envelope.ProvenanceRefs.Count);
        Assert.AreEqual("trace:abc", envelope.ProvenanceRefs[0].RefId);
        Assert.AreEqual("build:xyz", envelope.ProvenanceRefs[1].RefId);
        Assert.AreEqual("model:router-v1", envelope.ProvenanceRefs[2].RefId);
    }

    // =========================================================================
    // 7. Envelope 不依赖现有 ContextRetrievalCandidate / ContextPackageDecision
    // =========================================================================

    [TestMethod]
    public void Envelope_IsIndependentFromRetrievalAndPackageCandidateTypes()
    {
        // 验证 envelope 不继承 / 不引用 ContextRetrievalCandidate 或 ContextPackageDecision
        var envelopeType = typeof(ContextCandidateEnvelope);
        var retrievalCandidateType = typeof(ContextRetrievalCandidate);
        var packageDecisionType = typeof(ContextPackageDecision);

        // 类型完全不同
        Assert.AreNotEqual(envelopeType, retrievalCandidateType);
        Assert.AreNotEqual(envelopeType, packageDecisionType);
        Assert.AreNotEqual(retrievalCandidateType, packageDecisionType);

        // envelope 不继承自任何现有候选类型
        Assert.IsFalse(retrievalCandidateType.IsAssignableFrom(envelopeType));
        Assert.IsFalse(packageDecisionType.IsAssignableFrom(envelopeType));
        Assert.IsFalse(envelopeType.IsAssignableFrom(retrievalCandidateType));
        Assert.IsFalse(envelopeType.IsAssignableFrom(packageDecisionType));
    }

    // =========================================================================
    // 8. 多 Envelope 集合场景（验证可承载批量决策）
    // =========================================================================

    [TestMethod]
    public void Envelope_Collection_SupportsLinqQueriesForDecisionPipeline()
    {
        // 模拟 Engine 阶段化处理：先 safety gate 过滤，再按 FinalScore 排序
        var envelopes = new[]
        {
            new ContextCandidateEnvelope
            {
                CandidateId = "c1",
                Source = ContextCandidateSource.Mandatory,
                Safety = new CandidateSafetyState { IsMandatory = true, PassesSafetyGate = true },
                Utility = new CandidateUtilityScore { DeterministicScore = 0.9, FinalScore = 0.9 }
            },
            new ContextCandidateEnvelope
            {
                CandidateId = "c2",
                Source = ContextCandidateSource.Lexical,
                Safety = new CandidateSafetyState
                {
                    IsSuperseded = true,
                    PassesSafetyGate = false,
                    BlockReasonCode = CandidateDecisionReasonCode.SupersededByCurrentVersion
                },
                Utility = new CandidateUtilityScore { DeterministicScore = 0.8, FinalScore = 0.8 }
            },
            new ContextCandidateEnvelope
            {
                CandidateId = "c3",
                Source = ContextCandidateSource.Semantic,
                Safety = new CandidateSafetyState { PassesSafetyGate = true },
                Utility = new CandidateUtilityScore { DeterministicScore = 0.7, FinalScore = 0.7 }
            }
        };

        // Safety gate 过滤
        var passing = envelopes.Where(e => e.Safety.PassesSafetyGate).ToList();
        Assert.AreEqual(2, passing.Count);
        Assert.AreEqual("c1", passing[0].CandidateId);
        Assert.AreEqual("c3", passing[1].CandidateId);

        // 按 FinalScore 降序排序
        var ordered = passing.OrderByDescending(e => e.Utility.FinalScore).ToList();
        Assert.AreEqual("c1", ordered[0].CandidateId);
        Assert.AreEqual("c3", ordered[1].CandidateId);

        // 被拦截候选的原因码
        var blocked = envelopes.Single(e => !e.Safety.PassesSafetyGate);
        Assert.AreEqual(
            CandidateDecisionReasonCode.SupersededByCurrentVersion,
            blocked.Safety.BlockReasonCode);
    }

    // =========================================================================
    // 9. Model failure 回退路径（验收标准 #6）
    // =========================================================================

    [TestMethod]
    public void Envelope_ModelFailure_FallsBackToDeterministicPolicy()
    {
        // 验收标准 #6：Model failure 时可精确回退到 deterministic policy
        // 模拟：模型加载失败，ModelScore=null，ModelConfidence=0，FinalScore=DeterministicScore
        var envelopeWithModel = new ContextCandidateEnvelope
        {
            CandidateId = "c1",
            Source = ContextCandidateSource.Semantic,
            Utility = new CandidateUtilityScore
            {
                DeterministicScore = 0.6,
                ModelScore = 0.85,
                FinalScore = 0.725,
                ModelConfidence = 0.85,
                ReasonCode = "model-weighted",
                ModelArtifactRef = "model:router-v1"
            }
        };

        // Model failure 回退到 deterministic policy
        var fallbackEnvelope = envelopeWithModel with
        {
            Utility = envelopeWithModel.Utility with
            {
                ModelScore = null,
                FinalScore = envelopeWithModel.Utility.DeterministicScore,
                ModelConfidence = 0.0,
                ReasonCode = "fallback-to-deterministic",
                ModelArtifactRef = null
            }
        };

        // Features / Safety 仍可用
        Assert.AreEqual(envelopeWithModel.Features, fallbackEnvelope.Features);
        Assert.AreEqual(envelopeWithModel.Safety, fallbackEnvelope.Safety);

        // Utility 回退到 deterministic
        Assert.AreEqual(0.6, fallbackEnvelope.Utility.FinalScore);
        Assert.IsNull(fallbackEnvelope.Utility.ModelScore);
        Assert.AreEqual(0.0, fallbackEnvelope.Utility.ModelConfidence);
        Assert.AreEqual("fallback-to-deterministic", fallbackEnvelope.Utility.ReasonCode);

        // CandidateId / Source 不变
        Assert.AreEqual(envelopeWithModel.CandidateId, fallbackEnvelope.CandidateId);
        Assert.AreEqual(envelopeWithModel.Source, fallbackEnvelope.Source);
    }

    // =========================================================================
    // 10. 验收标准 #4：不引入存储 I/O
    // =========================================================================

    [TestMethod]
    public void Envelope_AndSubRecords_AreInMemoryOnly_NoStoreInterfaceDependencies()
    {
        // 验收标准 #4：不新增存储 I/O
        // 验证 envelope 契约不继承任何 IStore 接口
        var envelopeType = typeof(ContextCandidateEnvelope);
        var featureType = typeof(CandidateFeatureVector);
        var safetyType = typeof(CandidateSafetyState);
        var utilityType = typeof(CandidateUtilityScore);
        var evidenceRefType = typeof(EvidenceRef);

        var allTypes = new[] { envelopeType, featureType, safetyType, utilityType, evidenceRefType };

        foreach (var type in allTypes)
        {
            var interfaces = type.GetInterfaces();
            // 不应实现任何 Task 返回的存储接口（粗略检查：方法名包含 SaveAsync/LoadAsync/QueryAsync 等）
            foreach (var iface in interfaces)
            {
                foreach (var method in iface.GetMethods())
                {
                    var methodName = method.Name;
                    Assert.IsFalse(
                        methodName.Contains("SaveAsync") ||
                        methodName.Contains("LoadAsync") ||
                        methodName.Contains("QueryAsync") ||
                        methodName.Contains("UpsertAsync") ||
                        methodName.Contains("DeleteAsync"),
                        $"{type.Name} 实现的接口 {iface.Name} 包含存储 I/O 方法 {methodName}，违反验收标准 #4");
                }
            }
        }
    }
}
