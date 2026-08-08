namespace ContextCore.Abstractions;

/// <summary>
/// 一次 Agent Run 原子提交负载：事件流 + 可选状态 CAS + 可选 checkpoint + 结算意图。
/// 由 <see cref="IPersistentAgentRunCommitter"/> 单事务落库，替代"Event Store 顺便承担
/// Run Store 状态事务"与"Run Store 又维护另一条状态事务"的双轨结构。
/// </summary>
/// <remarks>
/// 提交内容与派生语义：
/// - <see cref="Events"/>：待追加的事件流（同 Run、Sequence 连续、哈希链完整）。
/// - <see cref="ExpectedCurrentState"/> + <see cref="NewRunSnapshot"/>：状态 CAS 前件与
///   提交后的 Run 快照；两者必须同时提供或同时为 null（null = 纯事件/checkpoint 提交，不推进状态）。
/// - <see cref="Checkpoint"/>：随提交落库的 checkpoint 本体；游标由提交器从事件流尾部派生。
/// - <see cref="UsageSnapshot"/>：结算用量快照；null 时提交器取 <see cref="NewRunSnapshot"/> 的 CostBudget。
/// - <see cref="SettlementIntent"/>：由状态语义层权威派生的结算意图；提交器在终态 CAS 成功后
///   按此写结算 outbox（仅当预留存在才入队，exactly-once），所需 outbox 条目为 0 或 1 条。
/// </remarks>
public sealed record AgentRunCommit
{
    /// <summary>
    /// Run 复合身份键（工作区 + Run）——提交负载内所有身份（事件流 / 状态快照 /
    /// checkpoint 游标 / checkpoint 会话）必须以本键为准，提交器在 SQL 执行前
    /// 统一校验一致性，杜绝"给 Run A 追加事件、把 Run B 推进终态"的跨 Run 污染。
    /// </summary>
    public required TenantRunKey Key { get; init; }

    /// <summary>待追加的事件流（同 Run、Sequence 从 0 起连续、哈希链完整）。</summary>
    public required IReadOnlyList<AgentRunEvent> Events { get; init; }

    /// <summary>期望的当前状态（CAS 前件；与 store 中现有 state 不匹配时抛异常）。null = 无状态 CAS。</summary>
    public AgentRunState? ExpectedCurrentState { get; init; }

    /// <summary>提交后的 Run 快照（含目标状态与全部可变字段）。null = 无状态 CAS。</summary>
    public AgentRun? NewRunSnapshot { get; init; }

    /// <summary>随提交落库的 checkpoint 本体；游标由提交器从事件流尾部派生。</summary>
    public AgentCheckpoint? Checkpoint { get; init; }

    /// <summary>
    /// 显式 checkpoint 游标（可选）。null 且 <see cref="Checkpoint"/> 非 null 时，
    /// 提交器从事件流尾部派生游标；两者同时为 null = 无 checkpoint 落库。
    /// </summary>
    public AgentCheckpointCursor? CheckpointCursor { get; init; }

    /// <summary>结算用量快照；null 时提交器取 <see cref="NewRunSnapshot"/> 的 CostBudget。</summary>
    public AgentCostBudget? UsageSnapshot { get; init; }

    /// <summary>可选 lease token，用于 fencing 校验。提供时（与 <see cref="FencingToken"/> 同时提供），
    /// 提交器在状态 CAS 的 WHERE 子句追加 lease 校验；lease 已被抢占时事务回滚并抛异常。</summary>
    public string? LeaseToken { get; init; }

    /// <summary>可选 fencing token，与 <see cref="LeaseToken"/> 配合使用。</summary>
    public long? FencingToken { get; init; }

    /// <summary>
    /// 结算意图（由状态语义层权威派生）：可能产生过消费的终态按实际用量转正、
    /// 准入即拒绝（从未执行）退回容量、非终态不结算。提交器在终态 CAS 成功后按此写
    /// 结算 outbox（仅预留存在才入队）。
    /// </summary>
    public QuotaSettlementPolicy SettlementIntent
        => NewRunSnapshot is null
            ? QuotaSettlementPolicy.None
            : AgentRunStateSemantics.Get(NewRunSnapshot.State).QuotaSettlementPolicy;
}

