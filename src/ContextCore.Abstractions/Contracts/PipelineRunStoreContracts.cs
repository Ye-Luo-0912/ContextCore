namespace ContextCore.Abstractions;

// ===========================================================================
// Evolution Pipeline Run Store 契约
//
// 目标（对齐 R27 规格）：
//   1. 持久化 Guarded Optimization Pipeline 的运行状态（PipelineRunSnapshot）+ 3 类审计记录
//      （CanaryAssignment / RollbackRecord / BaselineComparison）。
//   2. 让 HA 场景下 pipeline run state 可跨进程恢复（替代 DefaultGuardedOptimizationPipeline
//      内的 ConcurrentDictionary in-memory 存储）。
//   3. 失败语义：SaveRunAsync 幂等（同 RunId 覆盖）；GetRunAsync 不存在返回 null。
//   4. TryTransitionAsync 原子 CAS 推进（revision + stage 双重检查 + 审计批量同事务），
//      避免 HA 场景下两个实例同时推进同一 run 导致状态分裂。
//   5. TryCreateRunAsync insert-if-absent（同 RunId 已存在返回 false，不覆盖），
//      替代 SaveRunAsync 用于 StartAsync 创建新 run，避免秒精度 RunId 碰撞导致覆盖。
//   6. PipelineTransitionRequest 让调用方提供 TransitionId 实现端到端幂等；
//      Postgres 实现应在 transitions 审计表上建立 (run_id, transition_id) 唯一约束。
//   7. CanaryAssignment 应作为进入 ScopedCanary 的 transition audit 一部分原子提交，
//      不再通过独立的 SaveCanaryAssignmentAsync 写入。
//
// 设计边界：
//   - Store 仅负责持久化；不负责状态机转换（如 OfflineExperiment → Shadow）
//     或 RollbackCondition 触发判断；这些仍由 DefaultGuardedOptimizationPipeline 维护。
//   - Store 不调用 IPromotionJudge；仅保存/读取。
//   - 默认实现使用 ConcurrentDictionary（in-memory）；生产实现替换为 Postgres store。
//   - 与 IAgentCheckpointStore 设计模式对齐（R26-2）。
//   - TryTransitionAsync 是唯一允许并发推进 run state 的入口；
//     TryCreateRunAsync 是唯一创建新 run 的入口（insert-if-absent 语义）。
// ===========================================================================

/// <summary>
/// Pipeline 运行快照（不可变）。捕获一次 pipeline run 的完整可持久化状态。
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

    // ---------- HA 字段 ----------

    /// <summary>
    /// 单调递增版本号。StartAsync 初始为 1；每次 TryTransitionAsync 成功后 +1。
    /// CAS 推进的乐观锁基础：调用方传入 expectedRevision，store 在 WHERE 条件中校验。
    /// </summary>
    public required long Revision { get; init; }

    /// <summary>
    /// 可选的租约所有者标识（实例 ID / 主机名 / 进程名）。
    /// 由调用方管理语义；store 不主动获取或续约租约。
    /// </summary>
    public string? LeaseOwner { get; init; }

    /// <summary>
    /// 可选的租约到期时间（UTC）。null 表示无租约或已过期。
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>
    /// 上一次成功推进的逻辑 transition ID（由调用方生成，如 GUID）。
    /// 用于审计与重试去重：响应丢失时，调用方使用相同 transitionId 重试，
    /// store 可识别为已应用并返回当前快照（幂等）。
    /// </summary>
    public string? LastTransitionId { get; init; }
}

/// <summary>
/// 原子推进时附带的审计批量。所有非 null 项在同一事务内写入，
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

    /// <summary>可选的 StageTransitionRecord（canary 百分比推进审计）。</summary>
    public StageTransitionRecord? StageTransition { get; init; }
}

/// <summary>
/// 调用方提供的 transition 标识，用于端到端幂等。
/// </summary>
/// <remarks>
/// 当调用方在收到 <c>TryTransitionAsync</c> 响应前超时，重试时必须复用同一
/// <see cref="TransitionId"/>。Store 通过 <see cref="PipelineRunSnapshot.LastTransitionId"/>
/// 幂等检查识别重试并返回当前快照（CAS 跳过）。
/// <para>
/// Postgres 实现应在 transitions 审计表上建立 <c>(run_id, transition_id)</c> 唯一约束，
/// 确保数据库层面也拒绝重复 transition。
/// </para>
/// </remarks>
public sealed record PipelineTransitionRequest
{
    /// <summary>
    /// 调用方生成的唯一 transition ID（如 GUID）。重试时必须复用同一值。
    /// Store 将此值写入 <see cref="PipelineRunSnapshot.LastTransitionId"/>，
    /// 后续相同 TransitionId 的重试会被识别为幂等重放。
    /// </summary>
    public required string TransitionId { get; init; }

    /// <summary>可选的 observation batch ID（关联指标批次，用于审计溯源）。</summary>
    public string? ObservationBatchId { get; init; }

