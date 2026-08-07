namespace ContextCore.Abstractions;

/// <summary>
/// Run 终态配额结算策略：执行类终态按最终持久化实际用量转正（多退少补）；
/// 未执行类终态退回预留容量；非终态无需结算。
/// </summary>
public enum QuotaSettlementPolicy : byte
{
    /// <summary>非终态，不结算。</summary>
    None = 0,

    /// <summary>执行类终态：按实际用量转正。</summary>
    Actualize = 1,

    /// <summary>未执行类终态：退回预留容量。</summary>
    Release = 2
}

/// <summary>
/// Run 恢复策略：决定 Recovery 路径如何对待指定状态。
/// </summary>
public enum AgentRunRecoveryPolicy : byte
{
    /// <summary>终态阻断：不再恢复，不得继续执行。</summary>
    Block = 0,

    /// <summary>退避重试：依赖恢复后由恢复 Worker 在退避门通过后重新入队。</summary>
    Retry = 1,

    /// <summary>从事件流恢复：崩溃恢复场景，重放事件续跑。</summary>
    Resume = 2,

    /// <summary>全新启动：尚未产生持久化事件，从 ContextBuilding 开始。</summary>
    NewStart = 3
}

/// <summary>
/// Agent Run 状态的权威语义描述。所有 Storage / Scheduler / Compactor / Settlement
/// 对状态类别的判定（终态 / 可重试 / 需结算 / 可压缩 / 恢复方式）统一消费本描述，
/// 不再各自维护状态列表。
/// </summary>
public sealed record AgentRunStateSemanticsInfo
{
    /// <summary>被描述的状态。</summary>
    public required AgentRunState State { get; init; }

    /// <summary>是否为终态（不再流转、不再执行）。</summary>
    public required bool IsTerminal { get; init; }

    /// <summary>
    /// 状态层面是否可重试（调度器可重新领取执行）。注意 Failed 的实际重试
    /// 还取决于运行时重试预算（retry_count &lt; max_retries），本字段表达类别语义。
    /// </summary>
    public required bool Retryable { get; init; }

    /// <summary>进入该状态时是否需要写 finished_at（终态审计时间戳）。</summary>
    public required bool FinishedAtRequired { get; init; }

    /// <summary>是否需要人工介入（数据损坏 / 安全阻断 / 死信，不自动恢复）。</summary>
    public required bool RequiresManualIntervention { get; init; }

    /// <summary>配额结算策略（执行类转正 / 未执行类退回 / 不结算）。</summary>
    public required QuotaSettlementPolicy QuotaSettlementPolicy { get; init; }

    /// <summary>
    /// 是否默认可压缩（终态且不再被 Recovery 重放）。Failed 除外——
    /// 仍可重试的 Failed 会被调度器重新领取并全量重放，需结合重试预算判定。
    /// </summary>
    public required bool EventCompactable { get; init; }

    /// <summary>恢复策略（终态阻断 / 退避重试 / 事件流恢复 / 全新启动）。</summary>
    public required AgentRunRecoveryPolicy RecoveryPolicy { get; init; }
}

