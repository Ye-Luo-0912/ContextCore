using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services.DecisionEngine;

namespace ContextCore.Tests;

/// <summary>
/// R18-4：Package 候选适配器验证。
///
/// 验证目标：
///   1. PackageTraceCandidate → ContextCandidateEnvelope 字段映射正确
///   2. Kind 字符串 → ContextCandidateSource 枚举映射（含 raw/legacy/current_task 归并）
///   3. ItemScoreBreakdown 13 个子分维度 → Features.ScoreBreakdown 字典
///   4. Metadata lifecycleStatus 覆盖 Safety.LifecycleState
///   5. hard_constraint kind → IsMandatory + IsHardConstraint
///   6. 批量转换 + 整体 ToDecisionRequest
///   7. 不破坏现有 PackageTraceCandidate 类型（仅做适配，不修改）
///   8. 端到端：Package result → Adapter → Engine → Projector → Package DTO
/// </summary>
[TestClass]
[TestCategory("R18")]
public sealed class PackageCandidateAdapterTests
{
    // =========================================================================
    // 1. 字段映射
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_MapsAllFields_Correctly()
    {
        var item = new ContextMemoryItem
        {
            Id = "pkg-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Content = "sample content",
            SourceRefs = new[] { "trace:abc", "store:item-1" },
            Metadata = new Dictionary<string, string> { ["k"] = "v" }
        };
        var candidate = PackageTraceCandidate.FromMemory(item, kind: "working_memory", score: 0.85, estimatedTokens: 120);

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual("pkg-1", envelope.CandidateId);
        Assert.AreEqual(ContextCandidateSource.WorkingMemory, envelope.Source);
        Assert.AreEqual("note", envelope.Type);
        Assert.AreEqual(120, envelope.EstimatedTokens);
        Assert.AreEqual(0.85, envelope.Utility.DeterministicScore);
        Assert.AreEqual(0.85, envelope.Utility.FinalScore);
        Assert.AreEqual(2, envelope.ProvenanceRefs.Count);
        Assert.AreEqual("trace:abc", envelope.ProvenanceRefs[0].RefId);
        Assert.AreEqual("store:item-1", envelope.ProvenanceRefs[1].RefId);
        Assert.AreEqual("package-source-ref", envelope.ProvenanceRefs[0].RefType);
    }

