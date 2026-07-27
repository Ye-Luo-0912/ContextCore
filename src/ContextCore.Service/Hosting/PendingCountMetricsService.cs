using ContextCore.Abstractions;
using ContextCore.Core;
using ContextCore.Core.Services.AgentKernel;
using ContextCore.Storage.Postgres.Stores;
using Microsoft.Extensions.Options;

namespace ContextCore.Service.Hosting;

/// <summary>
/// P2：Pending 计数 OTel 指标后台服务。
/// 周期性查询 DB 精确值（全局，跨实例）并采样本实例趋势值（本地近似），
/// 更新 <see cref="CoreMetrics"/> 共享状态，供 ObservableGauge 在 OTel 抓取时导出。
/// </summary>
/// <remarks>
/// <b>背景</b>：HA 多实例部署下，各实例维护的本地近似计数（Interlocked counter）不反映 DB 已有 backlog
/// 或其他实例操作，<b>不可用于调度/安全判断</b>。本服务定期查询 DB COUNT(*) 获取全局精确值，
/// 作为独立的 <c>global_pending_count</c> 指标导出；同时采样本实例趋势值导出为 <c>local_pending_count</c>，
/// 供运维区分本实例负载与全局 backlog。
///
/// <b>注册</b>：由 <see cref="Extensions.DurableTransportServiceCollectionExtensions.AddDurableTransportHostedServices"/>
/// 自动注册。仅在 <see cref="DurableTransportHostingOptions.Enabled"/> 为 true 且
/// <see cref="DurableTransportHostingOptions.MetricsInterval"/> &gt; <see cref="TimeSpan.Zero"/> 时运行。
///
/// <b>容错</b>：DB 查询失败时保留上一次的共享状态值（不重置为 0），避免指标抖动误导告警。
/// 单次循环异常被吞掉并记录日志，下一周期重试。
/// </remarks>
internal sealed class PendingCountMetricsService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<DurableTransportHostingOptions> _options;
    private readonly ILogger<PendingCountMetricsService> _logger;

    public PendingCountMetricsService(
        IServiceProvider services,
        IOptions<DurableTransportHostingOptions> options,
        ILogger<PendingCountMetricsService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled || options.MetricsInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation("Pending count metrics service is disabled (Enabled={Enabled}, MetricsInterval={Interval}).",
                options.Enabled, options.MetricsInterval);
            return;
        }

        _logger.LogInformation("Pending count metrics service started. Interval={Interval}s.",
            options.MetricsInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.MetricsInterval, stoppingToken).ConfigureAwait(false);
                await UpdateMetricsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pending count metrics 循环异常；下一周期重试。");
            }
        }

        _logger.LogInformation("Pending count metrics service stopped.");
    }

    private async Task UpdateMetricsAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;

        await UpdateTransportMetricsAsync(sp, cancellationToken).ConfigureAwait(false);
        await UpdateOutboxMetricsAsync(sp, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateTransportMetricsAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var transport = sp.GetService<IAgentKernelTransport>();
        if (transport is null)
        {
            return;
        }

        if (transport is IDurableTransport durable)
        {
            // 全局精确值：DB COUNT(*) 查询
            var globalInstr = await durable.GetPendingInstructionCountAsync(cancellationToken).ConfigureAwait(false);
            var globalResult = await durable.GetPendingResultCountAsync(cancellationToken).ConfigureAwait(false);
            CoreMetrics.SetGlobalPendingCount(CoreMetrics.PendingQueueTag.Instruction, globalInstr);
            CoreMetrics.SetGlobalPendingCount(CoreMetrics.PendingQueueTag.Result, globalResult);

            // P0-6-7：死信队列行数（仅 PostgresDurableTransport 支持）
            if (transport is PostgresDurableTransport pgTransportForDlq)
            {
                var deadLetterCount = await pgTransportForDlq.GetDeadLetterCountAsync(cancellationToken).ConfigureAwait(false);
                CoreMetrics.SetGlobalDeadLetterCount(deadLetterCount);
            }

            // 本实例趋势值：从具体实现读取 Interlocked 维护的近似 counter
            if (transport is PostgresDurableTransport pgTransport)
            {
#pragma warning disable CS0618 // Obsolete: 本实例趋势值，仅供指标导出采样
                CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Instruction, pgTransport.PendingInstructionCount);
                CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Result, pgTransport.PendingResultCount);
#pragma warning restore CS0618
            }
            else
            {
                // 其他 IDurableTransport 实现：未暴露独立的本地近似计数，local = global
                CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Instruction, globalInstr);
                CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Result, globalResult);
            }
        }
        else if (transport is InProcessTransport inProc)
        {
            // 单进程部署：local = global = channel 计数（无 DB backlog）
            var instrCount = inProc.PendingInstructionCount;
            CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Instruction, instrCount);
            CoreMetrics.SetGlobalPendingCount(CoreMetrics.PendingQueueTag.Instruction, instrCount);

            var resultCount = inProc.PendingResultCount;
            CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Result, resultCount);
            CoreMetrics.SetGlobalPendingCount(CoreMetrics.PendingQueueTag.Result, resultCount);
        }
    }

    private async Task UpdateOutboxMetricsAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var outbox = sp.GetService<IKernelResultOutbox>();
        if (outbox is null)
        {
            return;
        }

        if (outbox is IPersistentKernelResultOutbox persistent)
        {
            // 全局精确值：DB COUNT(*) 查询
            var globalOutbox = await persistent.GetPendingCountAsync(cancellationToken).ConfigureAwait(false);
            CoreMetrics.SetGlobalPendingCount(CoreMetrics.PendingQueueTag.Outbox, globalOutbox);
            // 持久化 outbox 未暴露独立的本地近似计数（内部 cache 仅供 GetPendingCountAsync 同步用），local = global
            CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Outbox, globalOutbox);
        }
        else if (outbox is InMemoryKernelResultOutbox inMem)
        {
            // 单进程部署：local = global = channel 计数
            var outboxCount = inMem.PendingCount;
            CoreMetrics.SetLocalPendingCount(CoreMetrics.PendingQueueTag.Outbox, outboxCount);
            CoreMetrics.SetGlobalPendingCount(CoreMetrics.PendingQueueTag.Outbox, outboxCount);
        }
    }
}
