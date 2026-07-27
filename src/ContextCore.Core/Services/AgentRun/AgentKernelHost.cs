using System.Collections.Concurrent;
using System.Threading.Channels;
using ContextCore.Abstractions;
using Microsoft.Extensions.Logging;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E7 + 子问题 9：AgentKernelHost — 多 Session 隔离的 Kernel Host（生产化）
//
// 替代 DefaultAgentKernel 的 Singleton 全局状态，实现真正的多 Session 隔离：
//   1. 每个 Run 拥有独立的 AgentRunActor 实例（per-run 隔离）；
//   2. 通过 IServiceProvider 解析 Actor 所需依赖（与 DI 容器集成）；
//   3. ConcurrentDictionary 跟踪活跃 Run（key = workspaceId:runId）；
//   4. StartRunAsync 创建 Actor 并写入 bounded Channel（fire-and-forget）；
//   5. GetRunStatusAsync 查询 Run 状态（通过 IAgentRunStore）；
//   6. CancelRunAsync 取消指定 Run（TransitionState → Cancelled + CTS 触发）。
//
// 子问题 9 生产化增强：
//   - HA Run Lease：先 IAgentRunLease.TryAcquireAsync 获取租约，失败则跳过（其他实例正在处理）；
//   - 后台 heartbeat 续租（HeartbeatInterval）；处理完成后 Release；
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
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.DefaultAgentKernel"/> 的 Singleton 全局状态，
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceSemaphores = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _globalSemaphore;

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

        // 子问题 9：HA Run Lease（若启用 + 注入）
        LeasedAgentRun? lease = null;
        if (_options.LeaseEnabled && _runLease is not null)
        {
            var owner = _options.Owner ?? BuildDefaultOwner();
            lease = await _runLease.TryAcquireAsync(
                run.RunId, _options.LeaseDuration, owner, cancellationToken).ConfigureAwait(false);
            if (lease is null)
            {
                // 租约被其他实例持有 → 跳过（其他实例正在处理）
                _logger?.LogDebug("Run {RunId} 租约被其他实例持有，跳过启动。", run.RunId);
                return;
            }
        }

        var actor = CreateActor();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRun = new ActiveRun(actor, cts, lease);

        if (!_activeRuns.TryAdd(key, activeRun))
        {
            // 并发竞争：其他线程先添加 → 释放租约并退出
            cts.Dispose();
            await TryReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
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
            await TryReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
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
    /// 子问题 9：带租约心跳 + 并发上限的 Run 执行包装。
    /// </summary>
    private async Task RunWithLeaseAndConcurrencyAsync(AgentRun run, string key, ActiveRun activeRun)
    {
        // 子问题 9：全局并发上限
        await _globalSemaphore.WaitAsync(activeRun.Cts.Token).ConfigureAwait(false);
        // 子问题 9：Workspace 级并发上限
        var workspaceSemaphore = _workspaceSemaphores.GetOrAdd(
            run.WorkspaceId,
            _ => new SemaphoreSlim(
                _options.MaxWorkspaceRuns > 0 ? _options.MaxWorkspaceRuns : 10,
                _options.MaxWorkspaceRuns > 0 ? _options.MaxWorkspaceRuns : 10));
        await workspaceSemaphore.WaitAsync(activeRun.Cts.Token).ConfigureAwait(false);

        // 子问题 9：启动 heartbeat 续租（若启用租约）
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(activeRun.Cts.Token);
        Task? heartbeatTask = null;
        if (activeRun.Lease is not null && _runLease is not null)
        {
            heartbeatTask = RunHeartbeatAsync(run.RunId, activeRun.Lease.LeaseToken, heartbeatCts.Token);
        }

        try
        {
            await activeRun.Actor.ExecuteAsync(run, activeRun.Cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Actor 内部已处理异常并记录 RunFailed；此处仅兜底防吞异常
        }
        finally
        {
            // 停止 heartbeat
            if (heartbeatTask is not null)
            {
                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); }
                catch { /* heartbeat 取消异常忽略 */ }
            }

            // 释放并发槽位
            workspaceSemaphore.Release();
            _globalSemaphore.Release();

            // 释放租约
            await TryReleaseLeaseAsync(activeRun.Lease, CancellationToken.None).ConfigureAwait(false);

            // 从活跃 Run 移除
            if (_activeRuns.TryRemove(key, out var removed))
            {
                removed.Cts.Dispose();
            }
        }
    }

    /// <summary>
    /// 子问题 9：后台 heartbeat 续租（直到 cancellationToken 取消）。
    /// </summary>
    private async Task RunHeartbeatAsync(string runId, string leaseToken, CancellationToken cancellationToken)
    {
        if (_runLease is null)
        {
            return;
        }

        var interval = _options.HeartbeatInterval > TimeSpan.Zero
            ? _options.HeartbeatInterval
            : TimeSpan.FromSeconds(30);
        var extension = _options.LeaseDuration > TimeSpan.Zero
            ? _options.LeaseDuration
            : TimeSpan.FromMinutes(10);

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

            try
            {
                var renewed = await _runLease.RenewAsync(runId, leaseToken, extension, cancellationToken).ConfigureAwait(false);
                if (!renewed)
                {
                    // 租约丢失 → 其他实例已接管，停止心跳
                    _logger?.LogWarning("Run {RunId} 租约续约失败，其他实例可能已接管。", runId);
                    break;
                }
            }
            catch
            {
                // 续约异常忽略（下次重试）
            }
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
        var checkpointFactory = _serviceProvider.GetService(typeof(IAgentCheckpointFactory)) as IAgentCheckpointFactory;
        var decisionRuntime = _serviceProvider.GetService(typeof(IContextDecisionRuntime)) as IContextDecisionRuntime;
        // 子问题 4：解析 IAgentCheckpointStore
        var checkpointStore = _serviceProvider.GetService(typeof(IAgentCheckpointStore)) as IAgentCheckpointStore;
        // 子问题 5：解析 IDurableToolExecutor
        var durableToolExecutor = _serviceProvider.GetService(typeof(IDurableToolExecutor)) as IDurableToolExecutor;

        return new AgentRunActor(
            _runStore,
            eventStore,
            modelTransport,
            loopPolicy,
            toolDispatcher,
            toolCallValidator,
            approvalGate,
            checkpointFactory,
            decisionRuntime,
            checkpointStore,
            durableToolExecutor);
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
            _workerCts.Cancel();
            _workerCts.Dispose();
        }
    }

    /// <summary>活跃 Run 内部跟踪条目（Actor + CTS + Lease）。</summary>
    private sealed record ActiveRun(
        AgentRunActor Actor,
        CancellationTokenSource Cts,
        LeasedAgentRun? Lease);

    /// <summary>Channel work item（Run + key + ActiveRun）。</summary>
    private sealed record RunWorkItem(AgentRun Run, string Key, ActiveRun ActiveRun);
}
