using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// 生产 Composition Root — Postgres Pending Run Claimer（Durable Scheduler）
//
// 目标：
//   周期性从持久化 <see cref="IPersistentAgentRunStore"/> 领取待执行 Run 入队，
//   实现跨进程重启恢复的 Durable Run 调度：
//     1. 死信：把重试耗尽（Failed 且 retry_count >= max_retries）的 Run 原子标记为
//        DeadLettered（终态，保留 failure_reason / 事件流作为审计证据）。
//     2. 领取：SKIP LOCKED 原子领取 Created / 可重试 Failed Run（含重试重置为 Created +
//        retry_count+1 + 指数退避），按优先级倒序 + 每 workspace 公平上限。
//     3. 入队：领取到的 Run 经 <see cref="AgentKernelHost.TryEnqueueAsync"/> 入队执行；
//        队列满（QueueFull）时停止本周期领取（背压——Run 已持久化，下周期再取）。
//
// 设计边界：
//   1. 仅对持久化 <see cref="IAgentRunStore"/> 生效（IPersistentAgentRunStore 标记）。
//      InMemory store 进程重启后数据丢失，无 Run 可领取——worker 检测到非持久化
//      实现后立即退出（no-op）。
//   2. 幂等性：SKIP LOCKED 保证多实例并发领取时同一 Run 仅被一个实例领走；
//      <see cref="AgentKernelHost"/> 的 _activeRuns 去重防止同进程重复入队。
//   3. 领取不改 Created 状态（Actor 以 state=Created 判定全新启动）；
//      重试重置走原子 UPDATE（state Failed→Created + 事件流清空），跨实例安全。
//   4. 异常隔离：单个周期失败不中断轮询循环（catch + log）。
// ===========================================================================

