using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Stores;

namespace ContextCore.Tests;

/// <summary>
/// R30.1 事件压缩安全限制测试：压缩仅限终态（或重试已耗尽）的 Run。
///
/// 背景（P0-10 即时安全修复）：当前 Agent Recovery 不读取压缩快照/归档，
/// 非终态 Run 的事件流被压缩后，重启恢复会因事件链断裂判定 RecoveryCorrupted。
/// 因此 <see cref="PostgresAgentRunEventCompactor.FindCandidatesAsync"/> 的候选过滤
/// 与操作员压缩端点均使用 <see cref="PostgresAgentRunEventCompactor.IsCompactableRunState"/>
/// 只放行终态 Run（RetryPending 是非终态——会被调度器重新领取并全量重放事件流，
/// 压缩会破坏恢复；Failed 是重试预算耗尽后的真终态，天然可压缩）。
/// </summary>
[TestClass]
[TestCategory("Storage")]
[TestCategory("R30")]
public sealed class R30X_EventCompactionSafetyTests
{
    [TestMethod]
    public void IsCompactableRunState_TerminalStates_AreCompactable()
    {
        // 终态（不会再被 Recovery 重放）无论重试计数如何均可压缩。
        AgentRunState[] terminalStates =
        [
            AgentRunState.Completed,
            AgentRunState.Cancelled,
            AgentRunState.LeaseLost,
            AgentRunState.ReconciliationRejected,
            AgentRunState.RecoveryBlocked,
            AgentRunState.RecoveryCorrupted,
            AgentRunState.DeadLettered,
            AgentRunState.Failed
        ];

        foreach (var state in terminalStates)
        {
            Assert.IsTrue(
                PostgresAgentRunEventCompactor.IsCompactableRunState(state, retryCount: 0, maxRetries: 3),
                $"终态 {state} 应可压缩。");
        }
    }

    [TestMethod]
    public void IsCompactableRunState_RetryPending_NotCompactable()
    {
        // RetryPending（Attempt 失败但重试预算未耗尽）：会被调度器重新领取并
        // 全量重放事件流，压缩会破坏恢复——无论重试计数如何都不可压缩。
        Assert.IsFalse(
            PostgresAgentRunEventCompactor.IsCompactableRunState(AgentRunState.RetryPending, retryCount: 1, maxRetries: 3));
        Assert.IsFalse(
            PostgresAgentRunEventCompactor.IsCompactableRunState(AgentRunState.RetryPending, retryCount: 2, maxRetries: 5));
    }

    [TestMethod]
    public void IsCompactableRunState_Failed_IsCompactable()
    {
        // Failed 是重试预算耗尽后的真正 Run 终态（仍有重试机会的 Attempt 失败
        // 进入 RetryPending，不会进入 Failed），天然可压缩。
        Assert.IsTrue(
            PostgresAgentRunEventCompactor.IsCompactableRunState(AgentRunState.Failed, retryCount: 3, maxRetries: 3));
        // MaxRetries = 0（默认不重试）：失败即终态，可压缩。
        Assert.IsTrue(
            PostgresAgentRunEventCompactor.IsCompactableRunState(AgentRunState.Failed, retryCount: 0, maxRetries: 0));
    }

    [TestMethod]
    public void IsCompactableRunState_NonTerminalStates_NotCompactable()
    {
        // 所有非终态（含恢复依赖不可用这类会被 Recovery Worker 重试的状态）一律不可压缩。
        AgentRunState[] nonTerminalStates =
        [
            AgentRunState.Created,
            AgentRunState.ContextBuilding,
            AgentRunState.ModelCalling,
            AgentRunState.AwaitingApproval,
            AgentRunState.ToolDispatching,
            AgentRunState.Observing,
            AgentRunState.Checkpointing,
            AgentRunState.PendingToolExecution,
            AgentRunState.AwaitingReconciliation,
            AgentRunState.ReconciliationRunning,
            AgentRunState.RecoveryDependencyUnavailable,
            AgentRunState.RetryPending
        ];

        foreach (var state in nonTerminalStates)
        {
            Assert.IsFalse(
                PostgresAgentRunEventCompactor.IsCompactableRunState(state, retryCount: 0, maxRetries: 3),
                $"非终态 {state} 不应可压缩。");
        }
    }
}
