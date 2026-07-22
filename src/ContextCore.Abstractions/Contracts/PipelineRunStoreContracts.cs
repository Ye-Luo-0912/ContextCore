namespace ContextCore.Abstractions;

// ===========================================================================
// R27-2：Evolution Pipeline Run Store 契约
//
// 目标（对齐 R27 规格）：
//   1. 持久化 Guarded Optimization Pipeline 的运行状态（PipelineRunSnapshot）+ 3 类审计记录
//      （CanaryAssignment / RollbackRecord / BaselineComparison）。
//   2. 让 HA 场景下 pipeline run state 可跨进程恢复（替代 DefaultGuardedOptimizationPipeline
//      内的 ConcurrentDictionary in-memory 存储）。
//   3. 失败语义：SaveRunAsync 幂等（同 RunId 覆盖）；GetRunAsync 不存在返回 null。
//   4. P0-7：TryTransitionAsync 原子 CAS 推进（revision + stage 双重检查 + 审计批量同事务），
//      避免 HA 场景下两个实例同时推进同一 run 导致状态分裂。
//
// 设计边界：
//   - Store 仅负责持久化；不负责状态机转换（如 OfflineExperiment → Shadow）
//     或 RollbackCondition 触发判断；这些仍由 DefaultGuardedOptimizationPipeline 维护。
//   - Store 不调用 IPromotionJudge；仅保存/读取。
//   - 默认实现使用 ConcurrentDictionary（in-memory）；生产实现替换为 Postgres store。
//   - 与 IAgentCheckpointStore 设计模式对齐（R26-2）。
//   - P0-7：TryTransitionAsync 是唯一允许并发推进 run state 的入口；
//     SaveRunAsync 仅用于 StartAsync 创建新 run（首次写入），后续推进必须走 CAS 路径。
// ===========================================================================

/// <summary>
/// R27-2：Pipeline 运行快照（不可变）。捕获一次 pipeline run 的完整可持久化状态。
/// </summary>
/// <remarks>
/// 由 <see cref="IGuardedOptimizationPipeline"/> 实现在每次状态变更后生成新快照写入 store。
/// Store 不解析快照内部结构；仅保存/读取。
///
/// <b>P0-7 HA 字段</b>：
/// <list type="bullet">
/// <item><see cref="Revision"/>：单调递增版本号，初值为 1（StartAsync 写入），
///   每次 <c>TryTransitionAsync</c> 成功后 +1。CAS 推进的乐观锁基础。</item>
/// <item><see cref="LeaseOwner"/> / <see cref="LeaseExpiresAt"/>：可选的分布式租约字段，
///   由调用方管理语义；store 不主动获取或续约租约，仅持久化以便跨进程协调。</item>
/// <item><see cref="LastTransitionId"/>：上一次成功推进的逻辑 ID（调用方生成），
///   用于审计与重试去重（响应丢失时，相同 transitionId 重试可见于快照）。</item>
/// </list>
/// </remarks>
public sealed record PipelineRunSnapshot
{
    /// <summary>Run 唯一 ID（由 pipeline 生成，格式 "run-{component}-{proposalId}-{timestamp}"）。</summary>
    public required string RunId { get; init; }

    /// <summary>关联的 proposal ID。</summary>
    public required string ProposalId { get; init; }

    /// <summary>关联的 proposal 版本。</summary>
    public required OptimizationProposalVersion ProposalVersion { get; init; }

    /// <summary>原始 proposal（完整对象，用于 judge 评估与审计）。</summary>
    public required OptimizationProposal Proposal { get; init; }

    /// <summary>当前阶段。</summary>
    public required OptimizationStage CurrentStage { get; init; }

    /// <summary>当前状态。</summary>
    public required PipelineRunStatus Status { get; init; }

    /// <summary>Run 启动时间（UTC）。</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>最后更新时间（UTC）。</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>完成时间（UTC）；未完成时为 null。</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>回滚原因（仅当 Status=RolledBack/Rejected 时非空）。</summary>
    public string? RollbackReason { get; init; }

    /// <summary>阶段指标历史（每次 AdvanceWithMetricsAsync 注入的 BaselineComparison 列表）。</summary>
    public IReadOnlyList<BaselineComparison> StageMetrics { get; init; }
        = Array.Empty<BaselineComparison>();

    // ---------- P0-7：HA 字段 ----------

    /// <summary>
    /// P0-7：单调递增版本号。StartAsync 初始为 1；每次 TryTransitionAsync 成功后 +1。
    /// CAS 推进的乐观锁基础：调用方传入 expectedRevision，store 在 WHERE 条件中校验。
    /// </summary>
    public required long Revision { get; init; }

    /// <summary>
    /// P0-7：可选的租约所有者标识（实例 ID / 主机名 / 进程名）。
    /// 由调用方管理语义；store 不主动获取或续约租约。
    /// </summary>
    public string? LeaseOwner { get; init; }

    /// <summary>
    /// P0-7：可选的租约到期时间（UTC）。null 表示无租约或已过期。
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>
    /// P0-7：上一次成功推进的逻辑 transition ID（由调用方生成，如 GUID）。
    /// 用于审计与重试去重：响应丢失时，调用方使用相同 transitionId 重试，
    /// store 可识别为已应用并返回当前快照（幂等）。
    /// </summary>
    public string? LastTransitionId { get; init; }
}

/// <summary>
/// P0-7：原子推进时附带的审计批量。所有非 null 项在同一事务内写入，
/// 与 snapshot CAS 一起成功或一起失败（避免 audit 已写入但 snapshot 未更新）。
/// </summary>
public sealed record PipelineAuditBatch
{
    /// <summary>可选的 BaselineComparison（阶段指标对比）。</summary>
    public BaselineComparison? BaselineComparison { get; init; }