/// <summary>
/// 持久化 Agent Run 提交器：将 <see cref="AgentRunCommit"/>（事件流 + 状态 CAS + checkpoint +
/// 结算意图）作为一次原子事务落库。Actor 主路径与 Event Store 的批量追加均统一经此入口提交，
/// 消除各处各自维护"事件 + 状态"组合事务的实现漂移。
/// </summary>
public interface IPersistentAgentRunCommitter
{
    /// <summary>单事务提交 Agent Run 变更。</summary>
    /// <param name="commit">提交负载（事件流 + 可选状态 CAS + 可选 checkpoint）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask CommitAsync(AgentRunCommit commit, CancellationToken cancellationToken = default);
}

/// <summary>
/// AgentRunCommit 身份不变量校验器：提交负载内所有身份（事件流 / 状态快照 /
/// checkpoint 游标 / checkpoint 会话）必须与复合键一致。
/// 提交器在 SQL 执行前调用，任何不一致立即抛 <see cref="ArgumentException"/>——
/// 杜绝"给 Run A 追加事件、把 Run B 推进终态 + 结算"的跨 Run 污染。
/// </summary>
public static class AgentRunCommitIdentityValidator
{
    /// <summary>
    /// 校验提交负载的身份一致性；不一致时抛 <see cref="ArgumentException"/>。
    /// </summary>
    public static void ValidateIdentityConsistency(AgentRunCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var (workspaceId, runId) = (commit.Key.WorkspaceId, commit.Key.RunId);

        // 1. 事件流：每条事件的工作区 + Run 必须与复合键一致。
        for (var i = 0; i < commit.Events.Count; i++)
        {
            var evt = commit.Events[i];
            if (!string.Equals(evt.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(evt.RunId, runId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"AgentRunCommit 身份不变量校验失败：事件流第 {i} 条归属" +
                    $"(workspace_id={evt.WorkspaceId}, run_id={evt.RunId})，与复合键" +
                    $"(workspace_id={workspaceId}, run_id={runId}) 不一致。" +
                    "同一提交负载不得混入其他 Run 的事件。");
            }
        }

        // 2. 状态快照：Run 归属必须与复合键一致。
        if (commit.NewRunSnapshot is not null
            && (!string.Equals(commit.NewRunSnapshot.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(commit.NewRunSnapshot.RunId, runId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"AgentRunCommit 身份不变量校验失败：状态快照归属" +
                $"(workspace_id={commit.NewRunSnapshot.WorkspaceId}, run_id={commit.NewRunSnapshot.RunId})，" +
                $"与复合键 (workspace_id={workspaceId}, run_id={runId}) 不一致。" +
                "禁止同一提交把 Run A 的事件与 Run B 的状态推进混在一起。");
        }

        // 3. Checkpoint 游标：归属必须与复合键一致。
        if (commit.CheckpointCursor is not null
            && (!string.Equals(commit.CheckpointCursor.WorkspaceId, workspaceId, StringComparison.Ordinal)
                || !string.Equals(commit.CheckpointCursor.RunId, runId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"AgentRunCommit 身份不变量校验失败：checkpoint 游标归属" +
                $"(workspace_id={commit.CheckpointCursor.WorkspaceId}, run_id={commit.CheckpointCursor.RunId})，" +
                $"与复合键 (workspace_id={workspaceId}, run_id={runId}) 不一致。");
        }

        // 4. Checkpoint 本体：会话工作区必须与复合键的工作区一致。
        if (commit.Checkpoint is not null
            && !string.Equals(commit.Checkpoint.Session.WorkspaceId, workspaceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"AgentRunCommit 身份不变量校验失败：checkpoint 会话工作区" +
                $"(workspace_id={commit.Checkpoint.Session.WorkspaceId})，" +
                $"与复合键工作区 (workspace_id={workspaceId}) 不一致。");
        }
    }
}
