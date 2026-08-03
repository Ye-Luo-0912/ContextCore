using ContextCore.Abstractions;
using ContextCore.Core.Services.AgentRunRuntime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// ToolReconciliationWorker — Tool 对账工作器
//
// 轮询 IToolReconciliationStore 中的 Pending 对账记录，按
// ToolDescriptor.ReconciliationHandler 名称解析 IToolReconciliationHandler，
// 以 ExternalOperationId 查询外部系统确认模糊 Tool 调用的外部副作用真相：
//   - SideEffectOccurred=true  → journal.BeginReconciliationAsync + MarkReconciledWithResultAsync
//                               （提交真相结果），记录 → Resolved；
//   - SideEffectOccurred=false → journal 提交 void 结果（Succeeded=false），记录 → Rejected
//                               （禁止重放该 Tool，模型看到失败后可调整策略）；
//   - Handler 缺失/未注册       → 记录保持 Pending，等待人工 resolve 端点裁决；
//   - Handler 抛异常            → 记录回退 Pending，下轮重试。
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
    private readonly ToolReconciliationCoordinator _coordinator;
    private readonly IToolReconciliationStore _store;
    private readonly IReadOnlyDictionary<string, IToolReconciliationHandler> _handlers;
    private readonly AgentKernelHost? _kernelHost;
    private readonly IAgentRunStore _runStore;
    private readonly ILogger<ToolReconciliationWorker> _logger;
    private readonly TimeSpan _interval;

    public ToolReconciliationWorker(
        ToolReconciliationCoordinator coordinator,
        IToolReconciliationStore store,
        IEnumerable<IToolReconciliationHandler>? handlers,
        AgentKernelHost? kernelHost,
        IAgentRunStore runStore,
        ContextCoreRuntimeOptions options,
        ILogger<ToolReconciliationWorker> logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _kernelHost = kernelHost;
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _handlers = (handlers ?? Array.Empty<IToolReconciliationHandler>())
            .ToDictionary(h => h.HandlerName, StringComparer.Ordinal);
        _interval = options.RunRecoveryInterval > TimeSpan.Zero
            ? options.RunRecoveryInterval
            : TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ToolReconciliationWorker 启动：轮询间隔 {Interval}s。", _interval.TotalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
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
                _logger.LogError(ex, "ToolReconciliationWorker 轮询循环异常（不中断后续轮询）。");
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
        _logger.LogInformation("ToolReconciliationWorker 已停止。");
    }

    /// <summary>
    /// 人工/自动裁决对账记录（POST resolve 端点与 Handler 路径共用）。
    /// </summary>
    /// <returns>
    /// 0 = 成功裁决；1 = 记录不存在；2 = 已裁决（幂等冲突）。
    /// </returns>
    public Task<int> ResolveAsync(string reconciliationId, ToolReconciliationOutcome outcome, CancellationToken ct)
        => _coordinator.ResolveAsync(reconciliationId, outcome, ct);

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        var pending = await _store.ListPendingAsync(20, ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
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

        foreach (var group in pending.GroupBy(r => r.RunId, StringComparer.Ordinal))
        {
            // Worker 接管该 Run 的对账：AwaitingReconciliation → ReconciliationRunning（best-effort）。
            var workspaceId = group.First().WorkspaceId;
            await TransitionRunStateAsync(
                workspaceId, group.Key, AgentRunState.AwaitingReconciliation, AgentRunState.ReconciliationRunning, ct).ConfigureAwait(false);

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
                    await _coordinator.ReconcileRecordAsync(record, handler, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "ToolReconciliationWorker: 对账记录 {Id}（tool={Tool}）Handler 执行失败，重置为 Pending 重试。",
                        record.ReconciliationId, record.ToolName);
                }
            }

            await MaybeRequeueRunAsync(workspaceId, group.Key, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 裁决完成后若 Run 无未裁决记录：先回退 Run 状态（ReconciliationRunning → AwaitingReconciliation），
    /// 再重新入队让 Actor 恢复执行。
    /// </summary>
    private async Task MaybeRequeueRunAsync(string workspaceId, string runId, CancellationToken ct)
    {
        if (await _store.HasUnresolvedForRunAsync(runId, ct).ConfigureAwait(false))
        {
            return; // 仍有未裁决记录 → Run 不得恢复
        }
        if (_kernelHost is null)
        {
            return;
        }

        await TransitionRunStateAsync(
            workspaceId, runId, AgentRunState.ReconciliationRunning, AgentRunState.AwaitingReconciliation, ct).ConfigureAwait(false);

        try
        {
            var run = await _runStore.GetAsync(workspaceId, runId, ct).ConfigureAwait(false);
            if (run is not null)
            {
                await _kernelHost.StartRunAsync(run, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "ToolReconciliationWorker: Run {RunId} 对账全部完成，重新入队恢复执行。", runId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ToolReconciliationWorker: Run {RunId} 对账完成后重新入队失败。", runId);
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
}
