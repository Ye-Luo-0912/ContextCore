using System.Collections.Concurrent;
using System.Threading.Channels;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E7 + 子问题 9：AgentKernelHost — 多 Session 隔离的 Kernel Host（生产化）
//
// 替代旧单例 Kernel 平面的全局状态，实现真正的多 Session 隔离：
//   1. 每个 Run 拥有独立的 AgentRunActor 实例（per-run 隔离）；
//   2. 通过 IServiceProvider 解析 Actor 所需依赖（与 DI 容器集成）；
//   3. ConcurrentDictionary 跟踪活跃 Run（key = workspaceId:runId）；
//   4. StartRunAsync 创建 Actor 并写入 bounded Channel（fire-and-forget）；
//   5. GetRunStatusAsync 查询 Run 状态（通过 IAgentRunStore）；
//   6. CancelRunAsync 取消指定 Run（TransitionState → Cancelled + CTS 触发）。
//
// 子问题 9 生产化增强：
//   - HA Run Lease：P0-4 方案 A — Worker 从 Channel 取到 Run + 获得执行槽之后再
//     IAgentRunLease.TryAcquireAsync 获取租约，然后立即启动 heartbeat；入队前不获取 lease。
//     heartbeat 续租失败时 CancellationTokenSource.Cancel() 取消 Actor（防止双执行）；
//     处理完成后 Release；
//   - 全局并发上限：SemaphoreSlim(MaxGlobalRuns)；
//   - Workspace 级并发上限：per-workspace SemaphoreSlim(MaxWorkspaceRuns)；
//
// Learning Loop Durable Outbox 增强（替代 Task.Factory.StartNew）：
//   - bounded Channel + 固定 worker 池：消除每 Run 一个 Task 的 Task 风暴风险；
//   - 队列深度管理：ChannelCapacity 上限，超过后 StartRunAsync 拒绝入队（拒绝策略）；
//   - 公平调度：FIFO Channel 保证先入队的 Run 先被 worker 拉取；
//   - 优雅 drain：IAsyncDisposable.DisposeAsync 完成 Channel 并等待 worker 排空（DrainTimeout）。
// ===========================================================================

