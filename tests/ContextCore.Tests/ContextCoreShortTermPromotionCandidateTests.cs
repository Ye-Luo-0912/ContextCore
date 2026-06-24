using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Promotion;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreShortTermPromotionCandidateTests
{
    [TestMethod]
    public async Task RecentDecision_ShouldGenerateCandidate()
    {
        var service = CreateService(out var shortTermStore, out var candidateStore);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-1", "RecentDecision", "记录最终 API 契约", "recorded", 0.85, refs: ["event-1"]));

        var candidates = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("CandidateMemory", candidates[0].SuggestedTargetLayer);
        Assert.AreEqual("RecentDecision", candidates[0].Kind);
        Assert.AreEqual(1, (await candidateStore.QueryAsync(new ShortTermPromotionCandidateQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Limit = 10
        })).Count);
    }

    [TestMethod]
    public async Task KnownIssue_ShouldGenerateCandidate()
    {
        var service = CreateService(out var shortTermStore, out _);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("issue-1", "KnownIssue", "数据库连接抖动", "open", 0.80, refs: ["event-2"]));

        var candidates = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("CandidateMemory", candidates[0].SuggestedTargetLayer);
        StringAssert.Contains(candidates[0].Reason, "KnownIssue");
    }

    [TestMethod]
    public async Task TemporaryConstraint_ShouldGenerateConstraintCandidate()
    {
        var service = CreateService(out var shortTermStore, out _);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("constraint-1", "TemporaryConstraint", "不得覆盖用户原文", "active", 0.90, refs: ["event-3"]));

        var candidates = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("ConstraintCandidate", candidates[0].SuggestedTargetLayer);
    }

    [TestMethod]
    public async Task LowImportanceItem_ShouldNotGenerateCandidate()
    {
        var service = CreateService(out var shortTermStore, out _);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("question-1", "OpenQuestion", "是否需要补监控？", "open", 0.45, refs: ["event-4"]));

        var candidates = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(0, candidates.Count);
    }

    [TestMethod]
    public async Task DuplicateCandidate_ShouldNotBeRecreated()
    {
        var service = CreateService(out var shortTermStore, out var candidateStore);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-dup", "RecentDecision", "确定只保留显式 DTO", "recorded", 0.90, refs: ["event-5"]));

        var first = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
        var second = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
        var stored = await candidateStore.QueryAsync(new ShortTermPromotionCandidateQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1",
            Limit = 10
        });

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(1, second.Count);
        Assert.AreEqual(first[0].CandidateId, second[0].CandidateId);
        Assert.AreEqual(1, stored.Count);
    }

    [TestMethod]
    public async Task Candidate_ShouldIncludeEvidenceRefs_AndReason()
    {
        var service = CreateService(out var shortTermStore, out _);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("task-1", "ActiveTask", "修复 ready 探针", "resolved", 0.95, refs: ["event-6", "event-7"]));

        var candidates = await service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });

        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("DecisionRecord", candidates[0].SuggestedTargetLayer);
        Assert.IsTrue(candidates[0].EvidenceRefs.Count >= 2);
        Assert.IsFalse(string.IsNullOrWhiteSpace(candidates[0].Reason));
    }

    [TestMethod]
    public async Task AcceptCandidateMemory_ShouldCreateCandidateMemoryItem_WithEvidenceMetadata()
    {
        var service = CreateService(
            out var shortTermStore,
            out _,
            out var memoryStore,
            out _,
            out var relationStore);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-accept", "RecentDecision", "统一 Service 错误响应", "recorded", 0.92, refs: ["event-accept"]));
        var candidate = (await GenerateDefaultAsync(service)).Single();

        var result = await service.AcceptAsync(candidate.CandidateId, CreateReviewRequest("accept-op-1", "接受为候选记忆。"));
        var target = await memoryStore.GetAsync("workspace-test", "collection-test", result!.TargetItemId!);
        var relations = await relationStore.QueryForItemAsync("workspace-test", "collection-test", target!.Id);

        Assert.AreEqual(PromotionCandidateStatus.Accepted, result.Status);
        Assert.AreEqual("memory", result.TargetItemKind);
        Assert.AreEqual(result.TargetItemId, result.CreatedTargetItemId);
        Assert.AreEqual("tester", result.Reviewer);
        Assert.AreEqual("接受为候选记忆。", result.Reason);
        Assert.AreNotEqual(default, result.ReviewedAt);
        Assert.AreEqual(ContextMemoryLayer.Structured, target.Layer);
        Assert.AreEqual(ContextMemoryStatus.Candidate, target.Status);
        Assert.AreNotEqual(ContextMemoryLayer.Stable, target.Layer);
        Assert.AreEqual("decision", target.Type);
        Assert.AreEqual(candidate.CandidateId, target.Metadata["sourceCandidateId"]);
        Assert.AreEqual(candidate.SourceWorkingItemId, target.Metadata["sourceWorkingItemId"]);
        StringAssert.Contains(target.Metadata["evidenceRefs"], "event-accept");
        Assert.IsTrue(relations.Any(item => item.RelationType == ContextRelationTypes.PromotedFrom));
        Assert.IsTrue(relations.Any(item => item.RelationType == ContextRelationTypes.EvidenceFor));
    }

    [TestMethod]
    public async Task AcceptConstraintCandidate_ShouldCreateConstraintCandidate()
    {
        var service = CreateService(
            out var shortTermStore,
            out _,
            out _,
            out var constraintStore,
            out _);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("constraint-accept", "TemporaryConstraint", "输出必须保持中文", "active", 0.90, refs: ["event-constraint"]));
        var candidate = (await GenerateDefaultAsync(service)).Single();

        var result = await service.AcceptAsync(candidate.CandidateId, CreateReviewRequest("constraint-op-1", "接受为约束候选。"));
        var constraints = await constraintStore.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Status = ContextMemoryStatus.Candidate,
            Take = 10
        });

        Assert.AreEqual(PromotionCandidateStatus.Accepted, result!.Status);
        Assert.AreEqual("constraint", result.TargetItemKind);
        Assert.AreEqual(1, constraints.Count);
        Assert.AreEqual(result.TargetItemId, constraints[0].Id);
        Assert.AreEqual(candidate.CandidateId, constraints[0].Metadata["sourceCandidateId"]);
        CollectionAssert.Contains(constraints[0].SourceRefs.ToArray(), "event-constraint");
    }

    [TestMethod]
    public async Task RejectAndExpire_ShouldUpdateStatus_AndRecordReviews()
    {
        var rejectService = CreateService(out var rejectStore, out var rejectCandidateStore, out _, out _, out _);
        await rejectStore.SaveWorkingItemAsync(CreateWorkingItem("issue-reject", "KnownIssue", "缓存击穿", "open", 0.88, refs: ["event-reject"]));
        var rejectCandidate = (await GenerateDefaultAsync(rejectService)).Single();

        var reject = await rejectService.RejectAsync(rejectCandidate.CandidateId, CreateReviewRequest("reject-op-1", "暂不保留。"));
        var rejectReviews = await rejectCandidateStore.QueryReviewsAsync(rejectCandidate.CandidateId);

        var expireService = CreateService(out var expireStore, out var expireCandidateStore, out _, out _, out _);
        await expireStore.SaveWorkingItemAsync(CreateWorkingItem("question-expire", "OpenQuestion", "是否保留旧接口？", "open", 0.86, refs: ["event-expire"]));
        var expireCandidate = (await GenerateDefaultAsync(expireService)).Single();

        var expire = await expireService.ExpireAsync(expireCandidate.CandidateId, CreateReviewRequest("expire-op-1", "问题已过期。"));
        var expireReviews = await expireCandidateStore.QueryReviewsAsync(expireCandidate.CandidateId);

        Assert.AreEqual(PromotionCandidateStatus.Rejected, reject!.Status);
        Assert.AreEqual(1, rejectReviews.Count);
        Assert.AreEqual("reject", rejectReviews[0].Action);
        Assert.AreEqual("tester", rejectReviews[0].Reviewer);
        Assert.AreEqual("暂不保留。", rejectReviews[0].Reason);
        Assert.AreNotEqual(default, rejectReviews[0].ReviewedAt);
        Assert.AreEqual(PromotionCandidateStatus.Expired, expire!.Status);
        Assert.AreEqual(1, expireReviews.Count);
        Assert.AreEqual("expire", expireReviews[0].Action);
    }

    [TestMethod]
    public async Task AcceptedCandidate_ShouldNotBeAcceptedAgain()
    {
        var service = CreateService(out var shortTermStore, out _, out _, out _, out _);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-repeat", "RecentDecision", "保留短期审核历史", "recorded", 0.90, refs: ["event-repeat"]));
        var candidate = (await GenerateDefaultAsync(service)).Single();

        await service.AcceptAsync(candidate.CandidateId, CreateReviewRequest("accept-repeat-1", "首次接受。"));

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            service.AcceptAsync(candidate.CandidateId, CreateReviewRequest("accept-repeat-2", "重复接受。")));
    }

    [TestMethod]
    public async Task AcceptPromotion_ShouldCreatePositiveLearningRecord_AndCase()
    {
        var service = CreateServiceWithLearning(
            out var shortTermStore,
            out _,
            out _,
            out _,
            out _,
            out var learningStore);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("decision-learning", "RecentDecision", "保留学习记录查询接口", "recorded", 0.91, refs: ["event-learning"]));
        var candidate = (await GenerateDefaultAsync(service)).Single();

        var review = await service.AcceptAsync(candidate.CandidateId, CreateReviewRequest("learning-accept-op", "作为正样本保留。"));
        var records = await learningStore.QueryRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = candidate.CandidateId,
            Limit = 10
        });
        var cases = await learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceRecordId = records.Single().RecordId,
            Limit = 10
        });
        var feedback = await learningStore.QueryFeedbackAsync(new PromotionFeedbackSignalQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            CandidateId = candidate.CandidateId,
            Action = "Accepted",
            Limit = 10
        });

        Assert.AreEqual(1, feedback.Count);
        Assert.AreEqual("Accepted", feedback[0].Action);
        Assert.AreEqual(candidate.CandidateId, feedback[0].CandidateId);
        Assert.AreEqual(candidate.SourceWorkingItemId, feedback[0].SourceWorkingItemId);
        Assert.AreEqual(review!.CreatedTargetItemId, feedback[0].CreatedTargetItemId);
        Assert.AreEqual(candidate.SuggestedTargetLayer, feedback[0].SuggestedTargetLayer);
        CollectionAssert.Contains(feedback[0].EvidenceRefs.ToArray(), "event-learning");
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("PromotionAccepted", records[0].EventKind);
        Assert.AreEqual(ContextFeedbackSignal.Positive, records[0].Signal);
        Assert.AreEqual(ContextFailureType.None, records[0].FailureType);
        Assert.AreEqual(candidate.CandidateId, records[0].CandidateId);
        Assert.AreEqual(review.ReviewId, records[0].ReviewId);
        Assert.AreEqual(candidate.SourceWorkingItemId, records[0].Metadata["sourceWorkingItemId"]);
        Assert.AreEqual("learning-accept-op", records[0].Metadata["operationId"]);
        CollectionAssert.Contains(records[0].EvidenceRefs.ToArray(), "event-learning");
        Assert.AreEqual(1, cases.Count);
        Assert.AreEqual("PositivePromotionSample", cases[0].CaseKind);
        Assert.AreEqual(ContextFeedbackSignal.Positive, cases[0].Signal);
        Assert.AreEqual(ContextLearningCaseStatus.Draft, cases[0].Status);
        Assert.AreEqual("PromotionFeedbackSignal", cases[0].SourceType);
        Assert.AreEqual(feedback[0].FeedbackId, cases[0].SourceId);
        Assert.AreEqual(candidate.Summary, cases[0].InputSummary);
        Assert.AreEqual("作为正样本保留。", cases[0].CorrectionReason);
        CollectionAssert.Contains(cases[0].PositiveRefs.ToArray(), "event-learning");
    }

    [TestMethod]
    public async Task RejectPromotion_ShouldCreateNegativeLearningRecord_AndFalsePositiveCase()
    {
        var service = CreateServiceWithLearning(
            out var shortTermStore,
            out _,
            out _,
            out _,
            out _,
            out var learningStore);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("issue-learning", "KnownIssue", "临时噪声不应晋升", "open", 0.88, refs: ["event-reject-learning"]));
        var candidate = (await GenerateDefaultAsync(service)).Single();

        await service.RejectAsync(candidate.CandidateId, CreateReviewRequest("learning-reject-op", "误报候选。"));
        var records = await learningStore.QueryRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Signal = ContextFeedbackSignal.Negative,
            Limit = 10
        });
        var cases = await learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            FailureType = ContextFailureType.PromotionFalsePositive,
            Limit = 10
        });
        var feedback = await learningStore.QueryFeedbackAsync(new PromotionFeedbackSignalQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            CandidateId = candidate.CandidateId,
            Action = "Rejected",
            Limit = 10
        });

        Assert.AreEqual(1, feedback.Count);
        Assert.AreEqual("Rejected", feedback[0].Action);
        Assert.AreEqual(candidate.CandidateId, feedback[0].CandidateId);
        Assert.IsNull(feedback[0].CreatedTargetItemId);
        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("PromotionRejected", records[0].EventKind);
        Assert.AreEqual(ContextFailureType.PromotionFalsePositive, records[0].FailureType);
        Assert.AreEqual(candidate.CandidateId, records[0].Metadata["sourceCandidateId"]);
        Assert.AreEqual("learning-reject-op", records[0].Metadata["operationId"]);
        Assert.AreEqual(1, cases.Count);
        Assert.AreEqual("PromotionFalsePositive", cases[0].CaseKind);
        Assert.AreEqual(records[0].RecordId, cases[0].SourceRecordId);
        Assert.AreEqual(ContextLearningCaseStatus.Draft, cases[0].Status);
        Assert.AreEqual("PromotionFeedbackSignal", cases[0].SourceType);
        Assert.AreEqual(feedback[0].FeedbackId, cases[0].SourceId);
        CollectionAssert.Contains(cases[0].NegativeRefs.ToArray(), "event-reject-learning");
    }

    [TestMethod]
    public async Task ExpirePromotion_ShouldCreateStaleLearningRecord()
    {
        var service = CreateServiceWithLearning(
            out var shortTermStore,
            out _,
            out _,
            out _,
            out _,
            out var learningStore);
        await shortTermStore.SaveWorkingItemAsync(CreateWorkingItem("question-learning", "OpenQuestion", "过期的问题", "open", 0.82, refs: ["event-expire-learning"]));
        var candidate = (await GenerateDefaultAsync(service)).Single();

        await service.ExpireAsync(candidate.CandidateId, CreateReviewRequest("learning-expire-op", "已过期。"));
        var records = await learningStore.QueryRecordsAsync(new ContextLearningRecordQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Signal = ContextFeedbackSignal.Stale,
            Limit = 10
        });
        var cases = await learningStore.QueryCasesAsync(new ContextLearningCaseQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Limit = 10
        });

        Assert.AreEqual(1, records.Count);
        Assert.AreEqual("PromotionExpired", records[0].EventKind);
        Assert.AreEqual(ContextFailureType.StaleCandidate, records[0].FailureType);
        Assert.AreEqual(1, cases.Count);
        Assert.AreEqual("StaleContextSample", cases[0].CaseKind);
        Assert.AreEqual(ContextLearningCaseStatus.Draft, cases[0].Status);
    }

    private static ShortTermPromotionCandidateService CreateService(
        out InMemoryShortTermMemoryStore shortTermStore,
        out InMemoryShortTermPromotionCandidateStore candidateStore)
    {
        shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        candidateStore = new InMemoryShortTermPromotionCandidateStore();
        return new ShortTermPromotionCandidateService(shortTermStore, candidateStore);
    }

    private static ShortTermPromotionCandidateService CreateService(
        out InMemoryShortTermMemoryStore shortTermStore,
        out InMemoryShortTermPromotionCandidateStore candidateStore,
        out InMemoryMemoryStore memoryStore,
        out InMemoryConstraintStore constraintStore,
        out InMemoryRelationStore relationStore)
    {
        shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        candidateStore = new InMemoryShortTermPromotionCandidateStore();
        memoryStore = new InMemoryMemoryStore();
        constraintStore = new InMemoryConstraintStore();
        relationStore = new InMemoryRelationStore();
        return new ShortTermPromotionCandidateService(
            shortTermStore,
            candidateStore,
            memoryStore,
            constraintStore,
            relationStore);
    }

    private static ShortTermPromotionCandidateService CreateServiceWithLearning(
        out InMemoryShortTermMemoryStore shortTermStore,
        out InMemoryShortTermPromotionCandidateStore candidateStore,
        out InMemoryMemoryStore memoryStore,
        out InMemoryConstraintStore constraintStore,
        out InMemoryRelationStore relationStore,
        out InMemoryContextLearningStore learningStore)
    {
        shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        candidateStore = new InMemoryShortTermPromotionCandidateStore();
        memoryStore = new InMemoryMemoryStore();
        constraintStore = new InMemoryConstraintStore();
        relationStore = new InMemoryRelationStore();
        learningStore = new InMemoryContextLearningStore();
        return new ShortTermPromotionCandidateService(
            shortTermStore,
            candidateStore,
            memoryStore,
            constraintStore,
            relationStore,
            learningStore,
            new RuleBasedContextLearningCaseGenerator());
    }

    private static Task<IReadOnlyList<ShortTermPromotionCandidate>> GenerateDefaultAsync(ShortTermPromotionCandidateService service)
    {
        return service.GenerateAsync(new ShortTermPromotionCandidateGenerationRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SessionId = "session-1"
        });
    }

    private static ReviewPromotionCandidateRequest CreateReviewRequest(string operationId, string reason)
    {
        return new ReviewPromotionCandidateRequest
        {
            OperationId = operationId,
            Reviewer = "tester",
            Reason = reason,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["test"] = "true"
            }
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
}
