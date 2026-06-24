using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Planning;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCorePlanningSnapshotTests
{
    [TestMethod]
    public async Task PlanningSnapshot_ShouldIncludeActiveTasksStableConstraintsDecisionRecordsAndLearningSummary()
    {
        var shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var learningStore = new InMemoryContextLearningStore();
        var service = new PlanningSnapshotService(shortTermStore, memoryStore, constraintStore, learningStore);
        var now = DateTimeOffset.UtcNow;

        await shortTermStore.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "task-1",
            WorkspaceId = "workspace-p1",
            CollectionId = "collection-p1",
            SessionId = "session-p1",
            Kind = "ActiveTask",
            Title = "完成 P1 planning snapshot",
            Summary = "只读聚合 planning 输入。",
            Status = "active",
            Lifecycle = "Active",
            Importance = 0.95,
            Tags = ["ActiveTask"],
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now
        });
        await constraintStore.SaveAsync(new ContextConstraint
        {
            Id = "constraint-stable-1",
            WorkspaceId = "workspace-p1",
            CollectionId = "collection-p1",
            Level = ConstraintLevel.Hard,
            Scope = ContextScope.Collection,
            Content = "planning snapshot 不得影响 package 输出。",
            Status = ContextMemoryStatus.Stable,
            Confidence = 0.99,
            CreatedAt = now.AddMinutes(-8),
            UpdatedAt = now
        });
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "decision-1",
            WorkspaceId = "workspace-p1",
            CollectionId = "collection-p1",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "decision",
            Content = "P1 只读聚合，不进入 retrieval/package。",
            Tags = ["DecisionRecord"],
            Importance = 0.9,
            CreatedAt = now.AddMinutes(-7),
            UpdatedAt = now
        });
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "preference-1",
            WorkspaceId = "workspace-p1",
            CollectionId = "collection-p1",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "preference",
            Content = "规划页面偏好简洁。",
            Tags = ["preference"],
            Importance = 0.8,
            CreatedAt = now.AddMinutes(-6),
            UpdatedAt = now
        });
        await learningStore.AddRecordAsync(new ContextLearningRecord
        {
            RecordId = "record-1",
            WorkspaceId = "workspace-p1",
            CollectionId = "collection-p1",
            SessionId = "session-p1",
            SourceKind = "test",
            SourceId = "source-1",
            EventKind = "PromotionAccepted",
            Signal = ContextFeedbackSignal.Positive,
            FailureType = ContextFailureType.None,
            CreatedAt = now
        });
        await learningStore.AddCaseAsync(new ContextLearningCase
        {
            CaseId = "case-1",
            WorkspaceId = "workspace-p1",
            CollectionId = "collection-p1",
            SessionId = "session-p1",
            SourceType = "test",
            SourceKind = "test",
            SourceId = "source-1",
            CaseKind = "PositivePromotionSample",
            Status = ContextLearningCaseStatus.Draft,
            Signal = ContextFeedbackSignal.Positive,
            CreatedAt = now
        });

        var snapshot = await service.GetSnapshotAsync("workspace-p1", "collection-p1", "session-p1");

        Assert.AreEqual("workspace-p1", snapshot.WorkspaceId);
        Assert.AreEqual("collection-p1", snapshot.CollectionId);
        Assert.AreEqual("session-p1", snapshot.SessionId);
        Assert.AreEqual("context-planning-snapshot-policy/v1", snapshot.PolicyVersion);
        Assert.AreEqual("task-1", snapshot.ActiveTasks.Single().ItemId);
        Assert.AreEqual("constraint-stable-1", snapshot.StableConstraints.Single().Id);
        Assert.AreEqual("decision-1", snapshot.DecisionRecords.Single().Id);
        Assert.AreEqual("preference-1", snapshot.StablePreferences.Single().Id);
        Assert.AreEqual(1, snapshot.LearningSignalsSummary.RecordCount);
        Assert.AreEqual(1, snapshot.LearningSignalsSummary.CaseCount);
        Assert.AreEqual(1, snapshot.LearningSignalsSummary.PositiveCount);
        Assert.AreEqual(1, snapshot.LearningSignalsSummary.DraftCaseCount);
    }

    [DataTestMethod]
    [DataRow("审计已经废弃的旧方案是否还会被命中", PlanningIntentDetector.AuditDeprecated)]
    [DataRow("检查当前约束和历史实现有没有冲突", PlanningIntentDetector.ConflictCheck)]
    [DataRow("当前任务下一步应该做什么", PlanningIntentDetector.CurrentTask)]
    [DataRow("修复这个 C# 测试失败并更新代码", PlanningIntentDetector.CodingTask)]
    [DataRow("继续写下一章并兑现角色伏笔", PlanningIntentDetector.NovelGeneration)]
    [DataRow("自动化任务失败后从恢复点重试", PlanningIntentDetector.AutomationRecovery)]
    public async Task RetrievalPlanProposal_ShouldDetectExpectedIntent(string currentInput, string expectedIntent)
    {
        var service = CreateProposalService();

        var proposal = await service.ProposeAsync(new ContextPlanningProposalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            SessionId = "session-p2",
            CurrentInput = currentInput
        });

        Assert.AreEqual(expectedIntent, proposal.Intent);
        Assert.IsFalse(proposal.UseVector);
        Assert.AreEqual(0, proposal.VectorTopK);
    }

    [TestMethod]
    public async Task RetrievalPlanProposal_ShouldIncludeSnapshotContext()
    {
        var shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var learningStore = new InMemoryContextLearningStore();
        var service = CreateProposalService(shortTermStore, memoryStore, constraintStore, learningStore);
        var now = DateTimeOffset.UtcNow;

        await shortTermStore.SaveWorkingItemAsync(new ShortTermWorkingItem
        {
            ItemId = "task-p2",
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            SessionId = "session-p2",
            Kind = "ActiveTask",
            Title = "P2 proposal",
            Summary = "生成只读 retrieval proposal。",
            Status = "active",
            Lifecycle = "Active",
            Importance = 0.9,
            Tags = ["ActiveTask"],
            CreatedAt = now,
            UpdatedAt = now
        });
        await constraintStore.SaveAsync(new ContextConstraint
        {
            Id = "constraint-p2",
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            Level = ConstraintLevel.Hard,
            Scope = ContextScope.Collection,
            Content = "proposal 不执行 retrieval。",
            Status = ContextMemoryStatus.Stable,
            CreatedAt = now,
            UpdatedAt = now
        });
        await memoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = "decision-p2",
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            Layer = ContextMemoryLayer.Stable,
            Status = ContextMemoryStatus.Stable,
            Type = "decision",
            Content = "P2 proposal preview only.",
            Tags = ["DecisionRecord"],
            CreatedAt = now,
            UpdatedAt = now
        });
        await learningStore.AddRecordAsync(new ContextLearningRecord
        {
            RecordId = "learning-p2",
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            SessionId = "session-p2",
            SourceKind = "test",
            SourceId = "source-p2",
            EventKind = "PlanningProposal",
            Signal = ContextFeedbackSignal.Positive,
            FailureType = ContextFailureType.None,
            CreatedAt = now
        });

        var proposal = await service.ProposeAsync(new ContextPlanningProposalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            SessionId = "session-p2",
            CurrentInput = "当前任务下一步"
        });
        var reasons = string.Join('\n', proposal.Reasons);

        StringAssert.Contains(reasons, "snapshot:activeTasks=1");
        StringAssert.Contains(reasons, "stableConstraints=1");
        StringAssert.Contains(reasons, "decisionRecords=1");
        StringAssert.Contains(reasons, "learningRecords=1");
        StringAssert.Contains(reasons, "snapshot.activeTask:task-p2");
        StringAssert.Contains(reasons, "snapshot.stableConstraint:constraint-p2");
        StringAssert.Contains(reasons, "snapshot.decisionRecord:decision-p2");
        CollectionAssert.Contains(proposal.Warnings.ToArray(), "previewOnly: proposal does not execute retrieval or mutate retrieval output");
    }

    [TestMethod]
    public async Task RetrievalPlanProposal_ShouldGenerateNativeSafeTopK()
    {
        var service = CreateProposalService();

        var proposal = await service.ProposeAsync(new ContextPlanningProposalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            CurrentInput = "审计已经废弃的旧方案是否还会被命中"
        });

        Assert.AreEqual(10, proposal.FinalTopK);
        Assert.IsTrue(proposal.KeywordTopK <= RetrievalPlanSafetyProfile.CreateDefault().MaxKeywordTopK);
        Assert.IsTrue(proposal.MemoryTopK <= RetrievalPlanSafetyProfile.CreateDefault().MaxMemoryTopK);
        Assert.IsTrue(proposal.RelationTopK <= RetrievalPlanSafetyProfile.CreateDefault().MaxRelationTopK);
        Assert.IsFalse(proposal.UseVector);
        Assert.AreEqual(0, proposal.VectorTopK);
        Assert.IsFalse(proposal.Reasons.Any(reason =>
            reason.Contains(".clamped:", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(proposal.Warnings.Any(warning =>
            warning.Contains("proposal safety clamp", StringComparison.OrdinalIgnoreCase)));
    }

    [DataTestMethod]
    [DataRow("审计已经废弃的旧方案是否还会被命中", PlanningIntentDetector.AuditDeprecated, true, false, 24, 24, 8, 10)]
    [DataRow("检查当前约束和历史实现有没有冲突", PlanningIntentDetector.ConflictCheck, false, true, 24, 24, 8, 10)]
    [DataRow("当前任务下一步应该做什么", PlanningIntentDetector.CurrentTask, false, false, 18, 20, 8, 10)]
    [DataRow("修复这个 C# 测试失败并更新代码", PlanningIntentDetector.CodingTask, false, false, 24, 24, 8, 10)]
    [DataRow("继续写下一章并兑现角色伏笔", PlanningIntentDetector.NovelGeneration, false, false, 22, 24, 8, 10)]
    [DataRow("自动化任务失败后从恢复点重试", PlanningIntentDetector.AutomationRecovery, false, false, 22, 22, 8, 10)]
    public async Task RetrievalPlanProposal_ShouldRespectIntentSpecificSafeDefaults(
        string currentInput,
        string expectedIntent,
        bool expectedAudit,
        bool expectedConflict,
        int expectedKeywordTopK,
        int expectedMemoryTopK,
        int expectedRelationTopK,
        int expectedFinalTopK)
    {
        var service = CreateProposalService();

        var proposal = await service.ProposeAsync(new ContextPlanningProposalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            CurrentInput = currentInput
        });

        Assert.AreEqual(expectedIntent, proposal.Intent);
        Assert.AreEqual(expectedAudit, proposal.AuditMode);
        Assert.AreEqual(expectedConflict, proposal.ConflictMode);
        Assert.AreEqual(expectedKeywordTopK, proposal.KeywordTopK);
        Assert.AreEqual(expectedMemoryTopK, proposal.MemoryTopK);
        Assert.AreEqual(expectedRelationTopK, proposal.RelationTopK);
        Assert.AreEqual(expectedFinalTopK, proposal.FinalTopK);
        Assert.IsFalse(proposal.UseVector);
        Assert.AreEqual(0, proposal.VectorTopK);
    }

    [TestMethod]
    public async Task RetrievalPlanProposal_ShouldApplyIntentSpecificRecallReserve()
    {
        var service = CreateProposalService();

        var proposal = await service.ProposeAsync(new ContextPlanningProposalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            CurrentInput = "当前任务下一步应该做什么"
        });

        Assert.AreEqual(PlanningIntentDetector.CurrentTask, proposal.Intent);
        Assert.IsTrue(proposal.UseShortTermMemory);
        Assert.IsTrue(proposal.UseWorkingMemory);
        Assert.IsTrue(proposal.UseRelations);
        Assert.IsTrue(proposal.RelationTopK > 0);
        CollectionAssert.Contains(
            proposal.Reasons.ToArray(),
            "reserve.currentTask:shortTerm,working,relation");
        Assert.IsTrue(proposal.Reasons.Any(reason =>
            reason.StartsWith("coverageFloor:", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task RetrievalPlanProposal_ShouldNotAffectRetrievalOutput()
    {
        var contextStore = new InMemoryContextStore();
        var shortTermStore = new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy());
        var memoryStore = new InMemoryMemoryStore();
        var constraintStore = new InMemoryConstraintStore();
        var learningStore = new InMemoryContextLearningStore();
        var proposalService = CreateProposalService(shortTermStore, memoryStore, constraintStore, learningStore);
        var retriever = new HybridContextRetriever(
            contextStore,
            memoryStore,
            relationStore: null,
            embeddingProvider: null,
            vectorStore: null,
            traceStore: null,
            attentionRerankOptions: new RetrievalAttentionRerankOptions());
        var now = DateTimeOffset.UtcNow;
        await contextStore.SaveAsync(new ContextItem
        {
            Id = "retrieval-p2",
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            Type = "note",
            Content = "planning proposal boundary keyword.",
            Tags = ["p2"],
            CreatedAt = now,
            UpdatedAt = now
        });
        var request = new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            QueryText = "planning proposal boundary keyword",
            IncludeVectorRecall = false,
            IncludeRelationExpansion = false,
            CandidateTake = 10,
            TopK = 5
        };

        var before = await retriever.RetrieveAsync(request);
        _ = await proposalService.ProposeAsync(new ContextPlanningProposalRequest
        {
            WorkspaceId = "workspace-p2",
            CollectionId = "collection-p2",
            CurrentInput = "当前任务下一步"
        });
        var after = await retriever.RetrieveAsync(request);

        CollectionAssert.AreEqual(
            before.SelectedItems.Select(item => item.SourceId).ToArray(),
            after.SelectedItems.Select(item => item.SourceId).ToArray());
    }

    private static RetrievalPlanProposalService CreateProposalService(
        InMemoryShortTermMemoryStore? shortTermStore = null,
        InMemoryMemoryStore? memoryStore = null,
        InMemoryConstraintStore? constraintStore = null,
        InMemoryContextLearningStore? learningStore = null)
    {
        var snapshotService = new PlanningSnapshotService(
            shortTermStore ?? new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
            memoryStore ?? new InMemoryMemoryStore(),
            constraintStore ?? new InMemoryConstraintStore(),
            learningStore ?? new InMemoryContextLearningStore());

        return new RetrievalPlanProposalService(snapshotService, new PlanningIntentDetector());
    }
}
