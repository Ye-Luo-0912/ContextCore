using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;

namespace ContextCore.Tests;

/// <summary>
/// AgentRunStateSemantics 权威语义层测试：终态 / 可重试 / 结算策略 / 可压缩 / 恢复策略
/// 各维度判定，以及语义层与状态机的终态集合一致性。
/// </summary>
[TestClass]
[TestCategory("Contracts")]
public sealed class AgentRunStateSemanticsTests
{
    private static readonly AgentRunState[] ExpectedTerminalStates =
    [
        AgentRunState.Completed,
        AgentRunState.Failed,
        AgentRunState.Cancelled,
        AgentRunState.LeaseLost,
        AgentRunState.ReconciliationRejected,
        AgentRunState.RecoveryBlocked,
        AgentRunState.RecoveryCorrupted,
        AgentRunState.ContextSafetyBlocked,
        AgentRunState.DeadLettered,
        AgentRunState.AdmissionRejected
    ];

    /// <summary>全部枚举值都能解析出语义描述（无遗漏、无未知值）。</summary>
    [TestMethod]
    public void Get_CoversAllEnumValues()
    {
        var all = Enum.GetValues<AgentRunState>();
        Assert.AreEqual(27, all.Length, "AgentRunState 应恰好 27 个枚举值。");

        foreach (var state in all)
        {
            var info = AgentRunStateSemantics.Get(state);
            Assert.AreEqual(state, info.State, "语义描述应回显被描述的状态。");
        }
    }

