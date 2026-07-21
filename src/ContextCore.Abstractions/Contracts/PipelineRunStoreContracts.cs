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
//
// 设计边界：
//   - Store 仅负责持久化；不负责状态机转换（如 OfflineExperiment → Shadow）
//     或 RollbackCondition 触发判断；这些仍由 DefaultGuardedOptimizationPipeline 维护。
//   - Store 不调用 IPromotionJudge；仅保存/读取。
//   - 默认实现使用 ConcurrentDictionary（in-memory）；生产实现替换为 Postgres store。
//   - 与 IAgentCheckpointStore 设计模式对齐（R26-2）。
// ===========================================================================

/// <summary>
/// R27-2：Pipeline 运行快照（不可变）。捕获一次 pipeline run 的完整可持久化状态。
/// </summary>
/// <remarks>
/// 由 <see cref="IGuardedOptimizationPipeline"/> 实现在每次状态变更后生成新快照写入 store。
/// Store 不解析快照内部结构；仅保存/读取。
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