/// <summary>
/// Agent Run 状态权威语义层：单一来源定义每个状态在终态 / 结算 / 压缩 / 恢复
/// 各维度上的类别，供 Storage（Event Store / Run Store）、Scheduler、Compactor、
/// Settlement Worker 统一消费，消除各处重复维护的状态列表漂移。
/// </summary>
public static class AgentRunStateSemantics
{
    /// <summary>获取指定状态的权威语义描述。</summary>
    /// <param name="state">Agent Run 状态。</param>
    /// <returns>状态的语义描述（终态 / 可重试 / 结算策略 / 可压缩 / 恢复策略等）。</returns>
    public static AgentRunStateSemanticsInfo Get(AgentRunState state) => state switch
    {
        // ── 终态（10）：不再流转、不再执行，需要写 finished_at ──────────────
        // 执行类终态（已尝试执行）→ Actualize 按实际用量转正；Cancelled / AdmissionRejected
        // 为未执行类 → Release 退回预留容量。
        AgentRunState.Completed => Terminal(state, retryable: false, manual: false, settlement: QuotaSettlementPolicy.Actualize, compactable: true),
        AgentRunState.Failed => Terminal(state, retryable: true, manual: false, settlement: QuotaSettlementPolicy.Actualize, compactable: false),
        AgentRunState.Cancelled => Terminal(state, retryable: false, manual: false, settlement: QuotaSettlementPolicy.Release, compactable: true),
        AgentRunState.LeaseLost => Terminal(state, retryable: false, manual: false, settlement: QuotaSettlementPolicy.Actualize, compactable: true),
        AgentRunState.ReconciliationRejected => Terminal(state, retryable: false, manual: false, settlement: QuotaSettlementPolicy.Actualize, compactable: true),
        AgentRunState.RecoveryBlocked => Terminal(state, retryable: false, manual: true, settlement: QuotaSettlementPolicy.Actualize, compactable: true),
        AgentRunState.RecoveryCorrupted => Terminal(state, retryable: false, manual: true, settlement: QuotaSettlementPolicy.Actualize, compactable: true),
        AgentRunState.DeadLettered => Terminal(state, retryable: false, manual: true, settlement: QuotaSettlementPolicy.Actualize, compactable: true),
        AgentRunState.AdmissionRejected => Terminal(state, retryable: false, manual: false, settlement: QuotaSettlementPolicy.Release, compactable: false),
        AgentRunState.ContextSafetyBlocked => Terminal(state, retryable: false, manual: true, settlement: QuotaSettlementPolicy.Actualize, compactable: true),

        // ── 恢复依赖不可用：非终态但 fail-closed 不执行，依赖恢复后退避重试 ──
        AgentRunState.RecoveryDependencyUnavailable => NonTerminal(state, retryable: true, recovery: AgentRunRecoveryPolicy.Retry),

        // ── 前置 / 调度状态：尚未产生持久化事件，恢复时全新启动 ────────────
        AgentRunState.Created => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),
        AgentRunState.PendingAdmission => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),
        AgentRunState.Queued => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),
        AgentRunState.Claimed => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),
        AgentRunState.Running => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),
        AgentRunState.ClaimExpired => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),
        AgentRunState.ScheduledLocally => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.NewStart),

        // ── 执行中状态：崩溃后从事件流恢复续跑 ──────────────────────────────
        AgentRunState.ContextBuilding => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.ModelCalling => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.AwaitingApproval => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.ToolDispatching => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.Observing => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.Checkpointing => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.PendingToolExecution => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.AwaitingReconciliation => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),
        AgentRunState.ReconciliationRunning => NonTerminal(state, retryable: false, recovery: AgentRunRecoveryPolicy.Resume),

        _ => throw new ArgumentOutOfRangeException(nameof(state), state, $"未知的 AgentRunState 值：{state}")
    };

    /// <summary>
    /// 判定 Run 是否可压缩（终态且不再被 Recovery 重放）。Failed 仅在重试已耗尽
    /// （retry_count &gt;= max_retries）时可压缩——仍可重试的 Failed 会被调度器
    /// 重新领取并全量重放事件流。
    /// </summary>
    /// <param name="state">Run 当前状态。</param>
    /// <param name="retryCount">当前重试次数。</param>
    /// <param name="maxRetries">重试预算上限。</param>
    /// <returns>可压缩返回 true；否则返回 false。</returns>
    public static bool IsCompactable(AgentRunState state, int retryCount, int maxRetries)
        => Get(state).EventCompactable
           || (state == AgentRunState.Failed && retryCount >= maxRetries);

    private static AgentRunStateSemanticsInfo Terminal(
        AgentRunState state,
        bool retryable,
        bool manual,
        QuotaSettlementPolicy settlement,
        bool compactable)
        => new()
        {
            State = state,
            IsTerminal = true,
            Retryable = retryable,
            FinishedAtRequired = true,
            RequiresManualIntervention = manual,
            QuotaSettlementPolicy = settlement,
            EventCompactable = compactable,
            RecoveryPolicy = AgentRunRecoveryPolicy.Block
        };

    private static AgentRunStateSemanticsInfo NonTerminal(
        AgentRunState state,
        bool retryable,
        AgentRunRecoveryPolicy recovery)
        => new()
        {
            State = state,
            IsTerminal = false,
            Retryable = retryable,
            FinishedAtRequired = false,
            RequiresManualIntervention = false,
            QuotaSettlementPolicy = QuotaSettlementPolicy.None,
            EventCompactable = false,
            RecoveryPolicy = recovery
        };
}
