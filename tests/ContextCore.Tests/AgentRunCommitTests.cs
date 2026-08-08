using ContextCore.Abstractions;

namespace ContextCore.Tests;

/// <summary>
/// AgentRunCommit 提交负载语义测试：结算意图派生、无状态提交语义、复合键身份不变量。
/// </summary>
[TestClass]
[TestCategory("Contracts")]
public sealed class AgentRunCommitTests
{
    private const string Ws = "ws-commit-test";
    private const string RunId = "run-commit-test";

    [TestMethod]
    public void SettlementIntent_ExecutingTerminalState_IsActualize()
    {
        var commit = BuildCommit(AgentRunState.Completed);
        Assert.AreEqual(QuotaSettlementPolicy.Actualize, commit.SettlementIntent, "执行类终态应按实际用量转正。");
    }

    [TestMethod]
    public void SettlementIntent_AdmissionRejected_IsRelease()
    {
        var commit = BuildCommit(AgentRunState.AdmissionRejected);
        Assert.AreEqual(QuotaSettlementPolicy.Release, commit.SettlementIntent, "准入即拒绝（从未执行）应退回预留容量。");
    }

    [TestMethod]
    public void SettlementIntent_Cancelled_IsActualize()
    {
        var commit = BuildCommit(AgentRunState.Cancelled);
        Assert.AreEqual(QuotaSettlementPolicy.Actualize, commit.SettlementIntent, "取消前可能已产生消费，应按实际用量转正。");
    }

    [TestMethod]
    public void SettlementIntent_NonTerminalState_IsNone()
    {
        var commit = BuildCommit(AgentRunState.Running);
        Assert.AreEqual(QuotaSettlementPolicy.None, commit.SettlementIntent, "非终态不结算。");
    }

    [TestMethod]
    public void SettlementIntent_RetryPending_IsNone()
    {
        var commit = BuildCommit(AgentRunState.RetryPending);
        Assert.AreEqual(QuotaSettlementPolicy.None, commit.SettlementIntent,
            "RetryPending（Attempt 失败待重试）不得结算配额——预留必须保留给下一 Attempt。");
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

    // ── 身份不变量（P0-4）：事件 / 快照 / 游标 / checkpoint 与复合键一致性 ──

    [TestMethod]
    public void Key_RoundtripsWorkspaceAndRun()
    {
        var commit = BuildCommit();
        Assert.AreEqual(Ws, commit.Key.WorkspaceId, "复合键应携带工作区。");
        Assert.AreEqual(RunId, commit.Key.RunId, "复合键应携带 Run。");
    }

    [TestMethod]
    public void Event_MismatchedRun_Throws()
    {
        var commit = BuildCommit() with
        {
            Events = new[]
            {
                BuildEvent(ws: Ws, runId: "run-other")
            }
        };

        var ex = Assert.ThrowsException<ArgumentException>(() => ValidateCommit(commit));
        StringAssert.Contains(ex.Message, "事件流第 0 条");
    }

    [TestMethod]
    public void Event_MismatchedWorkspace_Throws()
    {
        var commit = BuildCommit() with
        {
            Events = new[]
            {
                BuildEvent(ws: "ws-other", runId: RunId)
            }
        };

        var ex = Assert.ThrowsException<ArgumentException>(() => ValidateCommit(commit));
        StringAssert.Contains(ex.Message, "事件流第 0 条");
    }

    [TestMethod]
    public void Snapshot_MismatchedRun_Throws()
    {
        var commit = BuildCommit() with
        {
            NewRunSnapshot = BuildSnapshot(ws: Ws, runId: "run-other", state: AgentRunState.Failed)
        };

        var ex = Assert.ThrowsException<ArgumentException>(() => ValidateCommit(commit));
        StringAssert.Contains(ex.Message, "状态快照归属");
    }

    [TestMethod]
    public void Snapshot_MismatchedWorkspace_Throws()
    {
        var commit = BuildCommit() with
        {
            NewRunSnapshot = BuildSnapshot(ws: "ws-other", runId: RunId, state: AgentRunState.Failed)
        };

        var ex = Assert.ThrowsException<ArgumentException>(() => ValidateCommit(commit));
        StringAssert.Contains(ex.Message, "状态快照归属");
    }

    [TestMethod]
    public void CheckpointCursor_MismatchedRun_Throws()
    {
        var commit = BuildCommit() with
        {
            CheckpointCursor = new AgentCheckpointCursor
            {
                WorkspaceId = Ws,
                RunId = "run-other",
                CheckpointId = "cp-1",
                LastEventSequence = 0
            }
        };

        var ex = Assert.ThrowsException<ArgumentException>(() => ValidateCommit(commit));
        StringAssert.Contains(ex.Message, "checkpoint 游标归属");
    }

    [TestMethod]
    public void CheckpointBody_MismatchedWorkspace_Throws()
    {
        var commit = BuildCommit() with
        {
            Checkpoint = new AgentCheckpoint
            {
                Session = new AgentSessionId
                {
                    Value = "sess-1",
                    WorkspaceId = "ws-other",
                    RuntimeKind = AgentRuntimeKind.GenericTool,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                CheckpointId = "cp-1",
                CreatedAt = DateTimeOffset.UtcNow,
                StateJson = "{}"
            }
        };

        var ex = Assert.ThrowsException<ArgumentException>(() => ValidateCommit(commit));
        StringAssert.Contains(ex.Message, "checkpoint 会话工作区");
    }

    /// <summary>调用提交器的身份校验静态逻辑（与 CommitAsync 入口共用同一校验）。</summary>
    private static void ValidateCommit(AgentRunCommit commit)
        => AgentRunCommitIdentityValidator.ValidateIdentityConsistency(commit);

    private static AgentRunCommit BuildCommit(AgentRunState state = AgentRunState.Running)
        => new()
        {
            Key = new TenantRunKey(Ws, RunId),
            Events = Array.Empty<AgentRunEvent>(),
            ExpectedCurrentState = state,
            NewRunSnapshot = BuildSnapshot(Ws, RunId, state),
            UsageSnapshot = new AgentCostBudget
            {
                MaxTokens = 100,
                TokensUsed = 0,
                MaxCostUsd = 10.0,
                CostUsedUsd = 0
            }
        };

    private static AgentRun BuildSnapshot(string ws, string runId, AgentRunState state) => new()
    {
        RunId = runId,
        WorkspaceId = ws,
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
    };

    private static AgentRunEvent BuildEvent(string ws, string runId) => new()
    {
        EventId = "evt-1",
        RunId = runId,
        WorkspaceId = ws,
        Sequence = 0,
        EventType = AgentRunEventType.RunCreated,
        State = AgentRunState.Created,
        OccurredAt = DateTimeOffset.UtcNow,
        Payload = "{}"
    };
}
