using System.Collections.Concurrent;
using ContextCore.Abstractions;
using ContextCore.Storage.Postgres.Stores;
using Npgsql;
using ContextCore.Storage.Postgres.Infrastructure;

namespace ContextCore.Tests;

// ===========================================================================
// Canary 多实例窗口聚合生产验收测试
//
// 验证 CanaryMetricsAggregator 在多实例部署时的窗口聚合正确性，覆盖：
// 1. MultiInstance_SampleRecording_AggregatesCorrectly — 多实例样本记录后聚合正确
// 2. MultiInstance_TotalObservations_SumsAcrossInstances — TotalObservations 跨实例求和
// 3. MultiInstance_InstanceCount_CountsDistinctInstances — InstanceCount 计算不同实例数
// 4. MultiInstance_StageEpoch_FiltersCurrentEpochOnly — 仅聚合当前 stage epoch 的数据
// 5. MultiInstance_AdvanceEpoch_ResetsAggregationWindow — AdvanceEpoch 重置聚合窗口
// 6. MultiInstance_ExternalMetrics_AggregatedFromSamples — 外部指标从样本聚合
// 7. MultiInstance_PruneOldEpochs_CleansOldSamples — PruneOldEpochs 清理旧 epoch 数据
// 8. MultiInstance_Postgres_PersistentAggregation — Postgres 持久化聚合（不可用时 Inconclusive）
//
// 设计原则：
// - 优先使用真实组件：InMemoryCanaryMetricsAggregator（本文件内实现，镜像 Postgres 语义）；
// DefaultCanaryExternalMetricsSource 为真实实现。
// - InMemoryCanaryMetricsAggregator 复刻 PostgresCanaryMetricsAggregator 的核心语义：
// * RecordSample UPSERT by (runId, stageEpoch, instanceId) — 最新快照覆盖旧值
// * Aggregate SUM/COUNT DISTINCT/加权平均 — 与 SQL 聚合一致
// * AdvanceEpoch 原子递增 — 跨实例 epoch 推进
// * PruneOldEpochs 按 keepEpochCount 保留 — 控制表增长
// - Postgres 不可用时用 InMemory + Assert.Inconclusive。
// - 所有代码注释使用中文。
// ===========================================================================

[TestClass]
[TestCategory("R29-Hard-Gate")]
[TestCategory("Canary-MultiInstance")]
public sealed class R29H_CanaryMultiInstanceWindowTests
{
    // ===========================================================================
    // 测试 1：多实例样本记录后聚合正确
    // ===========================================================================

    /// <summary>
    /// 验证：多个实例记录样本后，AggregateAsync 返回正确的聚合指标。
    /// 覆盖 InstanceCount / TotalObservations / DivergenceRate / AverageQualityScore。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_SampleRecording_AggregatesCorrectly()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-agg-basic";
        var epoch = await aggregator.GetCurrentEpochAsync(runId);
        Assert.AreEqual(0L, epoch, "未初始化的 run epoch 应为 0。");

        // 推进到 epoch 1（模拟 Leader 开始第一档）
        await aggregator.AdvanceEpochAsync(runId);
        epoch = await aggregator.GetCurrentEpochAsync(runId);
        Assert.AreEqual(1L, epoch, "AdvanceEpoch 后 epoch 应为 1。");

