using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreLearningFeatureDatasetTests
{
    private const string WorkspaceId = "workspace-learning-features";
    private const string CollectionId = "collection-learning-features";
    private const string SessionId = "session-learning-features";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void PromotionAccept_ShouldMapToPositiveFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-promotion-accept",
            "PromotionCandidateReviewRecord",
            "promotion-review-1",
            "accept",
            PolicyFeedbackLabels.Positive,
            "CandidateMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateId"] = "promotion-candidate-1",
                ["candidateKind"] = "Preference",
                ["candidateStatus"] = "Accepted",
                ["candidateImportance"] = "0.87",
                ["keywordMatchScore"] = "0.42",
                ["shortTermMatchScore"] = "0.76"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual("PolicyFeedback", example.TaskKind);
        Assert.AreEqual(PolicyFeedbackLabels.Positive, example.Label);
        Assert.AreEqual("promotion-candidate-1", example.CandidateId);
        Assert.AreEqual("Preference", example.CandidateKind);
        Assert.AreEqual("CandidateMemory", example.CandidateLayer);
        Assert.IsTrue(example.Accepted);
        Assert.IsFalse(example.Rejected);
        Assert.AreEqual(0.87, example.CandidateImportance, 0.001);
        Assert.AreEqual(0.42, example.KeywordMatchScore, 0.001);
        Assert.AreEqual(0.76, example.ShortTermMatchScore, 0.001);
        CollectionAssert.Contains(example.EvidenceRefs.ToArray(), "evidence-policy-promotion-accept");
    }

    [TestMethod]
    public void PromotionReject_ShouldMapToNegativeFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-promotion-reject",
            "PromotionCandidateReviewRecord",
            "promotion-review-2",
            "reject",
            PolicyFeedbackLabels.Negative,
            "RejectedCandidateMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateId"] = "promotion-candidate-2",
                ["candidateKind"] = "KnownIssue",
                ["candidateStatus"] = "Rejected"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual(PolicyFeedbackLabels.Negative, example.Label);
        Assert.AreEqual("promotion-candidate-2", example.CandidateId);
        Assert.IsFalse(example.Accepted);
        Assert.IsTrue(example.Rejected);
        Assert.AreEqual(1, example.LifecycleRisk);
    }

    [TestMethod]
    public async Task RuntimeLearningFeedbackService_ShouldSubmitFilterExportSummarizeAndDeduplicate()
    {
        var store = new InMemoryLearningFeedbackStore();
        var service = new LearningFeedbackService(store);
        var feedback = CreateRuntimeFeedback(metadataOnly: true);

        var created = await service.SubmitAsync(feedback);
        var replaced = await service.SubmitAsync(feedback);
        var rows = await service.ListAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            FeedbackKind = LearningFeedbackKinds.MissingContext,
            Limit = 20
        });
        var summary = await service.BuildSummaryAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId
        });
        var jsonl = await service.ExportJsonLinesAsync(new LearningFeedbackEventQuery
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId
        });

        Assert.IsTrue(created.Created);
        Assert.IsTrue(replaced.DuplicateReplaced);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(string.Empty, rows[0].Reason);
        Assert.AreEqual(string.Empty, rows[0].UserCorrection);
        Assert.AreEqual("metadata-only", rows[0].Metadata["redactionMode"]);
        Assert.AreEqual(1, summary.FeedbackCount);
        Assert.AreEqual(1, summary.FeedbackByCapability[ShadowCapabilityIds.VectorRetrieval]);
        Assert.AreEqual(1, summary.FeedbackByTargetType[LearningFeedbackTargetType.VectorCandidate.ToString()]);
        Assert.AreEqual(1, summary.MetadataOnlyCount);
        Assert.AreEqual(1, summary.TrainingUseDisabledCount);
        StringAssert.Contains(jsonl, rows[0].FeedbackId);
    }

    [TestMethod]
    public async Task RuntimeLearningFeedbackService_ShouldRejectInvalidCapability()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = new LearningFeedbackEvent
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            CapabilityId = "UnsupportedCapability",
            FeedbackKind = LearningFeedbackKinds.Useful
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(feedback));
    }

    [TestMethod]
    public async Task RuntimeLearningFeedbackService_ShouldRejectInvalidTargetType()
    {
        var service = new LearningFeedbackService(new InMemoryLearningFeedbackStore());
        var feedback = new LearningFeedbackEvent
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            CapabilityId = ShadowCapabilityIds.VectorRetrieval,
            TargetId = "candidate-1",
            TargetType = "UnsupportedTarget",
            FeedbackKind = LearningFeedbackKinds.Useful
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => service.SubmitAsync(feedback));
    }

    [TestMethod]
    public async Task FileLearningFeedbackStore_ShouldPersistAndQuery()
    {
        var root = Path.Combine(Path.GetTempPath(), "contextcore-feedback-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileLearningFeedbackStore(new FileStorageOptions { RootPath = root });
            var service = new LearningFeedbackService(store);
            var feedback = CreateRuntimeFeedback(metadataOnly: false);

            var result = await service.SubmitAsync(feedback);
            var found = await store.GetAsync(result.FeedbackId);
            var rows = await store.QueryAsync(new LearningFeedbackEventQuery
            {
                WorkspaceId = WorkspaceId,
                CollectionId = CollectionId,
                TargetId = "candidate-1",
                Limit = 10
            });

            Assert.IsNotNull(found);
            Assert.AreEqual(result.FeedbackId, found.FeedbackId);
            Assert.AreEqual(1, rows.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task FeedbackReviewSummary_ShouldTreatNewFeedbackAsPendingReview()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        var feedbackService = new LearningFeedbackService(feedbackStore);
        await feedbackService.SubmitAsync(CreateRuntimeFeedback(metadataOnly: true));

        var summary = await new LearningFeedbackReviewService(feedbackStore, reviewStore)
            .BuildSummaryAsync(DefaultFeedbackQuery(), new LearningFeedbackReviewQuery());

        Assert.AreEqual(1, summary.FeedbackCount);
        Assert.AreEqual(1, summary.PendingReviewCount);
        Assert.AreEqual(0, summary.ApprovedCount);
        Assert.AreEqual("disabled_until_review", (await feedbackStore.QueryAsync(DefaultFeedbackQuery()))[0].TrainingUse);
    }

    [TestMethod]
    public async Task FeedbackFeatureCandidateBuilder_ShouldSkipUnreviewedFeedback()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(metadataOnly: false, feedbackId: "feedback-unreviewed"));

        var report = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());

        Assert.AreEqual(0, report.GeneratedCandidateCount);
        Assert.AreEqual(1, report.PendingReviewCount);
    }

    [TestMethod]
    public async Task ApprovedFeedback_ShouldGenerateFeatureCandidate()
    {
        var (feedbackStore, reviewStore, feedback) = await CreateReviewedFeedbackAsync(
            "feedback-approved",
            metadataOnly: false,
            FeedbackReviewStatus.ApprovedForDataset);

        var report = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());

        Assert.AreEqual(1, report.GeneratedCandidateCount);
        Assert.AreEqual(feedback.FeedbackId, report.Candidates[0].SourceFeedbackId);
        Assert.AreEqual(ShadowCapabilityIds.VectorRetrieval, report.Candidates[0].CapabilityId);
        Assert.IsTrue(report.Candidates[0].PositiveLabel);
        Assert.AreEqual("approved_for_dataset", report.Candidates[0].TrainingUse);
    }

    [TestMethod]
    public async Task RejectedFeedback_ShouldNotGenerateFeatureCandidate()
    {
        var (feedbackStore, reviewStore, _) = await CreateReviewedFeedbackAsync(
            "feedback-rejected",
            metadataOnly: false,
            FeedbackReviewStatus.Rejected);

        var report = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());

        Assert.AreEqual(0, report.GeneratedCandidateCount);
        Assert.AreEqual(1, report.RejectedCount);
    }

    [TestMethod]
    public async Task NeedsRedactionFeedback_ShouldNotGenerateFeatureCandidate()
    {
        var (feedbackStore, reviewStore, _) = await CreateReviewedFeedbackAsync(
            "feedback-needs-redaction",
            metadataOnly: false,
            FeedbackReviewStatus.NeedsRedaction);

        var report = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());

        Assert.AreEqual(0, report.GeneratedCandidateCount);
        Assert.AreEqual(1, report.NeedsRedactionCount);
    }

    [TestMethod]
    public async Task MetadataOnlyFeedback_ShouldNotLeakRawTextIntoFeatureCandidate()
    {
        var (feedbackStore, reviewStore, _) = await CreateReviewedFeedbackAsync(
            "feedback-metadata-only",
            metadataOnly: true,
            FeedbackReviewStatus.ApprovedForDataset);

        var report = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());
        var candidate = report.Candidates.Single();

        Assert.AreEqual(string.Empty, candidate.QueryText);
        Assert.AreEqual(string.Empty, candidate.Reason);
        Assert.AreEqual("metadata-only", candidate.RedactionStatus);
        Assert.AreEqual("true", candidate.Metadata["metadataSafe"]);
    }

    [TestMethod]
    public async Task MissingFeedbackBinding_ShouldMarkNeedsMoreEvidenceWhenRequested()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        var feedback = await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(
                metadataOnly: false,
                feedbackId: "feedback-missing-binding",
                sourceOperationId: string.Empty));
        await new LearningFeedbackReviewService(feedbackStore, reviewStore)
            .ApproveAsync(feedback.FeedbackId, ApprovedReviewRequest());

        var report = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery(), updateNeedsMoreEvidence: true);
        var review = (await reviewStore.QueryAsync(new LearningFeedbackReviewQuery
        {
            FeedbackId = feedback.FeedbackId,
            Limit = 10
        })).Single();

        Assert.AreEqual(0, report.GeneratedCandidateCount);
        Assert.AreEqual(1, report.NeedsMoreEvidenceCount);
        Assert.AreEqual(FeedbackReviewStatus.NeedsMoreEvidence, review.ReviewStatus);
    }

    [TestMethod]
    public async Task DuplicateFeedbackFeatureCandidate_ShouldHaveStableCandidateId()
    {
        var (feedbackStore, reviewStore, _) = await CreateReviewedFeedbackAsync(
            "feedback-stable-candidate",
            metadataOnly: false,
            FeedbackReviewStatus.ApprovedForDataset);
        var builder = new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore);

        var first = await builder.BuildAsync(DefaultFeedbackQuery());
        var second = await builder.BuildAsync(DefaultFeedbackQuery());

        Assert.AreEqual(1, first.GeneratedCandidateCount);
        Assert.AreEqual(1, second.GeneratedCandidateCount);
        Assert.AreEqual(first.Candidates[0].CandidateId, second.Candidates[0].CandidateId);
    }

    [TestMethod]
    public async Task LearningFeedbackQuality_ShouldRecommendReviewForPendingOnlyFeedback()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(metadataOnly: true, feedbackId: "feedback-quality-pending"));

        var report = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);

        Assert.AreEqual(1, report.FeedbackCount);
        Assert.AreEqual(1, report.PendingReviewCount);
        Assert.AreEqual(LearningFeedbackQualityRecommendations.NeedsReviewedFeedback, report.Recommendation);
    }

    [TestMethod]
    public async Task LearningFeedbackQuality_ShouldRequireRedactionReviewForUncheckedApprovedFeedback()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        var feedback = await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(metadataOnly: false, feedbackId: "feedback-quality-redaction"));
        await new LearningFeedbackReviewService(feedbackStore, reviewStore)
            .ApproveAsync(feedback.FeedbackId, new LearningFeedbackReviewRequest
            {
                Reviewer = "reviewer-test",
                ReviewReason = "approved but redaction was not checked",
                RedactionChecked = false,
                TrainingUse = "approved_for_dataset"
            });

        var report = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);

        Assert.AreEqual(1, report.ApprovedCount);
        Assert.AreEqual(1, report.NeedsRedactionCount);
        Assert.AreEqual(LearningFeedbackQualityRecommendations.NeedsRedactionReview, report.Recommendation);
    }

    [TestMethod]
    public async Task LearningFeedbackQuality_ShouldMarkCapabilityReadyWhenApprovedLabelsAreCovered()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-quality-positive", LearningFeedbackKinds.MissingContext, metadataOnly: false);
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-quality-negative", LearningFeedbackKinds.DeprecatedContext, metadataOnly: false);

        var report = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);
        var readiness = report.ApprovedDatasetReadiness.Single(item => item.CapabilityId == ShadowCapabilityIds.VectorRetrieval);

        Assert.AreEqual(2, report.FeatureCandidateCount);
        Assert.IsTrue(readiness.Ready);
        Assert.AreEqual(1, readiness.PositiveLabelCount);
        Assert.AreEqual(1, readiness.NegativeLabelCount);
        Assert.AreEqual(LearningFeedbackQualityRecommendations.ReadyForOfflineBaseline, report.Recommendation);
    }

    [TestMethod]
    public async Task LearningFeedbackQuality_ShouldBlockMetadataOnlyInsufficientCapability()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-quality-meta-positive", LearningFeedbackKinds.MissingContext, metadataOnly: true);
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-quality-meta-negative", LearningFeedbackKinds.DeprecatedContext, metadataOnly: true);

        var report = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);
        var readiness = report.ApprovedDatasetReadiness.Single(item => item.CapabilityId == ShadowCapabilityIds.VectorRetrieval);

        Assert.IsFalse(readiness.Ready);
        CollectionAssert.Contains(readiness.BlockedReasons.ToArray(), LearningFeedbackQualityBlockedReasons.MetadataOnlyInsufficient);
        Assert.AreEqual(2, readiness.MetadataOnlyCount);
    }

    [TestMethod]
    public async Task LearningFeedbackQuality_ShouldBlockMissingPositiveOrNegativeLabels()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-quality-positive-only", LearningFeedbackKinds.MissingContext, metadataOnly: false);

        var report = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);
        var readiness = report.ApprovedDatasetReadiness.Single(item => item.CapabilityId == ShadowCapabilityIds.VectorRetrieval);

        Assert.IsFalse(readiness.Ready);
        CollectionAssert.Contains(readiness.BlockedReasons.ToArray(), LearningFeedbackQualityBlockedReasons.MissingNegativeSamples);
        Assert.AreEqual(LearningFeedbackQualityRecommendations.NeedsLabelCoverage, report.Recommendation);
    }

    [TestMethod]
    public async Task LearningFeedbackQuality_ShouldIgnoreReviewsOutsideQueriedFeedbackSet()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(metadataOnly: true, feedbackId: "feedback-quality-pending-isolated"));
        await reviewStore.UpsertAsync(new LearningFeedbackReviewRecord
        {
            FeedbackId = "feedback-other-workspace-approved",
            Reviewer = "reviewer-test",
            ReviewStatus = FeedbackReviewStatus.ApprovedForDataset,
            ReviewReason = "unrelated approved review",
            RedactionChecked = true,
            TrainingUse = "approved_for_dataset",
            ReviewedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["workspaceId"] = "other-workspace",
                ["collectionId"] = "other-collection"
            }
        });

        var report = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);

        Assert.AreEqual(0, report.ApprovedCount);
        Assert.AreEqual(1, report.PendingReviewCount);
        Assert.AreEqual(LearningFeedbackQualityRecommendations.NeedsReviewedFeedback, report.Recommendation);
    }

    [TestMethod]
    public async Task FeedbackReviewOperations_ShouldRecordApproveRejectRedactionAndEvidence()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        var feedbackService = new LearningFeedbackService(feedbackStore);
        var reviewService = new LearningFeedbackReviewService(feedbackStore, reviewStore);
        var approved = await feedbackService.SubmitAsync(CreateRuntimeFeedback(metadataOnly: true, feedbackId: "feedback-review-approved"));
        var rejected = await feedbackService.SubmitAsync(CreateRuntimeFeedback(metadataOnly: true, feedbackId: "feedback-review-rejected"));
        var redaction = await feedbackService.SubmitAsync(CreateRuntimeFeedback(metadataOnly: false, feedbackId: "feedback-review-redaction"));
        var evidence = await feedbackService.SubmitAsync(CreateRuntimeFeedback(metadataOnly: true, feedbackId: "feedback-review-evidence"));

        await reviewService.ApproveAsync(approved.FeedbackId, ApprovedReviewRequest());
        await reviewService.RejectAsync(rejected.FeedbackId, ApprovedReviewRequest());
        await reviewService.NeedsRedactionAsync(redaction.FeedbackId, ApprovedReviewRequest());
        await reviewService.NeedsMoreEvidenceAsync(evidence.FeedbackId, ApprovedReviewRequest());
        var summary = await reviewService.BuildSummaryAsync(DefaultFeedbackQuery(), new LearningFeedbackReviewQuery());

        Assert.AreEqual(4, summary.FeedbackCount);
        Assert.AreEqual(1, summary.ApprovedCount);
        Assert.AreEqual(1, summary.RejectedCount);
        Assert.AreEqual(1, summary.NeedsRedactionCount);
        Assert.AreEqual(1, summary.NeedsMoreEvidenceCount);
    }

    [TestMethod]
    public async Task SmokeFeedback_ShouldGenerateCandidateButRemainExcludedFromTrainableDataset()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        var feedback = await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(
                metadataOnly: true,
                feedbackId: "feedback-smoke-excluded"));
        await new LearningFeedbackReviewService(feedbackStore, reviewStore)
            .ApproveAsync(feedback.FeedbackId, new LearningFeedbackReviewRequest
            {
                Reviewer = "reviewer-test",
                ReviewReason = "smoke review",
                RedactionChecked = true,
                TrainingUse = "smoke_test_only",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["excludedFromTraining"] = "true"
                }
            });

        var candidates = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());
        var quality = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);
        var gate = new LearningApprovedFeedbackDatasetGateBuilder()
            .Build(quality, candidates);

        Assert.AreEqual(1, candidates.GeneratedCandidateCount);
        Assert.AreEqual("true", candidates.Candidates[0].Metadata["excludedFromTraining"]);
        Assert.AreEqual(0, gate.TrainableCandidateCount);
        Assert.AreEqual(1, gate.SmokeExcludedCount);
        CollectionAssert.Contains(gate.FailureReasons.ToArray(), LearningApprovedFeedbackDatasetGateFailureReasons.NoTrainableCandidates);
    }

    [TestMethod]
    public async Task ApprovedFeedbackDatasetGate_ShouldFailWhenNoApprovedFeedbackExists()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(metadataOnly: true, feedbackId: "feedback-gate-pending"));

        var candidates = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());
        var quality = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);
        var gate = new LearningApprovedFeedbackDatasetGateBuilder()
            .Build(quality, candidates);

        Assert.IsFalse(gate.Passed);
        CollectionAssert.Contains(gate.FailureReasons.ToArray(), LearningApprovedFeedbackDatasetGateFailureReasons.NoApprovedFeedback);
        CollectionAssert.Contains(gate.FailureReasons.ToArray(), LearningApprovedFeedbackDatasetGateFailureReasons.NeedsReviewedFeedback);
    }

    [TestMethod]
    public async Task ApprovedFeedbackDatasetGate_ShouldPassWhenCapabilityHasTrainablePositiveAndNegativeLabels()
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-gate-positive", LearningFeedbackKinds.MissingContext, metadataOnly: false);
        await SubmitApprovedFeedbackAsync(feedbackStore, reviewStore, "feedback-gate-negative", LearningFeedbackKinds.DeprecatedContext, metadataOnly: false);

        var candidates = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(DefaultFeedbackQuery());
        var quality = await BuildFeedbackQualityReportAsync(feedbackStore, reviewStore);
        var gate = new LearningApprovedFeedbackDatasetGateBuilder()
            .Build(quality, candidates);

        Assert.IsTrue(gate.Passed);
        Assert.AreEqual(2, gate.TrainableCandidateCount);
        Assert.AreEqual(0, gate.SmokeExcludedCount);
        Assert.AreEqual(1, gate.PositiveLabelCountByCapability[ShadowCapabilityIds.VectorRetrieval]);
        Assert.AreEqual(1, gate.NegativeLabelCountByCapability[ShadowCapabilityIds.VectorRetrieval]);
    }

    [TestMethod]
    public void StableAccept_ShouldMapToPositiveFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-stable-accept",
            "StableReviewRecord",
            "stable-review-1",
            "accept",
            PolicyFeedbackLabels.Positive,
            "StableMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["stableReviewCandidateId"] = "stable-review-candidate-1",
                ["suggestedStableTarget"] = "StableMemory",
                ["candidateStatus"] = "Accepted"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual("StableReviewRecord", example.SourceType);
        Assert.AreEqual("stable-review-candidate-1", example.CandidateId);
        Assert.AreEqual("StableMemory", example.CandidateKind);
        Assert.AreEqual("StableMemory", example.CandidateLayer);
        Assert.IsTrue(example.Accepted);
    }

    [TestMethod]
    public void ConstraintGapAccept_ShouldMapToPositiveFeatureExample()
    {
        var dataset = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-constraint-gap-accept",
            "ConstraintGapReviewRecord",
            "constraint-gap-review-1",
            "accept",
            PolicyFeedbackLabels.Positive,
            "Hard",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["gapId"] = "constraint-gap-1",
                ["candidateKind"] = "HardConstraint",
                ["sourceSampleId"] = "chat-20260529-003"
            }));

        var example = new LearningFeatureDatasetService()
            .GeneratePolicyFeedbackFeatureExamples(dataset)
            .Single();

        Assert.AreEqual("constraint-gap-1", example.CandidateId);
        Assert.AreEqual("HardConstraint", example.CandidateKind);
        Assert.AreEqual("Hard", example.CandidateLayer);
        Assert.AreEqual("chat-20260529-003", example.Metadata["sourceSampleId"]);
        Assert.IsTrue(example.Accepted);
    }

    [TestMethod]
    public void EvalMustHitAndMustNotHit_ShouldGenerateRankingPair()
    {
        var report = new ContextEvalReport
        {
            Results =
            [
                new ContextEvalResult
                {
                    SampleId = "sample-ranking-1",
                    Query = "current task recovery",
                    Mode = "AutomationMode",
                    Status = "Passed",
                    RetrievalRecall3 = 1,
                    RetrievalRecall5 = 1,
                    RetrievalRecall10 = 1,
                    RetrievalMrrAnyMustHit = 1,
                    SelectedCount = 2,
                    TokenBudget = 4000,
                    MustHit = ["must-hit-1"],
                    MustNotHit = ["must-not-hit-1"],
                    SelectedIds = ["must-hit-1", "supporting-item"],
                    PackageHasAllConstraints = true,
                    PackageHasAllEntities = true,
                    PackageHasAllUncertainties = true,
                    SelectedItemDiagnostics =
                    [
                        new ContextEvalItemDiagnostic
                        {
                            ItemId = "must-hit-1",
                            Kind = "working_memory",
                            SectionName = "working",
                            Score = 25,
                            Rank = 1,
                            IsMustHit = true
                        }
                    ],
                    DroppedItemDiagnostics =
                    [
                        new ContextEvalItemDiagnostic
                        {
                            ItemId = "must-not-hit-1",
                            Kind = "historical_context",
                            SectionName = "historical_context",
                            Score = 2,
                            Rank = 0,
                            IsMustNotHit = true
                        }
                    ]
                }
            ]
        };

        var pair = new LearningFeatureDatasetService()
            .GenerateRankingPairsFromEvalReport(report)
            .Single();

        Assert.AreEqual("sample-ranking-1", pair.EvalSampleId);
        Assert.AreEqual("must-hit-1", pair.PositiveCandidateId);
        Assert.AreEqual("must-not-hit-1", pair.NegativeCandidateId);
        Assert.AreEqual("True", pair.FeatureSnapshot["positiveSelected"]);
        Assert.AreEqual("False", pair.FeatureSnapshot["negativeSelected"]);
        Assert.AreEqual("working", pair.FeatureSnapshot["positiveSection"]);
        Assert.AreEqual("historical_context", pair.FeatureSnapshot["negativeSection"]);
    }

    [TestMethod]
    public async Task Export_ShouldWriteJsonLinesFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-features-{Guid.NewGuid():N}");
        try
        {
            var dataset = new LearningFeatureDataset
            {
                DatasetId = "learning-feature-dataset-test",
                FeatureExamples =
                [
                    CreateFeatureExample("feature-1", "PolicyFeedback", PolicyFeedbackLabels.Positive)
                ],
                RankingPairs =
                [
                    new RankingPairExample
                    {
                        Query = "query",
                        Mode = "ChatMode",
                        Intent = "CurrentTask",
                        PositiveCandidateId = "positive-1",
                        NegativeCandidateId = "negative-1",
                        Reason = "mustHit above mustNotHit",
                        EvalSampleId = "sample-1"
                    }
                ],
                RouterIntentExamples =
                [
                    CreateFeatureExample("router-1", "RouterIntent", "CurrentTask")
                ],
                FeatureCount = 1,
                RankingPairCount = 1,
                RouterIntentExampleCount = 1,
                PolicyVersion = LearningFeatureDatasetService.PolicyVersion
            };

            var result = await new LearningFeatureDatasetService().ExportAsync(dataset, outputDirectory);

            Assert.IsTrue(File.Exists(result.PolicyFeedbackFeaturesPath));
            Assert.IsTrue(File.Exists(result.RankingPairsPath));
            Assert.IsTrue(File.Exists(result.RouterIntentExamplesPath));
            Assert.AreEqual(1, result.FeatureCount);
            Assert.AreEqual(1, result.RankingPairCount);
            Assert.AreEqual(1, result.RouterIntentExampleCount);

            var featureLine = File.ReadAllLines(result.PolicyFeedbackFeaturesPath).Single();
            var rankingLine = File.ReadAllLines(result.RankingPairsPath).Single();
            var routerLine = File.ReadAllLines(result.RouterIntentExamplesPath).Single();
            Assert.AreEqual("feature-1", JsonSerializer.Deserialize<ContextPolicyFeatureExample>(featureLine, JsonOptions)!.ExampleId);
            Assert.AreEqual("positive-1", JsonSerializer.Deserialize<RankingPairExample>(rankingLine, JsonOptions)!.PositiveCandidateId);
            Assert.AreEqual("router-1", JsonSerializer.Deserialize<ContextPolicyFeatureExample>(routerLine, JsonOptions)!.ExampleId);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Build_ShouldOnlyProjectInputData()
    {
        var policyFeedback = CreatePolicyFeedbackDataset(CreatePolicyFeedbackRecord(
            "policy-readonly",
            "PromotionCandidateReviewRecord",
            "promotion-review-readonly",
            "accept",
            PolicyFeedbackLabels.Positive,
            "CandidateMemory",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["candidateId"] = "promotion-readonly"
            }));
        var originalRecordCount = policyFeedback.Records.Count;

        var dataset = new LearningFeatureDatasetService().Build(policyFeedback);

        Assert.AreEqual(originalRecordCount, policyFeedback.Records.Count);
        Assert.AreEqual(1, dataset.FeatureCount);
        Assert.AreEqual(0, dataset.RankingPairCount);
        Assert.AreEqual(0, dataset.RouterIntentExampleCount);
    }

    [TestMethod]
    public void PlanningShadowReport_ShouldGenerateRouterIntentExample()
    {
        var report = new ShadowRetrievalComparisonReport
        {
            ReportId = "planning-shadow-report-test",
            SampleSet = "a3",
            GeneratedAt = DateTimeOffset.UtcNow,
            Samples =
            [
                new ShadowRetrievalComparisonItem
                {
                    SampleId = "sample-router-1",
                    Mode = "CodingMode",
                    ProposalId = "proposal-1",
                    ProposalSummary = "CodingTask/CodingMode keyword=8 memory=8 relation=4 final=10",
                    LegacyOperationId = "legacy-1",
                    ShadowOperationId = "shadow-1",
                    ValidPlan = true,
                    NativeValidPlan = true,
                    ShadowRecall10 = 1,
                    LegacyRecall10 = 1,
                    ShadowMrr = 1,
                    ShadowConstraintHitRate = 1,
                    ShadowSelectedMustHit = ["must-hit-router"],
                    ShadowChannelSources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["keyword"] = ["must-hit-router"],
                        ["relations"] = ["relation-evidence"]
                    },
                    LegacyChannelSources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["working"] = ["working-item"]
                    }
                }
            ]
        };

        var example = new LearningFeatureDatasetService()
            .GenerateRouterIntentExamples(report)
            .Single();

        Assert.AreEqual("RouterIntent", example.TaskKind);
        Assert.AreEqual("CodingTask", example.Intent);
        Assert.AreEqual("CodingTask", example.Label);
        Assert.AreEqual("RetrievalPlanProposal", example.CandidateKind);
        Assert.IsTrue(example.ChannelSources.Contains("keyword"));
        Assert.IsTrue(example.ChannelSources.Contains("relations"));
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderLearningFeaturesSummary()
    {
        var snapshot = new ServiceLearningFeaturesSnapshot
        {
            CurrentTime = DateTimeOffset.Parse("2026-06-06T12:00:00Z"),
            BaseUrl = "http://localhost:5079/",
            Limit = 50,
            Offset = 0,
            Dataset = new LearningFeatureDataset
            {
                DatasetId = "learning-feature-dataset-test",
                FeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                LatestExportPath = "learning/features",
                PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
                LabelDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [PolicyFeedbackLabels.Positive] = 1,
                    ["CurrentTask"] = 3
                },
                SourceTypeDistribution = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PromotionCandidateReviewRecord"] = 1,
                    ["PlanningShadowComparison"] = 3
                },
                FeatureExamples =
                [
                    CreateFeatureExample("feature-render-1", "PolicyFeedback", PolicyFeedbackLabels.Positive)
                ]
            },
            QualityReport = new LearningDatasetQualityReport
            {
                PolicyFeedbackFeatureCount = 1,
                RankingPairCount = 2,
                RouterIntentExampleCount = 3,
                PositiveCount = 1,
                NegativeCount = 0,
                NeutralCount = 0,
                DataRisks =
                [
                    LearningDatasetDataRisks.MissingNegativeSamples
                ],
                TaskReadiness = new Dictionary<string, LearningDatasetTaskReadiness>(StringComparer.OrdinalIgnoreCase)
                {
                    [LearningDatasetTaskNames.RouterIntentClassifier] = new LearningDatasetTaskReadiness
                    {
                        TaskName = LearningDatasetTaskNames.RouterIntentClassifier,
                        Ready = true,
                        Status = LearningDatasetReadinessStatus.Ready,
                        RecommendedNextAction = "offline router analysis only"
                    }
                },
                RecommendedNextAction = "Add rejected examples."
            },
            RouterIntentBaselineReport = new RouterIntentClassifierBaselineReport
            {
                SampleCount = 12,
                Ready = true,
                Status = LearningDatasetReadinessStatus.Ready,
                BestBaseline = RouterIntentClassifierBaselineNames.TokenCentroidRouterBaseline,
                Recommendation = RouterIntentClassifierRecommendations.ReadyForRouterShadow,
                Baselines =
                [
                    new RouterIntentClassifierBaselineResult
                    {
                        BaselineName = RouterIntentClassifierBaselineNames.TokenCentroidRouterBaseline,
                        Accuracy = 0.75,
                        MacroF1 = 0.7,
                        CurrentTaskRecall = 0.8,
                        FuzzyQuestionRecall = 0.6,
                        CodingTaskRecall = 0.9,
                        NovelGenerationRecall = 0.7,
                        AutomationRecoveryRecall = 0.75
                    }
                ],
                PolicyVersion = RouterIntentEvaluationRunner.PolicyVersion
            },
            RouterDisagreementTriageA3Report = new RouterDisagreementTriageReport
            {
                DatasetName = "A3",
                SampleCount = 10,
                DisagreementCount = 2,
                ShadowFixesRuntime = 1,
                ShadowBreaksRuntime = 1,
                HardNegativeCount = 2,
                Recommendation = RouterDisagreementTriageRecommendations.KeepRuleBased,
                TopConfusionPairs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CodingTask->FuzzyQuestion"] = 1
                }
            },
            RouterDisagreementTriageExtendedReport = new RouterDisagreementTriageReport
            {
                DatasetName = "Extended",
                SampleCount = 10,
                DisagreementCount = 2,
                ShadowFixesRuntime = 1,
                ShadowBreaksRuntime = 1,
                HardNegativeCount = 2,
                Recommendation = RouterDisagreementTriageRecommendations.KeepRuleBased,
                TopConfusionPairs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CodingTask->FuzzyQuestion"] = 1
                }
            },
            RouterHardNegativeCount = 2,
            RouterGuardedOptInReadinessGateReport = new RouterGuardedOptInReadinessGateReport
            {
                Passed = false,
                ShadowFixesRuntime = 1,
                ShadowBreaksRuntime = 3,
                NetGain = -2,
                AgreementRate = 0.8857,
                Recommendation = RouterGuardedOptInGateRecommendations.KeepRuleBased,
                FailureReasons =
                [
                    RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes
                ]
            },
            CandidateRerankerFeatureCompletenessA3Report = new CandidateRerankerFeatureCompletenessReport
            {
                DatasetName = "A3",
                FeatureCompletenessRate = 0.91,
                MissingFeatureMetadataCount = 2,
                RiskCandidateBlockedBeforeRerank = 5,
                EligibilityGuardStatus = CandidateRerankerEligibilityGuardStatuses.Guarded,
                Recommendation = "ReadyForGuardedShadowEval"
            },
            CandidateRerankerFeatureCompletenessExtendedReport = new CandidateRerankerFeatureCompletenessReport
            {
                DatasetName = "Extended",
                FeatureCompletenessRate = 0.94,
                MissingFeatureMetadataCount = 3,
                RiskCandidateBlockedBeforeRerank = 8,
                EligibilityGuardStatus = CandidateRerankerEligibilityGuardStatuses.Guarded,
                Recommendation = "ReadyForGuardedShadowEval"
            },
            CandidateRerankerShadowEvalA3Report = new CandidateRerankerShadowEvalReport
            {
                DatasetName = "A3",
                Samples = 10,
                CandidateCount = 50,
                WouldImproveCount = 2,
                WouldRegressCount = 1,
                NetGain = 1,
                Recommendation = CandidateRerankerShadowRecommendations.NeedsFeatureTuning
            },
            CandidateRerankerShadowEvalExtendedReport = new CandidateRerankerShadowEvalReport
            {
                DatasetName = "Extended",
                Samples = 20,
                CandidateCount = 100,
                WouldImproveCount = 3,
                WouldRegressCount = 0,
                NetGain = 3,
                Recommendation = CandidateRerankerShadowRecommendations.ReadyForRankerShadow
            },
            CandidateRerankerShadowTraceQualityReport = new CandidateRerankerShadowTraceQualityReport
            {
                TraceCount = 30,
                CandidateCount = 120,
                Recommendation = CandidateRerankerShadowRecommendations.ReadyForRankerShadow
            },
            CandidateRerankerShadowFailureAuditA3Report = new CandidateRerankerShadowFailureAuditReport
            {
                DatasetName = "A3",
                RegressionCount = 2,
                ScoreContractStatus = CandidateRerankerScoreContractStatuses.NeedsAudit,
                RiskCandidateInShadowTopK = 3,
                RecommendedNextAction = "Keep formal ranking.",
                RegressionReasonSummary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [CandidateRerankerRegressionReasons.RiskCandidateAllowed] = 2
                }
            },
            CandidateRerankerShadowFailureAuditExtendedReport = new CandidateRerankerShadowFailureAuditReport
            {
                DatasetName = "Extended",
                RegressionCount = 1,
                ScoreContractStatus = CandidateRerankerScoreContractStatuses.NeedsAudit,
                RiskCandidateInShadowTopK = 1,
                RecommendedNextAction = "Keep formal ranking.",
                RegressionReasonSummary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [CandidateRerankerRegressionReasons.RankerFeatureTooWeak] = 1
                }
            },
            CandidateRerankerScoreDistributionA3Report = new CandidateRerankerScoreDistributionReport
            {
                DatasetName = "A3",
                ScoreMean = 12,
                ScoreStdDev = 2,
                LowMarginDecisionCount = 1,
                Recommendation = CandidateRerankerShadowRecommendations.NeedsFeatureTuning
            },
            CandidateRerankerScoreDistributionExtendedReport = new CandidateRerankerScoreDistributionReport
            {
                DatasetName = "Extended",
                ScoreMean = 14,
                ScoreStdDev = 3,
                LowMarginDecisionCount = 0,
                Recommendation = CandidateRerankerShadowRecommendations.KeepFormalRanking
            },
            CandidateRerankerListwiseCalibrationA3Report = new CandidateRerankerListwiseCalibrationReport
            {
                DatasetName = "A3",
                RegressionCount = 2,
                LowMarginDecisionCount = 1,
                FormalPriorityMismatchCount = 1,
                Recommendation = CandidateRerankerShadowRecommendations.NeedsFeatureTuning,
                CalibrationIssueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [CandidateRerankerCalibrationIssues.LowMarginAmbiguity] = 1
                }
            },
            CandidateRerankerListwiseCalibrationExtendedReport = new CandidateRerankerListwiseCalibrationReport
            {
                DatasetName = "Extended",
                RegressionCount = 1,
                LowMarginDecisionCount = 0,
                FormalPriorityMismatchCount = 1,
                Recommendation = CandidateRerankerShadowRecommendations.NeedsFeatureTuning,
                CalibrationIssueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [CandidateRerankerCalibrationIssues.FormalRankingHasImplicitPriority] = 1
                }
            },
            CandidateRerankerFormalPriorityAlignmentA3Report = new CandidateRerankerFormalPriorityAlignmentReport
            {
                DatasetName = "A3",
                RegressionCount = 2,
                RecoveredCount = 1,
                UnexplainedMismatchCount = 1,
                AbstainCount = 1,
                NetGainAfterAbstain = 0,
                RecoveredByLayerPriority = 1,
                RecoveredBySourcePriority = 1,
                RecoveredByCurrentTaskBoost = 1,
                RecoveredByConstraintRelevance = 1,
                RecoveredByStableMemoryBias = 0,
                Recommendation = CandidateRerankerShadowRecommendations.NeedsFeatureTuning
            },
            CandidateRerankerFormalPriorityAlignmentExtendedReport = new CandidateRerankerFormalPriorityAlignmentReport
            {
                DatasetName = "Extended",
                RegressionCount = 1,
                RecoveredCount = 1,
                UnexplainedMismatchCount = 0,
                AbstainCount = 0,
                NetGainAfterAbstain = 1,
                RecoveredByLayerPriority = 1,
                RecoveredBySourcePriority = 1,
                RecoveredByCurrentTaskBoost = 0,
                RecoveredByConstraintRelevance = 0,
                RecoveredByStableMemoryBias = 1,
                Recommendation = CandidateRerankerShadowRecommendations.NeedsFeatureTuning
            },
            LearningReadinessRegistry = new LearningReadinessRegistry
            {
                ReadyCount = 3,
                BlockedCount = 3,
                OverallRecommendation = "KeepRuntimeDefaults",
                Capabilities =
                [
                    new ShadowCapabilityReadiness
                    {
                        CapabilityId = ShadowCapabilityIds.GraphExpansion,
                        CurrentPhase = "G7.1",
                        Status = ShadowCapabilityReadinessStatuses.ReadyForGuardedOptIn,
                        GatePassed = true,
                        Recommendation = "ReadyForGuardedOptIn:audit-v1,conflict-v1",
                        AllowedRuntimeModes = ["ApplyGuarded:audit-v1", "ApplyGuarded:conflict-v1"],
                        ForbiddenRuntimeModes = ["ApplyGuarded:normal-v1", "ApplyGuarded:current-task-v1"],
                        LastEvalReportPath = "eval/graph-expansion-guarded-optin-gate.json"
                    },
                    new ShadowCapabilityReadiness
                    {
                        CapabilityId = ShadowCapabilityIds.VectorRetrieval,
                        CurrentPhase = "V3.F",
                        Status = ShadowCapabilityReadinessStatuses.PreviewOnly,
                        GatePassed = false,
                        Recommendation = "BlockedByRecall",
                        BlockedReasons = ["A3RecallAtLeast80Percent"],
                        AllowedRuntimeModes = [ShadowRuntimeModes.PreviewOnly],
                        ForbiddenRuntimeModes = [ShadowRuntimeModes.RuntimeShadow, ShadowRuntimeModes.ApplyGuarded],
                        LastEvalReportPath = "eval/vector-retrieval-shadow-readiness-gate.json"
                    }
                ]
            },
            LearningRuntimeChangeReadinessGateReport = new LearningRuntimeChangeReadinessGateReport
            {
                Passed = true,
                Recommendation = "RuntimeChangeRulesSatisfied",
                Checks =
                [
                    new LearningRuntimeChangeReadinessGateCheck
                    {
                        CapabilityId = ShadowCapabilityIds.VectorRetrieval,
                        Condition = "VectorV4GateBlocksRuntimeShadow",
                        Passed = true,
                        Reason = "Vector V4 gate 未通过时必须禁止 RuntimeShadow / ApplyGuarded。"
                    }
                ]
            }
        };

        var output = ServiceOperationalRenderer.RenderLearningFeatures(snapshot);

        StringAssert.Contains(output, "Service Learning Features");
        StringAssert.Contains(output, "features=1 rankingPairs=2 routerIntent=3");
        StringAssert.Contains(output, "Dataset Quality");
        StringAssert.Contains(output, "policy=1 rankingPairs=2 routerIntent=3");
        StringAssert.Contains(output, "MissingNegativeSamples");
        StringAssert.Contains(output, "RouterIntentClassifier: Ready");
        StringAssert.Contains(output, "Learning Readiness Dashboard");
        StringAssert.Contains(output, ShadowCapabilityIds.GraphExpansion);
        StringAssert.Contains(output, "ApplyGuarded:audit-v1");
        StringAssert.Contains(output, "BlockedByRecall");
        StringAssert.Contains(output, "Learning Runtime Change Gate");
        StringAssert.Contains(output, "RuntimeChangeRulesSatisfied");
        StringAssert.Contains(output, "Router Intent Baseline");
        StringAssert.Contains(output, RouterIntentClassifierRecommendations.ReadyForRouterShadow);
        StringAssert.Contains(output, RouterIntentClassifierBaselineNames.TokenCentroidRouterBaseline);
        StringAssert.Contains(output, "Router Disagreement Triage Summary");
        StringAssert.Contains(output, "hard negatives: 2");
        StringAssert.Contains(output, "CodingTask->FuzzyQuestion");
        StringAssert.Contains(output, "Router Opt-in Readiness Summary");
        StringAssert.Contains(output, RouterGuardedOptInGateFailureReasons.ShadowBreaksRuntimeGreaterThanFixes);
        StringAssert.Contains(output, "Candidate Feature Completeness / Eligibility Guard Summary");
        StringAssert.Contains(output, CandidateRerankerEligibilityGuardStatuses.Guarded);
        StringAssert.Contains(output, "Candidate Reranker Shadow Summary");
        StringAssert.Contains(output, CandidateRerankerShadowRecommendations.ReadyForRankerShadow);
        StringAssert.Contains(output, "Candidate Reranker Failure Audit Summary");
        StringAssert.Contains(output, CandidateRerankerRegressionReasons.RiskCandidateAllowed);
        StringAssert.Contains(output, CandidateRerankerScoreContractStatuses.NeedsAudit);
        StringAssert.Contains(output, "Ranker Calibration Summary");
        StringAssert.Contains(output, CandidateRerankerCalibrationIssues.LowMarginAmbiguity);
        StringAssert.Contains(output, CandidateRerankerCalibrationIssues.FormalRankingHasImplicitPriority);
        StringAssert.Contains(output, "Formal Priority Alignment Summary");
        StringAssert.Contains(output, "recovered=1");
        StringAssert.Contains(output, "netAfterAbstain=1");
        StringAssert.Contains(output, "Add rejected examples.");
        StringAssert.Contains(output, "Positive: 1");
        StringAssert.Contains(output, "PromotionCandidateReviewRecord: 1");
        StringAssert.Contains(output, "feature-render-1");
    }

    [TestMethod]
    public async Task EmptyPolicyFeedbackFile_ShouldTriggerNoPolicyFeedback()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-quality-empty-policy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.PolicyFeedbackFeaturesFileName), string.Empty);
            await WriteJsonLinesAsync(
                Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName),
                [
                    CreateRankingPair("sample-quality-1", "ChatMode", "CurrentTask")
                ]);
            await WriteJsonLinesAsync(
                Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName),
                [
                    CreateFeatureExample("router-quality-1", "RouterIntent", "CurrentTask")
                ]);

            var report = await new LearningDatasetQualityReportBuilder().BuildAsync(outputDirectory);

            Assert.AreEqual(0, report.PolicyFeedbackFeatureCount);
            Assert.AreEqual(1, report.RankingPairCount);
            Assert.AreEqual(1, report.RouterIntentExampleCount);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.NoPolicyFeedback);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.MissingNegativeSamples);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EvalOnlyRankingPairs_ShouldTriggerEvalOnlyDataset()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-quality-eval-only-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.PolicyFeedbackFeaturesFileName), string.Empty);
            await WriteJsonLinesAsync(
                Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName),
                [
                    CreateRankingPair("sample-eval-only-1", "AutomationMode", "AutomationRecovery")
                ]);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName), string.Empty);

            var report = await new LearningDatasetQualityReportBuilder().BuildAsync(outputDirectory);

            Assert.AreEqual(0, report.PolicyFeedbackFeatureCount);
            Assert.AreEqual(1, report.RankingPairCount);
            Assert.AreEqual(0, report.RouterIntentExampleCount);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.EvalOnlyDataset);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task JsonLineParser_ShouldHandleEmptyFiles()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"contextcore-learning-quality-empty-files-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.PolicyFeedbackFeaturesFileName), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RankingPairsFileName), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, LearningDatasetQualityReportBuilder.RouterIntentExamplesFileName), string.Empty);

            var report = await new LearningDatasetQualityReportBuilder().BuildAsync(outputDirectory);

            Assert.AreEqual(0, report.PolicyFeedbackFeatureCount);
            Assert.AreEqual(0, report.RankingPairCount);
            Assert.AreEqual(0, report.RouterIntentExampleCount);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.NoPolicyFeedback);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.LowIntentCoverage);
            CollectionAssert.Contains(report.DataRisks.ToArray(), LearningDatasetDataRisks.LowModeCoverage);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TaskReadiness_ShouldBeCalculatedFromCoverage()
    {
        var policyFeatures = Enumerable.Range(0, 20)
            .Select(index => CreatePolicyFeatureExample(
                $"promotion-ready-{index}",
                "PromotionCandidateReviewRecord",
                index % 2 == 0 ? PolicyFeedbackLabels.Positive : PolicyFeedbackLabels.Negative,
                index % 2 == 0,
                index % 2 != 0))
            .Concat(Enumerable.Range(0, 20)
                .Select(index => CreatePolicyFeatureExample(
                    $"constraint-ready-{index}",
                    "ConstraintGapReviewRecord",
                    index % 2 == 0 ? PolicyFeedbackLabels.Positive : PolicyFeedbackLabels.Negative,
                    index % 2 == 0,
                    index % 2 != 0)))
            .Concat(Enumerable.Range(0, 20)
                .Select(index => CreatePolicyFeatureExample(
                    $"attention-ready-{index}",
                    "AttentionReviewRecord",
                    PolicyFeedbackLabels.Positive,
                    accepted: true,
                    rejected: false)))
            .ToArray();
        var rankingPairs = Enumerable.Range(0, 100)
            .Select(index => CreateRankingPair($"sample-rank-{index}", index % 3 == 0 ? "ChatMode" : index % 3 == 1 ? "CodingMode" : "NovelMode", $"Intent{index % 4}"))
            .ToArray();
        var routerExamples = Enumerable.Range(0, 100)
            .Select(index => CreateRouterExample($"router-ready-{index}", index % 3 == 0 ? "ChatMode" : index % 3 == 1 ? "CodingMode" : "NovelMode", $"Intent{index % 4}"))
            .ToArray();

        var report = new LearningDatasetQualityReportBuilder().Build(
            policyFeatures,
            rankingPairs,
            routerExamples,
            "learning/features");

        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.RouterIntentClassifier].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.CandidateReranker].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.PromotionJudge].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.ConstraintGapJudge].Status);
        Assert.AreEqual(LearningDatasetReadinessStatus.Ready, report.TaskReadiness[LearningDatasetTaskNames.AttentionScorer].Status);
        Assert.IsFalse(report.DataRisks.Contains(LearningDatasetDataRisks.NoPolicyFeedback));
        Assert.IsFalse(report.DataRisks.Contains(LearningDatasetDataRisks.MissingNegativeSamples));
    }

    private static PolicyFeedbackDataset CreatePolicyFeedbackDataset(params PolicyFeedbackRecord[] records)
        => new()
        {
            DatasetId = "policy-feedback-dataset-test",
            Name = "Policy Feedback Dataset",
            Scope = $"workspace:{WorkspaceId}/collection:{CollectionId}/session:{SessionId}",
            CreatedAt = DateTimeOffset.UtcNow,
            Records = records,
            PositiveCount = records.Count(record => record.Label == PolicyFeedbackLabels.Positive),
            NegativeCount = records.Count(record => record.Label == PolicyFeedbackLabels.Negative),
            NeutralCount = records.Count(record => record.Label == PolicyFeedbackLabels.Neutral),
            SourceTypes = records
                .GroupBy(record => record.SourceType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            PolicyVersion = PolicyFeedbackDatasetService.PolicyVersion,
            EvalBaselineRef = PolicyFeedbackDatasetService.EvalBaselineRef
        };

    private static PolicyFeedbackRecord CreatePolicyFeedbackRecord(
        string feedbackRecordId,
        string sourceType,
        string sourceId,
        string action,
        string label,
        string targetLayer,
        Dictionary<string, string> metadata)
        => new()
        {
            FeedbackRecordId = feedbackRecordId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SessionId = SessionId,
            SourceType = sourceType,
            SourceId = sourceId,
            Action = action,
            Label = label,
            Reason = $"reason for {feedbackRecordId}",
            PositiveRefs = label == PolicyFeedbackLabels.Positive ? [$"positive-{feedbackRecordId}"] : [],
            NegativeRefs = label == PolicyFeedbackLabels.Negative ? [$"negative-{feedbackRecordId}"] : [],
            EvidenceRefs = [$"evidence-{feedbackRecordId}"],
            TargetLayer = targetLayer,
            CreatedAt = DateTimeOffset.UtcNow,
            Reviewer = "tester",
            PolicyVersion = PolicyFeedbackDatasetService.PolicyVersion,
            Metadata = metadata
        };

    private static ContextPolicyFeatureExample CreateFeatureExample(
        string exampleId,
        string taskKind,
        string label)
        => new()
        {
            ExampleId = exampleId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceType = taskKind == "RouterIntent"
                ? "PlanningShadowComparison"
                : "PromotionCandidateReviewRecord",
            SourceId = $"{exampleId}-source",
            TaskKind = taskKind,
            Mode = "ChatMode",
            Intent = label,
            Label = label,
            InputSummary = "feature summary",
            CandidateId = $"{exampleId}-candidate",
            CandidateKind = "Preference",
            CandidateLayer = "CandidateMemory",
            CandidateStatus = "Accepted",
            CandidateImportance = 0.8,
            CandidateRecency = 1,
            ChannelSources = ["policy-feedback"],
            Selected = true,
            Accepted = label == PolicyFeedbackLabels.Positive || taskKind == "RouterIntent",
            Rejected = label == PolicyFeedbackLabels.Negative,
            EvidenceRefs = [$"{exampleId}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ContextPolicyFeatureExample CreateRouterExample(
        string exampleId,
        string mode,
        string intent)
        => new()
        {
            ExampleId = exampleId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceType = "PlanningShadowComparison",
            SourceId = $"{exampleId}-source",
            TaskKind = "RouterIntent",
            Mode = mode,
            Intent = intent,
            Label = intent,
            InputSummary = "router summary",
            CandidateId = $"{exampleId}-proposal",
            CandidateKind = "RetrievalPlanProposal",
            CandidateLayer = "Planning",
            CandidateStatus = "NativeValid",
            Selected = true,
            Accepted = true,
            EvidenceRefs = [$"{exampleId}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ContextPolicyFeatureExample CreatePolicyFeatureExample(
        string exampleId,
        string sourceType,
        string label,
        bool accepted,
        bool rejected)
        => new()
        {
            ExampleId = exampleId,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            SourceType = sourceType,
            SourceId = $"{exampleId}-source",
            TaskKind = "PolicyFeedback",
            Mode = "ChatMode",
            Intent = "CurrentTask",
            Label = label,
            InputSummary = "policy feedback summary",
            CandidateId = $"{exampleId}-candidate",
            CandidateKind = "Preference",
            CandidateLayer = "CandidateMemory",
            CandidateStatus = accepted ? "Accepted" : "Rejected",
            CandidateImportance = 0.8,
            CandidateRecency = 1,
            ChannelSources = ["policy-feedback"],
            Selected = true,
            Accepted = accepted,
            Rejected = rejected,
            EvidenceRefs = [$"{exampleId}-evidence"],
            PolicyVersion = LearningFeatureDatasetService.PolicyVersion,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static LearningFeedbackEvent CreateRuntimeFeedback(
        bool metadataOnly,
        string? feedbackId = null,
        string? capabilityId = null,
        string? feedbackKind = null,
        string? targetId = null,
        string? sourceOperationId = null)
        => new()
        {
            FeedbackId = feedbackId ?? string.Empty,
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Source = "package-preview",
            SourceOperationId = sourceOperationId ?? "operation-feedback-1",
            CapabilityId = capabilityId ?? ShadowCapabilityIds.VectorRetrieval,
            TargetId = targetId ?? "candidate-1",
            TargetType = LearningFeedbackTargetType.VectorCandidate.ToString(),
            FeedbackKind = feedbackKind ?? LearningFeedbackKinds.MissingContext,
            FeedbackValue = -1,
            Reason = "missing context reason that should not become training data directly",
            UserCorrection = "corrected context detail",
            RedactionMode = metadataOnly ? "metadata-only" : string.Empty,
            MetadataOnly = metadataOnly,
            TrainingUse = "disabled_until_review",
            Confidence = 0.8,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["metadataOnly"] = metadataOnly ? "true" : "false"
            }
        };

    private static LearningFeedbackEventQuery DefaultFeedbackQuery()
        => new()
        {
            WorkspaceId = WorkspaceId,
            CollectionId = CollectionId,
            Limit = int.MaxValue
        };

    private static LearningFeedbackReviewRequest ApprovedReviewRequest()
        => new()
        {
            Reviewer = "reviewer-test",
            ReviewReason = "reviewed for offline dataset candidate",
            RedactionChecked = true,
            TrainingUse = "approved_for_dataset"
        };

    private static async Task<(InMemoryLearningFeedbackStore FeedbackStore, InMemoryLearningFeedbackReviewStore ReviewStore, LearningFeedbackEvent Feedback)> CreateReviewedFeedbackAsync(
        string feedbackId,
        bool metadataOnly,
        FeedbackReviewStatus status)
    {
        var feedbackStore = new InMemoryLearningFeedbackStore();
        var reviewStore = new InMemoryLearningFeedbackReviewStore();
        var feedback = await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(metadataOnly, feedbackId));
        var reviewService = new LearningFeedbackReviewService(feedbackStore, reviewStore);
        var request = ApprovedReviewRequest();
        _ = status switch
        {
            FeedbackReviewStatus.ApprovedForDataset => await reviewService.ApproveAsync(feedback.FeedbackId, request),
            FeedbackReviewStatus.Rejected => await reviewService.RejectAsync(feedback.FeedbackId, request),
            FeedbackReviewStatus.NeedsRedaction => await reviewService.NeedsRedactionAsync(feedback.FeedbackId, request),
            FeedbackReviewStatus.NeedsMoreEvidence => await reviewService.NeedsMoreEvidenceAsync(feedback.FeedbackId, request),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported review status for test setup.")
        };

        return (feedbackStore, reviewStore, feedback.Event);
    }

    private static async Task SubmitApprovedFeedbackAsync(
        InMemoryLearningFeedbackStore feedbackStore,
        InMemoryLearningFeedbackReviewStore reviewStore,
        string feedbackId,
        string feedbackKind,
        bool metadataOnly)
    {
        var feedback = await new LearningFeedbackService(feedbackStore)
            .SubmitAsync(CreateRuntimeFeedback(
                metadataOnly,
                feedbackId,
                feedbackKind: feedbackKind));
        await new LearningFeedbackReviewService(feedbackStore, reviewStore)
            .ApproveAsync(feedback.FeedbackId, ApprovedReviewRequest());
    }

    private static async Task<LearningFeedbackQualityReport> BuildFeedbackQualityReportAsync(
        InMemoryLearningFeedbackStore feedbackStore,
        InMemoryLearningFeedbackReviewStore reviewStore)
    {
        var query = DefaultFeedbackQuery();
        var feedback = await feedbackStore.QueryAsync(query);
        var reviews = await reviewStore.QueryAsync(new LearningFeedbackReviewQuery { Limit = int.MaxValue });
        var featureCandidates = await new LearningFeedbackFeatureCandidateBuilder(feedbackStore, reviewStore)
            .BuildAsync(query);
        return new LearningFeedbackQualityReportBuilder()
            .Build(feedback, reviews, featureCandidates);
    }

    private static RankingPairExample CreateRankingPair(
        string sampleId,
        string mode,
        string intent)
        => new()
        {
            Query = $"query {sampleId}",
            Mode = mode,
            Intent = intent,
            PositiveCandidateId = $"{sampleId}-positive",
            NegativeCandidateId = $"{sampleId}-negative",
            Reason = "mustHit above mustNotHit",
            EvalSampleId = sampleId
        };

    private static async Task WriteJsonLinesAsync<T>(
        string path,
        IReadOnlyList<T> records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record, JsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
    }
}
