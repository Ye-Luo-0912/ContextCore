using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
[TestCategory("Vector")]
public class ContextCoreVectorLifecycleMetadataReviewCandidateTests
{
    [TestMethod]
    public async Task HumanReviewRequiredRepairPlanItem_GeneratesCandidate()
    {
        var store = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var service = new VectorLifecycleMetadataReviewCandidateService(store);

        var result = await service.GenerateAsync(Request(), Summary(Candidate("sample-a", "item-a")), "vector/eligibility/plan.json");

        Assert.AreEqual(1, result.CandidateCount);
        var candidate = result.Candidates.Single();
        Assert.AreEqual(VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, candidate.Status);
        Assert.AreEqual("item-a", candidate.MustHitItemId);
        Assert.AreEqual("note", candidate.ItemKind);
        Assert.AreEqual("context", candidate.Layer);
        CollectionAssert.Contains(candidate.RiskIfRejected.ToArray(), "RecallRemainsBlockedByLifecycleMetadata");
    }

    [TestMethod]
    public async Task CorrectlyBlockedDeprecated_DoesNotGenerateNormalRepairCandidate()
    {
        var service = new VectorLifecycleMetadataReviewCandidateService(new InMemoryVectorLifecycleMetadataReviewCandidateStore());

        var result = await service.GenerateAsync(Request(), Summary(), "vector/eligibility/plan.json");

        Assert.AreEqual(0, result.CandidateCount);
        Assert.AreEqual(18, result.CorrectlyBlockedSkippedCount);
    }

    [TestMethod]
    public async Task ForbiddenRepair_DoesNotGenerateCandidate()
    {
        var service = new VectorLifecycleMetadataReviewCandidateService(new InMemoryVectorLifecycleMetadataReviewCandidateStore());
        var forbidden = Candidate(
            "sample-a",
            "item-a",
            requiresHumanReview: false,
            forbiddenReason: "UnsafeLifecycle");

        var result = await service.GenerateAsync(Request(), Summary(forbidden), "vector/eligibility/plan.json");

        Assert.AreEqual(0, result.CandidateCount);
    }

    [TestMethod]
    public async Task DuplicateGeneration_StableUpsertsAndPreservesStatus()
    {
        var store = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var service = new VectorLifecycleMetadataReviewCandidateService(store);
        var original = Candidate("sample-a", "item-a", evidence: "evidence-1");
        var first = await service.GenerateAsync(Request(), Summary(original), "vector/eligibility/plan.json");
        var candidateId = first.Candidates.Single().CandidateId;
        await store.SaveAsync(CopyWithStatus(first.Candidates.Single(), VectorLifecycleMetadataReviewCandidateStatuses.NeedsEvidence));

        var refreshed = Candidate("sample-a", "item-a", evidence: "evidence-2");
        await service.GenerateAsync(Request(), Summary(refreshed), "vector/eligibility/plan.json");
        var candidate = await store.GetAsync(candidateId);

        Assert.IsNotNull(candidate);
        Assert.AreEqual(VectorLifecycleMetadataReviewCandidateStatuses.NeedsEvidence, candidate!.Status);
        CollectionAssert.Contains(candidate.EvidenceRefs.ToArray(), "evidence-2");
        CollectionAssert.DoesNotContain(candidate.EvidenceRefs.ToArray(), "evidence-1");
    }

    [TestMethod]
    public async Task Explain_PreservesEvidenceSourceAvailabilityAndRiskFields()
    {
        var store = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var service = new VectorLifecycleMetadataReviewCandidateService(store);
        var result = await service.GenerateAsync(Request(), Summary(Candidate("sample-a", "item-a")), "vector/eligibility/plan.json");

        var explanation = await service.ExplainAsync(result.Candidates.Single().CandidateId);

        Assert.IsNotNull(explanation);
        CollectionAssert.Contains(explanation!.EvidenceRefs.ToArray(), "evidence-1");
        CollectionAssert.Contains(explanation.SourceRefs.ToArray(), "source-1");
        Assert.IsFalse(explanation.ProvenanceAvailable);
        Assert.IsTrue(explanation.RelationEvidenceAvailable);
        Assert.IsFalse(explanation.ReviewEvidenceAvailable);
        CollectionAssert.Contains(explanation.RiskIfApproved.ToArray(), "SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval");
        CollectionAssert.Contains(explanation.RiskIfRejected.ToArray(), "RecallRemainsBlockedByLifecycleMetadata");
    }

