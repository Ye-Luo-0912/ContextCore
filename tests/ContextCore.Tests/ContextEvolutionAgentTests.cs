using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

/// <summary>
/// Context Evolution Agent 契约单元测试：验证 NullContextEvolutionAgent 默认行为和 DTO 结构。
/// </summary>
[TestClass]
[TestCategory("Evolution")]
public sealed class ContextEvolutionAgentTests
{
    [TestMethod]
    public async Task NullAgent_ReturnsEmptyResult()
    {
        var agent = new NullContextEvolutionAgent();
        var request = new EvolutionCycleRequest
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1"
        };

        var result = await agent.RunCycleAsync(request);

        Assert.IsFalse(string.IsNullOrEmpty(result.CycleId));
        Assert.IsTrue(result.CompletedAt >= result.StartedAt);
        Assert.AreEqual(0, result.Goals.Count);
        Assert.AreEqual(0, result.Steps.Count);
        Assert.AreEqual(0, result.ProposedCount);
        Assert.AreEqual(0, result.AppliedCount);
        Assert.AreEqual(0, result.SkippedCount);
        Assert.AreEqual(0, result.FailedCount);
    }

    [TestMethod]
    public async Task NullAgent_NullRequest_Throws()
    {
        var agent = new NullContextEvolutionAgent();
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() =>
            agent.RunCycleAsync(null!));
    }

    [TestMethod]
    public void EvolutionGoal_Defaults_AreSafe()
    {
        var goal = new EvolutionGoal();

        Assert.AreEqual(string.Empty, goal.GoalId);
        Assert.AreEqual(EvolutionGoalType.PromoteShortTerm, goal.Type);
        Assert.AreEqual(string.Empty, goal.WorkspaceId);
        Assert.AreEqual(string.Empty, goal.CollectionId);
        Assert.IsNull(goal.TargetItemId);
        Assert.AreEqual(string.Empty, goal.Reason);
        Assert.AreEqual(0, goal.Priority);
    }

    [TestMethod]
    public void EvolutionStep_Defaults_AreSafe()
    {
        var step = new EvolutionStep();

        Assert.AreEqual(string.Empty, step.StepId);
        Assert.AreEqual(string.Empty, step.GoalId);
        Assert.AreEqual(EvolutionGoalType.PromoteShortTerm, step.Action);
        Assert.AreEqual(string.Empty, step.TargetItemId);
        Assert.AreEqual(0, step.EvidenceRefs.Count);
        Assert.AreEqual(EvolutionStepStatus.Proposed, step.Status);
        Assert.IsNull(step.AppliedAt);
        Assert.AreEqual(string.Empty, step.Message);
    }

    [TestMethod]
    public void EvolutionCycleRequest_Defaults_AreSafe()
    {
        var request = new EvolutionCycleRequest();

        Assert.AreEqual(string.Empty, request.WorkspaceId);
        Assert.AreEqual(string.Empty, request.CollectionId);
        Assert.AreEqual(0, request.GoalTypes.Count);
        Assert.AreEqual(100, request.MaxSteps);
        Assert.IsFalse(request.AutoApply);
    }

    [TestMethod]
    public void EvolutionCycleResult_Defaults_AreSafe()
    {
        var result = new EvolutionCycleResult();

        Assert.AreEqual(string.Empty, result.CycleId);
        Assert.AreEqual(0, result.Goals.Count);
        Assert.AreEqual(0, result.Steps.Count);
        Assert.AreEqual(0, result.ProposedCount);
        Assert.AreEqual(0, result.AppliedCount);
        Assert.AreEqual(0, result.SkippedCount);
        Assert.AreEqual(0, result.FailedCount);
    }

    [TestMethod]
    public void EvolutionGoalType_Enum_HasExpectedValues()
    {
        Assert.AreEqual(5, Enum.GetValues<EvolutionGoalType>().Length);
        Assert.AreEqual(0, (int)EvolutionGoalType.PromoteShortTerm);
        Assert.AreEqual(1, (int)EvolutionGoalType.ReviewStable);
        Assert.AreEqual(2, (int)EvolutionGoalType.DeprecateStale);
        Assert.AreEqual(3, (int)EvolutionGoalType.Supersede);
        Assert.AreEqual(4, (int)EvolutionGoalType.FillConstraintGap);
    }

    [TestMethod]
    public void EvolutionStepStatus_Enum_HasExpectedValues()
    {
        Assert.AreEqual(6, Enum.GetValues<EvolutionStepStatus>().Length);
        Assert.AreEqual(0, (int)EvolutionStepStatus.Proposed);
        Assert.AreEqual(1, (int)EvolutionStepStatus.Approved);
        Assert.AreEqual(2, (int)EvolutionStepStatus.Rejected);
        Assert.AreEqual(3, (int)EvolutionStepStatus.Applied);
        Assert.AreEqual(4, (int)EvolutionStepStatus.Skipped);
        Assert.AreEqual(5, (int)EvolutionStepStatus.Failed);
    }

    [TestMethod]
    public void EvolutionStep_WithAppliedStatus_HasAppliedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var step = new EvolutionStep
        {
            StepId = "step-1",
            GoalId = "goal-1",
            Action = EvolutionGoalType.PromoteShortTerm,
            TargetItemId = "item-1",
            Status = EvolutionStepStatus.Applied,
            AppliedAt = now,
            EvidenceRefs = new[] { "trace-001" }
        };

        Assert.AreEqual(EvolutionStepStatus.Applied, step.Status);
        Assert.AreEqual(now, step.AppliedAt);
        Assert.AreEqual(1, step.EvidenceRefs.Count);
    }
}
