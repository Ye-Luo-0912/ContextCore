using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// 生产 Composition Root — Postgres Pending Run Claimer（Durable Scheduler）
//
// 目标：
// 周期性从持久化 <see cref="IPersistentAgentRunStore"/> 领取待执行 Run 入队，
// 实现跨进程重启恢复的 Durable Run 调度：
// 1. 死信：把重试耗尽（Failed 且 retry_count >= max_retries）的 Run 原子标记为
// DeadLettered（终态，保留 failure_reason / 事件流作为审计证据）。
// 2. 领取：SKIP LOCKED 原子领取 Queued / 可重试 Failed / RecoveryDependencyUnavailable Run
// （P0-6/P0-8：Created/PendingAdmission/AdmissionRejected 永不领取），按优先级倒序 +
// 每 workspace 公平上限；领取即写入 Scheduler Claim Lease（claim_owner / claim_token /
// claim_expires_at），不再只打 UpdatedAt 补丁。
// 3. 入队：领取到的 Run 经 <see cref="AgentKernelHost.TryEnqueueAsync"/> 入队执行；
// 队列满（QueueFull）时释放当前 Run 的 Claim（回 Queued）并停止本周期领取
// （背压——Run 已持久化，下周期再取）。
//
// 设计边界：
// 1. 仅对持久化 <see cref="IAgentRunStore"/> 生效（IPersistentAgentRunStore 标记）。
// InMemory store 进程重启后数据丢失，无 Run 可领取——worker 检测到非持久化
// 实现后立即退出（no-op）。
// 2. 幂等性：SKIP LOCKED 保证多实例并发领取时同一 Run 仅被一个实例领走；
// claim_token fencing 保证释放/接管只作用于当前持有者（过期节点不得释放新持有者的 claim）；
// <see cref="AgentKernelHost"/> 的 _activeRuns 去重防止同进程重复入队。
// 3. 领取写入 Claimed（state=22）+ Scheduler Claim Lease；AgentKernelHost 取得
// Execution/Fencing Lease 后再推进 Claimed → Running（P0-8 两种 Lease 分离）。
// 4. 异常隔离：单个周期失败不中断轮询循环（catch + log）。
// ===========================================================================

/// <summary>
/// 生产 Composition Root：Postgres Pending Run Claimer（Durable Scheduler）。
/// 周期性死信重试耗尽的 Run 并领取 pending Run 入队执行，支持优先级 + 公平调度 + 重启恢复。
/// </summary>
internal sealed class PostgresPendingRunClaimer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PostgresPendingRunClaimer> _logger;
    private readonly ProductionRuntimeWorkerRegistry? _workerRegistry;
    // P0-8：本进程稳定的 Scheduler Claim Lease 持有者标识（观测用：哪个节点领取了哪些 Run）。
    private readonly string _claimOwner;

    public PostgresPendingRunClaimer(
        IServiceProvider services,
        ILogger<PostgresPendingRunClaimer> logger,
        ProductionRuntimeWorkerRegistry? workerRegistry = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerRegistry = workerRegistry;
        try
        {
            _claimOwner = $"claimer-{Environment.MachineName}-{Guid.NewGuid():N}";
        }
        catch
        {
            _claimOwner = $"claimer-{Guid.NewGuid():N}";
        }
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
            _workerRegistry?.SetLeaseStatus(nameof(PostgresPendingRunClaimer), "polling");
            try
            {
                await ClaimOnceAsync(stoppingToken).ConfigureAwait(false);
                _workerRegistry?.MarkCycleSucceeded(nameof(PostgresPendingRunClaimer));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgresPendingRunClaimer 轮询循环异常（不中断后续轮询）。");
                _workerRegistry?.RecordFailure(
                    nameof(PostgresPendingRunClaimer), ex.Message, interval);
                _workerRegistry?.SetLeaseStatus(nameof(PostgresPendingRunClaimer), "backoff");
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

        _workerRegistry?.SetLeaseStatus(nameof(PostgresPendingRunClaimer), "stopped");
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
        // P0-8：Scheduler Claim Lease 时长（节点领取后崩溃时，过期后其他节点重新领取）。
        var claimDuration = options.SchedulerClaimDuration > TimeSpan.Zero
            ? options.SchedulerClaimDuration
            : TimeSpan.FromSeconds(60);
        var claimOwner = options.Owner ?? _claimOwner;
        // Recovery Integrity State：人工介入告警接收器（未注册时跳过告警，best-effort 钩子）。
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
            // 死信属需人工介入事件（重试预算耗尽），best-effort 投递告警。
            await NotifyDeadLetterAlertsAsync(alertSink, deadLettered).ConfigureAwait(false);
        }

        // 2. 领取 pending Run（SKIP LOCKED，优先级倒序 + 每 workspace 公平上限）。
        // 领取数量与实际空闲队列槽位联动：只领取能入队的量，避免多余 Run 持有
        // Scheduler Claim 直到过期（阻塞其他节点重新调度）；队列已满时本周期不领取
        // （Run 已持久化，下周期再取）。
        var availableSlots = host.AvailableQueueSlots;
        if (availableSlots <= 0)
        {
            return;
        }
        claimBatch = Math.Min(claimBatch, availableSlots);

        // P0-8：Scheduler Claim Lease 真正落库（claim_owner / claim_token / claim_expires_at）——
        // 领取即写入持有者/令牌/过期时间，事务提交后其他节点不得重复领取同一 Run。
        IReadOnlyList<AgentRun> claimed;
        try
        {
            claimed = await store.ClaimPendingBatchAsync(
                claimBatch, perWorkspace, options.RetryBackoffBase, options.RetryBackoffMax,
                claimOwner, claimDuration, cancellationToken)
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

        _workerRegistry?.SetQueueLag(nameof(PostgresPendingRunClaimer), claimed.Count);

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
                // 背压：队列已满 → 释放当前 Run 的 Scheduler Claim（回 Queued，其他节点可重新领取），
                // 并停止本周期领取（Run 已持久化，下周期再取）。
                // P0-8：必须释放 claim，否则本节点崩溃后该 Run 直到 claim 过期才被重新调度，
                // 且同一 Run 的 claim 长期占住候选前列。
                if (run.ClaimToken is not null)
                {
                    try
                    {
                        var released = await store.ReleaseClaimAsync(
                            run.WorkspaceId, run.RunId, run.ClaimToken, cancellationToken).ConfigureAwait(false);
                        if (!released)
                        {
                            _logger.LogDebug(
                                "PostgresPendingRunClaimer: Run {RunId} 的 Scheduler Claim 释放失败（claim_token 不匹配——已被接管/推进），跳过。",
                                run.RunId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "PostgresPendingRunClaimer: 释放 Run {RunId} 的 Scheduler Claim 失败（claim 过期后由其他节点接管）。",
                            run.RunId);
                    }
                }
                _logger.LogInformation(
                    "PostgresPendingRunClaimer: 调度队列已满（{Depth}/{Capacity}），释放 Run {RunId} 的 Scheduler Claim 并停止本周期领取。",
                    result.QueueDepth, result.Capacity, run.RunId);
                break;
            }
            if (result.Status == AgentRunEnqueueStatus.Accepted)
            {
                enqueued++;
                if (run.RetryCount > 0)
                {
                    retried++;
                }
                // RecoveryDependencyUnavailable 的 Run 被领取 = 恢复依赖重试（非 Failed 重试）。
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
    /// 死信 Run 人工介入告警（best-effort，失败不阻断领取周期）。
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
