using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Extensions;
using ContextCore.Service.Infrastructure;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// 生产 Composition Root — AgentRun Recovery Worker
//
// 目标：
// 周期性扫描 <see cref="IAgentRunStore"/> 中处于非终态的 Run（崩溃前未完成），
// 做完整性判断 / LeaseLost 识别 / 状态修复：把可恢复 Run CAS 回 Queued，
// 由 Durable Scheduler（PostgresPendingRunClaimer）统一领取入队执行。
//
// 运行时能力补齐：
// 1. 超时检测：Run 在非终态停留超过 RunExecutionTimeout 且无活跃租约时原子标记为 LeaseLost
// （原 owner 丢租后未被接管；进程崩溃后 CTS 随进程消失，Run 永远不会自动取消；recovery worker 兜底）。
// 2. Checkpoint resume：扫描时记录 Run 是否有 checkpoint（日志），
// AgentRunActor.ExecuteAsync 通过 run.State + 事件流自动重建上下文。
//
// 设计边界：
// 1. 仅对持久化 <see cref="IAgentRunStore"/> 生效（IPersistentAgentRunStore 标记）。
// InMemory store 在进程重启后数据丢失，无 Run 可恢复——worker 检测到非持久化
// 实现后立即退出（no-op）。
// 2. 幂等性：CAS 回 Queued 为原子操作，多实例并发时只有一个 CAS 成功；
// 领取/入队由 Durable Scheduler 独家负责（ClaimPendingBatchAsync SKIP LOCKED +
// AgentKernelHost._activeRuns 去重）。
// 3. 非终态扫描：Created / ContextBuilding / ModelCalling / AwaitingApproval /
// ToolDispatching / Observing / Checkpointing 均为可恢复状态。
// Completed / Failed / Cancelled / LeaseLost 为终态，跳过。
// 4. 异常隔离：单个 Run 恢复失败不中断整个轮询循环（catch + log）。
// 5. 扫描防饥饿：每状态 keyset 游标（ORDER BY updated_at, run_id）跨轮次推进，
// 每轮每状态最多扫描 MaxRunsPerStatePerScan 条，且状态按 round-robin 轮转起始——
// 早期富状态无法独占扫描预算，后续状态与状态内后进 Run 都能持续获得超时检测/恢复机会。
//
// 调度边界（单一调度所有者）：本 Worker 只做状态修复（CAS → Queued），
// 绝不直接入队；只有 PostgresPendingRunClaimer 能把 Run 放入 Host 队列。
// ===========================================================================

