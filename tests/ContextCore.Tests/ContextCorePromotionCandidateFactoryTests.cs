using ContextCore.Abstractions.Models;
using ContextCore.Core;

namespace ContextCore.Tests;

/// <summary>覆盖 Promotion Review 候选状态的初始映射。</summary>
[TestClass]
public sealed class ContextCorePromotionCandidateFactoryTests
{
    [TestMethod]
    public void PromotionCandidateFactory_ShouldCreateCandidateForPromotableEvaluation()
    {
        var factory = new BasicPromotionCandidateFactory();
        var request = Request("阶段性结论：Promotion 条件评估器已可用。");
        var evaluation = new PromotionEvaluationResult
        {
            Decision = PromotionEvaluationDecision.PromoteToWorkingMemory,
            TargetLayer = ContextMemoryLayer.Working,
            Category = "阶段性结论",
            Reason = "命中中期记忆 Promotion 条件。",
            Score = 0.8,
            MatchedRules = ["阶段性结论"]
        };

        var candidate = factory.CreateCandidate(request, evaluation, sourceKind: "context");

        Assert.AreEqual(PromotionCandidateStatus.Candidate, candidate.Status);
        Assert.AreEqual(ContextMemoryLayer.Working, candidate.TargetLayer);
        Assert.AreEqual("context-source", candidate.SourceId);
        Assert.AreEqual("context", candidate.SourceKind);
        Assert.AreEqual("阶段性结论", candidate.Category);
        Assert.AreEqual(0.8, candidate.Confidence);
        CollectionAssert.Contains(candidate.MatchedRules.ToArray(), "阶段性结论");
    }

    [TestMethod]
    public void PromotionCandidateFactory_ShouldCreateNeedsReviewCandidateForUnclearEvaluation()
    {
        var factory = new BasicPromotionCandidateFactory();
        var candidate = factory.CreateCandidate(
            Request("这段内容有一定价值，但规则信号不足。"),
            new PromotionEvaluationResult
            {
                Decision = PromotionEvaluationDecision.NeedsReview,
                Category = "规则信号不足",
                Reason = "需要审核。",
                Score = 0.3,
                RequiresReview = true
            });

        Assert.AreEqual(PromotionCandidateStatus.NeedsReview, candidate.Status);
        Assert.IsNull(candidate.TargetLayer);
    }

    [TestMethod]
    public void PromotionCandidateFactory_ShouldCreateRejectedCandidateForNoPromotionEvaluation()
    {
        var factory = new BasicPromotionCandidateFactory();
        var candidate = factory.CreateCandidate(
            Request("普通寒暄：你好，谢谢。"),
            new PromotionEvaluationResult
            {
                Decision = PromotionEvaluationDecision.DoNotPromote,
                Category = "普通寒暄",
                Reason = "命中禁止提升规则。",
                Score = 0.9,
                MatchedRules = ["普通寒暄"]
            });

        Assert.AreEqual(PromotionCandidateStatus.Rejected, candidate.Status);
        Assert.IsNull(candidate.TargetLayer);
        Assert.AreEqual(PromotionEvaluationDecision.DoNotPromote, candidate.Decision);
    }

    [TestMethod]
    public void PromotionCandidateStatus_ShouldCoverReviewLifecycle()
    {
        var statuses = Enum.GetNames<PromotionCandidateStatus>();

        CollectionAssert.Contains(statuses, nameof(PromotionCandidateStatus.Candidate));
        CollectionAssert.Contains(statuses, nameof(PromotionCandidateStatus.Accepted));
        CollectionAssert.Contains(statuses, nameof(PromotionCandidateStatus.Rejected));
        CollectionAssert.Contains(statuses, nameof(PromotionCandidateStatus.NeedsReview));
        CollectionAssert.Contains(statuses, nameof(PromotionCandidateStatus.Superseded));
    }

    private static PromotionEvaluationRequest Request(string content)
    {
        return new PromotionEvaluationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "context-source",
            Type = "note",
            Content = content,
            SourceRefs = ["source:context-source"],
            Metadata = new Dictionary<string, string> { ["task"] = "promotion-review" }
        };
    }
}
