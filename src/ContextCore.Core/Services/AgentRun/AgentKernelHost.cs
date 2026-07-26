using System.Collections.Concurrent;
using ContextCore.Abstractions;

namespace ContextCore.Core.Services.AgentRunRuntime;

// ===========================================================================
// 任务 E7：AgentKernelHost — 多 Session 隔离的 Kernel Host
//
// 替代 DefaultAgentKernel 的 Singleton 全局状态，实现真正的多 Session 隔离：
//   1. 每个 Run 拥有独立的 AgentRunActor 实例（per-run 隔离）；
//   2. 通过 IServiceProvider 解析 Actor 所需依赖（与 DI 容器集成）；
//   3. ConcurrentDictionary 跟踪活跃 Run（key = workspaceId:runId）；
//   4. StartRunAsync 创建 Actor 并启动 ExecuteAsync（fire-and-forget）；
//   5. GetRunStatusAsync 查询 Run 状态（通过 IAgentRunStore）；
//   6. CancelRunAsync 取消指定 Run（TransitionState → Cancelled + CTS 触发）。
//
// 设计决策：
//   - Actor 内部维护独立的累积状态，不共享全局变量（多 Session 隔离）；
//   - StartRunAsync 不阻塞调用方（fire-and-forget + 错误日志）；
//   - Run 完成后从 _activeRuns 移除（避免内存泄漏）；
//   - CancelRunAsync 通过 CTS 触发 ExecuteAsync 内部取消（优雅退出）。
// ===========================================================================

/// <summary>
/// 任务 E7：多 Session 隔离的 Kernel Host。
/// 替代 <see cref="ContextCore.Core.Services.AgentKernel.DefaultAgentKernel"/> 的 Singleton 全局状态，
/// 为每个 Run 创建独立的 <see cref="AgentRunActor"/> 实例，实现真正的多 Session 隔离。
/// </summary>
public sealed class AgentKernelHost
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentRunStore _runStore;
    private readonly ConcurrentDictionary<string, ActiveRun> _activeRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// 构造 Kernel Host。
    /// </summary>
    /// <param name="serviceProvider">DI 容器（用于解析 Actor 依赖）。</param>
    /// <param name="runStore">Run 元数据存储（用于查询状态）。</param>
    public AgentKernelHost(IServiceProvider serviceProvider, IAgentRunStore runStore)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    }

    /// <summary>
    /// 为指定 Run 创建 Actor 并启动执行（fire-and-forget）。
    /// </summary>
    /// <param name="run">待执行的 Run 元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示启动完成的任务（不等待执行完成）。</returns>
    public async Task StartRunAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var actor = CreateActor();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeRun = new ActiveRun(actor, cts);

        var key = ActiveRunKey(run.WorkspaceId, run.RunId);
        if (!_activeRuns.TryAdd(key, activeRun))
        {
            // 已存在活跃 Run → 不重复启动
            cts.Dispose();
            return;
        }

        // fire-and-forget：ExecuteAsync 在后台运行，完成后清理
        _ = Task.Run(async () =>
        {
            try
            {
                await actor.ExecuteAsync(run, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Actor 内部已处理异常并记录 RunFailed；此处仅兜底防吞异常
            }
            finally
            {
                if (_activeRuns.TryRemove(key, out var removed))
                {
                    removed.Cts.Dispose();
                }
            }
        }, cts.Token);

        await Task.CompletedTask.ConfigureAwait(false);
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

        return new AgentRunActor(
            _runStore,
            eventStore,
            modelTransport,
            loopPolicy,
            toolDispatcher,
            toolCallValidator,
            approvalGate,
            checkpointFactory,
            decisionRuntime);
    }

    private static string ActiveRunKey(string workspaceId, string runId)
        => $"{workspaceId}:{runId}";

    /// <summary>活跃 Run 内部跟踪条目（Actor + CTS）。</summary>
    private sealed record ActiveRun(AgentRunActor Actor, CancellationTokenSource Cts);
}