    /// <summary>终态集合：10 个权威终态必须 IsTerminal=true，其余为 false。</summary>
    [TestMethod]
    public void IsTerminal_MatchesAuthoritativeTerminalSet()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var expected = ExpectedTerminalStates.Contains(state);
            Assert.AreEqual(expected, AgentRunStateSemantics.Get(state).IsTerminal,
                "IsTerminal 判定错误：{0} 期望 {1}。", state, expected);
        }
    }

    /// <summary>语义层与状态机的终态集合必须一致（同一权威来源）。</summary>
    [TestMethod]
    public void IsTerminal_ConsistentWithStateMachine()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            Assert.AreEqual(
                AgentRunStateSemantics.Get(state).IsTerminal,
                AgentRunStateMachine.IsTerminalState(state),
                "语义层与状态机的终态判定不一致：{0}。", state);
        }
    }

    /// <summary>所有终态都必须写 finished_at（FinishedAtRequired），非终态不写。</summary>
    [TestMethod]
    public void FinishedAtRequired_TrueOnlyForTerminalStates()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var info = AgentRunStateSemantics.Get(state);
            Assert.AreEqual(info.IsTerminal, info.FinishedAtRequired,
                "FinishedAtRequired 应与 IsTerminal 一致：{0}。", state);
        }
    }

    /// <summary>结算策略：仅 AdmissionRejected（准入即拒绝，从未执行）→ Release；其余终态 → Actualize；非终态 → None。</summary>
    [TestMethod]
    public void QuotaSettlementPolicy_ClassifiesTerminals()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var info = AgentRunStateSemantics.Get(state);
            if (!info.IsTerminal)
            {
                Assert.AreEqual(QuotaSettlementPolicy.None, info.QuotaSettlementPolicy,
                    "非终态不应有结算策略：{0}。", state);
                continue;
            }

            if (state is AgentRunState.AdmissionRejected)
            {
                Assert.AreEqual(QuotaSettlementPolicy.Release, info.QuotaSettlementPolicy,
                    "准入即拒绝（从未执行）应退回容量：{0}。", state);
            }
            else
            {
                Assert.AreEqual(QuotaSettlementPolicy.Actualize, info.QuotaSettlementPolicy,
                    "可能产生过消费的终态应按实际用量转正：{0}。", state);
            }
        }
    }

    /// <summary>可重试：Failed（调度器可在重试预算内重试）与 RecoveryDependencyUnavailable（退避重试）。</summary>
    [TestMethod]
    public void Retryable_OnlyForRetryableCategories()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var expected = state is AgentRunState.Failed or AgentRunState.RecoveryDependencyUnavailable;
            Assert.AreEqual(expected, AgentRunStateSemantics.Get(state).Retryable,
                "Retryable 判定错误：{0} 期望 {1}。", state, expected);
        }
    }

    /// <summary>需人工介入：数据损坏 / 安全阻断 / 死信类终态。</summary>
    [TestMethod]
    public void RequiresManualIntervention_OnlyForCorruptionSafetyDeadLetter()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var expected = state is AgentRunState.RecoveryBlocked
                or AgentRunState.RecoveryCorrupted
                or AgentRunState.ContextSafetyBlocked
                or AgentRunState.DeadLettered;
            Assert.AreEqual(expected, AgentRunStateSemantics.Get(state).RequiresManualIntervention,
                "RequiresManualIntervention 判定错误：{0} 期望 {1}。", state, expected);
        }
    }

    /// <summary>默认可压缩：终态中除 Failed（依赖重试预算）与 AdmissionRejected（无事件流）外均可压缩。</summary>
    [TestMethod]
    public void EventCompactable_IncludesContextSafetyBlocked_ExcludesFailedAndAdmissionRejected()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var expected = state is AgentRunState.Completed
                or AgentRunState.Cancelled
                or AgentRunState.LeaseLost
                or AgentRunState.ReconciliationRejected
                or AgentRunState.RecoveryBlocked
                or AgentRunState.RecoveryCorrupted
                or AgentRunState.ContextSafetyBlocked
                or AgentRunState.DeadLettered;
            Assert.AreEqual(expected, AgentRunStateSemantics.Get(state).EventCompactable,
                "EventCompactable 判定错误：{0} 期望 {1}。", state, expected);
        }
    }

    /// <summary>完整压缩判定：Failed 仅在重试耗尽时可压缩。</summary>
    [TestMethod]
    public void IsCompactable_FailedDependsOnRetryBudget()
    {
        foreach (var state in ExpectedTerminalStates)
        {
            // 终态压缩判定应与 EventCompactable 一致（Failed 走重试预算分支）。
            var expected = AgentRunStateSemantics.Get(state).EventCompactable
                || (state == AgentRunState.Failed);
            Assert.AreEqual(expected, AgentRunStateSemantics.IsCompactable(state, retryCount: 5, maxRetries: 5),
                "重试耗尽时终态应可压缩：{0}。", state);
        }

        Assert.IsFalse(AgentRunStateSemantics.IsCompactable(AgentRunState.Failed, retryCount: 2, maxRetries: 5),
            "重试未耗尽的 Failed 不可压缩（会被调度器重新领取并全量重放）。");
        Assert.IsTrue(AgentRunStateSemantics.IsCompactable(AgentRunState.Failed, retryCount: 5, maxRetries: 5),
            "重试耗尽的 Failed 可压缩。");
        Assert.IsFalse(AgentRunStateSemantics.IsCompactable(AgentRunState.Running, retryCount: 0, maxRetries: 0),
            "非终态不可压缩。");
    }

    /// <summary>恢复策略：终态阻断；依赖不可用退避重试；执行前状态全新启动；执行中状态事件流恢复。</summary>
    [TestMethod]
    public void RecoveryPolicy_ClassifiesByExecutionPhase()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var info = AgentRunStateSemantics.Get(state);
            if (info.IsTerminal)
            {
                Assert.AreEqual(AgentRunRecoveryPolicy.Block, info.RecoveryPolicy,
                    "终态恢复策略应为阻断：{0}。", state);
            }
        }

        Assert.AreEqual(AgentRunRecoveryPolicy.Retry,
            AgentRunStateSemantics.Get(AgentRunState.RecoveryDependencyUnavailable).RecoveryPolicy);

        foreach (var state in new[]
        {
            AgentRunState.Created,
            AgentRunState.PendingAdmission,
            AgentRunState.Queued,
            AgentRunState.Claimed,
            AgentRunState.Running,
            AgentRunState.ClaimExpired,
            AgentRunState.ScheduledLocally
        })
        {
            Assert.AreEqual(AgentRunRecoveryPolicy.NewStart,
                AgentRunStateSemantics.Get(state).RecoveryPolicy,
                "执行前状态应为全新启动：{0}。", state);
        }

        foreach (var state in new[]
        {
            AgentRunState.ContextBuilding,
            AgentRunState.ModelCalling,
            AgentRunState.AwaitingApproval,
            AgentRunState.ToolDispatching,
            AgentRunState.Observing,
            AgentRunState.Checkpointing,
            AgentRunState.PendingToolExecution,
            AgentRunState.AwaitingReconciliation,
            AgentRunState.ReconciliationRunning
        })
        {
            Assert.AreEqual(AgentRunRecoveryPolicy.Resume,
                AgentRunStateSemantics.Get(state).RecoveryPolicy,
                "执行中状态应为事件流恢复：{0}。", state);
        }
    }
}
