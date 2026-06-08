using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreCandidateMemoryGovernanceTests
{
    [TestMethod]
    public async Task Snapshot_ShouldCountCandidateMemories()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-candidate-1", "preference", "Keep terse answers.", ["evidence-1"]));
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("decision-candidate-1", "decision", "Use filesystem storage.", ["evidence-2"]));
        await fixture.ConstraintStore.SaveAsync(CreateCandidateConstraint("constraint-candidate-1", "Do not promote repeated clarification.", ["evidence-3"]));

        var snapshot = await fixture.Service.GetSnapshotAsync("workspace-test", "collection-test");

        Assert.AreEqual(1, snapshot.CandidateMemoryCount);
        Assert.AreEqual(1, snapshot.CandidateDecisionCount);
        Assert.AreEqual(1, snapshot.CandidateConstraintCount);
        Assert.AreEqual(3, snapshot.PendingReviewCount);
        Assert.AreEqual(3, snapshot.RecentCandidates.Count);
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectDuplicateCandidate()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-dup-1", "preference", "Same candidate content.", ["evidence-1"]));
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-dup-2", "preference", "Same candidate content.", ["evidence-2"]));

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.IsTrue(diagnostics.DuplicateCandidateCount >= 2);
        Assert.IsTrue(diagnostics.Diagnostics.Any(item => item.DiagnosticType == CandidateMemoryDiagnosticTypes.DuplicateCandidate));
    }

    [TestMethod]
    public async Task Diagnostics_ShouldDetectCandidateWithoutEvidence()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-no-evidence", "preference", "No evidence candidate.", []));

        var diagnostics = await fixture.Service.GetDiagnosticsAsync("workspace-test", "collection-test");

        Assert.AreEqual(1, diagnostics.CandidateWithoutEvidenceCount);
        Assert.AreEqual(CandidateMemoryDiagnosticTypes.CandidateWithoutEvidence, diagnostics.Diagnostics[0].DiagnosticType);
    }

    [TestMethod]
    public async Task Explain_ShouldReturnSourcePromotionCandidate()
    {
        var fixture = CreateFixture();
        await fixture.PromotionStore.SaveAsync(new ShortTermPromotionCandidate
        {
            CandidateId = "stpc-source-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            SourceWorkingItemId = "working-1",
            Kind = "RecentDecision",
            Title = "Use filesystem storage.",
            Summary = "Use filesystem storage for local persistence.",
            SuggestedTargetLayer = "CandidateMemory",
            Reason = "Accepted by reviewer.",
            Confidence = 0.9,
            Importance = 0.8,
            EvidenceRefs = ["evidence-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            Status = PromotionCandidateStatus.Accepted
        });
        await fixture.PromotionStore.AppendReviewAsync(new PromotionCandidateReviewRecord
        {
            ReviewId = "review-1",
            CandidateId = "stpc-source-1",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Action = "accept",
            FromStatus = PromotionCandidateStatus.Candidate,
            ToStatus = PromotionCandidateStatus.Accepted,
            Reviewer = "tester",
            Reason = "Accepted.",
            TargetItemId = "mem-candidate-1",
            TargetItemKind = "memory",
            TargetLayer = "CandidateMemory",
            EvidenceRefs = ["evidence-1"],
            CreatedAt = DateTimeOffset.UtcNow,
            ReviewedAt = DateTimeOffset.UtcNow
        });
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory(
            "mem-candidate-1",
            "decision",
            "Use filesystem storage.",
            ["evidence-1"],
            "stpc-source-1"));

        var explanation = await fixture.Service.ExplainAsync("mem-candidate-1", "workspace-test", "collection-test");

        Assert.IsNotNull(explanation);
        Assert.IsNotNull(explanation!.SourcePromotionCandidate);
        Assert.AreEqual("stpc-source-1", explanation.SourcePromotionCandidate!.CandidateId);
        Assert.AreEqual(1, explanation.PromotionReviewHistory.Count);
        Assert.IsTrue(explanation.ProvenanceChain.Any(item => item.SourceType == "ShortTermPromotionCandidate"));
    }

    [TestMethod]
    public void ControlRoom_ShouldRenderCandidateMemoryPage()
    {
        var snapshot = new ServiceCandidateMemorySnapshot
        {
            CurrentTime = DateTimeOffset.UtcNow,
            BaseUrl = "http://localhost:5079",
            Snapshot = new CandidateMemorySnapshot
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                CandidateMemoryCount = 1,
                CandidateConstraintCount = 1,
                CandidateDecisionCount = 1,
                PendingReviewCount = 3,
                RecentCandidates =
                [
                    new CandidateMemoryRecord
                    {
                        Id = "mem-candidate-1",
                        WorkspaceId = "workspace-test",
                        CollectionId = "collection-test",
                        CandidateKind = CandidateMemoryKinds.Memory,
                        Type = "preference",
                        Title = "Keep terse answers.",
                        Status = ContextMemoryStatus.Candidate,
                        Lifecycle = CandidateMemoryLifecycle.Current,
                        EvidenceRefs = ["evidence-1"]
                    }
                ]
            },
            Diagnostics = new CandidateMemoryDiagnosticsReport
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                CandidateWithoutEvidenceCount = 1,
                DiagnosticCount = 1,
                Diagnostics =
                [
                    new CandidateMemoryDiagnostic
                    {
                        CandidateId = "mem-candidate-1",
                        DiagnosticType = CandidateMemoryDiagnosticTypes.CandidateWithoutEvidence,
                        Severity = "High",
                        Reason = "Candidate has no evidence refs."
                    }
                ]
            }
        };

        var rendered = ServiceOperationalRenderer.RenderCandidateMemory(snapshot);

        StringAssert.Contains(rendered, "Service Candidate Memory");
        StringAssert.Contains(rendered, "CandidateMemoryCount");
        StringAssert.Contains(rendered, "Recent Candidates");
        StringAssert.Contains(rendered, "Diagnostics");
        StringAssert.Contains(rendered, "mem-candidate-1");
    }

    [TestMethod]
    public async Task RejectCandidate_ShouldRecordReview()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-reject", "preference", "Reject this candidate.", ["evidence-1"]));

        var result = await fixture.ReviewService.RejectAsync("mem-reject", CreateReviewRequest("manual reject"));
        var updated = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "mem-reject");
        var reviews = await fixture.ReviewStore.QueryReviewsAsync("mem-reject");

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Rejected, result!.ToStatus);
        Assert.AreEqual(ContextMemoryStatus.Rejected, updated!.Status);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(CandidateMemoryReviewActions.Reject, reviews[0].Action);
    }

    [TestMethod]
    public async Task ExpireCandidate_ShouldRecordReview()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-expire", "preference", "Old candidate.", ["evidence-1"]));

        var result = await fixture.ReviewService.ExpireAsync("mem-expire", CreateReviewRequest("stale"));
        var updated = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "mem-expire");
        var reviews = await fixture.ReviewStore.QueryReviewsAsync("mem-expire");

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, result!.ToStatus);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, updated!.Status);
        Assert.AreEqual(CandidateMemoryLifecycle.Stale, updated.Metadata["lifecycle"]);
        Assert.AreEqual(1, reviews.Count);
    }

    [TestMethod]
    public async Task NeedsMoreEvidence_ShouldRecordReview()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-evidence", "preference", "Needs evidence.", []));

        var result = await fixture.ReviewService.NeedsMoreEvidenceAsync("mem-evidence", CreateReviewRequest("missing evidence"));
        var updated = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "mem-evidence");
        var reviews = await fixture.ReviewStore.QueryReviewsAsync("mem-evidence");

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Candidate, result!.ToStatus);
        Assert.AreEqual(ContextMemoryStatus.Candidate, updated!.Status);
        Assert.AreEqual(CandidateMemoryReviewStates.NeedsMoreEvidence, updated.Metadata["candidateReviewState"]);
        Assert.AreEqual(1, reviews.Count);
        Assert.IsTrue(result.Warnings.Any(item => item.Contains("evidence", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SupersedeCandidate_ShouldValidateTarget()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-old", "preference", "Old candidate.", ["evidence-old"]));
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemory("mem-new", "preference", "New candidate.", ["evidence-new"]));

        var result = await fixture.ReviewService.SupersedeAsync("mem-old", CreateReviewRequest(
            "newer candidate",
            supersedeTargetCandidateId: "mem-new"));
        var updated = await fixture.MemoryStore.GetAsync("workspace-test", "collection-test", "mem-old");

        Assert.IsNotNull(result);
        Assert.AreEqual("mem-new", result!.SupersedeTargetCandidateId);
        Assert.AreEqual(ContextMemoryStatus.Deprecated, updated!.Status);
        Assert.AreEqual(CandidateMemoryLifecycle.Superseded, updated.Metadata["lifecycle"]);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.ReviewService.SupersedeAsync("mem-new", CreateReviewRequest(
                "missing target",
                supersedeTargetCandidateId: "missing-target")));
    }

    [TestMethod]
    public async Task InvalidTransition_ShouldThrowStructuredValidationExceptionSource()
    {
        var fixture = CreateFixture();
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "mem-stable",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "preference",
            Content = "Already stable.",
            SourceRefs = ["evidence-1"],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["evidenceRefs"] = "evidence-1"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.ReviewService.RejectAsync("mem-stable", CreateReviewRequest("invalid")));
        StringAssert.Contains(ex.Message, "Stable");
    }

    private static CandidateMemoryFixture CreateFixture()
    {
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var promotionStore = new InMemoryShortTermPromotionCandidateStore();
        var stableStore = new InMemoryStableReviewCandidateStore();
        var gapStore = new InMemoryConstraintGapCandidateStore();
        var learningStore = new InMemoryContextLearningStore();
        var constraintReviewStore = new InMemoryCandidateConstraintReviewStore();
        var candidateMemoryReviewStore = new InMemoryCandidateMemoryReviewStore();
        var service = new CandidateMemorySnapshotService(
            memoryStore,
            constraintStore,
            promotionStore,
            stableStore,
            gapStore,
            learningStore,
            constraintReviewStore,
            candidateMemoryReviewStore);
        var reviewService = new CandidateMemoryReviewService(
            memoryStore,
            constraintStore,
            candidateMemoryReviewStore);
        return new CandidateMemoryFixture(memoryStore, constraintStore, promotionStore, candidateMemoryReviewStore, service, reviewService);
    }

    private static CandidateMemoryReviewRequest CreateReviewRequest(
        string reason,
        string? supersedeTargetCandidateId = null)
    {
        return new CandidateMemoryReviewRequest
        {
            OperationId = $"test-{Guid.NewGuid():N}",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Reviewer = "tester",
            Reason = reason,
            SupersedeTargetCandidateId = supersedeTargetCandidateId,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = "true"
            }
        };
    }

    private static ContextMemoryItem CreateCandidateMemory(
        string id,
        string type,
        string content,
        IReadOnlyList<string> evidenceRefs,
        string? promotionCandidateId = null)
    {
        var sourceRefs = evidenceRefs
            .Concat(string.IsNullOrWhiteSpace(promotionCandidateId) ? [] : [promotionCandidateId])
            .ToArray();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["evidenceRefs"] = string.Join(",", evidenceRefs),
            ["suggestedTargetLayer"] = type == "decision" ? "DecisionRecord" : "CandidateMemory"
        };
        if (!string.IsNullOrWhiteSpace(promotionCandidateId))
        {
            metadata["sourceCandidateId"] = promotionCandidateId;
        }

        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Structured,
            Status = ContextMemoryStatus.Candidate,
            Type = type,
            Content = content,
            SourceRefs = sourceRefs,
            Importance = 0.8,
            Confidence = 0.9,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ContextConstraint CreateCandidateConstraint(
        string id,
        string content,
        IReadOnlyList<string> evidenceRefs)
    {
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Hard,
            Content = content,
            AppliesToRefs = [],
            SourceRefs = evidenceRefs,
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["evidenceRefs"] = string.Join(",", evidenceRefs)
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed record CandidateMemoryFixture(
        InMemoryMemoryStore MemoryStore,
        InMemoryConstraintStore ConstraintStore,
        InMemoryShortTermPromotionCandidateStore PromotionStore,
        InMemoryCandidateMemoryReviewStore ReviewStore,
        CandidateMemorySnapshotService Service,
        CandidateMemoryReviewService ReviewService);
}
