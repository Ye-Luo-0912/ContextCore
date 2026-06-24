using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
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
    public void ProductionServiceAndStores_DoNotContainFixtureDomainLexicon()
    {
        var files = new[]
        {
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleMetadataReviewCandidateService.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleMetadataReviewService.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleSidecarResolver.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorSidecarEligibilityPreviewRunner.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleMetadataReviewBatchService.cs"),
            ResolveRepoFile("src", "ContextCore.Core", "Services", "Vector", "VectorLifecycleMetadataEvidenceBackfillRunner.cs"),
            ResolveRepoFile("src", "ContextCore.Storage.FileSystem", "Stores", "FileVectorLifecycleMetadataReviewCandidateStore.cs"),
            ResolveRepoFile("src", "ContextCore.Storage.FileSystem", "Stores", "FileVectorLifecycleMetadataReviewStore.cs"),
            ResolveRepoFile("src", "ContextCore.Storage.FileSystem", "Stores", "FileVectorLifecycleSidecarMetadataStore.cs"),
            ResolveRepoFile("src", "ContextCore.Storage.InMemory", "Stores", "InMemoryVectorLifecycleMetadataReviewCandidateStore.cs"),
            ResolveRepoFile("src", "ContextCore.Storage.InMemory", "Stores", "InMemoryVectorLifecycleMetadataReviewStore.cs"),
            ResolveRepoFile("src", "ContextCore.Storage.InMemory", "Stores", "InMemoryVectorLifecycleSidecarMetadataStore.cs")
        };

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var forbidden in new[] { "林风", "苍穹大陆", "九转金丹", "龙魂草", "拍卖行" })
            {
                Assert.IsFalse(source.Contains(forbidden, StringComparison.Ordinal), $"Production source must not contain fixture/domain keyword: {forbidden}");
            }
        }
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
    public void EvidenceBackfillPreview_MissingRefsMarksNeedsEvidence()
    {
        var candidate = ReviewCandidate(
            "candidate-missing-evidence",
            "workspace-a",
            "collection-a",
            VectorLifecycleMetadataReviewCandidateStatuses.PendingReview,
            "context",
            "note",
            "A3",
            evidenceRefs: [],
            sourceRefs: []);
        var report = new VectorLifecycleMetadataEvidenceBackfillRunner().BuildReport(
            ReviewBatch(candidate),
            [candidate],
            new Dictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot>(StringComparer.OrdinalIgnoreCase),
            "vector/eligibility/review-batches/test/batch.json",
            "preview");

        Assert.AreEqual(1, report.CandidateCount);
        Assert.AreEqual(1, report.NeedsEvidenceCount);
        Assert.AreEqual("NeedsIngestionMetadataBackfill", report.Recommendation);
        Assert.IsTrue(report.Candidates.Single().ShouldRemainNeedsEvidence);
        Assert.IsFalse(report.SidecarWritten);
        Assert.IsFalse(report.FormalRetrievalAllowed);
        Assert.IsFalse(report.UseForRuntime);
    }

    [TestMethod]
    public void EvidenceBackfillPreview_BackfilledRefsCanReclassifyAutoRepairable()
    {
        var candidate = ReviewCandidate(
            "candidate-backfilled",
            "workspace-a",
            "collection-a",
            VectorLifecycleMetadataReviewCandidateStatuses.PendingReview,
            "context",
            "note",
            "A3",
            evidenceRefs: [],
            sourceRefs: []);
        var snapshot = new VectorLifecycleMetadataEvidenceSourceSnapshot
        {
            ItemId = candidate.MustHitItemId,
            SourceRefs = ["source-1"],
            EvidenceRefs = ["evidence-1"],
            ProvenanceRecordId = "provenance-1",
            Lifecycle = "Active",
            ReviewStatus = "Stable"
        };

        var report = new VectorLifecycleMetadataEvidenceBackfillRunner().BuildReport(
            ReviewBatch(candidate),
            [candidate],
            new Dictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [candidate.MustHitItemId] = snapshot
            },
            "vector/eligibility/review-batches/test/batch.json",
            "audit");

        Assert.AreEqual(1, report.AutoRepairableAfterBackfillCount);
        Assert.AreEqual("ReadyForRepairPlanRerun", report.Recommendation);
        Assert.IsTrue(report.Candidates.Single().CanReclassifyAsAutoRepairable);
    }

    [TestMethod]
    public void EvidenceBackfillPreview_ReplacementConflictForbidsRepair()
    {
        var candidate = ReviewCandidate(
            "candidate-conflict",
            "workspace-a",
            "collection-a",
            VectorLifecycleMetadataReviewCandidateStatuses.PendingReview,
            "context",
            "note",
            "A3",
            evidenceRefs: [],
            sourceRefs: []);
        var snapshot = new VectorLifecycleMetadataEvidenceSourceSnapshot
        {
            ItemId = candidate.MustHitItemId,
            SourceRefs = ["source-1"],
            EvidenceRefs = ["evidence-1"],
            ProvenanceRecordId = "provenance-1",
            ReplacementState = "superseded"
        };

        var report = new VectorLifecycleMetadataEvidenceBackfillRunner().BuildReport(
            ReviewBatch(candidate),
            [candidate],
            new Dictionary<string, VectorLifecycleMetadataEvidenceSourceSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [candidate.MustHitItemId] = snapshot
            },
            "vector/eligibility/review-batches/test/batch.json",
            "audit");

        Assert.AreEqual(1, report.ReplacementConflictCount);
        Assert.AreEqual(1, report.ForbiddenRepairCount);
        Assert.IsTrue(report.Candidates.Single().ForbiddenToRepair);
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

    [TestMethod]
    public void SidecarEligibilityPreview_NoSidecarEqualsBaseEligibility()
    {
        var runner = new VectorSidecarEligibilityPreviewRunner();
        var candidate = ReviewCandidate("candidate-base", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3");

        var report = runner.BuildReport([candidate], Array.Empty<VectorLifecycleSidecarMetadataEntry>(), "preview");
        var resolution = report.Resolutions.Single();

        Assert.AreEqual(VectorLifecycleSidecarResolutionSources.Base, resolution.Source);
        Assert.AreEqual(candidate.CurrentLifecycle, resolution.EffectiveLifecycle);
        Assert.AreEqual(candidate.CurrentTargetSection, resolution.EffectiveTargetSection);
        Assert.AreEqual(0, report.EffectiveMetadataChangedCount);
        Assert.AreEqual("NoApprovedSidecarEntries", report.Recommendation);
        Assert.IsFalse(report.UseForRuntime);
        Assert.IsFalse(report.FormalRetrievalAllowed);
    }

    [TestMethod]
    public void SidecarEligibilityPreview_ApprovedSidecarChangesEffectiveMetadata()
    {
        var runner = new VectorSidecarEligibilityPreviewRunner();
        var candidate = CopyWithStatus(
            ReviewCandidate("candidate-approved", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext),
            VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar);

        var report = runner.BuildReport([candidate], [Sidecar(candidate, VectorQueryTargetSections.AuditContext)], "preview");
        var resolution = report.Resolutions.Single();

        Assert.AreEqual(VectorLifecycleSidecarResolutionSources.Sidecar, resolution.Source);
        Assert.AreEqual(VectorQueryTargetSections.AuditContext, resolution.EffectiveTargetSection);
        Assert.AreEqual(1, report.EffectiveMetadataChangedCount);
        Assert.AreEqual("ReadyForHumanReviewBatch", report.Recommendation);
    }

    [TestMethod]
    public void SidecarEligibilityPreview_RejectedNeedsEvidenceAndSupersededSidecarsAreIgnored()
    {
        var runner = new VectorSidecarEligibilityPreviewRunner();
        var rejected = CopyWithStatus(
            ReviewCandidate("candidate-rejected", "workspace-a", "collection-a", "Rejected", "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext),
            VectorLifecycleMetadataReviewCandidateStatuses.Rejected);

        var report = runner.BuildReport([rejected], [Sidecar(rejected, VectorQueryTargetSections.AuditContext)], "preview");
        var resolution = report.Resolutions.Single();

        Assert.AreEqual(VectorLifecycleSidecarResolutionSources.Base, resolution.Source);
        Assert.AreEqual(0, report.EffectiveMetadataChangedCount);
        Assert.AreEqual("NoApprovedSidecarEntries", report.Recommendation);
    }

    [TestMethod]
    public void SidecarEligibilityPreview_UnsafeNormalContextSidecarIsBlocked()
    {
        var runner = new VectorSidecarEligibilityPreviewRunner();
        var candidate = CopyWithStatus(
            ReviewCandidate("candidate-unsafe", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", currentLifecycle: "Deprecated", proposedTargetSection: VectorQueryTargetSections.NormalContext),
            VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar);

        var report = runner.BuildReport([candidate], [Sidecar(candidate, VectorQueryTargetSections.NormalContext)], "preview");
        var resolution = report.Resolutions.Single();

        Assert.IsTrue(resolution.Blocked);
        Assert.AreEqual("UnsafeNormalContextSidecarBlocked", resolution.BlockedReason);
        Assert.AreEqual(1, report.UnsafeSidecarBlockedCount);
        Assert.AreEqual("BlockedByUnsafeSidecar", report.Recommendation);
    }

    [TestMethod]
    public void SidecarEligibilityPreview_ConflictSidecarFailsClosed()
    {
        var runner = new VectorSidecarEligibilityPreviewRunner();
        var candidate = CopyWithStatus(
            ReviewCandidate("candidate-conflict", "workspace-a", "collection-a", "PendingReview", "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext),
            VectorLifecycleMetadataReviewCandidateStatuses.ApprovedForSidecar);

        var report = runner.BuildReport(
            [candidate],
            [
                Sidecar(candidate, VectorQueryTargetSections.AuditContext),
                Sidecar(candidate, VectorQueryTargetSections.HistoricalContext)
            ],
            "preview");

        var resolution = report.Resolutions.Single();
        Assert.AreEqual(VectorLifecycleSidecarResolutionSources.Conflict, resolution.Source);
        Assert.IsTrue(resolution.Blocked);
        Assert.AreEqual(1, report.ConflictSidecarBlockedCount);
        Assert.IsTrue(report.SourceItemUnchanged);
    }

    [TestMethod]
    public void ReviewBatchCreate_FromPendingCandidates()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var candidates = Enumerable.Range(0, 32)
            .Select(index => ReviewCandidate($"candidate-{index:D2}", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3"))
            .ToArray();

        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");

        Assert.AreEqual(32, batch.CandidateCount);
        Assert.AreEqual(VectorLifecycleMetadataReviewBatchStatuses.Draft, batch.Status);
        Assert.AreEqual(32, batch.CandidateIds.Count);
    }

    [TestMethod]
    public void ReviewBatchExport_IsStableAndDeterministic()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var candidates = new[]
        {
            ReviewCandidate("candidate-b", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "memory", "fact", "A3"),
            ReviewCandidate("candidate-a", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3")
        };
        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");

        var first = service.ExportReviewSheet(batch, candidates);
        var second = service.ExportReviewSheet(batch, candidates);

        CollectionAssert.AreEqual(first.Select(static item => item.CandidateId).ToArray(), second.Select(static item => item.CandidateId).ToArray());
        Assert.AreEqual("candidate-a", first[0].CandidateId);
    }

    [TestMethod]
    public void ReviewBatchValidate_ImportsReviewerDecisions()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var approve = ReviewCandidate("candidate-approve", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext);
        var reject = ReviewCandidate("candidate-reject", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3");
        var needs = ReviewCandidate("candidate-needs", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3");
        var supersede = ReviewCandidate("candidate-super", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3");
        var candidates = new[] { approve, reject, needs, supersede };
        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");
        var rows = service.ExportReviewSheet(batch, candidates)
            .Select(row => row.CandidateId switch
            {
                "candidate-approve" => CopyRow(row, VectorLifecycleMetadataReviewDecisions.ApproveForSidecar),
                "candidate-reject" => CopyRow(row, VectorLifecycleMetadataReviewDecisions.Reject),
                "candidate-needs" => CopyRow(row, VectorLifecycleMetadataReviewDecisions.NeedsEvidence),
                _ => CopyRow(row, VectorLifecycleMetadataReviewDecisions.Supersede)
            })
            .ToArray();

        var report = service.Validate(batch, candidates, rows);

        Assert.AreEqual(0, report.ValidationErrorCount);
        Assert.AreEqual(1, report.ApprovalCount);
        Assert.AreEqual(1, report.RejectCount);
        Assert.AreEqual(1, report.NeedsEvidenceCount);
        Assert.AreEqual(1, report.SupersedeCount);
        Assert.AreEqual("ReadyForSidecarApply", report.Recommendation);
    }

    [TestMethod]
    public void ReviewBatchValidate_AllNeedsEvidenceRecommendsNeedsEvidence()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var candidates = new[]
        {
            ReviewCandidate("candidate-needs-a", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3"),
            ReviewCandidate("candidate-needs-b", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3")
        };
        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");
        var rows = service.ExportReviewSheet(batch, candidates)
            .Select(row => CopyRow(row, VectorLifecycleMetadataReviewDecisions.NeedsEvidence))
            .ToArray();

        var validation = service.Validate(batch, candidates, rows);
        var preview = service.BuildApplyPreview(batch, candidates, rows, validation);

        Assert.AreEqual(2, validation.NeedsEvidenceCount);
        Assert.AreEqual("NeedsEvidence", validation.Recommendation);
        Assert.AreEqual(0, preview.WouldWriteSidecarEntryCount);
        Assert.AreEqual("NeedsEvidence", preview.Recommendation);
        Assert.IsFalse(preview.RealSidecarWritten);
    }

    [TestMethod]
    public void ReviewBatchValidate_MissingReviewerOrEvidenceBlocksApprove()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var candidate = ReviewCandidate("candidate-missing", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", evidenceRefs: [], sourceRefs: []);
        var batch = service.CreateBatch("workspace-a", "collection-a", [candidate], "reviewer", "instructions");
        var row = CopyRow(service.ExportReviewSheet(batch, [candidate]).Single(), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, reviewer: "", reason: "");

        var report = service.Validate(batch, [candidate], [row]);

        Assert.IsTrue(report.ValidationErrorCount >= 3);
        Assert.AreEqual(1, report.MissingEvidenceCount);
        Assert.AreEqual("BlockedByMissingEvidence", report.Recommendation);
    }

    [TestMethod]
    public void ReviewBatchValidate_UnsafeNormalContextApprovalBlocks()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var candidate = ReviewCandidate("candidate-deprecated-batch", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", currentLifecycle: "Deprecated", proposedTargetSection: VectorQueryTargetSections.NormalContext);
        var batch = service.CreateBatch("workspace-a", "collection-a", [candidate], "reviewer", "instructions");
        var row = CopyRow(service.ExportReviewSheet(batch, [candidate]).Single(), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar);

        var report = service.Validate(batch, [candidate], [row]);

        Assert.AreEqual(1, report.UnsafeDecisionCount);
        Assert.AreEqual("BlockedByUnsafeDecision", report.Recommendation);
    }

    [TestMethod]
    public void ReviewBatchValidate_BlocksDuplicateAndUnknownDecision()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var first = ReviewCandidate("candidate-duplicate", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3");
        var second = ReviewCandidate("candidate-unknown", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3");
        var candidates = new[] { first, second };
        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");
        var exported = service.ExportReviewSheet(batch, candidates);
        var rows = new[]
        {
            CopyRow(exported.Single(row => row.CandidateId == "candidate-duplicate"), VectorLifecycleMetadataReviewDecisions.Reject),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-duplicate"), VectorLifecycleMetadataReviewDecisions.Reject),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-unknown"), "NotAValidDecision")
        };

        var report = service.Validate(batch, candidates, rows);

        Assert.AreEqual(2, report.ValidationErrorCount);
        Assert.AreEqual(1, report.Issues.Count(static issue => issue.Reason == "DuplicateCandidateDecision"));
        Assert.AreEqual(1, report.Issues.Count(static issue => issue.Reason == "UnknownDecision"));
        Assert.IsFalse(report.LastWriteWins);
    }

    [TestMethod]
    public async Task ReviewBatchApplyPreview_DoesNotWriteSidecar()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var candidate = ReviewCandidate("candidate-preview", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext);
        var batch = service.CreateBatch("workspace-a", "collection-a", [candidate], "reviewer", "instructions");
        var row = CopyRow(service.ExportReviewSheet(batch, [candidate]).Single(), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar);
        var validation = service.Validate(batch, [candidate], [row]);

        var preview = service.BuildApplyPreview(batch, [candidate], [row], validation);
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.AreEqual(1, preview.WouldWriteSidecarEntryCount);
        Assert.IsFalse(preview.RealSidecarWritten);
        Assert.AreEqual(0, sidecars.Count);
    }

    [TestMethod]
    public async Task ReviewBatchApplyPreview_CountsOnlyValidApproveWithInvalidRows()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var sidecarStore = new InMemoryVectorLifecycleSidecarMetadataStore();
        var valid = ReviewCandidate("candidate-valid-approve", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext);
        var unsafeCandidate = ReviewCandidate("candidate-unsafe-approve", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", currentLifecycle: "Deprecated", proposedTargetSection: VectorQueryTargetSections.NormalContext);
        var missingEvidence = ReviewCandidate("candidate-missing-evidence-preview", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", evidenceRefs: [], sourceRefs: []);
        var candidates = new[] { valid, unsafeCandidate, missingEvidence };
        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");
        var exported = service.ExportReviewSheet(batch, candidates);
        var rows = new[]
        {
            CopyRow(exported.Single(row => row.CandidateId == "candidate-valid-approve"), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-unsafe-approve"), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-missing-evidence-preview"), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar, evidenceRefs: [], sourceRefs: [])
        };
        var validation = service.Validate(batch, candidates, rows);

        var preview = service.BuildApplyPreview(batch, candidates, rows, validation);
        var sidecars = await sidecarStore.QueryAsync("workspace-a", "collection-a");

        Assert.AreEqual(2, validation.ValidationErrorCount);
        Assert.AreEqual(1, validation.UnsafeDecisionCount);
        Assert.AreEqual(1, validation.MissingEvidenceCount);
        Assert.AreEqual(1, preview.WouldWriteSidecarEntryCount);
        Assert.AreEqual(1, preview.UnsafeBlockedCount);
        Assert.AreEqual("BlockedByUnsafeDecision", preview.Recommendation);
        Assert.IsFalse(preview.RealSidecarWritten);
        Assert.IsFalse(preview.FormalRetrievalAllowed);
        Assert.IsFalse(preview.UseForRuntime);
        Assert.AreEqual(0, sidecars.Count);
    }

    [TestMethod]
    public void ReviewBatchImportSmokeMetrics_AreDeterministic()
    {
        var service = new VectorLifecycleMetadataReviewBatchService();
        var candidates = new[]
        {
            ReviewCandidate("candidate-smoke-approve", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3", proposedTargetSection: VectorQueryTargetSections.AuditContext),
            ReviewCandidate("candidate-smoke-reject", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3"),
            ReviewCandidate("candidate-smoke-unknown", "workspace-a", "collection-a", VectorLifecycleMetadataReviewCandidateStatuses.PendingReview, "context", "note", "A3")
        };
        var batch = service.CreateBatch("workspace-a", "collection-a", candidates, "reviewer", "instructions");
        var exported = service.ExportReviewSheet(batch, candidates);
        var rows = new[]
        {
            CopyRow(exported.Single(row => row.CandidateId == "candidate-smoke-approve"), VectorLifecycleMetadataReviewDecisions.ApproveForSidecar),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-smoke-reject"), VectorLifecycleMetadataReviewDecisions.Reject),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-smoke-reject"), VectorLifecycleMetadataReviewDecisions.Reject),
            CopyRow(exported.Single(row => row.CandidateId == "candidate-smoke-unknown"), "InvalidDecision")
        };

        var importResult = service.BuildImportResult(batch.BatchId, rows);
        var validation = service.Validate(batch, candidates, rows);

        Assert.AreEqual(4, importResult.RowCount);
        Assert.AreEqual(4, importResult.DecisionCount);
        Assert.AreEqual(2, validation.ValidationErrorCount);
        Assert.AreEqual("BlockedByValidationError", validation.Recommendation);
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

    private static VectorLifecycleMetadataReviewSheetRow CopyRow(
        VectorLifecycleMetadataReviewSheetRow row,
        string decision,
        string reviewer = "reviewer",
        string reason = "review reason",
        IReadOnlyList<string>? evidenceRefs = null,
        IReadOnlyList<string>? sourceRefs = null,
        string? targetSectionOverride = null,
        string notes = "")
        => new()
        {
            CandidateId = row.CandidateId,
            MustHitItemId = row.MustHitItemId,
            CurrentLifecycle = row.CurrentLifecycle,
            ProposedLifecycle = row.ProposedLifecycle,
            CurrentTargetSection = row.CurrentTargetSection,
            ProposedTargetSection = row.ProposedTargetSection,
            EvidenceRefs = evidenceRefs?.ToArray() ?? row.EvidenceRefs.ToArray(),
            SourceRefs = sourceRefs?.ToArray() ?? row.SourceRefs.ToArray(),
            RepairReason = row.RepairReason,
            ReviewerDecision = decision,
            ReviewerReason = reason,
            Reviewer = reviewer,
            TargetSectionOverride = targetSectionOverride ?? row.TargetSectionOverride,
            Notes = notes.Length == 0 ? row.Notes : notes
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
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail("Could not resolve repository file: " + Path.Combine(parts));
        return string.Empty;
    }
}