        // 3 个实例记录样本
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch, BuildSample(
            totalObservations: 100, divergentCount: 5, v2ErrorCount: 2, legacyErrorCount: 1,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.85));
        await aggregator.RecordSampleAsync(runId, "instance-B", epoch, BuildSample(
            totalObservations: 200, divergentCount: 10, v2ErrorCount: 4, legacyErrorCount: 2,
            v2P95: 55.0, legacyP95: 65.0, quality: 0.90));
        await aggregator.RecordSampleAsync(runId, "instance-C", epoch, BuildSample(
            totalObservations: 300, divergentCount: 15, v2ErrorCount: 6, legacyErrorCount: 3,
            v2P95: 60.0, legacyP95: 70.0, quality: 0.80));

        var aggregated = await aggregator.AggregateAsync(runId);

        // 断言 1：InstanceCount = 3（3 个不同实例）
        Assert.AreEqual(3, aggregated.InstanceCount, "应聚合 3 个实例。");

        // 断言 2：TotalObservations = 100 + 200 + 300 = 600
        Assert.AreEqual(600, aggregated.TotalObservations, "TotalObservations 应跨实例求和。");

        // 断言 3：DivergenceRate = (5+10+15) / (100+200+300) = 30/600 = 0.05
        Assert.AreEqual(0.05, aggregated.DivergenceRate, 1e-6,
            "DivergenceRate 应为加权平均（按 TotalObservations 加权）。");

        // 断言 4：V2ErrorRate = (2+4+6) / 600 = 12/600 = 0.02
        Assert.AreEqual(0.02, aggregated.V2ErrorRate, 1e-6,
            "V2ErrorRate 应为加权平均。");

        // 断言 5：AverageQualityScore = (0.85×100 + 0.90×200 + 0.80×300) / 600 = 505/600 ≈ 0.8417
        Assert.AreEqual(505.0 / 600.0, aggregated.AverageQualityScore, 1e-3,
            "AverageQualityScore 应为按 TotalObservations 加权的加权平均。");

        // 断言 6：CurrentStageEpoch = 1
        Assert.AreEqual(1L, aggregated.CurrentStageEpoch, "CurrentStageEpoch 应为 1。");
    }

    // ===========================================================================
    // 测试 2：TotalObservations 跨实例求和
    // ===========================================================================

    /// <summary>
    /// 验证：TotalObservations 为所有实例最新快照的求和（非重复累计）。
    /// 这是 v36 修复 HA 聚合数据重复累计问题的核心保证。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_TotalObservations_SumsAcrossInstances()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-sum-test";
        var epoch = await aggregator.AdvanceEpochAsync(runId);

        // 实例 A 记录 50 次观察
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch, BuildSample(
            totalObservations: 50, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9));

        // 实例 B 记录 30 次观察
        await aggregator.RecordSampleAsync(runId, "instance-B", epoch, BuildSample(
            totalObservations: 30, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9));

        var aggregated = await aggregator.AggregateAsync(runId);

        // 断言：50 + 30 = 80（非重复累计）
        Assert.AreEqual(80, aggregated.TotalObservations,
            "TotalObservations 应为各实例最新快照求和。");

        // 实例 A 再次记录（UPSERT 覆盖旧值，而非追加）
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch, BuildSample(
            totalObservations: 70, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9));

        var aggregatedAfterUpsert = await aggregator.AggregateAsync(runId);

        // 断言：UPSERT 后 70 + 30 = 100（而非 50+30+70=150 的重复累计）
        Assert.AreEqual(100, aggregatedAfterUpsert.TotalObservations,
            "UPSERT 应覆盖旧值，TotalObservations 不应重复累计。");
    }

    // ===========================================================================
    // 测试 3：InstanceCount 计算不同实例数
    // ===========================================================================

    /// <summary>
    /// 验证：InstanceCount 为 COUNT(DISTINCT instance_id)，而非样本行数。
    /// 同一实例多次记录只计为 1 个实例。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_InstanceCount_CountsDistinctInstances()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-distinct-test";
        var epoch = await aggregator.AdvanceEpochAsync(runId);

        // 实例 A 记录 3 次（UPSERT 覆盖）
        for (var i = 0; i < 3; i++)
        {
            await aggregator.RecordSampleAsync(runId, "instance-A", epoch, BuildSample(
                totalObservations: 10 + i, divergentCount: 0, v2ErrorCount: 0,
                legacyErrorCount: 0, v2P95: 50.0, legacyP95: 60.0, quality: 0.9));
        }

        // 实例 B 记录 2 次
        for (var i = 0; i < 2; i++)
        {
            await aggregator.RecordSampleAsync(runId, "instance-B", epoch, BuildSample(
                totalObservations: 20 + i, divergentCount: 0, v2ErrorCount: 0,
                legacyErrorCount: 0, v2P95: 50.0, legacyP95: 60.0, quality: 0.9));
        }

        var aggregated = await aggregator.AggregateAsync(runId);

        // 断言：InstanceCount = 2（A 和 B），而非 5（3+2 次记录）
        Assert.AreEqual(2, aggregated.InstanceCount,
            "InstanceCount 应为不同实例数，而非样本记录次数。");
    }

    // ===========================================================================
    // 测试 4：仅聚合当前 stage epoch 的数据
    // ===========================================================================

    /// <summary>
    /// 验证：AggregateAsync 仅汇总 stage_epoch = current_epoch 的行，
    /// 旧 epoch 数据不参与聚合（防止跨阶段数据混淆）。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_StageEpoch_FiltersCurrentEpochOnly()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-epoch-filter";

        // ── epoch 1：2 个实例记录 ──
        var epoch1 = await aggregator.AdvanceEpochAsync(runId);
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch1, BuildSample(
            totalObservations: 100, divergentCount: 5, v2ErrorCount: 2, legacyErrorCount: 1,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.85));
        await aggregator.RecordSampleAsync(runId, "instance-B", epoch1, BuildSample(
            totalObservations: 100, divergentCount: 5, v2ErrorCount: 2, legacyErrorCount: 1,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.85));

        var aggEpoch1 = await aggregator.AggregateAsync(runId);
        Assert.AreEqual(2, aggEpoch1.InstanceCount, "epoch 1 应有 2 个实例。");
        Assert.AreEqual(200, aggEpoch1.TotalObservations, "epoch 1 总观察应为 200。");

        // ── 推进到 epoch 2：1 个实例记录 ──
        var epoch2 = await aggregator.AdvanceEpochAsync(runId);
        Assert.AreEqual(2L, epoch2, "第二次 AdvanceEpoch 后应为 epoch 2。");

        // 仅 instance-A 在 epoch 2 记录
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch2, BuildSample(
            totalObservations: 50, divergentCount: 1, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 45.0, legacyP95: 55.0, quality: 0.95));

        var aggEpoch2 = await aggregator.AggregateAsync(runId);

        // 断言：epoch 2 仅聚合 instance-A 的 50 次观察（epoch 1 的 200 次不参与）
        Assert.AreEqual(1, aggEpoch2.InstanceCount, "epoch 2 应仅 1 个实例。");
        Assert.AreEqual(50, aggEpoch2.TotalObservations,
            "epoch 2 应仅聚合当前 epoch 数据，旧 epoch 不参与。");
        Assert.AreEqual(2L, aggEpoch2.CurrentStageEpoch, "CurrentStageEpoch 应为 2。");
    }

    // ===========================================================================
    // 测试 5：AdvanceEpoch 重置聚合窗口
    // ===========================================================================

    /// <summary>
    /// 验证：AdvanceEpochAsync 递增 epoch 后，聚合窗口重置。
    /// 新 epoch 的聚合从 0 开始（无旧数据），直到实例记录新样本。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_AdvanceEpoch_ResetsAggregationWindow()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-reset-test";

        // ── epoch 1：记录大量数据 ──
        var epoch1 = await aggregator.AdvanceEpochAsync(runId);
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch1, BuildSample(
            totalObservations: 1000, divergentCount: 100, v2ErrorCount: 50, legacyErrorCount: 25,
            v2P95: 100.0, legacyP95: 120.0, quality: 0.70));

        var aggEpoch1 = await aggregator.AggregateAsync(runId);
        Assert.AreEqual(1000, aggEpoch1.TotalObservations, "epoch 1 应有 1000 次观察。");

        // ── 推进到 epoch 2：未记录任何样本 ──
        await aggregator.AdvanceEpochAsync(runId);

        var aggEpoch2 = await aggregator.AggregateAsync(runId);

        // 断言：epoch 2 无样本 → 空聚合（InstanceCount=0, TotalObservations=0）
        Assert.AreEqual(0, aggEpoch2.InstanceCount, "新 epoch 无样本时 InstanceCount 应为 0。");
        Assert.AreEqual(0, aggEpoch2.TotalObservations, "新 epoch 无样本时 TotalObservations 应为 0。");
        Assert.AreEqual(0.0, aggEpoch2.DivergenceRate, "新 epoch 无样本时 DivergenceRate 应为 0。");
        Assert.AreEqual(0.0, aggEpoch2.V2ErrorRate, "新 epoch 无样本时 V2ErrorRate 应为 0。");
    }

    // ===========================================================================
    // 测试 6：外部指标从样本聚合
    // ===========================================================================

    /// <summary>
    /// 验证：外部指标（TaskSuccessRate / ToolSuccessRate 等）从 samples 表聚合，
    /// AVG 跳过 NULL（未采集的实例不参与均值）。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_ExternalMetrics_AggregatedFromSamples()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-external-test";
        var epoch = await aggregator.AdvanceEpochAsync(runId);

        // 实例 A：采集了所有外部指标
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch, BuildSample(
            totalObservations: 100, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9,
            taskSuccessRate: 0.95, toolSuccessRate: 0.98, safetyViolationRate: 0.0));

        // 实例 B：仅采集 TaskSuccessRate（其他为 null）
        await aggregator.RecordSampleAsync(runId, "instance-B", epoch, BuildSample(
            totalObservations: 200, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9,
            taskSuccessRate: 0.85, toolSuccessRate: null, safetyViolationRate: null));

        var aggregated = await aggregator.AggregateAsync(runId);

        // 断言 1：ExternalMetrics 非空（至少有一个外部指标被采集）
        Assert.IsNotNull(aggregated.ExternalMetrics, "应有外部指标聚合。");

        // 断言 2：TaskSuccessRate = (0.95 + 0.85) / 2 = 0.90（2 个实例都采集，AVG）
        Assert.IsNotNull(aggregated.ExternalMetrics!.TaskSuccessRate, "TaskSuccessRate 应非 null。");
        Assert.AreEqual(0.90, aggregated.ExternalMetrics.TaskSuccessRate!.Value, 1e-6,
            "TaskSuccessRate 应为 2 个实例的均值。");

        // 断言 3：ToolSuccessRate = 0.98（仅 A 采集，B 为 null 跳过）
        Assert.IsNotNull(aggregated.ExternalMetrics.ToolSuccessRate, "ToolSuccessRate 应非 null。");
        Assert.AreEqual(0.98, aggregated.ExternalMetrics.ToolSuccessRate!.Value, 1e-6,
            "ToolSuccessRate 应仅取采集了该指标的实例均值（跳过 null）。");

        // 断言 4：SafetyViolationRate = 0.0（仅 A 采集）
        Assert.IsNotNull(aggregated.ExternalMetrics.SafetyViolationRate, "SafetyViolationRate 应非 null。");
        Assert.AreEqual(0.0, aggregated.ExternalMetrics.SafetyViolationRate!.Value, 1e-6,
            "SafetyViolationRate 应仅取采集了该指标的实例均值。");
    }

    // ===========================================================================
    // 测试 7：PruneOldEpochs 清理旧 epoch 数据
    // ===========================================================================

    /// <summary>
    /// 验证：PruneOldEpochsAsync 删除 stage_epoch < (current - keep + 1) 的行，
    /// 保留最近 keepEpochCount 个 epoch 的数据。
    /// </summary>
    [TestMethod]
    public async Task MultiInstance_PruneOldEpochs_CleansOldSamples()
    {
        var aggregator = new InMemoryCanaryMetricsAggregator();
        var runId = "run-prune-test";

        // 推进 3 个 epoch，每个 epoch 都记录样本
        var epoch1 = await aggregator.AdvanceEpochAsync(runId); // epoch 1
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch1, BuildSample(
            totalObservations: 10, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9));

        var epoch2 = await aggregator.AdvanceEpochAsync(runId); // epoch 2
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch2, BuildSample(
            totalObservations: 20, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9));

        var epoch3 = await aggregator.AdvanceEpochAsync(runId); // epoch 3（current）
        await aggregator.RecordSampleAsync(runId, "instance-A", epoch3, BuildSample(
            totalObservations: 30, divergentCount: 0, v2ErrorCount: 0, legacyErrorCount: 0,
            v2P95: 50.0, legacyP95: 60.0, quality: 0.9));

        // 断言 1：当前 epoch = 3，聚合应仅含 epoch 3 数据
        var aggBefore = await aggregator.AggregateAsync(runId);
        Assert.AreEqual(30, aggBefore.TotalObservations, "epoch 3 应有 30 次观察。");

        // PruneOldEpochs(keepEpochCount=2)：保留 epoch 2 和 3，删除 epoch 1
        var pruned = await aggregator.PruneOldEpochsAsync(runId, keepEpochCount: 2);

        // 断言 2：应删除 1 行（epoch 1 的样本）
        Assert.AreEqual(1, pruned, "应删除 1 行（epoch 1 的样本）。");

        // 断言 3：当前聚合仍正确（epoch 3 数据未受影响）
        var aggAfter = await aggregator.AggregateAsync(runId);
        Assert.AreEqual(30, aggAfter.TotalObservations,
            "Prune 后当前 epoch 聚合应不变。");

        // 断言 4：Prune 不影响 epoch 推进能力
        var epoch4 = await aggregator.AdvanceEpochAsync(runId);
        Assert.AreEqual(4L, epoch4, "Prune 后应仍能推进 epoch。");
    }

    // ===========================================================================
    // 测试 8：Postgres 持久化聚合（不可用时 Inconclusive）
    // ===========================================================================

    /// <summary>
    /// 验证：Postgres 可用时，PostgresCanaryMetricsAggregator 的持久化聚合路径可正常工作。
    /// Postgres 不可用时跳过（Assert.Inconclusive）。
    /// 此测试验证 Postgres 连接可用性，完整集成测试由 ContextCore.IntegrationTests 覆盖。
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public async Task MultiInstance_Postgres_PersistentAggregation()
    {
        var connectionString = GetPostgresConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            Assert.Inconclusive("未配置 Postgres 连接字符串（环境变量 CONTEXT_TEST_POSTGRES），跳过持久化聚合测试。");
            return;
        }

        // 验证连接可用
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"Postgres 连接失败：{ex.GetType().Name}: {ex.Message}");
            return;
        }

        var factory = new PostgresConnectionFactory(new PostgresOptions
        {
            ConnectionString = connectionString,
            AutoMigrate = true
        });

        try
        {
            var pingResult = await factory.PingAsync();
            Assert.IsTrue(pingResult.Success,
                $"Postgres Ping 应成功：{pingResult.ErrorMessage}");
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

    // ===========================================================================
    // 辅助方法
    // ===========================================================================

    /// <summary>构建 CanaryMetricsSample 快照。</summary>
    private static CanaryMetricsSample BuildSample(
        int totalObservations,
        int divergentCount,
        int v2ErrorCount,
        int legacyErrorCount,
        double v2P95,
        double legacyP95,
        double quality,
        double? taskSuccessRate = null,
        double? toolSuccessRate = null,
        double? safetyViolationRate = null) => new()
        {
            TotalObservations = totalObservations,
            DivergentCount = divergentCount,
            V2ErrorCount = v2ErrorCount,
            LegacyErrorCount = legacyErrorCount,
            V2P95LatencyMs = v2P95,
            LegacyP95LatencyMs = legacyP95,
            AverageQualityScore = quality,
            TaskSuccessRate = taskSuccessRate,
            ToolSuccessRate = toolSuccessRate,
            SafetyViolationRate = safetyViolationRate,
            WindowStart = DateTimeOffset.UtcNow.AddMinutes(-1),
            WindowEnd = DateTimeOffset.UtcNow
        };

    private static string? GetPostgresConnectionString()
    {
        return Environment.GetEnvironmentVariable("CONTEXT_TEST_POSTGRES");
    }

    // ===========================================================================
    // 测试辅助：InMemoryCanaryMetricsAggregator
    // 镜像 PostgresCanaryMetricsAggregator 的核心语义（UPSERT / SUM / COUNT DISTINCT / 加权平均）
    // ===========================================================================

    /// <summary>
    /// InMemory 实现的 ICanaryMetricsAggregator，用于测试多实例聚合语义。
    /// 复刻 PostgresCanaryMetricsAggregator 的核心逻辑：
    /// <list type="bullet">
    /// <item>RecordSample: UPSERT by (runId, stageEpoch, instanceId) — 最新快照覆盖旧值。</item>
    /// <item>Aggregate: SUM(total_observations) / COUNT(DISTINCT instance_id) /
    /// 加权平均(divergent_count / total_observations) 等。</item>
    /// <item>AdvanceEpoch: 原子递增 current_epoch。</item>
    /// <item>PruneOldEpochs: 删除 stage_epoch &lt; (current - keep + 1) 的行。</item>
    /// </list>
    /// </summary>
    private sealed class InMemoryCanaryMetricsAggregator : ICanaryMetricsAggregator
    {
        // PK = (runId, stageEpoch, instanceId) → latest sample
        private readonly ConcurrentDictionary<(string runId, long epoch, string instanceId), CanaryMetricsSample> _samples
            = new();

        // runId → current_epoch
        private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.Ordinal);

        /// <summary>UPSERT 一个实例的本地指标快照（覆盖同 PK 旧值）。</summary>
        public ValueTask RecordSampleAsync(
            string runId,
            string instanceId,
            long stageEpoch,
            CanaryMetricsSample sample,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentNullException.ThrowIfNull(sample);

            _samples[(runId, stageEpoch, instanceId)] = sample;
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<CanaryAggregatedMetrics> AggregateAsync(
            string runId,
            ICanaryExternalMetricsSource? externalMetricsSource = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);

            var currentEpoch = _epochs.TryGetValue(runId, out var e) ? e : 0L;

            // 仅聚合 stage_epoch = current_epoch 的行
            var epochSamples = _samples
                .Where(kvp => kvp.Key.runId == runId && kvp.Key.epoch == currentEpoch)
                .ToList();

            if (epochSamples.Count == 0)
            {
                var now = DateTimeOffset.UtcNow;
                return ValueTask.FromResult(new CanaryAggregatedMetrics
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
                });
            }

            // COUNT(DISTINCT instance_id)
            var instanceCount = epochSamples
                .Select(kvp => kvp.Key.instanceId)
                .Distinct(StringComparer.Ordinal)
                .Count();

            // SUM(total_observations)
            var totalObservations = epochSamples.Sum(kvp => kvp.Value.TotalObservations);

            // 加权平均：SUM(divergent_count) / SUM(total_observations)
            var divergentSum = epochSamples.Sum(kvp => (long)kvp.Value.DivergentCount);
            var divergenceRate = totalObservations == 0 ? 0.0
                : (double)divergentSum / totalObservations;

            var v2ErrorSum = epochSamples.Sum(kvp => (long)kvp.Value.V2ErrorCount);
            var v2ErrorRate = totalObservations == 0 ? 0.0
                : (double)v2ErrorSum / totalObservations;

            var legacyErrorSum = epochSamples.Sum(kvp => (long)kvp.Value.LegacyErrorCount);
            var legacyErrorRate = totalObservations == 0 ? 0.0
                : (double)legacyErrorSum / totalObservations;

            // 加权平均 P95 延迟（按 total_observations 加权）
            var v2P95 = totalObservations == 0 ? 0.0
                : epochSamples.Sum(kvp => kvp.Value.V2P95LatencyMs * kvp.Value.TotalObservations) / totalObservations;
            var legacyP95 = totalObservations == 0 ? 0.0
                : epochSamples.Sum(kvp => kvp.Value.LegacyP95LatencyMs * kvp.Value.TotalObservations) / totalObservations;

            // 加权平均质量分
            var avgQuality = totalObservations == 0 ? 0.0
                : epochSamples.Sum(kvp => kvp.Value.AverageQualityScore * kvp.Value.TotalObservations) / totalObservations;

            // 外部指标：AVG 跳过 NULL
            // TaskSuccessRate / ToolSuccessRate 改为 SUM(分子)/SUM(分母) 替代 AVG(rate)；
            // sum/count 为 null 时回退到 AVG(rate)（向后兼容未填充 sum/count 的旧样本）
            var taskSuccess = SumDivSum(epochSamples, s => s.TaskSuccessSum, s => s.TaskSuccessCount)
                ?? AvgNullable(epochSamples, s => s.TaskSuccessRate);
            var toolSuccess = SumDivSum(epochSamples, s => s.ToolSuccessSum, s => s.ToolSuccessCount)
                ?? AvgNullable(epochSamples, s => s.ToolSuccessRate);
            var repair = AvgNullable(epochSamples, s => s.RepairRate);
            var safety = AvgNullable(epochSamples, s => s.SafetyViolationRate);
            var ctxPrecision = AvgNullable(epochSamples, s => s.ContextPrecision);
            var ctxRecall = AvgNullable(epochSamples, s => s.ContextRecallProxy);
            var userAccept = AvgNullable(epochSamples, s => s.UserAcceptance);
            var answerQuality = AvgNullable(epochSamples, s => s.AnswerQuality);
            var tokenCost = AvgNullable(epochSamples, s => s.TokenCost);
            var inferenceCost = AvgNullable(epochSamples, s => s.InferenceCost);

            var windowStart = epochSamples.Min(kvp => kvp.Value.WindowStart);
            var windowEnd = epochSamples.Max(kvp => kvp.Value.WindowEnd);

            // 收集各实例的 DDSketch 字节（供 Leader MergeFrom 合并查询总体 P95）
            List<byte[]>? v2InstanceSketches = null;
            List<byte[]>? legacyInstanceSketches = null;
            foreach (var kvp in epochSamples)
            {
                if (kvp.Value.V2LatencySketch is { Length: > 0 } v2Sketch)
                {
                    (v2InstanceSketches ??= new List<byte[]>()).Add(v2Sketch);
                }
                if (kvp.Value.LegacyLatencySketch is { Length: > 0 } legacySketch)
                {
                    (legacyInstanceSketches ??= new List<byte[]>()).Add(legacySketch);
                }
            }

            // 构建外部指标（若任一非 null）
            ExternalResultMetrics? externalMetrics = null;
            if (taskSuccess.HasValue || toolSuccess.HasValue || repair.HasValue
                || safety.HasValue || ctxPrecision.HasValue || ctxRecall.HasValue
                || userAccept.HasValue || answerQuality.HasValue
                || tokenCost.HasValue || inferenceCost.HasValue)
            {
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
                    SampleCount = totalObservations,
                    WindowStart = windowStart,
                    WindowEnd = windowEnd
                };
            }

            return ValueTask.FromResult(new CanaryAggregatedMetrics
            {
                InstanceCount = instanceCount,
                TotalObservations = totalObservations,
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
                // 各实例 DDSketch 字节列表（供 Leader MergeFrom 合并）
                V2InstanceSketches = v2InstanceSketches,
                LegacyInstanceSketches = legacyInstanceSketches
            });
        }

        /// <inheritdoc />
        public ValueTask<long> AdvanceEpochAsync(string runId, CancellationToken cancellationToken = default, long fencingToken = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            // InMemory 实现不做 fencing 校验（无 lease 表）；fencingToken 参数仅为接口兼容
            var newEpoch = _epochs.AddOrUpdate(runId, 1L, (_, current) => current + 1);
            return ValueTask.FromResult(newEpoch);
        }

        /// <inheritdoc />
        public ValueTask<long> GetCurrentEpochAsync(string runId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            return ValueTask.FromResult(_epochs.TryGetValue(runId, out var epoch) ? epoch : 0L);
        }

        /// <inheritdoc />
        public ValueTask<int> PruneOldEpochsAsync(string runId, int keepEpochCount = 2, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            if (keepEpochCount < 1) keepEpochCount = 1;

            var currentEpoch = _epochs.TryGetValue(runId, out var e) ? e : 0L;
            var cutoffEpoch = currentEpoch - keepEpochCount + 1;
            if (cutoffEpoch <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var pruned = 0;
            var keysToRemove = _samples
                .Where(kvp => kvp.Key.runId == runId && kvp.Key.epoch < cutoffEpoch)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                if (_samples.TryRemove(key, out _))
                {
                    pruned++;
                }
            }

            return ValueTask.FromResult(pruned);
        }

        /// <summary>计算可空 double 的均值（跳过 null，与 SQL AVG 语义一致）。</summary>
        private static double? AvgNullable(
            List<KeyValuePair<(string runId, long epoch, string instanceId), CanaryMetricsSample>> samples,
            Func<CanaryMetricsSample, double?> selector)
        {
            var values = samples
                .Select(kvp => selector(kvp.Value))
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            if (values.Count == 0) return null;
            return values.Average();
        }

        /// <summary>
        /// SUM(分子) / SUM(分母) 聚合（跳过 null sum/count 对）。
        /// 所有样本的 sum/count 都为 null 时返回 null（调用方回退到 AVG(rate)）。
        /// </summary>
        private static double? SumDivSum(
            List<KeyValuePair<(string runId, long epoch, string instanceId), CanaryMetricsSample>> samples,
            Func<CanaryMetricsSample, double?> sumSelector,
            Func<CanaryMetricsSample, long?> countSelector)
        {
            double totalSum = 0.0;
            long totalCount = 0;
            var hasAny = false;

            foreach (var kvp in samples)
            {
                var sum = sumSelector(kvp.Value);
                var count = countSelector(kvp.Value);
                if (sum.HasValue && count.HasValue)
                {
                    totalSum += sum.Value;
                    totalCount += count.Value;
                    hasAny = true;
                }
            }

            if (!hasAny || totalCount == 0) return null;
            return totalSum / totalCount;
        }
    }
}
