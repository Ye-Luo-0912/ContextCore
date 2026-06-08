using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Commands;
using ContextCore.ControlRoom.Services;

namespace ContextCore.Tests;

/// <summary>覆盖 ControlRoom Promotion Review 的候选项查询和状态更新。</summary>
[TestClass]
public sealed class ContextCorePromotionReviewControlRoomTests
{
    [TestMethod]
    public async Task ControlRoomPromotionReview_ShouldListShowAndUpdateCandidate()
    {
        var state = ControlRoomService.CreateState(
            "memory",
            "context-core-data-test",
            "workspace-test",
            "collection-test");
        var service = new ControlRoomService(state);
        await state.PromotionCandidateStore.SavePromotionCandidateAsync(CreateCandidate("candidate-controlroom"));

        var candidates = await service.ListPromotionCandidatesAsync(PromotionCandidateStatus.Candidate, 10);
        var candidate = await service.GetPromotionCandidateAsync("candidate-controlroom");
        var updated = await service.UpdatePromotionCandidateStatusAsync(
            "candidate-controlroom",
            PromotionCandidateStatus.Accepted,
            reviewer: "reviewer-controlroom",
            reason: "确认可提升。");

        Assert.AreEqual(1, candidates.Count);
        Assert.IsNotNull(candidate);
        Assert.AreEqual("candidate-controlroom", candidate!.Id);
        Assert.IsNotNull(updated);
        Assert.AreEqual(PromotionCandidateStatus.Accepted, updated!.Status);
        Assert.AreEqual("reviewer-controlroom", updated.Reviewer);
        Assert.AreEqual("确认可提升。", updated.Reason);
    }

    [TestMethod]
    public async Task PromotionCommand_ShouldListAndAcceptCandidate()
    {
        var state = ControlRoomService.CreateState(
            "memory",
            "context-core-data-test",
            "workspace-test",
            "collection-test");
        var service = new ControlRoomService(state);
        await state.PromotionCandidateStore.SavePromotionCandidateAsync(CreateCandidate("candidate-command"));

        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await PromotionCommand.ExecuteAsync(service, ["list", "--status", "Candidate"]);
            await PromotionCommand.ExecuteAsync(
                service,
                ["accept", "candidate-command", "--reviewer", "reviewer-command", "--reason", "命令确认。"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var accepted = await service.GetPromotionCandidateAsync("candidate-command");
        var text = output.ToString();

        StringAssert.Contains(text, "Promotion 候选项");
        StringAssert.Contains(text, "Promotion 候选项已更新");
        Assert.IsNotNull(accepted);
        Assert.AreEqual(PromotionCandidateStatus.Accepted, accepted!.Status);
        Assert.AreEqual("reviewer-command", accepted.Reviewer);
        Assert.AreEqual("命令确认。", accepted.Reason);
    }

    [TestMethod]
    public async Task PromotionCommand_AcceptWithContextSourceKind_ShouldWriteToWorkingMemory()
    {
        var state = ControlRoomService.CreateState(
            "memory",
            "context-core-data-test",
            "workspace-test",
            "collection-test");
        var service = new ControlRoomService(state);
        // 候选来源为 context（非 memory），accept 应写入工作记忆
        await state.PromotionCandidateStore.SavePromotionCandidateAsync(
            CreateCandidateWithSourceKind("candidate-ctx", "context"));

        var originalOut = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            await PromotionCommand.ExecuteAsync(
                service,
                ["accept", "candidate-ctx", "--reviewer", "tester", "--reason", "context 来源晋升测试。"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var accepted = await service.GetPromotionCandidateAsync("candidate-ctx");
        var recentWorking = await service.GetRecentWorkingMemoryAsync(10);
        var text = output.ToString();

        Assert.IsNotNull(accepted);
        Assert.AreEqual(PromotionCandidateStatus.Accepted, accepted!.Status);
        Assert.AreEqual("tester", accepted.Reviewer);
        // 应写入工作记忆
        Assert.IsTrue(recentWorking.Any(w => w.Id == "mem:promoted-candidate-ctx"), "应将候选内容写入工作记忆");
        // 输出应包含工作记忆写入提示
        StringAssert.Contains(text, "已写入工作记忆");
    }

    [TestMethod]
    public async Task ExecuteAcceptAsync_WithMemorySourceKind_ShouldCallPromotionService()
    {
        var state = ControlRoomService.CreateState(
            "memory",
            "context-core-data-test",
            "workspace-test",
            "collection-test");
        var service = new ControlRoomService(state);

        // 先写入一条记忆条目（SourceKind = "memory"，SourceId 需指向已有条目）
        const string memId = "mem:source-for-promotion";
        await state.MemoryStore.SaveAsync(new ContextMemoryItem
        {
            Id = memId,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Type = "阶段性结论",
            Content = "阶段性结论内容。",
            Status = ContextMemoryStatus.Active,
            Layer = ContextMemoryLayer.Working,
            Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        var candidate = CreateCandidateWithSourceKind("candidate-mem", "memory", sourceId: memId);
        await state.PromotionCandidateStore.SavePromotionCandidateAsync(candidate);

        var (updated, detail) = await service.ExecuteAcceptAsync(
            "candidate-mem", "tester", "记忆来源晋升测试。");

        Assert.IsNotNull(updated);
        Assert.AreEqual(PromotionCandidateStatus.Accepted, updated!.Status);
        // detail 应包含晋升成功的描述（PromotionService 调用成功）
        StringAssert.Contains(detail, memId, "detail 应包含源记忆 ID");
    }

    private static PromotionCandidate CreateCandidateWithSourceKind(
        string id, string sourceKind, string? sourceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PromotionCandidate
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId ?? $"source-{id}",
            SourceKind = sourceKind,
            Content = $"测试候选项内容（{id}）。",
            TargetLayer = ContextMemoryLayer.Working,
            Status = PromotionCandidateStatus.Candidate,
            Decision = PromotionEvaluationDecision.PromoteToWorkingMemory,
            Category = "阶段性结论",
            Reason = "命中中期记忆 Promotion 条件。",
            Confidence = 0.8,
            MatchedRules = ["阶段性结论"],
            SourceRefs = [$"source:{id}"],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static PromotionCandidate CreateCandidate(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new PromotionCandidate
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = "source-controlroom",
            SourceKind = "context",
            Content = "阶段性结论：ControlRoom promotion review 可以更新候选状态。",
            TargetLayer = ContextMemoryLayer.Working,
            Status = PromotionCandidateStatus.Candidate,
            Decision = PromotionEvaluationDecision.PromoteToWorkingMemory,
            Category = "阶段性结论",
            Reason = "命中中期记忆 Promotion 条件。",
            Confidence = 0.8,
            MatchedRules = ["阶段性结论"],
            SourceRefs = ["source:controlroom"],
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
