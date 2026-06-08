using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCorePolicyFeedbackDatasetTests
{
    private const string WorkspaceId = "workspace-policy-feedback";
    private const string CollectionId = "collection-policy-feedback";
    private const string SessionId = "session-policy-feedback";

    [TestMethod]
    public async Task PromotionAccept_ShouldMapToPositiveFeedback()
    {
        var store = new InMemoryShortTermPromotionCandidateStore();
        await store.SaveAsync(CreatePromotionCandidate("promotion-candidate-1"));
        await store.AppendReviewAsync(CreatePromotionReview("promotion-review-1", "accept"));

        var dataset = await new PolicyFeedbackDatasetService(store, null, null, null, null)
            .BuildAsync(WorkspaceId, CollectionId);

        var record = AssertSingleRecord(dataset, "PromotionCandidateReviewRecord");
        Assert.AreEqual(PolicyFeedbackLabels.Positive, record.Label);
        Assert.AreEqual(1, dataset.PositiveCount);
        Assert.AreEqual(0, dataset.NegativeCount);
        CollectionAssert.Contains(record.PositiveRefs.ToArray(), "evidence-promotion-review");
        Assert.AreEqual("CandidateMemory", record.TargetLayer);
    }

    [TestMethod]
    public async Task PromotionReject_ShouldMapToNegativeFeedback()
    {
        var store = new InMemoryShortTermPromotionCandidateStore();
        await store.SaveAsync(CreatePromotionCandidate("promotion-candidate-1"));
        await store.AppendReviewAsync(CreatePromotionReview("promotion-review-1", "reject"));

        var dataset = await new PolicyFeedbackDatasetService(store, null, null, null, null)
            .BuildAsync(WorkspaceId, CollectionId);

        var record = AssertSingleRecord(dataset, "PromotionCandidateReviewRecord");
        Assert.AreEqual(PolicyFeedbackLabels.Negative, record.Label);
        Assert.AreEqual(0, dataset.PositiveCount);
        Assert.AreEqual(1, dataset.NegativeCount);
        CollectionAssert.Contains(record.NegativeRefs.ToArray(), "evidence-promotion-review");
    }

    [TestMethod]
    public async Task StableReviewAccept_ShouldMapToPositiveFeedback()
    {
        var store = new InMemoryStableReviewCandidateStore();
        await store.SaveAsync(CreateStableCandidate("stable-review-candidate-1"));
        await store.AppendReviewAsync(CreateStableReview("stable-review-1", "accept"));

        var dataset = await new PolicyFeedbackDatasetService(null, store, null, null, null)
            .BuildAsync(WorkspaceId, CollectionId);

        var record = AssertSingleRecord(dataset, "StableReviewRecord");
        Assert.AreEqual(PolicyFeedbackLabels.Positive, record.Label);
        Assert.AreEqual("StableMemory", record.TargetLayer);
        CollectionAssert.Contains(record.EvidenceRefs.ToArray(), "evidence-stable-review");
    }

    [TestMethod]
    public async Task ConstraintGapAccept_ShouldMapToPositiveFeedback()
    {
        var store = new InMemoryConstraintGapCandidateStore();
        await store.SaveAsync(CreateConstraintGap("constraint-gap-1"));
        await store.AppendReviewAsync(CreateConstraintGapReview("constraint-gap-review-1", "accept"));

        var dataset = await new PolicyFeedbackDatasetService(null, null, store, null, null)
            .BuildAsync(WorkspaceId, CollectionId);

        var record = AssertSingleRecord(dataset, "ConstraintGapReviewRecord");
        Assert.AreEqual(PolicyFeedbackLabels.Positive, record.Label);
        Assert.AreEqual("Hard", record.TargetLayer);
        Assert.AreEqual("sample-constraint-gap", record.Metadata["sourceSampleId"]);
    }

    [TestMethod]
    public async Task CandidateConstraintReject_ShouldMapToNegativeFeedback()
    {
        var constraintStore = new InMemoryConstraintStore();
        var reviewStore = new InMemoryCandidateConstraintReviewStore();
        await constraintStore.SaveAsync(CreateCandidateConstraint("candidate-constraint-1"));
        await reviewStore.AppendReviewAsync(CreateCandidateConstraintReview("candidate-constraint-review-1", "reject"));

        var dataset = await new PolicyFeedbackDatasetService(null, null, null, reviewStore, constraintStore)
            .BuildAsync(WorkspaceId, CollectionId);

        var record = AssertSingleRecord(dataset, "CandidateConstraintReviewRecord");
        Assert.AreEqual(PolicyFeedbackLabels.Negative, record.Label);
        Assert.AreEqual("RejectedCandidateConstraint", record.TargetLayer);
        CollectionAssert.Contains(record.NegativeRefs.ToArray(), "evidence-candidate-constraint-review");
    }

    [TestMethod]
    public async Task Export_ShouldReturnJsonLinesCompatibleRecords()
    {
        var store = new InMemoryShortTermPromotionCandidateStore();
        await store.SaveAsync(CreatePromotionCandidate("promotion-candidate-1"));
        await store.AppendReviewAsync(CreatePromotionReview("promotion-review-1", "accept"));

        var jsonl = await new PolicyFeedbackDatasetService(store, null, null, null, null)
            .ExportJsonLinesAsync(WorkspaceId, CollectionId);

        var lines = jsonl.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(1, lines.Length);
        var record = JsonSerializer.Deserialize<PolicyFeedbackRecord>(lines[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(record);
        Assert.AreEqual(PolicyFeedbackLabels.Positive, record!.Label);
        Assert.AreEqual("PromotionCandidateReviewRecord", record.SourceType);
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderDatasetSummary()
    {
        var snapshot = new ServicePolicyFeedbackDatasetSnapshot
        {
            CurrentTime = DateTimeOffset.Parse("2026-06-06T12:00:00Z"),
            BaseUrl = "http://localhost:5079/",
            Limit = 50,
            Offset = 0,
            Dataset = new PolicyFeedbackDataset
            {
                DatasetId = "dataset-test",
                Name = "Policy Feedback Dataset",
                Scope = $"workspace:{WorkspaceId}/collection:{CollectionId}",
                PositiveCount = 1,
                NegativeCount = 1,
                NeutralCount = 0,
                PolicyVersion = "policy-feedback-dataset/v1",
                EvalBaselineRef = "docs/eval-baseline-p15.md",
                SourceTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PromotionCandidateReviewRecord"] = 1,
                    ["CandidateConstraintReviewRecord"] = 1
                },
                Records =
                [
                    new PolicyFeedbackRecord
                    {
                        FeedbackRecordId = "policy-feedback-1",
                        WorkspaceId = WorkspaceId,
                        CollectionId = CollectionId,
                        SourceType = "PromotionCandidateReviewRecord",
                        SourceId = "promotion-review-1",
                        Action = "accept",
                        Label = PolicyFeedbackLabels.Positive,
                        Reason = "accepted",
                        PositiveRefs = ["evidence-1"],
                        EvidenceRefs = ["evidence-1"],
                        TargetLayer = "CandidateMemory",
                        CreatedAt = DateTimeOffset.Parse("2026-06-06T12:00:00Z"),
                        Reviewer = "reviewer-a",
                        PolicyVersion = "policy-feedback-dataset/v1"
                    }
                ]
            }
        };

        var output = ServiceOperationalRenderer.RenderPolicyFeedbackDataset(snapshot);

        StringAssert.Contains(output, "Service Policy Feedback Dataset");
        StringAssert.Contains(output, "positive=1 negative=1 neutral=0");
        StringAssert.Contains(output, "PromotionCandidateReviewRecord: 1");
        StringAssert.Contains(output, "policy-feedback-1");
    }

    private static PolicyFeedbackRecord AssertSingleRecord(PolicyFeedbackDataset dataset, string sourceType)
    {
        Assert.AreEqual(1, dataset.Records.Count);
        Assert.AreEqual(1, dataset.SourceTypes[sourceType]);
        Assert.AreEqual(PolicyFeedbackDatasetService.PolicyVersion, dataset.PolicyVersion);
        Assert.AreEqual(PolicyFeedbackDatasetService.EvalBaselineRef, dataset.EvalBaselineRef);
        return dataset.Records[0];
    }

    private static ShortTermPromotionCandidate CreatePromotionCandidate(string candidateId)
        => new()
        {
            CandidateId = candidateId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            SourceWorkingItemId = "working-item-1",
            Kind = "Preference",
            Title = "promotion title",
            Summary = "promotion summary",
            SuggestedTargetLayer = "CandidateMemory",
            Reason = "promotion reason",
            Confidence = 0.9,
            Importance = 0.8,
            EvidenceRefs = ["evidence-promotion-candidate"],
            CreatedAt = DateTimeOffset.UtcNow,
            Status = PromotionCandidateStatus.Candidate,
            PolicyVersion = "promotion-policy/v1"
        };

    private static PromotionCandidateReviewRecord CreatePromotionReview(string reviewId, string action)
        => new()
        {
            ReviewId = reviewId,
            CandidateId = "promotion-candidate-1",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            Action = action,
            FromStatus = PromotionCandidateStatus.Candidate,
            ToStatus = string.Equals(action, "accept", StringComparison.OrdinalIgnoreCase)
                ? PromotionCandidateStatus.Accepted
                : PromotionCandidateStatus.Rejected,
            Reviewer = "reviewer-a",
            Reason = "promotion review reason",
            TargetLayer = "CandidateMemory",
            EvidenceRefs = ["evidence-promotion-review"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };

    private static StableReviewCandidate CreateStableCandidate(string candidateId)
        => new()
        {
            StableReviewCandidateId = candidateId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            SourceCandidateId = "promotion-candidate-1",
            SourceTargetItemId = "candidate-memory-1",
            SourceLearningCaseId = "learning-case-1",
            Kind = "StableMemory",
            Title = "stable title",
            Summary = "stable summary",
            SuggestedStableTarget = "StableMemory",
            Reason = "stable reason",
            Confidence = 0.9,
            Importance = 0.8,
            EvidenceRefs = ["evidence-stable-candidate"],
            CreatedAt = DateTimeOffset.UtcNow,
            Status = StableReviewCandidateStatuses.Candidate
        };

    private static StableReviewRecord CreateStableReview(string reviewId, string action)
        => new()
        {
            ReviewId = reviewId,
            StableReviewCandidateId = "stable-review-candidate-1",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            Action = action,
            FromStatus = StableReviewCandidateStatuses.Candidate,
            ToStatus = StableReviewCandidateStatuses.Accepted,
            Reviewer = "reviewer-a",
            Reason = "stable review reason",
            TargetLayer = "StableMemory",
            SourcePromotionCandidateId = "promotion-candidate-1",
            SourceTargetItemId = "candidate-memory-1",
            SourceLearningCaseId = "learning-case-1",
            EvidenceRefs = ["evidence-stable-review"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };

    private static ConstraintGapCandidate CreateConstraintGap(string gapId)
        => new()
        {
            GapId = gapId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            Source = "planning-optin-constraint-safety-report",
            SourceSampleId = "sample-constraint-gap",
            SourceOperationId = "operation-constraint-gap",
            ExpectedConstraintText = "hard constraint text",
            SuggestedConstraintTitle = "constraint title",
            SuggestedConstraintType = "Hard",
            Reason = "constraint gap reason",
            EvidenceRefs = ["evidence-constraint-gap"],
            Status = ConstraintGapStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ConstraintGapReviewRecord CreateConstraintGapReview(string reviewId, string action)
        => new()
        {
            ReviewId = reviewId,
            GapId = "constraint-gap-1",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            Action = action,
            FromStatus = ConstraintGapStatus.Pending,
            ToStatus = ConstraintGapStatus.Accepted,
            Reviewer = "reviewer-a",
            Reason = "constraint gap review reason",
            CreatedConstraintId = "candidate-constraint-1",
            TargetLayer = "Hard",
            SourceSampleId = "sample-constraint-gap",
            SourceOperationId = "operation-constraint-gap",
            ExpectedConstraintText = "hard constraint text",
            EvidenceRefs = ["evidence-constraint-gap-review"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };

    private static ContextConstraint CreateCandidateConstraint(string constraintId)
        => new()
        {
            Id = constraintId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Hard,
            Content = "candidate hard constraint",
            SourceRefs = ["evidence-candidate-constraint"],
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.95,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceConstraintGapId"] = "constraint-gap-1",
                ["createdFrom"] = "constraint_gap_accept",
                ["evidenceRefs"] = "evidence-candidate-constraint-metadata"
            }
        };

    private static CandidateConstraintReviewRecord CreateCandidateConstraintReview(string reviewId, string action)
        => new()
        {
            ReviewId = reviewId,
            ConstraintId = "candidate-constraint-1",
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Action = action,
            FromStatus = ContextMemoryStatus.Candidate,
            ToStatus = ContextMemoryStatus.Rejected,
            Reviewer = "reviewer-a",
            Reason = "candidate constraint review reason",
            SourceConstraintGapId = "constraint-gap-1",
            SourceSampleId = "sample-constraint-gap",
            SourceOperationId = "operation-constraint-gap",
            EvidenceRefs = ["evidence-candidate-constraint-review"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        };
}
