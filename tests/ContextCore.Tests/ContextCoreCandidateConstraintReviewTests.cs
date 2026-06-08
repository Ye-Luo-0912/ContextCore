using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;

namespace ContextCore.Tests;

/// <summary>覆盖 CandidateConstraint 人工 activate / reject 审核链路。</summary>
[TestClass]
public sealed class ContextCoreCandidateConstraintReviewTests
{
    [TestMethod]
    public async Task ActivateCandidateConstraint_ShouldPromoteToActiveHardAndPreserveSourceRefs()
    {
        var constraintStore = new InMemoryConstraintStore();
        var reviewStore = new InMemoryCandidateConstraintReviewStore();
        var service = new CandidateConstraintReviewService(constraintStore, reviewStore);
        await constraintStore.SaveAsync(CreateCandidateConstraint("candidate-constraint-1"));

        var result = await service.ActivateAsync(
            "candidate-constraint-1",
            new CandidateConstraintReviewRequest
            {
                OperationId = "activate-op-1",
                Reviewer = "reviewer-1",
                Reason = "manual activation"
            });

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Active, result.Status);
        Assert.AreEqual("candidate-constraint-1", result.ActivatedConstraintId);
        Assert.AreEqual("ActiveHardConstraint", result.TargetLayer);
        Assert.AreEqual(ConstraintLevel.Hard, result.Constraint.Level);
        Assert.AreEqual(ContextMemoryStatus.Active, result.Constraint.Status);
        Assert.AreEqual("candidate_constraint_activate", result.Constraint.Metadata["createdFrom"]);
        Assert.AreEqual("constraint_gap_accept", result.Constraint.Metadata["candidateCreatedFrom"]);
        Assert.AreEqual("gap-1", result.Constraint.Metadata["sourceConstraintGapId"]);
        Assert.AreEqual("sample-1", result.Constraint.Metadata["sourceSampleId"]);
        Assert.AreEqual("operation-1", result.Constraint.Metadata["sourceOperationId"]);
        Assert.AreEqual("eval:sample-1,event-1", result.Constraint.Metadata["evidenceRefs"]);
        Assert.AreEqual("reviewer-1", result.Constraint.Metadata["reviewer"]);
        Assert.AreEqual("manual activation", result.Constraint.Metadata["reviewReason"]);

