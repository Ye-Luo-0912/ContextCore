using ContextCore.Abstractions;
using ContextCore.Core.Services.DecisionEngine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.Evolution;

// ===========================================================================
// R28-B.8：Canary Progression HostedService（工作包 D）
//
// 目标（对齐 R28-B.8 规格）：
//   1. 后台定时（按 CanarySchedulerOptions.PollingInterval）轮询所有处于 ScopedCanary
//      阶段的 pipeline runs。
//   2. 对每个 run：从 ICanaryMetricsCollector 获取聚合指标 → 转换为 baseline/experiment
//      metrics 字典 → 调用 CanaryProgressionService.EvaluateAsync → 根据 Decision
//      （Advance/Rollback/Hold）执行相应动作。
//   3. Advance 时调用 AdvanceAsync + Reset metrics（开始新观察窗口）。
//   4. 异常隔离：单个 run 的处理失败不中断整个轮询循环（catch + log）。
//
// 设计边界：
//   - 本服务仅推进 ScopedCanary 阶段内部的百分比阶梯；不替代 IPromotionJudge
//     做跨阶段晋升决策。
//   - CanarySchedulerOptions.Enabled=false 时立即退出 ExecuteAsync（不轮询）。
//   - CanaryProgressionService 为 Singleton，直接注入；IServiceScopeFactory 保留用于
//     未来 scoped 依赖解析。
// ===========================================================================

/// <summary>
/// R28-B.8：Canary 调度器配置。
/// </summary>
public sealed class CanarySchedulerOptions
{
    /// <summary>轮询间隔（默认 60 秒）。</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>是否启用调度器（默认 true）。</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// R28-B.8：Canary 渐进推进后台服务。定时轮询 ScopedCanary 阶段的 pipeline runs，
/// 基于 ICanaryMetricsCollector 聚合的指标自动推进或回滚 canary 百分比。
/// </summary>
public sealed class CanaryProgressionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICanaryMetricsCollector _metricsCollector;
    private readonly CanaryProgressionService _progressionService;
    private readonly TimeProvider _timeProvider;
    private readonly CanarySchedulerOptions _options;
    private readonly ILogger<CanaryProgressionHostedService> _logger;

    /// <summary>构造 Canary 渐进推进后台服务。</summary>
    /// <param name="scopeFactory">DI scope 工厂（用于解析 scoped 依赖）。</param>
    /// <param name="metricsCollector">Canary 指标采集器。</param>
    /// <param name="progressionService">Canary 渐进推进服务。</param>
    /// <param name="timeProvider">时间提供者（可选，默认 System）。</param>
    /// <param name="options">调度器配置（可选，默认 <see cref="CanarySchedulerOptions"/>）。</param>
    /// <param name="logger">日志器。</param>
    public CanaryProgressionHostedService(
        IServiceScopeFactory scopeFactory,
        ICanaryMetricsCollector metricsCollector,
        CanaryProgressionService progressionService,
        TimeProvider? timeProvider = null,
        CanarySchedulerOptions? options = null,
        ILogger<CanaryProgressionHostedService>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
        _progressionService = progressionService ?? throw new ArgumentNullException(nameof(progressionService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new CanarySchedulerOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CanaryProgressionHostedService>.Instance;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("CanaryProgressionHostedService 已禁用（CanarySchedulerOptions.Enabled=false）；不启动轮询。");
            return;
        }

        _logger.LogInformation("CanaryProgressionHostedService 启动：轮询间隔 {PollingInterval}。", _options.PollingInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CanaryProgressionHostedService 轮询循环异常（不中断后续轮询）。");
            }

            try
            {
                await Task.Delay(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("CanaryProgressionHostedService 已停止。");
    }

    /// <summary>
    /// 执行一次轮询：列出所有 ScopedCanary 阶段的 run，逐个评估并推进/回滚。
    /// </summary>
    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        // 从 DI scope 解析 IPipelineRunStore（可能未注册）
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetService<IPipelineRunStore>();
        if (store is null)
        {
            _logger.LogDebug("IPipelineRunStore 未注册；跳过 Canary 轮询。");
            return;
        }

        var runs = await store.ListRunsByStageAsync(
            OptimizationStage.ScopedCanary, take: 100, cancellationToken).ConfigureAwait(false);

        if (runs.Count == 0)
        {
            return;
        }

        foreach (var run in runs)
        {
            // 已终态的 run 跳过（EvaluateAsync 内部也会判终态，这里提前跳过减少日志噪音）
            if (IsTerminal(run.Status))
            {
                continue;
            }

            try
            {
                await ProcessRunAsync(run.RunId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个 run 的处理失败不中断整个轮询循环
                _logger.LogError(ex, "处理 canary run {RunId} 时发生异常；跳过本次轮询。", run.RunId);
            }
        }
    }

    /// <summary>
    /// 处理单个 canary run：获取指标 → 评估 → Advance/Rollback/Hold。
    /// </summary>
    private async Task ProcessRunAsync(string runId, CancellationToken cancellationToken)
    {
        var metrics = _metricsCollector.GetAggregatedMetrics(runId);
        var baselineMetrics = metrics.ToBaselineMetrics();
        var experimentMetrics = metrics.ToExperimentMetrics();

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

                    // 推进成功后重置指标，开始新的观察窗口
                    if (result.Applied)
                    {
                        _metricsCollector.Reset(runId);
                        _logger.LogInformation(
                            "Canary run {RunId} 推进：{Prev}% → {Curr}%。",
                            runId, result.PreviousPercentage, result.CurrentPercentage);
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
                        "Canary run {RunId} 自动回滚：{Reason}（{Rationale}）。",
                        runId, evaluation.RollbackReason, evaluation.Rationale);
                    break;
                }

            case CanaryProgressionDecision.Promoted:
                _logger.LogInformation("Canary run {RunId} 已晋升到 100%（V2 only）。", runId);
                break;

            case CanaryProgressionDecision.Hold:
            default:
                // Hold：等待下次轮询，不记录 verbose 日志（避免刷屏）
                break;
        }
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
}
