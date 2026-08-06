using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using ContextCore.Service.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// ToolReconciliationWorker — Tool 对账工作器
//
// 轮询 IToolReconciliationStore 中的 Pending 对账记录，按
// ToolDescriptor.ReconciliationHandler 名称解析 IToolReconciliationHandler，
// 以 ExternalOperationId 查询外部系统确认模糊 Tool 调用的外部副作用真相：
// - SideEffectOccurred=true → journal.BeginReconciliationAsync + MarkReconciledWithResultAsync
// （提交真相结果），记录 → Resolved；
// - SideEffectOccurred=false → journal 提交 void 结果（Succeeded=false），记录 → Rejected
// （禁止重放该 Tool，模型看到失败后可调整策略）；
// - Handler 缺失/未注册 → 记录保持 Pending，等待人工 resolve 端点裁决；
// - Handler 抛异常 → 记录回退 Pending，下轮重试。
//
// 裁决完成后若该 Run 无未裁决记录，将 Run 重新入队（Actor 恢复执行），
// 并先把 Run 状态从 ReconciliationRunning 回退到 AwaitingReconciliation。
//
// 记录裁决（journal 提交 + 终态落库）统一委托 ToolReconciliationCoordinator，
// 与 HTTP resolve 端点共用同一入口，避免裁决逻辑分叉。
// ===========================================================================

/// <summary>
/// Tool 对账工作器：轮询 Pending 对账记录，经 <see cref="IToolReconciliationHandler"/>
/// 确认外部副作用真相并提交 journal；同时提供人工裁决入口
/// <see cref="ResolveAsync"/>（POST /runs/{runId}/reconciliations/{id}/resolve）。
/// </summary>
public sealed class ToolReconciliationWorker : BackgroundService
{
    /// <summary>Worker 领取的裁决租约时长（与协调器常量一致：过期后其他 Worker 可接管）。</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>共享批量心跳间隔：远小于租约时长，保证长 Handler 执行期间租约不失效。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>单轮轮询的待接管记录配额（ListPendingAsync 单页大小）。</summary>
    private const int PendingBatchSize = 20;

    private readonly ToolReconciliationCoordinator _coordinator;
    private readonly IToolReconciliationStore _store;
    private readonly IReadOnlyDictionary<string, IToolReconciliationHandler> _handlers;
    /// <summary>本节点已注册 Handler 名称集合（ListPendingAsync 过滤参数：无 Handler 可处理的记录不占用轮询配额）。</summary>
    private readonly HashSet<string> _availableHandlers;
    private readonly AgentKernelHost? _kernelHost;
    private readonly IAgentRunStore _runStore;
    private readonly ILogger<ToolReconciliationWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly ProductionRuntimeWorkerRegistry? _workerRegistry;

    /// <summary>keyset 游标：上一轮 ListPendingAsync 扫描到的最后一条记录位置（跨轮次持久）。</summary>
    private DateTimeOffset? _listCursorCreatedAt;
    private string? _listCursorReconciliationId;

    /// <summary>活跃裁决租约注册表（reconciliationId → 条目），供共享批量心跳循环续约。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReconciliationLeaseEntry> _leases =
        new(System.StringComparer.Ordinal);

    private readonly object _heartbeatLock = new();
    private Task? _heartbeatLoopTask;
    private CancellationTokenSource? _heartbeatLoopCts;

    /// <summary>共享心跳注册表条目：reconciliationId + leaseToken + 记录取消源 + 最后确认过期时间（本地 watchdog）。</summary>
    private sealed class ReconciliationLeaseEntry
    {
        public required string ReconciliationId { get; init; }
        public required string LeaseToken { get; init; }
        public required CancellationTokenSource LeaseCts { get; init; }
        public long LastConfirmedExpiresTicks;
        public int ConsecutiveFailures;
    }

