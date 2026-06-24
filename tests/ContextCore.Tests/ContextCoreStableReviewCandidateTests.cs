using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Promotion;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreStableReviewCandidateTests
{
    [TestMethod]
    public async Task AcceptedCandidateMemory_ShouldGenerateStableReviewCandidate()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-stable-1", "RecentDecision", "保留稳定评审入口", "recorded", 0.92, ["event-stable-1"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();

        var accepted = await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-accept-1", "接受为候选记忆。"));
        var generated = await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest());

        Assert.AreEqual(1, generated.Count);
        Assert.AreEqual("StableMemory", generated[0].SuggestedStableTarget);
        Assert.AreEqual(StableReviewValidationStatuses.Ready, generated[0].ValidationStatus);
        Assert.AreEqual(StableReviewCandidateStatuses.Candidate, generated[0].Status);
        Assert.AreEqual(sourceCandidate.CandidateId, generated[0].SourceCandidateId);
        Assert.AreEqual(accepted!.CreatedTargetItemId, generated[0].SourceTargetItemId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(generated[0].SourceLearningCaseId));
        CollectionAssert.Contains(generated[0].EvidenceRefs.ToArray(), "event-stable-1");
    }

    [TestMethod]
    public async Task RejectedPromotion_ShouldNotGenerateStableReviewCandidate()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-rejected-1", "RecentDecision", "不进入稳定评审", "recorded", 0.90, ["event-rejected-1"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.RejectAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-reject-1", "拒绝。"));

        var generated = await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest());

        Assert.AreEqual(0, generated.Count);
    }

    [TestMethod]
    public async Task AcceptedCandidateWithoutEvidence_ShouldBecomeNeedsMoreEvidence()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate("candidate-no-evidence", "target-no-evidence", evidenceRefs: []);
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemoryTarget(candidate, "target-no-evidence"));

        var generated = await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest());

        Assert.AreEqual(1, generated.Count);
        Assert.AreEqual(StableReviewValidationStatuses.NeedsMoreEvidence, generated[0].ValidationStatus);
        Assert.AreEqual(StableReviewCandidateStatuses.NeedsMoreEvidence, generated[0].Status);
        CollectionAssert.Contains(generated[0].RiskFlags.ToArray(), "missing_evidence");
    }

    [TestMethod]
    public async Task DuplicateStableCandidate_ShouldBeDetected()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate("candidate-duplicate", "target-duplicate", evidenceRefs: ["event-duplicate"]);
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemoryTarget(candidate, "target-duplicate"));
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-duplicate",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "recent_decision",
            Content = "已存在稳定记忆",
            SourceRefs = [candidate.CandidateId, "target-duplicate"],
            Confidence = 0.9,
            Importance = 0.9,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var generated = await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest());

        Assert.AreEqual(1, generated.Count);
        Assert.AreEqual(StableReviewValidationStatuses.DuplicateStableCandidate, generated[0].ValidationStatus);
        Assert.AreEqual(StableReviewCandidateStatuses.Blocked, generated[0].Status);
        CollectionAssert.Contains(generated[0].RiskFlags.ToArray(), "duplicate_stable");
    }

    [TestMethod]
    public async Task SourceTargetScopeMismatch_ShouldBeDetected()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate("candidate-scope", "target-scope", evidenceRefs: ["event-scope"]);
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "target-scope",
            WorkspaceId = "workspace-test",
            CollectionId = "other-collection",
            Layer = ContextMemoryLayer.Structured,
            Status = ContextMemoryStatus.Candidate,
            Type = "recent_decision",
            Content = "跨集合目标",
            SourceRefs = [candidate.CandidateId],
            Confidence = 0.9,
            Importance = 0.9,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var generated = await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest());

        Assert.AreEqual(1, generated.Count);
        Assert.AreEqual(StableReviewValidationStatuses.ScopeMismatch, generated[0].ValidationStatus);
        Assert.AreEqual(StableReviewCandidateStatuses.Blocked, generated[0].Status);
        CollectionAssert.Contains(generated[0].RiskFlags.ToArray(), "scope_mismatch");
    }

    [TestMethod]
    public async Task Explain_ShouldReturnSourceCandidate_LearningCase_AndEvidence()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-explain-stable", "RecentDecision", "解释稳定评审候选", "recorded", 0.93, ["event-explain-stable"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-explain-accept", "接受。"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        var explanation = await fixture.StableReviewService.ExplainAsync(stableCandidate.StableReviewCandidateId);

        Assert.IsNotNull(explanation);
        Assert.AreEqual(stableCandidate.StableReviewCandidateId, explanation!.StableReviewCandidateId);
        Assert.AreEqual(sourceCandidate.CandidateId, explanation.SourceCandidate.CandidateId);
        Assert.IsNotNull(explanation.SourceLearningCase);
        Assert.IsNotNull(explanation.SourceMemoryTarget);
        CollectionAssert.Contains(explanation.EvidenceRefs.ToArray(), "event-explain-stable");
    }

    [TestMethod]
    public async Task AcceptStableReviewCandidate_ShouldCreateStableMemory()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-accept-stable", "RecentDecision", "写入稳定记忆", "recorded", 0.94, ["event-accept-stable"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-review-source-accept", "接受为候选层。"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        var result = await fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-accept-final", "确认进入稳定记忆。"));

        Assert.IsNotNull(result);
        Assert.AreEqual(StableReviewCandidateStatuses.Accepted, result!.Status);
        Assert.AreEqual("StableMemory", result.TargetLayer);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.CreatedStableTargetItemId));

        var stableItems = await fixture.MemoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 10
        });
        var stableItem = stableItems.Single();
        Assert.AreEqual(result.CreatedStableTargetItemId, stableItem.Id);
        Assert.AreEqual(ContextMemoryLayer.Stable, stableItem.Layer);
        Assert.AreEqual(ContextMemoryStatus.Stable, stableItem.Status);
        CollectionAssert.Contains(stableItem.SourceRefs.ToArray(), stableCandidate.StableReviewCandidateId);
        CollectionAssert.Contains(stableItem.SourceRefs.ToArray(), sourceCandidate.CandidateId);
        CollectionAssert.Contains(stableItem.SourceRefs.ToArray(), sourceCandidate.SourceWorkingItemId);
        CollectionAssert.Contains(stableItem.SourceRefs.ToArray(), "event-accept-stable");
    }

    [TestMethod]
    public async Task RejectStableReviewCandidate_ShouldRecordReviewWithoutStableWrite()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-reject-stable", "RecentDecision", "拒绝稳定记忆", "recorded", 0.91, ["event-reject-stable"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-review-source-reject", "接受为候选层。"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        var result = await fixture.StableReviewService.RejectAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-reject-final", "不进入稳定层。"));

        Assert.IsNotNull(result);
        Assert.AreEqual(StableReviewCandidateStatuses.Rejected, result!.Status);
        var reviews = await fixture.StableReviewService.GetReviewsAsync(stableCandidate.StableReviewCandidateId);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual("reject", reviews[0].Action);
        Assert.AreEqual(StableReviewCandidateStatuses.Rejected, reviews[0].ToStatus);

        var stableItems = await fixture.MemoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 10
        });
        Assert.AreEqual(0, stableItems.Count);
    }

    [TestMethod]
    public async Task InvalidStableReviewCandidate_ShouldNotBeAccepted()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate("candidate-invalid-accept", "target-invalid-accept", evidenceRefs: []);
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemoryTarget(candidate, "target-invalid-accept"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-invalid-accept", "证据不足也接受。")));

        var unchanged = await fixture.StableReviewService.GetAsync(stableCandidate.StableReviewCandidateId);
        Assert.AreEqual(StableReviewCandidateStatuses.NeedsMoreEvidence, unchanged!.Status);
    }

    [TestMethod]
    public async Task DuplicateStableAtAcceptTime_ShouldBeRejectedByValidation()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate("candidate-duplicate-accept", "target-duplicate-accept", evidenceRefs: ["event-duplicate-accept"]);
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemoryTarget(candidate, "target-duplicate-accept"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-duplicate-accept",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "recent_decision",
            Content = "已存在稳定记忆",
            SourceRefs = [candidate.CandidateId, "target-duplicate-accept"],
            Confidence = 0.9,
            Importance = 0.9,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var exception = await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-duplicate-accept", "重复接受。")));

        StringAssert.Contains(exception.Message, StableReviewValidationStatuses.DuplicateStableCandidate);
    }

    [TestMethod]
    public async Task AcceptedStableItem_ShouldPreserveSourceRefsAndReviewMetadata()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-stable-refs", "RecentDecision", "保留来源链", "recorded", 0.95, ["event-stable-refs"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-review-source-refs", "接受为候选层。"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        var result = await fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-refs-accept", "来源链完整。"));
        var stableItem = (await fixture.MemoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Take = 10
        })).Single(item => item.Id == result!.CreatedStableTargetItemId);

        Assert.AreEqual(stableCandidate.StableReviewCandidateId, stableItem.Metadata["sourceStableReviewCandidateId"]);
        Assert.AreEqual(sourceCandidate.CandidateId, stableItem.Metadata["sourcePromotionCandidateId"]);
        Assert.AreEqual(stableCandidate.SourceLearningCaseId, stableItem.Metadata["sourceLearningCaseId"]);
        Assert.AreEqual(sourceCandidate.SourceWorkingItemId, stableItem.Metadata["sourceWorkingItemId"]);
        Assert.AreEqual("tester", stableItem.Metadata["reviewer"]);
        Assert.AreEqual("来源链完整。", stableItem.Metadata["reviewReason"]);
        Assert.AreEqual("stable-review-readiness-policy/v1", stableItem.Metadata["policyVersion"]);
        Assert.AreEqual("stable_review_accept", stableItem.Metadata["createdFrom"]);
        Assert.IsTrue(stableItem.Metadata.ContainsKey("sourceFeedbackId"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(stableItem.Metadata["sourceFeedbackId"]));
        StringAssert.Contains(stableItem.Metadata["evidenceRefs"], "event-stable-refs");
    }

    [TestMethod]
    public async Task Provenance_ShouldReturnFullStableSourceChain()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-provenance", "RecentDecision", "稳定来源链", "recorded", 0.96, ["event-provenance"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-review-source-provenance", "接受为候选层。"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();
        var result = await fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-provenance-accept", "来源链进入稳定层。"));

        var provenance = await fixture.ProvenanceService.GetAsync(result!.CreatedStableTargetItemId!, "workspace-test", "collection-test");

        Assert.IsNotNull(provenance);
        Assert.IsNotNull(provenance!.TargetMemoryItem);
        Assert.AreEqual(result.CreatedStableTargetItemId, provenance.TargetMemoryItem!.Id);
        Assert.AreEqual(stableCandidate.StableReviewCandidateId, provenance.StableReviewCandidate!.StableReviewCandidateId);
        Assert.AreEqual(sourceCandidate.CandidateId, provenance.PromotionCandidate!.CandidateId);
        Assert.IsNotNull(provenance.FeedbackSignal);
        Assert.AreEqual(provenance.FeedbackSignal!.FeedbackId, provenance.TargetMemoryItem.Metadata["sourceFeedbackId"]);
        Assert.IsNotNull(provenance.LearningCase);
        Assert.IsNotNull(provenance.SourceWorkingItem);
        Assert.IsTrue(provenance.StableReviewHistory.Any(item => item.Action == "accept"));
        Assert.IsTrue(provenance.PromotionReviewHistory.Any(item => item.Action == "accept"));
        CollectionAssert.Contains(provenance.EvidenceRefs.ToArray(), "event-provenance");
        Assert.AreEqual("stable_review_accept", provenance.TargetMemoryItem.Metadata["createdFrom"]);
    }

    [TestMethod]
    public async Task Provenance_ShouldReportStableDiagnostics()
    {
        var fixture = CreateFixture();
        var promotionCandidate = CreateAcceptedCandidate("candidate-diagnostics", "target-diagnostics", ["event-diagnostics"]);
        await fixture.PromotionStore.SaveAsync(promotionCandidate);
        var stableCandidate = new StableReviewCandidate
        {
            StableReviewCandidateId = "src-diagnostics",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            SourceCandidateId = promotionCandidate.CandidateId,
            SourceTargetItemId = "target-diagnostics",
            SourceLearningCaseId = "missing-learning-case",
            Kind = promotionCandidate.Kind,
            Title = "诊断来源链",
            Summary = "诊断来源链",
            SuggestedStableTarget = "StableMemory",
            Reason = "diagnostics",
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = ["event-diagnostics"],
            RiskFlags = ["lifecycle_conflict"],
            ValidationStatus = StableReviewValidationStatuses.ReadyForReview,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = StableReviewCandidateStatuses.Accepted,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceWorkingItemId"] = "missing-working-item",
                ["sourceFeedbackId"] = "missing-feedback"
            }
        };
        await fixture.StableReviewStore.SaveAsync(stableCandidate);
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable:mem:src-diagnostics",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "recent_decision",
            Content = "诊断目标",
            SourceRefs = [stableCandidate.StableReviewCandidateId, promotionCandidate.CandidateId, stableCandidate.SourceTargetItemId],
            Confidence = 0.9,
            Importance = 0.9,
            Version = 1,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceStableReviewCandidateId"] = stableCandidate.StableReviewCandidateId,
                ["sourcePromotionCandidateId"] = promotionCandidate.CandidateId,
                ["sourceLearningCaseId"] = stableCandidate.SourceLearningCaseId,
                ["sourceWorkingItemId"] = "missing-working-item",
                ["sourceFeedbackId"] = "missing-feedback",
                ["possibleConflict"] = "true",
                ["createdFrom"] = "stable_review_accept"
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await fixture.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "stable-duplicate-diagnostics",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "recent_decision",
            Content = "重复稳定目标",
            SourceRefs = [promotionCandidate.CandidateId, stableCandidate.SourceTargetItemId],
            Confidence = 0.9,
            Importance = 0.9,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var provenance = await fixture.ProvenanceService.GetAsync("stable:mem:src-diagnostics", "workspace-test", "collection-test");

        Assert.IsNotNull(provenance);
        var codes = provenance!.Diagnostics.Select(item => item.Code).ToArray();
        CollectionAssert.Contains(codes, "DuplicateStable");
        CollectionAssert.Contains(codes, "PossibleConflict");
        CollectionAssert.Contains(codes, "MissingSourceLink");
        Assert.IsTrue(provenance.MissingLinks.Any(item => item.Contains("feedbackSignal:missing-feedback", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(provenance.MissingLinks.Any(item => item.Contains("sourceWorkingItem:missing-working-item", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task AcceptedStableConstraint_ShouldBeInjectedIntoPackage()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate(
            "candidate-stable-constraint",
            "target-stable-constraint",
            ["event-stable-constraint"],
            suggestedTargetLayer: "ConstraintCandidate",
            targetKind: "constraint",
            targetLayer: "ConstraintCandidate");
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.ConstraintStore.SaveAsync(new ContextConstraint
        {
            Id = "target-stable-constraint",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = ConstraintLevel.Hard,
            Content = "必须保留 stable provenance refs",
            SourceRefs = [candidate.CandidateId, "event-stable-constraint"],
            Status = ContextMemoryStatus.Candidate,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        var result = await fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-constraint-accept", "进入稳定约束。"));

        Assert.AreEqual("StableConstraint", result!.TargetLayer);
        var contextStore = new InMemoryContextStore();
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "recent-stable-constraint",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "note",
            Title = "约束注入",
            Content = "验证稳定约束注入。",
            Importance = 0.8,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var builder = new BasicContextPackageBuilder(
            contextStore,
            fixture.ConstraintStore,
            globalContextStore: null,
            memoryStore: fixture.MemoryStore,
            relationStore: null);

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "约束注入",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true
            }
        });

        var hardConstraints = package.Sections.Single(section => section.Name == "hard_constraints");
        StringAssert.Contains(hardConstraints.Content, "必须保留 stable provenance refs");
        CollectionAssert.Contains(hardConstraints.SourceRefs.ToArray(), stableCandidate.StableReviewCandidateId);
    }

    [TestMethod]
    public async Task AcceptedDecisionRecord_ShouldBeRetrievable()
    {
        var fixture = CreateFixture();
        var candidate = CreateAcceptedCandidate(
            "candidate-decision-record",
            "target-decision-record",
            ["event-decision-record"],
            suggestedTargetLayer: "DecisionRecord",
            targetLayer: "DecisionRecord");
        await fixture.PromotionStore.SaveAsync(candidate);
        await fixture.MemoryStore.SaveAsync(CreateCandidateMemoryTarget(candidate, "target-decision-record"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();

        var result = await fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-decision-accept", "进入决策记录。"));
        Assert.IsNotNull(result);
        var stableItems = await fixture.MemoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Types = ["decision"],
            Take = 20
        });

        var decision = stableItems.Single(item => item.Id == result!.CreatedStableTargetItemId);
        StringAssert.StartsWith(decision.Id, "stable:decision:");
        Assert.AreEqual("decision", decision.Type);
        Assert.AreEqual("DecisionRecord", result!.TargetLayer);
    }

    [TestMethod]
    public async Task PackageTrace_ShouldPreserveStableProvenanceRefs()
    {
        var fixture = CreateFixture();
        await fixture.ShortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-package-provenance", "RecentDecision", "包追踪保留稳定来源链", "recorded", 0.96, ["event-package-provenance"]));
        var sourceCandidate = (await fixture.PromotionService.GenerateAsync(CreatePromotionGenerationRequest())).Single();
        await fixture.PromotionService.AcceptAsync(sourceCandidate.CandidateId, CreateReviewRequest("stable-review-source-package", "接受为候选层。"));
        var stableCandidate = (await fixture.StableReviewService.GenerateAsync(CreateStableGenerationRequest())).Single();
        var accept = await fixture.StableReviewService.AcceptAsync(stableCandidate.StableReviewCandidateId, CreateStableDecisionRequest("stable-package-accept", "进入稳定包追踪。"));
        var contextStore = new InMemoryContextStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore: fixture.MemoryStore,
            relationStore: null);

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "包追踪 稳定 来源链",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = true,
                IncludeRecentRawContext = false,
                MaxRecentItems = 5
            }
        });

        var decision = result.SelectedItems.Single(item => item.ItemId == accept!.CreatedStableTargetItemId);
        Assert.AreEqual(stableCandidate.StableReviewCandidateId, decision.Metadata["sourceStableReviewCandidateId"]);
        Assert.AreEqual("stable_review_accept", decision.Metadata["createdFrom"]);
        Assert.IsFalse(string.IsNullOrWhiteSpace(decision.Metadata["sourceFeedbackId"]));
        CollectionAssert.Contains(decision.SourceRefs.ToArray(), stableCandidate.StableReviewCandidateId);
        CollectionAssert.Contains(decision.SourceRefs.ToArray(), decision.Metadata["sourceFeedbackId"]);
    }

    private static Fixture CreateFixture()
    {
        var shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        var promotionStore = new InMemoryShortTermPromotionCandidateStore();
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var relationStore = new InMemoryRelationStore();
        var learningStore = new InMemoryContextLearningStore();
        var stableReviewStore = new InMemoryStableReviewCandidateStore();
        var promotionService = new ShortTermPromotionCandidateService(
            shortTermStore,
            promotionStore,
            memoryStore,
            constraintStore,
            relationStore,
            learningStore,
            new RuleBasedContextLearningCaseGenerator());
        var stableReviewService = new StableReviewCandidateService(
            promotionStore,
            stableReviewStore,
            memoryStore,
            constraintStore,
            learningStore);
        var provenanceService = new ContextProvenanceService(
            memoryStore,
            constraintStore,
            stableReviewStore,
            promotionStore,
            learningStore,
            shortTermStore);

        return new Fixture(
            shortTermStore,
            promotionStore,
            stableReviewStore,
            memoryStore,
            constraintStore,
            learningStore,
            promotionService,
            stableReviewService,
            provenanceService);
    }

    private static ShortTermPromotionCandidateGenerationRequest CreatePromotionGenerationRequest()
    {
        return new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        };
    }

    private static StableReviewCandidateGenerationRequest CreateStableGenerationRequest()
    {
        return new StableReviewCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Limit = 20
        };
    }

    private static ReviewPromotionCandidateRequest CreateReviewRequest(string operationId, string reason)
    {
        return new ReviewPromotionCandidateRequest
        {
            OperationId = operationId,
            Reviewer = "tester",
            Reason = reason
        };
    }

    private static StableReviewDecisionRequest CreateStableDecisionRequest(string operationId, string reason)
    {
        return new StableReviewDecisionRequest
        {
            OperationId = operationId,
            Reviewer = "tester",
            Reason = reason
        };
    }

    private static ShortTermPromotionCandidate CreateAcceptedCandidate(
        string candidateId,
        string targetId,
        IReadOnlyList<string> evidenceRefs,
        string suggestedTargetLayer = "CandidateMemory",
        string targetKind = "memory",
        string targetLayer = "CandidateMemory")
    {
        return new ShortTermPromotionCandidate
        {
            CandidateId = candidateId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            SourceWorkingItemId = $"working-{candidateId}",
            Kind = "RecentDecision",
            Title = "手工已接受候选",
            Summary = "手工已接受候选",
            SuggestedTargetLayer = suggestedTargetLayer,
            Reason = "accepted",
            Confidence = 0.9,
            Importance = 0.9,
            EvidenceRefs = evidenceRefs.ToArray(),
            CreatedAt = DateTimeOffset.UtcNow,
            Status = PromotionCandidateStatus.Accepted,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["acceptedTargetItemId"] = targetId,
                ["acceptedTargetItemKind"] = targetKind,
                ["acceptedTargetLayer"] = targetLayer
            }
        };
    }

    private static ContextMemoryItem CreateCandidateMemoryTarget(
        ShortTermPromotionCandidate candidate,
        string targetId)
    {
        return new ContextMemoryItem
        {
            Id = targetId,
            WorkspaceId = candidate.WorkspaceId,
            CollectionId = candidate.CollectionId,
            Layer = ContextMemoryLayer.Structured,
            Status = ContextMemoryStatus.Candidate,
            Type = "recent_decision",
            Content = candidate.Summary,
            SourceRefs = [candidate.CandidateId, .. candidate.EvidenceRefs],
            Confidence = candidate.Confidence,
            Importance = candidate.Importance,
            Version = 1,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceCandidateId"] = candidate.CandidateId
            },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ShortTermWorkingItem CreateWorkingItem(
        string itemId,
        string kind,
        string summary,
        string status,
        double importance,
        IReadOnlyList<string>? refs = null)
    {
        return new ShortTermWorkingItem
        {
            ItemId = itemId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Kind = kind,
            Title = summary,
            Summary = summary,
            Status = status,
            Lifecycle = "Recent",
            Importance = importance,
            Tags = [kind],
            Refs = refs ?? Array.Empty<string>(),
            SourceRefs = [$"source:{itemId}"],
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed record Fixture(
        InMemoryShortTermMemoryStore ShortTermStore,
        InMemoryShortTermPromotionCandidateStore PromotionStore,
        InMemoryStableReviewCandidateStore StableReviewStore,
        InMemoryMemoryStore MemoryStore,
        InMemoryConstraintStore ConstraintStore,
        InMemoryContextLearningStore LearningStore,
        ShortTermPromotionCandidateService PromotionService,
        StableReviewCandidateService StableReviewService,
        ContextProvenanceService ProvenanceService);
}
