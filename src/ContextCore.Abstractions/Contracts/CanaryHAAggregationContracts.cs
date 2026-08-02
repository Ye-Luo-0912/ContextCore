namespace ContextCore.Abstractions;

// ===========================================================================
// Canary HA 聚合与外部指标契约
//
// 目标（补齐 Canary 三-1 / 三-2 缺失）：
//   1. ExternalResultMetrics：外部结果指标（TaskSuccessRate / ToolSuccessRate /
//      RepairRate / SafetyViolationRate / ContextPrecision / ContextRecallProxy /
//      UserAcceptance / AnswerQuality / TokenCost / InferenceCost），
//      替代仅依赖 token budget coverage + FinalScore 的 quality_score。
//   2. ICanaryExternalMetricsSource：外部指标采集源抽象（从 Tool 执行结果、
//      用户反馈、安全审计等外部信号采集）。
//   3. ICanaryMetricsAggregator：HA 聚合抽象（跨实例指标合并），让
//      CanaryProgressionService 在多节点部署时消费全局聚合视图而非进程内局部视图。
//   4. ICanaryLeaderLease：Leader 租约契约，确保 CanaryProgressionHostedService
//      同一时刻仅一个实例处理同一 run（复用 P0-1 租约模式）。
//
// 设计原则：
//   1. 契约层不引入存储 I/O：聚合与租约的实现层可注入 Postgres rollup /
//      Prometheus 查询 / Redis stream 等。
//   2. 外部指标为可选信号：未采集时为 null，聚合器应优雅降级到进程内指标。
//   3. Leader 租约复用 ILeasedWorkStore 的 TryAcquireAsync/RenewAsync/ReleaseAsync
//      状态机语义（Pending → Leased → Acked）。
// ===========================================================================

/// <summary>
/// 外部结果指标（ground truth 信号），替代仅依赖 token budget + FinalScore 的 quality_score。
/// </summary>
/// <remarks>
/// 所有比率字段范围 [0.0, 1.0]；cost 字段为非负实数。
/// 未采集的字段为 null，聚合器应优雅跳过。
/// </remarks>
public sealed record ExternalResultMetrics
{
    /// <summary>任务成功率（1.0 = 全部成功；0.0 = 全部失败）。</summary>
    public double? TaskSuccessRate { get; init; }

    /// <summary>Tool 调用成功率（1.0 = 全部成功）。</summary>
    public double? ToolSuccessRate { get; init; }

    /// <summary>修复率（自动修复成功次数 / 需要修复的总次数；无修复需求时为 null）。</summary>
    public double? RepairRate { get; init; }

    /// <summary>安全违规率（0.0 = 无违规；越高越严重，应触发回滚）。</summary>
    public double? SafetyViolationRate { get; init; }

    /// <summary>上下文精确率（相关候选 / 总候选；proxy 可通过 click/accept 信号估算）。</summary>
    public double? ContextPrecision { get; init; }

    /// <summary>上下文召回率 proxy（命中 / 应命中；通常通过 ground-truth 标注集估算）。</summary>
    public double? ContextRecallProxy { get; init; }

    /// <summary>用户接受率（用户接受 / 总展示；1.0 = 全部接受）。</summary>
    public double? UserAcceptance { get; init; }

    /// <summary>回答质量分（人工评分或 LLM-as-judge；范围 [0.0, 1.0]）。</summary>
    public double? AnswerQuality { get; init; }

    /// <summary>Token 成本（每千次请求的 token 消耗；越低越好）。</summary>
    public double? TokenCost { get; init; }

    /// <summary>推理成本（每千次请求的推理费用，单位美元；越低越好）。</summary>
    public double? InferenceCost { get; init; }

    /// <summary>指标采集窗口内的样本数（用于判断统计显著性）。</summary>
    public required int SampleCount { get; init; }

    /// <summary>采集窗口起止时间（UTC）。</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>采集窗口结束时间（UTC）。</summary>
    public required DateTimeOffset WindowEnd { get; init; }
}