/// <summary>
/// 生产 Composition Root：Postgres Pending Run Claimer（Durable Scheduler）。
/// 周期性死信重试耗尽的 Run 并领取 pending Run 入队执行，支持优先级 + 公平调度 + 重启恢复。
/// </summary>
internal sealed class PostgresPendingRunClaimer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PostgresPendingRunClaimer> _logger;

    public PostgresPendingRunClaimer(
        IServiceProvider services,
        ILogger<PostgresPendingRunClaimer> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 检测 IPersistentAgentRunStore 是否为持久化实现。
        // InMemory store 进程重启后数据丢失，无 Run 可领取——退出（no-op）。
        using var probeScope = _services.CreateScope();
        var probeStore = probeScope.ServiceProvider.GetService<IPersistentAgentRunStore>();
        if (probeStore is null)
        {
            _logger.LogInformation(
                "PostgresPendingRunClaimer: 未检测到 IPersistentAgentRunStore 注册（InMemory/FileSystem provider）。" +
                "Worker 退出——无可领取的持久化 Run 数据。");
            return;
        }

        TimeSpan interval;
        using (var optionsScope = _services.CreateScope())
        {
            var hostOptions = optionsScope.ServiceProvider.GetService<AgentHostOptions>() ?? new AgentHostOptions();
            interval = hostOptions.PendingClaimInterval > TimeSpan.Zero
                ? hostOptions.PendingClaimInterval
                : TimeSpan.FromSeconds(5);
        }

        _logger.LogInformation(
            "PostgresPendingRunClaimer 启动：轮询间隔 {Interval}s，启用 Durable Run 调度（优先级 + 重试 + 死信 + 重启恢复）。",
            interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ClaimOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgresPendingRunClaimer 轮询循环异常（不中断后续轮询）。");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("PostgresPendingRunClaimer 已停止。");
    }

    /// <summary>
    /// 执行一次领取周期：先死信重试耗尽的 Run，再领取 pending Run 入队。
    /// 队列满（QueueFull）时提前结束本周期（背压），Run 已持久化由下周期接管。
    /// </summary>
    private async Task ClaimOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetService<IPersistentAgentRunStore>();
        var host = scope.ServiceProvider.GetService<AgentKernelHost>();
        if (store is null || host is null)
        {
            _logger.LogDebug("IPersistentAgentRunStore / AgentKernelHost 未注册；跳过领取周期。");
            return;
        }

        var options = scope.ServiceProvider.GetService<AgentHostOptions>() ?? new AgentHostOptions();
        var deadLetterBatch = options.DeadLetterBatchSize > 0 ? options.DeadLetterBatchSize : 50;
        var claimBatch = options.PendingClaimBatchSize > 0 ? options.PendingClaimBatchSize : 50;
        var perWorkspace = options.PendingClaimPerWorkspace > 0 ? options.PendingClaimPerWorkspace : 10;
        // P2-4 Recovery Integrity State：人工介入告警接收器（未注册时跳过告警，best-effort 钩子）。
        var alertSink = scope.ServiceProvider.GetService<IRecoveryAlertSink>();

        // 1. 死信重试耗尽的 Run（Failed 且 retry_count >= max_retries）。
        IReadOnlyList<AgentRun> deadLettered;
        try
        {
            deadLettered = await store.DeadLetterExhaustedRunsAsync(deadLetterBatch, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgresPendingRunClaimer: 死信批次失败（不阻断领取）。");
            deadLettered = Array.Empty<AgentRun>();
        }
        if (deadLettered.Count > 0)
        {
            _logger.LogWarning(
                "PostgresPendingRunClaimer: 死信 {Count} 个重试耗尽的 Run（retry_count 达到 max_retries，需运维介入）。",
                deadLettered.Count);
            // P2-4：死信属需人工介入事件（重试预算耗尽），best-effort 投递告警。
            await NotifyDeadLetterAlertsAsync(alertSink, deadLettered).ConfigureAwait(false);
        }

        // 2. 领取 pending Run（SKIP LOCKED，优先级倒序 + 每 workspace 公平上限）。
        IReadOnlyList<AgentRun> claimed;
        try
        {
            claimed = await store.ClaimPendingBatchAsync(
                claimBatch, perWorkspace, options.RetryBackoffBase, options.RetryBackoffMax, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PostgresPendingRunClaimer: 领取批次失败（下周期重试）。");
            return;
        }

        if (claimed.Count == 0)
        {
            return;
        }

        var enqueued = 0;
        var retried = 0;
        var recoveryClaimed = 0;
        foreach (var run in claimed)
        {
            var result = await host.TryEnqueueAsync(run, cancellationToken).ConfigureAwait(false);
            if (result.Status == AgentRunEnqueueStatus.QueueFull || result.Status == AgentRunEnqueueStatus.Closed)
            {
                // 背压：队列已满 → 停止本周期领取（Run 已持久化，下周期再取）。
                _logger.LogInformation(
                    "PostgresPendingRunClaimer: 调度队列已满（{Depth}/{Capacity}），本周期停止领取。",
                    result.QueueDepth, result.Capacity);
                break;
            }
            if (result.Status == AgentRunEnqueueStatus.Accepted)
            {
                enqueued++;
                if (run.RetryCount > 0)
                {
                    retried++;
                }
                // P2-4：RecoveryDependencyUnavailable 的 Run 被领取 = 恢复依赖重试（非 Failed 重试）。
                if (run.State == AgentRunState.RecoveryDependencyUnavailable)
                {
                    recoveryClaimed++;
                }
            }
            // AlreadyActive：同进程已有活跃 Run（重复入队竞争）→ 跳过，非错误。
        }

        if (enqueued > 0 || retried > 0 || recoveryClaimed > 0)
        {
            _logger.LogInformation(
                "PostgresPendingRunClaimer: 本周期入队 {Enqueued} 个 Run（其中重试 {Retried} 个，恢复依赖重试 {RecoveryClaimed} 个）。",
                enqueued, retried, recoveryClaimed);
        }
    }

    /// <summary>
    /// P2-4：死信 Run 人工介入告警（best-effort，失败不阻断领取周期）。
    /// </summary>
    private static async Task NotifyDeadLetterAlertsAsync(IRecoveryAlertSink? alertSink, IReadOnlyList<AgentRun> deadLettered)
    {
        if (alertSink is null)
        {
            return;
        }
        foreach (var run in deadLettered)
        {
            var alert = new AgentRunAlert
            {
                RunId = run.RunId,
                WorkspaceId = run.WorkspaceId,
                SessionId = run.SessionId,
                Kind = AgentRunAlertKind.DeadLetterExhausted,
                Reason = $"DeadLettered：重试预算耗尽（retry_count={run.RetryCount}，max_retries={run.MaxRetries}），需运维介入排查失败根因。",
                Attempt = run.RetryCount
            };
            try
            {
                await alertSink.NotifyInterventionRequiredAsync(alert, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // best-effort：告警投递失败不阻断领取周期（catch + log）。
                System.Diagnostics.Trace.TraceWarning(
                    "[PostgresPendingRunClaimer] 投递死信告警失败（run={0}，workspace={1}）：{2}。",
                    run.RunId, run.WorkspaceId, ex.Message);
            }
        }
    }
}