/// <summary>
/// 生产 Composition Root：AgentRun Recovery Worker。
/// 周期性扫描未完成的 Run：超时且无人持有租约的 Run 原子标记为 LeaseLost；
/// 其余可恢复 Run CAS 回 Queued（由 Durable Scheduler 领取执行）。
/// </summary>
internal sealed class AgentRunRecoveryWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ContextCoreRuntimeOptions _options;
    private readonly ILogger<AgentRunRecoveryWorker> _logger;
    private readonly ProductionRuntimeWorkerRegistry? _workerRegistry;

    /// <summary>
    /// 每状态每次扫描的运行数预算。超过预算的 Run 由 keyset 游标保留到后续轮次继续扫描，
    /// 防止单状态海量 Run 独占整轮扫描（其余状态与状态内后进 Run 饥饿）。
    /// </summary>
    private const int MaxRunsPerStatePerScan = 50;

    /// <summary>每状态 keyset 扫描游标（上一轮最后扫描到的 Run 位置），跨轮次持久。</summary>
    private readonly Dictionary<AgentRunState, RunScanCursor> _stateCursors = [];

    /// <summary>round-robin 起始状态索引：每轮从不同状态开始扫描，保证各状态轮转公平。</summary>
    private int _roundRobinIndex;

    /// <summary>keyset 游标：上一轮最后扫描到的 (UpdatedAt, RunId)，用于下一轮续扫。</summary>
    private sealed record RunScanCursor(DateTimeOffset UpdatedAt, string RunId);

    /// <summary>
    /// 需要扫描恢复的非终态状态列表。
    /// </summary>
    /// <remarks>
    /// AwaitingApproval 不在此列表中——等待审批的 Run 不应被 Recovery Worker 周期性重启。
    /// 审批决策由外部 POST /approvals/{approvalId} 端点提交，端点将状态推进到
    /// PendingToolExecution（批准）或 Failed（拒绝）后才由 Recovery Worker 重新入队执行。
    /// 周期性重启 AwaitingApproval 会导致 Actor 重复加载审批状态、重复持久化 ApprovalRequested 事件。
    ///
    /// Queued / Claimed（P0-6/P0-8 执行前状态）不在此列表中——它们由
    /// PostgresPendingRunClaimer 专属接管（Scheduler Claim Lease），Recovery Worker
    /// 不与其竞争入队，避免双调度真源；Created 同理（v59 迁移已全量转换为 Queued）。
    /// 被领取后崩溃的 Run（Claimed 且 claim 过期）由 Claimer 重新领取。
    ///
    /// Running（P0-8 新增）：Execution/Fencing Lease 已获取后推进到 Running；
    /// Worker 崩溃后执行租约过期，Run 停留 Running，由本 Worker 扫描并重新入队，
    /// 新节点取得过期执行租约后继续执行（Running 且已 flush 的 Run 走 resume 路径；
    /// 尚未 flush 的 Running 走全新启动——两种路径均安全）。
    ///
    /// RecoveryDependencyUnavailable（恢复依赖不可用）为可重试非终态，加入扫描列表——
    /// 由本 Worker 在退避门（NextRetryAtUtc）通过后 CAS 回 Queued（退避期内跳过，不触发
    /// 超时→LeaseLost 逻辑；LeaseLost 会把等待退避的 Run 误判为卡死并移出恢复路径）。
    ///
    /// ScheduledLocally（本地调度，入队成功即消费 Scheduler Claim）：Run 已离开可领取集合，
    /// 排队期间不依赖 Claim 续租。节点崩溃后本地队列随进程消失，Run 滞留 ScheduledLocally
    /// 无人接管——由本 Worker 直接 CAS 回 Queued（无退避门；排队等待执行槽不是卡死，
    /// 超时→LeaseLost 语义不适用），由 Durable Claimer 重新领取调度。
    /// </remarks>
    private static readonly AgentRunState[] RecoverableStates =
    [
        AgentRunState.Running,
        AgentRunState.ContextBuilding,
        AgentRunState.ModelCalling,
        AgentRunState.ToolDispatching,
        AgentRunState.Observing,
        AgentRunState.Checkpointing,
        AgentRunState.PendingToolExecution,
        AgentRunState.RecoveryDependencyUnavailable,
        AgentRunState.ScheduledLocally
    ];

    public AgentRunRecoveryWorker(
        IServiceProvider services,
        ContextCoreRuntimeOptions options,
        ILogger<AgentRunRecoveryWorker> logger,
        ProductionRuntimeWorkerRegistry? workerRegistry = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerRegistry = workerRegistry;
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
            _workerRegistry?.SetLeaseStatus(nameof(AgentRunRecoveryWorker), "polling");
            try
            {
                await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                _workerRegistry?.MarkCycleSucceeded(nameof(AgentRunRecoveryWorker));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AgentRunRecoveryWorker 轮询循环异常（不中断后续轮询）。");
                _workerRegistry?.RecordFailure(nameof(AgentRunRecoveryWorker), ex.Message, interval);
                _workerRegistry?.SetLeaseStatus(nameof(AgentRunRecoveryWorker), "backoff");
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

        _workerRegistry?.SetLeaseStatus(nameof(AgentRunRecoveryWorker), "stopped");
        _logger.LogInformation("AgentRunRecoveryWorker 已停止。");
    }

    /// <summary>
    /// 执行一次恢复扫描：按 round-robin 轮转遍历所有非终态状态，
    /// 每状态以 keyset 游标（updated_at, run_id）在预算内续扫，
    /// 对超时 Run 标记为 LeaseLost，对可恢复 Run 重新入队。
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

        // 执行租约存储（批量过滤正被其他实例执行的 Run；未注册时跳过过滤——单节点无租约竞争）。
        var runLease = scope.ServiceProvider.GetService<IAgentRunLease>();

        var timeout = _options.RunExecutionTimeout;
        var now = DateTimeOffset.UtcNow;
        var totalRecovered = 0;
        var totalTimedOut = 0;

        // 状态轮转：本轮从 _roundRobinIndex 开始依次扫描各状态（每状态预算内），
        // 下一轮从下一状态开始——忙状态无法独占整轮扫描预算，所有状态持续获得扫描机会。
        for (var step = 0; step < RecoverableStates.Length; step++)
        {
            var state = RecoverableStates[(_roundRobinIndex + step) % RecoverableStates.Length];
            var cursor = _stateCursors.TryGetValue(state, out var existing) ? existing : null;

            var runs = await runStore.ListByStateAsync(
                state,
                take: MaxRunsPerStatePerScan,
                afterUpdatedAt: cursor?.UpdatedAt,
                afterRunId: cursor?.RunId,
                cancellationToken).ConfigureAwait(false);

            if (runs.Count == 0)
            {
                // 该状态已扫描完（或本就从空开始）：重置游标，下轮从头续扫（新 Run 可能已出现）。
                _stateCursors.Remove(state);
                continue;
            }

            // 批量过滤活跃执行租约：正被其他实例执行的 Run 无需恢复——
            // 避免把无法取得 Execution Lease 的 Run 反复放入本地队列（入队后丢执行槽空转）。
            HashSet<string>? activeLeaseRunIds = null;
            if (runLease is not null)
            {
                try
                {
                    var active = await runLease.GetActiveLeaseRunIdsAsync(
                        runs.Select(r => r.RunId).ToList(), cancellationToken).ConfigureAwait(false);
                    if (active.Count > 0)
                    {
                        activeLeaseRunIds = new HashSet<string>(active, StringComparer.Ordinal);
                    }
                }
                catch (Exception ex)
                {
                    // 过滤失败按无过滤处理（保守恢复路径，不阻断扫描）。
                    _logger.LogDebug(ex, "AgentRunRecoveryWorker: 批量查询活跃执行租约失败（跳过过滤）。");
                }
            }

            foreach (var run in runs)
            {
                if (activeLeaseRunIds is not null && activeLeaseRunIds.Contains(run.RunId))
                {
                    // 该 Run 存在活跃执行租约（其他实例正在执行）→ 跳过恢复，等待其完成/租约过期。
                    continue;
                }
                // Recovery Integrity State：RecoveryDependencyUnavailable（17）是退避重试状态。
                // 跳过超时→LeaseLost 逻辑（Run 是故意等待退避门而非卡死——标记 LeaseLost 会把它
                // 移出恢复路径，破坏 fail-closed 语义），并在退避门（NextRetryAtUtc）未通过时
                // 跳过本轮；通过后 CAS 回 Queued（Durable Claimer 领取入队）。
                if (run.State == AgentRunState.RecoveryDependencyUnavailable)
                {
                    if (run.NextRetryAtUtc is not null && run.NextRetryAtUtc > now)
                    {
                        _logger.LogDebug(
                            "AgentRunRecoveryWorker: Run {RunId} 处于 RecoveryDependencyUnavailable，退避门未到（{NextRetryAt}），跳过。",
                            run.RunId, run.NextRetryAtUtc);
                        continue;
                    }
                    try
                    {
                        await runStore.TransitionStateAsync(
                            run.WorkspaceId, run.RunId, run.State, AgentRunState.Queued, cancellationToken)
                            .ConfigureAwait(false);
                        totalRecovered++;
                        _logger.LogInformation(
                            "AgentRunRecoveryWorker: Run {RunId}（RecoveryDependencyUnavailable，退避门已过，workspace={WorkspaceId}）CAS 回 Queued，由 Durable Claimer 领取执行。",
                            run.RunId, run.WorkspaceId);
                    }
                    catch (InvalidOperationException)
                    {
                        // CAS 失败：状态已被并发推进（Claimer 已领取 / 其他节点已恢复）→ 非致命。
                        _logger.LogDebug(
                            "AgentRunRecoveryWorker: Run {RunId}（RecoveryDependencyUnavailable）CAS 回 Queued 失败（已被并发推进）。",
                            run.RunId);
                    }
                    catch (Exception ex)
                    {
                        // 单个 Run 恢复失败不中断整个扫描
                        _logger.LogError(ex,
                            "AgentRunRecoveryWorker: Run {RunId}（RecoveryDependencyUnavailable）CAS 回 Queued 失败。",
                            run.RunId);
                    }
                    continue;
                }

                // ScheduledLocally（本地调度，入队成功即消费 Scheduler Claim）：
                // 节点崩溃后本地队列随进程消失，Run 滞留 ScheduledLocally 无人接管。
                // 直接 CAS 回 Queued（无退避门、不触发超时→LeaseLost——排队等待执行槽的
                // Run 不是卡死，LeaseLost 会把它移出恢复路径）；回退后由 Durable Claimer
                // 重新领取入队。存活节点的排队 Run 状态推进（ScheduledLocally→Running）与
                // 本 CAS 竞争时只有一方成功（单执行由执行租约保证），另一方放弃或跳过。
                if (run.State == AgentRunState.ScheduledLocally)
                {
                    try
                    {
                        await runStore.TransitionStateAsync(
                            run.WorkspaceId, run.RunId, run.State, AgentRunState.Queued, cancellationToken)
                            .ConfigureAwait(false);
                        totalRecovered++;
                        _logger.LogInformation(
                            "AgentRunRecoveryWorker: Run {RunId}（ScheduledLocally，workspace={WorkspaceId}）CAS 回 Queued，由 Durable Claimer 重新领取执行。",
                            run.RunId, run.WorkspaceId);
                    }
                    catch (InvalidOperationException)
                    {
                        // CAS 失败：状态已被并发推进（本节点已出队执行 / 其他节点已恢复）→ 非致命。
                        _logger.LogDebug(
                            "AgentRunRecoveryWorker: Run {RunId}（ScheduledLocally）CAS 回 Queued 失败（已被并发推进）。",
                            run.RunId);
                    }
                    catch (Exception ex)
                    {
                        // 单个 Run 恢复失败不中断整个扫描
                        _logger.LogError(ex,
                            "AgentRunRecoveryWorker: Run {RunId}（ScheduledLocally）CAS 回 Queued 失败。",
                            run.RunId);
                    }
                    continue;
                }

                // 运行时能力补齐：超时检测
                // Run 在非终态停留超过 RunExecutionTimeout 且无人持有租约 → 原子标记为 LeaseLost
                // （原 owner 丢租后未被接管；进程崩溃后 CTS 随进程消失，Run 永远不会自动取消；
                // recovery worker 兜底，LeaseLost 区别于 Failed——丢租而非执行失败）
                if (timeout > TimeSpan.Zero)
                {
                    var elapsed = now - run.UpdatedAt;
                    if (elapsed > timeout)
                    {
                        // DeadlineAt 校验——Run 自身超时未到期时不标记失败
                        if (run.DeadlineAt is not null && run.DeadlineAt > now)
                        {
                            _logger.LogDebug(
                                "AgentRunRecoveryWorker: Run {RunId} UpdatedAt 超时但 DeadlineAt 未到期（{Deadline}），跳过。",
                                run.RunId, run.DeadlineAt);
                            continue;
                        }

                        // 单 SQL 原子转移 — 消除 HasActiveLeaseAsync + TransitionStateAsync 的 check-then-act 竞态。
                        // 只有无活跃租约 AND 状态匹配时才更新为 Failed，避免误杀正被活跃 Actor 持有合法租约的 Run。
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

                // 状态修复：把可恢复 Run CAS 回 Queued，由 Durable Claimer（PostgresPendingRunClaimer）
                // 统一领取入队——本 Worker 不做入队（单一调度所有者），避免与 Claimer 双真源竞争。
                // AgentRunActor.ExecuteAsync 通过 run.State 检测是否为恢复场景：
                // - run.State == Created → 全新启动（正常路径）
                // - run.State != Created → 恢复场景，Actor 从事件流重建上下文
                try
                {
                    await runStore.TransitionStateAsync(
                        run.WorkspaceId, run.RunId, run.State, AgentRunState.Queued, cancellationToken)
                        .ConfigureAwait(false);
                    totalRecovered++;
                    _logger.LogInformation(
                        "AgentRunRecoveryWorker: 恢复 Run {RunId}（状态={State}, workspace={WorkspaceId}, turn={Turn}, modelCalls={ModelCalls}）CAS 回 Queued，由 Durable Claimer 领取执行。",
                        run.RunId, run.State, run.WorkspaceId, run.Turn, run.ModelCallsUsed);
                }
                catch (InvalidOperationException)
                {
                    // CAS 失败：状态已被并发推进（Claimer 已领取 / 其他节点已恢复）→ 非致命。
                    _logger.LogDebug(
                        "AgentRunRecoveryWorker: Run {RunId}（状态={State}）CAS 回 Queued 失败（已被并发推进）。",
                        run.RunId, run.State);
                }
                catch (Exception ex)
                {
                    // 单个 Run 恢复失败不中断整个扫描
                    _logger.LogError(ex,
                        "AgentRunRecoveryWorker: 恢复 Run {RunId} 失败（状态={State}）。",
                        run.RunId, run.State);
                }
            }

            // 推进游标到本状态最后扫描的 Run：
            // 满预算（== 每状态预算）说明可能还有更多 → 保留游标下轮续扫；
            // 不足预算说明已扫完 → 重置游标，下轮从头（新 Run 可能已出现）。
            var last = runs[^1];
            if (runs.Count >= MaxRunsPerStatePerScan)
            {
                _stateCursors[state] = new RunScanCursor(last.UpdatedAt, last.RunId);
            }
            else
            {
                _stateCursors.Remove(state);
            }
        }

        // round-robin 推进：下轮从下一状态开始扫描。
        _roundRobinIndex = (_roundRobinIndex + 1) % RecoverableStates.Length;

        if (totalRecovered > 0 || totalTimedOut > 0)
        {
            _logger.LogInformation(
                "AgentRunRecoveryWorker: 本轮恢复 {Recovered} 个 Run，超时标记 {TimedOut} 个 Run。",
                totalRecovered, totalTimedOut);
        }
    }
}