/// <summary>
/// 外部指标采集源抽象。从 Tool 执行结果、用户反馈、安全审计等外部信号采集指标。
/// </summary>
/// <remarks>
/// 实现层可对接：
/// - Tool 执行日志（ToolSuccessRate / RepairRate）
/// - 安全审计流水（SafetyViolationRate）
/// - 用户反馈管道（UserAcceptance / AnswerQuality）
/// - 评估标注集（ContextPrecision / ContextRecallProxy）
/// - 计费/用量仪表（TokenCost / InferenceCost）
/// </remarks>
public interface ICanaryExternalMetricsSource
{
    /// <summary>
    /// 采集指定 Canary run 在指定时间窗口内的外部结果指标。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="windowStart">采集窗口起始时间（UTC）。</param>
    /// <param name="windowEnd">采集窗口结束时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>外部指标；无数据时返回 SampleCount=0 的空指标（所有比率字段为 null）。</returns>
    ValueTask<ExternalResultMetrics> CollectAsync(
        string runId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canary HA 聚合指标（跨实例合并后的全局视图）。
/// </summary>
/// <remarks>
/// 进程内 CanaryObservationMetrics（parity/error/latency/quality）与
/// ExternalResultMetrics（外部 ground truth）的合并结果。
/// CanaryProgressionService 消费本类型做推进/回滚决策。
/// </remarks>
public sealed record CanaryAggregatedMetrics
{
    /// <summary>聚合来源的实例数（1 = 单节点；>1 = HA 聚合）。</summary>
    public required int InstanceCount { get; init; }

    /// <summary>总观察次数（跨所有实例求和）。</summary>
    public required int TotalObservations { get; init; }

    /// <summary>发散率（跨实例加权平均）。</summary>
    public required double DivergenceRate { get; init; }

    /// <summary>V2 错误率（跨实例加权平均）。</summary>
    public required double V2ErrorRate { get; init; }

    /// <summary>Legacy 错误率（跨实例加权平均）。</summary>
    public required double LegacyErrorRate { get; init; }

    /// <summary>V2 P95 延迟（跨实例取 max 或合并 DDSketch 后查询）。</summary>
    public required double V2P95LatencyMs { get; init; }

    /// <summary>Legacy P95 延迟（跨实例加权平均；用于 latency multiplier 回滚门）。</summary>
    public required double LegacyP95LatencyMs { get; init; }

    /// <summary>进程内质量分（token budget + FinalScore；诊断用）。</summary>
    public required double AverageQualityScore { get; init; }

    /// <summary>外部结果指标（ground truth；null = 未采集）。</summary>
    public ExternalResultMetrics? ExternalMetrics { get; init; }

    /// <summary>聚合时使用的 stage epoch（用于诊断与过滤确认）。</summary>
    public required long CurrentStageEpoch { get; init; }

    /// <summary>聚合窗口起止时间（UTC）。</summary>
    public required DateTimeOffset WindowStart { get; init; }

    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>
    /// 各实例 V2 延迟 DDSketch 的二进制字节列表（来自 canary_metrics_samples.v2_latency_sketch bytea 列）。
    /// 由 PostgresCanaryMetricsAggregator.AggregateAsync 读取所有实例的 sketch 字节填充；
    /// CanaryLeaderHostedService 消费时反序列化并 MergeFrom 合并，从合并后的 sketch 查询总体 P95，
    /// 覆盖 V2P95LatencyMs（加权平均值的近似）。null/空 = 无 sketch 数据，保持 V2P95LatencyMs 原值。
    /// </summary>
    public IReadOnlyList<byte[]>? V2InstanceSketches { get; init; }

    /// <summary>
    /// 各实例 Legacy 延迟 DDSketch 的二进制字节列表。语义同 <see cref="V2InstanceSketches"/>。
    /// </summary>
    public IReadOnlyList<byte[]>? LegacyInstanceSketches { get; init; }
}

/// <summary>
/// Canary HA 指标聚合器抽象。在多实例部署时合并跨节点的进程内指标。
/// </summary>
/// <remarks>
/// 实现层可选：
/// - Postgres rollup：各节点写入指标表，聚合器 SELECT SUM/AVG 合并。
/// - Prometheus/OTel 查询：通过 PromQL 合并跨实例指标。
/// - Redis/stream：各节点上报到 stream，聚合器消费合并。
///
/// 单节点部署时实现层可直接返回进程内 CanaryMetricsCollector 的快照（InstanceCount=1）。
///
/// <b>Stage Epoch 模型</b>（修复 HA 聚合数据重复累计问题）：
/// <list type="bullet">
/// <item>每个 Canary run 维护一个单调递增的 <c>stage_epoch</c>，存储在 <c>canary_run_epochs</c> 表。</item>
/// <item>各实例 UPSERT 本地快照到 <c>canary_metrics_samples</c>，PK = (run_id, stage_epoch, instance_id)，
///   即每个 epoch 内每实例只保留最新一条快照。</item>
/// <item>Leader 推进百分比档时调用 <see cref="AdvanceEpochAsync"/> 递增 epoch，
///   随后所有实例在下一次轮询时检测到 epoch 变化并 Reset 本地 Collector，从 0 开始新 epoch 累计。</item>
/// <item>聚合时只汇总 <c>WHERE stage_epoch = current_epoch</c> 的行，旧 epoch 数据不参与聚合。</item>
/// </list>
/// </remarks>
public interface ICanaryMetricsAggregator
{
    /// <summary>
    /// 聚合指定 Canary run 的跨实例指标 + 外部指标。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="externalMetricsSource">外部指标源（可选；为 null 时 ExternalMetrics=null）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全局聚合指标。</returns>
    /// <remarks>
    /// 聚合自动使用当前 stage epoch（从 <see cref="GetCurrentEpochAsync"/> 读取），
    /// 仅汇总 <c>stage_epoch = current_epoch</c> 的最新快照行。
    /// </remarks>
    ValueTask<CanaryAggregatedMetrics> AggregateAsync(
        string runId,
        ICanaryExternalMetricsSource? externalMetricsSource = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 递增指定 run 的 stage epoch（Leader 推进百分比档时调用）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="fencingToken">
    /// Leader 租约的 fencing token（单调递增）。非 0 时，数据库 UPDATE 校验
    /// <c>WHERE fencing_token &lt;= @fencingToken</c>，确保只有当前持有 lease 的 Leader 能推进 epoch。
    /// 旧 Leader（fencing token 较小）的 UPDATE 影响 0 行，返回 0 表示推进失败。
    /// 默认 0 = 不做 fencing 校验（向后兼容单节点模式与测试）。
    /// </param>
    /// <returns>递增后的新 epoch 值；fencing 校验失败时返回 0。</returns>
    /// <remarks>
    /// 递增后，所有实例在下一次轮询时应检测到 epoch 变化并 Reset 本地 Collector。
    /// 旧 epoch 的快照行不再参与聚合（但保留在表中供审计，由 <see cref="PruneOldEpochsAsync"/> 清理）。
    /// </remarks>
    ValueTask<long> AdvanceEpochAsync(string runId, CancellationToken cancellationToken = default, long fencingToken = 0);

    /// <summary>
    /// 获取指定 run 的当前 stage epoch。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 epoch（未初始化时为 0）。</returns>
    ValueTask<long> GetCurrentEpochAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理旧 epoch 的快照行（控制表增长）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="keepEpochCount">保留最近 N 个 epoch 的数据（默认 2）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>删除的行数。</returns>
    ValueTask<int> PruneOldEpochsAsync(string runId, int keepEpochCount = 2, CancellationToken cancellationToken = default);
}

/// <summary>
/// Canary Leader 租约契约。确保 CanaryProgressionHostedService 同一时刻仅一个实例
/// 处理同一 run，避免多实例同时推进/回滚同一 Canary。
/// </summary>
/// <remarks>
/// 复用 P0-1 租约模式（Pending → Leased → Acked）。
/// 实现层应使用 Postgres FOR UPDATE SKIP LOCKED 或分布式锁。
/// </remarks>
public interface ICanaryLeaderLease
{
    /// <summary>
    /// 尝试获取指定 run 的 leader 租约。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="leaseDuration">租约有效期。</param>
    /// <param name="owner">候选 leader 标识（如实例 ID）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>租约信息；已被其他实例持有时返回 null。</returns>
    ValueTask<LeasedLeadership?> TryAcquireAsync(
        string runId,
        TimeSpan leaseDuration,
        string owner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 续租约（leader 心跳）。续约失败（租约被抢占或过期）时返回 false，
    /// 调用方应立即停止处理该 run。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="leaseToken">租约 token（来自 TryAcquireAsync）。</param>
    /// <param name="extension">延长时间量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 续约成功；false = 租约已丢失，应停止处理。</returns>
    ValueTask<bool> RenewAsync(
        string runId,
        string leaseToken,
        TimeSpan extension,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 释放租约（主动让出 leader）。通常在 run 完成（Promoted）或回滚后调用。
    /// </summary>
    ValueTask ReleaseAsync(
        string runId,
        string leaseToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 回收过期租约（后台清理）。应由定时任务调用。
    /// </summary>
    /// <returns>回收的过期租约数。</returns>
    ValueTask<int> ReapExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Leader 租约信息（TryAcquireAsync 返回值）。
/// </summary>
public sealed record LeasedLeadership
{
    /// <summary>Canary run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>租约 token（续约/释放时必须提供）。</summary>
    public required string LeaseToken { get; init; }

    /// <summary>Leader 标识（当前持有者）。</summary>
    public required string Owner { get; init; }

    /// <summary>租约过期时间（UTC）。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Fencing token（单调递增，从 1 开始）。
    /// 每次 TryAcquireAsync 成功获取（含抢占过期租约）时递增；RenewAsync 不递增。
    /// 用于 Progression 更新（如 <see cref="ICanaryMetricsAggregator.AdvanceEpochAsync"/>）的 lease 校验：
    /// 数据库 UPDATE 追加 <c>WHERE fencing_token &lt;= @fencingToken</c>，
    /// lease 被抢占后旧 Leader 的 fencing token 较小，UPDATE 影响 0 行，推进失败。
    /// </summary>
    public long FencingToken { get; init; }
}

/// <summary>
/// Canary 决策类型（用于严格 HA 单事务推进）。
/// </summary>
/// <remarks>
/// 与 <see cref="CanaryProgressionDecision"/> 的差异：本枚举仅用于 <see cref="ICanaryDecisionApplier"/>
/// 单一事务接口，将"推进/回滚/保持"三种动作统一纳入 fencing 校验，避免旧 Leader 在 lease 失效后
/// 仍能修改 rollout 状态。<see cref="CanaryProgressionDecision.Promoted"/> 在本接口中映射为 <see cref="Promote"/>。
/// </remarks>
public enum CanaryDecision
{
    /// <summary>推进到下一档百分比（含 100% 晋升）。</summary>
    Promote,

    /// <summary>触发自动回滚（百分比归零）。</summary>
    Rollback,

    /// <summary>保持当前档位（仅写入审计，不修改 percentage）。</summary>
    Hold
}

/// <summary>
/// Canary 决策请求（单一事务接口入参）。
/// </summary>
/// <remarks>
/// 调用方（<c>CanaryLeaderHostedService</c>）在评估完聚合指标后构造本请求，
/// 由 <see cref="ICanaryDecisionApplier.ApplyCanaryDecisionAsync"/> 在单一 PostgreSQL 事务内
/// 完成 lease/fencing 校验 → pipeline revision CAS → transition audit 写入 → epoch 递增四步。
/// 任一步骤失败则整个事务回滚，确保 HA 推进的严格一致性。
/// </remarks>
public sealed record CanaryDecisionRequest
{
    /// <summary>Canary run ID（同时作为 canary_pipelines 主键）。</summary>
    public required string RunId { get; init; }

    /// <summary>
    /// 调用方已知的 pipeline 修订号（CAS 预期值）。
    /// 首次初始化时传 0（表示行尚不存在，事务内会 INSERT 初始行 revision=1）。
    /// 后续推进时传当前 revision，事务内 UPDATE 校验 <c>WHERE revision = @expectedRevision</c>。
    /// </summary>
    public required int ExpectedRevision { get; init; }

    /// <summary>
    /// Leader 租约的 fencing token（字符串形式，与 <see cref="LeasedLeadership.FencingToken"/> 对应）。
    /// 事务内 SELECT 校验 <c>canary_leader_leases.fencing_token = @fencingToken AND lease_expires_at > clock_timestamp()</c>，
    /// 确保只有当前持有有效 lease 的 Leader 能修改 pipeline 状态。
    /// </summary>
    public required string FencingToken { get; init; }

    /// <summary>决策类型（Promote/Rollback/Hold）。</summary>
    public required CanaryDecision Decision { get; init; }

    /// <summary>
    /// 新的百分比档（0-100）。Promote 时为下一档；Rollback 时为 0；Hold 时等于当前档。
    /// </summary>
    public required double NewPercentage { get; init; }

    /// <summary>
    /// 转换描述（人类可读，写入审计），例如 "5→10" 或 "shadow→canary" 或 "10→0(rollback)"。
    /// </summary>
    public required string Transition { get; init; }

    /// <summary>
    /// 新的 stage epoch 值（事务内 UPSERT 到 <c>canary_run_epochs</c>）。
    /// 调用方应传入 <c>当前 epoch + 1</c>；事务内 UPSERT <c>current_epoch = @newEpoch</c>。
    /// </summary>
    public required long NewEpoch { get; init; }

    /// <summary>可选的 transition ID（用于审计幂等去重，默认生成新 GUID）。</summary>
    public string? TransitionId { get; init; }

    /// <summary>决策理由（写入审计）。</summary>
    public required string Rationale { get; init; }
}

/// <summary>
/// Canary 决策执行结果。
/// </summary>
public sealed record CanaryDecisionResult
{
    /// <summary>是否成功应用（事务提交）。</summary>
    public required bool Applied { get; init; }

    /// <summary>推进前的百分比档（事务内 SELECT 得到）。</summary>
    public required int PreviousPercentage { get; init; }

    /// <summary>推进后的百分比档（= <see cref="CanaryDecisionRequest.NewPercentage"/>）。</summary>
    public required int CurrentPercentage { get; init; }

    /// <summary>推进后的新 revision（成功时 = expectedRevision + 1 或 1 首次初始化）。</summary>
    public required int NewRevision { get; init; }

    /// <summary>推进后的新 stage epoch（成功时 = <see cref="CanaryDecisionRequest.NewEpoch"/>）。</summary>
    public required long NewEpoch { get; init; }

    /// <summary>
    /// 失败原因代码（Applied=false 时有值）：
    /// <list type="bullet">
    /// <item><c>LeaseLost</c>：fencing token 不匹配或 lease 已过期。</item>
    /// <item><c>RevisionMismatch</c>：pipeline revision CAS 失败（已被其他 Leader 推进）。</item>
    /// <item><c>Success</c>：成功（Applied=true 时）。</item>
    /// </list>
    /// </summary>
    public required string FailureReason { get; init; }
}

/// <summary>
/// Canary 决策原子应用器（单一 PostgreSQL 事务接口）。
/// </summary>
/// <remarks>
/// <b>背景</b>：旧路径 <c>ProgressionService.AdvanceAsync</c> → <c>AdvanceEpochAsync(fencingToken)</c>
/// 分两步执行，旧 Leader 可能在 <c>AdvanceAsync</c> 已修改 rollout 后 <c>AdvanceEpochAsync</c> 才因
/// fencing 失败；Rollback 路径完全无 fencing 校验。本接口将四步操作合并为单一事务：
/// <code>
/// BEGIN;
/// -- 1. lease/fencing 验证（SELECT FOR UPDATE 锁住 lease 行）
/// -- 2. pipeline revision CAS（UPDATE WHERE revision = @expectedRevision）
/// -- 3. transition audit 写入（INSERT canary_transition_audit）
/// -- 4. epoch 更新（UPSERT canary_run_epochs）
/// COMMIT;
/// </code>
/// 任一步骤失败则 ROLLBACK，确保旧 Leader 无法在 lease 失效后修改 rollout。
/// </remarks>
public interface ICanaryDecisionApplier
{
    /// <summary>
    /// 在单一事务中原子应用 Canary 决策（推进/回滚/保持）。
    /// </summary>
    /// <param name="request">决策请求（含 fencing token、CAS 预期 revision、新百分比等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果（Applied=true 时事务已提交；Applied=false 时事务已回滚）。</returns>
    ValueTask<CanaryDecisionResult> ApplyCanaryDecisionAsync(
        CanaryDecisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在单一事务中原子应用 Canary 决策（单节点/本地模式，跳过 lease/fencing 校验）。
    /// </summary>
    /// <param name="request">决策请求（FencingToken 字段被忽略；其余字段同 <see cref="ApplyCanaryDecisionAsync"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果（Applied=true 时事务已提交；Applied=false 时事务已回滚）。</returns>
    /// <remarks>
    /// <b>用途</b>：单节点模式（<c>CanaryProgressionHostedService</c>）下统一 DB 真相源。
    /// 旧路径 <c>CanaryProgressionService.AdvanceAsync</c> 仅写进程内状态（<c>_runStates</c> +
    /// <c>CutoverController</c>），不写 <c>canary_pipelines</c> 表，导致进程重启后
    /// <c>RecoverFromStoreAsync</c> 读不到真实百分比。本方法跳过 lease 校验（单节点无 Leader），
    /// 但仍走 revision CAS + transition audit + epoch update 单事务，确保 DB 与审计一致。
    /// <para>
    /// <b>与 <see cref="ApplyCanaryDecisionAsync"/> 的区别</b>：仅省略步骤 1（lease/fencing 校验），
    /// 步骤 2-5（revision CAS + audit + epoch）完全一致。
    /// </para>
    /// </remarks>
    ValueTask<CanaryDecisionResult> ApplyCanaryDecisionLocalAsync(
        CanaryDecisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询指定 run 的当前 pipeline 状态（percentage + revision）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 revision 与 percentage；行不存在时返回 Revision=0, Percentage=0。</returns>
    /// <remarks>
    /// Leader 在调用 <see cref="ApplyCanaryDecisionAsync"/> 前应先调用本方法获取 <c>ExpectedRevision</c>，
    /// 避免 CAS 失败。本方法不持有事务，调用方应在获取状态后尽快提交决策以减少冲突窗口。
    /// </remarks>
    ValueTask<CanaryPipelineState> GetCanaryPipelineStateAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询指定 run 的当前 stage epoch（从 <c>canary_run_epochs</c> 表读取）。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前 stage epoch；行不存在时返回 0。</returns>
    /// <remarks>
    /// <b>用途</b>：单节点模式下 <see cref="ContextCore.Core.Services.Evolution.CanaryProgressionService"/>
    /// 在调用 <see cref="ApplyCanaryDecisionLocalAsync"/> 前需读取当前 epoch，计算
    /// <c>newEpoch = currentEpoch + 1</c>，确保 epoch 单调递增（重启后不回退）。
    /// HA 模式下 epoch 由 <see cref="ICanaryMetricsAggregator.GetCurrentEpochAsync"/> 提供。
    /// </remarks>
    ValueTask<long> GetCurrentEpochAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询所有处于活跃状态（非终态）的 Canary pipeline 状态。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>所有活跃 pipeline 的状态列表（status NOT IN 终态集合）；无活跃行时返回空列表。</returns>
    /// <remarks>
    /// <b>用途</b>：服务启动时从 DB 恢复 in-memory 状态（CutoverController 百分比 +
    /// <c>CanaryProgressionService._runStates</c>）。进程重启后这两个 in-memory 真值源丢失，
    /// 而 <c>canary_pipelines</c> 表仍持有权威百分比；本方法提供批量读取入口，
    /// 供 <c>RecoverFromStoreAsync</c> 重建进程内路由状态，避免重启后回到 0%。
    /// </remarks>
    ValueTask<IReadOnlyList<CanaryPipelineState>> GetAllActivePipelineStatesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Canary pipeline 当前状态（revision + percentage）。
/// </summary>
public sealed record CanaryPipelineState
{
    /// <summary>Canary run ID。</summary>
    public required string RunId { get; init; }

    /// <summary>当前 revision（行不存在时为 0）。</summary>
    public required int Revision { get; init; }

    /// <summary>当前百分比档（行不存在时为 0）。</summary>
    public required int Percentage { get; init; }

    /// <summary>当前状态文本（如 "Active"/"RolledBack"/"Promoted"；行不存在时为 null）。</summary>
    public string? Status { get; init; }
}

/// <summary>
/// Canary Leader HostedService 配置。
/// </summary>
public sealed class CanaryLeaderOptions
{
    /// <summary>是否启用 Leader 租约（false = 单节点模式，不竞争 leader）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>租约有效期（应大于 PollingInterval × 2 以避免误判）。</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>续约间隔（心跳频率；应小于 LeaseDuration / 2）。</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Leader 标识（null = 自动生成 host-{MachineName}-{guid}）。</summary>
    public string? Owner { get; set; }

    /// <summary>过期租约回收间隔。</summary>
    public TimeSpan ReapInterval { get; set; } = TimeSpan.FromMinutes(1);
}