    /// <summary>可选的 CanaryAssignment（ScopedCanary 阶段分配）。</summary>
    public CanaryAssignment? CanaryAssignment { get; init; }

    /// <summary>可选的 RollbackRecord（自动回滚记录）。</summary>
    public RollbackRecord? RollbackRecord { get; init; }
}

/// <summary>
/// R27-2：Pipeline Run Store 接口。持久化 <see cref="PipelineRunSnapshot"/> 与 3 类审计记录。
/// </summary>
/// <remarks>
/// 适用于 HA 场景下 pipeline run state 跨进程恢复。
/// Store 不负责状态机转换或 judge 调用；仅保存/读取。
/// 实现层：InMemoryPipelineRunStore（默认）/ PostgresPipelineRunStore（HA）。
/// </remarks>
public interface IPipelineRunStore
{
    // ---------- Pipeline runs ----------

    /// <summary>保存或更新 pipeline run snapshot（同 RunId 覆盖）。</summary>
    /// <param name="snapshot">Run 快照（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveRunAsync(PipelineRunSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>按 RunId 获取 pipeline run snapshot。</summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Run 快照；不存在返回 null。</returns>
    Task<PipelineRunSnapshot?> GetRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>按 ProposalId 列出所有 run snapshot（按 UpdatedAt 倒序）。</summary>
    /// <param name="proposalId">Proposal ID。</param>
    /// <param name="take">最大返回数量（默认 100）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Run 快照列表。</returns>
    Task<IReadOnlyList<PipelineRunSnapshot>> ListRunsByProposalAsync(
        string proposalId,
        int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>按 RunId 删除 pipeline run snapshot。</summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 删除成功；false = 不存在。</returns>
    Task<bool> DeleteRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// P0-7：原子 CAS 推进 pipeline run snapshot。
    /// 仅当 store 内当前 <c>Revision == expectedRevision</c> 且 <c>CurrentStage == expectedStage</c> 时，
    /// 替换为 <paramref name="next"/> 并在同事务内写入 <paramref name="audit"/> 中的审计记录。
    /// </summary>
    /// <param name="runId">Run ID（必须与 <paramref name="next"/>.RunId 一致）。</param>
    /// <param name="expectedRevision">期望的当前 revision（CAS 乐观锁）。</param>
    /// <param name="expectedStage">期望的当前 stage（双重检查，防止 revision 匹配但 stage 已变）。</param>
    /// <param name="next">新快照（Revision 应为 <paramref name="expectedRevision"/> + 1）。</param>
    /// <param name="audit">可选的审计批量；非 null 项与 snapshot 在同事务写入。默认 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 成功推进时返回更新后的 <paramref name="next"/> 快照；
    /// CAS 失败（revision 不匹配 / stage 不匹配 / run 不存在）时返回 null。
    /// 调用方据此决定重试或放弃。
    /// </returns>
    /// <remarks>
    /// <b>事务语义</b>：snapshot 更新 + audit 批量写入要么全部成功，要么全部失败。
    /// 避免 HA 场景下 audit 已写入但 snapshot 尚未更新导致的不一致。
    /// <para>
    /// <b>幂等语义</b>：若 <paramref name="next"/>.<see cref="PipelineRunSnapshot.LastTransitionId"/> 非 null
    /// 且与 store 内当前快照的 LastTransitionId 相等，视为重试，返回当前快照（CAS 跳过）。
    /// </para>
    /// </remarks>
    Task<PipelineRunSnapshot?> TryTransitionAsync(
        string runId,
        long expectedRevision,
        OptimizationStage expectedStage,
        PipelineRunSnapshot next,
        PipelineAuditBatch? audit = null,
        CancellationToken cancellationToken = default);

    // ---------- Canary assignments ----------

    /// <summary>保存 canary assignment（同 AssignmentId 覆盖）。</summary>
    /// <param name="assignment">Canary assignment（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveCanaryAssignmentAsync(CanaryAssignment assignment, CancellationToken cancellationToken = default);

    /// <summary>按 RunId 列出所有 canary assignment（按 AssignedAt 升序）。</summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Canary assignment 列表。</returns>
    Task<IReadOnlyList<CanaryAssignment>> ListCanaryAssignmentsByRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    // ---------- Rollback records ----------

    /// <summary>保存 rollback record（同 RecordId 覆盖）。</summary>
    /// <param name="record">Rollback record（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveRollbackRecordAsync(RollbackRecord record, CancellationToken cancellationToken = default);

    /// <summary>按 RunId 获取 rollback record（每个 run 至多 1 条；不存在返回 null）。</summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Rollback record；不存在返回 null。</returns>
    Task<RollbackRecord?> GetRollbackRecordByRunAsync(string runId, CancellationToken cancellationToken = default);

    // ---------- Baseline comparisons ----------

    /// <summary>保存 baseline comparison（同 ComparisonId 覆盖）。</summary>
    /// <param name="comparison">Baseline comparison（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveBaselineComparisonAsync(BaselineComparison comparison, CancellationToken cancellationToken = default);

    /// <summary>按 ProposalId 列出所有 baseline comparison（按 ComparedAt 倒序）。</summary>
    /// <param name="proposalId">Proposal ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Baseline comparison 列表。</returns>
    Task<IReadOnlyList<BaselineComparison>> ListBaselineComparisonsByProposalAsync(
        string proposalId,
        CancellationToken cancellationToken = default);
}
