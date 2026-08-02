using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Tests;

/// <summary>
/// 统一决策引擎接口 + Planner + Projectors 验证。
///
/// 验证目标：
///   1. DefaultContextDecisionEngine 三阶段编排（safety gate → utility scoring → budget allocation）
///   2. mandatory 候选永远选入（不受 budget 限制）
///   3. safety gate 拦截的候选进入 DroppedEnvelopes
///   4. budget 超限候选进入 DroppedEnvelopes（带正确 ReasonCode）
///   5. RetrievalResultProjector 投影为 ContextRetrievalResult（保持现有 DTO 兼容）
///   6. PackageResultProjector 投影为 ContextPackageBuildResult（保持现有 DTO 兼容）
///   7. ContextDecisionProjector.ProjectFromEnvelopes 投影为 ContextDecisionRecord
///   8. 不破坏现有 ProjectPackage / ProjectRetrieval 路径（向后兼容）
///   9. 幂等性：相同 Request 产生相同 Result
///  10. Model failure 回退到 deterministic policy（验收标准 #6）
/// </summary>
[TestClass]
[TestCategory("R18")]
public sealed class DecisionEngineTests
{
    // =========================================================================
    // 1. DefaultContextDecisionEngine 三阶段编排
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_EmptyCandidates_ReturnsEmptyResult()
    {
        var engine = new DefaultContextDecisionEngine();
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates: Array.Empty<ContextCandidateEnvelope>());

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(0, result.SelectedEnvelopes.Count);
        Assert.AreEqual(0, result.DroppedEnvelopes.Count);
        Assert.AreEqual(0, result.Outcome.SelectedCount);
        Assert.IsFalse(result.ModelEnabled);
    }

    [TestMethod]
    public async Task DecideAsync_AllPassingSafety_AllSelected()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Lexical, score: 0.5, tokens: 100),
            MakeEnvelope("c2", ContextCandidateSource.Semantic, score: 0.7, tokens: 200),
            MakeEnvelope("c3", ContextCandidateSource.WorkingMemory, score: 0.9, tokens: 300)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 1000);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(3, result.SelectedEnvelopes.Count);
        Assert.AreEqual(0, result.DroppedEnvelopes.Count);
        Assert.AreEqual(600, result.Outcome.EstimatedTokens);
    }

    [TestMethod]
    public async Task DecideAsync_OrderedByScoreDescThenIdAsc()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c-low", ContextCandidateSource.Lexical, score: 0.3, tokens: 100),
            MakeEnvelope("c-high", ContextCandidateSource.Semantic, score: 0.9, tokens: 100),
            MakeEnvelope("c-mid", ContextCandidateSource.Lexical, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 1000);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(3, result.SelectedEnvelopes.Count);
        Assert.AreEqual("c-high", result.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual("c-mid", result.SelectedEnvelopes[1].CandidateId);
        Assert.AreEqual("c-low", result.SelectedEnvelopes[2].CandidateId);
    }

    [TestMethod]
    public async Task DecideAsync_SameScore_TieBreakByIdAsc()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("zzz", ContextCandidateSource.Lexical, score: 0.5, tokens: 100),
            MakeEnvelope("aaa", ContextCandidateSource.Lexical, score: 0.5, tokens: 100),
            MakeEnvelope("mmm", ContextCandidateSource.Lexical, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 1000);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual("aaa", result.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual("mmm", result.SelectedEnvelopes[1].CandidateId);
        Assert.AreEqual("zzz", result.SelectedEnvelopes[2].CandidateId);
    }

    // =========================================================================
    // 2. Mandatory 候选永远选入（不受 budget 限制）
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_MandatoryCandidate_AlwaysSelectedRegardlessOfBudget()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("mandatory-1", ContextCandidateSource.Mandatory, score: 0.0, tokens: 500,
                safety: new CandidateSafetyState { IsMandatory = true, IsHardConstraint = true }),
            MakeEnvelope("optional-1", ContextCandidateSource.Lexical, score: 0.9, tokens: 500)
        };
        // token budget=300 < 500，但 mandatory 不受限制
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 300);

        var result = await engine.DecideAsync(request);

        // mandatory-1 选入（不受 budget 限制）
        Assert.IsTrue(result.SelectedEnvelopes.Any(e => e.CandidateId == "mandatory-1"));
        // optional-1 被 budget 拦截
        Assert.IsTrue(result.DroppedEnvelopes.Any(e => e.CandidateId == "optional-1"));
        Assert.AreEqual(
            CandidateDecisionReasonCode.TokenBudgetExceeded,
            result.DroppedEnvelopes.First(e => e.CandidateId == "optional-1").Safety.BlockReasonCode);
    }

    // =========================================================================
    // 3. Safety gate 拦截
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_SupersededCandidate_GoesToDroppedWithReasonCode()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("superseded", ContextCandidateSource.Lexical, score: 0.9, tokens: 100,
                safety: new CandidateSafetyState
                {
                    IsSuperseded = true,
                    PassesSafetyGate = false,
                    BlockReasonCode = CandidateDecisionReasonCode.SupersededByCurrentVersion,
                    BlockReasonDetail = "superseded by item-v2"
                }),
            MakeEnvelope("normal", ContextCandidateSource.Semantic, score: 0.5, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 1000);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual("normal", result.SelectedEnvelopes[0].CandidateId);

        Assert.AreEqual(1, result.DroppedEnvelopes.Count);
        var dropped = result.DroppedEnvelopes[0];
        Assert.AreEqual("superseded", dropped.CandidateId);
        Assert.AreEqual(
            CandidateDecisionReasonCode.SupersededByCurrentVersion,
            dropped.Safety.BlockReasonCode);
        Assert.AreEqual(1, result.Outcome.SafetyGateBlockedCount);
    }

    // =========================================================================
    // 4. Budget 超限
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_TokenBudgetExceeded_DropsLowestScoreCandidates()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("high", ContextCandidateSource.Semantic, score: 0.9, tokens: 300),
            MakeEnvelope("mid", ContextCandidateSource.Lexical, score: 0.5, tokens: 300),
            MakeEnvelope("low", ContextCandidateSource.Lexical, score: 0.3, tokens: 300)
        };
        // token budget=500；只能选 1 个（high 300 tokens）
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual("high", result.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual(300, result.Outcome.EstimatedTokens);

        Assert.AreEqual(2, result.DroppedEnvelopes.Count);
        Assert.AreEqual(2, result.Outcome.BudgetExceededCount);
        Assert.AreEqual(
            CandidateDecisionReasonCode.TokenBudgetExceeded,
            result.DroppedEnvelopes[0].Safety.BlockReasonCode);
    }

    [TestMethod]
    public async Task DecideAsync_TopKExceeded_DropsExcessCandidates()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 100),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.7, tokens: 100),
            MakeEnvelope("c3", ContextCandidateSource.Lexical, score: 0.5, tokens: 100)
        };
        // topK=2；c3 应被丢弃
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 1000, topK: 2);

        var result = await engine.DecideAsync(request);

        Assert.AreEqual(2, result.SelectedEnvelopes.Count);
        Assert.AreEqual("c1", result.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual("c2", result.SelectedEnvelopes[1].CandidateId);

        Assert.AreEqual(1, result.DroppedEnvelopes.Count);
        Assert.AreEqual("c3", result.DroppedEnvelopes[0].CandidateId);
        Assert.AreEqual(
            CandidateDecisionReasonCode.SectionQuotaExceeded,
            result.DroppedEnvelopes[0].Safety.BlockReasonCode);
    }

    // =========================================================================
    // 5. RetrievalResultProjector
    // =========================================================================

    [TestMethod]
    public void RetrievalProjector_ProjectsToContextRetrievalResult()
    {
        var result = new ContextDecisionResult
        {
            RequestId = "op-1",
            DecisionSource = ContextDecisionSource.Retrieval,
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.9, tokens: 100)
            },
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.5, tokens: 100,
                    safety: new CandidateSafetyState
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.SupersededByCurrentVersion,
                        BlockReasonDetail = "superseded"
                    })
            },
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 1, DroppedCount = 1, EstimatedTokens = 100, TokenBudget = 500
            },
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            ModelEnabled = false
        };

        var projector = new RetrievalResultProjector();
        var dto = projector.Project(result);

        Assert.AreEqual("op-1", dto.OperationId);
        Assert.IsTrue(dto.Succeeded);
        Assert.AreEqual(1, dto.SelectedItems.Count);
        Assert.AreEqual("c1", dto.SelectedItems[0].CandidateId);
        Assert.AreEqual(0.9, dto.SelectedItems[0].Score);
        Assert.AreEqual(100, dto.SelectedItems[0].EstimatedTokens);

        Assert.AreEqual(1, dto.DroppedItems.Count);
        Assert.AreEqual("c2", dto.DroppedItems[0].CandidateId);
        StringAssert.Contains(dto.DroppedItems[0].Reason, "SupersededByCurrentVersion");

        Assert.AreEqual(100, dto.EstimatedTokens);
        Assert.AreEqual("decision-schema/2.0", dto.Metadata["policyVersion"]);
        Assert.AreEqual("false", dto.Metadata["modelEnabled"]);
    }

    // =========================================================================
    // 6. PackageResultProjector
    // =========================================================================

    [TestMethod]
    public void PackageProjector_ProjectsToContextPackageBuildResult()
    {
        var result = new ContextDecisionResult
        {
            RequestId = "build-1",
            DecisionSource = ContextDecisionSource.Package,
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("item-1", ContextCandidateSource.WorkingMemory, score: 0.8, tokens: 150)
            },
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("item-2", ContextCandidateSource.Lexical, score: 0.3, tokens: 100,
                    safety: new CandidateSafetyState
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.TokenBudgetExceeded,
                        BlockReasonDetail = "exceeded"
                    })
            },
            Outcome = new ContextDecisionOutcomeSummary
            {
                SelectedCount = 1, DroppedCount = 1, EstimatedTokens = 150, TokenBudget = 200
            },
            PolicyVersion = ContextDecisionPolicyVersions.PackagePolicyV3_1,
            ModelEnabled = false
        };

        var projector = new PackageResultProjector();
        var dto = projector.Project(result);

        Assert.AreEqual("build-1", dto.BuildId);
        Assert.AreEqual(1, dto.SelectedItems.Count);
        Assert.AreEqual("item-1", dto.SelectedItems[0].ItemId);
        Assert.AreEqual("working_memory", dto.SelectedItems[0].Kind);
        Assert.AreEqual("working_memory", dto.SelectedItems[0].SectionName);
        Assert.AreEqual(0.8, dto.SelectedItems[0].Score);

        Assert.AreEqual(1, dto.DroppedItems.Count);
        Assert.AreEqual("item-2", dto.DroppedItems[0].ItemId);
        StringAssert.Contains(dto.DroppedItems[0].Reason, "TokenBudgetExceeded");

        Assert.AreEqual("package-policy/3.1", dto.Metadata["policyVersion"]);
    }

    // =========================================================================
    // 7. ContextDecisionProjector.ProjectFromEnvelopes
    // =========================================================================

    [TestMethod]
    public void ProjectFromEnvelopes_ReturnsContextDecisionRecord()
    {
        var result = new ContextDecisionResult
        {
            RequestId = "decision-1",
            DecisionSource = ContextDecisionSource.Package,
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("sel-1", ContextCandidateSource.Mandatory, score: 0.95, tokens: 200)
            },
            DroppedEnvelopes = new[]
            {
                MakeEnvelope("drop-1", ContextCandidateSource.Lexical, score: 0.4, tokens: 100,
                    safety: new CandidateSafetyState
                    {
                        PassesSafetyGate = false,
                        BlockReasonCode = CandidateDecisionReasonCode.DeprecatedBlocked
                    })
            },
            PolicyVersion = ContextDecisionPolicyVersions.DecisionSchemaV2_0,
            DecidedAt = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)
        };

        var record = ContextDecisionProjector.ProjectFromEnvelopes(result);

        Assert.AreEqual("decision-1", record.DecisionId);
        Assert.AreEqual(ContextDecisionSource.Package, record.Source);
        Assert.AreEqual(2, record.Candidates.Count);

        var selected = record.Candidates.Single(c => c.Outcome == ContextDecisionCandidateOutcome.Selected);
        Assert.AreEqual("sel-1", selected.ItemId);
        Assert.AreEqual("hard_constraint", selected.Kind);
        Assert.AreEqual("hard_constraints", selected.SectionName);
        Assert.AreEqual("mandatory", selected.Reason);
        Assert.AreEqual(0.95, selected.Score);

        var dropped = record.Candidates.Single(c => c.Outcome == ContextDecisionCandidateOutcome.Dropped);
        Assert.AreEqual("drop-1", dropped.ItemId);
        Assert.AreEqual("recent_context", dropped.SectionName);
        StringAssert.Contains(dropped.Reason, "DeprecatedBlocked");

        Assert.AreEqual("decision-schema/2.0", record.PolicyVersion);
        Assert.IsNull(record.Quality); // 不计算 quality
    }

    // =========================================================================
    // 8. 向后兼容：现有 ProjectPackage / ProjectRetrieval 不受影响
    // =========================================================================

    [TestMethod]
    public void ProjectFromEnvelopes_DoesNotAffectExistingProjectPackage()
    {
        // 验证：新增的 ProjectFromEnvelopes 不影响现有 ProjectPackage 的行为
        var packageResult = new ContextPackageBuildResult
        {
            BuildId = "build-1",
            SelectedItems = new[]
            {
                new ContextPackageDecision { ItemId = "i1", Kind = "memory", Score = 0.7 }
            }
        };

        // 现有路径正常工作
        var record1 = ContextDecisionProjector.ProjectPackage(packageResult);
        Assert.AreEqual("build-1", record1.DecisionId);
        Assert.AreEqual(1, record1.Candidates.Count);
        Assert.AreEqual("i1", record1.Candidates[0].ItemId);

        // ProjectFromEnvelopes 是独立路径，不与 ProjectPackage 冲突
        var envelopeResult = new ContextDecisionResult
        {
            RequestId = "envelope-1",
            DecisionSource = ContextDecisionSource.Package,
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("e1", ContextCandidateSource.WorkingMemory, score: 0.5, tokens: 100)
            }
        };
        var record2 = ContextDecisionProjector.ProjectFromEnvelopes(envelopeResult);
        Assert.AreEqual("envelope-1", record2.DecisionId);
        Assert.AreEqual("e1", record2.Candidates[0].ItemId);

        // 两个 record 独立
        Assert.AreNotEqual(record1.DecisionId, record2.DecisionId);
    }

    [TestMethod]
    public void ProjectFromEnvelopes_DoesNotAffectExistingProjectRetrieval()
    {
        var retrievalResult = new ContextRetrievalResult
        {
            OperationId = "op-1",
            SelectedItems = new[]
            {
                new ContextRetrievalCandidate { CandidateId = "r1", Score = 0.6 }
            }
        };

        var record1 = ContextDecisionProjector.ProjectRetrieval(retrievalResult);
        Assert.AreEqual(1, record1.Candidates.Count);

        var envelopeResult = new ContextDecisionResult
        {
            RequestId = "envelope-2",
            DecisionSource = ContextDecisionSource.Retrieval,
            SelectedEnvelopes = new[]
            {
                MakeEnvelope("e2", ContextCandidateSource.Semantic, score: 0.8, tokens: 100)
            }
        };
        var record2 = ContextDecisionProjector.ProjectFromEnvelopes(envelopeResult);

        Assert.AreNotEqual(record1.DecisionId, record2.DecisionId);
    }

    // =========================================================================
    // 9. 幂等性
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_SameRequest_ProducesSameResult()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100),
            MakeEnvelope("c2", ContextCandidateSource.Lexical, score: 0.6, tokens: 100)
        };
        var request = MakeRequest(ContextDecisionSource.Retrieval, candidates, tokenBudget: 500);

        var result1 = await engine.DecideAsync(request);
        var result2 = await engine.DecideAsync(request);

        Assert.AreEqual(result1.SelectedEnvelopes.Count, result2.SelectedEnvelopes.Count);
        Assert.AreEqual(result1.DroppedEnvelopes.Count, result2.DroppedEnvelopes.Count);
        for (var i = 0; i < result1.SelectedEnvelopes.Count; i++)
        {
            Assert.AreEqual(
                result1.SelectedEnvelopes[i].CandidateId,
                result2.SelectedEnvelopes[i].CandidateId);
        }
    }

    // =========================================================================
    // 10. Model failure 回退
    // =========================================================================

    [TestMethod]
    public async Task DecideAsync_EnableModelFalse_FallsBackToDeterministic()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95, // 模型评分
                    FinalScore = 0.875, // 加权后
                    ModelConfidence = 0.85,
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:v1"
                })
        };
        // EnableModel=false → 强制 deterministic-only
        var request = MakeRequest(
            ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: false);

        var result = await engine.DecideAsync(request);

        // 模型启用标志为 false
        Assert.IsFalse(result.ModelEnabled);
        // 候选仍选入（基于 DeterministicScore）
        Assert.AreEqual(1, result.SelectedEnvelopes.Count);
        Assert.AreEqual("c1", result.SelectedEnvelopes[0].CandidateId);
        // ModelVersion 为 null（未启用模型）
        Assert.IsNull(result.ModelVersion);
    }

    [TestMethod]
    public async Task DecideAsync_EnableModelTrue_WithModelScore_MarksModelEnabled()
    {
        var engine = new DefaultContextDecisionEngine();
        var candidates = new[]
        {
            MakeEnvelope("c1", ContextCandidateSource.Semantic, score: 0.8, tokens: 100,
                utility: new CandidateUtilityScore
                {
                    DeterministicScore = 0.8,
                    ModelScore = 0.95,
                    FinalScore = 0.875,
                    ModelConfidence = 0.85,
                    ReasonCode = "model-weighted",
                    ModelArtifactRef = "model:router-v1"
                })
        };
        var request = MakeRequest(
            ContextDecisionSource.Retrieval, candidates, tokenBudget: 500,
            enableModel: true);

        var result = await engine.DecideAsync(request);

        Assert.IsTrue(result.ModelEnabled);
        Assert.AreEqual("model:router-v1", result.ModelVersion);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static ContextDecisionRequest MakeRequest(
        ContextDecisionSource source,
        IReadOnlyList<ContextCandidateEnvelope> candidates,
        int tokenBudget = 1000,
        int topK = int.MaxValue,
        bool enableModel = true)
    {
        return new ContextDecisionRequest
        {
            RequestId = "req-test-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            DecisionSource = source,
            WorkspaceId = "ws-test",
            CollectionId = "col-test",
            Candidates = candidates,
            TokenBudget = tokenBudget,
            TopK = topK,
            EnableModel = enableModel
        };
    }

    private static ContextCandidateEnvelope MakeEnvelope(
        string candidateId,
        ContextCandidateSource source,
        double score,
        int tokens,
        CandidateSafetyState? safety = null,
        CandidateUtilityScore? utility = null)
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
            Source = source,
            EstimatedTokens = tokens,
            Safety = safety ?? new CandidateSafetyState(),
            Utility = utility ?? new CandidateUtilityScore
            {
                DeterministicScore = score,
                FinalScore = score,
                ReasonCode = "deterministic-only"
            }
        };
    }
}