    // =========================================================================
    // 2. Kind → Source 映射
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_WorkingMemoryKind_MapsToWorkingMemorySource()
    {
        var candidate = MakeCandidate("wm-1", "working_memory");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.WorkingMemory, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_StableMemoryKind_MapsToStableMemorySource()
    {
        var candidate = MakeCandidate("sm-1", "stable_memory");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.StableMemory, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_HistoricalContextKind_MapsToStableMemorySource()
    {
        var candidate = MakeCandidate("hist-1", "historical_context");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.StableMemory, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_GlobalContextKind_MapsToGlobalContextSource()
    {
        var candidate = MakeCandidate("glob-1", "global_context");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.GlobalContext, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_RecentContextKind_MapsToRecencySource()
    {
        var candidate = MakeCandidate("recent-1", "recent_context");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Recency, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_CurrentTaskKind_MapsToRecencySource()
    {
        // current_task 在枚举注释中明确归入 Recency（"Recency / Task-State"）
        var candidate = MakeCandidate("task-1", "current_task");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Recency, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_RawKind_MapsToLexicalSource()
    {
        // raw/legacy 在 PackageTraceRecorder 中映射到 Keyword channel（lexical 路径）
        var candidate = MakeCandidate("raw-1", "raw");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Lexical, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_LegacyKind_MapsToLexicalSource()
    {
        var candidate = MakeCandidate("legacy-1", "legacy");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Lexical, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_RelatedContextKind_MapsToRelatedContextSource()
    {
        var candidate = MakeCandidate("rel-1", "related_context");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.RelatedContext, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_HardConstraintKind_MapsToConstraintSource()
    {
        var candidate = MakeCandidate("hc-1", "hard_constraint");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Constraint, envelope.Source);
        Assert.IsTrue(envelope.Safety.IsMandatory);
        Assert.IsTrue(envelope.Safety.IsHardConstraint);
        Assert.AreEqual("mandatory", envelope.Utility.ReasonCode);
    }

    [TestMethod]
    public void ToEnvelope_MergedConstraintKind_MapsToConstraintSource()
    {
        var candidate = MakeCandidate("mc-1", "merged_constraint");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Constraint, envelope.Source);
        Assert.IsTrue(envelope.Safety.IsMandatory);
        Assert.IsTrue(envelope.Safety.IsHardConstraint);
    }

    [TestMethod]
    public void ToEnvelope_UnknownKind_MapsToUnknownSource()
    {
        var candidate = MakeCandidate("unk-1", "totally-unknown-kind");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Unknown, envelope.Source);
    }

    [TestMethod]
    public void ToEnvelope_EmptyKind_MapsToUnknownSource()
    {
        var candidate = MakeCandidate("empty-1", "");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(ContextCandidateSource.Unknown, envelope.Source);
    }

    // =========================================================================
    // 3. Metadata lifecycle 覆盖
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_MetadataLifecycleSuperseded_SetsSafetyIsSuperseded()
    {
        var candidate = MakeCandidate("sup-1", "working_memory",
            metadata: new Dictionary<string, string> { ["lifecycleStatus"] = "superseded" });

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual("superseded", envelope.Safety.LifecycleState);
        Assert.IsTrue(envelope.Safety.IsSuperseded);
        // 注意：ToEnvelope 不根据 IsSuperseded 自动设置 PassesSafetyGate=false
        // Safety gate 计算由 Engine 在 DecideAsync 阶段完成
        Assert.IsTrue(envelope.Safety.PassesSafetyGate);
    }

    [TestMethod]
    public void ToEnvelope_MetadataLifecycleDeprecated_StillPassesSafetyGateByDefault()
    {
        var candidate = MakeCandidate("dep-1", "working_memory",
            metadata: new Dictionary<string, string>
            {
                ["lifecycleStatus"] = "deprecated"
            });

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual("deprecated", envelope.Safety.LifecycleState);
        Assert.IsFalse(envelope.Safety.IsDeprecatedUsedByActiveChain);
        Assert.IsTrue(envelope.Safety.PassesSafetyGate);
    }

    [TestMethod]
    public void ToEnvelope_MetadataDeprecatedUsedByActiveChain_SetsFlag()
    {
        var candidate = MakeCandidate("dep-act-1", "working_memory",
            metadata: new Dictionary<string, string>
            {
                ["lifecycleStatus"] = "deprecated",
                ["usedByActiveChain"] = "true"
            });

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.IsTrue(envelope.Safety.IsDeprecatedUsedByActiveChain);
    }

    // =========================================================================
    // 4. ScoreBreakdown 转换
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_ScoreBreakdown_ConvertedToDictionaryWith13Fields()
    {
        var breakdown = new ItemScoreBreakdown
        {
            BaseScore = 1.0,
            LayerScore = 0.5,
            StatusScore = 0.3,
            SemanticAnchorScore = 0.2,
            RawTokenMatchScore = 0.1,
            AnchorMatchBonus = 0.05,
            ModeMatchScore = 0.0,        // 应被跳过（零值）
            TaskIntentScore = 0.4,
            RecencyScore = 0.15,
            RelationScore = 0.0,         // 应被跳过
            LifecyclePenalty = -0.1,
            RedundancyPenalty = -0.2,
            FinalScore = 2.4
        };
        var item = new ContextMemoryItem
        {
            Id = "sb-1",
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Content = "test"
        };
        var candidate = PackageTraceCandidate.FromMemory(item, kind: "working_memory", breakdown: breakdown, estimatedTokens: 100);

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        var dict = envelope.Features.ScoreBreakdown;
        Assert.AreEqual(11, dict.Count); // 13 个字段 - 2 个零值 = 11
        Assert.AreEqual(1.0, dict["base"]);
        Assert.AreEqual(0.5, dict["layer"]);
        Assert.AreEqual(0.3, dict["status"]);
        Assert.AreEqual(0.2, dict["semanticAnchor"]);
        Assert.AreEqual(0.1, dict["rawTokenMatch"]);
        Assert.AreEqual(0.05, dict["anchorMatchBonus"]);
        Assert.AreEqual(0.4, dict["taskIntent"]);
        Assert.AreEqual(0.15, dict["recency"]);
        Assert.AreEqual(-0.1, dict["lifecyclePenalty"]);
        Assert.AreEqual(-0.2, dict["redundancyPenalty"]);
        Assert.AreEqual(2.4, dict["final"]);
        Assert.IsFalse(dict.ContainsKey("modeMatch"));
        Assert.IsFalse(dict.ContainsKey("relation"));
    }

    [TestMethod]
    public void ToEnvelope_NullScoreBreakdown_ReturnsEmptyDictionary()
    {
        // FromMemory(item, kind, score, tokens) 不填充 ScoreBreakdown（保持 null）
        var candidate = MakeCandidate("null-sb-1", "working_memory");

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        Assert.AreEqual(0, envelope.Features.ScoreBreakdown.Count);
    }

    // =========================================================================
    // 5. 批量转换
    // =========================================================================

    [TestMethod]
    public void ToEnvelopes_BatchConversion_PreservesAllCandidates()
    {
        var candidates = new[]
        {
            MakeCandidate("p1", "working_memory"),
            MakeCandidate("p2", "hard_constraint"),
            MakeCandidate("p3", "recent_context")
        };

        var envelopes = PackageCandidateAdapter.ToEnvelopes(candidates);

        Assert.AreEqual(3, envelopes.Count);
        Assert.AreEqual("p1", envelopes[0].CandidateId);
        Assert.AreEqual("p2", envelopes[1].CandidateId);
        Assert.AreEqual("p3", envelopes[2].CandidateId);
        Assert.AreEqual(ContextCandidateSource.WorkingMemory, envelopes[0].Source);
        Assert.AreEqual(ContextCandidateSource.Constraint, envelopes[1].Source);
        Assert.AreEqual(ContextCandidateSource.Recency, envelopes[2].Source);
    }

    [TestMethod]
    public void ToEnvelopes_EmptyInput_ReturnsEmptyList()
    {
        var envelopes = PackageCandidateAdapter.ToEnvelopes(Array.Empty<PackageTraceCandidate>());

        Assert.AreEqual(0, envelopes.Count);
    }

    // =========================================================================
    // 6. ToDecisionRequest 整体转换
    // =========================================================================

    [TestMethod]
    public void ToDecisionRequest_ConvertsSelectedAndDroppedIntoRequest()
    {
        var result = new ContextPackageBuildResult
        {
            BuildId = "build-1",
            SelectedItems = new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "sel-1",
                    Kind = "working_memory",
                    Score = 0.9,
                    EstimatedTokens = 100,
                    SourceRefs = new[] { "trace:s1" }
                }
            },
            DroppedItems = new[]
            {
                new DroppedContextItem
                {
                    ItemId = "drop-1",
                    Kind = "recent_context",
                    Reason = "超过 token 预算",
                    Score = 0.3,
                    EstimatedTokens = 200
                }
            }
        };

        var request = PackageCandidateAdapter.ToDecisionRequest(result, tokenBudget: 500);

        Assert.AreEqual("build-1", request.RequestId);
        Assert.AreEqual(ContextDecisionSource.Package, request.DecisionSource);
        Assert.AreEqual(500, request.TokenBudget);
        Assert.AreEqual(2, request.Candidates.Count);

        // selected 候选 passes safety gate
        var sel = request.Candidates.Single(c => c.CandidateId == "sel-1");
        Assert.IsTrue(sel.Safety.PassesSafetyGate);
        Assert.AreEqual(0.9, sel.Utility.FinalScore);
        Assert.AreEqual(ContextCandidateSource.WorkingMemory, sel.Source);

        // dropped 候选不通过 safety gate（已标记为 dropped-by-package）
        var drop = request.Candidates.Single(c => c.CandidateId == "drop-1");
        Assert.IsFalse(drop.Safety.PassesSafetyGate);
        Assert.AreEqual("dropped-by-package", drop.Utility.ReasonCode);
        Assert.AreEqual(ContextCandidateSource.Recency, drop.Source);
    }

    // =========================================================================
    // 7. 不破坏原候选（不可变性）
    // =========================================================================

    [TestMethod]
    public void ToEnvelope_DoesNotMutateOriginalCandidate()
    {
        var candidate = MakeCandidate("orig-1", "working_memory",
            score: 0.7,
            metadata: new Dictionary<string, string> { ["lifecycleStatus"] = "active" });

        var envelope = PackageCandidateAdapter.ToEnvelope(candidate);

        // 原候选不变
        Assert.AreEqual("orig-1", candidate.Id);
        Assert.AreEqual(0.7, candidate.Score);
        Assert.AreEqual("active", candidate.Metadata["lifecycleStatus"]);
        Assert.AreEqual("working_memory", candidate.Kind);

        // envelope 是新对象
        Assert.AreNotSame((object)candidate, (object)envelope);
        Assert.IsNotNull(envelope.Features);
        Assert.IsNotNull(envelope.Features.ScoreBreakdown);
    }

    // =========================================================================
    // 8. 端到端：Package result → Adapter → Engine → Projector → Package DTO
    // =========================================================================

    [TestMethod]
    public async Task EndToEnd_PackagePath_ThroughAdapter_Engine_Projector()
    {
        // Step 1: 模拟 Package 主链产出
        var packageResult = new ContextPackageBuildResult
        {
            BuildId = "e2e-build-1",
            SelectedItems = new[]
            {
                new ContextPackageDecision
                {
                    ItemId = "high",
                    Kind = "working_memory",
                    Score = 0.9,
                    EstimatedTokens = 100,
                    SourceRefs = new[] { "trace:high" }
                },
                new ContextPackageDecision
                {
                    ItemId = "low",
                    Kind = "recent_context",
                    Score = 0.3,
                    EstimatedTokens = 100,
                    SourceRefs = new[] { "trace:low" }
                }
            },
            DroppedItems = Array.Empty<DroppedContextItem>()
        };

        // Step 2: 通过适配器转换为 DecisionRequest
        var request = PackageCandidateAdapter.ToDecisionRequest(
            packageResult, tokenBudget: 150); // 只够 1 个候选

        // Step 3: 通过 Engine 决策
        var engine = new DefaultContextDecisionEngine();
        var decisionResult = await engine.DecideAsync(request);

        // 断言：Engine 选入 high（高分优先），drop low（token budget 不足）
        Assert.AreEqual(1, decisionResult.SelectedEnvelopes.Count);
        Assert.AreEqual("high", decisionResult.SelectedEnvelopes[0].CandidateId);
        Assert.AreEqual(1, decisionResult.DroppedEnvelopes.Count);
        Assert.AreEqual("low", decisionResult.DroppedEnvelopes[0].CandidateId);
        Assert.AreEqual(
            CandidateDecisionReasonCode.TokenBudgetExceeded,
            decisionResult.DroppedEnvelopes[0].Safety.BlockReasonCode);

        // Step 4: 通过 Projector 转回 Package DTO
        var projector = new PackageResultProjector();
        var finalDto = projector.Project(decisionResult);

        // Projector 输出
        Assert.AreEqual("e2e-build-1", finalDto.BuildId);
        Assert.AreEqual(1, finalDto.SelectedItems.Count);
        Assert.AreEqual("high", finalDto.SelectedItems[0].ItemId);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private static PackageTraceCandidate MakeCandidate(
        string id,
        string kind,
        double score = 0.0,
        Dictionary<string, string>? metadata = null)
    {
        var item = new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "ws",
            CollectionId = "col",
            Type = "note",
            Content = $"content-{id}",
            SourceRefs = new[] { $"trace:{id}" },
            Metadata = metadata ?? new Dictionary<string, string>()
        };
        return PackageTraceCandidate.FromMemory(item, kind: kind, score: score, estimatedTokens: 100);
    }
}