    [TestMethod]
    public async Task InMemoryStore_QueryFilters()
    {
        var store = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        await store.SaveAsync(ReviewCandidate("candidate-1", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3"));
        await store.SaveAsync(ReviewCandidate("candidate-2", "workspace-a", "collection-b", "Rejected", "memory", "fact", "Extended"));

        var results = await store.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a",
            Status = VectorLifecycleMetadataReviewCandidateStatuses.PendingReview,
            Layer = "context",
            ItemKind = "note",
            MustHitItemId = "item-candidate-1",
            SourceEvalSet = "A3"
        });

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("candidate-1", results.Single().CandidateId);
    }

    [TestMethod]
    public async Task FileSystemStore_SaveQueryGetRoundtrip()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new FileVectorLifecycleMetadataReviewCandidateStore(new FileStorageOptions { RootPath = root });
            var candidate = ReviewCandidate("candidate-1", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3");
            await store.SaveAsync(candidate);

            var loaded = await store.GetAsync("candidate-1");
            var queried = await store.QueryAsync(new VectorLifecycleMetadataReviewCandidateQuery
            {
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                Layer = "context",
                ItemKind = "note",
                MustHitItemId = "item-candidate-1",
                SourceEvalSet = "A3"
            });

            Assert.IsNotNull(loaded);
            Assert.AreEqual("candidate-1", loaded!.CandidateId);
            Assert.AreEqual(1, queried.Count);
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
    public void CandidateId_IsDeterministicForSameReviewCandidateInstance()
    {
        var idA = VectorLifecycleMetadataReviewCandidateService.BuildCandidateId("workspace", "collection", "item-a", "Active", "normal_context", "sample-a", "A3");
        var idB = VectorLifecycleMetadataReviewCandidateService.BuildCandidateId("workspace", "collection", "item-a", "Active", "normal_context", "sample-a", "A3");
        var idC = VectorLifecycleMetadataReviewCandidateService.BuildCandidateId("workspace", "collection", "item-a", "Active", "normal_context", "sample-b", "A3");

        Assert.AreEqual(idA, idB);
        Assert.AreNotEqual(idA, idC);
    }

    [TestMethod]
    public void ServiceSource_DoesNotWriteSidecarOrFormalRetrieval()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleMetadataReviewCandidateService.cs"));

        Assert.IsFalse(source.Contains("SidecarMetadataStore", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("FormalRetrievalAllowed = true", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("runtimeEffect", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ApproveForSidecar_WritesSidecarAndUpdatesCandidateStatus()
    {
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var reviewStore = new InMemoryVectorLifecycleMetadataReviewStore();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var service = new VectorLifecycleMetadataReviewService(candidateStore, reviewStore, sidecarStore);
        var candidate = ReviewCandidate("candidate-approve", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3");
        await candidateStore.SaveAsync(candidate);

        var result = await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, confirmed: true));
        var updated = await candidateStore.GetAsync(candidate.CandidateId);
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.SidecarWritten);
        Assert.IsTrue(result.SourceItemUnchanged);
        Assert.AreEqual(VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar, updated!.Status);
        Assert.AreEqual(1, sidecars.Count);
        Assert.AreEqual(candidate.MustHitItemId, sidecars.Single().ItemId);
    }

    [TestMethod]
    public async Task RejectAndNeedsEvidence_DoNotWriteSidecar()
    {
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var reviewStore = new InMemoryVectorLifecycleMetadataReviewStore();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var service = new VectorLifecycleMetadataReviewService(candidateStore, reviewStore, sidecarStore);
        var reject = ReviewCandidate("candidate-reject", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3");
        var needsEvidence = ReviewCandidate("candidate-needs", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3");
        await candidateStore.SaveAsync(reject);
        await candidateStore.SaveAsync(needsEvidence);

        var rejected = await service.ReviewAsync(ReviewRequest(reject, VectorLifecycleMetadataReviewDecisions.Reject, confirmed: false));
        var needs = await service.ReviewAsync(ReviewRequest(needsEvidence, VectorLifecycleMetadataReviewDecisions.NeedsEvidence, confirmed: false));
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.IsTrue(rejected.Succeeded);
        Assert.IsFalse(rejected.SidecarWritten);
        Assert.IsTrue(needs.Succeeded);
        Assert.IsFalse(needs.SidecarWritten);
        Assert.AreEqual(0, sidecars.Count);
    }

    [TestMethod]
    public async Task DeprecatedCandidate_CannotApproveToNormalContext()
    {
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var reviewStore = new InMemoryVectorLifecycleMetadataReviewStore();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var service = new VectorLifecycleMetadataReviewService(candidateStore, reviewStore, sidecarStore);
        var candidate = ReviewCandidate("candidate-deprecated", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", currentLifecycle: "Deprecated");
        await candidateStore.SaveAsync(candidate);

        var result = await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, confirmed: true));
        var updated = await candidateStore.GetAsync(candidate.CandidateId);
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.UnsafeApprovalBlocked);
        Assert.IsFalse(result.SidecarWritten);
        Assert.AreEqual(VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, updated!.Status);
        Assert.AreEqual(0, sidecars.Count);
        StringAssert.Contains(result.BlockedReason, "Deprecated");
    }

    [TestMethod]
    public async Task ApprovedAuditContextSidecar_IsAllowed()
    {
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var reviewStore = new InMemoryVectorLifecycleMetadataReviewStore();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var service = new VectorLifecycleMetadataReviewService(candidateStore, reviewStore, sidecarStore);
        var candidate = ReviewCandidate("candidate-audit", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext, currentLifecycle: "Historical");
        await candidateStore.SaveAsync(candidate);

        var result = await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, confirmed: true));
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.SidecarWritten);
        Assert.AreEqual(VectorQueryTargetSections.AuditContext, sidecars.Single().TargetSectionOverride);
    }

