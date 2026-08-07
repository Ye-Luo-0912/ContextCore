using ContextCore.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// TerminalRunSettlementWorker — Run 终态结算工作器
//
// 轮询 ITerminalRunSettlementStore 中待结算的 Run 终态条目，
// 对每个条目执行配额结算（exactly-once）：
// - Completed / Failed / DeadLettered / LeaseLost → Actualize
//   （Run 实际执行，按最终持久化的实际用量转正，多退少补）；
// - Cancelled / AdmissionRejected → Release
//   （Run 未执行或取消退回容量）。
//
// 结算目标（IWorkspaceQuotaService）与写入方（PostgresAgentRunStore 的
// TransitionStateAsync 事务内 outbox 写入）解耦：worker 只负责消费，
// 使「仅取消端点释放配额、其余终态无结算入口」的路径收敛为统一结算入口。
// ===========================================================================

/// <summary>
/// Run 终态结算工作器：消费终态结算 outbox，统一执行配额 Actualize / Release。
/// </summary>
public sealed class TerminalRunSettlementWorker : BackgroundService
{
    /// <summary>结算租约时长（领取后持有；过期后其他节点可接管重试）。</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>每批次最多领取数。</summary>
    private const int BatchSize = 20;

    private readonly IServiceProvider _services;
    private readonly IWorkspaceQuotaService _quotaService;
    private readonly IAgentRunStore _runStore;
    private readonly ILogger<TerminalRunSettlementWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly string _owner;

    /// <summary>初始化终态结算工作器。</summary>
    public TerminalRunSettlementWorker(
        IServiceProvider services,
        IWorkspaceQuotaService quotaService,
        IAgentRunStore runStore,
        ContextCoreRuntimeOptions options,
        ILogger<TerminalRunSettlementWorker> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _quotaService = quotaService ?? throw new ArgumentNullException(nameof(quotaService));
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = options?.RunRecoveryInterval > TimeSpan.Zero
            ? options.RunRecoveryInterval
            : TimeSpan.FromSeconds(30);
        _owner = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 未注册 ITerminalRunSettlementStore（非 Postgres provider）时自退出 no-op。
        using var probeScope = _services.CreateScope();
        var store = probeScope.ServiceProvider.GetService<ITerminalRunSettlementStore>();
        if (store is null)
        {
            _logger.LogInformation(
                "TerminalRunSettlementWorker 检测到未注册 ITerminalRunSettlementStore（非 Postgres provider），自退出。");
            return;
        }

        _logger.LogInformation("TerminalRunSettlementWorker 启动：轮询间隔 {Interval}s。", _interval.TotalSeconds);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var hasMore = false;
                try
                {
                    hasMore = await SettleOnceAsync(store, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TerminalRunSettlementWorker 轮询循环异常（不中断后续轮询）。");
                }

                if (hasMore)
                {
                    // 满批领取：还有更多待结算条目，立即续扫，不等间隔（吞吐优先）。
                    continue;
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
            _logger.LogInformation("TerminalRunSettlementWorker 已停止。");
        }
    }

    /// <summary>
    /// 单轮结算：领取并处理一批条目。返回 true 表示本批取满（仍有积压），
    /// 调用方应立即续扫；false 表示队列已空，调用方可按间隔休眠。
    /// </summary>
    private async Task<bool> SettleOnceAsync(ITerminalRunSettlementStore store, CancellationToken ct)
    {
        var claimed = await store.ClaimBatchAsync(BatchSize, _owner, LeaseDuration, ct).ConfigureAwait(false);
        foreach (var entry in claimed)
        {
            try
            {
                await SettleEntryAsync(store, entry, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Run 终态结算失败（待重试）：workspace_id={WorkspaceId}, run_id={RunId}, terminal_state={State}。",
                    entry.WorkspaceId, entry.RunId, entry.TerminalState);
                try
                {
                    await store.MarkFailedAsync(entry.OutboxId, entry.LeaseToken, ex.Message, ct).ConfigureAwait(false);
                }
                catch (Exception markEx)
                {
                    _logger.LogWarning(markEx, "标记结算失败记录异常（租约可能已被抢占）。");
                }
            }
        }

        // 尝试耗尽的条目转死信（不再自动重试）。
        try
        {
            var deadLettered = await store.DeadLetterExhaustedAsync(ct).ConfigureAwait(false);
            if (deadLettered > 0)
            {
                _logger.LogWarning("Run 终态结算死信：{Count} 个条目尝试耗尽。", deadLettered);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "终态结算死信清理异常（非致命）。");
        }

        return claimed.Count >= BatchSize;
    }

    private async Task SettleEntryAsync(ITerminalRunSettlementStore store, TerminalSettlementEntry entry, CancellationToken ct)
    {
        // 实际用量：读取 Run 当前持久化的预算（执行过程中由 Actor 更新）。
        // Run 行缺失（已清理）时按 0 用量结算（预留转正/释放由配额服务幂等处理）。
        long actualTokens = 0;
        double actualCostUsd = 0;
        var run = await _runStore.GetAsync(entry.WorkspaceId, entry.RunId, ct).ConfigureAwait(false);
        if (run?.CostBudget is { } budget)
        {
            actualTokens = budget.TokensUsed;
            actualCostUsd = budget.CostUsedUsd;
        }

        // 终态语义：执行类终态按实际用量转正；未执行类终态退回容量。
        // Cancelled 走 Release 与取消端点即时释放语义一致（不向 workspace 计费）。
        if (entry.TerminalState is AgentRunState.AdmissionRejected or AgentRunState.Cancelled)
        {
            await _quotaService.ReleaseAsync(entry.WorkspaceId, entry.ReservationId, ct).ConfigureAwait(false);
        }
        else
        {
            await _quotaService.ActualizeAsync(
                entry.WorkspaceId, entry.ReservationId, actualTokens, actualCostUsd, ct).ConfigureAwait(false);
        }

        // CAS 标记已结算；0 行 = 租约已被抢占（另一节点接管），放弃。
        var processed = await store.MarkProcessedAsync(entry.OutboxId, entry.LeaseToken, ct).ConfigureAwait(false);
        if (!processed)
        {
            _logger.LogWarning(
                "终态结算标记失败（租约已被抢占）：workspace_id={WorkspaceId}, run_id={RunId}。",
                entry.WorkspaceId, entry.RunId);
        }
    }
}
