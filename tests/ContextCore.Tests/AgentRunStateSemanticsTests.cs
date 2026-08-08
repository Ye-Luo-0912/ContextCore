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
        Assert.AreEqual(28, all.Length, "AgentRunState 应恰好 28 个枚举值。");

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

    /// <summary>可重试：RetryPending（Attempt 失败待重试）与 RecoveryDependencyUnavailable（退避重试）。</summary>
    [TestMethod]
    public void Retryable_OnlyForRetryableCategories()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var expected = state is AgentRunState.RetryPending or AgentRunState.RecoveryDependencyUnavailable;
            Assert.AreEqual(expected, AgentRunStateSemantics.Get(state).Retryable,
                "Retryable 判定错误：{0} 期望 {1}。", state, expected);
        }

        Assert.IsFalse(AgentRunStateSemantics.Get(AgentRunState.Failed).Retryable,
            "Failed 是重试预算耗尽后的真终态，不可重试（可重试的 Attempt 失败进入 RetryPending）。");
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

    /// <summary>默认可压缩：终态（含 Failed——重试预算耗尽才到达）可压缩；RetryPending 等非终态不可。</summary>
    [TestMethod]
    public void EventCompactable_IncludesFailed_ExcludesRetryPendingAndAdmissionRejected()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var expected = state is AgentRunState.Completed
                or AgentRunState.Failed
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

        Assert.IsFalse(AgentRunStateSemantics.Get(AgentRunState.RetryPending).EventCompactable,
            "RetryPending 会被调度器重新领取并全量重放，不可压缩。");
    }

    /// <summary>完整压缩判定：Failed 无条件可压缩（到达时重试已耗尽）；RetryPending 无条件不可压缩。</summary>
    [TestMethod]
    public void IsCompactable_FailedAlways_RetryPendingNever()
    {
        foreach (var state in ExpectedTerminalStates)
        {
            Assert.AreEqual(
                AgentRunStateSemantics.Get(state).EventCompactable,
                AgentRunStateSemantics.IsCompactable(state, retryCount: 5, maxRetries: 5),
                "终态压缩判定应与 EventCompactable 一致：{0}。", state);
        }

        Assert.IsTrue(AgentRunStateSemantics.IsCompactable(AgentRunState.Failed, retryCount: 2, maxRetries: 5),
            "Failed 是重试预算耗尽的真终态，可压缩（不依赖运行时重试计数）。");
        Assert.IsTrue(AgentRunStateSemantics.IsCompactable(AgentRunState.Failed, retryCount: 5, maxRetries: 5),
            "Failed 可压缩。");
        Assert.IsFalse(AgentRunStateSemantics.IsCompactable(AgentRunState.RetryPending, retryCount: 1, maxRetries: 5),
            "RetryPending 非终态（会被重新领取重放），不可压缩。");
        Assert.IsFalse(AgentRunStateSemantics.IsCompactable(AgentRunState.Running, retryCount: 0, maxRetries: 0),
            "非终态不可压缩。");
    }

    // ── 状态语义性质不变量（P0-3）────────────────────────────────────────

    /// <summary>
    /// 性质 1：任何可重试状态（Retryable=true）都不得有配额结算策略（Settlement=None）。
    /// 可重试 = 还有下一次 Attempt = 预留必须保留给下一次执行，提前结算会打穿配额约束。
    /// </summary>
    [TestMethod]
    public void Invariant_RetryableImpliesNoSettlement()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var info = AgentRunStateSemantics.Get(state);
            if (info.Retryable)
            {
                Assert.AreEqual(QuotaSettlementPolicy.None, info.QuotaSettlementPolicy,
                    "可重试状态不得结算配额：{0}（预留必须保留给下一 Attempt）。", state);
            }
        }
    }

    /// <summary>
    /// 性质 2：任何可被调度器领取的状态（Retryable）都不得是终态结算状态——
    /// 即"可重试"与"IsTerminal"互斥（IsTerminal ⇒ !Retryable）。
    /// </summary>
    [TestMethod]
    public void Invariant_TerminalImpliesNotRetryable()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var info = AgentRunStateSemantics.Get(state);
            if (info.IsTerminal)
            {
                Assert.IsFalse(info.Retryable,
                    "终态不得可重试（终态不再被 Scheduler 领取）：{0}。", state);
            }
        }
    }

    /// <summary>
    /// 性质 3：终态结算（Settlement != None）的状态不得被 Scheduler 领取（Retryable=false）；
    /// 反过来说，任何可领取状态都不产生结算 outbox。这保证"结算恰好发生一次且只在真正终结时"。
    /// </summary>
    [TestMethod]
    public void Invariant_FinalSettlementNeverSchedulerClaimable()
    {
        foreach (var state in Enum.GetValues<AgentRunState>())
        {
            var info = AgentRunStateSemantics.Get(state);
            if (info.QuotaSettlementPolicy != QuotaSettlementPolicy.None)
            {
                Assert.IsTrue(info.IsTerminal, "有结算策略的状态必须是终态：{0}。", state);
                Assert.IsFalse(info.Retryable, "有结算策略的状态不得被调度器重新领取：{0}。", state);
            }
        }
    }

    /// <summary>
    /// 性质 4（终态不可逆）：终态不得被 Failed / Cancelled 短路改写。
    /// 幂等收尾只允许 from == to（同状态停留），绝不允许一个终态改写成另一个终态——
    /// Completed → Failed / Completed → Cancelled / ContextSafetyBlocked → Failed /
    /// RecoveryCorrupted → Cancelled 全部必须抛异常。
    /// </summary>
    [TestMethod]
    public void Invariant_TerminalStateCannotBeOverwrittenByFailedOrCancelled()
    {
        foreach (var terminal in ExpectedTerminalStates)
        {
            // 同状态停留（幂等收尾）合法：跳过 Failed→Failed / Cancelled→Cancelled。
            if (terminal is AgentRunState.Failed or AgentRunState.Cancelled)
            {
                continue;
            }

            Assert.ThrowsException<InvalidOperationException>(
                () => AgentRunStateMachine.ValidateTransition(terminal, AgentRunState.Failed),
                $"终态 {terminal} 不得被 Failed 覆盖。");
            Assert.ThrowsException<InvalidOperationException>(
                () => AgentRunStateMachine.ValidateTransition(terminal, AgentRunState.Cancelled),
                $"终态 {terminal} 不得被 Cancelled 覆盖。");
        }

        // 同状态停留（幂等收尾）仍允许。
        AgentRunStateMachine.ValidateTransition(AgentRunState.Completed, AgentRunState.Completed);
        AgentRunStateMachine.ValidateTransition(AgentRunState.Failed, AgentRunState.Failed);
        AgentRunStateMachine.ValidateTransition(AgentRunState.Cancelled, AgentRunState.Cancelled);

        // 非终态 → Failed / Cancelled 仍合法（异常/取消短路）。
        AgentRunStateMachine.ValidateTransition(AgentRunState.Running, AgentRunState.Failed);
        AgentRunStateMachine.ValidateTransition(AgentRunState.ContextBuilding, AgentRunState.Cancelled);
        AgentRunStateMachine.ValidateTransition(AgentRunState.RetryPending, AgentRunState.Failed);
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
