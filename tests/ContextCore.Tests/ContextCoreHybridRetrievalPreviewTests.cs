using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

/// <summary>
/// hybrid retrieval preview 测试；验证 label-free、确定性、union 去重、风险策略、formal retrieval 禁用。
/// </summary>
[TestClass]
public class ContextCoreHybridRetrievalPreviewTests
{
    // ===== LexicalCandidateProvider =====

    [TestMethod]
    public void LexicalCandidateProvider_NoDomainFixtureLexicon_GeneratesByQueryTokensOnly()
    {
        // query tokens 直接匹配 entry 文本，不需要任何外部词表
        var entries = new[]
        {
            NewEntry("item-1", "context", text: "hybrid retrieval lexical dense candidate", tags: "retrieval"),
            NewEntry("item-2", "context", text: "completely unrelated content here", tags: "other")
        };
        var provider = new LexicalCandidateProvider();
        var profile = NewNormalProfile();

        var candidates = provider.GenerateCandidates("retrieval", entries, profile, topK: 10);

        Assert.IsTrue(candidates.Any(c => c.ItemId == "item-1"));
        Assert.IsFalse(candidates.Any(c => c.ItemId == "item-2"));
    }

    [TestMethod]
    public void LexicalCandidateProvider_NoSampleIdItemIdSpecialCase()
    {
        // 两个 entry 文本相同但 ItemId 不同，应得到相同的分数（证明不按 ItemId 特判）
        var entries = new[]
        {
            NewEntry("item-A", "context", text: "same text content here"),
            NewEntry("item-B", "context", text: "same text content here")
        };
        var provider = new LexicalCandidateProvider();
        var profile = NewNormalProfile();

        var candidates = provider.GenerateCandidates("content", entries, profile, topK: 10);

        var a = candidates.FirstOrDefault(c => c.ItemId == "item-A");
        var b = candidates.FirstOrDefault(c => c.ItemId == "item-B");
        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreEqual(a.Similarity, b.Similarity, 0.0001);
    }

    [TestMethod]
    public void LexicalCandidateProvider_Deterministic_SameInputProducesSameOutput()
    {
        var entries = new[]
        {
            NewEntry("item-1", "context", text: "deterministic lexical test"),
            NewEntry("item-2", "context", text: "another deterministic entry")
        };
        var provider = new LexicalCandidateProvider();
        var profile = NewNormalProfile();

        var run1 = provider.GenerateCandidates("deterministic", entries, profile, topK: 10);
        var run2 = provider.GenerateCandidates("deterministic", entries, profile, topK: 10);

        Assert.AreEqual(run1.Count, run2.Count);
        for (var i = 0; i < run1.Count; i++)
        {
            Assert.AreEqual(run1[i].ItemId, run2[i].ItemId);
            Assert.AreEqual(run1[i].Similarity, run2[i].Similarity, 0.0001);
        }
    }

    // ===== AnchorCandidateProvider =====

    [TestMethod]
    public void AnchorCandidateProvider_NoFixtureLexicon_MatchesSourceTagsOnly()
    {
        // anchor 仅基于 sourceTags / ItemKind，不依赖 fixture 词表
        var entries = new[]
        {
            NewEntry("item-1", "task", text: "some content", tags: "retrieval,anchor"),
            NewEntry("item-2", "note", text: "other content", tags: "unrelated")
        };
        var provider = new AnchorCandidateProvider();
        var profile = NewNormalProfile();

        var candidates = provider.GenerateCandidates("retrieval", entries, profile, topK: 10);

        Assert.IsTrue(candidates.Any(c => c.ItemId == "item-1"));
        Assert.IsFalse(candidates.Any(c => c.ItemId == "item-2"));
    }

    // ===== HybridCandidateUnionPolicy =====

    [TestMethod]
    public void HybridCandidateUnionPolicy_DedupDeterministic_MergesByItemId()
    {
        var dense = new[]
        {
            NewCandidate("item-1", source: HybridCandidateSource.Dense, similarity: 0.9),
            NewCandidate("item-2", source: HybridCandidateSource.Dense, similarity: 0.7)
        };
        var lexical = new[]
        {
            NewCandidate("item-1", source: HybridCandidateSource.Lexical, similarity: 0.5),
            NewCandidate("item-3", source: HybridCandidateSource.Lexical, similarity: 0.4)
        };
        var policy = new HybridCandidateUnionPolicy();
        var options = new HybridVectorLexicalPreviewOptions { UnionTopK = 10 };

        var unioned = policy.Union(dense, lexical, null, options);

        // item-1 来自 dense+lexical，应合并为一条
        Assert.AreEqual(3, unioned.Count);
        var item1 = unioned.First(c => c.ItemId == "item-1");
        Assert.IsTrue(item1.Metadata["hybridSources"].Contains("dense"));
        Assert.IsTrue(item1.Metadata["hybridSources"].Contains("lexical"));
        // dense 优先级更高，应保留 dense 的分数
        Assert.AreEqual(0.9, item1.Similarity, 0.0001);
    }

