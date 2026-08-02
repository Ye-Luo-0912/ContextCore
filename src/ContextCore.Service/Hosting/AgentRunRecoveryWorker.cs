using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// 生产 Composition Root — AgentRun Recovery Worker
//
// 目标：
//   周期性扫描 <see cref="IAgentRunStore"/> 中处于非终态的 Run（崩溃前未完成），
//   通过 <see cref="AgentKernelHost.StartRunAsync"/> 重新入队执行。
//
// 运行时能力补齐：
//   1. 超时检测：Run 在非终态停留超过 RunExecutionTimeout 且无活跃租约时原子标记为 LeaseLost
//      （原 owner 丢租后未被接管；进程崩溃后 CTS 随进程消失，Run 永远不会自动取消；recovery worker 兜底）。
//   2. Checkpoint resume：扫描时记录 Run 是否有 checkpoint（日志），
//      AgentRunActor.ExecuteAsync 通过 run.State + 事件流自动重建上下文。
//
// 设计边界：
//   1. 仅对持久化 <see cref="IAgentRunStore"/> 生效（IPersistentAgentRunStore 标记）。
//      InMemory store 在进程重启后数据丢失，无 Run 可恢复——worker 检测到非持久化
//      实现后立即退出（no-op）。
//   2. 幂等性：<see cref="AgentKernelHost.StartRunAsync"/> 内部通过 _activeRuns
//      ConcurrentDictionary 去重，同一 Run 不会被重复入队。多实例场景下
//      <see cref="IAgentRunLease"/> 确保仅一个实例处理（ProductionHA profile）。
//   3. 非终态扫描：Created / ContextBuilding / ModelCalling / AwaitingApproval /
//      ToolDispatching / Observing / Checkpointing 均为可恢复状态。
//      Completed / Failed / Cancelled / LeaseLost 为终态，跳过。
//   4. 异常隔离：单个 Run 恢复失败不中断整个轮询循环（catch + log）。
// ===========================================================================

