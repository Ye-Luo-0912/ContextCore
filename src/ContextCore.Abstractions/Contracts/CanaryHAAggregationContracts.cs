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
//   3. Leader 租约复用 IDurableTransport 的 LeaseAsync/AckAsync/RenewLeaseAsync/
//      RequeueExpiredAsync 状态机语义（Pending → Leased → Acked）。
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
    /// <returns>递增后的新 epoch 值。</returns>
    /// <remarks>
    /// 递增后，所有实例在下一次轮询时应检测到 epoch 变化并 Reset 本地 Collector。
    /// 旧 epoch 的快照行不再参与聚合（但保留在表中供审计，由 <see cref="PruneOldEpochsAsync"/> 清理）。
    /// </remarks>
    ValueTask<long> AdvanceEpochAsync(string runId, CancellationToken cancellationToken = default);

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