    /// <summary>
    /// 可选的幂等键（用于 DB 唯一约束去重）。
    /// 若提供，Postgres 实现应将其作为 <c>(run_id, idempotency_key)</c> 唯一约束的一部分。
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Pipeline Run Store 接口。持久化 <see cref="PipelineRunSnapshot"/> 与 3 类审计记录。
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
    [Obsolete("Use TryCreateRunAsync for new run creation (insert-if-absent). SaveRunAsync overwrites on conflict.", error: false)]
    Task SaveRunAsync(PipelineRunSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建新 pipeline run（insert-if-absent 语义）。
    /// 同 RunId 已存在时返回 false（不覆盖）；不存在时插入并返回 true。
    /// </summary>
    /// <param name="snapshot">Run 快照（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 创建成功；false = RunId 已存在。</returns>
    /// <remarks>
    /// 修复：替代 <see cref="SaveRunAsync"/> 用于 <c>StartAsync</c> 创建新 run。
    /// <see cref="SaveRunAsync"/> 使用 ON CONFLICT DO UPDATE 会覆盖同 RunId 的已有 run，
    /// 导致同秒启动两次（秒精度 RunId 碰撞）时第二次覆盖第一次。
    /// TryCreateRunAsync 使用 ON CONFLICT DO NOTHING，第二次启动返回 false，
    /// 调用方据此决定重试（使用新 GUID RunId）或放弃。
    /// <para>
    /// RunId 应使用 GUID/ULID 保证唯一性，不依赖秒精度时间戳。
    /// </para>
    /// </remarks>
    Task<bool> TryCreateRunAsync(PipelineRunSnapshot snapshot, CancellationToken cancellationToken = default);

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
    /// 按 <paramref name="stage"/> 列出所有处于该阶段的 pipeline run snapshot
    /// （按 UpdatedAt 倒序）。供 CanaryProgressionHostedService 轮询 ScopedCanary 阶段的 run。
    /// </summary>
    /// <param name="stage">目标阶段。</param>
    /// <param name="take">最大返回数量（默认 100）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Run 快照列表。</returns>
    /// <remarks>
    /// 默认实现返回空列表（向后兼容未升级的 store 实现）；
    /// <see cref="InMemoryPipelineRunStore"/> 与 <see cref="PostgresPipelineRunStore"/> 覆盖此默认实现。
    /// </remarks>
    Task<IReadOnlyList<PipelineRunSnapshot>> ListRunsByStageAsync(
        OptimizationStage stage,
        int take = 100,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PipelineRunSnapshot>>(Array.Empty<PipelineRunSnapshot>());

    /// <summary>
    /// 原子 CAS 推进 pipeline run snapshot。
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
    /// <remarks>
    /// 推荐通过 <see cref="PipelineAuditBatch.CanaryAssignment"/> 在
    /// <see cref="TryTransitionAsync"/> 的 transition audit 中原子提交 canary assignment，
    /// 而非通过此方法独立写入。独立写入可能导致状态已进入 ScopedCanary 但 assignment 未写入
    /// （或反之）的不一致。此方法保留用于管理员手动恢复 / 测试场景。
    /// </remarks>
    [Obsolete("Use PipelineAuditBatch.CanaryAssignment in TryTransitionAsync for atomic canary assignment. This method bypasses transition audit.", error: false)]
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

    // ---------- Stage transitions (R28-B.8) ----------

    /// <summary>
    /// 保存 canary 百分比推进审计记录（同 TransitionId 覆盖）。
    /// </summary>
    /// <param name="record">Stage transition 记录（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SaveStageTransitionAsync(StageTransitionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 RunId 列出所有 stage transition 审计记录（按 TransitionedAt 升序）。
    /// </summary>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Stage transition 记录列表（按时间升序）。</returns>
    Task<IReadOnlyList<StageTransitionRecord>> ListStageTransitionsByRunAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canary 百分比推进审计记录（对应 stage_transitions 持久化表）。
/// </summary>
/// <remarks>
/// 每次 CanaryProgressionService 推进（Advance/Hold/Rollback/Promoted）生成一条记录。
/// 推荐通过 <see cref="PipelineAuditBatch.StageTransition"/> 在
/// <see cref="IPipelineRunStore.TryTransitionAsync"/> 的 transition audit 中原子提交，
/// 也可通过 <see cref="IPipelineRunStore.SaveStageTransitionAsync"/> 独立写入。
/// </remarks>
public sealed record StageTransitionRecord
{
    /// <summary>transition ID（主键）。</summary>
    public required string TransitionId { get; init; }

    /// <summary>关联的 pipeline run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>推进前的百分比档（from）。</summary>
    public required int FromPercentage { get; init; }

    /// <summary>推进后的百分比档（to）。</summary>
    public required int ToPercentage { get; init; }

    /// <summary>推进时间（UTC）。</summary>
    public required DateTimeOffset TransitionedAt { get; init; }

    /// <summary>幂等键（用于 stage_transitions 表去重）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>关联的 observation batch ID。</summary>
    public string? ObservationBatchId { get; init; }

    /// <summary>决策类型（Advance/Hold/Rollback/Promoted）。</summary>
    public required CanaryProgressionDecision Decision { get; init; }

    /// <summary>决策理由。</summary>
    public required string Rationale { get; init; }
}