    [TestMethod]
    public void HybridCandidateUnionPolicy_PreservesEligibilityMetadata()
    {
        var dense = new[]
        {
            NewCandidate("item-1", source: HybridCandidateSource.Dense, similarity: 0.9, blocked: true, blockedReason: "SimilarityBelowThreshold")
        };
        var policy = new HybridCandidateUnionPolicy();
        var options = new HybridVectorLexicalPreviewOptions { UnionTopK = 10 };

        var unioned = policy.Union(dense, null, null, options);

        Assert.AreEqual(1, unioned.Count);
        Assert.AreEqual(VectorCandidateEligibilityStatuses.Blocked, unioned[0].EligibilityStatus);
        CollectionAssert.Contains(unioned[0].BlockedReasons.ToArray(), "SimilarityBelowThreshold");
    }

    [TestMethod]
    public void HybridCandidateUnionPolicy_RiskPolicyStillBlocks()
    {
        // 含 RiskAfterPolicy 的候选在 union 后仍保留该属性
        var dense = new[]
        {
            NewCandidate("item-1", source: HybridCandidateSource.Dense, similarity: 0.9, riskAfterPolicy: true)
        };
        var policy = new HybridCandidateUnionPolicy();
        var options = new HybridVectorLexicalPreviewOptions { UnionTopK = 10 };

        var unioned = policy.Union(dense, null, null, options);

        Assert.AreEqual(1, unioned.Count);
        Assert.IsTrue(unioned[0].RiskAfterPolicy);
    }

    // ===== HybridRetrievalReadinessGate =====

    [TestMethod]
    public void HybridRetrievalReadinessGate_RiskNonZero_Blocks()
    {
        var preview = NewPreview(a3Recall: 0.85, extendedRecall: 0.85, risk: 1);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15GatePassed: true);