    public ToolReconciliationWorker(
        ToolReconciliationCoordinator coordinator,
        IToolReconciliationStore store,
        IEnumerable<IToolReconciliationHandler>? handlers,
        AgentKernelHost? kernelHost,
        IAgentRunStore runStore,
        ContextCoreRuntimeOptions options,
        ILogger<ToolReconciliationWorker> logger,
        ProductionRuntimeWorkerRegistry? workerRegistry = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _kernelHost = kernelHost;
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _workerRegistry = workerRegistry;
        _handlers = (handlers ?? Array.Empty<IToolReconciliationHandler>())
            .ToDictionary(h => h.HandlerName, StringComparer.Ordinal);
        _availableHandlers = new HashSet<string>(_handlers.Keys, StringComparer.Ordinal);
        _interval = options.RunRecoveryInterval > TimeSpan.Zero
            ? options.RunRecoveryInterval
            : TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ToolReconciliationWorker 启动：轮询间隔 {Interval}s。", _interval.TotalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _workerRegistry?.SetLeaseStatus(nameof(ToolReconciliationWorker), "polling");
                var cycleSucceeded = true;
                try
                {
                    await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    cycleSucceeded = false;
                    _logger.LogError(ex, "ToolReconciliationWorker 轮询循环异常（不中断后续轮询）。");
                }

                // 补偿扫描：恢复"停车且无未决对账记录"的 Run（兜底原子推进未覆盖的历史/异常路径）。
                try
                {
                    await RecoverParkedRunsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (cycleSucceeded)
                {
                    _workerRegistry?.MarkCycleSucceeded(nameof(ToolReconciliationWorker));
                }
                else
                {
                    _workerRegistry?.RecordFailure(nameof(ToolReconciliationWorker), "轮询循环异常", _interval);
                    _workerRegistry?.SetLeaseStatus(nameof(ToolReconciliationWorker), "backoff");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            // 停止共享批量心跳循环（worker 退出时不再续约）
            await StopHeartbeatLoopAsync().ConfigureAwait(false);
        }
        _workerRegistry?.SetLeaseStatus(nameof(ToolReconciliationWorker), "stopped");
        _logger.LogInformation("ToolReconciliationWorker 已停止。");
    }

    /// <summary>
    /// 人工/自动裁决对账记录（POST resolve 端点与 Handler 路径共用）。
    /// 校验记录属于指定 Workspace + Run（跨租户 reconciliationId 视为不存在）。
    /// </summary>
    /// <returns>
    /// 0 = 成功裁决；1 = 记录不存在或不属于该 Workspace/Run；2 = 已裁决（幂等冲突）。
    /// </returns>
    public Task<int> ResolveAsync(string workspaceId, string runId, string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken ct)
        => _coordinator.ResolveAsync(workspaceId, runId, reconciliationId, outcome, ct);

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        // 队头阻塞治理：
        // 1. handler 过滤——无 Handler 可处理的记录（含 reconciliation_handler 为 null 的仅人工记录）
        //    不占用轮询配额，等待人工 resolve 端点裁决；
        // 2. keyset 游标——单页配额内的记录处理完后推进游标，超配额记录在下轮继续扫描，
        //    避免队首记录持续占据配额导致队尾记录饥饿（慢/反复失败记录由退避门自然让位）。
        var pending = await _store.ListPendingAsync(
            PendingBatchSize, ct, _availableHandlers, _listCursorCreatedAt, _listCursorReconciliationId).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            // 已扫到队尾（无更多记录）：重置游标，下轮从头续扫（新记录可能已出现）。
            _listCursorCreatedAt = null;
            _listCursorReconciliationId = null;
            return;
        }

        // 告警钩子：超期未决（DeadlineUtc < now 且 Pending/Running）→ 告警日志。
        // ControlRoom 列表（GET /api/agents/reconciliations）同步按 DeadlineUtc 计算过期高亮。
        var now = DateTimeOffset.UtcNow;
        var overdue = pending
            .Where(r => r.DeadlineUtc.HasValue && r.DeadlineUtc.Value < now)
            .ToList();
        foreach (var record in overdue)
        {
            _logger.LogWarning(
                "ToolReconciliationWorker 告警：对账记录 {Id}（tool={Tool}, run={Run}, handler={Handler}）已超期未决（截止 {Deadline}，状态 {Status}）。" +
                "请人工介入裁决或检查 Handler 可用性。",
                record.ReconciliationId, record.ToolName, record.RunId, record.ReconciliationHandler ?? "<无>",
                record.DeadlineUtc?.ToString("O") ?? "<无截止>", record.Status);
        }

