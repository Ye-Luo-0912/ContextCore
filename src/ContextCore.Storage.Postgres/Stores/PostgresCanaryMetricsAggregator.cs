using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Infrastructure;
using Npgsql;

namespace ContextCore.Storage.Postgres.Stores;

/// <summary>
/// 本地指标样本输入（写入 <c>canary_metrics_samples</c> 表的 DTO）。
/// </summary>
/// <remarks>
/// Storage.Postgres 项目不引用 ContextCore.Core，无法直接使用
/// <c>ContextCore.Core.Services.Evolution.CanaryObservationMetrics</c>。
/// 本 record 作为存储层输入 DTO，由 <c>CanaryLeaderHostedService</c>（在 Service 项目中，
/// 同时引用 Core 与 Storage.Postgres）从 <c>CanaryObservationMetrics</c> 转换得到。
/// 字段与 <c>CanaryObservationMetrics</c> 一一对应。
/// </remarks>
public sealed record CanaryMetricsSample
{
    /// <summary>观察窗口内的总观察次数。</summary>
    public required int TotalObservations { get; init; }

    /// <summary>发散观察次数（ParityLevel &lt; Hard）。</summary>
    public required int DivergentCount { get; init; }

    /// <summary>V2 路径错误次数。</summary>
    public required int V2ErrorCount { get; init; }

    /// <summary>Legacy 路径错误次数。</summary>
    public required int LegacyErrorCount { get; init; }

    /// <summary>V2 p95 延迟（毫秒）。</summary>
    public required double V2P95LatencyMs { get; init; }

    /// <summary>Legacy p95 延迟（毫秒）。</summary>
    public required double LegacyP95LatencyMs { get; init; }

    /// <summary>V2 路径产出质量分（0.0-1.0）。</summary>
    public required double AverageQualityScore { get; init; }

    /// <summary>任务成功率（1.0 = 全部成功；0.0 = 全部失败）；null = 未采集。</summary>
    public double? TaskSuccessRate { get; init; }

    /// <summary>Tool 调用成功率（1.0 = 全部成功）；null = 未采集。</summary>
    public double? ToolSuccessRate { get; init; }

    /// <summary>修复率；null = 未采集。</summary>
    public double? RepairRate { get; init; }

    /// <summary>安全违规率；null = 未采集。</summary>
    public double? SafetyViolationRate { get; init; }

    /// <summary>上下文精确率；null = 未采集。</summary>
    public double? ContextPrecision { get; init; }

    /// <summary>上下文召回率 proxy；null = 未采集。</summary>
    public double? ContextRecallProxy { get; init; }

    /// <summary>用户接受率；null = 未采集。</summary>
    public double? UserAcceptance { get; init; }

    /// <summary>回答质量分；null = 未采集。</summary>
    public double? AnswerQuality { get; init; }

    /// <summary>Token 成本；null = 未采集。</summary>
    public double? TokenCost { get; init; }

    /// <summary>推理成本；null = 未采集。</summary>
    public double? InferenceCost { get; init; }

    /// <summary>
    /// V2 路径延迟 DDSketch 的二进制序列化字节。null/空 = 无 sketch 数据。
    /// 由 CanaryLeaderHostedService.ToSample 从 CanaryObservationMetrics.V2LatencySketch 透传。
    /// </summary>
    public byte[]? V2LatencySketch { get; init; }

    /// <summary>
    /// Legacy 路径延迟 DDSketch 的二进制序列化字节。语义同 <see cref="V2LatencySketch"/>。
    /// </summary>
    public byte[]? LegacyLatencySketch { get; init; }

    /// <summary>
    /// 任务成功率分子（sum of TaskSuccessRate over non-null samples）。
    /// null = 未采集。与 <see cref="TaskSuccessCount"/>（分母）配合，聚合时 SUM(分子) / SUM(分母)。
    /// </summary>
    public double? TaskSuccessSum { get; init; }

    /// <summary>
    /// 任务成功率分母（count of non-null TaskSuccessRate samples）。null = 未采集。
    /// </summary>
    public long? TaskSuccessCount { get; init; }

    /// <summary>
    /// Tool 调用成功率分子。null = 未采集。与 <see cref="ToolSuccessCount"/>（分母）配合。
    /// </summary>
    public double? ToolSuccessSum { get; init; }

    /// <summary>
    /// Tool 调用成功率分母。null = 未采集。
    /// </summary>
    public long? ToolSuccessCount { get; init; }