        Assert.IsFalse(gate.Passed);
        Assert.IsTrue(gate.BlockedReasons.Contains("RiskAfterPolicyNonZero"));
        Assert.AreEqual(HybridRetrievalReadinessRecommendations.BlockedByRisk, gate.Recommendation);
    }

    [TestMethod]
    public void HybridRetrievalReadinessGate_A3RecallBelow80_Blocks()
    {
        var preview = NewPreview(a3Recall: 0.79, extendedRecall: 0.85, risk: 0);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15GatePassed: true);

        Assert.IsFalse(gate.Passed);
        Assert.IsTrue(gate.BlockedReasons.Contains("A3RecallBelow80Percent"));
    }

    [TestMethod]
    public void HybridRetrievalReadinessGate_ExtendedRecallBelow80_Blocks()
    {
        var preview = NewPreview(a3Recall: 0.85, extendedRecall: 0.79, risk: 0);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15GatePassed: true);

        Assert.IsFalse(gate.Passed);
        Assert.IsTrue(gate.BlockedReasons.Contains("ExtendedRecallBelow80Percent"));
    }

    [TestMethod]
    public void HybridRetrievalReadinessGate_PolicyViolation_Blocks()
    {
        var preview = NewPreview(a3Recall: 0.85, extendedRecall: 0.85, risk: 0);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: true, p15GatePassed: true);

        Assert.IsFalse(gate.Passed);
        Assert.IsTrue(gate.BlockedReasons.Contains("PolicyViolationDetected"));
        Assert.AreEqual(HybridRetrievalReadinessRecommendations.BlockedByPolicyViolation, gate.Recommendation);
    }

    [TestMethod]
    public void HybridRetrievalReadinessGate_FormalRetrievalRemainsDisabled()
    {
        // 即便所有条件满足，formal retrieval 也必须保持禁用
        var preview = NewPreview(a3Recall: 0.85, extendedRecall: 0.85, risk: 0);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15GatePassed: true);

        Assert.IsFalse(gate.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void HybridRetrievalReadinessGate_P15RemainsPassing()
    {
        var preview = NewPreview(a3Recall: 0.85, extendedRecall: 0.85, risk: 0);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15GatePassed: true);

        Assert.IsTrue(gate.P15GatePassed);
    }

    [TestMethod]
    public void HybridRetrievalReadinessGate_AllConditionsMet_Passes()
    {
        var preview = NewPreview(a3Recall: 0.85, extendedRecall: 0.85, risk: 0);
        var gate = new HybridRetrievalReadinessGateRunner().BuildGateReport(preview, policyViolationFound: false, p15GatePassed: true);

        Assert.IsTrue(gate.Passed);
        Assert.AreEqual(0, gate.BlockedReasons.Count);
        Assert.AreEqual(HybridRetrievalReadinessRecommendations.ReadyForVectorV4Recheck, gate.Recommendation);
    }

    // ===== HybridRetrievalPreviewFreeze =====

    [TestMethod]
    public void HybridRetrievalFreeze_AuditNotPassed_Fails()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85), NewAudit(passed: false));

        Assert.IsFalse(freeze.FreezePassed);
        Assert.IsTrue(freeze.BlockedReasons.Contains("HybridRecallRegressionAuditNotPassed"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_DenseCandidateDropped_Blocks()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85), NewAudit(denseDropped: 1));

        Assert.IsFalse(freeze.FreezePassed);
        Assert.IsTrue(freeze.BlockedReasons.Contains("DenseCandidateDropped"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_EligibilityMismatch_Blocks()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85), NewAudit(eligibilityMismatch: 1));

        Assert.IsFalse(freeze.FreezePassed);
        Assert.IsTrue(freeze.BlockedReasons.Contains("EligibilityMismatch"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_DedupOverwrite_Blocks()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85), NewAudit(dedupOverwrite: 1));

        Assert.IsFalse(freeze.FreezePassed);
        Assert.IsTrue(freeze.BlockedReasons.Contains("DedupOverwriteDetected"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_RiskNonZero_Blocks()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85, risk: 1), NewAudit());

        Assert.IsFalse(freeze.FreezePassed);
        Assert.AreEqual(HybridRetrievalReadinessRecommendations.BlockedByRisk, freeze.Recommendation);
        Assert.IsTrue(freeze.BlockedReasons.Contains("RiskAfterPolicyNonZero"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_FormalOutputChanged_Blocks()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85, formalOutputChanged: 1), NewAudit());

        Assert.IsFalse(freeze.FreezePassed);
        Assert.AreEqual(HybridRetrievalReadinessRecommendations.BlockedByFormalOutputChange, freeze.Recommendation);
        Assert.IsTrue(freeze.BlockedReasons.Contains("FormalOutputChangedNonZero"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_RecallBelow80_BlocksV4RecheckOnly()
    {
        var freeze = NewFreeze(
            NewGate(a3Recall: 0.0454, extendedRecall: 0.0313, passed: false, blockedReasons: ["A3RecallBelow80Percent", "ExtendedRecallBelow80Percent"]),
            NewAudit(a3Recall: 0.0454, extendedRecall: 0.0313));

        Assert.IsTrue(freeze.FreezePassed);
        Assert.IsFalse(freeze.V4RecheckAllowed);
        Assert.IsFalse(freeze.FormalRetrievalAllowed);
        Assert.AreEqual(HybridRetrievalReadinessRecommendations.BlockedByA3Recall, freeze.Recommendation);
    }

    [TestMethod]
    public void HybridRetrievalFreeze_FormalRetrievalRemainsDisabled()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85, formalRetrievalAllowed: true), NewAudit());

        Assert.IsFalse(freeze.FreezePassed);
        Assert.IsTrue(freeze.BlockedReasons.Contains("FormalRetrievalAllowed"));
    }

    [TestMethod]
    public void HybridRetrievalFreeze_P15RemainsPassing()
    {
        var freeze = NewFreeze(NewGate(a3Recall: 0.85, extendedRecall: 0.85), NewAudit(), p15GatePassed: true);

        Assert.IsTrue(freeze.FreezePassed);
        Assert.IsTrue(freeze.V4RecheckAllowed);
    }

    // ===== Helpers =====

    private static VectorIndexEntry NewEntry(string itemId, string itemKind, string text, string tags = "")
    {
        return new VectorIndexEntry
        {
            EntryId = itemId + "-entry",
            ItemId = itemId,
            ItemKind = itemKind,
            Layer = "context",
            EmbeddingModel = "test-model",
            EmbeddingProvider = "test-provider",
            Dimension = 8,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["indexedText"] = text,
                ["sourceTags"] = tags,
                ["lifecycle"] = "Stable"
            }
        };
    }

    private static VectorQueryProfile NewNormalProfile()
    {
        return new VectorQueryProfile
        {
            ProfileId = VectorQueryProfileIds.NormalV1,
            MinSimilarity = 0.0,
            AllowedLayers = ["context", "working_context"],
            AllowedItemKinds = ["context", "task", "note", "memory"],
            RequireKnownLifecycle = false,
            RequireCompleteLifecycleMetadata = false
        };
    }

    private static VectorQueryPreviewCandidate NewCandidate(
        string itemId,
        string source,
        double similarity,
        bool blocked = false,
        string? blockedReason = null,
        bool riskAfterPolicy = false)
    {
        return new VectorQueryPreviewCandidate
        {
            CandidateId = itemId,
            EntryId = itemId + "-entry",
            ItemId = itemId,
            ItemKind = "context",
            Layer = "context",
            Similarity = similarity,
            EligibilityStatus = blocked ? VectorCandidateEligibilityStatuses.Blocked : VectorCandidateEligibilityStatuses.Eligible,
            BlockedReasons = blockedReason is null ? Array.Empty<string>() : [blockedReason],
            RiskAfterPolicy = riskAfterPolicy,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateSource"] = source
            }
        };
    }

    private static HybridRetrievalPreviewReport NewPreview(double a3Recall, double extendedRecall, int risk)
    {
        return new HybridRetrievalPreviewReport
        {
            OperationId = "test-preview",
            Options = new HybridVectorLexicalPreviewOptions(),
            Variants =
            [
                new HybridRetrievalVariantReport
                {
                    DatasetName = "A3",
                    Variant = HybridRetrievalVariant.DenseLexicalAnchor,
                    SampleCount = 10,
                    RecallAfterPolicy = a3Recall,
                    RiskAfterPolicy = risk,
                    FormalOutputChanged = 0
                },
                new HybridRetrievalVariantReport
                {
                    DatasetName = "Extended",
                    Variant = HybridRetrievalVariant.DenseLexicalAnchor,
                    SampleCount = 20,
                    RecallAfterPolicy = extendedRecall,
                    RiskAfterPolicy = risk,
                    FormalOutputChanged = 0
                }
            ],
            Recommendation = HybridRetrievalReadinessRecommendations.KeepPreviewOnly
        };
    }

    private static HybridRetrievalPreviewFreezeReport NewFreeze(
        HybridRetrievalReadinessGateReport gate,
        HybridRetrievalRecallRegressionAuditReport audit,
        bool p15GatePassed = true)
    {
        return new HybridRetrievalPreviewFreezeRunner().BuildFreezeReport(gate, audit, p15GatePassed);
    }

    private static HybridRetrievalReadinessGateReport NewGate(
        double a3Recall,
        double extendedRecall,
        int risk = 0,
        int formalOutputChanged = 0,
        bool formalRetrievalAllowed = false,
        bool passed = true,
        IReadOnlyList<string>? blockedReasons = null)
    {
        return new HybridRetrievalReadinessGateReport
        {
            Passed = passed,
            A3RecallAfterPolicy = a3Recall,
            ExtendedRecallAfterPolicy = extendedRecall,
            RiskAfterPolicy = risk,
            FormalOutputChanged = formalOutputChanged,
            P15GatePassed = true,
            FormalRetrievalAllowed = formalRetrievalAllowed,
            BlockedReasons = blockedReasons ?? Array.Empty<string>(),
            Recommendation = passed
                ? HybridRetrievalReadinessRecommendations.ReadyForVectorV4Recheck
                : HybridRetrievalReadinessRecommendations.BlockedByA3Recall
        };
    }

    private static HybridRetrievalRecallRegressionAuditReport NewAudit(
        bool passed = true,
        double a3Recall = 0.85,
        double extendedRecall = 0.85,
        int denseDropped = 0,
        int eligibilityMismatch = 0,
        int dedupOverwrite = 0)
    {
        return new HybridRetrievalRecallRegressionAuditReport
        {
            Passed = passed,
            LegacyDenseRecallA3 = a3Recall,
            HybridDenseOnlyRecallA3 = a3Recall,
            HybridBestRecallA3 = a3Recall,
            LegacyDenseRecallExtended = extendedRecall,
            HybridDenseOnlyRecallExtended = extendedRecall,
            HybridBestRecallExtended = extendedRecall,
            DenseCandidateDroppedCount = denseDropped,
            EligibilityMismatchCount = eligibilityMismatch,
            DedupOverwriteCount = dedupOverwrite,
            UseForRuntime = false,
            FormalOutputChanged = 0,
            Recommendation = passed
                ? HybridRecallRegressionAuditRecommendations.ReadyForHybridFreeze
                : HybridRecallRegressionAuditRecommendations.KeepPreviewOnly
        };
    }
}