    [TestMethod]
    public async Task MissingEvidence_BlocksApprove()
    {
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var service = new VectorLifecycleMetadataReviewService(
            candidateStore,
            new InMemoryVectorLifecycleMetadataReviewStore(),
            sidecarStore);
        var candidate = ReviewCandidate("candidate-missing-evidence", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", evidenceRefs: [], sourceRefs: []);
        await candidateStore.SaveAsync(candidate);

        var result = await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, confirmed: true, evidenceRefs: [], sourceRefs: []));
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.UnsafeApprovalBlocked);
        Assert.AreEqual("MissingEvidenceOrSourceRefs", result.BlockedReason);
        Assert.AreEqual(0, sidecars.Count);
    }

    [TestMethod]
    public async Task DuplicateReview_StableUpsertsButHistoryPreservesDifferentDecisions()
    {
        var candidateStore = new InMemoryVectorLifecycleMetadataReviewCandidateStore();
        var reviewStore = new InMemoryVectorLifecycleMetadataReviewStore();
        var service = new VectorLifecycleMetadataReviewService(
            candidateStore,
            reviewStore,
            new InMemoryVectorLifecycleSidecarMetadataStore());
        var candidate = ReviewCandidate("candidate-history", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext);
        await candidateStore.SaveAsync(candidate);

        await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.NeedsEvidence, confirmed: false, reason: "need more proof"));
        await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.NeedsEvidence, confirmed: false, reason: "need more proof"));
        await service.ReviewAsync(ReviewRequest(candidate, VectorLifecycleMetadataReviewDecisions.Reject, confirmed: false, reason: "not enough proof"));
        var history = await reviewStore.ListAsync(candidate.CandidateId);

        Assert.AreEqual(2, history.Count);
        Assert.AreEqual(1, history.Count(item => item.Decision == VectorLifecycleMetadataReviewDecisions.NeedsEvidence));
        Assert.AreEqual(1, history.Count(item => item.Decision == VectorLifecycleMetadataReviewDecisions.Reject));
    }

    [TestMethod]
    public async Task FileSystemReviewAndSidecarStore_Roundtrip()
    {
        var root = CreateTempRoot();
        try
        {
            var options = new FileStorageOptions { RootPath = root };
            var reviewStore = new FileVectorLifecycleMetadataReviewStore(options);
            var sidecarStore = new FileVectorLifecycleSidecarMetadataStore(options);
            var record = new VectorLifecycleMetadataReviewRecord
            {
                ReviewId = "review-1",
                CandidateId = "candidate-1",
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                MustHitItemId = "item-1",
                Decision = VectorLifecycleMetadataReviewDecisions.ApproveForSidecar,
                ResultStatus = VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar,
                Reviewer = "reviewer",
                Reason = "reason",
                ProposedLifecycle = "Active",
                ProposedReviewStatus = "Stable",
                ProposedTargetSection = VectorQueryTargetSections.AuditContext,
                EvidenceRefs = ["evidence-1"],
                SourceRefs = ["source-1"],
                SidecarWritten = true,
                ReviewedAt = DateTimeOffset.UtcNow
            };
            var sidecar = new VectorLifecycleSidecarMetadataEntry
            {
                ItemId = "item-1",
                WorkspaceId = "workspace-a",
                CollectionId = "collection-a",
                LifecycleOverride = "Active",
                ReviewStatusOverride = "Stable",
                TargetSectionOverride = VectorQueryTargetSections.AuditContext,
                SourceReviewId = "review-1",
                SourceCandidateId = "candidate-1",
                Reviewer = "reviewer",
                Reason = "reason",
                EvidenceRefs = ["evidence-1"],
                SourceRefs = ["source-1"]
            };

            await reviewStore.SaveAsync(record);
            await sidecarStore.SaveAsync(sidecar);
            var reviews = await reviewStore.ListAsync("candidate-1");
            var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

            Assert.AreEqual(1, reviews.Count);
            Assert.AreEqual("review-1", reviews.Single().ReviewId);
            Assert.AreEqual(1, sidecars.Count);
            Assert.AreEqual(VectorQueryTargetSections.AuditContext, sidecars.Single().TargetSectionOverride);
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
    public void ReviewServiceSource_DoesNotWriteSourceItemOrFormalRetrieval()
    {
        var source = File.ReadAllText(ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleMetadataReviewService.cs"));

        Assert.IsFalse(source.Contains("IContextStore", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("FormalRetrievalAllowed = true", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("sourceItemUnchanged", StringComparison.Ordinal));
    }

    private static VectorLifecycleMetadataReviewCandidateGenerationRequest Request()
        => new()
        {
            WorkspaceId = "workspace-a",
            CollectionId = "collection-a"
        };

    private static VectorLifecycleMetadataRepairPlanSummaryReport Summary(params VectorLifecycleMetadataRepairCandidate[] candidates)
        => new()
        {
            OperationId = "repair-summary-1",
            CorrectlyBlockedSkippedCount = 18,
            Reports =
            [
                new VectorLifecycleMetadataRepairPlanReport
                {
                    OperationId = "repair-a3-1",
                    DatasetName = "A3",
                    CandidateCount = candidates.Length,
                    HumanReviewRequiredCount = candidates.Count(static item => item.RequiresHumanReview),
                    CorrectlyBlockedSkippedCount = 18,
                    Candidates = candidates
                }
            ]
        };

    private static VectorLifecycleMetadataRepairCandidate Candidate(
        string sampleId,
        string itemId,
        string evidence = "evidence-1",
        bool requiresHumanReview = true,
        string forbiddenReason = "MissingProvenance")
        => new()
        {
            DatasetName = "A3",
            SampleId = sampleId,
            MustHitItemId = itemId,
            ItemKind = "note",
            Layer = "context",
            CurrentLifecycle = "Unknown",
            ProposedLifecycle = "Active",
            CurrentReviewStatus = string.Empty,
            ProposedReviewStatus = "Current",
            CurrentTargetSection = VectorQueryTargetSections.Excluded,
            ProposedTargetSection = VectorQueryTargetSections.NormalContext,
            EvidenceRefs = [evidence],
            SourceRefs = ["source-1"],
            ProvenanceAvailable = false,
            RelationEvidenceAvailable = true,
            ReviewEvidenceAvailable = false,
            RepairReason = "review required",
            RequiresHumanReview = requiresHumanReview,
            CanAutoRepair = false,
            ForbiddenReason = forbiddenReason
        };

    private static VectorLifecycleMetadataReviewCandidate CopyWithStatus(
        VectorLifecycleMetadataReviewCandidate candidate,
        string status)
        => new()
        {
            CandidateId = candidate.CandidateId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            SourceSampleId = candidate.SourceSampleId,
            SourceEvalSet = candidate.SourceEvalSet,
            MustHitItemId = candidate.MustHitItemId,
            ItemKind = candidate.ItemKind,
            Layer = candidate.Layer,
            CurrentLifecycle = candidate.CurrentLifecycle,
            CurrentReviewStatus = candidate.CurrentReviewStatus,
            CurrentTargetSection = candidate.CurrentTargetSection,
            ProposedLifecycle = candidate.ProposedLifecycle,
            ProposedReviewStatus = candidate.ProposedReviewStatus,
            ProposedTargetSection = candidate.ProposedTargetSection,
            RepairReason = candidate.RepairReason,
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            SourceRefs = candidate.SourceRefs.ToArray(),
            ProvenanceAvailable = candidate.ProvenanceAvailable,
            RelationEvidenceAvailable = candidate.RelationEvidenceAvailable,
            ReviewEvidenceAvailable = candidate.ReviewEvidenceAvailable,
            RiskIfApproved = candidate.RiskIfApproved.ToArray(),
            RiskIfRejected = candidate.RiskIfRejected.ToArray(),
            RequiresHumanReview = candidate.RequiresHumanReview,
            Status = status,
            CreatedAt = candidate.CreatedAt,
            Metadata = new Dictionary<string, string>(candidate.Metadata, StringComparer.OrdinalIgnoreCase)
        };

    private static VectorLifecycleMetadataReviewCandidate ReviewCandidate(
        string candidateId,
        string workspaceId,
        string collectionId,
        string status,
        string layer,
        string itemKind,
        string evalSet,
        string currentLifecycle = "Unknown",
        string proposedTargetSection = VectorQueryTargetSections.NormalContext,
        IReadOnlyList<string>? evidenceRefs = null,
        IReadOnlyList<string>? sourceRefs = null)
        => new()
        {
            CandidateId = candidateId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SourceSampleId = $"sample-{candidateId}",
            SourceEvalSet = evalSet,
            MustHitItemId = $"item-{candidateId}",
            ItemKind = itemKind,
            Layer = layer,
            CurrentLifecycle = currentLifecycle,
            CurrentReviewStatus = string.Empty,
            ProposedLifecycle = "Active",
            ProposedReviewStatus = "Stable",
            CurrentTargetSection = VectorQueryTargetSections.Excluded,
            ProposedTargetSection = proposedTargetSection,
            RepairReason = "review required",
            EvidenceRefs = evidenceRefs?.ToArray() ?? ["evidence-1"],
            SourceRefs = sourceRefs?.ToArray() ?? ["source-1"],
            RiskIfApproved = ["SidecarWriteWouldChangeEligibilityOnlyAfterFutureApproval"],
            RiskIfRejected = ["RecallRemainsBlockedByLifecycleMetadata"],
            RequiresHumanReview = true,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    private static VectorLifecycleMetadataReviewBatch ReviewBatch(params VectorLifecycleMetadataReviewCandidate[] candidates)
        => new()
        {
            BatchId = "batch-test",
            WorkspaceId = candidates.FirstOrDefault()?.WorkspaceId ?? "workspace-a",
            CollectionId = candidates.FirstOrDefault()?.CollectionId ?? "collection-a",
            CandidateIds = candidates.Select(static item => item.CandidateId).ToArray(),
            CandidateCount = candidates.Length,
            Status = VectorLifecycleMetadataReviewBatchStatuses.Draft,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test"
        };

    private static VectorLifecycleSidecarMetadataEntry Sidecar(
        VectorLifecycleMetadataReviewCandidate candidate,
        string targetSection)
        => new()
        {
            ItemId = candidate.MustHitItemId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            LifecycleOverride = candidate.ProposedLifecycle,
            ReviewStatusOverride = candidate.ProposedReviewStatus,
            TargetSectionOverride = targetSection,
            SourceReviewId = $"review-{candidate.CandidateId}",
            SourceCandidateId = candidate.CandidateId,
            Reviewer = "reviewer",
            Reason = "review reason",
            EvidenceRefs = candidate.EvidenceRefs.ToArray(),
            SourceRefs = candidate.SourceRefs.ToArray()
        };

    private static VectorLifecycleMetadataReviewRequest ReviewRequest(
        VectorLifecycleMetadataReviewCandidate candidate,
        string decision,
        bool confirmed,
        string reason = "review reason",
        IReadOnlyList<string>? evidenceRefs = null,
        IReadOnlyList<string>? sourceRefs = null)
        => new()
        {
            CandidateId = candidate.CandidateId,
            Decision = decision,
            Reviewer = "reviewer",
            Reason = reason,
            ProposedLifecycle = candidate.ProposedLifecycle,
            ProposedReviewStatus = candidate.ProposedReviewStatus,
            ProposedTargetSection = candidate.ProposedTargetSection,
            EvidenceRefs = evidenceRefs?.ToArray() ?? candidate.EvidenceRefs.ToArray(),
            SourceRefs = sourceRefs?.ToArray() ?? candidate.SourceRefs.ToArray(),
            Confirmed = confirmed,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

    private static string CreateTempRoot()
        => Path.Combine(Path.GetTempPath(), "contextcore-vector-review-candidate-tests", Guid.NewGuid().ToString("N"));

    private static string ResolveRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());return TestRepoFileResolver.Resolve(parts);}
}