        var active = await constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Level = ConstraintLevel.Hard,
            Status = ContextMemoryStatus.Active
        });
        Assert.AreEqual(1, active.Count);
        Assert.AreEqual("candidate-constraint-1", active[0].Id);
    }

    [TestMethod]
    public async Task RejectCandidateConstraint_ShouldMarkRejectedAndRecordReview()
    {
        var constraintStore = new InMemoryConstraintStore();
        var reviewStore = new InMemoryCandidateConstraintReviewStore();
        var service = new CandidateConstraintReviewService(constraintStore, reviewStore);
        await constraintStore.SaveAsync(CreateCandidateConstraint("candidate-constraint-1"));

        var result = await service.RejectAsync(
            "candidate-constraint-1",
            new CandidateConstraintReviewRequest
            {
                OperationId = "reject-op-1",
                Reviewer = "reviewer-1",
                Reason = "not a valid hard constraint"
            });

        Assert.IsNotNull(result);
        Assert.AreEqual(ContextMemoryStatus.Rejected, result.Status);
        Assert.IsNull(result.ActivatedConstraintId);
        Assert.AreEqual("RejectedCandidateConstraint", result.TargetLayer);

        var stored = await constraintStore.GetAsync("candidate-constraint-1");
        Assert.IsNotNull(stored);
        Assert.AreEqual(ContextMemoryStatus.Rejected, stored.Status);
        Assert.AreEqual(ConstraintLevel.User, stored.Level);
        Assert.AreEqual("reviewer-1", stored.Metadata["reviewer"]);
        Assert.AreEqual("not a valid hard constraint", stored.Metadata["reviewReason"]);

        var reviews = await service.GetReviewsAsync("candidate-constraint-1");
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("reject", reviews[0].Action);
        Assert.AreEqual(ContextMemoryStatus.Candidate, reviews[0].FromStatus);
        Assert.AreEqual(ContextMemoryStatus.Rejected, reviews[0].ToStatus);
        Assert.AreEqual("gap-1", reviews[0].SourceConstraintGapId);
        CollectionAssert.AreEqual(new[] { "eval:sample-1", "event-1" }, reviews[0].EvidenceRefs.ToArray());
    }

    [TestMethod]
    public async Task ActivateCandidateConstraint_ShouldBlockDuplicateActiveHardConstraint()
    {
        var constraintStore = new InMemoryConstraintStore();
        var reviewStore = new InMemoryCandidateConstraintReviewStore();
        var service = new CandidateConstraintReviewService(constraintStore, reviewStore);
        await constraintStore.SaveAsync(CreateCandidateConstraint("candidate-constraint-1"));
        await constraintStore.SaveAsync(CreateActiveConstraint("active-constraint-1"));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            service.ActivateAsync(
                "candidate-constraint-1",
                new CandidateConstraintReviewRequest
                {
                    Reviewer = "reviewer-1",
                    Reason = "duplicate test"
                }));
    }

    [TestMethod]
    public async Task CandidateConstraintReviewHistory_ShouldReturnActivationRecord()
    {
        var constraintStore = new InMemoryConstraintStore();
        var reviewStore = new InMemoryCandidateConstraintReviewStore();
        var service = new CandidateConstraintReviewService(constraintStore, reviewStore);
        await constraintStore.SaveAsync(CreateCandidateConstraint("candidate-constraint-1"));

        await service.ActivateAsync(
            "candidate-constraint-1",
            new CandidateConstraintReviewRequest
            {
                OperationId = "activate-op-1",
                Reviewer = "reviewer-1",
                Reason = "manual activation",
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = "unit-test"
                }
            });

        var reviews = await service.GetReviewsAsync("candidate-constraint-1");
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("activate", reviews[0].Action);
        Assert.AreEqual(ContextMemoryStatus.Active, reviews[0].ToStatus);
        Assert.AreEqual("candidate-constraint-1", reviews[0].ActivatedConstraintId);
        Assert.AreEqual("unit-test", reviews[0].Metadata["source"]);
        CollectionAssert.AreEqual(new[] { "eval:sample-1", "event-1" }, reviews[0].EvidenceRefs.ToArray());
    }

    [TestMethod]
    public async Task ConstraintGapAcceptAndCandidateActivate_ShouldCreateQueryableActiveConstraintWithSourceReviewRefs()
    {
        var constraintStore = new InMemoryConstraintStore();
        var gapStore = new InMemoryConstraintGapCandidateStore();
        var gapService = new ConstraintGapCandidateService(gapStore, constraintStore);
        var candidateReviewService = new CandidateConstraintReviewService(
            constraintStore,
            new InMemoryCandidateConstraintReviewStore());
        var gap = CreatePromotionRuleGap();
        await gapStore.SaveAsync(gap);

        var accepted = await gapService.AcceptAsync(
            gap.GapId,
            new ConstraintGapReviewRequest
            {
                OperationId = "gap-accept-op-1",
                Reviewer = "gap-reviewer",
                Reason = "accept for candidate constraint"
            });
        Assert.IsNotNull(accepted);
        Assert.IsFalse(string.IsNullOrWhiteSpace(accepted.CreatedConstraintId));

        var activated = await candidateReviewService.ActivateAsync(
            accepted.CreatedConstraintId!,
            new CandidateConstraintReviewRequest
            {
                OperationId = "candidate-activate-op-1",
                Reviewer = "constraint-reviewer",
                Reason = "activate hard constraint"
            });
        Assert.IsNotNull(activated);

        var active = await constraintStore.GetAsync(accepted.CreatedConstraintId!);
        Assert.IsNotNull(active);
        Assert.AreEqual(ContextMemoryStatus.Active, active.Status);
        Assert.AreEqual(ConstraintLevel.Hard, active.Level);
        Assert.AreEqual(gap.GapId, active.Metadata["sourceConstraintGapId"]);
        Assert.AreEqual(accepted.ReviewId, active.Metadata["sourceConstraintGapReviewId"]);
        Assert.AreEqual(activated.ReviewId, active.Metadata["sourceCandidateConstraintReviewId"]);
        Assert.AreEqual("chat-20260529-003", active.Metadata["sourceSampleId"]);
        Assert.AreEqual("phase-p15-constraint-activation-closure", active.Metadata["sourceOperationId"]);
        CollectionAssert.Contains(active.SourceRefs.ToArray(), "eval:chat-20260529-003");
        CollectionAssert.Contains(active.SourceRefs.ToArray(), gap.GapId);

        var activeHard = await constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Level = ConstraintLevel.Hard,
            Status = ContextMemoryStatus.Active
        });
        Assert.AreEqual(1, activeHard.Count);
        Assert.AreEqual(accepted.CreatedConstraintId, activeHard[0].Id);
    }

    [TestMethod]
    public async Task PackageBuilder_ShouldInjectActivatedHardConstraintAndPreserveSourceRefs()
    {
        var constraintStore = new InMemoryConstraintStore();
        var gapStore = new InMemoryConstraintGapCandidateStore();
        var gapService = new ConstraintGapCandidateService(gapStore, constraintStore);
        var candidateReviewService = new CandidateConstraintReviewService(
            constraintStore,
            new InMemoryCandidateConstraintReviewStore());
        var gap = CreatePromotionRuleGap();
        await gapStore.SaveAsync(gap);
        var accepted = await gapService.AcceptAsync(
            gap.GapId,
            new ConstraintGapReviewRequest
            {
                Reviewer = "gap-reviewer",
                Reason = "accept for package test"
            });
        Assert.IsNotNull(accepted);
        await candidateReviewService.ActivateAsync(
            accepted.CreatedConstraintId!,
            new CandidateConstraintReviewRequest
            {
                Reviewer = "constraint-reviewer",
                Reason = "activate for package test"
            });
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            QueryText = "总结这轮对话里真正需要下次继续用的结论。",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-1",
                CollectionId = "collection-1",
                TokenBudget = 1_000,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                IncludeGlobalContext = false
            }
        });

        var hardSection = result.Package.Sections.Single(section => section.Name == "hard_constraints");
        StringAssert.Contains(hardSection.Content, PromotionRuleConstraintText);
        CollectionAssert.Contains(hardSection.SourceRefs.ToArray(), "eval:chat-20260529-003");
        CollectionAssert.Contains(hardSection.ItemRefs.ToArray(), accepted.CreatedConstraintId!);

        var selected = result.SelectedItems.Single(item => item.ItemId == accepted.CreatedConstraintId);
        Assert.AreEqual("hard_constraints", selected.SectionName);
        CollectionAssert.Contains(selected.SourceRefs.ToArray(), "eval:chat-20260529-003");
        Assert.AreEqual(gap.GapId, selected.Metadata["sourceConstraintGapId"]);
        Assert.AreEqual(accepted.ReviewId, selected.Metadata["sourceConstraintGapReviewId"]);
    }

    private const string PromotionRuleConstraintText =
        "重复解释、重复澄清、重复说明本身不应被提升为长期偏好或稳定事实；只有用户明确确认其为长期规则时才可提升。";

    private static ContextConstraint CreateCandidateConstraint(string id)
    {
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.User,
            Content = "输出必须使用中文",
            SourceRefs = ["eval:sample-1"],
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.9,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "constraint_gap_accept",
                ["sourceConstraintGapId"] = "gap-1",
                ["sourceSampleId"] = "sample-1",
                ["sourceOperationId"] = "operation-1",
                ["expectedConstraintText"] = "输出必须使用中文",
                ["evidenceRefs"] = "eval:sample-1,event-1",
                ["status"] = "Candidate"
            },
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
    }

    private static ConstraintGapCandidate CreatePromotionRuleGap()
    {
        return new ConstraintGapCandidate
        {
            GapId = "constraint-gap-chat-20260529-003-no-promote-repetition",
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            SessionId = "session-1",
            Source = "eval-constraint-gap-fixture",
            SourceSampleId = "chat-20260529-003",
            SourceOperationId = "phase-p15-constraint-activation-closure",
            ExpectedConstraintText = PromotionRuleConstraintText,
            SuggestedConstraintTitle = "重复说明不得自动提升",
            SuggestedConstraintScope = "Collection",
            SuggestedConstraintType = "Hard",
            Severity = ConstraintGapSeverity.High,
            Reason = "missing hard constraint",
            EvidenceRefs = ["eval:chat-20260529-003", "phase:p15"],
            Status = ConstraintGapStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fixturePhase"] = "P15"
            }
        };
    }

    private static ContextConstraint CreateActiveConstraint(string id)
    {
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-1",
            CollectionId = "collection-1",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Hard,
            Content = "输出必须使用中文",
            Status = ContextMemoryStatus.Active,
            Confidence = 1.0,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["createdFrom"] = "manual"
            },
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
        };
    }
}