        foreach (var group in pending.GroupBy(r => (r.WorkspaceId, r.RunId)))
        {
            // Worker 接管该 Run 的对账：AwaitingReconciliation → ReconciliationRunning（best-effort）。
            // 分组键含 Workspace——跨租户同 RunId 不会互相串扰（各租户独立推进/重入队）。
            var workspaceId = group.Key.WorkspaceId;
            var runId = group.Key.RunId;
            await TransitionRunStateAsync(
                workspaceId, runId, AgentRunState.AwaitingReconciliation, AgentRunState.ReconciliationRunning, ct).ConfigureAwait(false);

            foreach (var record in group)
            {
                if (!_handlers.TryGetValue(record.ReconciliationHandler ?? string.Empty, out var handler))
                {
                    // 无匹配 Handler（未声明或未注册）→ 保持 Pending，等待人工 resolve 端点裁决。
                    _logger.LogDebug(
                        "ToolReconciliationWorker: 记录 {Id}（tool={Tool}, run={Run}）无匹配 Handler '{Handler}'，等待人工裁决。",
                        record.ReconciliationId, record.ToolName, record.RunId, record.ReconciliationHandler);
                    continue;
                }

                try
                {
                    // Worker 领取裁决租约（Pending → Running + lease/fencing）：
                    // 已持有租约后注册到共享批量心跳，由心跳循环统一续约（单次往返续整批）。
                    var lease = await _store.TryBeginAsync(
                        record.ReconciliationId, "worker:reconcile", LeaseDuration, ct).ConfigureAwait(false);
                    if (lease is null)
                    {
                        continue; // 已被并发 Worker / resolve 端点接管（仲裁权被占用）
                    }

                    using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    RegisterReconciliationLease(record.ReconciliationId, lease.LeaseToken, leaseCts);
                    try
                    {
                        await _coordinator.ReconcileWithLeaseAsync(record, lease, handler, leaseCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        UnregisterReconciliationLease(record.ReconciliationId);
                        leaseCts.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ToolReconciliationWorker: 对账记录 {Id}（tool={Tool}）Handler 执行失败，重置为 Pending 重试。",
                        record.ReconciliationId, record.ToolName);
                }
            }

            await MaybeRequeueRunAsync(workspaceId, runId, ct).ConfigureAwait(false);
        }

        // 推进 keyset 游标：单页配额已满 → 记录本页末条位置，下轮从其后续扫；
        // 不足配额 → 已扫到队尾，重置游标下轮从头续扫（新记录可能已出现）。
        var last = pending[^1];
        if (pending.Count >= PendingBatchSize)
        {
            _listCursorCreatedAt = last.CreatedAt;
            _listCursorReconciliationId = last.ReconciliationId;
        }
        else
        {
            _listCursorCreatedAt = null;
            _listCursorReconciliationId = null;
        }
    }

