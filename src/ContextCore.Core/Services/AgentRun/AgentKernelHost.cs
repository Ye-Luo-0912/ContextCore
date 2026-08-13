using System.Collections.Concurrent;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// 多 Session 隔离的 Kernel Host：每个 Run 一个 AgentRunActor。
// 通过 IServiceProvider 解析依赖；用优先级队列 + 固定 worker 池调度，而不是每 Run 一个 Task。
// 可选租约：取到执行槽后再 TryAcquire，心跳失败则取消 Actor，避免双执行。

/// <summary>
/// 多 Session 隔离的 Kernel Host（生产化）。
/// 替代旧单例 Kernel 平面的全局状态，
/// 为每个 Run 创建独立的 <see cref="AgentRunActor"/> 实例，实现真正的多 Session 隔离。
/// </summary>
/// <remarks>
/// 调度用有界优先级队列和固定 worker 池，避免每个 Run 起一个 Task。
/// 替代原 Task.Factory.StartNew / FIFO Channel 模式。队列提供深度管理、优先级排序与拒绝策略；
/// worker 池提供固定并发上限与优雅 drain。
/// </remarks>
public sealed class AgentKernelHost : IAsyncDisposable, IAgentRunScheduler
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
    private readonly ConcurrentDictionary<TenantRunKey, ActiveLeaseEntry> _leaseRegistry = new();
    private readonly object _heartbeatLock = new();
    private Task? _heartbeatLoopTask;
    private CancellationTokenSource? _heartbeatLoopCts;
    // workspace 信号量改为 LRU 条目（含信号量 + 最后访问时间 + MaxCount），避免无界增长
    private readonly ConcurrentDictionary<string, WorkspaceSemaphoreEntry> _workspaceSemaphores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _globalSemaphore;
    // workspace 信号量 LRU 最大条目数（超过时淘汰空闲条目）
    private const int WorkspaceSemaphoreMaxEntries = 128;

    // weighted fair queue + 固定 worker 池（替代 Task.Factory.StartNew / FIFO Channel）
    // 调度按优先级出队（高优先级先出），同优先级保持入队顺序。
    // 公平性：队列按 Workspace 分桶（weighted fair queue——出队选
    // min(ServiceCount / Weight) 的 Workspace，等权重 = 严格轮转），单 Workspace 排队上限
    // 防止独占全部槽位；出队时按 aged priority（原始优先级 + 排队时长老化提升）取桶内最优，
    // 防止低优先级饿死；保留容量（ReservedQueueCapacity）保障高优先级/系统路径在饱和时仍能入队；
    // 排队等待超 SLO 计入 CoreMetrics 排队指标。
    // 背压由 _queueCapacity（常规池）+ _reservedCapacity（保留池）两个 SemaphoreSlim 表达——
    // TryEnqueue 非阻塞尝试获取槽位，满时立即返回 QueueFull（与旧 Channel.TryWrite 语义一致）；
    // _queueSignal 通知 worker 有新任务（计数 = 队列中待执行数）。
    private readonly object _queueLock = new();
    private readonly Dictionary<string, WorkspaceQueueEntry> _workspaceQueues = new(StringComparer.Ordinal);
    private int _totalQueued;
    private readonly SemaphoreSlim _queueCapacity;
    private readonly SemaphoreSlim? _reservedCapacity;
    private readonly SemaphoreSlim _queueSignal;
    private readonly Task[] _workers;
    private readonly CancellationTokenSource _workerCts;
    private readonly int _workerCount;
    private int _disposed;

    /// <summary>Workspace 队列字典上限（超过时淘汰空闲条目，保证长期运行工作集有界）。</summary>
    private const int WorkspaceQueueMaxEntries = 256;

    /// <summary>
    /// 构造 Kernel Host。
    /// </summary>
    /// <param name="serviceProvider">DI 容器（用于解析 Actor 依赖）。</param>
    /// <param name="runStore">Run 元数据存储（用于查询状态）。</param>
    /// <param name="runLease">Run 租约（null = 单节点模式，不竞争租约）。</param>
    /// <param name="options">Host 配置（并发上限 / 租约参数 / Channel 容量）；null = 默认值。</param>
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

        // bounded 队列：常规池（ChannelCapacity - 保留容量）+ 保留池（高优先级专用）。
        // 保留容量自动 = clamp(max(8, ChannelCapacity/16), 0, ChannelCapacity/2)，防止小容量被保留池吞掉。
        var capacity = _options.ChannelCapacity > 0 ? _options.ChannelCapacity : 256;
        var reserved = ComputeReservedCapacity(capacity);
        _queueCapacity = new SemaphoreSlim(capacity - reserved, capacity - reserved);
        _reservedCapacity = reserved > 0 ? new SemaphoreSlim(reserved, reserved) : null;
        _queueSignal = new SemaphoreSlim(0, int.MaxValue);

        // 固定 worker 池：默认 = MaxGlobalRuns（worker 阻塞在 SemaphoreSlim 等待槽位，不成为瓶颈）。
        _workerCount = _options.WorkerCount > 0 ? _options.WorkerCount : globalMax;
        _workerCts = new CancellationTokenSource();
        // WorkersEnabled=false 时不启动 worker（宿主仅登记入队状态，不执行 Run）——
        // 供仅验证 HTTP 链路/持久化的宿主场景使用（如集成测试）。
        if (_options.WorkersEnabled)
        {
            _workers = new Task[_workerCount];
            for (var i = 0; i < _workerCount; i++)
            {
                var workerId = i;
                _workers[i] = RunWorkerLoopAsync(workerId, _workerCts.Token);
            }
        }
        else
        {
            _workers = Array.Empty<Task>();
        }
    }

    /// <summary>
    /// 为指定 Run 创建 Actor 并入队执行（fire-and-forget）。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示入队完成的任务（不等待执行完成）。</returns>
    /// <exception cref="InvalidOperationException">Host 已关闭（<see cref="AgentRunEnqueueStatus.Closed"/>）。</exception>
    /// <remarks>
    /// 方案 A：入队前不获取 lease。Worker 从队列取到 Run + 获得执行槽之后再
    /// <see cref="RunWithLeaseAndConcurrencyAsync"/> 中 Acquire Lease，然后立即启动 heartbeat。
    /// 避免排队期间 lease 过期导致 heartbeat 无法续租、双实例并发执行同一 Run。
    /// 队列满时不阻塞（非致命：Run 已持久化，RecoveryWorker 稍后接管）。
    /// </remarks>
    public async Task StartRunAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var result = await TryEnqueueAsync(run, cancellationToken).ConfigureAwait(false);
        if (result.Status == AgentRunEnqueueStatus.Closed)
        {
            throw new InvalidOperationException("AgentKernelHost 已关闭，无法入队新的 Run。");
        }
    }

    /// <inheritdoc />
    /// <summary>
    /// 非阻塞入队：队列满 / Host 已关闭时立即返回对应状态，不无限等待槽位。
    /// 所有失败路径（入队失败 / 重复竞争 / 关闭）都会清理 <see cref="_activeRuns"/> 条目与 CTS，
    /// 不残留活跃 Run 跟踪。
    /// </summary>
    public ValueTask<AgentRunEnqueueResult> TryEnqueueAsync(AgentRun run, CancellationToken cancellationToken = default)
        => EnqueueInternalAsync(run, TimeSpan.Zero, cancellationToken);

    /// <inheritdoc />
    /// <summary>
    /// 带超时的入队：等待队列槽位最多 <paramref name="timeout"/>（TimeSpan.Zero = 非阻塞，
    /// 等价于 <see cref="TryEnqueueAsync"/>）。超时仍无槽位时返回 QueueFull（不无限等待）；
    /// Host 已关闭返回 Closed。供内部调度/恢复路径在队列饱和时有界等待，提高吞吐。
    /// </summary>
    public ValueTask<AgentRunEnqueueResult> EnqueueAsync(AgentRun run, TimeSpan timeout, CancellationToken cancellationToken = default)
        => EnqueueInternalAsync(run, timeout, cancellationToken);

    /// <summary>
    /// 入队核心实现（TryEnqueueAsync 与 EnqueueAsync 的公共路径）。
    /// timeout=0 时非阻塞获取槽位（立即返回 QueueFull）；timeout&gt;0 时等待槽位最多
    /// <paramref name="timeout"/>，超时/取消仍无槽位则返回 QueueFull（绝不无限等待）。
    /// 所有失败路径清理 <see cref="_activeRuns"/> 条目与 CTS，不残留活跃 Run 跟踪。
    /// </summary>
    private async ValueTask<AgentRunEnqueueResult> EnqueueInternalAsync(
        AgentRun run, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var capacity = _options.ChannelCapacity > 0 ? _options.ChannelCapacity : 256;
        var key = ActiveRunKey(run.WorkspaceId, run.RunId);

        // 本地活跃 Run 去重（同进程内不重复启动）
        if (_activeRuns.ContainsKey(key))
        {
            return BuildEnqueueResult(
                AgentRunEnqueueStatus.AlreadyActive, run.RunId, capacity,
                "同进程内已有活跃 Run，跳过重复入队。");
        }

        // Host 已 Dispose（Channel 已 Completed）→ Closed，不再尝试入队
        if (Volatile.Read(ref _disposed) != 0)
        {
            return BuildEnqueueResult(
                AgentRunEnqueueStatus.Closed, run.RunId, capacity,
                "AgentKernelHost 已关闭，无法入队新的 Run。");
        }

        // WorkersEnabled=false：Host 不执行 Run（worker 未启动），入队无意义 → 直接返回 Closed，
        // 由调用方（端点）释放 Scheduler Claim 并返回 202（Run 已持久化，等待其他节点/调度接管）。
        if (!_options.WorkersEnabled)
        {
            return BuildEnqueueResult(
                AgentRunEnqueueStatus.Closed, run.RunId, capacity,
                "AgentKernelHost 未启用后台 worker，无法执行 Run（Run 已持久化，等待外部调度）。");
        }

        // 方案 A：入队前不获取 lease；Worker 从队列取到 Run + 获得执行槽之后再 Acquire Lease。
        // 避免排队期间 lease 过期导致 heartbeat 无法续租、双实例并发执行同一 Run。
        var actor = CreateActor();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRun = new ActiveRun(actor, cts, Lease: null);

        if (!_activeRuns.TryAdd(key, activeRun))
        {
            // 并发竞争：其他线程先添加 → 清理并退出
            cts.Dispose();
            return BuildEnqueueResult(
                AgentRunEnqueueStatus.AlreadyActive, run.RunId, capacity,
                "同进程内已有活跃 Run（并发竞争），跳过重复入队。");
        }

        // 保留池判定：Priority >= ReservedPriorityThreshold 的 Run 走保留容量（高优先级/系统路径），
        // 不占用常规容量、不受 Workspace 排队上限约束——保障饱和时高优先级工作仍能入队。
        var usesReserved = _reservedCapacity is not null && run.Priority >= _options.ReservedPriorityThreshold;
        var perWorkspaceLimit = _options.MaxQueuedPerWorkspace > 0 ? _options.MaxQueuedPerWorkspace : 64;

        // 快路径预检：常规池下 Workspace 排队已达上限 → 立即 QueueFull（不浪费等待/槽位）。
        // 权威判定在锁内重做（防并发超限竞态），此处仅为快速拒绝。
        if (!usesReserved && !IsWorkspaceQueueUnderLimit(run.WorkspaceId, perWorkspaceLimit))
        {
            _activeRuns.TryRemove(key, out _);
            cts.Dispose();
            return BuildEnqueueResult(
                AgentRunEnqueueStatus.QueueFull, run.RunId, capacity,
                $"调度队列已满（workspace 排队上限 {perWorkspaceLimit}，workspaceId={run.WorkspaceId}）；Run 已持久化，将由 RecoveryWorker 稍后接管执行。");
        }

        // 获取容量槽位：timeout=0 等价于非阻塞 Wait(0)（满队列立即返回 QueueFull）；
        // timeout>0 有界等待（Enqueue Timeout），超时仍满 → QueueFull。不无限等待。
        var pool = usesReserved ? _reservedCapacity! : _queueCapacity;
        var acquired = await AcquireQueueSlotAsync(pool, timeout, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            // 失败路径清理：移除活跃 Run 跟踪 + 释放 CTS（不留残留）
            _activeRuns.TryRemove(key, out _);
            cts.Dispose();

            var detail = timeout > TimeSpan.Zero
                ? $"调度队列已满（容量 {capacity}），等待槽位 {timeout.TotalSeconds:F1}s 超时；Run 已持久化，将由 RecoveryWorker 稍后接管执行。"
                : $"调度队列已满（容量 {capacity}），Run 已持久化，将由 RecoveryWorker 稍后接管执行。";
            return BuildEnqueueResult(AgentRunEnqueueStatus.QueueFull, run.RunId, capacity, detail);
        }

        lock (_queueLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                // Dispose 竞态窗口：释放槽位 + 清理跟踪（不留残留）
                ReleasePoolSlot(usesReserved);
                _activeRuns.TryRemove(key, out _);
                cts.Dispose();
                return BuildEnqueueResult(
                    AgentRunEnqueueStatus.Closed, run.RunId, capacity,
                    "AgentKernelHost 已关闭，无法入队新的 Run。");
            }

            // 权威 per-workspace 排队上限（锁内判定，杜绝并发超限）：常规池受限，保留池不受限。
            var workspaceQueue = GetOrCreateWorkspaceQueue(run.WorkspaceId);
            if (!usesReserved && workspaceQueue.Items.Count >= perWorkspaceLimit)
            {
                ReleasePoolSlot(usesReserved);
                _activeRuns.TryRemove(key, out _);
                cts.Dispose();
                return BuildEnqueueResult(
                    AgentRunEnqueueStatus.QueueFull, run.RunId, capacity,
                    $"调度队列已满（workspace 排队上限 {perWorkspaceLimit}，workspaceId={run.WorkspaceId}）；Run 已持久化，将由 RecoveryWorker 稍后接管执行。");
            }

            workspaceQueue.Items.Add(new RunWorkItem(
                run, key, activeRun,
                Priority: run.Priority,
                EnqueueUtcTicks: DateTimeOffset.UtcNow.Ticks,
                UsesReservedPool: usesReserved));
            _totalQueued++;
        }
        _queueSignal.Release();

        // 入队成功 → 立即消费 Scheduler Claim，转入 ScheduledLocally（独立于 Claim 生命周期）。
        // 排队期间不再依赖 Claim 续租：状态已离开可领取集合（Claimed），Claim 过期后其他节点
        // 不会重新领取，避免"队列等待超过 Claim 时长 → 他节点接管 → 本节点出队时仲裁失败"。
        // 消费失败（claim 已被接管/过期）：Run 仍在本地队列，出队时状态校验会放弃执行
        // （执行租约保证单执行者，不产生双执行）。
        if (run.State == AgentRunState.Claimed
            && run.ClaimToken is not null
            && _runStore is IPersistentAgentRunStore persistentStore)
        {
            try
            {
                await persistentStore.ScheduleLocallyAsync(
                    run.WorkspaceId, run.RunId, run.ClaimToken, run.ClaimOwner,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Run {RunId} 入队后消费 Scheduler Claim 失败（Claim 已被接管/过期）；Run 仍在本地队列，出队时按状态放弃执行。",
                    run.RunId);
            }
        }

        return BuildEnqueueResult(
            AgentRunEnqueueStatus.Accepted, run.RunId, capacity, null);
    }

    /// <summary>
    /// 获取队列容量槽位：timeout &lt;= 0 时非阻塞（Wait(0)）；timeout &gt; 0 时有界等待。
    /// 取消时视为未获取（返回 false，调用方按 QueueFull 处理——Run 已持久化，非致命）。
    /// </summary>
    private static async ValueTask<bool> AcquireQueueSlotAsync(
        SemaphoreSlim pool, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return pool.Wait(0);
        }

        try
        {
            return await pool.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>释放容量槽位到其来源池（保留池 / 常规池）。</summary>
    private void ReleasePoolSlot(bool usesReserved)
    {
        if (usesReserved && _reservedCapacity is not null)
        {
            _reservedCapacity.Release();
        }
        else
        {
            _queueCapacity.Release();
        }
    }

    /// <summary>
    /// 计算保留容量：显式配置 > 0 时取 min(配置, ChannelCapacity/2)（小容量不被保留池吞掉）；
    /// 否则自动 = clamp(max(8, ChannelCapacity/16), 0, ChannelCapacity/2)。
    /// </summary>
    private int ComputeReservedCapacity(int capacity)
    {
        var half = capacity / 2;
        if (_options.ReservedQueueCapacity > 0)
        {
            return Math.Min(_options.ReservedQueueCapacity, half);
        }
        return Math.Clamp(Math.Max(8, capacity / 16), 0, half);
    }

    /// <summary>快路径预检：Workspace 排队数是否低于上限（调用方随后持锁做权威判定）。</summary>
    private bool IsWorkspaceQueueUnderLimit(string workspaceId, int limit)
    {
        lock (_queueLock)
        {
            return !_workspaceQueues.TryGetValue(workspaceId, out var queue) || queue.Items.Count < limit;
        }
    }

    /// <summary>获取（或创建）Workspace 队列条目；调用方必须已持有 _queueLock。</summary>
    private WorkspaceQueueEntry GetOrCreateWorkspaceQueue(string workspaceId)
    {
        // 字典超过上限时淘汰空闲条目（Items 为空）：公平性记忆只保留到上限，
        // 保证长期运行下 Workspace 集合有界；非空条目数 ≤ 总排队数（≤ 队列容量）。
        if (_workspaceQueues.Count >= WorkspaceQueueMaxEntries)
        {
            foreach (var key in _workspaceQueues.Where(kvp => kvp.Value.Items.Count == 0).Select(kvp => kvp.Key).ToArray())
            {
                _workspaceQueues.Remove(key);
            }
        }

        if (!_workspaceQueues.TryGetValue(workspaceId, out var entry))
        {
            entry = new WorkspaceQueueEntry
            {
                WorkspaceId = workspaceId,
                Weight = _options.WorkspaceQueueWeight > 0 ? _options.WorkspaceQueueWeight : 1
            };
            _workspaceQueues[workspaceId] = entry;
        }
        Interlocked.Exchange(ref entry.LastTouchTicks, DateTimeOffset.UtcNow.Ticks);
        return entry;
    }

    private AgentRunEnqueueResult BuildEnqueueResult(
        AgentRunEnqueueStatus status, string runId, int capacity, string? detail)
        => new()
        {
            Status = status,
            RunId = runId,
            QueueDepth = QueueDepth,
            ActiveCount = _activeRuns.Count,
            Capacity = capacity,
            Detail = detail
        };

    /// <summary>当前队列深度（等待执行的 Run 数；诊断/监控用）。</summary>
    public int QueueDepth
    {
        get
        {
            lock (_queueLock)
            {
                return _totalQueued;
            }
        }
    }

    /// <summary>调度队列容量上限（诊断/监控用，含保留容量）。</summary>
    public int QueueCapacity => _options.ChannelCapacity > 0 ? _options.ChannelCapacity : 256;

    /// <summary>
    /// 当前空闲队列槽位数（常规池 + 保留池剩余 permit，诊断/调度联动用）。
    /// 供外部调度器（PostgresPendingRunClaimer）按实际可入队容量领取 Run，
    /// 避免领取超过空闲槽位导致多余 Run 持有 Scheduler Claim 直到过期。
    /// </summary>
    public int AvailableQueueSlots => _queueCapacity.CurrentCount + (_reservedCapacity?.CurrentCount ?? 0);

    /// <summary>固定 worker 数（从队列拉取 Run 并执行的后台任务数）。</summary>
    public int WorkerCount => _workerCount;

    /// <summary>
    /// 固定 worker 循环：从公平队列读取 Run work item，调用 RunWithLeaseAndConcurrencyAsync。
    /// 每轮先在锁内检查队列（有任务 → 加权公平出队执行；空且已 dispose → 退出；空且未 dispose → 等待信号）。
    /// check-before-wait 结构保证优雅 drain：Dispose 设置 disposed 后 worker 排空剩余任务，
    /// 队列为空时在下一次循环检查中退出（不依赖 drain 超时强制取消）。
    /// </summary>
    private async Task RunWorkerLoopAsync(int workerId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                RunWorkItem? item = null;
                lock (_queueLock)
                {
                    if (_totalQueued > 0)
                    {
                        item = DequeueFair();
                    }
                    else if (Volatile.Read(ref _disposed) != 0)
                    {
                        // 已排空且 Host 已关闭 → 退出 worker。
                        break;
                    }
                }

                if (item is null)
                {
                    // 无任务：等待新任务入队（Dispose 会释放唤醒信号，worker 醒来重查退出条件）。
                    try
                    {
                        await _queueSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    continue;
                }

                ReleasePoolSlot(item.UsesReservedPool);

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
    /// 加权公平出队（调用方必须已持有 _queueLock）：
    /// 1. Workspace 选择——min(ServiceCount / Weight)（交叉相乘避免浮点），平局按 WorkspaceId 字典序；
    ///    等权重 = 严格轮转：每个 Workspace 每轮最多被服务一次，再回到队首。
    /// 2. 桶内选择——取 (reverse aged priority, 入队时间) 最小者：原始优先级优先，
    ///    同优先级 FIFO；aged priority = 原始优先级 + 排队时长老化提升（防低优先级饿死）。
    /// 3. 出队后 ServiceCount+1、全局排队数 -1、记录排队等待指标（QueueWait SLO）。
    ///    空桶保留在字典中（ServiceCount 公平性记忆持续生效），由容量上限淘汰空闲条目。
    /// </summary>
    private RunWorkItem DequeueFair()
    {
        WorkspaceQueueEntry? winner = null;
        foreach (var kvp in _workspaceQueues)
        {
            if (kvp.Value.Items.Count == 0)
            {
                continue;
            }
            if (winner is null || CompareFairness(kvp.Value, winner) < 0)
            {
                winner = kvp.Value;
            }
        }

        if (winner is null)
        {
            // 理论不可达：_totalQueued > 0 时必有非空 Workspace 队列。
            _totalQueued = 0;
            return null!;
        }

        var now = DateTimeOffset.UtcNow;
        RunWorkItem? best = null;
        long bestReverse = long.MaxValue;
        long bestEnqueue = long.MaxValue;
        foreach (var item in winner.Items)
        {
            var agedPriority = ComputeAgedPriority(item.Priority, item.EnqueueUtcTicks, now);
            var reverse = -(long)agedPriority;
            if (reverse < bestReverse || (reverse == bestReverse && item.EnqueueUtcTicks < bestEnqueue))
            {
                bestReverse = reverse;
                bestEnqueue = item.EnqueueUtcTicks;
                best = item;
            }
        }

        winner.Items.Remove(best!);
        winner.ServiceCount++;
        Interlocked.Exchange(ref winner.LastTouchTicks, now.Ticks);
        _totalQueued--;
        RecordQueueWait(best!, now);
        return best!;
    }

    /// <summary>
    /// 公平性比较：a 比 b 更公平（ServiceCount/Weight 更小）返回负值；相等按 WorkspaceId 字典序。
    /// 交叉相乘（a.Count × b.Weight vs b.Count × a.Weight）避免浮点精度问题。
    /// </summary>
    private static int CompareFairness(WorkspaceQueueEntry a, WorkspaceQueueEntry b)
    {
        var left = a.ServiceCount * b.Weight;
        var right = b.ServiceCount * a.Weight;
        var cmp = left.CompareTo(right);
        return cmp != 0 ? cmp : string.CompareOrdinal(a.WorkspaceId, b.WorkspaceId);
    }

    /// <summary>
    /// 老化优先级：原始优先级 + 排队等待时长内完整老化间隔数 × 步长。
    /// 等待越久的 Run 提升越多，最终超过新到达的高优先级 Run（防饿死）。
    /// </summary>
    private int ComputeAgedPriority(int priority, long enqueueUtcTicks, DateTimeOffset now)
    {
        var interval = _options.PriorityAgingInterval > TimeSpan.Zero
            ? _options.PriorityAgingInterval
            : TimeSpan.FromSeconds(10);
        var step = _options.PriorityAgingStep > 0 ? _options.PriorityAgingStep : 1;
        var waited = now.UtcTicks - enqueueUtcTicks;
        if (waited <= 0)
        {
            return priority;
        }
        var boost = (long)(waited / interval.Ticks) * step;
        var aged = (long)priority + boost;
        return aged > int.MaxValue ? int.MaxValue : (int)aged;
    }

    /// <summary>记录排队等待指标：等待时长直方图 + 超过 QueueWaitSlo 的计数。</summary>
    private void RecordQueueWait(RunWorkItem item, DateTimeOffset now)
    {
        var waitedTicks = now.UtcTicks - item.EnqueueUtcTicks;
        if (waitedTicks <= 0)
        {
            return;
        }
        CoreMetrics.AgentQueueWaitDuration.Record(waitedTicks / TimeSpan.TicksPerMillisecond);
        var slo = _options.QueueWaitSlo > TimeSpan.Zero ? _options.QueueWaitSlo : TimeSpan.FromSeconds(30);
        if (waitedTicks >= slo.Ticks)
        {
            CoreMetrics.AgentQueueWaitSloExceeded.Add(1);
        }
    }

    /// <summary>
    /// 带租约心跳与并发上限的 Run 执行包装。
    /// </summary>
    /// <remarks>
    /// 方案 A：Worker 从队列取到 Run + 获得全局/Workspace 执行槽之后再 Acquire Lease，
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
            // 全局并发上限
            await _globalSemaphore.WaitAsync(activeRun.Cts.Token).ConfigureAwait(false);
            globalAcquired = true;

            // Workspace 级并发上限
            // 通过 GetOrCreateWorkspaceSemaphore 支持潜在 LRU 淘汰避免无界增长
            workspaceSemaphore = GetOrCreateWorkspaceSemaphore(run.WorkspaceId);
            await workspaceSemaphore.WaitAsync(activeRun.Cts.Token).ConfigureAwait(false);
            workspaceAcquired = true;

            // 方案 A：获得执行槽之后再 Acquire Lease（入队前不获取，避免排队期间过期）
            if (_options.LeaseEnabled && _runLease is not null)
            {
                var owner = _options.Owner ?? BuildDefaultOwner();
                lease = await _runLease.TryAcquireAsync(
                    run.WorkspaceId, run.RunId, _options.LeaseDuration, owner, activeRun.Cts.Token).ConfigureAwait(false);
                if (lease is null)
                {
                    // 租约被其他实例持有 → 释放执行槽并退出（其他实例正在处理）
                    // 释放由 finally 块按标志位处理，此处只需 return
                    _logger?.LogDebug("Run {RunId} 租约被其他实例持有，跳过执行。", run.RunId);
                    return;
                }
            }

            // Scheduler Claim Lease → Execution/Fencing Lease 交接（Claimed → Running）。
            // Run 被 Claimer/端点领取（Claimed）并放入本地队列后，本节点取得执行租约即宣告
            // "开始执行"——将状态推进为 Running。持久化路径使用 ConsumeClaimAsync：
            // 单事务校验 DB 中 claim_token / claim_owner 与队列项一致且 claim 未过期
            // （防 Claim 过期后他节点重新领取、旧节点仍消费旧 Claim 的仲裁失效竞态），
            // 成功后清空 claim 字段并附带执行租约 fencing（租约被抢占时推进失败）。
            // 非持久化（InMemory）回退通用 CAS（无 Scheduler Claim 语义）。
            if (run.State == AgentRunState.Claimed)
            {
                try
                {
                    if (_runStore is IPersistentAgentRunStore persistentStore)
                    {
                        run = await persistentStore.ConsumeClaimAsync(
                            run.WorkspaceId, run.RunId,
                            run.ClaimToken, run.ClaimOwner,
                            lease?.LeaseToken, lease?.FencingToken,
                            activeRun.Cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await _runStore.TransitionStateAsync(
                            run.WorkspaceId, run.RunId,
                            AgentRunState.Claimed, AgentRunState.Running,
                            activeRun.Cts.Token, lease?.LeaseToken, lease?.FencingToken).ConfigureAwait(false);
                        run = run with { State = AgentRunState.Running };
                    }
                }
                catch (InvalidOperationException)
                {
                    // 0 行受影响：Claim 已被接管（claim_token 不匹配）/已过期/状态已被其他节点推进。
                    // 执行租约保证单执行者；重新读取最新状态维持 Actor 的 CAS 一致性。
                    _logger?.LogDebug("Run {RunId} Claimed→Running 消费失败（Claim 被接管或状态已变），重新读取最新状态。", run.RunId);
                    try
                    {
                        var latest = await _runStore.GetAsync(run.WorkspaceId, run.RunId, activeRun.Cts.Token)
                            .ConfigureAwait(false);
                        if (latest is not null)
                        {
                            run = latest;
                        }
                    }
                    catch
                    {
                        // 读取失败非致命：Actor 的 resume 路径可容忍状态差异
                    }
                }
            }
            else if (run.State == AgentRunState.ScheduledLocally)
            {
                // 入队时已消费 Scheduler Claim（Claimed → ScheduledLocally）：
                // 直接推进 Running（带执行租约 fencing），无需再次校验 Claim。
                // 推进失败（恢复 Worker 已将 Run 回退 Queued / Claim 已被他节点接管）→
                // 放弃本次执行（执行租约保证单执行者，不产生双执行），Run 交由 Durable Claimer 重新调度。
                try
                {
                    await _runStore.TransitionStateAsync(
                        run.WorkspaceId, run.RunId,
                        AgentRunState.ScheduledLocally, AgentRunState.Running,
                        activeRun.Cts.Token, lease?.LeaseToken, lease?.FencingToken).ConfigureAwait(false);
                    run = run with { State = AgentRunState.Running };
                }
                catch (InvalidOperationException)
                {
                    // CAS 失败：状态已被恢复 Worker 回退或并发推进，本节点不再持有执行权。
                    _logger?.LogDebug(
                        "Run {RunId} ScheduledLocally→Running 推进失败（状态已被恢复/接管），放弃本次执行，由 Durable Claimer 重新调度。",
                        run.RunId);
                    return;
                }
            }

            // 将租约登记到共享批量心跳（续约失败时取消 Actor 防止双执行）
            // 传入 activeRun.Cts 以便续租失败时取消 Actor（防止双执行）
            Func<DateTimeOffset?>? leaseExpiryProvider = null;
            if (lease is not null && _runLease is not null)
            {
                RegisterLease(lease, activeRun.Cts);
                var leaseEntry = _leaseRegistry[lease.Key];
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
                UnregisterLease(lease.Key);
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
        _leaseRegistry[lease.Key] = new ActiveLeaseEntry
        {
            Key = lease.Key,
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
    private void UnregisterLease(TenantRunKey key)
    {
        _leaseRegistry.TryRemove(key, out _);
    }

    /// <summary>
    /// 共享批量心跳循环：每 <see cref="AgentHostOptions.HeartbeatInterval"/> 周期
    /// 通过一次 <see cref="IAgentRunLease.RenewBatchAsync"/> 调用续约全部活跃租约，
    /// 替代"每个 Run 一个独立续约任务 + 每次 DB 往返"的模式（N 次往返 → 1 次）。
    /// </summary>
    /// <remarks>
    /// 失败语义与旧的每 Run 心跳一致：
    /// - 续约失败（租约被抢占/过期）→ 取消对应 Actor，防止双执行；
    /// - 连续续约异常超过阈值（数据库不可达）→ 取消对应 Actor；
    /// - 本地 watchdog：最后一次确认的租约 ExpiresAt 已过 → 立即取消 Actor
    /// （续约异常不延长本地期限，防止租约实际已过期仍执行副作用）。
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
            var cancelSet = new HashSet<TenantRunKey>();

            // 本地 watchdog：最后一次确认的租约已过期 → 取消 Actor（不发起续约）
            foreach (var entry in entries)
            {
                if (now.UtcTicks >= Interlocked.Read(ref entry.LastConfirmedExpiresTicks))
                {
                    _logger?.LogError(
                        "Run {RunKey} 本地确认的租约已过期（ExpiresAt={ExpiresAt}），取消 Actor。",
                        entry.Key, new DateTimeOffset(entry.LastConfirmedExpiresTicks, TimeSpan.Zero));
                    CancelActor(entry.ActorCts);
                    cancelSet.Add(entry.Key);
                }
            }

            // 批量续约剩余租约（单次 DB 往返）
            var toRenew = entries
                .Where(e => !cancelSet.Contains(e.Key))
                .Select(e => new AgentRunLeaseRenewal { Key = e.Key, LeaseToken = e.LeaseToken })
                .ToList();

            if (toRenew.Count > 0)
            {
                try
                {
                    var renewFailed = await _runLease.RenewBatchAsync(toRenew, extension, cancellationToken).ConfigureAwait(false);
                    foreach (var entry in entries)
                    {
                        if (cancelSet.Contains(entry.Key))
                        {
                            continue;
                        }
                        if (renewFailed.Contains(entry.Key))
                        {
                            // 丢租后旧 owner 不写任何终态（无 fencing token 的写入会破坏新 owner 状态），
                            // 仅本地取消 Actor 防止双执行。Run 保持非终态由 RecoveryWorker 重新入队恢复
                            // （resume from checkpoint）；超时无人接管时由 RecoveryWorker 原子标记 LeaseLost。
                            _logger?.LogWarning(
                                "Run {RunKey} 租约续约失败，其他实例可能已接管；取消 Actor 执行。", entry.Key);
                            CancelActor(entry.ActorCts);
                            cancelSet.Add(entry.Key);
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
                        if (cancelSet.Contains(entry.Key))
                        {
                            continue;
                        }
                        var failures = Interlocked.Increment(ref entry.ConsecutiveFailures);
                        _logger?.LogWarning("Run {RunKey} heartbeat 续约异常（连续 {Count}/{Max}）。",
                            entry.Key, failures, MaxConsecutiveFailures);
                        if (failures >= MaxConsecutiveFailures)
                        {
                            _logger?.LogError(
                                "Run {RunKey} heartbeat 连续 {Count} 次异常，触发本地 watchdog 取消 Actor。",
                                entry.Key, failures);
                            CancelActor(entry.ActorCts);
                            cancelSet.Add(entry.Key);
                        }
                    }
                }
            }

            // 移除已取消的条目（Run 的 finally 也会移除；此处提前清理避免下一周期重复续约/重复取消）
            foreach (var entry in entries)
            {
                if (cancelSet.Contains(entry.Key))
                {
                    _leaseRegistry.TryRemove(entry.Key, out _);
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
            await _runLease.ReleaseAsync(lease.WorkspaceId, lease.RunId, lease.LeaseToken, cancellationToken).ConfigureAwait(false);
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
        // 解析 IAgentCheckpointStore
        var checkpointStore = _serviceProvider.GetService(typeof(IAgentCheckpointStore)) as IAgentCheckpointStore;
        // 解析 IDurableToolExecutor
        var durableToolExecutor = _serviceProvider.GetService(typeof(IDurableToolExecutor)) as IDurableToolExecutor;
        // 解析 IAgentModelContextProjector
        var modelContextProjector = _serviceProvider.GetService(typeof(IAgentModelContextProjector)) as IAgentModelContextProjector;
        // 解析 IToolReconciliationStore（未注册时 Actor 跳过"未裁决不完成"约束）
        var reconciliationStore = _serviceProvider.GetService(typeof(IToolReconciliationStore)) as IToolReconciliationStore;
        // 解析 IToolCatalog（提供模型 function calling 的 Tool 定义；未注册时 Actor 回退到 dispatcher 实现）
        var toolCatalog = _serviceProvider.GetService(typeof(IToolCatalog)) as IToolCatalog;
        // Recovery Integrity State：解析人工介入告警接收器（未注册时 Actor 不告警，best-effort 钩子）。
        var recoveryAlertSink = _serviceProvider.GetService(typeof(IRecoveryAlertSink)) as IRecoveryAlertSink;
        // 正式方案：解析事件流压缩器（未注册时 Actor 无快照/归档，走全量重放）。
        // 仅 Postgres provider 注册 IAgentRunEventCompactor。
        var eventCompactor = _serviceProvider.GetService(typeof(IAgentRunEventCompactor)) as IAgentRunEventCompactor;
        // 解析 Tool 授权策略（提供授权快照校验；未注册时 Actor 跳过快照校验——旧路径）。
        var toolAuthorizationPolicy = _serviceProvider.GetService(typeof(IToolAuthorizationPolicy)) as IToolAuthorizationPolicy;
        // 解析自适应检索规划器（未注册时 Actor 的 ContextBuilding 不应用自适应层）。
        var adaptivePlanner = _serviceProvider.GetService(typeof(IAdaptiveRetrievalPlanner)) as IAdaptiveRetrievalPlanner;
        // 解析统一提交入口（Postgres provider 注册；InMemory provider 未注册 → null，Actor 回退 Event Store 批量追加）。
        var committer = _serviceProvider.GetService(typeof(IPersistentAgentRunCommitter)) as IPersistentAgentRunCommitter;

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
            modelContextProjector,
            reconciliationStore,
            toolCatalog,
            _options,
            recoveryAlertSink,
            eventCompactor,
            toolAuthorizationPolicy,
            adaptivePlanner,
            committer);
    }

    private static string ActiveRunKey(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";

    /// <summary>
    /// 优雅 drain：标记关闭（不再接受新 Run），唤醒全部 worker 排空剩余队列（最多 DrainTimeout）。
    /// 由 DI 容器在 Singleton 释放时自动调用。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // 唤醒等待中的 worker：每 worker 释放一个信号，使其醒来重查退出条件
        // （check-before-wait：队列非空则继续排空，空则退出）。
        // 正在执行 Run 的 worker 完成后会在下一轮循环检查中退出，无需信号。
        for (var i = 0; i < _workerCount; i++)
        {
            _queueSignal.Release();
        }

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
        public required TenantRunKey Key { get; init; }
        public required string LeaseToken { get; init; }
        public required CancellationTokenSource ActorCts { get; init; }
        /// <summary>最后一次确认的租约过期时间（UTC ticks；续约异常时不更新）。</summary>
        public long LastConfirmedExpiresTicks;
        /// <summary>连续续约异常计数（超过阈值取消 Actor）。</summary>
        public int ConsecutiveFailures;
    }

    /// <summary>公平队列 work item（Run + key + ActiveRun + 出队判定元数据）。</summary>
    private sealed record RunWorkItem(
        AgentRun Run,
        string Key,
        ActiveRun ActiveRun,
        int Priority,
        long EnqueueUtcTicks,
        bool UsesReservedPool);

    /// <summary>
    /// Workspace 队列条目（weighted fair queue 调度单元）：
    /// Items 按入队序追加（出队时线性扫描 aged key——桶内规模受全局容量与 per-workspace 上限约束，有界）。
    /// ServiceCount 记录该 Workspace 已服务次数（公平性依据，跨空桶持续）；Weight 为加权公平权重；
    /// LastTouchTicks 为最近入队/出队时间（空闲条目淘汰依据）。
    /// </summary>
    private sealed class WorkspaceQueueEntry
    {
        public required string WorkspaceId { get; init; }
        public required int Weight { get; init; }
        public long ServiceCount;
        public long LastTouchTicks;
        public readonly List<RunWorkItem> Items = new();
    }

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