/// <summary>
/// 生产 Composition Root：AgentRun Recovery Worker。
/// 周期性扫描未完成的 Run 并重新入队执行；对超时且无人持有租约的 Run 原子标记为 LeaseLost。
/// </summary>
internal sealed class AgentRunRecoveryWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ProductionRuntimeOptions _options;
    private readonly ILogger<AgentRunRecoveryWorker> _logger;

    /// <summary>
    /// 需要扫描恢复的非终态状态列表。
    /// </summary>
    /// <remarks>
    /// P0-2：AwaitingApproval 不在此列表中——等待审批的 Run 不应被 Recovery Worker 周期性重启。
    /// 审批决策由外部 POST /approvals/{approvalId} 端点提交，端点将状态推进到
    /// PendingToolExecution（批准）或 Failed（拒绝）后才由 Recovery Worker 重新入队执行。
    /// 周期性重启 AwaitingApproval 会导致 Actor 重复加载审批状态、重复持久化 ApprovalRequested 事件。
    /// </remarks>
    private static readonly AgentRunState[] RecoverableStates =
    [
        AgentRunState.Created,
        AgentRunState.ContextBuilding,
        AgentRunState.ModelCalling,
        AgentRunState.ToolDispatching,
        AgentRunState.Observing,
        AgentRunState.Checkpointing,
        AgentRunState.PendingToolExecution
    ];

    public AgentRunRecoveryWorker(
        IServiceProvider services,
        ProductionRuntimeOptions options,
        ILogger<AgentRunRecoveryWorker> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 检测 IAgentRunStore 是否为持久化实现。
        // InMemory store 在进程重启后数据丢失，无 Run 可恢复——退出（no-op）。
        using var probeScope = _services.CreateScope();
        var probeStore = probeScope.ServiceProvider.GetService<IPersistentAgentRunStore>();
        if (probeStore is null)
        {
            _logger.LogInformation(
                "AgentRunRecoveryWorker: 未检测到 IPersistentAgentRunStore 注册（InMemory/FileSystem provider）。" +
                "Worker 退出——无可恢复的持久化 Run 数据。");
            return;
        }

        var interval = _options.RunRecoveryInterval > TimeSpan.Zero
            ? _options.RunRecoveryInterval
            : TimeSpan.FromSeconds(60);

        _logger.LogInformation(
            "AgentRunRecoveryWorker 启动：轮询间隔 {Interval}s，扫描 {StateCount} 个非终态，超时阈值 {Timeout}s。",
            interval.TotalSeconds, RecoverableStates.Length,
            _options.RunExecutionTimeout > TimeSpan.Zero ? _options.RunExecutionTimeout.TotalSeconds : -1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AgentRunRecoveryWorker 轮询循环异常（不中断后续轮询）。");
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

        _logger.LogInformation("AgentRunRecoveryWorker 已停止。");
    }

    /// <summary>
    /// 执行一次恢复扫描：遍历所有非终态状态，列出未完成 Run，
    /// 对超时 Run 标记为 Failed，对可恢复 Run 重新入队。
    /// </summary>
    private async Task RecoverOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var runStore = scope.ServiceProvider.GetService<IAgentRunStore>();
        if (runStore is null)
        {
            _logger.LogDebug("IAgentRunStore 未注册；跳过恢复扫描。");
            return;
        }

        var host = scope.ServiceProvider.GetService<AgentKernelHost>();
        if (host is null)
        {
            _logger.LogDebug("AgentKernelHost 未注册；跳过恢复扫描。");
            return;
        }

        var timeout = _options.RunExecutionTimeout;
        var now = DateTimeOffset.UtcNow;
        var totalRecovered = 0;
        var totalTimedOut = 0;

        foreach (var state in RecoverableStates)
        {
            var runs = await runStore.ListByStateAsync(state, take: 100, cancellationToken)
                .ConfigureAwait(false);

            if (runs.Count == 0)
            {
                continue;
            }

            foreach (var run in runs)
            {
                // 运行时能力补齐：超时检测
                // Run 在非终态停留超过 RunExecutionTimeout 且无人持有租约 → 原子标记为 LeaseLost
                // （原 owner 丢租后未被接管；进程崩溃后 CTS 随进程消失，Run 永远不会自动取消；
                //   recovery worker 兜底，LeaseLost 区别于 Failed——丢租而非执行失败）
                if (timeout > TimeSpan.Zero)
                {
                    var elapsed = now - run.UpdatedAt;
                    if (elapsed > timeout)
                    {
                        // P0-6：DeadlineAt 校验——Run 自身超时未到期时不标记失败
                        if (run.DeadlineAt is not null && run.DeadlineAt > now)
                        {
                            _logger.LogDebug(
                                "AgentRunRecoveryWorker: Run {RunId} UpdatedAt 超时但 DeadlineAt 未到期（{Deadline}），跳过。",
                                run.RunId, run.DeadlineAt);
                            continue;
                        }

                        // P0-7：单 SQL 原子转移 — 消除 HasActiveLeaseAsync + TransitionStateAsync 的 check-then-act 竞态。
                        // 只有无活跃租约 AND 状态匹配时才更新为 Failed，避免误杀正被活跃 Actor 持有合法租约的 Run。
                        var runLease = scope.ServiceProvider.GetService<IAgentRunLease>();
                        if (runLease is not null)
                        {
                            try
                            {
                                var affected = await runLease.MarkLeaseLostIfLeaseExpiredAsync(
                                    run.WorkspaceId, run.RunId, run.State, cancellationToken).ConfigureAwait(false);
                                if (affected > 0)
                                {
                                    totalTimedOut++;
                                    _logger.LogWarning(
                                        "AgentRunRecoveryWorker: Run {RunId} 超时（状态={State}, 已停留 {Elapsed}min > {Timeout}min，无活跃租约，DeadlineAt={Deadline}），已原子标记为 LeaseLost。",
                                        run.RunId, run.State, elapsed.TotalMinutes, timeout.TotalMinutes, run.DeadlineAt);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "AgentRunRecoveryWorker: 原子标记 Run {RunId} 超时失败。",
                                    run.RunId);
                            }
                        }
                        else
                        {
                            // 无 IAgentRunLease（单节点模式）→ 回退到 TransitionStateAsync
                            try
                            {
                                await runStore.TransitionStateAsync(
                                    run.WorkspaceId, run.RunId, run.State, AgentRunState.Failed, cancellationToken)
                                    .ConfigureAwait(false);
                                totalTimedOut++;
                                _logger.LogWarning(
                                    "AgentRunRecoveryWorker: Run {RunId} 超时（状态={State}, 已停留 {Elapsed}min > {Timeout}min，DeadlineAt={Deadline}），标记为 Failed。",
                                    run.RunId, run.State, elapsed.TotalMinutes, timeout.TotalMinutes, run.DeadlineAt);
                            }
                            catch (InvalidOperationException)
                            {
                                // CAS 失败 = 状态已被其他实例推进 → 非致命
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "AgentRunRecoveryWorker: 标记 Run {RunId} 超时失败。",
                                    run.RunId);
                            }
                        }
                        continue;
                    }
                }

                // 运行时能力补齐：resume from checkpoint
                // AgentRunActor.ExecuteAsync 通过 run.State 检测是否为恢复场景：
                //   - run.State == Created → 全新启动（正常路径）
                //   - run.State != Created → 恢复场景，Actor 从事件流重建上下文
                // 此处仅记录日志，实际 resume 由 Actor 内部处理
                try
                {
                    await host.StartRunAsync(run, cancellationToken).ConfigureAwait(false);
                    totalRecovered++;
                    _logger.LogInformation(
                        "AgentRunRecoveryWorker: 恢复 Run {RunId}（状态={State}, workspace={WorkspaceId}, turn={Turn}, modelCalls={ModelCalls}）。",
                        run.RunId, run.State, run.WorkspaceId, run.Turn, run.ModelCallsUsed);
                }
                catch (Exception ex)
                {
                    // 单个 Run 恢复失败不中断整个扫描
                    _logger.LogError(ex,
                        "AgentRunRecoveryWorker: 恢复 Run {RunId} 失败（状态={State}）。",
                        run.RunId, run.State);
                }
            }
        }

        if (totalRecovered > 0 || totalTimedOut > 0)
        {
            _logger.LogInformation(
                "AgentRunRecoveryWorker: 本轮恢复 {Recovered} 个 Run，超时标记 {TimedOut} 个 Run。",
                totalRecovered, totalTimedOut);
        }
    }
}
