using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Tests;

/// <summary>
/// Retrieval 候选适配器验证。
///
/// 验证目标：
///   1. ContextRetrievalCandidate → ContextCandidateEnvelope 字段映射正确
///   2. CandidateId 为空时回退到 SourceId
///   3. Kind=MemoryItem → Source=WorkingMemory；Kind=ContextItem → Source=Lexical
///   4. Metadata["source"] 可覆盖默认 Source 推断
///   5. Metadata["mandatory"]=true → Safety.IsMandatory=true
///   6. Metadata["lifecycleStatus"]=superseded → Safety.IsSuperseded=true
///   7. SourceRefs → ProvenanceRefs（封装为 EvidenceRef）
///   8. 批量转换 + 整体 ToDecisionRequest
///   9. 不破坏现有 ContextRetrievalCandidate 类型（仅做适配，不修改）
/// </summary>
[TestClass]
[TestCategory("R18")]
public sealed class RetrievalCandidateAdapterTests
{
    private static CandidateAdaptationContext MakeContext() => new()
    {
        WorkspaceId = "ws-test",
        CollectionId = "col-test",
        ObservedAt = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)
    };

    // =========================================================================
    // 1. 字段映射
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_MapsAllFields_Correctly()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "cand-1",
            SourceId = "src-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Type = "note",
            Score = 0.85,
            EstimatedTokens = 120,
            Reasons = new[] { "keyword-match", "anchor-hit" },
            SourceRefs = new[] { "trace:abc", "store:item-1" }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual("cand-1", envelope.CandidateId);
        Assert.AreEqual(ContextCandidateSource.Lexical, envelope.Source);
        Assert.AreEqual("note", envelope.Type);
        Assert.AreEqual(120, envelope.EstimatedTokens);
        Assert.AreEqual(0.85, envelope.Utility.DeterministicScore);
        Assert.AreEqual(0.85, envelope.Utility.FinalScore);
        Assert.AreEqual("keyword-match;anchor-hit", envelope.Utility.ReasonCode);
        Assert.AreEqual(2, envelope.ProvenanceRefs.Count);
        Assert.AreEqual("trace:abc", envelope.ProvenanceRefs[0].RefId);
        Assert.AreEqual("store:item-1", envelope.ProvenanceRefs[1].RefId);
    }

    [TestMethod]
    public void ToEnvelope_NullCandidateId_FallsBackToSourceId()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "",
            SourceId = "src-fallback",
            Kind = ContextRetrievalCandidateKind.ContextItem
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual("src-fallback", envelope.CandidateId);
    }

    // =========================================================================
    // 2. Kind → Source 映射
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_MemoryItemKind_MapsToWorkingMemorySource()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "mem-1",
            Kind = ContextRetrievalCandidateKind.MemoryItem
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual(ContextCandidateSource.WorkingMemory, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_ContextItemKind_MapsToLexicalSource()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "ctx-1",
            Kind = ContextRetrievalCandidateKind.ContextItem
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual(ContextCandidateSource.Lexical, envelope.Source);
    }

    // =========================================================================
    // 3. Metadata 覆盖
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_MetadataSource_OverridesDefaultSourceInference()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "override-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "Semantic"
            }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual(ContextCandidateSource.Semantic, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_MetadataMandatoryTrue_SetsSafetyIsMandatory()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "mandatory-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Metadata = new Dictionary<string, string>
            {
                ["mandatory"] = "true"
            }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.IsTrue(envelope.Safety.IsMandatory);
    }

    [TestMethod]
    public void ToEnvelope_MetadataLifecycleSuperseded_SetsSafetyIsSuperseded()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "superseded-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Metadata = new Dictionary<string, string>
            {
                ["lifecycleStatus"] = "superseded"
            }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual("superseded", envelope.Safety.LifecycleState);
        Assert.IsTrue(envelope.Safety.IsSuperseded);
    }

    [TestMethod]
    public void ToEnvelope_MetadataChannel_PopulatesFeaturesChannelSources()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "chan-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Metadata = new Dictionary<string, string>
            {
                ["channel"] = "vector",
                ["retrievalChannel"] = "vector_recall"
            }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        Assert.AreEqual(2, envelope.Features.ChannelSources.Count);
        Assert.AreEqual("vector", envelope.Features.ChannelSources[0]);
        Assert.AreEqual("vector_recall", envelope.Features.ChannelSources[1]);
    }

    // =========================================================================
    // 4. 批量转换
    // =========================================================================

    [TestMethod]
    public void ToEnvelopes_BatchConversion_PreservesAllCandidates()
    {
        var candidates = new[]
        {
            new ContextRetrievalCandidate { CandidateId = "c1", Kind = ContextRetrievalCandidateKind.ContextItem },
            new ContextRetrievalCandidate { CandidateId = "c2", Kind = ContextRetrievalCandidateKind.MemoryItem },
            new ContextRetrievalCandidate { CandidateId = "c3", Kind = ContextRetrievalCandidateKind.ContextItem }
        };

        var envelopes = RetrievalCandidateAdapter.ToEnvelopes(candidates, MakeContext());

        Assert.AreEqual(3, envelopes.Count);
        Assert.AreEqual("c1", envelopes[0].CandidateId);
        Assert.AreEqual("c2", envelopes[1].CandidateId);
        Assert.AreEqual("c3", envelopes[2].CandidateId);
    }

    [TestMethod]
    public void ToEnvelopes_EmptyInput_ReturnsEmptyList()
    {
        var envelopes = RetrievalCandidateAdapter.ToEnvelopes(Array.Empty<ContextRetrievalCandidate>(), MakeContext());

        Assert.AreEqual(0, envelopes.Count);
    }

    // =========================================================================
    // 5. ToDecisionRequest 整体转换
    // =========================================================================

    [TestMethod]
    public void ToDecisionRequest_ConvertsSelectedAndDroppedIntoRequest()
    {
        var result = new ContextRetrievalResult
        {
            OperationId = "op-1",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "sel-1",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Score = 0.9,
                    EstimatedTokens = 100
                }
            },
            DroppedItems = new[]
            {
                new ContextRetrievalDecision
                {
                    CandidateId = "drop-1",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Reason = "超过 token 预算",
                    Score = 0.3,
                    EstimatedTokens = 200
                }
            }
        };

        var request = RetrievalCandidateAdapter.ToDecisionRequest(result, tokenBudget: 500, topK: int.MaxValue, enableModel: false, MakeContext());

        Assert.AreEqual("op-1", request.RequestId);
        Assert.AreEqual(ContextDecisionSource.Retrieval, request.DecisionSource);
        Assert.AreEqual(500, request.TokenBudget);
        Assert.AreEqual(2, request.Candidates.Count);

        // selected 候选 passes safety gate
        var sel = request.Candidates.Single(c => c.CandidateId == "sel-1");
        Assert.IsTrue(sel.Safety.PassesSafetyGate);
        Assert.AreEqual(0.9, sel.Utility.FinalScore);

        // dropped 候选不通过 safety gate（已标记为 dropped-by-retrieval）
        var drop = request.Candidates.Single(c => c.CandidateId == "drop-1");
        Assert.IsFalse(drop.Safety.PassesSafetyGate);
        Assert.AreEqual("dropped-by-retrieval", drop.Utility.ReasonCode);
    }

    // =========================================================================
    // 6. 不破坏原候选（不可变性）
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_DoesNotMutateOriginalCandidate()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "original-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Score = 0.7,
            Metadata = new Dictionary<string, string>
            {
                ["mandatory"] = "true",
                ["lifecycleStatus"] = "active"
            }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, MakeContext());

        // 原候选不变
        Assert.AreEqual("original-1", candidate.CandidateId);
        Assert.AreEqual(0.7, candidate.Score);
        Assert.AreEqual("true", candidate.Metadata["mandatory"]);
        Assert.AreEqual("active", candidate.Metadata["lifecycleStatus"]);

        // envelope 是新对象
        Assert.AreNotSame((object)candidate, (object)envelope);
        Assert.IsNotNull(envelope.Features);
        Assert.IsNotNull(envelope.Features.ScoreBreakdown);
        Assert.AreNotSame(candidate.Metadata, (object)envelope.Features.ScoreBreakdown);
    }

    // =========================================================================
    // 7. 端到端：Retrieval 主链 → 适配器 → Engine → Projector → Retrieval DTO
    // =========================================================================

    [TestMethod]
    public async Task EndToEnd_RetrievalPath_ThroughAdapter_Engine_Projector()
    {
        // Step 1: 模拟 Retrieval 主链产出（已 selected）
        var retrievalResult = new ContextRetrievalResult
        {
            OperationId = "e2e-op-1",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "high",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Score = 0.9,
                    EstimatedTokens = 100
                },
                new ContextRetrievalCandidate
                {
                    CandidateId = "low",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Score = 0.3,
                    EstimatedTokens = 100
                }
            }
        };

        // Step 2: 通过适配器转换为 DecisionRequest
        var request = RetrievalCandidateAdapter.ToDecisionRequest(
            retrievalResult, tokenBudget: 150, topK: int.MaxValue, enableModel: false, MakeContext()); // 只够 1 个候选

        // Step 3: 通过 Engine 决策
        var engine = new DefaultContextDecisionEngine();
        var decisionResult = await engine.DecideAsync(request);

        // Step 4: 通过 Projector 转回 Retrieval DTO
        var projector = new RetrievalResultProjector();
        var finalDto = projector.Project(decisionResult);

        // 断言：Engine 选入 high（高分优先），drop low（token budget 不足）
        Assert.AreEqual(1, decisionResult.SelectedEnvelopes.Count);
        Assert.AreEqual("high", decisionResult.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual(1, decisionResult.DroppedEnvelopes.Count);
        Assert.AreEqual("low", decisionResult.DroppedEnvelopes[0].CandidateId);
        Assert.AreEqual(
            CandidateDecisionReasonCode.TokenBudgetExceeded,
            decisionResult.DroppedEnvelopes[0].Safety.BlockReasonCode);

        // Projector 输出
        Assert.AreEqual("e2e-op-1", finalDto.OperationId);
        Assert.AreEqual(1, finalDto.SelectedItems.Count);
        Assert.AreEqual("high", finalDto.SelectedItems[0].CandidateId);
    }

    // =========================================================================
    // 8. P0-5：CandidateAdaptationContext 上下文传入
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_WithContext_FillsWorkspaceAndCollectionAndObservedAt()
    {
        // context 提供 WorkspaceId/CollectionId/ObservedAt，适配器不应读系统时间
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "ctx-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            SourceRefs = new[] { "trace:abc" }
        };
        var observedAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-p0-5",
            CollectionId = "col-p0-5",
            RequestId = "req-123",
            QueryText = "test query",
            ObservedAt = observedAt
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, context);

        Assert.AreEqual("ws-p0-5", envelope.WorkspaceId);
        Assert.AreEqual("col-p0-5", envelope.CollectionId);
        Assert.AreEqual(observedAt, envelope.ProvenanceRefs[0].GeneratedAt);
        Assert.AreEqual("ws-p0-5", envelope.ProvenanceRefs[0].WorkspaceId);
        Assert.AreEqual("col-p0-5", envelope.ProvenanceRefs[0].CollectionId);
    }

    [TestMethod]
    public void ToEnvelope_WithContext_IsDeterministic_SameInputProducesSameOutput()
    {
        // 相同候选 + 相同 context 应产生完全相同的 envelope（幂等性）
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "det-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            Score = 0.5,
            SourceRefs = new[] { "trace:det" }
        };
        var observedAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-det",
            CollectionId = "col-det",
            ObservedAt = observedAt
        };

        var envelope1 = RetrievalCandidateAdapter.ToEnvelope(candidate, context);
        var envelope2 = RetrievalCandidateAdapter.ToEnvelope(candidate, context);

        Assert.AreEqual(envelope1.CandidateId, envelope2.CandidateId);
        Assert.AreEqual(envelope1.WorkspaceId, envelope2.WorkspaceId);
        Assert.AreEqual(envelope1.CollectionId, envelope2.CollectionId);
        Assert.AreEqual(envelope1.ProvenanceRefs[0].GeneratedAt, envelope2.ProvenanceRefs[0].GeneratedAt);
    }

    [TestMethod]
    public void ToEnvelope_WithContextPolicySnapshot_AttachesFingerprintToEvidenceRef()
    {
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "fp-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            SourceRefs = new[] { "trace:fp" }
        };
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-fp",
            CollectionId = "col-fp",
            ObservedAt = DateTimeOffset.UtcNow,
            PolicySnapshot = new ResolvedPolicySnapshot
            {
                BundleId = "bundle-2026-07-v1",
                Version = "1.0.0"
            }
        };

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, context);

        Assert.IsNotNull(envelope.ProvenanceRefs[0].ContentFingerprint);
        Assert.AreEqual("bundle-2026-07-v1@1.0.0", envelope.ProvenanceRefs[0].ContentFingerprint);
    }

    [TestMethod]
    public void ToDecisionRequest_WithContext_FillsWorkspaceCollectionQueryText()
    {
        // ToDecisionRequest 必须填充 WorkspaceId/CollectionId/QueryText，
        // 避免 PolicyRegistry 按空 workspace 解析默认 Bundle
        var result = new ContextRetrievalResult
        {
            OperationId = "op-ctx-1",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate
                {
                    CandidateId = "sel-1",
                    Kind = ContextRetrievalCandidateKind.ContextItem,
                    Score = 0.9,
                    EstimatedTokens = 100
                }
            },
            DroppedItems = Array.Empty<ContextRetrievalDecision>()
        };
        var context = new CandidateAdaptationContext
        {
            WorkspaceId = "ws-req",
            CollectionId = "col-req",
            RequestId = "req-explicit-1",
            QueryText = "find related memories",
            ObservedAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)
        };

        var request = RetrievalCandidateAdapter.ToDecisionRequest(result, tokenBudget: 500, topK: 10, enableModel: false, context);

        Assert.AreEqual("ws-req", request.WorkspaceId);
        Assert.AreEqual("col-req", request.CollectionId);
        Assert.AreEqual("find related memories", request.QueryText);
        Assert.AreEqual("req-explicit-1", request.RequestId);
        Assert.AreEqual("ws-req", request.Candidates[0].WorkspaceId);
        Assert.AreEqual("col-req", request.Candidates[0].CollectionId);
    }

    [TestMethod]
    public void ToEnvelope_WithContext_UsesContextObservedAtAndScope()
    {
        // 旧无 context 重载已标记 [Obsolete(error: true)]，所有调用必须显式传 context。
        // context 中的 ObservedAt / WorkspaceId / CollectionId 必须原样传播到 envelope，
        // 不再回退到 DateTimeOffset.UtcNow 或空字符串（保证确定性 + 作用域可追溯）。
        var candidate = new ContextRetrievalCandidate
        {
            CandidateId = "legacy-1",
            Kind = ContextRetrievalCandidateKind.ContextItem,
            SourceRefs = new[] { "trace:legacy" }
        };
        var context = MakeContext();

        var envelope = RetrievalCandidateAdapter.ToEnvelope(candidate, context);

        Assert.AreEqual(context.ObservedAt, envelope.ProvenanceRefs[0].GeneratedAt);
        Assert.AreEqual(context.WorkspaceId, envelope.WorkspaceId);
        Assert.AreEqual(context.CollectionId, envelope.CollectionId);
    }
}