    /// <summary>观察窗口起始时间（UTC）。</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>观察窗口结束时间（UTC）。</summary>
    public required DateTimeOffset WindowEnd { get; init; }
}

/// <summary>
/// PostgreSQL 持久化 Canary HA 指标聚合器。
/// </summary>
/// <remarks>
/// 各实例定期调用 <see cref="RecordSampleAsync"/> 将本地 <see cref="CanaryMetricsSample"/>
/// 快照 UPSERT 到 <c>canary_metrics_samples</c> 表；Leader 实例调用 <see cref="AggregateAsync"/>
/// 通过 SQL <c>SUM/AVG</c> 合并跨实例视图，产出 <see cref="CanaryAggregatedMetrics"/>。
///
/// <b>最新快照模型</b>（v36 修复 HA 聚合数据重复累计）：
/// <list type="bullet">
/// <item>表 PK = (run_id, stage_epoch, instance_id)，每次 UPSERT 覆盖该实例最新累计值。</item>
/// <item><c>TotalObservations</c>：跨实例求和（SUM）—— 因为每实例只有最新一行，不再重复累计。</item>
/// <item><c>InstanceCount</c>：COUNT(DISTINCT instance_id) —— 计算实例数而非样本行数。</item>
/// <item><c>DivergenceRate</c>：加权平均 = SUM(divergent_count) / SUM(total_observations)。</item>
/// <item><c>V2ErrorRate</c> / <c>LegacyErrorRate</c>：同上加权平均。</item>
/// <item><c>V2P95LatencyMs</c> / <c>LegacyP95LatencyMs</c>：跨实例加权平均（按 TotalObservations 加权），
/// 替代旧 MAX（保守上界）；修复：同时返回各实例 DDSketch 字节列表（V2InstanceSketches /
/// LegacyInstanceSketches），Leader 反序列化后 MergeFrom 合并查询总体 P95，覆盖加权平均近似值。</item>
/// <item><c>AverageQualityScore</c>：跨实例加权平均 = SUM(quality * observations) / SUM(observations)。</item>
/// <item>外部指标：AVG 跳过 NULL（未采集的实例不参与均值）。 修复：TaskSuccessRate / ToolSuccessRate
/// 改为 SUM(分子) / SUM(分母) 替代 AVG(rate)，避免小样本实例与大样本实例权重相同。</item>
/// <item>聚合时 <c>WHERE stage_epoch = current_epoch</c>，旧 epoch 数据不参与聚合。</item>
/// </list>
///
/// <b>外部指标优先级</b>：若 <see cref="AggregateAsync"/> 传入 <paramref name="externalMetricsSource"/>，
/// 优先使用其新鲜采集结果（更实时）；否则从 samples 表聚合（依赖各实例已写入）。
/// </remarks>
public sealed class PostgresCanaryMetricsAggregator : PostgresStoreBase, ICanaryMetricsAggregator
{
    /// <summary>初始化 PostgreSQL Canary HA 指标聚合器。</summary>
    public PostgresCanaryMetricsAggregator(
        PostgresConnectionFactory connectionFactory,
        PostgresJsonSerializer serializer,
        PostgresMigrationRunner migrationRunner)
        : base(connectionFactory, serializer, migrationRunner)
    {
    }

