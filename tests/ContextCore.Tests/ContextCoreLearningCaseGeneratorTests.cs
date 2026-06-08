using ContextCore.Abstractions;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreLearningCaseGeneratorTests
{
    private readonly RuleBasedContextLearningCaseGenerator _generator = new();

    [TestMethod]
    public void AcceptedPromotion_ShouldGeneratePositiveLearningCase()
    {
        var learningCase = _generator.Generate(CreateRecord("record-positive", "PromotionAccepted", ContextFeedbackSignal.Positive, ContextFailureType.None));

        Assert.IsNotNull(learningCase);
        Assert.AreEqual("PositivePromotionSample", learningCase!.CaseKind);
        Assert.AreEqual(ContextLearningCaseStatus.Draft, learningCase.Status);
        Assert.AreEqual("record-positive", learningCase.SourceRecordId);
    }

    [TestMethod]
    public void RejectedPromotion_ShouldGenerateFalsePositiveCase()
    {
        var learningCase = _generator.Generate(CreateRecord("record-negative", "PromotionRejected", ContextFeedbackSignal.Negative, ContextFailureType.PromotionFalsePositive));

        Assert.IsNotNull(learningCase);
        Assert.AreEqual("PromotionFalsePositive", learningCase!.CaseKind);
        Assert.AreEqual(ContextLearningCaseStatus.Draft, learningCase.Status);
        Assert.AreEqual(ContextFailureType.PromotionFalsePositive, learningCase.FailureType);
    }

    [TestMethod]
    public void ExpiredPromotion_ShouldGenerateStaleContextCase()
    {
        var learningCase = _generator.Generate(CreateRecord("record-stale", "PromotionExpired", ContextFeedbackSignal.Stale, ContextFailureType.StaleCandidate));

        Assert.IsNotNull(learningCase);
        Assert.AreEqual("StaleContextSample", learningCase!.CaseKind);
        Assert.AreEqual(ContextLearningCaseStatus.Draft, learningCase.Status);
        Assert.AreEqual(ContextFeedbackSignal.Stale, learningCase.Signal);
    }

    private static ContextLearningRecord CreateRecord(
        string recordId,
        string eventKind,
        ContextFeedbackSignal signal,
        ContextFailureType failureType)
    {
        return new ContextLearningRecord
        {
            RecordId = recordId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceKind = "ShortTermPromotionCandidate",
            SourceId = "candidate-1",
            CandidateId = "candidate-1",
            ReviewId = "review-1",
            EventKind = eventKind,
            Signal = signal,
            FailureType = failureType,
            Reason = "review reason",
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = ["event-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateTitle"] = "候选标题",
                ["candidateSummary"] = "候选摘要"
            }
        };
    }
}
