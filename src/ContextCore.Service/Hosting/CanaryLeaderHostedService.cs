using ContextCore.Abstractions;
using ContextCore.Core.Services.Evolution;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ContextCore.Service.Hosting;

/// <summary>
/// 任务 D：Canary Leader 选举后台服务（HA 模式）。
/// </summary>
/// <remarks>
/// 包装 <see cref="CanaryProgressionService"/> 的 leader 选举层，确保多实例部署时
/// 同一 Canary run 同一时刻仅由一个 leader 实例推进/回滚。
///
/// <b>运行模式</b>：
/// <list type="bullet">
/// <item><see cref="CanaryLeaderOptions.Enabled"/> = false：立即退出（单节点模式，
///   由 <see cref="CanaryProgressionHostedService"/> 处理）。</item>
/// <item><see cref="CanaryLeaderOptions.Enabled"/> = true：周期性轮询所有 ScopedCanary run，
///   每个实例都记录本地指标样本到共享表；仅 leader 实例聚合跨实例指标并驱动推进/回滚。</item>
/// </list>
///
/// <b>Leader 选举流程</b>（per run）：
/// <code>
/// TryAcquire(runId, leaseDuration, owner)
///   ├─ 成功 → leader：记录样本 → 聚合 → 评估 → 推进/回滚 → 续租
///   └─ 失败 → 非 leader：仅记录本地样本（供 leader 聚合）
/// Renew(runId, token, extension) → 续租；失败则放弃 leader 身份
/// Release(runId, token) → run 终态时主动释放
/// </code>
///
/// <b>与 <see cref="CanaryProgressionHostedService"/> 的关系</b>：
/// 两者互斥注册。HA 部署注册本服务；单节点部署注册 <see cref="CanaryProgressionHostedService"/>。
/// 本服务复用 <see cref="CanaryProgressionService"/> 做评估与推进，仅在外层添加租约与聚合。
/// </remarks>
internal sealed class CanaryLeaderHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ICanaryMetricsCollector _metricsCollector;
    private readonly CanaryProgressionService _progressionService;
    private readonly ICanaryLeaderLease _leaderLease;
    private readonly ICanaryMetricsAggregator _metricsAggregator;
    private readonly IOptions<CanaryLeaderOptions> _options;
    private readonly ILogger<CanaryLeaderHostedService> _logger;
    private readonly string _instanceId;
    private readonly TimeProvider _timeProvider;

    // 当前持有的租约（runId → lease token），用于续租与释放
    private readonly Dictionary<string, string> _heldLeases = new(StringComparer.Ordinal);

    public CanaryLeaderHostedService(
        IServiceProvider services,
        ICanaryMetricsCollector metricsCollector,
        CanaryProgressionService progressionService,
        ICanaryLeaderLease leaderLease,
        ICanaryMetricsAggregator metricsAggregator,
        IOptions<CanaryLeaderOptions> options,
        ILogger<CanaryLeaderHostedService> logger)
    {
        _services = services;
        _metricsCollector = metricsCollector;
        _progressionService = progressionService;
        _leaderLease = leaderLease;
        _metricsAggregator = metricsAggregator;
        _options = options;
        _logger = logger;
        _timeProvider = TimeProvider.System;

        var owner = options.Value.Owner;
        _instanceId = string.IsNullOrWhiteSpace(owner)
            ? $"host-{Environment.MachineName}-{Guid.NewGuid():N}".Substring(0, 48)
            : owner;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("CanaryLeaderHostedService 已禁用（CanaryLeaderOptions.Enabled=false）；单节点模式由 CanaryProgressionHostedService 处理。");
            return;
        }

        _logger.LogInformation(
            "CanaryLeaderHostedService 启动：InstanceId={InstanceId}, RenewInterval={Renew}, LeaseDuration={Lease}, ReapInterval={Reap}.",
            _instanceId, options.RenewInterval, options.LeaseDuration, options.ReapInterval);

        var reapStopwatch = Stopwatch.StartNew();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 周期性回收过期租约（清理崩溃 leader 持有的租约）
                if (reapStopwatch.Elapsed >= options.ReapInterval)
                {
                    reapStopwatch.Restart();
                    var reaped = await _leaderLease.ReapExpiredAsync(stoppingToken).ConfigureAwait(false);
                    if (reaped > 0)
                    {
                        _logger.LogInformation("Canary leader reaper 回收了 {Count} 个过期租约。", reaped);
                    }
                }

                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CanaryLeaderHostedService 轮询循环异常（不中断后续轮询）。");
            }

            try
            {
                await Task.Delay(options.RenewInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        // 关闭时主动释放所有持有的租约
        await ReleaseAllLeasesAsync(stoppingToken).ConfigureAwait(false);
        _logger.LogInformation("CanaryLeaderHostedService 已停止。");
    }

    /// <summary>
    /// 执行一次轮询：列出所有 ScopedCanary 阶段的 run，记录样本 + leader 处理。
    /// </summary>
    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetService<IPipelineRunStore>();
        if (store is null)
        {
            _logger.LogDebug("IPipelineRunStore 未注册；跳过 Canary leader 轮询。");
            return;
        }

        // 尝试获取 PostgresCanaryMetricsAggregator 以记录本地样本
        // （RecordSampleAsync 是具体类方法，不在 ICanaryMetricsAggregator 接口上）
        var postgresAggregator = scope.ServiceProvider.GetService<PostgresCanaryMetricsAggregator>();

        var runs = await store.ListRunsByStageAsync(
            OptimizationStage.ScopedCanary, take: 100, cancellationToken).ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return;
        }

        foreach (var run in runs)
        {
            if (IsTerminal(run.Status))
            {
                // 终态 run：释放持有的租约（若持有）
                await ReleaseLeaseIfHeldAsync(run.RunId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ProcessRunAsync(run.RunId, postgresAggregator, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理 canary run {RunId} 时发生异常；跳过本次轮询。", run.RunId);
            }
        }
    }

    /// <summary>
    /// 处理单个 canary run：记录本地样本 + 尝试获取/续租 leader 租约 + 聚合评估。
    /// </summary>
    private async Task ProcessRunAsync(
        string runId,
        PostgresCanaryMetricsAggregator? postgresAggregator,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;

        // 1. 所有实例都记录本地指标样本（供 leader 聚合）
        var localMetrics = _metricsCollector.GetAggregatedMetrics(runId);
        if (postgresAggregator is not null && localMetrics.TotalObservations > 0)
        {
            var sample = ToSample(localMetrics);
            try
            {
                await postgresAggregator.RecordSampleAsync(
                    runId, _instanceId, sample, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "记录 canary run {RunId} 的本地指标样本失败；继续处理。", runId);
            }
        }

        // 2. Leader 选举：尝试获取或续租
        var isLeader = await TryAcquireOrRenewAsync(runId, options, cancellationToken).ConfigureAwait(false);
        if (!isLeader)
        {
            return; // 非 leader：仅记录样本，不驱动推进
        }

        // 3. Leader：聚合跨实例指标 + 评估 + 推进/回滚
        try
        {
            var aggregated = await _metricsAggregator.AggregateAsync(
                runId, externalMetricsSource: null, cancellationToken).ConfigureAwait(false);

            // 聚合指标为 0 样本时（无实例上报），跳过评估避免误判
            if (aggregated.InstanceCount == 0 || aggregated.TotalObservations == 0)
            {
                _logger.LogDebug("Canary run {RunId} 聚合样本为空（InstanceCount=0）；跳过评估。", runId);
                return;
            }

            var baselineMetrics = ToBaselineMetrics(aggregated);
            var experimentMetrics = ToExperimentMetrics(aggregated);

            var evaluation = await _progressionService.EvaluateAsync(
                runId, baselineMetrics, experimentMetrics, cancellationToken).ConfigureAwait(false);

            switch (evaluation.Decision)
            {
                case CanaryProgressionDecision.Advance:
                {
                    var transitionId = $"adv-{runId}-{_timeProvider.GetUtcNow().UtcDateTime.Ticks}";
                    var result = await _progressionService.AdvanceAsync(
                        runId, transitionId, idempotencyKey: null,
                        baselineMetrics, experimentMetrics, cancellationToken).ConfigureAwait(false);

                    if (result.Applied)
                    {
                        _metricsCollector.Reset(runId);
                        _logger.LogInformation(
                            "Canary run {RunId} 推进（leader={Owner}）：{Prev}% → {Curr}%。",
                            runId, _instanceId, result.PreviousPercentage, result.CurrentPercentage);
                    }
                    break;
                }

                case CanaryProgressionDecision.Rollback:
                {
                    await _progressionService.RollbackAsync(
                        runId,
                        evaluation.RollbackReason ?? RollbackReason.ModelPerformanceRegression,
                        cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Canary run {RunId} 自动回滚（leader={Owner}）：{Reason}（{Rationale}）。",
                        runId, _instanceId, evaluation.RollbackReason, evaluation.Rationale);
                    break;
                }

                case CanaryProgressionDecision.Promoted:
                    _logger.LogInformation(
                        "Canary run {RunId} 已晋升到 100%（V2 only，leader={Owner}）。", runId, _instanceId);
                    break;

                case CanaryProgressionDecision.Hold:
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Canary run {RunId} leader 处理异常（leader={Owner}）。", runId, _instanceId);
        }
    }

    /// <summary>
    /// 尝试获取新租约或续租现有租约。
    /// </summary>
    /// <returns>true = 当前是 leader；false = 非 leader。</returns>
    private async Task<bool> TryAcquireOrRenewAsync(
        string runId,
        CanaryLeaderOptions options,
        CancellationToken cancellationToken)
    {
        // 已持有租约：尝试续租
        if (_heldLeases.TryGetValue(runId, out var existingToken))
        {
            var renewed = await _leaderLease.RenewAsync(
                runId, existingToken, options.LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (renewed)
            {
                return true;
            }

            // 续租失败：租约已丢失（被抢占或过期），清除本地记录
            _heldLeases.Remove(runId);
            _logger.LogWarning(
                "Canary run {RunId} 续租失败（leader={Owner}）；放弃 leader 身份。", runId, _instanceId);
        }

        // 未持有租约：尝试获取
        var leased = await _leaderLease.TryAcquireAsync(
            runId, options.LeaseDuration, _instanceId, cancellationToken).ConfigureAwait(false);
        if (leased is null)
        {
            return false; // 已被其他实例持有
        }

        _heldLeases[runId] = leased.LeaseToken;
        _logger.LogInformation(
            "Canary run {RunId} 获取 leader 租约（owner={Owner}, expiresAt={ExpiresAt}）。",
            runId, _instanceId, leased.ExpiresAt);
        return true;
    }

    /// <summary>释放指定 run 的租约（若持有）。</summary>
    private async Task ReleaseLeaseIfHeldAsync(string runId, CancellationToken cancellationToken)
    {
        if (!_heldLeases.TryGetValue(runId, out var token))
        {
            return;
        }

        try
        {
            await _leaderLease.ReleaseAsync(runId, token, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Canary run {RunId} 释放 leader 租约（owner={Owner}）。", runId, _instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放 canary run {RunId} 的租约失败。", runId);
        }
        finally
        {
            _heldLeases.Remove(runId);
        }
    }

    /// <summary>关闭时释放所有持有的租约。</summary>
    private async Task ReleaseAllLeasesAsync(CancellationToken cancellationToken)
    {
        foreach (var runId in _heldLeaseKeysSnapshot())
        {
            await ReleaseLeaseIfHeldAsync(runId, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<string> _heldLeaseKeysSnapshot()
    {
        // BackgroundService.ExecuteAsync 单线程执行，无需锁保护。
        return new List<string>(_heldLeases.Keys);
    }

    private static bool IsTerminal(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Promoted => true,
        PipelineRunStatus.RolledBack => true,
        PipelineRunStatus.Rejected => true,
        PipelineRunStatus.Cancelled => true,
        PipelineRunStatus.Failed => true,
        _ => false
    };

    /// <summary>将 <see cref="CanaryObservationMetrics"/> 转换为存储层 <see cref="CanaryMetricsSample"/>。</summary>
    private static CanaryMetricsSample ToSample(CanaryObservationMetrics m) => new()
    {
        TotalObservations = m.TotalObservations,
        DivergentCount = m.DivergentCount,
        V2ErrorCount = m.V2ErrorCount,
        LegacyErrorCount = m.LegacyErrorCount,
        V2P95LatencyMs = m.V2P95LatencyMs,
        LegacyP95LatencyMs = m.LegacyP95LatencyMs,
        AverageQualityScore = m.AverageQualityScore,
        TaskSuccessRate = m.TaskSuccessRate,
        ToolSuccessRate = m.ToolSuccessRate,
        RepairRate = m.RepairRate,
        SafetyViolationRate = m.SafetyViolationRate,
        ContextPrecision = m.ContextPrecision,
        ContextRecallProxy = m.ContextRecallProxy,
        UserAcceptance = m.UserAcceptance,
        AnswerQuality = m.AnswerQuality,
        TokenCost = m.TokenCost,
        InferenceCost = m.InferenceCost,
        WindowStart = m.WindowStart,
        WindowEnd = m.WindowEnd
    };

    /// <summary>将聚合指标转换为基线（Legacy 路径）指标字典。</summary>
    /// <remarks>
    /// <see cref="CanaryAggregatedMetrics"/> 合约未包含 LegacyP95LatencyMs 字段（仅含 V2P95LatencyMs）。
    /// 此处 baseline p95 用 0.0 占位：CanaryProgressionService 的 latency multiplier 比较时
    /// V2 p95 / 0 会触发除零保护（通常回退到 1.0 倍率），不会误触发回滚。
    /// 若未来合约补充 LegacyP95LatencyMs，应替换为真实值。
    /// </remarks>
    private static IReadOnlyDictionary<string, double> ToBaselineMetrics(CanaryAggregatedMetrics m)
        => new Dictionary<string, double>
        {
            ["error_rate"] = m.LegacyErrorRate,
            ["p95_latency_ms"] = 0.0
        };

    /// <summary>将聚合指标转换为实验路径（V2 路径）指标字典。</summary>
    private static IReadOnlyDictionary<string, double> ToExperimentMetrics(CanaryAggregatedMetrics m)
    {
        var dict = new Dictionary<string, double>
        {
            ["error_rate"] = m.V2ErrorRate,
            ["p95_latency_ms"] = m.V2P95LatencyMs,
            ["divergence_rate"] = m.DivergenceRate,
            ["quality_score"] = m.AverageQualityScore
        };

        if (m.ExternalMetrics is { } ext)
        {
            if (ext.TaskSuccessRate.HasValue) dict["task_success_rate"] = ext.TaskSuccessRate.Value;
            if (ext.ToolSuccessRate.HasValue) dict["tool_success_rate"] = ext.ToolSuccessRate.Value;
            if (ext.RepairRate.HasValue) dict["repair_rate"] = ext.RepairRate.Value;
            if (ext.SafetyViolationRate.HasValue) dict["safety_violation_rate"] = ext.SafetyViolationRate.Value;
            if (ext.ContextPrecision.HasValue) dict["context_precision"] = ext.ContextPrecision.Value;
            if (ext.ContextRecallProxy.HasValue) dict["context_recall_proxy"] = ext.ContextRecallProxy.Value;
            if (ext.UserAcceptance.HasValue) dict["user_acceptance"] = ext.UserAcceptance.Value;
            if (ext.AnswerQuality.HasValue) dict["answer_quality"] = ext.AnswerQuality.Value;
            if (ext.TokenCost.HasValue) dict["token_cost"] = ext.TokenCost.Value;
            if (ext.InferenceCost.HasValue) dict["inference_cost"] = ext.InferenceCost.Value;
        }

        return dict;
    }
}