    /// <summary>
    /// UPSERT 一个实例的本地指标快照到 <c>canary_metrics_samples</c> 表。
    /// </summary>
    /// <param name="runId">Canary run ID。</param>
    /// <param name="instanceId">实例标识（如 host name + GUID）。</param>
    /// <param name="stageEpoch">当前 stage epoch（从 <see cref="GetCurrentEpochAsync"/> 读取）。</param>
    /// <param name="sample">本地聚合指标快照（由 <c>CanaryObservationMetrics</c> 转换得到）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// v36 最新快照模型：PK = (run_id, stage_epoch, instance_id)。
    /// 同一 (run_id, stage_epoch, instance_id) 的 UPSERT 覆盖旧值，不再追加行。
    /// 聚合时 SUM 不再重复累计——每实例在每 epoch 内只有最新一条快照。
    /// </remarks>
    public async ValueTask RecordSampleAsync(
        string runId,
        string instanceId,
        long stageEpoch,
        CanaryMetricsSample sample,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(sample);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
INSERT INTO {Table("canary_metrics_samples")} (
    sample_id, run_id, instance_id, stage_epoch, recorded_at,
    total_observations, divergent_count, v2_error_count, legacy_error_count,
    v2_p95_latency_ms, legacy_p95_latency_ms, average_quality_score,
    task_success_rate, tool_success_rate, repair_rate, safety_violation_rate,
    context_precision, context_recall_proxy, user_acceptance, answer_quality,
    token_cost, inference_cost,
    external_sample_count, external_window_start, external_window_end,
    v2_latency_sketch, legacy_latency_sketch,
    task_success_sum, task_success_count, tool_success_sum, tool_success_count
) VALUES (
    @sample_id, @run_id, @instance_id, @stage_epoch, @recorded_at,
    @total_observations, @divergent_count, @v2_error_count, @legacy_error_count,
    @v2_p95_latency_ms, @legacy_p95_latency_ms, @average_quality_score,
    @task_success_rate, @tool_success_rate, @repair_rate, @safety_violation_rate,
    @context_precision, @context_recall_proxy, @user_acceptance, @answer_quality,
    @token_cost, @inference_cost,
    @external_sample_count, @external_window_start, @external_window_end,
    @v2_latency_sketch, @legacy_latency_sketch,
    @task_success_sum, @task_success_count, @tool_success_sum, @tool_success_count
)
ON CONFLICT (run_id, stage_epoch, instance_id) DO UPDATE SET
    sample_id = EXCLUDED.sample_id,
    recorded_at = EXCLUDED.recorded_at,
    total_observations = EXCLUDED.total_observations,
    divergent_count = EXCLUDED.divergent_count,
    v2_error_count = EXCLUDED.v2_error_count,
    legacy_error_count = EXCLUDED.legacy_error_count,
    v2_p95_latency_ms = EXCLUDED.v2_p95_latency_ms,
    legacy_p95_latency_ms = EXCLUDED.legacy_p95_latency_ms,
    average_quality_score = EXCLUDED.average_quality_score,
    task_success_rate = EXCLUDED.task_success_rate,
    tool_success_rate = EXCLUDED.tool_success_rate,
    repair_rate = EXCLUDED.repair_rate,
    safety_violation_rate = EXCLUDED.safety_violation_rate,
    context_precision = EXCLUDED.context_precision,
    context_recall_proxy = EXCLUDED.context_recall_proxy,
    user_acceptance = EXCLUDED.user_acceptance,
    answer_quality = EXCLUDED.answer_quality,
    token_cost = EXCLUDED.token_cost,
    inference_cost = EXCLUDED.inference_cost,
    external_sample_count = EXCLUDED.external_sample_count,
    external_window_start = EXCLUDED.external_window_start,
    external_window_end = EXCLUDED.external_window_end,
    v2_latency_sketch = EXCLUDED.v2_latency_sketch,
    legacy_latency_sketch = EXCLUDED.legacy_latency_sketch,
    task_success_sum = EXCLUDED.task_success_sum,
    task_success_count = EXCLUDED.task_success_count,
    tool_success_sum = EXCLUDED.tool_success_sum,
    tool_success_count = EXCLUDED.tool_success_count;
""";
        command.Parameters.AddWithValue("sample_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("stage_epoch", stageEpoch);
        command.Parameters.AddWithValue("recorded_at", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("total_observations", sample.TotalObservations);
        command.Parameters.AddWithValue("divergent_count", sample.DivergentCount);
        command.Parameters.AddWithValue("v2_error_count", sample.V2ErrorCount);
        command.Parameters.AddWithValue("legacy_error_count", sample.LegacyErrorCount);
        command.Parameters.AddWithValue("v2_p95_latency_ms", sample.V2P95LatencyMs);
        command.Parameters.AddWithValue("legacy_p95_latency_ms", sample.LegacyP95LatencyMs);
        command.Parameters.AddWithValue("average_quality_score", sample.AverageQualityScore);
        AddNullableDouble(command, "task_success_rate", sample.TaskSuccessRate);
        AddNullableDouble(command, "tool_success_rate", sample.ToolSuccessRate);
        AddNullableDouble(command, "repair_rate", sample.RepairRate);
        AddNullableDouble(command, "safety_violation_rate", sample.SafetyViolationRate);
        AddNullableDouble(command, "context_precision", sample.ContextPrecision);
        AddNullableDouble(command, "context_recall_proxy", sample.ContextRecallProxy);
        AddNullableDouble(command, "user_acceptance", sample.UserAcceptance);
        AddNullableDouble(command, "answer_quality", sample.AnswerQuality);
        AddNullableDouble(command, "token_cost", sample.TokenCost);
        AddNullableDouble(command, "inference_cost", sample.InferenceCost);
        // 外部指标窗口与 SampleCount 由调用方在 sample 中体现；
        // 此处 external_sample_count 记录是否有外部指标（>0 表示本样本含外部数据）
        var hasExternal = sample.TaskSuccessRate.HasValue
            || sample.ToolSuccessRate.HasValue
            || sample.RepairRate.HasValue
            || sample.SafetyViolationRate.HasValue
            || sample.ContextPrecision.HasValue
            || sample.ContextRecallProxy.HasValue
            || sample.UserAcceptance.HasValue
            || sample.AnswerQuality.HasValue
            || sample.TokenCost.HasValue
            || sample.InferenceCost.HasValue;
        command.Parameters.AddWithValue("external_sample_count", hasExternal ? sample.TotalObservations : 0);
        AddNullableDateTimeOffset(command, "external_window_start",
            hasExternal ? sample.WindowStart : null);
        AddNullableDateTimeOffset(command, "external_window_end",
            hasExternal ? sample.WindowEnd : null);
        // DDSketch 字节持久化到 bytea 列（null/空数组写 NULL）
        AddNullableBytes(command, "v2_latency_sketch", sample.V2LatencySketch);
        AddNullableBytes(command, "legacy_latency_sketch", sample.LegacyLatencySketch);
        // 成功率分子/分母持久化
        AddNullableDouble(command, "task_success_sum", sample.TaskSuccessSum);
        AddNullableLong(command, "task_success_count", sample.TaskSuccessCount);
        AddNullableDouble(command, "tool_success_sum", sample.ToolSuccessSum);
        AddNullableLong(command, "tool_success_count", sample.ToolSuccessCount);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 通过 SQL <c>SUM/AVG</c> 聚合 <c>canary_metrics_samples</c> 表中指定 run 在当前 stage epoch 的所有实例最新快照。
    /// v36 修复：使用 <c>COUNT(DISTINCT instance_id)</c> 计算实例数；<c>WHERE stage_epoch = current_epoch</c> 过滤旧阶段数据；
    /// 延迟改为加权平均（按 TotalObservations 加权）替代 MAX。
    /// 若 <paramref name="externalMetricsSource"/> 非 null，优先使用其新鲜采集结果作为 ExternalMetrics；
    /// 否则从 samples 表聚合外部指标列（AVG 跳过 NULL）。
    /// 无样本时返回 InstanceCount=0 的空聚合（调用方应优雅降级）。
    /// </remarks>
    public async ValueTask<CanaryAggregatedMetrics> AggregateAsync(
        string runId,
        ICanaryExternalMetricsSource? externalMetricsSource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        // 读取当前 stage epoch，聚合只汇总 WHERE stage_epoch = current_epoch 的行
        var currentEpoch = await GetCurrentEpochAsync(runId, cancellationToken).ConfigureAwait(false);

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT
    COUNT(DISTINCT instance_id) AS instance_count,
    COALESCE(SUM(total_observations), 0) AS total_observations,
    CASE WHEN COALESCE(SUM(total_observations), 0) = 0 THEN 0.0
         ELSE CAST(SUM(divergent_count) AS double precision) / SUM(total_observations)
    END AS divergence_rate,
    CASE WHEN COALESCE(SUM(total_observations), 0) = 0 THEN 0.0
         ELSE CAST(SUM(v2_error_count) AS double precision) / SUM(total_observations)
    END AS v2_error_rate,
    CASE WHEN COALESCE(SUM(total_observations), 0) = 0 THEN 0.0
         ELSE CAST(SUM(legacy_error_count) AS double precision) / SUM(total_observations)
    END AS legacy_error_rate,
    -- v36：P95 延迟改为加权平均（按 TotalObservations 加权）替代 MAX，
    -- 更接近跨实例 DDSketch 合并的真实分位数（保守上界 MAX 会高估 V2 延迟导致误回滚）。
    -- 加权平均仍作为 fallback；Leader 若发现 V2InstanceSketches 非空，应 MergeFrom 合并后覆盖此值。
    CASE WHEN COALESCE(SUM(total_observations), 0) = 0 THEN 0.0
         ELSE SUM(v2_p95_latency_ms * total_observations) / SUM(total_observations)
    END AS v2_p95_latency_ms,
    CASE WHEN COALESCE(SUM(total_observations), 0) = 0 THEN 0.0
         ELSE SUM(legacy_p95_latency_ms * total_observations) / SUM(total_observations)
    END AS legacy_p95_latency_ms,
    CASE WHEN COALESCE(SUM(total_observations), 0) = 0 THEN 0.0
         ELSE SUM(average_quality_score * total_observations) / SUM(total_observations)
    END AS average_quality_score,
    -- task/tool 成功率改为 SUM(分子)/SUM(分母) 替代 AVG(rate)，
    -- 避免 10 样本实例与 10000 样本实例权重相同。NULLIF 防 0 除。
    CASE WHEN COALESCE(SUM(task_success_count), 0) = 0 THEN NULL
         ELSE SUM(task_success_sum) / NULLIF(SUM(task_success_count), 0)
    END AS task_success_rate,
    CASE WHEN COALESCE(SUM(tool_success_count), 0) = 0 THEN NULL
         ELSE SUM(tool_success_sum) / NULLIF(SUM(tool_success_count), 0)
    END AS tool_success_rate,
    AVG(repair_rate) AS repair_rate,
    AVG(safety_violation_rate) AS safety_violation_rate,
    AVG(context_precision) AS context_precision,
    AVG(context_recall_proxy) AS context_recall_proxy,
    AVG(user_acceptance) AS user_acceptance,
    AVG(answer_quality) AS answer_quality,
    AVG(token_cost) AS token_cost,
    AVG(inference_cost) AS inference_cost,
    MIN(recorded_at) AS window_start,
    MAX(recorded_at) AS window_end
FROM {Table("canary_metrics_samples")}
WHERE run_id = @run_id AND stage_epoch = @stage_epoch;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("stage_epoch", currentEpoch);

        long instanceCount;
        long totalObservations;
        double divergenceRate, v2ErrorRate, legacyErrorRate, v2P95, legacyP95, avgQuality;
        double? taskSuccess, toolSuccess, repair, safety, ctxPrecision, ctxRecall, userAccept, answerQuality, tokenCost, inferenceCost;
        DateTimeOffset windowStart, windowEnd;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // 无行返回（理论上 COUNT(*) 总会返回一行，但防御性处理）
                var now = DateTimeOffset.UtcNow;
                return new CanaryAggregatedMetrics
                {
                    InstanceCount = 0,
                    TotalObservations = 0,
                    DivergenceRate = 0.0,
                    V2ErrorRate = 0.0,
                    LegacyErrorRate = 0.0,
                    V2P95LatencyMs = 0.0,
                    LegacyP95LatencyMs = 0.0,
                    AverageQualityScore = 0.0,
                    ExternalMetrics = null,
                    CurrentStageEpoch = currentEpoch,
                    WindowStart = now,
                    WindowEnd = now
                };
            }

            instanceCount = reader.GetInt64(reader.GetOrdinal("instance_count"));
            totalObservations = reader.GetInt64(reader.GetOrdinal("total_observations"));
            divergenceRate = reader.GetDouble(reader.GetOrdinal("divergence_rate"));
            v2ErrorRate = reader.GetDouble(reader.GetOrdinal("v2_error_rate"));
            legacyErrorRate = reader.GetDouble(reader.GetOrdinal("legacy_error_rate"));
            v2P95 = reader.GetDouble(reader.GetOrdinal("v2_p95_latency_ms"));
            legacyP95 = reader.GetDouble(reader.GetOrdinal("legacy_p95_latency_ms"));
            avgQuality = reader.GetDouble(reader.GetOrdinal("average_quality_score"));
            taskSuccess = ReadNullableDouble(reader, "task_success_rate");
            toolSuccess = ReadNullableDouble(reader, "tool_success_rate");
            repair = ReadNullableDouble(reader, "repair_rate");
            safety = ReadNullableDouble(reader, "safety_violation_rate");
            ctxPrecision = ReadNullableDouble(reader, "context_precision");
            ctxRecall = ReadNullableDouble(reader, "context_recall_proxy");
            userAccept = ReadNullableDouble(reader, "user_acceptance");
            answerQuality = ReadNullableDouble(reader, "answer_quality");
            tokenCost = ReadNullableDouble(reader, "token_cost");
            inferenceCost = ReadNullableDouble(reader, "inference_cost");
            windowStart = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("window_start"));
            windowEnd = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("window_end"));
        }

        // 外部指标：优先使用 externalMetricsSource 的新鲜采集；否则用 samples 表聚合结果
        ExternalResultMetrics? externalMetrics = null;
        if (externalMetricsSource is not null)
        {
            externalMetrics = await externalMetricsSource.CollectAsync(
                runId, windowStart, windowEnd, cancellationToken).ConfigureAwait(false);
        }
        else if (taskSuccess.HasValue || toolSuccess.HasValue || repair.HasValue
            || safety.HasValue || ctxPrecision.HasValue || ctxRecall.HasValue
            || userAccept.HasValue || answerQuality.HasValue
            || tokenCost.HasValue || inferenceCost.HasValue)
        {
            // 从 samples 表聚合的外部指标构建 ExternalResultMetrics
            externalMetrics = new ExternalResultMetrics
            {
                TaskSuccessRate = taskSuccess,
                ToolSuccessRate = toolSuccess,
                RepairRate = repair,
                SafetyViolationRate = safety,
                ContextPrecision = ctxPrecision,
                ContextRecallProxy = ctxRecall,
                UserAcceptance = userAccept,
                AnswerQuality = answerQuality,
                TokenCost = tokenCost,
                InferenceCost = inferenceCost,
                SampleCount = checked((int)totalObservations),
                WindowStart = windowStart,
                WindowEnd = windowEnd
            };
        }

        // 读取各实例的 DDSketch 字节，供 Leader 反序列化后 MergeFrom 合并查询总体 P95。
        // sketch 字节无法用 SQL 聚合（需应用层合并），故单独查询所有实例的 bytea 列。
        List<byte[]>? v2InstanceSketches = null;
        List<byte[]>? legacyInstanceSketches = null;
        if (instanceCount > 0)
        {
            await using var sketchCommand = connection.CreateCommand();
            sketchCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            sketchCommand.CommandText = $"""
SELECT v2_latency_sketch, legacy_latency_sketch
FROM {Table("canary_metrics_samples")}
WHERE run_id = @run_id AND stage_epoch = @stage_epoch
  AND (v2_latency_sketch IS NOT NULL OR legacy_latency_sketch IS NOT NULL);
""";
            sketchCommand.Parameters.AddWithValue("run_id", runId);
            sketchCommand.Parameters.AddWithValue("stage_epoch", currentEpoch);

            await using var sketchReader = await sketchCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await sketchReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var v2Ordinal = sketchReader.GetOrdinal("v2_latency_sketch");
                var legacyOrdinal = sketchReader.GetOrdinal("legacy_latency_sketch");

                if (!sketchReader.IsDBNull(v2Ordinal))
                {
                    (v2InstanceSketches ??= new List<byte[]>()).Add(sketchReader.GetFieldValue<byte[]>(v2Ordinal));
                }
                if (!sketchReader.IsDBNull(legacyOrdinal))
                {
                    (legacyInstanceSketches ??= new List<byte[]>()).Add(sketchReader.GetFieldValue<byte[]>(legacyOrdinal));
                }
            }
        }

        return new CanaryAggregatedMetrics
        {
            InstanceCount = checked((int)instanceCount),
            TotalObservations = checked((int)totalObservations),
            DivergenceRate = divergenceRate,
            V2ErrorRate = v2ErrorRate,
            LegacyErrorRate = legacyErrorRate,
            V2P95LatencyMs = v2P95,
            LegacyP95LatencyMs = legacyP95,
            AverageQualityScore = avgQuality,
            ExternalMetrics = externalMetrics,
            CurrentStageEpoch = currentEpoch,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            // sketch 字节列表（null/空 = 无 sketch 数据，Leader 保持加权平均 fallback）
            V2InstanceSketches = v2InstanceSketches,
            LegacyInstanceSketches = legacyInstanceSketches
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// 递增 <c>canary_run_epochs</c> 表中指定 run 的 current_epoch（UPSERT + 原子递增）。
    /// Leader 推进百分比档后调用此方法；所有实例在下一次轮询时检测到 epoch 变化并 Reset 本地 Collector。
    /// fencingToken 非 0 时，UPDATE 追加 EXISTS 子查询校验 <c>canary_leader_leases</c> 中
    /// 仍存在 fencing_token = @fencing_token 的租约。旧 Leader（lease 被抢占后 fencing_token 较小）
    /// 的 UPDATE 影响 0 行，返回 0 表示推进失败。fencingToken = 0（默认）时不做校验（向后兼容）。
    /// </remarks>
    public async ValueTask<long> AdvanceEpochAsync(string runId, CancellationToken cancellationToken = default, long fencingToken = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        // fencingToken > 0 时追加 EXISTS 校验到 INSERT 和 ON CONFLICT DO UPDATE 路径。
        // INSERT ... SELECT WHERE EXISTS 保证首行插入也校验 lease；
        // ON CONFLICT DO UPDATE ... WHERE EXISTS 保证后续递增校验 lease。
        var fenceClause = fencingToken > 0
            ? $@"WHERE EXISTS (
    SELECT 1 FROM {Table("canary_leader_leases")} l
    WHERE l.run_id = @run_id AND l.fencing_token = @fencing_token
)"
            : null;

        command.CommandText = fencingToken > 0
            ? $"""
INSERT INTO {Table("canary_run_epochs")} (run_id, current_epoch, advanced_at)
SELECT @run_id, 1, @now
{fenceClause}
ON CONFLICT (run_id) DO UPDATE SET
    current_epoch = {Table("canary_run_epochs")}.current_epoch + 1,
    advanced_at = EXCLUDED.advanced_at
{fenceClause}
RETURNING current_epoch;
"""
            : $"""
INSERT INTO {Table("canary_run_epochs")} (run_id, current_epoch, advanced_at)
VALUES (@run_id, 1, @now)
ON CONFLICT (run_id) DO UPDATE SET
    current_epoch = {Table("canary_run_epochs")}.current_epoch + 1,
    advanced_at = EXCLUDED.advanced_at
RETURNING current_epoch;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        if (fencingToken > 0)
        {
            command.Parameters.AddWithValue("fencing_token", fencingToken);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // fencing 校验失败时 RETURNING 无行 → result is null → 返回 0 表示推进失败
        return result is long epoch ? epoch : 0L;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 读取 <c>canary_run_epochs</c> 表中指定 run 的 current_epoch。未初始化时返回 0。
    /// </remarks>
    public async ValueTask<long> GetCurrentEpochAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
SELECT current_epoch FROM {Table("canary_run_epochs")}
WHERE run_id = @run_id;
""";
        command.Parameters.AddWithValue("run_id", runId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long epoch ? epoch : 0L;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 删除 <c>canary_metrics_samples</c> 表中 stage_epoch &lt; (current_epoch - keepEpochCount + 1) 的行。
    /// 控制 HA 聚合表的无限增长；保留最近 <paramref name="keepEpochCount"/> 个 epoch 供审计与回溯。
    /// </remarks>
    public async ValueTask<int> PruneOldEpochsAsync(string runId, int keepEpochCount = 2, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (keepEpochCount < 1) keepEpochCount = 1;
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        var currentEpoch = await GetCurrentEpochAsync(runId, cancellationToken).ConfigureAwait(false);
        var cutoffEpoch = currentEpoch - keepEpochCount + 1;
        if (cutoffEpoch <= 0)
        {
            return 0; // 无旧 epoch 需清理
        }

        await using var connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.CommandText = $"""
DELETE FROM {Table("canary_metrics_samples")}
WHERE run_id = @run_id AND stage_epoch < @cutoff_epoch;
""";
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("cutoff_epoch", cutoffEpoch);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter AddNullableDouble(NpgsqlCommand command, string name, double? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlTypes.NpgsqlDbType.Double);
        parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
        return parameter;
    }

    // 可空 long 参数（success count 分母）
    private static NpgsqlParameter AddNullableLong(NpgsqlCommand command, string name, long? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlTypes.NpgsqlDbType.Bigint);
        parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
        return parameter;
    }

    // 可空 byte[] 参数（DDSketch 序列化字节；null/空数组写 NULL）
    private static NpgsqlParameter AddNullableBytes(NpgsqlCommand command, string name, byte[]? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlTypes.NpgsqlDbType.Bytea);
        parameter.Value = (value is null || value.Length == 0) ? DBNull.Value : value;
        return parameter;
    }

    private static NpgsqlParameter AddNullableDateTimeOffset(NpgsqlCommand command, string name, DateTimeOffset? value)
    {
        var parameter = command.Parameters.Add(name, NpgsqlTypes.NpgsqlDbType.TimestampTz);
        parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
        return parameter;
    }

    private static double? ReadNullableDouble(NpgsqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}
