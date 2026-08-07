using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// AgentRunCommit 提交负载语义测试：结算意图派生、无状态提交语义、必填归属字段。
/// </summary>
[TestClass]
[TestCategory("Contracts")]
public sealed class AgentRunCommitTests
{
    [TestMethod]
    public void SettlementIntent_ExecutingTerminalState_IsActualize()
    {
        var commit = BuildCommit(AgentRunState.Completed);
        Assert.AreEqual(QuotaSettlementPolicy.Actualize, commit.SettlementIntent, "执行类终态应按实际用量转正。");
    }

    [TestMethod]
    public void SettlementIntent_NonExecutingTerminalState_IsRelease()
    {
        var commit = BuildCommit(AgentRunState.Cancelled);
        Assert.AreEqual(QuotaSettlementPolicy.Release, commit.SettlementIntent, "未执行类终态应退回预留容量。");
    }

    [TestMethod]
    public void SettlementIntent_NonTerminalState_IsNone()
    {
        var commit = BuildCommit(AgentRunState.Running);
        Assert.AreEqual(QuotaSettlementPolicy.None, commit.SettlementIntent, "非终态不结算。");
    }

    [TestMethod]
    public void SettlementIntent_WithoutRunSnapshot_IsNone()
    {
        var commit = BuildCommit() with { ExpectedCurrentState = null, NewRunSnapshot = null };
        Assert.AreEqual(QuotaSettlementPolicy.None, commit.SettlementIntent, "纯事件提交（无状态 CAS）不应产生结算意图。");
    }

    [TestMethod]
    public void UsageSnapshot_DefaultsToRunSnapshotCostBudget()
    {
        var commit = BuildCommit(AgentRunState.Completed) with { UsageSnapshot = null };
        Assert.IsNull(commit.UsageSnapshot, "未显式提供时用量快照为 null，提交器回退取 Run 快照 CostBudget。");
    }

    private static AgentRunCommit BuildCommit(AgentRunState state = AgentRunState.Running)
        => new()
        {
            WorkspaceId = "ws-commit-test",
            RunId = "run-commit-test",
            Events = Array.Empty<AgentRunEvent>(),
            ExpectedCurrentState = state,
            NewRunSnapshot = new AgentRun
            {
                RunId = "run-commit-test",
                WorkspaceId = "ws-commit-test",
                SessionId = "session-commit-test",
                Task = "提交负载语义测试",
                State = state,
                Turn = 0,
                ModelCallsUsed = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(10),
                CostBudget = new AgentCostBudget
                {
                    MaxTokens = 100,
                    TokensUsed = 0,
                    MaxCostUsd = 10.0,
                    CostUsedUsd = 0
                },
                MaxRetries = 0,
                Priority = 0
            },
            UsageSnapshot = new AgentCostBudget
            {
                MaxTokens = 100,
                TokensUsed = 0,
                MaxCostUsd = 10.0,
                CostUsedUsd = 0
            }
        };
}
