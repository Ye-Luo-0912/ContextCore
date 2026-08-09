using ContextCore.Abstractions;
using ContextCore.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ContextCore.Service.Hosting;

// ===========================================================================
// TerminalRunSettlementWorker — Run 终态结算工作器
//
// 轮询 ITerminalRunSettlementStore 中待结算的 Run 终态条目，
// 对每个条目执行配额结算（exactly-once）：
// - 准入即拒绝（AdmissionRejected，从未执行）→ Release 退回容量；
// - 其余终态（Completed / Failed / Cancelled / LeaseLost / DeadLettered /
//   ContextSafetyBlocked / RecoveryBlocked / RecoveryCorrupted / ReconciliationRejected）
//   → Actualize 按最终持久化的实际用量转正（多退少补，actualUsage=0 自然等价释放）。
//
// 结算目标（IWorkspaceQuotaService）与写入方（PostgresAgentRunStore 的
// TransitionStateAsync 事务内 outbox 写入）解耦：worker 只负责消费，
// 使「仅取消端点释放配额、其余终态无结算入口」的路径收敛为统一结算入口。
//
// 结算按账本一致性设计，不设终止状态：连续失败只把条目转入卡住（低频无限重试），
// 绝不放弃——放弃意味着预留与 reserved_tokens 永久占用 Workspace 可用额度；
// 同时按固定周期执行对账，补写终态 Run + 有效预留但缺失结算记录的条目。
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

    /// <summary>周期对账间隔：终态 Run + 有效预留 + 无结算记录 → 补写待结算条目。</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    /// <summary>后台负载治理预算（burst 限制 + 让出），避免结算 Worker 持续独占 DB。</summary>
    private static readonly BackgroundDrainBudget DrainBudget = BackgroundDrainBudget.QuotaSettlement;

    private readonly IServiceProvider _services;
    private readonly IWorkspaceQuotaService _quotaService;
    private readonly ILogger<TerminalRunSettlementWorker> _logger;
    private readonly IBackgroundLoadProbe? _loadProbe;
    private readonly TimeSpan _interval;
    private readonly string _owner;
    private DateTimeOffset _nextReconcileAt = DateTimeOffset.UtcNow;

    /// <summary>初始化终态结算工作器。</summary>
    /// <param name="runStore">保留参数（兼容既有 DI 注册）；结算事实以 outbox 冻结快照为准，不再读取 Run 实体。</param>
    public TerminalRunSettlementWorker(
        IServiceProvider services,
        IWorkspaceQuotaService quotaService,
        IAgentRunStore runStore,
        ContextCoreRuntimeOptions options,
        ILogger<TerminalRunSettlementWorker> logger)
    {
        _ = runStore;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _quotaService = quotaService ?? throw new ArgumentNullException(nameof(quotaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = options?.RunRecoveryInterval > TimeSpan.Zero
            ? options.RunRecoveryInterval
            : TimeSpan.FromSeconds(30);
        _owner = $"{Environment.MachineName}:{Environment.ProcessId}";
        // 动态降速探针（可选）：DB 池利用率高时收紧 burst 预算。
        _loadProbe = services.GetService<IBackgroundLoadProbe>();
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
                // 周期对账：终态 Run + 有效预留 + 无结算记录 → 补写待结算条目。
                // 兜底覆盖状态转换事务漏写 outbox / 结算记录丢失等缺口，
                // 与领取节奏解耦，按固定周期执行。
                var now = DateTimeOffset.UtcNow;
                if (now >= _nextReconcileAt)
                {
                    try
                    {
                        var repaired = await store.ReconcileSettlementGapsAsync(stoppingToken).ConfigureAwait(false);
                        if (repaired > 0)
                        {
                            _logger.LogWarning("终态结算对账补写：{Count} 条缺失的结算记录。", repaired);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "终态结算对账异常（非致命，下轮重试）。");
                    }
                    _nextReconcileAt = DateTimeOffset.UtcNow + ReconcileInterval;
                }

                var hasMore = false;
                var burstStart = DateTimeOffset.UtcNow;
                var batchesThisBurst = 0;
                // 动态降速（WP-D）：DB 池利用率高时收紧 burst 预算（探针可选；null = 静态）。
                var loadFactor = _loadProbe?.GetDbPoolUtilization() is { } utilization
                    ? BackgroundDrainBudget.ComputeScaleFactor(utilization)
                    : (double?)null;
                try
                {
                    // 满批续扫受 burst 预算约束：批次数 / 时长任一超限即让出，
                    // 防持续负载下无限追队尾独占 DB（BackgroundDrainBudget）。
                    do
                    {
                        hasMore = await SettleOnceAsync(store, stoppingToken).ConfigureAwait(false);
                        batchesThisBurst++;
                    }
                    while (hasMore
                           && DrainBudget.ShouldContinueBurst(batchesThisBurst, DateTimeOffset.UtcNow - burstStart, loadFactor)
                           && !stoppingToken.IsCancellationRequested);
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
                    // burst 预算耗尽但仍有积压：让出后继续（不放弃积压，只限单次突发）。
                    await DrainBudget.YieldAsync(stoppingToken).ConfigureAwait(false);
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

        // 尝试达到阈值的条目转卡住（低频无限重试，绝不放弃——结算放弃会锁死配额）。
        try
        {
            var stuck = await store.TransitionStuckAsync(ct).ConfigureAwait(false);
            if (stuck > 0)
            {
                _logger.LogWarning("Run 终态结算卡住：{Count} 个条目转入低频重试（不再以正常频率轮询）。", stuck);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "终态结算卡住过渡异常（非致命）。");
        }

        return claimed.Count >= BatchSize;
    }

    private async Task SettleEntryAsync(ITerminalRunSettlementStore store, TerminalSettlementEntry entry, CancellationToken ct)
    {
        // 结算事实来自 outbox 冻结快照（写入时从 UsageSnapshot 冻结）——
        // 绝不在此刻读取可变的 Run 实体：Run 归档 / 删除 / 数据损坏 / 未来 schema
        // 变化都不影响已经形成的账务事实。实际用量以冻结值为准（多退少补）。
        var actualTokens = entry.ActualTokens;
        var actualCostUsd = entry.ActualCostUsd;

        // 终态语义统一来自 AgentRunStateSemantics：准入即拒绝（AdmissionRejected，从未执行）
        // 退回容量；其余终态按实际用量转正（多退少补，actualUsage=0 自然等价释放）。
        // 冻结的结算策略与语义层权威一致（防御：以冻结值为准，不在此重新派生）。
        var settlementPolicy = entry.SettlementPolicy;
        if (settlementPolicy == QuotaSettlementPolicy.Release)
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