/// <summary>
/// 任务 E7 + 子问题 9：多 Session 隔离的 Kernel Host（生产化）。
/// 替代旧单例 Kernel 平面的全局状态，
/// 为每个 Run 创建独立的 <see cref="AgentRunActor"/> 实例，实现真正的多 Session 隔离。
/// </summary>
/// <remarks>
/// Learning Loop Durable Outbox 增强后，Run 执行通过 bounded Channel + 固定 worker 池调度，
/// 替代原 Task.Factory.StartNew 模式。Channel 提供队列深度管理与拒绝策略；
/// worker 池提供固定并发上限与优雅 drain。
/// </remarks>
public sealed class AgentKernelHost : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentRunStore _runStore;
    private readonly IAgentRunLease? _runLease;
    private readonly AgentHostOptions _options;
    private readonly ILogger<AgentKernelHost>? _logger;
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);
    // 共享批量心跳：租约注册表（runId → 活跃租约条目）。
    // 所有启用租约的 Run 在此登记，由单一心跳循环每周期批量续约一次，
    // 替代"每个 Run 一个独立续约任务"（N 个 Run = N 次 DB 往返/周期 → 1 次）。
    private readonly ConcurrentDictionary<string, ActiveLeaseEntry> _leaseRegistry = new(StringComparer.Ordinal);
    private readonly object _heartbeatLock = new();
    private Task? _heartbeatLoopTask;
    private CancellationTokenSource? _heartbeatLoopCts;
    // workspace 信号量改为 LRU 条目（含信号量 + 最后访问时间 + MaxCount），避免无界增长
    private readonly ConcurrentDictionary<string, WorkspaceSemaphoreEntry> _workspaceSemaphores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _globalSemaphore;
    // workspace 信号量 LRU 最大条目数（超过时淘汰空闲条目）
    private const int WorkspaceSemaphoreMaxEntries = 128;

    // bounded Channel + 固定 worker 池（替代 Task.Factory.StartNew）
    private readonly Channel<RunWorkItem> _channel;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _workerCts;
    private readonly int _workerCount;
    private int _disposed;

    /// <summary>
    /// 构造 Kernel Host。
    /// </summary>
    /// <param name="serviceProvider">DI 容器（用于解析 Actor 依赖）。</param>
    /// <param name="runStore">Run 元数据存储（用于查询状态）。</param>
    /// <param name="runLease">子问题 9：Run 租约（null = 单节点模式，不竞争租约）。</param>
    /// <param name="options">子问题 9：Host 配置（并发上限 / 租约参数 / Channel 容量）；null = 默认值。</param>
    /// <param name="logger">日志（null = 静默）。</param>
    public AgentKernelHost(
        IServiceProvider serviceProvider,
        IAgentRunStore runStore,
        IAgentRunLease? runLease = null,
        AgentHostOptions? options = null,
        ILogger<AgentKernelHost>? logger = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _runLease = runLease;
        _options = options ?? new AgentHostOptions();
        _logger = logger;

        // 校验 LeaseDuration >= 3 × HeartbeatInterval，确保续租窗口足够，
        // 否则 Actor 可能在租约过期后仍执行副作用（本地 watchdog 来不及触发）。
        if (_options.LeaseEnabled && _options.LeaseDuration < TimeSpan.FromTicks(_options.HeartbeatInterval.Ticks * 3))
        {
            throw new InvalidOperationException("LeaseDuration 必须 >= 3 × HeartbeatInterval，否则 Actor 可能在租约过期后仍执行。");
        }

        var globalMax = _options.MaxGlobalRuns > 0 ? _options.MaxGlobalRuns : 100;
        _globalSemaphore = new SemaphoreSlim(globalMax, globalMax);

        // bounded Channel：队列深度管理 + 拒绝策略。
        var capacity = _options.ChannelCapacity > 0 ? _options.ChannelCapacity : 256;
        _channel = Channel.CreateBounded<RunWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        // 固定 worker 池：默认 = MaxGlobalRuns（worker 阻塞在 SemaphoreSlim 等待槽位，不成为瓶颈）。
        _workerCount = _options.WorkerCount > 0 ? _options.WorkerCount : globalMax;
        _workerCts = new CancellationTokenSource();
        _workers = new Task[_workerCount];
        for (var i = 0; i < _workerCount; i++)
        {
            var workerId = i;
            _workers[i] = RunWorkerLoopAsync(workerId, _workerCts.Token);
        }
    }

    /// <summary>
    /// 为指定 Run 创建 Actor 并入队执行（fire-and-forget）。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示入队完成的任务（不等待执行完成）。</returns>
    /// <exception cref="InvalidOperationException">Channel 已关闭（Host 已 Dispose）或队列已满（拒绝策略）。</exception>
    /// <remarks>
    /// 方案 A：入队前不获取 lease。Worker 从 Channel 取到 Run + 获得执行槽之后再
    /// <see cref="RunWithLeaseAndConcurrencyAsync"/> 中 Acquire Lease，然后立即启动 heartbeat。
    /// 避免排队期间 lease 过期导致 heartbeat 无法续租、双实例并发执行同一 Run。
    /// </remarks>
    public async Task StartRunAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // 子问题 9：本地活跃 Run 去重（同进程内不重复启动）
        var key = ActiveRunKey(run.WorkspaceId, run.RunId);
        if (_activeRuns.ContainsKey(key))
        {
            // 已存在活跃 Run → 不重复启动
            return;
        }

        // 方案 A：入队前不获取 lease；Worker 从 Channel 取到 Run + 获得执行槽之后再 Acquire Lease。
        // 避免排队期间 lease 过期导致 heartbeat 无法续租、双实例并发执行同一 Run。
        var actor = CreateActor();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRun = new ActiveRun(actor, cts, Lease: null);

        if (!_activeRuns.TryAdd(key, activeRun))
        {
            // 并发竞争：其他线程先添加 → 退出
            cts.Dispose();
            return;
        }

        // 入队到 bounded Channel（替代 Task.Factory.StartNew）。
        // Channel 满时 WaitToWriteAsync 会阻塞直到有槽位；若需立即拒绝可改为 TryWrite + 检查返回值。
        // 这里使用 WriteAsync 以提供背压（调用方等待入队完成）。
        var workItem = new RunWorkItem(run, key, activeRun);
        try
        {
            await _channel.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Host 已 Dispose → 清理资源
            _activeRuns.TryRemove(key, out _);
            cts.Dispose();
            throw new InvalidOperationException("AgentKernelHost 已关闭，无法入队新的 Run。");
        }
    }

    /// <summary>
    /// 固定 worker 循环：从 Channel 读取 Run work item，调用 RunWithLeaseAndConcurrencyAsync。
    /// </summary>
    private async Task RunWorkerLoopAsync(int workerId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RunWithLeaseAndConcurrencyAsync(item.Run, item.Key, item.ActiveRun)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 单个 Run 执行异常不应中断 worker 循环（其他 Run 仍需处理）。
                    _logger?.LogError(ex,
                        "Worker {WorkerId} encountered exception while executing Run {RunId}.",
                        workerId, item.Run.RunId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AgentKernelHost worker {WorkerId} crashed.", workerId);
        }
    }

    /// <summary>
    /// 子问题 9 + P0-4 + P0-5：带租约心跳 + 并发上限的 Run 执行包装。
    /// </summary>
    /// <remarks>
    /// 方案 A：Worker 从 Channel 取到 Run + 获得全局/Workspace 执行槽之后再 Acquire Lease，
    /// 然后立即启动 heartbeat。避免排队期间 lease 过期导致 heartbeat 无法续租。
    /// heartbeat 续租失败时通过 <see cref="CancellationTokenSource.Cancel"/> 取消 Actor，
    /// 防止 lease 被抢占后当前实例继续执行副作用（双执行）。
    /// 所有 permit acquisition 用标志位包在一个 try/finally 中，
    /// 确保任何步骤取消或抛异常时已获取的 permit 都能被释放（避免 permit 泄漏）。
    /// </remarks>
    private async Task RunWithLeaseAndConcurrencyAsync(AgentRun run, string key, ActiveRun activeRun)
    {
        // 标志位跟踪每个 permit 是否已获取，finally 中按标志位释放
        var globalAcquired = false;
        var workspaceAcquired = false;
        SemaphoreSlim? workspaceSemaphore = null;
        LeasedAgentRun? lease = null;

        try
        {
            // 子问题 9：全局并发上限
            await _globalSemaphore.WaitAsync(activeRun.Cts.Token).ConfigureAwait(false);
            globalAcquired = true;

            // 子问题 9：Workspace 级并发上限
            // 通过 GetOrCreateWorkspaceSemaphore 支持潜在 LRU 淘汰避免无界增长
            workspaceSemaphore = GetOrCreateWorkspaceSemaphore(run.WorkspaceId);
            await workspaceSemaphore.WaitAsync(activeRun.Cts.Token).ConfigureAwait(false);
            workspaceAcquired = true;

            // 方案 A：获得执行槽之后再 Acquire Lease（入队前不获取，避免排队期间过期）
            if (_options.LeaseEnabled && _runLease is not null)
            {
                var owner = _options.Owner ?? BuildDefaultOwner();
                lease = await _runLease.TryAcquireAsync(
                    run.RunId, _options.LeaseDuration, owner, activeRun.Cts.Token).ConfigureAwait(false);
                if (lease is null)
                {
                    // 租约被其他实例持有 → 释放执行槽并退出（其他实例正在处理）
                    // 释放由 finally 块按标志位处理，此处只需 return
                    _logger?.LogDebug("Run {RunId} 租约被其他实例持有，跳过执行。", run.RunId);
                    return;
                }
            }

            // 子问题 9：将租约登记到共享批量心跳（续约失败时取消 Actor 防止双执行）
            // 传入 activeRun.Cts 以便续租失败时取消 Actor（防止双执行）
            Func<DateTimeOffset?>? leaseExpiryProvider = null;
            if (lease is not null && _runLease is not null)
            {
                RegisterLease(lease, activeRun.Cts);
                var leaseEntry = _leaseRegistry[lease.RunId];
                // 读取共享心跳维护的最新确认租约过期时间，让 Tool 副作用 fence
                // 与数据库 lease_expires_at 保持一致（每次续约后自动前移）。
                leaseExpiryProvider = () =>
                {
                    var ticks = Interlocked.Read(ref leaseEntry.LastConfirmedExpiresTicks);
                    return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
                };
            }

            // 将 leaseToken + fencingToken 传给 Actor，Actor 在每次副作用操作（FlushPendingEventsAsync）
            // 时带上，Postgres 实现在 WHERE 子句中校验 lease 仍由当前实例持有。
            try
            {
                await activeRun.Actor.ExecuteAsync(
                    run, activeRun.Cts.Token,
                    lease?.LeaseToken, lease?.FencingToken, leaseExpiryProvider).ConfigureAwait(false);
            }
            catch
            {
                // Actor 内部已处理异常并记录 RunFailed；此处仅兜底防吞异常
            }
        }
        finally
        {
            // 先从共享心跳注册表移除（停止续约；避免循环对已释放的 CTS 发起取消）
            if (lease is not null && _runLease is not null)
            {
                UnregisterLease(run.RunId);
            }

            // 按标志位释放 permit，确保任何路径下已获取的 permit 都被释放
            // workspaceAcquired 为 true 时 workspaceSemaphore 必不为 null
            if (workspaceAcquired)
            {
                workspaceSemaphore!.Release();
            }
            if (globalAcquired)
            {
                _globalSemaphore.Release();
            }

            // 释放租约
            await TryReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false);

            // 从活跃 Run 移除
            if (_activeRuns.TryRemove(key, out var removed))
            {
                removed.Cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 将租约登记到共享批量心跳注册表（首次登记时启动共享心跳循环）。
    /// </summary>
    private void RegisterLease(LeasedAgentRun lease, CancellationTokenSource actorCts)
    {
        _leaseRegistry[lease.RunId] = new ActiveLeaseEntry
        {
            RunId = lease.RunId,
            LeaseToken = lease.LeaseToken,
            ActorCts = actorCts,
            // 初始为租约获取时的 ExpiresAt；续约成功后更新为 UtcNow + extension
            LastConfirmedExpiresTicks = lease.ExpiresAt.UtcTicks
        };

        // 懒启动共享心跳循环（租约存在期间持续运行；注册表为空时循环空转，不发起续约）
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

    /// <summary>从共享批量心跳注册表移除租约（Run 结束后停止续约）。</summary>
    private void UnregisterLease(string runId)
    {
        _leaseRegistry.TryRemove(runId, out _);
    }

    /// <summary>
    /// 共享批量心跳循环：每 <see cref="AgentHostOptions.HeartbeatInterval"/> 周期
    /// 通过一次 <see cref="IAgentRunLease.RenewBatchAsync"/> 调用续约全部活跃租约，
    /// 替代"每个 Run 一个独立续约任务 + 每次 DB 往返"的模式（N 次往返 → 1 次）。
    /// </summary>
    /// <remarks>
    /// 失败语义与旧的每 Run 心跳一致：
    ///   - 续约失败（租约被抢占/过期）→ 取消对应 Actor，防止双执行；
    ///   - 连续续约异常超过阈值（数据库不可达）→ 取消对应 Actor；
    ///   - 本地 watchdog：最后一次确认的租约 ExpiresAt 已过 → 立即取消 Actor
    ///     （续约异常不延长本地期限，防止租约实际已过期仍执行副作用）。
    /// </remarks>
    private async Task RunBatchHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var interval = _options.HeartbeatInterval > TimeSpan.Zero
            ? _options.HeartbeatInterval
            : TimeSpan.FromSeconds(30);
        var extension = _options.LeaseDuration > TimeSpan.Zero
            ? _options.LeaseDuration
            : TimeSpan.FromMinutes(10);
        const int MaxConsecutiveFailures = 3;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var entries = _leaseRegistry.Values.ToList();
            if (entries.Count == 0 || _runLease is null)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var cancelSet = new HashSet<string>(StringComparer.Ordinal);

            // 本地 watchdog：最后一次确认的租约已过期 → 取消 Actor（不发起续约）
            foreach (var entry in entries)
            {
                if (now.UtcTicks >= Interlocked.Read(ref entry.LastConfirmedExpiresTicks))
                {
                    _logger?.LogError(
                        "Run {RunId} 本地确认的租约已过期（ExpiresAt={ExpiresAt}），取消 Actor。",
                        entry.RunId, new DateTimeOffset(entry.LastConfirmedExpiresTicks, TimeSpan.Zero));
                    CancelActor(entry.ActorCts);
                    cancelSet.Add(entry.RunId);
                }
            }

            // 批量续约剩余租约（单次 DB 往返）
            var toRenew = entries
                .Where(e => !cancelSet.Contains(e.RunId))
                .Select(e => new AgentRunLeaseRenewal { RunId = e.RunId, LeaseToken = e.LeaseToken })
                .ToList();

            if (toRenew.Count > 0)
            {
                try
                {
                    var renewFailed = await _runLease.RenewBatchAsync(toRenew, extension, cancellationToken).ConfigureAwait(false);
                    foreach (var entry in entries)
                    {
                        if (cancelSet.Contains(entry.RunId))
                        {
                            continue;
                        }
                        if (renewFailed.Contains(entry.RunId, StringComparer.Ordinal))
                        {
                            // 丢租后旧 owner 不写任何终态（无 fencing token 的写入会破坏新 owner 状态），
                            // 仅本地取消 Actor 防止双执行。Run 保持非终态由 RecoveryWorker 重新入队恢复
                            // （resume from checkpoint）；超时无人接管时由 RecoveryWorker 原子标记 LeaseLost。
                            _logger?.LogWarning(
                                "Run {RunId} 租约续约失败，其他实例可能已接管；取消 Actor 执行。", entry.RunId);
                            CancelActor(entry.ActorCts);
                            cancelSet.Add(entry.RunId);
                        }
                        else
                        {
                            // 续约成功 → 重置连续异常计数 + 更新最后确认的 ExpiresAt
                            Interlocked.Exchange(ref entry.ConsecutiveFailures, 0);
                            Interlocked.Exchange(
                                ref entry.LastConfirmedExpiresTicks,
                                DateTimeOffset.UtcNow.Add(extension).UtcTicks);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // 连续异常 watchdog：超过阈值后取消 Actor，防止无 lease 的副作用
                    foreach (var entry in entries)
                    {
                        if (cancelSet.Contains(entry.RunId))
                        {
                            continue;
                        }
                        var failures = Interlocked.Increment(ref entry.ConsecutiveFailures);
                        _logger?.LogWarning("Run {RunId} heartbeat 续约异常（连续 {Count}/{Max}）。",
                            entry.RunId, failures, MaxConsecutiveFailures);
                        if (failures >= MaxConsecutiveFailures)
                        {
                            _logger?.LogError(
                                "Run {RunId} heartbeat 连续 {Count} 次异常，触发本地 watchdog 取消 Actor。",
                                entry.RunId, failures);
                            CancelActor(entry.ActorCts);
                            cancelSet.Add(entry.RunId);
                        }
                    }
                }
            }

            // 移除已取消的条目（Run 的 finally 也会移除；此处提前清理避免下一周期重复续约/重复取消）
            foreach (var entry in entries)
            {
                if (cancelSet.Contains(entry.RunId))
                {
                    _leaseRegistry.TryRemove(entry.RunId, out _);
                }
            }
        }
    }

    /// <summary>取消 Actor（CTS 已释放时静默忽略）。</summary>
    private static void CancelActor(CancellationTokenSource actorCts)
    {
        try
        {
            actorCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Actor 已结束，CTS 已释放 — 无需处理
        }
    }

    /// <summary>
    /// 获取或创建 workspace 级信号量，并在字典超阈值时触发 LRU 淘汰避免无界增长。
    /// </summary>
    private SemaphoreSlim GetOrCreateWorkspaceSemaphore(string workspaceId)
    {
        var maxWs = _options.MaxWorkspaceRuns > 0 ? _options.MaxWorkspaceRuns : 10;
        var entry = _workspaceSemaphores.GetOrAdd(workspaceId, _ => new WorkspaceSemaphoreEntry
        {
            Semaphore = new SemaphoreSlim(maxWs, maxWs),
            MaxCount = maxWs,
            LastAccessTicks = DateTimeOffset.UtcNow.Ticks
        });
        Interlocked.Exchange(ref entry.LastAccessTicks, DateTimeOffset.UtcNow.Ticks);

        // LRU 淘汰 — 字典超过阈值时清理空闲信号量
        if (_workspaceSemaphores.Count > WorkspaceSemaphoreMaxEntries)
        {
            EvictIdleWorkspaceSemaphores();
        }

        return entry.Semaphore;
    }

    /// <summary>
    /// LRU 淘汰空闲 workspace 信号量。
    /// 仅移除 CurrentCount == MaxCount（无人等待）的最旧条目，避免无界增长。
    /// 不立即 Dispose 移除的 SemaphoreSlim — 可能有线程刚拿到引用尚未 WaitAsync，
    /// 由 GC 回收避免 ObjectDisposedException 竞态。
    /// </summary>
    private void EvictIdleWorkspaceSemaphores()
    {
        if (_workspaceSemaphores.Count <= WorkspaceSemaphoreMaxEntries)
        {
            return;
        }

        var toEvict = _workspaceSemaphores.Count - WorkspaceSemaphoreMaxEntries;
        // 物化候选列表避免在枚举期间修改字典抛 InvalidOperationException
        var candidates = _workspaceSemaphores
            .Where(x => x.Value.Semaphore.CurrentCount == x.Value.MaxCount)
            .OrderBy(x => Interlocked.Read(ref x.Value.LastAccessTicks))
            .Take(toEvict)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in candidates)
        {
            _workspaceSemaphores.TryRemove(key, out _);
        }
    }

    /// <summary>尝试释放租约（失败静默忽略）。</summary>
    private async Task TryReleaseLeaseAsync(LeasedAgentRun? lease, CancellationToken cancellationToken)
    {
        if (lease is null || _runLease is null)
        {
            return;
        }
        try
        {
            await _runLease.ReleaseAsync(lease.RunId, lease.LeaseToken, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 释放失败静默忽略（租约会自然过期）
        }
    }

    /// <summary>生成默认 owner 标识。</summary>
    private static string BuildDefaultOwner()
    {
        try
        {
            var machine = Environment.MachineName;
            return $"host-{machine}-{Guid.NewGuid():N}".Substring(0, Math.Min(64, $"host-{machine}-{Guid.NewGuid():N}".Length));
        }
        catch
        {
            return $"host-{Guid.NewGuid():N}";
        }
    }

    /// <summary>
    /// 查询指定 Run 的状态。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Run 元数据（含当前状态）；不存在返回 null。</returns>
    public async Task<AgentRun?> GetRunStatusAsync(
        string workspaceId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        return await _runStore.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取消指定 Run（TransitionState → Cancelled + 触发 CTS）。
    /// </summary>
    /// <param name="workspaceId">Workspace ID。</param>
    /// <param name="runId">Run ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否成功发起取消（Run 不存在或已终态时返回 false）。</returns>
    public async Task<bool> CancelRunAsync(
        string workspaceId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var run = await _runStore.GetAsync(workspaceId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return false;
        }

        if (AgentRunStateMachine.IsTerminalState(run.State))
        {
            return false;
        }

        // 触发 Actor 内部取消（ExecuteAsync 的 OperationCanceledException 路径）
        var key = ActiveRunKey(workspaceId, runId);
        if (_activeRuns.TryGetValue(key, out var active))
        {
            try
            {
                active.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS 已被清理（Run 刚结束）
            }
        }

        // 同时推进状态（确保即使 Actor 未感知 CTS，状态也推进到 Cancelled）
        try
        {
            await _runStore.TransitionStateAsync(
                workspaceId, runId, run.State, AgentRunState.Cancelled, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // CAS 失败 = 状态已被其他实例推进（可能 Actor 已先一步处理取消）→ 非致命
        }

        return true;
    }

    /// <summary>当前活跃的 Run 数量（诊断/监控用）。</summary>
    public int ActiveRunCount => _activeRuns.Count;

    /// <summary>通过 DI 容器创建 Actor 实例（解析所有可注入依赖）。</summary>
    private AgentRunActor CreateActor()
    {
        // 解析必需依赖（构造函数非空参数）
        var eventStore = _serviceProvider.GetService(typeof(IAgentRunEventStore)) as IAgentRunEventStore
            ?? throw new InvalidOperationException("IAgentRunEventStore 未注册到 DI 容器。");
        var loopPolicy = _serviceProvider.GetService(typeof(IAgentLoopPolicy)) as IAgentLoopPolicy
            ?? new DefaultAgentLoopPolicy();
        var toolDispatcher = _serviceProvider.GetService(typeof(IToolDispatcher)) as IToolDispatcher
            ?? throw new InvalidOperationException("IToolDispatcher 未注册到 DI 容器。");

        // 解析可选依赖（null 时 Actor 优雅降级）
        var modelTransport = _serviceProvider.GetService(typeof(IAgentModelTransport)) as IAgentModelTransport;
        var toolCallValidator = _serviceProvider.GetService(typeof(IAgentToolCallValidator)) as IAgentToolCallValidator;
        var approvalGate = _serviceProvider.GetService(typeof(IAgentApprovalGate)) as IAgentApprovalGate;
        // 解析 IAgentApprovalStore（让 Actor 用正确 workspaceId 创建审批记录，而非 Gate 内部的 "default"）
        var approvalStore = _serviceProvider.GetService(typeof(IAgentApprovalStore)) as IAgentApprovalStore;
        var checkpointFactory = _serviceProvider.GetService(typeof(IAgentCheckpointFactory)) as IAgentCheckpointFactory;
        var decisionRuntime = _serviceProvider.GetService(typeof(IContextDecisionRuntime)) as IContextDecisionRuntime;
        // 子问题 4：解析 IAgentCheckpointStore
        var checkpointStore = _serviceProvider.GetService(typeof(IAgentCheckpointStore)) as IAgentCheckpointStore;
        // 子问题 5：解析 IDurableToolExecutor
        var durableToolExecutor = _serviceProvider.GetService(typeof(IDurableToolExecutor)) as IDurableToolExecutor;
        // 解析 IAgentModelContextProjector
        var modelContextProjector = _serviceProvider.GetService(typeof(IAgentModelContextProjector)) as IAgentModelContextProjector;

        return new AgentRunActor(
            _runStore,
            eventStore,
            modelTransport,
            loopPolicy,
            toolDispatcher,
            toolCallValidator,
            approvalGate,
            approvalStore,
            checkpointFactory,
            decisionRuntime,
            checkpointStore,
            durableToolExecutor,
            modelContextProjector);
    }

    private static string ActiveRunKey(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";

    /// <summary>
    /// 优雅 drain：完成 Channel（不再接受新 Run），等待 worker 排空当前队列（最多 DrainTimeout）。
    /// 由 DI 容器在 Singleton 释放时自动调用。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 信号 Channel 不再接受新写入，让 worker 排空剩余项。
        _channel.Writer.TryComplete();

        var drainTimeout = _options.DrainTimeout > TimeSpan.Zero
            ? _options.DrainTimeout
            : TimeSpan.FromSeconds(30);

        try
        {
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(_workerCts.Token);
            drainCts.CancelAfter(drainTimeout);
            await Task.WhenAll(_workers).WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 排空超时 — 强制取消 worker。
            _logger?.LogWarning(
                "AgentKernelHost drain timed out after {Timeout}s; {WorkerCount} workers still running.",
                drainTimeout.TotalSeconds, _workerCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AgentKernelHost drain encountered exception.");
        }
        finally
        {
            // 停止共享批量心跳循环（若有运行中的租约心跳）
            if (_heartbeatLoopCts is not null)
            {
                lock (_heartbeatLock)
                {
                    _heartbeatLoopCts?.Cancel();
                    _heartbeatLoopCts?.Dispose();
                    _heartbeatLoopCts = null;
                }
            }
            _workerCts.Cancel();
            _workerCts.Dispose();
        }
    }

    /// <summary>活跃 Run 内部跟踪条目（Actor + CTS + Lease）。</summary>
    private sealed record ActiveRun(
        AgentRunActor Actor,
        CancellationTokenSource Cts,
        LeasedAgentRun? Lease);

    /// <summary>
    /// 共享批量心跳注册表条目：租约 + 对应 Actor 的 CTS + 本地 watchdog 状态。
    /// 续约成功/失败由共享心跳循环更新（多线程访问，计数器用 Interlocked）。
    /// </summary>
    private sealed class ActiveLeaseEntry
    {
        public required string RunId { get; init; }
        public required string LeaseToken { get; init; }
        public required CancellationTokenSource ActorCts { get; init; }
        /// <summary>最后一次确认的租约过期时间（UTC ticks；续约异常时不更新）。</summary>
        public long LastConfirmedExpiresTicks;
        /// <summary>连续续约异常计数（超过阈值取消 Actor）。</summary>
        public int ConsecutiveFailures;
    }

    /// <summary>Channel work item（Run + key + ActiveRun）。</summary>
    private sealed record RunWorkItem(AgentRun Run, string Key, ActiveRun ActiveRun);

    /// <summary>
    /// workspace 信号量 LRU 条目（含信号量与最后访问时间，用于淘汰空闲条目）。
    /// </summary>
    private sealed class WorkspaceSemaphoreEntry
    {
        public required SemaphoreSlim Semaphore { get; init; }
        public required int MaxCount { get; init; }
        public long LastAccessTicks;
    }
}