    /// <summary>
    /// 裁决完成后若 Run 无未裁决记录：进程内即时恢复优化。
    /// 持久化推进已由原子裁决在同一事务内完成（停车状态 → Queued，杜绝崩溃后永久停车）；
    /// 本方法仅在 Run 仍处于停车状态（历史/异常路径，原子推进未生效）时兜底恢复，
    /// 并让已 Queued 的 Run 即时入队（执行租约 CAS 保证单实例执行，Durable Claimer 兜底）。
    /// </summary>
    private async Task MaybeRequeueRunAsync(string workspaceId, string runId, CancellationToken ct)
    {
        if (await _store.HasUnresolvedForRunAsync(workspaceId, runId, ct).ConfigureAwait(false))
        {
            return; // 仍有未裁决记录 → Run 不得恢复
        }
        if (_kernelHost is null)
        {
            return;
        }

        try
        {
            var run = await _runStore.GetAsync(workspaceId, runId, ct).ConfigureAwait(false);
            if (run is null)
            {
                return;
            }
            if (run.State is AgentRunState.AwaitingReconciliation or AgentRunState.ReconciliationRunning)
            {
                // 原子推进未生效（历史/异常路径）：先回退停车状态再恢复。
                await TransitionRunStateAsync(
                    workspaceId, runId, AgentRunState.ReconciliationRunning, AgentRunState.AwaitingReconciliation, ct).ConfigureAwait(false);
            }
            await _kernelHost.StartRunAsync(run, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "ToolReconciliationWorker: Run {RunId} 对账全部完成，重新入队恢复执行。", runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ToolReconciliationWorker: Run {RunId} 对账完成后重新入队失败。", runId);
        }
    }

    /// <summary>
    /// 补偿扫描：把"停车且无未决对账记录"的 Run 恢复为 Queued（Durable Claimer 接管）。
    /// 封死历史遗留与异常路径导致的永久停车窗口。
    /// </summary>
    private async Task RecoverParkedRunsAsync(CancellationToken ct)
    {
        try
        {
            var recovered = await _store.RecoverParkedRunsAsync(100, ct).ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogInformation(
                    "ToolReconciliationWorker: 补偿扫描恢复 {Count} 个停车 Run 为 Queued。", recovered);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ToolReconciliationWorker: 补偿扫描失败（不中断后续轮询）。");
        }
    }

    /// <summary>best-effort 推进 Run 状态（CAS；不匹配或异常均忽略）。</summary>
    private async Task TransitionRunStateAsync(
        string workspaceId,
        string runId,
        AgentRunState from,
        AgentRunState to,
        CancellationToken ct)
    {
        try
        {
            var run = await _runStore.GetAsync(workspaceId, runId, ct).ConfigureAwait(false);
            if (run is null || run.State != from)
            {
                return;
            }
            await _runStore.TransitionStateAsync(workspaceId, runId, from, to, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ToolReconciliationWorker: Run {RunId} 状态推进 {From}→{To} 失败（best-effort）。", runId, from, to);
        }
    }

    /// <summary>
    /// 将裁决租约注册到共享批量心跳注册表；懒启动共享心跳循环（首个注册时）。
    /// 续约失败（租约被抢占）时由循环取消 <paramref name="leaseCts"/>。
    /// </summary>
    private void RegisterReconciliationLease(string reconciliationId, string leaseToken, CancellationTokenSource leaseCts)
    {
        _leases[reconciliationId] = new ReconciliationLeaseEntry
        {
            ReconciliationId = reconciliationId,
            LeaseToken = leaseToken,
            LeaseCts = leaseCts,
            LastConfirmedExpiresTicks = DateTimeOffset.UtcNow.Add(LeaseDuration).UtcTicks
        };

        lock (_heartbeatLock)
        {
            if (_heartbeatLoopTask is null || _heartbeatLoopTask.IsCompleted)
            {
                _heartbeatLoopCts?.Dispose();
                _heartbeatLoopCts = new CancellationTokenSource();
                _heartbeatLoopTask = RunBatchHeartbeatLoopAsync(_heartbeatLoopCts.Token);
            }
        }
    }

    /// <summary>从共享批量心跳注册表移除记录（记录处理结束后停止续约）。</summary>
    private void UnregisterReconciliationLease(string reconciliationId)
    {
        _leases.TryRemove(reconciliationId, out _);
    }

    /// <summary>停止共享批量心跳循环（worker 退出时调用）。</summary>
    private async Task StopHeartbeatLoopAsync()
    {
        lock (_heartbeatLock)
        {
            _heartbeatLoopCts?.Cancel();
        }
        if (_heartbeatLoopTask is not null)
        {
            try { await _heartbeatLoopTask.ConfigureAwait(false); }
            catch { /* 循环异常已在内部记录，此处忽略 */ }
        }
        lock (_heartbeatLock)
        {
            _heartbeatLoopCts?.Dispose();
            _heartbeatLoopCts = null;
            _heartbeatLoopTask = null;
        }
    }

    /// <summary>
    /// 共享批量心跳循环：每 <see cref="HeartbeatInterval"/> 周期通过一次
    /// <see cref="IToolReconciliationStore.RenewHeartbeatBatchAsync"/> 续约全部活跃租约，
    /// 替代"每条记录一个独立续约任务 + 每次 DB 往返"的模式（N 次往返 → 1 次）。
    /// 失败语义与旧逐条心跳一致：续约失败 → 取消对应记录；连续异常超过阈值 → 取消全部活跃记录。
    /// </summary>
    private async Task RunBatchHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        const int MaxConsecutiveFailures = 3;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var entries = _leases.Values.ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var cancelSet = new HashSet<string>(StringComparer.Ordinal);

            // 本地 watchdog：最后一次确认的租约已过期 → 取消对应记录（不发起续约）
            foreach (var entry in entries)
            {
                if (now.UtcTicks >= Interlocked.Read(ref entry.LastConfirmedExpiresTicks))
                {
                    _logger.LogWarning(
                        "ToolReconciliationWorker: 对账记录 {Id} 本地确认的租约已过期（ExpiresAt={ExpiresAt}），取消处理。",
                        entry.ReconciliationId, new DateTimeOffset(entry.LastConfirmedExpiresTicks, TimeSpan.Zero));
                    CancelReconciliationLease(entry);
                    cancelSet.Add(entry.ReconciliationId);
                }
            }

            var toRenew = entries
                .Where(e => !cancelSet.Contains(e.ReconciliationId))
                .Select(e => new ToolReconciliationHeartbeat
                {
                    ReconciliationId = e.ReconciliationId,
                    LeaseToken = e.LeaseToken
                })
                .ToList();
            if (toRenew.Count == 0)
            {
                continue;
            }

            try
            {
                var failed = await _store.RenewHeartbeatBatchAsync(toRenew, LeaseDuration, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    if (cancelSet.Contains(entry.ReconciliationId))
                    {
                        continue;
                    }
                    if (failed.Contains(entry.ReconciliationId, StringComparer.Ordinal))
                    {
                        // 租约丢失（被抢占 / 状态已改变）——取消当前记录处理，后续原子提交将返回 ArbitrationLost。
                        _logger.LogWarning(
                            "ToolReconciliationWorker: 对账记录 {Id} 租约续约失败（被抢占或状态已改变），中止处理。",
                            entry.ReconciliationId);
                        CancelReconciliationLease(entry);
                    }
                    else
                    {
                        Interlocked.Exchange(ref entry.ConsecutiveFailures, 0);
                        Interlocked.Exchange(
                            ref entry.LastConfirmedExpiresTicks,
                            DateTimeOffset.UtcNow.Add(LeaseDuration).UtcTicks);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 瞬时错误——不立即中止；连续异常超过阈值后取消全部活跃记录
                foreach (var entry in entries)
                {
                    var failures = Interlocked.Increment(ref entry.ConsecutiveFailures);
                    if (failures >= MaxConsecutiveFailures)
                    {
                        _logger.LogError(
                            "ToolReconciliationWorker: 对账记录 {Id} 心跳续约连续失败 {Failures} 次，中止处理。",
                            entry.ReconciliationId, failures);
                        CancelReconciliationLease(entry);
                    }
                }
            }
        }
    }

    /// <summary>取消记录处理（租约丢失或本地 watchdog 触发）。</summary>
    private void CancelReconciliationLease(ReconciliationLeaseEntry entry)
    {
        try { entry.LeaseCts.Cancel(); }
        catch (ObjectDisposedException) { /* 记录已处理完毕并释放了 leaseCts，忽略 */ }
    }
}
